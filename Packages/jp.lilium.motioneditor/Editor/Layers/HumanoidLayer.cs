using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;

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
                    label = "削除",
                    tooltip = "Humanoid Pose の段を外す（見比べ用の参照と反映も無くなる。構造を編集の「+Humanoid Pose」で足し直せる）",
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
                label = "参照",
                tooltip = "見比べる Humanoid のクリップ。流れてきた姿勢に重みで合成して表示するだけで、編集中のクリップは変えない（反映で今のフレームだけキーにできる）",
                getClip = () => host.humanoidClip,
                assign = host.SetHumanoidClip,
                getWeight = () => clipWeight,
                setWeight = value => host.SetClipWeight (this, value),
                getStatus = () => {
                    AnimationClip humanoid = host.humanoidClip;
                    if (humanoid == null) return new LayerClipStatus (LayerClipState.None, null);
                    if (!humanoid.humanMotion) return new LayerClipStatus (LayerClipState.Blocked, "Humanoid のクリップではないので合成しない");
                    return new LayerClipStatus (LayerClipState.Stale, LayerClipText.Blending (clipWeight) + "（見比べ用。編集中のクリップは変えない）");
                },
            };
            result.buttons.Add (new LayerClipButton {
                label = "反映",
                tooltip = "今のフレームの姿勢（Humanoid に直した姿勢）を編集用リグの値に直して、そのフレームにキーを打つ",
                run = () => host.ApplyHumanoidPoseToRig (),
                problem = host.GetHumanoidApplyProblem,
            });
            result.buttons.Add (OpenButton (() => host.humanoidClip));
            result.buttons.Add (new LayerClipButton {
                label = "Clear",
                tooltip = "合成をやめて、編集中の姿勢だけを表示する",
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
                label = "開く",
                tooltip = "クリップを Project で選んでインスペクターに出す（ループ・ルートの扱いなど）",
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
        public override string unavailableReason { get { return "設定だけを持つ段"; } }
        public override InverseKind inverse { get { return InverseKind.Approximate; } }

        public override string inverseReason
        {
            get { return "Humanoid の往復。骨の向き 約 1°・位置 約 1cm ずれ、ねじりは配り直される"; }
        }

        protected override string description
        {
            get { return canEvaluate ? "焼いたときの姿勢（可動範囲で丸め、ねじりを配り直す）。目（👁）を落とすと焼く前の姿勢" : "表示モデルが Humanoid でない"; }
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
