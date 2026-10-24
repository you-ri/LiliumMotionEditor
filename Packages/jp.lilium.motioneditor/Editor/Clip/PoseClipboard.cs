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

        static readonly float[] pointState_ = new float[BodyPoint.kValueCount];

        /// <param name="filter">写すカーブ（選んだ骨・点のカーブだけ写すとき）。null ならクリップ全体。
        /// 貼るときは写したカーブにだけ打つので、絞って写せば残りの骨は貼り先のまま残る</param>
        /// <param name="root">そのフレームを当てた編集用リグ（窓に出している物）。全身 IK の点で、そのフレームちょうどにキーの無い物は、
        /// 位置と向きをここから読む（null ならカーブの値）</param>
        /// <returns>写したカーブの数</returns>
        public static int Copy (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> filter = null, Transform root = null)
        {
            entries_.Clear ();
            if (clip == null) return 0;

            float time = frame / clip.frameRate;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (clip);
            HashSet<string> pointPaths = new HashSet<string> ();
            foreach (EditorCurveBinding binding in bindings) {
                if (binding.type == typeof (BodyPoint)) pointPaths.Add (binding.path);
            }
            foreach (EditorCurveBinding binding in bindings) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || curve.length == 0) continue;
                float value = curve.Evaluate (time);
                if (pointPaths.Contains (binding.path)) value = PointValue (clip, binding, time, root, value);
                entries_.Add (new Entry { binding = binding, value = value });
            }
            return entries_.Count;
        }

        /// <summary>
        /// 全身 IK の点のカーブの、窓に出している値（EditingClip.GetPointState）。点のカーブは補間して読まない:
        /// 状態（固定の強さ・Locked）は、その時刻以前でいちばん近いキーの値（最初のキーより前は 0 = Free）。
        /// カーブのまま読むと、最初のキーより前のフレームで最初のキーの状態と場所を写してしまい、
        /// 貼った先で点がそのキーの場所に固定されていた（10-24 ユーザー報告。右膝の点が後ろのキーの場所に出た）。
        /// 位置と向きは、その時刻ちょうどにキーがあればキーの値。無ければ、リグに出している場所（引き継いだ点は骨の位置）
        /// </summary>
        static float PointValue (AnimationClip clip, EditorCurveBinding binding, float time, Transform root, float value)
        {
            bool keyed;
            EditingClip.GetPointState (clip, binding.path, time, pointState_, out keyed);
            if (binding.type == typeof (BodyPoint)) {
                int index = BodyPoint.IndexOf (binding.propertyName);
                return index >= 0 ? pointState_[index] : value;
            }
            float shown;
            if (keyed || root == null || binding.type != typeof (Transform)) return value;
            return AnimationUtility.GetFloatValue (root.gameObject, binding, out shown) ? shown : value;
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
