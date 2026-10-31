
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の段（S25）。編集用リグ（と Override）が解いた姿勢を元の姿勢として受け取り、
    /// 固定の強さが入っている点（Controls/Body/...）へ届くように編集用の体を曲げて、表示モデルへ写す。
    ///
    /// 解くのは、点や骨を動かしている間と、キーの間で前後の点のキーが Locked の点があるときだけ（S25d。そのときは Locked の点のある手足だけを動かす）。手を離すと、解いた姿勢が骨のキーとして打たれる。
    /// それ以外の再生・フレームの移動では解かず、キーの間は骨の値の補間で動く。
    /// 点の状態（Pin / Locked）は次にその点のキーを打つまで引き継ぐが、場所は引き継がない（そのフレームの骨の位置に置く）。
    ///
    /// - 全身 IK の点が 1 つでも置けたキャラだけに並ぶ（PreviewStage.fullBody）。
    /// - 点の値は Editing Rig の段のクリップにある（保存データから配られた値）。つかむのは点で、間の段を通らずに保存データへ書く。
    /// - 効いている点が無ければ何もしない（素通し）。点のキーが無いクリップは、この段が無いときと同じ姿勢になる。
    /// - 重みは、元の姿勢と解いた姿勢の混ぜ具合（骨のローカルの値）。
    /// - 毎回、受け取った元の姿勢から解く。この段から解き直すとき（EvaluateKeepingValues）は、控えた元の姿勢へ戻してから解く
    /// </summary>
    public sealed class FullBodyLayer : PoseLayer
    {
        readonly PreviewStage stage_;

        public FullBodyLayer (PreviewStage stage)
        {
            stage_ = stage;
        }

        FullBodyRig rig
        {
            get { return stage_ != null ? stage_.fullBody : null; }
        }

        public override LayerKind kind { get { return LayerKind.FullBodyIk; } }
        public override int order { get { return PoseStack.kOrderFullBody; } }
        public override string label { get { return "Full Body IK"; } }
        public override bool canEvaluate { get { return rig != null && rig.pointCount > 0; } }
        public override PosePhase phase { get { return PosePhase.Body; } }
        public override bool hasWeight { get { return true; } }
        public override GrabTarget grab { get { return GrabTarget.Values; } }
        public override InverseKind inverse { get { return InverseKind.Approximate; } }
        public override string inverseReason { get { return Tr ("FULL_BODY_LAYER_INVERSE_REASON"); } }
        public override string unavailableReason { get { return rig == null ? Tr ("FULL_BODY_LAYER_NO_POINTS") : null; } }

        protected override string description
        {
            get {
                FullBodyRig body = rig;
                if (body == null) return "";
                // Free（体に付いて動く）/ Pin（動かした点）/ Locked（置いた場所から動かさない）
                int active = 0;
                int locked = 0;
                foreach (BodyPointId bone in body.pointIds) {
                    if (body.GetPositionWeight (bone) <= 0 && body.GetRotationWeight (bone) <= 0) continue;
                    if (body.GetLocked (bone)) locked++;
                    else active++;
                }
                return active + locked > 0
                    ? Tr ("FULL_BODY_LAYER_POINT_COUNTS", body.pointCount, active, locked)
                    : Tr ("FULL_BODY_LAYER_NO_PINNED_POINTS", body.pointCount);
            }
        }

        /// <summary>
        /// クリップを当てただけのとき（再生・フレームの移動・焼き）。姿勢はクリップの骨の値で決まる
        /// （点を離したときに、解いた姿勢を骨のキーとして打ってある）。キーのあるフレームでは解かない。
        /// キーの間は、前後の点のキーで Locked の点だけを効かせて解く（S25d。足を床に付けたままにする、など）。
        /// 骨のキーの補間した姿勢から毎回解くので、どのフレームでも同じ答えになる
        /// </summary>
        public override void Evaluate (float time)
        {
            FullBodyRig body = rig;
            if (body == null) return;
            // クリップから姿勢を当て直したら、足と一緒に動かしていた Pin の点も置いた場所の固定に戻す（Floor Clamp。FullBodyRig.CarryFootPins）
            body.EndGrab ();
            HeldObjectRig held = stage_.heldObjects;
            if (held != null) {
                held.EndDrag ();
                // 物の上の点も引き継ぐので、先に物をこのフレームの場所へ置く
                held.Refresh ();
            }
            // 前のキーフレームから状態だけ引き継いだ点は、このフレームの骨の位置に置く
            stage_.SnapCarriedPoints ();
            body.CaptureInput ();
            ICollection<UnityEngine.Transform> locked = stage_.lockedSpanPoints;
            if (held != null) held.Prepare (body, locked);
            if (weight > 0 && locked.Count > 0) {
                // Locked の点のある手足だけを IK で動かし、腰・背骨と Pin / Free の手足は骨のキーの補間（FK）のまま
                body.SolveOnly (locked, weight, true);
            }
            else if (held == null) return;
            // 持っている手は、キーのあるフレームでも解く（S25d の例外。物のキーだけ直しても手が外れないように）
            if (held != null) held.Finish (body, weight);
            stage_.ShowEditingBody ();
        }

        /// <summary>
        /// 前の段の値（骨・点）を直に動かした後。今のフレームで固定している点へ届くように解く
        /// </summary>
        public override void EvaluateAfterEdit (float time)
        {
            FullBodyRig body = rig;
            if (body == null) return;
            body.CaptureInput ();
            Solve (body);
        }

        /// <summary>
        /// 点の値を直に動かした後。前に解いた結果からでなく、控えた元の姿勢から解き直す
        /// </summary>
        public override void EvaluateKeepingValues (float time)
        {
            FullBodyRig body = rig;
            if (body == null) return;
            if (!body.RestoreInput ()) body.CaptureInput ();
            Solve (body);
        }

        void Solve (FullBodyRig body)
        {
            HeldObjectRig held = stage_.heldObjects;
            if (held != null) held.Prepare (body);
            bool solve = weight > 0 && body.hasActivePoint;
            if (solve) body.Solve (weight);
            if (held != null) held.Finish (body, weight);
            if (solve || held != null) stage_.ShowEditingBody ();
        }
    }

}
