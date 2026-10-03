using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// Transform のパネル（S9）: 数値欄の値の読み書き・行ごとの Reset・左右反転
    /// </summary>
    public class MirrorValuesTests
    {
        const float kPositionTolerance = 1e-3f;
        const float kAngleTolerance = 0.05f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<EditingRig> rigs_ = new List<EditingRig> ();

        [TearDown]
        public void TearDown ()
        {
            foreach (EditingRig rig in rigs_) rig.Dispose ();
            rigs_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        /// <summary>
        /// キャラの prefab でよくあるように、Animator の GameObject を Y270 に回して置く
        /// </summary>
        EditingRig CreateRig ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            // 2 本骨 IK の組を見るテストなので、既定（全身 IK）から切り替える
            definition.solver = RigSolver.TwoBoneIk;
            created_.Add (definition);
            GameObject prefabRoot = new GameObject ("Prefab");
            created_.Add (prefabRoot);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            display.transform.SetParent (prefabRoot.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            rigs_.Add (rig);
            return rig;
        }

        static Vector3 RootPosition (EditingRig rig, HumanBodyBones bone)
        {
            return rig.root.transform.InverseTransformPoint (rig.GetEditingBone (bone).position);
        }

        /// <summary>
        /// 体の左右の面で鏡に映した位置（ルートから見た）
        /// </summary>
        static Vector3 Mirror (EditingRig rig, Vector3 position)
        {
            Vector3 n = rig.bodyRight;
            float center = Vector3.Dot ((rig.GetRestRootPosition (rig.GetEditingBone (HumanBodyBones.LeftUpperArm))
                + rig.GetRestRootPosition (rig.GetEditingBone (HumanBodyBones.RightUpperArm))) * 0.5f, n);
            return position - 2 * (Vector3.Dot (position, n) - center) * n;
        }

        static void AssertMirrored (EditingRig rig, HumanBodyBones source, HumanBodyBones target)
        {
            Vector3 expected = Mirror (rig, RootPosition (rig, source));
            Vector3 actual = RootPosition (rig, target);
            Assert.That (Vector3.Distance (expected, actual), Is.LessThan (kPositionTolerance),
                target + " は " + source + " の鏡の位置 " + expected.ToString ("F4") + " / " + actual.ToString ("F4"));
        }

        [Test]
        public void MirrorNamesSwapSides ()
        {
            Assert.AreEqual ("RightUpperArm", MirrorNames.Swap ("LeftUpperArm"));
            Assert.AreEqual ("LeftLeg", MirrorNames.Swap ("RightLeg"));
            Assert.AreEqual ("hand_R", MirrorNames.Swap ("hand_L"));
            Assert.AreEqual ("weapon.l", MirrorNames.Swap ("weapon.r"));
            Assert.AreEqual ("R_foot", MirrorNames.Swap ("L_foot"));
            Assert.IsNull (MirrorNames.Swap ("Spine"));
            Assert.IsNull (MirrorNames.Swap ("Hair"), "語の中の L/R は左右の印にしない");
            Assert.IsNull (MirrorNames.Swap (null));
        }

        [Test]
        public void EulerAnglesStayNearThePreviousValue ()
        {
            Vector3 euler = EulerAngles.Closest (Quaternion.Euler (0, 190, 0), new Vector3 (0, 185, 0));
            Assert.AreEqual (190, euler.y, 0.01f, "±180° をまたいでも飛ばない");
            euler = EulerAngles.Closest (Quaternion.Euler (0, -170, 0), new Vector3 (0, -175, 0));
            Assert.AreEqual (-170, euler.y, 0.01f);
            Vector3 input = new Vector3 (100, 20, 10);
            euler = EulerAngles.Closest (Quaternion.Euler (input), input);
            Assert.That (Vector3.Distance (input, euler), Is.LessThan (0.01f), "入れた組のまま（もう 1 つの組 " + euler + " にしない）");
        }

        /// <summary>
        /// 左腕の FK を右腕へ反転すると、右手が左手の鏡の位置に来る
        /// </summary>
        [Test]
        public void MirrorFkCopiesToTheOtherSide ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            rig.GetEditingBone (HumanBodyBones.LeftUpperArm).localRotation *= Quaternion.Euler (20, 35, -40);
            rig.GetEditingBone (HumanBodyBones.LeftLowerArm).localRotation *= Quaternion.Euler (0, 60, 10);
            solver.Capture ();

            Assert.IsTrue (solver.MirrorFk (rig.GetEditingBone (HumanBodyBones.LeftUpperArm), rig.GetEditingBone (HumanBodyBones.RightUpperArm)));
            Assert.IsTrue (solver.MirrorFk (rig.GetEditingBone (HumanBodyBones.LeftLowerArm), rig.GetEditingBone (HumanBodyBones.RightLowerArm)));
            rig.ResetBonesToRest ();
            solver.Solve ();

            AssertMirrored (rig, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm);
            AssertMirrored (rig, HumanBodyBones.LeftHand, HumanBodyBones.RightHand);
        }

        /// <summary>
        /// 体の中心の骨は自分を反転する（右へひねった背骨が左へひねった形になる）
        /// </summary>
        [Test]
        public void MirrorCenterBoneFlipsItself ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            Transform spine = rig.GetEditingBone (HumanBodyBones.Spine);
            spine.rotation = rig.root.transform.rotation * Quaternion.Euler (0, 0, 25) * Quaternion.Inverse (rig.root.transform.rotation) * spine.rotation;
            solver.Capture ();
            Vector3 head = RootPosition (rig, HumanBodyBones.Head);

            Assert.IsTrue (solver.MirrorFk (spine, spine));
            rig.ResetBonesToRest ();
            solver.Solve ();

            Vector3 expected = Mirror (rig, head);
            Assert.That (Vector3.Distance (expected, RootPosition (rig, HumanBodyBones.Head)), Is.LessThan (kPositionTolerance), "頭が反対側へ");
            Assert.That (Vector3.Distance (head, expected), Is.GreaterThan (0.01f), "前提: 頭が横へ倒れている");
        }

        /// <summary>
        /// IK で動いている左腕を反転すると、右腕も IK になり、手と肘が鏡の位置に来る
        /// </summary>
        [Test]
        public void MirrorIkCopiesTargetHintAndWeight ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            solver.SwitchToIk ("LeftArm");
            Vector3 position;
            Quaternion rotation;
            solver.TryGetIkTargetWorld ("LeftArm", out position, out rotation);
            solver.SetIkTargetWorld ("LeftArm", position + rig.root.transform.TransformVector (new Vector3 (0.1f, -0.25f, 0.15f)), rotation * Quaternion.Euler (0, 40, 0));
            rig.ResetBonesToRest ();
            solver.Solve ();

            Assert.IsTrue (solver.MirrorIk ("LeftArm", "RightArm"));
            Assert.AreEqual (1, solver.GetIkWeight ("RightArm"), 1e-5f);
            rig.ResetBonesToRest ();
            solver.Solve ();

            AssertMirrored (rig, HumanBodyBones.LeftHand, HumanBodyBones.RightHand);
            AssertMirrored (rig, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm);
        }

        /// <summary>
        /// 数値欄の値: FK は保存値（位置はメートル）をそのまま出し入れする。行ごとの Reset は基準姿勢の値へ戻す
        /// </summary>
        [Test]
        public void TargetValuesSetAndReset ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            List<PoseTarget> targets = RigTargets.Create (rig, solver);
            PoseTarget hips = targets.First (t => t.mirrorKey == "Hips");
            PoseTarget leftArm = targets.First (t => t.mirrorKey == "LeftArm");

            Quaternion rotation = Quaternion.Euler (10, 20, 30);
            hips.SetValues (false, TransformChannels.Position | TransformChannels.Rotation, new Vector3 (0, 0.2f, 0), rotation);
            Vector3 position;
            Quaternion read;
            Assert.IsTrue (hips.TryGetValues (false, out position, out read));
            Assert.That (Vector3.Distance (new Vector3 (0, 0.2f, 0), position), Is.LessThan (1e-5f));
            Assert.That (Quaternion.Angle (rotation, read), Is.LessThan (kAngleTolerance));

            hips.ResetValues (false, TransformChannels.Rotation);
            hips.TryGetValues (false, out position, out read);
            Assert.That (Quaternion.Angle (Quaternion.identity, read), Is.LessThan (kAngleTolerance), "回転の行だけ戻る");
            Assert.That (position.y, Is.EqualTo (0.2f).Within (1e-5f), "位置は残る");

            // IK の目標を動かしてから Reset すると、手が基準姿勢の位置へ戻る
            leftArm.TryGetValues (false, out position, out read);
            leftArm.SetValues (false, TransformChannels.Position, position + new Vector3 (0, -0.3f, 0), read);
            leftArm.ResetValues (false, TransformChannels.Position | TransformChannels.Rotation);
            hips.ResetValues (false, TransformChannels.Position);
            rig.ResetBonesToRest ();
            solver.Solve ();
            Vector3 rest = rig.GetRestRootPosition (rig.GetEditingBone (HumanBodyBones.LeftHand));
            Assert.That (Vector3.Distance (rest, RootPosition (rig, HumanBodyBones.LeftHand)), Is.LessThan (kPositionTolerance), "手が基準姿勢へ");
            foreach (PoseTarget target in targets) target.Dispose ();
        }
    }

}
