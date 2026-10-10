using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 窓を開かずにモーションを作る操作口（S38。AI や外部の道具から使う）。JSON の要求を受けて JSON の応答を返す。誤りは例外を投げずに error で返す。
    /// 開いたキャラ（PoseSession）は、閉じるかスクリプトを読み直すまで持ち続けるので、続けて読み書きできる。
    ///
    /// 要求（MotionEditorRequest。使わない項目は省いてよい）:
    /// - open: prefab（キャラの prefab のパス）・clip（*.rig.anim のパス。create が true なら無ければ作る）・definition（リグ定義のパス。省くとプロジェクト設定）・
    ///   overrides（重ねる Override。clip と mode）・overrideWriteIndex（書き込み先の Override。-1 は元のクリップ）。応答は describe と同じ
    /// - describe: キャラ・クリップ・点の一覧・キーのあるフレーム・書けるフレームの範囲
    /// - readPose: frame の姿勢（PoseDocument）
    /// - writePose: pose（PoseDocument）を pose.frame に書き、その姿勢を読み直して返す
    /// - lint: start〜end（end を省くとクリップの最後）を検査する（MotionLintReport）
    /// - bake: ゲーム用の Humanoid のクリップを焼いて保存する（outputName を省くと元の名前）
    /// - save: 書き換えたクリップを保存する
    /// - capture: frame の姿勢を 4 方向（正面・右・上・右斜め前）から PNG に書き出す（folder・size。画面のある Unity だけ）
    /// - importOverride: 外で作った Humanoid のクリップ（source）を、元に重ねる Override（overrideClip）として取り込む（mode・lockFeet）
    /// - close: 閉じる
    ///
    /// 呼び方（パッケージには入れない。Unity でメソッドを呼ぶ手段なら何でもよい）:
    /// - 開いている Unity から: Run (要求の JSON) を呼ぶ（Unity CLI の eval など）
    /// - 画面なしの Unity から: -executeMethod Lilium.MotionEditorCommands.RunFromCommandLine -motionEditorRequest 要求.json -motionEditorResponse 応答.json。
    ///   要求のファイルは 1 つの要求か、{"requests":[...]} で並べたもの（並べた順に実行し、最後に閉じる）
    /// </summary>
    public static class MotionEditorCommands
    {
        const string kRequestArgument = "-motionEditorRequest";
        const string kResponseArgument = "-motionEditorResponse";

        static PoseSession session_;

        [InitializeOnLoadMethod]
        static void Initialize ()
        {
            // 開いたままスクリプトを読み直すと、描かない世界のシーンが誰にも閉じられずに残る
            AssemblyReloadEvents.beforeAssemblyReload -= Close;
            AssemblyReloadEvents.beforeAssemblyReload += Close;
            EditorApplication.quitting -= Close;
            EditorApplication.quitting += Close;
        }

        /// <summary>開いているキャラ（無ければ null）</summary>
        public static PoseSession session
        {
            get { return session_; }
        }

        /// <summary>要求を 1 つ実行して、応答の JSON を返す</summary>
        public static string Run (string requestJson)
        {
            MotionEditorRequest request;
            try {
                request = string.IsNullOrEmpty (requestJson) ? null : JsonUtility.FromJson<MotionEditorRequest> (requestJson);
            }
            catch (System.Exception e) {
                return Response (null, Tr ("MOTION_EDITOR_COMMANDS_BAD_JSON", e.Message), null);
            }
            return Run (request);
        }

        public static string Run (MotionEditorRequest request)
        {
            if (request == null || string.IsNullOrEmpty (request.command)) return Response (null, Tr ("MOTION_EDITOR_COMMANDS_NO_COMMAND"), null);
            string command = request.command;
            try {
                string error;
                string result = Execute (request, out error);
                return Response (command, error, error == null ? result : null);
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                return Response (command, e.GetType ().Name + ": " + e.Message, null);
            }
        }

        /// <summary>要求を並べた JSON（{"requests":[...]}）か 1 つの要求を実行して、応答を並べた JSON（{"responses":[...]}）を返す。最後に閉じる</summary>
        public static string RunBatch (string json)
        {
            List<string> responses = new List<string> ();
            try {
                MotionEditorBatch batch = string.IsNullOrEmpty (json) ? null : JsonUtility.FromJson<MotionEditorBatch> (json);
                if (batch != null && batch.requests != null && batch.requests.Count > 0) {
                    foreach (MotionEditorRequest request in batch.requests) responses.Add (Run (request));
                }
                else {
                    responses.Add (Run (json));
                }
            }
            catch (System.Exception e) {
                responses.Add (Response (null, Tr ("MOTION_EDITOR_COMMANDS_BAD_JSON", e.Message), null));
            }
            finally {
                Close ();
            }
            return "{\"responses\":[" + string.Join (",", responses) + "]}";
        }

        /// <summary>
        /// 画面なしの Unity から呼ぶ口（-executeMethod）。-motionEditorRequest のファイルを実行し、応答を -motionEditorResponse のファイルへ書く
        /// （省くとログへ出す）。終わったら Unity を閉じる（要求に誤りがあっても 0 で閉じる。結果は応答を見る）
        /// </summary>
        public static void RunFromCommandLine ()
        {
            string[] args = System.Environment.GetCommandLineArgs ();
            string requestPath = ArgumentAfter (args, kRequestArgument);
            string responsePath = ArgumentAfter (args, kResponseArgument);
            string response;
            if (string.IsNullOrEmpty (requestPath) || !File.Exists (requestPath)) {
                response = "{\"responses\":[" + Response (null, Tr ("MOTION_EDITOR_COMMANDS_NO_REQUEST_FILE", kRequestArgument, requestPath), null) + "]}";
            }
            else {
                response = RunBatch (File.ReadAllText (requestPath));
            }
            if (!string.IsNullOrEmpty (responsePath)) File.WriteAllText (responsePath, response);
            else Debug.Log (response);
            if (Application.isBatchMode) EditorApplication.Exit (0);
        }

        static string ArgumentAfter (string[] args, string name)
        {
            int index = System.Array.IndexOf (args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        /// <summary>開いているキャラを閉じる</summary>
        public static void Close ()
        {
            if (session_ != null) session_.Dispose ();
            session_ = null;
        }

        static string Execute (MotionEditorRequest request, out string error)
        {
            error = null;
            switch (request.command) {
                case "open":
                    return Open (request, out error);
                case "close":
                    Close ();
                    return "{}";
            }

            if (session_ == null) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NOT_OPEN");
                return null;
            }
            switch (request.command) {
                case "describe":
                    return JsonUtility.ToJson (Describe (session_));

                case "readPose": {
                    PoseDocument pose = PoseDocument.Read (session_, request.frame, out error);
                    return pose != null ? pose.ToJson (false) : null;
                }

                case "writePose": {
                    if (request.pose == null || request.pose.points == null || request.pose.points.Count == 0) {
                        error = Tr ("POSE_DOCUMENT_EMPTY");
                        return null;
                    }
                    error = PoseDocument.Write (session_, request.pose);
                    if (error != null) return null;
                    PoseDocument pose = PoseDocument.Read (session_, request.pose.frame, out error);
                    return pose != null ? pose.ToJson (false) : null;
                }

                case "lint": {
                    int end = request.end >= 0 ? request.end : LastFrame (session_.clip);
                    MotionLintReport report = MotionLint.Run (session_, request.start, end);
                    error = report.error;
                    return report.ToJson (false);
                }

                case "bake": {
                    string path;
                    HumanoidBaker.Result result;
                    error = session_.Bake (request.outputName, out path, out result);
                    if (error != null) return null;
                    return JsonUtility.ToJson (new BakeResult {
                        path = path,
                        frameCount = result.frameCount,
                        milliseconds = result.milliseconds,
                        notes = result.notes ?? new List<string> (),
                    });
                }

                case "save":
                    return JsonUtility.ToJson (new SaveResult { paths = session_.Save () });

                case "capture":
                    return Capture (request, out error);

                case "importOverride":
                    return ImportOverride (request, out error);

                default:
                    error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_COMMAND", request.command, "open, describe, readPose, writePose, lint, bake, save, capture, importOverride, close");
                    return null;
            }
        }

        /// <summary>
        /// 開いているキャラ・クリップ・Override のまま、描く世界で開き直して frame の姿勢を 4 方向から撮る（S39）。
        /// 書き出し先を省くとプロジェクトの Temp/MotionEditorCaptures（アセットにしない）
        /// </summary>
        static string Capture (MotionEditorRequest request, out string error)
        {
            error = null;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) {
                error = Tr ("POSE_CAPTURE_NO_GRAPHICS");
                return null;
            }
            string folder = string.IsNullOrEmpty (request.folder) ? Path.Combine ("Temp", "MotionEditorCaptures") : request.folder;
            List<string> paths;
            using (PoseSession rendered = new PoseSession (session_.stage.sourcePrefab, session_.definition, true)) {
                rendered.clip = session_.clip;
                foreach (OverrideClip entry in session_.overrides) rendered.AddOverride (entry.clip, entry.mode, entry.mask);
                rendered.overrideWriteIndex = session_.overrideWriteIndex;
                paths = PoseCapture.Capture (rendered, request.frame, folder, request.size, out error);
            }
            return error == null ? JsonUtility.ToJson (new SaveResult { paths = paths }) : null;
        }

        /// <summary>
        /// 外で作った Humanoid のクリップ（source）を、今の元のクリップに重ねる Override（overrideClip。create なら作る）として取り込む（S40）。
        /// 取り込んだ Override は開いているキャラに重ね（まだ重ねていなければ）、書き込み先にする
        /// </summary>
        static string ImportOverride (MotionEditorRequest request, out string error)
        {
            error = null;
            AnimationClip source = string.IsNullOrEmpty (request.source) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (request.source);
            if (source == null) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_CLIP", request.source);
                return null;
            }
            AnimationClip destination = OpenClip (request.overrideClip, request.create, out error);
            if (error != null) return null;
            OverrideMode mode;
            if (!System.Enum.TryParse (string.IsNullOrEmpty (request.mode) ? "Additive" : request.mode, true, out mode)) {
                error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_MODE", request.mode);
                return null;
            }
            PoseSession.OverrideImportResult result;
            error = session_.ImportOverride (source, destination, mode, request.lockFeet, out result);
            if (error != null) return null;
            int index = session_.overrides.ToList ().FindIndex (o => o.clip == destination);
            if (index < 0) {
                session_.AddOverride (destination, mode);
                index = session_.overrides.Count - 1;
            }
            session_.overrideWriteIndex = index;
            AssetDatabase.SaveAssetIfDirty (destination);
            return JsonUtility.ToJson (result);
        }

        static string Open (MotionEditorRequest request, out string error)
        {
            error = null;
            GameObject prefab = string.IsNullOrEmpty (request.prefab) ? null : AssetDatabase.LoadAssetAtPath<GameObject> (request.prefab);
            if (prefab == null) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_PREFAB", request.prefab);
                return null;
            }
            EditRigDefinition definition = null;
            if (!string.IsNullOrEmpty (request.definition)) {
                definition = AssetDatabase.LoadAssetAtPath<EditRigDefinition> (request.definition);
                if (definition == null) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_NO_DEFINITION", request.definition);
                    return null;
                }
            }
            else {
                definition = ProjectSettings.instance.defaultRigDefinition;
            }
            AnimationClip clip = OpenClip (request.clip, request.create, out error);
            if (error != null) return null;
            List<OverrideClip> layers = new List<OverrideClip> ();
            foreach (MotionEditorOverride entry in request.overrides ?? new List<MotionEditorOverride> ()) {
                AnimationClip layer = OpenClip (entry.clip, request.create, out error);
                if (error != null) return null;
                OverrideMode mode;
                if (!System.Enum.TryParse (string.IsNullOrEmpty (entry.mode) ? "Additive" : entry.mode, true, out mode)) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_MODE", entry.mode);
                    return null;
                }
                ChannelMask mask = null;
                if (!string.IsNullOrEmpty (entry.mask)) {
                    mask = AssetDatabase.LoadAssetAtPath<ChannelMask> (entry.mask);
                    if (mask == null) {
                        error = Tr ("MOTION_EDITOR_COMMANDS_NO_MASK", entry.mask);
                        return null;
                    }
                }
                layers.Add (new OverrideClip { clip = layer, mode = mode, mask = mask });
            }

            Close ();
            session_ = new PoseSession (prefab, definition);
            if (session_.error != null) {
                error = session_.error;
                Close ();
                return null;
            }
            session_.clip = clip;
            foreach (OverrideClip layer in layers) session_.AddOverride (layer.clip, layer.mode, layer.mask);
            session_.overrideWriteIndex = request.overrideWriteIndex;
            session_.Evaluate (0);
            return JsonUtility.ToJson (Describe (session_));
        }

        /// <summary>クリップを読む。create なら、無いときに編集用クリップ（60fps）を作る</summary>
        static AnimationClip OpenClip (string path, bool create, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty (path)) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_CLIP", path);
                return null;
            }
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            if (clip != null) return clip;
            if (!create || !path.StartsWith ("Assets/") || !path.EndsWith (".anim")) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_CLIP", path);
                return null;
            }
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            if (!AssetDatabase.IsValidFolder (folder)) {
                Directory.CreateDirectory (folder);
                AssetDatabase.Refresh ();
            }
            clip = new AnimationClip { frameRate = 60, name = Path.GetFileNameWithoutExtension (path) };
            AssetDatabase.CreateAsset (clip, path);
            return clip;
        }

        static int LastFrame (AnimationClip clip)
        {
            if (clip == null) return 0;
            int[] frames = ClipCycle.GetFrames (clip);
            return Mathf.Max (frames.Length > 0 ? frames[frames.Length - 1] : 0, Mathf.RoundToInt (clip.length * clip.frameRate));
        }

        static MotionEditorDescription Describe (PoseSession session)
        {
            PreviewStage stage = session.stage;
            AnimationClip target = session.targetClip;
            int[] keyFrames = ClipCycle.GetFrames (target);
            bool cycle = ClipCycle.Load (target);
            int first, last;
            MotionEditorDescription description = new MotionEditorDescription {
                character = stage.sourcePrefab != null ? stage.sourcePrefab.name : "",
                clip = AssetDatabase.GetAssetPath (session.clip),
                targetClip = AssetDatabase.GetAssetPath (target),
                frameRate = session.clip != null ? session.clip.frameRate : 60,
                lastFrame = LastFrame (session.clip),
                keyFrames = keyFrames.ToList (),
                cycle = cycle,
                // サイクルキーより後ろには打てない（-1 は限りなし）
                writableUntil = cycle && ClipCycle.TryGetRange (keyFrames, out first, out last) ? last : -1,
                fullBody = stage.fullBody != null,
                humanScale = stage.editingRig != null ? stage.editingRig.humanScale : 0,
                overrideWriteIndex = session.overrideWriteIndex,
                writeProblem = session.GetWriteProblem (),
            };
            if (stage.fullBody != null) {
                foreach (BodyPointId id in stage.fullBody.pointIds) {
                    description.points.Add (new MotionEditorPointInfo { name = id.name, hasRotation = stage.fullBody.PointHasRotation (id) });
                }
            }
            return description;
        }

        /// <summary>応答の JSON。result は結果の JSON をそのまま入れる（JsonUtility は型の決まらない値を持てないため、外側だけ手で組む）</summary>
        static string Response (string command, string error, string result)
        {
            string head = JsonUtility.ToJson (new ResponseHead { success = error == null, command = command ?? "", error = error ?? "" });
            return head.Substring (0, head.Length - 1) + ",\"result\":" + (string.IsNullOrEmpty (result) ? "null" : result) + "}";
        }

        [System.Serializable]
        sealed class ResponseHead
        {
            public bool success;
            public string command;
            public string error;
        }

        [System.Serializable]
        sealed class BakeResult
        {
            public string path;
            public int frameCount;
            public double milliseconds;
            public List<string> notes;
        }

        [System.Serializable]
        sealed class SaveResult
        {
            public List<string> paths;
        }
    }

    /// <summary>操作口への要求（MotionEditorCommands）。使わない項目は省いてよい</summary>
    [System.Serializable]
    public sealed class MotionEditorRequest
    {
        public string command;
        public string prefab;
        public string clip;
        public string definition;
        public bool create;
        public List<MotionEditorOverride> overrides = new List<MotionEditorOverride> ();
        public int overrideWriteIndex = -1;
        public int frame;
        public int start;
        public int end = -1;
        public string outputName;
        public PoseDocument pose;
        /// <summary>capture の書き出し先（省くとプロジェクトの Temp/MotionEditorCaptures）</summary>
        public string folder;
        /// <summary>capture の画像の一辺（ピクセル）</summary>
        public int size = 512;
        /// <summary>importOverride の取り込む Humanoid のクリップのパス</summary>
        public string source;
        /// <summary>importOverride の書き先の Override のクリップのパス（create なら作る）</summary>
        public string overrideClip;
        /// <summary>importOverride の重ね方（Additive / Override）</summary>
        public string mode;
        /// <summary>importOverride で足を床に付けるか</summary>
        public bool lockFeet;
    }

    [System.Serializable]
    public sealed class MotionEditorOverride
    {
        public string clip;
        /// <summary>Additive / Override</summary>
        public string mode;
        /// <summary>効く範囲（ChannelMask のアセットのパス。S17）。空なら全部に効く</summary>
        public string mask;
    }

    /// <summary>要求を並べたもの（-executeMethod で渡すファイル）</summary>
    [System.Serializable]
    public sealed class MotionEditorBatch
    {
        public List<MotionEditorRequest> requests = new List<MotionEditorRequest> ();
    }

    /// <summary>開いているキャラとクリップの説明（describe・open の応答）</summary>
    [System.Serializable]
    public sealed class MotionEditorDescription
    {
        public string character;
        public string clip;
        /// <summary>キーを打つクリップ（書き込み先が Override ならそのクリップ）</summary>
        public string targetClip;
        public float frameRate;
        public int lastFrame;
        public List<int> keyFrames = new List<int> ();
        public bool cycle;
        /// <summary>このフレームより後ろには打てない（サイクルキー。-1 は限りなし）</summary>
        public int writableUntil;
        /// <summary>全身 IK のキャラか（姿勢の読み書きは全身 IK のキャラだけ）</summary>
        public bool fullBody;
        public float humanScale;
        public int overrideWriteIndex;
        /// <summary>今キーを打てない理由（打てるなら空）</summary>
        public string writeProblem;
        public List<MotionEditorPointInfo> points = new List<MotionEditorPointInfo> ();
    }

    [System.Serializable]
    public sealed class MotionEditorPointInfo
    {
        public string name;
        public bool hasRotation;
    }

}
