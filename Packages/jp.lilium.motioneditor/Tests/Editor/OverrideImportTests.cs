using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 外で作った Humanoid のクリップを、元に重ねる Override として取り込む（S40）
    /// </summary>
    public class OverrideImportTests
    {
        const float kPositionTolerance = 0.01f;
        const float kAngleTolerance = 1f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;
        GameObject prefab_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab_.transform, false);
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
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        /// <summary>元のクリップ（窓で打ったものと同じく全身のキーを持つ。0F に基準姿勢を打つ）</summary>
        PoseSession OpenWithBase ()
        {
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = NewClip ();
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            return session;
        }

        static void SetMuscle (AnimationClip clip, string name, System.Func<float, float> value)
        {
            AnimationCurve curve = new AnimationCurve ();
            for (int f = 0; f <= 60; f += 2) curve.AddKey (f / 60f, value (f / 60f));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name), curve);
        }

        /// <summary>外で作った動き（muscle を 1 秒かけて揺らす）</summary>
        AnimationClip Generated ()
        {
            AnimationClip clip = NewClip ();
            SetMuscle (clip, "Spine Front-Back", t => 0.25f * Mathf.Sin (t * Mathf.PI * 2));
            SetMuscle (clip, "Left Arm Down-Up", t => -0.3f + 0.4f * t);
            SetMuscle (clip, "Right Upper Leg Front-Back", t => 0.3f * Mathf.Sin (t * Mathf.PI));
            SetMuscle (clip, "Head Nod Down-Up", t => 0.2f * Mathf.Cos (t * Mathf.PI * 2));
            Assert.IsTrue (clip.humanMotion);
            return clip;
        }

        static List<Pose> WorldPose (PoseSession session)
        {
            return session.stage.editingRig.editingBones.Select (t => new Pose (t.position, t.rotation)).ToList ();
        }

        static void AssertSamePose (List<Pose> expected, List<Pose> actual, string message)
        {
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, actual[i].position), kPositionTolerance, message + ": " + i + " 番目の骨の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, actual[i].rotation), kAngleTolerance, message + ": " + i + " 番目の骨の向き");
            }
        }

        static string Dump (AnimationClip clip)
        {
            return string.Join ("\n", AnimationUtility.GetCurveBindings (clip).OrderBy (b => b.path + b.propertyName)
                .Select (b => b.path + "/" + b.propertyName + ": " + string.Join (", ", AnimationUtility.GetEditorCurve (clip, b).keys.Select (k => k.time + "=" + k.value))));
        }

        [Test]
        public void ImportOverride_StacksToTheGeneratedMotion_AndLeavesTheBase ()
        {
            PoseSession session = OpenWithBase ();
            AnimationClip generated = Generated ();
            AnimationClip layer = NewClip ();
            string before = Dump (session.clip);

            PoseSession.OverrideImportResult result;
            Assert.IsNull (session.ImportOverride (generated, layer, OverrideMode.Additive, false, out result));
            Assert.AreEqual (before, Dump (session.clip), "元のクリップは変わらない");
            Assert.AreEqual (61, result.frameCount);
            Assert.LessOrEqual (result.keyCount * 3, result.keyCountBeforeReduce, "間引いてキーを 1/3 以下にする: " + result.keyCount + " / " + result.keyCountBeforeReduce);

            // 外の動きをそのまま編集用クリップへ取り込んだ姿勢（HumanoidImport。ルートの移動は腰へ移る）
            PoseSession reference = new PoseSession (prefab_, definition_);
            disposables_.Add (reference);
            AnimationClip absolute = NewClip ();
            List<PoseTarget> targets = RigTargets.Create (reference.stage.editingRig, reference.stage.editingRigSolver);
            Transform display = reference.stage.animator.transform;
            display.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            using (ClipSampler sampler = new ClipSampler (reference.stage.animator)) {
                HumanoidImport.Import (generated, absolute, reference.stage.editingRig, reference.stage.editingRigSolver, time => {
                    sampler.Sample (generated, time);
                    display.SetLocalPositionAndRotation (position, rotation);
                }, targets);
            }
            foreach (PoseTarget target in targets) target.Dispose ();
            reference.clip = absolute;

            session.AddOverride (layer, OverrideMode.Additive);
            session.overrideWriteIndex = 0;
            foreach (int frame in new[] { 0, 15, 30, 45, 60 }) {
                reference.Evaluate (frame);
                session.Evaluate (frame);
                AssertSamePose (WorldPose (reference), WorldPose (session), frame + "F の元＋Override");
            }

            // Override の重み 0 で元の姿勢
            session.overrideWriteIndex = -1;
            session.Evaluate (30);
            List<Pose> baseOnly = WorldPose (session);
            session.overrideWriteIndex = 0;
            session.stack.Find<OverrideLayer> ().weight = 0;
            session.Evaluate (30);
            AssertSamePose (baseOnly, WorldPose (session), "重み 0");
        }

        [Test]
        public void LockFeet_RemovesFootSliding ()
        {
            // 左脚を小さく前後に振る（足は床の近くで滑る）
            AnimationClip generated = NewClip ();
            SetMuscle (generated, "Left Upper Leg Front-Back", t => 0.12f * Mathf.Sin (t * Mathf.PI * 2));
            SetMuscle (generated, "Spine Front-Back", t => 0);

            int[] slides = new int[2];
            for (int i = 0; i < 2; i++) {
                PoseSession session = OpenWithBase ();
                AnimationClip layer = NewClip ();
                PoseSession.OverrideImportResult result;
                Assert.IsNull (session.ImportOverride (generated, layer, OverrideMode.Additive, i == 1, out result));
                session.AddOverride (layer, OverrideMode.Additive);
                session.overrideWriteIndex = 0;
                MotionLintReport report = MotionLint.Run (session, 0, 60);
                slides[i] = report.Count (MotionLint.kFootSlide);
                if (i == 1) Assert.Greater (result.lockedFrames, 0, "接地したフレームを解き直した");
            }
            Assert.Greater (slides[0], 0, "取り込んだだけでは足が滑る（確かめたい動きになっている）");
            Assert.AreEqual (0, slides[1], "足を床に付けると滑らない");
        }

        [Test]
        public void BadInputs_ReturnReasons ()
        {
            PoseSession session = OpenWithBase ();
            PoseSession.OverrideImportResult result;
            Assert.IsNotNull (session.ImportOverride (Generated (), session.clip, OverrideMode.Additive, false, out result), "元のクリップへは書かない");
            Assert.IsNotNull (session.ImportOverride (NewClip (), NewClip (), OverrideMode.Additive, false, out result), "Humanoid でないクリップ");
            Assert.IsNotNull (session.ImportOverride (Generated (), null, OverrideMode.Additive, false, out result), "書き先が無い");

            PoseSession empty = new PoseSession (prefab_, definition_);
            disposables_.Add (empty);
            Assert.IsNotNull (empty.ImportOverride (Generated (), NewClip (), OverrideMode.Additive, false, out result), "元のクリップが無い");
        }
    }

}
