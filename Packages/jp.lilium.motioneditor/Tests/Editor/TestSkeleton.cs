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
            return Build (created, withChest, size, false, false, false, 0, false, false, extraBones);
        }

        /// <summary>
        /// 胸と首と、両足のつま先を持つ人型（足の点を試すとき）。toeOut だけ、つま先の付け根を体の外へずらす（つま先が外を向いた足）
        /// </summary>
        public static Animator CreateHumanoidWithToes (List<Object> created, float size = 1, float toeOut = 0)
        {
            return Build (created, true, size, false, false, true, toeOut, false, false);
        }

        /// <summary>
        /// 胸と首と、両手の親指・人差し指（3 節ずつ）を持つ人型。手の形（muscle）を試すとき。
        /// 手のひらの点（人差し指・小指の付け根に置く）や足の点（つま先の骨が要る）も使うときは、littleFingers・toes で足す
        /// （既定では足さない。手の形のテストの指の組み合わせを変えないため）
        /// </summary>
        /// <param name="toes">両足のつま先も足す（CreateHumanoidWithToes と同じ場所）</param>
        /// <param name="littleFingers">両手の小指（3 節）も足す</param>
        public static Animator CreateHumanoidWithFingers (List<Object> created, bool toes = false, bool littleFingers = false)
        {
            return Build (created, true, 1, true, littleFingers, toes, 0, false, false);
        }

        /// <summary>
        /// 胸と首と、両目を持つ人型（目の高さを試すとき）。目は頭の付け根から上へ 8cm・前へ 8cm
        /// </summary>
        public static Animator CreateHumanoidWithEyes (List<Object> created)
        {
            return Build (created, true, 1, false, false, false, 0, true, false);
        }

        /// <summary>
        /// 胸と首と、両肩（鎖骨）を持つ人型（肩の骨の上の点を試すとき）。肩は胸の付け根から横へ 5cm・上へ 15cm で、上腕の付け根は肩のない人型と同じ場所
        /// </summary>
        public static Animator CreateHumanoidWithShoulders (List<Object> created)
        {
            return Build (created, true, 1, false, false, false, 0, false, true);
        }

        static Animator Build (List<Object> created, bool withChest, float size, bool fingers, bool littleFingers, bool toes, float toeOut, bool eyes, bool shoulders, params string[] extraBones)
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
                Add (HumanBodyBones.LeftToes, leftFoot, new Vector3 (-toeOut, -0.03f, 0.12f));
                Add (HumanBodyBones.RightToes, rightFoot, new Vector3 (toeOut, -0.03f, 0.12f));
            }

            Transform spine = Add (HumanBodyBones.Spine, hips, new Vector3 (0, 0.15f, 0));
            Transform upper = spine;
            Transform headParent = spine;
            if (withChest) {
                upper = Add (HumanBodyBones.Chest, spine, new Vector3 (0, 0.15f, 0));
                headParent = Add (HumanBodyBones.Neck, upper, new Vector3 (0, 0.2f, 0));
            }
            Transform headBone = Add (HumanBodyBones.Head, headParent, new Vector3 (0, withChest ? 0.1f : 0.45f, 0));
            if (eyes) {
                Add (HumanBodyBones.LeftEye, headBone, new Vector3 (-0.03f, 0.08f, 0.08f));
                Add (HumanBodyBones.RightEye, headBone, new Vector3 (0.03f, 0.08f, 0.08f));
            }

            float armY = withChest ? 0.15f : 0.3f;
            Transform leftArmParent = upper, rightArmParent = upper;
            float armX = 0.2f;
            if (shoulders) {
                leftArmParent = Add (HumanBodyBones.LeftShoulder, upper, new Vector3 (-0.05f, armY, 0));
                rightArmParent = Add (HumanBodyBones.RightShoulder, upper, new Vector3 (0.05f, armY, 0));
                armX = 0.15f;
                armY = 0;
            }
            Transform leftArm = Add (HumanBodyBones.LeftUpperArm, leftArmParent, new Vector3 (-armX, armY, 0));
            Transform leftElbow = Add (HumanBodyBones.LeftLowerArm, leftArm, new Vector3 (-0.3f, 0, 0));
            Transform leftHand = Add (HumanBodyBones.LeftHand, leftElbow, new Vector3 (-0.25f, 0, 0));
            Transform rightArm = Add (HumanBodyBones.RightUpperArm, rightArmParent, new Vector3 (armX, armY, 0));
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
                    if (littleFingers) {
                        // 小指は人差し指より手首寄りで後ろ（-z）。手のひらの 2 点（人差し指・小指の付け根）が前後に離れる
                        HumanBodyBones little = left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal;
                        Transform l = Add (little, hand, new Vector3 (0.07f * x, 0, -0.03f));
                        l = Add (little + 1, l, new Vector3 (0.025f * x, 0, 0));
                        Add (little + 2, l, new Vector3 (0.02f * x, 0, 0));
                    }
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
