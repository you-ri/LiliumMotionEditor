using System.Collections.Generic;
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
        static readonly string[] kTargets = { KimodoJob.kOverride, KimodoJob.kBetween };
        static readonly string[] kTargetLabels = { "Override layer on the clip", "Interpolation between keys" };
        static readonly string[] kRunnerLabels = { "Python environment", "Docker" };

        [SerializeField] PreviewWindow target_;
        [SerializeField] bool showSettings_;
        /// <summary>補間として使うときに外部の動きにする区間（始まりのフレーム）と、それを選んだクリップ（S83）</summary>
        [SerializeField] List<int> chosen_ = new List<int> ();
        [SerializeField] AnimationClip chosenClip_;
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
            KimodoTextServer.changed -= Repaint;
            KimodoTextServer.changed += Repaint;
            EditorApplication.update -= TrackFocus;
            EditorApplication.update += TrackFocus;
            KimodoSettings settings = KimodoSettings.instance;
            showSettings_ |= settings.skeleton == null || (settings.runner == KimodoSettings.kPython ? string.IsNullOrEmpty (settings.pythonPath) : string.IsNullOrEmpty (settings.kimodoFolder));
        }

        void OnDisable ()
        {
            KimodoRunner.changed -= Repaint;
            KimodoTextServer.changed -= Repaint;
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
            int target = Mathf.Max (0, System.Array.IndexOf (kTargets, settings.target));
            target = EditorGUILayout.Popup ("Use the Result as", target, kTargetLabels);
            settings.target = kTargets[target];
            if (EditorGUI.EndChangeCheck ()) settings.Save ();
            if (settings.target == KimodoJob.kBetween) DrawSegments ();

            EditorGUILayout.Space ();
            bool running = job != null && job.running;
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (running || target_ == null)) {
                    if (GUILayout.Button ("Generate Between Keys", GUILayout.Height (24))) {
                        problem_ = KimodoRunner.Start (target_, settings.prompt, settings.mode, settings.target, ChosenSegments ());
                    }
                }
                using (new EditorGUI.DisabledScope (!running)) {
                    if (GUILayout.Button ("Stop", GUILayout.Width (60), GUILayout.Height (24))) KimodoRunner.Cancel ();
                }
            }
            if (problem_ != null && !running) EditorGUILayout.HelpBox (problem_, MessageType.Warning);
            if (job != null) DrawJob (job);

            EditorGUILayout.Space ();
            // 文章の読み取りのサーバーは Docker のときだけ（Python の環境では WSL のメモリの上限が無く、読み込みも速いので要らない）
            if (settings.runner == KimodoSettings.kDocker) DrawTextServer ();

            EditorGUILayout.Space ();
            showSettings_ = EditorGUILayout.Foldout (showSettings_, "Settings", true);
            if (showSettings_) DrawSettings (settings);
            EditorGUILayout.EndScrollView ();
        }

        /// <summary>今のクリップの区間（隣り合うキーの間）</summary>
        List<BetweenClips.Range> Segments ()
        {
            List<BetweenClips.Range> segments = new List<BetweenClips.Range> ();
            AnimationClip clip = target_ != null ? target_.baseClip : null;
            if (clip == null) return segments;
            int[] frames = ClipCycle.GetFrames (clip);
            for (int i = 0; i + 1 < frames.Length; i++) segments.Add (new BetweenClips.Range { start = frames[i], end = frames[i + 1] });
            return segments;
        }

        List<BetweenClips.Range> ChosenSegments ()
        {
            return Segments ().Where (r => chosen_.Contains (r.start)).ToList ();
        }

        /// <summary>
        /// 補間として使う区間を選ぶ（S83）。クリップが替わったら、外部の動きを使っている区間（無ければ全部）を選び直す。
        /// Kimodo は毎回クリップ全体を作り直し、結果のファイルを丸ごと置き換える（選ばなかった区間は元の補間のまま）
        /// </summary>
        void DrawSegments ()
        {
            AnimationClip clip = target_ != null ? target_.baseClip : null;
            List<BetweenClips.Range> segments = Segments ();
            if (clip != chosenClip_) {
                chosenClip_ = clip;
                List<int> external = segments.Where (r => {
                    BetweenClips.State state = BetweenClips.GetSegmentState (clip, r);
                    return state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale;
                }).Select (r => r.start).ToList ();
                chosen_ = external.Count > 0 ? external : segments.Select (r => r.start).ToList ();
            }
            EditorGUILayout.LabelField ("Segments to fill with Kimodo's motion (the others keep their interpolation)", EditorStyles.wordWrappedMiniLabel);
            if (segments.Count == 0) {
                EditorGUILayout.HelpBox ("Key at least two frames of the clip.", MessageType.None);
                return;
            }
            using (new EditorGUILayout.HorizontalScope ()) {
                foreach (BetweenClips.Range range in segments) {
                    bool on = chosen_.Contains (range.start);
                    bool next = GUILayout.Toggle (on, range.start + "-" + range.end, EditorStyles.miniButton);
                    if (next == on) continue;
                    if (next) chosen_.Add (range.start);
                    else chosen_.Remove (range.start);
                }
            }
        }

        /// <summary>
        /// 文章の読み取りのサーバー（S78）。立てておくと 2 本目からの生成が速くなる（モデルの読み込みの大半が文章の読み取り）。
        /// 止めるまで PC のメモリを約 15GB 使い続ける
        /// </summary>
        static void DrawTextServer ()
        {
            KimodoTextServer.State state = KimodoTextServer.state;
            string label = state == KimodoTextServer.State.Ready ? "Text server: ready (generations skip loading the text model)"
                : state == KimodoTextServer.State.Starting ? "Text server: starting (loading the text model takes a few minutes)"
                : "Text server: stopped (each generation loads the text model, about 4 minutes)";
            EditorGUILayout.LabelField (label, EditorStyles.wordWrappedMiniLabel);
            if (state == KimodoTextServer.State.Starting && KimodoTextServer.log.Length > 0) EditorGUILayout.HelpBox (KimodoTextServer.log, MessageType.None);
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (state != KimodoTextServer.State.Stopped)) {
                    if (GUILayout.Button ("Start Text Server")) {
                        string problem = KimodoTextServer.Start ();
                        if (problem != null) EditorUtility.DisplayDialog ("Kimodo", problem, "OK");
                    }
                }
                using (new EditorGUI.DisabledScope (state == KimodoTextServer.State.Stopped)) {
                    if (GUILayout.Button ("Stop Text Server")) KimodoTextServer.Stop ();
                }
            }
            if (state != KimodoTextServer.State.Stopped) EditorGUILayout.LabelField ("The server keeps using about 15 GB of memory until you stop it.", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawJob (KimodoJob job)
        {
            EditorGUILayout.Space ();
            if (job.running) {
                System.TimeSpan elapsed = System.TimeSpan.FromTicks (System.DateTime.Now.Ticks - job.started);
                string hint = job.pid > 0 ? "(a new prompt takes about 2 minutes, the same prompt about 30 seconds)"
                    : job.server ? "(with the text server, about a minute)" : "(loading the models takes about 4 minutes)";
                EditorGUILayout.LabelField (string.Format ("Generating {0:m\\:ss}  ", elapsed) + hint);
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
            if (job.state == KimodoJob.kImported && job.target != KimodoJob.kBetween) DrawResult (job);
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
            int runner = settings.runner == KimodoSettings.kDocker ? 1 : 0;
            runner = EditorGUILayout.Popup ("Run Kimodo With", runner, kRunnerLabels);
            settings.runner = runner == 1 ? KimodoSettings.kDocker : KimodoSettings.kPython;
            if (settings.runner == KimodoSettings.kPython) {
                settings.pythonPath = FileField ("Python", settings.pythonPath, "The python of the environment where Kimodo is installed (for example kimodo/.venv/Scripts/python.exe)");
            }
            else {
                settings.kimodoFolder = FolderField ("Kimodo Folder", settings.kimodoFolder, "The git clone of Kimodo");
                settings.image = EditorGUILayout.TextField ("Docker Image", settings.image);
            }
            settings.huggingFaceCache = FolderField ("Hugging Face Cache", settings.huggingFaceCache, "Empty: " + settings.huggingFaceFolder);
            settings.skeleton = (ExternalSkeleton) EditorGUILayout.ObjectField ("SOMA Skeleton", settings.skeleton, typeof (ExternalSkeleton), false);
            if (EditorGUI.EndChangeCheck ()) settings.Save ();
            EditorGUILayout.HelpBox ("Setup and licenses: the README of the Kimodo sample. The Hugging Face token is read by Kimodo from the cache (hf auth login); this window never reads it.", MessageType.None);
        }

        static string FileField (string label, string value, string tooltip)
        {
            using (new EditorGUILayout.HorizontalScope ()) {
                value = EditorGUILayout.TextField (new GUIContent (label, tooltip), value);
                if (GUILayout.Button ("...", GUILayout.Width (28))) {
                    string picked = EditorUtility.OpenFilePanel (label, File.Exists (value) ? Path.GetDirectoryName (value) : "", "");
                    if (!string.IsNullOrEmpty (picked)) {
                        value = picked;
                        GUI.FocusControl (null);
                    }
                }
            }
            return value;
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
