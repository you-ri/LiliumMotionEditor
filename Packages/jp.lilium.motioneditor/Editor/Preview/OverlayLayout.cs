using System.Runtime.CompilerServices;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// パネル（Overlay）の幅の決め方をそろえる。
    /// - **左右の列にドックしているとき**: 幅を決めずに列いっぱいに伸ばす（列の幅を変えると中身も付いてくる）。
    /// - **ツールバー（上下左右の帯）に入れたとき**: ボタン 1 つに畳み、押すとパネルが出る（CollapseInToolbar）。
    ///   ツールバーの中身を持つもの（Timeline）は畳まず、帯の幅に合わせる（帯は窓の幅なので、中身も窓の幅いっぱいになる）。
    ///   帯の中では、パネルは中身の大きさで決まるので、伸ばすだけでは広がらない。
    /// - **浮いているとき**: パネルの枠いっぱいに広げる（枠を Unity のつまみで広げると中身も付いてくる）。
    ///   渡された幅は、まだ大きさを変えていないときの初期の幅（Overlay.defaultSize）にする。
    /// - **畳んだボタンから出したとき**: 浮いているときの幅にする。
    /// 枠は Unity のクラス名（unity-overlay-container / …-horizontal）で見分ける。見つからなければ浮いている扱い
    /// </summary>
    static class OverlayLayout
    {
        const string kContainerClass = "unity-overlay-container";
        const string kHorizontalClass = "unity-overlay-container-horizontal";
        /// <summary>上下の帯の枠との余白</summary>
        const float kBarMargin = 20;
        const float kMinBarWidth = 80;

        sealed class ToolbarCollapse
        {
            /// <summary>ツールバーに入れたので畳んだ（帯から出したら開き直す。自分で畳んだものは開かない）</summary>
            public bool collapsedByToolbar;
        }

        static readonly ConditionalWeakTable<Overlay, ToolbarCollapse> collapses_ = new ConditionalWeakTable<Overlay, ToolbarCollapse> ();

        /// <summary>
        /// ツールバーに入れたパネル（layout が Panel のもの）をボタン 1 つに畳む。帯に横長の中身を広げないため。
        /// 帯から列・浮きへ出したら開き直す。何度呼んでもよい（初めの 1 回だけ結び付ける）
        /// </summary>
        public static void CollapseInToolbar (Overlay overlay)
        {
            if (overlay == null || overlay.rootVisualElement == null) return;
            if (collapses_.TryGetValue (overlay, out _)) return;
            ToolbarCollapse state = new ToolbarCollapse ();
            collapses_.Add (overlay, state);

            System.Action apply = () => {
                // ツールバーの中身を持つもの（Timeline）はそのまま帯に出す
                if (overlay.layout != Layout.Panel) return;
                if (overlay.isInToolbar) {
                    state.collapsedByToolbar = true;
                    if (!overlay.collapsed) overlay.collapsed = true;
                }
                else if (state.collapsedByToolbar) {
                    state.collapsedByToolbar = false;
                    overlay.collapsed = false;
                }
            };
            // 枠を移した後（親が入れ替わってから）に見る
            VisualElement element = overlay.rootVisualElement;
            System.Action later = () => element.schedule.Execute (apply);
            element.RegisterCallback<AttachToPanelEvent> (e => later ());
            element.RegisterCallback<GeometryChangedEvent> (e => later ());
            overlay.floatingChanged += floating => later ();
            overlay.layoutChanged += layout => later ();
            later ();
        }

        /// <param name="root">パネルの中身のいちばん外側</param>
        /// <param name="floatingWidth">浮いているときの幅</param>
        public static void FollowDockWidth (Overlay overlay, VisualElement root, System.Func<float> floatingWidth)
        {
            if (overlay == null || root == null) return;
            CollapseInToolbar (overlay);
            VisualElement bar = null;
            EventCallback<GeometryChangedEvent> onBarResized = e => {
                if (bar == null) return;
                float width = bar.resolvedStyle.width - kBarMargin;
                if (!float.IsNaN (width) && width > kMinBarWidth) root.style.width = width;
            };
            System.Action<VisualElement> followBar = area => {
                if (bar == area) return;
                if (bar != null) bar.UnregisterCallback (onBarResized);
                bar = area;
                if (bar != null) {
                    bar.RegisterCallback (onBarResized);
                    onBarResized (null);
                }
            };

            System.Action apply = () => {
                if (overlay.collapsed) {
                    // 畳んだボタンから出したパネル: 浮いているときの幅にする（帯・列の幅には合わせない）
                    followBar (null);
                    float popup = floatingWidth != null ? floatingWidth () : 0;
                    root.style.width = popup > 0 ? popup : StyleKeyword.Auto;
                    root.style.minWidth = StyleKeyword.Auto;
                    root.style.flexGrow = 0;
                    return;
                }
                VisualElement container = null;
                for (VisualElement element = root.parent; element != null && container == null; element = element.parent) {
                    if (element.ClassListContains (kContainerClass)) container = element;
                }
                if (container != null && container.ClassListContains (kHorizontalClass)) {
                    // 上下の帯: 帯の幅に合わせる
                    root.style.flexGrow = 0;
                    root.style.minWidth = StyleKeyword.Auto;
                    followBar (container);
                    return;
                }
                followBar (null);
                if (container != null) {
                    // 左右の列: 幅を決めず、列いっぱいに伸ばす
                    root.style.width = StyleKeyword.Auto;
                    root.style.minWidth = StyleKeyword.Auto;
                    root.style.flexGrow = 1;
                    return;
                }
                // 浮いている: 枠いっぱいに広げる。初期の幅だけ渡された値にする
                float floating = floatingWidth != null ? floatingWidth () : 0;
                if (floating > 0 && Mathf.Abs (overlay.defaultSize.x - floating) > 0.5f) {
                    overlay.defaultSize = new Vector2 (floating, overlay.defaultSize.y);
                }
                root.style.width = StyleKeyword.Auto;
                root.style.minWidth = StyleKeyword.Auto;
                root.style.flexGrow = 1;
            };

            overlay.floatingChanged += floating => apply ();
            root.RegisterCallback<AttachToPanelEvent> (e => apply ());
            // 枠を移したとき（列 ↔ 帯 ↔ 浮き）は、親が入れ替わってから大きさが決まる
            root.RegisterCallback<GeometryChangedEvent> (e => apply ());
            root.RegisterCallback<DetachFromPanelEvent> (e => followBar (null));
            apply ();
        }
    }

}
