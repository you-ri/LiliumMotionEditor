using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 既存の Humanoid のモーションを編集用リグの値へ読み込む（S7）
    /// </summary>
    public class HumanoidImportTests : RigTestFixture
    {
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
            EditRigDefinition definition = CreateDefinition ();
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = CreateEditingRig (definition, display);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            foreach (PoseTarget target in targets) Keep (target);

            // 表示モデルへ当てる Humanoid のモーション（muscle のカーブ）
            AnimationClip source = new AnimationClip { frameRate = 30 };
            created_.Add (source);
            SetMuscle (source, "Left Arm Down-Up", 0, 0.5f);
            SetMuscle (source, "Right Upper Leg Front-Back", 0.4f, -0.3f);
            SetMuscle (source, "Spine Front-Back", 0.2f, 0.2f);
            Assert.IsTrue (source.humanMotion, "Humanoid のクリップになっている");
            Assert.IsNull (HumanoidImport.GetProblem (source));

            ClipSampler sampler = new ClipSampler (display);
            Keep (sampler);
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (destination);

            PoseImport.Result result = HumanoidImport.Import (source, destination, rig, solver, time => sampler.Sample (source, time), targets);

            Assert.AreEqual (Mathf.RoundToInt (source.length * 60) + 1, result.frameCount);
            Assert.Less (result.maxError, 1e-3f, "読み込んだ値で解き直した姿勢");
            Assert.IsTrue (result.notes.Any (n => n.Contains ("30fps")), string.Join ("\n", result.notes));
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (destination);
            Assert.IsNotEmpty (bindings);
            Assert.IsTrue (bindings.All (EditingClip.IsRigBinding), "書くのは編集用リグのカーブだけ");
        }

        /// <summary>
        /// ルートの移動は、段がその場で当てても腰の位置へ足し戻す
        /// </summary>
        [Test]
        public void RootMotionIsMovedIntoHips ()
        {
            EditRigDefinition definition = CreateDefinition ();
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = CreateEditingRig (definition, display);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            foreach (PoseTarget target in targets) Keep (target);

            // 前へ 1 進む（Humanoid の長さなので humanScale 倍になる）
            AnimationClip source = new AnimationClip { frameRate = 60 };
            created_.Add (source);
            SetMuscle (source, "Spine Front-Back", 0, 0);
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.y"), AnimationCurve.Constant (0, 0.5f, 1));
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.z"), AnimationCurve.Linear (0, 0, 0.5f, 1));
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootQ.w"), AnimationCurve.Constant (0, 0.5f, 1));

            ClipSampler sampler = new ClipSampler (display);
            Keep (sampler);
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
            Keep (editing);
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

        /// <summary>Animator の GameObject を modelYaw だけ回して置いた prefab を開く（Neko2 は Y 270°）</summary>
        PoseSession OpenSession (float modelYaw)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, modelYaw, 0);
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            return Keep (new PoseSession (prefab, definition));
        }

        /// <summary>左右で違う姿勢で、体を鉛直軸まわりに 40° 回したクリップ（ゲームのキャラのクリップと同じく、向きの基準は元のまま）</summary>
        AnimationClip CreateTurnedClip ()
        {
            AnimationClip source = new AnimationClip { frameRate = 60 };
            created_.Add (source);
            SetMuscle (source, "Left Upper Leg Front-Back", 0.4f, 0.4f);
            SetMuscle (source, "Right Arm Down-Up", 0.5f, 0.5f);
            SetMuscle (source, "Spine Twist Left-Right", 0.3f, 0.3f);
            Quaternion facing = Quaternion.Euler (0, 40, 0);
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootQ." + axes[i]), AnimationCurve.Constant (0, 10 / 30f, facing[i]));
            }
            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.y"), AnimationCurve.Constant (0, 10 / 30f, 1));
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (source);
            settings.loopBlendOrientation = true;
            settings.keepOriginalOrientation = true;
            AnimationUtility.SetAnimationClipSettings (source, settings);
            return source;
        }

        static readonly HumanBodyBones[] kMeasuredBones = {
            HumanBodyBones.Hips, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.Head,
        };

        /// <summary>clip を窓（Editing Rig の段）で開いた 0F の、編集用の体の骨の場所（体のルートから見て）</summary>
        static Vector3[] MeasureOpened (PoseSession session, AnimationClip clip)
        {
            session.clip = clip;
            session.Evaluate (0);
            EditingRig rig = session.stage.editingRig;
            Transform root = rig.root.transform;
            return kMeasuredBones.Select (b => root.InverseTransformPoint (rig.GetEditingBone (b).position)).ToArray ();
        }

        /// <summary>窓の「取込」と同じ経路で取り込んだクリップ</summary>
        AnimationClip ImportLikeTheWindow (PoseSession session, AnimationClip source)
        {
            session.clip = source;
            AnimationClip destination = new AnimationClip { frameRate = 60 };
            created_.Add (destination);
            HumanoidImport.Import (session.stage.ReadHumanoidClip (source), destination, session.stage.editingRig, session.stage.editingRigSolver,
                time => session.stack.EvaluateThroughHumanoid (time), session.targets.ToList (), session.stage.SnapCarriedPoints);
            return destination;
        }

        static void AssertSamePose (Vector3[] expected, Vector3[] actual, string label)
        {
            for (int i = 0; i < kMeasuredBones.Length; i++) {
                Assert.Less (Vector3.Distance (expected[i], actual[i]), 0.01f,
                    label + " " + kMeasuredBones[i] + "（期待 " + expected[i].ToString ("F3") + "・実際 " + actual[i].ToString ("F3") + "）");
            }
        }

        /// <summary>
        /// Animator の GameObject ごと回して置いた prefab（Neko2 は Y 270°）でも、取り込んだ姿勢は元のクリップを窓で見た姿勢と同じ向き。
        /// 体の向きの差に置き方の回転が混ざり、取り込むと体が 90° 回っていた（11-10 不具合）
        /// </summary>
        [TestCase (0f)]
        [TestCase (270f)]
        public void RotatedModel_ImportKeepsTheFacing (float modelYaw)
        {
            PoseSession session = OpenSession (modelYaw);
            AnimationClip source = CreateTurnedClip ();
            Vector3[] expected = MeasureOpened (session, source);
            AnimationClip imported = ImportLikeTheWindow (session, source);
            AssertSamePose (expected, MeasureOpened (session, imported), "取込");
        }

        /// <summary>焼いたクリップを左右反転するキャラの受け口（試験用）。どのキャラでも Humanoid のクリップを反転して読む</summary>
        sealed class MirrorReadHook : IHumanoidBakeHook, IHumanoidReadHook
        {
            public bool ShouldBake (HumanoidBakeContext context, EditorCurveBinding binding) { return true; }
            public void PostProcess (HumanoidBakeContext context, AnimationClip clip) { }
            public bool MirrorsHumanoidClips (GameObject model) { return true; }
        }

        /// <summary>
        /// 反転して読むキャラ（左向きのモデル）では、Humanoid のクリップを窓で開いた姿勢も、取り込んだ姿勢も、左右反転したクリップを当てた姿勢。
        /// 焼くときにもう一度反転するので、取り込んで焼くと元に戻る（反転せずに読むと、取り込んで焼くたびに左右が入れ替わった。11-10 不具合）
        /// </summary>
        [TestCase (0f)]
        [TestCase (270f)]
        public void MirroringCharacter_ReadsHumanoidClipsMirrored (float modelYaw)
        {
            AnimationClip source = CreateTurnedClip ();
            AnimationClip mirrored = HumanoidMirror.CreateMirroredCopy (source);
            created_.Add (mirrored);
            Vector3[] expected = MeasureOpened (OpenSession (modelYaw), mirrored);
            Vector3[] plain = MeasureOpened (OpenSession (modelYaw), source);
            Assert.Greater (Vector3.Distance (expected[1], plain[1]), 0.05f, "反転すると左足の場所が変わる（試験のクリップが左右で違う）");

            MirrorReadHook hook = new MirrorReadHook ();
            HumanoidBakeHooks.Register (hook);
            try {
                PoseSession session = OpenSession (modelYaw);
                Assert.IsTrue (session.stage.mirrorHumanoidClips);
                AssertSamePose (expected, MeasureOpened (session, source), "窓で開いた");
                AnimationClip imported = ImportLikeTheWindow (session, source);
                AssertSamePose (expected, MeasureOpened (session, imported), "取り込んだ");
            }
            finally {
                HumanoidBakeHooks.Unregister (hook);
            }
        }

        static void SetMuscle (AnimationClip clip, string muscle, float start, float end)
        {
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), muscle), AnimationCurve.Linear (0, start, 10 / 30f, end));
        }
    }

}
