using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// PoseBank: 姿勢 1 つ = クリップ 1 つ（*.pose.anim）の保存・一覧・読み出し（一部だけ）・置き換え・名前変更。使い捨てのフォルダに作って最後に消す
    /// </summary>
    public class PoseBankTests
    {
        const string kFolder = "Assets/__MktPoseBankTest";

        static readonly EditorCurveBinding kHandX = EditorCurveBinding.FloatCurve ("Controls/FK/RightHand", typeof (Transform), "m_LocalRotation.x");
        static readonly EditorCurveBinding kThumbX = EditorCurveBinding.FloatCurve ("Controls/FK/RightThumbProximal", typeof (Transform), "m_LocalRotation.x");
        static readonly EditorCurveBinding kIndexX = EditorCurveBinding.FloatCurve ("Controls/FK/RightIndexProximal", typeof (Transform), "m_LocalRotation.x");

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktPoseBankTest");
        }

        [TearDown]
        public void TearDown ()
        {
            AssetDatabase.DeleteAsset (kFolder);
        }

        static Dictionary<EditorCurveBinding, float> Values (float hand, float thumb, float index)
        {
            return new Dictionary<EditorCurveBinding, float> { { kHandX, hand }, { kThumbX, thumb }, { kIndexX, index } };
        }

        [Test]
        public void CreateStoresOneKeyPerCurveAndAvoidsNames ()
        {
            AnimationClip a = PoseBank.Create (kFolder, Values (0.1f, 0.2f, 0.3f), "Fist");
            AnimationClip b = PoseBank.Create (kFolder, Values (0, 0, 0), "Fist");
            Assert.AreEqual (kFolder + "/Fist.pose.anim", AssetDatabase.GetAssetPath (a));
            Assert.AreEqual (kFolder + "/Fist 1.pose.anim", AssetDatabase.GetAssetPath (b));

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (a);
            Assert.AreEqual (3, bindings.Length);
            foreach (EditorCurveBinding binding in bindings) {
                Assert.AreEqual (1, AnimationUtility.GetEditorCurve (a, binding).length, "1 キーだけ");
            }
            Assert.AreEqual (0.2f, PoseBank.Read (a)[kThumbX], 1e-6f);
        }

        [Test]
        public void ReadFiltersCurves ()
        {
            AnimationClip pose = PoseBank.Create (kFolder, Values (0.1f, 0.2f, 0.3f), "Point");
            // 指だけを選んで貼るとき
            HashSet<string> fingers = new HashSet<string> { kThumbX.path, kIndexX.path };
            Dictionary<EditorCurveBinding, float> values = PoseBank.Read (pose, b => fingers.Contains (b.path));
            CollectionAssert.AreEquivalent (new[] { kThumbX, kIndexX }, values.Keys);
            Assert.AreEqual (0.3f, values[kIndexX], 1e-6f);
        }

        [Test]
        public void ListShowsOnlyPosesGroupedBySubfolder ()
        {
            PoseBank.Create (kFolder, Values (0, 0, 0), "Open");
            PoseBank.Create (kFolder, Values (0, 0, 0), "Fist 10");
            PoseBank.Create (kFolder, Values (0, 0, 0), "Fist 2");
            PoseBank.Create (kFolder + "/Hands", Values (0, 0, 0), "Peace");
            // 編集用クリップ・ゲーム用クリップは出さない
            AnimBank.Create (kFolder, "Walk");
            AssetDatabase.CreateAsset (new AnimationClip (), kFolder + "/Walk.anim");

            List<PoseBank.Entry> entries = PoseBank.List (kFolder);
            Assert.AreEqual (new[] { "Fist 2", "Fist 10", "Open", "Peace" }, entries.Select (e => e.name).ToArray ());
            Assert.AreEqual ("Hands", entries[3].group);
            Assert.IsFalse (AnimBank.List (kFolder).Any (e => e.name.Contains ("Fist")), "AnimBank には姿勢を出さない");
        }

        [Test]
        public void OverwriteReplacesAllCurves ()
        {
            AnimationClip pose = PoseBank.Create (kFolder, Values (0.1f, 0.2f, 0.3f), "Fist");
            PoseBank.Overwrite (pose, new Dictionary<EditorCurveBinding, float> { { kHandX, 0.9f } });
            Dictionary<EditorCurveBinding, float> values = PoseBank.Read (pose);
            Assert.AreEqual (1, values.Count, "前の値は残さない");
            Assert.AreEqual (0.9f, values[kHandX], 1e-6f);
        }

        static AnimationClip MusclePose (List<Object> created, params (string attribute, float value)[] curves)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created.Add (clip);
            foreach ((string attribute, float value) in curves) {
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), attribute), AnimationCurve.Constant (0, 0, value));
            }
            return clip;
        }

        static int Muscle (string name)
        {
            return System.Array.IndexOf (HumanTrait.MuscleName, name);
        }

        [Test]
        public void ReadMusclesMapsFingerAndBodyNames ()
        {
            List<Object> created = new List<Object> ();
            try {
                // クリップの指は "LeftHand.Index.1 Stretched"、MuscleName は "Left Index 1 Stretched"
                AnimationClip pose = MusclePose (created,
                    ("LeftHand.Index.1 Stretched", -0.5f), ("RightHand.Thumb.Spread", 0.25f), ("Spine Front-Back", 0.1f));
                Assert.IsTrue (PoseBank.IsHumanoidPose (pose));
                AnimationUtility.SetEditorCurve (pose, kHandX, AnimationCurve.Constant (0, 0, 1));

                Dictionary<int, float> muscles = PoseBank.ReadMuscles (pose);
                Assert.AreEqual (3, muscles.Count, "Transform のカーブは muscle として読まない");
                Assert.AreEqual (-0.5f, muscles[Muscle ("Left Index 1 Stretched")], 1e-6f);
                Assert.AreEqual (0.25f, muscles[Muscle ("Right Thumb Spread")], 1e-6f);
                Assert.AreEqual (0.1f, muscles[Muscle ("Spine Front-Back")], 1e-6f);

                AnimationClip rigPose = PoseBank.Create (kFolder, Values (0, 0, 0), "Rig");
                Assert.IsFalse (PoseBank.IsHumanoidPose (rigPose), "編集用リグの値の姿勢は Humanoid でない");
            }
            finally {
                foreach (Object o in created) Object.DestroyImmediate (o);
            }
        }

        /// <summary>
        /// 手の形（muscle）を貼ると、その muscle が動かす指の FK だけが値を捉える。腕や体の操作値は変わらない
        /// </summary>
        [Test]
        public void ApplyMusclesCapturesOnlyTheMovedFingers ()
        {
            List<Object> created = new List<Object> ();
            List<System.IDisposable> disposables = new List<System.IDisposable> ();
            try {
                EditRigDefinition definition = EditRigDefinition.CreateDefault ();
                created.Add (definition);
                Animator display = TestSkeleton.CreateHumanoidWithFingers (created);
                EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
                disposables.Add (rig);
                EditingRigSolver solver = new EditingRigSolver (rig);
                solver.Capture ();

                HumanPoseHandler writer = new HumanPoseHandler (display.avatar, display.transform);
                disposables.Add (writer);
                HumanPose current = new HumanPose ();
                writer.GetHumanPose (ref current);

                Transform indexControl = rig.FindControl (rig.binding.FindFk (HumanBodyBones.LeftIndexIntermediate).path);
                Transform armControl = rig.FindControl (rig.binding.FindFk (HumanBodyBones.LeftLowerArm).path);
                Quaternion indexBefore = indexControl.localRotation;
                Quaternion armBefore = armControl.localRotation;

                AnimationClip fist = MusclePose (created, ("LeftHand.Index.1 Stretched", -1), ("LeftHand.Index.2 Stretched", -1), ("LeftHand.Index.3 Stretched", -1));
                HashSet<string> paths = PoseBank.ApplyMuscles (rig, solver, writer, current, PoseBank.ReadMuscles (fist));

                CollectionAssert.AreEquivalent (new[] {
                    rig.binding.FindFk (HumanBodyBones.LeftIndexProximal).path,
                    rig.binding.FindFk (HumanBodyBones.LeftIndexIntermediate).path,
                    rig.binding.FindFk (HumanBodyBones.LeftIndexDistal).path,
                }, paths, "動いた指の FK だけ");
                Assert.Greater (Quaternion.Angle (indexBefore, indexControl.localRotation), 30f, "人差し指の第二関節が曲がった");
                Assert.AreEqual (0, Quaternion.Angle (armBefore, armControl.localRotation), 1e-3f, "腕の操作値は変わらない");

                // 捉えた値で解き直すと、表示モデルの指と同じ向きになる
                solver.Solve ();
                Transform displayIndex = display.GetBoneTransform (HumanBodyBones.LeftIndexIntermediate);
                Assert.AreEqual (0, Quaternion.Angle (displayIndex.localRotation, rig.GetEditingBone (displayIndex).localRotation), 0.01f);
            }
            finally {
                for (int i = disposables.Count - 1; i >= 0; i--) disposables[i].Dispose ();
                foreach (Object o in created) {
                    if (o != null) Object.DestroyImmediate (o);
                }
            }
        }

        [Test]
        public void RenameKeepsSuffix ()
        {
            AnimationClip pose = PoseBank.Create (kFolder, Values (0, 0, 0));
            Assert.IsNull (PoseBank.Rename (pose, "Fist.pose"));
            Assert.AreEqual (kFolder + "/Fist.pose.anim", AssetDatabase.GetAssetPath (pose));
            PoseBank.Create (kFolder, Values (0, 0, 0), "Open");
            StringAssert.Contains ("同じ名前", PoseBank.Rename (pose, "Open"));
            StringAssert.Contains ("空", PoseBank.Rename (pose, " "));
        }
    }

}
