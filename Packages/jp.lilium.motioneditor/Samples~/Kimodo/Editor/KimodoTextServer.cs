using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using static Lilium.KimodoText;

namespace Lilium
{

    /// <summary>
    /// Kimodo の文章の読み取り（Llama 3 8B）を常駐させるサーバー（S78）。生成の時間のほとんどは文章の読み取りの読み込みなので、
    /// Kimodo のサーバー（kimodo.scripts.run_text_encoder_server。Gradio・9550 番）を立てたままにし、生成のコンテナを
    /// TEXT_ENCODER_MODE=api でつなぐ（auto にしない。サーバーが落ちていると黙ってローカルで読み直して 4 分かかる）。
    /// サーバーは CPU で動き、止めるまで PC のメモリを約 15GB 使い続ける
    /// </summary>
    [InitializeOnLoad]
    public static class KimodoTextServer
    {
        public const string kContainer = "lilium-kimodo-text";
        public const int kPort = 9550;
        /// <summary>生成のコンテナから見たサーバー（Docker Desktop のホスト）</summary>
        public const string kUrlFromContainer = "http://host.docker.internal:9550/";
        const double kPollSeconds = 3;

        public enum State
        {
            Stopped,
            Starting,
            Ready,
        }

        static State state_ = State.Stopped;
        static bool firstCheck_ = true;
        static double nextPoll_;
        static volatile bool answered_;
        static bool checking_;
        static string log_ = "";
        static readonly HttpClient http_ = new HttpClient { Timeout = System.TimeSpan.FromSeconds (2) };

        public static event System.Action changed;

        static KimodoTextServer ()
        {
            EditorApplication.update += Poll;
            EditorApplication.wantsToQuit += WantsToQuit;
        }

        public static State state
        {
            get { return state_; }
        }

        /// <summary>立ち上がるまでのログの末尾</summary>
        public static string log
        {
            get { return log_; }
        }

        /// <summary>サーバーを立てる。立てられなければ理由（Docker は起動しない）</summary>
        public static string Start ()
        {
            if (state_ != State.Stopped) return null;
            string problem = KimodoRunner.CheckEnvironment ();
            if (problem != null) return problem;
            KimodoSettings settings = KimodoSettings.instance;
            KimodoRunner.Run (new[] { "rm", "-f", kContainer }, 30000, out string ignored);
            string[] arguments = {
                "run", "-d", "--rm", "--name", kContainer, "-p", "127.0.0.1:" + kPort + ":" + kPort,
                "--shm-size", "16g", "--ipc", "host", "--entrypoint", "/bin/bash",
                "-v", KimodoRunner.DockerPath (settings.kimodoFolder) + ":/workspace",
                "-v", KimodoRunner.DockerPath (settings.huggingFaceFolder) + ":/workspace/.cache/huggingface",
                "-e", "HF_HOME=/workspace/.cache/huggingface",
                "-e", "TEXT_ENCODER_DEVICE=cpu",
                "-e", "PYTHONUNBUFFERED=1",
                settings.image, "-lc", "cd /workspace && python -m kimodo.scripts.run_text_encoder_server",
            };
            if (KimodoRunner.Run (arguments, 60000, out string output) != 0) return Tr ("KIMODO_SERVER_FAILED") + "\n" + KimodoRunner.Tail (output, 10);
            state_ = State.Starting;
            log_ = "";
            nextPoll_ = 0;
            Changed ();
            return null;
        }

        /// <summary>サーバーを止める（コンテナを消す）</summary>
        public static void Stop ()
        {
            KimodoRunner.Run (new[] { "rm", "-f", kContainer }, 30000, out string ignored);
            state_ = State.Stopped;
            answered_ = false;
            log_ = "";
            Changed ();
        }

        static void Changed ()
        {
            if (changed != null) changed ();
        }

        /// <summary>コンテナがあるかと、HTTP で答えるかを時々見る（答えは別のスレッドで待つ）</summary>
        static void Poll ()
        {
            // 止まっている間は見回らない（docker を呼ぶと少し止まる）。読み込み直した直後だけは、前から立っているかを 1 回見る
            if (state_ == State.Stopped && !firstCheck_) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll_) return;
            nextPoll_ = now + kPollSeconds;
            firstCheck_ = false;
            State before = state_;
            bool running = KimodoRunner.Run (new[] { "ps", "-q", "--filter", "name=^" + kContainer + "$" }, 5000, out string ids) == 0 && !string.IsNullOrWhiteSpace (ids);
            if (!running) {
                state_ = State.Stopped;
                answered_ = false;
            }
            else {
                if (!checking_) {
                    checking_ = true;
                    Task.Run (async () => {
                        try {
                            HttpResponseMessage response = await http_.GetAsync ("http://127.0.0.1:" + kPort + "/");
                            answered_ = response.IsSuccessStatusCode;
                        }
                        catch (System.Exception) {
                            answered_ = false;
                        }
                        finally {
                            checking_ = false;
                        }
                    });
                }
                state_ = answered_ ? State.Ready : State.Starting;
                if (state_ == State.Starting && KimodoRunner.Run (new[] { "logs", "--tail", "4", kContainer }, 5000, out string output) == 0) log_ = KimodoRunner.Tail (output, 4);
            }
            if (state_ != before || state_ == State.Starting) Changed ();
        }

        static bool WantsToQuit ()
        {
            if (state_ == State.Stopped) return true;
            int choice = EditorUtility.DisplayDialogComplex ("Kimodo", Tr ("KIMODO_SERVER_QUIT_DIALOG"), Tr ("KIMODO_STOP_AND_QUIT"), Tr ("KIMODO_CANCEL"), Tr ("KIMODO_QUIT_AND_LEAVE"));
            if (choice == 1) return false;
            if (choice == 0) Stop ();
            return true;
        }
    }

}
