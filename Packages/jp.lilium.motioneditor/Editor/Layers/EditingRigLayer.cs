using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 編集用リグ。保存するクリップ（*.rig.anim）を持ち、その値（リグの操作値・Rig の代理の重みとターゲット）を当ててから骨へ解く。
    /// 書き出し先になる唯一の組み込みの段。つかむのはリグの操作値で、値はこの段のクリップにあるので、つかむ段と書く段が同じになる。
    /// 下の段の骨をつかんだときは、骨から操作値を求め直して逆に通る（姿勢の段でもある）
    /// </summary>
    public sealed class EditingRigLayer : PoseLayer
    {
        readonly PreviewStage stage_;
        readonly IEditClipHost host_;

        public EditingRigLayer (PreviewStage stage = null, IEditClipHost host = null)
        {
            stage_ = stage;
            host_ = host;
            if (host != null) clip = CreateClip (host);
        }

        AnimationClip source
        {
            get { return host_ != null ? host_.clip : null; }
        }

        public override LayerKind kind { get { return LayerKind.EditingRig; } }
        public override int order { get { return PoseStack.kOrderEditingRig; } }
        public override string label { get { return "Editing Rig"; } }
        // クリップが無くても、基準の値で解く
        public override bool canEvaluate { get { return stage_ != null; } }
        public override PosePhase phase { get { return PosePhase.Body; } }
        public override bool isData { get { return true; } }
        public override bool isPoseStage { get { return true; } }
        public override GrabTarget grab { get { return GrabTarget.Values; } }
        public override string inverseReason { get { return Tr ("EDITING_RIG_LAYER_INVERSE_REASON"); } }

        protected override string description
        {
            get {
                EditingRigSolver solver = stage_ != null ? stage_.editingRigSolver : null;
                if (solver == null) return stage_ != null ? Tr ("EDITING_RIG_LAYER_NO_SOLVER") : "";
                string text = Tr ("EDITING_RIG_LAYER_SOLVER_COUNTS", solver.fkCount, solver.ikCount);
                RigProxies proxies = stage_.editingRig.rigProxies;
                if (proxies != null && proxies.rigs.Count > 0) {
                    text += Tr ("EDITING_RIG_LAYER_RIG_VALUE_COUNTS", proxies.weights.Count, proxies.sources.Count);
                }
                return text;
            }
        }

        public override void Evaluate (float time)
        {
            // 基準姿勢へ戻すのは段ではなくスタックの前処理（PoseStack.Evaluate の先頭）なので、ここでは当てるだけ。
            // 重みが 1 未満なら、コントロールの基準の値へ合成する
            // 土台は編集用クリップか、Humanoid のクリップをフレームごとに操作値へ直したもの（S14）
            stage_.ClearOverrideSource ();
            if (host_ != null && host_.useHumanoidBase) stage_.SampleHumanoidAsControls (host_.clip, time);
            else if (source != null) stage_.BlendDataClip (source, time, clipWeight);
            SolveAndShow ();
            // 任意のプロパティ（表示の ON/OFF・ブレンドシェイプなど）は表示モデルの部品へ直接入れる
            stage_.ApplyPropertyCurves (source, time);
        }

        /// <summary>
        /// 操作値を直に動かした後。クリップを当て直すと動かした値が消えるので、解くだけにする。
        /// 編集用の体を表示モデルへ写すと、任意のプロパティで入れた Transform の値（武器の握りなど）が編集用の体の値へ戻るので、
        /// 控えて入れ直す（戻すと、動かした瞬間に武器が飛んでいた。持ち替えた後で 2.4。2026-09-30 実測）
        /// </summary>
        public override void EvaluateKeepingValues (float time)
        {
            List<KeyValuePair<EditorCurveBinding, float>> properties = stage_.CapturePropertyValues ();
            SolveAndShow ();
            stage_.RestorePropertyValues (properties);
        }

        /// <summary>
        /// 解いた編集用の体を表示モデルへ写す。後ろの段（Generic Pose・Humanoid Pose）はこの姿勢を受け取って上書きする
        /// </summary>
        void SolveAndShow ()
        {
            stage_.Solve ();
            if (stage_.editingRig != null) stage_.editingRig.SyncToDisplay ();
            stage_.InvalidateHumanoidPose ();
        }

        /// <summary>
        /// 編集するクリップ。欄は今開いているクリップを見せるだけ（開くのは AnimBank・New・取込）
        /// </summary>
        LayerClip CreateClip (IEditClipHost host)
        {
            LayerClip result = new LayerClip {
                label = Tr ("EDITING_RIG_LAYER_CLIP_LABEL"),
                tooltip = Tr ("EDITING_RIG_LAYER_CLIP_TOOLTIP"),
                getClip = () => host.clip,
                getWeight = () => clipWeight,
                setWeight = value => host.SetClipWeight (this, value),
                getStatus = () => {
                    if (host.importPreviewing) {
                        return new LayerClipStatus (LayerClipState.Stale, Tr ("EDITING_RIG_LAYER_IMPORT_PREVIEWING"));
                    }
                    if (host.useHumanoidBase) {
                        return new LayerClipStatus (LayerClipState.Ready, Tr ("EDITING_RIG_LAYER_HUMANOID_BASE"));
                    }
                    if (host.clip == null) return new LayerClipStatus (LayerClipState.None, Tr ("EDITING_RIG_LAYER_NO_CLIP"));
                    if (host.clipProblem != null) return new LayerClipStatus (LayerClipState.Blocked, Tr ("EDITING_RIG_LAYER_CANNOT_EDIT", host.clipProblem));
                    if (clipWeight < 1) return new LayerClipStatus (LayerClipState.Stale, Tr ("EDITING_RIG_LAYER_BLENDING_NO_KEYS", Mathf.RoundToInt (clipWeight * 100)));
                    int stray = host.strayCurveCount;
                    if (stray > 0) return new LayerClipStatus (LayerClipState.Stale, Tr ("EDITING_RIG_LAYER_STRAY_CURVES", stray));
                    return new LayerClipStatus (LayerClipState.Ready, null);
                },
            };
            result.buttons.Add (new LayerClipButton {
                label = "New",
                tooltip = Tr ("EDITING_RIG_LAYER_NEW_TOOLTIP"),
                run = host.CreateClip,
            });
            result.buttons.Add (new LayerClipButton {
                label = "Clean",
                tooltip = Tr ("EDITING_RIG_LAYER_CLEAN_TOOLTIP"),
                run = host.RemoveStrayCurves,
                visible = () => host.strayCurveCount > 0,
            });
            result.buttons.Add (new LayerClipButton {
                label = Tr ("EDITING_RIG_LAYER_MERGE"),
                tooltip = Tr ("EDITING_RIG_LAYER_MERGE_TOOLTIP"),
                run = () => host.MergeOverrides (),
                problem = host.GetMergeOverridesProblem,
                visible = () => host.overrides.Count > 0,
            });
            result.buttons.Add (new LayerClipButton {
                label = Tr ("EDITING_RIG_LAYER_IMPORT"),
                tooltip = Tr ("EDITING_RIG_LAYER_IMPORT_TOOLTIP"),
                run = host.PickImportClip,
                problem = host.GetPickImportProblem,
            });
            return result;
        }
    }

}
