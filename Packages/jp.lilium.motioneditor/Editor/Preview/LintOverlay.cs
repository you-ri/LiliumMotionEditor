using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// モーションの検査（S37。MotionLint）の件数と一覧。Check で今のクリップ（と Override）を最初から最後まで検査する。
    /// 窓の姿勢を動かさないよう、窓を開かずに姿勢を作る PoseSession で検査する。行をクリックすると、そのフレームへ移る。
    /// Copy は結果を JSON でクリップボードへ（AI に渡すため）
    /// </summary>
    [Overlay (typeof (PreviewWindow), "mkt-lint", "Lint", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    class LintOverlay : MotionOverlay
    {
        const int kRowHeight = 18;
        const float kWidth = 230;

        Label summary_;
        ListView list_;
        MotionLintReport report_;
        bool stale_;
        readonly List<MotionLintIssue> rows_ = new List<MotionLintIssue> ();

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = kWidth;
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            window_ = ResolveWindow ();
            if (window_ == null) return root;

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            Button check = new Button (Check) { text = "Check", tooltip = Tr ("LINT_OVERLAY_CHECK_TOOLTIP") };
            check.style.flexGrow = 1;
            buttons.Add (check);
            Button copy = new Button (Copy) { text = "Copy", tooltip = Tr ("LINT_OVERLAY_COPY_TOOLTIP") };
            copy.style.flexGrow = 1;
            buttons.Add (copy);
            root.Add (buttons);

            summary_ = new Label ();
            summary_.style.whiteSpace = WhiteSpace.Normal;
            summary_.style.fontSize = 10;
            root.Add (summary_);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = () => {
                    Label label = new Label ();
                    label.style.unityTextAlign = TextAnchor.MiddleLeft;
                    label.style.fontSize = 10;
                    label.style.overflow = Overflow.Hidden;
                    label.style.textOverflow = TextOverflow.Ellipsis;
                    return label;
                },
                bindItem = (element, index) => {
                    MotionLintIssue issue = rows_[index];
                    Label label = (Label)element;
                    label.text = Describe (issue);
                    label.tooltip = label.text;
                },
                itemsSource = rows_,
            };
            list_.style.height = 160;
            list_.selectionChanged += selected => {
                MotionLintIssue issue = selected.OfType<MotionLintIssue> ().FirstOrDefault ();
                if (issue != null && window_ != null) window_.SetFrame (issue.frame);
            };
            root.Add (list_);

            Refresh ();
            Resubscribe ();
            return root;
        }

        protected override void Subscribe (bool on)
        {
            window_.keysChanged -= OnKeysChanged;
            if (!on) return;
            window_.keysChanged += OnKeysChanged;
        }

        void OnKeysChanged ()
        {
            if (report_ == null) return;
            stale_ = true;
            Refresh ();
        }

        void Check ()
        {
            PreviewStage stage = window_ != null ? window_.stage : null;
            AnimationClip clip = window_ != null ? window_.baseClip : null;
            if (stage == null || stage.sourcePrefab == null || clip == null) {
                report_ = new MotionLintReport { error = Tr ("LINT_OVERLAY_NO_CLIP") };
            }
            else {
                using (PoseSession session = new PoseSession (stage.sourcePrefab, window_.rigDefinition)) {
                    session.clip = clip;
                    foreach (OverrideClip entry in window_.overrides) session.AddOverride (entry.clip, entry.mode, entry.mask);
                    session.overrideWriteIndex = window_.overrideWriteIndex;
                    int end = Mathf.Max (0, Mathf.RoundToInt (clip.length * clip.frameRate));
                    report_ = MotionLint.Run (session, 0, end);
                }
            }
            stale_ = false;
            Refresh ();
        }

        void Copy ()
        {
            if (report_ == null) return;
            EditorGUIUtility.systemCopyBuffer = report_.ToJson ();
        }

        void Refresh ()
        {
            if (summary_ == null) return;
            rows_.Clear ();
            if (report_ == null) {
                summary_.text = Tr ("LINT_OVERLAY_NOT_CHECKED");
            }
            else if (report_.error != null) {
                summary_.text = report_.error;
            }
            else {
                rows_.AddRange (report_.issues);
                string text = report_.issues.Count == 0
                    ? Tr ("LINT_OVERLAY_NO_ISSUES", report_.startFrame, report_.endFrame)
                    : Tr ("LINT_OVERLAY_SUMMARY", report_.issues.Count, report_.Count (MotionLint.kFootSlide), report_.Count (MotionLint.kFloorPenetration),
                        report_.Count (MotionLint.kMuscleRange), report_.Count (MotionLint.kPop), report_.Count (MotionLint.kLargeKeyRotation),
                        report_.Count (MotionLint.kLoopVelocity), report_.Count (MotionLint.kHipSpeed), report_.Count (MotionLint.kBetween));
                text += "\n" + Tr ("LINT_OVERLAY_ASYMMETRY", report_.maxAsymmetry.ToString ("0.00"), report_.asymmetryFrame);
                if (stale_) text += "\n" + Tr ("LINT_OVERLAY_STALE");
                summary_.text = text;
            }
            list_.RefreshItems ();
        }

        static string Describe (MotionLintIssue issue)
        {
            string frames = issue.endFrame > issue.frame ? issue.frame + "-" + issue.endFrame + "F" : issue.frame + "F";
            string value;
            switch (issue.kind) {
                case MotionLint.kFootSlide:
                case MotionLint.kFloorPenetration:
                    value = (issue.value * 1000).ToString ("0.0") + "mm";
                    break;
                case MotionLint.kPop:
                case MotionLint.kLargeKeyRotation:
                case MotionLint.kLoopVelocity:
                    value = issue.value.ToString ("0.0") + "°";
                    break;
                case MotionLint.kHipSpeed:
                    value = issue.value.ToString ("0.0") + "m/s";
                    break;
                case MotionLint.kBetween:
                    value = "";
                    break;
                default:
                    value = issue.value.ToString ("0.00");
                    break;
            }
            return frames + "  " + issue.kind + "  " + issue.target + "  " + value;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Overlay (typeof (SceneWindow), "mkt-scene-lint", "Lint", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    sealed class LintOverlayScene : LintOverlay
    {
    }

}
