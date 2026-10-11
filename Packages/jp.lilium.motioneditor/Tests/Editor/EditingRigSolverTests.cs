using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの解決（コントロール → 骨）と、その逆（骨 → コントロール）
    /// </summary>
    public class EditingRigSolverTests : RigTestFixture
    {
        const float kPositionTolerance = 1e-4f;
        const float kAngleTolerance = 0.01f;

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = CreateDefinition ();
            Random.InitState (1234);
        }

        EditingRig CreateRig (bool withChest = true, bool twistFrames = false, float size = 1)
        {
            return CreateEditingRig (definition_, CreateCharacter (withChest, size, twistFrames: twistFrames));
        }

        static Transform Bone (EditingRig rig, HumanBodyBones bone)
        {
            return rig.GetEditingBone (bone);
        }

        static Transform Control (EditingRig rig, string path)
        {
            return rig.FindControl (path);
        }

        /// <summary>
        /// 体の骨を適当に曲げる（腰は動かす）
        /// </summary>
        static void RandomPose (EditingRig rig)
        {
            foreach (Transform bone in rig.editingBones) {
                bone.localRotation = bone.localRotation * Quaternion.Euler (Random.Range (-40f, 40f), Random.Range (-40f, 40f), Random.Range (-40f, 40f));
            }
            Bone (rig, HumanBodyBones.Hips).position += new Vector3 (0.1f, -0.2f, 0.3f);
        }

        [Test]
        public void RestPoseCapturesToIdentityDeltas ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();

            Assert.AreEqual (rig.binding.fk.Count (f => f.enabled), solver.fkCount);
            foreach (RigBinding.Fk fk in rig.binding.fk.Where (f => f.enabled)) {
                Transform control = Control (rig, fk.path);
                Assert.That (Quaternion.Angle (Quaternion.identity, control.localRotation), Is.LessThan (kAngleTolerance), fk.path);
                Assert.That (control.localPosition.magnitude, Is.LessThan (kPositionTolerance), fk.path);
            }
        }

        [Test]
        public void FkCaptureAndSolveRoundTrip ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            RandomPose (rig);
            var expected = Snapshot (rig);

            solver.Capture ();
            rig.ResetBonesToRest ();
            solver.Solve ();

            AssertSamePose (expected, rig, kPositionTolerance, kAngleTolerance);
        }

        [Test]
        public void DeltasGiveSameShapeOnCharactersWithDifferentBoneFrames ()
        {
            EditingRig a = CreateRig ();
            EditingRig b = CreateRig (twistFrames: true);
            EditingRigSolver solverA = new EditingRigSolver (a);
            EditingRigSolver solverB = new EditingRigSolver (b);

            // 前提: 基準姿勢の形（ルートから見た骨の位置）は同じで、骨の軸だけが違う
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand }) {
                Vector3 restA = a.GetRestRootPosition (Bone (a, bone));
                Vector3 restB = b.GetRestRootPosition (Bone (b, bone));
                Assert.That (Vector3.Distance (restA, restB), Is.LessThan (kPositionTolerance), bone + " の基準位置 " + restA.ToString ("F4") + " / " + restB.ToString ("F4"));
            }
            Assert.That (Quaternion.Angle (a.GetRestRootRotation (Bone (a, HumanBodyBones.LeftUpperArm)), b.GetRestRootRotation (Bone (b, HumanBodyBones.LeftUpperArm))),
                Is.GreaterThan (1), "骨の軸が違っていない");
            // 差分 0 で解いても形は変わらない
            solverB.Capture ();
            b.ResetBonesToRest ();
            solverB.Solve ();
            Vector3 restHand = b.GetRestRootPosition (Bone (b, HumanBodyBones.LeftHand));
            Vector3 solvedHand = b.root.transform.InverseTransformPoint (Bone (b, HumanBodyBones.LeftHand).position);
            Assert.That (Vector3.Distance (restHand, solvedHand), Is.LessThan (kPositionTolerance), "差分 0 で解いた手 " + restHand.ToString ("F4") + " / " + solvedHand.ToString ("F4"));

            RandomPose (a);
            solverA.Capture ();

            // 同じ値（パスが同じ）を b のコントロールへ写す
            foreach (RigBinding.Fk fk in a.binding.fk.Where (f => f.enabled)) {
                Transform from = Control (a, fk.path);
                Transform to = Control (b, fk.path);
                to.localRotation = from.localRotation;
                to.localPosition = from.localPosition;
            }
            b.ResetBonesToRest ();
            solverB.Solve ();

            // 骨の向き（子へのベクトル）がルートから見て一致する
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperLeg, HumanBodyBones.Spine, HumanBodyBones.Neck }) {
                HumanBodyBones child = bone == HumanBodyBones.LeftUpperArm ? HumanBodyBones.LeftLowerArm
                    : bone == HumanBodyBones.LeftLowerArm ? HumanBodyBones.LeftHand
                    : bone == HumanBodyBones.RightUpperLeg ? HumanBodyBones.RightLowerLeg
                    : bone == HumanBodyBones.Spine ? HumanBodyBones.Chest
                    : HumanBodyBones.Head;
                Vector3 dirA = a.root.transform.InverseTransformDirection (Bone (a, child).position - Bone (a, bone).position);
                Vector3 dirB = b.root.transform.InverseTransformDirection (Bone (b, child).position - Bone (b, bone).position);
                Assert.That (Vector3.Angle (dirA, dirB), Is.LessThan (kAngleTolerance), bone.ToString ());
            }
            // 腰の位置も一致する
            Vector3 hipsA = a.root.transform.InverseTransformPoint (Bone (a, HumanBodyBones.Hips).position);
            Vector3 hipsB = b.root.transform.InverseTransformPoint (Bone (b, HumanBodyBones.Hips).position);
            Assert.That (Vector3.Distance (hipsA, hipsB), Is.LessThan (kPositionTolerance));
        }

        [Test]
        public void FkValuesAreRelativeToHumanParent ()
        {
            EditingRig full = CreateRig (withChest: true);
            EditingRig minimal = CreateRig (withChest: false);
            EditingRigSolver solverFull = new EditingRigSolver (full);
            EditingRigSolver solverMinimal = new EditingRigSolver (minimal);

            Assert.AreEqual (Bone (full, HumanBodyBones.Chest), solverFull.GetFkParent (Bone (full, HumanBodyBones.LeftUpperArm)));
            Assert.AreEqual (Bone (full, HumanBodyBones.Neck), solverFull.GetFkParent (Bone (full, HumanBodyBones.Head)));
            Assert.IsNull (solverFull.GetFkParent (Bone (full, HumanBodyBones.Hips)));
            // 胸と首が無いキャラでは、その先の骨は背骨を基準にする
            Assert.AreEqual (Bone (minimal, HumanBodyBones.Spine), solverMinimal.GetFkParent (Bone (minimal, HumanBodyBones.LeftUpperArm)));
            Assert.AreEqual (Bone (minimal, HumanBodyBones.Spine), solverMinimal.GetFkParent (Bone (minimal, HumanBodyBones.Head)));
        }

        [Test]
        public void RotatingParentControlCarriesChildren ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            RandomPose (rig);
            solver.Capture ();
            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            Transform shoulder = Bone (rig, HumanBodyBones.LeftUpperArm);
            Quaternion elbowFromShoulder = Quaternion.Inverse (shoulder.rotation) * elbow.rotation;
            Quaternion elbowValue = Control (rig, RigPaths.Fk (HumanBodyBones.LeftLowerArm)).localRotation;

            // 肩の値だけを変えて解くと、肘は肩に付いて動く（肩から見た向きは変わらない）
            Transform shoulderControl = Control (rig, RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            shoulderControl.localRotation = shoulderControl.localRotation * Quaternion.Euler (0, 0, 45);
            rig.ResetBonesToRest ();
            solver.Solve ();
            Assert.That (Quaternion.Angle (elbowFromShoulder, Quaternion.Inverse (shoulder.rotation) * elbow.rotation), Is.LessThan (kAngleTolerance));

            // 骨を直接回して肩の値だけ捉え直しても、肘の値は変わらない
            shoulder.rotation = Quaternion.AngleAxis (30, Vector3.up) * shoulder.rotation;
            Assert.IsTrue (solver.CaptureFk (shoulder));
            Assert.IsTrue (solver.CaptureFk (elbow));
            Assert.That (Quaternion.Angle (elbowValue, Control (rig, RigPaths.Fk (HumanBodyBones.LeftLowerArm)).localRotation), Is.LessThan (kAngleTolerance));
        }

        [Test]
        public void MissingIntermediateBonesKeepSharedShapeWhenTheyAreNotRotated ()
        {
            EditingRig full = CreateRig (withChest: true);
            EditingRig minimal = CreateRig (withChest: false, twistFrames: true);
            EditingRigSolver solverFull = new EditingRigSolver (full);
            EditingRigSolver solverMinimal = new EditingRigSolver (minimal);

            // 胸と首は回さず、ほかの骨に値を入れる
            foreach (RigBinding.Fk fk in full.binding.fk.Where (f => f.enabled)) {
                bool skipped = fk.control.bone == HumanBodyBones.Chest || fk.control.bone == HumanBodyBones.Neck;
                Control (full, fk.path).localRotation = skipped ? Quaternion.identity : Random.rotation;
            }
            full.ResetBonesToRest ();
            solverFull.Solve ();
            foreach (RigBinding.Fk fk in minimal.binding.fk.Where (f => f.enabled)) {
                Control (minimal, fk.path).localRotation = Control (full, fk.path).localRotation;
            }
            minimal.ResetBonesToRest ();
            solverMinimal.Solve ();

            // 腕の向きは、ルートから見た基準姿勢の差の分だけずれる（胸の有無で肩の位置が違う）ので、回転そのもので比べる
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, HumanBodyBones.Head, HumanBodyBones.RightFoot }) {
                Quaternion deltaFull = Quaternion.Inverse (full.root.transform.rotation) * Bone (full, bone).rotation * Quaternion.Inverse (full.GetRestRootRotation (Bone (full, bone)));
                Quaternion deltaMinimal = Quaternion.Inverse (minimal.root.transform.rotation) * Bone (minimal, bone).rotation * Quaternion.Inverse (minimal.GetRestRootRotation (Bone (minimal, bone)));
                Assert.That (Quaternion.Angle (deltaFull, deltaMinimal), Is.LessThan (kAngleTolerance), bone.ToString ());
            }
        }

        [Test]
        public void HipsPositionIsNormalizedByHumanScale ()
        {
            EditingRig small = CreateRig (size: 1);
            EditingRig large = CreateRig (size: 2);
            EditingRigSolver solverSmall = new EditingRigSolver (small);
            EditingRigSolver solverLarge = new EditingRigSolver (large);
            Assert.That (large.humanScale, Is.GreaterThan (small.humanScale * 1.5f));

            // 体の大きさに比例して腰を動かすと、大きさが違っても保存値は同じ
            foreach (EditingRig rig in new[] { small, large }) {
                Bone (rig, HumanBodyBones.Hips).position += rig.root.transform.TransformDirection (new Vector3 (0.1f, -0.2f, 0.05f)) * rig.humanScale;
            }
            solverSmall.Capture ();
            solverLarge.Capture ();
            Vector3 hipsSmall = Control (small, RigPaths.Fk (HumanBodyBones.Hips)).localPosition;
            Vector3 hipsLarge = Control (large, RigPaths.Fk (HumanBodyBones.Hips)).localPosition;
            Assert.That (hipsSmall.magnitude, Is.GreaterThan (0.1f), "前提: 腰が動いた");
            Assert.That (Vector3.Distance (hipsSmall, hipsLarge), Is.LessThan (1e-3f));
        }

        [Test]
        public void CaptureSingleControls ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            elbow.localRotation = elbow.localRotation * Quaternion.Euler (0, 50, 0);

            Assert.IsTrue (solver.CaptureFk (elbow));
            Assert.That (Quaternion.Angle (Quaternion.identity, Control (rig, RigPaths.Fk (HumanBodyBones.LeftLowerArm)).localRotation), Is.GreaterThan (49));
            // 変えていない骨の値はそのまま
            Assert.That (Quaternion.Angle (Quaternion.identity, Control (rig, RigPaths.Fk (HumanBodyBones.RightLowerArm)).localRotation), Is.LessThan (kAngleTolerance));

            Assert.IsFalse (solver.CaptureFk (rig.root.transform));
        }
    }

}
