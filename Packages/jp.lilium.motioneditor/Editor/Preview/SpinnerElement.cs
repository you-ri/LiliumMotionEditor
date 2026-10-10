using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

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
                case PreviewWindow.SpinnerMode.Rotate: return Tr ("CONTROLLER_OVERLAY_MULTI_AXIS");
                case PreviewWindow.SpinnerMode.Scale: return Tr ("CONTROLLER_OVERLAY_ALL_AXES");
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
            readout_.text = target ?? "";
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
                readout_.text = Tr ("CONTROLLER_OVERLAY_MULTI_AXIS_READOUT", totalView_.x.ToString ("+0;-0"), totalView_.y.ToString ("+0;-0"));
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
            string axis = dragZone_ == kZoneCenter ? Tr ("CONTROLLER_OVERLAY_ALL_AXES") : ZoneNames ()[dragZone_];
            switch (window_.spinnerMode) {
                case PreviewWindow.SpinnerMode.Move: return string.Format ("{0} {1:+0.0;-0.0} cm", axis, applied_ * 100);
                case PreviewWindow.SpinnerMode.Scale: return string.Format ("{0} {1:+0.00;-0.00}", axis, applied_);
                default: return string.Format ("{0} {1:+0.0;-0.0}°", axis, applied_);
            }
        }
    }

}
