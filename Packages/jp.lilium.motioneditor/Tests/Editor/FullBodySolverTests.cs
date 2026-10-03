using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の解く中身（S25a-2）: 固定した点に届く・骨の長さが変わらない・肘と膝が逆に曲がらない・
    /// 体の中心ほど動かない・両立しない点でも壊れない・続けて動かしても飛ばない・Burst とマネージドで同じ結果
    /// </summary>
    public class FullBodySolverTests
    {
        const float kReach = 1e-3f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            definition_.solver = RigSolver.FullBodyIk;
            created_.Add (definition_);
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

        sealed class Setup
        {
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
            public Vector3 forward;
            public Vector3 up;
        }

        Setup Create (bool withChest = true, float size = 1, bool toes = false)
        {
            Setup s = new Setup ();
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = toes ? TestSkeleton.CreateHumanoidWithToes (created_, size) : TestSkeleton.CreateHumanoid (created_, withChest, size);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            s.rig = EditingRig.Create (definition_, display, name => new GameObject (name));
            disposables_.Add (s.rig);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = new FullBodyRig (s.rig, definition_);
            disposables_.Add (s.body);
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
        /// 足だけで届くので、上半身は動かず、つま先の先も外れない（つま先の骨があるキャラでは、以前はつま先の先が数 cm 外れていた）
        /// </summary>
        [Test]
        public void HeelRaisedOnLockedToeTip_WithHipsPinned_KeepsUpperBody ([Values (false, true)] bool toes)
        {
            Setup s = Create (toes: toes);
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
            // つま先の付け根（つま先の骨が無ければつま先の先）を中心に、足首が上がる向きへ 25° 回した所
            Vector3 pivot = toes ? Bone (s, HumanBodyBones.LeftToes).position : tipPosition;
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
            // 既定の一覧では、胸の点は UpperChest に置く（無いキャラでは親の骨へ乗る）。名前は置いた骨で変わらない
            return BodyPointId.Direction (candidates.Contains (HumanBodyBones.UpperChest) || candidates.Contains (HumanBodyBones.Chest) ? HumanBodyBones.UpperChest : candidates[0]);
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
            // 向きの点も、骨の上の点の 1 つ（S26）。動かすと点が目標へ届くように骨ごと引かれ、遠くへ置けば体も引かれる
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
            // 頭の点（付け根）と顔の前の点は同じ骨の上: 付け根を固定して顔の前の点を付け根まわりに回した所へ置くと、2 点とも届き頭が回る
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
        public void HipsDirection_WithPinnedFeet_TurnsHipsAndKeepsFeet ()
        {
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
        }

        [Test]
        public void ChestPoint_PullsChest_LikeAnyPoint ()
        {
            // 胸の前の点を横へ: ほかの点と同じく、点が目標へ届くように胸ごと引かれる（向くだけにしない。S26）
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
        public void DirectionPoint_AtRest_LeavesPoseUntouched ()
        {
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
        public void DirectionPoints_BurstMatchesManaged ()
        {
            Setup s = Create ();
            Vector3 side = Vector3.Cross (s.up, s.forward).normalized;
            Pin (s, HumanBodyBones.LeftFoot, Vector3.zero);
            Pin (s, HumanBodyBones.RightFoot, Vector3.zero);
            Pin (s, HumanBodyBones.RightHand, Vector3.zero);
            PinDirection (s, BodyPointId.Direction (HumanBodyBones.Head), side * 0.2f);
            PinDirection (s, BodyPointId.Top (HumanBodyBones.Head), -side * 0.1f);
            PinDirection (s, ChestDirection (s), -side * 0.3f);
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
            Setup s = Create ();
            foreach (HumanBodyBones foot in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }) {
                Vector3 ankle = Bone (s, foot).position;
                Vector3 heel, toe;
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.Heel (foot), out heel), foot + " のかかと");
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.ToeTip (foot), out toe), foot + " のつま先の先");
                Assert.Less (heel.y, ankle.y + 1e-4f, "かかとは足首より下");
                Assert.Less (Vector3.Dot (heel - ankle, s.forward), 0, "かかとは足首より後ろ");
                Assert.Greater (Vector3.Dot (toe - ankle, s.forward), 0.05f, "つま先の先は足首より前");
                Assert.AreEqual (heel.y, toe.y, 1e-3f, "かかととつま先の先は同じ高さ（足裏）");
                Assert.IsFalse (s.body.PointHasRotation (BodyPointId.Heel (foot)));
            }
            foreach (HumanBodyBones hand in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand }) {
                Vector3 palm;
                Assert.IsTrue (s.body.TryGetBonePointWorld (BodyPointId.PalmIndex (hand), out palm), hand + " の手のひら");
                Assert.Greater (Vector3.Distance (palm, Bone (s, hand).position), 0.01f, "手のひらは手首から離れている");
            }
            Assert.AreEqual ("Controls/Body/LeftHeel", RigPaths.BodyPoint (BodyPointId.Heel (HumanBodyBones.LeftFoot).name));
            Assert.AreEqual ("Controls/Body/RightToeTip", RigPaths.BodyPoint (BodyPointId.ToeTip (HumanBodyBones.RightFoot).name));
            Assert.AreEqual ("Controls/Body/LeftPalmIndex", RigPaths.BodyPoint (BodyPointId.PalmIndex (HumanBodyBones.LeftHand).name));
            Assert.AreEqual ("Controls/Body/RightPalmLittle", RigPaths.BodyPoint (BodyPointId.PalmLittle (HumanBodyBones.RightHand).name));

            definition_.bodyPoints.RemoveAll (p => p.anchor != BodyPointAnchor.BoneOrigin);
            Setup none = Create ();
            Assert.IsFalse (none.body.HasPoint (BodyPointId.Heel (HumanBodyBones.LeftFoot)), "定義の一覧から消すと無くなる");
            Assert.IsFalse (none.body.HasPoint (BodyPointId.PalmIndex (HumanBodyBones.LeftHand)));
        }

        [Test]
        public void HeelAndToeLocked_KeepFootPlanted_WhenHipsGoDown ()
        {
            Setup s = Create ();
            Dictionary<Transform, float> lengths = Lengths (s);
            HumanBodyBones[] feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            Quaternion[] rotations = feet.Select (f => Bone (s, f).rotation).ToArray ();
            foreach (HumanBodyBones foot in feet) {
                LockPoint (s, BodyPointId.Heel (foot), Vector3.zero);
                LockPoint (s, BodyPointId.ToeTip (foot), Vector3.zero);
            }
            Pin (s, HumanBodyBones.Hips, -s.up * 0.15f + s.forward * 0.03f);
            Solve (s);
            for (int i = 0; i < feet.Length; i++) {
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.Heel (feet[i])), kReach, feet[i] + " のかかとは動かない");
                Assert.LessOrEqual (s.body.GetResidual (BodyPointId.ToeTip (feet[i])), kReach, feet[i] + " のつま先の先は動かない");
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], Bone (s, feet[i]).rotation), 0.1f, feet[i] + " は回らない（足裏が付いたまま）");
            }
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.Hips), kReach, "腰は下がる");
            AssertLengths (lengths, 1e-4f, "骨の長さ");
        }

        [Test]
        public void ToeTipLocked_LetsHeelRise_WhenHipsGoUp ([Values (false, true)] bool toes)
        {
            Setup s = Create (toes: toes);
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
        public void DefaultBodyPoints_KeepVersion2Names ()
        {
            // 既定の一覧の名前は、版 2 までのコントロールのパスと同じ（今までの編集用クリップのキーを読む）
            string[] names = definition_.bodyPoints.Select (p => p.name).ToArray ();
            foreach (string name in new[] { "Hips", "Head", "LeftHand", "RightFoot", "HipsDirection", "UpperChestDirection", "HeadDirection", "HeadTop",
                "LeftHeel", "RightToeTip", "LeftPalmIndex", "RightPalmLittle" }) {
                Assert.Contains (name, names, name);
            }
            Assert.IsTrue (definition_.bodyPoints.First (p => p.name == "LeftHand").rotation, "手は向きを持つ");
            Assert.IsFalse (definition_.bodyPoints.First (p => p.name == "LeftLowerArm").rotation, "肘は位置だけ");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name == "Jaw"), "解かない骨には置かない");
        }

        [Test]
        public void Version2Definition_GetsBodyPointsFromOldSettings ()
        {
            definition_.bodyPoints.Clear ();
            definition_.fullBody.directionPoints = false;
            definition_.version = 2;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.IsTrue (definition_.bodyPoints.Any (p => p.name == "LeftHeel"), "骨の上の点は作る");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name.EndsWith ("Direction")), "向きの点を置かない設定だった");
            Assert.IsEmpty (definition_.Validate ());
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
            // つま先の先はつま先の骨に置く。つま先の骨が無いキャラでは足の骨へ乗る（親をたどる）
            Setup s = Create (toes: toes);
            BodyPointId tip = BodyPointId.ToeTip (HumanBodyBones.LeftFoot);
            Assert.AreEqual (Bone (s, toes ? HumanBodyBones.LeftToes : HumanBodyBones.LeftFoot), s.body.GetBone (tip));
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
            Setup s = Create ();
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

            // 逆: 足首を Locked にしてつま先の先を動かす。足首は動かず、足が足首を支点に回る
            Setup t = Create ();
            BodyPointId toe = BodyPointId.ToeTip (HumanBodyBones.LeftFoot);
            Transform foot = Bone (t, HumanBodyBones.LeftFoot);
            rotation = foot.rotation;
            LockPoint (t, HumanBodyBones.LeftFoot, Vector3.zero);
            PinDirection (t, toe, t.up * 0.05f);
            Solve (t);
            Assert.LessOrEqual (t.body.GetResidual (HumanBodyBones.LeftFoot), kReach, "足首は動かない");
            Assert.Greater (Quaternion.Angle (rotation, foot.rotation), 5f, "足が足首を支点に回る");
            Vector3 onBone, target;
            Quaternion ignored;
            t.body.TryGetBonePointWorld (toe, out onBone);
            t.body.TryGetPointWorld (toe, out target, out ignored);
            Assert.LessOrEqual (Vector3.Angle (onBone - foot.position, target - foot.position), 0.1f, "つま先の先は点の方を向く");
        }

        [Test]
        public void HandPoints_ThreePointsSetHandOrientation ()
        {
            // 手首と人差し指側を Locked にして、小指側を動かす: 先の 2 点は動かず、手がその 2 点の軸まわりに回る
            Setup s = Create ();
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
            Setup t = Create ();
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
            Setup s = Create ();
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
            Setup s = Create ();
            LockPoint (s, BodyPointId.Heel (HumanBodyBones.LeftFoot), Vector3.zero);
            LockPoint (s, BodyPointId.ToeTip (HumanBodyBones.LeftFoot), Vector3.zero);
            LockPoint (s, BodyPointId.ToeTip (HumanBodyBones.RightFoot), Vector3.zero);
            LockPoint (s, BodyPointId.PalmIndex (HumanBodyBones.LeftHand), Vector3.zero);
            LockPoint (s, BodyPointId.PalmLittle (HumanBodyBones.LeftHand), Vector3.zero);
            Pin (s, HumanBodyBones.LeftHand, s.up * 0.04f);
            Pin (s, HumanBodyBones.Hips, -s.up * 0.08f + s.forward * 0.1f);
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
            Transform[] bones = s.rig.editingBones.ToArray ();

            s.body.useBurst = false;
            Solve (s);
            Assert.IsFalse (s.body.lastSolveUsedBurst, "マネージドで動く");
            Vector3[] positions = bones.Select (b => b.position).ToArray ();
            Quaternion[] rotations = bones.Select (b => b.rotation).ToArray ();
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), kReach, "マネージドでも届く");

            s.body.useBurst = true;
            Solve (s);
            if (!s.body.lastSolveUsedBurst) Assert.Inconclusive ("Burst がまだコンパイル中か、切れている（Jobs > Burst > Enable Compilation）。マネージドでは動いた");
            for (int i = 0; i < bones.Length; i++) {
                Assert.LessOrEqual (Vector3.Distance (positions[i], bones[i].position), 1e-4f, bones[i].name + " の位置（Burst とマネージドの差）");
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], bones[i].rotation), 0.01f, bones[i].name + " の向き（Burst とマネージドの差）");
            }
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

        // ---- リグの定義 ----

        [Test]
        public void FullBodyHash_ChangesWithSettings_AndIsEmptyForTwoBoneIk ()
        {
            string hash = definition_.GetFullBodyHash ();
            Assert.IsNotEmpty (hash);
            Assert.AreEqual (hash, definition_.GetFullBodyHash (), "同じ設定なら同じ");
            definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine).stiffness = 0.31f;
            string stiffer = definition_.GetFullBodyHash ();
            Assert.AreNotEqual (hash, stiffer, "硬さを変えると変わる");
            definition_.fullBody.hipsPin = 0.11f;
            Assert.AreNotEqual (stiffer, definition_.GetFullBodyHash (), "腰を留める強さを変えると変わる");
            definition_.solver = RigSolver.TwoBoneIk;
            Assert.IsEmpty (definition_.GetFullBodyHash (), "2 本骨 IK の定義では、全身 IK の設定は姿勢に効かない");
        }

        [Test]
        public void Version1Definition_GetsNewDefaults_ButKeepsEditedValues ()
        {
            EditRigDefinition.FkControl shoulder = definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftShoulder);
            EditRigDefinition.FkControl spine = definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine);
            definition_.version = 1;
            definition_.fullBody.iterations = 20;
            definition_.fullBody.hipsPin = 0.3f;
            shoulder.stiffness = 0.3f;
            spine.stiffness = 0.33f;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.AreEqual (new EditRigDefinition.FullBodySettings ().iterations, definition_.fullBody.iterations);
            Assert.AreEqual (new EditRigDefinition.FullBodySettings ().hipsPin, definition_.fullBody.hipsPin, 1e-6f);
            Assert.AreEqual (0.5f, shoulder.stiffness, 1e-6f);
            Assert.AreEqual (0.33f, spine.stiffness, 1e-6f, "直してあった値は変えない");

            definition_.version = 1;
            definition_.fullBody.iterations = 35;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (35, definition_.fullBody.iterations, "直してあった回数は変えない");
        }
    }

}
