using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の解く中身（S25a-2）: 固定した点に届く・骨の長さが変わらない・肘と膝が逆に曲がらない・
    /// 体の中心ほど動かない・両立しない点でも壊れない・続けて動かしても飛ばない・Burst とマネージドで同じ結果
    /// </summary>
    public class FullBodySolverTests : RigTestFixture
    {
        const float kReach = 1e-3f;

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = CreateDefinition ();
        }

        sealed class Setup
        {
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
            public Vector3 forward;
            public Vector3 up;
        }

        /// <param name="toes">つま先の骨を足す（足の点はつま先の骨のあるキャラにだけできる）</param>
        /// <param name="fingers">親指・人差し指を足す（手のひらの人差し指側の点は指の骨のあるキャラにだけできる）。littleFingers なら小指も</param>
        /// <param name="shoulders">両肩（鎖骨）を足す（胸と首も付く）</param>
        Setup Create (bool withChest = true, float size = 1, bool toes = false, float toeOut = 0, bool eyes = false, bool fingers = false, bool littleFingers = false, bool shoulders = false)
        {
            Setup s = new Setup ();
            Animator display = CreateCharacter (withChest, size, toes, toeOut, fingers: fingers, eyes: eyes, littleFingers: littleFingers, shoulders: shoulders);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            s.rig = CreateEditingRig (definition_, display);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition_));
            s.forward = s.rig.root.transform.TransformDirection (s.rig.bodyForward).normalized;
            s.up = Vector3.up;
            return s;
        }

        static Transform Bone (Setup s, HumanBodyBones bone)
        {
            return s.rig.GetEditingBone (bone);
        }

        /// <summary>点を、今の骨の位置から offset だけ動かした所に固定する</summary>
        static void Pin (Setup s, HumanBodyBones bone, Vector3 offset, bool rotation = false)
        {
            s.body.CapturePoint (bone);
            s.body.SetPointPositionWorld (bone, Bone (s, bone).position + offset);
            s.body.SetPositionWeight (bone, 1);
            if (rotation) s.body.SetRotationWeight (bone, 1);
        }

        /// <summary>
        /// 今の点のまま、マネージドと Burst で 1 回ずつ解き、全部の骨の位置と向きが合うことを確かめる。checkManaged はマネージドで解いた直後に呼ぶ。
        /// Burst のコンパイルが済んでいなければ Inconclusive にする（短いバッチ実行では間に合わないことがある。失敗ではない）
        /// </summary>
        static void AssertBurstMatchesManaged (Setup s, System.Action checkManaged = null)
        {
            Transform[] bones = s.rig.editingBones.ToArray ();

            s.body.useBurst = false;
            Solve (s);
            if (checkManaged != null) checkManaged ();
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

        /// <summary>基準姿勢へ戻して、編集用リグ → 全身 IK の順に解く</summary>
        static void Solve (Setup s)
        {
            s.body.Unsolve ();
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            s.body.CaptureInput ();
            s.body.Solve ();
        }

        static Dictionary<Transform, float> Lengths (Setup s)
        {
            HashSet<Transform> bones = new HashSet<Transform> (s.rig.editingBones);
            return bones.Where (t => bones.Contains (t.parent)).ToDictionary (t => t, t => Vector3.Distance (t.position, t.parent.position));
        }

        static void AssertLengths (Dictionary<Transform, float> expected, float tolerance, string message)
        {
            foreach (KeyValuePair<Transform, float> pair in expected) {
                float now = Vector3.Distance (pair.Key.position, pair.Key.parent.position);
                Assert.IsFalse (float.IsNaN (now), message + ": " + pair.Key.name + " が NaN");
                Assert.LessOrEqual (Mathf.Abs (now - pair.Value), tolerance, message + ": " + pair.Key.name + " の長さ");
            }
        }

        static Vector3 ArmDirection (Setup s, bool right)
        {
            Transform hand = Bone (s, right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            Transform upper = Bone (s, right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
            return (hand.position - upper.position).normalized;
        }

        // ---- 届く・長さが変わらない ----

        [Test]
        public void PinnedHand_ReachesTarget_AndBoneLengthsStay ([Values (true, false)] bool withChest)
        {
            Setup s = Create (withChest);
            Dictionary<Transform, float> lengths = Lengths (s);
            Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.1f + s.forward * 0.15f - s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "固定した手が点に届く");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void PinnedFeet_StayWhileHandIsPulled ()
        {
            Setup s = Create ();
            Transform leftFoot = Bone (s, HumanBodyBones.LeftFoot);
            Quaternion footRotation = leftFoot.rotation;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.15f + s.forward * 0.25f - s.up * 0.2f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "左足");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightFoot), kReach, "右足");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "右手");
            Assert.LessOrEqual (Quaternion.Angle (footRotation, leftFoot.rotation), 0.01f, "向きを固定した足は回らない");
        }

        // ---- 肘・膝の曲がる向き ----

        [Test]
        public void HipsDown_WithFeetPinned_BendsKneesForward ()
        {
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Vector3 leftKnee = Bone (s, HumanBodyBones.LeftLowerLeg).position;
            Vector3 rightKnee = Bone (s, HumanBodyBones.RightLowerLeg).position;
            Vector3 chest = Bone (s, HumanBodyBones.Spine).position;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "左足");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightFoot), kReach, "右足");
            Assert.Greater (Vector3.Dot (Bone (s, HumanBodyBones.LeftLowerLeg).position - leftKnee, s.forward), 0.03f, "左膝は前へ出る");
            Assert.Greater (Vector3.Dot (Bone (s, HumanBodyBones.RightLowerLeg).position - rightKnee, s.forward), 0.03f, "右膝は前へ出る");
            // 点の無い上半身は腰に付いて動く（遅れて残らない）
            Assert.LessOrEqual (Vector3.Distance (Bone (s, HumanBodyBones.Spine).position, chest - s.up * 0.15f), 0.02f, "上半身は腰と一緒に下がる");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void HandPushedTowardShoulder_BendsElbowBackward ()
        {
            Setup s = Create ();
            Vector3 elbow = Bone (s, HumanBodyBones.RightLowerArm).position;
            Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.2f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach);
            Assert.Less (Vector3.Dot (Bone (s, HumanBodyBones.RightLowerArm).position - elbow, s.forward), -0.03f, "肘は後ろへ出る");
            Assert.Greater (s.body.lastDefaultBendCount, 0, "まっすぐな肘は、曲がる向きを定義から決める");
        }

        // ---- 動きの配分 ----

        [Test]
        public void BodyCenter_MovesLessThanLimb ()
        {
            Setup s = Create ();
            Vector3 hips = Bone (s, HumanBodyBones.Hips).position;
            Vector3 chest = Bone (s, HumanBodyBones.Chest).position;
            Vector3 elbow = Bone (s, HumanBodyBones.RightLowerArm).position;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.05f + s.forward * 0.2f);
            Solve (s);
            float hipsMoved = Vector3.Distance (hips, Bone (s, HumanBodyBones.Hips).position);
            float chestMoved = Vector3.Distance (chest, Bone (s, HumanBodyBones.Chest).position);
            float elbowMoved = Vector3.Distance (elbow, Bone (s, HumanBodyBones.RightLowerArm).position);
            Assert.Greater (elbowMoved, chestMoved, "肘は胸より動く");
            Assert.GreaterOrEqual (chestMoved + 1e-4f, hipsMoved, "胸は腰以上に動く");
            Assert.Less (hipsMoved, 0.03f, "腰はほとんど動かない");
        }

        [Test]
        public void Stiffness_ShiftsBendingToOtherJoints ()
        {
            float SpineBend (float stiffness)
            {
                definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine).stiffness = stiffness;
                Setup s = Create ();
                Quaternion rest = Bone (s, HumanBodyBones.Spine).localRotation;
                Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
                Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
                Pin (s, HumanBodyBones.Head, s.forward * 0.25f - s.up * 0.1f);
                Solve (s);
                return Quaternion.Angle (rest, Bone (s, HumanBodyBones.Spine).localRotation);
            }

            float soft = SpineBend (0.2f);
            float hard = SpineBend (0.95f);
            Assert.Greater (soft, hard + 1, "背骨を硬くすると、背骨の曲がりが減る（" + soft + "° → " + hard + "°）");
        }

        // ---- 壊れない・飛ばない ----

        [Test]
        public void IncompatiblePoints_GiveFiniteResult_AndKeepBoneLengths ()
        {
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, s.forward * 3f);
            Pin (s, HumanBodyBones.LeftHand, -s.forward * 3f + s.up * 2f);
            Pin (s, HumanBodyBones.Head, -s.up * 2f);
            Solve (s);
            foreach (Transform bone in s.rig.editingBones) {
                Vector3 p = bone.position;
                Quaternion q = bone.rotation;
                Assert.IsFalse (float.IsNaN (p.x + p.y + p.z + q.x + q.y + q.z + q.w), bone.name + " が NaN");
            }
            AssertLengths (lengths, 1e-3f, "骨の長さ");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "固定した腰は動かない");
        }

        [Test]
        public void MovingPoint_ChangesPoseContinuously ()
        {
            // 足は固定しない: 伸びきった脚は、腰がわずかに下がるだけで膝の角度が大きく変わる（角度が高さの平方根で効く）ので、
            // 連続でも 1 コマの変化がそろわない。伸びきる付近は見ない
            Setup s = Create ();
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 start = hand.position - ArmDirection (s, true) * 0.1f;
            Vector3 end = start + s.forward * 0.35f - s.up * 0.35f - ArmDirection (s, true) * 0.2f;
            Transform[] bones = s.rig.editingBones.ToArray ();
            const int kSteps = 40;
            List<Quaternion[]> poses = new List<Quaternion[]> ();
            s.body.CapturePoint (HumanBodyBones.RightHand);
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 1);
            for (int i = 0; i <= kSteps; i++) {
                s.body.SetPointPositionWorld (HumanBodyBones.RightHand, Vector3.Lerp (start, end, i / (float)kSteps));
                Solve (s);
                poses.Add (bones.Select (b => b.localRotation).ToArray ());
            }
            for (int b = 0; b < bones.Length; b++) {
                for (int i = 1; i < kSteps - 1; i++) {
                    float before = Quaternion.Angle (poses[i - 1][b], poses[i][b]);
                    float now = Quaternion.Angle (poses[i][b], poses[i + 1][b]);
                    float after = Quaternion.Angle (poses[i + 1][b], poses[i + 2][b]);
                    Assert.LessOrEqual (now, 3 * Mathf.Max (before, after) + 0.5f, bones[b].name + " がコマ " + i + " で飛んだ");
                }
            }
        }

        /// <summary>
        /// 腰と太ももの付け根を固定して足首の点を少しずつ動かしても、膝の向きがばらつかない（キャラ A で、足首を動かすと脚ががくがくした。2026-09-30）。
        /// 原因は 2 つ: 位置を固定した骨を向きの補正で動かしていた／付け根と先が決まった手足の、膝の向きを決める物が無かった
        /// </summary>
        [Test]
        public void FootMovedWithPinnedHip_KeepsKneeSteady ([Values (false, true)] bool beyondReach)
        {
            Setup s = Create ();
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.RightUpperLeg, Vector3.zero);
            Transform foot = Bone (s, HumanBodyBones.RightFoot);
            Vector3 side = Vector3.Cross (s.up, s.forward);
            // 届く範囲: 足を上げた所から横と前へ。届かない範囲: まっすぐな脚の足を、そのまま前へ引く
            Vector3 start = beyondReach ? foot.position : foot.position + s.up * 0.15f + s.forward * 0.05f;
            Vector3 end = beyondReach ? start + s.forward * 0.3f : start + side * 0.15f + s.forward * 0.2f;
            Transform[] bones = s.rig.editingBones.ToArray ();
            const int kSteps = 40;
            List<Quaternion[]> poses = new List<Quaternion[]> ();
            s.body.CapturePoint (HumanBodyBones.RightFoot);
            s.body.SetPositionWeight (HumanBodyBones.RightFoot, 1);
            for (int i = 0; i <= kSteps; i++) {
                s.body.SetPointPositionWorld (HumanBodyBones.RightFoot, Vector3.Lerp (start, end, i / (float)kSteps));
                Solve (s);
                poses.Add (bones.Select (b => b.rotation).ToArray ());
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightUpperLeg), kReach, "固定した付け根は動かない（コマ " + i + "）");
                if (!beyondReach) Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightFoot), kReach, "足が点に届く（コマ " + i + "）");
            }
            for (int b = 0; b < bones.Length; b++) {
                for (int i = 1; i < kSteps - 1; i++) {
                    float before = Quaternion.Angle (poses[i - 1][b], poses[i][b]);
                    float now = Quaternion.Angle (poses[i][b], poses[i + 1][b]);
                    float after = Quaternion.Angle (poses[i + 1][b], poses[i + 2][b]);
                    Assert.LessOrEqual (now, 2 * Mathf.Max (before, after) + 0.3f, bones[b].name + " がコマ " + i + " で飛んだ");
                }
            }
        }

        /// <summary>
        /// 腰を位置だけ固定して、まっすぐな脚の足首を真上に上げる: 膝が曲がって届き、腰は回らない（上半身が傾かない）。
        /// 以前は膝のずれが骨の向きに沿って膝が曲がり始めず、ずれの取り分で腰が 1〜2° 回っていた（2026-10-03）
        /// </summary>
        [Test]
        public void StraightLegFootRaised_WithHipsPinned_BendsKneeWithoutTurningHips ()
        {
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Quaternion hipsRotation = hips.rotation;
            Vector3 head = Bone (s, HumanBodyBones.Head).position;
            Vector3 knee = Bone (s, HumanBodyBones.LeftLowerLeg).position;
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.LeftFoot, s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首が点に届く");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は動かない");
            Assert.LessOrEqual (Quaternion.Angle (hipsRotation, hips.rotation), 0.05f, "腰は回らない");
            Assert.LessOrEqual (Vector3.Distance (head, Bone (s, HumanBodyBones.Head).position), 1e-3f, "上半身は動かない");
            Assert.Greater (Vector3.Dot (Bone (s, HumanBodyBones.LeftLowerLeg).position - knee, s.forward), 0.03f, "膝は前へ出る");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        /// <summary>
        /// つま先の先を Locked にし、腰を位置だけ固定して、足首をつま先のまわりに回して持ち上げる（かかとを上げる）。
        /// 足だけで届くので、上半身は動かず、つま先の先も外れない（つま先の骨があるキャラでは、以前はつま先の先が数 cm 外れていた）。
        /// つま先の先はつま先の骨のあるキャラにだけできるので、つま先の骨のあるキャラだけで試す
        /// </summary>
        [Test]
        public void HeelRaisedOnLockedToeTip_WithHipsPinned_KeepsUpperBody ()
        {
            Setup s = Create (toes: true);
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Quaternion hipsRotation = hips.rotation;
            Vector3 head = Bone (s, HumanBodyBones.Head).position;
            BodyPointId tip = BodyPointId.ToeTip (HumanBodyBones.LeftFoot);
            Vector3 tipPosition;
            Assert.IsTrue (s.body.TryGetBonePointWorld (tip, out tipPosition));
            LockPoint (s, tip, Vector3.zero);
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            // つま先の付け根を中心に、足首が上がる向きへ 25° 回した所
            Vector3 pivot = Bone (s, HumanBodyBones.LeftToes).position;
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            Vector3 raised = pivot + Quaternion.AngleAxis (25, side) * (foot.position - pivot);
            Vector3 lowered = pivot + Quaternion.AngleAxis (-25, side) * (foot.position - pivot);
            Pin (s, HumanBodyBones.LeftFoot, (raised.y > lowered.y ? raised : lowered) - foot.position);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (tip), kReach, "つま先の先は動かない");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首が点に届く");
            Assert.LessOrEqual (Quaternion.Angle (hipsRotation, hips.rotation), 0.05f, "腰は回らない");
            Assert.LessOrEqual (Vector3.Distance (head, Bone (s, HumanBodyBones.Head).position), 1e-3f, "上半身は動かない");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        /// <summary>
        /// まっすぐな脚の足を固定したまま、腰を位置だけ固定して後ろへ引く（脚が伸びきって足に届かない）:
        /// 腰は点に届き、上半身は傾かない（届かない分は足に残る）。
        /// 以前は、届かない足へ寄せようと腰を固定した点まわりに回し、上半身が倒れこんでいた（このテストの体で 12〜19°、デモのキャラで 31°。10-06）
        /// </summary>
        [Test]
        public void HipsPulledBack_BeyondLegReach_KeepsUpperBodyUpright ([Values (false, true)] bool contactPoints, [Values (0.2f, 0.3f)] float back)
        {
            // 足の点（踵・つま先の先）はつま先の骨のあるキャラにだけできる。足首で固定する側も同じキャラで比べる
            Setup s = Create (toes: true);
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Quaternion hipsRotation = hips.rotation;
            HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            Quaternion[] footRotations = feet.Select (f => Bone (s, f).rotation).ToArray ();
            foreach (HumanBodyBones foot in feet) {
                if (contactPoints) {
                    LockPoint (s, BodyPointId.Heel (foot), Vector3.zero);
                    LockPoint (s, BodyPointId.ToeTip (foot), Vector3.zero);
                }
                else {
                    Pin (s, foot, Vector3.zero, true);
                }
            }
            Pin (s, HumanBodyBones.Hips, -s.forward * back);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は点に届く");
            Assert.LessOrEqual (Quaternion.Angle (hipsRotation, hips.rotation), 2f, "腰は回らない（上半身が倒れこまない）");
            if (!contactPoints) {
                for (int i = 0; i < feet.Length; i++) {
                    Assert.LessOrEqual (Quaternion.Angle (footRotations[i], Bone (s, feet[i]).rotation), 0.01f, feet[i] + " は向きを固定したまま");
                }
            }
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        /// <summary>
        /// 腰と足を位置で固定したまま、背骨を曲げれば届く前下へ手を引く: 手は届く。
        /// 固定した腰を SolveReach で回さなくした（10-06）後も、背骨が曲がって届く
        /// </summary>
        [Test]
        public void HandPulledForwardDown_WithHipsAndFeetPinned_StillReaches ()
        {
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, s.forward * 0.3f - s.up * 0.4f - ArmDirection (s, true) * 0.3f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "手が点に届く");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "左足");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightFoot), kReach, "右足");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void PositionWeight_FadesInContinuously ()
        {
            Setup s = Create ();
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 rest = hand.position;
            Vector3 target = rest - ArmDirection (s, true) * 0.1f + s.forward * 0.2f;
            s.body.CapturePoint (HumanBodyBones.RightHand);
            s.body.SetPointPositionWorld (HumanBodyBones.RightHand, target);
            float previous = 0;
            foreach (float weight in new[] { 0.01f, 0.1f, 0.5f, 0.9f, 1f }) {
                s.body.SetPositionWeight (HumanBodyBones.RightHand, weight);
                Solve (s);
                float moved = Vector3.Distance (rest, hand.position);
                Assert.GreaterOrEqual (moved + 1e-4f, previous, "強さ " + weight + " で、前の強さより点に寄る");
                previous = moved;
                if (weight <= 0.01f) Assert.Less (moved, 0.03f, "強さが 0 に近ければ、ほとんど動かない（0 から滑らかに効き始める）");
            }
            Assert.LessOrEqual (Vector3.Distance (target, hand.position), kReach, "強さ 1 で届く");
        }

        // ---- 向きの点 ----

        static BodyPointId Direction (Setup s, params HumanBodyBones[] candidates)
        {
            // 既定の一覧では、胸の前の点は UpperChest に置く（無いキャラでは親の骨へ乗る）。名前は背骨をまとめた胸の点に合わせて ChestDirection
            return BodyPointId.Direction (candidates.Contains (HumanBodyBones.UpperChest) || candidates.Contains (HumanBodyBones.Chest) ? HumanBodyBones.Chest : candidates[0]);
        }

        static BodyPointId ChestDirection (Setup s)
        {
            return Direction (s, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine);
        }

        /// <summary>向きの点を、今の骨の上の点の位置から offset だけ動かした所に固定する</summary>
        static void PinDirection (Setup s, BodyPointId id, Vector3 offset)
        {
            Vector3 onBone;
            Assert.IsTrue (s.body.TryGetBonePointWorld (id, out onBone));
            s.body.CapturePoint (id);
            s.body.SetPointPositionWorld (id, onBone + offset);
            s.body.SetPositionWeight (id, 1);
        }

        /// <summary>骨の上の点が、骨の付け根から見て、点の方からどれだけ外れているか（度）</summary>
        static float AimError (Setup s, BodyPointId id)
        {
            Vector3 onBone, target;
            Quaternion rotation;
            s.body.TryGetBonePointWorld (id, out onBone);
            s.body.TryGetPointWorld (id, out target, out rotation);
            Vector3 origin = s.body.GetBone (id).position;
            return Vector3.Angle (onBone - origin, target - origin);
        }

        /// <summary>向きの点（名前が Direction・Top で終わる点）の重さを決める（S28。既定は 0）。Create の前に呼ぶ</summary>
        void SetDirectionWeight (int weight)
        {
            foreach (EditRigDefinition.BodyPoint point in definition_.bodyPoints) {
                if (point.name.EndsWith ("Direction") || point.name.EndsWith ("Top")) point.weight = weight;
            }
        }

        /// <summary>骨の位置と向きを控える</summary>
        static Pose[] Poses (Transform[] bones)
        {
            return bones.Select (b => new Pose (b.position, b.rotation)).ToArray ();
        }

        /// <summary>skip に当たらない骨が、控えた位置と向きのままか</summary>
        static void AssertUnchanged (Transform[] bones, Pose[] expected, float position, float angle, string message, System.Func<Transform, bool> skip = null)
        {
            for (int i = 0; i < bones.Length; i++) {
                if (skip != null && skip (bones[i])) continue;
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, bones[i].position), position, message + ": " + bones[i].name + " の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, bones[i].rotation), angle, message + ": " + bones[i].name + " の向き");
            }
        }

        // ---- 重さ 0 の点（向けるだけ。S28） ----

        [Test]
        public void DirectionPoints_DefaultToWeightZero ()
        {
            // 既定の一覧では、向きの点は重さ 0、ほかの点は 1（足の小指側も、手の小指側と同じく 1。10-07）
            foreach (EditRigDefinition.BodyPoint point in definition_.bodyPoints) {
                bool direction = point.name.EndsWith ("Direction") || point.name == "HeadTop";
                Assert.AreEqual (direction ? 0 : 1, point.weight, point.name);
            }
            Assert.IsEmpty (definition_.Validate ());
        }

        [Test]
        public void AimPoint_TurnsOnlyItsBone ()
        {
            // 顔の前の点（重さ 0）を横へ: 頭が付け根まわりに回って点の方を向く。首から根の側は動かない
            Setup s = Create ();
            Solve (s);
            Transform head = Bone (s, HumanBodyBones.Head);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            BodyPointId id = BodyPointId.Direction (HumanBodyBones.Head);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, id, side * 0.3f + s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (AimError (s, id), 0.5f, "顔の前の点が目標の方を向く");
            Assert.Greater (Quaternion.Angle (rest[System.Array.IndexOf (bones, head)].rotation, head.rotation), 20f, "頭が回った");
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "首から根の側", b => b == head || b.IsChildOf (head));
        }

        [Test]
        public void AimPoints_FrontAndTop_TiltHeadAroundFront ()
        {
            // 顔の前とてっぺん（どちらも重さ 0）: 顔の前で向きが決まり、てっぺんで顔の前の向きまわりのかしげが決まる
            Setup s = Create ();
            BodyPointId front = BodyPointId.Direction (HumanBodyBones.Head);
            BodyPointId top = BodyPointId.Top (HumanBodyBones.Head);
            Transform head = Bone (s, HumanBodyBones.Head);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, front, side * 0.2f);
            Solve (s);
            Quaternion frontOnly = head.rotation;

            PinDirection (s, top, side * 0.1f);
            Solve (s);
            Assert.LessOrEqual (AimError (s, front), 0.5f, "顔の前の点は目標の方を向いたまま");
            Assert.Greater (Quaternion.Angle (frontOnly, head.rotation), 5f, "頭がかしいだ");
            // てっぺんは、顔の前の向きに直交する面の中で、点の方を向く
            Vector3 frontPoint, topPoint, topTarget;
            Quaternion rotation;
            s.body.TryGetBonePointWorld (front, out frontPoint);
            s.body.TryGetBonePointWorld (top, out topPoint);
            s.body.TryGetPointWorld (top, out topTarget, out rotation);
            Vector3 axis = (frontPoint - head.position).normalized;
            float tilt = Vector3.Angle (Vector3.ProjectOnPlane (topPoint - head.position, axis), Vector3.ProjectOnPlane (topTarget - head.position, axis));
            Assert.LessOrEqual (tilt, 0.5f, "てっぺんがかしげの面で点の方を向く");
        }

        [Test]
        public void AimPoint_FollowsBody_PulledByOtherPoints ()
        {
            // 手を Pin して胴ごと引いたとき、顔の前の点（重さ 0）は頭の付け根を引かない。
            // 頭は首に付いたまま目標の方を向き、頭より根の側の骨は、顔の前の点が無いときと同じ
            Setup s = Create ();
            Solve (s);
            Transform head = Bone (s, HumanBodyBones.Head);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Vector3 neckRest = Bone (s, HumanBodyBones.Neck).position;
            Pin (s, HumanBodyBones.RightHand, s.forward * 0.6f - s.up * 0.3f);
            Solve (s);
            Pose[] pulled = Poses (bones);
            Assert.Greater (Vector3.Distance (neckRest, Bone (s, HumanBodyBones.Neck).position), 0.01f, "手に引かれて胴が動く");

            BodyPointId id = BodyPointId.Direction (HumanBodyBones.Head);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, id, side * 0.3f);
            Solve (s);
            Assert.LessOrEqual (AimError (s, id), 0.5f, "顔の前の点が目標の方を向く");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "手は点に届いたまま");
            AssertUnchanged (bones, pulled, 1e-4f, 0.01f, "頭より根の側は顔の前の点が無いときと同じ", b => b == head || b.IsChildOf (head));
        }

        [Test]
        public void AimPoint_OnChest_TurnsChest_KeepsLowerBody ([Values (false, true)] bool pinnedHands)
        {
            // 胸の前の点（重さ 0）を横へ: 胸が付け根まわりに回り、背骨・腰・脚は、胸の前の点が無いときのまま。
            // 両手を Pin していれば、胸は向きを譲らず、腕が曲がって手は点に留まる（T ポーズの伸びきった腕では届かないので、手を体の前へ寄せておく）
            Setup s = Create ();
            if (pinnedHands) {
                Pin (s, HumanBodyBones.LeftHand, -ArmDirection (s, false) * 0.15f + s.forward * 0.2f);
                Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.15f + s.forward * 0.2f);
            }
            Solve (s);
            BodyPointId id = ChestDirection (s);
            Transform chest = s.body.GetBone (id);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] before = Poses (bones);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, id, side * 0.1f);
            Solve (s);
            Assert.LessOrEqual (AimError (s, id), 0.5f, "胸の前の点が目標の方を向く");
            Assert.Greater (Quaternion.Angle (before[System.Array.IndexOf (bones, chest)].rotation, chest.rotation), 10f, "胸が回った");
            AssertUnchanged (bones, before, 1e-4f, 0.01f, "背骨・腰・脚", b => b == chest || b.IsChildOf (chest));
            if (pinnedHands) {
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kReach, "左手は点に留まる");
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "右手は点に留まる");
            }
        }

        [Test]
        public void AimPoint_WithPinnedOffsetPointOnSameBone_TurnsAroundThatPoint ()
        {
            // 胸の点（付け根から離れた点）を Pin にしたまま胸の前の点を横へ: 胸は胸の点を中心に回り、胸の点はその場に残る
            // （背骨が Spine だけのキャラでは、胸の点と胸の前の点が同じ Spine に乗る。10-20 まで、付け根まわりに向けると胸の点が外れるので、まったく回らなかった）
            Setup s = Create (false);
            BodyPointId chestPoint = s.body.pointIds.Single (id => id.name == "Chest");
            BodyPointId id = ChestDirection (s);
            Transform chest = s.body.GetBone (id);
            Assert.AreSame (chest, s.body.GetBone (chestPoint), "胸の点と胸の前の点は同じ骨に乗る");
            PinDirection (s, chestPoint, Vector3.zero);
            Solve (s);
            Vector3 pivot;
            s.body.TryGetBonePointWorld (chestPoint, out pivot);
            Assert.Greater (Vector3.Distance (pivot, chest.position), 0.01f, "胸の点は付け根から離れている");
            Quaternion before = chest.rotation;

            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, id, side * 0.1f);
            Solve (s);
            Assert.Greater (Quaternion.Angle (before, chest.rotation), 10f, "胸が回った");
            Assert.LessOrEqual (s.body.GetResidual (chestPoint), kReach, "胸の点はその場に残る");
            Vector3 onBone, target;
            Quaternion rotation;
            s.body.TryGetBonePointWorld (chestPoint, out pivot);
            s.body.TryGetBonePointWorld (id, out onBone);
            s.body.TryGetPointWorld (id, out target, out rotation);
            Assert.LessOrEqual (Vector3.Angle (onBone - pivot, target - pivot), 1f, "胸の前の点が、胸の点から見て目標の方を向く");
        }

        // ---- 軽い点（重さ 0）をつかんで動かすと、Free の重い点を引かない（S88。つかむのは MarkGrabbed。窓は点を動かすたびに呼ぶ） ----

        /// <summary>名前の点の重さを 0 にする（軽い点）。Create の前に呼ぶ</summary>
        void SetLight (params string[] names)
        {
            foreach (string name in names) definition_.bodyPoints.First (p => p.name == name).weight = 0;
        }

        /// <summary>左腕（肩があれば肩から）の骨か</summary>
        static System.Func<Transform, bool> LeftArm (Setup s)
        {
            Transform shoulder = Bone (s, HumanBodyBones.LeftShoulder);
            Transform top = shoulder != null ? shoulder : Bone (s, HumanBodyBones.LeftUpperArm);
            return b => b == top || b.IsChildOf (top);
        }

        [Test]
        public void LightHand_PullsLightArmPoints_AndLeavesHeavyPoints ([Values (false, true)] bool beyondReach)
        {
            // 手・肘・上腕の点を軽く（0）する。手を動かすと、腕は引かれて曲がる（届くなら届く）。胴・首・腰・ほかの手足の重い点は、届かなくても動かない
            SetLight ("LeftHand", "LeftLowerArm", "LeftUpperArm");
            Assert.IsEmpty (definition_.Validate ());
            Setup s = Create ();
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            Vector3 elbow = Bone (s, HumanBodyBones.LeftLowerArm).position;
            Dictionary<Transform, float> lengths = Lengths (s);
            // 届く所は、肘を曲げて手を前へ出しつつ体の方へ寄せた所
            s.body.MarkGrabbed (HumanBodyBones.LeftHand);
            Pin (s, HumanBodyBones.LeftHand, beyondReach ? s.forward * 1f : s.forward * 0.15f - ArmDirection (s, false) * 0.15f);
            Solve (s);
            if (!beyondReach) Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kReach, "届く所なら、手は届く");
            else Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftHand), 0.1f, "届かない所へは、胴を引かずに届かないまま");
            Assert.Greater (Vector3.Distance (elbow, Bone (s, HumanBodyBones.LeftLowerArm).position), 0.01f, "肘（軽い点）は引かれて動く");
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "左腕のほか（重い点の骨）", LeftArm (s));
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void HeavyPoint_StillPullsLightPoints ()
        {
            // 重い手（1）を動かすと、軽い肘・上腕（0）も今までどおり付いてくる。軽い点を固定していなければ、軽い点の無い定義と同じ姿勢
            Setup heavy = Create ();
            Pin (heavy, HumanBodyBones.LeftHand, ForwardDown (heavy));
            Solve (heavy);
            Pose[] expected = Poses (heavy.rig.editingBones.ToArray ());

            definition_ = CreateDefinition ();
            SetLight ("LeftLowerArm", "LeftUpperArm", "Chest", "Stomach");
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftHand, ForwardDown (s));
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kReach, "手は届く");
            AssertUnchanged (s.rig.editingBones.ToArray (), expected, 1e-4f, 0.01f, "軽い点を固定していないときは、軽い点の無い定義と同じ");
        }

        static Vector3 ForwardDown (Setup s)
        {
            return s.forward * 0.6f - s.up * 0.3f;
        }

        [Test]
        public void LightKnee_TurnsKneeOutward_WithFeetAndHipsHeld ()
        {
            // 膝の点（0）を外へ: 足首と腰（重い点）は動かずに、膝が外を向く（膝の Pole と同じような動き）
            SetLight ("LeftLowerLeg");
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform knee = Bone (s, HumanBodyBones.LeftLowerLeg);
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f);
            Solve (s);
            Vector3 bent = knee.position;
            Vector3 hips = Bone (s, HumanBodyBones.Hips).position;
            Vector3 foot = Bone (s, HumanBodyBones.LeftFoot).position;
            Vector3 outside = Vector3.ProjectOnPlane (foot - hips, s.up).normalized;

            s.body.MarkGrabbed (HumanBodyBones.LeftLowerLeg);
            Pin (s, HumanBodyBones.LeftLowerLeg, outside * 0.3f);
            Solve (s);
            Assert.Greater (Vector3.Dot (knee.position - bent, outside), 0.02f, "左膝が外へ向く");
            Assert.LessOrEqual (Vector3.Distance (foot, Bone (s, HumanBodyBones.LeftFoot).position), 1e-4f, "左足首は動かない");
            Assert.LessOrEqual (Vector3.Distance (hips, Bone (s, HumanBodyBones.Hips).position), 1e-4f, "腰は動かない");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void DraggedLightPoint_PullsLightFreePoints_AndKeepsPins ([Values (false, true)] bool pinElbow)
        {
            // 手・肘・上腕を軽く（0）し、手をつかんで動かす（MarkGrabbed）。手は肘と肩を引っ張る。
            // 肘が Free なら、引かれて付いてきて手は届く。肘が Pin なら、肘は置いた場所に留まり（軽い点でも Pin は動かない）、手は肘まわりにしか回れないので届かない。
            // 重い胸・首・反対の腕は、どちらでも動かない
            SetLight ("LeftHand", "LeftLowerArm", "LeftUpperArm");
            Setup s = Create ();
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            Transform elbow = Bone (s, HumanBodyBones.LeftLowerArm);
            Vector3 elbowRest = elbow.position;
            if (pinElbow) Pin (s, HumanBodyBones.LeftLowerArm, Vector3.zero);
            s.body.MarkGrabbed (HumanBodyBones.LeftHand);
            Pin (s, HumanBodyBones.LeftHand, s.forward * 0.15f - ArmDirection (s, false) * 0.15f);
            Solve (s);
            if (pinElbow) {
                Assert.LessOrEqual (Vector3.Distance (elbowRest, elbow.position), 1e-3f, "Pin の肘は留まる");
                Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftHand), 0.01f, "手は届かない");
            }
            else {
                Assert.Greater (Vector3.Distance (elbowRest, elbow.position), 0.01f, "Free の肘は引かれて動く");
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kReach, "手は届く");
            }
            // 肘が Pin なら、肘の向こうの上腕も動かない（前腕だけが肘まわりに回る）
            Transform moved = Bone (s, pinElbow ? HumanBodyBones.LeftLowerArm : HumanBodyBones.LeftUpperArm);
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, pinElbow ? "前腕のほか" : "左腕のほか", b => b == moved || b.IsChildOf (moved));
            s.body.ClearCarry ();
        }

        [Test]
        public void LightHand_KeepsArmBeyondHeavyElbow ()
        {
            // 手・上腕を軽く（0）し、肘は重い（1）まま。手をつかんで動かすと、前腕が肘まわりに回るだけで、肘とその向こうの上腕・胴は動かない
            SetLight ("LeftHand", "LeftUpperArm");
            Setup s = Create ();
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Vector3 handRest = hand.position;
            Pose[] rest = Poses (bones);
            s.body.MarkGrabbed (HumanBodyBones.LeftHand);
            Pin (s, HumanBodyBones.LeftHand, s.forward * 0.1f);
            Solve (s);
            Assert.Greater (Vector3.Distance (handRest, hand.position), 0.02f, "手は動く");
            Transform lowerArm = Bone (s, HumanBodyBones.LeftLowerArm);
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "前腕のほか", b => b == lowerArm || b.IsChildOf (lowerArm));
            s.body.ClearCarry ();
        }

        [Test]
        public void HeavyPoint_KeepsPinsInPlace_AndPullsThemOutOfReach ([Values (false, true)] bool outOfReach, [Values (false, true)] bool lockFeet)
        {
            // 重い腰（1）をつかんで動かす。重い Pin の足・右手は、届く間はその場に留まり、腰が届かない所へ行くと引かれる。
            // 軽い（0）Pin の肘は、Free の点と同じに引かれる。
            // Lock の足は、届く所なら動かない（届かない所では、強さ 1 のつかんだ腰と Lock の足がぶつかり、今までどおり Lock の足も動く。確かめない）
            if (outOfReach && lockFeet) Assert.Ignore ("つかんだ点と Lock の点がぶつかるときは確かめない");
            SetLight ("LeftLowerArm");
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.LeftLowerArm, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, Vector3.zero, true);
            if (lockFeet) {
                s.body.SetLocked (HumanBodyBones.LeftFoot, true);
                s.body.SetLocked (HumanBodyBones.RightFoot, true);
            }
            Solve (s);
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Transform elbow = Bone (s, HumanBodyBones.LeftLowerArm);
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 footRest = foot.position, elbowRest = elbow.position, handRest = hand.position;
            s.body.MarkGrabbed (HumanBodyBones.Hips);
            // 届く所は、膝を曲げて前へ。届かない所は、足から離れた上
            Pin (s, HumanBodyBones.Hips, outOfReach ? s.up * 0.4f : s.forward * 0.15f - s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は届く");
            if (outOfReach) {
                Assert.Greater (Vector3.Distance (footRest, foot.position), 0.05f, "届かなければ、Pin の重い足は引かれる");
            }
            else {
                Assert.LessOrEqual (Vector3.Distance (footRest, foot.position), lockFeet ? 1e-4f : 1e-3f, "届く間は、足はその場に留まる");
                // 強さ 1 の Pin の点を解いたときの、詰めきれない分（数 mm）は残る
                Assert.LessOrEqual (Vector3.Distance (handRest, hand.position), 5e-3f, "届く間は、Pin の重い手はその場に留まる");
                Assert.Greater (Vector3.Distance (elbowRest, elbow.position), 0.02f, "Pin の軽い肘は、Free の点と同じに引かれる");
            }
            s.body.ClearCarry ();
        }

        [Test]
        public void HeavyPoint_PullsLightPins_LikeFreePoints ()
        {
            // 重い腰（1）をつかんで動かすと、軽い（0）Pin の肘・上腕は Free の点と同じに引かれる（Free にしたときと同じ姿勢）
            SetLight ("LeftLowerArm", "LeftUpperArm");
            Setup free = Create ();
            Pin (free, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (free, HumanBodyBones.RightFoot, Vector3.zero, true);
            Solve (free);
            free.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (free, HumanBodyBones.Hips, free.forward * 0.15f - free.up * 0.1f);
            Solve (free);
            Pose[] expected = Poses (free.rig.editingBones.ToArray ());
            free.body.ClearCarry ();

            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.LeftLowerArm, Vector3.zero);
            Pin (s, HumanBodyBones.LeftUpperArm, Vector3.zero);
            Solve (s);
            s.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (s, HumanBodyBones.Hips, s.forward * 0.15f - s.up * 0.1f);
            Solve (s);
            AssertUnchanged (s.rig.editingBones.ToArray (), expected, 1e-4f, 0.01f, "肘・上腕を Free にしたときと同じ");
            s.body.ClearCarry ();
        }

        [Test]
        public void LiftingHeavyNeck_PullsPinnedHips ()
        {
            // 腰を Pin にして、重い首をつかんで持ち上げると、届かない分だけ Pin の腰が引かれて上がり、首は届く。
            // 腰は根で最後に置かれるので、Pin の点を外して解き直さないと、首の引きが腰の Pin に勝てなかった（PullPins）
            Setup s = Create ();
            Pin (s, HumanBodyBones.Hips, Vector3.zero, true);
            Pin (s, HumanBodyBones.Neck, Vector3.zero);
            Solve (s);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Vector3 hipsRest = hips.position;
            s.body.MarkGrabbed (HumanBodyBones.Neck);
            Pin (s, HumanBodyBones.Neck, s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Neck), kReach, "首は届く");
            Assert.Greater (hips.position.y - hipsRest.y, 0.05f, "Pin の腰は引かれて上がる");
            s.body.ClearCarry ();
        }

        [Test]
        public void LiftingHeavyNeck_ReleasesOnlyThePinsOnTheWayToTheRoot ()
        {
            // 腰と左手を Pin にして、重い首を 10cm 持ち上げる。首から根へたどる道の上にある腰の Pin だけが外れて上がり、
            // 届く所にある左手の Pin は留まる（10-28 ユーザー報告「首の点を動かすと、簡単に腰の点が動く。さらに腰の点の動きにあわせて、両手両足の点が動く」）
            Setup s = Create ();
            Pin (s, HumanBodyBones.Hips, Vector3.zero, true);
            // 肘を曲げておく（伸びきった腕のままだと、肩が上がると手に届かない）
            Pin (s, HumanBodyBones.LeftHand, s.forward * 0.15f - ArmDirection (s, false) * 0.15f, true);
            Pin (s, HumanBodyBones.Neck, Vector3.zero);
            Solve (s);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Vector3 hipsRest = hips.position, handRest = hand.position;
            s.body.MarkGrabbed (HumanBodyBones.Neck);
            Pin (s, HumanBodyBones.Neck, s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Neck), kReach, "首は届く");
            Assert.Greater (hips.position.y - hipsRest.y, 0.05f, "道の上の Pin の腰は引かれて上がる");
            Assert.LessOrEqual (Vector3.Distance (handRest, hand.position), 5e-3f, "届く所にある左手の Pin は留まる");
            s.body.ClearCarry ();
        }

        [Test]
        public void PulledPins_BurstMatchesManaged ()
        {
            // 重い腰をつかんだ間（軽い Pin の点を外し、重い Pin の点を効かせる）も、Burst とマネージドで同じ答えになる
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.LeftLowerArm, Vector3.zero);
            Solve (s);
            s.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (s, HumanBodyBones.Hips, s.forward * 0.15f - s.up * 0.1f);
            AssertBurstMatchesManaged (s);
            s.body.ClearCarry ();
        }

        [Test]
        public void GrabbingLightAndHeavyPoints_SolvesLikeTheHeavyPoint ()
        {
            // 軽い手と重い腰をまとめてつかむと、一番重い腰で決める（重い点をつかんだときと同じ。Free の重い点も引かれる）。
            // 手も重い定義で、同じ 2 点をつかんだときと同じ姿勢になる
            Setup expected = Create ();
            expected.body.MarkGrabbed (HumanBodyBones.LeftHand);
            expected.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (expected, HumanBodyBones.Hips, Vector3.zero);
            Pin (expected, HumanBodyBones.LeftHand, expected.forward * 1f);
            Solve (expected);
            Pose[] pose = Poses (expected.rig.editingBones.ToArray ());
            expected.body.ClearCarry ();

            definition_ = CreateDefinition ();
            SetLight ("LeftHand", "LeftLowerArm", "LeftUpperArm");
            Setup s = Create ();
            s.body.MarkGrabbed (HumanBodyBones.LeftHand);
            s.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.LeftHand, s.forward * 1f);
            Solve (s);
            AssertUnchanged (s.rig.editingBones.ToArray (), pose, 1e-4f, 0.01f, "手も重い定義と同じ");
            s.body.ClearCarry ();
        }

        [Test]
        public void LightShoulder_MovesOnlyTheArm_WhenTorsoIsHeavy ()
        {
            // 左上腕の付け根（肩）の点だけを軽く（0）する。肩を動かしても、胴・首・頭・右肩は動かない（重い点を動かない物として解く）。
            // このテストの骨には鎖骨が無いので、肩は重い胸に付いたまま動けず、届かない
            SetLight ("LeftUpperArm");
            Setup s = Create ();
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            s.body.MarkGrabbed (HumanBodyBones.LeftUpperArm);
            Pin (s, HumanBodyBones.LeftUpperArm, s.forward * 0.05f + s.up * 0.03f);
            Solve (s);
            Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftUpperArm), 0.05f, "肩は胸に付いたまま");
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "左腕のほか", LeftArm (s));
        }

        [Test]
        public void LightShoulder_WithLightTorso_KeepsHeavyNeckAndHead ()
        {
            // 胸・お腹も軽くすると胴は引かれて回るが、重い首の点は動かない物なので、首とその向こうの頭は動かない
            SetLight ("LeftUpperArm", "Chest", "Stomach");
            Setup s = Create ();
            Solve (s);
            Transform neck = Bone (s, HumanBodyBones.Neck);
            Transform head = Bone (s, HumanBodyBones.Head);
            Vector3 neckRest = neck.position, headRest = head.position;
            Quaternion headRotation = head.rotation;
            s.body.MarkGrabbed (HumanBodyBones.LeftUpperArm);
            Pin (s, HumanBodyBones.LeftUpperArm, s.forward * 0.1f + s.up * 0.05f);
            Solve (s);
            Assert.LessOrEqual (Vector3.Distance (neckRest, neck.position), 1e-4f, "首は動かない");
            Assert.LessOrEqual (Vector3.Distance (headRest, head.position), 1e-4f, "頭は動かない");
            Assert.LessOrEqual (Quaternion.Angle (headRotation, head.rotation), 0.05f, "頭は回らない");
        }

        // ---- 動かせない点（passive。S89）: 軽い点をつかんでいる間だけ、Pin の点と同じ壁になる ----

        /// <summary>左右の肩（鎖骨）の付け根（offset だけずらした所。体の向き・humanScale に対する割合）に、動かせない点を足す。Create の前に呼ぶ</summary>
        void AddPassiveShoulders (Vector3 offset = default)
        {
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder }) {
                definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = bone.ToString (), bone = bone, offset = offset, weight = 0, passive = true });
            }
        }

        [Test]
        public void PassiveShoulder_StopsLightElbowAtTheShoulder ([Values (false, true)] bool passive)
        {
            // 胴・腕を軽く（0）し、肘をつかんで前へ動かす。鎖骨の付け根に動かせない点があれば、そこが壁になり、
            // 鎖骨から先だけが動く（胴・首・頭・反対の腕は動かない）。無ければ、軽い胴が引かれて回る。
            // 手も軽くする（重い手は Free でも壁になり、肘は手と肩の間で少ししか動けない）
            SetLight ("LeftHand", "LeftLowerArm", "LeftUpperArm", "Chest", "Stomach");
            if (passive) AddPassiveShoulders ();
            Assert.IsEmpty (definition_.Validate ());
            Setup s = Create (shoulders: true);
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            Transform elbow = Bone (s, HumanBodyBones.LeftLowerArm);
            Transform chest = Bone (s, HumanBodyBones.Chest);
            Vector3 elbowRest = elbow.position;
            Quaternion chestRest = chest.rotation;
            Dictionary<Transform, float> lengths = Lengths (s);
            s.body.MarkGrabbed (HumanBodyBones.LeftLowerArm);
            Pin (s, HumanBodyBones.LeftLowerArm, s.forward * 0.1f + s.up * 0.05f);
            Solve (s);
            Assert.Greater (Vector3.Distance (elbowRest, elbow.position), 0.02f, "肘は動く");
            if (passive) AssertUnchanged (bones, rest, 1e-4f, 0.01f, "左の鎖骨から先のほか", LeftArm (s));
            else Assert.Greater (Quaternion.Angle (chestRest, chest.rotation), 0.5f, "動かせない点が無ければ、胸は引かれて回る");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
            s.body.ClearCarry ();
        }

        [Test]
        public void OffsetPassiveShoulder_TurnsShoulderAboutTheAxis ()
        {
            // 動かせない点を鎖骨の付け根から前へずらして置き、軽い肘を上へ動かす。鎖骨は付け根と点を結ぶ軸（体の前後）まわりに回って肩が上がり、
            // 付け根と点は動かず、胸から先（胴・首・頭・反対の腕）も動かない（10-28 ユーザー指示「やっぱり回したい」）
            SetLight ("LeftHand", "LeftLowerArm", "LeftUpperArm", "Chest", "Stomach");
            AddPassiveShoulders (new Vector3 (0, 0, 0.05f));
            Setup s = Create (shoulders: true);
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            BodyPointId id = new BodyPointId ("LeftShoulder");
            Transform shoulder = Bone (s, HumanBodyBones.LeftShoulder);
            Transform upperArm = Bone (s, HumanBodyBones.LeftUpperArm);
            Vector3 point, pointNow;
            s.body.TryGetBonePointWorld (id, out point);
            Vector3 root = shoulder.position;
            Vector3 axis = (point - root).normalized;
            Assert.Greater (Vector3.Dot (axis, s.forward), 0.99f, "点は付け根の前");
            Quaternion shoulderRest = shoulder.rotation;
            float armHeight = upperArm.position.y;
            Dictionary<Transform, float> lengths = Lengths (s);
            s.body.MarkGrabbed (HumanBodyBones.LeftLowerArm);
            Pin (s, HumanBodyBones.LeftLowerArm, s.up * 0.1f);
            Solve (s);
            s.body.TryGetBonePointWorld (id, out pointNow);
            float angle;
            Vector3 turnAxis;
            (shoulder.rotation * Quaternion.Inverse (shoulderRest)).ToAngleAxis (out angle, out turnAxis);
            Assert.Greater (angle, 1f, "鎖骨は回る");
            Assert.Greater (Mathf.Abs (Vector3.Dot (turnAxis, axis)), 0.99f, "付け根と点を結ぶ軸まわりに回る");
            Assert.Greater (upperArm.position.y - armHeight, 0.002f, "肩（上腕の付け根）が上がる");
            Assert.LessOrEqual (Vector3.Distance (root, shoulder.position), 1e-4f, "鎖骨の付け根は動かない");
            Assert.LessOrEqual (Vector3.Distance (point, pointNow), 1e-3f, "動かせない点は動かない");
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "左の鎖骨から先のほか", LeftArm (s));
            AssertLengths (lengths, 1e-4f, "骨の長さ");
            s.body.ClearCarry ();
        }

        [Test]
        public void PassiveShoulderAtRoot_ChangesNothing_ForHeavyPointsOrKeys ([Values (false, true)] bool grab)
        {
            // 付け根に置いた動かせない点は、軸を決めないので骨を縛らない。重い点をつかむ（腰）・キーから解き直す（つかまない）ときは、動かせない点の無い定義と同じ姿勢
            Setup plain = Create (shoulders: true);
            PullForPassive (plain, grab);
            Pose[] expected = Poses (plain.rig.editingBones.ToArray ());
            plain.body.ClearCarry ();

            definition_ = CreateDefinition ();
            AddPassiveShoulders ();
            Setup s = Create (shoulders: true);
            PullForPassive (s, grab);
            AssertUnchanged (s.rig.editingBones.ToArray (), expected, 1e-4f, 0.01f, "動かせない点の無い定義と同じ");
            s.body.ClearCarry ();
        }

        /// <summary>grab なら重い腰をつかんで前へ、そうでなければ手を固定して解く（キーから解き直すのと同じ）</summary>
        static void PullForPassive (Setup s, bool grab)
        {
            if (grab) {
                s.body.MarkGrabbed (HumanBodyBones.Hips);
                Pin (s, HumanBodyBones.Hips, s.forward * 0.1f);
            }
            else {
                Pin (s, HumanBodyBones.LeftHand, ForwardDown (s));
            }
            Solve (s);
        }

        [Test]
        public void OffsetPassiveShoulder_StaysOnTheChest ([Values (false, true)] bool grab)
        {
            // 鎖骨の付け根から前へずらした動かせない点は、胸から見ても動かない。重い手をつかんで引く（grab）ときも、キーから解き直す（つかまない）ときも、
            // 鎖骨は付け根と点を結ぶ軸まわりにだけ回る（10-28 ユーザー指示「passiveな制御点はローカルで固定されているので動かないようにしてほしい」）
            AddPassiveShoulders (new Vector3 (0, 0, 0.05f));
            Setup s = Create (shoulders: true);
            Solve (s);
            BodyPointId id = new BodyPointId ("LeftShoulder");
            Transform shoulder = Bone (s, HumanBodyBones.LeftShoulder);
            Transform chest = shoulder.parent;
            Vector3 point;
            s.body.TryGetBonePointWorld (id, out point);
            Vector3 onChest = chest.InverseTransformPoint (point);
            Vector3 axis = chest.InverseTransformDirection (point - shoulder.position).normalized;
            Quaternion relative = Quaternion.Inverse (chest.rotation) * shoulder.rotation;
            Dictionary<Transform, float> lengths = Lengths (s);
            if (grab) s.body.MarkGrabbed (HumanBodyBones.LeftHand);
            Pin (s, HumanBodyBones.LeftHand, ArmDirection (s, false) * 0.1f + s.up * 0.1f);
            Solve (s);
            s.body.TryGetBonePointWorld (id, out point);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kReach, "手は届く");
            Assert.LessOrEqual (Vector3.Distance (onChest, chest.InverseTransformPoint (point)), 1e-3f, "動かせない点は胸から見て動かない");
            float angle;
            Vector3 turnAxis;
            ((Quaternion.Inverse (chest.rotation) * shoulder.rotation) * Quaternion.Inverse (relative)).ToAngleAxis (out angle, out turnAxis);
            Assert.Greater (angle, 1f, "鎖骨は回る");
            Assert.Greater (Mathf.Abs (Vector3.Dot (turnAxis, axis)), 0.99f, "付け根と点を結ぶ軸まわりにだけ回る");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
            s.body.ClearCarry ();
        }

        [Test]
        public void OffsetPassiveShoulder_BurstMatchesManaged ()
        {
            // 軸まわりにだけ回る関節（axisLock）でも、Burst とマネージドで同じ答えになる
            // （手を外へ伸ばしきって引くと、動かせない点が無くても 0.12mm 食い違う姿勢なので、届く所で比べる）
            AddPassiveShoulders (new Vector3 (0, 0, 0.05f));
            Setup s = Create (shoulders: true);
            Pin (s, HumanBodyBones.LeftHand, ForwardDown (s));
            AssertBurstMatchesManaged (s);
        }

        [Test]
        public void PassiveShoulder_IsNotUsed_WhenTheTorsoSideIsGrabbed ([Values (false, true)] bool withHand)
        {
            // 胸（肩の根の側）をつかんで動かすときは、動かせない点を壁にしない（力は肩の先から伝わってこない）。重い手も一緒につかんでも同じ（10-28 ユーザー報告
            // 「passiveとふつうの点とを複数選択して移動するとpassiveの点が動く」。軽い胸だけをつかむと、両肩の点が壁になって胸が動かなかった）。
            // 点は付け根に置く（付け根から離すと軸まわりにだけ回るので、無い定義とは姿勢が違う）
            SetLight ("Chest", "Stomach");
            Setup plain = Create (shoulders: true);
            GrabTorso (plain, withHand);
            Pose[] expected = Poses (plain.rig.editingBones.ToArray ());
            plain.body.ClearCarry ();

            definition_ = CreateDefinition ();
            SetLight ("Chest", "Stomach");
            AddPassiveShoulders ();
            Setup s = Create (shoulders: true);
            GrabTorso (s, withHand);
            AssertUnchanged (s.rig.editingBones.ToArray (), expected, 1e-4f, 0.01f, "動かせない点の無い定義と同じ");
            s.body.ClearCarry ();
        }

        /// <summary>胸の点（withHand なら左手も）をつかんで、前へ動かして解く（まとめて選んで動かすのと同じ）</summary>
        static void GrabTorso (Setup s, bool withHand)
        {
            Solve (s);
            BodyPointId chest = new BodyPointId ("Chest");
            Vector3 position;
            Quaternion rotation;
            s.body.TryGetPointWorld (chest, out position, out rotation);
            s.body.MarkGrabbed (chest);
            s.body.CapturePoint (chest);
            s.body.TryGetBonePointWorld (chest, out position);
            s.body.SetPointPositionWorld (chest, position + s.forward * 0.1f);
            s.body.SetPositionWeight (chest, 1);
            if (withHand) {
                s.body.MarkGrabbed (HumanBodyBones.LeftHand);
                Pin (s, HumanBodyBones.LeftHand, s.forward * 0.1f);
            }
            Solve (s);
        }

        [Test]
        public void LightHead_TurnsHeavyNeck_AndKeepsNeckPoint ()
        {
            // 首の点は重く（1）、頭の点を軽く（0）する。頭の点を前へ動かすと、首が付け根まわりに回って頭が前へ出る。首の付け根と胸から根の側は動かない
            SetLight ("Head");
            Setup s = Create ();
            Solve (s);
            Transform neck = Bone (s, HumanBodyBones.Neck);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] rest = Poses (bones);
            s.body.MarkGrabbed (HumanBodyBones.Head);
            Pin (s, HumanBodyBones.Head, s.forward * 0.03f);
            Solve (s);
            Vector3 target;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.Head, out target, out rotation);
            Transform head = Bone (s, HumanBodyBones.Head);
            // 首の長さは変わらないので、目標が首の付け根を中心とした球の外なら、その差だけは残る
            float gap = Vector3.Distance (target, neck.position) - Vector3.Distance (head.position, neck.position);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Head), Mathf.Max (0, gap) + 1e-3f, "頭が目標の方へ出る");
            Assert.Greater (Quaternion.Angle (rest[System.Array.IndexOf (bones, neck)].rotation, neck.rotation), 1f, "首が回った");
            AssertUnchanged (bones, rest, 1e-4f, 0.01f, "首より根の側", b => b == neck || b.IsChildOf (neck));
        }

        [Test]
        public void LightHips_GoesDown_AndHeavyFeetStay ()
        {
            // 足の点だけを重く（1）し、ほかを全部軽く（0）する。腰の点（付け根の点も 0 にできる）を下げると、膝が曲がり、足は動かない
            foreach (EditRigDefinition.BodyPoint point in definition_.bodyPoints) {
                if (!point.contact) point.weight = 0;
            }
            Assert.IsEmpty (definition_.Validate ());
            Setup s = Create ();
            Solve (s);
            Vector3 leftFoot = Bone (s, HumanBodyBones.LeftFoot).position;
            Vector3 rightFoot = Bone (s, HumanBodyBones.RightFoot).position;
            s.body.MarkGrabbed (HumanBodyBones.Hips);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は下がる");
            Assert.LessOrEqual (Vector3.Distance (leftFoot, Bone (s, HumanBodyBones.LeftFoot).position), 1e-4f, "左足は動かない");
            Assert.LessOrEqual (Vector3.Distance (rightFoot, Bone (s, HumanBodyBones.RightFoot).position), 1e-4f, "右足は動かない");
        }

        [Test]
        public void DirectionPoints_AreCreatedForHipsChestAndHead ([Values (true, false)] bool withChest)
        {
            Setup s = Create (withChest);
            BodyPointId[] directions = s.body.pointIds.Where (id => id.name.EndsWith ("Direction")).ToArray ();
            Assert.AreEqual (3, directions.Length, "腰・胸・頭に 1 つずつ");
            Assert.IsTrue (s.body.HasPoint (BodyPointId.Direction (HumanBodyBones.Hips)));
            Assert.IsTrue (s.body.HasPoint (BodyPointId.Direction (HumanBodyBones.Head)));
            BodyPointId chest = ChestDirection (s);
            Assert.IsNotNull (s.body.GetBone (chest));

            // 骨の前に出ている（体の大きさに対する割合で）
            foreach (BodyPointId id in directions) {
                Vector3 onBone;
                s.body.TryGetBonePointWorld (id, out onBone);
                Vector3 offset = onBone - s.body.GetBone (id).position;
                Assert.Greater (Vector3.Dot (offset, s.forward), 0.2f * s.rig.humanScale, id + " は骨の前");
                Assert.IsFalse (s.body.PointHasRotation (id), "向きの点は向きを持たない");
            }

            definition_.bodyPoints.RemoveAll (p => p.name.EndsWith ("Direction"));
            Setup none = Create (withChest);
            Assert.IsFalse (none.body.pointIds.Any (id => id.name.EndsWith ("Direction")), "定義の一覧から消すと無くなる");
        }

        [Test]
        public void DirectionPoint_PullsBone_LikeAnyPoint ([Values (HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.Head)] HumanBodyBones which)
        {
            // 重さ 1 の向きの点も、骨の上の点の 1 つ（S26）。動かすと点が目標へ届くように骨ごと引かれ、遠くへ置けば体も引かれる
            SetDirectionWeight (1);
            Setup s = Create ();
            BodyPointId id = which == HumanBodyBones.Chest ? ChestDirection (s) : BodyPointId.Direction (which);
            Dictionary<Transform, float> lengths = Lengths (s);
            Vector3 hips = Bone (s, HumanBodyBones.Hips).position;
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;

            PinDirection (s, id, side * 0.1f + s.up * 0.03f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (id), kReach, "点が目標に届く");
            AssertLengths (lengths, 1e-4f, "骨の長さ");

            PinDirection (s, id, side * 3f);
            Solve (s);
            Assert.Greater (Vector3.Distance (hips, Bone (s, HumanBodyBones.Hips).position), 0.02f, "遠い点へは体ごと引かれる");
            AssertLengths (lengths, 1e-4f, "遠い点でも骨の長さ");
        }

        [Test]
        public void HeadPoints_OnSameBone_AreRigid ()
        {
            // 頭の点（付け根）と顔の前の点（重さ 1）は同じ骨の上: 付け根を固定して顔の前の点を付け根まわりに回した所へ置くと、2 点とも届き頭が回る
            SetDirectionWeight (1);
            Setup s = Create ();
            BodyPointId id = BodyPointId.Direction (HumanBodyBones.Head);
            Transform head = Bone (s, HumanBodyBones.Head);
            Quaternion rotation = head.rotation;
            Vector3 front;
            Assert.IsTrue (s.body.TryGetBonePointWorld (id, out front));
            Pin (s, HumanBodyBones.Head, Vector3.zero);
            PinDirection (s, id, head.position + Quaternion.AngleAxis (30, s.up) * (front - head.position) - front);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Head), kReach, "頭の点に届く");
            Assert.LessOrEqual (s.body.GetResidual (id), kReach, "顔の前の点に届く");
            // 2 点を結ぶ軸まわりの回りは決まらない（元の姿勢のまま）ので、回る角度は 30° に近いが一致はしない
            Assert.Greater (Quaternion.Angle (rotation, head.rotation), 20f, "頭が付け根まわりに回る");
        }

        [Test]
        public void HeadTop_TiltsHead_AndWithFrontPointOnlySetsRoll ()
        {
            // 重さ 1 の頭の点（S26 の動き）
            SetDirectionWeight (1);
            Setup s = Create ();
            BodyPointId top = BodyPointId.Top (HumanBodyBones.Head);
            BodyPointId front = BodyPointId.Direction (HumanBodyBones.Head);
            Assert.IsTrue (s.body.HasPoint (top), "頭のてっぺんの点がある");
            Transform head = Bone (s, HumanBodyBones.Head);
            Vector3 onBone;
            s.body.TryGetBonePointWorld (top, out onBone);
            Assert.Greater (onBone.y, head.position.y + 0.1f, "頭の上にある");
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            Vector3 hips = Bone (s, HumanBodyBones.Hips).position;
            Quaternion rest = head.rotation;

            // てっぺんの点だけ: 頭のてっぺんが点の方を向く（頭がかしぐ）。体は引かれない
            PinDirection (s, top, side * 0.1f);
            Solve (s);
            Assert.LessOrEqual (AimError (s, top), 0.05f, "てっぺんが点の方を向く");
            Assert.Greater (Quaternion.Angle (rest, head.rotation), 10f);
            Assert.LessOrEqual (Vector3.Distance (hips, Bone (s, HumanBodyBones.Hips).position), 0.02f, "腰は引かれない");

            // 顔の前の点も今の場所に固定し、てっぺんを顔の前の点のまわりに回した所へ置く: 同じ骨の 2 点は剛体として拘束しあい、2 点とも届く
            Vector3 frontOnBone;
            s.body.TryGetBonePointWorld (front, out frontOnBone);
            s.body.TryGetBonePointWorld (top, out onBone);
            PinDirection (s, front, Vector3.zero);
            PinDirection (s, top, frontOnBone + Quaternion.AngleAxis (10, s.forward) * (onBone - frontOnBone) - onBone);
            Solve (s);
            // 頭の付け根を首・背骨で横へずらす置き方は、解く回数 48 では詰め切れず 1cm ほど外れる（2026-10-02 実測: 200 回で 2.6mm、1000 回で 0）。
            // 外れるのは位置だけで、2 点の差は同じ（頭の形と向きは合っている）。仕上げの合わせ直しを直すまでは、この精度で見る
            Assert.LessOrEqual (s.body.GetResidual (front), 0.02f, "顔の前の点に届く（1cm ほど外れる）");
            Assert.AreEqual (s.body.GetResidual (front), s.body.GetResidual (top), 1e-3f, "2 点は同じだけ外れる（頭の形と向きは合っている）");

            // 基準の場所に置いた 2 点は、姿勢を変えない
            Setup t = Create ();
            Solve (t);
            Quaternion[] rotations = t.rig.editingBones.Select (b => b.rotation).ToArray ();
            PinDirection (t, top, Vector3.zero);
            PinDirection (t, front, Vector3.zero);
            Solve (t);
            Transform[] bones = t.rig.editingBones.ToArray ();
            for (int i = 0; i < bones.Length; i++) Assert.LessOrEqual (Quaternion.Angle (rotations[i], bones[i].rotation), 0.01f, bones[i].name);
        }

        [Test]
        public void HipsDirection_WithPinnedFeet_TurnsHipsAndKeepsFeet ([Values (0, 1)] int pull)
        {
            SetDirectionWeight (pull);
            Setup s = Create ();
            BodyPointId id = BodyPointId.Direction (HumanBodyBones.Hips);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            Quaternion rotation = Bone (s, HumanBodyBones.Hips).rotation;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero);
            // 腰を少し下げた所に点を置いてから回す（脚が伸びきったままでは、腰を回すと足が届かない）
            Pin (s, HumanBodyBones.Hips, -s.up * 0.08f);
            PinDirection (s, id, side * 0.1f - s.up * 0.08f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), 2 * kReach, "左足は動かない");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightFoot), 2 * kReach, "右足は動かない");
            Assert.Greater (Quaternion.Angle (rotation, Bone (s, HumanBodyBones.Hips).rotation), 10f, "腰が回った");
            if (pull == 0) {
                Assert.LessOrEqual (AimError (s, id), 0.5f, "腰の前の点が目標の方を向く（重さ 0）");
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰の付け根は点に留まり、その場で回る");
            }
        }

        [Test]
        public void ChestPoint_PullsChest_LikeAnyPoint ()
        {
            // 重さ 1 の胸の前の点を横へ: ほかの点と同じく、点が目標へ届くように胸ごと引かれる（向くだけにしない。S26）
            SetDirectionWeight (1);
            Setup s = Create ();
            BodyPointId id = ChestDirection (s);
            Transform chest = s.body.GetBone (id);
            Vector3 origin = chest.position;
            Quaternion rotation = chest.rotation;
            Dictionary<Transform, float> lengths = Lengths (s);
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            PinDirection (s, id, side * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (id), kReach, "胸の前の点が届く");
            Assert.Greater (Quaternion.Angle (rotation, chest.rotation) + Vector3.Distance (origin, chest.position) * 100, 1f, "胸が動いた");
            AssertLengths (lengths, 1e-4f, "骨の長さ");

            // 伸びきった腕の両手も固定して、届かない所へ引く: 手と胸の前の点は同じ固定なので、届かない分を分け合う（壊れない）
            Pin (s, HumanBodyBones.LeftHand, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, Vector3.zero);
            PinDirection (s, id, side * 0.4f);
            Solve (s);
            foreach (Transform bone in s.rig.editingBones) Assert.IsFalse (float.IsNaN (bone.position.x), bone.name);
            AssertLengths (lengths, 1e-4f, "届かないときも骨の長さ");
        }

        [Test]
        public void DirectionPoint_AtRest_LeavesPoseUntouched ([Values (0, 1)] int pull)
        {
            SetDirectionWeight (pull);
            Setup s = Create ();
            Solve (s);
            Vector3[] positions = s.rig.editingBones.Select (b => b.position).ToArray ();
            Quaternion[] rotations = s.rig.editingBones.Select (b => b.rotation).ToArray ();
            foreach (BodyPointId id in s.body.pointIds.Where (p => p.name.EndsWith ("Direction")).ToArray ()) PinDirection (s, id, Vector3.zero);
            Solve (s);
            Transform[] bones = s.rig.editingBones.ToArray ();
            for (int i = 0; i < bones.Length; i++) {
                Assert.LessOrEqual (Vector3.Distance (positions[i], bones[i].position), 1e-5f, bones[i].name);
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], bones[i].rotation), 0.01f, bones[i].name);
            }
        }

        [Test]
        public void DirectionPoints_BurstMatchesManaged ([Values (0, 1)] int pull)
        {
            SetDirectionWeight (pull);
            // 顔の前の点は、目の高さ（S31）にする前の置き方で比べる。重さ 1 で点どうしが引き合うこの組み合わせは、
            // Burst とマネージドの数学関数のわずかな差が広がり、点の高さで差が 0.009〜0.14mm と揺れる（10-05 実測。重さ 0 は 0）
            EditRigDefinition.BodyPoint face = definition_.bodyPoints.First (p => p.name == "HeadDirection");
            face.toward = HumanBodyBones.LastBone;
            face.offset = new Vector3 (0, 0.075f, 0.25f);
            Setup s = Create ();
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, Vector3.zero);
            PinDirection (s, BodyPointId.Direction (HumanBodyBones.Head), side * 0.2f);
            PinDirection (s, BodyPointId.Top (HumanBodyBones.Head), -side * 0.1f);
            PinDirection (s, ChestDirection (s), -side * 0.3f);
            AssertBurstMatchesManaged (s);
        }

        /// <summary>3 本の骨の鎖を上へ（長さ 0.5）。解く前の値を入れる</summary>
        static FullBodyIkData CreateChain (int effectors)
        {
            FullBodyIkData data = FullBodyIkData.Create (3, effectors, Allocator.Temp);
            for (int i = 0; i < 3; i++) {
                data.bones[i] = new FullBodyIkBone { parent = i - 1, stiffness = 0.3f };
                data.positions[i] = new float3 (0, i * 0.5f, 0);
                data.rotations[i] = quaternion.identity;
            }
            data.iterations = 20;
            data.rootPin = 0.6f;
            return data;
        }

        [Test]
        public void Solver_PointsOnSameBone_AreRigid_WithoutTransforms ()
        {
            // 先の骨の付け根と、その前（z）に出した点の 2 点を固定する。付け根はその場、前の点は斜め 45° へ。
            // 2 点の距離は変わらないので、骨は付け根まわりに回って前の点を目標へ向ける（向きの点を特別扱いしない）
            FullBodyIkData data = CreateChain (2);
            try {
                data.effectors[0] = new FullBodyIkEffector { bone = 2, position = new float3 (0, 1, 0), rotation = quaternion.identity, positionWeight = 1 };
                data.effectors[1] = new FullBodyIkEffector {
                    bone = 2, offset = new float3 (0, 0, 0.3f), position = new float3 (0, 1, 0) + 0.3f * math.normalize (new float3 (1, 0, 1)), rotation = quaternion.identity, positionWeight = 1,
                };
                data.effectorCount = 2;
                FullBodyIk.Solve (ref data);
                float3 aimed = math.rotate (data.rotations[2], new float3 (0, 0, 1));
                Assert.LessOrEqual (math.distance (aimed, math.normalize (new float3 (1, 0, 1))), 1e-3f, "前の点が目標の方を向く");
                Assert.LessOrEqual (math.distance (data.positions[2], new float3 (0, 1, 0)), 1e-3f, "付け根の点は動かない");
                Assert.AreEqual (0.5f, math.distance (data.positions[1], data.positions[2]), 1e-4f, "骨の長さ");
            }
            finally {
                data.Dispose ();
            }
        }

        [Test]
        public void Solver_SingleOffsetPoint_PullsTheBone_WithoutTransforms ()
        {
            // 前に出した点だけを固定して遠くへ引く: 骨の付け根も一緒に引かれる（向くだけにしない）
            FullBodyIkData data = CreateChain (1);
            try {
                float3 target = new float3 (0.25f, 1.1f, 0.3f);
                data.effectors[0] = new FullBodyIkEffector { bone = 2, offset = new float3 (0, 0, 0.3f), position = target, rotation = quaternion.identity, positionWeight = 1 };
                data.effectorCount = 1;
                FullBodyIk.Solve (ref data);
                Assert.Greater (math.distance (data.positions[2], new float3 (0, 1, 0)), 0.05f, "付け根が引かれる");
                Assert.LessOrEqual (data.residuals[0], 1e-3f, "点は目標に届く");
                Assert.AreEqual (0.5f, math.distance (data.positions[1], data.positions[2]), 1e-4f, "骨の長さ");
            }
            finally {
                data.Dispose ();
            }
        }

        [Test]
        public void Solver_AimPoint_TurnsOnlyItsBone_WithoutTransforms ([Values (1f, 0.5f)] float weight)
        {
            // 向けるだけの点（aim）: 先の骨の前（z）に出した点を、遠くの横へ。骨は付け根まわりに回って点を目標の方へ向けるだけで、
            // 付け根も手前の骨も動かない。強さが 1 未満なら、回す量が強さの分だけになる
            FullBodyIkData data = CreateChain (1);
            try {
                float3 origin = new float3 (0, 1, 0);
                float3 target = new float3 (2, 1, 0.5f);
                data.effectors[0] = new FullBodyIkEffector { bone = 2, offset = new float3 (0, 0, 0.3f), position = target, rotation = quaternion.identity, positionWeight = weight, aim = 1 };
                data.effectorCount = 1;
                FullBodyIk.Solve (ref data);
                float3 aimed = math.rotate (data.rotations[2], new float3 (0, 0, 1));
                float full = math.degrees (math.acos (math.dot (new float3 (0, 0, 1), math.normalize (target - origin))));
                float turned = math.degrees (math.acos (math.clamp (aimed.z, -1, 1)));
                Assert.AreEqual (full * weight, turned, 0.05f, "回した角度");
                if (weight >= 1) {
                    Assert.LessOrEqual (math.distance (aimed, math.normalize (target - origin)), 1e-3f, "前の点が目標の方を向く");
                    Assert.LessOrEqual (data.residuals[0], 1e-3f, "残りは線と目標の離れ（距離は合わせない）");
                }
                Assert.LessOrEqual (math.distance (data.positions[2], origin), 1e-5f, "付け根は動かない");
                for (int i = 0; i < 2; i++) Assert.Greater (math.abs (math.dot (data.rotations[i].value, quaternion.identity.value)), 0.9999999f, "手前の骨 " + i + " は回らない");
            }
            finally {
                data.Dispose ();
            }
        }

        // ---- 骨の上の点（かかと・つま先の先・手のひら） ----

        /// <summary>点を、今の骨の上の点の位置から offset だけ動かした所に置いて Locked にする</summary>
        static void LockPoint (Setup s, BodyPointId id, Vector3 offset)
        {
            PinDirection (s, id, offset);
            s.body.SetLocked (id, true);
        }

        [Test]
        public void ContactPoints_AreCreatedForFeetAndHands ()
        {
            // 足の点はつま先の骨、手のひらの点は指の付け根から置くので、両方を持つキャラで
            Setup s = Create (toes: true, fingers: true, littleFingers: true);
            foreach (HumanBodyBones foot in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }) {
                Vector3 ankle = Bone (s, foot).position;
                Vector3 heel, toe;
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.Heel (foot), out heel), foot + " のかかと");
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.ToeTip (foot), out toe), foot + " のつま先の先");
                Assert.Less (heel.y, ankle.y + 1e-4f, "かかとは足首より下");
                Assert.Less (Vector3.Dot (heel - ankle, s.forward), 0, "かかとは足首より後ろ");
                Assert.Less (Vector3.ProjectOnPlane (heel - ankle, s.up).magnitude, 0.04f, "かかとは足首の真下の近く（踵を支点に回す）");
                Assert.Greater (Vector3.Dot (toe - ankle, s.forward), 0.05f, "つま先の先は足首より前");
                Assert.AreEqual (heel.y, toe.y, 1e-3f, "かかととつま先の先は同じ高さ（足裏）");
                Assert.IsFalse (s.body.PointHasRotation (BodyPointId.Heel (foot)));

                // 足裏の中心と小指側は足の骨に乗り、足首とつま先の先の間。足裏の中心は足首の真正面、小指側はそこから体の外へ
                Vector3 ball, side;
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.SoleBall (foot), out ball), foot + " の足裏の中心");
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.LittleToeSide (foot), out side), foot + " の小指側");
                Assert.AreEqual (Bone (s, foot), s.body.GetBone (BodyPointId.SoleBall (foot)), "足裏の中心は足に乗る");
                Assert.AreEqual (Bone (s, foot), s.body.GetBone (BodyPointId.LittleToeSide (foot)), "小指側は足に乗る");
                Assert.Greater (Vector3.Dot (ball - ankle, s.forward), 0.02f, "足裏の中心は足首より前");
                Assert.Less (Vector3.Dot (ball - toe, s.forward), -0.02f, "足裏の中心はつま先の先より後ろ");
                Vector3 outside = Vector3.ProjectOnPlane (ankle - Bone (s, HumanBodyBones.Hips).position, s.up).normalized;
                Assert.AreEqual (0, Vector3.Dot (ball - ankle, outside), 1e-3f, "足裏の中心は足首の真正面（左右にずれない）");
                Assert.Greater (Vector3.Dot (side - ball, outside), 0.02f, "小指側は体の外");
                Assert.AreEqual (0, Vector3.Dot (side - ball, s.forward), 1e-3f, "小指側は足裏の中心の真横");
                // 4 点（踵・足裏の中心・小指側・つま先の先）は、床との当たりの球の下の端が地面（基準姿勢のルートの高さ）に着く高さ（10-22）
                float ground = s.rig.root.transform.position.y + EditRigDefinition.kSoleRadius * s.rig.humanScale;
                foreach (Vector3 p in new[] { heel, ball, side, toe }) Assert.AreEqual (ground, p.y, 1e-4f, "地面からの高さは半径");
            }
            Assert.IsFalse (s.body.HasPoint (HumanBodyBones.LeftToes), "つま先の骨の付け根には点を置かない（球とつま先の先で足りる）");
            foreach (HumanBodyBones hand in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand }) {
                Vector3 palm;
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.PalmIndex (hand), out palm), hand + " の手のひら");
                Assert.Greater (Vector3.Distance (palm, Bone (s, hand).position), 0.01f, "手のひらは手首から離れている");
            }
            Assert.AreEqual ("Controls/Body/LeftHeel", RigPaths.BodyPoint (BodyPointId.Heel (HumanBodyBones.LeftFoot).name));
            Assert.AreEqual ("Controls/Body/RightToeTip", RigPaths.BodyPoint (BodyPointId.ToeTip (HumanBodyBones.RightFoot).name));
            Assert.AreEqual ("Controls/Body/LeftPalmIndex", RigPaths.BodyPoint (BodyPointId.PalmIndex (HumanBodyBones.LeftHand).name));
            Assert.AreEqual ("Controls/Body/RightPalmLittle", RigPaths.BodyPoint (BodyPointId.PalmLittle (HumanBodyBones.RightHand).name));

            definition_.bodyPoints.RemoveAll (p => p.origin != HumanBodyBones.LastBone || p.toward != HumanBodyBones.LastBone);
            Setup none = Create (toes: true, fingers: true, littleFingers: true);
            Assert.IsFalse (none.body.HasPoint (BodyPointId.Heel (HumanBodyBones.LeftFoot)), "定義の一覧から消すと無くなる");
            Assert.IsFalse (none.body.HasPoint (BodyPointId.PalmIndex (HumanBodyBones.LeftHand)));
        }

        /// <summary>
        /// しゃがんだ姿勢で膝と足首を Pin、足裏の中心・つま先の先を Locked にし、腰だけ前へ動かす:
        /// 膝の点は保てなくても、足首と足の点は留まる（Pin はなるべく留まるが、両立しないときは膝・肘より手足の先を優先。10-05）
        /// </summary>
        [Test]
        public void HipsMovedForward_WithKneesAndFeetPinned_KeepsFeet_LetsKneesYield ([Values (0.02f, 0.05f, 0.1f)] float forward)
        {
            Setup s = Create (toes: true);
            Dictionary<Transform, float> lengths = Lengths (s);
            HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            HumanBodyBones[] knees = { HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg };
            foreach (HumanBodyBones foot in feet) Pin (s, foot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f);
            s.body.SetLocked (HumanBodyBones.Hips, true);
            Solve (s);
            Vector3[] ankles = feet.Select (f => Bone (s, f).position).ToArray ();
            foreach (HumanBodyBones knee in knees) Pin (s, knee, Vector3.zero);
            foreach (HumanBodyBones foot in feet) {
                LockPoint (s, BodyPointId.SoleBall (foot), Vector3.zero);
                LockPoint (s, BodyPointId.ToeTip (foot), Vector3.zero);
            }
            PinDirection (s, BodyPointId.LittleToeSide (HumanBodyBones.LeftFoot), Vector3.zero);

            s.body.SetPointPositionWorld (HumanBodyBones.Hips, Bone (s, HumanBodyBones.Hips).position + s.forward * forward);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は点に届く");
            for (int i = 0; i < feet.Length; i++) {
                Assert.LessOrEqual (s.body.GetResidual (feet[i]), kReach, feet[i] + " の足首は動かない");
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.SoleBall (feet[i])), kReach, feet[i] + " の足裏の中心は動かない");
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.ToeTip (feet[i])), kReach, feet[i] + " のつま先の先は動かない");
                Assert.LessOrEqual (Vector3.Distance (ankles[i], Bone (s, feet[i]).position), kReach, feet[i] + " の足首は元の場所");
            }
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void HeelAndToeLocked_KeepFootPlanted_WhenHipsGoDown ()
        {
            // 足の骨の上の 2 点（かかとと足裏の中心）を固定すれば、足は回らない。
            // つま先の先はつま先の骨に乗るので、かかととつま先の先の 2 点では足の骨の向きは決まらない
            Setup s = Create (toes: true);
            Dictionary<Transform, float> lengths = Lengths (s);
            HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            Quaternion[] rotations = feet.Select (f => Bone (s, f).rotation).ToArray ();
            foreach (HumanBodyBones foot in feet) {
                LockPoint (s, BodyPointId.Heel (foot), Vector3.zero);
                LockPoint (s, BodyPointId.SoleBall (foot), Vector3.zero);
            }
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f + s.forward * 0.03f);
            Solve (s);
            for (int i = 0; i < feet.Length; i++) {
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.Heel (feet[i])), kReach, feet[i] + " のかかとは動かない");
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.SoleBall (feet[i])), kReach, feet[i] + " の足裏の中心は動かない");
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], Bone (s, feet[i]).rotation), 0.1f, feet[i] + " は回らない（足裏が付いたまま）");
            }
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は下がる");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void ToeTipLocked_LetsHeelRise_WhenHipsGoUp ()
        {
            // つま先の先はつま先の骨のあるキャラにだけできる
            Setup s = Create (toes: true);
            HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            Quaternion[] rotations = feet.Select (f => Bone (s, f).rotation).ToArray ();
            Vector3[] ankles = feet.Select (f => Bone (s, f).position).ToArray ();
            foreach (HumanBodyBones foot in feet) LockPoint (s, BodyPointId.ToeTip (foot), Vector3.zero);
            // 脚は伸びきっているので、腰を上げると、つま先の先を支点にかかとが上がるしかない
            Pin (s, HumanBodyBones.Hips, s.up * 0.03f);
            Solve (s);
            for (int i = 0; i < feet.Length; i++) {
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.ToeTip (feet[i])), kReach, feet[i] + " のつま先の先は動かない");
                Assert.Greater (Quaternion.Angle (rotations[i], Bone (s, feet[i]).rotation), 1f, feet[i] + " は回る");
                Assert.Greater (Bone (s, feet[i]).position.y, ankles[i].y + 0.002f, feet[i] + " の足首（かかと）が上がる");
            }
        }

        // ---- 点の一覧（S26b） ----

        [Test]
        public void FootPoints_LieOnFootCenterLine_WhenToesTurnOut ()
        {
            // つま先が外を向いた足（つま先の付け根を外へ 5cm）。踵・足裏の中心・つま先の先は、足首からつま先の付け根へ向かう線の上に並ぶ
            // （足裏の中心を支点に回すと足の真ん中で回る）。小指側は足裏の中心から、その線の真横の外
            Setup s = Create (toes: true, toeOut: 0.05f);
            foreach (HumanBodyBones foot in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }) {
                Vector3 ankle = Bone (s, foot).position;
                Vector3 toes = Bone (s, foot == HumanBodyBones.LeftFoot ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes).position;
                Vector3 along = Vector3.ProjectOnPlane (toes - ankle, s.up).normalized;
                Vector3 across = Vector3.Cross (s.up, along).normalized;
                Assert.Greater (Vector3.Angle (along, s.forward), 10f, "つま先が外を向いている");
                Vector3 heel, ball, tip, side;
                s.body.TryGetBonePointWorld (BodyPointId.Heel (foot), out heel);
                s.body.TryGetBonePointWorld (BodyPointId.SoleBall (foot), out ball);
                s.body.TryGetBonePointWorld (BodyPointId.ToeTip (foot), out tip);
                s.body.TryGetBonePointWorld (BodyPointId.LittleToeSide (foot), out side);
                foreach (Vector3 p in new[] { heel, ball, tip }) {
                    Assert.AreEqual (0, Vector3.Dot (p - ankle, across), 1e-3f, foot + " の足の点が中心線の上");
                }
                Assert.AreEqual (0, Vector3.Dot (ball - toes, across), 1e-3f, "足裏の中心はつま先の付け根の真下");
                Assert.AreEqual (0, Vector3.Dot (ball - toes, along), 1e-3f, "足裏の中心はつま先の付け根の真下");
                Assert.AreEqual (0, Vector3.Dot (side - ball, along), 1e-3f, "小指側は足裏の中心の真横");
                Vector3 outside = Vector3.ProjectOnPlane (ankle - Bone (s, HumanBodyBones.Hips).position, s.up);
                Assert.Greater (Vector3.Dot (side - ball, outside), 0, "小指側は体の外");
            }
        }

        [Test]
        public void LittleToeSide_TiltsFootAroundPinnedAnkle ()
        {
            // 足首の位置を固定し、小指側の点を下へ: 足が足首まわりに回って小指側が下がる。脛から根の側は動かない
            // （小指側は重さ 1 だが、同じ骨の足首を固定しているので、足首まわりに回るだけ）
            Setup s = Create (toes: true);
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero);
            Solve (s);
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Pose[] before = Poses (bones);
            BodyPointId side = BodyPointId.LittleToeSide (HumanBodyBones.LeftFoot);
            Vector3 start;
            s.body.TryGetBonePointWorld (side, out start);
            PinDirection (s, side, -s.up * 0.03f);
            Solve (s);
            Vector3 now;
            s.body.TryGetBonePointWorld (side, out now);
            Assert.LessOrEqual (AimError (s, side), 0.5f, "小指側の点が目標の方を向く");
            Assert.Less (now.y, start.y - 0.01f, "小指側が下がる");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首は動かない");
            AssertUnchanged (bones, before, 1e-4f, 0.01f, "脛から根の側", b => b == foot || b.IsChildOf (foot));
        }

        [Test]
        public void FootTipPoints_PullLeg_LikePalmPoints ([Values (false, true)] bool toeTip)
        {
            // 小指側・足先（つま先の先）の点だけを固定して前上へ動かす: 手の 2 点と同じく、点が目標に届くまで脚ごと引く（向けるだけにしない）。
            // 足の点はつま先の骨のあるキャラにだけできる
            Setup s = Create (toes: true);
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Vector3 ankle = foot.position;
            BodyPointId side = toeTip ? BodyPointId.ToeTip (HumanBodyBones.LeftFoot) : BodyPointId.LittleToeSide (HumanBodyBones.LeftFoot);
            PinDirection (s, side, s.forward * 0.1f + s.up * 0.05f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (side), kReach, "点が目標に届く");
            Assert.Greater (Vector3.Distance (ankle, foot.position), 0.05f, "足首も引かれて動く");
        }

        // ---- 顔の前の点の高さ ----

        [Test]
        public void HeadDirection_IsAtEyeHeight ([Values ("eyes", "none")] string source)
        {
            // 顔の前の点は、頭の付け根の真上の目の高さに置き、そこから前へ出す。目の高さは目の骨から、無ければ体の大きさから決める
            Setup s = Create (eyes: source == "eyes");
            Transform head = Bone (s, HumanBodyBones.Head);
            float expected = source == "eyes" ? 0.08f : s.rig.humanScale * EditRigDefinition.kEyeHeight;
            Vector3 onBone;
            Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.Direction (HumanBodyBones.Head), out onBone));
            Assert.AreEqual (head.position.y + expected, onBone.y, 1e-4f, "顔の前の点は目の高さ");
            Assert.Greater (Vector3.Dot (onBone - head.position, s.forward), 0.2f * s.rig.humanScale, "顔の前に出ている");
            Assert.IsEmpty (definition_.Validate ());
        }

        // ---- 膝の Pole（既定の一覧には無いので AddPoles で足す。S56） ----

        [Test]
        public void KneePole_IsPlacedInFrontOfKnee_OnThigh ()
        {
            AddPoles (definition_);
            Setup s = Create ();
            foreach (HumanBodyBones thigh in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg }) {
                BodyPointId pole = KneePole (thigh);
                Transform knee = Bone (s, thigh == HumanBodyBones.LeftUpperLeg ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                Vector3 onBone;
                Assert.IsTrue (s.body.TryGetBonePointWorld (pole, out onBone), thigh + " の膝の Pole");
                Assert.AreEqual (Bone (s, thigh), s.body.GetBone (pole), "太ももに乗る");
                Vector3 expected = knee.position + s.forward * EditRigDefinition.kDirectionLength * s.rig.humanScale;
                Assert.LessOrEqual (Vector3.Distance (expected, onBone), 1e-4f, "膝の前");
                Assert.AreEqual (0, definition_.bodyPoints.First (p => p.name == pole.name).weight, "向けるだけの点");
            }
        }

        [Test]
        public void KneePole_TurnsKneeOutward_WithFeetPinned ()
        {
            // 足を固定して腰を下げる（膝は前へ曲がる）。左の膝の Pole を外へ動かすと、足首は動かずに膝が外を向く
            AddPoles (definition_);
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform knee = Bone (s, HumanBodyBones.LeftLowerLeg);
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f);
            Solve (s);
            Vector3 bent = knee.position;
            Vector3 rightKnee = Bone (s, HumanBodyBones.RightLowerLeg).position;
            Vector3 outside = Vector3.ProjectOnPlane (Bone (s, HumanBodyBones.LeftFoot).position - Bone (s, HumanBodyBones.Hips).position, s.up).normalized;

            BodyPointId pole = KneePole (HumanBodyBones.LeftUpperLeg);
            PinDirection (s, pole, outside * 0.3f);
            Solve (s);
            Assert.Greater (Vector3.Dot (knee.position - bent, outside), 0.02f, "左膝が外へ向く");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "左足首は動かない");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は動かない");
            Assert.LessOrEqual (Vector3.Distance (rightKnee, Bone (s, HumanBodyBones.RightLowerLeg).position), 1e-4f, "右膝は変わらない");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void KneePole_TurnsHalfWithShin_WhenKneeBends ([Values (0f, 0.5f)] float blend)
        {
            // 腿を前へ 90° 上げ、膝を 90° 曲げて脛を真下に戻す。太ももだけの向き（0）なら Pole は膝の真上、
            // 脛と半分ずつ混ぜる（0.5。S34）なら膝の前上 45°（膝の曲がりの二等分の向き）に出る
            AddPoles (definition_);
            Assert.AreEqual (HumanBodyBones.LeftLowerLeg, definition_.bodyPoints.First (p => p.name == "LeftKneePole").blendBone, "脛と混ぜる");
            foreach (EditRigDefinition.BodyPoint point in definition_.bodyPoints) {
                if (point.name.EndsWith ("KneePole")) point.blend = blend;
            }
            Setup s = Create ();
            Transform thigh = Bone (s, HumanBodyBones.LeftUpperLeg);
            Transform shin = Bone (s, HumanBodyBones.LeftLowerLeg);
            thigh.rotation = Quaternion.FromToRotation (-s.up, s.forward) * thigh.rotation;
            shin.rotation = Quaternion.FromToRotation (s.forward, -s.up) * shin.rotation;

            BodyPointId pole = KneePole (HumanBodyBones.LeftUpperLeg);
            Vector3 onBone;
            Assert.IsTrue (s.body.TryGetBonePointWorld (pole, out onBone));
            Vector3 direction = blend > 0 ? (s.forward + s.up).normalized : s.up;
            Vector3 expected = shin.position + direction * EditRigDefinition.kDirectionLength * s.rig.humanScale;
            Assert.LessOrEqual (Vector3.Distance (expected, onBone), 1e-4f, "膝の Pole の場所");
        }

        [Test]
        public void KneePole_PinnedWhereItIs_KeepsBentKnee ()
        {
            // 膝を曲げた姿勢で、膝の Pole を今の場所に固定しても姿勢は変わらない（表示の場所と解く側の場所が同じ。S34）
            AddPoles (definition_);
            Setup s = Create ();
            Transform knee = Bone (s, HumanBodyBones.LeftLowerLeg);
            Pin (s, HumanBodyBones.LeftFoot, s.forward * 0.1f + s.up * 0.25f, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.1f);
            Solve (s);
            Vector3 bent = knee.position;
            Vector3 thighDirection = (knee.position - Bone (s, HumanBodyBones.LeftUpperLeg).position).normalized;
            Vector3 shinDirection = (Bone (s, HumanBodyBones.LeftFoot).position - knee.position).normalized;
            Assert.Greater (Vector3.Angle (thighDirection, shinDirection), 30f, "膝が曲がっている");

            BodyPointId pole = KneePole (HumanBodyBones.LeftUpperLeg);
            PinDirection (s, pole, Vector3.zero);
            Solve (s);
            Assert.LessOrEqual (Vector3.Distance (bent, knee.position), 1e-4f, "膝は動かない");
            Assert.LessOrEqual (s.body.GetResidual (pole), 1e-4f, "Pole は骨の上の場所のまま");
        }

        // ---- 肘の Pole（S43） ----

        [Test]
        public void ElbowPole_IsPlacedBehindElbow_OnUpperArm ()
        {
            AddPoles (definition_);
            Setup s = Create ();
            foreach (HumanBodyBones upper in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm }) {
                BodyPointId pole = ElbowPole (upper);
                Transform elbow = Bone (s, upper == HumanBodyBones.LeftUpperArm ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                Vector3 onBone;
                Assert.IsTrue (s.body.TryGetBonePointWorld (pole, out onBone), upper + " の肘の Pole");
                Assert.AreEqual (Bone (s, upper), s.body.GetBone (pole), "上腕に乗る");
                Vector3 expected = elbow.position - s.forward * EditRigDefinition.kDirectionLength * s.rig.humanScale;
                Assert.LessOrEqual (Vector3.Distance (expected, onBone), 1e-4f, "肘の後ろ（基準姿勢で肘が向く方）");
                EditRigDefinition.BodyPoint point = definition_.bodyPoints.First (p => p.name == pole.name);
                Assert.AreEqual (0, point.weight, "向けるだけの点");
                Assert.AreEqual (kPoleBlend, point.blend, "前腕と半分ずつ混ぜる");
            }
        }

        [Test]
        public void ElbowPole_TurnsElbow_WithHandPinned ()
        {
            // 手を胸の前へ寄せて肘を曲げる。左の肘の Pole を外上へ動かすと、手は動かずに肘がそちらへ回る
            AddPoles (definition_);
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            Transform upper = Bone (s, HumanBodyBones.LeftUpperArm);
            Transform elbow = Bone (s, HumanBodyBones.LeftLowerArm);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Vector3 outward = (hand.position - upper.position).normalized;
            Pin (s, HumanBodyBones.Hips, Vector3.zero);
            Pin (s, HumanBodyBones.Chest, Vector3.zero);
            Pin (s, HumanBodyBones.LeftHand, (-outward * 0.25f + s.forward * 0.25f) * s.rig.humanScale, true);
            Solve (s);
            Vector3 bent = elbow.position;
            Vector3 handAt = hand.position;
            Assert.Greater (Vector3.Angle (elbow.position - upper.position, hand.position - elbow.position), 30f, "肘が曲がっている");

            BodyPointId pole = ElbowPole (HumanBodyBones.LeftUpperArm);
            Vector3 offset = (outward + s.up) * 0.3f * s.rig.humanScale;
            Vector3 target;
            Assert.IsTrue (s.body.TryGetBonePointWorld (pole, out target));
            target += offset;
            PinDirection (s, pole, offset);
            Solve (s);
            Assert.Less (Vector3.Distance (elbow.position, target), Vector3.Distance (bent, target) - 0.02f, "肘が Pole の方へ回る");
            Assert.LessOrEqual (Vector3.Distance (handAt, hand.position), kReach, "手は動かない");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void ElbowPole_Free_DoesNotChangeSolve ()
        {
            // 肘の Pole は、Free なら解いた姿勢を変えない（Pole を足しても、キーを打つまでは今までと同じ姿勢）
            System.Func<Setup, Pose[]> solve = s => {
                s.body.useBurst = false;
                Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
                Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
                Pin (s, HumanBodyBones.Hips, (-s.up * 0.12f + s.forward * 0.05f) * s.rig.humanScale);
                Pin (s, HumanBodyBones.LeftHand, (s.forward * 0.3f - s.up * 0.35f) * s.rig.humanScale, true);
                Pin (s, HumanBodyBones.RightHand, (s.forward * 0.2f + s.up * 0.25f) * s.rig.humanScale);
                Pin (s, HumanBodyBones.RightLowerArm, s.forward * 0.1f * s.rig.humanScale);
                Solve (s);
                return Poses (s.rig.editingBones.ToArray ());
            };
            Pose[] without = solve (Create ());
            AddPoles (definition_);
            definition_.bodyPoints.RemoveAll (p => p.name.EndsWith ("KneePole"));
            Pose[] with = solve (Create ());
            Assert.AreEqual (without.Length, with.Length);
            for (int i = 0; i < with.Length; i++) {
                Assert.LessOrEqual (Vector3.Distance (without[i].position, with[i].position), 1e-5f, i + " の位置");
                Assert.LessOrEqual (Quaternion.Angle (without[i].rotation, with[i].rotation), 1e-3f, i + " の向き");
            }
        }

        [Test]
        public void BlendedBodyPoint_WorksOnOtherLimbs_AndNeedsZeroPull ()
        {
            // 上腕の付け根の後ろに置き、前腕と半分ずつ混ぜる点を自分で足す（肘の Pole とは別の場所）
            EditRigDefinition.BodyPoint elbow = new EditRigDefinition.BodyPoint {
                name = "LeftArmBack", bone = HumanBodyBones.LeftUpperArm, offset = new Vector3 (0, 0, -0.25f),
                weight = 0, blendBone = HumanBodyBones.LeftLowerArm, blend = 0.5f,
            };
            definition_.bodyPoints.Add (elbow);
            Assert.IsEmpty (definition_.Validate ());
            Setup s = Create ();
            BodyPointId id = new BodyPointId ("LeftArmBack");
            Transform upper = Bone (s, HumanBodyBones.LeftUpperArm);
            Transform lower = Bone (s, HumanBodyBones.LeftLowerArm);
            Vector3 arm = (lower.position - upper.position).normalized;
            Vector3 onBone;
            Assert.IsTrue (s.body.TryGetBonePointWorld (id, out onBone));
            Vector3 back = upper.position - s.forward * 0.25f * s.rig.humanScale;
            Assert.LessOrEqual (Vector3.Distance (back, onBone), 1e-4f, "まっすぐな腕では、上腕の付け根の後ろ");

            // 前腕だけを肘で 90° 前へ曲げると、ずれは半分の 45° だけ前へ回る（付け根は上腕に付いたまま）
            lower.rotation = Quaternion.FromToRotation (arm, s.forward) * lower.rotation;
            Assert.IsTrue (s.body.TryGetBonePointWorld (id, out onBone));
            Vector3 expected = upper.position + Quaternion.AngleAxis (45, Vector3.Cross (arm, s.forward)) * (-s.forward) * 0.25f * s.rig.humanScale;
            Assert.LessOrEqual (Vector3.Distance (expected, onBone), 1e-4f, "前腕と半分ずつ混ぜた向き");

            // 重さ 1 の点は混ぜられない・割合は 0〜1
            elbow.weight = 1;
            Assert.IsNotEmpty (definition_.Validate ());
            elbow.weight = 0;
            elbow.blend = 1.5f;
            Assert.IsNotEmpty (definition_.Validate ());
        }

        [Test]
        public void CustomBodyPoint_IsPlacedByOffset_AndSolvedLikeAnyPoint ()
        {
            // 頭の付け根から上へ 0.1・前へ 0.2（humanScale 比）の所に、自分で点を足す
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "Nose", bone = HumanBodyBones.Head, offset = new Vector3 (0, 0.1f, 0.2f) });
            Assert.IsEmpty (definition_.Validate ());
            Setup s = Create ();
            BodyPointId nose = new BodyPointId ("Nose");
            Assert.IsTrue (s.body.HasPoint (nose));
            Transform head = Bone (s, HumanBodyBones.Head);
            Vector3 onBone;
            Assert.IsTrue (s.body.TryGetBonePointWorld (nose, out onBone));
            Vector3 expected = head.position + (s.up * 0.1f + s.forward * 0.2f) * s.rig.humanScale;
            Assert.LessOrEqual (Vector3.Distance (expected, onBone), 1e-4f, "体の向き・体の大きさで置かれる");
            Assert.IsNotNull (s.rig.FindControl ("Controls/Body/Nose"), "名前がコントロールのパスになる");

            PinDirection (s, nose, Vector3.Cross (s.up, s.forward).normalized * 0.05f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (nose), kReach, "ほかの点と同じく届く");

            // 名前の重複はエラー
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "Nose", bone = HumanBodyBones.Head });
            Assert.IsNotEmpty (definition_.Validate ());
        }

        [Test]
        public void BodyPoint_OnMissingBone_RidesOnParent ([Values (false, true)] bool toes)
        {
            // つま先の骨に乗せた点（基準は乗る骨の付け根で、前へずらすだけ）。つま先の骨が無いキャラでは足の骨へ乗る（親をたどる）。
            // 既定のつま先の先は、つま先の骨が無いと作らない（足の点の並ぶ向きが決まらない）ので、自分で足した点で確かめる
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "ToeCap", bone = HumanBodyBones.LeftToes, offset = new Vector3 (0, 0, 0.05f) });
            Setup s = Create (toes: toes);
            BodyPointId cap = new BodyPointId ("ToeCap");
            Assert.IsTrue (s.body.HasPoint (cap));
            Assert.AreEqual (Bone (s, toes ? HumanBodyBones.LeftToes : HumanBodyBones.LeftFoot), s.body.GetBone (cap));
            if (toes) {
                Assert.AreEqual (Bone (s, HumanBodyBones.LeftToes), s.body.GetBone (BodyPointId.ToeTip (HumanBodyBones.LeftFoot)), "既定のつま先の先はつま先の骨に乗る");
            }
        }

        [Test]
        public void FootAndPalmPoints_AreNotCreated_WithoutToesAndFingers ()
        {
            // 足の点はつま先の骨への向きで、手のひらの点は指の付け根に置く。その骨が無いキャラには作らない（足首・手首から見積もらない）。
            // ほかの点（顔の前）はそのまま作る
            Setup s = Create ();
            foreach (HumanBodyBones foot in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }) {
                foreach (BodyPointId id in new[] { BodyPointId.Heel (foot), BodyPointId.SoleBall (foot), BodyPointId.LittleToeSide (foot), BodyPointId.ToeTip (foot) }) {
                    Assert.IsFalse (s.body.HasPoint (id), "つま先の骨が無いと足の点は無い: " + id.name);
                    Assert.IsNull (s.rig.FindControl (RigPaths.BodyPoint (id.name)), "コントロールも作らない: " + id.name);
                }
            }
            foreach (HumanBodyBones hand in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand }) {
                Assert.IsFalse (s.body.HasPoint (BodyPointId.PalmIndex (hand)), "指の骨が無いと手のひらの点は無い: " + hand);
                Assert.IsFalse (s.body.HasPoint (BodyPointId.PalmLittle (hand)), "指の骨が無いと手のひらの点は無い: " + hand);
            }
            Assert.IsTrue (s.body.HasPoint (BodyPointId.Direction (HumanBodyBones.Head)), "顔の前の点はある");

            // 人差し指だけあって小指が無いキャラは、人差し指側の点だけ作る
            Setup index = Create (fingers: true);
            Assert.IsTrue (index.body.HasPoint (BodyPointId.PalmIndex (HumanBodyBones.LeftHand)));
            Assert.IsFalse (index.body.HasPoint (BodyPointId.PalmLittle (HumanBodyBones.LeftHand)));
        }

        [Test]
        public void ToeTipLocked_StaysOnPivot_WhenFootTurnsAroundIt ()
        {
            // つま先の骨があるキャラで、つま先の先を Locked にし、足首の点をつま先の先のまわりで回した場所と向きに置く（点の回転ギズモと同じ置き方）。
            // つま先の先はつま先の骨の上（足とは別の剛体）。足首を回しても、つま先の先は Locked の場所から外れない
            Setup s = Create (toes: true);
            BodyPointId toe = BodyPointId.ToeTip (HumanBodyBones.LeftFoot);
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Vector3 pivot;
            Assert.IsTrue (s.body.TryGetBonePointWorld (toe, out pivot));
            LockPoint (s, toe, Vector3.zero);
            Quaternion turn = Quaternion.AngleAxis (20, s.up);
            s.body.CapturePoint (HumanBodyBones.LeftFoot);
            s.body.SetPointPositionWorld (HumanBodyBones.LeftFoot, pivot + turn * (foot.position - pivot));
            s.body.SetPointRotationWorld (HumanBodyBones.LeftFoot, turn * foot.rotation);
            s.body.SetPositionWeight (HumanBodyBones.LeftFoot, 1);
            s.body.SetRotationWeight (HumanBodyBones.LeftFoot, 1);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (toe), kReach, "つま先の先は中心から動かない");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首は回した場所へ届く");
        }

        [Test]
        public void LockedContactPoint_WinsOverDraggedJointPoint_OnSameBone ()
        {
            // 手のひらを Locked にして手首の点を動かす: 手のひらは動かず、手首は手のひらを支点に届く所まで寄る
            // （手のひらの点は指の骨のあるキャラにだけできる）
            Setup s = Create (fingers: true);
            BodyPointId palm = BodyPointId.PalmIndex (HumanBodyBones.LeftHand);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Quaternion rotation = hand.rotation;
            Vector3 wrist = hand.position;
            LockPoint (s, palm, Vector3.zero);
            Pin (s, HumanBodyBones.LeftHand, s.up * 0.05f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (palm), kReach, "手のひらは動かない");
            Assert.Greater (Quaternion.Angle (rotation, hand.rotation), 5f, "手が手のひらを支点に回る");
            Assert.Greater (hand.position.y, wrist.y + 0.005f, "手首は上がる");

            // 逆: 足首を Locked にして、同じ足の骨の上の足裏の中心を動かす。足首は動かず、足が足首を支点に回る。
            // 足の点はつま先の骨のあるキャラにだけでき、そのキャラのつま先の先はつま先の骨（足とは別の骨）に乗るので、足に乗る足裏の中心で試す
            Setup t = Create (toes: true);
            BodyPointId ball = BodyPointId.SoleBall (HumanBodyBones.LeftFoot);
            Transform foot = Bone (t, HumanBodyBones.LeftFoot);
            Assert.AreEqual (foot, t.body.GetBone (ball), "足裏の中心は足首と同じ骨");
            rotation = foot.rotation;
            LockPoint (t, HumanBodyBones.LeftFoot, Vector3.zero);
            PinDirection (t, ball, t.up * 0.05f);
            Solve (t);
            Assert.LessOrEqual (t.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首は動かない");
            Assert.Greater (Quaternion.Angle (rotation, foot.rotation), 5f, "足が足首を支点に回る");
            Vector3 onBone, target;
            Quaternion ignored;
            t.body.TryGetBonePointWorld (ball, out onBone);
            t.body.TryGetPointWorld (ball, out target, out ignored);
            Assert.LessOrEqual (Vector3.Angle (onBone - foot.position, target - foot.position), 0.1f, "足裏の中心は点の方を向く");
        }

        [Test]
        public void HandPoints_ThreePointsSetHandOrientation ()
        {
            // 手首と人差し指側を Locked にして、小指側を動かす: 先の 2 点は動かず、手がその 2 点の軸まわりに回る
            // （手のひらの 2 点は人差し指・小指の付け根に置くので、小指まであるキャラで）
            Setup s = Create (fingers: true, littleFingers: true);
            HumanBodyBones hand = HumanBodyBones.LeftHand;
            BodyPointId index = BodyPointId.PalmIndex (hand);
            BodyPointId little = BodyPointId.PalmLittle (hand);
            Assert.IsTrue (s.body.HasPoint (index) && s.body.HasPoint (little), "手に 2 点ある");
            Transform bone = Bone (s, hand);
            Vector3 a, b;
            s.body.TryGetBonePointWorld (index, out a);
            s.body.TryGetBonePointWorld (little, out b);
            Assert.Greater (Vector3.Distance (a, b), 0.01f, "2 点は離れている");
            Assert.Greater (Vector3.Distance (a, bone.position), 0.01f);
            Quaternion rotation = bone.rotation;

            LockPoint (s, hand, Vector3.zero);
            LockPoint (s, index, Vector3.zero);
            PinDirection (s, little, s.up * 0.04f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (hand), kReach, "手首は動かない");
            Assert.LessOrEqual (s.body.GetResidual (index), kReach, "人差し指側は動かない");
            Assert.Greater (Quaternion.Angle (rotation, bone.rotation), 5f, "手が回る");
            Vector3 onBone, target;
            Quaternion ignored;
            s.body.TryGetBonePointWorld (index, out a);
            s.body.TryGetBonePointWorld (little, out onBone);
            s.body.TryGetPointWorld (little, out target, out ignored);
            Vector3 axis = (a - bone.position).normalized;
            Assert.LessOrEqual (Vector3.Angle (Vector3.ProjectOnPlane (onBone - bone.position, axis), Vector3.ProjectOnPlane (target - bone.position, axis)), 0.1f,
                "小指側は、先の 2 点の軸まわりで点の方へ回る");

            // 手の 2 点を Locked にして手首を動かす: 2 点は動かず、手首は届く所まで
            Setup t = Create (fingers: true, littleFingers: true);
            rotation = Bone (t, hand).rotation;
            LockPoint (t, index, Vector3.zero);
            LockPoint (t, little, Vector3.zero);
            Pin (t, hand, t.up * 0.05f);
            Solve (t);
            Assert.LessOrEqual (t.body.GetResidual (index), kReach, "人差し指側は動かない");
            Assert.LessOrEqual (t.body.GetResidual (little), kReach, "小指側は動かない");
            Assert.Greater (Quaternion.Angle (rotation, Bone (t, hand).rotation), 5f, "手が 2 点の軸まわりに回って手首が上がる");
            foreach (Transform each in t.rig.editingBones) Assert.IsFalse (float.IsNaN (each.position.x), each.name);
        }

        [Test]
        public void ContactPointAlone_PullsArm ()
        {
            // 手のひらの点だけを動かす: 点が届くように腕が付いてくる。骨の上で固定した点が 1 つだけなので、手の向きは決めない（S26）
            // （手のひらの点は指の骨のあるキャラにだけできる）
            Setup s = Create (fingers: true);
            BodyPointId palm = BodyPointId.PalmIndex (HumanBodyBones.RightHand);
            Dictionary<Transform, float> lengths = Lengths (s);
            PinDirection (s, palm, -ArmDirection (s, true) * 0.1f + s.forward * 0.15f - s.up * 0.1f);
            Solve (s);
            Assert.LessOrEqual (s.body.GetResidual (palm), kReach, "手のひらが点に届く");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void ContactPoints_BurstMatchesManaged ()
        {
            // 足の点と手のひらの 2 点を使うので、つま先と小指までの指を持つキャラで
            Setup s = Create (toes: true, fingers: true, littleFingers: true);
            LockPoint (s, BodyPointId.Heel (HumanBodyBones.LeftFoot), Vector3.zero);
            LockPoint (s, BodyPointId.ToeTip (HumanBodyBones.LeftFoot), Vector3.zero);
            LockPoint (s, BodyPointId.ToeTip (HumanBodyBones.RightFoot), Vector3.zero);
            LockPoint (s, BodyPointId.PalmIndex (HumanBodyBones.LeftHand), Vector3.zero);
            LockPoint (s, BodyPointId.PalmLittle (HumanBodyBones.LeftHand), Vector3.zero);
            Pin (s, HumanBodyBones.LeftHand, s.up * 0.04f);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.08f + s.forward * 0.1f);
            AssertBurstMatchesManaged (s);
        }

        [Test]
        public void Solver_TwoPinsOnOneBone_FollowPriority ()
        {
            // 3 本の骨の鎖を上へ。先の骨の付け根と、前（z）に出した点の両方を固定し、付け根の目標を上へずらす（両方は満たせない）
            for (int first = 0; first < 2; first++) {
                FullBodyIkData data = FullBodyIkData.Create (3, 2, Allocator.Temp);
                try {
                    for (int i = 0; i < 3; i++) {
                        data.bones[i] = new FullBodyIkBone { parent = i - 1, stiffness = 0.3f };
                        data.positions[i] = new float3 (0, i * 0.5f, 0);
                        data.rotations[i] = quaternion.identity;
                    }
                    data.effectors[0] = new FullBodyIkEffector {
                        bone = 2, position = new float3 (0, 1.1f, 0), rotation = quaternion.identity, positionWeight = 1, priority = first == 0 ? 1 : 0,
                    };
                    data.effectors[1] = new FullBodyIkEffector {
                        bone = 2, offset = new float3 (0, 0, 0.3f), position = new float3 (0, 1, 0.3f), rotation = quaternion.identity, positionWeight = 1, priority = first == 1 ? 1 : 0,
                    };
                    data.effectorCount = 2;
                    data.iterations = 40;
                    data.rootPin = 0.2f;
                    FullBodyIk.Solve (ref data);
                    Assert.LessOrEqual (data.residuals[first], kReach, "優先する点（" + first + "）は目標に届く");
                    Assert.Greater (data.residuals[1 - first], 0.005f, "もう 1 つは届く所まで");
                    float3 onBone = data.positions[2] + math.rotate (data.rotations[2], new float3 (0, 0, 0.3f));
                    Assert.AreEqual (0.3f, math.distance (onBone, data.positions[2]), 1e-4f);
                    // もう 1 つの点は、優先する点から見て目標の方にある
                    float3 pivot = first == 0 ? data.positions[2] : onBone;
                    float3 other = first == 0 ? onBone : data.positions[2];
                    float3 wanted = first == 0 ? new float3 (0, 1, 0.3f) : new float3 (0, 1.1f, 0);
                    Assert.LessOrEqual (math.distance (math.normalize (other - pivot), math.normalize (wanted - pivot)), 2e-3f, "届かない点は、優先する点を支点に目標の方へ向く");
                }
                finally {
                    data.Dispose ();
                }
            }
        }

        // ---- Burst ----

        [Test]
        public void Burst_IsUsed_AndMatchesManaged ()
        {
            Setup s = Create ();
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero, true);
            Pin (s, HumanBodyBones.RightHand, -ArmDirection (s, true) * 0.15f + s.forward * 0.2f - s.up * 0.15f);
            AssertBurstMatchesManaged (s, () => {
                Assert.IsFalse (s.body.lastSolveUsedBurst, "マネージドで動く");
                Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "マネージドでも届く");
            });
        }

        [Test]
        public void Solver_RunsWithoutTransforms ()
        {
            // 3 本の骨の鎖（根 → 中 → 先）。先の骨を引く。Transform を使わずに、配列だけで解ける
            FullBodyIkData data = FullBodyIkData.Create (3, 1, Allocator.Temp);
            try {
                data.bones[0] = new FullBodyIkBone { parent = -1, stiffness = 0.9f };
                data.bones[1] = new FullBodyIkBone { parent = 0, stiffness = 0.2f };
                data.bones[2] = new FullBodyIkBone { parent = 1, stiffness = 0.2f };
                data.positions[0] = new float3 (0, 1, 0);
                data.positions[1] = new float3 (0.3f, 1, 0);
                data.positions[2] = new float3 (0.6f, 1, 0);
                for (int i = 0; i < 3; i++) data.rotations[i] = quaternion.identity;
                data.effectors[0] = new FullBodyIkEffector { bone = 2, position = new float3 (0.45f, 1.2f, 0.1f), rotation = quaternion.identity, positionWeight = 1 };
                data.effectorCount = 1;
                data.iterations = 40;
                data.rootPin = 1;
                FullBodyIk.Solve (ref data);
                Assert.LessOrEqual (data.residuals[0], kReach, "届く");
                Assert.AreEqual (0.3f, math.distance (data.positions[0], data.positions[1]), 1e-4f, "骨の長さ");
                Assert.AreEqual (0.3f, math.distance (data.positions[1], data.positions[2]), 1e-4f, "骨の長さ");
                Assert.LessOrEqual (math.distance (data.positions[0], new float3 (0, 1, 0)), 1e-4f, "強さ 1 で留めた根は動かない");
            }
            finally {
                data.Dispose ();
            }
        }

        [Test]
        public void Rig_CanBeCreatedAndDisposedRepeatedly ()
        {
            Setup s = Create ();
            for (int i = 0; i < 20; i++) {
                FullBodyRig body = new FullBodyRig (s.rig, definition_);
                Assert.Greater (body.bodyCount, 0);
                body.Dispose ();
                body.Dispose ();
            }
        }
    }

}
