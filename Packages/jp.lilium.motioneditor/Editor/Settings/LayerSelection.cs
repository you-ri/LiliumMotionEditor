using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Layers の ✏（書き出す段）と ✋（つかむ段）の選択。キャラ（prefab の GUID）ごとに、この PC の設定（EditorPrefs）へ持つ。
    /// 作業の途中で切り替えるものなので、キャラの設定（バージョン管理するアセット）に書くと、切り替えるたびにアセットが変わってしまう。
    /// この PC にまだ無いときは、キャラの設定（無ければプロジェクト設定）の Layers にある選択を初めの値にする（前はそこに保存していた）
    /// </summary>
    static class LayerSelection
    {
        const string kPrefsKey = "LiliumMotionEditor.Layers.Selection.";

        [System.Serializable]
        sealed class Saved
        {
            public string write;
            public string manipulate;
        }

        /// <summary>
        /// prefab の選択を読む。この PC に無ければ fallback（キャラの設定・プロジェクト設定の Layers）の選択
        /// </summary>
        public static void Load (GameObject prefab, LayerState fallback, out string write, out string manipulate)
        {
            write = fallback != null ? fallback.write : null;
            manipulate = fallback != null ? fallback.manipulate : null;
            string key = Key (prefab);
            if (key == null) return;
            string json = EditorPrefs.GetString (key, "");
            if (string.IsNullOrEmpty (json)) return;
            Saved saved;
            try {
                saved = JsonUtility.FromJson<Saved> (json);
            } catch (System.ArgumentException) {
                return;
            }
            if (saved == null) return;
            write = saved.write;
            manipulate = saved.manipulate;
        }

        public static void Save (GameObject prefab, string write, string manipulate)
        {
            string key = Key (prefab);
            if (key == null) return;
            EditorPrefs.SetString (key, JsonUtility.ToJson (new Saved { write = write, manipulate = manipulate }));
        }

        /// <summary>テストの後始末用</summary>
        internal static void Delete (GameObject prefab)
        {
            string key = Key (prefab);
            if (key != null) EditorPrefs.DeleteKey (key);
        }

        static string Key (GameObject prefab)
        {
            string path = prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            if (string.IsNullOrEmpty (path)) return null;
            string guid = AssetDatabase.AssetPathToGUID (path);
            return string.IsNullOrEmpty (guid) ? null : kPrefsKey + guid;
        }
    }

}
