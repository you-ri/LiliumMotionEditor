using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 開いている Motion Editor の窓のキーと文章から、Kimodo でキーの間を作って Override として取り込む窓（S77）。
    /// 取り込んだ Override を見るには ✏ をそこへ移す必要がある（書き込み先より上の段は編集中は効かない）。
    /// ✏ を移したまま打ったキーは Override に入り次の生成に渡らないので、「結果を見る」と「キーを直す」を切り替えられるようにする
    /// </summary>
    public sealed class KimodoWindow : EditorWindow
    {
        static readonly string[] kModeLabels = { "Full body (whole pose at each key)", "Hands and feet only", "Hips path only" };

        [SerializeField] PreviewWindow target_;
        [SerializeField] bool showSettings_;
        Vector2 scroll_;
        string problem_;

        [MenuItem ("Tools/Lilium Motion Editor/Kimodo/Generate Between Keys")]
        static void Open ()
        {
            KimodoWindow window = GetWindow<KimodoWindow> ("Kimodo");
            window.minSize = new Vector2 (360, 320);
        }

        void OnEnable ()
        {
            KimodoRunner.changed -= Repaint;
            KimodoRunner.changed += Repaint;
            EditorApplication.update -= TrackFocus;
            EditorApplication.update += TrackFocus;
            showSettings_ |= KimodoSettings.instance.skeleton == null || string.IsNullOrEmpty (KimodoSettings.instance.kimodoFolder);
        }

        void OnDisable ()
        {
            KimodoRunner.changed -= Repaint;
            EditorApplication.update -= TrackFocus;
            Watch (null);
        }

        /// <summary>最後に触った Motion Editor の窓を相手にする</summary>
        void TrackFocus ()
        {
            if (EditorWindow.focusedWindow is PreviewWindow focused && focused != target_) {
                Watch (focused);
                Repaint ();
            }
        }

        void Watch (PreviewWindow window)
        {
            if (target_ != null) target_.stateChanged -= Repaint;
            target_ = window;
            if (target_ != null) target_.stateChanged += Repaint;
        }

        void OnGUI ()
        {
            if (target_ == null) Watch (Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault (w => w.baseClip != null));
            KimodoSettings settings = KimodoSettings.instance;
            KimodoJob job = KimodoRunner.job;
            scroll_ = EditorGUILayout.BeginScrollView (scroll_);

            EditorGUILayout.LabelField ("Clip", target_ != null && target_.baseClip != null ? target_.baseClip.name : "(open a clip in the Motion Editor window)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck ();
            EditorGUILayout.LabelField ("Prompt (English, the whole motion)");
            settings.prompt = EditorGUILayout.TextArea (settings.prompt, GUILayout.MinHeight (40));
            int mode = System.Array.IndexOf (KimodoSettings.kModes, settings.mode);
            mode = EditorGUILayout.Popup ("Constraints", Mathf.Max (0, mode), kModeLabels);
            settings.mode = KimodoSettings.kModes[mode];
            if (EditorGUI.EndChangeCheck ()) settings.Save ();

            EditorGUILayout.Space ();
            bool running = job != null && job.running;
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (running || target_ == null)) {
                    if (GUILayout.Button ("Generate Between Keys", GUILayout.Height (24))) {
                        problem_ = KimodoRunner.Start (target_, settings.prompt, settings.mode);
                    }
                }
                using (new EditorGUI.DisabledScope (!running)) {
                    if (GUILayout.Button ("Stop", GUILayout.Width (60), GUILayout.Height (24))) KimodoRunner.Cancel ();
                }
            }
            if (problem_ != null && !running) EditorGUILayout.HelpBox (problem_, MessageType.Warning);
            if (job != null) DrawJob (job);

            EditorGUILayout.Space ();
            showSettings_ = EditorGUILayout.Foldout (showSettings_, "Settings", true);
            if (showSettings_) DrawSettings (settings);
            EditorGUILayout.EndScrollView ();
        }

        void DrawJob (KimodoJob job)
        {
            EditorGUILayout.Space ();
            if (job.running) {
                System.TimeSpan elapsed = System.TimeSpan.FromTicks (System.DateTime.Now.Ticks - job.started);
                EditorGUILayout.LabelField (string.Format ("Generating {0:m\\:ss}  (loading the models takes about 4 minutes)", elapsed));
                string tail = KimodoRunner.Tail (KimodoRunner.ReadLog (), 8);
                if (tail.Length > 0) EditorGUILayout.HelpBox (tail, MessageType.None);
                return;
            }
            MessageType type = job.state == KimodoJob.kFailed ? MessageType.Error : job.state == KimodoJob.kCancelled ? MessageType.Warning : MessageType.Info;
            string summary = job.message;
            if (job.state == KimodoJob.kImported) summary += string.Format ("\nTook {0:0} s. Prompt: {1}", job.seconds, job.prompt);
            EditorGUILayout.HelpBox (summary, type);
            if (job.state == KimodoJob.kGenerated) {
                PreviewWindow window = KimodoRunner.FindWindow (job.baseClip);
                using (new EditorGUI.DisabledScope (window == null)) {
                    if (GUILayout.Button ("Import")) problem_ = KimodoRunner.TryImport (window);
                }
            }
            if (job.state == KimodoJob.kImported) DrawResult (job);
            using (new EditorGUILayout.HorizontalScope ()) {
                if (GUILayout.Button ("Open Work Folder")) EditorUtility.RevealInFinder (job.logPath);
                if (GUILayout.Button ("Clear")) KimodoRunner.Clear ();
            }
        }

        void DrawResult (KimodoJob job)
        {
            PreviewWindow window = KimodoRunner.FindWindow (job.baseClip);
            int index = KimodoRunner.FindOverride (window, job.destination);
            if (window != null && index >= 0) {
                bool showing = window.overrideWriteIndex == index;
                EditorGUILayout.LabelField (showing
                    ? "Showing the generated motion. New keys go into the Override and are not sent to Kimodo."
                    : "Editing the keys. The generated motion is hidden while the base clip is the write target.", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope ()) {
                    using (new EditorGUI.DisabledScope (showing)) {
                        if (GUILayout.Button ("See the Result")) window.SetOverrideWrite (index);
                    }
                    using (new EditorGUI.DisabledScope (!showing)) {
                        if (GUILayout.Button ("Edit the Keys")) window.SetOverrideWrite (-1);
                    }
                }
            }
            if (job.keyDeviations == null || job.keyDeviations.Count == 0) return;
            // 寄せる前に Kimodo の動きがキーからどれだけ離れていたか。大きいキーは寄せる前後が目に見える（キーを足すか文章を変える）
            EditorGUILayout.LabelField ("How far Kimodo was from each key (before keeping the keys)", EditorStyles.miniBoldLabel);
            foreach (PoseSession.KeyDeviation deviation in job.keyDeviations) {
                string line = string.Format ("Frame {0}: {1:0.0} cm ({2}), hips {3:0.0} cm", deviation.frame, deviation.distance * 100, deviation.bone, deviation.hips * 100);
                EditorGUILayout.LabelField (deviation.distance > 0.1f ? line + "  - large" : line, EditorStyles.miniLabel);
            }
        }

        static void DrawSettings (KimodoSettings settings)
        {
            EditorGUI.BeginChangeCheck ();
            settings.kimodoFolder = FolderField ("Kimodo Folder", settings.kimodoFolder, "The git clone of Kimodo");
            settings.image = EditorGUILayout.TextField ("Docker Image", settings.image);
            settings.huggingFaceCache = FolderField ("Hugging Face Cache", settings.huggingFaceCache, "Empty: " + settings.huggingFaceFolder);
            settings.skeleton = (ExternalSkeleton) EditorGUILayout.ObjectField ("SOMA Skeleton", settings.skeleton, typeof (ExternalSkeleton), false);
            if (EditorGUI.EndChangeCheck ()) settings.Save ();
            EditorGUILayout.HelpBox ("Setup and licenses: the README of the Kimodo sample. The Hugging Face token is read by Kimodo from the cache (hf auth login); this window never reads it.", MessageType.None);
        }

        static string FolderField (string label, string value, string tooltip)
        {
            using (new EditorGUILayout.HorizontalScope ()) {
                value = EditorGUILayout.TextField (new GUIContent (label, tooltip), value);
                if (GUILayout.Button ("...", GUILayout.Width (28))) {
                    string picked = EditorUtility.OpenFolderPanel (label, Directory.Exists (value) ? value : "", "");
                    if (!string.IsNullOrEmpty (picked)) {
                        value = picked;
                        GUI.FocusControl (null);
                    }
                }
            }
            return value;
        }
    }

}
