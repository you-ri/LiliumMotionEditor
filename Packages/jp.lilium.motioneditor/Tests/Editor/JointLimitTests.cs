using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Random = UnityEngine.Random;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の角度制限（S95）: 範囲 + 遊びを越えない・遊びの中は越えるほど回しにくい・固定の軸は回らない・元の姿勢が範囲の外でも姿勢を変えない・
    /// 骨の軸の取り方に依らない・Humanoid の可動域から作る・左右の鏡写し・Burst とマネージドで同じ結果
    /// </summary>
    public class JointLimitTests : RigTestFixture
    {
        const float kAngleTolerance = 0.1f;

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = CreateDefinition ();
        }

        sealed class Setup
        {
            public Animator display;
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
            public Vector3 forward;
        }

        Setup Create (bool twistFrames = false)
        {
            Setup s = new Setup ();
            s.display = CreateCharacter (twistFrames: twistFrames);
            s.display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            s.rig = CreateEditingRig (definition_, s.display);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition_));
            s.forward = s.rig.root.transform.TransformDirection (s.rig.bodyForward).normalized;
            return s;
        }

        static Transform Bone (Setup s, HumanBodyBones bone)
        {
            return s.rig.GetEditingBone (bone);
        }

        /// <summary>全軸を同じ範囲にする</summary>
        void Limit (HumanBodyBones bone, float min, float max, float slack)
        {
            EditRigDefinition.JointLimit limit = definition_.FindFkControl (bone).limit;
            for (int axis = 0; axis < 3; axis++) {
                limit[axis].mode = EditRigDefinition.LimitMode.Range;
                limit[axis].min = min;
                limit[axis].max = max;
            }
            limit.slack = slack;
        }

        static void Pin (Setup s, HumanBodyBones bone, Vector3 offset, float weight = 1, bool rotation = false)
        {
            s.body.CapturePoint (bone);
            s.body.SetPointPositionWorld (bone, Bone (s, bone).position + offset);
            s.body.SetPositionWeight (bone, weight);
            if (rotation) s.body.SetRotationWeight (bone, 1);
        }

        /// <summary>基準姿勢へ戻して、編集用リグ → 全身 IK の順に解く</summary>
        static void Solve (Setup s)
        {
            s.body.Unsolve ();
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            s.body.CaptureInput ();
            s.body.Solve ();
        }

        /// <summary>両足を固定し、左手を上と前へ大きく引く（腕を範囲の外まで振らないと届かない）</summary>
        static void PullLeftHand (Setup s, float weight = 1)
        {
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.LeftHand, Vector3.up * 0.6f + s.forward * 0.3f, weight);
            Solve (s);
        }

        /// <summary>骨の、親の解く骨から見た T ポーズからの回り（体の向きの軸ごと・度）。ソルバーと同じ量</summary>
        static Vector3 Angles (EditingRig rig, Transform bone)
        {
            HashSet<Transform> solved = new HashSet<Transform> ();
            foreach (RigBinding.BoneControl control in rig.binding.boneControls) {
                if (control.enabled && control.fullBodySolve) solved.Add (rig.GetEditingBone (control.bone));
            }
            Transform parent = bone.parent;
            while (parent != null && !solved.Contains (parent)) parent = parent.parent;
            Assert.IsTrue (FullBodyRig.TryGetLimitFrame (rig, bone, parent, out Quaternion frame, out Quaternion rest, out int twist));
            Quaternion inverse = Quaternion.Inverse (rig.root.transform.rotation);
            Quaternion relative = Quaternion.Inverse (inverse * parent.rotation) * (inverse * bone.rotation);
            return FullBodyRig.LimitToBodyAxes ((Vector3)FullBodyIk.LimitAngles (frame, rest, relative), twist) * Mathf.Rad2Deg;
        }

        /// <summary>骨の名前ごとの、ルートから見た位置と向き（別々に組んだリグどうしを比べる）</summary>
        static Dictionary<string, (Vector3, Quaternion)> Pose (EditingRig rig)
        {
            Transform root = rig.root.transform;
            return rig.editingBones.ToDictionary (t => t.name, t => (root.InverseTransformPoint (t.position), Quaternion.Inverse (root.rotation) * t.rotation));
        }

        static void AssertSamePose (Dictionary<string, (Vector3, Quaternion)> expected, EditingRig rig, string message)
        {
            foreach (KeyValuePair<string, (Vector3, Quaternion)> pair in Pose (rig)) {
                Assert.LessOrEqual (Vector3.Distance (expected[pair.Key].Item1, pair.Value.Item1), 1e-4f, message + ": " + pair.Key + " の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[pair.Key].Item2, pair.Value.Item2), 0.01f, message + ": " + pair.Key + " の向き");
            }
        }

        static float MaxAbs (Vector3 v)
        {
            return Mathf.Max (Mathf.Abs (v.x), Mathf.Abs (v.y), Mathf.Abs (v.z));
        }

        // ---- 値の換算 ----

        [Test]
        public void LimitAngles_RoundTrip ()
        {
            Random.InitState (7);
            for (int i = 0; i < 50; i++) {
                quaternion frame = Random.rotation;
                quaternion rest = Random.rotation;
                quaternion relative = Random.rotation;
                float3 angles = FullBodyIk.LimitAngles (frame, rest, relative);
                quaternion back = FullBodyIk.LimitRelative (frame, rest, angles);
                Assert.LessOrEqual (Quaternion.Angle (relative, back), 0.01f, "角度制限の値から向きへ戻せる");
            }
        }

        [Test]
        public void Angles_AreTheSameAcrossBoneFrames ()
        {
            // 骨の軸の取り方が違うキャラでも、同じ FK の値なら同じ角度制限の値になる（体の向きの軸で測る）
            Vector3[] angles = new Vector3[2];
            for (int k = 0; k < 2; k++) {
                Setup s = Create (twistFrames: k == 1);
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm)).localRotation = Quaternion.Euler (10, 20, 30);
                s.rig.ResetBonesToRest ();
                s.solver.Solve ();
                angles[k] = Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm));
            }
            Assert.Greater (MaxAbs (angles[0]), 10, "回っている");
            Assert.LessOrEqual (Vector3.Distance (angles[0], angles[1]), 0.01f, "骨の軸に依らない");
        }

        // ---- 解き方 ----

        [Test]
        public void Pull_DoesNotExceedRangePlusSlack ()
        {
            // 制限なしなら範囲 + 遊び（30°）を越えて振る引き方
            Setup free = Create ();
            PullLeftHand (free);
            Assert.Greater (MaxAbs (Angles (free.rig, Bone (free, HumanBodyBones.LeftUpperArm))), 35, "制限なしでは 30° を越える（引き方の確かめ）");

            Limit (HumanBodyBones.LeftUpperArm, -20, 20, 10);
            Setup s = Create ();
            PullLeftHand (s);
            Vector3 angles = Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm));
            Assert.LessOrEqual (MaxAbs (angles), 30 + kAngleTolerance, "範囲 + 遊びを越えない: " + angles);
            Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftHand), 0.01f, "点と両立しないときは制限が勝つ");
        }

        [Test]
        public void ResolvingFromTheResult_DoesNotWiden ()
        {
            // 全身 IK の結果はキーに残り、次に解くときの元の姿勢になる。結果から解き直しても、範囲 + 遊びの分ずつ広がらない
            Limit (HumanBodyBones.LeftUpperArm, -20, 20, 10);
            Setup s = Create ();
            PullLeftHand (s);
            for (int k = 0; k < 3; k++) {
                s.body.CaptureInput ();
                s.body.Solve ();
            }
            Vector3 angles = Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm));
            Assert.LessOrEqual (MaxAbs (angles), 30 + kAngleTolerance, "範囲 + 遊びを越えない: " + angles);
        }

        [Test]
        public void PinnedRotation_DoesNotExceedRangePlusSlack ()
        {
            // 向きを固定した点とも、両立しないときは制限が勝つ（仕上げで点の向きへ戻すときも、範囲 + 遊びで切る）。
            // 腕のひねり（体の右の軸 x）を上腕・前腕で止め、手のひねりを ±10°（遊び 5°）にして、手を 60° ひねる
            foreach (HumanBodyBones arm in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm }) {
                definition_.FindFkControl (arm).limit.x.mode = EditRigDefinition.LimitMode.Locked;
            }
            EditRigDefinition.JointLimit hand = definition_.FindFkControl (HumanBodyBones.LeftHand).limit;
            hand.x.mode = EditRigDefinition.LimitMode.Range;
            hand.x.min = -10;
            hand.x.max = 10;
            hand.slack = 5;
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.LeftHand, Vector3.zero, 1, true);
            Transform bone = Bone (s, HumanBodyBones.LeftHand);
            Vector3 right = s.rig.root.transform.TransformDirection (s.rig.bodyRight);
            s.body.SetPointRotationWorld (HumanBodyBones.LeftHand, Quaternion.AngleAxis (60, right) * bone.rotation);
            Solve (s);

            Vector3 angles = Angles (s.rig, bone);
            Assert.LessOrEqual (Mathf.Abs (angles.x), 15 + kAngleTolerance, "範囲 + 遊びを越えない: " + angles);
        }

        [Test]
        public void Slack_GetsHarderTowardItsEnd ()
        {
            // 遊びが大きいほど範囲の外まで回るが、強さ 1 未満で引くと、遊びの端までは行かない（越えるほど回しにくい）
            float[] overshoot = new float[2];
            float[] slacks = { 10, 40 };
            for (int k = 0; k < 2; k++) {
                definition_ = CreateDefinition ();
                Limit (HumanBodyBones.LeftUpperArm, -20, 20, slacks[k]);
                Setup s = Create ();
                PullLeftHand (s, 0.9f);
                overshoot[k] = MaxAbs (Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm))) - 20;
            }
            Assert.Greater (overshoot[0], 0, "範囲の外まで回る: " + overshoot[0] + " / " + overshoot[1]);
            Assert.Less (overshoot[0], slacks[0] - 1, "柔らかく引くと遊びの端まで行かない");
            Assert.Less (overshoot[1], slacks[1] - 1, "柔らかく引くと遊びの端まで行かない");
            Assert.Greater (overshoot[1], overshoot[0], "遊びが大きいほど回る");
        }

        [Test]
        public void LockedAxis_DoesNotTurn ()
        {
            // 腕のひねりは体の右の軸（x）
            definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.x.mode = EditRigDefinition.LimitMode.Locked;
            Setup s = Create ();
            PullLeftHand (s);
            Vector3 angles = Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm));
            Assert.LessOrEqual (Mathf.Abs (angles.x), kAngleTolerance, "固定の軸は回らない: " + angles);
            Assert.Greater (Mathf.Max (Mathf.Abs (angles.y), Mathf.Abs (angles.z)), 20, "ほかの軸は回る: " + angles);
        }

        [Test]
        public void InputOutsideRange_IsKept ()
        {
            // 元の姿勢が範囲 + 遊びの外でも、その値までは許す。点を今の場所に留めても少し動かしても、制限なしと同じ姿勢になる
            Dictionary<string, (Vector3, Quaternion)>[] stay = new Dictionary<string, (Vector3, Quaternion)>[2];
            Dictionary<string, (Vector3, Quaternion)>[] moved = new Dictionary<string, (Vector3, Quaternion)>[2];
            for (int k = 0; k < 2; k++) {
                definition_ = CreateDefinition ();
                if (k == 1) Limit (HumanBodyBones.LeftUpperArm, -20, 20, 10);
                Setup s = Create ();
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm)).localRotation = Quaternion.Euler (0, 0, 70);
                s.rig.ResetBonesToRest ();
                s.solver.Solve ();
                Assert.Greater (MaxAbs (Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm))), 45, "元の姿勢が範囲 + 遊びの外");
                Pin (s, HumanBodyBones.LeftHand, Vector3.zero);
                Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, 1, true);
                s.body.CaptureInput ();
                s.body.Solve ();
                stay[k] = Pose (s.rig);
                Pin (s, HumanBodyBones.LeftHand, s.forward * 0.02f);
                s.body.RestoreInput ();
                s.body.Solve ();
                moved[k] = Pose (s.rig);
                if (k == 1) Assert.Greater (MaxAbs (Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm))), 45, "範囲の内側へ引き戻さない");
            }
            Setup check = Create ();
            AssertSamePose (stay[0], LoadPose (check, stay[1]), "点を留めたとき");
            AssertSamePose (moved[0], LoadPose (check, moved[1]), "点を少し動かしたとき");
        }

        /// <summary>名前ごとの姿勢を、リグの骨へ入れる（比べるために、同じ形のリグへ写す）</summary>
        static EditingRig LoadPose (Setup s, Dictionary<string, (Vector3, Quaternion)> pose)
        {
            Transform root = s.rig.root.transform;
            foreach (Transform bone in s.rig.editingBones) {
                bone.SetPositionAndRotation (root.TransformPoint (pose[bone.name].Item1), root.rotation * pose[bone.name].Item2);
            }
            return s.rig;
        }

        [Test]
        public void FreeLimits_DoNotChangeTheSolve ()
        {
            // 範囲を十分広くしても、制限なしと同じ姿勢（制限の計算が、効かない所で姿勢を動かさない）
            Setup free = Create ();
            PullLeftHand (free);
            Dictionary<string, (Vector3, Quaternion)> expected = Pose (free.rig);
            foreach (EditRigDefinition.FkControl control in definition_.fkControls) {
                for (int axis = 0; axis < 3; axis++) {
                    control.limit[axis].mode = EditRigDefinition.LimitMode.Range;
                    control.limit[axis].min = -179;
                    control.limit[axis].max = 179;
                }
            }
            Setup s = Create ();
            PullLeftHand (s);
            AssertSamePose (expected, s.rig, "制限なしと同じ");
        }

        [Test]
        public void Burst_MatchesManaged_WithLimits ()
        {
            Limit (HumanBodyBones.LeftUpperArm, -20, 20, 10);
            Limit (HumanBodyBones.Spine, -10, 10, 15);
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.LeftHand, Vector3.up * 0.6f + s.forward * 0.3f);
            Transform[] bones = s.rig.editingBones.ToArray ();

            s.body.useBurst = false;
            Solve (s);
            Vector3[] positions = bones.Select (b => b.position).ToArray ();
            Quaternion[] rotations = bones.Select (b => b.rotation).ToArray ();
            s.body.useBurst = true;
            Solve (s);
            if (!s.body.lastSolveUsedBurst) Assert.Inconclusive ("Burst がまだコンパイル中か、切れている（Jobs > Burst > Enable Compilation）。マネージドでは動いた");
            for (int i = 0; i < bones.Length; i++) {
                Assert.LessOrEqual (Vector3.Distance (positions[i], bones[i].position), 1e-4f, bones[i].name + " の位置（Burst とマネージドの差）");
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], bones[i].rotation), 0.01f, bones[i].name + " の向き（Burst とマネージドの差）");
            }
        }

        // ---- Humanoid の可動域から作る ----

        [Test]
        public void FromHumanoid_CoversMuscleRange_AndRestoresPose ()
        {
            Animator display = CreateCharacter ();
            Transform[] transforms = display.GetComponentsInChildren<Transform> ();
            Quaternion[] before = transforms.Select (t => t.localRotation).ToArray ();
            definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.slack = 7;

            int count = HumanoidJointLimits.Apply (definition_, display, out string problem);
            Assert.IsNull (problem);
            Assert.Greater (count, 5, "解く骨に範囲が入る");
            for (int i = 0; i < transforms.Length; i++) {
                Assert.LessOrEqual (Quaternion.Angle (before[i], transforms[i].localRotation), 1e-3f, "表示モデルの姿勢は戻す: " + transforms[i].name);
            }
            EditRigDefinition.JointLimit limit = definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit;
            Assert.AreEqual (7, limit.slack, "遊びは残す");
            Assert.IsTrue (Enumerable.Range (0, 3).All (a => limit[a].mode == EditRigDefinition.LimitMode.Range && limit[a].min <= limit[a].max));
            Assert.IsTrue (Enumerable.Range (0, 3).Any (a => limit[a].max - limit[a].min > 60), "腕の上げ下げは広い範囲");
            Assert.AreEqual (EditRigDefinition.LimitMode.Free, definition_.FindFkControl (HumanBodyBones.Hips).limit.x.mode, "根（腰）には入れない");
            Assert.IsEmpty (definition_.Validate ());

            // muscle を ±1 にした姿勢は、作った範囲に入る（範囲は Avatar の可動域そのもの）
            EditingRig rig = CreateEditingRig (definition_, display);
            HumanPoseHandler handler = new HumanPoseHandler (display.avatar, display.transform);
            try {
                HumanPose pose = new HumanPose ();
                handler.GetHumanPose (ref pose);
                Vector3 low = Vector3.positiveInfinity, high = Vector3.negativeInfinity;
                for (int dof = 0; dof < 3; dof++) {
                    int muscle = HumanTrait.MuscleFromBone ((int)HumanBodyBones.LeftUpperArm, dof);
                    if (muscle < 0) continue;
                    foreach (float value in new[] { -1f, 1f }) {
                        System.Array.Clear (pose.muscles, 0, pose.muscles.Length);
                        pose.muscles[muscle] = value;
                        handler.SetHumanPose (ref pose);
                        rig.SyncFromDisplay ();
                        Vector3 angles = Angles (rig, rig.GetEditingBone (HumanBodyBones.LeftUpperArm));
                        low = Vector3.Min (low, angles);
                        high = Vector3.Max (high, angles);
                        for (int a = 0; a < 3; a++) {
                            Assert.GreaterOrEqual (angles[a], limit[a].min - kAngleTolerance, HumanTrait.MuscleName[muscle] + " " + value + " の軸 " + a);
                            Assert.LessOrEqual (angles[a], limit[a].max + kAngleTolerance, HumanTrait.MuscleName[muscle] + " " + value + " の軸 " + a);
                        }
                    }
                }
                // 範囲の端は、測った端から 1° の丸めの内
                for (int a = 0; a < 3; a++) {
                    if (high[a] - low[a] < 1) continue;
                    Assert.LessOrEqual (low[a] - limit[a].min, 1 + kAngleTolerance, "最小は測った端: 軸 " + a);
                    Assert.LessOrEqual (limit[a].max - high[a], 1 + kAngleTolerance, "最大は測った端: 軸 " + a);
                }
            }
            finally {
                handler.Dispose ();
            }
        }

        [Test]
        public void FromHumanoid_RejectsGeneric ()
        {
            Animator display = TestSkeleton.CreateQuadruped (created_);
            Assert.AreEqual (0, HumanoidJointLimits.Apply (definition_, display, out string problem));
            Assert.IsNotNull (problem);
        }

        // ---- 定義 ----

        [Test]
        public void Mirrored_FlipsSideAxes ()
        {
            EditRigDefinition.JointLimit limit = new EditRigDefinition.JointLimit { slack = 12 };
            limit.x = new EditRigDefinition.AxisLimit { mode = EditRigDefinition.LimitMode.Range, min = -10, max = 30 };
            limit.y = new EditRigDefinition.AxisLimit { mode = EditRigDefinition.LimitMode.Range, min = -20, max = 50 };
            limit.z = new EditRigDefinition.AxisLimit { mode = EditRigDefinition.LimitMode.Locked, min = 5, max = 60 };
            EditRigDefinition.JointLimit mirrored = limit.Mirrored ();
            Assert.AreEqual ((-10f, 30f), (mirrored.x.min, mirrored.x.max), "左右の面に垂直な x はそのまま");
            Assert.AreEqual ((-50f, 20f), (mirrored.y.min, mirrored.y.max), "y は符号を変えて入れ替える");
            Assert.AreEqual ((-60f, -5f), (mirrored.z.min, mirrored.z.max));
            Assert.AreEqual (EditRigDefinition.LimitMode.Locked, mirrored.z.mode);
            Assert.AreEqual (12, mirrored.slack);
            Assert.AreEqual (HumanBodyBones.RightUpperArm, EditRigDefinition.OppositeBone (HumanBodyBones.LeftUpperArm));
            Assert.IsNull (EditRigDefinition.OppositeBone (HumanBodyBones.Spine));
        }

        [Test]
        public void Mirrored_LimitOnOppositeBone_GivesMirroredPose ()
        {
            // 左に入れた制限の鏡写しを右に入れると、左右対称に引いたときに左右対称の角度で止まる
            Limit (HumanBodyBones.LeftUpperArm, -20, 20, 10);
            definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.y.max = 5;
            definition_.FindFkControl (HumanBodyBones.RightUpperArm).limit = definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.Mirrored ();
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, 1, true);
            Pin (s, HumanBodyBones.LeftHand, Vector3.up * 0.4f + s.forward * 0.4f);
            Pin (s, HumanBodyBones.RightHand, Vector3.up * 0.4f + s.forward * 0.4f);
            Solve (s);
            Vector3 left = Angles (s.rig, Bone (s, HumanBodyBones.LeftUpperArm));
            Vector3 right = Angles (s.rig, Bone (s, HumanBodyBones.RightUpperArm));
            Assert.LessOrEqual (Vector3.Distance (left, new Vector3 (right.x, -right.y, -right.z)), 0.5f, "左右対称: " + left + " / " + right);
        }

        [Test]
        public void Hash_ChangesOnlyWithLimits ()
        {
            string before = definition_.GetFullBodyHash ();
            definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.slack = 30;
            Assert.AreEqual (before, definition_.GetFullBodyHash (), "制限していない骨の遊びはハッシュに入らない");
            definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit.y.mode = EditRigDefinition.LimitMode.Range;
            Assert.AreNotEqual (before, definition_.GetFullBodyHash ());
        }

        [Test]
        public void Validate_ReportsBadLimits ()
        {
            EditRigDefinition.JointLimit limit = definition_.FindFkControl (HumanBodyBones.LeftUpperArm).limit;
            limit.y.mode = EditRigDefinition.LimitMode.Range;
            limit.y.min = 10;
            limit.y.max = -10;
            limit.slack = -1;
            Assert.AreEqual (2, definition_.Validate ().Count);
        }
    }

}
