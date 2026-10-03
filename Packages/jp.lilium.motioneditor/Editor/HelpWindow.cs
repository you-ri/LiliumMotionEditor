using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using Lilium;

namespace Lilium
{


    public class HelpWindow : EditorWindow
    {
        bool doClose_ = false;

        public static void Popup (Rect buttonRect)
        {
            HelpWindow window = EditorWindow.CreateInstance<HelpWindow> ();
            //window.position = new Rect(Screen.width/2,Screen.height/2, 250, 250);
            window.ShowAsDropDown (buttonRect, new Vector2 (250f, 250f));
            window.Focus ();
        }

        void OnLostFocus ()
        {
            doClose_ = true;
        }

        void Update ()
        {
            if (doClose_) {
                Close ();
            }
        }

        void OnGUI ()
        {
            GUILayout.Label ("View", EditorStyles.boldLabel);
            EditorGUILayout.LabelField ("F1", "View at Left");
            EditorGUILayout.LabelField ("F2", "View at Front");
            EditorGUILayout.LabelField ("F3", "View at Top");

            GUILayout.Label ("Pose", EditorStyles.boldLabel);
            EditorGUILayout.LabelField ("C", "Copy Pose");            EditorGUILayout.LabelField ("V", "Paste Pose");
            EditorGUILayout.LabelField ("R", "Reset Pose");

            GUILayout.Label ("Animation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField ("P", "Play Animation");
            EditorGUILayout.LabelField ("S", "Stop Animation");
        }
    }

}
