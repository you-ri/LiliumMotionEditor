using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
    /// 書くのは並べた点だけで、並べなかった点はそのフレームの状態のまま。キーを打たずに解いた姿勢だけを見るときは Solve → ReadCurrent。
    /// 1 つのキーは全身の骨のキーになる（手だけを打つことはできない）。接地を保ちたい区間は、両端のキーでその点を Locked にする。
    /// 全身 IK の点が 1 つも置けなかったキャラ（PreviewStage.fullBody が無い）は読み書きできない
    /// </summary>
    [System.Serializable]
    public sealed partial class PoseDocument
    {
        public int frame;
        /// <summary>体の大きさ（Humanoid の humanScale。メートル）。場所をこれで割ると、体型によらない値になる</summary>
        public float humanScale;
        public List<PosePoint> points = new List<PosePoint> ();
        /// <summary>接地をまとめて書く（S60。書くときだけ使う。PoseContact）</summary>
        public List<PoseContact> contacts = new List<PoseContact> ();
        /// <summary>持ち物（S91）の場所・向き・持っているか。読むとキャラの持ち物が全部入る。書くときは並べた物だけ変える</summary>
        public List<PoseObject> objects = new List<PoseObject> ();

        /// <summary>書く中身があるか（点・接地・持ち物のどれか）</summary>
        public bool hasContent
        {
            get {
                return points != null && points.Count > 0 || contacts != null && contacts.Count > 0 || objects != null && objects.Count > 0;
            }
        }

        public string ToJson (bool pretty = true)
        {
            return JsonUtility.ToJson (this, pretty);
        }

        /// <summary>
        /// トークンを抑えた JSON（S58。AI へ返す形）。場所と residual を 1mm、向きを 0.1° に丸め、Free の点の場所・向き、
        /// 向きを持たない点の向きの項目、0 の residual を省く。FromJson でそのまま読め、書き戻すと丸めの分だけ違う（≤0.5mm・≤0.05°）。
        /// JsonUtility は項目を省けないので手で組み立てる
        /// </summary>
        public string ToCompactJson ()
        {
            StringBuilder json = new StringBuilder ();
            json.Append ("{\"frame\":").Append (frame)
                .Append (",\"humanScale\":").Append (Number (humanScale, 4))
                .Append (",\"points\":");
            AppendCompactPoints (json);
            AppendCompactObjects (json);
            return json.Append ('}').ToString ();
        }

        /// <summary>持ち物の並びを ",\"objects\":[...]" の形で書く（持ち物の無いキャラは何も書かない）</summary>
        internal void AppendCompactObjects (StringBuilder json)
        {
            if (objects == null || objects.Count == 0) return;
            json.Append (",\"objects\":[");
            for (int i = 0; i < objects.Count; i++) {
                PoseObject item = objects[i];
                if (i > 0) json.Append (',');
                json.Append ("{\"name\":\"").Append (item.name).Append ("\",\"position\":");
                AppendVector (json, item.position, 3);
                json.Append (",\"aim\":{\"forward\":");
                AppendVector (json, item.aim.forward, 3);
                json.Append (",\"up\":");
                AppendVector (json, item.aim.up, 3);
                json.Append ("},\"attach\":\"").Append (item.attach).Append ("\"}");
            }
            json.Append (']');
        }

        /// <summary>点の並びを ToCompactJson と同じ形で書く（[...]）</summary>
        internal void AppendCompactPoints (StringBuilder json)
        {
            json.Append ('[');
            for (int i = 0; i < points.Count; i++) {
                PosePoint point = points[i];
                if (i > 0) json.Append (',');
                json.Append ("{\"name\":\"").Append (point.name).Append ("\",\"state\":\"").Append (point.state).Append ('"');
                if (point.state != BodyPointState.Free.ToString ()) {
                    json.Append (",\"position\":");
                    AppendVector (json, point.position, 3);
                    if (point.hasRotation) {
                        json.Append (",\"hasRotation\":true,\"rotation\":");
                        AppendVector (json, point.rotation, 1);
                        if (point.pinRotation) json.Append (",\"pinRotation\":true");
                    }
                }
                string residual = Number (point.residual, 3);
                if (residual != "0") json.Append (",\"residual\":").Append (residual);
                json.Append ('}');
            }
            json.Append (']');
        }

        static void AppendVector (StringBuilder json, Vector3 v, int digits)
        {
            json.Append ("{\"x\":").Append (Number (v.x, digits))
                .Append (",\"y\":").Append (Number (v.y, digits))
                .Append (",\"z\":").Append (Number (v.z, digits)).Append ('}');
        }

        /// <summary>小数 digits 桁に丸めた数（-0 は 0）</summary>
        internal static string Number (float value, int digits)
        {
            double rounded = System.Math.Round (value, digits, System.MidpointRounding.AwayFromZero);
            if (rounded == 0) return "0";
            return rounded.ToString ("0." + new string ('#', digits), CultureInfo.InvariantCulture);
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
            if (objects != null) {
                foreach (PoseObject item in objects) {
                    if (item != null) item.position *= ratio;
                }
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
            return ReadCurrent (session);
        }

        /// <summary>
        /// 今の姿勢を、クリップから作り直さずに読む（Solve で解いた姿勢を、キーを打つ前に読むため。S57）。読めなければ null
        /// </summary>
        public static PoseDocument ReadCurrent (PoseSession session)
        {
            if (GetProblem (session) != null) return null;
            EditingRig rig = session.stage.editingRig;
            FullBodyRig body = session.stage.fullBody;
            Quaternion toBody = Quaternion.Inverse (BodyBasis (rig));
            PoseDocument document = new PoseDocument { frame = session.frame, humanScale = rig.humanScale };
            foreach (BodyPointId id in body.pointIds) {
                // 動かせない点（S89）は値を持たず書けないので出さない（読んだ物をそのまま書き戻せるように）
                if (body.IsPassive (id)) continue;
                Vector3 local;
                Quaternion delta;
                if (!body.TryGetCapturedValues (body.GetControl (id), out local, out delta)) continue;
                BodyPointState state = StateOf (body, id);
                PosePoint point = new PosePoint {
                    name = id.name,
                    state = state.ToString (),
                    position = toBody * (local * rig.humanScale),
                    hasRotation = body.PointHasRotation (id),
                    // Free の点は目標を持たないので、離れていても届かなかったことにならない
                    residual = state == BodyPointState.Free ? 0 : body.GetResidual (id),
                };
                if (point.hasRotation) {
                    point.rotation = SignedEuler (toBody * delta * Quaternion.Inverse (toBody));
                    point.pinRotation = body.GetRotationWeight (id) > 0;
                }
                document.points.Add (point);
            }
            HeldObjectRig held = session.stage.heldObjects;
            if (held != null) {
                Transform root = session.stage.editingRoot;
                foreach (HeldObjectRig.Entry entry in held.entries) {
                    entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
                    Quaternion local = toBody * Quaternion.Inverse (root.rotation) * rotation;
                    document.objects.Add (new PoseObject {
                        name = entry.name,
                        position = toBody * root.InverseTransformPoint (position),
                        aim = new PoseAim { forward = local * Vector3.forward, up = local * Vector3.up },
                        attach = AttachNames (entry),
                        gap = entry.snapGap,
                        grip = ReadGrips (entry),
                    });
                }
            }
            return document;
        }

        /// <summary>どこにも付けていないことを表す名前（PoseObject.attach）。付ける先の名前には使えない</summary>
        public const string kAttachNone = "None";

        /// <summary>キーで付けた位置を使っている先の位置（どれも使っていなければ空。物の設定の位置は返さない）</summary>
        static List<PoseGrip> ReadGrips (HeldObjectRig.Entry entry)
        {
            List<PoseGrip> grips = new List<PoseGrip> ();
            foreach (HeldObjectRig.Attachment attachment in entry.attachments) {
                if (!attachment.gripKeyed) continue;
                grips.Add (new PoseGrip { point = attachment.name, position = attachment.heldGripPosition, rotation = attachment.heldGripRotation.eulerAngles });
            }
            return grips;
        }

        /// <summary>付けている先の名前をカンマでつないだもの（どこにも付けていなければ None）</summary>
        static string AttachNames (HeldObjectRig.Entry entry)
        {
            List<string> names = entry.attachments.FindAll (a => a.attached).ConvertAll (a => a.name);
            return names.Count > 0 ? string.Join (",", names) : kAttachNone;
        }

        /// <summary>
        /// 姿勢を書く（document.frame のキーにする）。書けなければ何も変えずに理由を返す。書けたら null
        /// </summary>
        public static string Write (PoseSession session, PoseDocument document)
        {
            string problem = Solve (session, document);
            if (problem != null) return problem;
            return session.CommitPose ("Write Pose");
        }

        /// <summary>読み書きできない理由（できるなら null）</summary>
        public static string GetProblem (PoseSession session)
        {
            string character = PoseSession.CharacterProblem (session);
            if (character != null) return character;
            if (session.stage.fullBody == null) return Tr ("POSE_DOCUMENT_NO_POINTS");
            return null;
        }

        /// <summary>体の向き基準（x 右・y 上・z 前）から、編集用ルートから見た向きへの回転</summary>
        internal static Quaternion BodyBasis (EditingRig rig)
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
        /// <summary>
        /// 届かなかった差（メートル。読んだときに入る。書くときは使わない）。Pin / Locked の点が、解いた後に目標からどれだけ離れているか。Free の点は 0（S57）。
        /// キーのフレームでは、点は打ったときに骨が届いた所でキーになっているので 0 に近い。書いた点が届いたかは、操作口の writePose の応答（打つ前に解いた差）で見る
        /// </summary>
        public float residual;

        // ---- 書くときだけ使う（S60）----

        /// <summary>向きをベクトルで書く（rotation より優先。向きを持つ点だけ）</summary>
        public PoseAim aim;
        /// <summary>position・aim・rotation を、この点の場所を原点に、この点の基準姿勢からの回転を軸にした値として読む（空なら体の向き基準）</summary>
        public string space;
        /// <summary>space の軸の回転を、この点の回転から space の点の回転へ spaceBlend だけ寄せたものにする（空なら混ぜない）</summary>
        public string spaceBlendFrom;
        /// <summary>spaceBlendFrom の点の回転（0）から space の点の回転（1）への割合</summary>
        public float spaceBlend;
        /// <summary>線でつながる親の点と子の点から、骨の長さで届く場所のうち、書いた場所にいちばん近い所へ寄せる（肘・膝）</summary>
        public bool snap;
    }

    /// <summary>
    /// 向きをベクトルで書く（PosePoint.aim。S60）。基準姿勢でこの点の restForward・restUp だった向きが、forward・up を向くように回す。
    /// forward が 0 なら target（場所）の方を向く。どちらも 0 なら使わない。ベクトル・場所は点の space の座標（空なら体の向き基準）
    /// </summary>
    [System.Serializable]
    public sealed class PoseAim
    {
        public Vector3 forward;
        /// <summary>省く（0）と restUp</summary>
        public Vector3 up;
        /// <summary>forward の代わりに向く場所</summary>
        public Vector3 target;
        /// <summary>基準姿勢でこの点の「前」としたい向き（体の向き基準。省くと体の前 +z）</summary>
        public Vector3 restForward;
        /// <summary>基準姿勢でこの点の「上」としたい向き（体の向き基準。省くと体の上 +y）</summary>
        public Vector3 restUp;
    }

    /// <summary>
    /// 接地をまとめて書く（PoseDocument.contacts。S60）。point の骨と子の骨に乗る接地の点（定義の contact）を、
    /// 基準姿勢での形のまま床に置き、point の向きも合わせる
    /// </summary>
    [System.Serializable]
    public sealed class PoseContact
    {
        /// <summary>足首などの点</summary>
        public string point;
        /// <summary>Flat（足裏を床に付ける）/ Ball（つま先の付け根で立つ）/ Heel（かかとで立つ）。省くと Flat</summary>
        public string mode;
        /// <summary>Flat のときの足首の床の上の場所（体の向き基準。x と z を使う）。Ball・Heel でも支点は Flat のときと同じ所に留まる</summary>
        public Vector3 at;
        /// <summary>足の向き（基準姿勢からの、上の軸まわりの回転。度）</summary>
        public float yaw;
        /// <summary>Ball ならかかとの上がり、Heel ならつま先の上がり（度）</summary>
        public float pitch;
        /// <summary>床からの高さ（メートル。足を真上に上げ下ろしするキー用）</summary>
        public float lift;
        /// <summary>点の状態（省くと Locked）</summary>
        public string state;
    }

    /// <summary>
    /// 持ち物 1 つ（PoseDocument.objects。S91・S104）。付けている骨は物に付いてくる。
    /// 枠が胸の物は、キーの間は胸に付いて動く
    /// </summary>
    [System.Serializable]
    public sealed class PoseObject
    {
        /// <summary>持ち物の名前（キャラの設定の持ち物の名前）</summary>
        public string name;
        /// <summary>物の原点の場所（体の向き基準・足元から。メートル）。0 なら今の場所のまま</summary>
        public Vector3 position;
        /// <summary>
        /// 物の向き（体の向き基準）。物のローカルで restForward（省くと +Z）だった向きが forward を、restUp（省くと +Y）だった向きが up を向く。
        /// forward の代わりに target（場所）でもよい。どちらも 0 なら今の向きのまま
        /// </summary>
        public PoseAim aim;
        /// <summary>
        /// 付けている先（リグ定義の付ける先の名前をカンマでつなぐ。例 "RightHand,LeftHand"。None でどこにも付けない）。空なら今のまま。
        /// 次にこの物のキーを打つまで段差で続く。新しく付ける先は、grip を書かなければ今の場所のまま付く（窓の付けるボタンと同じ）
        /// </summary>
        public string attach;
        /// <summary>
        /// 物の設定の位置で、この付ける先に付く所へ物を動かす（窓の「寄せる」。position・aim は使わない）。付けている骨は付いてくる。
        /// 付けるのは attach で書く（寄せてから付け替えると、前のキーの間で骨が物に付いたまま動く）
        /// </summary>
        public string snap;
        /// <summary>読むときだけ。寄せたときに、付けている先に届かなかった差（メートル）</summary>
        public float gap;
        /// <summary>
        /// 付ける先ごとの位置（S99。物から見た付ける先の場所と向き）。書かない先は今のまま。次に位置を書くまで続く。前後のキーで同じ先に付けていれば、キーの間は位置を補間する（手が柄の上を滑る）。
        /// 読むときは、キーで付けた位置（窓の付けるボタン・current）を使っている先だけ
        /// </summary>
        public List<PoseGrip> grip;
    }

    /// <summary>付ける先 1 つの位置（S99・S104）</summary>
    [System.Serializable]
    public sealed class PoseGrip
    {
        /// <summary>付ける先の名前（リグ定義の付ける先。RightHand など）</summary>
        public string point;
        /// <summary>この要求の点を置いて解いた後の、今の付ける先の場所と向きを位置にする（窓の付けるボタンと同じ。付けても骨は動かない）</summary>
        public bool current;
        /// <summary>物の設定の位置（リグエディタで決めた位置）へ戻す</summary>
        public bool useSettings;
        /// <summary>current・useSettings でないとき、位置の場所（物のローカル。メートル）</summary>
        public Vector3 position;
        /// <summary>current・useSettings でないとき、位置の向き（物のローカル。オイラー角）</summary>
        public Vector3 rotation;
    }

    /// <summary>snap で寄せた点（S60）</summary>
    [System.Serializable]
    public sealed class PoseSnap
    {
        public string name;
        /// <summary>書いた場所から寄せた距離（メートル）</summary>
        public float distance;
        /// <summary>寄せた場所（体の向き基準）</summary>
        public Vector3 position;
    }

}
