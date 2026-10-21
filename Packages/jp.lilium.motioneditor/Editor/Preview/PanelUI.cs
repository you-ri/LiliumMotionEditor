using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// パネル（Overlay）の小さな部品。同じ見た目のボタンをパネルごとに書いていたのをまとめる
    /// </summary>
    static class PanelUI
    {
        /// <summary>横に並べて幅を分け合うボタン（左右の余白なし）</summary>
        public static Button FlexButton (string text, string tooltip, System.Action clicked)
        {
            Button button = new Button (clicked) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            return button;
        }

        /// <summary>詰めたボタン（幅を均等に分け、高さを決める。隣との間は右に 1 ピクセル）</summary>
        public static Button CompactButton (string text, string tooltip, System.Action clicked, float height)
        {
            Button button = new Button (clicked) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.height = height;
            button.style.marginLeft = 0;
            button.style.marginRight = 1;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            return button;
        }
    }

}
