using System.Text;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// ボタンの説明（tooltip）の後ろに、その操作のショートカットに今割り当てているキーを付ける（例: 「次のキー (.)」）。
    /// キーは Edit > Shortcuts で変えられるので、文字で書かずに Shortcut Manager から読み、説明が出る前（マウスが乗ったとき）に読み直す
    /// </summary>
    static class ShortcutTooltip
    {
        const string kPreviewPrefix = "Lilium Motion Editor/";
        const string kScenePrefix = "Lilium Motion Editor (Scene)/";

        /// <param name="host">パネルが載っている窓。ショートカットは窓ごとに別に持つ（Motion Scene は "(Scene)" の付く方）</param>
        /// <param name="name">ショートカットの名前（[Shortcut] の ID の、窓の名前より後ろ。"Next Key" など）</param>
        public static void Set (VisualElement element, EditorWindow host, string text, string name)
        {
            string id = (host is SceneWindow ? kScenePrefix : kPreviewPrefix) + name;
            element.tooltip = Format (text, id);
            element.RegisterCallback<PointerEnterEvent> (e => element.tooltip = Format (text, id));
        }

        /// <summary>text の後ろに、id のショートカットのキーを付ける。キーが無い（割り当てを外した・その窓には無い）ならそのまま</summary>
        public static string Format (string text, string id)
        {
            string keys = GetKeys (id);
            return string.IsNullOrEmpty (keys) ? text : text + " (" + keys + ")";
        }

        static string GetKeys (string id)
        {
            ShortcutBinding binding;
            try {
                binding = ShortcutManager.instance.GetShortcutBinding (id);
            }
            catch (System.ArgumentException) {
                // その窓には無いショートカット（Motion Scene に無い Paste Pose など）
                return null;
            }
            StringBuilder builder = new StringBuilder ();
            foreach (KeyCombination combination in binding.keyCombinationSequence) {
                if (builder.Length > 0) builder.Append (", ");
                AppendCombination (builder, combination);
            }
            return builder.ToString ();
        }

        static void AppendCombination (StringBuilder builder, KeyCombination combination)
        {
            ShortcutModifiers modifiers = combination.modifiers;
            if ((modifiers & ShortcutModifiers.Action) != 0) builder.Append (Application.platform == RuntimePlatform.OSXEditor ? "Cmd+" : "Ctrl+");
            if ((modifiers & ShortcutModifiers.Alt) != 0) builder.Append ("Alt+");
            if ((modifiers & ShortcutModifiers.Shift) != 0) builder.Append ("Shift+");
            builder.Append (KeyText (combination.keyCode));
        }

        /// <summary>キーの見せ方。記号のキーは KeyCode の名前（Comma など）より記号そのもののほうが読みやすい</summary>
        static string KeyText (KeyCode keyCode)
        {
            switch (keyCode) {
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.BackQuote: return "`";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
            }
            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9) return ((int)(keyCode - KeyCode.Alpha0)).ToString ();
            return keyCode.ToString ();
        }
    }

}
