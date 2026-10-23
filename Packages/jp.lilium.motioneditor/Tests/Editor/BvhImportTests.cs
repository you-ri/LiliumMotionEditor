using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 外部の骨格の BVH を Override として取り込む（S74）
    /// </summary>
    public class BvhImportTests
    {
        const float kPositionTolerance = 0.01f;
        const float kAngleTolerance = 1f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        HumanPose rest_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoidWithFingers (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab_.transform, false);
            rest_ = new HumanPose ();
            using (HumanPoseHandler handler = new HumanPoseHandler (display.avatar, display.transform)) handler.GetHumanPose (ref rest_);
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

        static void SetCurve (AnimationClip clip, string name, System.Func<float, float> value)
        {
            AnimationCurve curve = new AnimationCurve ();
            for (int f = 0; f <= 60; f += 2) curve.AddKey (f / 60f, value (f / 60f));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name), curve);
        }

        /// <summary>1 秒の Humanoid の動き。体は rotation を向き、地面の上で position にいて、muscle を揺らす</summary>
        AnimationClip Generated (Quaternion rotation, Vector3 position)
        {
            AnimationClip clip = NewClip ();
            SetCurve (clip, "Spine Front-Back", t => 0.3f * Mathf.Sin (t * Mathf.PI * 2));
            SetCurve (clip, "Left Arm Down-Up", t => -0.4f + 0.6f * t);
            SetCurve (clip, "Right Forearm Stretch", t => 0.5f * Mathf.Cos (t * Mathf.PI));
            SetCurve (clip, "Left Upper Leg Front-Back", t => 0.3f * Mathf.Sin (t * Mathf.PI * 2));
            SetCurve (clip, "Right Lower Leg Stretch", t => -0.4f * t);
            float[] root = { position.x, rest_.bodyPosition.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
            string[] names = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (int i = 0; i < names.Length; i++) SetCurve (clip, names[i], t => root[i]);
            Assert.IsTrue (clip.humanMotion);
            return clip;
        }

        /// <summary>Humanoid のクリップを、外部の骨格の BVH（30fps）にする（モデルの出力の代わり）</summary>
        Bvh ToBvh (AnimationClip clip, ExternalSkeleton skeleton)
        {
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            Assert.IsNull (error);
            disposables_.Add (instance);
            Bvh bvh = skeleton.CreateBvh (1 / 30f);
            float Evaluate (string name, float time)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name));
                return curve != null ? curve.Evaluate (time) : 0;
            }
            for (int f = 0; f <= 30; f++) {
                float time = f / 30f;
                HumanPose pose = new HumanPose {
                    bodyPosition = new Vector3 (Evaluate ("RootT.x", time), Evaluate ("RootT.y", time), Evaluate ("RootT.z", time)),
                    bodyRotation = new Quaternion (Evaluate ("RootQ.x", time), Evaluate ("RootQ.y", time), Evaluate ("RootQ.z", time), Evaluate ("RootQ.w", time)),
                    muscles = HumanoidBaker.clipMuscleNames.Select (name => Evaluate (name, time)).ToArray (),
                };
                instance.handler.SetHumanPose (ref pose);
                skeleton.Capture (instance, bvh);
            }
            return Bvh.Parse (bvh.ToText (), out error);
        }

        /// <summary>元のクリップ（0F に基準姿勢を打つ）</summary>
        PoseSession OpenWithBase ()
        {
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = NewClip ();
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            return session;
        }

        /// <summary>Humanoid のクリップをそのまま編集用クリップへ取り込んだもの（元のクリップにする）</summary>
        PoseSession OpenImported (AnimationClip source)
        {
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = NewClip ();
            List<PoseTarget> targets = RigTargets.Create (session.stage.editingRig, session.stage.editingRigSolver);
            Transform display = session.stage.animator.transform;
            display.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            using (ClipSampler sampler = new ClipSampler (session.stage.animator)) {
                HumanoidImport.Import (source, session.clip, session.stage.editingRig, session.stage.editingRigSolver, time => {
                    sampler.Sample (source, time);
                    display.SetLocalPositionAndRotation (position, rotation);
                }, targets);
            }
            foreach (PoseTarget target in targets) target.Dispose ();
            session.Evaluate (0);
            return session;
        }

        static void Layer (PoseSession session, AnimationClip layer)
        {
            session.AddOverride (layer, OverrideMode.Additive);
            session.overrideWriteIndex = 0;
        }

        static List<Pose> WorldPose (PoseSession session)
        {
            return session.stage.editingRig.editingBones.Select (t => new Pose (t.position, t.rotation)).ToList ();
        }

        static void AssertSamePose (List<Pose> expected, List<Pose> actual, string message, float position = kPositionTolerance, float angle = kAngleTolerance)
        {
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, actual[i].position), position, message + ": " + i + " 番目の骨の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, actual[i].rotation), angle, message + ": " + i + " 番目の骨の向き");
            }
        }

        [Test]
        public void BvhImport_MatchesTheSameMotionImportedFromAnim ()
        {
            AnimationClip generated = Generated (Quaternion.identity, Vector3.zero);
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_, 1.2f);
            Bvh bvh = ToBvh (generated, skeleton);

            PoseSession fromAnim = OpenWithBase ();
            AnimationClip animLayer = NewClip ();
            Assert.IsNull (fromAnim.ImportOverride (generated, animLayer, OverrideMode.Additive, false, out PoseSession.OverrideImportResult result));
            Layer (fromAnim, animLayer);

            PoseSession fromBvh = OpenWithBase ();
            AnimationClip bvhLayer = NewClip ();
            Assert.IsNull (fromBvh.ImportOverride (bvh, skeleton, bvhLayer, OverrideMode.Additive, false, true, true, out result));
            Assert.AreEqual (61, result.frameCount, "30fps の BVH を 60fps の格子で取り直す");
            Layer (fromBvh, bvhLayer);

            foreach (int frame in new[] { 0, 15, 30, 45, 60 }) {
                fromAnim.Evaluate (frame);
                fromBvh.Evaluate (frame);
                AssertSamePose (WorldPose (fromAnim), WorldPose (fromBvh), frame + "F");
            }
        }

        [Test]
        public void ByDefault_TheKeyedHandShapeIsKept ()
        {
            PoseSession session = OpenWithBase ();
            int index = System.Array.IndexOf (HumanTrait.MuscleName, "Left Index 1 Stretched");
            int thumb = System.Array.IndexOf (HumanTrait.MuscleName, "Right Thumb 1 Stretched");
            Assert.IsNull (session.WriteMuscles (new Dictionary<int, float> { { index, -0.9f }, { thumb, -0.8f } }));

            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = ToBvh (Generated (Quaternion.identity, Vector3.zero), skeleton);
            AnimationClip layer = NewClip ();
            Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, false, true, false, out PoseSession.OverrideImportResult result));

            HashSet<string> fingerPaths = new HashSet<string> (session.stage.editingRig.binding.boneControls
                .Where (c => c.human.HasValue && c.human.Value >= HumanBodyBones.LeftThumbProximal && c.human.Value <= HumanBodyBones.RightLittleDistal).Select (c => c.path));
            Assert.Greater (fingerPaths.Count, 0, "指のコントロールがある（確かめたい骨になっている）");
            Assert.IsFalse (AnimationUtility.GetCurveBindings (layer).Any (b => fingerPaths.Contains (b.path)), "指のカーブを書かない");

            Transform finger = session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftIndexProximal);
            session.Evaluate (30);
            Quaternion baseOnly = finger.localRotation;
            Layer (session, layer);
            session.Evaluate (30);
            Assert.LessOrEqual (Quaternion.Angle (baseOnly, finger.localRotation), 0.01f, "重ねても打った手の形のまま");
        }

        [Test]
        public void FramesAfterTheSource_KeepTheBase ()
        {
            PoseSession session = OpenWithBase ();
            session.Evaluate (120);
            Assert.IsNull (session.CommitPose ());

            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = ToBvh (Generated (Quaternion.identity, Vector3.zero), skeleton);
            AnimationClip layer = NewClip ();
            Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, false, true, false, out PoseSession.OverrideImportResult result));
            Assert.AreEqual (121, result.frameCount, "元のクリップの長さまで");

            List<Pose> Read (bool layered, int frame)
            {
                session.overrideWriteIndex = layered ? 0 : -1;
                session.stack.Find<OverrideLayer> ().weight = layered ? 1 : 0;
                session.Evaluate (frame);
                return WorldPose (session);
            }
            Layer (session, layer);
            AssertSamePose (Read (false, 90), Read (true, 90), "取り込み元（1 秒）より後ろは元のまま", 1e-4f, 0.01f);
            List<Pose> inside = Read (true, 30);
            List<Pose> baseInside = Read (false, 30);
            Assert.Greater (inside.Zip (baseInside, (a, b) => Quaternion.Angle (a.rotation, b.rotation)).Max (), 5, "範囲の中は動いている");
        }

        /// <summary>元のクリップ（0・30・60F に基準姿勢のキー）</summary>
        PoseSession OpenWithKeys ()
        {
            PoseSession session = OpenWithBase ();
            foreach (int frame in new[] { 30, 60 }) {
                session.Evaluate (frame);
                Assert.IsNull (session.CommitPose ());
            }
            return session;
        }

        static PoseSession.OverrideImportOptions KeepKeys ()
        {
            return new PoseSession.OverrideImportOptions { fingers = false, keepKeys = true };
        }

        [Test]
        public void KeepKeys_KeyFramesKeepTheKeyedPose_AndTheDeviationIsReported ()
        {
            PoseSession session = OpenWithKeys ();
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = ToBvh (Generated (Quaternion.identity, Vector3.zero), skeleton);
            AnimationClip layer = NewClip ();
            Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));
            CollectionAssert.AreEqual (new[] { 0, 30, 60 }, result.keyDeviations.Select (d => d.frame).ToArray (), "元のキーのフレームごとに返す");
            Assert.Greater (result.keyDeviations.Max (d => d.distance), 0.05f, "寄せる前は動いた分ずれている");

            List<Pose> Read (bool layered, int frame)
            {
                session.overrideWriteIndex = layered ? 0 : -1;
                session.stack.Find<OverrideLayer> ().weight = layered ? 1 : 0;
                session.Evaluate (frame);
                return WorldPose (session);
            }
            Layer (session, layer);
            foreach (int frame in new[] { 0, 30, 60 }) AssertSamePose (Read (false, frame), Read (true, frame), frame + "F は元のキーの姿勢", 0.001f, 0.1f);
            List<Pose> between = Read (true, 15);
            List<Pose> baseBetween = Read (false, 15);
            Assert.Greater (between.Zip (baseBetween, (a, b) => Quaternion.Angle (a.rotation, b.rotation)).Max (), 5, "キーから離れた所は取り込んだ動き");
        }

        [Test]
        public void KeepKeys_BlendsIntoAShiftedMotion_WithoutPops ()
        {
            // キーの姿勢から腕と背骨を 10cm ほどずらしたままの入力。キーの前後 0.25 秒でずれへ移る
            PoseSession session = OpenWithKeys ();
            AnimationClip shifted = NewClip ();
            SetCurve (shifted, "Left Arm Down-Up", t => 0.3f);
            SetCurve (shifted, "Right Arm Down-Up", t => 0.3f);
            SetCurve (shifted, "Spine Front-Back", t => 0.2f);
            float[] root = { 0, rest_.bodyPosition.y, 0, 0, 0, 0, 1 };
            string[] names = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (int i = 0; i < names.Length; i++) SetCurve (shifted, names[i], t => root[i]);
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = ToBvh (shifted, skeleton);
            AnimationClip layer = NewClip ();
            Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));
            Assert.Greater (result.keyDeviations.Max (d => d.distance), 0.08f, "キーで手が 10cm ほどずれた入力になっている");

            Layer (session, layer);
            MotionLintReport report = MotionLint.Run (session, 0, 60);
            Assert.IsNull (report.error);
            Assert.AreEqual (0, report.Count (MotionLint.kPop), report.ToJson (false));
            Assert.AreEqual (0, report.Count (MotionLint.kFootSlide), report.ToJson (false));
            Assert.AreEqual (0, report.Count (MotionLint.kHipSpeed), report.ToJson (false));
        }

        [Test]
        public void AlignRoot_PutsTheFirstFrameOnTheBaseBody ()
        {
            // 元: 体が 90° 回って横へずれた所にいる。BVH: 原点で +Z を向く（Kimodo の出力と同じそろえ方）
            Quaternion turned = Quaternion.Euler (0, 90, 0);
            Vector3 moved = new Vector3 (0.5f, 0, 0.3f);
            PoseSession session = OpenImported (Generated (turned, moved));
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_, 0.9f);
            Bvh bvh = ToBvh (Generated (Quaternion.identity, Vector3.zero), skeleton);
            Transform hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips);
            session.Evaluate (0);
            Pose before = new Pose (hips.position, hips.rotation);

            foreach (bool align in new[] { true, false }) {
                AnimationClip layer = NewClip ();
                Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, false, align, false, out PoseSession.OverrideImportResult result));
                PoseSession check = OpenImported (Generated (turned, moved));
                Layer (check, layer);
                check.Evaluate (0);
                Transform checkHips = check.stage.editingRig.GetEditingBone (HumanBodyBones.Hips);
                float distance = Vector3.Distance (before.position, checkHips.position);
                float angle = Quaternion.Angle (before.rotation, checkHips.rotation);
                if (align) {
                    Assert.LessOrEqual (distance, 0.001f, "最初のフレームの腰の場所が元と同じ");
                    Assert.LessOrEqual (angle, 0.1f, "最初のフレームの腰の向きが元と同じ");
                }
                else {
                    Assert.Greater (angle, 45, "合わせなければ BVH の向きのまま（確かめたい入力になっている）");
                }
            }
        }
    }

}
