using System.IO;
using UnityEditor;
using UnityEngine;
using static Lilium.KimodoText;

namespace Lilium
{

    /// <summary>
    /// Kimodo が使えるようになっているかと、入れ方の案内（Kimodo Install の窓に出す）と、Project Settings の Kimodo のページ。
    /// ファイルがあるかだけで分かること（python・kimodo のフォルダ・トークン・SOMA の骨格）は描くたびに見る。
    /// python や docker を動かして分かること（kimodo が入っているか・イメージがあるか）は、生成を始めるときと「Check」で確かめた結果（KimodoRunner.environmentProblem）を使う
    /// </summary>
    static class KimodoSetup
    {
        const string kKimodoUrl = "https://github.com/nv-tlabs/kimodo";
        const string kLlamaUrl = "https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct";

        public static bool hasToken
        {
            get { return File.Exists (Path.Combine (KimodoSettings.instance.huggingFaceFolder, "token")); }
        }

        public static bool hasKimodo
        {
            get {
                KimodoSettings settings = KimodoSettings.instance;
                if (settings.runner == KimodoSettings.kPython) return !string.IsNullOrEmpty (settings.pythonPath) && File.Exists (settings.pythonPath);
                return !string.IsNullOrEmpty (settings.kimodoFolder) && Directory.Exists (Path.Combine (settings.kimodoFolder, "kimodo"));
            }
        }

        public static bool hasSkeleton
        {
            get { return KimodoSettings.instance.skeleton != null; }
        }

        /// <summary>生成できる（入れ方の案内を出さない）</summary>
        public static bool ready
        {
            get { return hasToken && hasKimodo && hasSkeleton && KimodoRunner.environmentProblem == null; }
        }

        const string kTokenUrl = "https://huggingface.co/settings/tokens";
        static bool showDetails_;
        static string checkProblem_;

        /// <summary>
        /// 入れ方の案内（Kimodo Install の窓）。利用者がすることを 3 つ（申請する・まとめて入れる・確かめる）にし、
        /// 1 つの手順は短い文とボタンだけにする（ユーザー指示「まだ難しい。もっと簡単にして」）。
        /// 骨格は「まとめて入れる」が作る T ポーズから「確かめる」が作るので、手順にしない。動かし方の選択・コマンドのコピー・各欄は「くわしい設定」にしまう
        /// </summary>
        public static void DrawGuide ()
        {
            // 使える状態でも案内は開ける（入れ直す・設定を変える）ので、出だしの文だけ替える
            EditorGUILayout.LabelField (ready ? "✓ " + Tr ("KIMODO_SETTINGS_READY") : Tr ("KIMODO_SETUP_TITLE"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField (ready ? Tr ("KIMODO_SETUP_READY_INTRO") : Tr ("KIMODO_SETUP_INTRO"), EditorStyles.wordWrappedLabel);

            Step (Tr ("KIMODO_SETUP_STEP_APPLY"), hasToken);
            Line (Tr ("KIMODO_SETUP_APPLY_ACCOUNT"), Tr ("KIMODO_SETUP_OPEN_LLAMA"), kLlamaUrl);
            Line (Tr ("KIMODO_SETUP_APPLY_TOKEN"), Tr ("KIMODO_SETUP_OPEN_TOKENS"), kTokenUrl);

            Step (Tr ("KIMODO_SETUP_STEP_INSTALL"), hasKimodo);
            EditorGUILayout.LabelField (Tr ("KIMODO_SETUP_INSTALL_TEXT"), EditorStyles.wordWrappedLabel);
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            using (new EditorGUI.DisabledScope (!windows)) {
                if (GUILayout.Button (Tr ("KIMODO_SETUP_INSTALL_ALL"), GUILayout.Height (32))) installProblem_ = InstallAll ();
            }
            if (!windows) EditorGUILayout.HelpBox (Tr ("KIMODO_SETUP_INSTALL_WINDOWS_ONLY"), MessageType.None);
            if (installProblem_ != null) EditorGUILayout.HelpBox (installProblem_, MessageType.Warning);
            else if (installStarted_ && !ready) EditorGUILayout.HelpBox (Tr ("KIMODO_SETUP_INSTALL_RUNNING"), MessageType.Info);

            Step (Tr ("KIMODO_SETUP_STEP_CHECK"), ready);
            EditorGUILayout.LabelField (Tr ("KIMODO_SETUP_CHECK_TEXT"), EditorStyles.wordWrappedLabel);
            // python を動かすので数秒かかる
            if (GUILayout.Button (Tr ("KIMODO_SETUP_CHECK"), GUILayout.Height (32))) checkProblem_ = CheckAll ();
            string problem = checkProblem_ ?? KimodoRunner.environmentProblem;
            if (problem != null) EditorGUILayout.HelpBox (problem, MessageType.Warning);
            else if (ready) EditorGUILayout.HelpBox (Tr ("KIMODO_SETUP_READY_NOTE"), MessageType.Info);

            EditorGUILayout.Space (12);
            showDetails_ = EditorGUILayout.Foldout (showDetails_, Tr ("KIMODO_SETUP_DETAILS"), true);
            if (showDetails_) DrawDetails ();
        }

        /// <summary>文と、その右にページを開くボタン（手順 1 の 2 行）</summary>
        static void Line (string text, string button, string url)
        {
            using (new EditorGUILayout.HorizontalScope ()) {
                EditorGUILayout.LabelField ("• " + text, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button (button, GUILayout.Width (170))) Application.OpenURL (url);
            }
        }

        /// <summary>くわしい設定（自分でコマンドを流す人・Docker で動かす人・骨格を作り直す人向け）</summary>
        static void DrawDetails ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            using (new EditorGUILayout.HorizontalScope ()) {
                if (settings.runner == KimodoSettings.kPython) {
                    if (GUILayout.Button (Tr ("KIMODO_SETUP_COPY_INSTALL"))) Copy (PythonInstallCommands ());
                }
                else if (GUILayout.Button (Tr ("KIMODO_SETUP_COPY_BUILD"))) Copy (DockerBuildCommands ());
                if (GUILayout.Button (Tr ("KIMODO_SETUP_OPEN_KIMODO"))) Application.OpenURL (kKimodoUrl);
            }
            if (settings.runner == KimodoSettings.kDocker) EditorGUILayout.LabelField (Tr ("KIMODO_SETUP_DOCKER"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField (Tr ("KIMODO_SETUP_SKELETON"), EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope ()) {
                using (new EditorGUI.DisabledScope (!hasKimodo)) {
                    if (GUILayout.Button (Tr ("KIMODO_SETUP_COPY_TPOSE"))) Copy (TPoseCommand ());
                }
                if (GUILayout.Button (Tr ("KIMODO_SETUP_MAKE_SKELETON"))) EditorApplication.ExecuteMenuItem ("Assets/Create/Lilium Motion Editor/Kimodo SOMA Skeleton From BVH...");
            }
            EditorGUILayout.Space ();
            DrawSettings ();
            if (GUILayout.Button (Tr ("KIMODO_SETUP_OPEN_README"))) OpenReadme ();
        }

        /// <summary>
        /// 「確かめる」: 動かす環境を確かめ（結果は KimodoRunner.environmentProblem）、骨格がまだ無ければ T ポーズから作る。
        /// 足りないものの理由を返す（環境の理由は environmentProblem で出すので、ここでは返さない）
        /// </summary>
        static string CheckAll ()
        {
            if (KimodoRunner.CheckEnvironment () != null) return null;
            return hasSkeleton ? null : MakeSkeleton ();
        }

        /// <summary>
        /// 「まとめて入れる」が作った T ポーズ（Kimodo のフォルダの tpose.bvh）から Kimodo の骨格を作り、設定に入れる。
        /// 骨格はサンプルのフォルダに置く（前に作ったものがあればそれを使う）
        /// </summary>
        static string MakeSkeleton ()
        {
            string sample = SampleFolder ();
            if (sample == null) return Tr ("KIMODO_SETUP_INSTALL_NO_SCRIPT");
            string asset = sample + "/Kimodo SOMA Skeleton.asset";
            ExternalSkeleton skeleton = AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (asset);
            if (skeleton == null) {
                string bvh = TPosePath ();
                if (bvh == null || !File.Exists (bvh)) return Tr ("KIMODO_SETUP_NO_TPOSE");
                string error = KimodoSkeleton.Create (bvh, asset);
                if (error != null) return error;
                skeleton = AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (asset);
            }
            KimodoSettings settings = KimodoSettings.instance;
            settings.skeleton = skeleton;
            settings.Save ();
            return null;
        }

        /// <summary>T ポーズの BVH の場所（Python の環境は kimodo/.venv/Scripts/python.exe から kimodo のフォルダを辿る。Docker は kimodo のフォルダ）</summary>
        static string TPosePath ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            if (settings.runner == KimodoSettings.kDocker) return string.IsNullOrEmpty (settings.kimodoFolder) ? null : Path.Combine (settings.kimodoFolder, "tpose.bvh");
            if (string.IsNullOrEmpty (settings.pythonPath)) return null;
            DirectoryInfo scripts = Directory.GetParent (Path.GetFullPath (settings.pythonPath));
            DirectoryInfo kimodo = scripts != null && scripts.Parent != null ? scripts.Parent.Parent : null;
            return kimodo != null ? Path.Combine (kimodo.FullName, "tpose.bvh") : null;
        }

        static bool installStarted_;
        static string installProblem_;

        /// <summary>
        /// 手順 2 をまとめて行う（ユーザー指示「すべて一括でできるようにスクリプト用意できない？」）。
        /// Kimodo を置くフォルダを選んでもらい、サンプルの install_kimodo.ps1 を見える窓で動かす（途中で winget の確認や
        /// Hugging Face の合言葉の入力があるので、Unity の中では動かさない）。でき上がる python.exe の場所は決まっているので、先に設定へ入れる。
        /// 窓を開けなければ理由を返す
        /// </summary>
        static string InstallAll ()
        {
            installStarted_ = false;
            string sample = SampleFolder ();
            string script = sample != null ? Path.GetFullPath (sample + "/Python/install_kimodo.ps1") : null;
            if (script == null || !File.Exists (script)) return Tr ("KIMODO_SETUP_INSTALL_NO_SCRIPT");
            string folder = EditorUtility.OpenFolderPanel (Tr ("KIMODO_SETUP_INSTALL_FOLDER"), System.Environment.GetFolderPath (System.Environment.SpecialFolder.UserProfile), "");
            if (string.IsNullOrEmpty (folder)) return null;
            // 末尾の \ は引数の閉じる " を消してしまうので外す（ドライブの直下は C:\. にする）
            folder = Path.GetFullPath (folder).TrimEnd ('\\', '/');
            if (folder.EndsWith (":")) folder += "\\.";
            if (!EditorUtility.DisplayDialog ("Kimodo", Tr ("KIMODO_SETUP_INSTALL_CONFIRM", Path.Combine (folder, "kimodo")), Tr ("KIMODO_SETUP_INSTALL_START"), Tr ("KIMODO_CANCEL"))) return null;

            KimodoSettings settings = KimodoSettings.instance;
            string arguments = "-NoExit -ExecutionPolicy Bypass -File \"" + script + "\" -Folder \"" + folder + "\" -Language " + MotionEditorLanguage.current;
            if (!string.IsNullOrEmpty (settings.huggingFaceCache)) arguments += " -HuggingFaceHome \"" + Path.GetFullPath (settings.huggingFaceCache).TrimEnd ('\\', '/') + "\"";
            try {
                // UseShellExecute で新しい窓を開く（入力を受けるため。Unity は終わりを待たない）
                System.Diagnostics.Process.Start (new System.Diagnostics.ProcessStartInfo ("powershell.exe", arguments) { UseShellExecute = true });
            }
            catch (System.Exception e) {
                return Tr ("KIMODO_SETUP_INSTALL_FAILED", e.Message);
            }
            settings.runner = KimodoSettings.kPython;
            settings.pythonPath = Path.Combine (folder, "kimodo", ".venv", "Scripts", "python.exe").Replace ('\\', '/');
            settings.Save ();
            KimodoRunner.ForgetEnvironment ();
            installStarted_ = true;
            return null;
        }

        /// <summary>Project Settings の Kimodo のページ（パッケージの「Lilium Motion Editor」のページの下）</summary>
        public const string kSettingsPath = "Project/Lilium Motion Editor/Kimodo";

        /// <summary>
        /// 設定は Project Settings の窓に出す（ユーザー指示「Lilium Motion Editor のプロジェクト設定に入れてほしい」）。
        /// 保存先は UserSettings のまま: Python のパスなどは PC ごとに違い、ProjectSettings はリポジトリに入るので
        /// </summary>
        [SettingsProvider]
        static SettingsProvider CreateSettingsProvider ()
        {
            return new SettingsProvider (kSettingsPath, SettingsScope.Project) {
                label = "Kimodo",
                keywords = new System.Collections.Generic.HashSet<string> (new[] { "Kimodo", "Python", "Docker", "Hugging Face", "SOMA" }),
                guiHandler = search => {
                    float labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 160;
                    try {
                        EditorGUILayout.HelpBox (Tr ("KIMODO_SETTINGS_SAVED_IN"), MessageType.None);
                        // 入れ方の案内は Kimodo Install の窓だけに置き、ここは設定の欄とその窓を開くボタンにする
                        if (ready) EditorGUILayout.LabelField ("✓ " + Tr ("KIMODO_SETTINGS_READY"), EditorStyles.boldLabel);
                        else EditorGUILayout.HelpBox (Tr ("KIMODO_PANEL_NOT_READY"), MessageType.Warning);
                        if (GUILayout.Button (Tr ("KIMODO_PANEL_OPEN_INSTALL"), GUILayout.Height (24))) KimodoInstallWindow.Open ();
                        EditorGUILayout.Space ();
                        DrawSettings ();
                        using (new EditorGUILayout.HorizontalScope ()) {
                            if (GUILayout.Button (Tr ("KIMODO_SETUP_CHECK"), GUILayout.Height (24))) KimodoRunner.CheckEnvironment ();
                            if (GUILayout.Button (Tr ("KIMODO_SETUP_OPEN_README"), GUILayout.Height (24))) OpenReadme ();
                        }
                    }
                    finally {
                        EditorGUIUtility.labelWidth = labelWidth;
                    }
                },
            };
        }

        public static void OpenSettings ()
        {
            SettingsService.OpenProjectSettings (kSettingsPath);
        }

        /// <summary>済んだ後に開く設定（案内と同じ欄）</summary>
        public static void DrawSettings ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            EditorGUI.BeginChangeCheck ();
            settings.runner = RunnerField (settings.runner);
            if (settings.runner == KimodoSettings.kPython) {
                settings.pythonPath = FileField (Tr ("KIMODO_SETTINGS_PYTHON"), settings.pythonPath, Tr ("KIMODO_SETTINGS_PYTHON_TOOLTIP"));
            }
            else {
                settings.kimodoFolder = FolderField (Tr ("KIMODO_SETTINGS_KIMODO_FOLDER"), settings.kimodoFolder, Tr ("KIMODO_SETTINGS_KIMODO_FOLDER_TOOLTIP"));
                settings.image = EditorGUILayout.TextField (Tr ("KIMODO_SETTINGS_DOCKER_IMAGE"), settings.image);
            }
            settings.huggingFaceCache = FolderField (Tr ("KIMODO_SETTINGS_HUGGING_FACE_CACHE"), settings.huggingFaceCache, Tr ("KIMODO_SETTINGS_HUGGING_FACE_CACHE_TOOLTIP", settings.huggingFaceFolder));
            settings.skeleton = (ExternalSkeleton) EditorGUILayout.ObjectField (Tr ("KIMODO_SETTINGS_SKELETON"), settings.skeleton, typeof (ExternalSkeleton), false);
            if (EditorGUI.EndChangeCheck ()) {
                settings.Save ();
                KimodoRunner.ForgetEnvironment ();
            }
            EditorGUILayout.HelpBox (Tr ("KIMODO_SETTINGS_NOTE"), MessageType.None);
        }

        static void Step (string label, bool done)
        {
            EditorGUILayout.Space ();
            EditorGUILayout.LabelField ((done ? "✓ " : "• ") + label, EditorStyles.boldLabel);
        }

        static string RunnerField (string runner)
        {
            int index = runner == KimodoSettings.kDocker ? 1 : 0;
            index = EditorGUILayout.Popup (Tr ("KIMODO_SETTINGS_RUNNER"), index, new[] { Tr ("KIMODO_SETTINGS_RUNNER_PYTHON"), Tr ("KIMODO_SETTINGS_RUNNER_DOCKER") });
            return index == 1 ? KimodoSettings.kDocker : KimodoSettings.kPython;
        }

        static void Copy (string text)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            Debug.Log (Tr ("KIMODO_SETUP_COPIED") + "\n" + text);
        }

        // README の「Setup」と同じ手順（Windows のコマンドプロンプト）
        static string PythonInstallCommands ()
        {
            return "git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo.git\n" +
                "cd kimodo\n" +
                "uv venv .venv --python 3.11\n" +
                "uv pip install --python .venv\\Scripts\\python.exe torch==2.6.0 --index-url https://download.pytorch.org/whl/cu124\n" +
                "uv pip install --python .venv\\Scripts\\python.exe cmake\n" +
                "set PATH=%CD%\\.venv\\Scripts;%PATH%\n" +
                "uv pip install --python .venv\\Scripts\\python.exe -e .\n" +
                ".venv\\Scripts\\hf auth login\n";
        }

        static string DockerBuildCommands ()
        {
            return "git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo.git\n" +
                "cd kimodo\n" +
                "git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo-viser.git\n" +
                "docker build -t " + KimodoSettings.instance.image + " .\n" +
                "pip install --upgrade huggingface_hub\n" +
                "hf auth login\n";
        }

        /// <summary>T ポーズの BVH を作るコマンド。kimodo のフォルダ（Python なら環境のあるフォルダ）に tpose.bvh ができる</summary>
        static string TPoseCommand ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            const string generate = " \"a person stands still\" --duration 1 --output tpose --bvh --bvh_standard_tpose";
            if (settings.runner == KimodoSettings.kPython) {
                string scripts = Path.GetDirectoryName (Path.GetFullPath (settings.pythonPath));
                return "set TEXT_ENCODER_MODE=local\n" +
                    "set TEXT_ENCODER_DEVICE=cpu\n" +
                    "\"" + Path.Combine (scripts, "kimodo_gen") + "\"" + generate + "\n";
            }
            string kimodo = Path.GetFullPath (settings.kimodoFolder).Replace ('\\', '/');
            string cache = Path.GetFullPath (settings.huggingFaceFolder).Replace ('\\', '/');
            return "docker run --rm --gpus all --shm-size 16g --ipc host --entrypoint /bin/bash" +
                " -v \"" + kimodo + ":/workspace\" -v \"" + cache + ":/workspace/.cache/huggingface\"" +
                " -e HF_HOME=/workspace/.cache/huggingface -e TEXT_ENCODER_MODE=local -e TEXT_ENCODER_DEVICE=cpu " +
                settings.image + " -lc \"kimodo_gen 'a person stands still' --duration 1 --output /workspace/tpose --bvh --bvh_standard_tpose\"\n";
        }

        static void OpenReadme ()
        {
            string folder = SampleFolder ();
            string readme = folder != null ? folder + "/README.md" : null;
            if (readme != null && File.Exists (readme)) EditorUtility.OpenWithDefaultApp (readme);
            else Application.OpenURL (kKimodoUrl);
        }

        /// <summary>
        /// サンプルのフォルダ（Editor の親）。サンプルを取り込んだ場所はプロジェクトごとに違うので、この .cs の場所から探す
        /// </summary>
        public static string SampleFolder ()
        {
            foreach (string guid in AssetDatabase.FindAssets ("KimodoSetup t:MonoScript")) {
                string path = AssetDatabase.GUIDToAssetPath (guid);
                if (Path.GetFileName (path) == "KimodoSetup.cs") return Path.GetDirectoryName (Path.GetDirectoryName (path)).Replace ('\\', '/');
            }
            return null;
        }

        static Texture2D icon_;

        /// <summary>
        /// パネルと Kimodo の窓のアイコン（「AI で作る」の星。Unity の AI のアイコンに似せた大小 2 つのきらめき）。
        /// 10-24 に Kimodo の「K」から替えた（ユーザー指示。今は Kimodo だけだが、将来ほかの生成 AI も選べるようにするので、Kimodo に限らない絵にする）。
        /// 取り込んだ場所が決まらないので [Icon] 属性では付けられず、Editor/Icons から読む。灰色なので、暗いテーマは d_ の明るい方を使う
        /// </summary>
        public static Texture2D icon
        {
            get {
                if (icon_ != null) return icon_;
                string folder = SampleFolder ();
                if (folder == null) return null;
                icon_ = LoadIcon (folder + "/Editor/Icons/" + (EditorGUIUtility.isProSkin ? "d_" : "") + "Generate");
                return icon_;
            }
        }

        static Texture2D kimodoIcon_;

        /// <summary>
        /// Kimodo のものだけの窓（Kimodo Install）のアイコン。Kimodo のバナーの緑（#85B737）で描いた「K」（Kimodo に公式のアイコンは無い）。
        /// 緑は明るいテーマでも暗いテーマでも見えるので分けない
        /// </summary>
        public static Texture2D kimodoIcon
        {
            get {
                if (kimodoIcon_ != null) return kimodoIcon_;
                string folder = SampleFolder ();
                if (folder == null) return null;
                kimodoIcon_ = LoadIcon (folder + "/Editor/Icons/Kimodo");
                return kimodoIcon_;
            }
        }

        static Texture2D LoadIcon (string path)
        {
            Texture2D texture = EditorGUIUtility.pixelsPerPoint > 1 ? AssetDatabase.LoadAssetAtPath<Texture2D> (path + "@2x.png") : null;
            return texture != null ? texture : AssetDatabase.LoadAssetAtPath<Texture2D> (path + ".png");
        }

        public static string FileField (string label, string value, string tooltip)
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

        public static string FolderField (string label, string value, string tooltip)
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
