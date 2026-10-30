using NUnit.Framework;
using UnityEngine;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// S88 の決まりの表（隣の点の状態と重さ × つかんだ点の重さで、隣の点が引かれるか）。
    /// つかむのは左手、隣の点は左肘。表の 1 マスごとに 1 本（重さ 1 の Pin の点を重い点が引くマスだけ、届く所と届かない所で 2 本）
    /// </summary>
    public class FullBodyWeightRuleTests : RigTestFixture
    {
        /// <summary>動かないとみなす肘の動き（m）</summary>
        const float kHeld = 3e-3f;
        /// <summary>引かれたとみなす肘の動き（m）</summary>
        const float kPulled = 0.01f;

        public enum Neighbor { Free, Pin, Lock }
        public enum Expect { Pulled, Held }

        [TestCase (Neighbor.Free, 0, 0, Expect.Pulled)]
        [TestCase (Neighbor.Free, 1, 0, Expect.Held)]
        [TestCase (Neighbor.Pin, 0, 0, Expect.Pulled)]
        [TestCase (Neighbor.Pin, 1, 0, Expect.Held)]
        [TestCase (Neighbor.Lock, 0, 0, Expect.Held)]
        [TestCase (Neighbor.Lock, 1, 0, Expect.Held)]
        [TestCase (Neighbor.Free, 0, 1, Expect.Pulled)]
        [TestCase (Neighbor.Free, 1, 1, Expect.Pulled)]
        [TestCase (Neighbor.Pin, 0, 1, Expect.Pulled)]
        [TestCase (Neighbor.Lock, 0, 1, Expect.Held)]
        [TestCase (Neighbor.Lock, 1, 1, Expect.Held)]
        public void Neighbor_FollowsTheTable (Neighbor state, int elbowWeight, int handWeight, Expect expect)
        {
            float moved = DragHand (state, elbowWeight, handWeight, false);
            if (expect == Expect.Pulled) Assert.Greater (moved, kPulled, "肘は引かれる");
            else Assert.LessOrEqual (moved, kHeld, "肘は動かない");
        }

        [Test]
        public void HeavyPin_StaysWhileReachable_AndFollowsBeyondReach ()
        {
            Assert.LessOrEqual (DragHand (Neighbor.Pin, 1, 1, true), kHeld, "届く間は留まる");
            Assert.Greater (DragHand (Neighbor.Pin, 1, 1, false), kPulled, "届かなければ外れて付いて動く");
        }

        /// <summary>左手をつかんで動かしたときの左肘の動き（m）。aroundElbow なら肘を動かさずに届く所（前腕を肘まわりに回した所）へ、そうでなければ肘が動かないと届かない所へ動かす</summary>
        float DragHand (Neighbor state, int elbowWeight, int handWeight, bool aroundElbow)
        {
            EditRigDefinition definition = CreateDefinition ();
            definition.bodyPoints.First (p => p.name == "LeftLowerArm").weight = elbowWeight;
            definition.bodyPoints.First (p => p.name == "LeftHand").weight = handWeight;
            Animator display = CreateCharacter ();
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            EditingRig rig = CreateEditingRig (definition, display);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            FullBodyRig body = Keep (new FullBodyRig (rig, definition));
            System.Action solve = () => {
                body.Unsolve ();
                rig.ResetBonesToRest ();
                solver.Solve ();
                body.CaptureInput ();
                body.Solve ();
            };
            solve ();

            Transform elbow = rig.GetEditingBone (HumanBodyBones.LeftLowerArm);
            Transform hand = rig.GetEditingBone (HumanBodyBones.LeftHand);
            Vector3 forward = rig.root.transform.TransformDirection (rig.bodyForward).normalized;
            Vector3 elbowRest = elbow.position;
            Vector3 toHand = hand.position - elbowRest;
            if (state != Neighbor.Free) {
                body.CapturePoint (HumanBodyBones.LeftLowerArm);
                body.SetPositionWeight (HumanBodyBones.LeftLowerArm, 1);
                body.SetLocked (HumanBodyBones.LeftLowerArm, state == Neighbor.Lock);
            }
            Vector3 target;
            if (aroundElbow) target = elbowRest + Quaternion.AngleAxis (45, Vector3.up) * toHand;
            // 重い手で重い Pin の肘を外すには、体ごと届かない所まで引く
            else if (state == Neighbor.Pin && elbowWeight == 1 && handWeight == 1) target = hand.position + forward * 1f;
            else target = hand.position + forward * 0.15f - toHand.normalized * 0.15f;

            body.CapturePoint (HumanBodyBones.LeftHand);
            body.SetPositionWeight (HumanBodyBones.LeftHand, 1);
            body.MarkGrabbed (HumanBodyBones.LeftHand);
            body.SetPointPositionWorld (HumanBodyBones.LeftHand, target);
            solve ();
            float moved = Vector3.Distance (elbowRest, elbow.position);
            body.ClearCarry ();
            return moved;
        }
    }

}
