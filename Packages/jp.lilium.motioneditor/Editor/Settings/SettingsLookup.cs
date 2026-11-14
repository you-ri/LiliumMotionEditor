using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 設定の引き当て（S19・S108）。キャラへの結び付け（使う定義・Layers）はキャラの設定、使い方（フォルダ・Auto Bake）はプロジェクト設定 → パッケージの既定値
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

        /// <summary>
        /// この prefab のキャラの設定（無ければ null）。同じ prefab を指すものが 2 つ以上あれば、パスの順で先のものを使い、警告を出す
        /// </summary>
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
                if (settings.hasObsoleteOverrides && warned_.Add ("obsolete:" + path)) {
                    Debug.LogWarning (Tr ("SETTINGS_LOOKUP_OBSOLETE_OVERRIDES", path), settings);
                }
                CharacterSettings first;
                if (byPrefab_.TryGetValue (prefab, out first)) {
                    if (warned_.Add (path)) {
                        Debug.LogWarning (Tr ("SETTINGS_LOOKUP_DUPLICATE_CHARACTER_SETTINGS", settings.prefab.name, AssetDatabase.GetAssetPath (first), path), settings);
                    }
                    continue;
                }
                byPrefab_.Add (prefab, settings);
            }
        }

        internal static string PrefabGuid (GameObject prefab)
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
            return ResolveFolders (prefab, true, ProjectSettings.instance);
        }

        /// <summary>PoseBank のフォルダの並び（先頭が作る場所。2 つ目からは実在するものだけ）</summary>
        public static List<string> GetPoseBankFolders (GameObject prefab)
        {
            return ResolveFolders (prefab, false, ProjectSettings.instance);
        }

        /// <summary>
        /// フォルダの並び。プロジェクト設定の並び（空ならパッケージの既定）で、この PC で作る場所を選び直していれば先頭だけそれに替える。
        /// 選び直しはキャラの設定へ書かない（S108。チームで共有する決まりはプロジェクト設定の {name} で書く）
        /// </summary>
        internal static List<string> ResolveFolders (GameObject prefab, bool animBank, ProjectSettings project)
        {
            List<string> result = new List<string> ();
            if (prefab == null) return result;
            List<string> patterns = project != null ? (animBank ? project.animBankFolders : project.poseBankFolders) : null;
            if (patterns == null || patterns.Count == 0) {
                // 指定が無ければ、パッケージの既定（この PC で選んだもの → 拡張点 → prefab のフォルダの下）
                string legacy = animBank ? AnimBank.GetDefaultFolder (prefab) : PoseBank.GetDefaultFolder (prefab);
                if (!string.IsNullOrEmpty (legacy)) result.Add (legacy);
                return result;
            }
            // この PC で選び直した場所は、並びの先頭（作る場所）の代わりになる
            string saved = animBank ? AnimBank.GetSavedFolder (prefab) : PoseBank.GetSavedFolder (prefab);
            if (saved != null) result.Add (saved);
            foreach (string pattern in patterns) {
                string folder = Expand (pattern, prefab, project);
                if (string.IsNullOrEmpty (folder) || result.Contains (folder)) continue;
                // 先頭は作る場所なので、まだ無くても入れる（作るときにフォルダを作る）。選び直した場所が先頭なら、本来の先頭も実在するときだけ並べる
                if (result.Count > 0 && !AssetDatabase.IsValidFolder (folder)) continue;
                result.Add (folder);
            }
            return result;
        }

        /// <summary>
        /// キャラの設定を作る。置き場所はプロジェクト設定（characterSettingsFolder）。
        /// 今の値（Layers・編集用リグの定義）を取り込む。プロジェクト設定と同じ定義は上書きにしない
        /// </summary>
        public static CharacterSettings Create (GameObject prefab, LayerState layers, EditRigDefinition rigDefinition)
        {
            if (prefab == null || !EditorUtility.IsPersistent (prefab)) return null;
            CharacterSettings existing = Find (prefab);
            if (existing != null) return existing;

            ProjectSettings project = ProjectSettings.instance;
            string folder = Expand (project.characterSettingsFolder, prefab, project);
            if (string.IsNullOrEmpty (folder) || !folder.StartsWith ("Assets")) folder = Expand (kPrefabFolderToken, prefab, project);
            AnimBank.EnsureFolder (folder);

            CharacterSettings settings = ScriptableObject.CreateInstance<CharacterSettings> ();
            settings.prefab = prefab;
            settings.layers = layers != null ? Clone (layers) : Clone (project.layers);
            settings.rigDefinition = rigDefinition;
            settings.overrideRigDefinition = rigDefinition != project.rigDefinition;

            string path = AssetDatabase.GenerateUniqueAssetPath (folder + "/" + CharacterName (prefab, project) + kAssetSuffix);
            AssetDatabase.CreateAsset (settings, path);
            AssetDatabase.SaveAssetIfDirty (settings);
            Invalidate ();
            return settings;
        }

        /// <summary>S108 より前の形。Auto Bake はプロジェクト設定だけが持つので、autoBake は使わない</summary>
        [System.Obsolete ("S108: Auto Bake comes from the project settings only. Use Create (prefab, layers, rigDefinition)")]
        public static CharacterSettings Create (GameObject prefab, LayerState layers, EditRigDefinition rigDefinition, bool autoBake)
        {
            return Create (prefab, layers, rigDefinition);
        }

        internal static LayerState Clone (LayerState state)
        {
            return state != null ? JsonUtility.FromJson<LayerState> (JsonUtility.ToJson (state)) : new LayerState ();
        }
    }

}
