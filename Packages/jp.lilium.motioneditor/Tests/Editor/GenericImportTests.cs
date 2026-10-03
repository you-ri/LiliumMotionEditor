using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 骨を直接動かす Generic のクリップを、編集用リグの値に直して取り込む
    /// </summary>
    public class GenericImportTests
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

        AnimationClip CreateClip (float rate)
        {
            AnimationClip clip = new AnimationClip { frameRate = rate };
            created_.Add (clip);
            return clip;
        }

        static void SetRotation (AnimationClip clip, string path, float time0, Quaternion q0, float time1, Quaternion q1)
        {
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                AnimationCurve curve = new AnimationCurve (new Keyframe (time0, q0[i]), new Keyframe (time1, q1[i]));
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), curve);
            }
        }

        [Test]
        public void ProblemRejectsNonGenericClips ()
        {
            StringAssert.Contains ("無い", GenericImport.GetProblem (null));
            AnimationClip empty = CreateClip (60);
            StringAssert.Contains ("カーブが無い", GenericImport.GetProblem (empty));

            AnimationClip rig = CreateClip (60);
            AnimationUtility.SetEditorCurve (rig, EditorCurveBinding.FloatCurve ("Controls/FK/Head", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0));
            AnimationUtility.SetEditorCurve (rig, EditorCurveBinding.FloatCurve ("Hips", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0));
            StringAssert.Contains ("編集用リグ", GenericImport.GetProblem (rig));

            AnimationClip bones = CreateClip (60);
            AnimationUtility.SetEditorCurve (bones, EditorCurveBinding.FloatCurve ("Hips", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0));
            Assert.IsNull (GenericImport.GetProblem (bones));
        }

        /// <summary>
        /// 取り込んだクリップを当てて解くと、元のクリップを骨へ当てた姿勢に戻る（30fps の元を 60fps の格子で取り直す）。
        /// 書き出し先の古い編集用リグのカーブは消え、それ以外のカーブは残る
        /// </summary>
        [Test]
        public void ImportedValuesReproduceSourcePose ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            disposables_.Add (rig);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            ClipSampler sourceSampler = new ClipSampler (rig.animator);
            disposables_.Add (sourceSampler);
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            foreach (PoseTarget target in targets) disposables_.Add (target);

            Transform lowerArm = display.GetBoneTransform (HumanBodyBones.LeftLowerArm);
            Transform upperLeg = display.GetBoneTransform (HumanBodyBones.RightUpperLeg);
            string armPath = AnimationUtility.CalculateTransformPath (lowerArm, display.transform);
            string legPath = AnimationUtility.CalculateTransformPath (upperLeg, display.transform);

            AnimationClip source = CreateClip (30);
            SetRotation (source, armPath, 0, lowerArm.localRotation, 10 / 30f, lowerArm.localRotation * Quaternion.Euler (10, 50, 20));
            SetRotation (source, legPath, 0, upperLeg.localRotation * Quaternion.Euler (-30, 0, 5), 10 / 30f, upperLeg.localRotation);
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Transform), "m_LocalPosition.x"), AnimationCurve.Linear (0, 0, 10 / 30f, 3));
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("Nowhere", typeof (Transform), "m_LocalPosition.x"), AnimationCurve.Constant (0, 10 / 30f, 0));

            AnimationClip destination = CreateClip (60);
            EditorCurveBinding old = EditorCurveBinding.FloatCurve ("Controls/Old", typeof (Transform), "m_LocalPosition.x");
            EditorCurveBinding stray = EditorCurveBinding.FloatCurve ("Stray", typeof (Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve (destination, old, AnimationCurve.Constant (0, 0.1f, 1));
            AnimationUtility.SetEditorCurve (destination, stray, AnimationCurve.Constant (0, 0.1f, 1));

            PoseImport.Result result = GenericImport.Import (source, destination, rig, solver, time => {
                rig.ResetBonesToRest ();
                sourceSampler.Sample (source, time);
            }, targets);
            Assert.AreEqual (21, result.frameCount);
            Assert.Less (result.maxError, 1e-4f, "取り込んだ値で解き直した姿勢");
            Assert.IsTrue (result.notes.Any (n => n.Contains ("ルートモーション")), string.Join ("\n", result.notes));
            Assert.IsTrue (result.notes.Any (n => n.Contains ("無い骨")), string.Join ("\n", result.notes));
            Assert.IsTrue (result.notes.Any (n => n.Contains ("30fps")), string.Join ("\n", result.notes));
            Assert.IsNull (AnimationUtility.GetEditorCurve (destination, old), "古い編集用リグのカーブは消える");
            Assert.IsNotNull (AnimationUtility.GetEditorCurve (destination, stray), "それ以外のカーブは残る");
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (destination);
            Assert.IsTrue (bindings.Where (b => b.path != "Stray").All (EditingClip.IsRigBinding));
            Assert.IsTrue (bindings.Where (b => b.propertyName == IkControl.kIkWeightProperty).All (b => AnimationUtility.GetEditorCurve (destination, b).Evaluate (0.1f) == 0), "取り込んだ手足は FK");

            Transform root = rig.root.transform;
            Transform[] bones = { rig.GetEditingBone (HumanBodyBones.LeftLowerArm), rig.GetEditingBone (HumanBodyBones.RightUpperLeg), rig.GetEditingBone (HumanBodyBones.LeftHand) };
            ClipSampler destinationSampler = new ClipSampler (rig.animator);
            disposables_.Add (destinationSampler);
            foreach (int frame in new[] { 0, 7, 20 }) {
                float time = frame / 60f;
                rig.ResetBonesToRest ();
                sourceSampler.Sample (source, time);
                root.localPosition = Vector3.zero;
                Vector3[] expected = bones.Select (b => root.InverseTransformPoint (b.position)).ToArray ();
                Quaternion[] expectedRotation = bones.Select (b => b.rotation).ToArray ();

                rig.ResetBonesToRest ();
                destinationSampler.Sample (destination, time);
                solver.Solve ();
                for (int i = 0; i < bones.Length; i++) {
                    Assert.Less (Vector3.Distance (expected[i], root.InverseTransformPoint (bones[i].position)), 1e-4f, bones[i].name + " の位置 " + frame + "F");
                    Assert.Less (Quaternion.Angle (expectedRotation[i], bones[i].rotation), 0.05f, bones[i].name + " の向き " + frame + "F");
                }
            }
        }
    }

}
