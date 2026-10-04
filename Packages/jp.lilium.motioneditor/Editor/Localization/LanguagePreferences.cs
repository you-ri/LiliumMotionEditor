using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Preferences の「Lilium Motion Editor」のページ（S27）。言語は人ごとに違うので、プロジェクト設定ではなくここに置く。
    /// LiveStudio では言語をリモートのアプリから選ぶが、モーションエディタにはそれが無いのでここで選ぶ
    /// </summary>
    static class LanguagePreferences
    {
        // 言語の名前はその言語で書く（読めない言語に切り替えても戻せるように）
        static readonly Dictionary<string, string> kNativeNames = new Dictionary<string, string> {
            { "en", "English" },
            { "ja", "日本語" }, // noloc: 言語の名前はその言語で出す
        };

        [SettingsProvider]
        static SettingsProvider Create ()
        {
            return new SettingsProvider ("Preferences/Lilium Motion Editor", SettingsScope.User) {
                label = "Lilium Motion Editor",
                keywords = new HashSet<string> (new[] { "Motion", "Language", "Localization" }),
                guiHandler = search => {
                    string[] codes = MotionEditorLocalization.kLanguages;
                    string[] names = codes.Select (c => kNativeNames.TryGetValue (c, out string name) ? name : c).ToArray ();
                    int index = Array.IndexOf (codes, LocalizationSystem.currentLanguage);

                    EditorGUI.BeginChangeCheck ();
                    // 項目名は英語に固定する（読めない言語にしてしまっても、ここは見つけられるように）
                    int picked = EditorGUILayout.Popup (new GUIContent ("Language", "Language of the Motion Editor windows"),
                        index < 0 ? 0 : index, names);
                    if (EditorGUI.EndChangeCheck ()) {
                        LocalizationSystem.currentLanguage = codes[picked];
                        // 文字は窓やオーバーレイを作るときに入れているので、作り直して切り替える
                        EditorUtility.RequestScriptReload ();
                    }
                },
            };
        }
    }

}
