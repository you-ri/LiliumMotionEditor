using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// ChannelMask と AvatarMask の行き来（S17）。AvatarMask が持てるのは部位とモデルの骨の階層だけなので、
    /// 移らない分は notes に書いて知らせる（黙って落とすと、ゲームの Animator で効く範囲が変わったことに気づけない）
    /// </summary>
    public static class ChannelMaskAvatar
    {
        /// <summary>
        /// AvatarMask を読み込む。部位はそのまま写し、骨のパスは model（Humanoid の Animator を持つキャラ）で骨へ引き当てて、
        /// 部位の値と違う骨だけ FK のパスの決まりにする。今の決まりは置き換える
        /// </summary>
        /// <param name="model">骨のパスを引き当てるキャラ。null なら骨のパスは読まない（部位だけ）</param>
        public static void Import (AvatarMask source, ChannelMask destination, GameObject model, List<string> notes)
        {
            if (source == null || destination == null) return;
            for (int i = 0; i < ChannelMask.kPartCount; i++) {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                destination.SetBodyPart (part, source.GetHumanoidBodyPartActive (part));
            }
            destination.paths.Clear ();
            destination.properties.Clear ();
            if (source.transformCount == 0) return;

            Animator animator = model != null ? model.GetComponentInChildren<Animator> (true) : null;
            if (animator == null || !animator.isHuman) {
                notes.Add (Tr ("CHANNEL_MASK_AVATAR_NO_MODEL", source.transformCount));
                return;
            }
            Dictionary<Transform, HumanBodyBones> bones = new Dictionary<Transform, HumanBodyBones> ();
            for (HumanBodyBones bone = 0; bone < HumanBodyBones.LastBone; bone++) {
                Transform t = animator.GetBoneTransform (bone);
                if (t != null) bones[t] = bone;
            }
            // AvatarMask のパスはモデルの一番上から。Animator が一番上に無いキャラもあるので、両方から探す
            Transform top = animator.transform.root;
            int dropped = 0;
            for (int i = 0; i < source.transformCount; i++) {
                string path = source.GetTransformPath (i);
                if (string.IsNullOrEmpty (path)) continue;
                Transform t = top.Find (path) ?? animator.transform.Find (path);
                HumanBodyBones bone;
                if (t == null || !bones.TryGetValue (t, out bone)) {
                    dropped++;
                    continue;
                }
                bool active = source.GetTransformActive (i);
                if (active == destination.GetBodyPart (ChannelMaskParts.PartOf (bone))) continue;
                destination.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.Fk (bone), include = active });
            }
            if (dropped > 0) notes.Add (Tr ("CHANNEL_MASK_AVATAR_DROPPED_TRANSFORMS", dropped));
        }

        /// <summary>
        /// AvatarMask へ書き出す（ゲームの Animator のレイヤーで使う）。書き出すのは部位だけ。
        /// 骨の決まりとプロパティの決まりは AvatarMask で表せない（Humanoid のレイヤーでは部位で絞る）ので、その数を notes に書く
        /// </summary>
        public static void Export (ChannelMask source, AvatarMask destination, List<string> notes)
        {
            if (source == null || destination == null) return;
            for (int i = 0; i < ChannelMask.kPartCount; i++) {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                destination.SetHumanoidBodyPartActive (part, source.GetBodyPart (part));
            }
            destination.transformCount = 0;
            int paths = CountRules (source.paths);
            int properties = CountRules (source.properties);
            if (paths > 0) notes.Add (Tr ("CHANNEL_MASK_AVATAR_PATHS_NOT_EXPORTED", paths));
            if (properties > 0) notes.Add (Tr ("CHANNEL_MASK_AVATAR_PROPERTIES_NOT_EXPORTED", properties));
        }

        static int CountRules<T> (List<T> rules) where T : class
        {
            int count = 0;
            if (rules == null) return 0;
            foreach (T rule in rules) {
                if (rule != null) count++;
            }
            return count;
        }

        /// <summary>
        /// 書き出し先の AvatarMask のパス（マスクと同じフォルダに `<名前> Avatar Mask.mask` で、重ならない名前）
        /// </summary>
        public static string DefaultExportPath (ChannelMask mask)
        {
            string path = mask != null ? AssetDatabase.GetAssetPath (mask) : null;
            if (string.IsNullOrEmpty (path)) return null;
            string folder = System.IO.Path.GetDirectoryName (path).Replace ('\\', '/');
            return AssetDatabase.GenerateUniqueAssetPath (folder + "/" + mask.name + " Avatar Mask.mask");
        }
    }

}
