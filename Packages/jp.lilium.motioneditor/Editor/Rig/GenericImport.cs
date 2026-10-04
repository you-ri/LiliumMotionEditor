using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Generic Pose の段の出力（骨を直接動かす Generic のクリップを合成した姿勢）を、編集用リグの値に直して編集用クリップへ書く（Generic Pose の段の逆）。
    /// フレームごとに段の出力を編集用の体に作り（evaluate）、骨の姿勢を操作値として捉え（EditingRigSolver.Capture）、
    /// 窓がキーを打つときと同じ値（PoseTarget.WriteKeys）を格子の全フレームに書く。
    /// - 範囲は Generic のクリップと書き出し先の長いほう。格子は書き出し先のクリップの fps（30fps の元でも 60fps の格子で取り直す）
    /// - 取り込んだ手足は FK（IK の重み 0）。IK で触ると、その時点の見た目から IK へ切り替わる
    /// - Animator の GameObject（パスが空）のカーブと、編集用リグに無い骨のカーブは取り込まない（注意に出す）
    /// - 書き出し先の編集用リグのカーブは置き換える。それ以外のカーブ（旧形式の名残）は触らない
    /// </summary>
    public static class GenericImport
    {
        /// <summary>
        /// 取り込めない理由（取り込めるなら null）
        /// </summary>
        public static string GetProblem (AnimationClip source)
        {
            if (source == null) return Tr ("GENERIC_IMPORT_NO_CLIP");
            if (source.humanMotion) return Tr ("GENERIC_IMPORT_HUMANOID_CLIP");
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (source);
            if (bindings.Any (EditingClip.IsRigBinding)) return Tr ("GENERIC_IMPORT_EDITING_RIG_CLIP");
            if (!bindings.Any (b => b.type == typeof (Transform))) return Tr ("GENERIC_IMPORT_NO_TRANSFORM_CURVES");
            return null;
        }

        /// <param name="source">合成している Generic のクリップ（範囲と注意に使う）</param>
        /// <param name="evaluate">その時刻の段の出力を編集用の骨に作る（窓は段の並びを Generic Pose まで通す）</param>
        /// <param name="targets">キーを打つ対象（窓と同じもの）</param>
        public static PoseImport.Result Import (AnimationClip source, AnimationClip destination, EditingRig rig, EditingRigSolver solver,
            System.Action<float> evaluate, IList<PoseTarget> targets)
        {
            string problem = GetProblem (source);
            if (problem != null) throw new System.InvalidOperationException (problem);
            if (rig == null || rig.root == null) throw new System.InvalidOperationException (Tr ("GENERIC_IMPORT_NO_EDITING_RIG"));

            List<string> notes = new List<string> ();
            List<Transform> bones = CheckBindings (source, rig, notes);
            float rate = destination != null && destination.frameRate > 0 ? destination.frameRate : 60;
            if (!Mathf.Approximately (source.frameRate, rate)) {
                notes.Add (Tr ("GENERIC_IMPORT_RESAMPLED", source.frameRate, rate));
            }
            return PoseImport.Run (new PoseImport.Options {
                destination = destination,
                rig = rig,
                solver = solver,
                evaluate = evaluate,
                targets = targets,
                checkBones = bones,
                length = Mathf.Max (source.empty ? 0 : source.length, destination == null || destination.empty ? 0 : destination.length),
                notes = notes,
            });
        }

        /// <summary>
        /// 元のクリップのカーブが、編集用リグの操作値で表せる骨を指しているか。表せる骨（取り込む骨）を返す
        /// </summary>
        static List<Transform> CheckBindings (AnimationClip source, EditingRig rig, List<string> notes)
        {
            Transform root = rig.root.transform;
            HashSet<Transform> controlled = new HashSet<Transform> ();
            foreach (RigBinding.Fk fk in rig.binding.fk) {
                if (fk.enabled) controlled.Add (rig.GetEditingBone (fk.bone));
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                if (extra.enabled) controlled.Add (rig.GetEditingBone (extra.bone));
            }

            HashSet<Transform> bones = new HashSet<Transform> ();
            bool rootCurves = false;
            SortedSet<string> missing = new SortedSet<string> ();
            SortedSet<string> uncontrolled = new SortedSet<string> ();
            int others = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (source)) {
                if (binding.type != typeof (Transform)) {
                    others++;
                    continue;
                }
                if (string.IsNullOrEmpty (binding.path)) {
                    rootCurves = true;
                    continue;
                }
                Transform bone = root.Find (binding.path);
                if (bone == null) missing.Add (binding.path);
                else if (!controlled.Contains (bone)) uncontrolled.Add (bone.name);
                else bones.Add (bone);
            }
            if (rootCurves) notes.Add (Tr ("GENERIC_IMPORT_ROOT_CURVES_SKIPPED"));
            if (missing.Count > 0) notes.Add (Tr ("GENERIC_IMPORT_MISSING_BONES_SKIPPED", missing.Count, ImportNotes.Summary (missing)));
            if (uncontrolled.Count > 0) notes.Add (Tr ("GENERIC_IMPORT_UNCONTROLLED_BONES_SKIPPED", uncontrolled.Count, ImportNotes.Summary (uncontrolled)));
            if (others > 0) notes.Add (Tr ("GENERIC_IMPORT_OTHER_CURVES_SKIPPED", others));
            return bones.ToList ();
        }
    }

    static class ImportNotes
    {
        public static string Summary (IEnumerable<string> names)
        {
            List<string> list = names.ToList ();
            string joined = string.Join (Tr ("GENERIC_IMPORT_LIST_SEPARATOR"), list.Take (5));
            return list.Count > 5 ? Tr ("GENERIC_IMPORT_LIST_AND_MORE", joined) : joined;
        }
    }

}
