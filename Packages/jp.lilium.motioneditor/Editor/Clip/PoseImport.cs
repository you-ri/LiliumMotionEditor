using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 取り込みの共通部分。フレームごとに「段の出力を編集用の体に作る（evaluate）→ 骨の姿勢を操作値として捉える（Capture）→
    /// 窓がキーを打つときと同じ値を集める（PoseTarget.WriteKeys）」を回して、まとめて編集用クリップへ書く。
    /// 取り込み元が Generic のクリップ（GenericImport）でも既存の Humanoid のモーション（HumanoidImport）でも、ここは同じ
    /// </summary>
    public static class PoseImport
    {
        public struct Result
        {
            public int frameCount;
            public int curveCount;
            public double milliseconds;
            /// <summary>取り込んだ値で解き直したときの骨の位置のずれの最大（m）</summary>
            public float maxError;
            public List<string> notes;
        }

        public struct Options
        {
            /// <summary>書き出し先（編集用リグのクリップ）</summary>
            public AnimationClip destination;
            public EditingRig rig;
            public EditingRigSolver solver;
            /// <summary>その時刻の姿勢を編集用の体に作る</summary>
            public System.Action<float> evaluate;
            /// <summary>キーを打つ対象（窓と同じもの）</summary>
            public IList<PoseTarget> targets;
            /// <summary>ずれを測る骨（編集用の体の骨）</summary>
            public IList<Transform> checkBones;
            /// <summary>取り込む長さ（秒）</summary>
            public float length;
            public List<string> notes;
        }

        public static Result Run (Options options)
        {
            AnimationClip destination = options.destination;
            EditingRig rig = options.rig;
            EditingRigSolver solver = options.solver;
            if (destination == null) throw new System.ArgumentNullException ("destination");
            if (rig == null || rig.root == null || solver == null) throw new System.InvalidOperationException (Tr ("GENERIC_IMPORT_NO_EDITING_RIG"));

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();
            Result result = new Result { notes = options.notes ?? new List<string> () };
            Transform root = rig.root.transform;
            IList<Transform> bones = options.checkBones ?? new List<Transform> ();

            float rate = CurveEdit.FrameRateOf (destination);
            int frameCount = CurveEdit.FrameCount (options.length, rate);

            FrameValues values = new FrameValues (frameCount);
            Vector3[] expected = new Vector3[bones.Count];

            root.GetLocalPositionAndRotation (out Vector3 rootPosition, out Quaternion rootRotation);
            Vector3 rootScale = root.localScale;
            try {
                for (int f = 0; f < frameCount; f++) {
                    options.evaluate (f / rate);
                    // Animator の GameObject のカーブ（ルートモーション）は取り込まないので、当たっても戻す
                    root.SetLocalPositionAndRotation (rootPosition, rootRotation);
                    root.localScale = rootScale;
                    for (int b = 0; b < bones.Count; b++) expected[b] = root.InverseTransformPoint (bones[b].position);

                    solver.Capture ();

                    values.Capture (f, root, options.targets, rig.rigProxies);

                    // 捉えた値で解き直して、元の姿勢に戻るか
                    rig.ResetBonesToRest ();
                    solver.Solve ();
                    for (int b = 0; b < bones.Count; b++) {
                        result.maxError = Mathf.Max (result.maxError, Vector3.Distance (expected[b], root.InverseTransformPoint (bones[b].position)));
                    }
                }
            }
            finally {
                root.SetLocalPositionAndRotation (rootPosition, rootRotation);
                root.localScale = rootScale;
            }

            values.Write (destination, rate);
            result.frameCount = frameCount;
            result.curveCount = values.curveCount;
            result.milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// フレームごとの今の値を集めて、後で一度にクリップへ書く（取り込み・Override の統合）。
        /// 書きながら同じクリップを読むと差分が二重に乗るので、全フレームぶん集めてから書く
        /// </summary>
        public sealed class FrameValues
        {
            readonly int frameCount_;
            readonly Dictionary<EditorCurveBinding, float[]> values_ = new Dictionary<EditorCurveBinding, float[]> ();
            readonly HashSet<string> rotationPaths_ = new HashSet<string> ();

            public FrameValues (int frameCount)
            {
                frameCount_ = frameCount;
            }

            public int curveCount
            {
                get { return values_.Count; }
            }

            /// <summary>frame の今の値（全部の対象と Rig の重み。キーを打つときと同じ組）を控える</summary>
            public void Capture (int frame, Transform root, IEnumerable<PoseTarget> targets, RigProxies proxies)
            {
                using (CurveWriter writer = CurveWriter.Begin (null, root, frame, null)) {
                    PoseCommit.WriteAllKeys (writer, targets, proxies);
                    foreach (string path in writer.rotationPaths) rotationPaths_.Add (path);
                    foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) {
                        float[] curve;
                        if (!values_.TryGetValue (pair.Key, out curve)) {
                            curve = new float[frameCount_];
                            values_.Add (pair.Key, curve);
                        }
                        curve[frame] = pair.Value;
                    }
                }
            }

            /// <summary>集めた値を destination へ書く（WriteCollected）</summary>
            public void Write (AnimationClip destination, float rate)
            {
                WriteCollected (destination, values_, rotationPaths_, frameCount_, rate);
            }
        }

        /// <summary>
        /// 集めた値（フレームごと）を、回転の符号をそろえてから編集用クリップへ書く。
        /// 統合（S14）のように、集め方が違っても書き方は同じ
        /// </summary>
        public static void WriteCollected (AnimationClip destination, Dictionary<EditorCurveBinding, float[]> values, IEnumerable<string> rotationPaths, int frameCount, float rate)
        {
            AlignRotationSigns (values, rotationPaths, frameCount);
            Write (destination, values, rate);
        }

        /// <summary>
        /// 補間で遠回りしないよう、回転は前のフレームと同じ側の四元数にそろえる
        /// </summary>
        static void AlignRotationSigns (Dictionary<EditorCurveBinding, float[]> values, IEnumerable<string> rotationPaths, int frameCount)
        {
            foreach (string path in rotationPaths) {
                float[][] q = new float[4][];
                bool complete = true;
                for (int i = 0; i < 4; i++) {
                    complete &= values.TryGetValue (CurveEdit.RotationBinding (path, i), out q[i]);
                }
                if (!complete) continue;

                for (int f = 1; f < frameCount; f++) {
                    float dot = q[0][f] * q[0][f - 1] + q[1][f] * q[1][f - 1] + q[2][f] * q[2][f - 1] + q[3][f] * q[3][f - 1];
                    if (dot >= 0) continue;
                    for (int i = 0; i < 4; i++) q[i][f] = -q[i][f];
                }
            }
        }

        /// <summary>
        /// 集めた値を編集用リグのカーブとして書く（元からある編集用リグのカーブは置き換える）
        /// </summary>
        static void Write (AnimationClip destination, Dictionary<EditorCurveBinding, float[]> values, float rate)
        {
            Undo.RecordObject (destination, "Import Motion");
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (destination)) {
                if (EditingClip.IsRigBinding (binding)) AnimationUtility.SetEditorCurve (destination, binding, null);
            }
            foreach (KeyValuePair<EditorCurveBinding, float[]> pair in values) {
                Keyframe[] keys = new Keyframe[pair.Value.Length];
                for (int f = 0; f < keys.Length; f++) {
                    keys[f] = new Keyframe (f / rate, pair.Value[f]);
                    keys[f].inTangent = 0;
                    keys[f].outTangent = 0;
                }
                AnimationCurve curve = new AnimationCurve (keys);
                for (int f = 0; f < keys.Length; f++) {
                    AnimationUtility.SetKeyLeftTangentMode (curve, f, AnimationUtility.TangentMode.ClampedAuto);
                    AnimationUtility.SetKeyRightTangentMode (curve, f, AnimationUtility.TangentMode.ClampedAuto);
                }
                AnimationUtility.SetEditorCurve (destination, pair.Key, curve);
            }
            EditorUtility.SetDirty (destination);
        }
    }

}
