using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Spinner と Transform を 1 枚にしたパネル（S8 + S9）。
    /// 上から、動かす物の名前と Pole の切替・モード（Move / Rot / Scale）・ギズモの軸（Local / World）・Spinner の操作盤・数値欄（位置・回転・大きさ）・Mirror。
    /// 数値欄は保存している値（位置はメートル、回転はオイラー角）を出し、入力 1 回で今のフレームにキーを打つ
    /// </summary>
    // 右の列の View の下に置く。下寄せにするとタイムラインに重なる
    [Icon (Icons.kFolder + "Transform.png")]
    [Overlay (typeof (PreviewWindow), "mkt-transform", "Transform", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    class TransformOverlay : Overlay
    {
        static readonly Color kActiveColor = new Color (0.29f, 0.45f, 0.69f);
        static readonly Color kActiveRowColor = new Color (0.29f, 0.45f, 0.69f, 0.25f);
        static readonly Color[] kAxisColors = {
            new Color (0.93f, 0.4f, 0.36f),
            new Color (0.45f, 0.8f, 0.4f),
            new Color (0.42f, 0.62f, 0.98f),
        };
        static readonly string[] kAxisNames = { "X", "Y", "Z" };
        // 足の転がしの軸は体の右（roll）・前（bank）・上（twist）なので、X・Z・Y の色にそろえる
        static readonly string[] kFootNames = { "R", "B", "T" };
        static readonly Color[] kFootColors = { kAxisColors[0], kAxisColors[2], kAxisColors[1] };
        const float kWidth = 208;

        PreviewWindow window_;
        SpinnerElement spinner_;
        Button[] modes_;
        Toggle pole_;
        ValueRow[] rows_;
        /// <summary>足の転がし（roll / bank / twist）の行。足の転がしを持つ脚の IK を選んでいるときだけ出す</summary>
        ValueRow foot_;
        Button mirror_;
        Label warning_;
        PoseTarget shownTarget_;
        bool shownPole_;
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
            root.style.width = kWidth;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            VisualElement modes = new VisualElement ();
            modes.style.flexDirection = FlexDirection.Row;
            modes_ = new Button[3];
            modes_[0] = AddModeButton (modes, PreviewWindow.SpinnerMode.Move, "Move", Tr ("TRANSFORM_OVERLAY_MODE_MOVE_TOOLTIP"), "Spinner Move");
            modes_[1] = AddModeButton (modes, PreviewWindow.SpinnerMode.Rotate, "Rot", Tr ("TRANSFORM_OVERLAY_MODE_ROTATE_TOOLTIP"), "Spinner Rotate");
            modes_[2] = AddModeButton (modes, PreviewWindow.SpinnerMode.Scale, "Scale", Tr ("TRANSFORM_OVERLAY_MODE_SCALE_TOOLTIP"), "Spinner Scale");
            root.Add (modes);

            // ビューのギズモの軸（Local = 骨の軸 / World = ワールドの軸）
            VisualElement spaces = new VisualElement ();
            spaces.style.flexDirection = FlexDirection.Row;
            spaces_ = new Button[2];
            spaces_[0] = AddSpaceButton (spaces, false, "Local", Tr ("TRANSFORM_OVERLAY_SPACE_LOCAL_TOOLTIP"));
            spaces_[1] = AddSpaceButton (spaces, true, "World", Tr ("TRANSFORM_OVERLAY_SPACE_WORLD_TOOLTIP"));
            root.Add (spaces);

            pole_ = new Toggle ("Pole");
            ShortcutTooltip.Set (pole_, containerWindow, Tr ("TRANSFORM_OVERLAY_POLE_TOOLTIP"), "Spinner Toggle Pole");
            pole_.RegisterValueChangedCallback (e => window_.spinnerPole = e.newValue);
            root.Add (pole_);

            spinner_ = new SpinnerElement (window_);
            root.Add (spinner_);

            rows_ = new ValueRow[3];
            rows_[0] = AddRow (root, TransformChannels.Position, "Pos", Tr ("TRANSFORM_OVERLAY_POSITION_TOOLTIP"));
            rows_[1] = AddRow (root, TransformChannels.Rotation, "Rot", Tr ("TRANSFORM_OVERLAY_ROTATION_TOOLTIP"));
            rows_[2] = AddRow (root, TransformChannels.Scale, "Scale", Tr ("TRANSFORM_OVERLAY_SCALE_TOOLTIP"));
            foot_ = AddRow (root, TransformChannels.None, "Foot",
                Tr ("TRANSFORM_OVERLAY_FOOT_TOOLTIP"), kFootNames, kFootColors,
                Tr ("TRANSFORM_OVERLAY_FOOT_ROLL_TOOLTIP"), Tr ("TRANSFORM_OVERLAY_FOOT_BANK_TOOLTIP"), Tr ("TRANSFORM_OVERLAY_FOOT_TWIST_TOOLTIP"));

            // 全身 IK の点を選んでいる間だけ出す。いくつか選んでいれば、まとめて変える
            pointStates_ = new VisualElement ();
            pointStates_.style.flexDirection = FlexDirection.Row;
            pointStates_.style.marginTop = 4;
            pointStateButtons_ = new Button[3];
            pointStateButtons_[0] = AddPointStateButton (BodyPointState.Free, "Free", Tr ("TRANSFORM_OVERLAY_POINT_FREE_TOOLTIP"));
            pointStateButtons_[1] = AddPointStateButton (BodyPointState.Pin, "Pin", Tr ("TRANSFORM_OVERLAY_POINT_PIN_TOOLTIP"));
            pointStateButtons_[2] = AddPointStateButton (BodyPointState.Locked, "Lock", Tr ("TRANSFORM_OVERLAY_POINT_LOCK_TOOLTIP"));
            ShortcutTooltip.Set (pointStateButtons_[2], containerWindow, pointStateButtons_[2].tooltip, "Toggle Point Lock");
            root.Add (pointStates_);

            mirror_ = new Button (() => window_.MirrorSpinValues ()) { text = "Mirror" };
            mirror_.style.marginTop = 4;
            root.Add (mirror_);

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

        ValueRow AddRow (VisualElement parent, TransformChannels channel, string title, string tooltip,
            string[] names = null, Color[] colors = null, params string[] fieldTooltips)
        {
            names = names ?? kAxisNames;
            colors = colors ?? kAxisColors;
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
                FloatField field = new FloatField (names[axis]) { isDelayed = false };
                if (fieldTooltips != null && axis < fieldTooltips.Length) field.tooltip = fieldTooltips[axis];
                field.style.flexGrow = 1;
                field.style.flexBasis = 0;
                field.style.marginLeft = 0;
                field.style.marginRight = 1;
                field.labelElement.style.minWidth = 0;
                field.labelElement.style.width = 9;
                field.labelElement.style.paddingLeft = 0;
                field.labelElement.style.color = colors[axis];
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

            row.reset = channel == TransformChannels.None
                ? new Button (() => window_.ResetFootAngles ()) { text = "↺", tooltip = Tr ("TRANSFORM_OVERLAY_RESET_FOOT_TOOLTIP") }
                : new Button (() => window_.ResetSpinValues (channel)) { text = "↺", tooltip = Tr ("TRANSFORM_OVERLAY_RESET_ROW_TOOLTIP") };
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
            if (row == foot_) {
                if (!liveEditing_) window_.BeginSpin ();
                liveEditing_ = true;
                window_.SetFootAnglesLive (value);
                return;
            }
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
                modes_[i].style.backgroundColor = active ? new StyleColor (kActiveColor) : new StyleColor (StyleKeyword.Null);
            }
            for (int i = 0; i < spaces_.Length; i++) {
                bool active = (i == 1) == window_.gizmoWorld;
                spaces_[i].style.backgroundColor = active ? new StyleColor (kActiveColor) : new StyleColor (StyleKeyword.Null);
            }
            pole_.style.display = window_.isIKSelected ? DisplayStyle.Flex : DisplayStyle.None;
            pole_.SetValueWithoutNotify (window_.spinnerPole);
            spinner_.Refresh ();

            PoseTarget mirror = window_.GetMirrorTarget ();
            PoseTarget selected = window_.selectedTarget;
            mirror_.SetEnabled (mirror != null);
            string warning = window_.spinOverrideWarning;
            warning_.text = warning ?? "";
            warning_.style.display = warning != null ? DisplayStyle.Flex : DisplayStyle.None;
            mirror_.tooltip = mirror == null
                ? Tr ("TRANSFORM_OVERLAY_MIRROR_NO_PAIR")
                : mirror == selected ? Tr ("TRANSFORM_OVERLAY_MIRROR_SELF") : Tr ("TRANSFORM_OVERLAY_MIRROR_TO", mirror.label);
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
            if (target != shownTarget_ || window_.spinnerPole != shownPole_) {
                shownTarget_ = target;
                shownPole_ = window_.spinnerPole;
                shownEuler_ = Wrap (rotation.eulerAngles);
            }
            Vector3 euler = has ? EulerAngles.Closest (rotation, shownEuler_) : Vector3.zero;
            shownEuler_ = euler;

            // 足の転がしの間は、扇形が動かすのは Foot の行
            int activeRow = window_.spinnerFoot ? -1 : (int)window_.spinnerMode;
            for (int r = 0; r < rows_.Length; r++) {
                ValueRow row = rows_[r];
                bool editable = has && window_.CanEditValues (row.channel);
                row.root.SetEnabled (editable);
                row.root.style.backgroundColor = r == activeRow ? new StyleColor (kActiveRowColor) : new StyleColor (StyleKeyword.Null);
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

            Vector3 angles;
            bool foot = window_.TryGetFootAngles (out angles);
            foot_.root.style.display = foot ? DisplayStyle.Flex : DisplayStyle.None;
            foot_.root.style.backgroundColor = window_.spinnerFoot ? new StyleColor (kActiveRowColor) : new StyleColor (StyleKeyword.Null);
            if (foot) {
                foot_.root.SetEnabled (window_.CanEditFootAngles ());
                SetFieldsWithoutNotify (foot_, angles, TransformChannels.Rotation);
            }
        }

        /// <summary>
        /// 行の 3 つの欄に値を出す。入力中の欄は上書きしない
        /// </summary>
        static void SetFieldsWithoutNotify (ValueRow row, Vector3 value, TransformChannels rounding)
        {
            for (int axis = 0; axis < 3; axis++) {
                FloatField field = row.fields[axis];
                VisualElement focused = field.focusController != null ? field.focusController.focusedElement as VisualElement : null;
                if (focused != null && (focused == field || field.Contains (focused))) continue;
                field.SetValueWithoutNotify (Round (value[axis], rounding));
            }
        }

        static float Round (float value, TransformChannels channel)
        {
            float step = channel == TransformChannels.Rotation ? 0.01f : 0.0001f;
            return Mathf.Round (value / step) * step;
        }

        static Vector3 Wrap (Vector3 euler)
        {
            for (int i = 0; i < 3; i++) euler[i] = Mathf.DeltaAngle (0, euler[i]);
            return euler;
        }
    }

    /// <summary>
    /// Spinner の丸い操作盤。扇形の X（赤）・Y（緑）・Z（青）を上下にドラッグすると、その軸の値が増減する。
    /// 中央は、回転なら見ている向きの軸まわり（多軸）、大きさなら全軸。回転の多軸は中ボタンでもできる。
    /// Shift で細かく、Ctrl できりのいい値に止まる
    /// </summary>
    sealed class SpinnerElement : VisualElement
    {
        const float kSize = 150;
        const float kInnerRatio = 0.42f;
        /// <summary>1 ピクセルあたりの変化量</summary>
        const float kMovePerPixel = 0.0025f;
        const float kRotatePerPixel = 0.6f;
        const float kScalePerPixel = 0.004f;
        /// <summary>Ctrl を押している間、止まる刻み</summary>
        const float kMoveSnap = 0.01f;
        const float kRotateSnap = 5;
        const float kScaleSnap = 0.05f;
        const float kFineScale = 0.2f;

        static readonly Color kXColor = new Color (0.79f, 0.25f, 0.22f);
        static readonly Color kYColor = new Color (0.3f, 0.65f, 0.28f);
        static readonly Color kZColor = new Color (0.23f, 0.45f, 0.83f);
        static readonly Color kCenterColor = new Color (0.45f, 0.45f, 0.47f);
        static readonly Color kDisabledColor = new Color (0.3f, 0.3f, 0.32f);
        static readonly Color kBackColor = new Color (0.16f, 0.16f, 0.17f);
        /// <summary>扇形の向き（度、右が 0・反時計回り）。Y は上、X は左下、Z は右下</summary>
        static readonly float[] kZoneStart = { 150, 30, 270 };

        const int kZoneX = 0;
        const int kZoneY = 1;
        const int kZoneZ = 2;
        const int kZoneCenter = 3;

        static readonly string[] kAxisNames = { "X", "Y", "Z" };
        /// <summary>
        /// 足の転がしのときの扇形（Reverse Foot）。軸が体の右・上・前なので、X に Roll、Y に Twist、Z に Bank を当てる
        /// </summary>
        static readonly string[] kFootNames = { "Roll", "Twist", "Bank" };

        readonly PreviewWindow window_;
        readonly Label[] labels_ = new Label[4];
        readonly Label readout_;
        int hoverZone_ = -1;
        int dragZone_ = -1;
        bool viewRotate_;
        float total_;
        float applied_;
        Vector2 totalView_;

        public SpinnerElement (PreviewWindow window)
        {
            window_ = window;
            style.height = kSize;
            style.marginTop = 2;

            generateVisualContent += OnGenerateVisualContent;
            for (int i = 0; i < labels_.Length; i++) {
                labels_[i] = CreateLabel ();
            }
            readout_ = CreateLabel ();
            readout_.style.width = kSize;

            RegisterCallback<PointerDownEvent> (OnPointerDown);
            RegisterCallback<PointerMoveEvent> (OnPointerMove);
            RegisterCallback<PointerUpEvent> (OnPointerUp);
            RegisterCallback<PointerLeaveEvent> (e => SetHover (-1));
            RegisterCallback<GeometryChangedEvent> (e => LayoutLabels ());
        }

        Label CreateLabel ()
        {
            Label label = new Label ();
            label.style.position = Position.Absolute;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 10;
            label.pickingMode = PickingMode.Ignore;
            Add (label);
            return label;
        }

        Vector2 center
        {
            get { return new Vector2 (contentRect.width * 0.5f, contentRect.height * 0.5f - 6); }
        }

        float radius
        {
            get { return Mathf.Max (10, Mathf.Min (contentRect.width, contentRect.height - 12) * 0.5f - 2); }
        }

        /// <summary>
        /// 選んでいる物・モードが替わったとき（オーバーレイから）
        /// </summary>
        public void Refresh ()
        {
            LayoutLabels ();
            ShowIdleReadout ();
            MarkDirtyRepaint ();
        }

        void LayoutLabels ()
        {
            // まだ配置されていない（大きさが決まっていない）間は置かない
            if (float.IsNaN (contentRect.width) || float.IsNaN (contentRect.height)) return;

            float r = (radius + radius * kInnerRatio) * 0.5f;
            string[] names = ZoneNames ();
            for (int zone = 0; zone < 3; zone++) {
                PlaceLabel (labels_[zone], Polar (center, r, kZoneStart[zone] + 60), names[zone]);
            }
            PlaceLabel (labels_[kZoneCenter], center, CenterLabel ());

            readout_.style.left = 0;
            readout_.style.top = contentRect.height - 14;
        }

        string CenterLabel ()
        {
            if (window_ == null) return "";
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Rotate: return Tr ("TRANSFORM_OVERLAY_MULTI_AXIS");
                case PreviewWindow.SpinnerMode.Scale: return Tr ("TRANSFORM_OVERLAY_ALL_AXES");
                default: return "";
            }
        }

        /// <summary>足の転がしの間は扇形が Roll / Twist / Bank</summary>
        bool footMode
        {
            get { return window_ != null && window_.spinnerFoot; }
        }

        string[] ZoneNames ()
        {
            return footMode ? kFootNames : kAxisNames;
        }

        static void PlaceLabel (Label label, Vector2 position, string text)
        {
            label.text = text;
            label.style.width = 36;
            label.style.height = 14;
            label.style.left = position.x - 18;
            label.style.top = position.y - 7;
        }

        void ShowIdleReadout ()
        {
            string target = window_ != null ? window_.spinTargetLabel : null;
            readout_.text = target ?? Tr ("TRANSFORM_OVERLAY_SELECT_BONE");
        }

        void OnGenerateVisualContent (MeshGenerationContext context)
        {
            Painter2D painter = context.painter2D;
            Vector2 c = center;
            float r = radius;
            float inner = r * kInnerRatio;

            FillWedge (painter, c, 0, r, 0, 360, kBackColor);
            for (int zone = 0; zone < 3; zone++) {
                FillWedge (painter, c, inner, r, kZoneStart[zone], kZoneStart[zone] + 120, ZoneColor (zone));
            }
            FillWedge (painter, c, 0, inner * 0.92f, 0, 360, ZoneColor (kZoneCenter));
        }

        Color ZoneColor (int zone)
        {
            Color color;
            switch (zone) {
                case kZoneX: color = kXColor; break;
                case kZoneY: color = kYColor; break;
                case kZoneZ: color = kZColor; break;
                default: color = kCenterColor; break;
            }
            if (!IsZoneEnabled (zone)) return kDisabledColor;
            if (zone == dragZone_) return Color.Lerp (color, Color.white, 0.45f);
            if (zone == hoverZone_) return Color.Lerp (color, Color.white, 0.2f);
            return color;
        }

        /// <summary>
        /// 中央は、回転（多軸）と大きさ（全軸）のときだけ使う
        /// </summary>
        bool IsZoneEnabled (int zone)
        {
            if (window_ == null || !window_.CanSpin (window_.spinnerMode)) return false;
            if (zone != kZoneCenter) return !footMode || window_.CanEditFootAngles ();
            return window_.spinnerMode != PreviewWindow.SpinnerMode.Move;
        }

        static Vector2 Polar (Vector2 c, float r, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector2 (c.x + r * Mathf.Cos (a), c.y - r * Mathf.Sin (a));
        }

        /// <summary>
        /// 扇形（半径 r0〜r1、角度 a0〜a1）を塗る。Arc の向きに悩まないよう、多角形にして塗る
        /// </summary>
        static void FillWedge (Painter2D painter, Vector2 c, float r0, float r1, float a0, float a1, Color color)
        {
            int steps = Mathf.Max (2, Mathf.RoundToInt ((a1 - a0) / 6));
            painter.fillColor = color;
            painter.BeginPath ();
            painter.MoveTo (Polar (c, r1, a0));
            for (int i = 1; i <= steps; i++) {
                painter.LineTo (Polar (c, r1, Mathf.Lerp (a0, a1, (float)i / steps)));
            }
            for (int i = steps; i >= 0; i--) {
                painter.LineTo (Polar (c, r0, Mathf.Lerp (a0, a1, (float)i / steps)));
            }
            painter.ClosePath ();
            painter.Fill ();
        }

        int ZoneAt (Vector2 position)
        {
            Vector2 d = position - center;
            float length = d.magnitude;
            if (length > radius) return -1;
            if (length < radius * kInnerRatio) return kZoneCenter;

            float angle = Mathf.Atan2 (-d.y, d.x) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360;
            if (angle >= kZoneStart[kZoneY] && angle < kZoneStart[kZoneX]) return kZoneY;
            if (angle >= kZoneStart[kZoneX] && angle < kZoneStart[kZoneZ]) return kZoneX;
            return kZoneZ;
        }

        void SetHover (int zone)
        {
            if (hoverZone_ == zone) return;
            hoverZone_ = zone;
            MarkDirtyRepaint ();
        }

        void OnPointerDown (PointerDownEvent evt)
        {
            if (window_ == null) return;

            int zone = ZoneAt (evt.localPosition);
            // 回転の多軸は、中ボタンならどこでも効く
            bool multiAxis = window_.spinnerMode == PreviewWindow.SpinnerMode.Rotate && (evt.button == 2 || zone == kZoneCenter);
            if (zone < 0 || !IsZoneEnabled (multiAxis ? kZoneCenter : zone)) return;

            dragZone_ = multiAxis ? kZoneCenter : zone;
            viewRotate_ = multiAxis;
            total_ = 0;
            applied_ = 0;
            totalView_ = Vector2.zero;
            window_.BeginSpin ();
            this.CapturePointer (evt.pointerId);
            evt.StopPropagation ();
            MarkDirtyRepaint ();
        }

        void OnPointerMove (PointerMoveEvent evt)
        {
            if (dragZone_ < 0) {
                SetHover (ZoneAt (evt.localPosition));
                return;
            }

            float fine = evt.shiftKey ? kFineScale : 1;
            if (viewRotate_) {
                Vector2 amount = new Vector2 (evt.deltaPosition.x, evt.deltaPosition.y) * kRotatePerPixel * fine;
                totalView_ += amount;
                window_.SpinRotateView (amount);
                readout_.text = Tr ("TRANSFORM_OVERLAY_MULTI_AXIS_READOUT", totalView_.x.ToString ("+0;-0"), totalView_.y.ToString ("+0;-0"));
            }
            else {
                // 上へドラッグで増やす
                float raw = -evt.deltaPosition.y * StepPerPixel () * fine;
                total_ += raw;
                float snap = Snap ();
                float want = (evt.ctrlKey || evt.commandKey) && snap > 0 ? Mathf.Round (total_ / snap) * snap : total_;
                float delta = want - applied_;
                applied_ = want;
                if (Mathf.Abs (delta) > 1e-7f) Apply (delta);
                readout_.text = Readout ();
            }
            evt.StopPropagation ();
            MarkDirtyRepaint ();
        }

        void OnPointerUp (PointerUpEvent evt)
        {
            if (dragZone_ < 0) return;

            dragZone_ = -1;
            // 離したところで、その姿勢を全体のキーにする
            window_.EndSpin ();
            this.ReleasePointer (evt.pointerId);
            evt.StopPropagation ();
            ShowIdleReadout ();
            MarkDirtyRepaint ();
        }

        /// <summary>
        /// 中央（大きさの全軸）は全部の軸、それ以外はその軸だけ
        /// </summary>
        Vector3 Axis ()
        {
            switch (dragZone_) {
                case kZoneX: return Vector3.right;
                case kZoneY: return Vector3.up;
                case kZoneZ: return Vector3.forward;
                default: return Vector3.one;
            }
        }

        float StepPerPixel ()
        {
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Move: return kMovePerPixel * window_.spinMoveScale;
                case PreviewWindow.SpinnerMode.Scale: return kScalePerPixel;
                default: return kRotatePerPixel;
            }
        }

        float Snap ()
        {
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Move: return kMoveSnap;
                case PreviewWindow.SpinnerMode.Scale: return kScaleSnap;
                default: return kRotateSnap;
            }
        }

        void Apply (float delta)
        {
            Vector3 value = Axis () * delta;
            if (footMode && dragZone_ != kZoneCenter) {
                // 扇形の X / Y / Z は Roll / Twist / Bank。角度は (roll, bank, twist) の並び
                window_.SpinFootAngles (new Vector3 (value.x, value.z, value.y));
                return;
            }
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Move: window_.SpinMove (value); break;
                case PreviewWindow.SpinnerMode.Scale: window_.SpinScale (value); break;
                default: window_.SpinRotate (value); break;
            }
        }

        string Readout ()
        {
            string axis = dragZone_ == kZoneCenter ? Tr ("TRANSFORM_OVERLAY_ALL_AXES") : ZoneNames ()[dragZone_];
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Move: return string.Format ("{0} {1:+0.0;-0.0} cm", axis, applied_ * 100);
                case PreviewWindow.SpinnerMode.Scale: return string.Format ("{0} {1:+0.00;-0.00}", axis, applied_);
                default: return string.Format ("{0} {1:+0.0;-0.0}°", axis, applied_);
            }
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Transform.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-transform", "Transform", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    sealed class TransformOverlayScene : TransformOverlay
    {
    }

}
