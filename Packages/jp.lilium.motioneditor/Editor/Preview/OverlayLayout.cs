using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// パネル（Overlay）の幅の決め方をそろえる。
    /// - **左右の列にドックしているとき**: 幅を決めずに列いっぱいに伸ばす（列の幅を変えると中身も付いてくる）。
    /// - **上下の帯にドックしているとき**: 帯の幅に合わせる（帯は窓の幅なので、中身も窓の幅いっぱいになる）。
    ///   帯の中では、パネルは中身の大きさで決まるので、伸ばすだけでは広がらない。
    /// - **浮いているとき**: パネルの枠いっぱいに広げる（枠を Unity のつまみで広げると中身も付いてくる）。
    ///   渡された幅は、まだ大きさを変えていないときの初期の幅（Overlay.defaultSize）にする。
    /// 枠は Unity のクラス名（unity-overlay-container / …-horizontal）で見分ける。見つからなければ浮いている扱い
    /// </summary>
    static class OverlayLayout
    {
        const string kContainerClass = "unity-overlay-container";
        const string kHorizontalClass = "unity-overlay-container-horizontal";
        /// <summary>上下の帯の枠との余白</summary>
        const float kBarMargin = 20;
        const float kMinBarWidth = 80;

        /// <param name="root">パネルの中身のいちばん外側</param>
        /// <param name="floatingWidth">浮いているときの幅</param>
        public static void FollowDockWidth (Overlay overlay, VisualElement root, System.Func<float> floatingWidth)
        {
            if (overlay == null || root == null) return;
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
