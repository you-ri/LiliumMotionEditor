using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// モーションの検査（S37）。フレームごとに姿勢を作って（PoseSession。窓と同じ段を通す）、問題のあるフレームと場所を出す。
    /// AI が自分の作ったモーションを確かめるのに使うが、人が使っても役に立つ。
    /// - FootSlide: 接地している足の点が、1 フレームで水平に動いた（メートル）
    /// - FloorPenetration: 点（全身 IK でなければ骨）が床（Y=0）より下にある（メートル。負の高さ）
    /// - MuscleRange: Humanoid の muscle が範囲を超えた。ゲーム用に焼くと丸められる所（muscle の値）
    /// - Pop: 骨の 1 フレームの回転が、前後のフレームの回転の 3 倍 + 0.5° を越えた、または 1 フレームだけ前後から外れた（跳ね。度）
    /// - LargeKeyRotation: 隣り合うキーの間の回転が 90° を超えた。回転のカーブは 4 成分ごとの補間なので、速さが歪み、180° を超えると逆に回る（度）
    /// 同じ問題が続くフレームは 1 件にまとめる（frame〜endFrame。value はその間の一番大きい値）。
    /// 左右の非対称度は問題にせず、値だけ出す（左右の muscle の差の平均の最大）
    /// </summary>
    public static class MotionLint
    {
        public const string kFootSlide = "FootSlide";
        public const string kFloorPenetration = "FloorPenetration";
        public const string kMuscleRange = "MuscleRange";
        public const string kPop = "Pop";
        public const string kLargeKeyRotation = "LargeKeyRotation";

        /// <summary>閾値（評価指標と閾値の「検査」）</summary>
        [System.Serializable]
        public sealed class Settings
        {
            /// <summary>接地している足の点の 1 フレームの水平の動きの上限（メートル。体の大きさ 1 のとき）</summary>
            public float footSlide = 0.005f;
            /// <summary>接地とみなす足の点の高さ（メートル。体の大きさ 1 のとき）</summary>
            public float contactHeight = 0.03f;
            /// <summary>床より下に許す深さ（メートル）</summary>
            public float floorTolerance = 0.005f;
            /// <summary>muscle の上限（1 を少し超えるのは許す）</summary>
            public float muscleLimit = 1.05f;
            /// <summary>跳ね: 前後の変化の何倍まで許すか</summary>
            public float popRatio = 3;
            /// <summary>跳ね: 足す分（度）</summary>
            public float popMargin = 0.5f;
            /// <summary>
            /// 跳ね: 前後を結んだ中間から離れてよいのは、前後の回転の何倍まで。一定の速さなら 0、急に止まる・動き出すなら 0.5、
            /// 1 フレームだけ跳ねる・行って戻るなら 1 くらいになる
            /// </summary>
            public float popSpike = 0.75f;
            /// <summary>キーの間の回転の上限（度）</summary>
            public float keyRotation = 90;
        }

        /// <summary>
        /// start〜end のフレーム（両端を含む）を検査する。最後に start のフレームの姿勢に戻す
        /// </summary>
        public static MotionLintReport Run (PoseSession session, int start, int end, Settings settings = null)
        {
            settings = settings ?? new Settings ();
            MotionLintReport report = new MotionLintReport { startFrame = start, endFrame = end };
            if (session == null || session.stage == null || session.stage.editingRig == null) {
                report.error = session != null && session.error != null ? session.error : Tr ("POSE_SESSION_NO_CHARACTER");
                return report;
            }
            if (end < start) {
                report.error = Tr ("MOTION_LINT_BAD_RANGE", start, end);
                return report;
            }

            PreviewStage stage = session.stage;
            EditingRig rig = stage.editingRig;
            float scale = rig.humanScale > 0 ? rig.humanScale : 1;
            List<Transform> bones = rig.editingBones.Where (b => b != null).ToList ();
            List<(string name, System.Func<Vector3> position, bool foot)> points = CollectPoints (stage);
            Runs runs = new Runs (report);
            int frameCount = end - start + 1;
            Quaternion[,] rotations = new Quaternion[frameCount, bones.Count];
            Vector3[] previousFeet = new Vector3[points.Count];
            bool[] previousContact = new bool[points.Count];

            HumanoidBaker reader = rig.displayAnimator != null && rig.displayAnimator.isHuman ? stage.CreateHumanoidBaker () : null;
            if (reader != null && reader.error != null) {
                reader.Dispose ();
                reader = null;
            }
            HumanPose pose = new HumanPose ();
            List<(int left, int right)> pairs = MusclePairs ();
            // 基準姿勢（T ポーズ）で、もう範囲の外にある muscle（キャラの作りによる。親指など）は、基準姿勢より外へ出たときだけ出す
            float[] restMuscles = null;
            if (reader != null) {
                rig.ResetBonesToRest ();
                if (reader.ReadPose (ref pose)) restMuscles = (float[])pose.muscles.Clone ();
            }
            try {
                for (int f = start; f <= end; f++) {
                    session.Evaluate (f);
                    for (int b = 0; b < bones.Count; b++) rotations[f - start, b] = bones[b].localRotation;

                    for (int i = 0; i < points.Count; i++) {
                        Vector3 p = points[i].position ();
                        runs.Add (kFloorPenetration, points[i].name, f, -p.y, settings.floorTolerance, p.y < -settings.floorTolerance);
                        if (!points[i].foot) continue;
                        bool contact = p.y <= settings.contactHeight * scale;
                        if (f > start && contact && previousContact[i]) {
                            Vector3 move = p - previousFeet[i];
                            move.y = 0;
                            float limit = settings.footSlide * scale;
                            runs.Add (kFootSlide, points[i].name, f, move.magnitude, limit, move.magnitude > limit);
                        }
                        previousFeet[i] = p;
                        previousContact[i] = contact;
                    }

                    if (reader != null && reader.ReadPose (ref pose)) {
                        for (int m = 0; m < pose.muscles.Length && m < HumanTrait.MuscleCount; m++) {
                            float value = Mathf.Abs (pose.muscles[m]);
                            float limit = settings.muscleLimit;
                            if (restMuscles != null && m < restMuscles.Length) limit = Mathf.Max (limit, Mathf.Abs (restMuscles[m]) + settings.muscleLimit - 1);
                            runs.Add (kMuscleRange, HumanTrait.MuscleName[m], f, value, limit, value > limit);
                        }
                        if (pairs.Count > 0) {
                            float asymmetry = pairs.Average (p => Mathf.Abs (pose.muscles[p.left] - pose.muscles[p.right]));
                            if (asymmetry > report.maxAsymmetry) {
                                report.maxAsymmetry = asymmetry;
                                report.asymmetryFrame = f;
                            }
                        }
                    }
                }
            }
            finally {
                if (reader != null) reader.Dispose ();
                session.Evaluate (start);
            }

            FindPops (runs, bones, rotations, start, settings);
            FindLargeKeyRotations (report, session.targetClip ?? session.clip, settings);
            runs.Close ();
            report.issues.Sort ((a, b) => a.frame != b.frame ? a.frame.CompareTo (b.frame) : string.CompareOrdinal (a.kind + a.target, b.kind + b.target));
            return report;
        }

        /// <summary>
        /// 床と接地を見る点。全身 IK のキャラは点（骨の上の点。かかと・つま先の先など）で、定義の接地の点（contact）を接地の対象にする。
        /// そうでなければ骨で、足（足首とつま先の骨）を接地の対象にする
        /// </summary>
        static List<(string, System.Func<Vector3>, bool)> CollectPoints (PreviewStage stage)
        {
            EditingRig rig = stage.editingRig;
            HashSet<Transform> feet = new HashSet<Transform> ();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }) {
                Transform t = rig.GetEditingBone (bone);
                if (t != null) feet.Add (t);
            }
            List<(string, System.Func<Vector3>, bool)> result = new List<(string, System.Func<Vector3>, bool)> ();
            FullBodyRig body = stage.fullBody;
            if (body != null) {
                foreach (BodyPointId id in body.pointIds) {
                    BodyPointId point = id;
                    result.Add ((id.name, () => {
                        Vector3 position;
                        body.TryGetBonePointWorld (point, out position);
                        return position;
                    }, body.IsContact (id)));
                }
                return result;
            }
            foreach (Transform bone in rig.editingBones) {
                if (bone == null) continue;
                Transform t = bone;
                result.Add ((t.name, () => t.position, feet.Contains (t)));
            }
            return result;
        }

        /// <summary>左右で対になる muscle（"Left ..." と "Right ..."）の番号</summary>
        static List<(int, int)> MusclePairs ()
        {
            List<(int, int)> pairs = new List<(int, int)> ();
            string[] names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++) {
                if (!names[i].StartsWith ("Left ")) continue;
                int right = System.Array.IndexOf (names, "Right " + names[i].Substring (5));
                if (right >= 0) pairs.Add ((i, right));
            }
            return pairs;
        }

        /// <summary>
        /// 跳ね。2 通りで見る。value はそのフレームへの回転（度）
        /// - 飛ぶ: フレーム f から f+1 への骨の回転が、前後（f-1→f と f+1→f+2）の回転の大きい方の popRatio 倍 + popMargin を越えた（f+1 の問題）
        /// - 1 フレームだけ外れる: フレーム f の姿勢が、前後のフレームを結んだ回転の中間から、前後の回転の大きい方の popSpike 倍 + popMargin より離れた（f の問題）。
        ///   1 フレームだけ跳ねると前後 2 つの回転がどちらも大きくなり、上の式ではお互いを「前後」にして見逃すため
        /// </summary>
        static void FindPops (Runs runs, List<Transform> bones, Quaternion[,] rotations, int start, Settings settings)
        {
            int count = rotations.GetLength (0);
            if (count < 3) return;
            for (int b = 0; b < bones.Count; b++) {
                float[] steps = new float[count - 1];
                for (int i = 0; i < steps.Length; i++) steps[i] = Quaternion.Angle (rotations[i, b], rotations[i + 1, b]);
                for (int f = 1; f < count; f++) {
                    // f-1 → f が飛んだか
                    int i = f - 1;
                    float before = i > 0 ? steps[i - 1] : 0;
                    float after = i + 1 < steps.Length ? steps[i + 1] : 0;
                    float jumpLimit = Mathf.Max (before, after) * settings.popRatio + settings.popMargin;
                    if (steps[i] > jumpLimit) {
                        runs.Add (kPop, bones[b].name, start + f, steps[i], jumpLimit, true);
                        continue;
                    }
                    // f が前後から外れたか（最後のフレームは後ろが無い）。値は中間からの離れ
                    float off = 0;
                    float spikeLimit = 0;
                    if (f + 1 < count) {
                        off = Quaternion.Angle (rotations[f, b], Quaternion.Slerp (rotations[f - 1, b], rotations[f + 1, b], 0.5f));
                        spikeLimit = Mathf.Max (steps[f - 1], steps[f]) * settings.popSpike + settings.popMargin;
                    }
                    runs.Add (kPop, bones[b].name, start + f, off, spikeLimit, f + 1 < count && off > spikeLimit);
                }
            }
        }

        /// <summary>隣り合うキーの間の回転（回転のカーブを持つ物ごと。編集用リグのカーブだけ）</summary>
        static void FindLargeKeyRotations (MotionLintReport report, AnimationClip clip, Settings settings)
        {
            if (clip == null || clip.humanMotion) return;
            Dictionary<string, AnimationCurve[]> groups = new Dictionary<string, AnimationCurve[]> ();
            string[] axes = { "x", "y", "z", "w" };
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (binding.type != typeof (Transform) || !EditingClip.IsRigBinding (binding)) continue;
                if (!binding.propertyName.StartsWith ("m_LocalRotation.")) continue;
                int axis = System.Array.IndexOf (axes, binding.propertyName.Substring ("m_LocalRotation.".Length));
                if (axis < 0) continue;
                AnimationCurve[] curves;
                if (!groups.TryGetValue (binding.path, out curves)) groups[binding.path] = curves = new AnimationCurve[4];
                curves[axis] = AnimationUtility.GetEditorCurve (clip, binding);
            }
            foreach (KeyValuePair<string, AnimationCurve[]> group in groups.OrderBy (g => g.Key)) {
                AnimationCurve[] curves = group.Value;
                if (curves.Any (c => c == null)) continue;
                List<float> times = curves.SelectMany (c => c.keys.Select (k => k.time)).Distinct ().OrderBy (t => t).ToList ();
                for (int i = 1; i < times.Count; i++) {
                    float angle = Quaternion.Angle (Evaluate (curves, times[i - 1]), Evaluate (curves, times[i]));
                    if (angle <= settings.keyRotation) continue;
                    report.issues.Add (new MotionLintIssue {
                        kind = kLargeKeyRotation,
                        target = group.Key,
                        frame = Mathf.RoundToInt (times[i - 1] * clip.frameRate),
                        endFrame = Mathf.RoundToInt (times[i] * clip.frameRate),
                        value = angle,
                        limit = settings.keyRotation,
                    });
                }
            }
        }

        static Quaternion Evaluate (AnimationCurve[] curves, float time)
        {
            return Quaternion.Normalize (new Quaternion (curves[0].Evaluate (time), curves[1].Evaluate (time), curves[2].Evaluate (time), curves[3].Evaluate (time)));
        }

        /// <summary>同じ物の同じ問題が続くフレームを 1 件にまとめる</summary>
        sealed class Runs
        {
            readonly MotionLintReport report_;
            readonly Dictionary<(string, string), MotionLintIssue> open_ = new Dictionary<(string, string), MotionLintIssue> ();

            public Runs (MotionLintReport report)
            {
                report_ = report;
            }

            public void Add (string kind, string target, int frame, float value, float limit, bool bad)
            {
                (string, string) key = (kind, target);
                MotionLintIssue issue;
                bool running = open_.TryGetValue (key, out issue);
                if (!bad) {
                    if (running) open_.Remove (key);
                    return;
                }
                if (running && issue.endFrame == frame - 1) {
                    issue.endFrame = frame;
                    issue.value = Mathf.Max (issue.value, value);
                    return;
                }
                issue = new MotionLintIssue { kind = kind, target = target, frame = frame, endFrame = frame, value = value, limit = limit };
                open_[key] = issue;
                report_.issues.Add (issue);
            }

            public void Close ()
            {
                open_.Clear ();
            }
        }
    }

    /// <summary>検査の結果（MotionLint）。JsonUtility で書き出せる</summary>
    [System.Serializable]
    public sealed class MotionLintReport
    {
        public int startFrame;
        public int endFrame;
        /// <summary>検査できなかった理由（できたら null）</summary>
        public string error;
        /// <summary>左右の muscle の差の平均の最大（0 なら左右対称）と、そのフレーム</summary>
        public float maxAsymmetry;
        public int asymmetryFrame;
        public List<MotionLintIssue> issues = new List<MotionLintIssue> ();

        public string ToJson (bool pretty = true)
        {
            return JsonUtility.ToJson (this, pretty);
        }

        public int Count (string kind)
        {
            return issues.Count (i => i.kind == kind);
        }
    }

    /// <summary>検査で見つかった問題 1 件</summary>
    [System.Serializable]
    public sealed class MotionLintIssue
    {
        /// <summary>種類（MotionLint.kFootSlide など）</summary>
        public string kind;
        /// <summary>場所（点・骨・muscle の名前、カーブのパス）</summary>
        public string target;
        /// <summary>問題が始まるフレームと終わるフレーム（両端を含む。キーの間の回転は前のキーと後ろのキー）</summary>
        public int frame;
        public int endFrame;
        /// <summary>その間の一番大きい値と、上限（単位は種類ごと）</summary>
        public float value;
        public float limit;
    }

}
