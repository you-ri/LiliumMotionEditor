using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Stacker（S12）のキー操作。組（骨のまとまり）のカーブのキーのフレームをまとめたものを「ポーズキー」として扱い、
    /// 挿入・削除・間隔の変更で、組の中の後ろのキーを一緒にずらす。ループ（Loop）はサイクルキー（S32。ClipCycle）が受け持つ。
    /// どれも組のカーブ（filter）だけに効き、ほかの組のキーは動かさない。Undo の記録は呼ぶ側
    /// </summary>
    public static class Stacker
    {
        /// <summary>挿入の後ろに次のキーが無いときの間隔（フレーム）</summary>
        public const int kDefaultInterval = 10;

        /// <summary>
        /// 組のポーズキー（昇順）
        /// </summary>
        public static int[] GetPoseKeys (AnimationClip clip, System.Predicate<EditorCurveBinding> filter)
        {
            return clip != null ? ClipKeyUtility.GetKeyFrames (clip, filter).Where (f => f >= 0).ToArray () : new int[0];
        }

        /// <summary>
        /// frame のポーズキーの後ろに、同じ値のポーズキーを足す。後ろのキーは間隔の分だけ押し出す。
        /// 間隔は次のポーズキーまでの長さ（無ければ既定）。足したフレームを返す（frame がポーズキーでなければ -1）
        /// </summary>
        public static int InsertAfter (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> filter)
        {
            int[] keys = GetPoseKeys (clip, filter);
            if (System.Array.IndexOf (keys, frame) < 0) return -1;
            int next = keys.Where (k => k > frame).DefaultIfEmpty (-1).Min ();
            int interval = next > frame ? next - frame : kDefaultInterval;
            ClipKeyUtility.ShiftKeysAfter (clip, frame, interval, filter);
            ClipKeyUtility.MoveKeysAtFrame (clip, frame, frame + interval, true, filter);
            return frame + interval;
        }

        /// <summary>
        /// frame のポーズキーを消し、後ろのキーをその間隔の分だけ詰める（次のポーズキーが消したキーの位置へ来る）
        /// </summary>
        public static bool Delete (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> filter)
        {
            int[] keys = GetPoseKeys (clip, filter);
            if (System.Array.IndexOf (keys, frame) < 0) return false;
            int next = keys.Where (k => k > frame).DefaultIfEmpty (-1).Min ();
            ClipKeyUtility.RemoveKeysAtFrame (clip, frame, filter);
            if (next > frame) ClipKeyUtility.ShiftKeysAfter (clip, frame - 1, frame - next, filter);
            return true;
        }

        /// <summary>
        /// frame のポーズキーから次のポーズキーまでの間隔を interval フレームにする（後ろのキーを全部ずらす）
        /// </summary>
        public static bool SetInterval (AnimationClip clip, int frame, int interval, System.Predicate<EditorCurveBinding> filter)
        {
            if (interval < 1) return false;
            int[] keys = GetPoseKeys (clip, filter);
            int index = System.Array.IndexOf (keys, frame);
            if (index < 0 || index + 1 >= keys.Length) return false;
            int delta = interval - (keys[index + 1] - frame);
            if (delta == 0) return false;
            ClipKeyUtility.ShiftKeysAfter (clip, frame, delta, filter);
            return true;
        }

        // ---- クリップごとの設定（組・残像）。AnimationClip は利用者のデータを持てないので .meta の userData に持つ ----

        [System.Serializable]
        public sealed class Group
        {
            public string name;
            /// <summary>組に入れる対象（PoseTarget.label）</summary>
            public List<string> targets = new List<string> ();
        }

        [System.Serializable]
        public sealed class Settings
        {
            /// <summary>自分で作った組（組み込みの組 All / Body / Hands / Rig は持たない）</summary>
            public List<Group> groups = new List<Group> ();
            public string selected = "All";
            /// <summary>前の Loop（組ごとに先頭のポーズキーを末尾へ写した）。読むときにサイクルキーの印へ移し、false にする（ClipUserData.Load）</summary>
            public bool loop;
            public bool ghost;
            /// <summary>残像を出す幅（前後それぞれ何フレームまで）</summary>
            public int ghostRange = kGhostRangeDefault;
        }

        public const int kGhostRangeDefault = 2;
        public const int kGhostRangeMax = 16;

        /// <summary>1〜16 に収める（古い設定で 0 のときは既定）</summary>
        public static int ClampGhostRange (int range)
        {
            return range <= 0 ? kGhostRangeDefault : Mathf.Min (range, kGhostRangeMax);
        }

        public static Settings Load (AnimationClip clip)
        {
            return ClipUserData.Load (clip).stacker ?? new Settings ();
        }

        /// <summary>
        /// 保存する（サイクルキーの印など、.meta のほかの項目は残す）。アセットでないクリップには保存しない（false）
        /// </summary>
        public static bool Save (AnimationClip clip, Settings settings)
        {
            return ClipUserData.Save (clip, data => data.stacker = settings);
        }
    }

}
