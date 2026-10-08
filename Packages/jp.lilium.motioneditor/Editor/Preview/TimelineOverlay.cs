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
            AccentStyle.Attach (root);
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


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Timeline.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-timeline", "Timeline", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom,
        defaultLayout = Layout.HorizontalToolbar)]
    sealed class TimelineOverlayScene : TimelineOverlay
    {
    }

}
