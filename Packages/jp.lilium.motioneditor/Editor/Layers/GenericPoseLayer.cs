using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 編集用ボーンの姿勢。つかむのは骨で、編集用リグを逆に解いて操作値へ書く。
    /// 既定の並びには入れていない（段の並びを選べるようにしたときに足す）。
    /// 持つクリップは、骨を直接動かす Generic のクリップ。流れてきた姿勢に重みで合成する（既定の 100% は、カーブのある骨の上書き）
    /// </summary>
    public sealed class GenericPoseLayer : PoseLayer
    {
        readonly PreviewStage stage_;
        readonly IGenericHost host_;

        public GenericPoseLayer (PreviewStage stage = null, IGenericHost host = null)
        {
            stage_ = stage;
            host_ = host;
            if (host != null) clip = CreateClip (host);
        }

        /// <summary>
        /// 合成している Generic のクリップ（合成できないクリップ・重み 0 は null）
        /// </summary>
        AnimationClip blending
        {
            get {
                AnimationClip generic = host_ != null ? host_.genericClip : null;
                if (generic == null || clipWeight <= 0) return null;
                return GenericImport.GetProblem (generic) == null ? generic : null;
            }
        }

        LayerClip CreateClip (IGenericHost host)
        {
            LayerClip result = new LayerClip {
                label = "Clip",
                tooltip = "骨を直接動かす Generic のクリップ（旧形式など）。流れてきた姿勢に重みで合成する。Import で合成した姿勢を Editing Rig のクリップへ書く",
                getClip = () => host.genericClip,
                assign = host.SetGenericClip,
                getWeight = () => clipWeight,
                setWeight = value => host.SetClipWeight (this, value),
                getStatus = () => {
                    AnimationClip generic = host.genericClip;
                    if (generic == null) return new LayerClipStatus (LayerClipState.None, null);
                    string problem = GenericImport.GetProblem (generic);
                    if (problem != null) return new LayerClipStatus (LayerClipState.Blocked, "合成できない: " + problem);
                    string text = LayerClipText.Blending (clipWeight);
                    string importProblem = host.GetImportProblem ();
                    return importProblem != null
                        ? new LayerClipStatus (LayerClipState.Blocked, text + "。取り込めない: " + importProblem)
                        : new LayerClipStatus (LayerClipState.Stale, text + "。Import で合成した姿勢を Editing Rig のクリップへ書き、ここを空にする");
                },
            };
            result.buttons.Add (new LayerClipButton {
                label = "Import",
                tooltip = "この段の出力（クリップを合成した姿勢）を編集用リグの値に直して Editing Rig のクリップへ書く（そのクリップの編集用リグのカーブは置き換える）",
                run = () => host.ImportGenericClip (),
                problem = host.GetImportProblem,
                visible = () => host.genericClip != null,
            });
            result.buttons.Add (new LayerClipButton {
                label = "Clear",
                tooltip = "合成をやめる",
                run = () => host.SetGenericClip (null),
                visible = () => host.genericClip != null,
            });
            return result;
        }

        public override bool canEvaluate
        {
            get { return stage_ != null && stage_.editingRig != null && stage_.editingRig.root != null; }
        }

        public override LayerKind kind { get { return LayerKind.GenericPose; } }
        public override int order { get { return PoseStack.kOrderGenericPose; } }
        public override string label { get { return "Generic Pose"; } }
        public override PosePhase phase { get { return PosePhase.Body; } }
        public override GrabTarget grab { get { return GrabTarget.Pose; } }
        public override string unavailableReason { get { return "値を持たない（保存は Editing Rig のクリップ）"; } }

        protected override string description
        {
            get { return "編集用の体（表示モデルへ写す）"; }
        }

        /// <summary>
        /// Generic のクリップを編集用の骨へ重みで合成して、表示モデルへ写し直す（合成しないときも写す）
        /// </summary>
        public override void Evaluate (float time)
        {
            if (stage_ == null || stage_.editingRig == null) return;
            AnimationClip generic = blending;
            if (generic != null) stage_.BlendGenericClip (generic, time, clipWeight);
            stage_.editingRig.SyncToDisplay ();
            stage_.InvalidateHumanoidPose ();
        }
    }

}
