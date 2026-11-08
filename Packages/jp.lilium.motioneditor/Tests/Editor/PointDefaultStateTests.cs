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
