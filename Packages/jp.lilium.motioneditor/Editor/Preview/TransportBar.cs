using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// タイムライン上端の段: Auto Key の切替、ポーズのコピー/貼り付け/リセット、再生の transport、今のフレーム、Tracks の切替、Fit
    /// </summary>
    sealed class TransportBar : VisualElement
    {
        const float kHeight = 26;

        readonly PreviewWindow window_;
        readonly TimebarElement timebar_;
        /// <summary>パネルが載っている窓（ボタンの説明に出すショートカットは窓ごとに別）</summary>
        readonly EditorWindow host_;
        readonly Button playButton_;
        readonly IntegerField frameField_;
        readonly Label fractionLabel_;
        readonly Label rangeLabel_;
        readonly EnumField tracksField_;
        readonly Button autoKey_;
        readonly Button interpolate_;
        // 今出しているアイコンの状態（Refresh は姿勢を作るたびに来るので、変わったときだけ差し替える）
        bool? shownAutoKey_;
        bool? shownInterpolate_;

        public TransportBar (PreviewWindow window, TimebarElement timebar, EditorWindow host)
        {
            window_ = window;
            timebar_ = timebar;
            host_ = host;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.height = kHeight;
            style.paddingLeft = 6;
            style.paddingRight = 4;
            style.flexShrink = 0;

            VisualElement poseGroup = new VisualElement ();
            poseGroup.style.flexDirection = FlexDirection.Row;
            poseGroup.style.alignItems = Align.Center;
            // プロジェクト設定の Auto Key をここでも切り替える（設定のページまで行かずに済むように）。
            // 表示は、保存を受けた窓の stateChanged → Refresh で合わせる（設定のページで変えたときも同じ道を通る）
            // アイコンが今の状態を表す（赤のひし形 = 入 / 枠だけ = 切）。押すと切り替わる
            autoKey_ = new Button (() => {
                ProjectSettings settings = ProjectSettings.instance;
                settings.autoKey = !settings.autoKey;
                settings.Save ();
                Refresh ();
            }) { tooltip = Tr ("TIMELINE_OVERLAY_AUTO_KEY_TOOLTIP") };
            autoKey_.style.width = 24;
            autoKey_.style.height = 20;
            poseGroup.Add (autoKey_);
            // キーの間の補間を見せるか（切るとコマ送り。キーは書き換えない）
            // アイコンが今の見せ方（曲線でつなぐ = 補間する / 段 = コマ送り）を表し、押すと切り替わる
            interpolate_ = new Button (() => window_.interpolate = !window_.interpolate) { tooltip = Tr ("TIMELINE_OVERLAY_INTERPOLATE_TOOLTIP") };
            interpolate_.style.width = 24;
            interpolate_.style.height = 20;
            interpolate_.style.marginRight = 6;
            poseGroup.Add (interpolate_);
            poseGroup.Add (WithShortcut (MiniButton ("Key", null, window_.KeyAll), Tr ("TIMELINE_OVERLAY_KEY_TOOLTIP"), "Key All"));
            poseGroup.Add (WithShortcut (MiniButton ("Copy", null, window_.CopyPose), Tr ("TIMELINE_OVERLAY_COPY_POSE_TOOLTIP"), "Copy Pose"));
            poseGroup.Add (WithShortcut (MiniButton ("Paste", null, window_.PastePose), Tr ("TIMELINE_OVERLAY_PASTE_POSE_TOOLTIP"), "Paste Pose"));
            poseGroup.Add (MiniButton ("Reset", Tr ("TIMELINE_OVERLAY_RESET_POSE_TOOLTIP"), window_.ResetPose));
            Add (poseGroup);

            Add (Spacer ());

            VisualElement transport = new VisualElement ();
            transport.style.flexDirection = FlexDirection.Row;
            // 両端は先頭・末尾（Home / End と同じ）。読み込んだクリップは毎フレームにキーがあり、前後のキーでは端へ行けない
            transport.Add (WithShortcut (IconButton ("Animation.FirstKey", null, () => window_.SetFrame (0)), Tr ("TIMELINE_OVERLAY_FIRST_FRAME"), "First Frame"));
            transport.Add (WithShortcut (IconButton ("Animation.PrevKey", null, () => window_.GoToKey (-1)), Tr ("TIMELINE_OVERLAY_PREVIOUS_KEY"), "Previous Key"));
            playButton_ = WithShortcut (IconButton ("PlayButton", null, window_.TogglePlay), Tr ("TIMELINE_OVERLAY_PLAY"), "Play");
            transport.Add (playButton_);
            transport.Add (WithShortcut (IconButton ("Animation.NextKey", null, () => window_.GoToKey (1)), Tr ("TIMELINE_OVERLAY_NEXT_KEY"), "Next Key"));
            transport.Add (WithShortcut (IconButton ("Animation.LastKey", null, () => window_.SetFrame (window_.GetLastKeyFrame ())), Tr ("TIMELINE_OVERLAY_LAST_KEY_FRAME"), "Last Key Frame"));
            Add (transport);

            Add (Spacer ());

            VisualElement info = new VisualElement ();
            info.style.flexDirection = FlexDirection.Row;
            info.style.alignItems = Align.Center;

            frameField_ = new IntegerField { value = window_.frame };
            frameField_.style.width = 40;
            frameField_.RegisterValueChangedCallback (e => window_.SetFrame (e.newValue));
            info.Add (frameField_);

            // 今の時刻が格子から外れているとき（再生中など）は、端数とキーの行き先を出す
            fractionLabel_ = new Label ();
            fractionLabel_.style.width = 34;
            fractionLabel_.style.fontSize = 10;
            info.Add (fractionLabel_);

            rangeLabel_ = new Label ();
            rangeLabel_.style.width = 86;
            rangeLabel_.style.fontSize = 10;
            info.Add (rangeLabel_);

            tracksField_ = new EnumField (window_.showTracks);
            tracksField_.style.width = 70;
            tracksField_.RegisterValueChangedCallback (e => {
                window_.showTracks = (ShowTracksFlag)e.newValue;
                timebar_.RebuildTracks ();
            });
            info.Add (tracksField_);

            Button fit = new Button (timebar_.FitTimeline) { text = "Fit", tooltip = Tr ("TIMELINE_OVERLAY_FIT_TOOLTIP") };
            fit.style.width = 32;
            info.Add (fit);
            Add (info);

            Refresh ();
        }

        static VisualElement Spacer ()
        {
            VisualElement spacer = new VisualElement ();
            spacer.style.flexGrow = 1;
            return spacer;
        }

        static Button MiniButton (string text, string tooltip, System.Action click)
        {
            Button button = new Button (click) { text = text, tooltip = tooltip };
            button.style.width = 44;
            button.style.fontSize = 10;
            return button;
        }

        /// <summary>説明の後ろに、shortcut（[Shortcut] の名前）に今割り当てているキーを付ける</summary>
        Button WithShortcut (Button button, string tooltip, string shortcut)
        {
            ShortcutTooltip.Set (button, host_, tooltip, shortcut);
            return button;
        }

        static Button IconButton (string icon, string tooltip, System.Action click)
        {
            Button button = new Button (click) { tooltip = tooltip };
            Texture2D texture = Icons.LoadBuiltin (icon);
            if (texture != null) button.iconImage = Background.FromTexture2D (texture);
            else button.text = icon.Substring (0, 1);
            button.style.width = 28;
            button.style.height = 20;
            return button;
        }

        /// <summary>
        /// 窓の側（再生・フレーム・クリップ）で替わったとき表示を合わせる
        /// </summary>
        public void Refresh ()
        {
            if (window_ == null) return;

            Clock clock = window_.clock;
            Texture2D icon = Icons.LoadBuiltin (clock.isPlaying ? "PauseButton" : "PlayButton");
            if (icon != null) playButton_.iconImage = Background.FromTexture2D (icon);

            frameField_.SetValueWithoutNotify (window_.frame);
            if (!clock.onGrid) {
                fractionLabel_.text = clock.frame.ToString ("0.0");
                fractionLabel_.tooltip = Tr ("TIMELINE_OVERLAY_FRACTION_TOOLTIP", window_.frame);
                fractionLabel_.style.display = DisplayStyle.Flex;
            }
            else {
                fractionLabel_.style.display = DisplayStyle.None;
            }

            rangeLabel_.text = window_.targetClip != null ? "/ " + window_.GetLastKeyFrame () + "   " + clock.rate + " fps" : "";
            tracksField_.SetValueWithoutNotify (window_.showTracks);
            bool autoKeyOn = ProjectSettings.instance.autoKey;
            if (shownAutoKey_ != autoKeyOn) {
                shownAutoKey_ = autoKeyOn;
                Texture2D autoKey = Icons.Load (autoKeyOn ? "AutoKey" : "AutoKeyOff");
                if (autoKey != null) autoKey_.iconImage = Background.FromTexture2D (autoKey);
            }
            bool interpolate = window_.interpolate;
            if (shownInterpolate_ != interpolate) {
                shownInterpolate_ = interpolate;
                Texture2D interpolation = Icons.Load (interpolate ? "Interpolate" : "Stepped");
                if (interpolation != null) interpolate_.iconImage = Background.FromTexture2D (interpolation);
            }
        }
    }

}
