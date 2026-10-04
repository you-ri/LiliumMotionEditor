using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 翻訳の管理・解決（S27）。LiveStudio（jp.lilium.remotecontrol）の LocalizationSystem を写したもの。
    /// キーに対応する翻訳テキストを返し、見つからない場合はキーをそのまま返す。
    /// 元との違い: エディタでしか使わないので、言語は PlayerPrefs ではなく EditorPrefs に持つ（ゲームの PlayerPrefs を汚さない）。
    /// 再生のたびに表を空にする処理（Domain Reload 切りへの備え）も要らないので持たない
    /// </summary>
    static class LocalizationSystem
    {
        const string kPrefsKey = "LiliumMotionEditor_Language";
        const string kDefaultLanguage = "en";

        // language -> (key -> translated text)
        static Dictionary<string, Dictionary<string, string>> translations_ = new Dictionary<string, Dictionary<string, string>> ();
        static List<string> availableLanguages_ = new List<string> ();
        static string currentLanguage_;
        static int generation_;

        /// <summary>
        /// Translate の答えが変わり得るとき（言語・表）に進む数。キーを一度だけ引いて持っておく読み手が、引き直す時を知るのに使う
        /// </summary>
        public static int generation => generation_;

        /// <summary>
        /// 現在の言語コード（例: "en", "ja"）
        /// </summary>
        public static string currentLanguage
        {
            get {
                if (currentLanguage_ == null) Initialize ();
                return currentLanguage_;
            }
            set {
                if (string.IsNullOrEmpty (value)) return;
                if (currentLanguage_ == value) return;

                currentLanguage_ = value;
                generation_++;
                EditorPrefs.SetString (kPrefsKey, value);
            }
        }

        /// <summary>
        /// 利用可能な言語一覧
        /// </summary>
        public static IReadOnlyList<string> availableLanguages
        {
            get {
                if (currentLanguage_ == null) Initialize ();
                return availableLanguages_;
            }
        }

        /// <summary>
        /// 初期化。EditorPrefs またはシステム言語から現在の言語を決める
        /// </summary>
        static void Initialize ()
        {
            if (currentLanguage_ != null) return;

            currentLanguage_ = EditorPrefs.HasKey (kPrefsKey)
                ? EditorPrefs.GetString (kPrefsKey)
                : SystemLanguageToCode (Application.systemLanguage);

            // en は常に利用可能
            if (!availableLanguages_.Contains (kDefaultLanguage)) availableLanguages_.Add (kDefaultLanguage);
        }

        /// <summary>
        /// キーに対応する翻訳テキストを返す。見つからない場合はキーをそのまま返す
        /// </summary>
        public static string Translate (string key)
        {
            if (string.IsNullOrEmpty (key)) return key;
            if (currentLanguage_ == null) Initialize ();

            if (translations_.TryGetValue (currentLanguage_, out var dict) && dict.TryGetValue (key, out var translated)) return translated;
            return key;
        }

        /// <summary>
        /// 1 つの言語だけでキーを引く（ほかへは落とさない）。落とし先を呼ぶ側で決めたいとき用
        /// </summary>
        public static bool TryTranslate (string language, string key, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty (language) || string.IsNullOrEmpty (key)) return false;
            return translations_.TryGetValue (language, out var dict) && dict.TryGetValue (key, out text);
        }

        /// <summary>
        /// 値を含むキーを訳して埋める。語順は翻訳で真っ先に変わるので、訳した断片をつなげず、埋める所を訳文の中に持つ。
        /// 埋める所の壊れた訳文はここで例外になる（直す所はファイルなので、黙って違う文を出すより良い）
        /// </summary>
        public static string Format (string key, params object[] args)
        {
            string text = Translate (key);
            return args == null || args.Length == 0 ? text : string.Format (text, args);
        }

        /// <summary>
        /// 翻訳データを登録する。JSON 形式: { "key": "translated text", ... }。既存のキーは上書きされる
        /// </summary>
        public static void LoadTranslations (string language, string json)
        {
            if (string.IsNullOrEmpty (language) || string.IsNullOrEmpty (json)) {
                Debug.LogWarning ("[Lilium Motion Editor] LoadTranslations: language or json is null/empty.");
                return;
            }

            try {
                Dictionary<string, string> parsed = ParseFlatJson (json);

                if (!translations_.TryGetValue (language, out var dict)) {
                    dict = new Dictionary<string, string> ();
                    translations_[language] = dict;
                }
                foreach (var pair in parsed) dict[pair.Key] = pair.Value;

                if (!availableLanguages_.Contains (language)) availableLanguages_.Add (language);
                generation_++;
            }
            catch (Exception ex) {
                Debug.LogError ("[Lilium Motion Editor] Failed to load translations for '" + language + "': " + ex.Message);
            }
        }

        /// <summary>
        /// 文字列だけの平らな JSON（{ "key": "text", ... }）を読む。
        /// 元は Newtonsoft の JObject で読んでいたが、このためだけにパッケージの依存を増やさない（JsonUtility は Dictionary を読めない）。
        /// 形が違えば FormatException
        /// </summary>
        public static Dictionary<string, string> ParseFlatJson (string json)
        {
            var result = new Dictionary<string, string> ();
            int i = 0;
            SkipSpace (json, ref i);
            Expect (json, ref i, '{');
            SkipSpace (json, ref i);
            if (i < json.Length && json[i] == '}') return result;
            while (true) {
                SkipSpace (json, ref i);
                string key = ReadString (json, ref i);
                SkipSpace (json, ref i);
                Expect (json, ref i, ':');
                SkipSpace (json, ref i);
                result[key] = ReadString (json, ref i);
                SkipSpace (json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                Expect (json, ref i, '}');
                return result;
            }
        }

        static void SkipSpace (string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace (s[i]) || s[i] == '﻿')) i++;
        }

        static void Expect (string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException ($"'{c}' expected at {i}");
            i++;
        }

        static string ReadString (string s, ref int i)
        {
            Expect (s, ref i, '"');
            var sb = new StringBuilder ();
            while (i < s.Length) {
                char c = s[i++];
                if (c == '"') return sb.ToString ();
                if (c != '\\') { sb.Append (c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e) {
                    case 'n': sb.Append ('\n'); break;
                    case 't': sb.Append ('\t'); break;
                    case 'r': sb.Append ('\r'); break;
                    case 'b': sb.Append ('\b'); break;
                    case 'f': sb.Append ('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException ($"bad \\u escape at {i}");
                        sb.Append ((char)Convert.ToInt32 (s.Substring (i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append (e); break; // \" \\ \/
                }
            }
            throw new FormatException ("unterminated string");
        }

        /// <summary>
        /// Application.systemLanguage を言語コードに変換
        /// </summary>
        static string SystemLanguageToCode (SystemLanguage lang)
        {
            switch (lang) {
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional: return "zh-CN";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Russian: return "ru";
                default: return kDefaultLanguage;
            }
        }
    }

}
