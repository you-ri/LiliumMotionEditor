using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 焼いて書き出す段（S20・S21）。**この段より上を通った姿勢**を Humanoid のクリップに焼く。足す・消す・並びの中で動かすことができ、いくつも置ける。
    /// - Humanoid Pose の直後（Rig などより上）なら、編集用の体から焼き、Rig の重みとターゲットは値として焼く（今までの焼き方）
    /// - Rig・ゲーム側の段・Display Pose より下なら、そこまで段を通した表示モデルの骨から焼く。Rig を通した後なら Rig の値は焼かない（二重に掛からないように）
    /// Humanoid Pose の参照（見比べ用）と演出の段の合成は焼かない。姿勢は変えないので、👁・✏・✋ は持たない
    /// </summary>
    public sealed class OutputLayer : PoseLayer
    {
        readonly IOutputHost host_;

        /// <summary>並びの設定（名前と置き場所）</summary>
        public OutputEntry entry { get; private set; }

        public OutputLayer (IOutputHost host = null, OutputEntry entry = null)
        {
            host_ = host;
            this.entry = entry ?? new OutputEntry ();
            if (host != null) clip = CreateClip (host);
        }

        /// <summary>焼いたクリップの名前に足す名前（空なら 名前.anim）</summary>
        public string outputName
        {
            get { return entry.name ?? ""; }
        }

        /// <summary>並びの設定 1 つから、その Output の段の id を出す（段を作らずに「直前の段」の id を辿るため）</summary>
        public static string IdFor (OutputEntry entry)
        {
            return MakeId (LayerKind.Output, entry != null ? entry.name : null);
        }

        public override LayerKind kind { get { return LayerKind.Output; } }
        public override string idSuffix { get { return outputName; } }
        public override int order { get { return PoseStack.kOrderOutput; } }
        public override string label { get { return string.IsNullOrEmpty (outputName) ? "Output" : "Output (" + outputName + ")"; } }
        public override bool isPoseStage { get { return false; } }
        public override string unavailableReason { get { return Tr ("OUTPUT_LAYER_UNAVAILABLE"); } }

        protected override string description
        {
            get {
                string where = host_ != null ? host_.DescribeOutput (this) : null;
                return Tr ("OUTPUT_LAYER_DESCRIPTION", where);
            }
        }

        LayerClip CreateClip (IOutputHost host)
        {
            LayerClip output = new LayerClip {
                label = "Clip",
                tooltip = Tr ("OUTPUT_LAYER_CLIP_TOOLTIP"),
                getClip = () => {
                    string path = host.GetOutputPath (this);
                    return string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
                },
                getStatus = () => host.GetOutputStatus (this),
            };
            output.buttons.Add (HumanoidLayer.OpenButton (output.getClip));
            output.buttons.Add (new LayerClipButton {
                label = "Bake",
                tooltip = Tr ("OUTPUT_LAYER_BAKE_TOOLTIP"),
                run = () => host.BakeOutput (this),
                problem = () => host.GetOutputBakeProblem (this),
            });
            output.buttons.Add (new LayerClipButton {
                label = Tr ("OUTPUT_LAYER_SWAP"),
                tooltip = Tr ("OUTPUT_LAYER_SWAP_TOOLTIP"),
                run = () => host.SwapClipReferences (false),
                problem = host.GetSwapProblem,
                visible = () => host.bakeOverrides.Count > 0 && string.IsNullOrEmpty (outputName),
            });
            output.buttons.Add (new LayerClipButton {
                label = Tr ("OUTPUT_LAYER_REVERT"),
                tooltip = Tr ("OUTPUT_LAYER_REVERT_TOOLTIP"),
                run = () => host.SwapClipReferences (true),
                problem = host.GetSwapProblem,
                visible = () => host.bakeOverrides.Count > 0 && string.IsNullOrEmpty (outputName),
            });
            headButtons.Add (new LayerClipButton {
                label = "↑",
                tooltip = Tr ("OUTPUT_LAYER_MOVE_UP_TOOLTIP"),
                run = () => host.MoveOutput (this, -1),
                problem = () => host.GetMoveOutputProblem (this, -1),
            });
            headButtons.Add (new LayerClipButton {
                label = "↓",
                tooltip = Tr ("OUTPUT_LAYER_MOVE_DOWN_TOOLTIP"),
                run = () => host.MoveOutput (this, 1),
                problem = () => host.GetMoveOutputProblem (this, 1),
            });
            headButtons.Add (new LayerClipButton {
                label = Tr ("HUMANOID_LAYER_REMOVE"),
                tooltip = Tr ("OUTPUT_LAYER_REMOVE_TOOLTIP"),
                run = () => host.RemoveOutput (this),
            });
            output.toggles.Add (new LayerClipToggle {
                label = "Auto",
                tooltip = Tr ("OUTPUT_LAYER_AUTO_TOOLTIP"),
                get = () => host.autoBake,
                set = value => host.autoBake = value,
            });
            return output;
        }
    }

}
