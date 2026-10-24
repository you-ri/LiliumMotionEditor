using UnityEditor;
using UnityEngine;
using static Lilium.KimodoText;

namespace Lilium
{

    /// <summary>
    /// Kimodo を使えるようにする案内の窓（ユーザー指示「Kimodo Install 専用の Window を作って」）。
    /// 案内（手順 1〜3 と「まとめて入れる」）は長い文と横に並ぶボタンがあり、Motion Editor の窓のパネルでは狭くて読みにくかったので、ここに出す。
    /// パネルと Project Settings の Kimodo のページは、使えないときにこの窓を開くボタンを出す
    /// </summary>
    public sealed class KimodoInstallWindow : EditorWindow
    {
        Vector2 scroll_;

        [MenuItem ("Window/Lilium Motion Editor/Kimodo Install")]
        public static void Open ()
        {
            // 題名は OnEnable でアイコンと一緒に付ける（GetWindow に題名を渡すと、アイコンが消える）
            KimodoInstallWindow window = GetWindow<KimodoInstallWindow> ();
            window.minSize = new Vector2 (480, 560);
            window.Show ();
            window.Focus ();
        }

        void OnEnable ()
        {
            titleContent = new GUIContent (Tr ("KIMODO_INSTALL_TITLE"), KimodoSetup.kimodoIcon);
            KimodoRunner.changed -= Repaint;
            KimodoRunner.changed += Repaint;
        }

        void OnDisable ()
        {
            KimodoRunner.changed -= Repaint;
        }

        // 別の窓で入れている間に ✓ が付いていくよう、時々描き直す（ファイルがあるかを見るだけなので軽い）
        void OnInspectorUpdate ()
        {
            Repaint ();
        }

        void OnGUI ()
        {
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 170;
            try {
                scroll_ = EditorGUILayout.BeginScrollView (scroll_);
                EditorGUILayout.Space ();
                KimodoSetup.DrawGuide ();
                EditorGUILayout.Space ();
                EditorGUILayout.HelpBox (Tr ("KIMODO_SETTINGS_SAVED_IN"), MessageType.None);
                EditorGUILayout.EndScrollView ();
            }
            finally {
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }
    }

}
