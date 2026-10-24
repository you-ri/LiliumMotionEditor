using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>窓から始めた 1 回の生成（S77）。スクリプトの再読み込みを跨いで続けるので SessionState に JSON で控える</summary>
    [System.Serializable]
    public sealed class KimodoJob
    {
        public const string kRunning = "running";
        public const string kGenerated = "generated";
        public const string kImported = "imported";
        public const string kFailed = "failed";
        public const string kCancelled = "cancelled";
        /// <summary>結果の使い方: 元のクリップに重ねる Override（S77）か、キーの間の補間（S83。BetweenClips）か</summary>
        public const string kOverride = "override";
        public const string kBetween = "between";

        public string state;
        public string container;
        public string folder;
        public string baseClip;
        public string destination;
        /// <summary>書き出したときの元のクリップのキーの指紋（取り込む前に変わっていないか見る）</summary>
        public string fingerprint;
        public string prompt;
        public string mode;
        public string target = kOverride;
        /// <summary>補間として使うとき、外部の動きにする区間</summary>
        public List<BetweenClips.Range> segments = new List<BetweenClips.Range> ();
        /// <summary>文章の読み取りのサーバーを使ったか（S78）</summary>
        public bool server;
        public long started;
        public float seconds;
        public string message;
        public List<PoseSession.KeyDeviation> keyDeviations = new List<PoseSession.KeyDeviation> ();

        public bool running { get { return state == kRunning; } }
        public string bvhPath { get { return Path.Combine (folder, "gen.bvh"); } }
        public string logPath { get { return Path.Combine (folder, "log.txt"); } }
        public string exitPath { get { return Path.Combine (folder, "exit.txt"); } }
    }

    /// <summary>
    /// Kimodo を Docker で動かし、出てきた動きを Motion Editor の窓へ Override として取り込む（S77）。
    /// コンテナには名前を付けて起動する（docker のプロセスを止めてもコンテナは止まらないので、止めるときは名前で消す）。
    /// コンテナの中の手順は、終わると作業フォルダに終了コード（exit.txt）を書く。こちらはそれを見て終わりを知るので、
    /// スクリプトの再読み込みで docker のプロセスを見失っても、終わったら取り込める
    /// </summary>
    [InitializeOnLoad]
    public static class KimodoRunner
    {
        const string kSessionKey = "Lilium.Kimodo.Job";
        const double kPollSeconds = 1;
        const double kContainerCheckSeconds = 5;

        static KimodoJob job_;
        static Process process_;
        static readonly StringBuilder processOutput_ = new StringBuilder ();
        static double nextPoll_;
        static double nextContainerCheck_;

        public static event System.Action changed;

        static KimodoRunner ()
        {
            string json = SessionState.GetString (kSessionKey, "");
            if (!string.IsNullOrEmpty (json)) job_ = JsonUtility.FromJson<KimodoJob> (json);
            EditorApplication.update += Poll;
            EditorApplication.wantsToQuit += WantsToQuit;
        }

        public static KimodoJob job
        {
            get { return job_; }
        }

        static void Store ()
        {
            SessionState.SetString (kSessionKey, job_ != null ? JsonUtility.ToJson (job_) : "");
            if (changed != null) changed ();
        }

        /// <summary>
        /// 窓の元のクリップのキーと文章から生成を始める。始められなければ理由を返す（Docker は起動しない）
        /// </summary>
        /// <param name="target">結果の使い方（KimodoJob.kOverride か kBetween）</param>
        /// <param name="segments">補間として使うとき、外部の動きにする区間（元のクリップの隣り合うキーの間）</param>
        public static string Start (PreviewWindow window, string prompt, string mode, string target = KimodoJob.kOverride, IList<BetweenClips.Range> segments = null)
        {
            if (job_ != null && job_.running) return "A generation is already running.";
            string problem = Check (window, prompt, mode, out ExternalSkeleton skeleton);
            if (problem != null) return problem;
            if (target == KimodoJob.kBetween) {
                if (segments == null || segments.Count == 0) return "Choose the segments to fill with Kimodo's motion.";
                foreach (BetweenClips.Range range in segments) {
                    problem = BetweenClips.GetSegmentProblem (window.baseClip, range);
                    if (problem != null) return problem;
                }
            }
            // 文章の読み取りのサーバー（S78）が立ち上がりかけなら待ってもらう（ローカルで読み直すと 4 分かかる）
            KimodoTextServer.State server = KimodoTextServer.state;
            if (server == KimodoTextServer.State.Starting) return "The text server is still starting. Wait until it is ready, or stop it.";
            AnimationClip clip = window.baseClip;
            AssetDatabase.SaveAssetIfDirty (clip);

            string error = window.ExportKeysToSkeleton (skeleton, out Bvh bvh, out MotionEditorExport export);
            if (error != null) return error;
            if (export.frames.Count < 2) return "Key at least two frames of the clip. Kimodo fills the frames between the keys.";
            if (export.lastFrame / export.frameRate > 10) return "The clip is longer than 10 seconds (the longest Kimodo makes).";
            string script = FindSampleScript ("kimodo_constraints");
            string generator = FindSampleScript ("kimodo_generate");
            if (script == null || generator == null) return "The Python scripts of the Kimodo sample are missing. Import the sample again from the Package Manager.";

            string folder = Path.Combine (Path.GetFullPath ("Temp"), "Kimodo", SafeName (BaseName (clip)), System.DateTime.Now.ToString ("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory (folder);
            UTF8Encoding utf8 = new UTF8Encoding (false);
            File.WriteAllText (Path.Combine (folder, "keys.bvh"), bvh.ToText (), utf8);
            File.WriteAllText (Path.Combine (folder, "export.json"), JsonUtility.ToJson (export), utf8);
            // 文章はファイルで渡す（コマンドラインに入れると ' や " で壊れる）
            File.WriteAllText (Path.Combine (folder, "prompt.txt"), prompt.Trim (), utf8);
            File.WriteAllText (Path.Combine (folder, "run.sh"), RunScript (mode).Replace ("\r\n", "\n"), utf8);
            File.Copy (script, Path.Combine (folder, "kimodo_constraints.py"), true);
            File.Copy (generator, Path.Combine (folder, "kimodo_generate.py"), true);
            // 文章の読み取りの控え（S78）。同じ文章で作り直すときは、サーバーも文章のモデルの読み込みも要らない
            string textCache = Path.Combine (Path.GetFullPath ("Temp"), "Kimodo", "text-cache");
            Directory.CreateDirectory (textCache);

            KimodoSettings settings = KimodoSettings.instance;
            string container = "lilium-kimodo-" + System.Guid.NewGuid ().ToString ("N").Substring (0, 8);
            string[] arguments = {
                "run", "--rm", "--name", container, "--gpus", "all", "--shm-size", "16g", "--ipc", "host", "--entrypoint", "/bin/bash",
                "-v", DockerPath (settings.kimodoFolder) + ":/workspace",
                "-v", DockerPath (folder) + ":/work",
                "-v", DockerPath (textCache) + ":/cache",
                "-v", DockerPath (settings.huggingFaceFolder) + ":/workspace/.cache/huggingface",
                "-e", "HF_HOME=/workspace/.cache/huggingface",
                // サーバーがあれば文章の読み取りはサーバーで（api。auto はサーバーが落ちていると黙ってローカルで読み直す）
                "-e", server == KimodoTextServer.State.Ready ? "TEXT_ENCODER_MODE=api" : "TEXT_ENCODER_MODE=local",
                "-e", "TEXT_ENCODER_URL=" + KimodoTextServer.kUrlFromContainer,
                "-e", "TEXT_ENCODER_DEVICE=cpu",
                "-e", "PYTHONUNBUFFERED=1",
                settings.image, "-l", "/work/run.sh",
            };
            processOutput_.Clear ();
            try {
                process_ = Launch (arguments);
            }
            catch (System.Exception e) {
                return "Docker could not be started: " + e.Message;
            }
            job_ = new KimodoJob {
                state = KimodoJob.kRunning,
                container = container,
                folder = folder,
                baseClip = AssetDatabase.GetAssetPath (clip),
                destination = DestinationPath (clip),
                fingerprint = Fingerprint (clip),
                prompt = prompt.Trim (),
                mode = mode,
                target = target,
                segments = segments != null ? segments.ToList () : new List<BetweenClips.Range> (),
                server = server == KimodoTextServer.State.Ready,
                started = System.DateTime.Now.Ticks,
            };
            nextContainerCheck_ = EditorApplication.timeSinceStartup + 30;
            Store ();
            return null;
        }

        /// <summary>
        /// Docker を起動する前に、足りないものを確かめる（足りなければ理由。4 分待ってから失敗しないように）。
        /// Hugging Face のトークンは、ファイルがあるかだけを見る（中は読まない）
        /// </summary>
        public static string Check (PreviewWindow window, string prompt, string mode, out ExternalSkeleton skeleton)
        {
            KimodoSettings settings = KimodoSettings.instance;
            skeleton = settings.skeleton;
            if (window == null || window.baseClip == null) return "Open a clip in the Motion Editor window.";
            if (!EditorUtility.IsPersistent (window.baseClip)) return "Save the clip as an asset first.";
            if (string.IsNullOrWhiteSpace (prompt)) return "Write the prompt (English). Kimodo needs it.";
            if (!KimodoSettings.kModes.Contains (mode)) return "Unknown constraint mode: " + mode;
            if (skeleton == null) return "Set the SOMA skeleton (Tools > Lilium Motion Editor > Kimodo > Create SOMA Skeleton From BVH...).";
            return CheckEnvironment ();
        }

        /// <summary>Kimodo のフォルダ・Hugging Face のトークンのファイル（中は読まない）・Docker・イメージを確かめる（足りなければ理由）</summary>
        public static string CheckEnvironment ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            if (string.IsNullOrEmpty (settings.kimodoFolder) || !Directory.Exists (Path.Combine (settings.kimodoFolder, "kimodo"))) return "Set the kimodo folder (the git clone of Kimodo).";
            if (!File.Exists (Path.Combine (settings.huggingFaceFolder, "token"))) return "No Hugging Face token in " + settings.huggingFaceFolder + ". Run hf auth login.";
            if (Run (new[] { "info", "--format", "{{.ServerVersion}}" }, 20000, out string output) != 0) return "Docker is not running.\n" + Tail (output, 5);
            if (Run (new[] { "image", "inspect", "--format", "{{.Id}}", settings.image }, 20000, out output) != 0) return "The Docker image " + settings.image + " is not built.";
            return null;
        }

        /// <summary>生成を止める（コンテナを消す）</summary>
        public static void Cancel ()
        {
            if (job_ == null || !job_.running) return;
            Run (new[] { "rm", "-f", job_.container }, 30000, out string output);
            if (process_ != null && !process_.HasExited) {
                try { process_.Kill (); } catch (System.Exception) { }
            }
            process_ = null;
            Finish (KimodoJob.kCancelled, "Stopped.");
        }

        public static void Clear ()
        {
            if (job_ != null && job_.running) return;
            job_ = null;
            Store ();
        }

        static void Finish (string state, string message)
        {
            job_.state = state;
            job_.message = message;
            job_.seconds = (float) System.TimeSpan.FromTicks (System.DateTime.Now.Ticks - job_.started).TotalSeconds;
            Store ();
        }

        static void Poll ()
        {
            if (job_ == null || !job_.running) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll_) return;
            nextPoll_ = now + kPollSeconds;
            if (changed != null) changed ();

            if (File.Exists (job_.exitPath)) {
                process_ = null;
                string text = File.ReadAllText (job_.exitPath).Trim ();
                if (text == "0" && File.Exists (job_.bvhPath)) {
                    Finish (KimodoJob.kGenerated, "Generated.");
                    TryImport (null);
                }
                else {
                    Finish (KimodoJob.kFailed, Diagnose ("exit code " + text, ReadLog ()));
                }
                return;
            }
            // コンテナが始まる前に docker が止まった（GPU が使えないなど）。手順は動いていないので exit.txt は無い
            if (process_ != null) {
                if (process_.HasExited) {
                    string output;
                    lock (processOutput_) output = processOutput_.ToString ();
                    int code = process_.ExitCode;
                    process_ = null;
                    if (!File.Exists (job_.exitPath)) Finish (KimodoJob.kFailed, Diagnose ("docker exit code " + code, output));
                }
                return;
            }
            // 再読み込みで docker のプロセスを見失ったときは、コンテナがまだあるかを時々見る
            if (now < nextContainerCheck_) return;
            nextContainerCheck_ = now + kContainerCheckSeconds;
            if (Run (new[] { "ps", "-q", "--filter", "name=^" + job_.container + "$" }, 10000, out string ids) == 0 && string.IsNullOrWhiteSpace (ids) && !File.Exists (job_.exitPath)) {
                Finish (KimodoJob.kFailed, Diagnose ("the container stopped", ReadLog ()));
            }
        }

        /// <summary>
        /// 生成した動きを、元のクリップを開いている窓へ取り込む。window を省くと開いている窓から探す（無ければ後で取り込む）。
        /// 書き出した後に元のキーが変わっていたら確かめる（寄せる先のキーと渡したキーが違うと、黙ってずれる）
        /// </summary>
        public static string TryImport (PreviewWindow window)
        {
            if (job_ == null || job_.state != KimodoJob.kGenerated) return "Nothing to import.";
            if (window == null) window = FindWindow (job_.baseClip);
            if (window == null) {
                job_.message = "Generated. Open " + job_.baseClip + " in the Motion Editor window and press Import.";
                Store ();
                return job_.message;
            }
            AnimationClip clip = window.baseClip;
            if (Fingerprint (clip) != job_.fingerprint) {
                if (!EditorUtility.DisplayDialog ("Kimodo", "The keys of " + clip.name + " changed after they were sent to Kimodo. Import the motion anyway?", "Import", "Not Now")) {
                    job_.message = "Generated. The keys changed after they were sent; import or generate again.";
                    Store ();
                    return job_.message;
                }
            }
            ExternalSkeleton skeleton = KimodoSettings.instance.skeleton;
            Bvh bvh = Bvh.Parse (File.ReadAllText (job_.bvhPath), out string error);
            if (bvh == null) return Fail (error);
            if (job_.target == KimodoJob.kBetween) {
                // キーの間の補間として使う（S83）。結果のファイルは丸ごと置き換え、選んだ区間を外部の動きにする
                error = window.ImportBetween (bvh, skeleton, job_.segments, out PoseSession.OverrideImportResult between);
                if (error != null) return Fail (error);
                job_.keyDeviations = new List<PoseSession.KeyDeviation> ();
                Finish (KimodoJob.kImported, "Used as the interpolation of " + job_.segments.Count + " segment(s): " + BetweenClips.PathFor (clip) + ".");
                return null;
            }
            AnimationClip destination = AssetDatabase.LoadAssetAtPath<AnimationClip> (job_.destination);
            if (destination == null) {
                destination = new AnimationClip { frameRate = clip.frameRate };
                AssetDatabase.CreateAsset (destination, job_.destination);
            }
            PoseSession.OverrideImportOptions options = new PoseSession.OverrideImportOptions { fingers = false, keepKeys = true };
            error = window.ImportOverride (bvh, skeleton, destination, OverrideMode.Additive, options, out PoseSession.OverrideImportResult result);
            if (error != null) return Fail (error);
            job_.keyDeviations = result.keyDeviations;
            Finish (KimodoJob.kImported, "Imported into " + job_.destination + ".");
            return null;
        }

        static string Fail (string error)
        {
            job_.message = "Could not import: " + error;
            Store ();
            return job_.message;
        }

        /// <summary>元のクリップ（パス）を開いている Motion Editor の窓</summary>
        public static PreviewWindow FindWindow (string clipPath)
        {
            return Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault (w => w.baseClip != null && AssetDatabase.GetAssetPath (w.baseClip) == clipPath);
        }

        /// <summary>取り込んだ Override が窓の何枚目か（無ければ -1）</summary>
        public static int FindOverride (PreviewWindow window, string clipPath)
        {
            if (window == null) return -1;
            for (int i = 0; i < window.overrides.Count; i++) {
                if (window.overrides[i].clip != null && AssetDatabase.GetAssetPath (window.overrides[i].clip) == clipPath) return i;
            }
            return -1;
        }

        public static string ReadLog ()
        {
            if (job_ == null || !File.Exists (job_.logPath)) return "";
            try {
                using (FileStream stream = new FileStream (job_.logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader (stream)) return reader.ReadToEnd ();
            }
            catch (IOException) {
                return "";
            }
        }

        /// <summary>ログの末尾 count 行（進み具合の行は \r で上書きされるので、最後の分だけ）</summary>
        public static string Tail (string text, int count)
        {
            string[] lines = text.Split ('\n').Select (l => l.Split ('\r').LastOrDefault (s => s.Trim ().Length > 0) ?? "").Where (l => l.Trim ().Length > 0).ToArray ();
            return string.Join ("\n", lines.Skip (Mathf.Max (0, lines.Length - count)));
        }

        /// <summary>失敗の理由に、よくある原因を添える</summary>
        static string Diagnose (string what, string log)
        {
            string hint = null;
            if (log.Contains ("could not select device driver") || log.Contains ("nvidia-container")) hint = "Docker cannot use the NVIDIA GPU. Check the GPU driver and Docker's GPU support.";
            else if (log.Contains ("CUDA out of memory")) hint = "The GPU ran out of memory. Close other programs that use the GPU.";
            else if (log.Contains ("401") || log.Contains ("gated") || log.Contains ("Access to model")) hint = "Hugging Face refused the download. Check that your access to meta-llama/Meta-Llama-3-8B-Instruct was approved and run hf auth login.";
            else if (what.EndsWith (" 137") || log.Contains ("Killed")) hint = "The container ran out of memory. Raise the WSL memory limit in %USERPROFILE%\\.wslconfig ([wsl2] memory=24GB).";
            return "Failed (" + what + ")." + (hint != null ? "\n" + hint : "") + "\n\n" + Tail (log, 30);
        }

        static bool WantsToQuit ()
        {
            if (job_ == null || !job_.running) return true;
            int choice = EditorUtility.DisplayDialogComplex ("Kimodo", "Kimodo is still generating. Stop it and quit?", "Stop and Quit", "Keep Generating", "Quit and Leave It Running");
            if (choice == 1) return false;
            if (choice == 0) Cancel ();
            return true;
        }

        /// <summary>コンテナの中の手順。終わったら終了コードを exit.txt へ（書き終えてから名前を変えるので、半端な中身は読まない）</summary>
        static string RunScript (string mode)
        {
            return
                "#!/bin/bash\n" +
                "# Written by the Kimodo sample of Lilium Motion Editor for one generation.\n" +
                "cd /work\n" +
                "generate () {\n" +
                "  python /work/kimodo_constraints.py /work/keys.bvh /work/export.json /work/constraints.json --mode " + mode + " > /work/constraints_out.txt || return $?\n" +
                "  cat /work/constraints_out.txt\n" +
                "  DURATION=$(tail -n 1 /work/constraints_out.txt | python -c 'import json, sys; print(json.load(sys.stdin)[\"duration\"])') || return $?\n" +
                // kimodo_gen を、文章の読み取りを控える読み取り器で動かす（S78。kimodo_generate.py）
                "  LILIUM_TEXT_CACHE=/cache python /work/kimodo_generate.py \"$(cat /work/prompt.txt)\" --duration \"$DURATION\" --constraints /work/constraints.json --output /work/gen --bvh --bvh_standard_tpose\n" +
                "}\n" +
                "generate > /work/log.txt 2>&1\n" +
                "echo $? > /work/exit.tmp && mv /work/exit.tmp /work/exit.txt\n";
        }

        /// <summary>サンプルの Python のスクリプト（name.py）のフルパス（無ければ null）</summary>
        static string FindSampleScript (string name)
        {
            foreach (string guid in AssetDatabase.FindAssets (name)) {
                string path = AssetDatabase.GUIDToAssetPath (guid);
                if (Path.GetFileName (path) == name + ".py") return Path.GetFullPath (path);
            }
            return null;
        }

        /// <summary>取り込む先: 元のクリップの隣の「名前.kimodo.override.anim」（編集用クリップの .rig は外す）</summary>
        static string DestinationPath (AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath (clip);
            return Path.Combine (Path.GetDirectoryName (path), BaseName (clip) + ".kimodo.override.anim").Replace ('\\', '/');
        }

        static string BaseName (AnimationClip clip)
        {
            string name = Path.GetFileNameWithoutExtension (AssetDatabase.GetAssetPath (clip));
            return name.EndsWith (".rig") ? name.Substring (0, name.Length - 4) : name;
        }

        /// <summary>元のクリップのキーの指紋（カーブの名前とキーの時間と値）</summary>
        public static string Fingerprint (AnimationClip clip)
        {
            StringBuilder text = new StringBuilder ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip).OrderBy (b => b.path).ThenBy (b => b.propertyName)) {
                text.Append (binding.path).Append ('/').Append (binding.propertyName).Append (':');
                foreach (Keyframe key in AnimationUtility.GetEditorCurve (clip, binding).keys) {
                    text.Append (key.time.ToString ("R")).Append (',').Append (key.value.ToString ("R")).Append (';');
                }
            }
            return Hash128.Compute (text.ToString ()).ToString ();
        }

        static string SafeName (string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars ()) name = name.Replace (c, '_');
            return name.Replace (' ', '_');
        }

        internal static string DockerPath (string path)
        {
            return Path.GetFullPath (path).Replace ('\\', '/');
        }

        /// <summary>Windows のコマンドラインの決まりで 1 つの引数にする（空白を含むパスでも 1 つで渡る）</summary>
        static string Quote (string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny (new[] { ' ', '\t', '"' }) < 0) return argument;
            StringBuilder quoted = new StringBuilder ("\"");
            int backslashes = 0;
            foreach (char c in argument) {
                if (c == '\\') {
                    backslashes++;
                    continue;
                }
                quoted.Append ('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append (c);
            }
            quoted.Append ('\\', backslashes * 2).Append ('"');
            return quoted.ToString ();
        }

        static ProcessStartInfo StartInfo (IEnumerable<string> arguments)
        {
            return new ProcessStartInfo ("docker", string.Join (" ", arguments.Select (Quote))) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        static Process Launch (IEnumerable<string> arguments)
        {
            Process process = new Process { StartInfo = StartInfo (arguments) };
            // 出力はスレッドプールから来るので、Unity の API は触らずに溜めるだけ
            process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (processOutput_) processOutput_.AppendLine (e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (processOutput_) processOutput_.AppendLine (e.Data); };
            process.Start ();
            process.BeginOutputReadLine ();
            process.BeginErrorReadLine ();
            return process;
        }

        /// <summary>docker を待って動かす（短いものだけ）。終了コード（起動できない・時間切れは -1）</summary>
        internal static int Run (IEnumerable<string> arguments, int timeout, out string output)
        {
            try {
                using (Process process = Process.Start (StartInfo (arguments))) {
                    System.Threading.Tasks.Task<string> standard = process.StandardOutput.ReadToEndAsync ();
                    System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync ();
                    if (!process.WaitForExit (timeout)) {
                        try { process.Kill (); } catch (System.Exception) { }
                        output = "";
                        return -1;
                    }
                    output = standard.Result + error.Result;
                    return process.ExitCode;
                }
            }
            catch (System.Exception e) {
                output = e.Message;
                return -1;
            }
        }
    }

}
