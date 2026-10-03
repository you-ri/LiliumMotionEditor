using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 既存の Humanoid のモーションを編集用リグの値へ読み込む（S7）
    /// </summary>
    public class HumanoidImportTests
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

        [Test]
        public void GenericClipIsRejected ()
        {
            AnimationClip generic = new AnimationClip { frameRate = 60 };
            created_.Add (generic);
            AnimationUtility.SetEditorCurve (generic, EditorCurveBinding.FloatCurve ("Hips", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0));
            StringAssert.Contains ("Humanoid", HumanoidImport.GetProblem (generic));
            StringAssert.Contains ("クリップが無い", HumanoidImport.GetProblem (null));
        }

        /// <summary>
        /// 読み込んだ値で解き直すと、元のモーションを当てた姿勢に戻る（30fps の元を 60fps の格子で取り直す）
        /// </summary>
        [Test]
        public void ImportedValuesReproduceTheMotion ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            disposables_.Add (rig);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            foreach (PoseTarget target in targets) disposables_.Add (target);

            // 表示モデルへ当てる Humanoid のモーション（muscle のカーブ）
            AnimationClip source = new AnimationClip { frameRate = 30 };
            created_.Add (source);
            SetMuscle (source, "Left Arm Down-Up", 0, 0.5f);
            SetMuscle (source, "Right Upper Leg Front-Back", 0.4f, -0.3f);
            SetMuscle (source, "Spine Front-Back", 0.2f, 0.2f);
            Assert.IsTrue (source.humanMotion, "Humanoid のクリップになっている");
            Assert.IsNull (HumanoidImport.GetProblem (source));

            ClipSampler sampler = new ClipSampler (display);
            disposables_.Add (sampler);
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (destination);

            PoseImport.Result result = HumanoidImport.Import (source, destination, rig, solver, time => sampler.Sample (source, time), targets);

            Assert.AreEqual (Mathf.RoundToInt (source.length * 60) + 1, result.frameCount);
            Assert.Less (result.maxError, 1e-3f, "読み込んだ値で解き直した姿勢");
            Assert.IsTrue (result.notes.Any (n => n.Contains ("30fps")), string.Join ("\n", result.notes));
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (destination);
            Assert.IsNotEmpty (bindings);
            Assert.IsTrue (bindings.All (EditingClip.IsRigBinding), "書くのは編集用リグのカーブだけ");
            Assert.IsTrue (bindings.Where (b => b.propertyName == IkControl.kIkWeightProperty)
                .All (b => AnimationUtility.GetEditorCurve (destination, b).Evaluate (0.1f) == 0), "読み込んだ手足は FK");
        }

        /// <summary>
        /// ルートの移動は、段がその場で当てても腰の位置へ足し戻す
        /// </summary>
        [Test]
        public void RootMotionIsMovedIntoHips ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            disposables_.Add (rig);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            foreach (PoseTarget target in targets) disposables_.Add (target);

            // 前へ 1 進む（Humanoid の長さなので humanScale 倍になる）
            AnimationClip source = new AnimationClip { frameRate = 60 };
            created_.Add (source);
            SetMuscle (source, "Spine Front-Back", 0, 0);
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.y"), AnimationCurve.Constant (0, 0.5f, 1));
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.z"), AnimationCurve.Linear (0, 0, 0.5f, 1));
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootQ.w"), AnimationCurve.Constant (0, 0.5f, 1));

            ClipSampler sampler = new ClipSampler (display);
            disposables_.Add (sampler);
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (destination);
            Transform hips = rig.GetEditingBone (HumanBodyBones.Hips);
            Transform root = rig.root.transform;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (source);
            settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings (source, settings);

            HumanoidImport.Import (source, destination, rig, solver, time => sampler.Sample (source, time), targets);
            Assert.IsTrue (AnimationUtility.GetAnimationClipSettings (destination).keepOriginalPositionXZ, "ルートの扱いの設定を写す");

            ClipSampler editing = new ClipSampler (rig.animator);
            disposables_.Add (editing);
            rig.ResetBonesToRest ();
            editing.Sample (destination, 0);
            solver.Solve ();
            Vector3 start = root.InverseTransformPoint (hips.position);
            rig.ResetBonesToRest ();
            editing.Sample (destination, 0.5f);
            solver.Solve ();
            Vector3 end = root.InverseTransformPoint (hips.position);
            Assert.AreEqual (display.humanScale, end.z - start.z, 0.02f, "腰が進んだ距離");
            Assert.AreEqual (0, end.x - start.x, 0.01f);
        }

        /// <summary>
        /// 読込は、姿勢から作り直さないカーブ（武器の骨など）を任意のプロパティとして持ち越す。焼くとゲーム prefab から見たパスへ戻る
        /// </summary>
        [Test]
        public void ImportCarriesCurvesItDoesNotSolve ()
        {
            AnimationClip source = new AnimationClip ();
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (source);
            created_.Add (destination);
            SetMuscle (source, "Spine Front-Back", 0, 1);
            EditorCurveBinding weapon = EditorCurveBinding.FloatCurve ("root/hand_L/weapon", typeof (Transform), "m_LocalRotation.x");
            AnimationCurve curve = AnimationCurve.Linear (0, 0.1f, 1, 0.3f);
            AnimationUtility.SetEditorCurve (source, weapon, curve);

            List<string> notes = new List<string> ();
            Assert.AreEqual (1, HumanoidImport.CarryCurves (source, destination, null, notes), "muscle は持ち越さない");
            EditorCurveBinding carried = PropertyPlayer.ToClip (weapon);
            Assert.AreEqual ("Controls/Props/root/hand_L/weapon", carried.path);
            Assert.AreEqual (curve.keys, AnimationUtility.GetEditorCurve (destination, carried).keys);
            Assert.IsTrue (EditingClip.IsRigBinding (carried), "編集用クリップのカーブとして扱う（旧形式のカーブとして消されない）");
            Assert.IsTrue (PropertyPlayer.TryToDisplay (carried, out EditorCurveBinding display));
            Assert.AreEqual (weapon, display, "焼くと元のパスへ戻る");
        }

        static void SetMuscle (AnimationClip clip, string muscle, float start, float end)
        {
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), muscle), AnimationCurve.Linear (0, start, 10 / 30f, end));
        }
    }

}
