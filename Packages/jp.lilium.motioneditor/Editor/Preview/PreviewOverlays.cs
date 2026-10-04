using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEditor.UIElements;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// プレビュー窓の上端のツールバー Context（編集するキャラ、キャラの設定、作り直し）。
    /// クリップは段が持つので Layers のパネルの行で選ぶ（どの段にどのクリップが付いているかを見せるため）
    /// </summary>
    [Icon (Icons.kFolder + "Motion.png")]
    [Overlay (typeof (PreviewWindow), "mkt-preview-toolbar", "Context", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top)]
    class PreviewToolbarOverlay : Overlay, ICreateHorizontalToolbar
    {
        PreviewWindow window_;
        ObjectField model_;
        ObjectField settings_;
        EditorToolbarButton createSettings_;
        ObjectField context_;
        IntegerField contextOffset_;
        EditorToolbarDropdown timelineClip_;
        IntegerField masterFrame_;
        EditorToolbarToggle contextCamera_;
        EditorToolbarToggle followTimeline_;

        public override VisualElement CreatePanelContent ()
        {
            return CreateHorizontalToolbarContent ();
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return toolbar;

            model_ = new ObjectField { objectType = typeof (GameObject), allowSceneObjects = false, tooltip = Tr ("PREVIEW_OVERLAYS_MODEL_TOOLTIP") };
            model_.style.width = 180;
            model_.RegisterValueChangedCallback (e => window_.SetModel (e.newValue as GameObject));
            toolbar.Add (model_);

            toolbar.Add (new EditorToolbarButton ("Reload", window_.RebuildStage) { tooltip = Tr ("PREVIEW_OVERLAYS_RELOAD_TOOLTIP") });

            // このキャラの設定（S19）。無ければ作れる。既にある設定を入れると、それをこのキャラに結び付ける
            settings_ = new ObjectField {
                objectType = typeof (CharacterSettings),
                allowSceneObjects = false,
                tooltip = Tr ("PREVIEW_OVERLAYS_SETTINGS_TOOLTIP"),
            };
            settings_.style.width = 160;
            // 設定は prefab から引き当てる。入れたアセットはこのキャラに結び付け、空にしたとき・やめたときは今のアセットに戻す
            settings_.RegisterValueChangedCallback (e => {
                window_.AssignCharacterSettings (e.newValue as CharacterSettings);
                settings_.SetValueWithoutNotify (window_.characterSettings);
            });
            toolbar.Add (settings_);
            createSettings_ = new EditorToolbarButton (Tr ("PREVIEW_OVERLAYS_CREATE_SETTINGS"), () => window_.CreateCharacterSettings ()) { tooltip = Tr ("PREVIEW_OVERLAYS_CREATE_SETTINGS_TOOLTIP") };
            toolbar.Add (createSettings_);

            // 一緒に見る演出（Timeline を持つ prefab）。時計は窓が正で、フレームを動かすと演出もそのフレームになる
            context_ = new ObjectField {
                objectType = typeof (GameObject),
                allowSceneObjects = false,
                tooltip = Tr ("PREVIEW_OVERLAYS_CONTEXT_TOOLTIP"),
            };
            context_.style.width = 160;
            context_.RegisterValueChangedCallback (e => window_.SetContext (e.newValue as GameObject));
            toolbar.Add (context_);

            contextOffset_ = new IntegerField { tooltip = Tr ("PREVIEW_OVERLAYS_CONTEXT_OFFSET_TOOLTIP") };
            contextOffset_.style.width = 44;
            contextOffset_.RegisterValueChangedCallback (e => window_.SetContextOffset (e.newValue));
            toolbar.Add (contextOffset_);

            // 演出のどのクリップとして見るか（S15b）。結び付けると、演出の時刻はそのクリップの置き方（開始・頭出し・速さ）で決まる
            timelineClip_ = new EditorToolbarDropdown ("Clip", ShowTimelineClipMenu) {
                tooltip = Tr ("PREVIEW_OVERLAYS_TIMELINE_CLIP_TOOLTIP"),
            };
            toolbar.Add (timelineClip_);

            masterFrame_ = new IntegerField { tooltip = Tr ("PREVIEW_OVERLAYS_MASTER_FRAME_TOOLTIP") };
            masterFrame_.style.width = 44;
            masterFrame_.RegisterValueChangedCallback (e => window_.SetMasterFrame (e.newValue));
            toolbar.Add (masterFrame_);

            // 演出のカメラ割りで見る・Timeline 窓の再生位置に付いていく（S15c）
            contextCamera_ = new EditorToolbarToggle { text = "Cam", tooltip = Tr ("PREVIEW_OVERLAYS_CAM_TOOLTIP") };
            contextCamera_.RegisterValueChangedCallback (e => window_.SetUseContextCamera (e.newValue));
            toolbar.Add (contextCamera_);
            followTimeline_ = new EditorToolbarToggle { text = "Follow", tooltip = Tr ("PREVIEW_OVERLAYS_FOLLOW_TOOLTIP") };
            followTimeline_.RegisterValueChangedCallback (e => window_.SetFollowTimelineWindow (e.newValue));
            toolbar.Add (followTimeline_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            window_.poseSampled -= RefreshTime;
            window_.poseSampled += RefreshTime;
            return toolbar;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.poseSampled -= RefreshTime;
            }
            base.OnWillBeDestroyed ();
        }

        void ShowTimelineClipMenu ()
        {
            if (window_ == null) return;
            GenericMenu menu = new GenericMenu ();
            ContextClip current = window_.timelineClip;
            menu.AddItem (new GUIContent (Tr ("PREVIEW_OVERLAYS_TIMELINE_CLIP_AUTO")), window_.timelineClipAuto, () => window_.SetTimelineClip (null));
            menu.AddItem (new GUIContent (Tr ("PREVIEW_OVERLAYS_TIMELINE_CLIP_UNLINKED")), !window_.timelineClipAuto && current == null, window_.SetTimelineClipNone);
            menu.AddSeparator ("");
            double rate = window_.contextFrameRate;
            foreach (ContextClip candidate in window_.timelineClipCandidates) {
                ContextClip item = candidate;
                string text = Tr ("PREVIEW_OVERLAYS_TIMELINE_CLIP_ITEM", item.label, Mathf.RoundToInt ((float)(item.start * rate)), Mathf.RoundToInt ((float)(item.end * rate)))
                    + (System.Math.Abs (item.timeScale - 1) > 1e-6 ? "  ×" + item.timeScale.ToString ("0.###") : "");
                // メニューの「/」は階層になるので置き換える
                menu.AddItem (new GUIContent (text.Replace ("/", "∕")), !window_.timelineClipAuto && current == item, () => window_.SetTimelineClip (item.key));
            }
            menu.ShowAsContext ();
        }

        /// <summary>
        /// フレームを動かしたとき（演出の時刻の欄）
        /// </summary>
        void RefreshTime ()
        {
            if (window_ == null || masterFrame_ == null) return;
            if (window_.followsTimelineClip) masterFrame_.SetValueWithoutNotify (window_.masterFrame);
        }

        /// <summary>
        /// 窓の側で替わったとき（ドラッグ＆ドロップ・新規作成など）に表示を合わせる
        /// </summary>
        void Refresh ()
        {
            if (window_ == null || model_ == null) return;
            model_.SetValueWithoutNotify (window_.model);
            if (settings_ != null) {
                CharacterSettings settings = window_.characterSettings;
                settings_.SetValueWithoutNotify (settings);
                settings_.style.display = window_.model != null ? DisplayStyle.Flex : DisplayStyle.None;
                createSettings_.style.display = settings == null && window_.model != null ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // Motion Scene では演出は開いているシーンのもの（Timeline 窓が開いているもの）を使うので、prefab の演出は選ばせない
            bool inScene = window_.sceneTarget != null;
            if (context_ != null) {
                context_.SetValueWithoutNotify (window_.contextPrefab);
                context_.style.display = inScene ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (contextOffset_ != null) contextOffset_.SetValueWithoutNotify (window_.contextOffset);
            if (timelineClip_ == null) return;
            bool hasCandidates = window_.timelineClipCandidates.Count > 0;
            bool follows = window_.followsTimelineClip;
            ContextClip current = window_.timelineClip;
            timelineClip_.style.display = hasCandidates ? DisplayStyle.Flex : DisplayStyle.None;
            timelineClip_.text = current != null && current.clip != null ? current.clip.name : Tr ("PREVIEW_OVERLAYS_TIMELINE_CLIP_NONE");
            // 結び付けていればオフセットは写像が決めるので隠し、代わりに演出の時刻を出す
            contextOffset_.style.display = follows || (window_.contextPrefab == null && !inScene) ? DisplayStyle.None : DisplayStyle.Flex;
            masterFrame_.style.display = follows ? DisplayStyle.Flex : DisplayStyle.None;
            bool hasContext = window_.contextPrefab != null && !inScene;
            contextCamera_.style.display = hasContext && window_.hasContextShots ? DisplayStyle.Flex : DisplayStyle.None;
            contextCamera_.SetValueWithoutNotify (window_.useContextCamera);
            followTimeline_.style.display = hasContext ? DisplayStyle.Flex : DisplayStyle.None;
            followTimeline_.SetValueWithoutNotify (window_.followTimelineWindow);
            RefreshTime ();
        }
    }

    /// <summary>
    /// プレビュー窓の表示設定（骨・床の表示、ハンドルの大きさ、視点）
    /// </summary>
    [Icon (Icons.kFolder + "View.png")]
    [Overlay (typeof (PreviewWindow), "mkt-preview-view", "View", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    sealed class PreviewViewOverlay : Overlay
    {
        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.minWidth = 200;
            PreviewWindow window = containerWindow as PreviewWindow;
            if (window == null) return root;

            Toggle bones = new Toggle ("Bones") { value = window.showBones };
            bones.RegisterValueChangedCallback (e => window.showBones = e.newValue);
            root.Add (bones);

            Toggle ik = new Toggle ("IK") {
                tooltip = Tr ("PREVIEW_OVERLAYS_IK_TOOLTIP"),
                value = window.showIk,
            };
            ik.RegisterValueChangedCallback (e => window.showIk = e.newValue);
            root.Add (ik);

            Toggle floor = new Toggle ("Floor") { value = window.showFloor };
            floor.RegisterValueChangedCallback (e => window.showFloor = e.newValue);
            root.Add (floor);

            // 大きさ・倍率は非線形のスライダー（UE のカメラの移動速度と同じく、つまみの位置で値が倍々に変わる）
            LogSlider handleSize = new LogSlider ("Handle", 0.005f, 0.2f, window.handleSize) { tooltip = Tr ("PREVIEW_OVERLAYS_HANDLE_TOOLTIP") };
            handleSize.valueChanged += v => window.handleSize = v;
            root.Add (handleSize);

            LogSlider moveScale = new LogSlider ("Move ×", 0.05f, 20f, window.spinMoveScale) {
                tooltip = Tr ("PREVIEW_OVERLAYS_MOVE_SCALE_TOOLTIP"),
            };
            moveScale.valueChanged += v => window.spinMoveScale = v;
            root.Add (moveScale);

            VisualElement views = new VisualElement ();
            views.style.flexDirection = FlexDirection.Row;
            views.Add (ViewButton (window, "Front", Tr ("PREVIEW_OVERLAYS_FRONT_TOOLTIP"), "Front View", () => window.LookFrom (PreviewWindow.ViewPreset.Front)));
            views.Add (ViewButton (window, "Side", Tr ("PREVIEW_OVERLAYS_SIDE_TOOLTIP"), "Side View", () => window.LookFrom (PreviewWindow.ViewPreset.Side)));
            views.Add (ViewButton (window, "Top", Tr ("PREVIEW_OVERLAYS_TOP_TOOLTIP"), "Top View", () => window.LookFrom (PreviewWindow.ViewPreset.Top)));
            views.Add (ViewButton (window, "Frame", Tr ("PREVIEW_OVERLAYS_FRAME_TOOLTIP"), "Frame Selected", window.FrameSelection));
            root.Add (views);

            return root;
        }

        /// <param name="shortcut">同じ操作のショートカットの名前（説明の後ろに今のキーを付ける）</param>
        static Button ViewButton (PreviewWindow window, string text, string tooltip, string shortcut, System.Action click)
        {
            Button button = new Button (click) { text = text };
            ShortcutTooltip.Set (button, window, tooltip, shortcut);
            return button;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Motion.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-preview-toolbar", "Context", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top)]
    sealed class PreviewToolbarOverlayScene : PreviewToolbarOverlay
    {
    }

}
