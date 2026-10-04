using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// パネルのアクセントの色（入っているボタン・今の行・選んだキー）。色は自前で持たず、Unity のエディタのテーマが定義する
    /// 選択の色（USS 変数 --unity-colors-highlight-background）を使う。テーマ（明るい・暗い）を替えても付いていく。
    /// USS 変数は C# から直に読めないので、スタイルシート（MotionEditor.uss）のクラスで塗る
    /// </summary>
    static class AccentStyle
    {
        const string kStyleSheetPath = "Packages/jp.lilium.motioneditor/Editor/Preview/MotionEditor.uss";
        const string kAccentClass = "lilium-accent";
        const string kSourceClass = "lilium-accent-source";
        static readonly CustomStyleProperty<Color> kAccentProperty = new CustomStyleProperty<Color> ("--lilium-accent");

        /// <summary>スタイルが決まる前（Painter2D で描く所の初期値）。暗いテーマの選択の色と同じ</summary>
        public static readonly Color kFallbackColor = new Color32 (0x2C, 0x5D, 0x87, 0xFF);

        static StyleSheet styleSheet_;

        /// <summary>
        /// パネルの根にスタイルシートを付ける（下の要素でアクセントのクラスが効くようにする）
        /// </summary>
        public static void Attach (VisualElement root)
        {
            if (styleSheet_ == null) styleSheet_ = AssetDatabase.LoadAssetAtPath<StyleSheet> (kStyleSheetPath);
            if (styleSheet_ != null && !root.styleSheets.Contains (styleSheet_)) root.styleSheets.Add (styleSheet_);
        }

        /// <summary>
        /// 要素をアクセントの色で塗る・戻す。インラインの backgroundColor が残っているとクラスより強いので、使う側では設定しない
        /// </summary>
        public static void Set (VisualElement element, bool on)
        {
            element.EnableInClassList (kAccentClass, on);
        }

        /// <summary>
        /// Painter2D で描く要素がアクセントの色を受け取る。スタイルが決まるたび（テーマの切り替えを含む）に changed を呼ぶ
        /// </summary>
        public static void Track (VisualElement element, System.Action<Color> changed)
        {
            element.AddToClassList (kSourceClass);
            element.RegisterCallback<CustomStyleResolvedEvent> (e => {
                Color color;
                if (e.customStyle.TryGetValue (kAccentProperty, out color)) changed (color);
            });
        }
    }

}
