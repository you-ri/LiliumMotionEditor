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
    /// 目盛り（ルーラー）とトラック（クリップ全体＋骨ごと）。見た目:
    /// 黒い目盛り・キーのトラック。キーは箱（今のフレームのキーは白、サイクルキーは青緑に Material Icons の loop のアイコン・目盛りに三角、
    /// 全身 IK の点のトラックはそのフレームの点の状態の色＝丸の色）、キーの間は両矢印の線とフレーム数
    ///（全身 IK の点のトラックは、その間の点の状態＝前のキーの状態の色）。
    /// 今のフレームは白い縦線。目盛りとトラックの上でホイールは横の拡大縮小、中ボタン（Alt+左）ドラッグは横の移動。
    ///
    /// 形は Painter2D（generateVisualContent）で毎回いまの状態から描き直し、数字は使い回す Label を絶対座標で置く
    /// </summary>
    sealed class TimebarElement : VisualElement
    {
        const float kRulerHeight = 20;
        const float kTrackHeight = 20;
        const float kLabelWidth = 120;
        const float kKeyWidth = 12;
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
        /// <summary>演出の中でこのクリップが使われている区間（S15b）の不透明度。色はアクセントの色</summary>
        const float kTimelineRangeAlpha = 0.9f;
        /// <summary>その中で前後のクリップと混ざる区間（キーは打たない）</summary>
        static readonly Color kTimelineBlendColor = new Color (1f, 0.6f, 0.2f, 0.9f);
        static readonly Color kLabelColor = new Color (0.13f, 0.13f, 0.14f);
        static readonly Color kLabelTextColor = new Color (0.82f, 0.82f, 0.82f);
        static readonly Color kTrackColor = new Color (0.45f, 0.45f, 0.45f);
        static readonly Color kTrackSeparatorColor = new Color (0.3f, 0.3f, 0.3f);
        static readonly Color kKeyColor = new Color (0.73f, 0.73f, 0.73f);
        static readonly Color kCurrentKeyColor = new Color (0.97f, 0.97f, 0.97f);
        static readonly Color kKeyBorderColor = new Color (0.2f, 0.2f, 0.2f);
        static readonly Color kIntervalColor = new Color (0.86f, 0.86f, 0.86f);
        static readonly Color kPlayheadColor = Color.white;
        static readonly Color kDragKeyColor = new Color (1f, 0.72f, 0.25f);
        static readonly Color kDeleteKeyColor = new Color (0.95f, 0.3f, 0.25f);
        /// <summary>サイクルキー（S32。先頭のキーを写した最後のキー）。キーの上に Material Icons の loop のアイコンも重ねる（選んで色が変わっても分かるように）</summary>
        static readonly Color kCycleKeyColor = new Color (0.4f, 0.78f, 0.72f);
        static readonly Color kCurrentCycleKeyColor = new Color (0.72f, 0.95f, 0.9f);
        static readonly Color kCycleMarkColor = new Color (0.1f, 0.25f, 0.23f);
        // 外部の動きの区間の波線（S82）
        const float kWaveStep = 3;
        const float kWaveHeight = 2;
        /// <summary>外で作っている区間（S85）の波線が 1 山ぶん流れる秒数と、描き直す間（ミリ秒）</summary>
        const double kWaveFlowSeconds = 0.6;
        const long kWaveFrameMs = 50;
        // 混在の区間の点線（S80）
        const float kDashLength = 3;
        const float kDashGap = 2;
        /// <summary>全身 IK の点のキー（点の状態の色）で、今のフレームのキーを白へ寄せる割合</summary>
        const float kCurrentStateKeyLighten = 0.5f;
        static readonly Color kHandleColor = new Color (0, 0, 0, 0.15f);
        // 選んだキー・枠選択の枠の不透明度。色はアクセントの色
        const float kBoxFillAlpha = 0.15f;
        /// <summary>選んだキーの間の矢印の帯の不透明度（S86。枠より少し濃くして、枠の中でも分かるように）</summary>
        const float kIntervalFillAlpha = 0.3f;
        const float kBoxBorderAlpha = 0.8f;
        /// <summary>これより動かなければ枠選択ではなくクリック（フレームを移すだけ）</summary>
        const float kBoxDragThreshold = 3;

        readonly PreviewWindow window_;
        /// <summary>アクセントの色（Unity のテーマの選択の色。AccentStyle から受け取る）</summary>
        Color accent_ = AccentStyle.kFallbackColor;
        readonly VisualElement rulerCanvas_;
        readonly ScrollView trackScroll_;
        readonly VisualElement tracksCanvas_;
        readonly VisualElement playhead_;
        readonly Label noClipLabel_;

        List<PreviewWindow.TimelineTrack> tracks_ = new List<PreviewWindow.TimelineTrack> ();

        readonly List<Label> rulerLabels_ = new List<Label> ();
        readonly List<Label> trackNameLabels_ = new List<Label> ();
        readonly List<Label> intervalLabels_ = new List<Label> ();
        /// <summary>サイクルキーのアイコン（Label と同じく使い回し、キーの上に絶対座標で置く）</summary>
        readonly List<Image> cycleIcons_ = new List<Image> ();
        const float kCycleIconSize = 10;
        const float kDraggedIconAlpha = 0.35f;

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
        // 選んでいるキーの間の矢印（S86。トラック名と、前のキーのフレーム）。キーの選択と同じく名前で持つ
        readonly HashSet<(string, int)> selectedIntervals_ = new HashSet<(string, int)> ();
        readonly HashSet<(string, int)> boxBaseIntervals_ = new HashSet<(string, int)> ();
        // 枠選択を始めた所の矢印（動かさずに離したら、その矢印を押したことにする）と、Shift + クリックの起点
        (string, int)? pressedInterval_;
        (string, int)? lastInterval_;
        bool boxShift_;
        bool boxAction_;
        // Ctrl+C で覚えたキーの選択（トラック名と、一番前のフレームからのずれ）。貼った後に貼ったキーを選ぶのに使う
        readonly List<(string, int)> copiedSelection_ = new List<(string, int)> ();
        readonly IVisualElementScheduledItem waveTicker_;

        public TimebarElement (PreviewWindow window)
        {
            window_ = window;
            style.flexDirection = FlexDirection.Column;
            style.flexShrink = 0;
            AccentStyle.Track (this, color => {
                accent_ = color;
                rulerCanvas_.MarkDirtyRepaint ();
                tracksCanvas_.MarkDirtyRepaint ();
            });

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
            // 外で区間を作っている間だけ、波線を流すために描き直し続ける（S85）
            waveTicker_ = tracksCanvas_.schedule.Execute (() => tracksCanvas_.MarkDirtyRepaint ()).Every (kWaveFrameMs);
            waveTicker_.Pause ();

            noClipLabel_ = new Label (Tr ("TIMELINE_OVERLAY_NO_CLIP"));
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
            window_.ShowNotification (new GUIContent (Tr ("TIMELINE_OVERLAY_KEYS_COPIED", selectedKeys_.Count)), 0.8);
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
            VisualElement handle = new VisualElement { tooltip = Tr ("TIMELINE_OVERLAY_RESIZE_HANDLE_TOOLTIP") };
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
            // 消えたキー・見えなくなったトラックの選択は捨てる。矢印は前のキーが残っていても最後のキーになったら捨てる
            selectedKeys_.RemoveWhere (k => !tracks_.Any (t => t.label == k.Item1 && System.Array.IndexOf (t.keys, k.Item2) >= 0));
            selectedIntervals_.RemoveWhere (k => !tracks_.Any (t => t.label == k.Item1 && System.Array.IndexOf (t.keys, k.Item2) >= 0 && System.Array.IndexOf (t.keys, k.Item2) + 1 < t.keys.Length));

            float viewHeight = trackScroll_.resolvedStyle.height;
            if (float.IsNaN (viewHeight)) viewHeight = window_.timelineTracksHeight;
            float contentHeight = tracks_.Count * kTrackHeight;
            tracksCanvas_.style.height = Mathf.Max (contentHeight, viewHeight);

            noClipLabel_.style.display = window_.targetClip == null ? DisplayStyle.Flex : DisplayStyle.None;
            if (window_.betweenInProgress.Count > 0) waveTicker_.Resume ();
            else waveTicker_.Pause ();
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

        Rect GetKeyRect (int frame, Rect row)
        {
            float x = FrameToX (frame);
            return new Rect (Mathf.Round (x - kKeyWidth * 0.5f), row.y + (row.height - kKeyHeight) * 0.5f, kKeyWidth, kKeyHeight);
        }

        static float MeasureIntervalText (string text)
        {
            return text.Length * 6f + 2f;
        }

        /// <summary>
        /// キーの間（x0〜x1）に出すフレーム数の幅。間が狭くて出さないときは 0（線を切らずに引く）
        /// </summary>
        static float IntervalTextWidth (int frames, float x0, float x1)
        {
            float width = MeasureIntervalText (frames.ToString ());
            return x1 - x0 > width + 24 ? width : 0;
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
            DrawCycleMarker (painter, ruler);

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
            band (start, end, WithAlpha (accent_, kTimelineRangeAlpha));
            if (blendInEnd > start) band (start, blendInEnd, kTimelineBlendColor);
            if (blendOutStart < end) band (blendOutStart, end, kTimelineBlendColor);
        }

        /// <summary>
        /// サイクルキーのフレームに、目盛りの上から下向きの三角を出す（S32）
        /// </summary>
        void DrawCycleMarker (Painter2D painter, Rect ruler)
        {
            int frame = window_.cycleFrame;
            if (frame < 0) return;
            float x = Mathf.Round (FrameToX (frame)) + 0.5f;
            if (x < ruler.x || x > ruler.xMax) return;
            painter.fillColor = kCycleKeyColor;
            painter.BeginPath ();
            painter.MoveTo (new Vector2 (x - 5, ruler.y + 4));
            painter.LineTo (new Vector2 (x + 5, ruler.y + 4));
            painter.LineTo (new Vector2 (x, ruler.y + 10));
            painter.ClosePath ();
            painter.Fill ();
        }

        void OnGenerateTracks (MeshGenerationContext ctx)
        {
            Painter2D painter = ctx.painter2D;
            float width = tracksCanvas_.contentRect.width;
            float height = tracksCanvas_.contentRect.height;
            if (float.IsNaN (width) || float.IsNaN (height)) return;

            FillRect (painter, new Rect (0, 0, kLabelWidth, height), kLabelColor);
            int cycleFrame = window_.cycleFrame;
            // 外で作っている区間の波線を流す位置（0〜1 で 1 山）
            float flow = window_.betweenInProgress.Count > 0 ? (float) (EditorApplication.timeSinceStartup % kWaveFlowSeconds / kWaveFlowSeconds) : 0;

            for (int i = 0; i < tracks_.Count; i++) {
                PreviewWindow.TimelineTrack track = tracks_[i];
                Rect row = new Rect (0, i * kTrackHeight, width, kTrackHeight);
                Rect keyArea = Rect.MinMaxRect (kLabelWidth, row.y, row.xMax, row.yMax);

                FillRect (painter, keyArea, kTrackColor);
                FillRect (painter, new Rect (row.x, row.yMax - 1, row.width, 1), kTrackSeparatorColor);

                float midY = row.y + row.height * 0.5f;
                for (int k = 1; k < track.keys.Length; k++) {
                    Rect prev = GetKeyRect (track.keys[k - 1], row);
                    Rect next = GetKeyRect (track.keys[k], row);
                    float x0 = Mathf.Max (prev.xMax + 2, keyArea.x);
                    float x1 = Mathf.Min (next.x - 2, keyArea.xMax);
                    KeyInterpolation? mode = IntervalMode (track, k);
                    BetweenClips.State between = track.between != null && k - 1 < track.between.Length ? track.between[k - 1] : BetweenClips.State.None;
                    float textWidth = IntervalTextWidth (track.keys[k] - track.keys[k - 1], x0, x1);
                    // 選んだ矢印（S86）は帯を薄く塗る
                    if (x1 > x0 && selectedIntervals_.Contains ((track.label, track.keys[k - 1]))) {
                        FillRect (painter, Rect.MinMaxRect (x0, row.y + 2, x1, row.yMax - 3), WithAlpha (accent_, kIntervalFillAlpha));
                    }
                    // 外で作っている区間は、今の補間が何であっても流れる波線（できると外部の動きになる）
                    if (window_.IsBetweenInProgress (track.keys[k - 1], track.keys[k])) DrawExternalInterval (painter, x0, x1, midY, textWidth, IntervalColor (track, k), false, flow);
                    else if (between != BetweenClips.State.None) DrawExternalInterval (painter, x0, x1, midY, textWidth, IntervalColor (track, k), between != BetweenClips.State.Fresh);
                    else DrawInterval (painter, x0, x1, midY, textWidth, IntervalColor (track, k), mode);
                }

                for (int k = 0; k < track.keys.Length; k++) {
                    int frame = track.keys[k];
                    Rect box = GetKeyRect (frame, row);
                    if (box.xMax < keyArea.x || box.x > keyArea.xMax) continue;
                    bool isDragged = dragTrack_ == i && dragKeyFrame_ == frame && dragMoved_;
                    bool isCycle = frame == cycleFrame;
                    bool isCurrent = frame == window_.frame;
                    // 全身 IK の点のキーは点の状態の色（サイクルキーでもこちら。サイクルキーはアイコンで分かる）
                    Color fill = selectedKeys_.Contains ((track.label, frame)) ? accent_
                        : track.keyColors != null ? (isCurrent ? Color.Lerp (track.keyColors[k], Color.white, kCurrentStateKeyLighten) : track.keyColors[k])
                        : isCycle ? (isCurrent ? kCurrentCycleKeyColor : kCycleKeyColor)
                        : isCurrent ? kCurrentKeyColor : kKeyColor;
                    DrawKeyBox (painter, box, fill, isDragged ? 0.35f : 1);
                }
                // ドラッグ中のキーは行き先に描く（トラックの外へ出すと赤＝離すと消える）
                if (dragTrack_ == i && dragMoved_) {
                    Rect box = GetKeyRect (dragTargetFrame_, row);
                    DrawKeyBox (painter, box, dragDelete_ ? kDeleteKeyColor : kDragKeyColor, 1);
                }
            }

            if (boxSelecting_ && boxMoved_) {
                Rect band = BoxRect ();
                Color border = WithAlpha (accent_, kBoxBorderAlpha);
                FillRect (painter, band, WithAlpha (accent_, kBoxFillAlpha));
                FillRect (painter, new Rect (band.x, band.y, band.width, 1), border);
                FillRect (painter, new Rect (band.x, band.yMax - 1, band.width, 1), border);
                FillRect (painter, new Rect (band.x, band.y, 1, band.height), border);
                FillRect (painter, new Rect (band.xMax - 1, band.y, 1, band.height), border);
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
        /// キーの間の線と矢じり（数字は LayoutTracks が置く Label）。形は次のキーまでの補間で変える（S80）:
        /// 曲線は細い線と両端の矢じり、リニアは太い線と矢じり、一定は前のキーの高さで止まって次のキーで段になる形、混在は点線
        /// </summary>
        /// <param name="textWidth">真ん中に置くフレーム数の幅。線はそこを空けて引く（数字に線が重なって読みにくい）</param>
        static void DrawInterval (Painter2D painter, float x0, float x1, float y, float textWidth, Color color, KeyInterpolation? mode)
        {
            if (x1 - x0 < 8) return;
            y = Mathf.Round (y);
            float mid = (x0 + x1) * 0.5f;
            float gap = textWidth > 0 ? textWidth * 0.5f + 4 : 0;
            System.Action<float, float, float, float> line = (from, to, top, height) => {
                if (gap <= 0) {
                    DrawLine (painter, from, to, top, height, color, mode == null);
                    return;
                }
                DrawLine (painter, from, Mathf.Min (to, mid - gap), top, height, color, mode == null);
                DrawLine (painter, Mathf.Max (from, mid + gap), to, top, height, color, mode == null);
            };
            if (mode == KeyInterpolation.Constant) {
                // 前のキーの値のまま進み、次のキーの手前で段になる
                float top = y - 3;
                FillRect (painter, new Rect (x0, top - 1, 1, 4), color);
                line (x0, x1 - 1, top, 1);
                FillRect (painter, new Rect (x1 - 1, top, 1, 7), color);
                return;
            }
            line (x0, x1, mode == KeyInterpolation.Linear ? y - 1 : y, mode == KeyInterpolation.Linear ? 3 : 1);
            for (int i = 0; i < 3; i++) {
                FillRect (painter, new Rect (x0 + i, y - i, 1, i * 2 + 1), color);
                FillRect (painter, new Rect (x1 - 1 - i, y - i, 1, i * 2 + 1), color);
            }
        }

        /// <summary>
        /// 外部の動きの区間（S82）の矢印: 波線と両端の矢じり。古い（キーの値が変わった）・使えなくなった（キーのフレームが変わった）区間は点線の波線。
        /// flow（0〜1）だけ波を右へずらす（S85。外で作っている区間は時間で増やして流す）。端で切れる山は途中まで描く
        /// </summary>
        void DrawExternalInterval (Painter2D painter, float x0, float x1, float y, float textWidth, Color color, bool dashed, float flow = 0)
        {
            if (x1 - x0 < 8) return;
            y = Mathf.Round (y) + 0.5f;
            float mid = (x0 + x1) * 0.5f;
            float gap = textWidth > 0 ? textWidth * 0.5f + 4 : 0;
            float left = x0 + 2;
            float right = x1 - 2;
            painter.strokeColor = color;
            painter.lineWidth = 1;
            // 2 山前から始めて左端で切る（ずらしても左端に隙間ができない。2 山なので山と谷の並びは flow 0 のときと同じ）
            int step = 0;
            for (float x = left - 2 * kWaveStep + flow * 2 * kWaveStep; x < right; x += kWaveStep, step++) {
                float a = Mathf.Max (x, left);
                float b = Mathf.Min (x + kWaveStep, right);
                if (b - a < 0.5f) continue;
                if (gap > 0 && b > mid - gap && a < mid + gap) continue;
                if (dashed && step % 3 == 1) continue;
                float from = step % 2 == 0 ? -kWaveHeight : kWaveHeight;
                painter.BeginPath ();
                painter.MoveTo (new Vector2 (a, y + Mathf.Lerp (from, -from, (a - x) / kWaveStep)));
                painter.LineTo (new Vector2 (b, y + Mathf.Lerp (from, -from, (b - x) / kWaveStep)));
                painter.Stroke ();
            }
            for (int i = 0; i < 3; i++) {
                FillRect (painter, new Rect (x0 + i, y - 0.5f - i, 1, i * 2 + 1), color);
                FillRect (painter, new Rect (x1 - 1 - i, y - 0.5f - i, 1, i * 2 + 1), color);
            }
        }

        /// <summary>横の線（dashed なら点線）</summary>
        static void DrawLine (Painter2D painter, float from, float to, float top, float height, Color color, bool dashed)
        {
            if (to - from < 1) return;
            if (!dashed) {
                FillRect (painter, Rect.MinMaxRect (from, top, to, top + height), color);
                return;
            }
            for (float x = from; x < to; x += kDashLength + kDashGap) FillRect (painter, Rect.MinMaxRect (x, top, Mathf.Min (x + kDashLength, to), top + height), color);
        }

        /// <summary>キー k-1 と k の間の補間（S80。混在なら null。分からなければ曲線）</summary>
        static KeyInterpolation? IntervalMode (PreviewWindow.TimelineTrack track, int k)
        {
            return track.interpolations != null && k - 1 < track.interpolations.Length ? track.interpolations[k - 1] : KeyInterpolation.Curve;
        }

        /// <summary>
        /// キー k-1 と k の間の線と数字の色。全身 IK の点のトラックは前のキーの状態の色
        /// （点の状態は次のキーまで引き継ぎ、間で補間しないので、間はずっと前のキーの状態）
        /// </summary>
        static Color IntervalColor (PreviewWindow.TimelineTrack track, int k)
        {
            return track.keyColors != null ? track.keyColors[k - 1] : kIntervalColor;
        }

        static Color WithAlpha (Color color, float alpha)
        {
            color.a = alpha;
            return color;
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
            int usedIntervals = 0;
            int usedIcons = 0;
            float width = tracksCanvas_.resolvedStyle.width;
            if (float.IsNaN (width)) width = 0;
            int cycleFrame = window_.cycleFrame;

            for (int i = 0; i < tracks_.Count; i++) {
                PreviewWindow.TimelineTrack track = tracks_[i];
                Rect row = new Rect (0, i * kTrackHeight, width, kTrackHeight);
                Rect keyArea = Rect.MinMaxRect (kLabelWidth, row.y, row.xMax, row.yMax);

                Label name = GetPooled (trackNameLabels_, ref usedNames, tracksCanvas_, StyleTrackLabel);
                name.text = track.label;
                name.style.top = row.y;

                for (int k = 1; k < track.keys.Length; k++) {
                    Rect prev = GetKeyRect (track.keys[k - 1], row);
                    Rect next = GetKeyRect (track.keys[k], row);
                    float x0 = Mathf.Max (prev.xMax + 2, keyArea.x);
                    float x1 = Mathf.Min (next.x - 2, keyArea.xMax);
                    int frames = track.keys[k] - track.keys[k - 1];
                    float textWidth = IntervalTextWidth (frames, x0, x1);
                    if (textWidth <= 0) continue;

                    Label interval = GetPooled (intervalLabels_, ref usedIntervals, tracksCanvas_, StyleIntervalLabel);
                    interval.text = frames.ToString ();
                    interval.style.color = IntervalColor (track, k);
                    float mid = (x0 + x1) * 0.5f;
                    interval.style.left = mid - textWidth * 0.5f - 2;
                    interval.style.top = row.y + row.height * 0.5f - 7;
                    interval.style.width = textWidth + 4;
                }

                // サイクルキーのアイコン。ドラッグ中は元の場所を薄くし、行き先にも出す（消すときは出さない）
                if (cycleFrame >= 0 && System.Array.IndexOf (track.keys, cycleFrame) >= 0) {
                    bool dragged = dragTrack_ == i && dragKeyFrame_ == cycleFrame && dragMoved_;
                    PlaceCycleIcon (GetKeyRect (cycleFrame, row), keyArea, dragged ? kDraggedIconAlpha : 1, ref usedIcons);
                    if (dragged && !dragDelete_) PlaceCycleIcon (GetKeyRect (dragTargetFrame_, row), keyArea, 1, ref usedIcons);
                }
            }

            HideUnused (trackNameLabels_, usedNames);
            HideUnused (intervalLabels_, usedIntervals);
            for (int i = usedIcons; i < cycleIcons_.Count; i++) cycleIcons_[i].style.display = DisplayStyle.None;
        }

        /// <summary>キーの箱の真ん中にサイクルキーのアイコンを置く（キーが名前の欄や右端からはみ出す所には出さない）</summary>
        void PlaceCycleIcon (Rect box, Rect keyArea, float alpha, ref int used)
        {
            if (box.xMax < keyArea.x || box.x > keyArea.xMax) return;
            Image icon;
            if (used < cycleIcons_.Count) {
                icon = cycleIcons_[used];
            }
            else {
                icon = new Image { image = Icons.LoadTintable ("Cycle"), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                icon.style.position = Position.Absolute;
                icon.style.width = kCycleIconSize;
                icon.style.height = kCycleIconSize;
                tracksCanvas_.Add (icon);
                cycleIcons_.Add (icon);
            }
            used++;
            Color tint = kCycleMarkColor;
            tint.a *= alpha;
            icon.tintColor = tint;
            icon.style.left = Mathf.Round (box.center.x - kCycleIconSize * 0.5f);
            icon.style.top = Mathf.Round (box.center.y - kCycleIconSize * 0.5f);
            icon.style.display = DisplayStyle.Flex;
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
                if (HitTestKey (x, y, out trackIndex, out frame)) {
                    (string, int) key = (tracks_[trackIndex].label, frame);
                    // Ctrl（Mac は Cmd）+ クリックは選択に足す・外す
                    if (evt.button == 0 && evt.actionKey) {
                        if (!selectedKeys_.Remove (key)) selectedKeys_.Add (key);
                        tracksCanvas_.MarkDirtyRepaint ();
                        evt.StopPropagation ();
                        return;
                    }
                    // 選んでいないキーを押したら、そのキーだけを選ぶ（矢印の選択も外す）
                    if (!selectedKeys_.Contains (key)) {
                        selectedKeys_.Clear ();
                        selectedKeys_.Add (key);
                        selectedIntervals_.Clear ();
                        tracksCanvas_.MarkDirtyRepaint ();
                    }
                    if (evt.button == 1) {
                        ShowKeyMenu (frame);
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

                // キーの間の矢印（S86）: 右は選んだ矢印の補間のメニュー（選んでいなければその矢印だけを選ぶ）
                (string, int)? interval = HitTestInterval (x, y);
                if (evt.button == 1 && interval.HasValue) {
                    if (!selectedIntervals_.Contains (interval.Value)) {
                        selectedIntervals_.Clear ();
                        selectedIntervals_.Add (interval.Value);
                        selectedKeys_.Clear ();
                        lastInterval_ = interval;
                        tracksCanvas_.MarkDirtyRepaint ();
                    }
                    ShowIntervalMenu ();
                    evt.StopPropagation ();
                    return;
                }
                // キーの無い所: 右は選んだキーのメニュー、左のドラッグは枠選択（フレームを動かすのは目盛りの帯）。
                // 矢印の上で押して動かさずに離したら、その矢印を選ぶ（離すまで分からないので、押した矢印を覚えておく）
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
                pressedInterval_ = interval;
                boxShift_ = evt.shiftKey;
                boxAction_ = evt.actionKey;
                boxBase_.Clear ();
                boxBaseIntervals_.Clear ();
                // Shift / Ctrl を押しながらなら今の選択に足す
                if (evt.shiftKey || evt.actionKey) {
                    boxBase_.UnionWith (selectedKeys_);
                    boxBaseIntervals_.UnionWith (selectedIntervals_);
                }
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
                // 動かさずに離したらクリック: 矢印の上なら矢印を選ぶ。ほかは選択を外して、そのフレームへ移る
                if (!boxMoved_ && pressedInterval_.HasValue) {
                    ClickInterval (pressedInterval_.Value, boxShift_, boxAction_);
                }
                else if (!boxMoved_) {
                    selectedKeys_.Clear ();
                    selectedKeys_.UnionWith (boxBase_);
                    selectedIntervals_.Clear ();
                    selectedIntervals_.UnionWith (boxBaseIntervals_);
                    window_.SetFrame (Mathf.RoundToInt (XToFrame (boxStart_.x)));
                }
                pressedInterval_ = null;
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
        bool HitTestKey (float x, float y, out int trackIndex, out int frame)
        {
            trackIndex = -1;
            frame = 0;

            int row = Mathf.FloorToInt (y / kTrackHeight);
            if (row < 0 || row >= tracks_.Count) return false;

            PreviewWindow.TimelineTrack track = tracks_[row];
            Rect rowRect = new Rect (0, row * kTrackHeight, 0, kTrackHeight);
            foreach (int f in track.keys) {
                Rect box = GetKeyRect (f, rowRect);
                if (box.Contains (new Vector2 (x, y))) {
                    trackIndex = row;
                    frame = f;
                    return true;
                }
            }
            return false;
        }

        /// <summary>押した場所がどれかのキーの間の矢印（前のキーの右から次のキーの左まで）に当たっていれば、そのトラック名と前のキーのフレーム（S86）</summary>
        (string, int)? HitTestInterval (float x, float y)
        {
            int row = Mathf.FloorToInt (y / kTrackHeight);
            if (row < 0 || row >= tracks_.Count) return null;
            PreviewWindow.TimelineTrack track = tracks_[row];
            Rect rowRect = new Rect (0, row * kTrackHeight, 0, kTrackHeight);
            for (int k = 1; k < track.keys.Length; k++) {
                if (x > GetKeyRect (track.keys[k - 1], rowRect).xMax && x < GetKeyRect (track.keys[k], rowRect).x) return (track.label, track.keys[k - 1]);
            }
            return null;
        }

        /// <summary>
        /// 矢印を押した（S86）: その矢印だけを選ぶ（キーの選択は外す）。Ctrl（Mac は Cmd）なら足す・外す。
        /// Shift なら同じトラックの前に押した矢印からの間を全部選ぶ（前に押した矢印が無い・ほかのトラックなら、その矢印だけ）
        /// </summary>
        void ClickInterval ((string, int) interval, bool shift, bool action)
        {
            if (action) {
                if (!selectedIntervals_.Remove (interval)) selectedIntervals_.Add (interval);
                lastInterval_ = interval;
                return;
            }
            selectedKeys_.Clear ();
            PreviewWindow.TimelineTrack track = tracks_.FirstOrDefault (t => t.label == interval.Item1);
            if (shift && lastInterval_.HasValue && lastInterval_.Value.Item1 == interval.Item1 && track.keys != null) {
                int from = Mathf.Min (lastInterval_.Value.Item2, interval.Item2);
                int to = Mathf.Max (lastInterval_.Value.Item2, interval.Item2);
                selectedIntervals_.Clear ();
                for (int k = 0; k + 1 < track.keys.Length; k++) {
                    if (track.keys[k] >= from && track.keys[k] <= to) selectedIntervals_.Add ((track.label, track.keys[k]));
                }
                return;
            }
            selectedIntervals_.Clear ();
            selectedIntervals_.Add (interval);
            lastInterval_ = interval;
        }

        Rect BoxRect ()
        {
            return Rect.MinMaxRect (Mathf.Min (boxStart_.x, boxEnd_.x), Mathf.Min (boxStart_.y, boxEnd_.y),
                Mathf.Max (boxStart_.x, boxEnd_.x), Mathf.Max (boxStart_.y, boxEnd_.y));
        }

        /// <summary>
        /// 枠に掛かったキーと、真ん中が枠に入ったキーの間の矢印（S86）を選ぶ（ドラッグ前の選択 boxBase_・boxBaseIntervals_ に足す）。
        /// 矢印は真ん中で見る（キーだけをかすめたときに、隣の矢印まで選ばないように）
        /// </summary>
        void SelectInBox ()
        {
            Rect band = BoxRect ();
            selectedKeys_.Clear ();
            selectedKeys_.UnionWith (boxBase_);
            selectedIntervals_.Clear ();
            selectedIntervals_.UnionWith (boxBaseIntervals_);
            for (int i = 0; i < tracks_.Count; i++) {
                Rect row = new Rect (0, i * kTrackHeight, 0, kTrackHeight);
                if (row.yMax < band.yMin || row.yMin > band.yMax) continue;
                int[] keys = tracks_[i].keys;
                for (int k = 0; k < keys.Length; k++) {
                    if (GetKeyRect (keys[k], row).Overlaps (band)) selectedKeys_.Add ((tracks_[i].label, keys[k]));
                    if (k + 1 >= keys.Length) continue;
                    float mid = (GetKeyRect (keys[k], row).xMax + GetKeyRect (keys[k + 1], row).x) * 0.5f;
                    if (mid >= band.xMin && mid <= band.xMax) selectedIntervals_.Add ((tracks_[i].label, keys[k]));
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

        /// <summary>
        /// 選んだキーの削除。キーのコピー・貼り付けはメニューに出さない（Ctrl+C / Ctrl+V で足りる。
        /// 姿勢のコピー・貼り付けは、キー欄にフォーカスが無いときの Ctrl+C / Ctrl+V とタイムライン上端の Copy / Paste）
        /// </summary>
        void AddDeleteSelectedItem (GenericMenu menu)
        {
            GUIContent item = new GUIContent (Tr ("TIMELINE_OVERLAY_DELETE_SELECTED_KEYS", selectedKeys_.Count));
            if (selectedKeys_.Count > 0) menu.AddItem (item, false, DeleteSelectedKeys);
            else menu.AddDisabledItem (item);
        }

        void ShowSelectionMenu ()
        {
            GenericMenu menu = new GenericMenu ();
            AddDeleteSelectedItem (menu);
            menu.ShowAsContext ();
        }

        /// <summary>選んでいる矢印（S86）を、トラックと矢印の番号（前のキーの番号）で。トラックの並び順・左から</summary>
        List<(PreviewWindow.TimelineTrack, int)> SelectedIntervalList ()
        {
            List<(PreviewWindow.TimelineTrack, int)> result = new List<(PreviewWindow.TimelineTrack, int)> ();
            foreach (PreviewWindow.TimelineTrack track in tracks_) {
                for (int k = 0; k + 1 < track.keys.Length; k++) {
                    if (selectedIntervals_.Contains ((track.label, track.keys[k]))) result.Add ((track, k));
                }
            }
            return result;
        }

        /// <summary>矢印の今の補間の名前（AI の区間は ai、混在は mixed。メニューのチェックに使う）</summary>
        static string IntervalModeName (PreviewWindow.TimelineTrack track, int k)
        {
            BetweenClips.State between = track.between != null && k < track.between.Length ? track.between[k] : BetweenClips.State.None;
            if (between == BetweenClips.State.Fresh || between == BetweenClips.State.Stale) return "ai";
            KeyInterpolation? mode = track.interpolations != null && k < track.interpolations.Length ? track.interpolations[k] : KeyInterpolation.Curve;
            return KeyInterpolations.ToName (mode);
        }

        void AddInterpolationItem (GenericMenu menu, string label, List<PreviewWindow.Interval> intervals, KeyInterpolation mode, string current, bool enabled)
        {
            GUIContent item = new GUIContent (label);
            bool on = current == KeyInterpolations.ToName (mode);
            if (!enabled) {
                menu.AddDisabledItem (item, on);
                return;
            }
            menu.AddItem (item, on, () => {
                window_.SetInterpolation (intervals, mode);
                RebuildTracks ();
            });
        }

        /// <summary>
        /// 選んだ矢印の補間のメニュー（S86）: 曲線・リニア・ステップ・AI・AI の動きをキーにする。全部が同じ補間ならチェック。
        /// AI の印はクリップの区間に付くので、AI と「キーにする」はクリップ全体のトラックの矢印だけを選んでいるときに選べる
        /// </summary>
        void ShowIntervalMenu ()
        {
            List<(PreviewWindow.TimelineTrack, int)> chosen = SelectedIntervalList ();
            if (chosen.Count == 0) return;
            List<PreviewWindow.Interval> intervals = chosen.Select (c => new PreviewWindow.Interval { start = c.Item1.keys[c.Item2], end = c.Item1.keys[c.Item2 + 1], filter = c.Item1.filter }).ToList ();
            bool wholeClip = chosen.All (c => c.Item1.filter == null);
            List<string> modes = chosen.Select (c => IntervalModeName (c.Item1, c.Item2)).Distinct ().ToList ();
            string current = modes.Count == 1 ? modes[0] : null;
            bool canSet = intervals.All (i => window_.CanSetInterpolation (i.start));

            GenericMenu menu = new GenericMenu ();
            menu.AddDisabledItem (new GUIContent (Tr ("TIMELINE_OVERLAY_INTERPOLATION", chosen.Count)));
            menu.AddSeparator ("");
            AddInterpolationItem (menu, Tr ("TIMELINE_OVERLAY_INTERPOLATION_CURVE"), intervals, KeyInterpolation.Curve, current, canSet);
            AddInterpolationItem (menu, Tr ("TIMELINE_OVERLAY_INTERPOLATION_LINEAR"), intervals, KeyInterpolation.Linear, current, canSet);
            AddInterpolationItem (menu, Tr ("TIMELINE_OVERLAY_INTERPOLATION_CONSTANT"), intervals, KeyInterpolation.Constant, current, canSet);
            if (current == "mixed") menu.AddDisabledItem (new GUIContent (Tr ("TIMELINE_OVERLAY_INTERPOLATION_MIXED")), true);
            // AI（S82）。結果のファイルがあり、元のクリップへ書いているときだけ選べる。もう AI の区間は外す
            List<int> toExternal = wholeClip ? intervals.Where (i => window_.CanSetExternal (i.start) && !IsExternalState (window_.GetBetweenState (i.start))).Select (i => i.start).ToList () : new List<int> ();
            GUIContent externalItem = new GUIContent (Tr ("TIMELINE_OVERLAY_INTERPOLATION_EXTERNAL"));
            if (toExternal.Count > 0) {
                menu.AddItem (externalItem, current == "ai", () => {
                    window_.SetExternal (toExternal);
                    RebuildTracks ();
                });
            }
            else {
                menu.AddDisabledItem (externalItem, current == "ai");
            }
            if (wholeClip && intervals.Any (i => window_.GetBetweenState (i.start) == BetweenClips.State.Stale)) menu.AddDisabledItem (new GUIContent (Tr ("TIMELINE_OVERLAY_BETWEEN_STALE")), false);
            if (wholeClip && intervals.Any (i => window_.GetBetweenState (i.start) == BetweenClips.State.Lost)) menu.AddDisabledItem (new GUIContent (Tr ("TIMELINE_OVERLAY_BETWEEN_LOST")), false);
            // 後ろの区間から焼く（焼くと区間の中にキーが入るが、前の区間の始まりは変わらない）
            List<int> toBake = wholeClip ? intervals.Where (i => window_.CanBakeBetween (i.start)).Select (i => i.start).OrderByDescending (f => f).ToList () : new List<int> ();
            GUIContent bake = new GUIContent (Tr ("TIMELINE_OVERLAY_BAKE_BETWEEN"));
            if (toBake.Count > 0) {
                menu.AddItem (bake, false, () => {
                    foreach (int frame in toBake) {
                        string error = window_.BakeBetween (frame);
                        if (error != null) Debug.LogWarning ("Motion Editor: " + error);
                    }
                    RebuildTracks ();
                });
            }
            else {
                menu.AddDisabledItem (bake);
            }
            menu.ShowAsContext ();
        }

        static bool IsExternalState (BetweenClips.State state)
        {
            return state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale;
        }

        void ShowKeyMenu (int frame)
        {
            GenericMenu menu = new GenericMenu ();
            // 押したキーは選ばれているので、そのキーだけ消すのは「キーを削除」で足りる
            AddDeleteSelectedItem (menu);
            // 選んでいる骨のキーをまとめて減らす（読込で毎フレームに入ったキーを、指だけ打ち直すときなど）
            menu.AddSeparator ("");
            int selected = window_.selectedTargetCount;
            GUIContent keepOne = new GUIContent (Tr ("TIMELINE_OVERLAY_KEEP_ONE_KEY", frame, selected));
            GUIContent clearAll = new GUIContent (Tr ("TIMELINE_OVERLAY_CLEAR_ALL_KEYS", selected));
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
            // サイクルキー（S32）: 最後のキーに付け外しする。付いていればチェックを出す
            menu.AddSeparator ("");
            bool cycle = window_.cycle;
            GUIContent cycleItem = new GUIContent (Tr ("TIMELINE_OVERLAY_CYCLE_KEY"));
            if (window_.CanSetCycle (frame)) {
                menu.AddItem (cycleItem, cycle, () => {
                    window_.SetCycle (!cycle);
                    RebuildTracks ();
                });
            }
            else {
                menu.AddDisabledItem (cycleItem, cycle);
            }
            menu.ShowAsContext ();
        }
    }

}
