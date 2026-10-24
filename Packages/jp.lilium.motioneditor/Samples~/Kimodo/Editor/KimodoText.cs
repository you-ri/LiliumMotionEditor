using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Kimodo のサンプルの画面の言葉。モーションエディタと同じ言語（Preferences > Lilium Motion Editor > Language）で出す。
    /// 言語ファイルはサンプルの Editor/Locales/&lt;言語&gt;.json。取り込んだ場所はプロジェクトごとに違うので、KimodoSetup の場所から探す。
    /// 呼ぶ側は <c>using static Lilium.KimodoText;</c> にして <c>Tr ("KIMODO_...")</c> と書く（キーは組み立てない。テストがコードから拾って言語ファイルと比べる）
    /// </summary>
    static class KimodoText
    {
        static readonly string[] kLanguages = { "en", "ja" };
        static bool loaded_;

        public static string Tr (string key)
        {
            EnsureLoaded ();
            return MotionEditorLanguage.Tr (key);
        }

        public static string Tr (string key, params object[] args)
        {
            EnsureLoaded ();
            return MotionEditorLanguage.Tr (key, args);
        }

        /// <summary>
        /// 言語ファイルを本体の表へ 1 度だけ足す（表はスクリプトを読み込み直すまで残る。言語を変えると読み込み直す）。
        /// 英語のファイルが読めなかったとき（取り込みの途中）は済みにせず、次に引くときに読み直す
        /// </summary>
        static void EnsureLoaded ()
        {
            if (loaded_) return;
            string folder = KimodoSetup.SampleFolder ();
            if (folder == null) return;
            foreach (string language in kLanguages) {
                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset> (folder + "/Editor/Locales/" + language + ".json");
                if (asset == null) continue;
                MotionEditorLanguage.LoadTranslations (language, asset.text);
                if (language == kLanguages[0]) loaded_ = true;
            }
        }
    }

}
