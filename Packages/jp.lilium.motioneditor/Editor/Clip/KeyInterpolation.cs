using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>キーから次のキーまで（区間）の補間（S80）</summary>
    public enum KeyInterpolation
    {
        /// <summary>なめらかな曲線（ClampedAuto。キーを打ったときの既定）</summary>
        Curve,
        /// <summary>等速の直線</summary>
        Linear,
        /// <summary>次のキーの直前まで前のキーの値（止める）</summary>
        Constant,
    }

    /// <summary>
    /// 区間の補間を接線で持つ（S80。保存を増やさない）。区間の補間は、前のキーの右の接線と次のキーの左の接線で決まる。
    /// Unity の Auto・ClampedAuto はキー全体（両側）のモードで、片側だけ自動にすると反対側の傾きも自動の値で上書きされる
    /// （左を曲線・右をリニアにしたキーは、右の傾きが直線にならない。10-23 実測）。
    /// そこで、片側がリニア・一定のキーの曲線の側は Free（固定の傾き）にして、傾きはこちらで ClampedAuto と同じ値に付け直す（「管理する Free」）。
    /// 区間はクリップのキーのフレームの並び（ClipCycle.GetFrames）の隣り合う 2 つ。点の状態（Pin・Locked）のカーブは段差で接線を使わないので対象外、
    /// 持ち替えのカーブ（重み・握り）は段差を作っているので呼ぶ側が外す
    /// </summary>
    public static class KeyInterpolations
    {
        /// <summary>区間（start のキーから end のキーまで）と、その補間（混在なら null）</summary>
        public struct Segment
        {
            public int start;
            public int end;
            public KeyInterpolation? mode;
        }

        /// <summary>キーの片側の補間（Linear・Constant 以外は曲線）</summary>
        public static KeyInterpolation Side (AnimationCurve curve, int index, bool right)
        {
            AnimationUtility.TangentMode mode = right ? AnimationUtility.GetKeyRightTangentMode (curve, index) : AnimationUtility.GetKeyLeftTangentMode (curve, index);
            return mode == AnimationUtility.TangentMode.Linear ? KeyInterpolation.Linear
                : mode == AnimationUtility.TangentMode.Constant ? KeyInterpolation.Constant
                : KeyInterpolation.Curve;
        }

        /// <summary>
        /// 1 本のカーブの、index のキーから次のキーまでの補間。両端がそろっていなければ null（混在）。
        /// どちらかの端が一定なら一定（Unity はどちらかの傾きが無限なら段差にする）
        /// </summary>
        public static KeyInterpolation? Get (AnimationCurve curve, int index)
        {
            if (curve == null || index < 0 || index + 1 >= curve.length) return null;
            KeyInterpolation from = Side (curve, index, true);
            KeyInterpolation to = Side (curve, index + 1, false);
            if (from == KeyInterpolation.Constant || to == KeyInterpolation.Constant) return KeyInterpolation.Constant;
            return from == to ? from : (KeyInterpolation?) null;
        }

        /// <summary>1 本のカーブの、index のキーから次のキーまでを mode にする（両端のキーの片側ずつ）</summary>
        public static void Set (AnimationCurve curve, int index, KeyInterpolation mode)
        {
            if (curve == null || index < 0 || index + 1 >= curve.length) return;
            SetKey (curve, index, Side (curve, index, false), mode);
            SetKey (curve, index + 1, mode, Side (curve, index + 1, true));
            CurveEdit.RefreshTangents (curve);
        }

        /// <summary>
        /// キーの左右の補間を決める。両側が曲線なら ClampedAuto（壊れていないキー）、片側がリニア・一定なら曲線の側は管理する Free
        /// </summary>
        public static void SetKey (AnimationCurve curve, int index, KeyInterpolation left, KeyInterpolation right)
        {
            if (left == KeyInterpolation.Curve && right == KeyInterpolation.Curve) {
                // 両側が Free（サイクルのつなぎ目の傾き。ClipCycle.SetLoopTangents）ならそのまま
                if (IsFree (curve, index, false) && IsFree (curve, index, true)) return;
                AnimationUtility.SetKeyBroken (curve, index, false);
                AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                return;
            }
            AnimationUtility.SetKeyBroken (curve, index, true);
            AnimationUtility.SetKeyLeftTangentMode (curve, index, ToMode (left));
            AnimationUtility.SetKeyRightTangentMode (curve, index, ToMode (right));
            UpdateManagedSlope (curve, index);
        }

        static AnimationUtility.TangentMode ToMode (KeyInterpolation mode)
        {
            return mode == KeyInterpolation.Linear ? AnimationUtility.TangentMode.Linear
                : mode == KeyInterpolation.Constant ? AnimationUtility.TangentMode.Constant
                : AnimationUtility.TangentMode.Free;
        }

        static bool IsFree (AnimationCurve curve, int index, bool right)
        {
            return (right ? AnimationUtility.GetKeyRightTangentMode (curve, index) : AnimationUtility.GetKeyLeftTangentMode (curve, index)) == AnimationUtility.TangentMode.Free;
        }

        /// <summary>片側が Free で、反対側がリニア・一定（区間の境目のキーの曲線の側）か</summary>
        public static bool IsManagedFree (AnimationCurve curve, int index, bool right)
        {
            return IsFree (curve, index, right) && Side (curve, index, !right) != KeyInterpolation.Curve;
        }

        /// <summary>管理する Free の側の傾きを、そのキーが両側 ClampedAuto だったときの傾きに付け直す</summary>
        public static void UpdateManagedSlope (AnimationCurve curve, int index)
        {
            bool left = IsManagedFree (curve, index, false);
            bool right = IsManagedFree (curve, index, true);
            if (!left && !right) return;
            float slope = AutoSlope (curve, index);
            Keyframe key = curve[index];
            if (left) key.inTangent = slope;
            if (right) key.outTangent = slope;
            curve.MoveKey (index, key);
        }

        /// <summary>index のキーを両側 ClampedAuto にしたときの傾き（ClampedAuto の傾きは両隣の時刻と値だけで決まる）</summary>
        public static float AutoSlope (AnimationCurve curve, int index)
        {
            AnimationCurve copy = new AnimationCurve (curve.keys);
            AnimationUtility.SetKeyBroken (copy, index, false);
            AnimationUtility.SetKeyLeftTangentMode (copy, index, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode (copy, index, AnimationUtility.TangentMode.ClampedAuto);
            return copy[index].outTangent;
        }

        /// <summary>
        /// 区間の中に足したキーは、区間の補間を受け継ぐ: 両隣の区間の端（前のキーの右・次のキーの左）がそろってリニアか一定で、
        /// このキーが両側とも曲線（打ったときの既定）なら、両側をそれにする。キーを打つ・動かす・貼ったときに（CurveEdit.AutoTangentsNear から）呼ぶ
        /// </summary>
        public static void Inherit (AnimationCurve curve, int index)
        {
            if (index <= 0 || index + 1 >= curve.length) return;
            if (Side (curve, index, false) != KeyInterpolation.Curve || Side (curve, index, true) != KeyInterpolation.Curve) return;
            KeyInterpolation before = Side (curve, index - 1, true);
            if (before == KeyInterpolation.Curve || before != Side (curve, index + 1, false)) return;
            SetKey (curve, index, before, before);
        }

        /// <summary>区間の補間を持つカーブか（編集用リグのカーブ。点の状態（Pin・Locked・固定の強さ）は段差で接線を使わないので外す）</summary>
        public static bool IsTarget (EditorCurveBinding binding)
        {
            return EditingClip.IsRigBinding (binding) && binding.type != typeof (BodyPoint);
        }

        /// <summary>
        /// クリップの frame のキーから次のキーまでの区間（frame はクリップのキーのフレーム）。最後のキーなら false
        /// </summary>
        public static bool TryGetSegment (AnimationClip clip, int frame, out int start, out int end)
        {
            int[] frames = ClipCycle.GetFrames (clip);
            int index = System.Array.IndexOf (frames, frame);
            start = frame;
            end = index >= 0 && index + 1 < frames.Length ? frames[index + 1] : -1;
            return end > start;
        }

        /// <summary>
        /// クリップの frame のキーから次のキーまでの補間。区間に掛かる対象のカーブの補間がそろっていなければ null（混在）。
        /// 区間の両端にキーの無いカーブは、区間を含む自分の区間の補間で見る。対象のカーブが無ければ曲線
        /// </summary>
        /// <param name="exclude">外すカーブ（持ち替えの重み・握りなど）</param>
        public static KeyInterpolation? GetSegment (AnimationClip clip, int frame, System.Predicate<EditorCurveBinding> exclude = null)
        {
            if (clip == null || !TryGetSegment (clip, frame, out int start, out int end)) return null;
            float rate = clip.frameRate;
            KeyInterpolation? result = null;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!IsTarget (binding) || exclude != null && exclude (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                int index = SegmentIndex (curve, rate, start, end);
                if (index < 0) continue;
                KeyInterpolation? mode = Get (curve, index);
                if (mode == null) return null;
                if (result == null) result = mode;
                else if (result != mode) return null;
            }
            return result ?? KeyInterpolation.Curve;
        }

        /// <summary>カーブの、[start, end] を含む区間の前のキー（区間がカーブのキーの範囲の外なら -1）</summary>
        static int SegmentIndex (AnimationCurve curve, float rate, int start, int end)
        {
            if (curve == null || curve.length < 2) return -1;
            int index = -1;
            for (int i = 0; i < curve.length; i++) {
                if (Mathf.RoundToInt (curve[i].time * rate) <= start) index = i;
                else break;
            }
            if (index < 0 || index + 1 >= curve.length) return -1;
            return Mathf.RoundToInt (curve[index + 1].time * rate) >= end ? index : -1;
        }

        /// <summary>
        /// クリップの frame のキーから次のキーまでを mode にする。区間に掛かる対象のカーブで、区間の端にキーが無いものには、
        /// その時刻の値でキーを足す（区間が本当にその補間になる。足したキーのまわりの曲線の形は少し変わる）。書いたカーブの数を返す。
        /// Undo は呼ぶ側で（クリップを Undo.RecordObject してから呼ぶ）
        /// </summary>
        public static int SetSegment (AnimationClip clip, int frame, KeyInterpolation mode, System.Predicate<EditorCurveBinding> exclude = null)
        {
            if (clip == null || !TryGetSegment (clip, frame, out int start, out int end)) return 0;
            return SetRange (clip, start, end, mode, null, exclude);
        }

        /// <summary>
        /// start のフレームから end のフレームまでを mode にする（SetSegment と同じ書き方）。include で絞ったカーブだけを書く
        /// （S86。タイムラインの骨のトラックの矢印: その骨のカーブの、その骨のキーの間。その間にキーのあるカーブは無いので、どのカーブも 1 つの区間に収まる）
        /// </summary>
        public static int SetRange (AnimationClip clip, int start, int end, KeyInterpolation mode, System.Predicate<EditorCurveBinding> include, System.Predicate<EditorCurveBinding> exclude = null)
        {
            if (clip == null || end <= start) return 0;
            float rate = clip.frameRate;
            List<EditorCurveBinding> bindings = new List<EditorCurveBinding> ();
            List<AnimationCurve> curves = new List<AnimationCurve> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!IsTarget (binding) || include != null && !include (binding) || exclude != null && exclude (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (SegmentIndex (curve, rate, start, end) < 0) continue;
                int index = EnsureKey (curve, rate, start);
                EnsureKey (curve, rate, end);
                Set (curve, index, mode);
                bindings.Add (binding);
                curves.Add (curve);
            }
            if (bindings.Count > 0) AnimationUtility.SetEditorCurves (clip, bindings.ToArray (), curves.ToArray ());
            return bindings.Count;
        }

        /// <summary>
        /// 外部の動きにする区間（S82）の下の補間を曲線にする（start のキーから次のキーまで）。外部の動きはキーの近くで下の補間へ混ぜるので、
        /// 一定のままだと区間の終わりが前のキーの姿勢へ寄ってから次のキーへ跳び、リニアでも端の速さが合わない（10-25 の不具合
        /// 「一定にしてから外部の動きへ戻すと変になる」）。既に曲線なら何もしない（カーブを書き換えない）。書いたカーブの数を返す。Undo は呼ぶ側で
        /// </summary>
        public static int SetCurveUnderExternal (AnimationClip clip, int start, System.Predicate<EditorCurveBinding> exclude = null)
        {
            foreach (Segment segment in GetSegments (clip, exclude)) {
                if (segment.start != start) continue;
                return segment.mode == KeyInterpolation.Curve ? 0 : SetSegment (clip, start, KeyInterpolation.Curve, exclude);
            }
            return 0;
        }

        /// <summary>frame のキーの番号（無ければその時刻の値で足す。足したキーは曲線）</summary>
        static int EnsureKey (AnimationCurve curve, float rate, int frame)
        {
            int index = ClipKeyUtility.FindKeyAtFrame (curve, rate, frame);
            if (index >= 0) return index;
            index = curve.AddKey (new Keyframe (frame / rate, curve.Evaluate (frame / rate)));
            AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
            return index;
        }

        /// <summary>
        /// クリップの区間の補間を全部（キーのフレームの並びの隣り合う 2 つごと。最後のキーは含まない）
        /// </summary>
        public static List<Segment> GetSegments (AnimationClip clip, System.Predicate<EditorCurveBinding> exclude = null)
        {
            List<Segment> result = new List<Segment> ();
            if (clip == null) return result;
            int[] frames = ClipCycle.GetFrames (clip);
            KeyInterpolation?[] modes = GetRanges (clip, frames, null, exclude);
            for (int s = 0; s < modes.Length; s++) result.Add (new Segment { start = frames[s], end = frames[s + 1], mode = modes[s] });
            return result;
        }

        /// <summary>
        /// フレームの並び（昇順）の隣り合う 2 つの間ごとの補間（混在なら null。掛かる対象のカーブが無ければ曲線）。include で絞ったカーブだけを見る
        /// （タイムラインの骨ごとのトラックの、そのトラックのキーの間）。カーブは 1 回ずつだけ読む
        /// （区間ごとに読むと、読み込んだ密なクリップで区間の数 × カーブの数だけ読むことになる）
        /// </summary>
        public static KeyInterpolation?[] GetRanges (AnimationClip clip, int[] frames, System.Predicate<EditorCurveBinding> include, System.Predicate<EditorCurveBinding> exclude = null)
        {
            int count = frames != null ? Mathf.Max (0, frames.Length - 1) : 0;
            KeyInterpolation?[] modes = new KeyInterpolation?[count];
            if (clip == null || count == 0) return modes;
            float rate = clip.frameRate;
            bool[] mixed = new bool[count];
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!IsTarget (binding) || include != null && !include (binding) || exclude != null && exclude (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || curve.length < 2) continue;
                for (int s = 0; s < count; s++) {
                    if (mixed[s]) continue;
                    int index = SegmentIndex (curve, rate, frames[s], frames[s + 1]);
                    if (index < 0) continue;
                    KeyInterpolation? mode = Get (curve, index);
                    if (mode == null || modes[s] != null && modes[s] != mode) mixed[s] = true;
                    else modes[s] = mode;
                }
            }
            for (int s = 0; s < count; s++) modes[s] = mixed[s] ? null : modes[s] ?? KeyInterpolation.Curve;
            return modes;
        }

        /// <summary>補間の名前（操作口・CLI。curve / linear / constant）</summary>
        public static string ToName (KeyInterpolation? mode)
        {
            return mode == null ? "mixed" : mode.Value.ToString ().ToLowerInvariant ();
        }

        public static bool TryParse (string name, out KeyInterpolation mode)
        {
            return System.Enum.TryParse (name, true, out mode) && System.Enum.IsDefined (typeof (KeyInterpolation), mode);
        }
    }

}
