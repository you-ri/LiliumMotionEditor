using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 姿勢を少ない値で読み書きする形（S36。AI や外部の道具とのやりとり）。全身 IK の点の場所・向き・状態だけを持つ。
    /// - 場所は体の向き基準（x 右・y 上・z 前。メートル）で、キャラの足元（編集用の体のルート）から。
    /// - 向きは基準姿勢（T ポーズ）からの差を、体の向き基準のオイラー角（度。-180〜180）で。向きを持つ点だけ。
    /// - 骨の回転（四元数）や操作値の名前は出さない。AI は四元数の値を扱えないため。
    ///
    /// 読むと、そのフレームの姿勢での骨の上の点（固定した点の目標ではなく、体が実際にある所）を返す。
    /// 書くと、点を置いて全身 IK で解き、窓で点を離したときと同じくキーを打つ（PoseSession.CommitPose）。
    /// 書くのは並べた点だけで、並べなかった点はそのフレームの状態のまま。
    /// 1 つのキーは全身の骨のキーになる（手だけを打つことはできない）。接地を保ちたい区間は、両端のキーでその点を Locked にする。
    /// 全身 IK の点が 1 つも置けなかったキャラ（PreviewStage.fullBody が無い）は読み書きできない
    /// </summary>
    [System.Serializable]
    public sealed class PoseDocument
    {
        public int frame;
        /// <summary>体の大きさ（Humanoid の humanScale。メートル）。場所をこれで割ると、体型によらない値になる</summary>
        public float humanScale;
        public List<PosePoint> points = new List<PosePoint> ();

        public string ToJson (bool pretty = true)
        {
            return JsonUtility.ToJson (this, pretty);
        }

        public static PoseDocument FromJson (string json)
        {
            return string.IsNullOrEmpty (json) ? null : JsonUtility.FromJson<PoseDocument> (json);
        }

        public PosePoint Find (string name)
        {
            return points.FirstOrDefault (p => p != null && p.name == name);
        }

        /// <summary>
        /// 体の大きさの違うキャラへ写す。場所を足元からの比で伸び縮みさせる（向きと状態はそのまま）
        /// </summary>
        public void Rescale (float targetHumanScale)
        {
            if (humanScale <= 0 || targetHumanScale <= 0) return;
            float ratio = targetHumanScale / humanScale;
            foreach (PosePoint point in points) {
                if (point != null) point.position *= ratio;
            }
            humanScale = targetHumanScale;
        }

        /// <summary>
        /// そのフレームの姿勢を読む。読めなければ null と理由
        /// </summary>
        public static PoseDocument Read (PoseSession session, int frame, out string problem)
        {
            problem = GetProblem (session);
            if (problem != null) return null;

            session.Evaluate (frame);
            EditingRig rig = session.stage.editingRig;
            FullBodyRig body = session.stage.fullBody;
            Quaternion toBody = Quaternion.Inverse (BodyBasis (rig));
            PoseDocument document = new PoseDocument { frame = frame, humanScale = rig.humanScale };
            foreach (BodyPointId id in body.pointIds) {
                Vector3 local;
                Quaternion delta;
                if (!body.TryGetCapturedValues (body.GetControl (id), out local, out delta)) continue;
                PosePoint point = new PosePoint {
                    name = id.name,
                    state = StateOf (body, id).ToString (),
                    position = toBody * (local * rig.humanScale),
                    hasRotation = body.PointHasRotation (id),
                };
                if (point.hasRotation) {
                    point.rotation = SignedEuler (toBody * delta * Quaternion.Inverse (toBody));
                    point.pinRotation = body.GetRotationWeight (id) > 0;
                }
                document.points.Add (point);
            }
            return document;
        }

        /// <summary>
        /// 姿勢を書く（document.frame のキーにする）。書けなければ何も変えずに理由を返す。書けたら null
        /// </summary>
        public static string Write (PoseSession session, PoseDocument document)
        {
            string problem = GetProblem (session);
            if (problem != null) return problem;
            if (document == null || document.points == null) return Tr ("POSE_DOCUMENT_EMPTY");

            // 先に全部確かめる（途中で止めて、半分だけ置いた状態を残さない）
            FullBodyRig body = session.stage.fullBody;
            List<(PosePoint point, BodyPointTarget target, BodyPointState state)> entries = new List<(PosePoint, BodyPointTarget, BodyPointState)> ();
            foreach (PosePoint point in document.points) {
                if (point == null) continue;
                BodyPointId id = new BodyPointId (point.name);
                Transform control = body.GetControl (id);
                BodyPointTarget target = control != null ? session.targets.OfType<BodyPointTarget> ().FirstOrDefault (t => t.gameObject == control.gameObject) : null;
                if (target == null) return Tr ("POSE_DOCUMENT_UNKNOWN_POINT", point.name, string.Join (", ", body.pointIds.Select (p => p.name)));
                BodyPointState state;
                if (!System.Enum.TryParse (point.state, true, out state) || !System.Enum.IsDefined (typeof (BodyPointState), state)) {
                    return Tr ("POSE_DOCUMENT_UNKNOWN_STATE", point.name, point.state);
                }
                entries.Add ((point, target, state));
            }

            session.Evaluate (document.frame);
            problem = session.GetWriteProblem ();
            if (problem != null) return problem;
            EditingRig rig = session.stage.editingRig;
            Quaternion toRoot = BodyBasis (rig);
            foreach ((PosePoint point, BodyPointTarget target, BodyPointState state) in entries) {
                if (state == BodyPointState.Free) {
                    target.SetState (BodyPointState.Free);
                    continue;
                }
                bool rotation = point.hasRotation && point.pinRotation && body.PointHasRotation (new BodyPointId (point.name));
                TransformChannels channels = TransformChannels.Position | (rotation ? TransformChannels.Rotation : 0);
                target.SetValues (channels, toRoot * point.position, toRoot * Quaternion.Euler (point.rotation) * Quaternion.Inverse (toRoot));
                if (!rotation) target.ReleaseRotation ();
                target.SetState (state);
            }
            session.SolveAfterEdit ();
            return session.CommitPose ("Write Pose");
        }

        /// <summary>読み書きできない理由（できるなら null）</summary>
        public static string GetProblem (PoseSession session)
        {
            if (session == null || session.stage == null || session.stage.editingRig == null) {
                return session != null && session.error != null ? session.error : Tr ("POSE_SESSION_NO_CHARACTER");
            }
            if (session.stage.fullBody == null) return Tr ("POSE_DOCUMENT_NO_POINTS");
            return null;
        }

        /// <summary>体の向き基準（x 右・y 上・z 前）から、編集用ルートから見た向きへの回転</summary>
        static Quaternion BodyBasis (EditingRig rig)
        {
            return Quaternion.LookRotation (rig.bodyForward, rig.bodyUp);
        }

        static BodyPointState StateOf (FullBodyRig body, BodyPointId id)
        {
            bool pinned = body.GetPositionWeight (id) > 0 || body.GetRotationWeight (id) > 0;
            if (!pinned) return BodyPointState.Free;
            return body.GetLocked (id) ? BodyPointState.Locked : BodyPointState.Pin;
        }

        static Vector3 SignedEuler (Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            for (int i = 0; i < 3; i++) {
                if (euler[i] > 180) euler[i] -= 360;
            }
            return euler;
        }
    }

    /// <summary>全身 IK の点 1 つ（PoseDocument）</summary>
    [System.Serializable]
    public sealed class PosePoint
    {
        /// <summary>点の名前（リグ定義の点の一覧の名前。LeftHand・LeftHeel・HeadTop など）</summary>
        public string name;
        /// <summary>Free（体に付いて動く）/ Pin（この場所へ寄せる）/ Locked（置いた場所から動かさない。次のキーまで留まる）</summary>
        public string state;
        /// <summary>体の向き基準（x 右・y 上・z 前）の、足元からの場所（メートル）。Free の点を書くときは使わない</summary>
        public Vector3 position;
        /// <summary>向きを持つ点か（読んだときに入る）</summary>
        public bool hasRotation;
        /// <summary>向きも固定するか（Pin / Locked で、向きを持つ点だけ）</summary>
        public bool pinRotation;
        /// <summary>基準姿勢（T ポーズ）からの向きの差。体の向き基準のオイラー角（度）</summary>
        public Vector3 rotation;
    }

}
