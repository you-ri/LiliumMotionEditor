using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// キャラに組まれている Animation Rigging の 1 層（Rig の層ごとに 1 インスタンス）。
    /// 重みとターゲットは保存データから受け取る。逆に通れるかは、層に入っている拘束のうち一番悪いもので決まる
    /// </summary>
    public sealed class RigBuilderLayer : PoseLayer
    {
        readonly RigLayerInfo info_;
        readonly int index_;
        readonly PreviewStage stage_;
        readonly IReadOnlyList<RigLayerInfo> siblings_;
        readonly InverseKind inverse_ = InverseKind.Exact;
        readonly string inverseReason_;

        /// <param name="info">null なら「Rigging が組まれているのに読み取れない」行</param>
        /// <param name="siblings">同じ表示モデルの Rig の層すべて（申告の無い層を 0 にするため）</param>
        public RigBuilderLayer (RigLayerInfo info, int index, PreviewStage stage = null, IReadOnlyList<RigLayerInfo> siblings = null)
        {
            info_ = info;
            index_ = index;
            stage_ = stage;
            siblings_ = siblings;
            if (info == null) return;

            weight = info.weight;
            foreach (RigConstraintInfo constraint in info.constraints) {
                details.Add (constraint.label + " — " + PoseStack.Describe (constraint.inverse));
                if (!PoseStack.Worse (constraint.inverse, inverse_)) continue;
                inverse_ = constraint.inverse;
                inverseReason_ = constraint.label + ": " + constraint.reason;
            }
        }

        /// <summary>Animation Rigging が読み込まれていなくて中身を読めない行か</summary>
        public bool unreadable
        {
            get { return info_ == null; }
        }

        public override LayerKind kind { get { return LayerKind.RigBuilder; } }
        public override string idSuffix { get { return info_ != null ? info_.label + "@" + index_ : null; } }
        public override int order { get { return PoseStack.kOrderRigBuilder + index_; } }
        public override string label { get { return info_ != null ? "Rig: " + info_.label : "Rig Builder"; } }
        public override bool hasWeight { get { return info_ != null; } }
        // ゲームと同じ姿勢を出す段（S4）。Animation Rigging が入っていないときは素通し
        public override bool canEvaluate { get { return info_ != null && stage_ != null && stage_.canApplyRig; } }
        public override PosePhase phase { get { return PosePhase.Graph; } }
        public override GrabTarget grab { get { return info_ != null ? GrabTarget.Values : GrabTarget.None; } }
        // ターゲットをつかむ操作はまだ無いので、つかむものはあるが選べない
        public override bool canManipulate { get { return false; } }
        public override InverseKind inverse { get { return inverse_; } }
        public override string inverseReason { get { return inverseReason_; } }
        // 表示の骨をつかむ間は、その骨（か親）を動かす拘束だけを重み 0 にして通す（PreviewStage.FlushPoseGraph）
        public override bool bypassOnDisplayGrab { get { return info_ != null && stage_ != null && stage_.canApplyRig; } }

        public override string unavailableReason
        {
            get { return info_ != null ? Tr ("RIG_BUILDER_LAYER_UNAVAILABLE") : Tr ("RIG_BUILDER_LAYER_NOT_LOADED"); }
        }

        protected override string description
        {
            get {
                if (info_ == null) return Tr ("RIG_BUILDER_LAYER_UNREADABLE");
                if (stage_ == null || !stage_.canApplyRig) return Tr ("RIG_BUILDER_LAYER_CANNOT_APPLY");
                return Tr ("RIG_BUILDER_LAYER_DESCRIPTION");
            }
        }

        /// <summary>
        /// この層の重みを申告する。重みは「クリップから配られた値 × この段の重み」。
        /// 実際に解くのは段の並びの終端（Display Pose）。ストリームは表示モデルに 1 本なので、通すのは 1 回
        /// </summary>
        public override void Evaluate (float time)
        {
            if (info_ == null || stage_ == null) return;
            float data = info_.getWeight != null ? info_.getWeight () : info_.weight;
            stage_.SetRigLayerWeight (info_, data * weight);
            stage_.ApplyRig (siblings_);
        }
    }

}
