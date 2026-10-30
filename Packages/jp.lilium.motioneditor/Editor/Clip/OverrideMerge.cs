using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Override を元のクリップへ焼き込む（統合。S14）。
    /// 合成した値をフレームごとに全部打ってから、無くても同じになるキーを削る
    /// </summary>
    public static class OverrideMerge
    {
        /// <summary>
        /// 無くても同じ形になるキーを削る。戻り値は削ったキーの数。
        /// キーを外して、その時刻の値が元の曲線から tolerance 以上ずれなければ、そのキーは要らない
        /// </summary>
        public static int ReduceKeys (AnimationClip clip, float tolerance = 1e-4f)
        {
            if (clip == null) return 0;
            int removed = 0;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (clip);
            List<EditorCurveBinding> changedBindings = new List<EditorCurveBinding> ();
            List<AnimationCurve> changedCurves = new List<AnimationCurve> ();
            foreach (EditorCurveBinding binding in bindings) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || curve.length < 3) continue;
                int before = curve.length;
                AnimationCurve reduced = Reduce (curve, tolerance);
                if (reduced.length == before) continue;
                removed += before - reduced.length;
                changedBindings.Add (binding);
                changedCurves.Add (reduced);
            }
            if (changedBindings.Count > 0) {
                AnimationUtility.SetEditorCurves (clip, changedBindings.ToArray (), changedCurves.ToArray ());
            }
            return removed;
        }

        /// <summary>
        /// 端のキーは残し、間のキーを前から順に「外しても形が変わらないか」で見ていく。
        /// 外した後の前後のキーの間にある元のキーの時刻を全部見直して、元の曲線から tolerance 以内かを確かめる
        /// （外したキーの時刻だけを見ると、前に外したキーの時刻でのずれが見直されず、外すたびに積み重なる。2026-10-10 に 2° ずれた）
        /// </summary>
        internal static AnimationCurve Reduce (AnimationCurve curve, float tolerance)
        {
            Keyframe[] original = curve.keys;
            List<Keyframe> keys = new List<Keyframe> (original);
            int index = 1;
            while (index < keys.Count - 1) {
                Keyframe key = keys[index];
                keys.RemoveAt (index);
                AnimationCurve candidate = new AnimationCurve (keys.ToArray ());
                float from = keys[index - 1].time;
                float to = keys[index].time;
                bool same = true;
                foreach (Keyframe sample in original) {
                    if (sample.time <= from || sample.time >= to) continue;
                    if (Mathf.Abs (candidate.Evaluate (sample.time) - sample.value) <= tolerance) continue;
                    same = false;
                    break;
                }
                if (same) continue;
                keys.Insert (index, key);
                index++;
            }
            return new AnimationCurve (keys.ToArray ());
        }
    }

}
