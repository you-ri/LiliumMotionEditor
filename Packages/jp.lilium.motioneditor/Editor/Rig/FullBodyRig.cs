using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点の名前（リグ定義の点の一覧の名前。コントロールのパス Controls/Body/名前 の末尾）。
    /// 骨を書けば、その骨の付け根の点（既定の一覧での名前）。かかと・つま先の先・手の 2 点・向きの点は、既定の一覧での名前を作る
    /// </summary>
    public readonly struct BodyPointId : System.IEquatable<BodyPointId>
    {
        public readonly string name;

        public BodyPointId (string name)
        {
            this.name = name ?? "";
        }

        /// <summary>頭のてっぺんの点（既定の一覧での名前）</summary>
        internal static BodyPointId Top (HumanBodyBones bone)
        {
            return new BodyPointId (bone + "Top");
        }

        /// <summary>骨の前に出した点（既定の一覧での名前）</summary>
        internal static BodyPointId Direction (HumanBodyBones bone)
        {
            return new BodyPointId (bone + "Direction");
        }

        /// <summary>かかと（foot は足首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId Heel (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "Heel");
        }

        /// <summary>つま先の先（foot は足首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId ToeTip (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "ToeTip");
        }

        /// <summary>母趾球の列の足裏の中心（foot は足首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId SoleBall (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "SoleBall");
        }

        /// <summary>小指側の、足を回す点（foot は足首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId LittleToeSide (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "LittleToeSide");
        }

        /// <summary>人差し指の付け根の点（hand は手首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId PalmIndex (HumanBodyBones hand)
        {
            return new BodyPointId (RigPaths.SideOf (hand) + "PalmIndex");
        }

        /// <summary>小指の付け根の点（hand は手首の骨。既定の一覧での名前）</summary>
        internal static BodyPointId PalmLittle (HumanBodyBones hand)
        {
            return new BodyPointId (RigPaths.SideOf (hand) + "PalmLittle");
        }

        public static implicit operator BodyPointId (HumanBodyBones bone)
        {
            return new BodyPointId (bone.ToString ());
        }

        public bool Equals (BodyPointId other)
        {
            return string.Equals (name ?? "", other.name ?? "", System.StringComparison.Ordinal);
        }

        public override bool Equals (object other)
        {
            return other is BodyPointId && Equals ((BodyPointId)other);
        }

        public override int GetHashCode ()
        {
            return (name ?? "").GetHashCode ();
        }

        public override string ToString ()
        {
            return name ?? "";
        }
    }

    /// <summary>
    /// 全身 IK（S25）。編集用リグが解いた姿勢（FK。＝元の姿勢）を受け取り、固定の強さが入っている点（Controls/Body/...）へ
    /// 届くように編集用の骨を曲げる。点のキーが 1 本も効いていなければ何もしない（素通し）。
    ///
    /// 点の値（＝編集用クリップに保存する値）。コントロールの GameObject のローカル値がそのまま保存値:
    /// - localPosition = ルートから見た点の位置 / humanScale（体型の違うキャラで同じ意味になる）
    /// - localRotation = ルートから見た、基準姿勢（T ポーズ）からの向きの差（骨の軸の取り方に依らない。向きを持つ点だけ）
    /// - 固定の強さ = BodyPoint.positionWeight / rotationWeight
    ///
    /// 点はどれも「骨の上の点」（骨の付け根から決まった分だけ離れた所。関節の点は 0）。種類で解き方を変えない（S26）:
    /// その点を目標へ寄せるだけで、同じ骨の上の点どうしは剛体として拘束しあう（2 点で骨が置かれ、3 点で向きまで決まる）。
    /// 固定した点が 1 つだけの骨は、その点まわりに回れる（向きを留めたいときは、点の向きを固定するか、同じ骨にもう 1 点固定する）。
    /// 定義の「引く強さ」が 0 の点（S28。既定では向きの点）は、骨を回して点を目標の方へ向けるだけで、ほかの骨を引かない
    ///
    /// 毎回、受け取った元の姿勢から解く（前に解いた結果を引き継がない）。値を直に動かして解き直すときのために、
    /// 受け取った姿勢を控えておき（CaptureInput）、解き直す前に戻す（RestoreInput）。
    /// 編集用リグを解き直す前にも戻す（Unsolve）。編集用リグはコントロールの無い骨（腰の親など）を書かないので、
    /// 戻さないと、全身 IK が曲げた分がそこに残って、解き直すたびに積み重なる
    ///
    /// 解く計算は LiliumMotionEditor.FullBodyIk（Burst。Transform を見ない）。ここは、編集用の骨から剛体の並びを組み、
    /// 元の姿勢と点の値をルート空間の値にして渡し、結果を骨へ書き戻す。
    /// 計算用の配列（NativeArray）を持つので、使い終わったら Dispose する（持ち主は PreviewStage）
    /// </summary>
    public sealed class FullBodyRig : System.IDisposable
    {
        const float kEpsilon = 1e-6f;

        sealed class Point
        {
            public BodyPointId id;
            /// <summary>骨の上の点の、骨の付け根からの位置（基準姿勢の骨の枠・メートル）。関節の点は 0。向きを混ぜる点は PointOffset で今の値を出す</summary>
            public Vector3 offset;
            /// <summary>ずれの向きを混ぜる相手の骨（S34。混ぜない点は null）</summary>
            public Transform blendBone;
            /// <summary>混ぜる割合（0〜1）</summary>
            public float blend;
            /// <summary>offset のうち、混ぜた向きで回す分（基準姿勢の骨の枠・メートル）</summary>
            public Vector3 blendOffset;
            /// <summary>基準姿勢の、相手の骨から見た骨の向き</summary>
            public Quaternion blendRest = Quaternion.identity;
            /// <summary>相手の骨の、解く骨の並びでの番号（混ぜない・解く骨でなければ -1）</summary>
            public int blendBody = -1;
            public Transform control;
            public BodyPoint values;
            public Transform bone;
            public bool hasRotation;
            /// <summary>向けるだけの点か（定義の引く強さが 0）</summary>
            public bool aim;
            /// <summary>接地の点か（定義の contact）</summary>
            public bool contact;
            /// <summary>床との当たりの球の半径（メートル。定義の radius × humanScale）</summary>
            public float radius;
            public Quaternion restRootRotation;
            public Vector3 restRootPosition;
            /// <summary>骨の階層での深さ</summary>
            public int depth;
            /// <summary>解く骨の並びでの番号（解く骨でなければ -1）</summary>
            public int body = -1;
            /// <summary>接地の点のまとまり（足）の根の骨の番号。接地の点の乗る骨を、接地の点の乗る親の骨へたどった先（つま先の点は足首の骨）。接地の点でなければ -1</summary>
            public int floorGroup = -1;
        }

        readonly EditingRig rig_;
        readonly EditRigDefinition.FullBodySettings settings_;
        // 親が子より先に来る順
        readonly List<Point> points_ = new List<Point> ();
        readonly Transform[] bones_;
        readonly Vector3[] inputPositions_;
        readonly Quaternion[] inputRotations_;
        bool hasInput_;
        // 編集用の骨が、控えた元の姿勢から解いた後の姿勢になっているか
        bool solved_;
        // 解く骨（剛体）。親が子より先に来る順
        readonly List<Transform> bodies_ = new List<Transform> ();
        readonly List<int> bodyParents_ = new List<int> ();
        FullBodyIkData data_;
        Quaternion[] solveRotations_;
        Vector3[] solvePositions_;

        public FullBodyRig (EditingRig rig, EditRigDefinition definition)
        {
            rig_ = rig;
            settings_ = definition != null && definition.fullBody != null ? definition.fullBody : new EditRigDefinition.FullBodySettings ();
            List<Transform> bones = new List<Transform> ();
            if (rig != null && rig.root != null) {
                bones.AddRange (rig.editingBones);
                foreach (RigBinding.Point point in rig.binding.points) {
                    if (!point.enabled) continue;
                    Transform control = rig.FindControl (point.path);
                    Transform bone = rig.GetEditingBone (point.bone);
                    BodyPoint values = control != null ? control.GetComponent<BodyPoint> () : null;
                    if (control == null || bone == null || values == null) continue;
                    Quaternion restRotation = rig.GetRestRootRotation (bone);
                    Vector3 offset = rig.GetPointRestOffset (point);
                    Transform blendBone = rig.GetEditingBone (point.blendBone);
                    Vector3 blendOffset = blendBone != null ? rig.GetPointBlendRestOffset (point) : Vector3.zero;
                    if (blendOffset.sqrMagnitude <= kEpsilon * kEpsilon) blendBone = null;
                    points_.Add (new Point {
                        id = new BodyPointId (point.name),
                        offset = Quaternion.Inverse (restRotation) * offset,
                        blendBone = blendBone,
                        blend = blendBone != null ? Mathf.Clamp01 (point.definition.blend) : 0,
                        blendOffset = blendBone != null ? Quaternion.Inverse (restRotation) * blendOffset : Vector3.zero,
                        blendRest = blendBone != null ? Quaternion.Inverse (rig.GetRestRootRotation (blendBone)) * restRotation : Quaternion.identity,
                        control = control,
                        values = values,
                        bone = bone,
                        hasRotation = point.hasRotation,
                        aim = point.definition != null && point.definition.pullStrength <= 0,
                        contact = point.definition != null && point.definition.contact,
                        radius = point.definition != null ? Mathf.Max (0, point.definition.radius) * rig.humanScale : 0,
                        restRootRotation = restRotation,
                        restRootPosition = rig.GetRestRootPosition (bone) + offset,
                        depth = Depth (bone, rig.root.transform),
                    });
                }
                // 同じ深さは定義の順（List.Sort は安定でないので、順番も比べる）
                List<Point> order = new List<Point> (points_);
                points_.Sort ((a, b) => a.depth != b.depth ? a.depth.CompareTo (b.depth) : order.IndexOf (a).CompareTo (order.IndexOf (b)));
                CreateBodies ();
            }
            bones_ = bones.ToArray ();
            inputPositions_ = new Vector3[bones_.Length];
            inputRotations_ = new Quaternion[bones_.Length];
        }

        /// <summary>
        /// 解く骨（定義で「解く」にした FK の骨のうち、このキャラにあるもの）を剛体の並びにする。
        /// 親は、骨の階層を上へたどって最初に当たる解く骨（間の解かない骨は、親に付いて動く）
        /// </summary>
        void CreateBodies ()
        {
            Transform root = rig_.root.transform;
            List<RigBinding.Fk> solved = new List<RigBinding.Fk> ();
            List<Transform> solvedBones = new List<Transform> ();
            foreach (RigBinding.Fk fk in rig_.binding.fk) {
                if (!fk.enabled || !fk.control.fullBodySolve) continue;
                Transform bone = rig_.GetEditingBone (fk.bone);
                if (bone == null || solvedBones.Contains (bone)) continue;
                solved.Add (fk);
                solvedBones.Add (bone);
            }
            // 親が子より先に来る順（同じ深さは定義の順）
            List<int> order = new List<int> ();
            for (int i = 0; i < solved.Count; i++) order.Add (i);
            order.Sort ((a, b) => {
                int da = Depth (solvedBones[a], root), db = Depth (solvedBones[b], root);
                return da != db ? da.CompareTo (db) : a.CompareTo (b);
            });
            foreach (int index in order) bodies_.Add (solvedBones[index]);
            if (bodies_.Count == 0) return;

            // 床との当たり（Floor Clamp）は点の数まで
            data_ = FullBodyIkData.Create (bodies_.Count, points_.Count, points_.Count, Allocator.Persistent);
            solveRotations_ = new Quaternion[bodies_.Count];
            solvePositions_ = new Vector3[bodies_.Count];
            for (int i = 0; i < bodies_.Count; i++) {
                RigBinding.Fk fk = solved[order[i]];
                int parent = -1;
                for (Transform t = bodies_[i].parent; t != null && t != root && parent < 0; t = t.parent) parent = bodies_.IndexOf (t);
                bodyParents_.Add (parent);
                // 蝶番の軸は基準姿勢で決める: 親の骨の向きと、関節が出る向き（定義の bendDirection）の両方に直交する向き。親の骨の枠で持つ
                bool hinge = fk.control.hinge && parent >= 0;
                Vector3 axis = Vector3.zero;
                if (hinge) {
                    Vector3 upper = rig_.GetRestRootPosition (bodies_[i]) - rig_.GetRestRootPosition (bodies_[parent]);
                    Vector3 cross = Vector3.Cross (rig_.BodyToRoot (fk.control.bendDirection), upper);
                    if (cross.sqrMagnitude > kEpsilon) axis = Quaternion.Inverse (rig_.GetRestRootRotation (bodies_[parent])) * cross.normalized;
                }
                data_.bones[i] = new FullBodyIkBone {
                    parent = parent,
                    stiffness = Mathf.Clamp01 (fk.control.stiffness),
                    hinge = hinge ? 1 : 0,
                    hingeAxis = axis,
                };
            }
            foreach (Point point in points_) {
                point.body = bodies_.IndexOf (point.bone);
                point.blendBody = point.blendBone != null ? bodies_.IndexOf (point.blendBone) : -1;
            }
            HashSet<int> contactBodies = new HashSet<int> ();
            foreach (Point point in points_) {
                if (point.contact && point.body >= 0) contactBodies.Add (point.body);
            }
            foreach (Point point in points_) {
                if (!point.contact || point.body < 0) continue;
                int group = point.body;
                while (bodyParents_[group] >= 0 && contactBodies.Contains (bodyParents_[group])) group = bodyParents_[group];
                point.floorGroup = group;
            }
        }

        public void Dispose ()
        {
            if (data_.isCreated) data_.Dispose ();
            data_ = default (FullBodyIkData);
        }

        /// <summary>計算を Burst のジョブで走らせるか。false なら同じ関数をマネージドで直に呼ぶ（テスト用。結果の差を比べる）</summary>
        internal bool useBurst = true;

        /// <summary>解く骨（剛体）の数</summary>
        internal int bodyCount
        {
            get { return bodies_.Count; }
        }

        /// <summary>直前に解いたとき、計算が Burst で動いたか（切れているとマネージドで動く）</summary>
        public bool lastSolveUsedBurst { get; private set; }

        /// <summary>直前に解いたとき、曲がる向きを既定から決めた蝶番（まっすぐな肘・膝）の数</summary>
        internal int lastDefaultBendCount { get; private set; }

        static int Depth (Transform bone, Transform root)
        {
            int depth = 0;
            for (Transform t = bone; t != null && t != root; t = t.parent) depth++;
            return depth;
        }

        public int pointCount
        {
            get { return points_.Count; }
        }

        /// <summary>点の名前（親が子より先に来る順）</summary>
        public IEnumerable<BodyPointId> pointIds
        {
            get {
                foreach (Point point in points_) yield return point.id;
            }
        }

        /// <summary>
        /// 固定の強さが入っている点があるか。無ければ解かない（素通し）
        /// </summary>
        public bool hasActivePoint
        {
            get {
                foreach (Point point in points_) {
                    if (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0)) return true;
                }
                return false;
            }
        }

        Transform rootTransform
        {
            get { return rig_.root.transform; }
        }

        float humanScale
        {
            get { return rig_.humanScale; }
        }

        Point Find (BodyPointId id)
        {
            foreach (Point point in points_) {
                if (point.id.Equals (id)) return point;
            }
            return null;
        }

        Point Find (Transform control)
        {
            foreach (Point point in points_) {
                if (point.control == control) return point;
            }
            return null;
        }

        public bool HasPoint (BodyPointId bone)
        {
            return Find (bone) != null;
        }

        /// <summary>点のコントロール（無ければ null）</summary>
        public Transform GetControl (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? point.control : null;
        }

        /// <summary>
        /// 点が付いている「骨の上の点」の今のワールドの位置。付け根の点は骨の位置、離れた点は定義の場所
        /// </summary>
        public bool TryGetBonePointWorld (BodyPointId bone, out Vector3 position)
        {
            Point point = Find (bone);
            position = point != null ? BonePointWorld (point) : Vector3.zero;
            return point != null;
        }

        static Vector3 BonePointWorld (Point point)
        {
            return point.bone.position + point.bone.rotation * PointOffset (point, point.bone.rotation, point.blendBone != null ? point.blendBone.rotation : point.bone.rotation);
        }

        /// <summary>
        /// 骨の上の点の、骨の付け根からの位置（今の骨の枠・メートル）。向きを混ぜる点（S34）は、ずれ（blendOffset）を
        /// 骨から見た相手の骨の、基準姿勢からの回りを blend だけ掛けて回す（解く側の FullBodyIk.PointOffset と同じ計算）。
        /// boneRotation / blendRotation は骨と相手の骨の向き（同じ空間なら、ワールドでもルート空間でもよい）
        /// </summary>
        static Vector3 PointOffset (Point point, Quaternion boneRotation, Quaternion blendRotation)
        {
            if (point.blendBone == null || point.blend <= 0) return point.offset;
            Quaternion relative = Quaternion.Inverse (boneRotation) * blendRotation * point.blendRest;
            return point.offset - point.blendOffset + Quaternion.Slerp (Quaternion.identity, EditingRigSolver.Normalize (relative), point.blend) * point.blendOffset;
        }

        /// <summary>点が動かす編集用の骨（無ければ null）</summary>
        /// <summary>接地の点か（定義の contact。床に留める・床と滑りを見る点）</summary>
        public bool IsContact (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.contact;
        }

        /// <summary>床との当たりの球の半径（メートル。定義の radius。無ければ 0）</summary>
        public float GetRadius (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? point.radius : 0;
        }

        /// <summary>
        /// 点を動かすとき、床（ワールドの Y = 0）より下へ行かない点の高さの下限（Floor Clamp）。
        /// 接地の点（足首・足裏の点）は、今の足の形のまま前後に傾けて、足裏の球がどれも床の上に乗るいちばん低い高さ（踵とつま先が両方着く所。
        /// まっすぐなつま先なら足が平らに着く高さ）。そこまでは下ろせ、下ろす間に潜る足裏は、
        /// 解くときに床との当たりで足が転がって床に沿う（FullBodyIk.SolveContact。つま先が着いたまま踵が下りて、足が平らになっていく）。
        /// 足首だけを床まで下げられると、足裏を床の上に置くには足をつま先が上を向くまで回すしかなく、足が下がらずに起きた（10-22 ユーザー指示）。
        /// 今の足の形のまま止めると、つま先の下がった足は、つま先が床に着いた所からまったく下ろせなかった。
        /// 足の向きを固定している足も、床との当たりが向きの固定に勝って回る。
        /// ほかの点は自分の球の半径
        /// </summary>
        public float GetFloorLimit (BodyPointId bone)
        {
            Point point = Find (bone);
            if (point == null) return 0;
            if (point.floorGroup < 0) return point.radius;
            // 足の点の、今の場所（つま先の骨の今の曲がりも入る。向きを固定していれば、その向きの値の向き）を、今の向きから前後に傾けて、
            // 足裏の球がどれも床の上に乗るいちばん低い高さを探す。基準姿勢の形で決めると、つま先の骨が曲がっていたり足が横に傾いていたりする足は、
            // その高さまで下ろしたときに前後に回すだけでは床の上に乗らず、足が踵側へ倒れてつま先が 8〜12cm 浮いた（10-22）
            Vector3 self = BonePointWorld (point);
            Quaternion toTarget = point.hasRotation && point.values.rotationWeight >= 1 ? PointWorldRotation (point) * Quaternion.Inverse (point.bone.rotation) : Quaternion.identity;
            List<KeyValuePair<Vector3, float>> arms = new List<KeyValuePair<Vector3, float>> ();
            Vector3 forward = Vector3.zero;
            foreach (Point other in points_) {
                if (other == point || other.floorGroup != point.floorGroup) continue;
                Vector3 arm = toTarget * (BonePointWorld (other) - self);
                arms.Add (new KeyValuePair<Vector3, float> (arm, other.radius));
                forward += arm;
            }
            forward = Vector3.ProjectOnPlane (forward, Vector3.up);
            Vector3 axis = forward.sqrMagnitude > 1e-8f ? Vector3.Cross (forward.normalized, Vector3.up).normalized : Vector3.right;
            float slope = FloorSlope (arms, forward, Quaternion.identity);
            float best = float.MaxValue;
            for (int step = -kFloorRollSteps / 2; step <= kFloorRollSteps / 2; step++) {
                Quaternion tilt = Quaternion.AngleAxis (step * kFloorRollStep, axis);
                // 足が平ら（踵とつま先が同じ高さ）になる所を越えては傾けない（越えると、踵やつま先だけで立って足首がいくらでも下がる）
                if (!IsTiltAllowed (slope, FloorSlope (arms, forward, tilt))) continue;
                float limit = point.radius;
                foreach (KeyValuePair<Vector3, float> arm in arms) limit = Mathf.Max (limit, arm.Value - (tilt * arm.Key).y);
                best = Mathf.Min (best, limit);
            }
            return best;
        }

        /// <summary>
        /// 足の前後の傾き: tilt だけ傾けたときの、いちばん前の点といちばん後ろの点（forward の向きで）の球の下の端の高さの差（前が高ければ正）。
        /// 足が平らなら 0
        /// </summary>
        /// <summary>
        /// 今の前後の傾き now から、傾き tilted へ傾けてよいか。平らに近づく向きだけで、平らになる所（傾き 0）を越えない。今が平らなら、平らなままの向きだけ
        /// </summary>
        static bool IsTiltAllowed (float now, float tilted)
        {
            if (Mathf.Abs (tilted) <= kFlatSlope) return true;
            // 平らに近づく向きだけ（踵だけで立った足をさらに反らせれば足首はいくらでも下がるが、下げても足は平らにならない。10-22）
            return now * tilted > 0 && Mathf.Abs (tilted) <= Mathf.Abs (now) + kFlatSlope;
        }

        /// <summary>足が平らとみなす、踵とつま先の高さの差（メートル）。傾きを調べる刻み（0.25°）で足の長さ 25cm なら 1.1mm ずつ変わる</summary>
        const float kFlatSlope = 2e-3f;

        static float FloorSlope (List<KeyValuePair<Vector3, float>> arms, Vector3 forward, Quaternion tilt)
        {
            if (arms.Count == 0) return 0;
            int front = 0, back = 0;
            for (int i = 1; i < arms.Count; i++) {
                if (Vector3.Dot (arms[i].Key, forward) > Vector3.Dot (arms[front].Key, forward)) front = i;
                if (Vector3.Dot (arms[i].Key, forward) < Vector3.Dot (arms[back].Key, forward)) back = i;
            }
            return ((tilt * arms[front].Key).y - arms[front].Value) - ((tilt * arms[back].Key).y - arms[back].Value);
        }

        /// <summary>足の止める高さを探すとき、傾けて調べる刻み（度）と回数（左右合わせて。±90° まで）</summary>
        const float kFloorRollStep = 0.25f;
        const int kFloorRollSteps = 720;

        public Transform GetBone (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? point.bone : null;
        }

        public bool PointHasRotation (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.hasRotation;
        }

        // ---- 入力（元の姿勢）の控え ----

        /// <summary>
        /// 受け取った元の姿勢（編集用の骨の今の値）を控える。全身 IK の段が、解く前に呼ぶ
        /// </summary>
        public void CaptureInput ()
        {
            for (int i = 0; i < bones_.Length; i++) {
                if (bones_[i] == null) continue;
                bones_[i].GetLocalPositionAndRotation (out inputPositions_[i], out inputRotations_[i]);
            }
            hasInput_ = true;
            solved_ = false;
        }

        /// <summary>
        /// 控えた元の姿勢へ戻す（点の値を直に動かした後、前に解いた結果からでなく元の姿勢から解き直すため）。控えが無ければ false
        /// </summary>
        public bool RestoreInput ()
        {
            if (!hasInput_) return false;
            for (int i = 0; i < bones_.Length; i++) {
                if (bones_[i] == null) continue;
                bones_[i].SetLocalPositionAndRotation (inputPositions_[i], inputRotations_[i]);
            }
            solved_ = false;
            return true;
        }

        /// <summary>控えを捨てる（姿勢を一から作り始めるとき）</summary>
        public void ClearInput ()
        {
            hasInput_ = false;
            solved_ = false;
        }

        /// <summary>
        /// 解いた後なら、控えた元の姿勢へ戻す。編集用リグを解き直す前に呼ぶ（全身 IK が曲げた分を、コントロールの無い骨に残さない）
        /// </summary>
        public void Unsolve ()
        {
            if (solved_) RestoreInput ();
        }

        // ---- 点の値 ----

        /// <summary>点のワールドの位置と向き（コントロールは正規化した値を持つので、GameObject の位置そのものではない）</summary>
        public bool TryGetPointWorld (BodyPointId bone, out Vector3 position, out Quaternion rotation)
        {
            Point point = Find (bone);
            if (point == null) {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = PointWorldPosition (point);
            rotation = PointWorldRotation (point);
            return true;
        }

        Vector3 PointWorldPosition (Point point)
        {
            return rootTransform.TransformPoint (point.control.localPosition * humanScale);
        }

        Quaternion PointWorldRotation (Point point)
        {
            return rootTransform.rotation * point.control.localRotation * point.restRootRotation;
        }

        public bool SetPointPositionWorld (BodyPointId bone, Vector3 position)
        {
            Point point = Find (bone);
            if (point == null) return false;
            point.control.localPosition = rootTransform.InverseTransformPoint (position) / humanScale;
            return true;
        }

        public bool SetPointRotationWorld (BodyPointId bone, Quaternion rotation)
        {
            Point point = Find (bone);
            if (point == null || !point.hasRotation) return false;
            point.control.localRotation = EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * rotation * Quaternion.Inverse (point.restRootRotation));
            return true;
        }

        /// <summary>
        /// 点の位置と向きを、今の骨の見た目に合わせる（固定の強さは変えない）。固定していない点をつかむ前に呼ぶと、今の場所から動かせる
        /// </summary>
        public bool CapturePoint (BodyPointId bone)
        {
            Point point = Find (bone);
            if (point == null) return false;
            CapturePoint (point);
            return true;
        }

        /// <summary>controls にあるコントロールの点を、今の骨の位置と向きに置く</summary>
        public void CapturePoints (ICollection<Transform> controls)
        {
            foreach (Point point in points_) {
                if (controls.Contains (point.control)) CapturePoint (point);
            }
        }

        /// <summary>
        /// control の点を今の骨の位置と向きに置いたときの、コントロールのローカルの値（コントロールは動かさない）。
        /// 向きを持たない点の向きは今のコントロールの値。点のコントロールでなければ false
        /// </summary>
        public bool TryGetCapturedValues (Transform control, out Vector3 localPosition, out Quaternion localRotation)
        {
            Point point = Find (control);
            if (point == null) {
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                return false;
            }
            localPosition = BoneLocalPosition (point);
            localRotation = point.hasRotation
                ? BoneLocalRotation (point)
                : point.control.localRotation;
            return true;
        }

        /// <summary>点を今の骨の上の点に置いたときの、コントロールのローカルの位置</summary>
        Vector3 BoneLocalPosition (Point point)
        {
            return rootTransform.InverseTransformPoint (BonePointWorld (point)) / humanScale;
        }

        /// <summary>点を今の骨の向きにしたときの、コントロールのローカルの向き（基準姿勢からの差）</summary>
        Quaternion BoneLocalRotation (Point point)
        {
            return EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * point.bone.rotation * Quaternion.Inverse (point.restRootRotation));
        }

        void CapturePoint (Point point)
        {
            point.control.localPosition = BoneLocalPosition (point);
            if (point.hasRotation) {
                point.control.localRotation = BoneLocalRotation (point);
            }
        }

        public float GetPositionWeight (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? point.values.positionWeight : 0;
        }

        public float GetRotationWeight (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.hasRotation ? point.values.rotationWeight : 0;
        }

        public void SetPositionWeight (BodyPointId bone, float weight)
        {
            Point point = Find (bone);
            if (point != null) point.values.positionWeight = Mathf.Clamp01 (weight);
        }

        public void SetRotationWeight (BodyPointId bone, float weight)
        {
            Point point = Find (bone);
            if (point != null && point.hasRotation) point.values.rotationWeight = Mathf.Clamp01 (weight);
        }

        /// <summary>完全固定か（置いた場所から動かさない点。そうでない点は、編集のたびに骨の位置へ合わせ直す）</summary>
        public bool GetLocked (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.values.locked > 0.5f;
        }

        public void SetLocked (BodyPointId bone, bool locked)
        {
            Point point = Find (bone);
            if (point != null) point.values.locked = locked ? 1 : 0;
        }

        /// <summary>
        /// 強さ 1 で固定していて完全固定でない点を、実際の骨の位置（向きも固定していれば向きも）へ合わせ直す。
        /// 体が届かない所へ置いた点・ほかの点を動かして届かなくなった点が、体の形から離れたまま残らないようにする。
        /// 位置を直したら position、向きを直したら rotation が true
        /// </summary>
        public void SyncPointToBone (BodyPointId bone, out bool position, out bool rotation)
        {
            position = false;
            rotation = false;
            Point point = Find (bone);
            if (point == null || point.values.locked > 0.5f) return;
            if (point.values.positionWeight >= 1 && Vector3.Distance (BonePointWorld (point), PointWorldPosition (point)) > kSyncDistance) {
                point.control.localPosition = BoneLocalPosition (point);
                position = true;
            }
            if (point.hasRotation && point.values.rotationWeight >= 1 && Quaternion.Angle (point.bone.rotation, PointWorldRotation (point)) > kSyncAngle) {
                point.control.localRotation = BoneLocalRotation (point);
                rotation = true;
            }
        }

        /// <summary>点を骨へ合わせ直す差の下限（これ以下は届いているとみなす）</summary>
        const float kSyncDistance = 1e-4f;
        const float kSyncAngle = 0.01f;

        /// <summary>
        /// 点の基準の値（基準姿勢の骨の位置・向きの差なし）。ルートから見た値（＝保存する値）
        /// </summary>
        public bool TryGetPointRestValues (BodyPointId bone, out Vector3 position, out Quaternion rotation)
        {
            Point point = Find (bone);
            position = point != null ? point.restRootPosition / humanScale : Vector3.zero;
            rotation = Quaternion.identity;
            return point != null;
        }

        /// <summary>
        /// 点の骨を、親の骨から見た基準姿勢（ローカル）に戻したときの点のワールドの位置と向き（骨は動かさない）。
        /// 点の値はルートから見た値なので、基準の値（TryGetPointRestValues）へ戻すと、今の姿勢の体から離れた所へ飛ぶ。
        /// こちらは親の骨の今の姿勢に乗せるので、点は体に付いたまま、その骨の曲げだけが無くなる（FK の Reset と同じ）。
        /// 骨の付け根の点は位置が変わらず（腰は親から見た基準の位置へ戻る）、骨から離れた点は骨の向きと一緒に回る。
        /// resetBones は一緒に戻す骨（いくつかの点をまとめて戻すとき、その点の骨すべて）。祖先に戻す骨があれば、
        /// FK で根から順に戻したのと同じく、戻した祖先に付いて動いた所で求める（無ければ点の骨だけ戻す）。
        /// 祖先を戻さずに点ごとに求めると、脚の点をまとめて戻したとき、足首は曲がったままの脛に、膝の Pole はまっすぐにした太ももに乗り、食い違う
        /// </summary>
        public bool TryGetPointLocalRestWorld (BodyPointId bone, ICollection<Transform> resetBones, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            Point point = Find (bone);
            if (point == null || point.bone.parent == null) return false;
            if (resetBones == null) resetBones = new[] { point.bone };
            else if (!resetBones.Contains (point.bone)) resetBones = new List<Transform> (resetBones) { point.bone };
            Pose pose = LocalRestPose (point.bone, resetBones);
            // 点の向きは骨の向きそのもの（CapturePoint と同じ）
            rotation = pose.rotation;
            // 向きを混ぜる点は、相手の骨も戻した（戻した祖先に付いて動いた）向きで混ぜる
            Quaternion blendRotation = point.blendBone != null ? LocalRestPose (point.blendBone, resetBones).rotation : pose.rotation;
            position = pose.position + pose.rotation * PointOffset (point, pose.rotation, blendRotation);
            return true;
        }

        /// <summary>
        /// resetBones の骨を親から見た基準姿勢に戻したときの、bone のワールドの位置と向き（骨は動かさない）。
        /// 戻さない骨は親から見た今の値のまま、戻した祖先に付いて動く
        /// </summary>
        Pose LocalRestPose (Transform bone, ICollection<Transform> resetBones)
        {
            Transform parent = bone.parent;
            bool moved = false;
            foreach (Transform reset in resetBones) {
                // IsChildOf は自分自身でも true
                if (reset != null && bone.IsChildOf (reset)) {
                    moved = true;
                    break;
                }
            }
            if (!moved || parent == null) return new Pose (bone.position, bone.rotation);

            Pose parentPose = LocalRestPose (parent, resetBones);
            Vector3 localPosition;
            Quaternion localRotation;
            if (!resetBones.Contains (bone) || !rig_.TryGetRestPosition (bone, out localPosition) || !rig_.TryGetRestRotation (bone, out localRotation)) {
                bone.GetLocalPositionAndRotation (out localPosition, out localRotation);
            }
            return new Pose (parentPose.position + parentPose.rotation * Vector3.Scale (parent.lossyScale, localPosition), parentPose.rotation * localRotation);
        }

        /// <summary>
        /// 点が乗る骨の、基準姿勢からの今の向きの差（ルートから見た値）。向きを持たない点（胸など）でも出す（S60。点を座標の基準にするとき）
        /// </summary>
        internal bool TryGetBoneRotation (BodyPointId bone, out Quaternion rotation)
        {
            Point point = Find (bone);
            rotation = point != null ? BoneLocalRotation (point) : Quaternion.identity;
            return point != null;
        }

        /// <summary>届かなかった差（メートル）。固定の強さが 1 の点が、解いた後にどれだけ離れているか</summary>
        internal float GetResidual (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? Vector3.Distance (BonePointWorld (point), PointWorldPosition (point)) : 0;
        }

        // ---- 解く ----

        /// <summary>
        /// controls にあるコントロールの点だけを効かせて解く（ほかの点は、固定の強さが入っていても使わない）。
        /// キーの間で、前後のキーで Locked の点だけを留めるときに使う（S25d）。
        /// limbsOnly なら、点のある手足（首から先）だけを動かし、腰・背骨などそのほかの骨は元の姿勢（FK）のままにする:
        /// 点のある骨から根の方へ、枝分かれする骨（腰・上胸など）の手前までを動かす。点が腰そのものにあれば腰も動かす
        /// </summary>
        public void SolveOnly (ICollection<Transform> controls, float weight = 1, bool limbsOnly = false)
        {
            if (controls == null || controls.Count == 0) return;
            solveOnly_ = controls;
            limbsOnly_ = limbsOnly;
            try {
                Solve (weight);
            }
            finally {
                solveOnly_ = null;
                limbsOnly_ = false;
            }
        }

        ICollection<Transform> solveOnly_;
        bool limbsOnly_;

        /// <summary>足と一緒に動かしている（固定に使わない）Pin の点。CarryFootPins</summary>
        readonly HashSet<Point> floorCarried_ = new HashSet<Point> ();

        /// <summary>
        /// Floor Clamp で足の点（grabbed）をつかんで動かしている間、同じ足のほかの Pin の点（Locked でないもの）を固定に使わず、足と一緒に動かす（10-22 ユーザー指示）。
        /// 足裏の点が Pin のままだと、足首をつかんで下げても足裏の点がその場に留まり、足がひとかたまりで下がらずに、つま先の骨だけが曲がった。
        /// 一緒に動かした点は、離したときに骨の今の場所で Pin のまま付け直す（SyncToBone。Pin のいつもの決まり）。Locked の点は留めたまま。
        /// キーを打ったとき・クリップから姿勢を当て直したときに止める（ClearFootCarry）
        /// </summary>
        public void CarryFootPins (BodyPointId grabbed)
        {
            Point point = Find (grabbed);
            if (point == null || point.floorGroup < 0) return;
            foreach (Point other in points_) {
                if (other == point || other.floorGroup != point.floorGroup || other.values.locked > 0.5f) continue;
                if (other.values.positionWeight > 0 || (other.hasRotation && other.values.rotationWeight > 0)) floorCarried_.Add (other);
            }
        }

        /// <summary>足と一緒に動かすのをやめる（キーを打ったとき・クリップから姿勢を当て直したとき）。足の箱（TryMoveFoot）も捨てる</summary>
        public void ClearFootCarry ()
        {
            floorCarried_.Clear ();
            footBoxes_.Clear ();
            footBoxPoints_.Clear ();
            footBoxToes_.Clear ();
        }

        /// <summary>ドラッグの間に持ち越す足の箱（足の根の骨の番号ごと）と、箱の頂点にした点</summary>
        readonly Dictionary<int, FootBox> footBoxes_ = new Dictionary<int, FootBox> ();
        readonly Dictionary<int, List<Point>> footBoxPoints_ = new Dictionary<int, List<Point>> ();
        /// <summary>足の箱のつま先の骨の番号（無ければ -1）と、始めの足から見たつま先の向き</summary>
        readonly Dictionary<int, KeyValuePair<int, Quaternion>> footBoxToes_ = new Dictionary<int, KeyValuePair<int, Quaternion>> ();

        /// <summary>
        /// Floor Clamp で足の点（grabbed）をつかんで hand（ワールド）へ動かす: 足を、接地の点を頂点にした硬い箱として床に押し当てたときのように動かし（FootBox）、
        /// 点の置き場所（届かなければ手前）を placed に返す（10-22 ユーザー指示）。箱はドラッグの間持ち越し、キーを打つとき・クリップから当て直すときに捨てる（ClearFootCarry）。
        /// 解くときは、足の骨を箱の位置と向きに強さ 1 で留め、脚は全身 IK で届かせる（足のほかの点は使わない）。
        /// 向きを持つ点（足首の点）の向きの値は、向きを固定していれば箱の向きに合わせる（キーと姿勢が食い違わない）。足の点でなければ false
        /// </summary>
        public bool TryMoveFoot (BodyPointId grabbed, Vector3 hand, out Vector3 placed)
        {
            placed = hand;
            Point point = Find (grabbed);
            if (point == null || point.floorGroup < 0 || point.floorGroup >= bodies_.Count) return false;
            int group = point.floorGroup;
            FootBox box;
            List<Point> members;
            if (!footBoxes_.TryGetValue (group, out box)) {
                members = new List<Point> ();
                List<Vector3> vertices = new List<Vector3> ();
                List<float> radius = new List<float> ();
                List<bool> fixedVertices = new List<bool> ();
                List<bool> toeVertices = new List<bool> ();
                Transform root = bodies_[group];
                int toes = -1;
                Vector3 tip = Vector3.zero;
                foreach (Point other in points_) {
                    if (other.floorGroup != group) continue;
                    members.Add (other);
                    vertices.Add (BonePointWorld (other));
                    radius.Add (other.radius);
                    // 動かさないと決めた点（Locked）は、置いた場所から動かない支点。つかんだ点は手で動かすので除く
                    fixedVertices.Add (other != point && other.values.locked > 0.5f && other.values.positionWeight >= 1);
                    // つま先の骨（足の骨の子）の上の点は、つま先の付け根で曲がる
                    bool onToes = other.body != group && other.body >= 0;
                    toeVertices.Add (onToes);
                    if (onToes && toes < 0) {
                        toes = other.body;
                        tip = BonePointWorld (other);
                    }
                }
                Vector3 joint = toes >= 0 ? bodies_[toes].position : Vector3.zero;
                // つま先が曲がる軸は、足の横向き（上向きとつま先の向きに直交する）
                Vector3 axis = toes >= 0 ? Vector3.Cross (Vector3.up, tip - joint) : Vector3.zero;
                if (toes >= 0 && axis.sqrMagnitude < 1e-10f) axis = root.rotation * Vector3.right;
                box = new FootBox (root.position, root.rotation, vertices, radius, fixedVertices, toes >= 0 ? toeVertices : null, joint, axis.normalized);
                footBoxes_[group] = box;
                footBoxPoints_[group] = members;
                footBoxToes_[group] = new KeyValuePair<int, Quaternion> (toes, toes >= 0 ? Quaternion.Inverse (root.rotation) * bodies_[toes].rotation : Quaternion.identity);
            }
            members = footBoxPoints_[group];
            int index = members.IndexOf (point);
            if (index < 0) return false;
            placed = box.Move (index, hand);
            foreach (Point other in members) {
                if (other.bone == bodies_[group] && other.hasRotation && other.values.rotationWeight > 0) {
                    other.control.localRotation = EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * box.rotation * Quaternion.Inverse (other.restRootRotation));
                }
            }
            return true;
        }

        /// <summary>点が、ドラッグの間に持ち越している足の箱の頂点か（Floor Clamp の止める高さは箱が受け持つ）</summary>
        public bool IsFootBoxed (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup);
        }

        /// <summary>足と一緒に動かしている Pin の点か（固定に使わない。点は骨の今の場所に出す）</summary>
        public bool IsFootCarried (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && floorCarried_.Contains (point);
        }

        /// <summary>
        /// 動かさない骨の印を入れる（limbsOnly のとき、点のある手足以外は 1）
        /// </summary>
        void MarkFixedBones ()
        {
            int n = bodies_.Count;
            for (int i = 0; i < n; i++) data_.fixedBones[i] = limbsOnly_ ? 1 : 0;
            if (!limbsOnly_) return;
            int[] children = new int[n];
            for (int i = 0; i < n; i++) {
                if (bodyParents_[i] >= 0) children[bodyParents_[i]]++;
            }
            foreach (Point point in points_) {
                if (point.body < 0 || !IsActive (point)) continue;
                int body = point.body;
                data_.fixedBones[body] = 0;
                for (int p = bodyParents_[body]; p >= 0 && children[p] <= 1; p = bodyParents_[p]) data_.fixedBones[p] = 0;
            }
        }

        /// <summary>
        /// 今の編集用の骨の姿勢（元の姿勢）から、固定の強さが入っている点へ寄せる。
        /// weight は段の重み（元の姿勢と解いた姿勢を、骨のローカルの値で混ぜる）。0 なら何もしない
        /// </summary>
        public void Solve (float weight = 1)
        {
            if (rig_ == null || rig_.root == null || weight <= 0 || !hasActivePoint) return;

            Vector3[] beforePositions = null;
            Quaternion[] beforeRotations = null;
            if (weight < 1) {
                beforePositions = new Vector3[bones_.Length];
                beforeRotations = new Quaternion[bones_.Length];
                for (int i = 0; i < bones_.Length; i++) {
                    if (bones_[i] != null) bones_[i].GetLocalPositionAndRotation (out beforePositions[i], out beforeRotations[i]);
                }
            }

            if (!SolveBodies ()) return;
            solved_ = true;

            if (weight >= 1) return;
            for (int i = 0; i < bones_.Length; i++) {
                if (bones_[i] == null) continue;
                bones_[i].GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                bones_[i].SetLocalPositionAndRotation (Vector3.Lerp (beforePositions[i], position, weight), Quaternion.Slerp (beforeRotations[i], rotation, weight));
            }
        }

        bool IsActive (Point point)
        {
            return IsSolved (point) && (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0));
        }

        /// <summary>今解く点か（SolveOnly で絞っているときは、その点だけ）</summary>
        bool IsSolved (Point point)
        {
            return solveOnly_ == null || solveOnly_.Contains (point.control);
        }

        /// <summary>
        /// 今の編集用の骨（元の姿勢）と点の値をルート空間の値にして解き、結果を骨へ書き戻す。
        /// 書き戻すのは骨の向きと、根（腰）の位置だけ（骨の長さは変わらない）。何も変わらなければ骨に触らず false
        /// </summary>
        bool SolveBodies ()
        {
            if (!data_.isCreated) return false;
            Transform root = rootTransform;
            Quaternion rootRotation = root.rotation;
            Quaternion rootInverse = Quaternion.Inverse (rootRotation);
            Vector3 rootPosition = root.position;
            float scale = humanScale;
            float pull = Mathf.Clamp01 (settings_.pull);

            for (int i = 0; i < bodies_.Count; i++) {
                solvePositions_[i] = rootInverse * (bodies_[i].position - rootPosition);
                solveRotations_[i] = rootInverse * bodies_[i].rotation;
                data_.positions[i] = solvePositions_[i];
                data_.rotations[i] = solveRotations_[i];
            }
            int count = 0;
            foreach (Point point in points_) {
                // 足の箱で動かしている足の点は使わない（足の骨を箱の位置と向きに留める。下で足す）
                if (point.body < 0 || !IsSolved (point) || floorCarried_.Contains (point) || (point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup))) continue;
                float positionWeight = Mathf.Clamp01 (point.values.positionWeight) * pull;
                float rotationWeight = point.hasRotation ? Mathf.Clamp01 (point.values.rotationWeight) * pull : 0;
                // Floor Clamp の間は、同じ骨に位置を固定した点がほかにもあれば、向きは点の位置で決める（向きの固定を使わない）
                if (floorClamp && solveOnly_ == null && point.contact && rotationWeight > 0 && HasOtherPositionPin (point, pull)) rotationWeight = 0;
                if (positionWeight <= 0 && rotationWeight <= 0) continue;
                // どの点も「骨の上の点を目標へ寄せる」だけ（S26。種類で解き方を変えない。同じ骨の点どうしは剛体として拘束しあう）
                data_.effectors[count++] = new FullBodyIkEffector {
                    bone = point.body,
                    offset = point.offset,
                    priority = point.values.locked > 0.5f ? 1 : 0,
                    position = point.control.localPosition * scale,
                    rotation = point.control.localRotation * point.restRootRotation,
                    positionWeight = positionWeight,
                    rotationWeight = rotationWeight,
                    aim = point.aim ? 1 : 0,
                    blendBone = point.blendBody,
                    blend = point.blendBody >= 0 ? point.blend : 0,
                    blendOffset = point.blendOffset,
                    blendRest = point.blendRest,
                };
            }
            if (solveOnly_ == null) {
                foreach (KeyValuePair<int, FootBox> pair in footBoxes_) {
                    if (count >= data_.effectors.Length) break;
                    FootBox box = pair.Value;
                    data_.effectors[count++] = new FullBodyIkEffector {
                        bone = pair.Key,
                        priority = 1,
                        position = rootInverse * (box.position - rootPosition),
                        rotation = rootInverse * box.rotation,
                        positionWeight = 1,
                        rotationWeight = 1,
                        blendBone = -1,
                    };
                    // つま先の骨も、箱で曲げた向きに留める
                    KeyValuePair<int, Quaternion> toes = footBoxToes_[pair.Key];
                    if (toes.Key < 0 || count >= data_.effectors.Length) continue;
                    data_.effectors[count++] = new FullBodyIkEffector {
                        bone = toes.Key,
                        priority = 1,
                        position = rootInverse * (box.toeJoint - rootPosition),
                        rotation = rootInverse * (box.rotation * box.toeTurn * toes.Value),
                        positionWeight = 1,
                        rotationWeight = 1,
                        blendBone = -1,
                    };
                }
            }
            if (count == 0) return false;
            MarkFixedBones ();
            data_.iterations = Mathf.Max (1, settings_.iterations);
            data_.rootPin = Mathf.Clamp01 (settings_.hipsPin);
            SetContacts (rootPosition.y, rootInverse * Vector3.up);
            RunSolver (count);

            // 変わらなかった骨は元の値がそのまま返る。全部そうなら触らない（固定した点が今の姿勢の上にあるとき、姿勢をまったく変えない）
            bool changed = false;
            for (int i = 0; i < bodies_.Count && !changed; i++) {
                quaternion rotation = data_.rotations[i];
                changed = !rotation.Equals ((quaternion)solveRotations_[i])
                    || (bodyParents_[i] < 0 && !data_.positions[i].Equals ((float3)solvePositions_[i]));
            }
            if (!changed) return false;

            // 親から順に書く（向きはワールドの値で入れるので、親を先に決める）
            for (int i = 0; i < bodies_.Count; i++) {
                if (bodyParents_[i] < 0) bodies_[i].position = rootPosition + rootRotation * (Vector3)data_.positions[i];
                bodies_[i].rotation = rootRotation * Quaternion.Normalize ((Quaternion)data_.rotations[i]);
            }
            return true;
        }

        /// <summary>先頭から count 個の目標で、元の姿勢（solvePositions_ / solveRotations_）から解く</summary>
        void RunSolver (int count)
        {
            for (int i = 0; i < bodies_.Count; i++) {
                data_.positions[i] = solvePositions_[i];
                data_.rotations[i] = solveRotations_[i];
            }
            data_.effectorCount = count;
            if (useBurst) {
                new FullBodyIkJob { data = data_ }.Run ();
            }
            else {
                data_.status[FullBodyIkData.kStatusBurst] = 0;
                FullBodyIk.Solve (ref data_);
            }
            lastSolveUsedBurst = data_.status[FullBodyIkData.kStatusBurst] != 0;
            lastDefaultBendCount = data_.status[FullBodyIkData.kStatusDefaultBend];
        }

        /// <summary>
        /// 解いた姿勢で、接地の点（定義の contact）の球を床（ワールドの Y = 0）より下へ置かない（Controller の Floor Clamp）。
        /// 全身 IK の中で床との当たりとして解く（FullBodyIkContact。足は床に当たった所を支点に転がる。点の目標には触らない）。
        /// 操作のときだけのフラグで、窓が配る（クリップには保存しない）
        /// </summary>
        public bool floorClamp { get; set; }

        /// <summary>
        /// 床との当たりをソルバーへ渡す（Floor Clamp。姿勢を作るときだけ。前後のキーで Locked の点だけを留めるとき（SolveOnly）は渡さない）。
        /// 10-22 まではエディタ側で、解いた後に足を持ち上げる・回す・点の向きの値を回すなどを重ねていて、床に当たると足の転がり方が不自然だった（ユーザー報告）
        /// </summary>
        void SetContacts (float floorHeight, Vector3 up)
        {
            int count = 0;
            if (floorClamp && solveOnly_ == null) {
                foreach (Point point in points_) {
                    if (!point.contact || point.body < 0 || count >= data_.contacts.Length) continue;
                    // 足の箱で動かしている足は、箱が床との当たりを受け持つ（全身 IK の当たりを重ねると、箱がちょうど床に置いた足裏の点を潜ったとみなして足を回し直した）
                    if (point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup)) continue;
                    // つま先の点などは、足（接地の点のまとまりの根の骨）の上の点として渡す（解く前の姿勢での、足から見た場所）。
                    // つま先の骨に当てると、足が転がる前につま先の骨だけが反った。足の形（つま先の曲がり）は保つ（10-22 ユーザー指示）
                    int body = point.floorGroup >= 0 ? point.floorGroup : point.body;
                    Vector3 offset = point.offset;
                    if (body != point.body) {
                        Vector3 position = solvePositions_[point.body] + solveRotations_[point.body] * point.offset;
                        offset = Quaternion.Inverse (solveRotations_[body]) * (position - solvePositions_[body]);
                    }
                    data_.contacts[count++] = new FullBodyIkContact { bone = body, offset = offset, radius = point.radius };
                }
            }
            data_.contactCount = count;
            data_.floorNormal = up;
            data_.floorHeight = floorHeight;
        }

        /// <summary>
        /// 向きを固定した点の骨に、ほかにも位置を強さ 1 で固定した点があるか（Floor Clamp の間は、向きを点の位置で決める）。
        /// 足首の向きを固定し、足裏の中心を Locked にして足首を持ち上げると、足が回れずに踵が付いてこなかった（10-22 ユーザー指示）
        /// </summary>
        bool HasOtherPositionPin (Point point, float pull)
        {
            foreach (Point other in points_) {
                if (other != point && other.body == point.body && !floorCarried_.Contains (other) && Mathf.Clamp01 (other.values.positionWeight) * pull >= 1) return true;
            }
            return false;
        }
    }

}
