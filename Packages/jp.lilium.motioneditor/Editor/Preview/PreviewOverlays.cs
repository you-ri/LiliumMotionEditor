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
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top, defaultDockIndex = 0)]
    class PreviewToolbarOverlay : MotionOverlay, ICreateHorizontalToolbar
    {
        ObjectField model_;
        ObjectField settings_;
        EditorToolbarButton createSettings_;

        public override VisualElement CreatePanelContent ()
        {
            return CreateHorizontalToolbarContent ();
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            window_ = ResolveWindow ();
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

            // リグの定義はリグエディタで直す。今のキャラと、そのキャラが使う定義で開く
            toolbar.Add (new EditorToolbarButton ("Rig", Icons.Load ("PoseTool"), OpenRigEditor) { tooltip = Tr ("PREVIEW_OVERLAYS_RIG_EDITOR_TOOLTIP") });

            Refresh ();
            Resubscribe ();
            return toolbar;
        }

        protected override void Subscribe (bool on)
        {
            window_.stateChanged -= Refresh;
            if (!on) return;
            window_.stateChanged += Refresh;
        }

        void OpenRigEditor ()
        {
            RigEditorWindow rigEditor = RigEditorWindow.Open ();
            if (window_ == null || window_.model == null) return;
            rigEditor.SetModel (window_.model);
            // リグエディタの欄で別の定義を選んでいても、このキャラが使う定義に戻す
            rigEditor.SetDefinition (null);
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
        }
    }

    /// <summary>
    /// プレビュー窓の表示設定（骨・床の表示、ハンドルの大きさ、視点）
    /// </summary>
    [Icon (Icons.kFolder + "View.png")]
    // 既定は上端のツールバーの右（Overlay メニューの次）。パネルだけなのでボタンに畳まれる
    [Overlay (typeof (PreviewWindow), "mkt-preview-view", "View", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Bottom, defaultDockIndex = 1)]
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

            EnumField lighting = new EnumField ("Lighting", window.lighting) { tooltip = Tr ("PREVIEW_OVERLAYS_LIGHTING_TOOLTIP") };
            lighting.RegisterValueChangedCallback (e => window.lighting = (PreviewLighting)e.newValue);
            root.Add (lighting);

            Toggle flipLighting = new Toggle ("Flip Light") {
                tooltip = Tr ("PREVIEW_OVERLAYS_FLIP_LIGHT_TOOLTIP"),
                value = window.flipLighting,
            };
            flipLighting.RegisterValueChangedCallback (e => window.flipLighting = e.newValue);
            root.Add (flipLighting);

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
            views.Add (ViewButton ("Front", Tr ("PREVIEW_OVERLAYS_FRONT_TOOLTIP"), "Front View", () => window.LookFrom (PreviewWindow.ViewPreset.Front)));
            views.Add (ViewButton ("Side", Tr ("PREVIEW_OVERLAYS_SIDE_TOOLTIP"), "Side View", () => window.LookFrom (PreviewWindow.ViewPreset.Side)));
            views.Add (ViewButton ("Top", Tr ("PREVIEW_OVERLAYS_TOP_TOOLTIP"), "Top View", () => window.LookFrom (PreviewWindow.ViewPreset.Top)));
            views.Add (ViewButton ("Frame", Tr ("PREVIEW_OVERLAYS_FRAME_TOOLTIP"), "Frame Selected", window.FrameSelection));
            root.Add (views);

            return root;
        }

        /// <param name="shortcut">同じ操作のショートカットの名前（説明の後ろに今のキーを付ける）</param>
        static Button ViewButton (string text, string tooltip, string shortcut, System.Action click)
        {
            Button button = new Button (click) { text = text };
            ShortcutTooltip.Set (button, tooltip, shortcut);
            return button;
        }
    }

}
