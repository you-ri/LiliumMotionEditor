using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点の既定の状態（リグ定義の defaultState）。キーの無いフレームと、姿勢を基準へ戻したとき（Reset）に、その状態になる。
    /// 既定の定義では足首の点が Pin（11-08 ユーザー指示）
    /// </summary>
    public class PointDefaultStateTests
    {
        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        PoseSession Open (System.Action<EditRigDefinition> edit = null)
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            if (edit != null) edit (definition);
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition);
            disposables_.Add (session);
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            session.clip = clip;
            session.Evaluate (0);
            return session;
        }

        static BodyPointTarget Point (PoseSession session, string name)
        {
            return session.targets.OfType<BodyPointTarget> ().Single (p => p.id.name == name);
        }

        static void Reset (PoseSession session)
        {
            foreach (PoseTarget target in session.targets.ToList ()) target.ResetPose ();
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ("Reset"));
        }

        [Test]
        public void DefaultDefinition_PinsTheAnkles ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            foreach (EditRigDefinition.BodyPoint point in definition.bodyPoints) {
                bool ankle = point.bone == HumanBodyBones.LeftFoot || point.bone == HumanBodyBones.RightFoot;
                ankle &= point.name == point.bone.ToString ();
                Assert.AreEqual (ankle ? BodyPointState.Pin : BodyPointState.Free, point.defaultState, point.name);
            }
            EditRigDefinition asset = EditRigDefinition.packageDefault;
            Assert.IsNotNull (asset, "パッケージの既定の定義");
            foreach (string name in new[] { "LeftFoot", "RightFoot" }) {
                Assert.AreEqual (BodyPointState.Pin, asset.bodyPoints.Single (p => p.name == name).defaultState, "既定の定義のアセットの " + name);
            }
        }

        [Test]
        public void ClipWithoutKeys_UsesTheDefaultState ()
        {
            PoseSession session = Open (d => d.bodyPoints.Single (p => p.name == "LeftHand").defaultState = BodyPointState.Locked);
            Assert.AreEqual (BodyPointState.Pin, Point (session, "LeftFoot").state);
            Assert.AreEqual (BodyPointState.Pin, Point (session, "RightFoot").state);
            Assert.AreEqual (BodyPointState.Locked, Point (session, "LeftHand").state);
            Assert.AreEqual (BodyPointState.Free, Point (session, "RightHand").state);
            Assert.IsFalse (Point (session, "LeftFoot").rotationPinned, "向きは留めない");
        }

        [Test]
        public void BeforeTheFirstKey_UsesTheDefaultState ()
        {
            PoseSession session = Open ();
            session.Evaluate (10);
            Point (session, "LeftFoot").SetState (BodyPointState.Free);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (10);
            Assert.AreEqual (BodyPointState.Free, Point (session, "LeftFoot").state, "キーの状態");
            session.Evaluate (20);
            Assert.AreEqual (BodyPointState.Free, Point (session, "LeftFoot").state, "キーの後は引き継ぐ");
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, Point (session, "LeftFoot").state, "最初のキーより前は既定の状態");
        }

        [TestCase (BodyPointState.Free)]
        [TestCase (BodyPointState.Pin)]
        [TestCase (BodyPointState.Locked)]
        public void Reset_ReturnsToTheDefaultState (BodyPointState state)
        {
            PoseSession session = Open (d => d.bodyPoints.Single (p => p.name == "LeftHand").defaultState = state);
            BodyPointTarget hand = Point (session, "LeftHand");
            BodyPointState other = state == BodyPointState.Pin ? BodyPointState.Locked : BodyPointState.Pin;
            hand.SetState (other);
            hand.MoveBy (new Vector3 (0, 0.1f, 0.1f));
            Point (session, "LeftFoot").SetState (BodyPointState.Locked);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.AreEqual (other, Point (session, "LeftHand").state);

            Reset (session);
            Assert.AreEqual (state, Point (session, "LeftHand").state, "Reset で既定の状態");
            Assert.AreEqual (BodyPointState.Pin, Point (session, "LeftFoot").state, "足首は既定の Pin");
            // 打ったキーから読み直しても同じ
            session.Evaluate (0);
            Assert.AreEqual (state, Point (session, "LeftHand").state, "読み直した状態");
            Assert.AreEqual (BodyPointState.Pin, Point (session, "LeftFoot").state, "読み直した足首");
            // 留める点は基準姿勢の骨の上
            BodyPointTarget ankle = Point (session, "LeftFoot");
            Assert.Less (Vector3.Distance (ankle.framePosition, ankle.editingBone.position), 1e-3f, "足首の点は骨の上");
        }

        /// <summary>脚を開いて閉じる Humanoid のクリップ（全身 IK の点のキーは無い）。rootMotion なら前へ 1 進む</summary>
        AnimationClip CreateLegSpreadClip (bool rootMotion = false)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            foreach (string muscle in new[] { "Left Upper Leg In-Out", "Right Upper Leg In-Out" }) {
                SetCurve (clip, muscle, new AnimationCurve (new Keyframe (0, 0), new Keyframe (0.25f, 1), new Keyframe (0.5f, 0)));
            }
            if (rootMotion) {
                SetCurve (clip, "RootT.y", AnimationCurve.Constant (0, 0.5f, 1));
                SetCurve (clip, "RootT.z", AnimationCurve.Linear (0, 0, 0.5f, 1));
                SetCurve (clip, "RootQ.w", AnimationCurve.Constant (0, 0.5f, 1));
            }
            Assert.IsTrue (clip.humanMotion);
            return clip;
        }

        static void SetCurve (AnimationClip clip, string property, AnimationCurve curve)
        {
            UnityEditor.AnimationUtility.SetEditorCurve (clip, UnityEditor.EditorCurveBinding.FloatCurve ("", typeof (Animator), property), curve);
        }

        static void AssertAnklesOnTheBones (PoseSession session, string label)
        {
            foreach (string name in new[] { "LeftFoot", "RightFoot" }) {
                BodyPointTarget ankle = Point (session, name);
                Assert.AreEqual (BodyPointState.Pin, ankle.state, label + " " + name + " の状態");
                Assert.Less (Vector3.Distance (ankle.framePosition, ankle.editingBone.position), 1e-3f, label + " " + name + " の点は骨の上");
            }
        }

        /// <summary>
        /// Humanoid のクリップを土台に開いても、既定で Pin の足首の点はそのフレームの骨の上にある（基準姿勢の場所に残らない。11-10 不具合）
        /// </summary>
        [Test]
        public void HumanoidBase_PinnedAnklesFollowTheBones ()
        {
            PoseSession session = Open ();
            session.clip = CreateLegSpreadClip ();
            session.Evaluate (0);
            Vector3 rest = Point (session, "LeftFoot").framePosition;
            session.Evaluate (15);
            Assert.Greater (Vector3.Distance (rest, Point (session, "LeftFoot").editingBone.position), 0.05f, "脚が開いている");
            AssertAnklesOnTheBones (session, "15F");
        }

        /// <summary>
        /// 取込で書いた足首の点のキーは、そのフレームの骨の位置（基準姿勢の場所を全フレームに書かない。11-10 不具合）。
        /// ルートの移動を腰へ足し戻すクリップでも、足し戻した後の骨の位置（点だけがルートの移動の分残らない。11-10 不具合の 2 つ目）
        /// </summary>
        [TestCase (false)]
        [TestCase (true)]
        public void ImportedHumanoidClip_KeysTheAnklesOnTheBones (bool rootMotion)
        {
            PoseSession session = Open ();
            AnimationClip source = CreateLegSpreadClip (rootMotion);
            session.clip = source;
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (destination);
            HumanoidImport.Import (source, destination, session.stage.editingRig, session.stage.editingRigSolver,
                time => session.stack.EvaluateThroughHumanoid (time), session.targets.ToList (), session.stage.SnapCarriedPoints);

            session.clip = destination;
            Transform hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips);
            session.Evaluate (0);
            Vector3 rest = Point (session, "LeftFoot").framePosition;
            Vector3 hipsStart = hips.position;
            session.Evaluate (15);
            Assert.Greater (Vector3.Distance (rest, Point (session, "LeftFoot").framePosition), 0.05f, "足首の点が動く");
            if (rootMotion) Assert.Greater (hips.position.z - hipsStart.z, 0.1f, "腰が前へ進む");
            foreach (int frame in new[] { 0, 15, 30 }) {
                session.Evaluate (frame);
                AssertAnklesOnTheBones (session, "取り込んだ " + frame + "F");
            }
        }

        [Test]
        public void PassivePoint_IgnoresTheDefaultState ()
        {
            PoseSession session = Open (d => {
                EditRigDefinition.BodyPoint point = d.bodyPoints.Single (p => p.name == "LeftHand");
                point.passive = true;
                point.defaultState = BodyPointState.Pin;
            });
            Assert.AreEqual (BodyPointState.Free, Point (session, "LeftHand").state);
        }
    }

}
