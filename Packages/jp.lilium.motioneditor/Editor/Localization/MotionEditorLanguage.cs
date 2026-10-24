namespace Lilium
{

    /// <summary>
    /// 拡張やサンプルが、画面の言葉をモーションエディタと同じ言語（Preferences > Lilium Motion Editor > Language）で出すための口。
    /// 自分の言語ファイル（{ "キー": "文字" } の平らな JSON）を言語ごとに登録し、Tr で引く。今の言語に無いキーは英語へ、それも無ければキーのまま。
    /// キーは本体と重ならない頭を付ける（Kimodo のサンプルは KIMODO_）。本体の翻訳の仕組みは internal のままにし、ここだけを出す
    /// </summary>
    public static class MotionEditorLanguage
    {
        /// <summary>今の言語のコード（"en"・"ja"）</summary>
        public static string current => LocalizationSystem.currentLanguage;

        /// <summary>
        /// 言語ファイルを登録する（同じキーは上書き）。表はスクリプトを読み込み直すまで残るので、読み込みのたびに 1 回呼べばよい
        /// （言語を変えるとスクリプトを読み込み直す）
        /// </summary>
        public static void LoadTranslations (string language, string json)
        {
            LocalizationSystem.LoadTranslations (language, json);
        }

        public static string Tr (string key)
        {
            return MotionEditorLocalization.Tr (key);
        }

        /// <summary>値を含むキーの文字を、値を埋めて返す（string.Format）</summary>
        public static string Tr (string key, params object[] args)
        {
            return MotionEditorLocalization.Tr (key, args);
        }
    }

}
