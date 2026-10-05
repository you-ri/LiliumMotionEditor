using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップの .meta の userData（Stacker の設定・サイクルキーの印）。AnimationClip は利用者のデータを持てないため .meta に持つ。
    /// どれか 1 つを保存するときも、ほかの項目は読んだまま残す（項目ごとに上書きすると、もう片方が消える）。
    /// Stacker の前の Loop（stacker.loop）は、読むときにサイクルキーの印へ移す（Loop のトグルはサイクルキーの付け外しになった。S32）
    /// </summary>
    [System.Serializable]
    sealed class ClipUserData
    {
        public Stacker.Settings stacker;
        public bool cycle;

        public static ClipUserData Load (AnimationClip clip)
        {
            AssetImporter importer = GetImporter (clip);
            if (importer == null || string.IsNullOrEmpty (importer.userData)) return new ClipUserData ();
            ClipUserData data;
            try {
                data = JsonUtility.FromJson<ClipUserData> (importer.userData) ?? new ClipUserData ();
            }
            catch (System.ArgumentException) {
                return new ClipUserData ();
            }
            if (data.stacker != null && data.stacker.loop) {
                data.cycle = true;
                data.stacker.loop = false;
            }
            return data;
        }

        /// <summary>
        /// 今の中身を読み、change で直して保存する。アセットでないクリップには保存しない（false）
        /// </summary>
        public static bool Save (AnimationClip clip, System.Action<ClipUserData> change)
        {
            AssetImporter importer = GetImporter (clip);
            if (importer == null) return false;
            ClipUserData data = Load (clip);
            change (data);
            string json = JsonUtility.ToJson (data);
            if (importer.userData == json) return true;
            importer.userData = json;
            AssetDatabase.WriteImportSettingsIfDirty (importer.assetPath);
            return true;
        }

        static AssetImporter GetImporter (AnimationClip clip)
        {
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            return string.IsNullOrEmpty (path) ? null : AssetImporter.GetAtPath (path);
        }
    }

}
