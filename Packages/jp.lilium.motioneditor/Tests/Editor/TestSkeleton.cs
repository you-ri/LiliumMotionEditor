using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// テスト用の人型の骨。パッケージの外のアセットに頼らないよう、骨を組んで Avatar をその場で作る
    /// </summary>
    static class TestSkeleton
    {
        /// <summary>
        /// T ポーズの骨を組んで Humanoid の Avatar を付ける。withChest なら必須でない胸と首も足す。
        /// extraBones は左手の下に足す（武器など）。作った物は created に入れるので、呼ぶ側で捨てる
        /// </summary>
        public static Animator CreateHumanoid (List<Object> created, bool withChest, params string[] extraBones)
        {
            return CreateHumanoid (created, withChest, 1, extraBones);
        }

        /// <param name="size">骨の長さの倍率（体の大きさ。Avatar の humanScale が変わる）</param>
        public static Animator CreateHumanoid (List<Object> created, bool withChest, float size, params string[] extraBones)
        {
            return Build (created, withChest, size, false, false, extraBones);
        }

        /// <summary>
        /// 胸と首と、両足のつま先を持つ人型（足の転がしを試すとき）
        /// </summary>
        public static Animator CreateHumanoidWithToes (List<Object> created, float size = 1)
        {
            return Build (created, true, size, false, true);
        }

        /// <summary>
        /// 胸と首と、両手の親指・人差し指（3 節ずつ）を持つ人型。手の形（muscle）を試すとき
        /// </summary>
        public static Animator CreateHumanoidWithFingers (List<Object> created)
        {
            return Build (created, true, 1, true, false);
        }

        static Animator Build (List<Object> created, bool withChest, float size, bool fingers, bool toes, params string[] extraBones)
        {
            GameObject root = new GameObject ("Character");
            created.Add (root);
            Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform> ();

            Transform Add (HumanBodyBones bone, Transform parent, Vector3 local)
            {
                Transform t = new GameObject ("bone_" + bone).transform;
                t.SetParent (parent, false);
                t.localPosition = local * size;
                bones[bone] = t;
                return t;
            }

            Transform hips = Add (HumanBodyBones.Hips, root.transform, new Vector3 (0, 1, 0));
            Transform leftLeg = Add (HumanBodyBones.LeftUpperLeg, hips, new Vector3 (-0.1f, -0.05f, 0));
            Transform leftKnee = Add (HumanBodyBones.LeftLowerLeg, leftLeg, new Vector3 (0, -0.45f, 0));
            Transform leftFoot = Add (HumanBodyBones.LeftFoot, leftKnee, new Vector3 (0, -0.45f, 0));
            Transform rightLeg = Add (HumanBodyBones.RightUpperLeg, hips, new Vector3 (0.1f, -0.05f, 0));
            Transform rightKnee = Add (HumanBodyBones.RightLowerLeg, rightLeg, new Vector3 (0, -0.45f, 0));
            Transform rightFoot = Add (HumanBodyBones.RightFoot, rightKnee, new Vector3 (0, -0.45f, 0));
            if (toes) {
                // つま先の付け根は足首の前 12cm・下 3cm（足首は床から 5cm）
                Add (HumanBodyBones.LeftToes, leftFoot, new Vector3 (0, -0.03f, 0.12f));
                Add (HumanBodyBones.RightToes, rightFoot, new Vector3 (0, -0.03f, 0.12f));
            }

            Transform spine = Add (HumanBodyBones.Spine, hips, new Vector3 (0, 0.15f, 0));
            Transform upper = spine;
            Transform headParent = spine;
            if (withChest) {
                upper = Add (HumanBodyBones.Chest, spine, new Vector3 (0, 0.15f, 0));
                headParent = Add (HumanBodyBones.Neck, upper, new Vector3 (0, 0.2f, 0));
            }
            Add (HumanBodyBones.Head, headParent, new Vector3 (0, withChest ? 0.1f : 0.45f, 0));

            float armY = withChest ? 0.15f : 0.3f;
            Transform leftArm = Add (HumanBodyBones.LeftUpperArm, upper, new Vector3 (-0.2f, armY, 0));
            Transform leftElbow = Add (HumanBodyBones.LeftLowerArm, leftArm, new Vector3 (-0.3f, 0, 0));
            Transform leftHand = Add (HumanBodyBones.LeftHand, leftElbow, new Vector3 (-0.25f, 0, 0));
            Transform rightArm = Add (HumanBodyBones.RightUpperArm, upper, new Vector3 (0.2f, armY, 0));
            Transform rightElbow = Add (HumanBodyBones.RightLowerArm, rightArm, new Vector3 (0.3f, 0, 0));
            Transform rightHand = Add (HumanBodyBones.RightHand, rightElbow, new Vector3 (0.25f, 0, 0));

            if (fingers) {
                // 指は手の先へ 3 節。親指は手前（+z）へ少しずらす
                foreach (bool left in new[] { true, false }) {
                    float x = left ? -1 : 1;
                    Transform hand = left ? leftHand : rightHand;
                    HumanBodyBones thumb = left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal;
                    HumanBodyBones index = left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal;
                    Transform t = Add (thumb, hand, new Vector3 (0.03f * x, 0, 0.03f));
                    t = Add (thumb + 1, t, new Vector3 (0.03f * x, 0, 0.01f));
                    Add (thumb + 2, t, new Vector3 (0.025f * x, 0, 0.01f));
                    Transform i = Add (index, hand, new Vector3 (0.08f * x, 0, 0.02f));
                    i = Add (index + 1, i, new Vector3 (0.035f * x, 0, 0));
                    Add (index + 2, i, new Vector3 (0.025f * x, 0, 0));
                }
            }

            foreach (string name in extraBones) {
                Transform extra = new GameObject (name).transform;
                extra.SetParent (leftHand, false);
            }

            HumanDescription description = new HumanDescription {
                human = bones.Select (pair => new HumanBone {
                    boneName = pair.Value.name,
                    humanName = HumanTrait.BoneName[(int)pair.Key],
                    limit = new HumanLimit { useDefaultValues = true },
                }).ToArray (),
                skeleton = root.GetComponentsInChildren<Transform> ().Select (t => new SkeletonBone {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                }).ToArray (),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0,
                hasTranslationDoF = false,
            };
            Avatar avatar = AvatarBuilder.BuildHumanAvatar (root, description);
            created.Add (avatar);
            Assert.IsTrue (avatar.isValid && avatar.isHuman, "テスト用の Avatar が作れていない");

            Animator animator = root.AddComponent<Animator> ();
            animator.avatar = avatar;
            return animator;
        }
    }

}
