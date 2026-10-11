using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// リグ定義のアセットの扱い（S45b）。キャラが使う定義の引き当て、直せるかの判定、複製して割り当てる、その定義を使うキャラの逆引き
    /// </summary>
    static class RigDefinitionAssets
    {
        /// <summary>複製した定義をどこに使うか</summary>
        public enum AssignTarget
        {
            /// <summary>窓に出しているキャラの設定（無ければ作る）</summary>
            Character,
            /// <summary>プロジェクト設定（キャラの設定で上書きしていないキャラ全部）</summary>
            Project,
            /// <summary>どこにも入れない（直すだけ）</summary>
            None,
        }

        /// <summary>定義を使っているキャラ 1 体</summary>
        public struct User
        {
            public CharacterSettings settings;
            public GameObject prefab;
            /// <summary>キャラの設定で上書きせず、プロジェクト設定（またはパッケージの既定）の定義を使っている</summary>
            public bool viaProject;
        }

        const string kAssetSuffix = " Rig Definition.asset";

        /// <summary>
        /// キャラが使う定義。キャラの設定で上書きしていればその値、それ以外（設定が無い・上書きの欄が空も）はプロジェクト設定の値（空ならパッケージの既定）。
        /// モーションエディタとリグエディタで同じ引き当てにする
        /// </summary>
        public static EditRigDefinition Resolve (CharacterSettings settings)
        {
            if (settings != null && settings.overrideRigDefinition && settings.rigDefinition != null) return settings.rigDefinition;
            return ProjectSettings.instance.defaultRigDefinition;
        }

        /// <summary>
        /// ここでは直せない理由（直せるなら null）。パッケージの既定はパッケージを更新すると戻り、git から入れたパッケージは書き換えもできない
        /// </summary>
        public static string GetReadOnlyReason (EditRigDefinition definition)
        {
            if (definition == null || !EditorUtility.IsPersistent (definition)) return Tr ("RIG_EDITOR_READ_ONLY_TEMPORARY");
            if (definition == EditRigDefinition.packageDefault) return Tr ("RIG_EDITOR_READ_ONLY_PACKAGE_DEFAULT");
            if (AssetDatabase.GetAssetPath (definition).StartsWith ("Packages/")) return Tr ("RIG_EDITOR_READ_ONLY_PACKAGE");
            return null;
        }

        /// <summary>
        /// source の中身を持った定義のアセットを作り、target に割り当てる（source が null なら既定の中身）。
        /// 置き場所はキャラの設定と同じフォルダ（無ければ prefab のフォルダ、それも無ければ Assets）、名前は「キャラ名 Rig Definition」
        /// </summary>
        public static EditRigDefinition CreateCopy (EditRigDefinition source, GameObject prefab, AssignTarget target)
        {
            ProjectSettings project = ProjectSettings.instance;
            CharacterSettings settings = prefab != null ? SettingsLookup.Find (prefab) : null;
            string folder = "Assets";
            string near = settings != null ? AssetDatabase.GetAssetPath (settings) : prefab != null ? AssetDatabase.GetAssetPath (prefab) : null;
            if (!string.IsNullOrEmpty (near) && near.StartsWith ("Assets")) folder = Path.GetDirectoryName (near).Replace ('\\', '/');
            string name = prefab != null ? SettingsLookup.CharacterName (prefab, project) : "New";

            EditRigDefinition copy = ScriptableObject.CreateInstance<EditRigDefinition> ();
            if (source != null) copy.CopyFrom (source);
            else copy.SetDefault ();
            string path = AssetDatabase.GenerateUniqueAssetPath (folder + "/" + name + kAssetSuffix);
            AssetDatabase.CreateAsset (copy, path);
            // アセットを作ったことは Undo に載せない（Create のメニューと同じ）。載せると Undo で物だけ消えてファイルが残る
            AssetDatabase.SaveAssetIfDirty (copy);

            if (target == AssignTarget.Character && prefab != null && EditorUtility.IsPersistent (prefab)) {
                if (settings == null) {
                    // キャラの設定が無ければ作る（作るときに、プロジェクト設定と違う定義は上書きとして入る）
                    SettingsLookup.Create (prefab, null, copy, project.autoBake);
                }
                else {
                    Undo.RecordObject (settings, "Assign Rig Definition");
                    settings.overrideRigDefinition = true;
                    settings.rigDefinition = copy;
                    EditorUtility.SetDirty (settings);
                    AssetDatabase.SaveAssetIfDirty (settings);
                }
            }
            else if (target == AssignTarget.Project) {
                Undo.RecordObject (project, "Assign Rig Definition");
                project.rigDefinition = copy;
                project.Save ();
            }
            return copy;
        }

        /// <summary>
        /// その定義を使っているキャラ（キャラの設定があるものだけ。キャラの設定の無いキャラは、プロジェクト設定の定義を使う）。
        /// キャラの設定で上書きしているものが先
        /// </summary>
        public static List<User> FindUsers (EditRigDefinition definition)
        {
            List<User> users = new List<User> ();
            if (definition == null) return users;
            List<string> paths = new List<string> ();
            foreach (string guid in AssetDatabase.FindAssets ("t:" + typeof (CharacterSettings).Name)) paths.Add (AssetDatabase.GUIDToAssetPath (guid));
            paths.Sort (System.StringComparer.Ordinal);
            foreach (string path in paths) {
                CharacterSettings settings = AssetDatabase.LoadAssetAtPath<CharacterSettings> (path);
                if (settings == null || settings.prefab == null || Resolve (settings) != definition) continue;
                users.Add (new User {
                    settings = settings,
                    prefab = settings.prefab,
                    viaProject = !(settings.overrideRigDefinition && settings.rigDefinition != null),
                });
            }
            users.Sort ((a, b) => a.viaProject.CompareTo (b.viaProject));
            return users;
        }
    }

}
