using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Lilium
{

    /// <summary>
    /// タイムラインを UI Toolkit の Overlay として出す。ドッキング・フロートは
    /// Unity の Overlay の仕組みに任せる。中身は TransportBar（再生ボタンの段）と TimebarElement（目盛りとトラック）。
    ///
    /// 上端・下端のドック域は HorizontalToolbar のレイアウトしか受けず、Panel だけのオーバーレイは畳まれてしまう。
    /// 下端に中身ごと置けるよう、ツールバーのレイアウトでも同じ中身を出す（ICreateHorizontalToolbar）
    /// </summary>
    [Icon (Icons.kFolder + "Timeline.png")]
    [Overlay (typeof (PreviewWindow), "mkt-timeline", "Timeline", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom,
        defaultLayout = Layout.HorizontalToolbar)]
    class TimelineOverlay : Overlay, ICreateHorizontalToolbar
    {
        const float kWidth = 640;
        const float kMinWidth = 320;
        /// <summary>ドック域の中で、オーバーレイのつまみと枠が取る幅</summary>
        const float kDockMargin = 20;
        /// <summary>Unity がツールバーのドック域に付けるクラス</summary>
        const string kToolbarAreaClass = "overlay-toolbar-area";
        const string kTopToolbarAreaName = "overlay-toolbar__top";
        static readonly Color kPanelColor = new Color (0.17f, 0.17f, 0.18f);

        PreviewWindow window_;
        TransportBar transport_;
        TimebarElement timebar_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            BuildContent (root, false);
            return root;
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            // ツールバーは横に並べる前提の器なので、中身は縦に積む箱に入れて渡す
            VisualElement root = new VisualElement ();
            root.style.flexDirection = FlexDirection.Column;
            BuildContent (root, true);
            toolbar.Add (root);
            return toolbar;
        }

        /// <summary>
        /// 上端・下端のドック域に置いたとき。ドック域の幅いっぱいに広げ（ドック域は中身の幅で並べるだけで、伸ばしてはくれない）、
        /// 高さのつまみは伸びる側に出す（下端のドック域なら上端、上端のドック域なら下端）
        /// </summary>
        static void AttachToDockArea (VisualElement root, VisualElement topHandle, VisualElement bottomHandle)
        {
            VisualElement area = null;
            EventCallback<GeometryChangedEvent> onResize = e => {
                float width = area.resolvedStyle.width - kDockMargin;
                if (!float.IsNaN (width) && width > kMinWidth) root.style.width = width;
            };
            root.RegisterCallback<AttachToPanelEvent> (e => {
                for (area = root.parent; area != null && !area.ClassListContains (kToolbarAreaClass); area = area.parent) { }
                if (area == null) return;
                bool atTop = area.name == kTopToolbarAreaName;
                topHandle.style.display = atTop ? DisplayStyle.None : DisplayStyle.Flex;
                bottomHandle.style.display = atTop ? DisplayStyle.Flex : DisplayStyle.None;
                area.RegisterCallback (onResize);
                onResize (null);
            });
            root.RegisterCallback<DetachFromPanelEvent> (e => {
                if (area != null) area.UnregisterCallback (onResize);
                area = null;
            });
        }

        /// <summary>
        /// レイアウトを切り替えると作り直されるので、前の中身の購読を外してから作る。
        /// 高さのつまみは、ドッキング中（上端・下端のドック域）は上端、パネルのときは下端に置く
        /// </summary>
        void BuildContent (VisualElement root, bool docked)
        {
            Unsubscribe ();
            root.style.width = kWidth;
            root.style.backgroundColor = kPanelColor;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return;

            timebar_ = new TimebarElement (window_);
            transport_ = new TransportBar (window_, timebar_, containerWindow);
            VisualElement topHandle = timebar_.CreateResizeHandle (true);
            VisualElement bottomHandle = timebar_.CreateResizeHandle (false);
            root.Add (topHandle);
            root.Add (transport_);
            root.Add (timebar_);
            root.Add (bottomHandle);
            topHandle.style.display = DisplayStyle.None;
            if (docked) AttachToDockArea (root, topHandle, bottomHandle);

            window_.stackChanged += timebar_.RebuildTracks;
            window_.stateChanged += timebar_.RebuildTracks;
            window_.keysChanged += timebar_.RebuildTracks;
            window_.poseSampled += timebar_.RefreshPlayhead;
            window_.poseSampled += transport_.Refresh;
            window_.stateChanged += transport_.Refresh;
            window_.keysChanged += transport_.Refresh;

            timebar_.RebuildTracks ();
        }

        void Unsubscribe ()
        {
            if (window_ == null) return;
            if (timebar_ != null) {
                window_.stackChanged -= timebar_.RebuildTracks;
                window_.stateChanged -= timebar_.RebuildTracks;
                window_.keysChanged -= timebar_.RebuildTracks;
                window_.poseSampled -= timebar_.RefreshPlayhead;
            }
            if (transport_ != null) {
                window_.poseSampled -= transport_.Refresh;
                window_.stateChanged -= transport_.Refresh;
                window_.keysChanged -= transport_.Refresh;
            }
        }

        public override void OnWillBeDestroyed ()
        {
            Unsubscribe ();
            base.OnWillBeDestroyed ();
        }
    }

    /// <summary>
    /// 組み込みアイコン（暗いスキンでは d_ 付き）。トランスポートのボタンが使う
    /// </summary>
    static class TimelineIcons
    {
        public static Texture2D Load (string name)
        {
            Texture2D texture = EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture ("d_" + name) : null;
            return texture != null ? texture : EditorGUIUtility.FindTexture (name);
        }
    }

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
        readonly Toggle autoKey_;

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
            autoKey_ = new Toggle { text = "Auto Key", tooltip = "入: 操作したらキーを打つ。切: 今のフレームにキーがある物しか動かせない（プロジェクト設定の Auto Key と同じ）" };
            autoKey_.style.marginRight = 6;
            autoKey_.RegisterValueChangedCallback (e => {
                ProjectSettings settings = ProjectSettings.instance;
                settings.autoKey = e.newValue;
                settings.Save ();
            });
            poseGroup.Add (autoKey_);
            poseGroup.Add (MiniButton ("Key", "今のフレームで全部（体・手・Rig の重み・表情・任意のプロパティ）にキーを打つ（Stacker の Key All と同じ）", window_.KeyAll));
            poseGroup.Add (WithShortcut (MiniButton ("Copy", null, window_.CopyPose), "今のフレームのポーズをコピー。骨・点を選んでいれば選んだ物（複数可）だけ、選んでいなければ全体", "Copy Pose"));
            poseGroup.Add (WithShortcut (MiniButton ("Paste", null, window_.PastePose), "コピーしたポーズを今のフレームに貼る", "Paste Pose"));
            poseGroup.Add (MiniButton ("Reset", "骨の向きを基準の姿勢に戻してキーを打つ", window_.ResetPose));
            Add (poseGroup);

            Add (Spacer ());

            VisualElement transport = new VisualElement ();
            transport.style.flexDirection = FlexDirection.Row;
            // 両端は先頭・末尾（Home / End と同じ）。読み込んだクリップは毎フレームにキーがあり、前後のキーでは端へ行けない
            transport.Add (WithShortcut (IconButton ("Animation.FirstKey", null, () => window_.SetFrame (0)), "先頭へ", "First Frame"));
            transport.Add (WithShortcut (IconButton ("Animation.PrevKey", null, () => window_.GoToKey (-1)), "前のキー", "Previous Key"));
            playButton_ = WithShortcut (IconButton ("PlayButton", null, window_.TogglePlay), "再生 / 停止", "Play");
            transport.Add (playButton_);
            transport.Add (WithShortcut (IconButton ("Animation.NextKey", null, () => window_.GoToKey (1)), "次のキー", "Next Key"));
            transport.Add (WithShortcut (IconButton ("Animation.LastKey", null, () => window_.SetFrame (window_.GetLastKeyFrame ())), "末尾へ", "Last Key Frame"));
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

            Button fit = new Button (timebar_.FitTimeline) { text = "Fit", tooltip = "キーの範囲が収まるように目盛りを合わせる" };
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
            Texture2D texture = TimelineIcons.Load (icon);
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
            Texture2D icon = TimelineIcons.Load (clock.isPlaying ? "PauseButton" : "PlayButton");
            if (icon != null) playButton_.iconImage = Background.FromTexture2D (icon);

            frameField_.SetValueWithoutNotify (window_.frame);
            if (!clock.onGrid) {
                fractionLabel_.text = clock.frame.ToString ("0.0");
                fractionLabel_.tooltip = "今の時刻（フレーム）。キーは " + window_.frame + " に打つ";
                fractionLabel_.style.display = DisplayStyle.Flex;
            }
            else {
                fractionLabel_.style.display = DisplayStyle.None;
            }

            rangeLabel_.text = window_.targetClip != null ? "/ " + window_.GetLastKeyFrame () + "   " + clock.rate + " fps" : "";
            tracksField_.SetValueWithoutNotify (window_.showTracks);
            autoKey_.SetValueWithoutNotify (ProjectSettings.instance.autoKey);
        }
    }

    /// <summary>
    /// 目盛り（ルーラー）とトラック（クリップ全体＋骨ごと）。見た目:
    /// 黒い目盛り・キーのトラック。キーは通し番号の箱（今のフレームのキーは白）、キーの間は両矢印の線とフレーム数。
    /// 今のフレームは白い縦線。目盛りとトラックの上でホイールは横の拡大縮小、中ボタン（Alt+左）ドラッグは横の移動。
    ///
    /// 形は Painter2D（generateVisualContent）で毎回いまの状態から描き直し、数字は使い回す Label を絶対座標で置く
    /// </summary>
    sealed class TimebarElement : VisualElement
    {
        const float kRulerHeight = 20;
        const float kTrackHeight = 20;
        const float kLabelWidth = 120;
        const float kKeyHeight = 14;
        const float kMinPixelsPerFrame = 2;
        const float kMaxPixelsPerFrame = 60;
        const float kHandleHeight = 6;
        /// <summary>トラック一覧を高くしても、窓の高さからこれだけは残す</summary>
        const float kMaxHeightMargin = 160;
        /// <summary>
        /// 目盛りの数字の間隔の候補（フレーム）。数字どうしが kMinLabelSpacing ピクセル以上離れる最小のものを使う
        /// </summary>
        static readonly int[] kLabelSteps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };
        const float kMinLabelSpacing = 26;

        static readonly Color kRulerColor = new Color (0.04f, 0.04f, 0.04f);
        static readonly Color kRulerTickColor = new Color (0.55f, 0.55f, 0.55f);
        static readonly Color kRulerTextColor = new Color (0.8f, 0.8f, 0.8f);
        static readonly Color kNegativeTimeColor = new Color (0, 0, 0, 0.25f);
        /// <summary>演出の中でこのクリップが使われている区間（S15b）</summary>
        static readonly Color kTimelineRangeColor = new Color (0.35f, 0.65f, 1f, 0.9f);
        /// <summary>その中で前後のクリップと混ざる区間（キーは打たない）</summary>
        static readonly Color kTimelineBlendColor = new Color (1f, 0.6f, 0.2f, 0.9f);
        static readonly Color kLabelColor = new Color (0.13f, 0.13f, 0.14f);
        static readonly Color kLabelTextColor = new Color (0.82f, 0.82f, 0.82f);
        static readonly Color kTrackColor = new Color (0.45f, 0.45f, 0.45f);
        static readonly Color kTrackSeparatorColor = new Color (0.3f, 0.3f, 0.3f);
        static readonly Color kKeyColor = new Color (0.73f, 0.73f, 0.73f);
        static readonly Color kCurrentKeyColor = new Color (0.97f, 0.97f, 0.97f);
        static readonly Color kKeyBorderColor = new Color (0.2f, 0.2f, 0.2f);
        static readonly Color kKeyTextColor = new Color (0.12f, 0.12f, 0.12f);
        static readonly Color kIntervalColor = new Color (0.86f, 0.86f, 0.86f);
        static readonly Color kPlayheadColor = Color.white;
        static readonly Color kDragKeyColor = new Color (1f, 0.72f, 0.25f);
        static readonly Color kDeleteKeyColor = new Color (0.95f, 0.3f, 0.25f);
        static readonly Color kHandleColor = new Color (0, 0, 0, 0.15f);
        static readonly Color kSelectedKeyColor = new Color (0.45f, 0.72f, 1f);
        static readonly Color kBoxFillColor = new Color (0.45f, 0.72f, 1f, 0.15f);
        static readonly Color kBoxBorderColor = new Color (0.45f, 0.72f, 1f, 0.8f);
        /// <summary>これより動かなければ枠選択ではなくクリック（フレームを移すだけ）</summary>
        const float kBoxDragThreshold = 3;

        readonly PreviewWindow window_;
        readonly VisualElement rulerCanvas_;
        readonly ScrollView trackScroll_;
        readonly VisualElement tracksCanvas_;
        readonly VisualElement playhead_;
        readonly Label noClipLabel_;

        List<PreviewWindow.TimelineTrack> tracks_ = new List<PreviewWindow.TimelineTrack> ();
        int[] allKeys_ = new int[0];

        readonly List<Label> rulerLabels_ = new List<Label> ();
        readonly List<Label> trackNameLabels_ = new List<Label> ();
        readonly List<Label> keyNumberLabels_ = new List<Label> ();
        readonly List<Label> intervalLabels_ = new List<Label> ();

        // キーのドラッグ
        int dragTrack_ = -1;
        int dragKeyFrame_;
        int dragTargetFrame_;
        float dragStartX_;
        bool dragMoved_;
        bool dragDelete_;
        int dragPointerId_;

        bool panning_;
        int panPointerId_;
        bool scrubbing_;
        int scrubPointerId_;

        // 選んでいるキー（トラック名とフレーム）。トラックは選んだ骨で入れ替わるので、番号ではなく名前で持つ
        readonly HashSet<(string, int)> selectedKeys_ = new HashSet<(string, int)> ();
        // キーの無い所からのドラッグで出す枠
        bool boxSelecting_;
        bool boxMoved_;
        int boxPointerId_;
        Vector2 boxStart_;
        Vector2 boxEnd_;
        readonly HashSet<(string, int)> boxBase_ = new HashSet<(string, int)> ();
        // Ctrl+C で覚えたキーの選択（トラック名と、一番前のフレームからのずれ）。貼った後に貼ったキーを選ぶのに使う
        readonly List<(string, int)> copiedSelection_ = new List<(string, int)> ();

        public TimebarElement (PreviewWindow window)
        {
            window_ = window;
            style.flexDirection = FlexDirection.Column;
            style.flexShrink = 0;

            rulerCanvas_ = new VisualElement { name = "MktTimelineRuler" };
            rulerCanvas_.style.height = kRulerHeight;
            rulerCanvas_.style.flexShrink = 0;
            rulerCanvas_.generateVisualContent += OnGenerateRuler;
            Add (rulerCanvas_);

            trackScroll_ = new ScrollView (ScrollViewMode.Vertical);
            trackScroll_.style.height = window_.timelineTracksHeight;
            Add (trackScroll_);

            tracksCanvas_ = new VisualElement { name = "MktTimelineTracks" };
            tracksCanvas_.generateVisualContent += OnGenerateTracks;
            trackScroll_.Add (tracksCanvas_);

            noClipLabel_ = new Label ("クリップを開くとキーが出ます");
            noClipLabel_.style.position = Position.Absolute;
            noClipLabel_.style.left = kLabelWidth + 8;
            noClipLabel_.style.top = 4;
            noClipLabel_.style.fontSize = 10;
            noClipLabel_.style.color = kLabelTextColor;
            noClipLabel_.pickingMode = PickingMode.Ignore;
            tracksCanvas_.Add (noClipLabel_);

            playhead_ = new VisualElement { name = "MktTimelinePlayhead", pickingMode = PickingMode.Ignore };
            playhead_.style.position = Position.Absolute;
            playhead_.style.top = 0;
            playhead_.style.width = 1;
            playhead_.style.backgroundColor = kPlayheadColor;
            Add (playhead_);

            RegisterCallback<GeometryChangedEvent> (e => LayoutAll ());
            RegisterNavigation (rulerCanvas_, false);
            RegisterNavigation (tracksCanvas_, true);

            // Ctrl+C / Ctrl+V（Unity の Copy / Paste のコマンド）で選んだキーをコピー・貼り付け。押したトラック欄がフォーカスを持つ
            tracksCanvas_.focusable = true;
            tracksCanvas_.RegisterCallback<ValidateCommandEvent> (OnValidateCommand);
            tracksCanvas_.RegisterCallback<ExecuteCommandEvent> (OnExecuteCommand);
        }

        void OnValidateCommand (ValidateCommandEvent evt)
        {
            if ((evt.commandName == "Copy" && selectedKeys_.Count > 0) || (evt.commandName == "Paste" && window_.canPasteKeys)) {
                evt.StopPropagation ();
            }
        }

        void OnExecuteCommand (ExecuteCommandEvent evt)
        {
            if (evt.commandName == "Copy" && selectedKeys_.Count > 0) {
                CopySelectedKeys ();
                evt.StopPropagation ();
            }
            else if (evt.commandName == "Paste" && window_.canPasteKeys) {
                PasteKeys ();
                evt.StopPropagation ();
            }
        }

        /// <summary>選んでいるキーを覚える（Ctrl+C）</summary>
        void CopySelectedKeys ()
        {
            List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys = SelectedKeyList ();
            if (keys.Count == 0) return;
            int first = keys.Min (k => k.Key);
            copiedSelection_.Clear ();
            copiedSelection_.AddRange (selectedKeys_.Select (k => (k.Item1, k.Item2 - first)));
            int count = window_.CopyKeys (keys);
            window_.ShowNotification (new GUIContent ("キーをコピー（" + selectedKeys_.Count + " 個）"), 0.8);
            if (count == 0) copiedSelection_.Clear ();
        }

        /// <summary>覚えたキーを今のフレームを先頭にして貼り、貼ったキーを選ぶ（Ctrl+V）</summary>
        void PasteKeys ()
        {
            int frame = window_.frame;
            if (window_.PasteKeys () == 0) return;
            RebuildTracks ();
            selectedKeys_.Clear ();
            foreach ((string, int) key in copiedSelection_) {
                (string, int) pasted = (key.Item1, frame + key.Item2);
                if (tracks_.Any (t => t.label == pasted.Item1 && System.Array.IndexOf (t.keys, pasted.Item2) >= 0)) selectedKeys_.Add (pasted);
            }
            tracksCanvas_.MarkDirtyRepaint ();
        }

        /// <summary>
        /// トラック一覧の高さを変えるつまみ。置く場所はオーバーレイが決める。
        /// 下端にドッキングしたときは上へ伸びるので、上端に置いて上へ引くと高くなるようにする（fromTop）
        /// </summary>
        public VisualElement CreateResizeHandle (bool fromTop)
        {
            VisualElement handle = new VisualElement { tooltip = "ドラッグで高さを変更" };
            handle.style.height = kHandleHeight;
            handle.style.flexShrink = 0;
            if (fromTop) handle.style.marginBottom = 1;
            else handle.style.marginTop = 2;
            handle.style.backgroundColor = kHandleColor;
            handle.style.cursor = ResizeCursor ();

            float startPointerY = 0;
            float startHeight = 0;
            handle.RegisterCallback<PointerDownEvent> (e => {
                if (e.button != 0) return;
                startPointerY = e.position.y;
                startHeight = trackScroll_.resolvedStyle.height;
                handle.CapturePointer (e.pointerId);
                e.StopPropagation ();
            });
            handle.RegisterCallback<PointerMoveEvent> (e => {
                if (!handle.HasPointerCapture (e.pointerId)) return;
                float delta = e.position.y - startPointerY;
                // 窓からはみ出さない高さまで（ツール類と表示域の分を残す）
                float max = Mathf.Max (kMaxHeightMargin, window_.position.height - kMaxHeightMargin);
                window_.timelineTracksHeight = Mathf.Min (max, startHeight + (fromTop ? -delta : delta));
                SetTracksHeight (window_.timelineTracksHeight);
                e.StopPropagation ();
            });
            handle.RegisterCallback<PointerUpEvent> (e => {
                if (handle.HasPointerCapture (e.pointerId)) handle.ReleasePointer (e.pointerId);
            });
            return handle;
        }

        void SetTracksHeight (float height)
        {
            trackScroll_.style.height = height;
            tracksCanvas_.style.height = Mathf.Max (tracks_.Count * kTrackHeight, height);
            LayoutAll ();
        }

        /// <summary>
        /// 上下の矢印のカーソル。UI Toolkit はエディタの組み込みカーソルを公開していないので、内部の番号で指す
        /// </summary>
        static StyleCursor ResizeCursor ()
        {
            PropertyInfo id = typeof (UnityEngine.UIElements.Cursor).GetProperty ("defaultCursorId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (id == null) return new StyleCursor (StyleKeyword.Null);
            object cursor = new UnityEngine.UIElements.Cursor ();
            id.SetValue (cursor, (int)MouseCursor.ResizeVertical);
            return new StyleCursor ((UnityEngine.UIElements.Cursor)cursor);
        }

        // ---- トラックの作り直し ----

        /// <summary>
        /// クリップ・選択・段が替わったとき。トラックの一覧を窓から取り直す
        /// </summary>
        public void RebuildTracks ()
        {
            if (window_ == null) return;

            tracks_ = window_.BuildTracks ();
            allKeys_ = tracks_.Count > 0 ? tracks_[0].keys : new int[0];
            // 消えたキー・見えなくなったトラックの選択は捨てる
            selectedKeys_.RemoveWhere (k => !tracks_.Any (t => t.label == k.Item1 && System.Array.IndexOf (t.keys, k.Item2) >= 0));

            float viewHeight = trackScroll_.resolvedStyle.height;
            if (float.IsNaN (viewHeight)) viewHeight = window_.timelineTracksHeight;
            float contentHeight = tracks_.Count * kTrackHeight;
            tracksCanvas_.style.height = Mathf.Max (contentHeight, viewHeight);

            noClipLabel_.style.display = window_.targetClip == null ? DisplayStyle.Flex : DisplayStyle.None;
            LayoutAll ();
        }

        /// <summary>
        /// 0 からキーの最後までが収まるようにする
        /// </summary>
        public void FitTimeline ()
        {
            int last = Mathf.Max (window_.GetLastKeyFrame (), 10);
            float width = rulerCanvas_.resolvedStyle.width;
            if (float.IsNaN (width) || width <= 0) width = resolvedStyle.width;
            width = Mathf.Max (0, (float.IsNaN (width) ? 0 : width) - kLabelWidth - 24);
            window_.timelineFirstFrame = -1;
            window_.timelinePixelsPerFrame = Mathf.Clamp (width / (last + 3), kMinPixelsPerFrame, kMaxPixelsPerFrame);
            LayoutAll ();
        }

        /// <summary>
        /// 今のフレーム（再生・SetFrame・Undo）が替わったとき。再生中は画面外へ出たら送る
        /// </summary>
        public void RefreshPlayhead ()
        {
            if (rulerCanvas_ == null || window_ == null) return;

            if (window_.clock.isPlaying) {
                float rulerWidth = rulerCanvas_.resolvedStyle.width;
                if (!float.IsNaN (rulerWidth) && rulerWidth > 0) {
                    float x = FrameToX (window_.clock.frame);
                    if (x < kLabelWidth || x > kLabelWidth + rulerWidth - 16) {
                        window_.timelineFirstFrame = window_.frame - 2;
                        LayoutRuler ();
                        LayoutTracks ();
                    }
                }
            }

            playhead_.style.left = Mathf.Round (FrameToX (window_.clock.frame));
            float h = rulerCanvas_.resolvedStyle.height + trackScroll_.resolvedStyle.height;
            playhead_.style.height = float.IsNaN (h) ? 0 : h;

            rulerCanvas_.MarkDirtyRepaint ();
            tracksCanvas_.MarkDirtyRepaint ();
        }

        void LayoutAll ()
        {
            LayoutRuler ();
            LayoutTracks ();
            RefreshPlayhead ();
        }

        // ---- 座標変換 ----

        float FrameToX (float frame)
        {
            return kLabelWidth + (frame - window_.timelineFirstFrame) * window_.timelinePixelsPerFrame;
        }

        float XToFrame (float x)
        {
            return window_.timelineFirstFrame + (x - kLabelWidth) / window_.timelinePixelsPerFrame;
        }

        void RulerRange (float width, out int start, out int end, out int labelStep)
        {
            labelStep = kLabelSteps.FirstOrDefault (s => s * window_.timelinePixelsPerFrame >= kMinLabelSpacing);
            if (labelStep == 0) labelStep = kLabelSteps[kLabelSteps.Length - 1];
            start = Mathf.CeilToInt (window_.timelineFirstFrame);
            end = Mathf.FloorToInt (XToFrame (width));
        }

        static int KeyNumber (int[] allKeys, int frame)
        {
            int index = System.Array.BinarySearch (allKeys, frame);
            return index >= 0 ? index + 1 : 0;
        }

        Rect GetKeyRect (int frame, Rect row, int number)
        {
            float width = Mathf.Max (12, number.ToString ().Length * 6f + 5);
            float x = FrameToX (frame);
            return new Rect (Mathf.Round (x - width * 0.5f), row.y + (row.height - kKeyHeight) * 0.5f, width, kKeyHeight);
        }

        static float MeasureIntervalText (string text)
        {
            return text.Length * 6f + 2f;
        }

        // ---- 形（Painter2D）。毎回いまの状態から描き直す ----

        void OnGenerateRuler (MeshGenerationContext ctx)
        {
            Painter2D painter = ctx.painter2D;
            float width = rulerCanvas_.contentRect.width;
            if (float.IsNaN (width)) return;

            Rect ruler = new Rect (kLabelWidth, 0, Mathf.Max (0, width - kLabelWidth), kRulerHeight);
            FillRect (painter, ruler, kRulerColor);
            if (window_.timelineFirstFrame < 0) {
                FillRect (painter, Rect.MinMaxRect (ruler.x, ruler.y, Mathf.Min (FrameToX (0), ruler.xMax), ruler.yMax), kNegativeTimeColor);
            }
            DrawTimelineRange (painter, ruler);

            int start, end, labelStep;
            RulerRange (width, out start, out end, out labelStep);
            for (int f = start; f <= end; f++) {
                float x = Mathf.Round (FrameToX (f));
                if (f % labelStep == 0) {
                    FillRect (painter, new Rect (x, ruler.yMax - 6, 1, 6), kRulerTickColor);
                }
                else if (window_.timelinePixelsPerFrame >= 5) {
                    FillRect (painter, new Rect (x, ruler.yMax - 3, 1, 3), kRulerTickColor);
                }
            }
        }

        /// <summary>
        /// 演出のクリップと結び付けているとき、目盛りの上端に「演出で使われている区間」と「ブレンドの区間」を帯で出す（S15b）。
        /// スローの演出ではクリップのうち使われる区間が短い（0.1 倍なら演出 2 秒でクリップ 12F）ので、どこを直せば演出に効くかを見せる
        /// </summary>
        void DrawTimelineRange (Painter2D painter, Rect ruler)
        {
            float start, end, blendInEnd, blendOutStart;
            if (!window_.TryGetTimelineClipFrames (out start, out end, out blendInEnd, out blendOutStart)) return;
            System.Action<float, float, Color> band = (from, to, color) => {
                float x0 = Mathf.Max (ruler.x, FrameToX (from));
                float x1 = Mathf.Min (ruler.xMax, FrameToX (to));
                if (x1 - x0 < 1) x1 = x0 + 1;
                if (x0 >= ruler.xMax || x1 <= ruler.x) return;
                FillRect (painter, Rect.MinMaxRect (x0, ruler.y, x1, ruler.y + 3), color);
            };
            band (start, end, kTimelineRangeColor);
            if (blendInEnd > start) band (start, blendInEnd, kTimelineBlendColor);
            if (blendOutStart < end) band (blendOutStart, end, kTimelineBlendColor);
        }

        void OnGenerateTracks (MeshGenerationContext ctx)
        {
            Painter2D painter = ctx.painter2D;
            float width = tracksCanvas_.contentRect.width;
            float height = tracksCanvas_.contentRect.height;
            if (float.IsNaN (width) || float.IsNaN (height)) return;

            FillRect (painter, new Rect (0, 0, kLabelWidth, height), kLabelColor);

            for (int i = 0; i < tracks_.Count; i++) {
                PreviewWindow.TimelineTrack track = tracks_[i];
                Rect row = new Rect (0, i * kTrackHeight, width, kTrackHeight);
                Rect keyArea = Rect.MinMaxRect (kLabelWidth, row.y, row.xMax, row.yMax);

                FillRect (painter, keyArea, kTrackColor);
                FillRect (painter, new Rect (row.x, row.yMax - 1, row.width, 1), kTrackSeparatorColor);

                float midY = row.y + row.height * 0.5f;
                for (int k = 1; k < track.keys.Length; k++) {
                    Rect prev = GetKeyRect (track.keys[k - 1], row, KeyNumber (allKeys_, track.keys[k - 1]));
                    Rect next = GetKeyRect (track.keys[k], row, KeyNumber (allKeys_, track.keys[k]));
                    DrawInterval (painter, Mathf.Max (prev.xMax + 2, keyArea.x), Mathf.Min (next.x - 2, keyArea.xMax), midY);
                }

                foreach (int frame in track.keys) {
                    Rect box = GetKeyRect (frame, row, KeyNumber (allKeys_, frame));
                    if (box.xMax < keyArea.x || box.x > keyArea.xMax) continue;
                    bool isDragged = dragTrack_ == i && dragKeyFrame_ == frame && dragMoved_;
                    Color fill = selectedKeys_.Contains ((track.label, frame)) ? kSelectedKeyColor : frame == window_.frame ? kCurrentKeyColor : kKeyColor;
                    DrawKeyBox (painter, box, fill, isDragged ? 0.35f : 1);
                }
                // ドラッグ中のキーは行き先に描く（トラックの外へ出すと赤＝離すと消える）
                if (dragTrack_ == i && dragMoved_) {
                    Rect box = GetKeyRect (dragTargetFrame_, row, KeyNumber (allKeys_, dragKeyFrame_));
                    DrawKeyBox (painter, box, dragDelete_ ? kDeleteKeyColor : kDragKeyColor, 1);
                }
            }

            if (boxSelecting_ && boxMoved_) {
                Rect band = BoxRect ();
                FillRect (painter, band, kBoxFillColor);
                FillRect (painter, new Rect (band.x, band.y, band.width, 1), kBoxBorderColor);
                FillRect (painter, new Rect (band.x, band.yMax - 1, band.width, 1), kBoxBorderColor);
                FillRect (painter, new Rect (band.x, band.y, 1, band.height), kBoxBorderColor);
                FillRect (painter, new Rect (band.xMax - 1, band.y, 1, band.height), kBoxBorderColor);
            }
        }

        static void DrawKeyBox (Painter2D painter, Rect box, Color fill, float alpha)
        {
            Color border = kKeyBorderColor;
            border.a *= alpha;
            fill.a *= alpha;
            FillRect (painter, box, border);
            FillRect (painter, new Rect (box.x + 1, box.y + 1, box.width - 2, box.height - 2), fill);
        }

        /// <summary>
        /// キーの間の線と矢じり（数字は LayoutTracks が置く Label）
        /// </summary>
        static void DrawInterval (Painter2D painter, float x0, float x1, float y)
        {
            if (x1 - x0 < 8) return;
            y = Mathf.Round (y);
            FillRect (painter, new Rect (x0, y, x1 - x0, 1), kIntervalColor);
            for (int i = 0; i < 3; i++) {
                FillRect (painter, new Rect (x0 + i, y - i, 1, i * 2 + 1), kIntervalColor);
                FillRect (painter, new Rect (x1 - 1 - i, y - i, 1, i * 2 + 1), kIntervalColor);
            }
        }

        /// <summary>
        /// 矩形を塗る（Arc の向きに悩まないよう多角形にして塗る。SpinnerElement.FillWedge と同じ考え方）
        /// </summary>
        static void FillRect (Painter2D painter, Rect r, Color color)
        {
            if (color.a <= 0 || r.width <= 0 || r.height <= 0) return;
            painter.fillColor = color;
            painter.BeginPath ();
            painter.MoveTo (new Vector2 (r.xMin, r.yMin));
            painter.LineTo (new Vector2 (r.xMax, r.yMin));
            painter.LineTo (new Vector2 (r.xMax, r.yMax));
            painter.LineTo (new Vector2 (r.xMin, r.yMax));
            painter.ClosePath ();
            painter.Fill ();
        }

        // ---- 数字（使い回す Label を絶対座標で置く） ----

        void LayoutRuler ()
        {
            float width = rulerCanvas_.resolvedStyle.width;
            if (float.IsNaN (width) || width <= kLabelWidth) {
                HideUnused (rulerLabels_, 0);
                return;
            }

            int start, end, labelStep;
            RulerRange (width, out start, out end, out labelStep);

            int used = 0;
            for (int f = start; f <= end; f++) {
                if (f % labelStep != 0) continue;
                Label label = GetPooled (rulerLabels_, ref used, rulerCanvas_, StyleRulerLabel);
                label.text = f.ToString ();
                label.style.left = Mathf.Round (FrameToX (f)) - 20;
            }
            HideUnused (rulerLabels_, used);
        }

        void LayoutTracks ()
        {
            int usedNames = 0;
            int usedKeys = 0;
            int usedIntervals = 0;
            float width = tracksCanvas_.resolvedStyle.width;
            if (float.IsNaN (width)) width = 0;

            for (int i = 0; i < tracks_.Count; i++) {
                PreviewWindow.TimelineTrack track = tracks_[i];
                Rect row = new Rect (0, i * kTrackHeight, width, kTrackHeight);
                Rect keyArea = Rect.MinMaxRect (kLabelWidth, row.y, row.xMax, row.yMax);

                Label name = GetPooled (trackNameLabels_, ref usedNames, tracksCanvas_, StyleTrackLabel);
                name.text = track.label;
                name.style.top = row.y;

                for (int k = 1; k < track.keys.Length; k++) {
                    Rect prev = GetKeyRect (track.keys[k - 1], row, KeyNumber (allKeys_, track.keys[k - 1]));
                    Rect next = GetKeyRect (track.keys[k], row, KeyNumber (allKeys_, track.keys[k]));
                    float x0 = Mathf.Max (prev.xMax + 2, keyArea.x);
                    float x1 = Mathf.Min (next.x - 2, keyArea.xMax);
                    string text = (track.keys[k] - track.keys[k - 1]).ToString ();
                    float textWidth = MeasureIntervalText (text);
                    if (x1 - x0 <= textWidth + 24) continue;

                    Label interval = GetPooled (intervalLabels_, ref usedIntervals, tracksCanvas_, StyleIntervalLabel);
                    interval.text = text;
                    float mid = (x0 + x1) * 0.5f;
                    interval.style.left = mid - textWidth * 0.5f - 2;
                    interval.style.top = row.y + row.height * 0.5f - 7;
                    interval.style.width = textWidth + 4;
                }

                foreach (int frame in track.keys) {
                    Rect box = GetKeyRect (frame, row, KeyNumber (allKeys_, frame));
                    if (box.xMax < keyArea.x || box.x > keyArea.xMax) continue;
                    bool isDragged = dragTrack_ == i && dragKeyFrame_ == frame && dragMoved_;
                    if (isDragged) continue;
                    PlaceKeyLabel (box, KeyNumber (allKeys_, frame), ref usedKeys);
                }
                if (dragTrack_ == i && dragMoved_) {
                    Rect box = GetKeyRect (dragTargetFrame_, row, KeyNumber (allKeys_, dragKeyFrame_));
                    PlaceKeyLabel (box, KeyNumber (allKeys_, dragKeyFrame_), ref usedKeys);
                }
            }

            HideUnused (trackNameLabels_, usedNames);
            HideUnused (keyNumberLabels_, usedKeys);
            HideUnused (intervalLabels_, usedIntervals);
        }

        void PlaceKeyLabel (Rect box, int number, ref int used)
        {
            if (number <= 0) return;
            Label label = GetPooled (keyNumberLabels_, ref used, tracksCanvas_, StyleKeyLabel);
            label.text = number.ToString ();
            label.style.left = box.x;
            label.style.top = box.y;
            label.style.width = box.width;
            label.style.height = box.height;
        }

        static Label GetPooled (List<Label> pool, ref int used, VisualElement parent, System.Action<Label> setup)
        {
            Label label;
            if (used < pool.Count) {
                label = pool[used];
            }
            else {
                label = new Label ();
                label.style.position = Position.Absolute;
                label.pickingMode = PickingMode.Ignore;
                setup (label);
                parent.Add (label);
                pool.Add (label);
            }
            label.style.display = DisplayStyle.Flex;
            used++;
            return label;
        }

        static void HideUnused (List<Label> pool, int used)
        {
            for (int i = used; i < pool.Count; i++) pool[i].style.display = DisplayStyle.None;
        }

        static void StyleRulerLabel (Label label)
        {
            label.style.top = 1;
            label.style.width = 40;
            label.style.height = 12;
            label.style.fontSize = 9;
            label.style.unityTextAlign = TextAnchor.UpperCenter;
            label.style.color = kRulerTextColor;
        }

        static void StyleKeyLabel (Label label)
        {
            label.style.fontSize = 9;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.color = kKeyTextColor;
        }

        static void StyleIntervalLabel (Label label)
        {
            label.style.height = 14;
            label.style.fontSize = 9;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.color = kIntervalColor;
        }

        static void StyleTrackLabel (Label label)
        {
            label.style.left = 6;
            label.style.width = kLabelWidth - 10;
            label.style.height = kTrackHeight;
            label.style.fontSize = 10;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            label.style.color = kLabelTextColor;
        }

        // ---- 操作（横の拡大縮小・移動、スクラブ、キーのドラッグ） ----

        void RegisterNavigation (VisualElement canvas, bool isTracks)
        {
            canvas.RegisterCallback<PointerDownEvent> (e => OnCanvasPointerDown (e, canvas, isTracks));
            canvas.RegisterCallback<PointerMoveEvent> (e => OnCanvasPointerMove (e, canvas));
            canvas.RegisterCallback<PointerUpEvent> (e => OnCanvasPointerUp (e, canvas));
            canvas.RegisterCallback<WheelEvent> (e => OnWheel (e, canvas));
        }

        void OnCanvasPointerDown (PointerDownEvent evt, VisualElement canvas, bool isTracks)
        {
            float x = evt.localPosition.x;
            float y = evt.localPosition.y;
            // 左の名前欄はナビゲーションもスクラブも受けない
            if (x < kLabelWidth) return;

            if (evt.button == 2 || (evt.button == 0 && evt.altKey)) {
                panning_ = true;
                panPointerId_ = evt.pointerId;
                canvas.CapturePointer (evt.pointerId);
                evt.StopPropagation ();
                return;
            }

            if (isTracks && (evt.button == 0 || evt.button == 1)) {
                tracksCanvas_.Focus ();
                int trackIndex, frame;
                System.Predicate<EditorCurveBinding> filter;
                if (HitTestKey (x, y, out trackIndex, out frame, out filter)) {
                    (string, int) key = (tracks_[trackIndex].label, frame);
                    // Ctrl（Mac は Cmd）+ クリックは選択に足す・外す
                    if (evt.button == 0 && evt.actionKey) {
                        if (!selectedKeys_.Remove (key)) selectedKeys_.Add (key);
                        tracksCanvas_.MarkDirtyRepaint ();
                        evt.StopPropagation ();
                        return;
                    }
                    // 選んでいないキーを押したら、そのキーだけを選ぶ
                    if (!selectedKeys_.Contains (key)) {
                        selectedKeys_.Clear ();
                        selectedKeys_.Add (key);
                        tracksCanvas_.MarkDirtyRepaint ();
                    }
                    if (evt.button == 1) {
                        ShowKeyMenu (frame, filter);
                        evt.StopPropagation ();
                        return;
                    }
                    dragTrack_ = trackIndex;
                    dragKeyFrame_ = frame;
                    dragTargetFrame_ = frame;
                    dragStartX_ = x;
                    dragMoved_ = false;
                    dragDelete_ = false;
                    dragPointerId_ = evt.pointerId;
                    canvas.CapturePointer (evt.pointerId);
                    evt.StopPropagation ();
                    return;
                }

                // キーの無い所: 右は選んだキーのメニュー、左のドラッグは枠選択（フレームを動かすのは目盛りの帯）
                if (evt.button == 1) {
                    ShowSelectionMenu ();
                    evt.StopPropagation ();
                    return;
                }
                boxSelecting_ = true;
                boxMoved_ = false;
                boxPointerId_ = evt.pointerId;
                boxStart_ = new Vector2 (x, y);
                boxEnd_ = boxStart_;
                boxBase_.Clear ();
                // Shift / Ctrl を押しながらなら今の選択に足す
                if (evt.shiftKey || evt.actionKey) boxBase_.UnionWith (selectedKeys_);
                canvas.CapturePointer (evt.pointerId);
                evt.StopPropagation ();
                return;
            }

            if (evt.button == 0 && !evt.altKey) {
                scrubbing_ = true;
                scrubPointerId_ = evt.pointerId;
                canvas.CapturePointer (evt.pointerId);
                window_.SetFrame (Mathf.RoundToInt (XToFrame (x)));
                evt.StopPropagation ();
            }
        }

        void OnCanvasPointerMove (PointerMoveEvent evt, VisualElement canvas)
        {
            if (panning_ && canvas.HasPointerCapture (panPointerId_)) {
                window_.timelineFirstFrame -= evt.deltaPosition.x / window_.timelinePixelsPerFrame;
                LayoutAll ();
                return;
            }
            if (boxSelecting_ && canvas.HasPointerCapture (boxPointerId_)) {
                boxEnd_ = evt.localPosition;
                boxMoved_ = boxMoved_ || Vector2.Distance (boxStart_, boxEnd_) > kBoxDragThreshold;
                if (boxMoved_) SelectInBox ();
                tracksCanvas_.MarkDirtyRepaint ();
                return;
            }
            if (dragTrack_ >= 0 && canvas.HasPointerCapture (dragPointerId_)) {
                float x = evt.localPosition.x;
                dragTargetFrame_ = dragKeyFrame_ + Mathf.RoundToInt ((x - dragStartX_) / window_.timelinePixelsPerFrame);
                dragMoved_ = dragMoved_ || Mathf.Abs (x - dragStartX_) > 2;
                // トラックの帯から縦に外れたら消す
                float rowY = dragTrack_ * kTrackHeight;
                dragDelete_ = evt.localPosition.y < rowY - kTrackHeight || evt.localPosition.y > rowY + kTrackHeight * 2;
                dragMoved_ = dragMoved_ || dragDelete_;
                LayoutTracks ();
                tracksCanvas_.MarkDirtyRepaint ();
                return;
            }
            if (scrubbing_ && canvas.HasPointerCapture (scrubPointerId_)) {
                int frame = Mathf.RoundToInt (XToFrame (evt.localPosition.x));
                if (frame != window_.frame) window_.SetFrame (frame);
            }
        }

        void OnCanvasPointerUp (PointerUpEvent evt, VisualElement canvas)
        {
            if (panning_ && canvas.HasPointerCapture (panPointerId_)) {
                panning_ = false;
                canvas.ReleasePointer (panPointerId_);
                return;
            }
            if (boxSelecting_ && canvas.HasPointerCapture (boxPointerId_)) {
                boxSelecting_ = false;
                canvas.ReleasePointer (boxPointerId_);
                // 動かさずに離したらクリック: 選択を外して、そのフレームへ移る
                if (!boxMoved_) {
                    selectedKeys_.Clear ();
                    selectedKeys_.UnionWith (boxBase_);
                    window_.SetFrame (Mathf.RoundToInt (XToFrame (boxStart_.x)));
                }
                tracksCanvas_.MarkDirtyRepaint ();
                return;
            }
            if (dragTrack_ >= 0 && canvas.HasPointerCapture (dragPointerId_)) {
                System.Predicate<EditorCurveBinding> filter = tracks_[dragTrack_].filter;
                string dragLabel = tracks_[dragTrack_].label;
                bool keysChanged = false;
                if (dragDelete_) {
                    window_.RemoveKeys (dragKeyFrame_, filter);
                    keysChanged = true;
                }
                else if (dragMoved_ && dragTargetFrame_ != dragKeyFrame_ && dragTargetFrame_ >= 0) {
                    // 選択は動かした先のキーへ
                    if (selectedKeys_.Remove ((dragLabel, dragKeyFrame_))) selectedKeys_.Add ((dragLabel, dragTargetFrame_));
                    window_.MoveKeys (dragKeyFrame_, dragTargetFrame_, evt.shiftKey, filter);
                    window_.SetFrame (dragTargetFrame_);
                    keysChanged = true;
                }
                else if (!dragMoved_) {
                    window_.SetFrame (dragKeyFrame_);
                }
                dragTrack_ = -1;
                canvas.ReleasePointer (dragPointerId_);
                if (keysChanged) RebuildTracks ();
                else LayoutAll ();
                return;
            }
            if (scrubbing_ && canvas.HasPointerCapture (scrubPointerId_)) {
                scrubbing_ = false;
                canvas.ReleasePointer (scrubPointerId_);
            }
        }

        void OnWheel (WheelEvent evt, VisualElement canvas)
        {
            float x = evt.localMousePosition.x;
            if (x < kLabelWidth) return;

            float frame = XToFrame (x);
            window_.timelinePixelsPerFrame = Mathf.Clamp (window_.timelinePixelsPerFrame * Mathf.Pow (1.1f, -evt.delta.y), kMinPixelsPerFrame, kMaxPixelsPerFrame);
            window_.timelineFirstFrame = frame - (x - kLabelWidth) / window_.timelinePixelsPerFrame;
            LayoutAll ();
            evt.StopPropagation ();
        }

        /// <summary>
        /// 押した場所（トラック領域のローカル座標）がどれかのキーに当たっているか
        /// </summary>
        bool HitTestKey (float x, float y, out int trackIndex, out int frame, out System.Predicate<EditorCurveBinding> filter)
        {
            trackIndex = -1;
            frame = 0;
            filter = null;

            int row = Mathf.FloorToInt (y / kTrackHeight);
            if (row < 0 || row >= tracks_.Count) return false;

            PreviewWindow.TimelineTrack track = tracks_[row];
            Rect rowRect = new Rect (0, row * kTrackHeight, 0, kTrackHeight);
            foreach (int f in track.keys) {
                Rect box = GetKeyRect (f, rowRect, KeyNumber (allKeys_, f));
                if (box.Contains (new Vector2 (x, y))) {
                    trackIndex = row;
                    frame = f;
                    filter = track.filter;
                    return true;
                }
            }
            return false;
        }

        Rect BoxRect ()
        {
            return Rect.MinMaxRect (Mathf.Min (boxStart_.x, boxEnd_.x), Mathf.Min (boxStart_.y, boxEnd_.y),
                Mathf.Max (boxStart_.x, boxEnd_.x), Mathf.Max (boxStart_.y, boxEnd_.y));
        }

        /// <summary>
        /// 枠に掛かったキーを選ぶ（ドラッグ前の選択 boxBase_ に足す）
        /// </summary>
        void SelectInBox ()
        {
            Rect band = BoxRect ();
            selectedKeys_.Clear ();
            selectedKeys_.UnionWith (boxBase_);
            for (int i = 0; i < tracks_.Count; i++) {
                Rect row = new Rect (0, i * kTrackHeight, 0, kTrackHeight);
                if (row.yMax < band.yMin || row.yMin > band.yMax) continue;
                foreach (int frame in tracks_[i].keys) {
                    if (GetKeyRect (frame, row, KeyNumber (allKeys_, frame)).Overlaps (band)) selectedKeys_.Add ((tracks_[i].label, frame));
                }
            }
        }

        /// <summary>
        /// 選んでいるキーを消す。クリップ全体のトラックならそのフレームの全カーブ、骨のトラックならその骨のカーブ
        /// </summary>
        void DeleteSelectedKeys ()
        {
            List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys = SelectedKeyList ();
            selectedKeys_.Clear ();
            window_.RemoveKeys (keys);
            RebuildTracks ();
        }

        /// <summary>
        /// 選んでいるキーを「フレームとトラックのカーブの絞り込み」で。クリップ全体のトラックならそのフレームの全カーブ、骨のトラックならその骨のカーブ
        /// </summary>
        List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> SelectedKeyList ()
        {
            List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys = new List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> ();
            foreach (PreviewWindow.TimelineTrack track in tracks_) {
                foreach (int frame in track.keys) {
                    if (selectedKeys_.Contains ((track.label, frame))) keys.Add (new KeyValuePair<int, System.Predicate<EditorCurveBinding>> (frame, track.filter));
                }
            }
            return keys;
        }

        void AddDeleteSelectedItem (GenericMenu menu)
        {
            GUIContent copy = new GUIContent ("選んだキーをコピー（" + selectedKeys_.Count + " 個）  Ctrl+C");
            if (selectedKeys_.Count > 0) menu.AddItem (copy, false, CopySelectedKeys);
            else menu.AddDisabledItem (copy);
            GUIContent paste = new GUIContent ("キーを " + window_.frame + "F に貼る  Ctrl+V");
            if (window_.canPasteKeys) menu.AddItem (paste, false, PasteKeys);
            else menu.AddDisabledItem (paste);
            menu.AddSeparator ("");
            GUIContent item = new GUIContent ("選んだキーを削除（" + selectedKeys_.Count + " 個）");
            if (selectedKeys_.Count > 0) menu.AddItem (item, false, DeleteSelectedKeys);
            else menu.AddDisabledItem (item);
        }

        void ShowSelectionMenu ()
        {
            GenericMenu menu = new GenericMenu ();
            AddDeleteSelectedItem (menu);
            menu.ShowAsContext ();
        }

        void ShowKeyMenu (int frame, System.Predicate<EditorCurveBinding> filter)
        {
            GenericMenu menu = new GenericMenu ();
            AddDeleteSelectedItem (menu);
            menu.AddSeparator ("");
            menu.AddItem (new GUIContent ("Go to Frame " + frame), false, () => window_.SetFrame (frame));
            menu.AddItem (new GUIContent ("Delete Key"), false, () => {
                window_.RemoveKeys (frame, filter);
                RebuildTracks ();
            });
            // 選んでいる骨のキーをまとめて減らす（読込で毎フレームに入ったキーを、指だけ打ち直すときなど）
            menu.AddSeparator ("");
            int selected = window_.selectedTargetCount;
            GUIContent keepOne = new GUIContent ("選んでいる骨のキーを " + frame + "F の 1 つだけにする（" + selected + " 個）");
            GUIContent clearAll = new GUIContent ("選んでいる骨のキーを全部消す（" + selected + " 個）");
            if (selected > 0) {
                menu.AddItem (keepOne, false, () => {
                    window_.ClearSelectedKeys (frame);
                    RebuildTracks ();
                });
                menu.AddItem (clearAll, false, () => {
                    window_.ClearSelectedKeys (-1);
                    RebuildTracks ();
                });
            }
            else {
                menu.AddDisabledItem (keepOne);
                menu.AddDisabledItem (clearAll);
            }
            menu.ShowAsContext ();
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Timeline.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-timeline", "Timeline", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom,
        defaultLayout = Layout.HorizontalToolbar)]
    sealed class TimelineOverlayScene : TimelineOverlay
    {
    }

}
