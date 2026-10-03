using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using Lilium;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの解決（コントロール → 骨）と、その逆（骨 → コントロール）
    /// </summary>
    public class EditingRigSolverTests
    {
        const float kPositionTolerance = 1e-4f;
        const float kAngleTolerance = 0.01f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<EditingRig> rigs_ = new List<EditingRig> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            // 2 本骨 IK の組を見るテストなので、既定（全身 IK）から切り替える
            definition_.solver = RigSolver.TwoBoneIk;
            created_.Add (definition_);
            Random.InitState (1234);
        }

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
        /// キャラの prefab でよくあるように、Animator の GameObject を Y270 に回して置く。
        /// twistFrames なら骨の軸の向き（ローカルの回転）をばらばらにする（位置は変えない。体の形は同じで骨の軸だけ違うキャラ）
        /// </summary>
        EditingRig CreateRig (bool withChest = true, bool twistFrames = false, float size = 1)
        {
            GameObject prefabRoot = new GameObject ("Prefab");
            created_.Add (prefabRoot);
            Animator display = TestSkeleton.CreateHumanoid (created_, withChest, size);
            display.transform.SetParent (prefabRoot.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            if (twistFrames) TwistFrames (display.GetBoneTransform (HumanBodyBones.Hips));

            EditingRig rig = EditingRig.Create (definition_, display, name => new GameObject (name));
            rigs_.Add (rig);
            return rig;
        }

        static void TwistFrames (Transform hips)
        {
            // 回す前に全部の骨の位置を取っておく（親を回すと孫まで動くので、先に記録しないと形が崩れる）
            Transform[] bones = hips.GetComponentsInChildren<Transform> ();
            Dictionary<Transform, Vector3> positions = bones.ToDictionary (t => t, t => t.position);
            foreach (Transform bone in bones) {
                bone.rotation = Random.rotation;
                foreach (Transform child in bone) child.position = positions[child];
            }
        }

        static Transform Bone (EditingRig rig, HumanBodyBones bone)
        {
            return rig.GetEditingBone (bone);
        }

        static Transform Control (EditingRig rig, string path)
        {
            return rig.FindControl (path);
        }

        static void SetIkWeight (EditingRig rig, string chain, float weight)
        {
            Control (rig, RigPaths.IkChain (chain)).GetComponent<IkControl> ().ikWeight = weight;
        }

        /// <summary>
        /// 骨の姿勢を記録する（ルートから見た値）
        /// </summary>
        static Dictionary<Transform, (Vector3, Quaternion)> Snapshot (EditingRig rig)
        {
            Transform root = rig.root.transform;
            return rig.editingBones.ToDictionary (b => b, b => (root.InverseTransformPoint (b.position), Quaternion.Inverse (root.rotation) * b.rotation));
        }

        static void AssertSamePose (Dictionary<Transform, (Vector3, Quaternion)> expected, EditingRig rig)
        {
            Dictionary<Transform, (Vector3, Quaternion)> actual = Snapshot (rig);
            foreach (KeyValuePair<Transform, (Vector3, Quaternion)> pair in expected) {
                (Vector3 position, Quaternion rotation) = actual[pair.Key];
                Assert.That (Vector3.Distance (pair.Value.Item1, position), Is.LessThan (kPositionTolerance), pair.Key.name + " の位置");
                Assert.That (Quaternion.Angle (pair.Value.Item2, rotation), Is.LessThan (kAngleTolerance), pair.Key.name + " の回転");
            }
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
            Assert.AreEqual (4, solver.ikCount);
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

            AssertSamePose (expected, rig);
        }

        [Test]
        public void IkCaptureAndSolveRoundTrip ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            RandomPose (rig);
            var expected = Snapshot (rig);

            solver.Capture ();
            foreach (EditRigDefinition.IkChain chain in definition_.ikChains) SetIkWeight (rig, chain.name, 1);
            rig.ResetBonesToRest ();
            solver.Solve ();

            AssertSamePose (expected, rig);
        }

        [Test]
        public void IkOverridesFkPositionsButNotTwist ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            RandomPose (rig);
            var expected = Snapshot (rig);
            solver.Capture ();
            SetIkWeight (rig, "LeftArm", 1);

            // FK の値を崩しても、IK の組の骨の位置と先端の回転は目標とヒントで戻る（ねじれは FK のまま。IK はねじれを決めない）
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm }) {
                Control (rig, RigPaths.Fk (bone)).localRotation = Quaternion.Euler (30, 60, 90);
            }
            rig.ResetBonesToRest ();
            solver.Solve ();

            var actual = Snapshot (rig);
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand }) {
                Transform t = Bone (rig, bone);
                Assert.That (Vector3.Distance (expected[t].Item1, actual[t].Item1), Is.LessThan (kPositionTolerance), bone + " の位置");
            }
            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            Assert.That (Quaternion.Angle (expected[hand].Item2, actual[hand].Item2), Is.LessThan (kAngleTolerance));
            Assert.That (Quaternion.Angle (expected[Bone (rig, HumanBodyBones.LeftUpperArm)].Item2, actual[Bone (rig, HumanBodyBones.LeftUpperArm)].Item2),
                Is.GreaterThan (1), "崩した FK のねじれが残る");
        }

        [Test]
        public void IkReachesTargetAndBendsTowardHint ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            SetIkWeight (rig, "LeftArm", 1);

            Transform shoulder = Bone (rig, HumanBodyBones.LeftUpperArm);
            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            Vector3 goal = shoulder.position + rig.root.transform.TransformDirection (rig.bodyForward) * 0.4f + Vector3.down * 0.1f;
            Quaternion goalRotation = Quaternion.Euler (10, 20, 30);
            Assert.IsTrue (solver.SetIkTargetWorld ("LeftArm", goal, goalRotation));
            Control (rig, RigPaths.IkHint ("LeftArm")).localPosition = Vector3.up;

            solver.Solve ();

            Assert.That (Vector3.Distance (goal, hand.position), Is.LessThan (kPositionTolerance));
            Assert.That (Quaternion.Angle (goalRotation, hand.rotation), Is.LessThan (kAngleTolerance));
            // 肘は、肩→手の線に垂直にしたヒントの向きにある
            Vector3 axis = (hand.position - shoulder.position).normalized;
            Vector3 bend = elbow.position - shoulder.position;
            bend -= axis * Vector3.Dot (bend, axis);
            Vector3 up = rig.root.transform.TransformDirection (Vector3.up);
            Vector3 expectedBend = (up - axis * Vector3.Dot (up, axis)).normalized;
            Assert.That (Vector3.Dot (bend.normalized, expectedBend), Is.GreaterThan (0.9999f));

            Vector3 world;
            Quaternion rotation;
            Assert.IsTrue (solver.TryGetIkTargetWorld ("LeftArm", out world, out rotation));
            Assert.That (Vector3.Distance (goal, world), Is.LessThan (kPositionTolerance));
        }

        [Test]
        public void UnreachableTargetStretchesTowardIt ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            SetIkWeight (rig, "RightLeg", 1);

            Transform hip = Bone (rig, HumanBodyBones.RightUpperLeg);
            Transform foot = Bone (rig, HumanBodyBones.RightFoot);
            float length = Vector3.Distance (hip.position, Bone (rig, HumanBodyBones.RightLowerLeg).position)
                + Vector3.Distance (Bone (rig, HumanBodyBones.RightLowerLeg).position, foot.position);
            Vector3 direction = new Vector3 (0.3f, -1, 0.2f).normalized;
            solver.SetIkTargetWorld ("RightLeg", hip.position + direction * length * 3, Quaternion.identity);

            solver.Solve ();

            Assert.That (Vector3.Distance (hip.position + direction * length, foot.position), Is.LessThan (1e-3f));
        }

        [Test]
        public void StraightLimbUsesDefaultHint ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            // 基準姿勢は腕がまっすぐなので、ヒントは既定の向き（肘は後ろ）になる
            solver.Capture ();
            Vector3 hint = Control (rig, RigPaths.IkHint ("LeftArm")).localPosition;
            Assert.That (Vector3.Dot (hint.normalized, -rig.bodyForward), Is.GreaterThan (0.99f));
            Vector3 legHint = Control (rig, RigPaths.IkHint ("LeftLeg")).localPosition;
            Assert.That (Vector3.Dot (legHint.normalized, rig.bodyForward), Is.GreaterThan (0.99f));

            // 手を肩へ近づけると、肘は後ろへ曲がる
            SetIkWeight (rig, "LeftArm", 1);
            Transform shoulder = Bone (rig, HumanBodyBones.LeftUpperArm);
            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            solver.SetIkTargetWorld ("LeftArm", Vector3.Lerp (shoulder.position, hand.position, 0.5f), hand.rotation);
            solver.Solve ();

            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            Vector3 elbowInRoot = rig.root.transform.InverseTransformPoint (elbow.position) - rig.root.transform.InverseTransformPoint (shoulder.position);
            Assert.That (Vector3.Dot (elbowInRoot, rig.bodyForward), Is.LessThan (-0.05f));
        }

        [Test]
        public void ZeroWeightKeepsFkAndHalfWeightBlends ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            Vector3 fkHand = hand.position;
            Transform shoulder = Bone (rig, HumanBodyBones.LeftUpperArm);
            Vector3 goal = Vector3.Lerp (shoulder.position, hand.position, 0.6f) + Vector3.down * 0.2f;
            solver.SetIkTargetWorld ("LeftArm", goal, hand.rotation);

            SetIkWeight (rig, "LeftArm", 0);
            rig.ResetBonesToRest ();
            solver.Solve ();
            Assert.That (Vector3.Distance (fkHand, hand.position), Is.LessThan (kPositionTolerance));

            SetIkWeight (rig, "LeftArm", 0.5f);
            rig.ResetBonesToRest ();
            solver.Solve ();
            float toFk = Vector3.Distance (fkHand, hand.position);
            float toGoal = Vector3.Distance (goal, hand.position);
            Assert.That (toFk, Is.GreaterThan (0.01f));
            Assert.That (toGoal, Is.GreaterThan (0.01f));
        }

        /// <summary>
        /// FK で動かしている間、IK の目標は手の上に付いてくる（切り替えても姿勢が飛ばない）
        /// </summary>
        [Test]
        public void SyncPutsTheIkGoalOnTheHandWhileFkDrives ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            SetIkWeight (rig, "LeftArm", 0);

            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            elbow.rotation = Quaternion.AngleAxis (40, rig.root.transform.up) * elbow.rotation;
            solver.CaptureFk (elbow);
            solver.Solve ();
            solver.SyncIkFk ();

            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            Vector3 fkPosition = hand.position;
            Quaternion fkRotation = hand.rotation;

            SetIkWeight (rig, "LeftArm", 1);
            solver.Solve ();

            Assert.That (Vector3.Distance (fkPosition, hand.position), Is.LessThan (kPositionTolerance));
            Assert.That (Quaternion.Angle (fkRotation, hand.rotation), Is.LessThan (kAngleTolerance));
        }

        /// <summary>
        /// IK で動かしている間、手足 3 本の FK の値は IK の結果に付いてくる（切り替えても姿勢が飛ばない）
        /// </summary>
        [Test]
        public void SyncPutsTheFkValuesOnTheIkResultWhileIkDrives ()
        {
            EditingRig rig = CreateRig ();
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            SetIkWeight (rig, "LeftArm", 1);

            Transform shoulder = Bone (rig, HumanBodyBones.LeftUpperArm);
            Transform hand = Bone (rig, HumanBodyBones.LeftHand);
            solver.SetIkTargetWorld ("LeftArm", Vector3.Lerp (shoulder.position, hand.position, 0.6f), hand.rotation);
            solver.Solve ();
            solver.SyncIkFk ();

            Transform elbow = Bone (rig, HumanBodyBones.LeftLowerArm);
            Vector3 ikPosition = hand.position;
            Quaternion ikRotation = hand.rotation;
            Vector3 ikElbow = elbow.position;

            SetIkWeight (rig, "LeftArm", 0);
            rig.ResetBonesToRest ();
            solver.Solve ();

            Assert.That (Vector3.Distance (ikPosition, hand.position), Is.LessThan (kPositionTolerance));
            Assert.That (Quaternion.Angle (ikRotation, hand.rotation), Is.LessThan (kAngleTolerance));
            Assert.That (Vector3.Distance (ikElbow, elbow.position), Is.LessThan (kPositionTolerance));
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
        public void IkTargetIsNormalizedByHumanScale ()
        {
            EditingRig small = CreateRig (size: 1);
            EditingRig large = CreateRig (size: 2);
            EditingRigSolver solverSmall = new EditingRigSolver (small);
            EditingRigSolver solverLarge = new EditingRigSolver (large);
            solverSmall.Capture ();
            solverLarge.Capture ();

            Assert.That (large.humanScale, Is.GreaterThan (small.humanScale * 1.5f));
            // 同じ体の形なら、大きさが違っても保存値は同じ
            Vector3 targetSmall = Control (small, RigPaths.IkTarget ("LeftLeg")).localPosition;
            Vector3 targetLarge = Control (large, RigPaths.IkTarget ("LeftLeg")).localPosition;
            Assert.That (Vector3.Distance (targetSmall, targetLarge), Is.LessThan (1e-3f));
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

            Assert.IsTrue (solver.CaptureIk ("LeftArm"));
            Assert.IsFalse (solver.CaptureIk ("Tail"));
            Assert.IsFalse (solver.CaptureFk (rig.root.transform));
        }
    }

}
