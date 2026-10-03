using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// AnimBank（S10）。キャラごとのフォルダにある編集用クリップ（*.rig.anim）の一覧と、作成・複製・名前変更・削除。
    /// フォルダはキャラの設定・プロジェクト設定で決める（S19。SettingsLookup）。どちらにも無ければ拡張点 → 「キャラの prefab のフォルダ/Motions」。
    /// サブフォルダはそのまま一覧の組になる
    /// </summary>
    public static class AnimBank
    {
        public const string kDefaultFolderName = "Motions";
        internal const string kPrefsKey = "MikotoMotionEditor.AnimBank.";
        static readonly Regex kTrailingNumber = new Regex (@"^(.*?)(\d+)$");

        public struct Entry
        {
            public string path;
            /// <summary>表示名（.rig を除いたもの）</summary>
            public string name;
            /// <summary>フォルダから見たサブフォルダ（直下なら空）</summary>
            public string group;
        }

        /// <summary>
        /// 既定の置き場所を決める口（プロジェクトの決まりに合わせたいとき、[InitializeOnLoad] などから入れる）。
        /// null を返すと、パッケージの既定（prefab のフォルダ/Motions）
        /// </summary>
        public static System.Func<GameObject, string> defaultFolder;

        /// <summary>
        /// このキャラのクリップを作る場所（フォルダの並びの先頭）。prefab が無ければ null
        /// </summary>
        public static string GetFolder (GameObject prefab)
        {
            List<string> folders = GetFolders (prefab);
            return folders.Count > 0 ? folders[0] : null;
        }

        /// <summary>このキャラの一覧に並べるフォルダ（先頭が作る場所）</summary>
        public static List<string> GetFolders (GameObject prefab)
        {
            return SettingsLookup.GetAnimBankFolders (prefab);
        }

        /// <summary>
        /// 作る場所を選び直す（null で既定へ戻す）。キャラの設定があればそこへ書き、無ければこの PC にだけ覚える（EditorPrefs）
        /// </summary>
        public static void SetFolder (GameObject prefab, string folder)
        {
            CharacterSettings settings = SettingsLookup.Find (prefab);
            if (settings != null) {
                SetFirstFolder (settings, ref settings.overrideAnimBankFolders, settings.animBankFolders, ProjectSettings.instance.animBankFolders, folder);
                return;
            }
            SetFolder (prefab, kPrefsKey, folder);
        }

        /// <summary>設定が無いときの既定（この PC で選んだもの → 拡張点 → prefab のフォルダ/Motions）</summary>
        internal static string GetDefaultFolder (GameObject prefab)
        {
            return GetFolder (prefab, kPrefsKey, defaultFolder, kDefaultFolderName);
        }

        /// <summary>この PC で選んだフォルダ（EditorPrefs。無ければ null）。キャラの設定を作るときに取り込む</summary>
        internal static string GetSavedFolder (GameObject prefab)
        {
            return GetSavedFolder (prefab, kPrefsKey);
        }

        internal static string GetSavedFolder (GameObject prefab, string prefsKey)
        {
            string prefabPath = prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            if (string.IsNullOrEmpty (prefabPath)) return null;
            string saved = EditorPrefs.GetString (prefsKey + AssetDatabase.AssetPathToGUID (prefabPath), "");
            return string.IsNullOrEmpty (saved) ? null : saved;
        }

        /// <summary>キャラの設定のフォルダの並びの先頭を差し替える（null で上書きをやめる）</summary>
        /// <param name="inherited">上書きを始めるときに引き継ぐ並び（プロジェクト設定の並び）</param>
        internal static void SetFirstFolder (CharacterSettings settings, ref bool overrides, List<string> folders, List<string> inherited, string folder)
        {
            Undo.RecordObject (settings, "Motion Editor Folder");
            if (string.IsNullOrEmpty (folder)) {
                overrides = false;
            }
            else {
                if (!overrides) {
                    // 上書きを始めるときは、プロジェクト設定の並びを引き継いで先頭だけ差し替える（共通のフォルダを並べたまま）
                    folders.Clear ();
                    if (inherited != null) folders.AddRange (inherited);
                }
                overrides = true;
                if (folders.Count == 0) folders.Add (folder.TrimEnd ('/'));
                else folders[0] = folder.TrimEnd ('/');
            }
            EditorUtility.SetDirty (settings);
            AssetDatabase.SaveAssetIfDirty (settings);
        }

        /// <summary>
        /// キャラごとの置き場所（AnimBank と PoseBank で共通）。選んだもの（EditorPrefs に prefab の GUID で覚える）→ 既定の口 → 「prefab のフォルダ/既定の名前」の順
        /// </summary>
        internal static string GetFolder (GameObject prefab, string prefsKey, System.Func<GameObject, string> resolve, string defaultName)
        {
            string prefabPath = prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            if (string.IsNullOrEmpty (prefabPath)) return null;
            string saved = EditorPrefs.GetString (prefsKey + AssetDatabase.AssetPathToGUID (prefabPath), "");
            if (!string.IsNullOrEmpty (saved)) return saved;
            string resolved = resolve != null ? resolve (prefab) : null;
            if (!string.IsNullOrEmpty (resolved)) return resolved.TrimEnd ('/');
            return Path.GetDirectoryName (prefabPath).Replace ('\\', '/') + "/" + defaultName;
        }

        internal static void SetFolder (GameObject prefab, string prefsKey, string folder)
        {
            string prefabPath = prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            if (string.IsNullOrEmpty (prefabPath)) return;
            string key = prefsKey + AssetDatabase.AssetPathToGUID (prefabPath);
            if (string.IsNullOrEmpty (folder)) EditorPrefs.DeleteKey (key);
            else EditorPrefs.SetString (key, folder.TrimEnd ('/'));
        }

        /// <summary>
        /// フォルダの下（サブフォルダも）の編集用クリップ。組（サブフォルダ）→ 名前の順
        /// </summary>
        public static List<Entry> List (string folder)
        {
            List<Entry> result = new List<Entry> ();
            if (string.IsNullOrEmpty (folder) || !AssetDatabase.IsValidFolder (folder)) return result;
            foreach (string guid in AssetDatabase.FindAssets ("t:AnimationClip", new[] { folder })) {
                string path = AssetDatabase.GUIDToAssetPath (guid);
                if (!EditingClip.IsEditingClipPath (path)) continue;
                string directory = Path.GetDirectoryName (path).Replace ('\\', '/');
                result.Add (new Entry {
                    path = path,
                    name = BaseName (path),
                    group = directory.Length > folder.Length ? directory.Substring (folder.Length + 1) : "",
                });
            }
            result.Sort ((a, b) => {
                int group = string.CompareOrdinal (a.group, b.group);
                return group != 0 ? group : EditorUtility.NaturalCompare (a.name, b.name);
            });
            return result;
        }

        /// <summary>
        /// 表示名（ファイル名から .rig.anim を除いたもの）
        /// </summary>
        public static string BaseName (string path)
        {
            string name = Path.GetFileNameWithoutExtension (path);
            return name.EndsWith (EditingClip.kSuffix, System.StringComparison.OrdinalIgnoreCase)
                ? name.Substring (0, name.Length - EditingClip.kSuffix.Length)
                : name;
        }

        public static string MakePath (string folder, string baseName)
        {
            return folder + "/" + baseName + EditingClip.kSuffix + EditingClip.kExtension;
        }

        /// <summary>
        /// 使われていない名前。末尾に番号があればそれを上げ（桁数は保つ）、無ければ " 1" から付ける
        /// </summary>
        public static string NextName (string baseName, System.Func<string, bool> exists)
        {
            if (!exists (baseName)) return baseName;
            Match match = kTrailingNumber.Match (baseName);
            string head = match.Success ? match.Groups[1].Value : baseName + " ";
            int number = match.Success ? int.Parse (match.Groups[2].Value) : 0;
            int width = match.Success ? match.Groups[2].Value.Length : 1;
            for (int i = number + 1; i < number + 10000; i++) {
                string candidate = head + i.ToString ().PadLeft (width, '0');
                if (!exists (candidate)) return candidate;
            }
            return head + System.Guid.NewGuid ().ToString ("N").Substring (0, 6);
        }

        static bool Exists (string folder, string baseName)
        {
            return AssetDatabase.LoadMainAssetAtPath (MakePath (folder, baseName)) != null || File.Exists (MakePath (folder, baseName));
        }

        /// <summary>
        /// 新しいクリップ（60fps・空）を作る。フォルダが無ければ作る
        /// </summary>
        public static AnimationClip Create (string folder, string baseName = "New Motion")
        {
            EnsureFolder (folder);
            string name = NextName (baseName, n => Exists (folder, n));
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, MakePath (folder, name));
            return clip;
        }

        /// <summary>
        /// 同じフォルダに複製する。名前は末尾の番号を上げたもの（Attack 01 → Attack 02）
        /// </summary>
        public static AnimationClip Duplicate (AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath (clip);
            if (string.IsNullOrEmpty (path)) return null;
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            string name = NextName (BaseName (path), n => Exists (folder, n));
            string destination = MakePath (folder, name);
            return AssetDatabase.CopyAsset (path, destination) ? AssetDatabase.LoadAssetAtPath<AnimationClip> (destination) : null;
        }

        /// <summary>
        /// 名前を変える（.rig は付け直す）。焼いた版がこのクリップから作られたものなら、その名前も合わせる。
        /// できなければ理由を返す（できたら null）
        /// </summary>
        public static string Rename (AnimationClip clip, string newBaseName)
        {
            string path = AssetDatabase.GetAssetPath (clip);
            if (string.IsNullOrEmpty (path)) return "アセットでないクリップ";
            newBaseName = newBaseName != null ? newBaseName.Trim () : "";
            if (newBaseName.EndsWith (EditingClip.kSuffix, System.StringComparison.OrdinalIgnoreCase)) {
                newBaseName = newBaseName.Substring (0, newBaseName.Length - EditingClip.kSuffix.Length);
            }
            if (newBaseName.Length == 0) return "名前が空";
            if (newBaseName.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0) return "ファイル名に使えない文字がある";
            if (newBaseName == BaseName (path)) return null;
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            if (Exists (folder, newBaseName)) return "同じ名前のクリップがある";

            // 焼いた版の置き場所は名前から決まるので、先に対応を調べておく
            string oldOutput = HumanoidOutput.GetOutputPath (path);
            HumanoidOutput.Link link = AssetDatabase.LoadMainAssetAtPath (oldOutput) != null ? HumanoidOutput.ReadLink (oldOutput) : null;
            bool ownsOutput = link != null && link.source == AssetDatabase.AssetPathToGUID (path);

            string error = AssetDatabase.RenameAsset (path, newBaseName + EditingClip.kSuffix);
            if (!string.IsNullOrEmpty (error)) return error;
            if (ownsOutput) {
                string newOutput = HumanoidOutput.GetOutputPath (MakePath (folder, newBaseName));
                if (AssetDatabase.LoadMainAssetAtPath (newOutput) == null) {
                    AssetDatabase.RenameAsset (oldOutput, Path.GetFileNameWithoutExtension (newOutput));
                }
            }
            return null;
        }

        /// <summary>
        /// ゴミ箱へ移す（OS のゴミ箱から戻せる）。焼いた版は残す
        /// </summary>
        public static bool Delete (AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath (clip);
            return !string.IsNullOrEmpty (path) && AssetDatabase.MoveAssetToTrash (path);
        }

        internal static void EnsureFolder (string folder)
        {
            if (string.IsNullOrEmpty (folder) || AssetDatabase.IsValidFolder (folder)) return;
            string parent = Path.GetDirectoryName (folder).Replace ('\\', '/');
            EnsureFolder (parent);
            AssetDatabase.CreateFolder (parent, Path.GetFileName (folder));
        }
    }

}
