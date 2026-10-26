using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 既存の Humanoid のモーション（ゲームで使っているクリップ）を、編集用リグの値に直して編集用クリップへ書く（Humanoid Pose の段の逆。S7）。
    /// フレームごとに「段の並びを Humanoid Pose まで通す → 表示モデルの骨を編集用の体へ写す → 操作値として捉える」を回す。
    /// - 範囲は元のクリップと書き出し先の長いほう。格子は書き出し先の fps（30fps の元でも 60fps で取り直す）
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
            if (source == null) return Tr ("GENERIC_IMPORT_NO_CLIP");
            if (!source.humanMotion) return Tr ("HUMANOID_IMPORT_NOT_HUMANOID_CLIP");
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
            if (rig == null || rig.root == null) throw new System.InvalidOperationException (Tr ("GENERIC_IMPORT_NO_EDITING_RIG"));

            List<string> notes = new List<string> ();
            List<Transform> bones = ControlledBones (rig, notes);
            float rate = CurveEdit.FrameRateOf (destination);
            if (!Mathf.Approximately (source.frameRate, rate)) {
                notes.Add (Tr ("GENERIC_IMPORT_RESAMPLED", source.frameRate, rate));
            }
            notes.Add (Tr ("HUMANOID_IMPORT_HUMANOID_DEVIATION"));

            float length = Mathf.Max (source.empty ? 0 : source.length, destination == null || destination.empty ? 0 : destination.length);
            int frameCount = CurveEdit.FrameCount (length, rate);
            // 元のクリップの範囲だけ測る（後ろは最後の置き場所のまま）
            int sourceFrames = Mathf.Clamp (Mathf.RoundToInt ((source.empty ? 0 : source.length) * rate) + 1, 1, frameCount);
            RootMotion.Sample (rig.displayAnimator, source, rate, sourceFrames, out Vector3[] rootPositions, out Quaternion[] rootRotations);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (source);
            // 最初の体の向きと水平の場所を、クリップに焼いた値（RootT・RootQ）へ戻す。足し戻すルートの移動ごと回す
            if (GetStartOffset (source, rig.displayAnimator, evaluate, out Vector3 startPosition, out Quaternion startRotation)) {
                for (int f = 0; f < sourceFrames; f++) {
                    rootPositions[f] = startPosition + startRotation * rootPositions[f];
                    rootRotations[f] = startRotation * rootRotations[f];
                }
            }

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
                ClipUndo.Record (destination, "Import Motion");
                HumanoidOutput.CopyClipSettings (source, destination);
                notes.Add (Tr ("HUMANOID_IMPORT_CLIP_SETTINGS_COPIED"));
            }
            if (!Mathf.Approximately (settings.level, 0) || !Mathf.Approximately (settings.orientationOffsetY, 0)) {
                notes.Add (Tr ("HUMANOID_IMPORT_OFFSETS_BAKED"));
            }
            return result;
        }

        /// <summary>
        /// 当てた姿勢の体の置き場所を、クリップに焼いた最初の置き場所へ戻す回転（鉛直軸まわり）と水平の移動（Animator の中の長さ）。
        /// Root Transform Rotation の基準が体の向き（Based Upon Body Orientation。焼いたクリップの既定）だと、Unity は最初の体の向きを
        /// Animator の GameObject へ移して当て、evaluate は GameObject を元へ戻すので、最初の向きが落ちる。
        /// 骨が後ろ（-Z）を向いて立つモデルは体の向きが 180° なので、取り込むと体が 180° 回り、元に重ねると脚がねじれた
        /// （Override の muscle の範囲超え・跳ね）。高さは今までどおり当てた姿勢のまま
        /// </summary>
        static bool GetStartOffset (AnimationClip source, Animator animator, System.Action<float> evaluate, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return false;
            if (!TryGetRoot (source, out Vector3 rootT, out Quaternion rootQ)) return false;

            evaluate (0);
            HumanPose pose = new HumanPose ();
            using (HumanPoseHandler handler = new HumanPoseHandler (animator.avatar, animator.transform)) {
                handler.GetHumanPose (ref pose);
            }
            // Unity が GameObject へ移すのは鉛直軸まわりの向きだけ（前後・左右の傾きは姿勢に残る）
            Vector3 forward = rootQ * Quaternion.Inverse (pose.bodyRotation) * Vector3.forward;
            forward.y = 0;
            if (forward.sqrMagnitude > 1e-8f) rotation = Quaternion.LookRotation (forward);
            // HumanPose の体の場所は体の大きさで割った値
            Vector3 delta = rootT - rotation * pose.bodyPosition;
            position = new Vector3 (delta.x, 0, delta.z) * animator.humanScale;
            return Quaternion.Angle (rotation, Quaternion.identity) > 0.01f || position.sqrMagnitude > 1e-10f;
        }

        /// <summary>クリップの 0 秒の体の場所（RootT）と向き（RootQ）</summary>
        static bool TryGetRoot (AnimationClip clip, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            string[] axes = { "x", "y", "z", "w" };
            float[] values = new float[7];
            for (int i = 0; i < 7; i++) {
                string name = i < 3 ? HumanoidBaker.kRootT + "." + axes[i] : HumanoidBaker.kRootQ + "." + axes[i - 3];
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name));
                if (curve == null || curve.length == 0) return false;
                values[i] = curve.Evaluate (0);
            }
            position = new Vector3 (values[0], values[1], values[2]);
            rotation = Quaternion.Normalize (new Quaternion (values[3], values[4], values[5], values[6]));
            return true;
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
                foreach (RigBinding.BoneControl control in rig.binding.boneControls) {
                    if (control.enabled && control.bone != null) controlled.Add (AnimationUtility.CalculateTransformPath (control.bone, root));
                }
            }

            ClipUndo.Record (destination, "Import Motion");
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
            if (carried > 0) notes.Add (Tr ("HUMANOID_IMPORT_CURVES_CARRIED", carried));
            if (skipped.Count > 0) notes.Add (Tr ("HUMANOID_IMPORT_CONTROLLED_CURVES_NOT_CARRIED", string.Join (Tr ("GENERIC_IMPORT_LIST_SEPARATOR"), skipped)));
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
            foreach (RigBinding.BoneControl control in rig.binding.boneControls) {
                Transform bone = rig.GetEditingBone (control.bone);
                if (bone == null) continue;
                if (control.enabled) bones.Add (bone);
                else skipped.Add (bone.name);
            }
            if (skipped.Count > 0) notes.Add (Tr ("HUMANOID_IMPORT_UNCONTROLLED_BONES_SKIPPED", skipped.Count));
            return bones;
        }
    }

}
