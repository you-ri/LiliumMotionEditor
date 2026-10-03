using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// クリップが保存されたら、それを開いている窓に Humanoid 版を焼き直させる。
    /// 保存の最中にほかのアセットを作ると保存と絡むので、保存が終わってから（delayCall）行う
    /// </summary>
    sealed class HumanoidSaveWatcher : AssetModificationProcessor
    {
        static readonly HashSet<string> pending_ = new HashSet<string> ();

        static string[] OnWillSaveAssets (string[] paths)
        {
            bool added = false;
            foreach (string path in paths) {
                if (!path.EndsWith (EditingClip.kExtension, System.StringComparison.OrdinalIgnoreCase)) continue;
                added |= pending_.Add (path);
            }
            if (added) {
                EditorApplication.delayCall -= Flush;
                EditorApplication.delayCall += Flush;
            }
            return paths;
        }

        static void Flush ()
        {
            if (pending_.Count == 0) return;
            HashSet<string> saved = new HashSet<string> (pending_);
            pending_.Clear ();
            foreach (PreviewWindow window in Resources.FindObjectsOfTypeAll<PreviewWindow> ()) {
                window.OnClipsSaved (saved);
            }
        }
    }

}
