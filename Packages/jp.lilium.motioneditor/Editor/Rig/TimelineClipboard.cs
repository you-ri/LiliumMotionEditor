using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// タイムラインで選んだキーのコピーと貼り付け（Ctrl+C / Ctrl+V）。
    /// キーはカーブ（EditorCurveBinding）ごとに、選んだ中で一番前のフレームからのずれで持つ。貼るときは今のフレームを先頭にして、
    /// 行き先にあるキーは上書きする。キーは接線ごとそのまま写す（MoveKeysAtFrame のコピーと同じ）。
    /// 別のクリップにも貼れる（同じパスのカーブが無ければ作る）
    /// </summary>
    public static class TimelineClipboard
    {
        struct Entry
        {
            public EditorCurveBinding binding;
            public int offset;
            public Keyframe key;
        }

        static readonly List<Entry> entries_ = new List<Entry> ();

        public static bool isEmpty
        {
            get { return entries_.Count == 0; }
        }

        /// <summary>
        /// 選んだキーを覚える。keys はフレームとトラックのカーブの絞り込み（null ならクリップ全体）。同じカーブの同じフレームは 1 つにまとめる
        /// </summary>
        /// <returns>覚えたキーの数（カーブ単位）</returns>
        public static int Copy (AnimationClip clip, IList<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys)
        {
            entries_.Clear ();
            if (clip == null || keys == null || keys.Count == 0) return 0;

            int first = int.MaxValue;
            foreach (KeyValuePair<int, System.Predicate<EditorCurveBinding>> key in keys) first = Mathf.Min (first, key.Key);

            HashSet<(EditorCurveBinding, int)> seen = new HashSet<(EditorCurveBinding, int)> ();
            float rate = clip.frameRate;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                AnimationCurve curve = null;
                foreach (KeyValuePair<int, System.Predicate<EditorCurveBinding>> key in keys) {
                    if (key.Value != null && !key.Value (binding)) continue;
                    if (!seen.Add ((binding, key.Key))) continue;
                    if (curve == null) curve = AnimationUtility.GetEditorCurve (clip, binding);
                    int index = ClipKeyUtility.FindKeyAtFrame (curve, rate, key.Key);
                    if (index < 0) continue;
                    entries_.Add (new Entry { binding = binding, offset = key.Key - first, key = curve.keys[index] });
                }
            }
            return entries_.Count;
        }

        /// <summary>
        /// 覚えたキーを frame を先頭にして貼る。行き先にあるキーは上書きする
        /// </summary>
        /// <returns>貼ったキーの数（カーブ単位）</returns>
        public static int Paste (AnimationClip clip, int frame)
        {
            if (clip == null || entries_.Count == 0) return 0;

            float rate = clip.frameRate;
            Dictionary<EditorCurveBinding, AnimationCurve> curves = new Dictionary<EditorCurveBinding, AnimationCurve> ();
            int count = 0;
            foreach (Entry entry in entries_) {
                int target = frame + entry.offset;
                if (target < 0) continue;
                AnimationCurve curve;
                if (!curves.TryGetValue (entry.binding, out curve)) {
                    curve = AnimationUtility.GetEditorCurve (clip, entry.binding) ?? new AnimationCurve ();
                    curves.Add (entry.binding, curve);
                }
                int existing = ClipKeyUtility.FindKeyAtFrame (curve, rate, target);
                if (existing >= 0) curve.RemoveKey (existing);
                Keyframe key = entry.key;
                key.time = target / rate;
                curve.AddKey (key);
                count++;
            }
            foreach (KeyValuePair<EditorCurveBinding, AnimationCurve> pair in curves) {
                // 貼ったキーの接線は、貼る前の場所の両隣で計算した物なので付け直す
                foreach (Entry entry in entries_) {
                    if (entry.binding == pair.Key && frame + entry.offset >= 0) CurveEdit.AutoTangentsNear (pair.Value, (frame + entry.offset) / rate);
                }
                AnimationUtility.SetEditorCurve (clip, pair.Key, pair.Value);
            }
            return count;
        }

        /// <summary>覚えたキーを捨てる（テスト用）</summary>
        public static void Clear ()
        {
            entries_.Clear ();
        }
    }

}
