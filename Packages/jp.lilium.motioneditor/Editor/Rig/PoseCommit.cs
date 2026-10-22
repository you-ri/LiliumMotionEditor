using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 作った姿勢をクリップのキーにするときの決まり（S35）。窓（PreviewWindow）と窓を開かずに使う PoseSession が同じ物を通る。
    /// 2 か所に書くと、窓で打ったキーと外から打ったキーが食い違うため
    /// </summary>
    public static class PoseCommit
    {
        /// <summary>
        /// 書き込み先が Override のとき、打つ値（狙いの値）をクリップに入れる値へ直す口を書き出し口に付ける（S14）。
        /// 元の値は、姿勢を作ったときに書き込み先の層の手前で控えたもの（PreviewStage.CaptureOverrideSource）。
        /// 段にマスクがあれば、マスクの外のカーブは打たない（S17。打っても重ねるときに使われず、クリップに使われない値が残るだけ）
        /// </summary>
        public static void AttachOverrideStore (CurveWriter writer, PreviewStage stage, OverrideMode mode, ChannelMask mask = null)
        {
            if (writer == null || stage == null) return;
            if (mask != null) writer.filter = binding => stage.InMask (mask, binding);
            writer.storePosition = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StorePosition (mode, position, value) : value;
            };
            writer.storeRotation = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StoreRotation (mode, rotation, value) : value;
            };
            writer.storeScale = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StoreScale (mode, scale, value) : value;
            };
        }

        /// <summary>
        /// キーを打つフレーム（S32）。サイクルキーのフレームなら先頭のキー（サイクルキーは先頭を写した物で、直接打っても先頭へ戻される）。
        /// Override へ書くときは土台の値との差分なので、写し先を変えない
        /// </summary>
        /// <param name="keyFrames">書き込み先のクリップのキーのあるフレーム</param>
        public static int KeyFrameToWrite (int frame, bool cycle, int[] keyFrames, bool toOverride)
        {
            int first, last;
            if (!toOverride && cycle && ClipCycle.TryGetRange (keyFrames, out first, out last) && frame == last) return first;
            return frame;
        }

        /// <summary>
        /// サイクルキーより後ろのフレームか（S32）。ここでキーを打つと、そこが最後のキーになって先頭の姿勢で上書きされるので、打たない
        /// </summary>
        public static bool IsPastCycleKey (int frame, bool cycle, int[] keyFrames)
        {
            int first, last;
            return cycle && ClipCycle.TryGetRange (keyFrames, out first, out last) && frame > last;
        }

        /// <summary>
        /// 全身 IK で作った今の姿勢を、キーにする（キーフレームは全身の姿勢を持つ。S25）。
        /// 1. 動かした点（Locked でないもの）を、実際の骨の位置へ合わせ直す
        /// 2. 解いた姿勢を骨の値（FK の差分・腰の位置）にして、全部の骨のキーを打つ
        /// 3. 動かした点・固定した点のキーを打つ（このキーフレームへ戻ったときに、同じ点から直せる）
        /// </summary>
        /// <param name="begin">今のクリップ・今のフレームへの書き出し口を作る（Undo の記録・Override の差分化・サイクルキーは呼ぶ側が決める）</param>
        public static void WriteFullBodyPose (PreviewStage stage, IReadOnlyList<PoseTarget> targets, System.Func<CurveWriter> begin)
        {
            if (stage == null || stage.fullBody == null || begin == null) return;
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) point.SyncToBone ();
            }
            // 足と一緒に動かした Pin の点は、骨の今の場所へ付け直した（Floor Clamp。FullBodyRig.CarryFootPins）
            stage.fullBody.ClearFootCarry ();
            // ここからは、この姿勢が元の姿勢
            if (stage.editingRigSolver != null) stage.editingRigSolver.Capture ();
            stage.fullBody.CaptureInput ();
            using (CurveWriter writer = begin ()) {
                foreach (PoseTarget target in targets) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null && point.hasPendingKeys) point.WritePendingKeys (writer);
                }
                WriteAllKeys (writer, targets, stage.editingRig != null ? stage.editingRig.rigProxies : null);
            }
        }

        /// <summary>
        /// 全部の対象と、ゲームの Rig の重みのキーを打つ（姿勢全体のキーのカーブの組。どこで打っても同じ組にする）
        /// </summary>
        public static void WriteAllKeys (CurveWriter writer, IEnumerable<PoseTarget> targets, RigProxies proxies)
        {
            foreach (PoseTarget target in targets) target.WriteKeys (writer);
            if (proxies != null) proxies.WriteWeightKeys (writer);
        }

        /// <summary>今の値（WriteAllKeys で打つ値）を、クリップに書かずに取り出す</summary>
        public static Dictionary<EditorCurveBinding, float> CaptureValues (Transform root, IEnumerable<PoseTarget> targets, RigProxies proxies, int frame)
        {
            Dictionary<EditorCurveBinding, float> result = new Dictionary<EditorCurveBinding, float> ();
            using (CurveWriter writer = CurveWriter.Begin (null, root, frame, null)) {
                WriteAllKeys (writer, targets, proxies);
                foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) result[pair.Key] = pair.Value;
            }
            return result;
        }

        /// <summary>
        /// muscle の値（Humanoid の姿勢の一部。手の形など）を今の姿勢に重ね、動いた骨の今の操作値を取り出す（窓の PoseBank と PoseSession.WriteMuscles）。
        /// only があれば、そのパスの値だけ。取り出せなければ理由を返す（取り出せたら null）
        /// </summary>
        public static string CaptureMuscles (PreviewStage stage, IEnumerable<PoseTarget> targets, IReadOnlyDictionary<int, float> muscles, int frame,
            ICollection<string> only, out Dictionary<EditorCurveBinding, float> values)
        {
            values = new Dictionary<EditorCurveBinding, float> ();
            if (muscles == null || muscles.Count == 0) return Tr ("PREVIEW_WINDOW_POSE_BANK_NO_HUMANOID_VALUES");
            Transform root = stage != null ? stage.editingRoot : null;
            if (root == null) return Tr ("PREVIEW_WINDOW_POSE_BANK_SELECT_CHARACTER");
            HashSet<string> moved = stage.ApplyMusclesAsControls (muscles);
            if (moved == null) return Tr ("PREVIEW_WINDOW_POSE_BANK_CANNOT_APPLY_HUMANOID");
            if (moved.Count == 0) return Tr ("PREVIEW_WINDOW_POSE_BANK_NO_MOVED_BONES");
            // 全部の対象に書かせて、動いた骨の分だけ取る
            foreach (KeyValuePair<EditorCurveBinding, float> pair in CaptureValues (root, targets, null, frame)) {
                if (!moved.Contains (pair.Key.path)) continue;
                if (only != null && !only.Contains (pair.Key.path)) continue;
                values[pair.Key] = pair.Value;
            }
            return null;
        }

        /// <summary>body の中の Undo を 1 回にまとめ、name で戻せるようにする</summary>
        public static void InUndoGroup (string name, System.Action body)
        {
            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            body ();
            Undo.SetCurrentGroupName (name);
            Undo.CollapseUndoOperations (group);
        }
    }

}
