using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 段（Override）が効く範囲（S17）。AvatarMask は Humanoid の部位と骨の階層しか持てず、ブレンドシェイプや任意の数値を絞れない。
    /// また、このエディタが保存するのはモデルの骨ではなく編集用リグの操作値（Controls/...）なので、自前のアセットにする。
    ///
    /// 決め方: まず部位（操作値はリグ定義から部位へ振り分ける。部位の無い操作値は含める）。
    /// 次にパスの決まり、数値のカーブならプロパティの決まりを上から順に当て、当たった決まりの含める/除くで上書きする（後の決まりが勝つ）
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/Channel Mask", fileName = "New Channel Mask", order = 1102)]
    public sealed class ChannelMask : ScriptableObject
    {
        /// <summary>
        /// パスの決まり。pattern は編集用クリップのパス（Controls/FK/LeftHand など）。* は何文字でも（/ もまたぐ）、? は 1 文字。
        /// パターンに当たったパスの子（下の階層）にも効く
        /// </summary>
        [System.Serializable]
        public sealed class PathRule
        {
            public string pattern = "";
            public bool include = true;
        }

        /// <summary>
        /// 数値のカーブの決まり。空の欄はどれにでも当たる。* と ? を使える。
        /// path は任意のプロパティ（Controls/Props/...）なら表示モデルから見たパス、それ以外は編集用クリップのパス。
        /// type はコンポーネントの型の名前（SkinnedMeshRenderer など）、property はプロパティ名（blendShape.Mouth_* など）
        /// </summary>
        [System.Serializable]
        public sealed class PropertyRule
        {
            public string type = "";
            public string path = "";
            public string property = "";
            public bool include = true;
        }

        /// <summary>部位の数（AvatarMaskBodyPart の LastBodyPart）</summary>
        public const int kPartCount = (int)AvatarMaskBodyPart.LastBodyPart;

        /// <summary>部位ごとに含めるか（AvatarMaskBodyPart の順）。足りない分は含める</summary>
        [SerializeField] bool[] bodyParts_ = NewParts (true);
        public List<PathRule> paths = new List<PathRule> ();
        public List<PropertyRule> properties = new List<PropertyRule> ();

        static readonly Dictionary<string, Regex> patterns_ = new Dictionary<string, Regex> ();

        static bool[] NewParts (bool value)
        {
            bool[] parts = new bool[kPartCount];
            for (int i = 0; i < parts.Length; i++) parts[i] = value;
            return parts;
        }

        public bool GetBodyPart (AvatarMaskBodyPart part)
        {
            int index = (int)part;
            if (index < 0 || index >= kPartCount) return true;
            return bodyParts_ == null || index >= bodyParts_.Length || bodyParts_[index];
        }

        public void SetBodyPart (AvatarMaskBodyPart part, bool value)
        {
            int index = (int)part;
            if (index < 0 || index >= kPartCount) return;
            if (bodyParts_ == null || bodyParts_.Length < kPartCount) {
                bool[] parts = NewParts (true);
                if (bodyParts_ != null) System.Array.Copy (bodyParts_, parts, bodyParts_.Length);
                bodyParts_ = parts;
            }
            bodyParts_[index] = value;
        }

        /// <summary>全部の部位をまとめて切り替える</summary>
        public void SetAllBodyParts (bool value)
        {
            bodyParts_ = NewParts (value);
        }

        /// <summary>
        /// カーブがマスクの中か。part はそのカーブの部位（部位の無い操作値は LastBodyPart。ChannelMaskParts で引く）
        /// </summary>
        public bool Contains (EditorCurveBinding binding, AvatarMaskBodyPart part)
        {
            bool result = GetBodyPart (part);
            if (paths != null) {
                foreach (PathRule rule in paths) {
                    if (rule != null && MatchPath (rule.pattern, binding.path)) result = rule.include;
                }
            }
            if (binding.type != typeof (Transform) && properties != null) {
                string displayPath = RigPaths.FromProps (binding.path);
                string path = displayPath ?? binding.path;
                foreach (PropertyRule rule in properties) {
                    if (rule == null) continue;
                    if (!MatchOptional (rule.type, binding.type != null ? binding.type.Name : "")) continue;
                    if (!MatchOptional (rule.path, path)) continue;
                    if (!MatchOptional (rule.property, binding.propertyName)) continue;
                    result = rule.include;
                }
            }
            return result;
        }

        /// <summary>パスがパターンに当たるか（当たったパスの子にも当たる）</summary>
        public static bool MatchPath (string pattern, string path)
        {
            if (string.IsNullOrEmpty (pattern)) return false;
            path = path ?? "";
            if (Match (pattern, path)) return true;
            // 子にも効かせる（パターンの後ろに /... を足した形）
            int slash = path.LastIndexOf ('/');
            while (slash > 0) {
                if (Match (pattern, path.Substring (0, slash))) return true;
                slash = path.LastIndexOf ('/', slash - 1);
            }
            return false;
        }

        static bool MatchOptional (string pattern, string text)
        {
            return string.IsNullOrEmpty (pattern) || Match (pattern, text ?? "");
        }

        /// <summary>* と ? のパターン（大文字小文字は区別する。カーブのパスとプロパティ名は区別されるため）</summary>
        public static bool Match (string pattern, string text)
        {
            if (pattern.IndexOf ('*') < 0 && pattern.IndexOf ('?') < 0) return pattern == text;
            Regex regex;
            if (!patterns_.TryGetValue (pattern, out regex)) {
                regex = new Regex ("^" + Regex.Escape (pattern).Replace ("\\*", ".*").Replace ("\\?", ".") + "$", RegexOptions.Singleline);
                patterns_[pattern] = regex;
            }
            return regex.IsMatch (text);
        }
    }

    /// <summary>
    /// マスクを設定（.meta・キャラの設定）に GUID で持つための行き来と、パッケージに同梱する既定のマスク
    /// </summary>
    public static class ChannelMaskFiles
    {
        /// <summary>既定のマスク（Body / Upper Body / Hands / Face）を置くフォルダ</summary>
        public const string kDefaultFolder = "Packages/jp.lilium.motioneditor/Editor/Masks";

        public const string kBody = "Body";
        public const string kUpperBody = "Upper Body";
        public const string kHands = "Hands";
        public const string kFace = "Face";
        public static readonly string[] kDefaultNames = { kBody, kUpperBody, kHands, kFace };

        /// <summary>同梱した既定のマスク（名前順ではなく kDefaultNames の順）</summary>
        public static List<ChannelMask> LoadDefaults ()
        {
            List<ChannelMask> result = new List<ChannelMask> ();
            foreach (string name in kDefaultNames) {
                ChannelMask mask = AssetDatabase.LoadAssetAtPath<ChannelMask> (kDefaultFolder + "/" + name + ".asset");
                if (mask != null) result.Add (mask);
            }
            return result;
        }

        /// <summary>
        /// 既定のマスクの中身を入れる（同梱のアセットはこれで作った）。
        /// - Body: 体の全部（任意のプロパティは除く。表情の段と分ける）
        /// - Upper Body: 腰より上（脚・ルート・腰の骨を除く）
        /// - Hands: 指だけ
        /// - Face: ブレンドシェイプだけ（体の操作値は全部除く）
        /// </summary>
        public static void FillDefault (ChannelMask mask, string name)
        {
            mask.SetAllBodyParts (true);
            mask.paths.Clear ();
            mask.properties.Clear ();
            switch (name) {
                case kBody:
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kProps, include = false });
                    break;
                case kUpperBody:
                    foreach (AvatarMaskBodyPart part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg, AvatarMaskBodyPart.LeftFootIK, AvatarMaskBodyPart.RightFootIK }) {
                        mask.SetBodyPart (part, false);
                    }
                    // 腰は Body の部位だが、回すと脚ごと動くので上半身に入れない
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.Fk (HumanBodyBones.Hips), include = false });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.Body (HumanBodyBones.Hips), include = false });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kProps, include = false });
                    break;
                case kHands:
                    mask.SetAllBodyParts (false);
                    mask.SetBodyPart (AvatarMaskBodyPart.LeftFingers, true);
                    mask.SetBodyPart (AvatarMaskBodyPart.RightFingers, true);
                    // 部位の無い操作値（Rig の代理・任意のプロパティなど）も除く
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kRoot, include = false });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Left*Proximal", include = true });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Left*Intermediate", include = true });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Left*Distal", include = true });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Right*Proximal", include = true });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Right*Intermediate", include = true });
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kFk + "/Right*Distal", include = true });
                    break;
                case kFace:
                    mask.SetAllBodyParts (false);
                    mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kRoot, include = false });
                    mask.properties.Add (new ChannelMask.PropertyRule { property = "blendShape.*", include = true });
                    break;
            }
        }

        public static string ToGuid (ChannelMask mask)
        {
            string path = mask != null ? AssetDatabase.GetAssetPath (mask) : null;
            return string.IsNullOrEmpty (path) ? "" : AssetDatabase.AssetPathToGUID (path);
        }

        public static ChannelMask FromGuid (string guid)
        {
            if (string.IsNullOrEmpty (guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath (guid);
            return string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<ChannelMask> (path);
        }
    }

    /// <summary>
    /// 編集用クリップのパスを Humanoid の部位へ振り分ける（S17）。FK は骨、全身 IK の点は点の骨で決める。
    /// 部位の無いもの（Rig の代理・任意のプロパティ・Extra）は LastBodyPart
    /// </summary>
    public sealed class ChannelMaskParts
    {
        readonly Dictionary<string, AvatarMaskBodyPart> byName_ = new Dictionary<string, AvatarMaskBodyPart> ();
        readonly Dictionary<string, AvatarMaskBodyPart> cache_ = new Dictionary<string, AvatarMaskBodyPart> ();

        public ChannelMaskParts (EditRigDefinition definition)
        {
            if (definition == null) return;
            foreach (EditRigDefinition.BodyPoint point in definition.bodyPoints) {
                if (point == null || string.IsNullOrEmpty (point.name)) continue;
                byName_[RigPaths.BodyPoint (point.name)] = PartOf (point.bone);
            }
        }

        public AvatarMaskBodyPart PartOf (string path)
        {
            path = path ?? "";
            AvatarMaskBodyPart part;
            if (cache_.TryGetValue (path, out part)) return part;
            part = Resolve (path);
            cache_[path] = part;
            return part;
        }

        AvatarMaskBodyPart Resolve (string path)
        {
            if (path.Length == 0) return AvatarMaskBodyPart.Root;
            // 骨の FK（Controls/FK/<骨>）
            if (path.StartsWith (RigPaths.kFk + "/")) {
                string name = FirstSegment (path, RigPaths.kFk.Length + 1);
                HumanBodyBones bone;
                if (System.Enum.TryParse (name, out bone) && bone != HumanBodyBones.LastBone) return PartOf (bone);
                return AvatarMaskBodyPart.LastBodyPart;
            }
            // 全身 IK の点（Controls/Body/<点>。向きの点は <骨>Direction）
            if (path.StartsWith (RigPaths.kBody + "/")) {
                string name = FirstSegment (path, RigPaths.kBody.Length + 1);
                AvatarMaskBodyPart part;
                if (byName_.TryGetValue (RigPaths.kBody + "/" + name, out part)) return part;
                HumanBodyBones bone;
                const string kDirection = "Direction";
                if (name.EndsWith (kDirection) && System.Enum.TryParse (name.Substring (0, name.Length - kDirection.Length), out bone) && bone != HumanBodyBones.LastBone) return PartOf (bone);
                if (System.Enum.TryParse (name, out bone) && bone != HumanBodyBones.LastBone) return PartOf (bone);
            }
            return AvatarMaskBodyPart.LastBodyPart;
        }

        static string FirstSegment (string path, int start)
        {
            int end = path.IndexOf ('/', start);
            return end < 0 ? path.Substring (start) : path.Substring (start, end - start);
        }

        /// <summary>骨の部位（AvatarMask の部位の分け方と同じ）</summary>
        public static AvatarMaskBodyPart PartOf (HumanBodyBones bone)
        {
            switch (bone) {
                case HumanBodyBones.Hips:
                case HumanBodyBones.Spine:
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest:
                    return AvatarMaskBodyPart.Body;
                case HumanBodyBones.Neck:
                case HumanBodyBones.Head:
                case HumanBodyBones.LeftEye:
                case HumanBodyBones.RightEye:
                case HumanBodyBones.Jaw:
                    return AvatarMaskBodyPart.Head;
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.LeftHand:
                    return AvatarMaskBodyPart.LeftArm;
                case HumanBodyBones.RightShoulder:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.RightHand:
                    return AvatarMaskBodyPart.RightArm;
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.LeftToes:
                    return AvatarMaskBodyPart.LeftLeg;
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.RightFoot:
                case HumanBodyBones.RightToes:
                    return AvatarMaskBodyPart.RightLeg;
            }
            if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.LeftLittleDistal) return AvatarMaskBodyPart.LeftFingers;
            if (bone >= HumanBodyBones.RightThumbProximal && bone <= HumanBodyBones.RightLittleDistal) return AvatarMaskBodyPart.RightFingers;
            return AvatarMaskBodyPart.LastBodyPart;
        }
    }

}
