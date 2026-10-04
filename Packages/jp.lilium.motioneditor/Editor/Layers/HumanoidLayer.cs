using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Humanoid への変換。骨はつかめない。
    /// 持つクリップは見比べ用の参照（既存の Humanoid のクリップ。流れてきた姿勢に合成する。既定の 100% は上書き）だけ。焼いた版は Output の段が持つ（S20・S21）。
    /// 逆は近似（往復で骨の向きとねじりがずれる。2026-09-17 の実測）
    /// </summary>
    public sealed class HumanoidLayer : PoseLayer
    {
        readonly PreviewStage stage_;
        readonly IHumanoidHost host_;

        public HumanoidLayer (PreviewStage stage = null, IHumanoidHost host = null)
        {
            stage_ = stage;
            host_ = host;
            if (host != null) {
                clip = CreateClip (host);
                // 構造を編集している間だけ出る（S21。Humanoid Pose は足したり外したりできる段。位置は固定）
                headButtons.Add (new LayerClipButton {
                    label = Tr ("HUMANOID_LAYER_REMOVE"),
                    tooltip = Tr ("HUMANOID_LAYER_REMOVE_TOOLTIP"),
                    run = () => host.SetHumanoidPoseLayer (false),
                });
            }
        }

        /// <summary>
        /// 合成している Humanoid のクリップ（Humanoid でないクリップ・重み 0 は null）
        /// </summary>
        AnimationClip blending
        {
            get {
                AnimationClip humanoid = host_ != null ? host_.humanoidClip : null;
                if (humanoid == null || clipWeight <= 0) return null;
                return humanoid.humanMotion ? humanoid : null;
            }
        }

        LayerClip CreateClip (IHumanoidHost host)
        {
            LayerClip result = new LayerClip {
                label = Tr ("HUMANOID_LAYER_REFERENCE"),
                tooltip = Tr ("HUMANOID_LAYER_REFERENCE_TOOLTIP"),
                getClip = () => host.humanoidClip,
                assign = host.SetHumanoidClip,
                getWeight = () => clipWeight,
                setWeight = value => host.SetClipWeight (this, value),
                getStatus = () => {
                    AnimationClip humanoid = host.humanoidClip;
                    if (humanoid == null) return new LayerClipStatus (LayerClipState.None, null);
                    if (!humanoid.humanMotion) return new LayerClipStatus (LayerClipState.Blocked, Tr ("HUMANOID_LAYER_NOT_HUMANOID_CLIP"));
                    return new LayerClipStatus (LayerClipState.Stale, Tr ("HUMANOID_LAYER_BLENDING_FOR_COMPARISON", LayerClipText.Blending (clipWeight)));
                },
            };
            result.buttons.Add (new LayerClipButton {
                label = Tr ("HUMANOID_LAYER_APPLY"),
                tooltip = Tr ("HUMANOID_LAYER_APPLY_TOOLTIP"),
                run = () => host.ApplyHumanoidPoseToRig (),
                problem = host.GetHumanoidApplyProblem,
            });
            result.buttons.Add (OpenButton (() => host.humanoidClip));
            result.buttons.Add (new LayerClipButton {
                label = "Clear",
                tooltip = Tr ("HUMANOID_LAYER_CLEAR_TOOLTIP"),
                run = () => host.SetHumanoidClip (null),
                visible = () => host.humanoidClip != null,
            });

            return result;
        }

        /// <summary>
        /// クリップを Project で選び、いつものインスペクターに出す（ループ・ルートの扱いなどを直せる）
        /// </summary>
        internal static LayerClipButton OpenButton (System.Func<AnimationClip> getClip)
        {
            return new LayerClipButton {
                label = Tr ("HUMANOID_LAYER_OPEN"),
                tooltip = Tr ("HUMANOID_LAYER_OPEN_TOOLTIP"),
                run = () => {
                    AnimationClip target = getClip ();
                    if (target == null) return;
                    Selection.activeObject = target;
                    EditorGUIUtility.PingObject (target);
                },
                visible = () => getClip () != null,
            };
        }

        public override bool canEvaluate
        {
            get { return stage_ != null && stage_.canPreviewHumanoid; }
        }

        public override LayerKind kind { get { return LayerKind.HumanoidAnimation; } }
        public override int order { get { return PoseStack.kOrderHumanoid; } }
        public override string label { get { return "Humanoid Pose"; } }
        public override PosePhase phase { get { return PosePhase.Body; } }
        public override string unavailableReason { get { return Tr ("HUMANOID_LAYER_UNAVAILABLE"); } }
        public override InverseKind inverse { get { return InverseKind.Approximate; } }

        public override string inverseReason
        {
            get { return Tr ("HUMANOID_LAYER_INVERSE_REASON"); }
        }

        protected override string description
        {
            get { return canEvaluate ? Tr ("HUMANOID_LAYER_DESCRIPTION") : Tr ("HUMANOID_LAYER_NOT_HUMANOID_MODEL"); }
        }

        /// <summary>
        /// 編集用の体を Humanoid に直して表示モデルへ当てる（焼いたクリップを再生したときと同じ姿勢）
        /// </summary>
        public override void Evaluate (float time)
        {
            AnimationClip humanoid = blending;
            if (humanoid != null) stage_.BlendHumanoidClip (humanoid, time, clipWeight);
            else stage_.ApplyHumanoidPose ();
        }
    }

}
