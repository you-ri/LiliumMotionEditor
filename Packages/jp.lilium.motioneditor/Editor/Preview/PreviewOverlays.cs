using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEditor.UIElements;

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

            model_ = new ObjectField { objectType = typeof (GameObject), allowSceneObjects = false, tooltip = "編集するキャラクターの prefab" };
            model_.style.width = 180;
            model_.RegisterValueChangedCallback (e => window_.SetModel (e.newValue as GameObject));
            toolbar.Add (model_);

            toolbar.Add (new EditorToolbarButton ("Reload", window_.RebuildStage) { tooltip = "キャラを読み込み直す" });

            // このキャラの設定（S19）。無ければ作れる。既にある設定を入れると、それをこのキャラに結び付ける
            settings_ = new ObjectField {
                objectType = typeof (CharacterSettings),
                allowSceneObjects = false,
                tooltip = "このキャラの設定（Layers・AnimBank / PoseBank のフォルダ・リグの定義・Auto）。無いときはプロジェクト設定の値で、Layers の変更は保存されない。" +
                    "既にある設定のアセットを入れると、このキャラの設定になる（1 キャラ 1 設定）",
            };
            settings_.style.width = 160;
            // 設定は prefab から引き当てる。入れたアセットはこのキャラに結び付け、空にしたとき・やめたときは今のアセットに戻す
            settings_.RegisterValueChangedCallback (e => {
                window_.AssignCharacterSettings (e.newValue as CharacterSettings);
                settings_.SetValueWithoutNotify (window_.characterSettings);
            });
            toolbar.Add (settings_);
            createSettings_ = new EditorToolbarButton ("作る", () => window_.CreateCharacterSettings ()) { tooltip = "このキャラの設定を作る（今の値を取り込む）" };
            toolbar.Add (createSettings_);

            // 一緒に見る演出（Timeline を持つ prefab）。時計は窓が正で、フレームを動かすと演出もそのフレームになる
            context_ = new ObjectField {
                objectType = typeof (GameObject),
                allowSceneObjects = false,
                tooltip = "一緒に見る演出の prefab（Timeline を持つもの）。自キャラの姿勢は編集中のものが出る",
            };
            context_.style.width = 160;
            context_.RegisterValueChangedCallback (e => window_.SetContext (e.newValue as GameObject));
            toolbar.Add (context_);

            contextOffset_ = new IntegerField { tooltip = "演出の何フレーム目を、編集しているクリップの 0F に合わせるか" };
            contextOffset_.style.width = 44;
            contextOffset_.RegisterValueChangedCallback (e => window_.SetContextOffset (e.newValue));
            toolbar.Add (contextOffset_);

            // 演出のどのクリップとして見るか（S15b）。結び付けると、演出の時刻はそのクリップの置き方（開始・頭出し・速さ）で決まる
            timelineClip_ = new EditorToolbarDropdown ("Clip", ShowTimelineClipMenu) {
                tooltip = "演出のどのクリップとして見るか。自動なら、編集中のクリップ（焼いた版・Humanoid Pose のクリップ）と同じものを探す",
            };
            toolbar.Add (timelineClip_);

            masterFrame_ = new IntegerField { tooltip = "演出の時刻（演出のフレーム）。スローの区間ではクリップの時刻が端数になり、キーは丸めたフレームに打つ" };
            masterFrame_.style.width = 44;
            masterFrame_.RegisterValueChangedCallback (e => window_.SetMasterFrame (e.newValue));
            toolbar.Add (masterFrame_);

            // 演出のカメラ割りで見る・Timeline 窓の再生位置に付いていく（S15c）
            contextCamera_ = new EditorToolbarToggle { text = "Cam", tooltip = "演出のカメラ割りで見る。視点を動かすと、その位置からいつもの視点に戻る" };
            contextCamera_.RegisterValueChangedCallback (e => window_.SetUseContextCamera (e.newValue));
            toolbar.Add (contextCamera_);
            followTimeline_ = new EditorToolbarToggle { text = "Follow", tooltip = "Unity の Timeline 窓で同じ演出を開いていれば、その再生位置に付いていく" };
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
            menu.AddItem (new GUIContent ("自動（編集中のクリップと同じもの）"), window_.timelineClipAuto, () => window_.SetTimelineClip (null));
            menu.AddItem (new GUIContent ("結び付けない（オフセットで合わせる）"), !window_.timelineClipAuto && current == null, window_.SetTimelineClipNone);
            menu.AddSeparator ("");
            double rate = window_.contextFrameRate;
            foreach (ContextClip candidate in window_.timelineClipCandidates) {
                ContextClip item = candidate;
                string text = item.label + "  " + Mathf.RoundToInt ((float)(item.start * rate)) + "F〜" + Mathf.RoundToInt ((float)(item.end * rate)) + "F"
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
            timelineClip_.text = current != null && current.clip != null ? current.clip.name : "Clip: なし";
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
                tooltip = "手足の IK のコントロールを、Bones を切っていても出す。全身 IK の点（固定の印も）の表示も兼ね、切ると選んでいる点だけ出す",
                value = window.showIk,
            };
            ik.RegisterValueChangedCallback (e => window.showIk = e.newValue);
            root.Add (ik);

            Toggle floor = new Toggle ("Floor") { value = window.showFloor };
            floor.RegisterValueChangedCallback (e => window.showFloor = e.newValue);
            root.Add (floor);

            // 大きさ・倍率は非線形のスライダー（UE のカメラの移動速度と同じく、つまみの位置で値が倍々に変わる）
            LogSlider handleSize = new LogSlider ("Handle", 0.005f, 0.2f, window.handleSize) { tooltip = "ハンドルの大きさ" };
            handleSize.valueChanged += v => window.handleSize = v;
            root.Add (handleSize);

            LogSlider moveScale = new LogSlider ("Move ×", 0.05f, 20f, window.spinMoveScale) {
                tooltip = "Transform パネルの Spinner の Move で、ドラッグ量に掛ける倍率。1 で 1 ピクセル 2.5mm（Shift で細かくなるのは今までどおり）",
            };
            moveScale.valueChanged += v => window.spinMoveScale = v;
            root.Add (moveScale);

            VisualElement views = new VisualElement ();
            views.style.flexDirection = FlexDirection.Row;
            views.Add (ViewButton (window, "Front", "正面から見る", "Front View", () => window.LookFrom (PreviewWindow.ViewPreset.Front)));
            views.Add (ViewButton (window, "Side", "横から見る", "Side View", () => window.LookFrom (PreviewWindow.ViewPreset.Side)));
            views.Add (ViewButton (window, "Top", "上から見る", "Top View", () => window.LookFrom (PreviewWindow.ViewPreset.Top)));
            views.Add (ViewButton (window, "Frame", "選んでいる骨（無ければキャラ全体）を画面に収める", "Frame Selected", window.FrameSelection));
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
