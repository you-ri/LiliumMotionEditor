using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// クリップを参照しているアセットを探して、別のクリップへ差し替える（S14 の段取り 4）。
    /// 「元＋Override」を焼いた `<元>.edited.anim` をゲームへ出すときに、技や Timeline の参照を差し替えるのに使う。
    ///
    /// - 探すのは Unity の Search の逆参照（`ref:`）。プロジェクト全体を総当たりしない。
    /// - 差し替えるのは prefab・Timeline（.playable）・ScriptableObject（.asset）・AnimatorController など**アセットだけ**。
    ///   シーン（.unity）は開いていないと書けないので、見つけても触らずに一覧へ出す。
    /// - アセットの書き換えは Undo で戻せないので、呼ぶ側が先に確認を取る
    /// </summary>
    public static class ClipReferences
    {
        /// <summary>差し替えの下調べ</summary>
        public sealed class Plan
        {
            /// <summary>差し替えられるアセットのパス</summary>
            public readonly List<string> assets = new List<string> ();
            /// <summary>触らないもの（シーンなど）とその理由</summary>
            public readonly List<string> skipped = new List<string> ();
        }

        /// <summary>
        /// そのクリップを参照しているアセットを探す
        /// </summary>
        public static Plan Find (AnimationClip clip)
        {
            Plan plan = new Plan ();
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            if (string.IsNullOrEmpty (path)) return plan;

            HashSet<string> seen = new HashSet<string> ();
            using (UnityEditor.Search.SearchContext context = UnityEditor.Search.SearchService.CreateContext ("asset", "ref:\"" + path + "\"")) {
                foreach (UnityEditor.Search.SearchItem item in UnityEditor.Search.SearchService.Request (context, UnityEditor.Search.SearchFlags.Synchronous)) {
                    Object asset = item.ToObject ();
                    string found = asset != null ? AssetDatabase.GetAssetPath (asset) : null;
                    if (string.IsNullOrEmpty (found) || found == path || !seen.Add (found)) continue;

                    string extension = Path.GetExtension (found).ToLowerInvariant ();
                    if (extension == ".unity") {
                        plan.skipped.Add (Tr ("CLIP_REFERENCES_SKIPPED_SCENE", found));
                        continue;
                    }
                    if (extension == ".fbx" || extension == ".prefab" || extension == ".asset" || extension == ".playable" || extension == ".controller" || extension == ".overridecontroller") {
                        if (extension == ".fbx") {
                            plan.skipped.Add (Tr ("CLIP_REFERENCES_SKIPPED_IMPORTED", found));
                            continue;
                        }
                        plan.assets.Add (found);
                        continue;
                    }
                    plan.skipped.Add (Tr ("CLIP_REFERENCES_SKIPPED_EXTENSION", found, extension));
                }
            }
            plan.assets.Sort ();
            plan.skipped.Sort ();
            return plan;
        }

        /// <summary>
        /// 一覧のアセットの中の from の参照を to へ差し替える。書き換えたアセットの数を返す。
        /// prefab は中身を開いて差し替え、それ以外はアセットの中の物を順に見る
        /// </summary>
        public static int Replace (AnimationClip from, AnimationClip to, IList<string> assets, List<string> changed)
        {
            if (from == null || to == null || assets == null) return 0;
            int count = 0;
            for (int i = 0; i < assets.Count; i++) {
                string path = assets[i];
                EditorUtility.DisplayProgressBar ("Motion Editor", Tr ("CLIP_REFERENCES_REPLACING", path), (i + 1) / (float)assets.Count);
                try {
                    bool touched = Path.GetExtension (path).ToLowerInvariant () == ".prefab"
                        ? ReplaceInPrefab (from, to, path)
                        : ReplaceInAsset (from, to, path);
                    if (!touched) continue;
                    count++;
                    if (changed != null) changed.Add (path);
                }
                catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            EditorUtility.ClearProgressBar ();
            return count;
        }

        static bool ReplaceInPrefab (AnimationClip from, AnimationClip to, string path)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents (path);
            if (contents == null) return false;
            try {
                bool touched = false;
                foreach (Component component in contents.GetComponentsInChildren<Component> (true)) {
                    if (component == null) continue;
                    touched |= ReplaceInObject (from, to, component);
                }
                if (touched) PrefabUtility.SaveAsPrefabAsset (contents, path);
                return touched;
            }
            finally {
                PrefabUtility.UnloadPrefabContents (contents);
            }
        }

        static bool ReplaceInAsset (AnimationClip from, AnimationClip to, string path)
        {
            bool touched = false;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath (path)) {
                if (asset == null || asset == from) continue;
                touched |= ReplaceInObject (from, to, asset);
            }
            if (touched) AssetDatabase.SaveAssetIfDirty (AssetDatabase.GUIDFromAssetPath (path));
            return touched;
        }

        static bool ReplaceInObject (AnimationClip from, AnimationClip to, Object target)
        {
            SerializedObject serialized = new SerializedObject (target);
            SerializedProperty property = serialized.GetIterator ();
            bool touched = false;
            while (property.Next (true)) {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (property.objectReferenceValue != from) continue;
                property.objectReferenceValue = to;
                touched = true;
            }
            if (touched) {
                serialized.ApplyModifiedPropertiesWithoutUndo ();
                EditorUtility.SetDirty (target);
            }
            return touched;
        }
    }

}
