using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 設定の引き当て（S19）。キャラの設定（上書きした項目だけ）→ プロジェクト設定 → パッケージの既定値 の順
    /// </summary>
    public static class SettingsLookup
    {
        public const string kNameToken = "{name}";
        public const string kPrefabFolderToken = "{prefabFolder}";
        const string kAssetSuffix = " Motion Editor.asset";

        static Dictionary<string, CharacterSettings> byPrefab_;
        static readonly HashSet<string> warned_ = new HashSet<string> ();

        [InitializeOnLoadMethod]
        static void Register ()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.projectChanged += Invalidate;
        }

        /// <summary>キャラの設定を探し直す（プロジェクトのファイルが変わったとき）</summary>
        public static void Invalidate ()
        {
            byPrefab_ = null;
        }

        /// <summary>
        /// この prefab のキャラの設定（無ければ null）。同じ prefab を指すものが 2 つ以上あれば、パスの順で先のものを使い、警告を出す
        /// </summary>
        /// <summary>
        /// 設定をこのキャラ（prefab）に結び付ける。同じキャラを指していたほかの設定は、キャラを外す（1 キャラ 1 設定）
        /// </summary>
        public static void Assign (CharacterSettings settings, GameObject prefab)
        {
            if (settings == null || prefab == null) return;
            string guid = PrefabGuid (prefab);
            foreach (string found in AssetDatabase.FindAssets ("t:" + typeof (CharacterSettings).Name)) {
                CharacterSettings other = AssetDatabase.LoadAssetAtPath<CharacterSettings> (AssetDatabase.GUIDToAssetPath (found));
                if (other == null || other == settings || other.prefab == null || PrefabGuid (other.prefab) != guid) continue;
                Undo.RecordObject (other, "Motion Editor Character Settings");
                other.prefab = null;
                EditorUtility.SetDirty (other);
                AssetDatabase.SaveAssetIfDirty (other);
            }
            Undo.RecordObject (settings, "Motion Editor Character Settings");
            settings.prefab = prefab;
            EditorUtility.SetDirty (settings);
            AssetDatabase.SaveAssetIfDirty (settings);
            Invalidate ();
        }

        public static CharacterSettings Find (GameObject prefab)
        {
            string guid = PrefabGuid (prefab);
            if (guid == null) return null;
            if (byPrefab_ == null) Collect ();
            CharacterSettings settings;
            return byPrefab_.TryGetValue (guid, out settings) && settings != null ? settings : null;
        }

        static void Collect ()
        {
            byPrefab_ = new Dictionary<string, CharacterSettings> ();
            List<string> paths = new List<string> ();
            foreach (string guid in AssetDatabase.FindAssets ("t:" + typeof (CharacterSettings).Name)) paths.Add (AssetDatabase.GUIDToAssetPath (guid));
            paths.Sort (System.StringComparer.Ordinal);
            foreach (string path in paths) {
                CharacterSettings settings = AssetDatabase.LoadAssetAtPath<CharacterSettings> (path);
                string prefab = settings != null ? PrefabGuid (settings.prefab) : null;
                if (prefab == null) continue;
                CharacterSettings first;
                if (byPrefab_.TryGetValue (prefab, out first)) {
                    if (warned_.Add (path)) {
                        Debug.LogWarning ("MotionEditor: 同じキャラ（" + settings.prefab.name + "）の設定が 2 つある。" + AssetDatabase.GetAssetPath (first) + " を使い、" + path + " は使わない", settings);
                    }
                    continue;
                }
                byPrefab_.Add (prefab, settings);
            }
        }

        static string PrefabGuid (GameObject prefab)
        {
            string path = prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            return string.IsNullOrEmpty (path) ? null : AssetDatabase.AssetPathToGUID (path);
        }

        /// <summary>{name} に入る名前（prefab の名前から、プロジェクト設定の接尾辞を除いたもの）</summary>
        public static string CharacterName (GameObject prefab, ProjectSettings project)
        {
            if (prefab == null) return null;
            string name = prefab.name;
            if (project != null) {
                foreach (string suffix in project.nameSuffixes) {
                    if (!string.IsNullOrEmpty (suffix) && name.Length > suffix.Length && name.EndsWith (suffix, System.StringComparison.Ordinal)) {
                        return name.Substring (0, name.Length - suffix.Length);
                    }
                }
            }
            return name;
        }

        /// <summary>置き換え文字列（{name}・{prefabFolder}）を入れたパス。prefab が無ければ null</summary>
        public static string Expand (string pattern, GameObject prefab, ProjectSettings project)
        {
            if (string.IsNullOrEmpty (pattern) || prefab == null) return null;
            string prefabPath = AssetDatabase.GetAssetPath (prefab);
            string prefabFolder = string.IsNullOrEmpty (prefabPath) ? "Assets" : Path.GetDirectoryName (prefabPath).Replace ('\\', '/');
            return pattern
                .Replace (kNameToken, CharacterName (prefab, project))
                .Replace (kPrefabFolderToken, prefabFolder)
                .Replace ('\\', '/')
                .TrimEnd ('/');
        }

        /// <summary>AnimBank のフォルダの並び（先頭が作る場所。2 つ目からは実在するものだけ）</summary>
        public static List<string> GetAnimBankFolders (GameObject prefab)
        {
            return ResolveFolders (prefab, true, ProjectSettings.instance, Find (prefab));
        }

        /// <summary>PoseBank のフォルダの並び（先頭が作る場所。2 つ目からは実在するものだけ）</summary>
        public static List<string> GetPoseBankFolders (GameObject prefab)
        {
            return ResolveFolders (prefab, false, ProjectSettings.instance, Find (prefab));
        }

        internal static List<string> ResolveFolders (GameObject prefab, bool animBank, ProjectSettings project, CharacterSettings character)
        {
            List<string> result = new List<string> ();
            if (prefab == null) return result;
            List<string> patterns = null;
            if (character != null && (animBank ? character.overrideAnimBankFolders : character.overridePoseBankFolders)) {
                patterns = animBank ? character.animBankFolders : character.poseBankFolders;
            }
            else if (project != null) {
                patterns = animBank ? project.animBankFolders : project.poseBankFolders;
            }
            if (patterns == null || patterns.Count == 0) {
                // 指定が無ければ、パッケージの既定（拡張点 → prefab のフォルダの下）
                string legacy = animBank ? AnimBank.GetDefaultFolder (prefab) : PoseBank.GetDefaultFolder (prefab);
                if (!string.IsNullOrEmpty (legacy)) result.Add (legacy);
                return result;
            }
            foreach (string pattern in patterns) {
                string folder = Expand (pattern, prefab, project);
                if (string.IsNullOrEmpty (folder) || result.Contains (folder)) continue;
                // 先頭は作る場所なので、まだ無くても入れる（作るときにフォルダを作る）
                if (result.Count > 0 && !AssetDatabase.IsValidFolder (folder)) continue;
                result.Add (folder);
            }
            return result;
        }

        /// <summary>
        /// キャラの設定を作る。置き場所はプロジェクト設定（characterSettingsFolder）。
        /// 今の値（Layers・編集用リグの定義・Auto）と、EditorPrefs に残っている旧いフォルダの選択を取り込む。プロジェクト設定と同じ値は上書きにしない
        /// </summary>
        public static CharacterSettings Create (GameObject prefab, LayerState layers, EditRigDefinition rigDefinition, bool autoBake)
        {
            if (prefab == null || !EditorUtility.IsPersistent (prefab)) return null;
            CharacterSettings existing = Find (prefab);
            if (existing != null) return existing;

            ProjectSettings project = ProjectSettings.instance;
            string folder = Expand (project.characterSettingsFolder, prefab, project);
            if (string.IsNullOrEmpty (folder) || !folder.StartsWith ("Assets")) folder = Expand (kPrefabFolderToken, prefab, project);
            EnsureFolder (folder);

            CharacterSettings settings = ScriptableObject.CreateInstance<CharacterSettings> ();
            settings.prefab = prefab;
            settings.layers = layers != null ? Clone (layers) : Clone (project.layers);
            string animBank = AnimBank.GetSavedFolder (prefab);
            if (!string.IsNullOrEmpty (animBank)) {
                settings.overrideAnimBankFolders = true;
                settings.animBankFolders.Add (animBank);
            }
            string poseBank = PoseBank.GetSavedFolder (prefab);
            if (!string.IsNullOrEmpty (poseBank)) {
                settings.overridePoseBankFolders = true;
                settings.poseBankFolders.Add (poseBank);
            }
            settings.rigDefinition = rigDefinition;
            settings.overrideRigDefinition = rigDefinition != project.rigDefinition;
            settings.autoBake = autoBake;
            settings.overrideAutoBake = autoBake != project.autoBake;

            string path = AssetDatabase.GenerateUniqueAssetPath (folder + "/" + CharacterName (prefab, project) + kAssetSuffix);
            AssetDatabase.CreateAsset (settings, path);
            AssetDatabase.SaveAssetIfDirty (settings);
            Invalidate ();
            return settings;
        }

        internal static LayerState Clone (LayerState state)
        {
            return state != null ? JsonUtility.FromJson<LayerState> (JsonUtility.ToJson (state)) : new LayerState ();
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
