using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 選んでいるコントローラーのパネル（S8 + S9。Spinner と数値欄を 1 枚にしたもの。10-05 に Transform から改名）。
    /// 上から、モード（Move / Rot / Scale）・ギズモの軸（Local / World）・Spinner の操作盤・数値欄（位置・回転・大きさ）。
    /// 数値欄は保存している値（位置はメートル、回転はオイラー角）を出し、入力 1 回で今のフレームにキーを打つ
    /// </summary>
    // 右の列の View の下に置く。下寄せにするとタイムラインに重なる。
    // ID は改名前の "mkt-transform" のまま（変えると、窓に保存してあるパネルの置き場所・開閉が引き継がれない）
    [Icon (Icons.kFolder + "Controller.png")]
    [Overlay (typeof (PreviewWindow), "mkt-transform", "Controller", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    class ControllerOverlay : Overlay
    {
        static readonly Color[] kAxisColors = {
            new Color (0.93f, 0.4f, 0.36f),
            new Color (0.45f, 0.8f, 0.4f),
            new Color (0.42f, 0.62f, 0.98f),
        };
        static readonly string[] kAxisNames = { "X", "Y", "Z" };
        const float kWidth = 208;

        PreviewWindow window_;
        SpinnerElement spinner_;
        Button[] modes_;
        Toggle floorClamp_;
        ValueRow[] rows_;
        Button resetAll_;
        Label warning_;
        PoseTarget shownTarget_;
        Vector3 shownEuler_;
        /// <summary>数値欄を動かしている途中（まだ姿勢全体のキーを打っていない）</summary>
        bool liveEditing_;

        /// <summary>
        /// 数値欄の 1 行（位置・回転・大きさ）
        /// </summary>
        sealed class ValueRow
        {
            public TransformChannels channel;
            public VisualElement root;
            public FloatField[] fields;
            public Button reset;
        }

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            AccentStyle.Attach (root);
            root.style.width = kWidth;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            VisualElement modes = new VisualElement ();
            modes.style.flexDirection = FlexDirection.Row;
            modes_ = new Button[3];
            modes_[0] = AddModeButton (modes, PreviewWindow.SpinnerMode.Move, "Move", Tr ("CONTROLLER_OVERLAY_MODE_MOVE_TOOLTIP"), "Spinner Move");
            modes_[1] = AddModeButton (modes, PreviewWindow.SpinnerMode.Rotate, "Rot", Tr ("CONTROLLER_OVERLAY_MODE_ROTATE_TOOLTIP"), "Spinner Rotate");
            modes_[2] = AddModeButton (modes, PreviewWindow.SpinnerMode.Scale, "Scale", Tr ("CONTROLLER_OVERLAY_MODE_SCALE_TOOLTIP"), "Spinner Scale");
            root.Add (modes);

            // ビューのギズモの軸（Local = 骨の軸 / World = ワールドの軸）
            VisualElement spaces = new VisualElement ();
            spaces.style.flexDirection = FlexDirection.Row;
            spaces_ = new Button[2];
            spaces_[0] = AddSpaceButton (spaces, false, "Local", Tr ("CONTROLLER_OVERLAY_SPACE_LOCAL_TOOLTIP"));
            spaces_[1] = AddSpaceButton (spaces, true, "World", Tr ("CONTROLLER_OVERLAY_SPACE_WORLD_TOOLTIP"));
            root.Add (spaces);

            floorClamp_ = new Toggle ("Floor Clamp") { tooltip = Tr ("CONTROLLER_OVERLAY_FLOOR_CLAMP_TOOLTIP") };
            floorClamp_.RegisterValueChangedCallback (e => window_.floorClamp = e.newValue);
            root.Add (floorClamp_);

            spinner_ = new SpinnerElement (window_);
            root.Add (spinner_);

            rows_ = new ValueRow[3];
            rows_[0] = AddRow (root, TransformChannels.Position, "Pos", Tr ("CONTROLLER_OVERLAY_POSITION_TOOLTIP"));
            rows_[1] = AddRow (root, TransformChannels.Rotation, "Rot", Tr ("CONTROLLER_OVERLAY_ROTATION_TOOLTIP"));
            rows_[2] = AddRow (root, TransformChannels.Scale, "Scale", Tr ("CONTROLLER_OVERLAY_SCALE_TOOLTIP"));

            // 行の ↺ は 1 行だけ。こちらは選んでいる物すべての値をまとめて戻す
            resetAll_ = new Button (() => window_.ResetSelectedValues ()) { text = "Reset", tooltip = Tr ("CONTROLLER_OVERLAY_RESET_SELECTION_TOOLTIP") };
            resetAll_.style.marginTop = 2;
            resetAll_.style.marginLeft = 0;
            resetAll_.style.marginRight = 0;
            root.Add (resetAll_);

            // 全身 IK の点を選んでいる間だけ出す。いくつか選んでいれば、まとめて変える
            pointStates_ = new VisualElement ();
            pointStates_.style.flexDirection = FlexDirection.Row;
            pointStates_.style.marginTop = 4;
            pointStateButtons_ = new Button[3];
            pointStateButtons_[0] = AddPointStateButton (BodyPointState.Free, "Free", Tr ("CONTROLLER_OVERLAY_POINT_FREE_TOOLTIP"));
            pointStateButtons_[1] = AddPointStateButton (BodyPointState.Pin, "Pin", Tr ("CONTROLLER_OVERLAY_POINT_PIN_TOOLTIP"));
            pointStateButtons_[2] = AddPointStateButton (BodyPointState.Locked, "Lock", Tr ("CONTROLLER_OVERLAY_POINT_LOCK_TOOLTIP"));
            ShortcutTooltip.Set (pointStateButtons_[2], containerWindow, pointStateButtons_[2].tooltip, "Toggle Point Lock");
            root.Add (pointStates_);

            warning_ = new Label ();
            warning_.style.whiteSpace = WhiteSpace.Normal;
            warning_.style.fontSize = 10;
            warning_.style.color = new Color (1, 0.7f, 0.35f);
            warning_.style.display = DisplayStyle.None;
            root.Add (warning_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            // 段の 👁 を切り替えたとき（上書きの注意を出し直す）
            window_.stackChanged -= Refresh;
            window_.stackChanged += Refresh;
            window_.poseSampled -= RefreshValues;
            window_.poseSampled += RefreshValues;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.stackChanged -= Refresh;
                window_.poseSampled -= RefreshValues;
            }
            base.OnWillBeDestroyed ();
        }

        VisualElement pointStates_;
        Button[] pointStateButtons_;
        static readonly Color[] kPointStateColors = {
            new Color (0.2f, 0.55f, 0.75f),
            new Color (0.8f, 0.45f, 0.1f),
            new Color (0.75f, 0.2f, 0.15f),
        };

        Button AddPointStateButton (BodyPointState state, string text, string tooltip)
        {
            Button button = new Button (() => window_.SetSelectedPointsState (state)) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            pointStates_.Add (button);
            return button;
        }

        /// <summary>
        /// 点の状態のボタン。選んでいる点の状態に色を付ける（いくつか選んでいて状態が混ざっていれば、ある状態すべてに付ける）
        /// </summary>
        void RefreshPointStates ()
        {
            if (pointStates_ == null) return;
            int count = window_.selectedPointCount;
            pointStates_.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (count == 0) return;
            for (int i = 0; i < pointStateButtons_.Length; i++) {
                int n = window_.CountSelectedPoints ((BodyPointState)i);
                pointStateButtons_[i].style.backgroundColor = n > 0 ? new StyleColor (kPointStateColors[i]) : new StyleColor (StyleKeyword.Null);
                string name = i == 0 ? "Free" : i == 1 ? "Pin" : "Lock";
                pointStateButtons_[i].text = count > 1 && n > 0 && n < count ? name + " " + n : name;
            }
        }

        /// <param name="shortcut">切り替えるショートカットの名前（説明の後ろに今のキーを付ける）</param>
        Button AddModeButton (VisualElement parent, PreviewWindow.SpinnerMode mode, string text, string tooltip, string shortcut)
        {
            Button button = new Button (() => window_.spinnerMode = mode) { text = text };
            ShortcutTooltip.Set (button, containerWindow, tooltip, shortcut);
            button.style.flexGrow = 1;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            parent.Add (button);
            return button;
        }

        Button[] spaces_;

        Button AddSpaceButton (VisualElement parent, bool world, string text, string tooltip)
        {
            Button button = new Button (() => window_.gizmoWorld = world) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            parent.Add (button);
            return button;
        }

        ValueRow AddRow (VisualElement parent, TransformChannels channel, string title, string tooltip)
        {
            ValueRow row = new ValueRow { channel = channel, fields = new FloatField[3] };
            row.root = new VisualElement { tooltip = tooltip };
            row.root.style.flexDirection = FlexDirection.Row;
            row.root.style.alignItems = Align.Center;
            row.root.style.marginTop = 1;

            Label label = new Label (title);
            label.style.width = 34;
            label.style.fontSize = 10;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.paddingLeft = 2;
            row.root.Add (label);

            for (int axis = 0; axis < 3; axis++) {
                // 途中の値もすぐ姿勢へ出す（ラベルのドラッグ・入力中）。姿勢全体のキーは離したとき・確定したとき
                FloatField field = new FloatField (kAxisNames[axis]) { isDelayed = false };
                field.style.flexGrow = 1;
                field.style.flexBasis = 0;
                field.style.marginLeft = 0;
                field.style.marginRight = 1;
                field.labelElement.style.minWidth = 0;
                field.labelElement.style.width = 9;
                field.labelElement.style.paddingLeft = 0;
                field.labelElement.style.color = kAxisColors[axis];
                field.labelElement.style.unityFontStyleAndWeight = FontStyle.Bold;
                field.RegisterValueChangedCallback (e => OnFieldChanged (row));
                field.RegisterCallback<PointerUpEvent> (e => EndLiveEdit (), TrickleDown.TrickleDown);
                field.RegisterCallback<PointerCaptureOutEvent> (e => EndLiveEdit (), TrickleDown.TrickleDown);
                field.RegisterCallback<FocusOutEvent> (e => EndLiveEdit ());
                field.RegisterCallback<KeyDownEvent> (e => {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) EndLiveEdit ();
                }, TrickleDown.TrickleDown);
                row.fields[axis] = field;
                row.root.Add (field);
            }

            row.reset = new Button (() => window_.ResetSpinValues (channel)) { text = "↺", tooltip = Tr ("CONTROLLER_OVERLAY_RESET_ROW_TOOLTIP") };
            row.reset.style.width = 18;
            row.reset.style.marginLeft = 1;
            row.reset.style.marginRight = 0;
            row.reset.style.paddingLeft = 0;
            row.reset.style.paddingRight = 0;
            row.root.Add (row.reset);

            parent.Add (row.root);
            return row;
        }

        /// <summary>
        /// 1 欄の入力を確定したとき。行の 3 つの値をまとめて入れる
        /// </summary>
        void OnFieldChanged (ValueRow row)
        {
            Vector3 value = new Vector3 (row.fields[0].value, row.fields[1].value, row.fields[2].value);
            Vector3 position;
            Quaternion rotation;
            window_.TryGetSpinValues (out position, out rotation);
            if (row.channel == TransformChannels.Position) position = value;
            else if (row.channel == TransformChannels.Rotation) {
                rotation = Quaternion.Euler (value);
                shownEuler_ = value;
            }
            else return;
            if (!liveEditing_) window_.BeginSpin ();
            liveEditing_ = true;
            window_.SetSpinValuesLive (row.channel, position, rotation);
        }

        /// <summary>
        /// 数値欄の操作が終わった（離した・確定した・フォーカスが外れた）。姿勢全体のキーを打つ
        /// </summary>
        void EndLiveEdit ()
        {
            if (!liveEditing_ || window_ == null) return;
            liveEditing_ = false;
            window_.EndSpin ();
            RefreshValues ();
        }

        /// <summary>
        /// 窓の側（選択・モードのショートカット）で替わったとき表示を合わせる
        /// </summary>
        void Refresh ()
        {
            if (window_ == null || modes_ == null) return;

            for (int i = 0; i < modes_.Length; i++) {
                bool active = (PreviewWindow.SpinnerMode)i == window_.spinnerMode;
                AccentStyle.Set (modes_[i], active);
            }
            for (int i = 0; i < spaces_.Length; i++) {
                bool active = (i == 1) == window_.gizmoWorld;
                AccentStyle.Set (spaces_[i], active);
            }
            floorClamp_.style.display = window_.hasBodyPoints ? DisplayStyle.Flex : DisplayStyle.None;
            floorClamp_.SetValueWithoutNotify (window_.floorClamp);
            spinner_.Refresh ();

            string warning = window_.spinOverrideWarning;
            warning_.text = warning ?? "";
            warning_.style.display = warning != null ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshValues ();
        }

        /// <summary>
        /// 数値欄を今の値に合わせる。入力中の欄は上書きしない
        /// </summary>
        void RefreshValues ()
        {
            if (window_ != null) RefreshPointStates ();
            if (window_ == null || rows_ == null) return;

            Vector3 position;
            Quaternion rotation;
            bool has = window_.TryGetSpinValues (out position, out rotation);
            PoseTarget target = window_.selectedTarget;
            // 対象が替わったら、角度の寄せ先を今の値にし直す
            if (target != shownTarget_) {
                shownTarget_ = target;
                shownEuler_ = EulerAngles.Wrap (rotation.eulerAngles);
            }
            Vector3 euler = has ? EulerAngles.Closest (rotation, shownEuler_) : Vector3.zero;
            shownEuler_ = euler;

            int activeRow = (int)window_.spinnerMode;
            for (int r = 0; r < rows_.Length; r++) {
                ValueRow row = rows_[r];
                bool editable = has && window_.CanEditValues (row.channel);
                row.root.SetEnabled (editable);
                AccentStyle.Set (row.root, r == activeRow);
                Vector3 value = row.channel == TransformChannels.Position ? position
                    : row.channel == TransformChannels.Rotation ? euler
                    : Vector3.one;
                if (!has) value = row.channel == TransformChannels.Scale ? Vector3.one : Vector3.zero;
                for (int axis = 0; axis < 3; axis++) {
                    FloatField field = row.fields[axis];
                    // 入力中の欄は上書きしない
                    VisualElement focused = field.focusController != null ? field.focusController.focusedElement as VisualElement : null;
                    if (focused != null && (focused == field || field.Contains (focused))) continue;
                    field.SetValueWithoutNotify (Round (value[axis], row.channel));
                }
            }

            resetAll_.SetEnabled (window_.CanResetSelectedValues ());
        }

        static float Round (float value, TransformChannels channel)
        {
            float step = channel == TransformChannels.Rotation ? 0.01f : 0.0001f;
            return Mathf.Round (value / step) * step;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Controller.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-transform", "Controller", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    sealed class ControllerOverlayScene : ControllerOverlay
    {
    }

}
