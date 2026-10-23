using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// キーの姿勢を外部の骨格へ写して BVH に書き出す（S73）
    /// </summary>
    public class ExternalExportTests
    {
        const float kPositionTolerance = 0.02f;

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

        PoseSession Open (AnimationClip clip)
        {
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = clip;
            return session;
        }

        static void SetMuscle (AnimationClip clip, string name, System.Func<float, float> value)
        {
            AnimationCurve curve = new AnimationCurve ();
            for (int f = 0; f <= 60; f += 2) curve.AddKey (f / 60f, value (f / 60f));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name), curve);
        }

        /// <summary>腕と脚と背骨を動かした Humanoid の動きを、編集用クリップへ取り込んだもの（全フレームにキーがある）</summary>
        PoseSession OpenMoving ()
        {
            AnimationClip generated = NewClip ();
            SetMuscle (generated, "Spine Front-Back", t => 0.3f * Mathf.Sin (t * Mathf.PI * 2));
            SetMuscle (generated, "Left Arm Down-Up", t => -0.4f + 0.6f * t);
            SetMuscle (generated, "Right Forearm Stretch", t => 0.5f * Mathf.Cos (t * Mathf.PI));
            SetMuscle (generated, "Left Upper Leg Front-Back", t => 0.4f * Mathf.Sin (t * Mathf.PI * 2));
            SetMuscle (generated, "Right Lower Leg Stretch", t => -0.5f * t);

            PoseSession session = Open (NewClip ());
            List<PoseTarget> targets = RigTargets.Create (session.stage.editingRig, session.stage.editingRigSolver);
            Transform display = session.stage.animator.transform;
            display.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            using (ClipSampler sampler = new ClipSampler (session.stage.animator)) {
                HumanoidImport.Import (generated, session.clip, session.stage.editingRig, session.stage.editingRigSolver, time => {
                    sampler.Sample (generated, time);
                    display.SetLocalPositionAndRotation (position, rotation);
                }, targets);
            }
            foreach (PoseTarget target in targets) target.Dispose ();
            session.Evaluate (0);
            return session;
        }

        static readonly HumanBodyBones[] kEnds = { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.Head };

        [Test]
        public void ExportedPoses_KeepHandsAndFeet_ScaledByBodySize ()
        {
            PoseSession session = OpenMoving ();
            // 体の大きさが 1.25 倍の外部の骨格（腕・脚の比は同じ）
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_, 1.25f);
            int[] frames = { 0, 20, 40, 60 };
            Assert.IsNull (session.ExportToSkeleton (skeleton, frames, out Bvh bvh, out List<int> exported));
            CollectionAssert.AreEqual (frames, exported);
            Assert.AreEqual (frames.Length, bvh.frames.Count, "書いたフレームだけ BVH のフレームになる");
            Assert.AreEqual (1 / 60f, bvh.frameTime, 1e-6f);
            Assert.AreEqual (0, session.frame, "書き出しの前のフレームへ戻す");

            // 書いた BVH を読み直して、外部の骨格に当てる
            Bvh read = Bvh.Parse (bvh.ToText (), out string error);
            Assert.IsNull (error);
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out error);
            Assert.IsNull (error);
            disposables_.Add (instance);
            Dictionary<HumanBodyBones, Transform> external = new Dictionary<HumanBodyBones, Transform> ();
            for (int j = 0; j < skeleton.joints.Count; j++) {
                if (skeleton.joints[j].human != HumanBodyBones.LastBone) external[skeleton.joints[j].human] = instance.transforms[j];
            }

            Animator character = session.stage.animator;
            for (int i = 0; i < frames.Length; i++) {
                session.Evaluate (frames[i]);
                Assert.IsTrue (session.stage.ApplyHumanoidPose ());
                skeleton.Apply (instance, read, i);
                Vector3 characterHips = character.GetBoneTransform (HumanBodyBones.Hips).position;
                Vector3 externalHips = external[HumanBodyBones.Hips].position;
                foreach (HumanBodyBones bone in kEnds) {
                    // 腰からの場所を体の大きさで割って比べ、キャラの大きさの長さに戻す
                    Vector3 c = character.transform.InverseTransformVector (character.GetBoneTransform (bone).position - characterHips) / character.humanScale;
                    Vector3 e = instance.root.transform.InverseTransformVector (external[bone].position - externalHips) / instance.humanScale;
                    Assert.LessOrEqual (Vector3.Distance (c, e) * character.humanScale, kPositionTolerance, frames[i] + " フレーム目の " + bone);
                }
            }
        }

        [Test]
        public void WithoutFrames_ExportsTheKeyFrames_AndNoKeysIsAReason ()
        {
            PoseSession session = Open (NewClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsNotNull (session.ExportToSkeleton (skeleton, null, out Bvh bvh, out List<int> exported), "キーの無いクリップは理由を返す");
            Assert.IsNull (bvh);

            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Assert.IsNull (session.CommitPose ());
            Assert.IsNull (session.ExportToSkeleton (skeleton, null, out bvh, out exported));
            CollectionAssert.AreEqual (new[] { 0, 30 }, exported, "キーのあるフレームだけ");
            Assert.AreEqual (2, bvh.frames.Count);
        }
    }

}
