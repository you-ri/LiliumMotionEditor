using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// - describe: キャラ・クリップ・点の一覧・キーのあるフレーム・書けるフレームの範囲。点ごとに、乗る骨・T ポーズでの場所・線でつなぐ相手の点とその長さ・接地の点か
    /// - readPose: frame の姿勢（PoseDocument）。compact なら丸めて項目を省いた形（readPose・writePose とも）
    /// - writePose: pose（PoseDocument）を pose.frame に書き、その姿勢を読み直して返す。応答の各点の residual（届かなかった差）と、
    ///   unreached（届かなかった点）・issues（そのフレームの床と muscle の範囲）も返す。dryRun ならキーを打たずに解いた姿勢だけを返す。
    ///   pose の代わりに poseFile（PoseDocument の JSON のファイル）でもよい
    /// - writeMotion: keys（キーごとに frame・points・hands）を 1 回で書く。先に全部確かめ、誤りがあれば何も書かない。replace なら先にキーを全部消す。
    ///   応答はキーごとの届かなかった点と、lint（既定 true）なら全体の検査
    /// - readMotion: キーのあるフレームごとの姿勢（compact）と指の muscle を、writeMotion の要求と同じ形で返す
    /// - deleteKeys: frames（all なら全部）のキーを消す。無いフレームがあれば何も消さない。応答は describe と cycleRemoved
    /// - sample: start〜end を step ごとに、点（pointNames。省くと全部）の場所・速さ・接地と、muscleNames の値を、点ごとに並べて返す
    /// - undo: 操作口で書いた最後の 1 回（writePose・writeHand・writeMotion・deleteKeys）を戻す。書いた後にクリップが変わっていたら戻さない。応答は describe と同じ
    /// - lint: start〜end（end を省くとクリップの最後）を検査する（MotionLintReport）
    /// - bake: ゲーム用の Humanoid のクリップを焼いて保存する（outputName を省くと元の名前）
    /// - save: 書き換えたクリップを保存する
    /// - capture: frame の姿勢を 4 方向（正面・右・上・右斜め前）から PNG に書き出す（folder・size。画面のある Unity だけ）
    /// - importOverride: 外で作った Humanoid のクリップ（source）を、元に重ねる Override（overrideClip）として取り込む（mode・lockFeet）
    /// - writeHand: frame に手の形を打つ。preset（PoseBank の Humanoid の姿勢の名前かパス）を hand（Left / Right / Both）の指に当て、
    ///   muscles（指の muscle の名前と値）で個別に上書きする。応答は readHand と同じ
    /// - readHand: frame の両手の指の muscle の値と、使えるプリセットの名前
    /// - close: 閉じる
    ///
    /// 呼び方（パッケージには入れない。Unity でメソッドを呼ぶ手段なら何でもよい）:
    /// - 開いている Unity から: Run (要求の JSON) を呼ぶ（Unity CLI の eval など）
    /// - 画面なしの Unity から: -executeMethod Lilium.MotionEditorCommands.RunFromCommandLine -motionEditorRequest 要求.json -motionEditorResponse 応答.json。
    ///   要求のファイルは 1 つの要求か、{"requests":[...]} で並べたもの（並べた順に実行し、最後に閉じる。RunBatch は keepOpen なら閉じない）
    /// </summary>
    public static class MotionEditorCommands
    {
        const string kRequestArgument = "-motionEditorRequest";
        const string kResponseArgument = "-motionEditorResponse";

        /// <summary>届かなかったとみなす差（× humanScale。メートル）</summary>
        const float kReachTolerance = 0.01f;
        /// <summary>undo で戻せる書き込みの数（写しを持つので限る）</summary>
        const int kUndoLimit = 32;

        static PoseSession session_;
        static readonly List<WriteRecord> undo_ = new List<WriteRecord> ();

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

        /// <summary>
        /// 要求を並べた JSON（{"requests":[...]}）か 1 つの要求を実行して、応答を並べた JSON（{"responses":[...]}）を返す。
        /// 最後に閉じる（keepOpen なら閉じない）
        /// </summary>
        public static string RunBatch (string json)
        {
            List<string> responses = new List<string> ();
            bool keepOpen = false;
            try {
                MotionEditorBatch batch = string.IsNullOrEmpty (json) ? null : JsonUtility.FromJson<MotionEditorBatch> (json);
                if (batch != null && batch.requests != null && batch.requests.Count > 0) {
                    keepOpen = batch.keepOpen;
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
                if (!keepOpen) Close ();
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
                // keepOpen でも閉じる（Unity を閉じるので、続けて使う人がいない）
                Close ();
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
            foreach (WriteRecord record in undo_) {
                if (record.copy != null) Object.DestroyImmediate (record.copy);
            }
            undo_.Clear ();
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
                    if (pose == null) return null;
                    return request.compact ? pose.ToCompactJson () : pose.ToJson (false);
                }

                case "writePose": {
                    PoseDocument pose = request.pose;
                    if (!string.IsNullOrEmpty (request.poseFile)) {
                        pose = ReadPoseFile (request.poseFile, out error);
                        if (error != null) return null;
                    }
                    if (pose == null || (pose.points == null || pose.points.Count == 0) && (pose.contacts == null || pose.contacts.Count == 0)) {
                        error = Tr ("POSE_DOCUMENT_EMPTY");
                        return null;
                    }
                    List<PoseSnap> snapped = new List<PoseSnap> ();
                    error = PoseDocument.Solve (session_, pose, snapped);
                    if (error == null) {
                        // 届いたかはキーを打つ前に読む。打つと点は骨が届いた所でキーになり、読み直すと差が無くなる
                        PoseDocument solved = PoseDocument.ReadCurrent (session_);
                        if (!request.dryRun) error = RecordWrite (() => session_.CommitPose ("Write Pose"));
                        if (error == null) return WriteResult (solved, snapped, request.compact);
                    }
                    // 点を置いた後に打てなかったときも、置いた点を残さない
                    session_.Evaluate (session_.frame);
                    return null;
                }

                case "undo":
                    return UndoWrite (out error);

                case "writeMotion":
                    return WriteMotion (request, out error);

                case "readMotion":
                    return ReadMotion (out error);

                case "deleteKeys":
                    return DeleteKeys (request, out error);

                case "sample":
                    return Sample (request, out error);

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
                        keyCount = result.keyCount,
                        keyCountBeforeReduce = result.keyCountBeforeReduce,
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

                case "writeHand":
                    return WriteHand (request, out error);

                case "readHand":
                    return ReadHand (request.frame, out error);

                case "exportKeys":
                    return ExportKeys (request, out error);

                default:
                    error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_COMMAND", request.command, "open, describe, readPose, writePose, writeMotion, readMotion, deleteKeys, sample, undo, lint, bake, save, capture, importOverride, exportKeys, writeHand, readHand, close");
                    return null;
            }
        }

        /// <summary>
        /// writePose の応答（S57）: 今の姿勢（書いた後、または試しに解いた後）に、届かなかった差（solved の、キーを打つ前に解いた姿勢のもの）と点の名前、
        /// そのフレームの床・muscle の範囲の検査、snap で寄せた点（S60。あるときだけ）を足す。
        /// 検査は骨を崩すので、最後にそのフレームの姿勢をクリップから作り直す（試しに解いた姿勢もここで捨てる）
        /// </summary>
        static string WriteResult (PoseDocument solved, List<PoseSnap> snapped, bool compact)
        {
            PoseDocument pose = PoseDocument.ReadCurrent (session_);
            foreach (PosePoint point in pose.points) {
                PosePoint before = solved.Find (point.name);
                point.residual = before != null ? before.residual : 0;
            }
            List<MotionLintIssue> issues = MotionLint.CheckPose (session_);
            session_.Evaluate (session_.frame);
            float reach = kReachTolerance * pose.humanScale;
            WriteExtra extra = new WriteExtra {
                unreached = pose.points.Where (p => p.residual > reach).Select (p => p.name).ToList (),
                issues = issues,
                snapped = snapped,
            };
            if (!compact) return Merge (pose.ToJson (false), JsonUtility.ToJson (extra));

            StringBuilder json = new StringBuilder ("{\"unreached\":[");
            json.Append (string.Join (",", extra.unreached.Select (n => "\"" + n + "\""))).Append ("],\"issues\":[");
            for (int i = 0; i < issues.Count; i++) {
                MotionLintIssue issue = issues[i];
                if (i > 0) json.Append (',');
                json.Append ("{\"kind\":\"").Append (issue.kind).Append ("\",\"target\":\"").Append (issue.target)
                    .Append ("\",\"frame\":").Append (issue.frame).Append (",\"endFrame\":").Append (issue.endFrame)
                    .Append (",\"value\":").Append (PoseDocument.Number (issue.value, 3))
                    .Append (",\"limit\":").Append (PoseDocument.Number (issue.limit, 3)).Append ('}');
            }
            json.Append (']');
            AppendSnapped (json, snapped);
            return Merge (pose.ToCompactJson (), json.Append ('}').ToString ());
        }

        /// <summary>snap で寄せた点を ,"snapped":[...] で書く（無ければ書かない。距離と場所は 1mm に丸める）</summary>
        static void AppendSnapped (StringBuilder json, List<PoseSnap> snapped)
        {
            if (snapped == null || snapped.Count == 0) return;
            json.Append (",\"snapped\":[");
            for (int i = 0; i < snapped.Count; i++) {
                PoseSnap snap = snapped[i];
                if (i > 0) json.Append (',');
                json.Append ("{\"name\":\"").Append (snap.name).Append ("\",\"distance\":").Append (PoseDocument.Number (snap.distance, 3))
                    .Append (",\"position\":{\"x\":").Append (PoseDocument.Number (snap.position.x, 3))
                    .Append (",\"y\":").Append (PoseDocument.Number (snap.position.y, 3))
                    .Append (",\"z\":").Append (PoseDocument.Number (snap.position.z, 3)).Append ("}}");
            }
            json.Append (']');
        }

        /// <summary>
        /// 全部のキーの体の点と手の形を 1 回で書く（S59）。キーはフレームの順に並べ、キーごとに体 → 手の順で書く
        /// （体のキーは指もそのときの値で打つので、手を後にすると同じキーの手の形が固まらない）。
        /// 先に全部確かめ、誤りがあれば何も書かない。書いている途中で止まっても書く前に戻す。全部で 1 回の undo で戻る。
        /// replace なら書く前に書き先のクリップのキーを全部消す。応答はキーごとの届かなかった点と差の最大、lint なら全体の検査
        /// </summary>
        static string WriteMotion (MotionEditorRequest request, out string error)
        {
            error = PoseDocument.GetProblem (session_);
            if (error != null) return null;
            List<MotionEditorKey> keys = (request.keys ?? new List<MotionEditorKey> ()).Where (k => k != null).OrderBy (k => k.frame).ToList ();
            if (keys.Count == 0) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_KEYS");
                return null;
            }

            // 先に全部確かめる
            int writableUntil = Describe (session_).writableUntil;
            List<Dictionary<int, float>> hands = new List<Dictionary<int, float>> ();
            for (int i = 0; i < keys.Count; i++) {
                MotionEditorKey key = keys[i];
                string problem = null;
                Dictionary<int, float> muscles = null;
                bool hasPoints = key.points != null && key.points.Count > 0 || key.contacts != null && key.contacts.Count > 0;
                bool hasHands = key.hands != null && key.hands.Count > 0;
                if (key.frame < 0) problem = Tr ("MOTION_EDITOR_COMMANDS_NEGATIVE_FRAME");
                else if (!string.IsNullOrEmpty (key.interpolation) && !KeyInterpolations.TryParse (key.interpolation, out KeyInterpolation ignored)) problem = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_INTERPOLATION", key.interpolation);
                else if (i > 0 && keys[i - 1].frame == key.frame) problem = Tr ("MOTION_EDITOR_COMMANDS_DUPLICATE_FRAME");
                else if (!hasPoints && !hasHands) problem = Tr ("MOTION_EDITOR_COMMANDS_EMPTY_KEY");
                // replace ならキーを消してから書くので、今のサイクルキーの範囲は効かない
                else if (!request.replace && writableUntil >= 0 && key.frame > writableUntil) problem = Tr ("PREVIEW_WINDOW_PAST_CYCLE_KEY");
                else if (hasPoints) problem = PoseDocument.Check (session_, KeyPose (key));
                if (problem == null && hasHands) {
                    muscles = new Dictionary<int, float> ();
                    foreach (MotionEditorHandShape shape in key.hands) {
                        Dictionary<int, float> values = HandMuscles (shape != null ? shape.preset : null, shape != null ? shape.hand : null, shape != null ? shape.muscles : null, out problem);
                        if (problem != null) break;
                        foreach (KeyValuePair<int, float> pair in values) muscles[pair.Key] = pair.Value;
                    }
                }
                if (problem != null) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_BAD_KEY", key.frame, problem);
                    return null;
                }
                hands.Add (muscles);
            }

            StringBuilder json = new StringBuilder ("{\"keys\":[");
            error = RecordWrite (() => {
                string problem = null;
                PoseCommit.InUndoGroup ("Write Motion", () => {
                    if (request.replace) {
                        RemoveKeys (session_.targetClip, null, true, "Write Motion");
                        session_.stage.InvalidateClipCurves ();
                    }
                    for (int i = 0; i < keys.Count && problem == null; i++) {
                        MotionEditorKey key = keys[i];
                        PoseDocument solved = null;
                        List<PoseSnap> snapped = new List<PoseSnap> ();
                        if (key.points != null && key.points.Count > 0 || key.contacts != null && key.contacts.Count > 0) {
                            problem = PoseDocument.Solve (session_, KeyPose (key), snapped);
                            if (problem == null) {
                                solved = PoseDocument.ReadCurrent (session_);
                                problem = session_.CommitPose ("Write Motion");
                            }
                        }
                        if (problem == null && hands[i] != null) {
                            session_.Evaluate (key.frame);
                            problem = session_.WriteMuscles (hands[i], "Write Motion");
                        }
                        if (problem != null) {
                            problem = Tr ("MOTION_EDITOR_COMMANDS_BAD_KEY", key.frame, problem);
                            break;
                        }
                        float reach = kReachTolerance * session_.stage.editingRig.humanScale;
                        List<PosePoint> points = solved != null ? solved.points : new List<PosePoint> ();
                        if (i > 0) json.Append (',');
                        json.Append ("{\"frame\":").Append (key.frame)
                            .Append (",\"maxResidual\":").Append (PoseDocument.Number (points.Count > 0 ? points.Max (p => p.residual) : 0, 3))
                            .Append (",\"unreached\":[").Append (string.Join (",", points.Where (p => p.residual > reach).Select (p => "\"" + p.name + "\""))).Append (']');
                        AppendSnapped (json, snapped);
                        json.Append ('}');
                    }
                    // 区間の補間は全部のキーを打ってから（次のキーが決まってから）。最後のキーなど区間の無いキーの指定は理由を返す
                    for (int i = 0; i < keys.Count && problem == null; i++) {
                        if (string.IsNullOrEmpty (keys[i].interpolation)) continue;
                        KeyInterpolations.TryParse (keys[i].interpolation, out KeyInterpolation mode);
                        problem = session_.SetInterpolation (keys[i].frame, mode, "Write Motion");
                        if (problem != null) problem = Tr ("MOTION_EDITOR_COMMANDS_BAD_KEY", keys[i].frame, problem);
                    }
                });
                return problem;
            });
            session_.Evaluate (session_.frame);
            if (error != null) return null;
            json.Append (']');
            if (request.lint) {
                MotionLintReport report = MotionLint.Run (session_, 0, LastFrame (session_.clip));
                json.Append (",\"lint\":").Append (report.ToJson (false));
            }
            return json.Append ('}').ToString ();
        }

        static PoseDocument KeyPose (MotionEditorKey key)
        {
            return new PoseDocument { frame = key.frame, points = key.points ?? new List<PosePoint> (), contacts = key.contacts ?? new List<PoseContact> () };
        }

        /// <summary>
        /// キーを消す（S61。S38 の「キーを消すコマンドは入れない」を改めた）。frames のフレームの、書き先のクリップの全部のカーブのキー（点の状態・指も）を消す。
        /// all なら全部（writeMotion の replace と同じ）。キーの無いフレームがあれば何も消さずに理由を返す。1 回の undo で戻る。応答は describe と cycleRemoved
        /// </summary>
        static string DeleteKeys (MotionEditorRequest request, out string error)
        {
            // 書けるかはフレームでも決まる（サイクルキーより後ろ）。キーを消すのはフレームによらないので、0 で見る
            int frame = session_.frame;
            session_.Evaluate (0);
            error = session_.GetWriteProblem ();
            session_.Evaluate (frame);
            if (error != null) return null;
            AnimationClip clip = session_.targetClip;
            int[] existing = ClipCycle.GetFrames (clip);
            List<int> frames = request.all ? existing.ToList () : (request.frames ?? new List<int> ()).Distinct ().OrderBy (f => f).ToList ();
            if (frames.Count == 0) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_FRAMES_TO_DELETE");
                return null;
            }
            List<int> missing = frames.Where (f => System.Array.IndexOf (existing, f) < 0).ToList ();
            if (missing.Count > 0) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_KEY_AT", string.Join (", ", missing), string.Join (", ", existing));
                return null;
            }
            bool cycleRemoved = false;
            error = RecordWrite (() => {
                PoseCommit.InUndoGroup ("Delete Keys", () => cycleRemoved = RemoveKeys (clip, frames, request.all, "Delete Keys"));
                return null;
            });
            session_.stage.InvalidateClipCurves ();
            session_.Evaluate (frame);
            if (error != null) return null;
            return Merge (JsonUtility.ToJson (Describe (session_)), "{\"cycleRemoved\":" + (cycleRemoved ? "true" : "false") + "}");
        }

        /// <summary>
        /// キーを消す（all ならカーブごと全部）。サイクルのクリップでは窓と同じく、最後のキー（サイクルキー）を消したら印を外し（true を返す）、
        /// それ以外は最後のキーを先頭のキーに合わせ直す（先頭のキーを消したら、新しい先頭が最後へ写る）
        /// </summary>
        static bool RemoveKeys (AnimationClip clip, List<int> frames, bool all, string undoName)
        {
            bool cycle = ClipCycle.Load (clip);
            int[] before = ClipCycle.GetFrames (clip);
            Undo.RecordObject (clip, undoName);
            if (all) {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (clip);
                if (bindings.Length > 0) AnimationUtility.SetEditorCurves (clip, bindings, new AnimationCurve[bindings.Length]);
                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (clip)) AnimationUtility.SetObjectReferenceCurve (clip, binding, null);
            }
            else {
                foreach (int frame in frames) ClipKeyUtility.RemoveKeysAtFrame (clip, frame);
            }
            if (!cycle) return false;
            int first, last;
            if (ClipCycle.TryGetRange (ClipCycle.GetFrames (clip), out first, out last) && !ClipCycle.CanFollow (before, last)) {
                ClipCycle.Save (clip, false);
                return true;
            }
            ClipCycle.Sync (clip);
            return false;
        }

        /// <summary>
        /// キーの間の点の動きを、フレームごとの数で返す（S61）。start〜end を step ごとに、点の場所（体の向き基準・1mm に丸める）・
        /// 速さ（m/s。前に見たフレームからの距離 ÷ 時間。最初のフレームは 0）・接地の点なら接地しているか（床から検査の contactHeight × 体の大きさ以内）と、
        /// muscleNames の muscle の値を返す。点（pointNames）を省くと全部。項目名を繰り返さないよう点ごとに値を並べる（列）。JsonUtility では書けないので手で組み立てる
        /// </summary>
        static string Sample (MotionEditorRequest request, out string error)
        {
            error = PoseDocument.GetProblem (session_);
            if (error != null) return null;
            int start = request.start;
            int end = request.end >= 0 ? request.end : LastFrame (session_.clip);
            int step = Mathf.Max (1, request.step);
            if (end < start) {
                error = Tr ("MOTION_LINT_BAD_RANGE", start, end);
                return null;
            }
            FullBodyRig body = session_.stage.fullBody;
            List<BodyPointId> points = new List<BodyPointId> ();
            foreach (string name in request.pointNames ?? new List<string> ()) {
                BodyPointId id = new BodyPointId (name);
                if (!body.HasPoint (id)) {
                    error = Tr ("POSE_DOCUMENT_UNKNOWN_POINT", name, string.Join (", ", body.pointIds.Select (p => p.name)));
                    return null;
                }
                points.Add (id);
            }
            if (points.Count == 0) points.AddRange (body.pointIds);
            List<int> muscles = new List<int> ();
            foreach (string name in request.muscleNames ?? new List<string> ()) {
                int index;
                if (!PoseBank.TryGetMuscle (name, out index)) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_MUSCLE", name);
                    return null;
                }
                muscles.Add (index);
            }

            float rate = CurveEdit.FrameRateOf (session_.clip);
            float contactHeight = new MotionLint.Settings ().contactHeight * session_.stage.editingRig.humanScale;
            List<int> frames = new List<int> ();
            for (int f = start; f <= end; f += step) frames.Add (f);
            Vector3[,] positions = new Vector3[points.Count, frames.Count];
            float[,] values = new float[muscles.Count, frames.Count];
            int keep = session_.frame;
            try {
                for (int k = 0; k < frames.Count; k++) {
                    PoseDocument pose = PoseDocument.Read (session_, frames[k], out error);
                    if (pose == null) return null;
                    for (int i = 0; i < points.Count; i++) {
                        PosePoint point = pose.Find (points[i].name);
                        positions[i, k] = point != null ? point.position : Vector3.zero;
                    }
                    if (muscles.Count == 0) continue;
                    float[] read = session_.stage.ReadMuscles ();
                    // 表示モデルへ当てた分を戻す（ReadHand と同じ）
                    session_.Evaluate (frames[k]);
                    if (read == null) {
                        error = Tr ("PREVIEW_WINDOW_POSE_BANK_DISPLAY_NOT_HUMANOID");
                        return null;
                    }
                    for (int m = 0; m < muscles.Count; m++) values[m, k] = muscles[m] < read.Length ? read[muscles[m]] : 0;
                }
            }
            finally {
                session_.Evaluate (keep);
            }

            StringBuilder json = new StringBuilder ();
            json.Append ("{\"start\":").Append (start).Append (",\"end\":").Append (end).Append (",\"step\":").Append (step)
                .Append (",\"frameRate\":").Append (PoseDocument.Number (rate, 3))
                .Append (",\"frames\":[").Append (string.Join (",", frames)).Append ("],\"points\":[");
            for (int i = 0; i < points.Count; i++) {
                if (i > 0) json.Append (',');
                json.Append ("{\"name\":\"").Append (points[i].name).Append ('"');
                for (int axis = 0; axis < 3; axis++) {
                    json.Append (",\"").Append ("xyz"[axis]).Append ("\":[");
                    for (int k = 0; k < frames.Count; k++) json.Append (k > 0 ? "," : "").Append (PoseDocument.Number (positions[i, k][axis], 3));
                    json.Append (']');
                }
                json.Append (",\"speed\":[");
                for (int k = 0; k < frames.Count; k++) {
                    float speed = k > 0 ? Vector3.Distance (positions[i, k], positions[i, k - 1]) * rate / step : 0;
                    json.Append (k > 0 ? "," : "").Append (PoseDocument.Number (speed, 3));
                }
                json.Append (']');
                if (body.IsContact (points[i])) {
                    json.Append (",\"contact\":[");
                    // 床との当たりの球（定義の radius）の下の端の高さで見る
                    float radius = body.GetRadius (points[i]);
                    for (int k = 0; k < frames.Count; k++) json.Append (k > 0 ? "," : "").Append (positions[i, k].y - radius <= contactHeight ? 1 : 0);
                    json.Append (']');
                }
                json.Append ('}');
            }
            json.Append ("],\"muscles\":[");
            for (int m = 0; m < muscles.Count; m++) {
                if (m > 0) json.Append (',');
                json.Append ("{\"name\":\"").Append (HumanTrait.MuscleName[muscles[m]]).Append ("\",\"value\":[");
                for (int k = 0; k < frames.Count; k++) json.Append (k > 0 ? "," : "").Append (PoseDocument.Number (values[m, k], 3));
                json.Append ("]}");
            }
            return json.Append ("]}").ToString ();
        }

        /// <summary>
        /// キーのあるフレームごとの姿勢（compact）と指の muscle を、writeMotion の要求と同じ形で返す（S59。外で直して書き戻せる）
        /// </summary>
        static string ReadMotion (out string error)
        {
            error = PoseDocument.GetProblem (session_);
            if (error != null) return null;
            int[] frames = ClipCycle.GetFrames (session_.targetClip);
            List<KeyInterpolations.Segment> segments = session_.GetInterpolations ();
            StringBuilder json = new StringBuilder ("{\"keys\":[");
            for (int k = 0; k < frames.Length; k++) {
                PoseDocument pose = PoseDocument.Read (session_, frames[k], out error);
                if (pose == null) return null;
                if (k > 0) json.Append (',');
                json.Append ("{\"frame\":").Append (frames[k]);
                // 次のキーまでの補間（S80）。既定の曲線は省く（長くしない）
                int segment = segments.FindIndex (s => s.start == frames[k]);
                if (segment >= 0 && segments[segment].mode != KeyInterpolation.Curve) {
                    json.Append (",\"interpolation\":\"").Append (KeyInterpolations.ToName (segments[segment].mode)).Append ('"');
                }
                json.Append (",\"points\":");
                pose.AppendCompactPoints (json);
                float[] muscles = session_.stage.ReadMuscles ();
                // 表示モデルへ当てた分を戻す（ReadHand と同じ）
                session_.Evaluate (frames[k]);
                if (muscles != null) {
                    json.Append (",\"hands\":[{\"hand\":\"Both\",\"muscles\":[");
                    bool first = true;
                    for (int i = 0; i < muscles.Length; i++) {
                        bool isRight;
                        if (!PoseBank.IsFingerMuscle (i, out isRight)) continue;
                        if (!first) json.Append (',');
                        first = false;
                        json.Append ("{\"name\":\"").Append (HumanTrait.MuscleName[i]).Append ("\",\"value\":").Append (PoseDocument.Number (muscles[i], 3)).Append ('}');
                    }
                    json.Append ("]}]");
                }
                json.Append ('}');
            }
            return json.Append ("]}").ToString ();
        }

        /// <summary>
        /// 要求の JSON をファイルから読む（Unity CLI の --file・--pose_file。S58。引数の文字列で渡すと、シェルの引用符や改行で壊れやすいため）。
        /// 相対パスはプロジェクトのフォルダから。読めなければ null と理由
        /// </summary>
        public static string ReadJsonFile (string path, out string error)
        {
            error = null;
            try {
                return File.ReadAllText (path);
            }
            catch (System.Exception e) {
                error = Tr ("MOTION_EDITOR_COMMANDS_CANNOT_READ_FILE", path, e.Message);
                return null;
            }
        }

        static PoseDocument ReadPoseFile (string path, out string error)
        {
            string json = ReadJsonFile (path, out error);
            if (error != null) return null;
            try {
                return PoseDocument.FromJson (json);
            }
            catch (System.Exception e) {
                error = Tr ("MOTION_EDITOR_COMMANDS_BAD_POSE_FILE", path, e.Message);
                return null;
            }
        }

        /// <summary>2 つの JSON のオブジェクトの項目を 1 つにまとめる（JsonUtility では型を継がずに項目を足せないため）</summary>
        static string Merge (string a, string b)
        {
            if (b.Length <= 2) return a;
            return a.Substring (0, a.Length - 1) + "," + b.Substring (1);
        }

        /// <summary>
        /// 操作口で書く。書く前の書き先のクリップを写しておき、書けたら undo で戻せるように積む（S57）。
        /// Unity の Undo は窓と共通で、いちばん新しい記録が操作口のものか見分けられないので、自分で写しを持つ
        /// </summary>
        static string RecordWrite (System.Func<string> write)
        {
            AnimationClip clip = session_.targetClip;
            AnimationClip copy = clip != null ? Object.Instantiate (clip) : null;
            if (copy != null) copy.hideFlags = HideFlags.HideAndDontSave;
            int before = clip != null ? EditingClip.GetCurveVersion (clip) : 0;
            // サイクルキーの印は .meta にあり、カーブの写しでは戻らないので別に覚える（キーを消すと外れることがある）
            bool cycle = clip != null && ClipCycle.Load (clip);
            string problem = write ();
            if (problem != null || copy == null) {
                // 途中まで書いてから止まったら（writeMotion）、半分だけ書いた形を残さない
                if (problem != null && copy != null && EditingClip.GetCurveVersion (clip) != before) {
                    RestoreCurves (clip, copy);
                    if (ClipCycle.Load (clip) != cycle) ClipCycle.Save (clip, cycle);
                    session_.stage.InvalidateClipCurves ();
                }
                if (copy != null) Object.DestroyImmediate (copy);
                return problem;
            }
            undo_.Add (new WriteRecord { clip = clip, copy = copy, version = EditingClip.GetCurveVersion (clip), cycle = cycle });
            if (undo_.Count > kUndoLimit) {
                Object.DestroyImmediate (undo_[0].copy);
                undo_.RemoveAt (0);
            }
            return null;
        }

        /// <summary>
        /// 操作口で書いた最後の 1 回を戻す（S57）。書いた後にそのクリップが変わっていたら（窓で直した・Undo した など）、
        /// ユーザーの操作を消さないよう戻さずに理由を返す。戻すのも Unity の Undo に積むので、窓から Undo でやり直せる。応答は describe
        /// </summary>
        static string UndoWrite (out string error)
        {
            error = null;
            if (undo_.Count == 0) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NOTHING_TO_UNDO");
                return null;
            }
            WriteRecord record = undo_[undo_.Count - 1];
            if (record.clip == null || EditingClip.GetCurveVersion (record.clip) != record.version) {
                error = Tr ("MOTION_EDITOR_COMMANDS_UNDO_CHANGED", record.clip != null ? record.clip.name : "");
                return null;
            }
            undo_.RemoveAt (undo_.Count - 1);
            PoseCommit.InUndoGroup ("Undo Write", () => RestoreCurves (record.clip, record.copy));
            if (ClipCycle.Load (record.clip) != record.cycle) ClipCycle.Save (record.clip, record.cycle);
            Object.DestroyImmediate (record.copy);
            // 1 つ前の書き込みの後の形に戻ったので、その記録の目印を今の数にそろえる（続けて戻せるように）
            if (undo_.Count > 0 && undo_[undo_.Count - 1].clip == record.clip) undo_[undo_.Count - 1].version = EditingClip.GetCurveVersion (record.clip);
            session_.stage.InvalidateClipCurves ();
            session_.Evaluate (session_.frame);
            return JsonUtility.ToJson (Describe (session_));
        }

        /// <summary>clip のカーブを copy と同じにする（copy に無いカーブは消す）</summary>
        static void RestoreCurves (AnimationClip clip, AnimationClip copy)
        {
            Undo.RecordObject (clip, "Undo Write");
            List<EditorCurveBinding> bindings = new List<EditorCurveBinding> ();
            List<AnimationCurve> curves = new List<AnimationCurve> ();
            HashSet<EditorCurveBinding> saved = new HashSet<EditorCurveBinding> (AnimationUtility.GetCurveBindings (copy));
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (saved.Contains (binding)) continue;
                bindings.Add (binding);
                curves.Add (null);
            }
            foreach (EditorCurveBinding binding in saved) {
                bindings.Add (binding);
                curves.Add (AnimationUtility.GetEditorCurve (copy, binding));
            }
            if (bindings.Count > 0) AnimationUtility.SetEditorCurves (clip, bindings.ToArray (), curves.ToArray ());

            HashSet<EditorCurveBinding> savedReferences = new HashSet<EditorCurveBinding> (AnimationUtility.GetObjectReferenceCurveBindings (copy));
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (clip)) {
                if (!savedReferences.Contains (binding)) AnimationUtility.SetObjectReferenceCurve (clip, binding, null);
            }
            foreach (EditorCurveBinding binding in savedReferences) {
                AnimationUtility.SetObjectReferenceCurve (clip, binding, AnimationUtility.GetObjectReferenceCurve (copy, binding));
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
            // .bvh は外部の骨格（skeleton）の BVH として読む（S74）。それ以外は Humanoid のクリップのアセット
            bool bvhSource = !string.IsNullOrEmpty (request.source) && request.source.EndsWith (".bvh", System.StringComparison.OrdinalIgnoreCase);
            Bvh bvh = null;
            ExternalSkeleton skeleton = null;
            AnimationClip source = null;
            if (bvhSource) {
                string text = ReadJsonFile (request.source, out error);
                if (error != null) return null;
                bvh = Bvh.Parse (text, out error);
                if (error != null) return null;
                skeleton = string.IsNullOrEmpty (request.skeleton) ? null : AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (request.skeleton);
                if (skeleton == null) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_NO_SKELETON", request.skeleton);
                    return null;
                }
            }
            else {
                source = string.IsNullOrEmpty (request.source) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (request.source);
                if (source == null) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_NO_CLIP", request.source);
                    return null;
                }
            }
            AnimationClip destination = OpenClip (request.overrideClip, request.create, out error);
            if (error != null) return null;
            OverrideMode mode;
            if (!System.Enum.TryParse (string.IsNullOrEmpty (request.mode) ? "Additive" : request.mode, true, out mode)) {
                error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_MODE", request.mode);
                return null;
            }
            PoseSession.OverrideImportResult result;
            PoseSession.OverrideImportOptions options = new PoseSession.OverrideImportOptions {
                lockFeet = request.lockFeet,
                alignRoot = request.alignRoot,
                // .bvh は既定で指を取り込まない（打った手の形を残す）。.anim は今までどおり指も取り込む
                fingers = !bvhSource || request.fingers,
                keepKeys = request.keepKeys,
                keepKeysBlend = request.keepKeysBlend,
            };
            error = bvhSource
                ? session_.ImportOverride (bvh, skeleton, destination, mode, options, out result)
                : session_.ImportOverride (source, destination, mode, options, out result);
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

        /// <summary>
        /// キーの姿勢を外部の骨格（skeleton。ExternalSkeleton のアセット）へ写して、BVH を path へ書く（S73）。
        /// frames を省くとキーのフレーム。path の相対パスはプロジェクトのフォルダから
        /// </summary>
        static string ExportKeys (MotionEditorRequest request, out string error)
        {
            error = null;
            ExternalSkeleton skeleton = string.IsNullOrEmpty (request.skeleton) ? null : AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (request.skeleton);
            if (skeleton == null) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_SKELETON", request.skeleton);
                return null;
            }
            if (string.IsNullOrEmpty (request.path)) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_EXPORT_PATH");
                return null;
            }
            error = session_.ExportToSkeleton (skeleton, request.frames, out Bvh bvh, out List<int> frames);
            if (error != null) return null;
            try {
                string folder = Path.GetDirectoryName (Path.GetFullPath (request.path));
                if (!string.IsNullOrEmpty (folder)) Directory.CreateDirectory (folder);
                File.WriteAllText (request.path, bvh.ToText ());
            }
            catch (System.Exception e) {
                error = Tr ("MOTION_EDITOR_COMMANDS_CANNOT_WRITE_FILE", request.path, e.Message);
                return null;
            }
            return JsonUtility.ToJson (new MotionEditorExport {
                path = Path.GetFullPath (request.path).Replace (Path.DirectorySeparatorChar, '/'),
                frameRate = session_.clip.frameRate,
                lastFrame = LastFrame (session_.clip),
                frames = frames,
                jointCount = bvh.joints.Count,
            });
        }

        /// <summary>
        /// 手の形を打つ（S42）。プリセットの指の値を選んだ手へ当て（その手の値が無ければ反対の手の値を写す）、muscles で個別に上書きする。
        /// muscles の名前は左右を付けた名前（"Left Index 1 Stretched" / "LeftHand.Index.1 Stretched"）か、左右を省いた名前
        /// （"Index 1 Stretched" / "Index.1 Stretched"。選んだ手に当てる）。指でない muscle は受け付けない
        /// </summary>
        static string WriteHand (MotionEditorRequest request, out string error)
        {
            Dictionary<int, float> muscles = HandMuscles (request.preset, request.hand, request.muscles, out error);
            if (error != null) return null;
            session_.Evaluate (request.frame);
            error = RecordWrite (() => session_.WriteMuscles (muscles, "Write Hand"));
            return error == null ? ReadHand (request.frame, out error) : null;
        }

        /// <summary>
        /// 手の形の指定（preset を hand の指に当て、muscles で個別に上書きする）を、muscle の番号と値にする。打たない。
        /// 指定が誤っていれば null と理由（writeHand・writeMotion で先に確かめるため）
        /// </summary>
        static Dictionary<int, float> HandMuscles (string presetName, string hand, List<MotionEditorMuscle> entries, out string error)
        {
            error = null;
            bool left, right;
            if (!ParseHand (hand, out left, out right)) {
                error = Tr ("MOTION_EDITOR_COMMANDS_UNKNOWN_HAND", hand);
                return null;
            }
            Dictionary<int, float> muscles = new Dictionary<int, float> ();
            if (!string.IsNullOrEmpty (presetName)) {
                AnimationClip preset = FindPreset (presetName.Trim ());
                if (preset == null || !PoseBank.IsHumanoidPose (preset)) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_NO_PRESET", presetName, string.Join (", ", PresetNames ()));
                    return null;
                }
                // 指の値を手ごとに並べ直す（片手だけのプリセットは反対の手にも使う）
                Dictionary<string, float>[] byHand = { new Dictionary<string, float> (), new Dictionary<string, float> () };
                foreach (KeyValuePair<int, float> pair in PoseBank.ReadMuscles (preset)) {
                    bool isRight;
                    if (!PoseBank.IsFingerMuscle (pair.Key, out isRight)) continue;
                    byHand[isRight ? 1 : 0][WithoutSide (HumanTrait.MuscleName[pair.Key])] = pair.Value;
                }
                foreach ((bool use, string side, int self) in new[] { (left, "Left", 0), (right, "Right", 1) }) {
                    if (!use) continue;
                    Dictionary<string, float> values = byHand[self].Count > 0 ? byHand[self] : byHand[1 - self];
                    foreach (KeyValuePair<string, float> pair in values) {
                        int index;
                        if (PoseBank.TryGetMuscle (side + " " + pair.Key, out index)) muscles[index] = pair.Value;
                    }
                }
            }
            foreach (MotionEditorMuscle entry in entries ?? new List<MotionEditorMuscle> ()) {
                List<int> indices = new List<int> ();
                int index;
                bool isRight;
                if (PoseBank.TryGetMuscle (entry.name, out index)) {
                    indices.Add (index);
                }
                else {
                    // 左右を省いた名前は選んだ手へ（"Index.1 Stretched" は "Index 1 Stretched" と同じ）
                    string name = entry.name != null ? entry.name.Replace ('.', ' ') : "";
                    if (left && PoseBank.TryGetMuscle ("Left " + name, out index)) indices.Add (index);
                    if (right && PoseBank.TryGetMuscle ("Right " + name, out index)) indices.Add (index);
                }
                if (indices.Count == 0 || indices.Any (i => !PoseBank.IsFingerMuscle (i, out isRight))) {
                    error = Tr ("MOTION_EDITOR_COMMANDS_NOT_FINGER_MUSCLE", entry.name);
                    return null;
                }
                foreach (int i in indices) muscles[i] = entry.value;
            }
            if (muscles.Count == 0) {
                error = Tr ("MOTION_EDITOR_COMMANDS_NO_HAND_VALUES");
                return null;
            }
            return muscles;
        }

        static string ReadHand (int frame, out string error)
        {
            error = null;
            session_.Evaluate (frame);
            float[] muscles = session_.stage.ReadMuscles ();
            // 表示モデルへ当てた分を戻す（当てたままだと、次に読む・撮るまで表示モデルが Humanoid で丸めた姿勢になる）
            session_.Evaluate (frame);
            if (muscles == null) {
                error = Tr ("PREVIEW_WINDOW_POSE_BANK_DISPLAY_NOT_HUMANOID");
                return null;
            }
            MotionEditorHand hand = new MotionEditorHand { frame = frame, presets = PresetNames () };
            for (int i = 0; i < muscles.Length; i++) {
                bool isRight;
                if (PoseBank.IsFingerMuscle (i, out isRight)) hand.muscles.Add (new MotionEditorMuscle { name = HumanTrait.MuscleName[i], value = muscles[i] });
            }
            return JsonUtility.ToJson (hand);
        }

        static bool ParseHand (string hand, out bool left, out bool right)
        {
            string value = string.IsNullOrEmpty (hand) ? "Both" : hand.Trim ();
            bool both = value.Equals ("Both", System.StringComparison.OrdinalIgnoreCase);
            left = both || value.Equals ("Left", System.StringComparison.OrdinalIgnoreCase);
            right = both || value.Equals ("Right", System.StringComparison.OrdinalIgnoreCase);
            return left || right;
        }

        /// <summary>"Left Index 1 Stretched" → "Index 1 Stretched"</summary>
        static string WithoutSide (string muscle)
        {
            int space = muscle.IndexOf (' ');
            return space >= 0 ? muscle.Substring (space + 1) : muscle;
        }

        /// <summary>プリセットを名前（PoseBank の一覧の名前。大文字小文字は問わない）かアセットのパスで引く</summary>
        static AnimationClip FindPreset (string preset)
        {
            if (preset.EndsWith (".anim", System.StringComparison.OrdinalIgnoreCase)) return AssetDatabase.LoadAssetAtPath<AnimationClip> (preset);
            foreach (string folder in PoseBank.GetFolders (session_.stage.sourcePrefab)) {
                foreach (BankEntry entry in PoseBank.List (folder)) {
                    if (string.Equals (entry.name, preset, System.StringComparison.OrdinalIgnoreCase)) return AssetDatabase.LoadAssetAtPath<AnimationClip> (entry.path);
                }
            }
            return null;
        }

        /// <summary>このキャラで使える手の形のプリセット（PoseBank の Humanoid の姿勢）の名前</summary>
        static List<string> PresetNames ()
        {
            List<string> names = new List<string> ();
            foreach (string folder in PoseBank.GetFolders (session_.stage.sourcePrefab)) {
                foreach (BankEntry entry in PoseBank.List (folder)) {
                    if (!names.Contains (entry.name) && PoseBank.IsHumanoidPose (AssetDatabase.LoadAssetAtPath<AnimationClip> (entry.path))) names.Add (entry.name);
                }
            }
            return names;
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
                FullBodyRig body = stage.fullBody;
                Dictionary<BodyPointId, BodyPointTarget> targets = session.targets.OfType<BodyPointTarget> ().ToDictionary (t => t.id);
                foreach (BodyPointId id in body.pointIds) {
                    // 骨の長さ・T ポーズの場所は、キーが入った後でも基準姿勢から出す（AI が 0 フレームを読んで推測しなくて済むように。S58）
                    Vector3 rest = PoseDocument.RestPosition (session, id);
                    BodyPointTarget point;
                    targets.TryGetValue (id, out point);
                    BodyPointTarget link = point != null ? point.link : null;
                    RigBinding.Point binding = stage.editingRig.binding.points.FirstOrDefault (p => p.name == id.name);
                    description.points.Add (new MotionEditorPointInfo {
                        name = id.name,
                        hasRotation = body.PointHasRotation (id),
                        bone = point != null ? point.bone.ToString () : "",
                        rest = rest,
                        link = link != null ? link.id.name : "",
                        length = link != null ? Vector3.Distance (rest, PoseDocument.RestPosition (session, link.id)) : 0,
                        contact = body.IsContact (id),
                        ground = binding != null && binding.definition != null && binding.definition.ground,
                    });
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
            /// <summary>焼いて作ったカーブのキーの数と、間引く前の数</summary>
            public int keyCount;
            public int keyCountBeforeReduce;
            public double milliseconds;
            public List<string> notes;
        }

        [System.Serializable]
        sealed class SaveResult
        {
            public List<string> paths;
        }

        /// <summary>writePose の応答に足す項目（S57）</summary>
        [System.Serializable]
        sealed class WriteExtra
        {
            /// <summary>目標から kReachTolerance × humanScale より離れた点</summary>
            public List<string> unreached;
            /// <summary>そのフレームの床と muscle の範囲の検査</summary>
            public List<MotionLintIssue> issues;
            /// <summary>snap で寄せた点（S60）</summary>
            public List<PoseSnap> snapped;
        }

        /// <summary>操作口で書いた 1 回分（undo で戻す）</summary>
        sealed class WriteRecord
        {
            public AnimationClip clip;
            /// <summary>書く前のクリップの写し</summary>
            public AnimationClip copy;
            /// <summary>書いた直後のクリップのカーブの版（EditingClip.GetCurveVersion）。変わっていたら戻さない</summary>
            public int version;
            /// <summary>書く前のサイクルキーの印</summary>
            public bool cycle;
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
        /// <summary>writePose でキーを打たずに解いた姿勢だけを返す（S57）</summary>
        public bool dryRun;
        /// <summary>readPose・writePose の応答を、丸めて項目を省いた形（PoseDocument.ToCompactJson）にする（S58）</summary>
        public bool compact;
        /// <summary>writePose の姿勢を、pose の代わりにこのファイル（PoseDocument の JSON）から読む（S58。相対パスはプロジェクトのフォルダから）</summary>
        public string poseFile;
        /// <summary>writeMotion で書くキー（S59）</summary>
        public List<MotionEditorKey> keys = new List<MotionEditorKey> ();
        /// <summary>writeMotion で、書く前に書き先のクリップのキーを全部消す</summary>
        public bool replace;
        /// <summary>writeMotion の後に全体を検査して返す</summary>
        public bool lint = true;
        /// <summary>deleteKeys で消すキーのフレーム（S61）</summary>
        public List<int> frames = new List<int> ();
        /// <summary>deleteKeys で全部のキーを消す</summary>
        public bool all;
        /// <summary>sample のフレームの間隔（省くと 1）</summary>
        public int step = 1;
        /// <summary>sample で返す点の名前（省くと全部）</summary>
        public List<string> pointNames = new List<string> ();
        /// <summary>sample で返す muscle の名前（HumanTrait.MuscleName。省くと返さない）</summary>
        public List<string> muscleNames = new List<string> ();
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
        /// <summary>importOverride（.bvh）で、最初のフレームの体の向きと地面の上の場所を元に合わせる（S74）</summary>
        public bool alignRoot = true;
        /// <summary>importOverride（.bvh）で指も取り込む（既定は取り込まず、打った手の形を残す。.anim は今までどおり指も取り込む）</summary>
        public bool fingers;
        /// <summary>importOverride で、元のクリップのキーのフレームでは元のキーの姿勢に戻し、前後 keepKeysBlend 秒でなめらかにつなぐ（S75）</summary>
        public bool keepKeys;
        public float keepKeysBlend = 0.25f;
        /// <summary>writeHand のプリセット（PoseBank の姿勢の名前かパス。Hand_Fist など）</summary>
        public string preset;
        /// <summary>writeHand で当てる手（Left / Right / Both。省くと Both）</summary>
        public string hand;
        /// <summary>writeHand で個別に当てる指の muscle（プリセットの後に当てる）</summary>
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
        /// <summary>exportKeys で写す外部の骨格（ExternalSkeleton のアセットのパス。S73）</summary>
        public string skeleton;
        /// <summary>exportKeys で書く BVH のパス（相対パスはプロジェクトのフォルダから）</summary>
        public string path;
    }

    /// <summary>exportKeys の応答（S73）</summary>
    [System.Serializable]
    public sealed class MotionEditorExport
    {
        /// <summary>書いた BVH のパス</summary>
        public string path;
        public float frameRate;
        public int lastFrame;
        /// <summary>書いたフレーム（BVH の 1 フレームがこの 1 つ）</summary>
        public List<int> frames = new List<int> ();
        public int jointCount;
    }

    /// <summary>muscle の名前と値（writeHand・readHand）</summary>
    [System.Serializable]
    public sealed class MotionEditorMuscle
    {
        /// <summary>"Left Index 1 Stretched" / "LeftHand.Index.1 Stretched"、writeHand では左右を省いた "Index 1 Stretched" も</summary>
        public string name;
        public float value;
    }

    /// <summary>手の形（readHand・writeHand の応答）</summary>
    [System.Serializable]
    public sealed class MotionEditorHand
    {
        public int frame;
        /// <summary>両手の指の muscle の値（HumanTrait.MuscleName の名前）</summary>
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
        /// <summary>このキャラで使えるプリセットの名前（writeHand の preset）</summary>
        public List<string> presets = new List<string> ();
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

    /// <summary>writeMotion・readMotion のキー 1 つ（S59）。体の点と手の形のどちらか、または両方</summary>
    [System.Serializable]
    public sealed class MotionEditorKey
    {
        public int frame;
        /// <summary>全身 IK の点（PoseDocument の points と同じ）</summary>
        public List<PosePoint> points = new List<PosePoint> ();
        /// <summary>接地をまとめて書く（PoseDocument の contacts と同じ。S60）</summary>
        public List<PoseContact> contacts = new List<PoseContact> ();
        /// <summary>手の形（writeHand と同じ指定。並べた順に当て、後のものが上書きする）</summary>
        public List<MotionEditorHandShape> hands = new List<MotionEditorHandShape> ();
        /// <summary>このキーから次のキーまでの補間（S80。curve / linear / constant）。省くと変えない。readMotion では mixed もある（最後のキーには無い）</summary>
        public string interpolation;
    }

    /// <summary>手の形の指定（writeMotion。writeHand の preset・hand・muscles と同じ）</summary>
    [System.Serializable]
    public sealed class MotionEditorHandShape
    {
        public string preset;
        /// <summary>Left / Right / Both（省くと Both）</summary>
        public string hand;
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
    }

    /// <summary>要求を並べたもの（-executeMethod で渡すファイル）</summary>
    [System.Serializable]
    public sealed class MotionEditorBatch
    {
        public List<MotionEditorRequest> requests = new List<MotionEditorRequest> ();
        /// <summary>最後に閉じない（S59。Unity CLI から並べて送り、続けて読み書きするとき）。-executeMethod の口では使わず、必ず閉じる</summary>
        public bool keepOpen;
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
        /// <summary>点が乗る骨（HumanBodyBones の名前）</summary>
        public string bone;
        /// <summary>基準姿勢（T ポーズ）での場所（体の向き基準・メートル。キーが入った後でも基準姿勢のまま）</summary>
        public Vector3 rest;
        /// <summary>点をつなぐ線の相手の点（定義の link。空なら骨の階層の 1 つ上の関節の点。無ければ空）</summary>
        public string link;
        /// <summary>基準姿勢での link の点からの長さ（メートル。上腕・前腕などの骨の長さ。link が無ければ 0）</summary>
        public float length;
        /// <summary>接地の点か（定義の contact。床と足の滑りを見る点）</summary>
        public bool contact;
        /// <summary>基準の場所を地面に落とす点か（定義の ground。足裏の点）</summary>
        public bool ground;
    }

}
