using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 元のクリップ（その上の段）を書き換えずに、差分を重ねる段（S14。1 枚のクリップが 1 段。09-20 ユーザー指示で段にした）。
    ///
    /// - 重ねるのは編集用リグの操作値（コントロール）。同じコントロールの値同士しか混ぜないので、空間の違う値は混ざらない。
    /// - Additive は元に差分を足し（回転は後ろに掛ける）、Override はキーのある値を置き換える。どちらも段の重みで元と混ぜる。
    /// - 👁 を落とすと素通し（ミュート）。
    /// - ✏（書き出し先）にすると、キーを打つ・動かす・消す操作がこの段のクリップへ差分として入る。
    ///   **✏ より下の（後から重なる）段は、編集中は効かせない。** 効かせたまま書くと、下の段の分まで ✏ の段へ入ってしまうため
    /// </summary>
    public sealed class OverrideLayer : PoseLayer
    {
        readonly PreviewStage stage_;
        readonly IOverrideHost host_;
        readonly int index_;

        public OverrideLayer (PreviewStage stage, IOverrideHost host, int index)
        {
            stage_ = stage;
            host_ = host;
            index_ = index;
            clip = CreateClip ();
        }

        /// <summary>何枚目の Override か（下から 0）</summary>
        public int index
        {
            get { return index_; }
        }

        OverrideClip entry
        {
            get { return host_ != null && index_ < host_.overrides.Count ? host_.overrides[index_] : null; }
        }

        /// <summary>この段が重ねるクリップ</summary>
        public AnimationClip overrideClip
        {
            get { return entry != null ? entry.clip : null; }
        }

        public OverrideMode mode
        {
            get { return entry != null ? entry.mode : OverrideMode.Additive; }
        }

        public override LayerKind kind { get { return LayerKind.Override; } }
        public override int order { get { return PoseStack.kOrderOverride + index_; } }
        public override string idSuffix { get { return index_.ToString (); } }
        public override string label { get { return "Override " + (index_ + 1); } }
        public override bool isData { get { return true; } }
        public override bool isPoseStage { get { return true; } }
        public override bool canEvaluate { get { return stage_ != null && overrideClip != null; } }
        public override PosePhase phase { get { return PosePhase.Body; } }
        public override bool canWrite { get { return overrideClip != null; } }
        public override bool hasWeight { get { return true; } }
        public override GrabTarget grab { get { return GrabTarget.Values; } }
        public override string unavailableReason { get { return overrideClip == null ? Tr ("OVERRIDE_LAYER_NO_CLIP") : null; } }
        public override string inverseReason { get { return Tr ("OVERRIDE_LAYER_INVERSE_REASON"); } }

        /// <summary>編集中に効かせない（✏ の段より下）か</summary>
        bool belowWriteLayer
        {
            get {
                int write = host_ != null ? host_.overrideWriteIndex : -1;
                return index_ > write;
            }
        }

        protected override string description
        {
            get {
                if (overrideClip == null) return Tr ("OVERRIDE_LAYER_NO_CLIP_DESCRIPTION");
                string text = mode == OverrideMode.Additive ? Tr ("OVERRIDE_LAYER_ADDITIVE_DESCRIPTION") : Tr ("OVERRIDE_LAYER_OVERRIDE_DESCRIPTION");
                if (belowWriteLayer) text += Tr ("OVERRIDE_LAYER_BELOW_WRITE_SUFFIX");
                return text;
            }
        }

        LayerClip CreateClip ()
        {
            OverrideMode next = mode == OverrideMode.Additive ? OverrideMode.Override : OverrideMode.Additive;
            LayerClip result = new LayerClip {
                label = "Clip",
                tooltip = Tr ("OVERRIDE_LAYER_CLIP_TOOLTIP"),
                getClip = () => overrideClip,
                assign = value => host_.SetOverrideClip (index_, value),
                getStatus = () => {
                    if (overrideClip == null) return new LayerClipStatus (LayerClipState.None, Tr ("OVERRIDE_LAYER_NO_CLIP"));
                    if (belowWriteLayer) return new LayerClipStatus (LayerClipState.Stale, Tr ("OVERRIDE_LAYER_BELOW_WRITE"));
                    return new LayerClipStatus (LayerClipState.Ready, mode.ToString ());
                },
            };
            // 並べ替えと外すのは段の名前の行（Output の段と同じ）。ファイルごと消すのはクリップの行
            headButtons.Add (new LayerClipButton {
                label = "↑",
                tooltip = Tr ("OVERRIDE_LAYER_MOVE_UP_TOOLTIP"),
                run = () => host_.MoveOverride (index_, -1),
                problem = () => host_.GetMoveOverrideProblem (index_, -1),
            });
            headButtons.Add (new LayerClipButton {
                label = "↓",
                tooltip = Tr ("OVERRIDE_LAYER_MOVE_DOWN_TOOLTIP"),
                run = () => host_.MoveOverride (index_, 1),
                problem = () => host_.GetMoveOverrideProblem (index_, 1),
            });
            headButtons.Add (new LayerClipButton {
                label = Tr ("HUMANOID_LAYER_REMOVE"),
                tooltip = Tr ("OVERRIDE_LAYER_REMOVE_TOOLTIP"),
                run = () => host_.RemoveOverride (index_, false),
            });
            result.buttons.Add (new LayerClipButton {
                label = Tr ("OVERRIDE_LAYER_DELETE_FILE"),
                tooltip = Tr ("OVERRIDE_LAYER_DELETE_FILE_TOOLTIP"),
                run = () => host_.RemoveOverride (index_, true),
                visible = () => host_.layerStructureEditing,
            });
            result.buttons.Add (new LayerClipButton {
                label = mode.ToString (),
                tooltip = Tr ("OVERRIDE_LAYER_MODE_TOOLTIP", next),
                run = () => host_.SetOverrideMode (index_, next),
            });
            return result;
        }

        /// <summary>
        /// 差分を今のコントロールの値へ重ねて、解き直す
        /// </summary>
        public override void Evaluate (float time)
        {
            if (belowWriteLayer) return;
            // ✏ の段は、重ねる前の値を控える（書くときの元になる）
            if (host_ != null && index_ == host_.overrideWriteIndex) stage_.CaptureOverrideSource ();
            stage_.ApplyOverride (overrideClip, mode, weight, time);
            stage_.Solve ();
            if (stage_.editingRig != null) stage_.editingRig.SyncToDisplay ();
            stage_.InvalidateHumanoidPose ();
        }
    }

}
