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
    /// 元のクリップを当てるとき（PreviewStage.SampleBase）に、区間の中はすべてのフレームで結果を使い、キーでの結果とキーの姿勢のずれを足す:
    /// 始まりのキーでのずれから終わりのキーでのずれへ、区間の中で smoothstep で移す（10-26 に変更。前はキーの近くで元の曲線へ混ぜていて、
    /// 区間の 1/3 から 2/3 が結果でなく混ぜ物になっていた）。ずれは評価するときに測るので、キーの値を直してもキーを通る。
    /// smoothstep なので、キーでの速さは結果の速さのまま（結果の続いている隣の区間との境目で速さが続く）。
    /// 差分でなく絶対値で持つので、隣の区間の補間を変えて元の曲線が変わっても結果はずれない。
    /// 区間の両端が隣り合うキーでなくなった区間（キーを足した・動かした・消した）は使わない（その区間は元のクリップの補間に戻る）
    /// </summary>
    public static class BetweenClips
    {
        public const string kSuffix = ".between";

        /// <summary>区間（start のキーから end のキーまで）</summary>
        [System.Serializable]
        public struct Range
        {
            public int start;
            public int end;
        }

        /// <summary>結果を作ったときの区間の指紋（S82。区間の両端のフレームと、その 2 フレームの元のクリップの全部のカーブの値）</summary>
        [System.Serializable]
        public struct Stamp
        {
            public int start;
            public int end;
            public string hash;
        }

        /// <summary>
        /// 区間の状態（S82）。Fresh: 結果を作ったときのキーのまま使う。Stale: キーの値が変わった（使い続ける。寄せる先は新しいキー）。
        /// Lost: キーのフレームが変わって区間でなくなった（使わない。元の補間で見せる）。None: 外部の補間でない
        /// </summary>
        public enum State
        {
            None,
            Fresh,
            Stale,
            Lost,
        }

        /// <summary>.meta に書く印（ClipUserData.between）</summary>
        [System.Serializable]
        internal sealed class Settings
        {
            /// <summary>元のクリップの GUID</summary>
            public string baseClip;
            /// <summary>外部の補間を使う区間</summary>
            public List<Range> segments = new List<Range> ();
            /// <summary>結果を作ったときの、元のクリップの全部の区間の指紋</summary>
            public List<Stamp> stamps = new List<Stamp> ();
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

        /// <summary>結果のファイルの指紋（印が無ければ空）</summary>
        public static List<Stamp> GetStamps (AnimationClip between)
        {
            Settings settings = between != null ? ClipUserData.Load (between).between : null;
            return settings != null && settings.stamps != null ? settings.stamps.ToList () : new List<Stamp> ();
        }

        /// <summary>
        /// 結果のファイルに、元のクリップと区間の印を書く（.meta なので Undo には乗らない）。stamps を渡せば指紋も書き換える（省くと今のまま）。
        /// 元のクリップの .meta にも結果のファイルの印を付ける（結果のファイルが無くなったことを検査で知らせるため）
        /// </summary>
        public static bool SaveSegments (AnimationClip between, AnimationClip baseClip, IEnumerable<Range> segments, IEnumerable<Stamp> stamps = null)
        {
            string baseGuid = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (baseClip));
            List<Stamp> kept = stamps != null ? stamps.ToList () : GetStamps (between);
            bool saved = ClipUserData.Save (between, data => data.between = new Settings {
                baseClip = baseGuid,
                segments = segments.Distinct ().OrderBy (r => r.start).ToList (),
                stamps = kept,
            });
            string betweenGuid = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (between));
            ClipUserData.Save (baseClip, data => data.betweenClip = betweenGuid);
            version_++;
            return saved;
        }

        /// <summary>元のクリップの、今の全部の区間の指紋</summary>
        public static List<Stamp> MakeStamps (AnimationClip baseClip)
        {
            List<Stamp> stamps = new List<Stamp> ();
            int[] frames = ClipCycle.GetFrames (baseClip);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (baseClip).OrderBy (b => b.path).ThenBy (b => b.propertyName).ToArray ();
            AnimationCurve[] curves = bindings.Select (b => AnimationUtility.GetEditorCurve (baseClip, b)).ToArray ();
            for (int i = 0; i + 1 < frames.Length; i++) {
                stamps.Add (new Stamp { start = frames[i], end = frames[i + 1], hash = Fingerprint (baseClip, bindings, curves, frames[i], frames[i + 1]) });
            }
            return stamps;
        }

        /// <summary>区間の両端のフレームと、その 2 フレームの全部のカーブの値の指紋</summary>
        static string Fingerprint (AnimationClip baseClip, EditorCurveBinding[] bindings, AnimationCurve[] curves, int start, int end)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder ();
            text.Append (start).Append ('-').Append (end).Append (':');
            float rate = baseClip.frameRate;
            for (int i = 0; i < bindings.Length; i++) {
                if (curves[i] == null) continue;
                text.Append (bindings[i].path).Append ('/').Append (bindings[i].propertyName).Append ('=')
                    .Append (curves[i].Evaluate (start / rate).ToString ("R")).Append (',').Append (curves[i].Evaluate (end / rate).ToString ("R")).Append (';');
            }
            return Hash128.Compute (text.ToString ()).ToString ();
        }

        /// <summary>
        /// 区間を外部の補間にする（on）・外す。結果のファイルが無ければ false。区間の確かめは呼ぶ側で（外すときは区間でなくてもよい）
        /// </summary>
        public static bool SetSegment (AnimationClip baseClip, Range range, bool on)
        {
            AnimationClip between = Find (baseClip);
            if (between == null) return false;
            List<Range> segments = GetSegments (between);
            bool had = segments.Contains (range);
            if (on == had) return true;
            if (on) segments.Add (range);
            else segments.Remove (range);
            return SaveSegments (between, baseClip, segments);
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

        /// <summary>区間の中の重み（キーと区間の外で 0・中は 1）</summary>
        public static float Weight (Range range, float frame)
        {
            return frame > range.start && frame < range.end ? 1 : 0;
        }

        /// <summary>始まりのキーのずれから終わりのキーのずれへ移す割合（始まりで 0・終わりで 1。smoothstep）</summary>
        public static float OffsetBlend (Range range, float frame)
        {
            if (range.end <= range.start) return 0;
            float u = Mathf.Clamp01 ((frame - range.start) / (range.end - range.start));
            return u * u * (3 - 2 * u);
        }

        sealed class Entry
        {
            public int baseVersion;
            public int version;
            /// <summary>結果のファイルを見つけたか（見つけたファイルが消されたら探し直す）</summary>
            public bool found;
            public AnimationClip between;
            /// <summary>使う区間（両端が今も隣り合うキーのもの）と、印はあるが区間でなくなったもの</summary>
            public Range[] active;
            public Range[] lost;
            public Dictionary<Range, string> stamps;
            /// <summary>使う区間の状態（Fresh か Stale。指紋はカーブを全部読むので、カーブが変わるまで覚える）</summary>
            public Dictionary<Range, State> states = new Dictionary<Range, State> ();
        }

        static readonly Dictionary<AnimationClip, Entry> cache_ = new Dictionary<AnimationClip, Entry> ();
        /// <summary>印やファイルが変わるたびに増やす（カーブの版では分からない .meta の書き換え・ファイルの追加と削除）</summary>
        static int version_;
        static bool hooked_;

        /// <summary>元のクリップに、外部の補間の区間の印があるか（使えなくなった区間も）</summary>
        public static bool HasAny (AnimationClip baseClip)
        {
            Entry entry = GetEntry (baseClip);
            return entry != null && entry.between != null && entry.active.Length + entry.lost.Length > 0;
        }

        /// <summary>元のクリップに、今使える外部の補間の区間があるか</summary>
        public static bool HasSegments (AnimationClip baseClip)
        {
            Entry entry = GetEntry (baseClip);
            return entry != null && entry.between != null && entry.active.Length > 0;
        }

        /// <summary>
        /// 元のクリップの time で、外部の補間を使うか（使うなら結果のファイルと重み。区間の中は 1）。元のクリップのカーブとプロジェクトが変わるまで使い回す
        /// </summary>
        public static bool TryGetBlend (AnimationClip baseClip, float time, out AnimationClip between, out float weight)
        {
            weight = TryGetSegment (baseClip, time, out between, out Range range) ? 1 : 0;
            return weight > 0;
        }

        /// <summary>元のクリップの time が、使う区間の中（両端のキーは含まない）か。中なら結果のファイルと区間</summary>
        public static bool TryGetSegment (AnimationClip baseClip, float time, out AnimationClip between, out Range range)
        {
            between = null;
            range = default;
            Entry entry = GetEntry (baseClip);
            if (entry == null || entry.between == null || entry.active.Length == 0) return false;
            float frame = time * baseClip.frameRate;
            foreach (Range r in entry.active) {
                if (Weight (r, frame) <= 0) continue;
                between = entry.between;
                range = r;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 外部の補間の評価で覚えておく値（キーでのずれ）の目印。元のクリップのカーブ・結果のファイルのカーブ・印・プロジェクトのどれかが変わると変わる
        /// </summary>
        public static int GetVersion (AnimationClip baseClip)
        {
            Entry entry = GetEntry (baseClip);
            if (entry == null) return 0;
            int between = entry.between != null ? EditingClip.GetCurveVersion (entry.between) : 0;
            return (entry.baseVersion * 7919 + entry.version) * 31 + between;
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
            Entry entry = new Entry { baseVersion = baseVersion, version = version_, active = new Range[0], lost = new Range[0] };
            entry.between = Find (baseClip);
            entry.found = entry.between != null;
            if (!entry.found) return entry;
            List<Range> segments = GetSegments (entry.between);
            entry.active = segments.Where (r => GetSegmentProblem (baseClip, r) == null).ToArray ();
            entry.lost = segments.Except (entry.active).ToArray ();
            entry.stamps = new Dictionary<Range, string> ();
            foreach (Stamp stamp in GetStamps (entry.between)) entry.stamps[new Range { start = stamp.start, end = stamp.end }] = stamp.hash;
            return entry;
        }

        /// <summary>印の付いた区間 1 つの状態（使えれば Fresh か Stale、区間でなくなっていれば Lost。印が無ければ None）</summary>
        public static State GetSegmentState (AnimationClip baseClip, Range range)
        {
            Entry entry = GetEntry (baseClip);
            if (entry == null || entry.between == null) return State.None;
            if (System.Array.IndexOf (entry.active, range) >= 0) return ActiveState (baseClip, entry, range);
            return System.Array.IndexOf (entry.lost, range) >= 0 ? State.Lost : State.None;
        }

        /// <summary>
        /// フレームの間（start から end。タイムラインのトラックのキーの間）に掛かる外部の補間の状態。
        /// 使う区間が掛かれば Fresh（どれかが古ければ Stale）、使えなくなった区間だけが掛かれば Lost、どれも掛からなければ None
        /// </summary>
        public static State GetState (AnimationClip baseClip, int start, int end)
        {
            Entry entry = GetEntry (baseClip);
            if (entry == null || entry.between == null) return State.None;
            State result = State.None;
            foreach (Range range in entry.active) {
                if (range.end <= start || range.start >= end) continue;
                State state = ActiveState (baseClip, entry, range);
                if (state == State.Stale) return State.Stale;
                result = state;
            }
            if (result != State.None) return result;
            foreach (Range range in entry.lost) {
                if (range.end > start && range.start < end) return State.Lost;
            }
            return State.None;
        }

        static State ActiveState (AnimationClip baseClip, Entry entry, Range range)
        {
            if (entry.states.TryGetValue (range, out State state)) return state;
            // ほかの使う区間の状態も同じ読みで決まるので、まとめて決める
            foreach (Range other in entry.active) entry.states[other] = State.Stale;
            foreach (Stamp stamp in MakeStamps (baseClip)) {
                Range other = new Range { start = stamp.start, end = stamp.end };
                if (entry.states.ContainsKey (other) && entry.stamps.TryGetValue (other, out string hash) && hash == stamp.hash) entry.states[other] = State.Fresh;
            }
            return entry.states[range];
        }

        /// <summary>覚えた区間を捨てる（テスト・結果のファイルを外で書き換えたとき）</summary>
        public static void Invalidate ()
        {
            version_++;
        }
    }

}
