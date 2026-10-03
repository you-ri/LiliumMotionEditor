using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 既存の Humanoid のモーション（ゲームで使っているクリップ）を、編集用リグの値に直して編集用クリップへ書く（Humanoid Pose の段の逆。S7）。
    /// フレームごとに「段の並びを Humanoid Pose まで通す → 表示モデルの骨を編集用の体へ写す → 操作値として捉える」を回す。
    /// - 範囲は元のクリップと書き出し先の長いほう。格子は書き出し先の fps（30fps の元でも 60fps で取り直す）
    /// - 取り込んだ手足は FK（IK の重み 0）
    /// - ルートの移動（ルートモーション）は腰の位置と向きへ足し戻す。段はその場で当てるので、Animator を進めて測った分を足す
    /// - 編集用リグで動かさない骨は取り込まない（注意に出す）
    /// - Humanoid の往復の分だけ元とずれる（骨の向き 約 1°。S2 の測定）
    /// </summary>
    public static class HumanoidImport
    {
        /// <summary>
        /// 取り込めない理由（取り込めるなら null）
        /// </summary>
        public static string GetProblem (AnimationClip source)
        {
            if (source == null) return "クリップが無い";
            if (!source.humanMotion) return "Humanoid のクリップではない（Generic のクリップは Generic Pose の段から取り込む）";
            return null;
        }

        /// <param name="source">読み込む Humanoid のクリップ</param>
        /// <param name="evaluate">その時刻の姿勢を表示モデルに作る（窓は段の並びを Humanoid Pose まで通す）</param>
        /// <param name="targets">キーを打つ対象（窓と同じもの）</param>
        public static PoseImport.Result Import (AnimationClip source, AnimationClip destination, EditingRig rig, EditingRigSolver solver,
            System.Action<float> evaluate, IList<PoseTarget> targets)
        {
            string problem = GetProblem (source);
            if (problem != null) throw new System.InvalidOperationException (problem);
            if (rig == null || rig.root == null) throw new System.InvalidOperationException ("編集用リグが無い");

            List<string> notes = new List<string> ();
            List<Transform> bones = ControlledBones (rig, notes);
            float rate = destination != null && destination.frameRate > 0 ? destination.frameRate : 60;
            if (!Mathf.Approximately (source.frameRate, rate)) {
                notes.Add ("元は " + source.frameRate + "fps。" + rate + "fps の格子で取り直した");
            }
            notes.Add ("Humanoid を通すので、元のモーションとは骨の向きが少しずれる（可動範囲の丸めとねじりの配り直し）");

            float length = Mathf.Max (source.empty ? 0 : source.length, destination == null || destination.empty ? 0 : destination.length);
            int frameCount = Mathf.Max (1, Mathf.RoundToInt (length * rate) + 1);
            // 元のクリップの範囲だけ測る（後ろは最後の置き場所のまま）
            int sourceFrames = Mathf.Clamp (Mathf.RoundToInt ((source.empty ? 0 : source.length) * rate) + 1, 1, frameCount);
            RootMotion.Sample (rig.displayAnimator, source, rate, sourceFrames, out Vector3[] rootPositions, out Quaternion[] rootRotations);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (source);

            PoseImport.Result result = PoseImport.Run (new PoseImport.Options {
                destination = destination,
                rig = rig,
                solver = solver,
                // 表示モデルに作った姿勢を編集用の体へ写してから捉える
                evaluate = time => {
                    evaluate (time);
                    rig.SyncFromDisplay ();
                    int f = Mathf.Clamp (Mathf.RoundToInt (time * rate), 0, sourceFrames - 1);
                    RootMotion.Apply (rig, rootPositions[f], rootRotations[f]);
                },
                targets = targets,
                checkBones = bones,
                length = length,
                notes = notes,
            });

            if (destination != null) CarryCurves (source, destination, rig, notes);

            // ルートの移動の分け方（Bake Into Pose・Based Upon）は元のクリップに合わせる。焼いた出力を新しく作るときに引き継ぐ
            if (destination != null) {
                Undo.RecordObject (destination, "Import Motion");
                HumanoidOutput.CopyClipSettings (source, destination);
                notes.Add ("ループとルートの移動の扱いの設定を元のクリップから写した（焼いた出力が既にあれば、その設定は変えない）");
            }
            if (!Mathf.Approximately (settings.level, 0) || !Mathf.Approximately (settings.orientationOffsetY, 0)) {
                notes.Add ("元のクリップの高さ・向きの補正は姿勢に焼き込んだ");
            }
            return result;
        }

        /// <summary>
        /// 元のクリップにあって姿勢から作り直さないカーブ（武器など人型でない骨・そのほかのプロパティ）を、任意のプロパティ（Controls/Props/...）として持ち越す。
        /// 焼くときにゲーム prefab から見たパスへ戻るので、H土台 で焼いたときと同じく焼いた版に残る。編集用リグが動かす骨のカーブは持ち越さない（二重に動かさない）
        /// </summary>
        internal static int CarryCurves (AnimationClip source, AnimationClip destination, EditingRig rig, List<string> notes)
        {
            HashSet<string> controlled = new HashSet<string> ();
            Transform root = rig != null && rig.displayAnimator != null ? rig.displayAnimator.transform : null;
            if (root != null) {
                foreach (RigBinding.Fk fk in rig.binding.fk) {
                    if (fk.enabled && fk.bone != null) controlled.Add (AnimationUtility.CalculateTransformPath (fk.bone, root));
                }
                foreach (RigBinding.Extra extra in rig.binding.extras) {
                    if (extra.enabled && extra.bone != null) controlled.Add (AnimationUtility.CalculateTransformPath (extra.bone, root));
                }
            }

            Undo.RecordObject (destination, "Import Motion");
            int carried = 0;
            SortedSet<string> skipped = new SortedSet<string> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (source)) {
                if (HumanoidBaker.IsHumanPoseCurve (binding)) continue;
                if (binding.type == typeof (Transform) && controlled.Contains (binding.path)) {
                    skipped.Add (binding.path);
                    continue;
                }
                AnimationUtility.SetEditorCurve (destination, PropertyPlayer.ToClip (binding), AnimationUtility.GetEditorCurve (source, binding));
                carried++;
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (source)) {
                AnimationUtility.SetObjectReferenceCurve (destination, PropertyPlayer.ToClip (binding), AnimationUtility.GetObjectReferenceCurve (source, binding));
                carried++;
            }
            if (carried > 0) notes.Add ("人型の姿勢でないカーブ " + carried + " 本（武器の骨など）をそのまま持ち越した");
            if (skipped.Count > 0) notes.Add ("編集用リグが動かす骨のカーブは持ち越さない: " + string.Join ("、", skipped));
            EditorUtility.SetDirty (destination);
            return carried;
        }

        /// <summary>
        /// 編集用リグが動かす骨（取り込める骨）。動かさない骨は元の動きが落ちるので注意に出す
        /// </summary>
        static List<Transform> ControlledBones (EditingRig rig, List<string> notes)
        {
            List<Transform> bones = new List<Transform> ();
            SortedSet<string> skipped = new SortedSet<string> ();
            foreach (RigBinding.Fk fk in rig.binding.fk) {
                Transform bone = rig.GetEditingBone (fk.bone);
                if (bone == null) continue;
                if (fk.enabled) bones.Add (bone);
                else skipped.Add (bone.name);
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                Transform bone = rig.GetEditingBone (extra.bone);
                if (bone == null) continue;
                if (extra.enabled) bones.Add (bone);
                else skipped.Add (bone.name);
            }
            if (skipped.Count > 0) notes.Add ("編集用リグで動かさない骨 " + skipped.Count + " 本の動きは取り込まない");
            return bones;
        }
    }

}
