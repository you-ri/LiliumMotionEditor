using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の正解の動き（Docs/Specs/full-body-ik-cases.md）。単純なリグの型ごとに、点を引いたときの正解を確かめる。
    /// 骨はどれも 0.3m、角度・位置の制限なし（R8 の軸の骨を除く）。持ち物は入れない。
    /// 窓と同じく、毎回キーの姿勢（ここでは基準姿勢）から解き直し、マウスを少しずつ動かす。
    /// 1 つのテストで見ることは全部見てから、外れたものをまとめて出す（どれが合っていないかを洗い出すため）
    /// </summary>
    public class FullBodyCaseTests : RigTestFixture
    {
        const float kBone = 0.3f;
        /// <summary>届いた（つかんだ点と目標の差）</summary>
        const float kReach = 5e-3f;
        /// <summary>留まった（Pin の点が元の場所から動いた量。強さ 1 の Pin でも詰めきれない数 mm は残る。FullBodySolverTests と同じ）</summary>
        const float kStay = 5e-3f;
        /// <summary>引かれた先が、最小だけ引いた所からずれてよい量</summary>
        const float kPlace = 0.01f;
        /// <summary>Lock の点が動いてよい量（FullBodyRig.LocksHeld と同じ）</summary>
        const float kLock = 1e-3f;
        /// <summary>FK のまま（親に対する向き）の骨が回ってよい角度</summary>
        const float kKeepAngle = 0.5f;
        /// <summary>向けるだけの点が、目標の向きからずれてよい角度</summary>
        const float kAimAngle = 2;
        const int kSteps = 20;
        /// <summary>離して解き直したとき（I1）に、骨が動いてよい量・回ってよい角度</summary>
        const float kReleasePosition = 1e-3f;
        const float kReleaseAngle = 0.5f;

        sealed class Setup
        {
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
            public readonly List<string> failures = new List<string> ();
        }

        struct BoneSpec
        {
            public string name, parent;
            public Vector3 value;
        }

        /// <summary>根の骨。value はワールドの場所</summary>
        static BoneSpec Top (string name, Vector3 position)
        {
            return new BoneSpec { name = name, value = position };
        }

        /// <summary>parent の付け根から direction の向きに 0.3m の所に付け根を置く骨</summary>
        static BoneSpec Child (string name, string parent, Vector3 direction)
        {
            return new BoneSpec { name = name, parent = parent, value = direction };
        }

        /// <summary>
        /// 骨を組み、骨ごとに付け根の点（重さ 1）を置いた定義で開く（EditRigDefinition.SetFromBones）。
        /// pinnedRoot なら根を留める定義（腰の Pin 0.6）、でなければ留めない定義（0。S98）
        /// </summary>
        Setup Create (bool pinnedRoot, BoneSpec[] bones, System.Action<EditRigDefinition> edit = null)
        {
            GameObject go = new GameObject ("Case");
            created_.Add (go);
            Dictionary<string, Transform> made = new Dictionary<string, Transform> ();
            foreach (BoneSpec spec in bones) {
                Transform bone = new GameObject (spec.name).transform;
                Transform parent = spec.parent == null ? go.transform : made[spec.parent];
                bone.SetParent (parent, false);
                bone.position = spec.parent == null ? spec.value : parent.position + spec.value.normalized * kBone;
                made[spec.name] = bone;
            }
            Animator animator = go.AddComponent<Animator> ();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            EditRigDefinition definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            created_.Add (definition);
            definition.SetFromBones (animator);
            definition.fullBody.hipsPin = pinnedRoot ? 0.6f : 0;
            if (edit != null) edit (definition);
            Assert.IsEmpty (definition.Validate ());

            Setup s = new Setup ();
            s.rig = CreateEditingRig (definition, animator);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition));
            Solve (s);
            return s;
        }

        /// <summary>骨 control の付け根から offset（ワールドの向き・メートル。体の大きさは 1）の所に点を足す</summary>
        static void AddPoint (EditRigDefinition definition, string name, string control, Vector3 offset, int weight)
        {
            definition.bodyPoints.Add (new EditRigDefinition.BodyPoint {
                name = name, control = control, origin = HumanBodyBones.LastBone, toward = HumanBodyBones.LastBone, offset = offset, weight = weight,
            });
        }

        static void SetWeight (EditRigDefinition definition, string name, int weight)
        {
            definition.bodyPoints.First (p => p.name == name).weight = weight;
        }

        static BodyPointId Id (string name)
        {
            return new BodyPointId (name);
        }

        static Transform Bone (Setup s, string name)
        {
            return s.rig.editingBones.First (t => t.name == name);
        }

        /// <summary>点の、骨の今の場所</summary>
        static Vector3 At (Setup s, string name)
        {
            Assert.IsTrue (s.body.TryGetBonePointWorld (Id (name), out Vector3 position), name);
            return position;
        }

        /// <summary>今の骨の場所で位置を Pin する</summary>
        static void Pin (Setup s, string name)
        {
            s.body.CapturePoint (Id (name));
            s.body.SetPositionWeight (Id (name), 1);
        }

        static void Lock (Setup s, string name)
        {
            Pin (s, name);
            s.body.SetLocked (Id (name), true);
        }

        /// <summary>窓と同じく、キーの姿勢（基準姿勢）へ戻して、編集用リグ → 全身 IK の順に解く</summary>
        static void Solve (Setup s)
        {
            s.body.Unsolve ();
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            s.body.CaptureInput ();
            s.body.Solve ();
        }

        /// <summary>
        /// 点 name をつかみ、target まで kSteps 回に分けて動かす（窓でつかむのと同じ: BodyPointTarget.PinPosition）。
        /// 各回の後に step を呼ぶ。どの骨も、1 回でマウスの動きの 4 倍 + 5mm より大きくは跳ばない（I2）ことを確かめる
        /// </summary>
        static void Drag (Setup s, string name, Vector3 target, System.Action<int> step = null)
        {
            BodyPointId id = Id (name);
            Solve (s);
            Vector3 start = At (s, name);
            if (s.body.GetPositionWeight (id) <= 0) s.body.CapturePoint (id);
            s.body.MarkGrabbed (id);
            Transform[] bones = s.rig.editingBones.ToArray ();
            Vector3[] last = bones.Select (b => b.position).ToArray ();
            float allowed = 4 * Vector3.Distance (start, target) / kSteps + 5e-3f;
            float worst = 0;
            string worstAt = null;
            for (int k = 1; k <= kSteps; k++) {
                s.body.SetPointPositionWorld (id, Vector3.Lerp (start, target, (float)k / kSteps));
                s.body.SetPositionWeight (id, 1);
                Solve (s);
                for (int i = 0; i < bones.Length; i++) {
                    float moved = Vector3.Distance (last[i], bones[i].position);
                    if (moved > worst) {
                        worst = moved;
                        worstAt = bones[i].name + " の " + k + " 回目";
                    }
                    last[i] = bones[i].position;
                }
                if (step != null) step (k);
            }
            Check (s, worst <= allowed, "I2 跳ばない: " + worstAt + " で " + Mm (worst) + "（許す " + Mm (allowed) + "）");
        }

        static string Mm (float meters)
        {
            return (meters * 1000).ToString ("F1") + "mm";
        }

        static void Check (Setup s, bool ok, string message)
        {
            if (!ok) s.failures.Add (message);
        }

        static void Reached (Setup s, string name, Vector3 target)
        {
            float gap = Vector3.Distance (At (s, name), target);
            Check (s, gap <= kReach, name + " は届く: 差 " + Mm (gap));
        }

        static void Stayed (Setup s, string name, Vector3 rest, float tolerance = kStay)
        {
            float moved = Vector3.Distance (At (s, name), rest);
            Check (s, moved <= tolerance, name + " は留まる: " + Mm (moved) + " 動いた");
        }

        /// <summary>name は、center から distance 離れた所のうち、元の場所 rest に一番近い所（最小だけ引かれた所）にある</summary>
        static void PulledLeast (Setup s, string name, Vector3 rest, Vector3 center, float distance)
        {
            Vector3 expected = center + (rest - center).normalized * distance;
            float off = Vector3.Distance (At (s, name), expected);
            float moved = Vector3.Distance (At (s, name), rest);
            Check (s, off <= kPlace, name + " は必要な分だけ引かれる: 最小の所から " + Mm (off) + " ずれ（" + Mm (moved) + " 動いた。最小は " + Mm (Vector3.Distance (expected, rest)) + "）");
        }

        /// <summary>骨の親に対する向き（localRotation）が変わらない（FK のまま）</summary>
        static void KeptLocal (Setup s, string name, Quaternion rest)
        {
            float angle = Quaternion.Angle (rest, Bone (s, name).localRotation);
            Check (s, angle <= kKeepAngle, "骨 " + name + " は親に対する向きのまま: " + angle.ToString ("F1") + "° 回った");
        }

        static void Aimed (Setup s, string name, Vector3 pivot, Vector3 target)
        {
            float angle = Vector3.Angle (At (s, name) - pivot, target - pivot);
            Check (s, angle <= kAimAngle, name + " は目標へ向く: " + angle.ToString ("F1") + "° ずれ");
        }

        /// <summary>I1 を確かめてから、外れたものをまとめて出す</summary>
        static void Report (Setup s)
        {
            Released (s);
            s.body.EndGrab ();
            if (s.failures.Count > 0) Assert.Fail (string.Join ("\n", s.failures));
        }

        /// <summary>
        /// I1: 離して、この姿勢をキーにして解き直しても、姿勢が変わらない。窓で離したときと同じ手順（PoseCommit.WriteFullBodyPose）:
        /// 引かれた Pin・Lock の点を骨の今の場所へ付け直し（SyncPointToBone）、つかむのをやめ、今の姿勢を元の姿勢（キー）にする
        /// </summary>
        static void Released (Setup s)
        {
            Transform[] bones = s.rig.editingBones.ToArray ();
            Vector3[] positions = bones.Select (b => b.position).ToArray ();
            Quaternion[] rotations = bones.Select (b => b.rotation).ToArray ();
            foreach (BodyPointId id in s.body.pointIds.ToArray ()) s.body.SyncPointToBone (id, out bool position, out bool rotation);
            s.body.EndGrab ();
            s.solver.Capture ();
            s.body.CaptureInput ();
            Solve (s);
            float worstPosition = 0, worstAngle = 0;
            string worstAt = null;
            for (int i = 0; i < bones.Length; i++) {
                float moved = Vector3.Distance (positions[i], bones[i].position);
                float turned = Quaternion.Angle (rotations[i], bones[i].rotation);
                if (moved > worstPosition || turned > worstAngle) worstAt = bones[i].name;
                worstPosition = Mathf.Max (worstPosition, moved);
                worstAngle = Mathf.Max (worstAngle, turned);
            }
            Check (s, worstPosition <= kReleasePosition && worstAngle <= kReleaseAngle,
                "I1 離しても姿勢が変わらない: " + worstAt + " などが " + Mm (worstPosition) + "・" + worstAngle.ToString ("F2") + "° 変わった");
        }

        /// <summary>
        /// I3: 点の間が伸びきったら、さらに引いても伸びきったまま（引いた距離に応じて、キーの曲がりへ戻さない）。
        /// from と to の点の離れが length に達した後の回で、length − 5mm より縮まないか
        /// </summary>
        static System.Action<int> StaysStretched (Setup s, string from, string to, float length)
        {
            bool stretched = false;
            return k => {
                float span = Vector3.Distance (At (s, from), At (s, to));
                if (stretched) Check (s, span >= length - 5e-3f, "I3 伸びきったまま: " + k + " 回目に " + from + "─" + to + " が " + Mm (span) + " に縮んだ");
                else stretched = span >= length - 5e-3f;
            };
        }

        // ---- R1 一本の鎖 R─A─B─C ----

        static BoneSpec[] Chain ()
        {
            // 関節ごとに 50° ずつ下へ曲がる鎖（まっすぐだと曲がる向きが決まらず、伸びきる前の余りも無い）
            return new[] {
                Top ("R", new Vector3 (0, 1, 0)),
                Child ("A", "R", new Vector3 (0, 0, 1)),
                Child ("B", "A", new Vector3 (0, -0.766f, 0.643f)),
                Child ("C", "B", new Vector3 (0, -0.985f, -0.174f)),
            };
        }

        [Test]
        public void Case1_1_PullTip_ChainBendsAndTipReaches ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 target = At (s, "C") + new Vector3 (0, 0.1f, -0.1f);
            Drag (s, "C", target);
            Reached (s, "C", target);
            Report (s);
        }

        [Test]
        public void Case1_2_PinnedMiddle_StaysWhileTipReaches ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 a = At (s, "A");
            Pin (s, "A");
            Vector3 target = At (s, "C") + new Vector3 (0, 0.1f, -0.05f);
            Drag (s, "C", target);
            Reached (s, "C", target);
            Stayed (s, "A", a);
            Report (s);
        }

        [Test]
        public void Case1_3_PinnedMiddle_IsPulledOnlyOnceStretched ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 a = At (s, "A");
            Pin (s, "A");
            Vector3 target = At (s, "C") + new Vector3 (0, 0, 0.5f);
            System.Action<int> stretched = StaysStretched (s, "A", "C", 2 * kBone);
            Drag (s, "C", target, k => {
                stretched (k);
                float moved = Vector3.Distance (At (s, "A"), a);
                float span = Vector3.Distance (At (s, "A"), At (s, "C"));
                Check (s, moved <= kStay || span >= 2 * kBone - 5e-3f, "A は伸びきるまで動かない: " + k + " 回目に " + Mm (moved) + " 動いた（A─C " + Mm (span) + "）");
            });
            Reached (s, "C", target);
            PulledLeast (s, "A", a, target, 2 * kBone);
            Report (s);
        }

        [Test]
        public void Case1_4_PinnedNeighbour_IsPulledOnlyAsNeeded ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 b = At (s, "B");
            Pin (s, "B");
            Vector3 target = At (s, "C") + new Vector3 (0, 0.15f, 0.1f);
            Drag (s, "C", target);
            Reached (s, "C", target);
            PulledLeast (s, "B", b, target, kBone);
            Report (s);
        }

        [Test]
        public void Case1_5_LockedNeighbour_StaysAndTipGoesAsCloseAsItCan ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 b = At (s, "B");
            Lock (s, "B");
            Vector3 target = At (s, "C") + new Vector3 (0, 0.15f, 0.1f);
            Drag (s, "C", target);
            Stayed (s, "B", b, kLock);
            Vector3 expected = b + (target - b).normalized * kBone;
            float off = Vector3.Distance (At (s, "C"), expected);
            Check (s, off <= kPlace, "C は B から 0.3m の球の上で目標に一番近い所: " + Mm (off) + " ずれ");
            Report (s);
        }

        [Test]
        public void Case1_6_PinnedTip_IsPulledOnlyAsNeeded ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Vector3 c = At (s, "C");
            Pin (s, "C");
            Vector3 target = At (s, "B") + new Vector3 (0, 0.1f, 0.05f);
            Drag (s, "B", target);
            Reached (s, "B", target);
            PulledLeast (s, "C", c, target, kBone);
            Report (s);
        }

        [Test]
        public void Case1_7_FreeChildren_KeepTheirLocalRotation ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Chain ());
            Quaternion b = Bone (s, "B").localRotation, c = Bone (s, "C").localRotation;
            Vector3 target = At (s, "B") + new Vector3 (0, 0.1f, 0.05f);
            Drag (s, "B", target);
            Reached (s, "B", target);
            KeptLocal (s, "B", b);
            KeptLocal (s, "C", c);
            Report (s);
        }

        [Test]
        public void Case1_8_UnpinnedRoot_ChainFollowsTheTip ([Values ("C", "R")] string grabbed)
        {
            Setup s = Create (false, Chain ());
            Vector3 r = At (s, "R"), c = At (s, "C");
            Vector3 target = At (s, grabbed) + new Vector3 (0, 0, 1);
            Drag (s, grabbed, target, StaysStretched (s, "R", "C", 3 * kBone));
            Reached (s, grabbed, target);
            string other = grabbed == "C" ? "R" : "C";
            float followed = Vector3.Distance (At (s, other), grabbed == "C" ? r : c);
            Check (s, followed >= 0.3f, other + " は付いてくる: " + Mm (followed) + " 動いた");
            Report (s);
        }

        // ---- R2 蝶番のある手足 S─E(蝶番)─W ----

        static BoneSpec[] Limb ()
        {
            return new[] {
                Top ("S", new Vector3 (0, 1.5f, 0)),
                Child ("E", "S", new Vector3 (0, -1, 0.15f)),
                Child ("W", "E", new Vector3 (0, -1, -0.15f)),
            };
        }

        static void Hinge (EditRigDefinition definition)
        {
            EditRigDefinition.ExtraControl elbow = definition.extraControls.First (c => c.name == "E");
            elbow.hinge = true;
            elbow.bendDirection = new Vector3 (0, 0, 1);
        }

        [Test]
        public void Case2_1_HingeLimb_BendsOnItsAxis ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Limb (), Hinge);
            Vector3 shoulder = At (s, "S");
            Transform elbow = Bone (s, "E");
            Quaternion elbowRest = elbow.localRotation;
            Vector3 axis = Quaternion.Inverse (elbow.rotation) * Vector3.Cross (At (s, "E") - At (s, "S"), At (s, "W") - At (s, "E")).normalized;
            Pin (s, "S");
            Vector3 target = At (s, "W") + new Vector3 (0.1f, 0.15f, 0.1f);
            Drag (s, "W", target);
            Reached (s, "W", target);
            Stayed (s, "S", shoulder);
            (Quaternion.Inverse (elbowRest) * elbow.localRotation).ToAngleAxis (out float angle, out Vector3 turned);
            Check (s, angle <= 1 || Mathf.Abs (Vector3.Dot (turned.normalized, axis)) >= 0.99f, "E は蝶番の軸まわりにだけ回る: 軸から " + Mathf.Acos (Mathf.Clamp01 (Mathf.Abs (Vector3.Dot (turned.normalized, axis)))) * Mathf.Rad2Deg + "°");
            Report (s);
        }

        [Test]
        public void Case2_2_Pole_TurnsTheLimbAboutItsAxis ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Limb (), d => {
                Hinge (d);
                // 人型の肘・膝の Pole と同じく、上の骨に乗せ、ずれの向きは下の骨と半分ずつ混ぜる（RigTestFixture.AddPoles）
                AddPoint (d, "Pole", "S", new Vector3 (0, -kBone, 0.25f), 0);
                EditRigDefinition.BodyPoint pole = d.bodyPoints.Last ();
                pole.blendControl = "E";
                pole.blend = kPoleBlend;
            });
            Vector3 shoulder = At (s, "S"), wrist = At (s, "W"), elbow = At (s, "E");
            Vector3 axis = (wrist - shoulder).normalized;
            float radius = Vector3.ProjectOnPlane (elbow - shoulder, axis).magnitude;
            Pin (s, "S");
            Pin (s, "W");
            Vector3 target = At (s, "Pole") + new Vector3 (0.2f, 0, 0);
            Drag (s, "Pole", target);
            Stayed (s, "S", shoulder);
            Stayed (s, "W", wrist);
            float turnedRadius = Vector3.ProjectOnPlane (At (s, "E") - shoulder, axis).magnitude;
            Check (s, Mathf.Abs (turnedRadius - radius) <= 2e-3f, "E は S─W の軸まわりに回るだけ: 軸からの離れが " + Mm (turnedRadius - radius) + " 変わった");
            Check (s, At (s, "E").x >= 0.02f, "E は Pole の方へ回る: x " + Mm (At (s, "E").x));
            Report (s);
        }

        [Test]
        public void Case2_3_PinnedWrist_IsPulledOnlyAsNeeded ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Limb (), Hinge);
            Vector3 wrist = At (s, "W");
            Pin (s, "W");
            Vector3 target = At (s, "E") + new Vector3 (0, 0.05f, 0.1f);
            Drag (s, "E", target);
            Reached (s, "E", target);
            PulledLeast (s, "W", wrist, target, kBone);
            Report (s);
        }

        // ---- R3 枝分かれ R─A─{B1─C1─D1, B2─C2─D2} ----

        static BoneSpec[] Branch ()
        {
            // 骨 A の先に 2 本の枝（人型の胸と両腕と同じ形）。枝の付け根 B1・B2 は骨 A に付いて動き、枝は骨 2 本で C で曲がる（伸びきる前の余りがある）
            return new[] {
                Top ("R", new Vector3 (0, 1, 0)),
                Child ("A", "R", new Vector3 (0, 1, 0)),
                Child ("B1", "A", new Vector3 (-1, 0, 0)),
                Child ("C1", "B1", new Vector3 (-0.2f, -1, 0)),
                Child ("D1", "C1", new Vector3 (0, -0.5f, 1)),
                Child ("B2", "A", new Vector3 (1, 0, 0)),
                Child ("C2", "B2", new Vector3 (0.2f, -1, 0)),
                Child ("D2", "C2", new Vector3 (0, -0.5f, 1)),
            };
        }

        [Test]
        public void Case3_1_PinnedOtherBranch_DoesNotMove ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Branch ());
            Vector3 b2 = At (s, "B2"), c2 = At (s, "C2"), d2 = At (s, "D2");
            Pin (s, "D2");
            Vector3 target = At (s, "D1") + new Vector3 (0, 0.1f, 0.1f);
            Drag (s, "D1", target);
            Reached (s, "D1", target);
            Stayed (s, "D2", d2);
            Stayed (s, "C2", c2);
            Stayed (s, "B2", b2);
            Report (s);
        }

        [Test]
        public void Case3_2_PinnedOtherBranch_FollowsOnlyOnceStretched ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Branch ());
            Vector3 d2 = At (s, "D2");
            Pin (s, "D2");
            Vector3 target = At (s, "D1") + new Vector3 (-0.8f, 0, 0);
            System.Action<int> stretched = StaysStretched (s, "B2", "D2", 2 * kBone);
            Drag (s, "D1", target, k => {
                stretched (k);
                float moved = Vector3.Distance (At (s, "D2"), d2);
                float span = Vector3.Distance (At (s, "B2"), At (s, "D2"));
                Check (s, moved <= kStay || span >= 2 * kBone - 5e-3f, "D2 は枝が伸びきるまで動かない: " + k + " 回目に " + Mm (moved) + " 動いた（B2─D2 " + Mm (span) + "）");
            });
            Reached (s, "D1", target);
            Report (s);
        }

        /// <summary>もう片方の枝が骨 1 本（初めから伸びきっている）。Pin の枝の先は、体がそのまわりに回れる間は留まり、その後は跳ばずに付いてくる</summary>
        [Test]
        public void Case3_4_PinnedOneBoneBranch_FollowsWithoutJumping ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, new[] {
                Top ("R", new Vector3 (0, 1, 0)),
                Child ("A", "R", new Vector3 (0, 1, 0)),
                Child ("B1", "A", new Vector3 (-1, 0, 0)),
                Child ("C1", "B1", new Vector3 (-0.2f, -1, 0)),
                Child ("B2", "A", new Vector3 (1, 0, 0)),
                Child ("C2", "B2", new Vector3 (0.2f, -1, 0)),
            });
            Quaternion a = Bone (s, "A").localRotation;
            Pin (s, "C2");
            Vector3 target = At (s, "C1") + new Vector3 (-0.8f, 0, 0);
            Drag (s, "C1", target);
            Reached (s, "C1", target);
            // 根を留めない定義では、点の間の道（C2─B2─A─B1─C1）の外にぶら下がる根の骨 R は、A に付いて回る（A の R に対する向きが変わらない。11-08 ユーザー決定）
            if (!pinnedRoot) KeptLocal (s, "A", a);
            Report (s);
        }

        [Test]
        public void Case3_3_FreeOtherBranch_KeepsItsLocalRotation ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Branch ());
            Quaternion b2 = Bone (s, "B2").localRotation, c2 = Bone (s, "C2").localRotation, d2 = Bone (s, "D2").localRotation;
            Vector3 target = At (s, "D1") + new Vector3 (0, 0.1f, 0.1f);
            Drag (s, "D1", target);
            Reached (s, "D1", target);
            KeptLocal (s, "B2", b2);
            KeptLocal (s, "C2", c2);
            KeptLocal (s, "D2", d2);
            Report (s);
        }

        // ---- R4・R5 骨の上の点 A─B─C、B の中ほどに点 P ----

        static BoneSpec[] Rod ()
        {
            return new[] {
                Top ("A", new Vector3 (0, 1, 0)),
                Child ("B", "A", new Vector3 (0, -0.27f, 1)),
                Child ("C", "B", new Vector3 (0, 0, 1)),
            };
        }

        static void MidPoint (EditRigDefinition definition)
        {
            AddPoint (definition, "P", "B", new Vector3 (0, 0, kBone / 2), 1);
        }

        [Test]
        public void Case4_1_PointOnBone_TurnsTheBoneWhileTheJointStays ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Rod (), MidPoint);
            Vector3 b = At (s, "B");
            Pin (s, "B");
            Vector3 target = b + Quaternion.Euler (0, 30, 0) * (At (s, "P") - b);
            Drag (s, "P", target);
            Reached (s, "P", target);
            Stayed (s, "B", b);
            Report (s);
        }

        [Test]
        public void Case4_1_PointOnBone_PullsTheJointOnlyAsNeeded ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Rod (), MidPoint);
            Vector3 b = At (s, "B");
            Pin (s, "B");
            Vector3 target = At (s, "P") + new Vector3 (0, 0.1f, 0.1f);
            Drag (s, "P", target);
            Reached (s, "P", target);
            PulledLeast (s, "B", b, target, kBone / 2);
            Report (s);
        }

        [Test]
        public void Case5_1_PinnedPointOnBone_StaysWhileTheJointTurnsAboutIt ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Rod (), MidPoint);
            Vector3 p = At (s, "P");
            Pin (s, "P");
            Vector3 target = p + Quaternion.Euler (0, 30, 0) * (At (s, "B") - p);
            Drag (s, "B", target);
            Reached (s, "B", target);
            Stayed (s, "P", p);
            Report (s);
        }

        [Test]
        public void Case5_1_PinnedPointOnBone_IsPulledOnlyAsNeeded ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, Rod (), MidPoint);
            Vector3 p = At (s, "P");
            Pin (s, "P");
            Vector3 target = At (s, "B") + new Vector3 (0, 0.1f, -0.05f);
            Drag (s, "B", target);
            Reached (s, "B", target);
            PulledLeast (s, "P", p, target, kBone / 2);
            Report (s);
        }

        // ---- R6 軽い点の鎖 Root─S─E─W（S・E は重さ 0、W は 1） ----

        static BoneSpec[] LightLimb ()
        {
            return new[] {
                Top ("Root", new Vector3 (0, 1.5f, 0)),
                Child ("S", "Root", new Vector3 (1, 0, 0)),
                Child ("E", "S", new Vector3 (0, -1, 0.1f)),
                Child ("W", "E", new Vector3 (0, -1, -0.1f)),
            };
        }

        static void Light (EditRigDefinition definition)
        {
            SetWeight (definition, "S", 0);
            SetWeight (definition, "E", 0);
        }

        [Test]
        public void Case6_1_LightPoint_LeavesFreeHeavyPoint ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, LightLimb (), Light);
            Vector3 w = At (s, "W");
            Vector3 target = At (s, "E") + new Vector3 (0, 0, 0.1f);
            Drag (s, "E", target);
            Stayed (s, "W", w);
            Report (s);
        }

        [Test]
        public void Case6_2_LightPoint_PullsPinnedLightPoint ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, LightLimb (), Light);
            Vector3 sh = At (s, "S"), w = At (s, "W");
            Pin (s, "S");
            // W から届く所（W は重いので留まる）・重い Root から届く所で、S を留めたままでは届かない所
            Vector3 target = w + Quaternion.Euler (0, 0, -25) * (At (s, "E") - w);
            Assert.Greater (Vector3.Distance (target, sh), kBone + 0.03f);
            Assert.Less (Vector3.Distance (target, At (s, "Root")), 2 * kBone - 0.03f);
            Drag (s, "E", target);
            Reached (s, "E", target);
            Stayed (s, "W", w);
            float moved = Vector3.Distance (At (s, "S"), sh);
            Check (s, moved >= 0.02f, "Pin の軽い S は引かれる: " + Mm (moved) + " 動いた");
            Report (s);
        }

        [Test]
        public void Case6_3_LightPoint_LeavesLockedHeavyPoint ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, LightLimb (), Light);
            Vector3 w = At (s, "W");
            Lock (s, "W");
            Vector3 target = At (s, "E") + new Vector3 (0.1f, 0, 0.1f);
            Drag (s, "E", target);
            Stayed (s, "W", w, kLock);
            Report (s);
        }

        // ---- R7 向けるだけの点 R─A─B、A に重さ 0 の点 D ----

        static BoneSpec[] AimChain ()
        {
            return new[] {
                Top ("R", new Vector3 (0, 1, 0)),
                Child ("A", "R", new Vector3 (0, 0, 1)),
                Child ("B", "A", new Vector3 (0, -0.27f, 1)),
            };
        }

        static void AimPoint (EditRigDefinition definition)
        {
            AddPoint (definition, "D", "A", new Vector3 (0, 0.1f, 0.15f), 0);
        }

        [Test]
        public void Case7_1And7_2_AimPoint_OnlyTurnsItsBone ([Values] bool pinnedRoot, [Values] bool pinned)
        {
            Setup s = Create (pinnedRoot, AimChain (), AimPoint);
            Vector3 r = At (s, "R"), a = At (s, "A");
            Quaternion rRotation = Bone (s, "R").rotation;
            if (pinned) Pin (s, "A");
            Vector3 target = At (s, "D") + new Vector3 (0.1f, 0, 0);
            Drag (s, "D", target);
            Aimed (s, "D", a, target);
            Stayed (s, "A", a);
            Stayed (s, "R", r);
            float turned = Quaternion.Angle (rRotation, Bone (s, "R").rotation);
            Check (s, turned <= kKeepAngle, "親の骨 R は回らない: " + turned.ToString ("F1") + "°");
            Report (s);
        }

        [Test]
        public void Case7_3_AimPoint_PullsPinnedAimPointOnTheSameBone ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, AimChain (), d => {
                AimPoint (d);
                AddPoint (d, "D2", "A", new Vector3 (0, -0.1f, 0.15f), 0);
            });
            Vector3 a = At (s, "A"), d2 = At (s, "D2");
            Pin (s, "D2");
            Vector3 target = At (s, "D") + new Vector3 (0.1f, 0, 0);
            Drag (s, "D", target);
            Aimed (s, "D", a, target);
            float moved = Vector3.Distance (At (s, "D2"), d2);
            Check (s, moved >= 0.01f, "Pin の D2 は引かれる: " + Mm (moved) + " 動いた");
            Report (s);
        }

        [Test]
        public void Case7_4_AimPoint_TurnsAboutPinnedPointOnTheBone ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, AimChain (), d => {
                AimPoint (d);
                AddPoint (d, "M", "A", new Vector3 (0, 0, 0.15f), 1);
            });
            Vector3 m = At (s, "M");
            Pin (s, "M");
            Vector3 target = At (s, "D") + new Vector3 (0.1f, 0, 0);
            Drag (s, "D", target);
            Stayed (s, "M", m);
            Aimed (s, "D", m, target);
            Report (s);
        }

        // ---- R8 軸まわりにしか回らない骨 R─P─K(軸)─A─B ----

        static BoneSpec[] AxisChain ()
        {
            // K の親 P は根でない骨（根の骨は、向けるだけの点のためには回さない。回すと体全体が回る）
            return new[] {
                Top ("R", new Vector3 (0, 1, 0)),
                Child ("P", "R", new Vector3 (0, 1, 0)),
                Child ("K", "P", new Vector3 (0, 0, 1)),
                Child ("A", "K", new Vector3 (0, -0.27f, 1)),
                Child ("B", "A", new Vector3 (0, -0.58f, 1)),
            };
        }

        /// <summary>K は上の軸（y）まわりにだけ回る</summary>
        static void AxisOnly (EditRigDefinition definition)
        {
            EditRigDefinition.ExtraControl k = definition.extraControls.First (c => c.name == "K");
            k.limit.x.mode = EditRigDefinition.LimitMode.Locked;
            k.limit.z.mode = EditRigDefinition.LimitMode.Locked;
        }

        /// <summary>K の、親 P に対する基準姿勢からの回りが、上の軸まわりだけか</summary>
        static void TurnedOnAxis (Setup s, Quaternion rest)
        {
            Transform k = Bone (s, "K");
            Quaternion relative = Quaternion.Inverse (k.parent.rotation) * k.rotation;
            (relative * Quaternion.Inverse (rest)).ToAngleAxis (out float angle, out Vector3 axis);
            float off = Mathf.Acos (Mathf.Clamp01 (Mathf.Abs (Vector3.Dot (axis.normalized, Vector3.up)))) * Mathf.Rad2Deg;
            Check (s, angle <= 0.5f || off <= 1, "K は上の軸まわりにだけ回る: " + angle.ToString ("F1") + "° 回り、軸から " + off.ToString ("F1") + "°");
        }

        [Test]
        public void Case8_1_AimPointOnAxisBone_TurnsTheParentForTheRest ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, AxisChain (), d => {
                AxisOnly (d);
                AddPoint (d, "Kaim", "K", new Vector3 (0, 0, 0.2f), 0);
            });
            Transform kBone = Bone (s, "K");
            Quaternion rest = Quaternion.Inverse (kBone.parent.rotation) * kBone.rotation;
            Vector3 k = At (s, "K");
            Vector3 target = At (s, "Kaim") + new Vector3 (0.05f, 0.15f, 0);
            Drag (s, "Kaim", target);
            Aimed (s, "Kaim", At (s, "K"), target);
            TurnedOnAxis (s, rest);
            Report (s);
        }

        [Test]
        public void Case8_2_AxisBoneInChain_TurnsOnlyOnItsAxis ([Values] bool pinnedRoot)
        {
            Setup s = Create (pinnedRoot, AxisChain (), AxisOnly);
            Transform kBone = Bone (s, "K");
            Quaternion rest = Quaternion.Inverse (kBone.parent.rotation) * kBone.rotation;
            Vector3 r = At (s, "R");
            Pin (s, "R");
            Vector3 target = At (s, "B") + new Vector3 (0.15f, 0.05f, -0.05f);
            Drag (s, "B", target);
            Reached (s, "B", target);
            Stayed (s, "R", r);
            TurnedOnAxis (s, rest);
            Report (s);
        }
    }

}
