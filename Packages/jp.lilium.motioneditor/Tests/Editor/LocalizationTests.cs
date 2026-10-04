using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Lilium
{

    /// <summary>
    /// 画面の文字の翻訳（S27）。言語の切り替え・落とし先と、コードと言語ファイルがそろっているか
    /// </summary>
    public class LocalizationTests
    {
        // 行の終わりにこれを書いた行の日本語は訳さない（骨の名前の照合など、画面に出さない文字）
        const string kIgnoreMark = "noloc";

        string saved_;

        [SetUp]
        public void SetUp ()
        {
            saved_ = LocalizationSystem.currentLanguage;
        }

        [TearDown]
        public void TearDown ()
        {
            LocalizationSystem.currentLanguage = saved_;
        }

        [Test]
        public void ParseFlatJson_ReadsStrings ()
        {
            // 先頭の BOM・エスケープ（\" \n \uXXXX \\）・空の値
            const string json = "\uFEFF{\n    \"A\": \"x \\\"q\\\" \\n y\",\n    \"B\": \"\\u3042\\\\\",\n    \"C\": \"\"\n}\n";
            var table = LocalizationSystem.ParseFlatJson (json);
            Assert.AreEqual (3, table.Count);
            Assert.AreEqual ("x \"q\" \n y", table["A"]);
            Assert.AreEqual ("\u3042\\", table["B"]);
            Assert.AreEqual ("", table["C"]);
            Assert.AreEqual (0, LocalizationSystem.ParseFlatJson ("{ }").Count);
            Assert.Throws<System.FormatException> (() => LocalizationSystem.ParseFlatJson ("{ \"A\": 1 }"), "文字列でない値は読まない");
        }

        [Test]
        public void Tr_FollowsLanguage_AndFallsBack ()
        {
            Dictionary<string, string> en = LoadLocale ("en");
            Dictionary<string, string> ja = LoadLocale ("ja");
            string key = en.Keys.First (k => en[k] != ja[k]);

            LocalizationSystem.currentLanguage = "ja";
            Assert.AreEqual (ja[key], MotionEditorLocalization.Tr (key));
            LocalizationSystem.currentLanguage = "en";
            Assert.AreEqual (en[key], MotionEditorLocalization.Tr (key));
            LocalizationSystem.currentLanguage = "fr";
            Assert.AreEqual (en[key], MotionEditorLocalization.Tr (key), "表の無い言語は英語へ落とす");
            Assert.AreEqual ("NO_SUCH_KEY", MotionEditorLocalization.Tr ("NO_SUCH_KEY"), "どこにも無いキーはそのまま");
        }

        [Test]
        public void Tr_FillsArguments ()
        {
            LocalizationSystem.currentLanguage = "en";
            Dictionary<string, string> en = LoadLocale ("en");
            string key = en.Keys.First (k => en[k].Contains ("{0}") && !en[k].Contains ("{1}"));
            Assert.AreEqual (en[key].Replace ("{0}", "xyz"), MotionEditorLocalization.Tr (key, "xyz"));
        }

        /// <summary>
        /// 言語ファイルどうしが同じキーを持ち、英語に日本語が残らず、埋める所がそろっている
        /// </summary>
        [Test]
        public void Locales_AreConsistent ()
        {
            Dictionary<string, string> en = LoadLocale ("en");
            Dictionary<string, string> ja = LoadLocale ("ja");
            Assert.IsNotEmpty (en);

            Assert.IsEmpty (en.Keys.Except (ja.Keys).ToList (), "ja.json に無いキー");
            Assert.IsEmpty (ja.Keys.Except (en.Keys).ToList (), "en.json に無いキー");
            Assert.IsEmpty (en.Where (p => string.IsNullOrWhiteSpace (p.Value) || string.IsNullOrWhiteSpace (ja[p.Key])).Select (p => p.Key).ToList (), "空の訳");
            Assert.IsEmpty (en.Where (p => HasJapanese (p.Value)).Select (p => p.Key).ToList (), "en.json に日本語が残っている");
            Assert.IsEmpty (en.Keys.Where (k => !SamePlaceholders (en[k], ja[k])).ToList (), "{0} などの埋める所が言語で合わない");
        }

        /// <summary>
        /// コードで引くキーがどれも言語ファイルにあり、言語ファイルに使っていないキーが無く、コードに日本語の文字列が残っていない
        /// </summary>
        [Test]
        public void Code_UsesOnlyKnownKeys_AndNoJapaneseLiterals ()
        {
            Dictionary<string, string> en = LoadLocale ("en");
            var used = new HashSet<string> ();
            var unknown = new List<string> ();
            var japanese = new List<string> ();

            var call = new Regex (@"\bTr\s*\(\s*""([A-Z0-9_]+)""");
            string root = Path.GetFullPath ("Packages/jp.lilium.motioneditor/Editor");
            foreach (string file in Directory.GetFiles (root, "*.cs", SearchOption.AllDirectories)) {
                string relative = file.Substring (root.Length + 1).Replace ('\\', '/');
                string text = File.ReadAllText (file, Encoding.UTF8);
                string[] lines = text.Split ('\n');
                foreach (Match match in call.Matches (text)) {
                    string key = match.Groups[1].Value;
                    used.Add (key);
                    if (!en.ContainsKey (key)) unknown.Add ($"{relative}: {key}");
                }
                foreach (Literal literal in Literals (text)) {
                    if (HasJapanese (literal.text) && !lines[literal.line].Contains (kIgnoreMark)) japanese.Add ($"{relative}:{literal.line + 1}: {literal.text}");
                }
            }

            Assert.IsEmpty (unknown, "言語ファイルに無いキー:\n" + string.Join ("\n", unknown));
            Assert.IsEmpty (japanese, "コードに残っている日本語の文字列（訳すか、画面に出さない文字なら行に noloc を書く）:\n" + string.Join ("\n", japanese));
            var unused = en.Keys.Where (k => !used.Contains (k)).ToList ();
            Assert.IsEmpty (unused, "コードで使っていないキー:\n" + string.Join ("\n", unused));
        }

        static Dictionary<string, string> LoadLocale (string language)
        {
            string path = Path.GetFullPath (MotionEditorLocalization.kLocaleFolder + language + ".json");
            return LocalizationSystem.ParseFlatJson (File.ReadAllText (path, Encoding.UTF8));
        }

        static readonly Regex kJapanese = new Regex (@"[぀-ヿ㐀-鿿]");
        static readonly Regex kPlaceholder = new Regex (@"\{\d+(:[^}]*)?\}");

        static bool HasJapanese (string text) => kJapanese.IsMatch (text);

        static bool SamePlaceholders (string a, string b)
        {
            var pa = kPlaceholder.Matches (a).Cast<Match> ().Select (m => m.Value).OrderBy (s => s);
            var pb = kPlaceholder.Matches (b).Cast<Match> ().Select (m => m.Value).OrderBy (s => s);
            return pa.SequenceEqual (pb);
        }

        struct Literal
        {
            public string text;
            public int line;
        }

        /// <summary>
        /// C# の文字列を取り出す（注釈と文字の定数は飛ばす）。逐語的文字列（@"..."）と $"..." も読む
        /// （$"..." の {} の中は文字列の一部として読むが、日本語があるかを見るだけなので困らない）
        /// </summary>
        static IEnumerable<Literal> Literals (string source)
        {
            int line = 0;
            int i = 0;
            while (i < source.Length) {
                char c = source[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) {
                        if (source[i] == '\n') line++;
                        i++;
                    }
                    i += 2;
                    continue;
                }
                if (c == '\'') {
                    i++;
                    while (i < source.Length && source[i] != '\'') i += source[i] == '\\' ? 2 : 1;
                    i++;
                    continue;
                }
                bool prefixed = (c == '@' || c == '$') && i + 1 < source.Length && (source[i + 1] == '"' || source[i + 1] == '@' || source[i + 1] == '$');
                if (c != '"' && !prefixed) { i++; continue; }

                bool verbatim = false;
                while (source[i] != '"') {
                    if (source[i] == '@') verbatim = true;
                    i++;
                }
                i++;
                int startLine = line;
                var sb = new StringBuilder ();
                while (i < source.Length) {
                    char d = source[i];
                    if (d == '"') {
                        if (verbatim && i + 1 < source.Length && source[i + 1] == '"') { sb.Append ('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (!verbatim && d == '\\' && i + 1 < source.Length) {
                        sb.Append (source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (d == '\n') line++;
                    sb.Append (d);
                    i++;
                }
                yield return new Literal { text = sb.ToString (), line = startLine };
            }
        }
    }

}
