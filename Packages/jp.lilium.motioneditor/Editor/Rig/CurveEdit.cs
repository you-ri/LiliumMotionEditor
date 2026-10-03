using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// カーブエディタ（S5）の書き換え。数値のカーブはキーをそのまま動かす。
    /// 回転は保存が四元数（m_LocalRotation.x/y/z/w の 4 本）なので、画面では角度に直して見せ、キーの値を変えるときは 4 本のキーを同じフレームに書き直す。
    /// 接線の種類は数値のカーブだけ（回転は 4 本の補間なので角度の接線は持てない）
    /// </summary>
    public static class CurveEdit
    {
        public const string kRotation = "m_LocalRotation";
        static readonly string[] kAxes = { "x", "y", "z", "w" };

        public static bool IsRotation (EditorCurveBinding binding)
        {
            return binding.type == typeof (Transform) && binding.propertyName.StartsWith (kRotation + ".");
        }

        public static EditorCurveBinding RotationBinding (string path, int component)
        {
            return EditorCurveBinding.FloatCurve (path, typeof (Transform), kRotation + "." + kAxes[component]);
        }

        /// <summary>
        /// その時刻の回転（4 本のカーブから。無い成分は単位回転の値）
        /// </summary>
        public static Quaternion EvaluateRotation (AnimationClip clip, string path, float time)
        {
            float[] q = { 0, 0, 0, 1 };
            for (int i = 0; i < 4; i++) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, RotationBinding (path, i));
                if (curve != null && curve.length > 0) q[i] = curve.Evaluate (time);
            }
            Quaternion rotation = new Quaternion (q[0], q[1], q[2], q[3]);
            float length = Mathf.Sqrt (Quaternion.Dot (rotation, rotation));
            return length > 1e-6f ? new Quaternion (q[0] / length, q[1] / length, q[2] / length, q[3] / length) : Quaternion.identity;
        }

        /// <summary>
        /// frame のキーの値を value にする（無ければ足す）。接線の種類は保ち、自動の接線は付け直す
        /// </summary>
        public static void SetKey (AnimationClip clip, EditorCurveBinding binding, int frame, float value)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding) ?? new AnimationCurve ();
            SetKey (curve, clip.frameRate, frame, value);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
        }

        static void SetKey (AnimationCurve curve, float rate, int frame, float value)
        {
            int index = ClipKeyUtility.FindKeyAtFrame (curve, rate, frame);
            if (index >= 0) {
                Keyframe key = curve.keys[index];
                key.value = value;
                curve.MoveKey (index, key);
            }
            else {
                index = curve.AddKey (new Keyframe (frame / rate, value));
                AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
            }
            RefreshTangents (curve);
        }

        /// <summary>
        /// frame に回転のキーを打つ（4 本とも）。補間で遠回りしないよう、前後のキーと同じ側の四元数にそろえる
        /// </summary>
        public static void SetRotationKey (AnimationClip clip, string path, int frame, Quaternion rotation)
        {
            Quaternion reference = EvaluateRotation (clip, path, frame / clip.frameRate);
            if (Quaternion.Dot (reference, rotation) < 0) rotation = new Quaternion (-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            float[] q = { rotation.x, rotation.y, rotation.z, rotation.w };
            for (int i = 0; i < 4; i++) SetKey (clip, RotationBinding (path, i), frame, q[i]);
        }

        /// <summary>
        /// 回転のキーの 1 軸だけを角度で変える（ほかの 2 軸は今の見え方のまま）
        /// </summary>
        /// <param name="hint">今見せている角度（同じ回転の表し方のうち、これに近いものを直す）</param>
        public static void SetRotationAngle (AnimationClip clip, string path, int frame, int axis, float degrees, Vector3 hint)
        {
            Vector3 euler = EulerAngles.Closest (EvaluateRotation (clip, path, frame / clip.frameRate), hint);
            euler[axis] = degrees;
            SetRotationKey (clip, path, frame, Quaternion.Euler (euler));
        }

        /// <summary>
        /// 数値のカーブの frame のキーの接線の種類を、両側そろえて変える
        /// </summary>
        public static bool SetTangentMode (AnimationClip clip, EditorCurveBinding binding, int frame, AnimationUtility.TangentMode mode)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            if (curve == null) return false;
            int index = ClipKeyUtility.FindKeyAtFrame (curve, clip.frameRate, frame);
            if (index < 0) return false;
            AnimationUtility.SetKeyBroken (curve, index, false);
            AnimationUtility.SetKeyLeftTangentMode (curve, index, mode);
            AnimationUtility.SetKeyRightTangentMode (curve, index, mode);
            RefreshTangents (curve);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
            return true;
        }

        /// <summary>
        /// time のキー（無ければ time を挟む 2 つ）と、その両隣の接線を自動（ClampedAuto）にしてから、自動の接線を付け直す。
        /// キーを打つ・動かす・消す・貼ったときに呼ぶ。固定の接線（Free）は、そのときの両隣から計算した傾きのまま残るので、
        /// 後で間のキーを消したり、キーを遠くへ動かしたりすると、キーの間で値が大きく行き過ぎる（2 フレーム間隔の傾きが 1.5 秒続くなど）。
        /// 接線を手で決める操作は無いので、Free は打ったときの名残りとして自動にする（Constant・Linear などはそのまま）
        /// </summary>
        public static void AutoTangentsNear (AnimationCurve curve, float time)
        {
            int after = 0;
            while (after < curve.length && curve[after].time < time - 1e-5f) after++;
            for (int i = after - 2; i <= after + 1; i++) {
                if (i < 0 || i >= curve.length) continue;
                if (AnimationUtility.GetKeyLeftTangentMode (curve, i) == AnimationUtility.TangentMode.Free) {
                    AnimationUtility.SetKeyLeftTangentMode (curve, i, AnimationUtility.TangentMode.ClampedAuto);
                }
                if (AnimationUtility.GetKeyRightTangentMode (curve, i) == AnimationUtility.TangentMode.Free) {
                    AnimationUtility.SetKeyRightTangentMode (curve, i, AnimationUtility.TangentMode.ClampedAuto);
                }
            }
            RefreshTangents (curve);
        }

        /// <summary>
        /// 自動の接線（Auto・ClampedAuto・Linear など）を、今のキーの並びで付け直す（値や時刻を動かした後）
        /// </summary>
        public static void RefreshTangents (AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++) {
                AnimationUtility.TangentMode left = AnimationUtility.GetKeyLeftTangentMode (curve, i);
                AnimationUtility.TangentMode right = AnimationUtility.GetKeyRightTangentMode (curve, i);
                if (left != AnimationUtility.TangentMode.Free) AnimationUtility.SetKeyLeftTangentMode (curve, i, left);
                if (right != AnimationUtility.TangentMode.Free) AnimationUtility.SetKeyRightTangentMode (curve, i, right);
            }
        }

        /// <summary>
        /// frame のキーを消す（回転のカーブなら 4 本とも）
        /// </summary>
        public static void RemoveKey (AnimationClip clip, EditorCurveBinding binding, int frame)
        {
            if (IsRotation (binding)) {
                for (int i = 0; i < 4; i++) RemoveKeyOf (clip, RotationBinding (binding.path, i), frame);
                return;
            }
            RemoveKeyOf (clip, binding, frame);
        }

        static void RemoveKeyOf (AnimationClip clip, EditorCurveBinding binding, int frame)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            if (curve == null) return;
            int index = ClipKeyUtility.FindKeyAtFrame (curve, clip.frameRate, frame);
            if (index < 0) return;
            curve.RemoveKey (index);
            AutoTangentsNear (curve, frame / clip.frameRate);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
        }

        /// <summary>
        /// frame のキーを movedFrame へ動かし、値を value にする（回転なら 4 本とも動かし、値は変えない）。行き先のキーは上書き
        /// </summary>
        public static void MoveKey (AnimationClip clip, EditorCurveBinding binding, int frame, int movedFrame, float? value)
        {
            if (IsRotation (binding)) {
                for (int i = 0; i < 4; i++) MoveKeyOf (clip, RotationBinding (binding.path, i), frame, movedFrame, null);
                return;
            }
            MoveKeyOf (clip, binding, frame, movedFrame, value);
        }

        static void MoveKeyOf (AnimationClip clip, EditorCurveBinding binding, int frame, int movedFrame, float? value)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            if (curve == null) return;
            float rate = clip.frameRate;
            int index = ClipKeyUtility.FindKeyAtFrame (curve, rate, frame);
            if (index < 0) return;
            Keyframe key = curve.keys[index];
            if (movedFrame != frame) {
                int overwritten = ClipKeyUtility.FindKeyAtFrame (curve, rate, movedFrame);
                if (overwritten >= 0) {
                    curve.RemoveKey (overwritten);
                    if (overwritten < index) index--;
                }
                key.time = movedFrame / rate;
            }
            if (value.HasValue) key.value = value.Value;
            curve.MoveKey (index, key);
            AutoTangentsNear (curve, frame / rate);
            AutoTangentsNear (curve, movedFrame / rate);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
        }
    }

}
