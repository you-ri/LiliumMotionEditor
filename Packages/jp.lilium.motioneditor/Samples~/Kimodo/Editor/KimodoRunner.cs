using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using static Lilium.KimodoText;

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
        /// <summary>
        /// 取り込んだ後の元のクリップのキーの指紋（S85）。補間として取り込むと境目にキーが足されて指紋が変わるので、
        /// 自動で作るときは、これと今の指紋を比べて作り直すかを決める。送った後でキーが変わっていたら、送ったときの指紋のまま（作り直させる）
        /// </summary>
        public string importedFingerprint;
        /// <summary>キーが変わったので自動で始めたか（S85）。取り込むときに確かめない（作り直すので）</summary>
        public bool auto;
        public string prompt;
        public string mode;
        public string target = kOverride;
        /// <summary>補間として使うとき、外部の動きにする区間</summary>
        public List<BetweenClips.Range> segments = new List<BetweenClips.Range> ();
        /// <summary>始めたときに既に外部の動きだった区間（作っている間に手で外した区間を、取り込みで戻さないため。S85）</summary>
        public List<BetweenClips.Range> externalAtStart = new List<BetweenClips.Range> ();
        /// <summary>文章の読み取りのサーバーを使ったか（S78）</summary>
        public bool server;
        /// <summary>Python の環境で動かしたときのプロセスの番号（止める・再読み込みの後に生きているかを見る）</summary>
        public int pid;
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
        /// <param name="auto">キーが変わったので自動で始める（S85）</param>
        /// <param name="seed">kimodo_gen に渡す乱数の種（負なら渡さず、毎回違う動きになる）</param>
        public static string Start (PreviewWindow window, string prompt, string mode, string target = KimodoJob.kOverride, IList<BetweenClips.Range> segments = null, bool auto = false, int seed = -1)
        {
            if (job_ != null && job_.running) return Tr ("KIMODO_RUN_ALREADY_RUNNING");
            string problem = Check (window, prompt, mode, out ExternalSkeleton skeleton);
            if (problem != null) return problem;
            if (target == KimodoJob.kBetween) {
                if (segments == null || segments.Count == 0) return Tr ("KIMODO_RUN_NO_SEGMENTS");
                foreach (BetweenClips.Range range in segments) {
                    problem = BetweenClips.GetSegmentProblem (window.baseClip, range);
                    if (problem != null) return problem;
                }
            }
            // 文章の読み取りのサーバー（S78）が立ち上がりかけなら待ってもらう（ローカルで読み直すと 4 分かかる）
            KimodoTextServer.State server = KimodoTextServer.state;
            if (server == KimodoTextServer.State.Starting) return Tr ("KIMODO_RUN_SERVER_STARTING");
            AnimationClip clip = window.baseClip;
            AssetDatabase.SaveAssetIfDirty (clip);

            string error = window.ExportKeysToSkeleton (skeleton, out Bvh bvh, out MotionEditorExport export);
            if (error != null) return error;
            if (export.frames.Count < 2) return Tr ("KIMODO_RUN_TOO_FEW_KEYS");
            if (export.lastFrame / export.frameRate > 10) return Tr ("KIMODO_RUN_TOO_LONG");
            string script = FindSampleScript ("kimodo_constraints");
            string generator = FindSampleScript ("kimodo_generate");
            if (script == null || generator == null) return Tr ("KIMODO_RUN_NO_SCRIPTS");

            string folder = Path.Combine (Path.GetFullPath ("Temp"), "Kimodo", SafeName (BaseName (clip)), System.DateTime.Now.ToString ("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory (folder);
            UTF8Encoding utf8 = new UTF8Encoding (false);
            File.WriteAllText (Path.Combine (folder, "keys.bvh"), bvh.ToText (), utf8);
            File.WriteAllText (Path.Combine (folder, "export.json"), JsonUtility.ToJson (export), utf8);
            // 文章はファイルで渡す（コマンドラインに入れると ' や " で壊れる）
            File.WriteAllText (Path.Combine (folder, "prompt.txt"), prompt.Trim (), utf8);
            File.WriteAllText (Path.Combine (folder, "run.sh"), RunScript (mode, seed).Replace ("\r\n", "\n"), utf8);
            File.Copy (script, Path.Combine (folder, "kimodo_constraints.py"), true);
            File.Copy (generator, Path.Combine (folder, "kimodo_generate.py"), true);
            string driver = FindSampleScript ("kimodo_run");
            if (driver != null) File.Copy (driver, Path.Combine (folder, "kimodo_run.py"), true);
            // 文章の読み取りの控え（S78）。同じ文章で作り直すときは、サーバーも文章のモデルの読み込みも要らない
            string textCache = Path.Combine (Path.GetFullPath ("Temp"), "Kimodo", "text-cache");
            Directory.CreateDirectory (textCache);

            KimodoSettings settings = KimodoSettings.instance;
            if (settings.runner == KimodoSettings.kPython) {
                // Python の環境で動かす（Docker を使わない）。手順は kimodo_run.py（run.sh と同じ流れ）
                if (driver == null) return Tr ("KIMODO_RUN_NO_DRIVER");
                ProcessStartInfo info = new ProcessStartInfo (Path.GetFullPath (settings.pythonPath), Quote (Path.Combine (folder, "kimodo_run.py")) + " " + Quote (folder) + " " + mode + SeedArgument (seed)) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = folder,
                };
                info.Environment["PYTHONUNBUFFERED"] = "1";
                info.Environment["TEXT_ENCODER_MODE"] = "local";
                info.Environment["TEXT_ENCODER_DEVICE"] = "cpu";
                info.Environment["LILIUM_TEXT_CACHE"] = textCache;
                if (!string.IsNullOrEmpty (settings.huggingFaceCache)) info.Environment["HF_HOME"] = settings.huggingFaceCache;
                Process python;
                try {
                    python = Process.Start (info);
                }
                catch (System.Exception e) {
                    return Tr ("KIMODO_RUN_PYTHON_FAILED", e.Message);
                }
                process_ = python;
                job_ = new KimodoJob {
                    state = KimodoJob.kRunning,
                    pid = python.Id,
                    folder = folder,
                    baseClip = AssetDatabase.GetAssetPath (clip),
                    destination = DestinationPath (clip),
                    fingerprint = Fingerprint (clip),
                    auto = auto,
                    prompt = prompt.Trim (),
                    mode = mode,
                    target = target,
                    segments = segments != null ? segments.ToList () : new List<BetweenClips.Range> (),
                    externalAtStart = ExternalSegments (clip, segments),
                    started = System.DateTime.Now.Ticks,
                };
                nextContainerCheck_ = EditorApplication.timeSinceStartup + 30;
                Store ();
                return null;
            }
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
                return Tr ("KIMODO_RUN_DOCKER_FAILED", e.Message);
            }
            job_ = new KimodoJob {
                state = KimodoJob.kRunning,
                container = container,
                folder = folder,
                baseClip = AssetDatabase.GetAssetPath (clip),
                destination = DestinationPath (clip),
                fingerprint = Fingerprint (clip),
                auto = auto,
                prompt = prompt.Trim (),
                mode = mode,
                target = target,
                segments = segments != null ? segments.ToList () : new List<BetweenClips.Range> (),
                externalAtStart = ExternalSegments (clip, segments),
                server =server == KimodoTextServer.State.Ready,
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
            if (window == null || window.baseClip == null) return Tr ("KIMODO_RUN_NO_CLIP");
            if (!EditorUtility.IsPersistent (window.baseClip)) return Tr ("KIMODO_RUN_NOT_SAVED");
            if (string.IsNullOrWhiteSpace (prompt)) return Tr ("KIMODO_RUN_NO_PROMPT");
            if (!KimodoSettings.kModes.Contains (mode)) return Tr ("KIMODO_RUN_UNKNOWN_MODE", mode);
            if (skeleton == null) return Tr ("KIMODO_RUN_NO_SKELETON");
            return CheckEnvironment ();
        }

        /// <summary>
        /// 最後に CheckEnvironment で確かめた結果（null なら通った、か、まだ確かめていない）。パネルはこれがあると入れ方の案内に替わる。
        /// python や docker を動かして確かめるので、描くたびには確かめず、生成を始めるときと案内の「Check」で確かめた結果を覚えておく
        /// </summary>
        public static string environmentProblem
        {
            get {
                // SessionState は無いときも既定値でなく空の文字を返す
                string problem = SessionState.GetString (kEnvironmentKey, "");
                return problem.Length > 0 ? problem : null;
            }
            private set {
                if (string.IsNullOrEmpty (value)) SessionState.EraseString (kEnvironmentKey);
                else SessionState.SetString (kEnvironmentKey, value);
                if (changed != null) changed ();
            }
        }

        const string kEnvironmentKey = "Lilium.Kimodo.EnvironmentProblem";

        /// <summary>設定を変えたら、前の確かめの結果を忘れる（直したのに案内のままにならないように）</summary>
        public static void ForgetEnvironment ()
        {
            if (environmentProblem != null) environmentProblem = null;
        }

        /// <summary>
        /// 動かす環境を確かめる（足りなければ理由）。結果は environmentProblem に覚える
        /// </summary>
        public static string CheckEnvironment ()
        {
            string problem = ProbeEnvironment ();
            environmentProblem = problem;
            return problem;
        }

        /// <summary>
        /// Hugging Face のトークンのファイルは、あるかだけを見る（中は読まない）。
        /// Python の環境: python があり、kimodo と motion_correction を読み込めるか（読み込まずに、あるかだけ見る）。Docker: Kimodo のフォルダ・Docker・イメージ
        /// </summary>
        static string ProbeEnvironment ()
        {
            KimodoSettings settings = KimodoSettings.instance;
            if (!File.Exists (Path.Combine (settings.huggingFaceFolder, "token"))) return Tr ("KIMODO_RUN_NO_TOKEN", settings.huggingFaceFolder);
            if (settings.runner == KimodoSettings.kPython) {
                if (string.IsNullOrEmpty (settings.pythonPath) || !File.Exists (settings.pythonPath)) return Tr ("KIMODO_RUN_NO_PYTHON");
                string probe = "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('kimodo') and importlib.util.find_spec('motion_correction') else 3)";
                int code = RunProgram (settings.pythonPath, new[] { "-c", probe }, 30000, out string probed);
                if (code == 3) return Tr ("KIMODO_RUN_NOT_INSTALLED", settings.pythonPath);
                if (code != 0) return Tr ("KIMODO_RUN_PYTHON_BROKEN") + "\n" + Tail (probed, 5);
                return null;
            }
            if (string.IsNullOrEmpty (settings.kimodoFolder) || !Directory.Exists (Path.Combine (settings.kimodoFolder, "kimodo"))) return Tr ("KIMODO_RUN_NO_FOLDER");
            if (!File.Exists (Path.Combine (settings.huggingFaceFolder, "token"))) return Tr ("KIMODO_RUN_NO_TOKEN", settings.huggingFaceFolder);
            if (Run (new[] { "info", "--format", "{{.ServerVersion}}" }, 20000, out string output) != 0) return Tr ("KIMODO_RUN_NO_DOCKER") + "\n" + Tail (output, 5);
            if (Run (new[] { "image", "inspect", "--format", "{{.Id}}", settings.image }, 20000, out output) != 0) return Tr ("KIMODO_RUN_NO_IMAGE", settings.image);
            return null;
        }

        /// <summary>生成を止める（コンテナを消す）</summary>
        public static void Cancel ()
        {
            if (job_ == null || !job_.running) return;
            // Python の環境: 子のプロセスごと止める。Docker: コンテナを消す（docker のプロセスを止めてもコンテナは止まらない）
            if (job_.pid > 0) RunProgram ("taskkill", new[] { "/PID", job_.pid.ToString (), "/T", "/F" }, 30000, out string killed);
            if (!string.IsNullOrEmpty (job_.container)) Run (new[] { "rm", "-f", job_.container }, 30000, out string output);
            if (process_ != null && !process_.HasExited) {
                try { process_.Kill (); } catch (System.Exception) { }
            }
            process_ = null;
            Finish (KimodoJob.kCancelled, Tr ("KIMODO_RUN_STOPPED"));
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
                    Finish (KimodoJob.kGenerated, Tr ("KIMODO_RUN_GENERATED"));
                    TryImport (null);
                }
                else {
                    Finish (KimodoJob.kFailed, Diagnose (Tr ("KIMODO_RUN_EXIT_CODE", text), ReadLog ()));
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
                    if (!File.Exists (job_.exitPath)) Finish (KimodoJob.kFailed, Diagnose (job_.pid > 0 ? Tr ("KIMODO_RUN_PYTHON_EXIT_CODE", code) : Tr ("KIMODO_RUN_DOCKER_EXIT_CODE", code), job_.pid > 0 ? ReadLog () : output));
                }
                return;
            }
            // 再読み込みでプロセスを見失ったときは、プロセス（Python の環境）かコンテナ（Docker）がまだあるかを時々見る
            if (now < nextContainerCheck_) return;
            nextContainerCheck_ = now + kContainerCheckSeconds;
            if (job_.pid > 0) {
                if (!IsAlive (job_.pid) && !File.Exists (job_.exitPath)) Finish (KimodoJob.kFailed, Diagnose (Tr ("KIMODO_RUN_PYTHON_STOPPED"), ReadLog ()));
                return;
            }
            if (Run (new[] { "ps", "-q", "--filter", "name=^" + job_.container + "$" }, 10000, out string ids) == 0 && string.IsNullOrWhiteSpace (ids) && !File.Exists (job_.exitPath)) {
                Finish (KimodoJob.kFailed, Diagnose (Tr ("KIMODO_RUN_CONTAINER_STOPPED"), ReadLog ()));
            }
        }

        /// <summary>
        /// 生成した動きを、元のクリップを開いている窓へ取り込む。window を省くと開いている窓から探す（無ければ後で取り込む）。
        /// 書き出した後に元のキーが変わっていたら確かめる（寄せる先のキーと渡したキーが違うと、黙ってずれる）
        /// </summary>
        public static string TryImport (PreviewWindow window)
        {
            if (job_ == null || job_.state != KimodoJob.kGenerated) return Tr ("KIMODO_RUN_NOTHING_TO_IMPORT");
            if (window == null) window = FindWindow (job_.baseClip);
            if (window == null) {
                job_.message = Tr ("KIMODO_RUN_OPEN_TO_IMPORT", job_.baseClip);
                Store ();
                return job_.message;
            }
            AnimationClip clip = window.baseClip;
            bool keysChanged = Fingerprint (clip) != job_.fingerprint;
            // 作っている間に外部の動きから手で外した区間（曲線・リニア・一定にした）は取り込まない（取り込むと外部の動きへ戻り、選んだ補間が消える）
            List<BetweenClips.Range> segments = job_.segments.Where (r => !job_.externalAtStart.Contains (r) || IsExternal (clip, r)).ToList ();
            if (keysChanged && job_.auto) {
                // 自動のときは確かめずに、まだ区間のままの所だけ取り込む（すぐ作り直すので、それまでの見た目に使う）
                segments = segments.Where (r => BetweenClips.GetSegmentProblem (clip, r) == null).ToList ();
                if (job_.target == KimodoJob.kBetween && segments.Count == 0) {
                    job_.message = Tr ("KIMODO_RUN_KEYS_CHANGED_AUTO");
                    Store ();
                    return job_.message;
                }
            }
            else if (keysChanged) {
                if (!EditorUtility.DisplayDialog ("Kimodo", Tr ("KIMODO_RUN_KEYS_CHANGED_DIALOG", clip.name), Tr ("KIMODO_PANEL_IMPORT"), Tr ("KIMODO_RUN_NOT_NOW"))) {
                    job_.message = Tr ("KIMODO_RUN_KEYS_CHANGED");
                    Store ();
                    return job_.message;
                }
            }
            ExternalSkeleton skeleton = KimodoSettings.instance.skeleton;
            Bvh bvh = Bvh.Parse (File.ReadAllText (job_.bvhPath), out string error);
            if (bvh == null) return Fail (error);
            if (job_.target == KimodoJob.kBetween) {
                // キーの間の補間として使う（S83）。結果のファイルは丸ごと置き換え、選んだ区間を外部の動きにする
                error = window.ImportBetween (bvh, skeleton, segments, out PoseSession.OverrideImportResult between);
                if (error != null) return Fail (error);
                job_.segments = segments;
                job_.keyDeviations = new List<PoseSession.KeyDeviation> ();
                job_.importedFingerprint = keysChanged ? job_.fingerprint : Fingerprint (clip);
                Finish (KimodoJob.kImported, Tr ("KIMODO_RUN_IMPORTED_BETWEEN", job_.segments.Count, BetweenClips.PathFor (clip)));
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
            job_.importedFingerprint = keysChanged ? job_.fingerprint : Fingerprint (clip);
            Finish (KimodoJob.kImported, Tr ("KIMODO_RUN_IMPORTED_OVERRIDE", job_.destination));
            return null;
        }

        static string Fail (string error)
        {
            job_.message = Tr ("KIMODO_RUN_IMPORT_FAILED", error);
            Store ();
            return job_.message;
        }

        /// <summary>元のクリップ（パス）を開いている Motion Editor の窓</summary>
        public static PreviewWindow FindWindow (string clipPath)
        {
            return Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault (w => w.baseClip != null && AssetDatabase.GetAssetPath (w.baseClip) == clipPath);
        }

        static List<BetweenClips.Range> ExternalSegments (AnimationClip clip, IEnumerable<BetweenClips.Range> segments)
        {
            return segments != null ? segments.Where (r => IsExternal (clip, r)).ToList () : new List<BetweenClips.Range> ();
        }

        static bool IsExternal (AnimationClip clip, BetweenClips.Range range)
        {
            BetweenClips.State state = BetweenClips.GetSegmentState (clip, range);
            return state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale;
        }

        /// <summary>
        /// clip のどのキーで最後に作ったか（指紋。S85）。自動で作るとき、今の指紋がこれと同じなら作り直さない。
        /// 取り込んだなら取り込んだ後の指紋、それ以外（作っている・失敗した・止めた）は送ったときの指紋（同じキーで失敗を繰り返さない）。
        /// 作っていない・ほかのクリップのものなら null
        /// </summary>
        public static string GeneratedFingerprint (AnimationClip clip)
        {
            if (job_ == null || clip == null || job_.baseClip != AssetDatabase.GetAssetPath (clip)) return null;
            if (job_.state == KimodoJob.kImported && !string.IsNullOrEmpty (job_.importedFingerprint)) return job_.importedFingerprint;
            return job_.fingerprint;
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
            if (log.Contains ("could not select device driver") || log.Contains ("nvidia-container")) hint = Tr ("KIMODO_RUN_HINT_GPU");
            else if (log.Contains ("CUDA out of memory")) hint = Tr ("KIMODO_RUN_HINT_GPU_MEMORY");
            else if (log.Contains ("401") || log.Contains ("gated") || log.Contains ("Access to model")) hint = Tr ("KIMODO_RUN_HINT_HUGGING_FACE");
            else if (what.EndsWith (" 137") || log.Contains ("Killed")) hint = Tr ("KIMODO_RUN_HINT_WSL_MEMORY");
            return Tr ("KIMODO_RUN_FAILED", what) + (hint != null ? "\n" + hint : "") + "\n\n" + Tail (log, 30);
        }

        static bool WantsToQuit ()
        {
            if (job_ == null || !job_.running) return true;
            int choice = EditorUtility.DisplayDialogComplex ("Kimodo", Tr ("KIMODO_RUN_QUIT_DIALOG"), Tr ("KIMODO_STOP_AND_QUIT"), Tr ("KIMODO_RUN_KEEP_GENERATING"), Tr ("KIMODO_QUIT_AND_LEAVE"));
            if (choice == 1) return false;
            if (choice == 0) Cancel ();
            return true;
        }

        /// <summary>コンテナの中の手順。終わったら終了コードを exit.txt へ（書き終えてから名前を変えるので、半端な中身は読まない）</summary>
        static string RunScript (string mode, int seed)
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
                "  LILIUM_TEXT_CACHE=/cache python /work/kimodo_generate.py \"$(cat /work/prompt.txt)\" --duration \"$DURATION\" --constraints /work/constraints.json --output /work/gen --bvh --bvh_standard_tpose" + SeedArgument (seed) + "\n" +
                "}\n" +
                "generate > /work/log.txt 2>&1\n" +
                "echo $? > /work/exit.tmp && mv /work/exit.tmp /work/exit.txt\n";
        }

        /// <summary>kimodo_gen に足す --seed（種が負なら空）</summary>
        static string SeedArgument (int seed)
        {
            return seed >= 0 ? " --seed " + seed : "";
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

        static bool IsAlive (int pid)
        {
            try {
                return !Process.GetProcessById (pid).HasExited;
            }
            catch (System.Exception) {
                return false;
            }
        }

        static ProcessStartInfo StartInfo (IEnumerable<string> arguments)
        {
            return StartInfo ("docker", arguments);
        }

        static ProcessStartInfo StartInfo (string file, IEnumerable<string> arguments)
        {
            return new ProcessStartInfo (file, string.Join (" ", arguments.Select (Quote))) {
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
            return RunProgram ("docker", arguments, timeout, out output);
        }

        /// <summary>プログラムを待って動かす（短いものだけ）。終了コード（起動できない・時間切れは -1）</summary>
        internal static int RunProgram (string file, IEnumerable<string> arguments, int timeout, out string output)
        {
            try {
                using (Process process = Process.Start (StartInfo (file, arguments))) {
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
