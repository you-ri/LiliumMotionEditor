using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 外部の補間（S81）: キーの間の値を、外部のツール（学習済みのモーション生成モデルなど）が作った動きから取る。
    /// 結果は元のクリップの隣の `<元の名前>.between.anim` に、編集用リグの値そのもの（絶対値）で持つ。Override の段には出さない。
    /// どの元のクリップの、どの区間（隣り合う 2 つのキーの間）に使うかは、結果のファイルの .meta に持つ。
    /// 元のクリップを当てるとき（PreviewStage.SampleBase）に、区間の中は結果、キーでは元のキー、キーの近くは smoothstep で混ぜる。
    /// 混ぜる重みは評価するときに掛けるので、キーの値を直しても寄せる先は新しいキーになり、キーでの速さは元の曲線の傾きのまま
    /// （曲線の区間との境目で速さが続く）。差分でなく絶対値で持つので、隣の区間の補間を変えて元の曲線が変わっても結果はずれない。
    /// 区間の両端が隣り合うキーでなくなった区間（キーを足した・動かした・消した）は使わない（その区間は元のクリップの補間に戻る）
    /// </summary>
    public static class BetweenClips
    {
        public const string kSuffix = ".between";
        /// <summary>キーの前後で混ぜる長さ（秒。S75 の keepKeysBlend と同じ）。区間の長さの 1/3 を超えない</summary>
        public const float kBlendSeconds = 0.25f;

        /// <summary>区間（start のキーから end のキーまで）</summary>
        [System.Serializable]
        public struct Range
        {
            public int start;
            public int end;
        }

        /// <summary>.meta に書く印（ClipUserData.between）</summary>
        [System.Serializable]
        internal sealed class Settings
        {
            /// <summary>元のクリップの GUID</summary>
            public string baseClip;
            public List<Range> segments = new List<Range> ();
        }

        /// <summary>元のクリップの名前（編集用クリップの .rig は外す）</summary>
        static string BaseName (string path)
        {
            string name = Path.GetFileNameWithoutExtension (path);
            return name.EndsWith (EditingClip.kSuffix) ? name.Substring (0, name.Length - EditingClip.kSuffix.Length) : name;
        }

        /// <summary>元のクリップの結果のファイルのパス（元がアセットでなければ null）</summary>
        public static string PathFor (AnimationClip baseClip)
        {
            string path = baseClip != null ? AssetDatabase.GetAssetPath (baseClip) : null;
            if (string.IsNullOrEmpty (path) || AssetDatabase.IsSubAsset (baseClip)) return null;
            return Path.GetDirectoryName (path).Replace ('\\', '/') + "/" + BaseName (path) + kSuffix + ".anim";
        }

        /// <summary>元のクリップの結果のファイル（無い・別の元の印なら null）</summary>
        public static AnimationClip Find (AnimationClip baseClip)
        {
            string path = PathFor (baseClip);
            if (path == null) return null;
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            if (clip == null) return null;
            Settings settings = ClipUserData.Load (path).between;
            return settings != null && settings.baseClip == AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (baseClip)) ? clip : null;
        }

        /// <summary>結果のファイルの区間（印が無ければ空）</summary>
        public static List<Range> GetSegments (AnimationClip between)
        {
            Settings settings = between != null ? ClipUserData.Load (between).between : null;
            return settings != null && settings.segments != null ? settings.segments.ToList () : new List<Range> ();
        }

        /// <summary>結果のファイルに、元のクリップと区間の印を書く（.meta なので Undo には乗らない）</summary>
        public static bool SaveSegments (AnimationClip between, AnimationClip baseClip, IEnumerable<Range> segments)
        {
            string baseGuid = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (baseClip));
            bool saved = ClipUserData.Save (between, data => data.between = new Settings {
                baseClip = baseGuid,
                segments = segments.Distinct ().OrderBy (r => r.start).ToList (),
            });
            version_++;
            return saved;
        }

        /// <summary>
        /// 区間の始まり・終わりが、元のクリップの隣り合うキーか（外部の補間を使えるか）。使えなければ理由
        /// </summary>
        public static string GetSegmentProblem (AnimationClip baseClip, Range range)
        {
            int[] frames = ClipCycle.GetFrames (baseClip);
            int index = System.Array.IndexOf (frames, range.start);
            if (index < 0 || index + 1 >= frames.Length || frames[index + 1] != range.end) {
                return MotionEditorLocalization.Tr ("BETWEEN_CLIPS_NOT_A_SEGMENT", range.start, range.end);
            }
            return null;
        }

        /// <summary>区間の中の重み（キーで 0・中は 1・キーの前後 blend フレームは smoothstep）</summary>
        public static float Weight (Range range, float frame, float rate)
        {
            if (frame <= range.start || frame >= range.end) return 0;
            float blend = Mathf.Min (kBlendSeconds * rate, (range.end - range.start) / 3f);
            if (blend <= 0) return 1;
            float u = Mathf.Clamp01 (Mathf.Min (frame - range.start, range.end - frame) / blend);
            return u * u * (3 - 2 * u);
        }

        sealed class Entry
        {
            public int baseVersion;
            public int version;
            /// <summary>結果のファイルを見つけたか（見つけたファイルが消されたら探し直す）</summary>
            public bool found;
            public AnimationClip between;
            public Range[] active;
        }

        static readonly Dictionary<AnimationClip, Entry> cache_ = new Dictionary<AnimationClip, Entry> ();
        /// <summary>印やファイルが変わるたびに増やす（カーブの版では分からない .meta の書き換え・ファイルの追加と削除）</summary>
        static int version_;
        static bool hooked_;

        /// <summary>元のクリップに、今使える外部の補間の区間があるか</summary>
        public static bool HasSegments (AnimationClip baseClip)
        {
            Entry entry = GetEntry (baseClip);
            return entry != null && entry.between != null && entry.active.Length > 0;
        }

        /// <summary>
        /// 元のクリップの time で、外部の補間を混ぜるか（混ぜるなら結果のファイルと重み）。元のクリップのカーブとプロジェクトが変わるまで使い回す
        /// </summary>
        public static bool TryGetBlend (AnimationClip baseClip, float time, out AnimationClip between, out float weight)
        {
            between = null;
            weight = 0;
            Entry entry = GetEntry (baseClip);
            if (entry == null || entry.between == null || entry.active.Length == 0) return false;
            float rate = baseClip.frameRate;
            float frame = time * rate;
            foreach (Range range in entry.active) {
                float w = Weight (range, frame, rate);
                if (w <= 0) continue;
                between = entry.between;
                weight = w;
                return true;
            }
            return false;
        }

        static Entry GetEntry (AnimationClip baseClip)
        {
            if (baseClip == null) return null;
            if (!hooked_) {
                hooked_ = true;
                EditorApplication.projectChanged += () => version_++;
            }
            int baseVersion = EditingClip.GetCurveVersion (baseClip);
            if (!cache_.TryGetValue (baseClip, out Entry entry) || entry.baseVersion != baseVersion || entry.version != version_ || entry.found && entry.between == null) {
                entry = Build (baseClip, baseVersion);
                cache_[baseClip] = entry;
            }
            return entry;
        }

        static Entry Build (AnimationClip baseClip, int baseVersion)
        {
            Entry entry = new Entry { baseVersion = baseVersion, version = version_, active = new Range[0] };
            entry.between = Find (baseClip);
            entry.found = entry.between != null;
            if (!entry.found) return entry;
            entry.active = GetSegments (entry.between).Where (r => GetSegmentProblem (baseClip, r) == null).ToArray ();
            return entry;
        }

        /// <summary>覚えた区間を捨てる（テスト・結果のファイルを外で書き換えたとき）</summary>
        public static void Invalidate ()
        {
            version_++;
        }
    }

}
