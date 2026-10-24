using System.IO;
using UnityEditor;
using UnityEngine;
using static Lilium.KimodoText;

namespace Lilium
{

    /// <summary>
    /// Kimodo の出力（生成のログ・失敗の詳細・文章の読み取りのサーバーのログ）を出す窓。
    /// 操作は Motion Editor の窓のパネル（KimodoOverlay）にあり、場所を取るログだけをこちらに分ける
    /// </summary>
    public sealed class KimodoConsoleWindow : EditorWindow
    {
        /// <summary>IMGUI の文字欄は長すぎると描けないので、末尾だけ出す</summary>
        const int kMaxLines = 400;
        const int kMaxChars = 15000;

        Vector2 scroll_;
        /// <summary>末尾を追いかける（上へ戻して読んでいる間は追わない）</summary>
        bool follow_ = true;
        string log_ = "";
        string lastLog_ = "";

        [MenuItem ("Window/Lilium Motion Editor/Kimodo Console")]
        public static void Open ()
        {
            // 題名は OnEnable でアイコンと一緒に付ける（GetWindow に題名を渡すと、題名だけで上書きされアイコンが消える）
            KimodoConsoleWindow window = GetWindow<KimodoConsoleWindow> ();
            window.minSize = new Vector2 (360, 200);
            window.Reload ();
        }

        /// <summary>パネルに出す結果の要約（失敗の詳細とログの末尾はコンソールに出す）</summary>
        public static string Summary (KimodoJob job)
        {
            string message = job.message ?? "";
            int detail = message.IndexOf ("\n\n");
            if (detail >= 0) message = message.Substring (0, detail);
            if (job.state == KimodoJob.kFailed) message += "\n" + Tr ("KIMODO_CONSOLE_SEE_LOG");
            if (job.state == KimodoJob.kImported) message += "\n" + Tr ("KIMODO_CONSOLE_TOOK", job.seconds, job.prompt);
            return message;
        }

        void OnEnable ()
        {
            titleContent = new GUIContent (Tr ("KIMODO_CONSOLE_TITLE"), KimodoSetup.icon);
            KimodoRunner.changed -= Reload;
            KimodoRunner.changed += Reload;
            KimodoTextServer.changed -= Reload;
            KimodoTextServer.changed += Reload;
            Reload ();
        }

        /// <summary>生成中はタブのきらめきを動かす（S85。コマが替わったときだけ差し替える）</summary>
        void Update ()
        {
            KimodoJob job = KimodoRunner.job;
            Texture2D icon = job != null && job.running ? KimodoSetup.BusyIcon (EditorApplication.timeSinceStartup) : KimodoSetup.icon;
            if (titleContent.image != icon) titleContent = new GUIContent (Tr ("KIMODO_CONSOLE_TITLE"), icon);
        }

        void OnDisable ()
        {
            KimodoRunner.changed -= Reload;
            KimodoTextServer.changed -= Reload;
        }

        /// <summary>ログを読み直す（生成中は KimodoRunner が 1 秒ごとに知らせる。描くたびにファイルを読まない）</summary>
        void Reload ()
        {
            log_ = Clip (KimodoRunner.Tail (KimodoRunner.ReadLog (), kMaxLines));
            if (log_ != lastLog_) {
                lastLog_ = log_;
                if (follow_) scroll_.y = float.MaxValue;
            }
            Repaint ();
        }

        static string Clip (string text)
        {
            return text.Length > kMaxChars ? text.Substring (text.Length - kMaxChars) : text;
        }

        void OnGUI ()
        {
            KimodoJob job = KimodoRunner.job;
            using (new EditorGUILayout.HorizontalScope (EditorStyles.toolbar)) {
                GUILayout.Label (job == null ? Tr ("KIMODO_CONSOLE_NO_JOB") : Tr ("KIMODO_CONSOLE_JOB", StateLabel (job), job.prompt), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace ();
                follow_ = GUILayout.Toggle (follow_, Tr ("KIMODO_CONSOLE_FOLLOW"), EditorStyles.toolbarButton);
                using (new EditorGUI.DisabledScope (job == null)) {
                    if (GUILayout.Button (Tr ("KIMODO_CONSOLE_COPY"), EditorStyles.toolbarButton)) EditorGUIUtility.systemCopyBuffer = KimodoRunner.ReadLog ();
                    if (GUILayout.Button (Tr ("KIMODO_CONSOLE_OPEN_FOLDER"), EditorStyles.toolbarButton)) EditorUtility.RevealInFinder (job.logPath);
                }
            }

            scroll_ = EditorGUILayout.BeginScrollView (scroll_);
            // 失敗したときは、理由とよくある原因（KimodoRunner.Diagnose）を先に出す
            if (job != null && !job.running && !string.IsNullOrEmpty (job.message)) {
                string message = job.message;
                int detail = message.IndexOf ("\n\n");
                if (detail >= 0) message = message.Substring (0, detail);
                EditorGUILayout.HelpBox (message, job.state == KimodoJob.kFailed ? MessageType.Error : MessageType.Info);
            }
            KimodoTextServer.State server = KimodoTextServer.state;
            if (server == KimodoTextServer.State.Starting && KimodoTextServer.log.Length > 0) {
                EditorGUILayout.LabelField (Tr ("KIMODO_CONSOLE_TEXT_SERVER"), EditorStyles.boldLabel);
                SelectableText (KimodoTextServer.log);
            }
            if (log_.Length > 0) {
                EditorGUILayout.LabelField (job != null && File.Exists (job.logPath) ? job.logPath : Tr ("KIMODO_CONSOLE_LOG"), EditorStyles.miniBoldLabel);
                SelectableText (log_);
            }
            else if (job == null) {
                EditorGUILayout.LabelField (Tr ("KIMODO_CONSOLE_EMPTY"), EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView ();
        }

        /// <summary>生成の状態の名前（KimodoJob の状態は保存用の英語の文字なので、画面ではこの言葉にする）</summary>
        static string StateLabel (KimodoJob job)
        {
            if (job.running) return Tr ("KIMODO_STATE_RUNNING");
            switch (job.state) {
                case KimodoJob.kGenerated: return Tr ("KIMODO_STATE_GENERATED");
                case KimodoJob.kImported: return Tr ("KIMODO_STATE_IMPORTED");
                case KimodoJob.kFailed: return Tr ("KIMODO_STATE_FAILED");
                case KimodoJob.kCancelled: return Tr ("KIMODO_STATE_CANCELLED");
                default: return job.state;
            }
        }

        void SelectableText (string text)
        {
            GUIStyle style = EditorStyles.wordWrappedLabel;
            float height = style.CalcHeight (new GUIContent (text), position.width - 24);
            EditorGUILayout.SelectableLabel (text, style, GUILayout.Height (height));
        }
    }

}
