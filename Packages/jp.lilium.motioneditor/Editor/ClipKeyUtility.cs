using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{
    /// <summary>
    /// クリップのキーの一覧・移動・削除（タイムライン用）。キーの書き込みは CurveWriter
    /// </summary>
    public static class ClipKeyUtility
    {
        /// <summary>
        /// キーのあるフレーム（昇順・重複なし）
        /// </summary>
        /// <param name="filter">対象のカーブ。null なら全部</param>
        public static int[] GetKeyFrames (AnimationClip clip, System.Predicate<EditorCurveBinding> filter = null)
        {
            SortedSet<int> frames = new SortedSet<int> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                foreach (Keyframe key in AnimationUtility.GetEditorCurve (clip, binding).keys) {
                    frames.Add (Mathf.RoundToInt (key.time * clip.frameRate));
                }
            }
            return frames.ToArray ();
        }

        /// <summary>
        /// frame にあるキーを movedFrame へ動かす。copy なら元のキーを残す。行き先にあったキーは上書きする
        /// </summary>
        /// <param name="filter">対象のカーブ。null なら全部</param>
        public static void MoveKeysAtFrame (AnimationClip clip, int frame, int movedFrame, bool copy, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (frame == movedFrame) return;

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                int index = FindKeyAtFrame (curve, clip.frameRate, frame);
                if (index < 0) continue;

                Keyframe key = curve.keys[index];
                key.time = movedFrame / clip.frameRate;
                int overwritten = FindKeyAtFrame (curve, clip.frameRate, movedFrame);
                if (overwritten >= 0) {
                    curve.RemoveKey (overwritten);
                    if (overwritten < index) index--;
                }
                if (copy) {
                    curve.AddKey (key);
                }
                else {
                    curve.MoveKey (index, key);
                    CurveEdit.AutoTangentsNear (curve, frame / clip.frameRate);
                }
                CurveEdit.AutoTangentsNear (curve, key.time);
                AnimationUtility.SetEditorCurve (clip, binding, curve);
            }
        }

        /// <summary>
        /// frame にあるキーを消す
        /// </summary>
        /// <param name="filter">対象のカーブ。null なら全部</param>
        public static void RemoveKeysAtFrame (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> filter = null)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                int index = FindKeyAtFrame (curve, clip.frameRate, frame);
                if (index < 0) continue;

                curve.RemoveKey (index);
                CurveEdit.AutoTangentsNear (curve, frame / clip.frameRate);
                AnimationUtility.SetEditorCurve (clip, binding, curve);
            }
        }

        /// <summary>
        /// 対象のカーブのキーを、keepFrame の 1 つだけにする（値はそのフレームの値）。keepFrame が負ならカーブごと消す。
        /// 読込で毎フレームに入ったキーを、指だけ打ち直すときなどに使う
        /// </summary>
        /// <param name="filter">対象のカーブ。null なら全部</param>
        /// <returns>変えたカーブの数</returns>
        public static int KeepOnlyKeyAtFrame (AnimationClip clip, int keepFrame, System.Predicate<EditorCurveBinding> filter = null)
        {
            int changed = 0;
            float time = keepFrame / clip.frameRate;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                if (keepFrame < 0) {
                    AnimationUtility.SetEditorCurve (clip, binding, null);
                    changed++;
                    continue;
                }
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve.length == 1 && FindKeyAtFrame (curve, clip.frameRate, keepFrame) == 0) continue;
                AnimationUtility.SetEditorCurve (clip, binding, new AnimationCurve (new Keyframe (time, curve.Evaluate (time))));
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// afterFrame より後ろのキーを delta フレームずらす（Stacker の挿入・削除・間隔の変更）。ずらした先が afterFrame 以下になるキーは作らない
        /// </summary>
        /// <param name="filter">対象のカーブ。null なら全部</param>
        public static void ShiftKeysAfter (AnimationClip clip, int afterFrame, int delta, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (delta == 0) return;
            float rate = clip.frameRate;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                Keyframe[] keys = curve.keys;
                bool changed = false;
                for (int i = 0; i < keys.Length; i++) {
                    int frame = Mathf.RoundToInt (keys[i].time * rate);
                    if (frame <= afterFrame) continue;
                    keys[i].time = Mathf.Max (afterFrame + 1, frame + delta) / rate;
                    changed = true;
                }
                if (!changed) continue;
                // 詰めたときに同じフレームへ重なったキーは、後ろのものを残す
                List<Keyframe> merged = new List<Keyframe> ();
                foreach (Keyframe key in keys.OrderBy (k => k.time)) {
                    if (merged.Count > 0 && Mathf.RoundToInt (merged[merged.Count - 1].time * rate) == Mathf.RoundToInt (key.time * rate)) {
                        merged[merged.Count - 1] = key;
                    }
                    else {
                        merged.Add (key);
                    }
                }
                curve.keys = merged.ToArray ();
                AnimationUtility.SetEditorCurve (clip, binding, curve);
            }
        }

        internal static int FindKeyAtFrame (AnimationCurve curve, float frameRate, int frame)
        {
            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++) {
                if (Mathf.RoundToInt (keys[i].time * frameRate) == frame) return i;
            }
            return -1;
        }
    }

}
