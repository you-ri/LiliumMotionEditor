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
    /// カーブエディタ（S5）。選んでいる物のカーブを、左の一覧で選んで右のグラフに描く。
    /// 位置・数値のカーブはそのまま、回転は保存が四元数なので角度（X/Y/Z）に直して描き、キーの値を角度で直せる（接線は数値のカーブだけ）。
    /// 操作: キーのクリックで選択（Shift で追加）・ドラッグで時刻と値、空いた所のクリックでフレーム移動、ダブルクリックでキーを足す、
    /// 右クリックで接線の種類と削除、Delete で削除、F で全体を収める、ホイールで横の拡大縮小（Ctrl で縦）、中ボタン / Alt ドラッグで移動
    /// </summary>
    [Icon (Icons.kFolder + "Curves.png")]
    [Overlay (typeof (PreviewWindow), "mkt-curves", "Curves", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom)]
    class CurveEditorOverlay : Overlay, ICreateHorizontalToolbar
    {
        const float kWidth = 640;
        const float kHeight = 220;
        const float kListWidth = 160;
        static readonly Color kPanelColor = new Color (0.17f, 0.17f, 0.18f);

        PreviewWindow window_;
        ScrollView list_;
        CurveView view_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            Build (root);
            return root;
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            VisualElement root = new VisualElement ();
            Build (root);
            toolbar.Add (root);
            return toolbar;
        }

        void Build (VisualElement root)
        {
            Unsubscribe ();
            root.style.width = kWidth;
            root.style.height = kHeight;
            root.style.flexDirection = FlexDirection.Row;
            root.style.backgroundColor = kPanelColor;
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return;

            list_ = new ScrollView ();
            list_.style.width = kListWidth;
            list_.style.flexShrink = 0;
            root.Add (list_);

            view_ = new CurveView (window_);
            IMGUIContainer graph = new IMGUIContainer (view_.OnGUI) { focusable = true };
            graph.style.flexGrow = 1;
            view_.container = graph;
            root.Add (graph);

            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            Rebuild ();
            window_.stateChanged += Rebuild;
            window_.keysChanged += OnKeysChanged;
            window_.poseSampled += Repaint;
            window_.propertySelectionChanged += Rebuild;
        }

        void Unsubscribe ()
        {
            if (window_ == null) return;
            window_.stateChanged -= Rebuild;
            window_.keysChanged -= OnKeysChanged;
            window_.poseSampled -= Repaint;
            window_.propertySelectionChanged -= Rebuild;
        }

        public override void OnWillBeDestroyed ()
        {
            Unsubscribe ();
            base.OnWillBeDestroyed ();
        }

        void OnKeysChanged ()
        {
            if (view_ == null) return;
            // カーブの組が変わった（キーを初めて打った骨など）ときは一覧から作り直す
            if (!view_.SameCurves (window_.GetSelectedCurves ())) Rebuild ();
            else {
                view_.Invalidate ();
                Repaint ();
            }
        }

        void Repaint ()
        {
            if (view_ != null && view_.container != null) view_.container.MarkDirtyRepaint ();
        }

        /// <summary>
        /// 選択・クリップが替わったとき。一覧を作り直す
        /// </summary>
        void Rebuild ()
        {
            if (window_ == null || list_ == null) return;
            view_.SetOwners (window_.GetSelectedCurves ());
            list_.Clear ();
            string problem = window_.GetCurveEditProblem ();
            if (view_.channels.Count == 0) {
                list_.Add (Note (window_.clip == null ? Tr ("CURVE_EDITOR_OVERLAY_OPEN_CLIP") : Tr ("CURVE_EDITOR_OVERLAY_SELECT_BONE")));
            }
            string owner = null;
            foreach (CurveView.Channel channel in view_.channels) {
                if (channel.owner != owner) {
                    owner = channel.owner;
                    Label header = new Label (owner);
                    header.style.unityFontStyleAndWeight = FontStyle.Bold;
                    header.style.paddingLeft = 2;
                    header.style.marginTop = 2;
                    list_.Add (header);
                }
                CurveView.Channel captured = channel;
                Toggle toggle = new Toggle (channel.label) { value = channel.visible };
                toggle.labelElement.style.color = channel.color;
                toggle.labelElement.style.minWidth = 0;
                toggle.labelElement.style.width = 110;
                toggle.style.marginLeft = 8;
                toggle.RegisterValueChangedCallback (e => {
                    captured.visible = e.newValue;
                    view_.Invalidate ();
                    Repaint ();
                });
                list_.Add (toggle);
            }
            if (problem != null && window_.clip != null) list_.Add (Note (Tr ("CURVE_EDITOR_OVERLAY_CANNOT_WRITE", problem)));
            view_.FrameAll ();
            Repaint ();
        }

        static Label Note (string text)
        {
            Label label = new Label (text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 10;
            label.style.color = new Color (0.7f, 0.7f, 0.7f);
            label.style.paddingLeft = 4;
            return label;
        }
    }

    /// <summary>
    /// カーブのグラフ（IMGUI）
    /// </summary>
    sealed class CurveView
    {
        const float kKeySize = 7;
        const float kPickDistance = 6;
        const float kMargin = 18;
        static readonly Color kBackColor = new Color (0.13f, 0.13f, 0.14f);
        static readonly Color kGridColor = new Color (1, 1, 1, 0.06f);
        static readonly Color kGridStrongColor = new Color (1, 1, 1, 0.12f);
        static readonly Color kPlayheadColor = new Color (1, 0.3f, 0.25f);
        static readonly Color[] kAxisColors = {
            new Color (0.93f, 0.4f, 0.36f),
            new Color (0.45f, 0.8f, 0.4f),
            new Color (0.42f, 0.62f, 0.98f),
        };
        static readonly Color kOtherColor = new Color (0.95f, 0.8f, 0.35f);

        public sealed class Channel
        {
            public string owner;
            public string label;
            public EditorCurveBinding binding;
            /// <summary>回転の軸（0〜2）。数値のカーブは -1</summary>
            public int axis = -1;
            public Color color;
            public bool visible = true;

            public bool isRotation
            {
                get { return axis >= 0; }
            }

            public string key
            {
                get { return binding.path + "|" + binding.propertyName + "|" + axis; }
            }
        }

        struct SelectedKey
        {
            public Channel channel;
            public int frame;
        }

        readonly PreviewWindow window_;
        public IMGUIContainer container;
        public readonly List<Channel> channels = new List<Channel> ();
        readonly List<SelectedKey> selected_ = new List<SelectedKey> ();
        readonly Dictionary<string, Vector3[]> eulerCache_ = new Dictionary<string, Vector3[]> ();
        List<PreviewWindow.CurveOwner> owners_ = new List<PreviewWindow.CurveOwner> ();

        float frameMin_ = 0, frameMax_ = 60, valueMin_ = -1, valueMax_ = 1;
        enum Drag { None, Pan, Keys, Scrub }
        Drag drag_;
        Vector2 dragStart_;
        float dragFrameMin_, dragFrameMax_, dragValueMin_, dragValueMax_;
        Dictionary<EditorCurveBinding, AnimationCurve> snapshot_;
        List<(SelectedKey key, float value, Vector3 euler)> dragOrigins_;

        public CurveView (PreviewWindow window)
        {
            window_ = window;
        }

        AnimationClip clip
        {
            get { return window_.targetClip; }
        }

        float rate
        {
            get { return clip != null && clip.frameRate > 0 ? clip.frameRate : 60; }
        }

        public bool SameCurves (List<PreviewWindow.CurveOwner> owners)
        {
            if (owners.Count != owners_.Count) return false;
            for (int i = 0; i < owners.Count; i++) {
                if (owners[i].label != owners_[i].label || !owners[i].bindings.SequenceEqual (owners_[i].bindings)) return false;
            }
            return true;
        }

        public void SetOwners (List<PreviewWindow.CurveOwner> owners)
        {
            Dictionary<string, bool> visible = channels.ToDictionary (c => c.key, c => c.visible);
            owners_ = owners;
            channels.Clear ();
            foreach (PreviewWindow.CurveOwner owner in owners) {
                HashSet<string> rotations = new HashSet<string> ();
                foreach (EditorCurveBinding binding in owner.bindings) {
                    if (CurveEdit.IsRotation (binding)) {
                        if (!rotations.Add (binding.path)) continue;
                        for (int axis = 0; axis < 3; axis++) {
                            channels.Add (new Channel {
                                owner = owner.label,
                                label = Short (binding.path) + " Rot " + "XYZ"[axis],
                                binding = CurveEdit.RotationBinding (binding.path, 0),
                                axis = axis,
                                color = kAxisColors[axis],
                            });
                        }
                        continue;
                    }
                    channels.Add (new Channel {
                        owner = owner.label,
                        label = Short (binding.path) + " " + PropertyLabel (binding.propertyName),
                        binding = binding,
                        color = AxisColor (binding.propertyName),
                    });
                }
            }
            foreach (Channel channel in channels) {
                bool v;
                if (visible.TryGetValue (channel.key, out v)) channel.visible = v;
            }
            selected_.RemoveAll (s => !channels.Contains (s.channel));
            Invalidate ();
        }

        static string Short (string path)
        {
            int slash = path.LastIndexOf ('/');
            return slash >= 0 ? path.Substring (slash + 1) : path;
        }

        static string PropertyLabel (string property)
        {
            if (property.StartsWith ("m_LocalPosition.")) return "Pos " + property.Substring (16).ToUpperInvariant ();
            if (property.StartsWith ("m_LocalScale.")) return "Scale " + property.Substring (13).ToUpperInvariant ();
            return property;
        }

        static Color AxisColor (string property)
        {
            if (property.EndsWith (".x")) return kAxisColors[0];
            if (property.EndsWith (".y")) return kAxisColors[1];
            if (property.EndsWith (".z")) return kAxisColors[2];
            return kOtherColor;
        }

        /// <summary>
        /// キーが変わったとき。角度に直した値を作り直す
        /// </summary>
        public void Invalidate ()
        {
            eulerCache_.Clear ();
        }

        // ---- 値 ----

        int lastFrame
        {
            get {
                int last = 0;
                if (clip == null) return last;
                foreach (Channel channel in channels) {
                    foreach (int frame in KeyFrames (channel)) last = Mathf.Max (last, frame);
                }
                return last;
            }
        }

        /// <summary>
        /// 回転を 0 フレームから 1 フレームずつ角度にしたもの（前のフレームに近い表し方を選んで、つながった線にする）
        /// </summary>
        Vector3[] Eulers (string path)
        {
            Vector3[] eulers;
            if (eulerCache_.TryGetValue (path, out eulers)) return eulers;
            int count = Mathf.Max (lastFrame, Mathf.CeilToInt (frameMax_)) + 2;
            eulers = new Vector3[count];
            Vector3 hint = Vector3.zero;
            for (int f = 0; f < count; f++) {
                Quaternion q = CurveEdit.EvaluateRotation (clip, path, f / rate);
                hint = f == 0 ? Wrap (q.eulerAngles) : EulerAngles.Closest (q, hint);
                eulers[f] = hint;
            }
            eulerCache_[path] = eulers;
            return eulers;
        }

        static Vector3 Wrap (Vector3 euler)
        {
            for (int i = 0; i < 3; i++) euler[i] = Mathf.DeltaAngle (0, euler[i]);
            return euler;
        }

        float Evaluate (Channel channel, float frame)
        {
            if (channel.isRotation) {
                Vector3[] eulers = Eulers (channel.binding.path);
                if (frame <= 0) return eulers[0][channel.axis];
                int f = Mathf.Min (Mathf.FloorToInt (frame), eulers.Length - 2);
                return Mathf.Lerp (eulers[f][channel.axis], eulers[f + 1][channel.axis], Mathf.Clamp01 (frame - f));
            }
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, channel.binding);
            return curve != null ? curve.Evaluate (frame / rate) : 0;
        }

        Vector3 EulerAt (Channel channel, int frame)
        {
            Vector3[] eulers = Eulers (channel.binding.path);
            return eulers[Mathf.Clamp (frame, 0, eulers.Length - 1)];
        }

        IEnumerable<int> KeyFrames (Channel channel)
        {
            if (clip == null) return Enumerable.Empty<int> ();
            if (channel.isRotation) return ClipKeyUtility.GetKeyFrames (clip, b => b.path == channel.binding.path && CurveEdit.IsRotation (b));
            return ClipKeyUtility.GetKeyFrames (clip, b => b == channel.binding);
        }

        float KeyValue (Channel channel, int frame)
        {
            if (channel.isRotation) return EulerAt (channel, frame)[channel.axis];
            return Evaluate (channel, frame);
        }

        // ---- 座標 ----

        Rect rect_;

        Rect plotRect
        {
            get { return new Rect (rect_.x + 34, rect_.y + kMargin, Mathf.Max (10, rect_.width - 40), Mathf.Max (10, rect_.height - kMargin - 6)); }
        }

        float FrameToX (float frame)
        {
            Rect r = plotRect;
            return r.x + (frame - frameMin_) / Mathf.Max (1e-3f, frameMax_ - frameMin_) * r.width;
        }

        float XToFrame (float x)
        {
            Rect r = plotRect;
            return frameMin_ + (x - r.x) / r.width * (frameMax_ - frameMin_);
        }

        float ValueToY (float value)
        {
            Rect r = plotRect;
            return r.yMax - (value - valueMin_) / Mathf.Max (1e-6f, valueMax_ - valueMin_) * r.height;
        }

        float YToValue (float y)
        {
            Rect r = plotRect;
            return valueMin_ + (r.yMax - y) / r.height * (valueMax_ - valueMin_);
        }

        /// <summary>
        /// 見えているカーブ全体を収める
        /// </summary>
        public void FrameAll ()
        {
            if (clip == null) return;
            int last = Mathf.Max (10, lastFrame);
            frameMin_ = -1;
            frameMax_ = last + 1;
            float min = float.MaxValue, max = float.MinValue;
            foreach (Channel channel in channels.Where (c => c.visible)) {
                for (int f = 0; f <= last; f++) {
                    float v = Evaluate (channel, f);
                    min = Mathf.Min (min, v);
                    max = Mathf.Max (max, v);
                }
            }
            if (min > max) {
                min = -1;
                max = 1;
            }
            float pad = Mathf.Max (0.01f, (max - min) * 0.1f);
            valueMin_ = min - pad;
            valueMax_ = max + pad;
        }

        // ---- 描画と操作 ----

        public void OnGUI ()
        {
            if (container == null) return;
            rect_ = container.contentRect;
            if (rect_.width < 20 || rect_.height < 20) return;
            Event ev = Event.current;

            if (ev.type == EventType.Repaint) Draw ();
            if (clip == null) return;
            HandleInput (ev);
        }

        void Draw ()
        {
            EditorGUI.DrawRect (rect_, kBackColor);
            Rect r = plotRect;
            DrawGrid (r);
            if (clip == null) return;

            foreach (Channel channel in channels.Where (c => c.visible)) {
                int samples = Mathf.Clamp ((int)r.width / 2, 16, 800);
                Vector3[] points = new Vector3[samples + 1];
                for (int i = 0; i <= samples; i++) {
                    float frame = Mathf.Lerp (frameMin_, frameMax_, i / (float)samples);
                    points[i] = new Vector3 (FrameToX (frame), Mathf.Clamp (ValueToY (Evaluate (channel, frame)), r.y - 2, r.yMax + 2), 0);
                }
                Handles.color = channel.color;
                Handles.DrawAAPolyLine (2, points);
                foreach (int frame in KeyFrames (channel)) {
                    float x = FrameToX (frame);
                    if (x < r.x - kKeySize || x > r.xMax + kKeySize) continue;
                    float y = ValueToY (KeyValue (channel, frame));
                    bool isSelected = selected_.Any (s => s.channel == channel && s.frame == frame);
                    Rect key = new Rect (x - kKeySize * 0.5f, y - kKeySize * 0.5f, kKeySize, kKeySize);
                    EditorGUI.DrawRect (key, isSelected ? Color.white : Color.Lerp (channel.color, Color.black, 0.2f));
                }
            }

            float playhead = FrameToX (window_.currentTime * rate);
            if (playhead >= r.x && playhead <= r.xMax) EditorGUI.DrawRect (new Rect (playhead, rect_.y, 1, rect_.height), kPlayheadColor);
        }

        void DrawGrid (Rect r)
        {
            float frameStep = NiceStep ((frameMax_ - frameMin_) / Mathf.Max (1, r.width / 60));
            frameStep = Mathf.Max (1, frameStep);
            GUIStyle style = EditorStyles.miniLabel;
            for (float f = Mathf.Ceil (frameMin_ / frameStep) * frameStep; f <= frameMax_; f += frameStep) {
                float x = FrameToX (f);
                EditorGUI.DrawRect (new Rect (x, r.y, 1, r.height), Mathf.Approximately (f, 0) ? kGridStrongColor : kGridColor);
                GUI.Label (new Rect (x + 2, rect_.y, 40, 14), ((int)f).ToString (), style);
            }
            float valueStep = NiceStep ((valueMax_ - valueMin_) / Mathf.Max (1, r.height / 30));
            for (float v = Mathf.Ceil (valueMin_ / valueStep) * valueStep; v <= valueMax_; v += valueStep) {
                float y = ValueToY (v);
                EditorGUI.DrawRect (new Rect (r.x, y, r.width, 1), Mathf.Abs (v) < valueStep * 0.01f ? kGridStrongColor : kGridColor);
                GUI.Label (new Rect (rect_.x + 1, y - 7, 34, 14), v.ToString (valueStep < 1 ? "0.##" : "0"), style);
            }
        }

        static float NiceStep (float raw)
        {
            if (raw <= 0) return 1;
            float magnitude = Mathf.Pow (10, Mathf.Floor (Mathf.Log10 (raw)));
            float normalized = raw / magnitude;
            float nice = normalized < 1.5f ? 1 : normalized < 3.5f ? 2 : normalized < 7.5f ? 5 : 10;
            return nice * magnitude;
        }

        bool TryPickKey (Vector2 position, out SelectedKey picked)
        {
            float best = kPickDistance;
            picked = default;
            bool found = false;
            foreach (Channel channel in channels.Where (c => c.visible)) {
                foreach (int frame in KeyFrames (channel)) {
                    float distance = Vector2.Distance (position, new Vector2 (FrameToX (frame), ValueToY (KeyValue (channel, frame))));
                    if (distance > best) continue;
                    best = distance;
                    picked = new SelectedKey { channel = channel, frame = frame };
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// マウスの下を通っているカーブ（ダブルクリックでキーを足す先）
        /// </summary>
        Channel NearestCurve (Vector2 position, float frame)
        {
            Channel best = null;
            float bestDistance = 12;
            foreach (Channel channel in channels.Where (c => c.visible)) {
                float distance = Mathf.Abs (ValueToY (Evaluate (channel, frame)) - position.y);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = channel;
            }
            return best;
        }

        void HandleInput (Event ev)
        {
            Rect r = plotRect;
            int id = GUIUtility.GetControlID (FocusType.Keyboard);
            switch (ev.GetTypeForControl (id)) {
                case EventType.ScrollWheel:
                    if (!rect_.Contains (ev.mousePosition)) return;
                    float zoom = ev.delta.y > 0 ? 1.15f : 1 / 1.15f;
                    if (ev.control) {
                        float value = YToValue (ev.mousePosition.y);
                        valueMin_ = value + (valueMin_ - value) * zoom;
                        valueMax_ = value + (valueMax_ - value) * zoom;
                    }
                    else {
                        float frame = XToFrame (ev.mousePosition.x);
                        frameMin_ = frame + (frameMin_ - frame) * zoom;
                        frameMax_ = frame + (frameMax_ - frame) * zoom;
                    }
                    Invalidate ();
                    ev.Use ();
                    break;

                case EventType.MouseDown:
                    if (!rect_.Contains (ev.mousePosition)) return;
                    GUIUtility.keyboardControl = id;
                    container.Focus ();
                    if (ev.button == 2 || (ev.button == 0 && ev.alt)) {
                        BeginDrag (Drag.Pan, ev);
                        GUIUtility.hotControl = id;
                        ev.Use ();
                        return;
                    }
                    if (ev.button != 0) return;
                    SelectedKey picked;
                    if (TryPickKey (ev.mousePosition, out picked)) {
                        bool already = selected_.Any (s => s.channel == picked.channel && s.frame == picked.frame);
                        if (ev.shift) {
                            if (already) selected_.RemoveAll (s => s.channel == picked.channel && s.frame == picked.frame);
                            else selected_.Add (picked);
                        }
                        else if (!already) {
                            selected_.Clear ();
                            selected_.Add (picked);
                        }
                        if (selected_.Count > 0 && window_.GetCurveEditProblem () == null) {
                            BeginDrag (Drag.Keys, ev);
                            GUIUtility.hotControl = id;
                        }
                        ev.Use ();
                        return;
                    }
                    if (ev.clickCount == 2) {
                        AddKey (ev.mousePosition);
                        ev.Use ();
                        return;
                    }
                    if (!ev.shift) selected_.Clear ();
                    BeginDrag (Drag.Scrub, ev);
                    Scrub (ev.mousePosition.x);
                    GUIUtility.hotControl = id;
                    ev.Use ();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) return;
                    UpdateDrag (ev);
                    ev.Use ();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) return;
                    GUIUtility.hotControl = 0;
                    EndDrag ();
                    ev.Use ();
                    break;

                case EventType.ContextClick:
                    if (!rect_.Contains (ev.mousePosition)) return;
                    ShowContextMenu (ev.mousePosition);
                    ev.Use ();
                    break;

                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl != id) return;
                    if (ev.keyCode == KeyCode.Delete || ev.keyCode == KeyCode.Backspace) {
                        DeleteSelected ();
                        ev.Use ();
                    }
                    else if (ev.keyCode == KeyCode.F) {
                        FrameAll ();
                        Invalidate ();
                        ev.Use ();
                    }
                    break;
            }
            if (ev.type == EventType.Used) container.MarkDirtyRepaint ();
        }

        void BeginDrag (Drag drag, Event ev)
        {
            drag_ = drag;
            dragStart_ = ev.mousePosition;
            dragFrameMin_ = frameMin_;
            dragFrameMax_ = frameMax_;
            dragValueMin_ = valueMin_;
            dragValueMax_ = valueMax_;
            if (drag != Drag.Keys) return;

            // 動かす前のカーブを取っておき、ドラッグのたびにそこから当て直す（少しずつずらすと丸めがたまる）
            snapshot_ = new Dictionary<EditorCurveBinding, AnimationCurve> ();
            dragOrigins_ = new List<(SelectedKey, float, Vector3)> ();
            foreach (SelectedKey key in selected_) {
                foreach (EditorCurveBinding binding in Bindings (key.channel)) {
                    if (snapshot_.ContainsKey (binding)) continue;
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                    if (curve != null) snapshot_[binding] = new AnimationCurve (curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
                }
                dragOrigins_.Add ((key, KeyValue (key.channel, key.frame), key.channel.isRotation ? EulerAt (key.channel, key.frame) : Vector3.zero));
            }
            window_.BeginCurveEdit ("Edit Curve Keys");
        }

        static IEnumerable<EditorCurveBinding> Bindings (Channel channel)
        {
            if (!channel.isRotation) {
                yield return channel.binding;
                yield break;
            }
            for (int i = 0; i < 4; i++) yield return CurveEdit.RotationBinding (channel.binding.path, i);
        }

        void UpdateDrag (Event ev)
        {
            Vector2 delta = ev.mousePosition - dragStart_;
            Rect r = plotRect;
            switch (drag_) {
                case Drag.Pan:
                    float frames = delta.x / r.width * (dragFrameMax_ - dragFrameMin_);
                    float values = delta.y / r.height * (dragValueMax_ - dragValueMin_);
                    frameMin_ = dragFrameMin_ - frames;
                    frameMax_ = dragFrameMax_ - frames;
                    valueMin_ = dragValueMin_ + values;
                    valueMax_ = dragValueMax_ + values;
                    Invalidate ();
                    break;
                case Drag.Scrub:
                    Scrub (ev.mousePosition.x);
                    break;
                case Drag.Keys:
                    int frameDelta = Mathf.RoundToInt (delta.x / r.width * (frameMax_ - frameMin_));
                    float valueDelta = -delta.y / r.height * (valueMax_ - valueMin_);
                    if (ev.shift) {
                        // Shift で縦か横の片方だけ
                        if (Mathf.Abs (delta.x) > Mathf.Abs (delta.y)) valueDelta = 0;
                        else frameDelta = 0;
                    }
                    ApplyKeyDrag (frameDelta, valueDelta);
                    break;
            }
        }

        void ApplyKeyDrag (int frameDelta, float valueDelta)
        {
            foreach (KeyValuePair<EditorCurveBinding, AnimationCurve> pair in snapshot_) {
                AnimationUtility.SetEditorCurve (clip, pair.Key, new AnimationCurve (pair.Value.keys) { preWrapMode = pair.Value.preWrapMode, postWrapMode = pair.Value.postWrapMode });
            }
            Invalidate ();
            List<SelectedKey> moved = new List<SelectedKey> ();
            // 回転の同じ骨の同じフレームは 1 回だけ時刻を動かす（3 軸で 4 本を共有するため）
            HashSet<string> movedRotations = new HashSet<string> ();
            foreach ((SelectedKey key, float value, Vector3 euler) origin in dragOrigins_) {
                SelectedKey key = origin.key;
                int target = Mathf.Max (0, key.frame + frameDelta);
                if (key.channel.isRotation) {
                    string id = key.channel.binding.path + "|" + key.frame;
                    if (target != key.frame && movedRotations.Add (id)) CurveEdit.MoveKey (clip, key.channel.binding, key.frame, target, null);
                    if (valueDelta != 0) {
                        Vector3 euler = origin.euler;
                        euler[key.channel.axis] = origin.value + valueDelta;
                        CurveEdit.SetRotationKey (clip, key.channel.binding.path, target, Quaternion.Euler (euler));
                    }
                }
                else {
                    CurveEdit.MoveKey (clip, key.channel.binding, key.frame, target, origin.value + valueDelta);
                }
                moved.Add (new SelectedKey { channel = key.channel, frame = target });
            }
            selected_.Clear ();
            selected_.AddRange (moved);
            Invalidate ();
            window_.EndCurveEdit ();
        }

        void EndDrag ()
        {
            if (drag_ == Drag.Keys && dragOrigins_ != null) {
                // 動かしたキーの位置を、次のドラッグの元にする
                snapshot_ = null;
                dragOrigins_ = null;
            }
            drag_ = Drag.None;
        }

        void Scrub (float x)
        {
            window_.SetFrame (Mathf.Max (0, Mathf.RoundToInt (XToFrame (x))));
        }

        void AddKey (Vector2 position)
        {
            if (window_.GetCurveEditProblem () != null) return;
            int frame = Mathf.Max (0, Mathf.RoundToInt (XToFrame (position.x)));
            Channel channel = NearestCurve (position, frame);
            if (channel == null) return;
            window_.BeginCurveEdit ("Add Curve Key");
            if (channel.isRotation) CurveEdit.SetRotationKey (clip, channel.binding.path, frame, CurveEdit.EvaluateRotation (clip, channel.binding.path, frame / rate));
            else CurveEdit.SetKey (clip, channel.binding, frame, Evaluate (channel, frame));
            selected_.Clear ();
            selected_.Add (new SelectedKey { channel = channel, frame = frame });
            Invalidate ();
            window_.EndCurveEdit ();
        }

        void DeleteSelected ()
        {
            if (selected_.Count == 0 || window_.GetCurveEditProblem () != null) return;
            window_.BeginCurveEdit ("Delete Curve Keys");
            foreach (SelectedKey key in selected_) CurveEdit.RemoveKey (clip, key.channel.binding, key.frame);
            selected_.Clear ();
            Invalidate ();
            window_.EndCurveEdit ();
        }

        void ShowContextMenu (Vector2 position)
        {
            SelectedKey picked;
            if (TryPickKey (position, out picked) && !selected_.Any (s => s.channel == picked.channel && s.frame == picked.frame)) {
                selected_.Clear ();
                selected_.Add (picked);
            }
            GenericMenu menu = new GenericMenu ();
            bool editable = window_.GetCurveEditProblem () == null && selected_.Count > 0;
            bool numeric = selected_.Any (s => !s.channel.isRotation);
            AddTangent (menu, "Clamped Auto", AnimationUtility.TangentMode.ClampedAuto, editable && numeric);
            AddTangent (menu, "Auto", AnimationUtility.TangentMode.Auto, editable && numeric);
            AddTangent (menu, "Linear", AnimationUtility.TangentMode.Linear, editable && numeric);
            AddTangent (menu, "Constant", AnimationUtility.TangentMode.Constant, editable && numeric);
            menu.AddSeparator ("");
            if (editable) menu.AddItem (new GUIContent ("Delete Key"), false, DeleteSelected);
            else menu.AddDisabledItem (new GUIContent ("Delete Key"));
            menu.AddItem (new GUIContent ("Frame All (F)"), false, () => {
                FrameAll ();
                Invalidate ();
                container.MarkDirtyRepaint ();
            });
            menu.ShowAsContext ();
        }

        void AddTangent (GenericMenu menu, string label, AnimationUtility.TangentMode mode, bool enabled)
        {
            GUIContent content = new GUIContent ("Tangent/" + label);
            if (!enabled) {
                menu.AddDisabledItem (content);
                return;
            }
            menu.AddItem (content, false, () => {
                window_.BeginCurveEdit ("Curve Tangent");
                foreach (SelectedKey key in selected_.Where (s => !s.channel.isRotation)) {
                    CurveEdit.SetTangentMode (clip, key.channel.binding, key.frame, mode);
                }
                Invalidate ();
                window_.EndCurveEdit ();
            });
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Curves.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-curves", "Curves", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom)]
    sealed class CurveEditorOverlayScene : CurveEditorOverlay
    {
    }

}
