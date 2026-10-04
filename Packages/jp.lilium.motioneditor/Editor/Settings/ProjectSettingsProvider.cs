using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

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
                        Tr ("PROJECT_SETTINGS_PROVIDER_HELP", SettingsLookup.kNameToken, SettingsLookup.kPrefabFolderToken),
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
                        new GUIContent ("Rig Definition", Tr ("PROJECT_SETTINGS_PROVIDER_RIG_DEFINITION_TOOLTIP")),
                        shown, typeof (EditRigDefinition), false);
                    if (picked != shown) rig.objectReferenceValue = picked == packageDefault ? null : picked;
                    EditorGUILayout.PropertyField (serialized.FindProperty ("autoBake"), new GUIContent ("Auto Bake"));
                    EditorGUILayout.PropertyField (serialized.FindProperty ("autoKey"), new GUIContent ("Auto Key", Tr ("PROJECT_SETTINGS_PROVIDER_AUTO_KEY_TOOLTIP")));
                    if (EditorGUI.EndChangeCheck ()) {
                        serialized.ApplyModifiedProperties ();
                        settings.Save ();
                    }
                },
            };
        }
    }

}
