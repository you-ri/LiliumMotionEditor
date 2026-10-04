using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 表示モデルの骨（出力）。上の段が申告した値（Rig の重み・ゲーム側の段）で**ストリームを 1 回通す**のはここ。
    /// 途中の段で通すと、段の順番と実際の処理の順番がずれる（ゲームの後段の処理が Rig の段の中で起きてしまう）。
    /// つかむと、上の姿勢の段をすべて逆に解いて書く
    /// </summary>
    public sealed class DisplayBoneLayer : PoseLayer
    {
        readonly PreviewStage stage_;

        public DisplayBoneLayer (PreviewStage stage = null)
        {
            stage_ = stage;
        }

        public override LayerKind kind { get { return LayerKind.DisplayBone; } }
        public override int order { get { return PoseStack.kOrderDisplayBone; } }
        public override string label { get { return "Display Pose"; } }
        public override PosePhase phase { get { return PosePhase.Terminal; } }
        public override GrabTarget grab { get { return GrabTarget.Pose; } }
        public override string unavailableReason { get { return Tr ("DISPLAY_BONE_LAYER_UNAVAILABLE"); } }
        public override bool canEvaluate { get { return stage_ != null && stage_.canApplyRig; } }

        /// <summary>
        /// 人型のキャラでは、終端で人型の骨を muscle で入れる（ゲームがクリップで流すのと同じ持ち方）ので、表示の骨をつかんで戻すと
        /// Humanoid の往復が 1 回入る。骨の軸まわりのねじりは配り直されて届かない（2026-09-18 実測: 腕を振る 30° で残差 3.7°、ねじり 30° で 12.5°）
        /// </summary>
        public override InverseKind inverse
        {
            get { return stage_ != null && stage_.animator != null && stage_.animator.isHuman ? InverseKind.Approximate : InverseKind.Exact; }
        }

        public override string inverseReason
        {
            get { return inverse == InverseKind.Approximate ? Tr ("DISPLAY_BONE_LAYER_INVERSE_APPROXIMATE") : Tr ("DISPLAY_BONE_LAYER_INVERSE_EXACT"); }
        }

        protected override string description
        {
            get {
                return stage_ != null && stage_.canApplyRig
                    ? Tr ("DISPLAY_BONE_LAYER_DESCRIPTION_WITH_GRAPH")
                    : Tr ("DISPLAY_BONE_LAYER_DESCRIPTION");
            }
        }

        public override void Evaluate (float time)
        {
            stage_.FlushPoseGraph ();
        }
    }

}
