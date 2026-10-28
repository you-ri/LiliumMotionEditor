using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 書く姿勢の点を、解く前に体の向き基準の場所と向きへ決める（S60）。AI に幾何の計算をさせないため、
    /// 向きをベクトルで書く（aim）・点の座標で書く（space）・接地をまとめて書く（contacts）・骨の長さで届く所へ寄せる（snap）を受ける。
    /// 決める順: 接地 → space の無い点（aim も）→ space のある点（基準の点は決めた後の値を使う）→ snap
    /// </summary>
    public sealed partial class PoseDocument
    {
        const float kEpsilon = 1e-6f;

        /// <summary>置く点 1 つ（決めた場所と向き）</summary>
        sealed class Placement
        {
            public PosePoint point;
            public BodyPointId id;
            public BodyPointTarget target;
            public BodyPointState state;
            /// <summary>体の向き基準の場所（決めた後）</summary>
            public Vector3 position;
            /// <summary>向きも固定するか</summary>
            public bool rotation;
            /// <summary>体の向き基準の、基準姿勢からの向きの差（決めた後）</summary>
            public Quaternion delta = Quaternion.identity;
            /// <summary>接地（contacts）で決めた点か</summary>
            public bool contact;
        }

        /// <summary>
        /// 点を置いて全身 IK で解くところまで行い、キーは打たない（S57 の試しに解く口）。解けなければ何も変えずに理由を返す。解けたら null。
        /// 解いた姿勢は session に残る（ReadCurrent で読める）。捨てるときは session.Evaluate でクリップから作り直す。
        /// snapped を渡すと、snap で寄せた点を足す
        /// </summary>
        public static string Solve (PoseSession session, PoseDocument document, List<PoseSnap> snapped = null)
        {
            // 先に全部確かめる（途中で止めて、半分だけ置いた状態を残さない）
            List<Placement> placements;
            string problem = Collect (session, document, out placements);
            if (problem != null) return problem;

            session.Evaluate (document.frame);
            problem = session.GetWriteProblem ();
            if (problem != null) return problem;
            Resolve (session, placements, snapped);

            EditingRig rig = session.stage.editingRig;
            Quaternion toRoot = BodyBasis (rig);
            foreach (Placement placement in placements) {
                if (placement.state == BodyPointState.Free) {
                    placement.target.SetState (BodyPointState.Free);
                    continue;
                }
                TransformChannels channels = TransformChannels.Position | (placement.rotation ? TransformChannels.Rotation : 0);
                placement.target.SetValues (channels, toRoot * placement.position, toRoot * placement.delta * Quaternion.Inverse (toRoot));
                if (!placement.rotation) placement.target.ReleaseRotation ();
                placement.target.SetState (placement.state);
            }
            session.SolveAfterEdit ();
            return null;
        }

        /// <summary>
        /// 書く前に、点の名前・状態・aim・space・contacts・snap の指定だけを確かめる（姿勢もクリップも変えない。いくつもの姿勢をまとめて書く前に全部確かめるため。S59）。
        /// 書けるかどうか（フレームがサイクルキーより後ろか など）は、書くときのクリップで決まるので見ない
        /// </summary>
        public static string Check (PoseSession session, PoseDocument document)
        {
            List<Placement> placements;
            return Collect (session, document, out placements);
        }

        static string Collect (PoseSession session, PoseDocument document, out List<Placement> placements)
        {
            placements = new List<Placement> ();
            string problem = GetProblem (session);
            if (problem != null) return problem;
            if (document == null || document.points == null) return Tr ("POSE_DOCUMENT_EMPTY");
            FullBodyRig body = session.stage.fullBody;
            Dictionary<BodyPointId, BodyPointTarget> targets = session.targets.OfType<BodyPointTarget> ().ToDictionary (t => t.id);
            string names = string.Join (", ", body.pointIds.Select (p => p.name));

            foreach (PosePoint point in document.points) {
                if (point == null) continue;
                BodyPointTarget target;
                if (!targets.TryGetValue (new BodyPointId (point.name ?? ""), out target)) return Tr ("POSE_DOCUMENT_UNKNOWN_POINT", point.name, names);
                // 動かせない点（S89）は値を持たない（書いても何も変わらないので、黙って捨てずに断る）
                if (target.passive) return Tr ("POSE_DOCUMENT_PASSIVE_POINT", point.name);
                BodyPointState state;
                if (!TryParseState (point.state, out state)) return Tr ("POSE_DOCUMENT_UNKNOWN_STATE", point.name, point.state);
                bool aim = HasAim (point.aim);
                if (aim && !body.PointHasRotation (target.id)) return Tr ("POSE_DOCUMENT_AIM_NO_ROTATION", point.name);
                placements.Add (new Placement {
                    point = point,
                    id = target.id,
                    target = target,
                    state = state,
                    rotation = (aim || point.hasRotation && point.pinRotation) && body.PointHasRotation (target.id),
                });
            }

            // space の基準の点は、無い点・それ自身が space を持つ点（基準が連なる）を断る
            Dictionary<string, PosePoint> byName = new Dictionary<string, PosePoint> ();
            foreach (Placement placement in placements) byName[placement.id.name] = placement.point;
            foreach (Placement placement in placements) {
                foreach (string reference in new[] { placement.point.space, placement.point.spaceBlendFrom }) {
                    if (string.IsNullOrEmpty (reference)) continue;
                    if (!targets.ContainsKey (new BodyPointId (reference))) return Tr ("POSE_DOCUMENT_UNKNOWN_SPACE", placement.id.name, reference, names);
                    PosePoint other;
                    if (byName.TryGetValue (reference, out other) && !string.IsNullOrEmpty (other.space)) return Tr ("POSE_DOCUMENT_SPACE_CHAIN", placement.id.name, reference);
                }
                if (!string.IsNullOrEmpty (placement.point.spaceBlendFrom) && string.IsNullOrEmpty (placement.point.space)) {
                    return Tr ("POSE_DOCUMENT_UNKNOWN_SPACE", placement.id.name, "", names);
                }
                if (placement.point.snap) {
                    problem = CheckSnap (placement, targets.Values, placements);
                    if (problem != null) return problem;
                }
            }

            foreach (PoseContact contact in document.contacts ?? new List<PoseContact> ()) {
                if (contact == null) continue;
                problem = CollectContact (session, contact, targets, placements);
                if (problem != null) return problem;
            }
            return null;
        }

        static bool TryParseState (string text, out BodyPointState state)
        {
            return System.Enum.TryParse (text, true, out state) && System.Enum.IsDefined (typeof (BodyPointState), state);
        }

        static bool HasAim (PoseAim aim)
        {
            return aim != null && (aim.forward.sqrMagnitude > kEpsilon || aim.target.sqrMagnitude > kEpsilon);
        }

        /// <summary>
        /// 解く前の姿勢（そのフレームのクリップの姿勢）を参照しながら、置く点の場所と向きを決める。
        /// space の基準の点は、同じ要求で置いていればその値、置いていなければ今の姿勢
        /// </summary>
        static void Resolve (PoseSession session, List<Placement> placements, List<PoseSnap> snapped)
        {
            FullBodyRig body = session.stage.fullBody;
            EditingRig rig = session.stage.editingRig;
            Quaternion toBody = Quaternion.Inverse (BodyBasis (rig));
            Dictionary<BodyPointId, Placement> placed = new Dictionary<BodyPointId, Placement> ();
            foreach (Placement placement in placements) placed[placement.id] = placement;

            // 今の姿勢の点の場所と、乗る骨の向き（体の向き基準）
            System.Func<BodyPointId, Vector3> currentPosition = id => {
                Vector3 local;
                Quaternion delta;
                body.TryGetCapturedValues (body.GetControl (id), out local, out delta);
                return toBody * (local * rig.humanScale);
            };
            System.Func<BodyPointId, Quaternion> currentRotation = id => {
                Quaternion delta;
                body.TryGetBoneRotation (id, out delta);
                return toBody * delta * Quaternion.Inverse (toBody);
            };
            // 基準の点の場所と向き。置いた点なら決めた値（向きを固定しない点は今の骨の向き）
            System.Func<string, Pose> reference = name => {
                BodyPointId id = new BodyPointId (name);
                Placement other;
                if (placed.TryGetValue (id, out other) && other.state != BodyPointState.Free) {
                    return new Pose (other.position, other.rotation ? other.delta : currentRotation (id));
                }
                return new Pose (currentPosition (id), currentRotation (id));
            };

            foreach (Placement placement in placements.Where (p => !p.contact && string.IsNullOrEmpty (p.point.space))) ResolvePoint (placement, Vector3.zero, Quaternion.identity);
            foreach (Placement placement in placements.Where (p => !p.contact && !string.IsNullOrEmpty (p.point.space))) {
                Pose frame = reference (placement.point.space);
                Quaternion axes = frame.rotation;
                if (!string.IsNullOrEmpty (placement.point.spaceBlendFrom)) {
                    axes = Quaternion.Slerp (reference (placement.point.spaceBlendFrom).rotation, frame.rotation, placement.point.spaceBlend);
                }
                ResolvePoint (placement, frame.position, axes);
            }

            // snap は、決めた後の親と子の場所を使う（寄せる前の値。寄せた点どうしを連ねない）
            Dictionary<BodyPointId, Vector3> unsnapped = new Dictionary<BodyPointId, Vector3> ();
            foreach (Placement placement in placements) unsnapped[placement.id] = placement.position;
            foreach (Placement placement in placements.Where (p => p.point != null && p.point.snap && p.state != BodyPointState.Free)) {
                BodyPointTarget parent = placement.target.link;
                BodyPointTarget child = SnapChild (placement, session.targets.OfType<BodyPointTarget> (), placements);
                System.Func<BodyPointTarget, Vector3> position = t => {
                    Placement other;
                    return placed.TryGetValue (t.id, out other) && other.state != BodyPointState.Free ? unsnapped[t.id] : currentPosition (t.id);
                };
                Vector3 p = position (parent);
                Vector3 c = position (child);
                float upper = Vector3.Distance (RestPosition (session, parent.id), RestPosition (session, placement.id));
                float lower = Vector3.Distance (RestPosition (session, placement.id), RestPosition (session, child.id));
                Vector3 requested = placement.position;
                placement.position = Reach (p, c, upper, lower, requested);
                if (snapped != null) snapped.Add (new PoseSnap { name = placement.id.name, distance = Vector3.Distance (requested, placement.position), position = placement.position });
            }
        }

        /// <summary>点の場所と向きを、origin・axes の座標（体の向き基準なら 0 と単位）から体の向き基準へ直す</summary>
        static void ResolvePoint (Placement placement, Vector3 origin, Quaternion axes)
        {
            PosePoint point = placement.point;
            placement.position = origin + axes * point.position;
            if (HasAim (point.aim)) {
                PoseAim aim = point.aim;
                Vector3 restForward = aim.restForward.sqrMagnitude > kEpsilon ? aim.restForward : Vector3.forward;
                Vector3 restUp = aim.restUp.sqrMagnitude > kEpsilon ? aim.restUp : Vector3.up;
                Vector3 forward = aim.forward.sqrMagnitude > kEpsilon ? axes * aim.forward : origin + axes * aim.target - placement.position;
                Vector3 up = aim.up.sqrMagnitude > kEpsilon ? axes * aim.up : axes * restUp;
                placement.delta = LookRotation (forward, up) * Quaternion.Inverse (LookRotation (restForward, restUp));
            }
            else {
                placement.delta = axes * Quaternion.Euler (point.rotation);
            }
        }

        /// <summary>LookRotation（前と上が平行なときは、前に直交する上を選ぶ）</summary>
        static Quaternion LookRotation (Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude <= kEpsilon) return Quaternion.identity;
            if (Vector3.Cross (forward, up).sqrMagnitude <= kEpsilon * forward.sqrMagnitude) up = Mathf.Abs (forward.normalized.y) < 0.9f ? Vector3.up : Vector3.forward;
            return Quaternion.LookRotation (forward, up);
        }

        // ---- snap ----

        static string CheckSnap (Placement placement, IEnumerable<BodyPointTarget> targets, List<Placement> placements)
        {
            if (placement.target.link == null || !targets.Any (t => t.link == placement.target)) return Tr ("POSE_DOCUMENT_SNAP_NO_LINK", placement.id.name);
            List<string> children = placements.Where (p => p.target.link == placement.target && p.state != BodyPointState.Free).Select (p => p.id.name).ToList ();
            if (children.Count > 1) return Tr ("POSE_DOCUMENT_SNAP_CHILDREN", placement.id.name, string.Join (", ", children));
            return null;
        }

        /// <summary>snap の子の点。同じ要求で置いた子があればそれ、無ければ定義の順で最初の子（今の姿勢の場所を使う）</summary>
        static BodyPointTarget SnapChild (Placement placement, IEnumerable<BodyPointTarget> targets, List<Placement> placements)
        {
            Placement placed = placements.FirstOrDefault (p => p.target.link == placement.target && p.state != BodyPointState.Free);
            return placed != null ? placed.target : targets.First (t => t.link == placement.target);
        }

        /// <summary>
        /// 2 本の骨（parent → 点 → child）で届く場所のうち、requested にいちばん近い所。parent から upper、child から lower の距離の円の上の点。
        /// 届かない（伸びきる）ときはまっすぐ伸ばした所
        /// </summary>
        static Vector3 Reach (Vector3 parent, Vector3 child, float upper, float lower, Vector3 requested)
        {
            Vector3 line = child - parent;
            float d = line.magnitude;
            if (d <= kEpsilon) return parent + (requested - parent).normalized * upper;
            Vector3 n = line / d;
            if (d >= upper + lower) return parent + n * upper;
            float a = Mathf.Clamp ((upper * upper - lower * lower + d * d) / (2 * d), -upper, upper);
            float h = Mathf.Sqrt (Mathf.Max (0, upper * upper - a * a));
            Vector3 center = parent + n * a;
            Vector3 side = requested - center;
            side -= n * Vector3.Dot (side, n);
            if (side.sqrMagnitude <= kEpsilon) {
                side = Vector3.Cross (n, Vector3.up);
                if (side.sqrMagnitude <= kEpsilon) side = Vector3.Cross (n, Vector3.forward);
            }
            return center + side.normalized * h;
        }

        // ---- contacts ----

        /// <summary>
        /// 接地 1 つを、置く点にする。point の骨と子の骨に乗る接地の点（と point 自身）を、基準姿勢での形のまま、
        /// at・yaw に置いて床の高さ（lift）へ下ろす。Ball はつま先の付け根（子の骨の付け根の床の上）、Heel はいちばん後ろの床の点を支点に pitch だけ回し、
        /// 支点より後ろ（Ball）・前（Heel）の点だけを回す（床に付いている側は Flat のまま）
        /// </summary>
        static string CollectContact (PoseSession session, PoseContact contact, Dictionary<BodyPointId, BodyPointTarget> targets, List<Placement> placements)
        {
            FullBodyRig body = session.stage.fullBody;
            BodyPointTarget ankle;
            if (!targets.TryGetValue (new BodyPointId (contact.point ?? ""), out ankle)) return Tr ("POSE_DOCUMENT_UNKNOWN_POINT", contact.point, string.Join (", ", body.pointIds.Select (p => p.name)));
            string mode = string.IsNullOrEmpty (contact.mode) ? "Flat" : contact.mode.Trim ();
            int pivotSide;
            if (mode.Equals ("Flat", System.StringComparison.OrdinalIgnoreCase)) pivotSide = 0;
            else if (mode.Equals ("Ball", System.StringComparison.OrdinalIgnoreCase)) pivotSide = 1;
            else if (mode.Equals ("Heel", System.StringComparison.OrdinalIgnoreCase)) pivotSide = -1;
            else return Tr ("POSE_DOCUMENT_UNKNOWN_CONTACT_MODE", contact.point, contact.mode);
            BodyPointState state = BodyPointState.Locked;
            if (!string.IsNullOrEmpty (contact.state) && !TryParseState (contact.state, out state)) return Tr ("POSE_DOCUMENT_UNKNOWN_STATE", contact.point, contact.state);

            Transform foot = body.GetBone (ankle.id);
            List<BodyPointId> members = body.pointIds.Where (id => {
                Transform bone = body.GetBone (id);
                return id.Equals (ankle.id) || body.IsContact (id) && bone != null && (bone == foot || bone.IsChildOf (foot));
            }).ToList ();
            foreach (BodyPointId id in members) {
                if (placements.Any (p => p.id.Equals (id))) return Tr ("POSE_DOCUMENT_CONTACT_CONFLICT", id.name);
            }

            Vector3 ankleRest = RestPosition (session, ankle.id);
            Vector3 flatAnkle = new Vector3 (ankleRest.x, 0, ankleRest.z);
            List<BodyPointId> grounds = members.Where (id => IsGround (session, id)).ToList ();
            // 支点（基準姿勢）。Ball: 子の骨（つま先）の付け根の関節。無ければいちばん前の床の点。Heel: いちばん後ろの床の点。
            // 床の点の支点は、床との当たりの球の中心（点そのもの。10-22）。真下の床にすると、球の半径の分だけ支点の点が回って下がる
            // Ball の支点を関節の真下の床にすると、回したときに関節そのものが動いてつま先の骨の長さが合わず、つま先が浮く
            Vector3 pivot = flatAnkle;
            if (pivotSide > 0) {
                Transform toes = members.Select (id => body.GetBone (id)).FirstOrDefault (b => b != null && b.parent == foot);
                if (toes != null) pivot = Quaternion.Inverse (BodyBasis (session.stage.editingRig)) * session.stage.editingRig.GetRestRootPosition (toes);
                else if (grounds.Count > 0) pivot = grounds.Select (id => RestPosition (session, id)).OrderByDescending (p => p.z).First ();
            }
            else if (pivotSide < 0 && grounds.Count > 0) {
                pivot = grounds.Select (id => RestPosition (session, id)).OrderBy (p => p.z).First ();
            }

            Quaternion yaw = Quaternion.AngleAxis (contact.yaw, Vector3.up);
            // Ball はかかとを上げる（+x まわりで前が下がる向き）、Heel はつま先を上げる
            Quaternion pitch = Quaternion.AngleAxis (pivotSide * contact.pitch, Vector3.right);
            Vector3 lift = new Vector3 (0, contact.lift, 0);
            // 支点は Flat のときに来る所に留める（at は Flat のときの足首の床の上の場所）
            Vector3 placedPivot = new Vector3 (contact.at.x, 0, contact.at.z) + yaw * (pivot - flatAnkle);
            foreach (BodyPointId id in members) {
                Vector3 rest = RestPosition (session, id);
                // 支点より床に付いている側（Ball は前、Heel は後ろ）の点は回さない
                bool turns = pivotSide != 0 && (id.Equals (ankle.id) || pivotSide * (rest.z - pivot.z) < 0);
                Quaternion rotation = turns ? yaw * pitch : yaw;
                placements.Add (new Placement {
                    point = new PosePoint { name = id.name, state = state.ToString () },
                    id = id,
                    target = targets[id],
                    state = state,
                    position = placedPivot + rotation * (rest - pivot) + lift,
                    rotation = id.Equals (ankle.id) && body.PointHasRotation (id),
                    delta = rotation,
                    contact = true,
                });
            }
            return null;
        }

        /// <summary>基準の場所を地面に落とす点か（定義の ground。足裏の点）</summary>
        static bool IsGround (PoseSession session, BodyPointId id)
        {
            RigBinding.Point point = session.stage.editingRig.binding.points.FirstOrDefault (p => p.name == id.name);
            return point != null && point.definition != null && point.definition.ground;
        }

        /// <summary>点の基準姿勢（T ポーズ）での場所（体の向き基準・メートル。読む場所と同じ測り方）</summary>
        internal static Vector3 RestPosition (PoseSession session, BodyPointId id)
        {
            EditingRig rig = session.stage.editingRig;
            Vector3 local;
            Quaternion rotation;
            if (!session.stage.fullBody.TryGetPointRestValues (id, out local, out rotation)) return Vector3.zero;
            return Quaternion.Inverse (BodyBasis (rig)) * (local * rig.humanScale);
        }
    }

}
