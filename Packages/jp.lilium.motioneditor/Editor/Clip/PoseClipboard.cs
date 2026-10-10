using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// ポーズのコピーと貼り付け。クリップの 1 フレーム分の値（カーブごと）を持ち、別のフレームや別のクリップへ打つ。
    /// つかむ対象の種類に依らないので、コントローラーではなくクリップで扱う
    /// </summary>
    static class PoseClipboard
    {
        struct Entry
        {
            public EditorCurveBinding binding;
            public float value;
        }

        static readonly List<Entry> entries_ = new List<Entry> ();

        public static bool isEmpty
        {
            get { return entries_.Count == 0; }
        }

        /// <param name="filter">写すカーブ（選んだ骨・点のカーブだけ写すとき）。null ならクリップ全体。
        /// 貼るときは写したカーブにだけ打つので、絞って写せば残りの骨は貼り先のまま残る</param>
        /// <returns>写したカーブの数</returns>
        public static int Copy (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> filter = null)
        {
            entries_.Clear ();
            if (clip == null) return 0;

            float time = frame / clip.frameRate;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || curve.length == 0) continue;
                entries_.Add (new Entry { binding = binding, value = curve.Evaluate (time) });
            }
            return entries_.Count;
        }

        /// <summary>
        /// コピーした値を、writer のクリップとフレームにキーとして打つ。貼り先にまだ無いカーブも作る（別のクリップへ貼ったときに欠けないように）
        /// </summary>
        public static void Paste (CurveWriter writer)
        {
            foreach (Entry entry in entries_) {
                writer.Set (entry.binding, entry.value);
            }
        }
    }

}
