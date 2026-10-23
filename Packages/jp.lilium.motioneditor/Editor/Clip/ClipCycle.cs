using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// サイクルキー（akeytsu のサイクルマーカー）。クリップの最後のキーに付けると、そのキーをいつも先頭のキーと同じ姿勢に保ち、
    /// ループのつなぎ目で姿勢も動きの速さも途切れないようにする。
    /// - 最後のキー（どれかのカーブのキーのうち、いちばん後ろのフレーム）へ、全部のカーブの先頭のキーを写す。
    ///   先頭にキーの無いカーブは先頭の時刻の値を写す（無いままだと、最後のキーの時刻で値が先頭と違う）
    /// - 自動の接線は、つなぎ目をまたいで付ける。先頭のキーは最後のキーの 1 つ前のキーを前の隣として見て、先頭と最後で同じ傾きにする。
    ///   片側だけの自動の接線（端は平ら）のままだと、つなぎ目で一度止まる
    /// - 回転（四元数）は、最後のキーの 1 つ前のキーと同じ側（内積が正）にそろえて写す。逆の側のまま写すと補間が遠回りする
    /// 印はクリップごとに .meta に持つ（ClipUserData）。印は「最後のキー」に付くので、最後のキーを動かせば印も付いて行く
    /// </summary>
    public static class ClipCycle
    {
        /// <summary>同じ値・傾きとみなす差（書いたものを読み直したときの誤差で、書き直しを繰り返さないように）</summary>
        const float kTolerance = 1e-5f;

        public static bool Load (AnimationClip clip)
        {
            return ClipUserData.Load (clip).cycle;
        }

        /// <summary>
        /// 保存する（Stacker の設定など、.meta のほかの項目は残す）。アセットでないクリップには保存しない（false）
        /// </summary>
        public static bool Save (AnimationClip clip, bool cycle)
        {
            return ClipUserData.Save (clip, data => data.cycle = cycle);
        }

        /// <summary>クリップのキーのフレーム（0 以上。昇順）</summary>
        public static int[] GetFrames (AnimationClip clip)
        {
            if (clip == null) return new int[0];
            List<int> frames = new List<int> ();
            foreach (int frame in ClipKeyUtility.GetKeyFrames (clip)) {
                if (frame >= 0) frames.Add (frame);
            }
            frames.Sort ();
            return frames.ToArray ();
        }

        /// <summary>
        /// 先頭のキーと最後のキー（サイクルキー）のフレーム。キーのあるフレームが 2 つ未満なら false
        /// </summary>
        public static bool TryGetRange (int[] frames, out int first, out int last)
        {
            first = int.MaxValue;
            last = int.MinValue;
            if (frames != null) {
                foreach (int frame in frames) {
                    if (frame < 0) continue;
                    first = Mathf.Min (first, frame);
                    last = Mathf.Max (last, frame);
                }
            }
            if (first < last) return true;
            first = last = -1;
            return false;
        }

        public static bool TryGetRange (AnimationClip clip, out int first, out int last)
        {
            return TryGetRange (GetFrames (clip), out first, out last);
        }

        /// <summary>
        /// 印を付けたまま合わせてよいか。最後のキーが前に合わせたときより前へ来て、そこに前からキーがあった
        /// （サイクルキーを消した・前のキーより前へ動かした）なら、合わせるとそのキーの姿勢が消えるので false（印を外す）。
        /// 前より後ろへ来た・前に無かったフレームへ来たなら、そこが新しいサイクルキー
        /// </summary>
        /// <param name="previousFrames">前に合わせたときのキーのフレーム（分からなければ null）</param>
        public static bool CanFollow (int[] previousFrames, int last)
        {
            int first, previousLast;
            if (!TryGetRange (previousFrames, out first, out previousLast) || last >= previousLast) return true;
            return System.Array.IndexOf (previousFrames, last) < 0;
        }

        /// <summary>
        /// 最後のキーを先頭のキーに合わせ、つなぎ目の接線を付ける。変わらなければ書かない（書くとキーが変わった知らせが出て、また呼ばれるため）。
        /// Undo の記録は呼ぶ側。書いたら true
        /// </summary>
        public static bool Sync (AnimationClip clip)
        {
            int first, last;
            if (clip == null || !TryGetRange (clip, out first, out last)) return false;
            float rate = clip.frameRate;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (clip);
            Dictionary<string, float> signs = RotationSigns (clip, bindings, rate, first, last);
            List<EditorCurveBinding> written = new List<EditorCurveBinding> ();
            List<AnimationCurve> curves = new List<AnimationCurve> ();
            foreach (EditorCurveBinding binding in bindings) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || curve.length == 0) continue;
                float sign;
                if (!CurveEdit.IsRotation (binding) || !signs.TryGetValue (binding.path, out sign)) sign = 1;
                AnimationCurve synced = new AnimationCurve (curve.keys);
                SyncCurve (synced, rate, first, last, sign);
                if (SameCurve (curve, synced)) continue;
                written.Add (binding);
                curves.Add (synced);
            }
            if (written.Count > 0) AnimationUtility.SetEditorCurves (clip, written.ToArray (), curves.ToArray ());
            return written.Count > 0;
        }

        /// <param name="sign">最後のキーへ写すときに掛ける数（回転の成分で、最後のキーを逆の側の四元数にするとき -1）</param>
        static void SyncCurve (AnimationCurve curve, float rate, int first, int last, float sign)
        {
            int from = ClipKeyUtility.FindKeyAtFrame (curve, rate, first);
            Keyframe end;
            if (from >= 0) {
                end = curve[from];
                end.value *= sign;
                end.inTangent *= sign;
                end.outTangent *= sign;
            }
            else {
                end = new Keyframe (0, curve.Evaluate (first / rate) * sign);
            }
            end.time = last / rate;
            int to = ClipKeyUtility.FindKeyAtFrame (curve, rate, last);
            // 最後の区間の補間（最後のキーの左）は最後のキーのまま（S80。先頭のキーの左は区間を持たないので、つなぎ目の左として最後の区間にそろえる）
            KeyInterpolation lastLeft = to >= 0 ? KeyInterpolations.Side (curve, to, false) : KeyInterpolation.Curve;
            if (to >= 0) curve.MoveKey (to, end);
            else to = curve.AddKey (end);

            // 右（最初の区間）は先頭のキーにそろえる（先頭にキーが無ければ曲線）
            KeyInterpolation firstRight = from >= 0 ? KeyInterpolations.Side (curve, from, true) : KeyInterpolation.Curve;
            if (from >= 0) KeyInterpolations.SetKey (curve, from, lastLeft, firstRight);
            KeyInterpolations.SetKey (curve, to, lastLeft, firstRight);
            CurveEdit.RefreshTangents (curve);
            if (from >= 0) SetLoopTangents (curve, from, to, sign);
        }

        /// <summary>
        /// 先頭のキー（start）と最後のキー（end）の曲線の側の接線を、つなぎ目をまたいだ自動の接線にする（Constant・Linear の側はそのまま）。
        /// 先頭の次のキーと、最後の 1 つ前のキー（先頭の側の四元数へ戻して）を結ぶ傾き。先頭が山・谷なら平ら（ClampedAuto と同じ）
        /// </summary>
        static void SetLoopTangents (AnimationCurve curve, int start, int end, float sign)
        {
            bool left = IsAuto (AnimationUtility.GetKeyLeftTangentMode (curve, start));
            bool right = IsAuto (AnimationUtility.GetKeyRightTangentMode (curve, start));
            if (!left && !right) return;
            float slope = 0;
            if (end - start >= 2) {
                Keyframe next = curve[start + 1];
                Keyframe previous = curve[end - 1];
                float value = curve[start].value;
                float before = previous.value * sign;
                float span = (next.time - curve[start].time) + (curve[end].time - previous.time);
                bool extremum = (next.value - value) * (value - before) <= 0;
                slope = extremum || span <= 0 ? 0 : (next.value - before) / span;
            }
            if (left && right) {
                SetFreeTangent (curve, start, slope);
                SetFreeTangent (curve, end, slope * sign);
                return;
            }
            // 片側だけ曲線（つなぎ目が区間の補間の境目）: 曲線の側だけ傾きを入れる
            SetFreeSide (curve, start, left, slope);
            SetFreeSide (curve, end, left, slope * sign);
        }

        static void SetFreeSide (AnimationCurve curve, int index, bool left, float slope)
        {
            Keyframe key = curve[index];
            if (left) key.inTangent = slope;
            else key.outTangent = slope;
            curve.MoveKey (index, key);
        }

        static bool IsAuto (AnimationUtility.TangentMode mode)
        {
            return mode == AnimationUtility.TangentMode.ClampedAuto || mode == AnimationUtility.TangentMode.Auto || mode == AnimationUtility.TangentMode.Free;
        }

        static void SetFreeTangent (AnimationCurve curve, int index, float slope)
        {
            AnimationUtility.SetKeyBroken (curve, index, false);
            AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.Free);
            AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.Free);
            Keyframe key = curve[index];
            key.inTangent = slope;
            key.outTangent = slope;
            curve.MoveKey (index, key);
        }

        /// <summary>
        /// 回転のパスごとに、最後のキーへ写す四元数の向き（1 か -1）。最後のキーの 1 つ前のキー（4 本のどれかのキー）の四元数と内積が負なら -1
        /// </summary>
        static Dictionary<string, float> RotationSigns (AnimationClip clip, EditorCurveBinding[] bindings, float rate, int first, int last)
        {
            Dictionary<string, float> signs = new Dictionary<string, float> ();
            foreach (EditorCurveBinding binding in bindings) {
                if (!CurveEdit.IsRotation (binding) || signs.ContainsKey (binding.path)) continue;
                AnimationCurve[] parts = new AnimationCurve[4];
                bool complete = true;
                for (int a = 0; a < 4 && complete; a++) {
                    parts[a] = AnimationUtility.GetEditorCurve (clip, CurveEdit.RotationBinding (binding.path, a));
                    complete = parts[a] != null && parts[a].length > 0;
                }
                if (!complete) continue;
                // 最後のキーの 1 つ前のキー（無ければ先頭と同じなので向きはそのまま）
                float previous = -1;
                foreach (AnimationCurve part in parts) {
                    foreach (Keyframe key in part.keys) {
                        int frame = Mathf.RoundToInt (key.time * rate);
                        if (frame < last && frame > first) previous = Mathf.Max (previous, key.time);
                    }
                }
                float dot = 0;
                if (previous >= 0) {
                    for (int a = 0; a < 4; a++) dot += parts[a].Evaluate (first / rate) * parts[a].Evaluate (previous);
                }
                signs[binding.path] = dot < 0 ? -1 : 1;
            }
            return signs;
        }

        static bool SameCurve (AnimationCurve a, AnimationCurve b)
        {
            if (a.length != b.length) return false;
            for (int i = 0; i < a.length; i++) {
                Keyframe x = a[i];
                Keyframe y = b[i];
                if (Mathf.Abs (x.time - y.time) > kTolerance || Mathf.Abs (x.value - y.value) > kTolerance) return false;
                if (!SameSlope (x.inTangent, y.inTangent) || !SameSlope (x.outTangent, y.outTangent)) return false;
                if (AnimationUtility.GetKeyLeftTangentMode (a, i) != AnimationUtility.GetKeyLeftTangentMode (b, i)) return false;
                if (AnimationUtility.GetKeyRightTangentMode (a, i) != AnimationUtility.GetKeyRightTangentMode (b, i)) return false;
            }
            return true;
        }

        static bool SameSlope (float a, float b)
        {
            if (float.IsInfinity (a) || float.IsInfinity (b)) return a == b;
            return Mathf.Abs (a - b) <= kTolerance * Mathf.Max (1, Mathf.Abs (a));
        }
    }

}
