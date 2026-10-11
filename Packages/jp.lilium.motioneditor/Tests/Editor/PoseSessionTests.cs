using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 窓を開かずに姿勢を作ってキーを打つ（S35）。描かない世界でも描く世界（窓）と同じ姿勢になり、打ったキーから同じ姿勢に戻る
    /// </summary>
    public class PoseSessionTests
    {
        const float kPositionTolerance = 1e-4f;
        const float kAngleTolerance = 0.01f;
        const string kFolder = "Assets/__MktPoseSessionTest";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
        }

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        T Keep<T> (T disposable) where T : System.IDisposable
        {
            disposables_.Add (disposable);
            return disposable;
        }

        GameObject CreatePrefab ()
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            return prefab;
        }

        PoseSession Open (GameObject prefab, bool render = false)
        {
            return Keep (new PoseSession (prefab, definition_, render));
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        static List<Pose> Snapshot (PoseSession session)
        {
            List<Pose> result = new List<Pose> ();
            foreach (Transform t in session.stage.editingRig.editingBones) result.Add (new Pose (t.localPosition, t.localRotation));
            foreach (Transform t in session.stage.animator.GetComponentsInChildren<Transform> ()) result.Add (new Pose (t.localPosition, t.localRotation));
            return result;
        }

        static void AssertSamePose (List<Pose> expected, List<Pose> actual, string message)
        {
            Assert.AreEqual (expected.Count, actual.Count, message + ": 骨の数");
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, actual[i].position), kPositionTolerance, message + ": " + i + " 番目の骨の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, actual[i].rotation), kAngleTolerance, message + ": " + i + " 番目の骨の回転");
            }
        }

        static void SetRotation (AnimationClip clip, string path, float start, Quaternion a, float end, Quaternion b)
        {
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), AnimationCurve.Linear (start, a[i], end, b[i]));
            }
        }

        static void SetFloat (AnimationClip clip, string path, System.Type type, string property, float start, float a, float end, float b)
        {
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, type, property), AnimationCurve.Linear (start, a, end, b));
        }

        static Quaternion RestRotation (PoseSession session, HumanBodyBones bone)
        {
            return session.stage.editingRig.FindControl (RigPaths.Fk (bone)).localRotation;
        }

        static BodyPointTarget Point (PoseSession session, HumanBodyBones bone)
        {
            GameObject control = session.stage.fullBody.GetControl (bone).gameObject;
            return session.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
        }

        static string Dump (AnimationClip clip)
        {
            return string.Join ("\n", AnimationUtility.GetCurveBindings (clip)
                .OrderBy (b => b.path + "/" + b.propertyName)
                .Select (b => b.path + "/" + b.propertyName + ": " + string.Join (", ", AnimationUtility.GetEditorCurve (clip, b).keys.Select (k => k.time + "=" + k.value))));
        }

        /// <summary>そのフレームで左手の点を動かして解き、キーを打つ。打つ直前の姿勢を返す</summary>
        static List<Pose> MoveHandAndCommit (PoseSession session, int frame, Vector3 offset)
        {
            session.Evaluate (frame);
            BodyPointTarget hand = Point (session, HumanBodyBones.LeftHand);
            hand.MoveTo (hand.framePosition + offset);
            session.SolveAfterEdit ();
            List<Pose> posed = Snapshot (session);
            Assert.IsNull (session.CommitPose (), "キーを打てる");
            return posed;
        }

        // ---- 描かない世界 ----

        /// <summary>
        /// ✏ を Override へ移しても、全身 IK をつかんでいるなら ✋ は全身 IK のまま（ハンドルが消えない）。
        /// 操作値（Editing Rig）をつかんでいるときは、✋ も Override へ移る（保存データの段の値はその段へ書くときだけつかめる）
        /// </summary>
        [Test]
        public void MovingTheWriteLayer_KeepsFullBodyGrab ()
        {
            PoseSession session = Open (CreatePrefab ());
            session.clip = NewClip ();
            session.AddOverride (NewClip (), OverrideMode.Additive);
            PoseLayer fullBody = session.stack.Find (LayerKind.FullBodyIk);
            Assert.IsNotNull (fullBody);
            string reason;
            Assert.IsTrue (session.stack.TrySetManipulate (fullBody, out reason), reason);

            session.overrideWriteIndex = 0;
            Assert.IsInstanceOf<OverrideLayer> (session.stack.writeLayer);
            Assert.AreSame (fullBody, session.stack.manipulateLayer, "全身 IK をつかんだまま");
            session.overrideWriteIndex = -1;
            Assert.AreSame (fullBody, session.stack.manipulateLayer);

            Assert.IsTrue (session.stack.TrySetManipulate (session.stack.Find (LayerKind.EditingRig), out reason), reason);
            session.overrideWriteIndex = 0;
            Assert.AreSame (session.stack.writeLayer, session.stack.manipulateLayer, "操作値をつかんでいたら ✋ も Override へ移る");
        }

        [Test]
        public void Headless_CreatesNoRenderer_AndLeavesNothingAfterDispose ()
        {
            GameObject prefab = CreatePrefab ();
            // GetAllGraphs は壊したグラフも一覧に残す（そこから取ると IsValid も true）ので、作った・壊した知らせで数える
            int graphs = 0;
            System.Action<UnityEngine.Playables.PlayableGraph> created = g => graphs++;
            System.Action<UnityEngine.Playables.PlayableGraph> destroying = g => graphs--;
            UnityEditor.Playables.Utility.graphCreated += created;
            UnityEditor.Playables.Utility.destroyingGraph += destroying;
            try {
                CreateEvaluateAndDispose (prefab);
            }
            finally {
                UnityEditor.Playables.Utility.graphCreated -= created;
                UnityEditor.Playables.Utility.destroyingGraph -= destroying;
            }
            Assert.AreEqual (0, graphs, "PlayableGraph が残らない");
        }

        void CreateEvaluateAndDispose (GameObject prefab)
        {
            PoseSession session = new PoseSession (prefab, definition_);
            AnimationClip clip = NewClip ();
            SetRotation (clip, RigPaths.Fk (HumanBodyBones.Spine), 0, RestRotation (session, HumanBodyBones.Spine), 0.5f, RestRotation (session, HumanBodyBones.Spine) * Quaternion.Euler (20, 0, 0));
            session.clip = clip;
            session.Evaluate (10);

            Assert.IsNull (session.error);
            Assert.IsTrue (session.stage.headless);
            Assert.IsNull (session.stage.renderUtility, "PreviewRenderUtility を作らない");
            Assert.IsNull (session.stage.camera);
            GameObject model = session.stage.model;
            GameObject body = session.stage.editingRig.root;
            Assert.AreNotEqual (SceneManager.GetActiveScene (), model.scene, "開いているシーンに置かない");
            Assert.AreNotEqual (SceneManager.GetActiveScene (), body.scene, "開いているシーンに置かない");

            session.Dispose ();
            Assert.IsTrue (model == null, "表示モデルが残らない");
            Assert.IsTrue (body == null, "編集用の体が残らない");
        }

        [Test]
        public void Headless_MatchesRenderedStage_WithLockedSpan ()
        {
            // 0F と 20F で左足を Locked（同じ場所）、腿は前へ上げていく。キーの間は全身 IK が足を留める
            GameObject prefab = CreatePrefab ();
            PoseSession headless = Open (prefab);
            PoseSession rendered = Open (prefab, true);
            AnimationClip clip = NewClip ();
            Vector3 point = headless.stage.fullBody.GetControl (HumanBodyBones.LeftFoot).localPosition;
            string path = RigPaths.Body (HumanBodyBones.LeftFoot);
            float end = 20 / 60f;
            SetFloat (clip, path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, 0, 1, end, 1);
            SetFloat (clip, path, typeof (BodyPoint), BodyPoint.kLockedProperty, 0, 1, end, 1);
            SetFloat (clip, path, typeof (Transform), "m_LocalPosition.x", 0, point.x, end, point.x);
            SetFloat (clip, path, typeof (Transform), "m_LocalPosition.y", 0, point.y, end, point.y);
            SetFloat (clip, path, typeof (Transform), "m_LocalPosition.z", 0, point.z, end, point.z);
            Quaternion thigh = RestRotation (headless, HumanBodyBones.LeftUpperLeg);
            SetRotation (clip, RigPaths.Fk (HumanBodyBones.LeftUpperLeg), 0, thigh, end, thigh * Quaternion.Euler (-40, 0, 0));
            headless.clip = clip;
            rendered.clip = clip;

            foreach (int frame in new[] { 0, 5, 10, 20 }) {
                headless.Evaluate (frame);
                rendered.Evaluate (frame);
                AssertSamePose (Snapshot (rendered), Snapshot (headless), frame + "F");
            }
            headless.Evaluate (10);
            Assert.IsNotEmpty (headless.stage.lockedSpanPoints, "キーの間で足を留めている（確かめたい経路を通っている）");
        }

        [Test]
        public void Headless_MatchesRenderedStage_WithOverride ()
        {
            GameObject prefab = CreatePrefab ();
            PoseSession headless = Open (prefab);
            PoseSession rendered = Open (prefab, true);
            // 元のクリップの腕は、基準の値のままの一定のカーブ（窓で触らなかった部位と同じ。Unity はこのカーブを当てない）
            AnimationClip clip = NewClip ();
            Quaternion spine = RestRotation (headless, HumanBodyBones.Spine);
            SetRotation (clip, RigPaths.Fk (HumanBodyBones.Spine), 0, spine, 0.5f, spine * Quaternion.Euler (25, 10, 0));
            Quaternion armRest = RestRotation (headless, HumanBodyBones.LeftUpperArm);
            SetRotation (clip, RigPaths.Fk (HumanBodyBones.LeftUpperArm), 0, armRest, 0.5f, armRest);
            AnimationClip layer = NewClip ();
            Quaternion delta = Quaternion.Euler (0, 0, 30);
            SetRotation (layer, RigPaths.Fk (HumanBodyBones.LeftUpperArm), 0, delta, 0.5f, delta);
            foreach (PoseSession session in new[] { headless, rendered }) {
                session.clip = clip;
                session.AddOverride (layer, OverrideMode.Additive);
                session.overrideWriteIndex = 0;
            }
            Assert.AreEqual (0, headless.overrideWriteIndex);

            foreach (int frame in new[] { 0, 15, 30 }) {
                headless.Evaluate (frame);
                rendered.Evaluate (frame);
                AssertSamePose (Snapshot (rendered), Snapshot (headless), frame + "F");
            }

            // Override が効いている（確かめたい経路を通っている）
            Transform arm = headless.stage.editingRig.GetEditingBone (HumanBodyBones.LeftUpperArm);
            headless.Evaluate (15);
            Quaternion layered = arm.rotation;
            headless.Evaluate (15);
            Assert.LessOrEqual (Quaternion.Angle (layered, arm.rotation), kAngleTolerance, "評価し直しても Override が積み重ならない");
            headless.overrideWriteIndex = -1;
            headless.Evaluate (15);
            Assert.Greater (Quaternion.Angle (layered, arm.rotation), 10, "書き込み先が元の間は Override を効かせない");
        }

        [Test]
        public void Headless_MatchesRenderedStage_WithHumanoidBase ()
        {
            GameObject prefab = CreatePrefab ();
            PoseSession headless = Open (prefab);
            PoseSession rendered = Open (prefab, true);
            AnimationClip humanoid = NewClip ();
            SetFloat (humanoid, "", typeof (Animator), "Left Arm Down-Up", 0, 0, 0.5f, 0.5f);
            SetFloat (humanoid, "", typeof (Animator), "Spine Front-Back", 0, 0.2f, 0.5f, -0.2f);
            Assert.IsTrue (humanoid.humanMotion);
            AnimationClip layer = NewClip ();
            Quaternion delta = Quaternion.Euler (15, 0, 0);
            SetRotation (layer, RigPaths.Fk (HumanBodyBones.Chest), 0, delta, 0.5f, delta);
            foreach (PoseSession session in new[] { headless, rendered }) {
                session.clip = humanoid;
                session.AddOverride (layer, OverrideMode.Additive);
                session.overrideWriteIndex = 0;
            }
            Assert.IsTrue (headless.useHumanoidBase);

            foreach (int frame in new[] { 0, 15, 30 }) {
                headless.Evaluate (frame);
                rendered.Evaluate (frame);
                AssertSamePose (Snapshot (rendered), Snapshot (headless), frame + "F");
            }
        }

        // ---- キーを打つ ----

        [Test]
        public void CommitPose_FullBody_KeysReproduceThePose ()
        {
            PoseSession session = Open (CreatePrefab ());
            AnimationClip clip = NewClip ();
            session.clip = clip;
            Transform hand = session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand);
            session.Evaluate (12);
            Vector3 rest = hand.position;

            List<Pose> posed = MoveHandAndCommit (session, 12, new Vector3 (0.1f, 0.25f, 0.15f));

            Assert.Greater (Vector3.Distance (rest, hand.position), 0.05f, "手が動いている");
            CollectionAssert.AreEqual (new[] { 12 }, ClipCycle.GetFrames (clip), "今のフレームに打つ");
            Assert.IsTrue (EditingClip.HasPointKey (clip, RigPaths.Body (HumanBodyBones.LeftHand), 12 / 60f), "動かした点のキーを打つ");
            session.Evaluate (0);
            session.Evaluate (12);
            AssertSamePose (posed, Snapshot (session), "打ったキーから作り直した姿勢");
        }

        [Test]
        public void CommitPose_WritesTheSameKeysAsTheRenderedStage ()
        {
            GameObject prefab = CreatePrefab ();
            PoseSession headless = Open (prefab);
            PoseSession rendered = Open (prefab, true);
            AnimationClip a = NewClip ();
            AnimationClip b = NewClip ();
            headless.clip = a;
            rendered.clip = b;
            MoveHandAndCommit (headless, 6, new Vector3 (0, 0.3f, 0.2f));
            MoveHandAndCommit (rendered, 6, new Vector3 (0, 0.3f, 0.2f));
            Assert.IsNotEmpty (Dump (a));
            Assert.AreEqual (Dump (b), Dump (a), "同じ操作で同じキー");
        }

        [Test]
        public void CommitPose_ToOverride_LeavesTheBaseClip ()
        {
            // 元のクリップは窓で打ったものと同じく全身のキーを持つ（先に元へ 1 回打つ）
            PoseSession session = Open (CreatePrefab ());
            AnimationClip clip = NewClip ();
            session.clip = clip;
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            AnimationClip layer = NewClip ();
            session.AddOverride (layer, OverrideMode.Additive);
            session.overrideWriteIndex = 0;
            string before = Dump (clip);

            List<Pose> posed = MoveHandAndCommit (session, 15, new Vector3 (0.05f, 0.2f, 0.2f));

            Assert.AreEqual (before, Dump (clip), "元のクリップは変わらない");
            Assert.IsNotEmpty (AnimationUtility.GetCurveBindings (layer), "Override へ書く");
            session.Evaluate (0);
            session.Evaluate (15);
            AssertSamePose (posed, Snapshot (session), "元＋Override から作り直した姿勢");
        }

        [Test]
        public void CommitPose_AtCycleKey_WritesTheFirstKey ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktPoseSessionTest");
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.rig.anim");
            PoseSession session = Open (CreatePrefab ());
            session.clip = clip;
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Assert.IsNull (session.CommitPose ());
            Assert.IsTrue (ClipCycle.Save (clip, true));

            // サイクルキーのフレームで直すと、先頭のキーへ打ち、サイクルキーは先頭に合わせ直す
            Transform hand = session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand);
            session.Evaluate (30);
            BodyPointTarget point = Point (session, HumanBodyBones.LeftHand);
            point.MoveTo (point.framePosition + new Vector3 (0, 0.2f, 0.25f));
            session.SolveAfterEdit ();
            Vector3 posed = hand.position;
            Assert.IsNull (session.CommitPose ());
            CollectionAssert.AreEqual (new[] { 0, 30 }, ClipCycle.GetFrames (clip), "キーのフレームは増えない");
            session.Evaluate (0);
            Vector3 first = hand.position;
            session.Evaluate (30);
            Assert.LessOrEqual (Vector3.Distance (posed, first), kPositionTolerance, "先頭のキーへ打った");
            Assert.LessOrEqual (Vector3.Distance (first, hand.position), kPositionTolerance, "サイクルキーは先頭と同じ");

            session.Evaluate (40);
            Assert.IsNotNull (session.GetWriteProblem (), "サイクルキーより後ろには打たない");
        }

        [Test]
        public void CommitPose_WithoutBodyPoints_KeysReproduceThePose ()
        {
            // 全身 IK の点が無いキャラは、つかめる物全部の今の値を打つ
            definition_.bodyPoints.Clear ();
            PoseSession session = Open (CreatePrefab ());
            Assert.IsNull (session.stage.fullBody);
            AnimationClip clip = NewClip ();
            session.clip = clip;
            session.Evaluate (5);
            Transform control = session.stage.editingRig.FindControl (RigPaths.Fk (HumanBodyBones.Spine));
            control.localRotation *= Quaternion.Euler (10, 20, 0);
            session.SolveAfterEdit ();
            List<Pose> posed = Snapshot (session);

            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            session.Evaluate (5);
            AssertSamePose (posed, Snapshot (session), "打ったキーから作り直した姿勢");
        }

        [Test]
        public void GetWriteProblem_ExplainsWhyItCannotWrite ()
        {
            PoseSession session = Open (CreatePrefab ());
            Assert.IsNotNull (session.GetWriteProblem (), "クリップが無い");
            Assert.IsNotNull (session.CommitPose (), "打たずに理由を返す");

            AnimationClip humanoid = NewClip ();
            SetFloat (humanoid, "", typeof (Animator), "Spine Front-Back", 0, 0.2f, 0.5f, 0.2f);
            session.clip = humanoid;
            Assert.IsNotNull (session.GetWriteProblem (), "Humanoid の土台へは書かない");
            session.AddOverride (NewClip ());
            session.overrideWriteIndex = 0;
            Assert.IsNull (session.GetWriteProblem (), "土台に重ねた Override へは書ける");
        }

        [Test]
        public void NonHumanoidPrefab_ReportsTheError ()
        {
            GameObject prefab = new GameObject ("NotHumanoid");
            created_.Add (prefab);
            PoseSession session = Open (prefab);
            Assert.IsNotNull (session.error);
            Assert.IsEmpty (session.targets);
            Assert.IsNotNull (session.GetWriteProblem ());
            session.Evaluate (0);
        }
    }

}
