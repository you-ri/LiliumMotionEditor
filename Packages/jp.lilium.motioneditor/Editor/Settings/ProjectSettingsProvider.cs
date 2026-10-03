using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Project Settings の「Lilium Motion Editor」のページ（S19）
    /// </summary>
    static class ProjectSettingsProvider
    {
        [SettingsProvider]
        static SettingsProvider Create ()
        {
            SerializedObject serialized = null;
            return new SettingsProvider ("Project/Lilium Motion Editor", SettingsScope.Project) {
                label = "Lilium Motion Editor",
                keywords = new HashSet<string> (new[] { "Motion", "AnimBank", "PoseBank", "Layers", "Rig" }),
                guiHandler = search => {
                    ProjectSettings settings = ProjectSettings.instance;
                    // ScriptableSingleton は編集できない印が付いているので外す（外さないと欄が灰色になる）
                    settings.hideFlags &= ~HideFlags.NotEditable;
                    if (serialized == null || serialized.targetObject != settings) serialized = new SerializedObject (settings);
                    serialized.Update ();

                    EditorGUILayout.HelpBox (
                        "キャラの設定（Lilium Motion Editor/Character Settings）で上書きしない項目は、ここの値になります。\n"
                        + "フォルダの置き換え: " + SettingsLookup.kNameToken + "（prefab の名前から下の接尾辞を除いたもの）・" + SettingsLookup.kPrefabFolderToken + "（prefab のあるフォルダ）。"
                        + "並びの先頭が作る場所で、2 つ目からは実在するフォルダだけを一覧に並べます。",
                        MessageType.None);
                    EditorGUI.BeginChangeCheck ();
                    EditorGUILayout.PropertyField (serialized.FindProperty ("animBankFolders"), new GUIContent ("AnimBank Folders"), true);
                    EditorGUILayout.PropertyField (serialized.FindProperty ("poseBankFolders"), new GUIContent ("PoseBank Folders"), true);
                    EditorGUILayout.PropertyField (serialized.FindProperty ("nameSuffixes"), new GUIContent ("Name Suffixes"), true);
                    EditorGUILayout.PropertyField (serialized.FindProperty ("characterSettingsFolder"), new GUIContent ("Character Settings Folder"));
                    EditorGUILayout.Space ();
                    // 空のときはパッケージの既定を見せる。既定を選び直す・外すと空に戻す（パッケージを更新したら既定の中身も付いてくる）
                    SerializedProperty rig = serialized.FindProperty ("rigDefinition");
                    EditRigDefinition packageDefault = EditRigDefinition.packageDefault;
                    Object shown = rig.objectReferenceValue != null ? rig.objectReferenceValue : packageDefault;
                    Object picked = EditorGUILayout.ObjectField (
                        new GUIContent ("Rig Definition", "編集用リグの定義。キャラの設定で上書きしないキャラはこれを使う。空ならパッケージの既定（Default Rig Definition）"),
                        shown, typeof (EditRigDefinition), false);
                    if (picked != shown) rig.objectReferenceValue = picked == packageDefault ? null : picked;
                    EditorGUILayout.PropertyField (serialized.FindProperty ("autoBake"), new GUIContent ("Auto Bake"));
                    EditorGUILayout.PropertyField (serialized.FindProperty ("autoKey"), new GUIContent ("Auto Key", "入: 操作したらキーを打つ。切: 今のフレームにキーがある物しか動かせない（キーは Stacker の Key All などで打つ）"));
                    if (EditorGUI.EndChangeCheck ()) {
                        serialized.ApplyModifiedProperties ();
                        settings.Save ();
                    }
                },
            };
        }
    }

}
