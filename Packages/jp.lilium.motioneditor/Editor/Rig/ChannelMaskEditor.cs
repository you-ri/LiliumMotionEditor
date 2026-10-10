using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// ChannelMask のインスペクタ（S17）。部位は配列のままだと何番がどこか分からないので、部位の名前を付けたトグルで出す
    /// </summary>
    [CustomEditor (typeof (ChannelMask))]
    public sealed class ChannelMaskEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI ()
        {
            ChannelMask mask = (ChannelMask)target;
            serializedObject.Update ();

            EditorGUILayout.LabelField (Tr ("CHANNEL_MASK_BODY_PARTS"), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope ()) {
                if (GUILayout.Button (Tr ("CHANNEL_MASK_ALL"))) SetAll (mask, true);
                if (GUILayout.Button (Tr ("CHANNEL_MASK_NONE"))) SetAll (mask, false);
            }
            for (int i = 0; i < ChannelMask.kPartCount; i++) {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                bool value = mask.GetBodyPart (part);
                bool next = EditorGUILayout.Toggle (ObjectNames.NicifyVariableName (part.ToString ()), value);
                if (next == value) continue;
                Undo.RecordObject (mask, "Channel Mask");
                mask.SetBodyPart (part, next);
                EditorUtility.SetDirty (mask);
            }

            EditorGUILayout.Space ();
            EditorGUILayout.HelpBox (Tr ("CHANNEL_MASK_RULES_HELP"), MessageType.None);
            EditorGUILayout.PropertyField (serializedObject.FindProperty ("paths"), true);
            EditorGUILayout.PropertyField (serializedObject.FindProperty ("properties"), true);
            serializedObject.ApplyModifiedProperties ();

            EditorGUILayout.Space ();
            AvatarMaskGUI (mask);
        }

        AvatarMask avatarMask_;
        GameObject model_;
        readonly List<string> notes_ = new List<string> ();

        /// <summary>
        /// AvatarMask の読み込み・書き出し。読み込みで骨のパスを引き当てるにはキャラが要る（AvatarMask の骨のパスはモデルの階層なので）
        /// </summary>
        void AvatarMaskGUI (ChannelMask mask)
        {
            EditorGUILayout.LabelField ("Avatar Mask", EditorStyles.boldLabel);
            avatarMask_ = (AvatarMask)EditorGUILayout.ObjectField (new GUIContent ("Avatar Mask", Tr ("CHANNEL_MASK_AVATAR_FIELD_TOOLTIP")), avatarMask_, typeof (AvatarMask), false);
            model_ = (GameObject)EditorGUILayout.ObjectField (new GUIContent (Tr ("CHANNEL_MASK_AVATAR_MODEL"), Tr ("CHANNEL_MASK_AVATAR_MODEL_TOOLTIP")), model_, typeof (GameObject), false);
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (avatarMask_ == null)) {
                    if (GUILayout.Button (new GUIContent (Tr ("CHANNEL_MASK_AVATAR_IMPORT"), Tr ("CHANNEL_MASK_AVATAR_IMPORT_TOOLTIP")))) {
                        notes_.Clear ();
                        Undo.RecordObject (mask, "Import Avatar Mask");
                        ChannelMaskAvatar.Import (avatarMask_, mask, model_, notes_);
                        EditorUtility.SetDirty (mask);
                        if (notes_.Count == 0) notes_.Add (Tr ("CHANNEL_MASK_AVATAR_IMPORTED", avatarMask_.name));
                    }
                }
                if (GUILayout.Button (new GUIContent (Tr ("CHANNEL_MASK_AVATAR_EXPORT"), Tr ("CHANNEL_MASK_AVATAR_EXPORT_TOOLTIP")))) Export (mask);
            }
            if (notes_.Count > 0) EditorGUILayout.HelpBox (string.Join ("\n", notes_), MessageType.Info);
        }

        /// <summary>選んでいる AvatarMask へ書き出す。選んでいなければマスクの隣に作る</summary>
        void Export (ChannelMask mask)
        {
            notes_.Clear ();
            AvatarMask destination = avatarMask_;
            if (destination == null) {
                string path = ChannelMaskAvatar.DefaultExportPath (mask);
                if (path == null) return;
                destination = new AvatarMask ();
                ChannelMaskAvatar.Export (mask, destination, notes_);
                AssetDatabase.CreateAsset (destination, path);
                avatarMask_ = destination;
            }
            else {
                Undo.RecordObject (destination, "Export Avatar Mask");
                ChannelMaskAvatar.Export (mask, destination, notes_);
                EditorUtility.SetDirty (destination);
                AssetDatabase.SaveAssetIfDirty (destination);
            }
            notes_.Insert (0, Tr ("CHANNEL_MASK_AVATAR_EXPORTED", AssetDatabase.GetAssetPath (destination)));
            EditorGUIUtility.PingObject (destination);
        }

        static void SetAll (ChannelMask mask, bool value)
        {
            Undo.RecordObject (mask, "Channel Mask");
            mask.SetAllBodyParts (value);
            EditorUtility.SetDirty (mask);
        }
    }

}
