using System.Collections.Generic;
using UnityEngine;

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
            // ここからは、この姿勢が元の姿勢
            if (stage.editingRigSolver != null) stage.editingRigSolver.Capture ();
            stage.fullBody.CaptureInput ();
            using (CurveWriter writer = begin ()) {
                foreach (PoseTarget target in targets) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null && point.hasPendingKeys) point.WritePendingKeys (writer);
                }
                foreach (PoseTarget target in targets) target.WriteKeys (writer);
                RigProxies proxies = stage.editingRig != null ? stage.editingRig.rigProxies : null;
                if (proxies != null) proxies.WriteWeightKeys (writer);
            }
        }
    }

}
