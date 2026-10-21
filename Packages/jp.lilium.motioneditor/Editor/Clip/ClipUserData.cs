using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップ・Override のクリップの .meta の userData（Stacker の設定・サイクルキーの印・Override の段の印）。
    /// AnimationClip は利用者のデータを持てないため .meta に持つ。
    /// どれか 1 つを保存するときも、ほかの項目は読んだまま残す（項目ごとに上書きすると、もう片方が消える。
    /// 前は Override の印を別に書いていて、Override のクリップにサイクルキーの印を付けると Override の印が消えていた）。
    /// Stacker の前の Loop（stacker.loop）は、読むときにサイクルキーの印へ移す（Loop のトグルはサイクルキーの付け外しになった。S32）
    /// </summary>
    [System.Serializable]
    sealed class ClipUserData
    {
        public Stacker.Settings stacker;
        public bool cycle;
        /// <summary>Override の段の印（どの元に重ねるか。OverrideFiles）。印が無ければ baseClip が空</summary>
        public OverrideFiles.Settings overrideLayer;

        public static ClipUserData Load (AnimationClip clip)
        {
            return Load (GetPath (clip));
        }

        public static ClipUserData Load (string path)
        {
            AssetImporter importer = string.IsNullOrEmpty (path) ? null : AssetImporter.GetAtPath (path);
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
            return Save (GetPath (clip), change);
        }

        public static bool Save (string path, System.Action<ClipUserData> change)
        {
            AssetImporter importer = string.IsNullOrEmpty (path) ? null : AssetImporter.GetAtPath (path);
            if (importer == null) return false;
            ClipUserData data = Load (path);
            change (data);
            string json = JsonUtility.ToJson (data);
            if (importer.userData == json) return true;
            importer.userData = json;
            AssetDatabase.WriteImportSettingsIfDirty (path);
            return true;
        }

        static string GetPath (AnimationClip clip)
        {
            return clip != null ? AssetDatabase.GetAssetPath (clip) : null;
        }
    }

}
