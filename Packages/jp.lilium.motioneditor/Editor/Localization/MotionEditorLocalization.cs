using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// モーションエディタの画面の言葉（S27）。LiveStudio の RemoteControlEditorLocalization と同じ形。
    /// 呼ぶ側は <c>using static Lilium.MotionEditorLocalization;</c> にして <c>Tr (キーの文字列)</c> と書く。
    /// キーは呼ぶ所に文字列でそのまま書く（組み立てない。言語ファイルにそろっているかをテストがコードから拾って見る）。
    /// ファイルは Resources の外（Editor/Localization/MotionEditorLocales/&lt;言語&gt;.json）に置き、AssetDatabase で読む
    /// （エディタにしか無い窓の文字なので、ビルドに入れない）
    /// </summary>
    static class MotionEditorLocalization
    {
        const string kPackageRoot = "Packages/jp.lilium.motioneditor/";
        public const string kLocaleFolder = kPackageRoot + "Editor/Localization/MotionEditorLocales/";

        /// <summary>
        /// 元の言語。ほかの言語に無いキーはこれに落とす
        /// </summary>
        public const string kSourceLanguage = "en";

        public static readonly string[] kLanguages = { kSourceLanguage, "ja" };

        static int loadedGeneration_ = -1;
        static bool missingReported_;

        /// <summary>
        /// 窓の文字が変わり得るときに進む数（LocalizationSystem.generation）
        /// </summary>
        public static int generation
        {
            get {
                EnsureLoaded ();
                return LocalizationSystem.generation;
            }
        }

        /// <summary>
        /// キーの文字を、今の言語・無ければ英語・それも無ければキーのまま返す。
        /// 英語へ落とすのは、訳していない窓がキーだらけになるより読めるため
        /// </summary>
        public static string Tr (string key)
        {
            EnsureLoaded ();
            if (LocalizationSystem.TryTranslate (LocalizationSystem.currentLanguage, key, out string text)) return text;
            return LocalizationSystem.TryTranslate (kSourceLanguage, key, out string source) ? source : key;
        }

        /// <summary>
        /// 値を含むキーの文字を、値を埋めて返す
        /// </summary>
        public static string Tr (string key, params object[] args)
        {
            string text = Tr (key);
            return args == null || args.Length == 0 ? text : string.Format (text, args);
        }

        /// <summary>
        /// 表を登録する（済んでいれば何もしない）。読むたびに呼ぶ（世代の比べだけなので軽い）。
        /// 元の言語のファイルが読めなかったときは済みにしない（取り込み前に呼ばれたときに、後で読み直せるように）
        /// </summary>
        public static void EnsureLoaded ()
        {
            if (loadedGeneration_ == LocalizationSystem.generation) return;

            bool loaded = true;
            foreach (string language in kLanguages) loaded &= Load (language) || language != kSourceLanguage;
            if (loaded) loadedGeneration_ = LocalizationSystem.generation;
        }

        [InitializeOnLoadMethod]
        static void Initialize () => EnsureLoaded ();

        static bool Load (string language)
        {
            string path = kLocaleFolder + language + ".json";
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset> (path);
            if (asset == null) {
                // ファイルが無くても窓は動く（残った言語で出る）ので、失敗にせず 1 度だけ知らせる
                if (language == kSourceLanguage && !missingReported_) {
                    missingReported_ = true;
                    Debug.LogWarning ($"[Lilium Motion Editor] Editor locale not found at \"{path}\".");
                }
                return false;
            }
            LocalizationSystem.LoadTranslations (language, asset.text);
            return true;
        }
    }

}
