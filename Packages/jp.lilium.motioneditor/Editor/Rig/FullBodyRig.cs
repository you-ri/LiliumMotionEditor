using UnityEngine;
using System.Collections.Generic;
using System.Linq;
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
    /// 姿勢を解く基盤（S26。ほかの決まりはこの上に載る）: 点はどれも「骨の上の点」で、骨に対して好きな場所に、1 本の骨にいくつでも置ける（関節の点は付け根で、ずれが 0 なだけ）。
    /// 種類で解き方を変えず、その点を目標へ寄せるだけ。骨は留めた点を中心に回る（回る中心は骨の付け根である必要はない。付け根と親は付いて動く）。
    /// 同じ骨の上の点どうしは剛体として拘束しあう: 留めた点が 1 つならその点まわりに自由に回り、2 つならその 2 点を結ぶ軸まわりだけ、3 つで向きまで決まる
    /// （付け根が動かない親に付いていれば、付け根も留めた所に数える。AxisTurns）。向きを留めたいときは、点の向きを固定するか、同じ骨にもう 1 点固定する。
    /// 定義の「重さ」が 0 の点（軽い点。S88）をつかんで動かすと、つかんだ点が引き、引かれる点は状態と重さで決まる: Pin の点は重さに関係なく留まり、
    /// Free の点は軽ければ引かれ、重ければ（1 の点）今の場所に留まる。重い点を動かすとき・キーから解き直すときは、今までどおり Free の点は重さに関係なく引かれる。
    /// 膝の Pole のように向きを混ぜる点（S34）と、重い点のある骨の上の 0 の点（既定の向きの点）は、骨を回して点を目標の方へ向けるだけ（S28）。
    /// 動かせない点（定義の passive。S89）は値を持たず、乗る骨の上でも親から見ても動かない点: 付け根から離れていれば、骨は付け根と点を結ぶ軸まわりにだけ回る
    /// （FullBodyIkBone.axisLock。キーから解くときも同じ）。軽い点を引くときは、そこから先へ力を伝えない壁にもなる（AddHeldEffectors）
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
        /// <summary>軽い点をつかんでいる間の、つかんだ点の位置の強さの上限。重い点・Pin の点（強さ 1）より少しだけ弱くし、ぶつかったらつかんでいる点が譲る</summary>
        const float kLightPull = 0.999f;
        /// <summary>重い点をつかんで動かしているとき、つかんだ点が届いたとみなす残り（m）。これより離れていれば、Lock でない Pin の点を外して解き直す（PullPins）</summary>
        const float kReachTolerance = 1e-3f;
        /// <summary>
        /// 軽い点をつかんでいる間の反復の回数（定義の iterations の何倍か）。重い点を留めたまま軽い点を引くと、関節のずれが詰まりにくい（10-27 実測で、1 倍では首が 1.45mm、4 倍で 0.5mm）。
        /// 残りは SnapHeld で合わせる
        /// </summary>
        const int kLightIterations = 4;
        /// <summary>SnapHeld で、留めた骨を戻すために手前の骨を回す回数</summary>
        const int kSnapHeldIterations = 32;

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
            /// <summary>重さ 0 の点か（定義の重さが 0）。軽い点（light）でなければ、骨を回して点を目標の方へ向けるだけの点（S28）</summary>
            public bool aim;
            /// <summary>
            /// 軽い点か（S88）。重さ 0 で、向きを混ぜず、乗る骨に重さ 1 の点が無い点。ふつうの点として解き、つかんで動かすと、Free の重い点を引かない。
            /// 乗る骨に重い点があれば、その骨は引けないので、今までどおり骨を付け根まわりに回して向けるだけ（既定の向きの点）
            /// </summary>
            public bool light;
            /// <summary>接地の点か（定義の contact）</summary>
            public bool contact;
            /// <summary>動かせない点か（定義の passive。S89）。乗る骨を付け根と点を結ぶ軸まわりにだけ回す（axisLock）。軽い点をつかんでいる間は今の場所に留める壁にする（AddHeldEffectors）。自分の目標は使わない</summary>
            public bool passive;
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
        // 軽い点をつかんでいる間に、動かない点（Free の重い点・Pin の点）とその向こうを留める姿勢（1 回目に、つかんだ点を除いて解いた姿勢）
        Quaternion[] heavyRotations_;
        Vector3[] heavyPositions_;
        // 軽い点をつかんでいる間に留めた骨: 付け根の場所を留めたか・向きを留めたか（SnapHeld で合わせ直す）。
        // frozen_ は、動かない点の向こう側の骨（つかんだ点から、動かない点を通らずに届かない骨。動かない骨にする）
        bool[] heldOrigin_;
        bool[] heldRotation_;
        bool[] frozen_;
        // 軽い点をつかんでいる間に、動かない点として留めた点（自分の目標の代わりに、1 回目の場所へ留める）
        readonly HashSet<Point> held_ = new HashSet<Point> ();
        // 重い点をつかんでいる間の、1 回目に解いたつかんだ点の目標の並び（届いたかを見る）
        readonly List<int> grabbedEffectors_ = new List<int> ();

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
                        aim = point.definition != null && point.definition.weight <= 0,
                        contact = point.definition != null && point.definition.contact,
                        passive = point.definition != null && point.definition.passive,
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
            heavyRotations_ = new Quaternion[bodies_.Count];
            heavyPositions_ = new Vector3[bodies_.Count];
            heldOrigin_ = new bool[bodies_.Count];
            heldRotation_ = new bool[bodies_.Count];
            frozen_ = new bool[bodies_.Count];
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
            // 動かせない点（S89）は、乗る骨の上でも親から見ても動かない。付け根から離れていれば、骨は付け根と点を結ぶ軸まわりにだけ回る（基盤どおり、
            // 付け根と点の 2 点で骨が留まる）。1 本の骨に動かせない点がいくつあっても、軸は最初の点で決める。肘・膝の蝶番の骨には付けない
            foreach (Point point in points_) {
                if (!point.passive || point.body < 0 || bodyParents_[point.body] < 0 || point.offset.sqrMagnitude <= kEpsilon * kEpsilon) continue;
                FullBodyIkBone bone = data_.bones[point.body];
                if (bone.axisLock != 0 || bone.hinge != 0) continue;
                bone.axisLock = 1;
                bone.lockAxis = point.offset;
                data_.bones[point.body] = bone;
            }
            foreach (Point point in points_) {
                // 動かせない点は重さを使わないので、重い点に数えない
                point.light = point.aim && point.blendBone == null && point.body >= 0 && !points_.Any (p => !p.aim && !p.passive && p.body == point.body);
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
                    if (!point.passive && (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0))) return true;
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

        /// <summary>動かせない点か（定義の passive。S89。選べるが動かせず、キーを持たない）</summary>
        public bool IsPassive (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && point.passive;
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
            return point != null && !point.passive ? point.values.positionWeight : 0;
        }

        public float GetRotationWeight (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && !point.passive && point.hasRotation ? point.values.rotationWeight : 0;
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
            return point != null && !point.passive && point.values.locked > 0.5f;
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
            if (point == null || point.passive || point.values.locked > 0.5f) return;
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

        /// <summary>つかんだ点と一緒に動かしている（固定に使わない）Pin の点。CarryFootPins</summary>
        readonly HashSet<Point> carried_ = new HashSet<Point> ();
        /// <summary>今つかんで動かしている点（MarkGrabbed。軽い点をつかんでいる間は、ほかの点とぶつかったら譲る）</summary>
        readonly HashSet<Point> grabbed_ = new HashSet<Point> ();

        /// <summary>
        /// Floor Clamp で足の点（grabbed）をつかんで動かしている間、同じ足のほかの Pin の点（Locked でないもの）を固定に使わず、足と一緒に動かす（10-22 ユーザー指示）。
        /// 足裏の点が Pin のままだと、足首をつかんで下げても足裏の点がその場に留まり、足がひとかたまりで下がらずに、つま先の骨だけが曲がった。
        /// 一緒に動かした点は、離したときに骨の今の場所で Pin のまま付け直す（SyncToBone。Pin のいつもの決まり）。Locked の点は留めたまま。
        /// キーを打ったとき・クリップから姿勢を当て直したときに止める（ClearCarry）
        /// </summary>
        public void CarryFootPins (BodyPointId grabbed)
        {
            Point point = Find (grabbed);
            if (point == null || point.floorGroup < 0) return;
            foreach (Point other in points_) {
                if (other == point || other.passive || other.floorGroup != point.floorGroup || other.values.locked > 0.5f) continue;
                if (other.values.positionWeight > 0 || (other.hasRotation && other.values.rotationWeight > 0)) carried_.Add (other);
            }
        }

        /// <summary>
        /// 点をつかんで動かした（値を直に置いた）。つかんでいる点は、軽い点をつかんでいる間、Pin の点・重い点とぶつかったら譲る（kLightPull）。
        /// 引く点と、引かれる点の決まり（10-27 ユーザー指示「肘をうごかしているなら、手首、肩両方をひっぱる挙動になる。
        /// 自身の weight が 0 で手首は 1 で pin になっているから手首は動かない。肩は 0 のため、pin の場合は動かない。free の場合は動く」）:
        /// Pin の点は重さに関係なく置いた場所に留まり、Free の点は軽ければ引かれ、重ければ留まる。
        /// 重い点をつかんだときは、Free の点と重さ 0 の Pin の点は引かれ、重さ 1 の Pin の点は届く間はその場に留まり、届かなければ外れて付いて動く（PullsPin）。ClearCarry で消える
        /// </summary>
        public void MarkGrabbed (BodyPointId bone)
        {
            Point point = Find (bone);
            if (point != null) MarkGrabbed (point);
        }

        void MarkGrabbed (Point point)
        {
            grabbed_.Add (point);
            carried_.Remove (point);
        }

        /// <summary>つかんだ点と一緒に動かすのをやめる（キーを打ったとき・クリップから姿勢を当て直したとき）。足の箱（TryMoveFoot）も捨てる</summary>
        public void ClearCarry ()
        {
            carried_.Clear ();
            grabbed_.Clear ();
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
        /// 点の置き場所（届かなければ手前）を placed に返す（10-22 ユーザー指示）。箱はドラッグの間持ち越し、キーを打つとき・クリップから当て直すときに捨てる（ClearCarry）。
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
            return point != null && IsFootBoxed (point);
        }

        /// <summary>足と一緒に動かしている Pin の点か（固定に使わない。点は骨の今の場所に出す）</summary>
        public bool IsCarried (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null && carried_.Contains (point);
        }

        /// <summary>
        /// 点を、置いた場所でなく骨の今の場所に出すか（位置・向きそれぞれ）。足と一緒に動かしている Pin の点（CarryFootPins）と、
        /// ほかの点をつかんで動かしている間の、離したときに骨へ付け直す点（SyncPointToBone と同じ条件。強さ 1 で Locked でない）。
        /// 置いた場所に出すと、動かしている間は動かす前の場所に残り、離したときに骨の場所へ飛んで見えた（10-27 ユーザー指示）
        /// </summary>
        public void GetShownOnBone (BodyPointId bone, out bool position, out bool rotation)
        {
            position = rotation = false;
            Point point = Find (bone);
            if (point == null || point.passive) return;
            if (carried_.Contains (point)) {
                position = rotation = true;
                return;
            }
            if (grabbed_.Count == 0 || grabbed_.Contains (point) || point.values.locked > 0.5f) return;
            position = point.values.positionWeight >= 1;
            rotation = point.hasRotation && point.values.rotationWeight >= 1;
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
            return !point.passive && IsSolved (point) && (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0));
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
            // 軽い点（重さ 0。S88）をつかんで動かしているか。つかんでいれば、つかんだ点が引き、引かれる点は状態と重さで決まる:
            // Pin の点は重さに関係なく置いた場所に留まり、Free の点は軽ければ引かれ、重ければ今の場所に留まる（10-27 ユーザー指示）。
            // まとめてつかんだ点に重い点が混ざれば、一番重い点で決める（重い点をつかんだときと同じ。10-27 ユーザー決定）。
            // 重い点をつかんでいれば、重さ 0 の Pin の点は Free の点と同じに引かれ、重さ 1 の Pin の点は、届く間はその場に留まり、つかんだ点が届かなければ外れて付いて動く
            // （PullsPin。10-27 ユーザー指示「ウェイト１がウェイト１をひっぱる場合、pin でも動くようにする」「pinの場合はその場を維持するように。ただし引っ張ることはできる」）。
            // つかんでいなければ（キーから解き直す）、今までどおり Pin の点を全部効かせ、Free の点は重さに関係なく引かれる
            bool lightGrab = false, heavyGrab = false;
            foreach (Point point in points_) {
                if (!grabbed_.Contains (point)) continue;
                // Floor Clamp の足の箱で動かしている足の点は、点としては解かない（箱の目標で動かす）が、つかんだ点として数える
                // （数えないと、重い足を引いて脚が伸びきっても、Pin の腰を外して解き直さず、体が付いてこなかった。10-28 ユーザー報告）
                float positionWeight, rotationWeight;
                if (!GetWeights (point, pull, out positionWeight, out rotationWeight) && !IsFootBoxed (point)) continue;
                if (IsLight (point)) lightGrab = true;
                else if (!point.aim) heavyGrab = true;
            }
            if (heavyGrab) lightGrab = false;

            // 1 回目: つかんでいる軽い点を除いて、ほかの点を全部いつもどおり解く（軽い点をつかんでいなければ、これだけ）。
            // 解き直すたびに元の姿勢（キーの姿勢）から解くので、動かない点の「今の場所」は、つかんだ点が無いときに点が居る場所
            int count = 0;
            bool pins = false;
            grabbedEffectors_.Clear ();
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (point, pull, out positionWeight, out rotationWeight)) continue;
                if (lightGrab && grabbed_.Contains (point)) continue;
                if (heavyGrab && grabbed_.Contains (point)) grabbedEffectors_.Add (count);
                // 重さ 0 の Pin の点は、Free の点と同じに引かれる（目標に使わない。10-28 ユーザー指示「自分（つかんだ点）が重さ 1 のとき
                // その先の点の重さが0ならpinでも freeと同じように引かれるようにして」）。重さ 1 の Pin の点は強さ 1 のまま効かせる
                // （重さ 0 の Pin の点（腹・胸など）とぶつかって答えが跳ねていたのは、これで起きない。10-28 実測で、強さを 0.99 に抑えて
                // 仕上げでも効かせる形は、腕のように鎖が何本もつながる所で詰め切れず、腰を 20cm 動かすと Pin の手が 9cm 流れた）
                if (heavyGrab && point.aim && PullsPin (point)) continue;
                pins |= heavyGrab && PullsPin (point);
                data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight, scale);
            }
            int boxes = count;
            count = AddFootBoxes (count, rootInverse, rootPosition);
            // 足の箱はつかんで動かしている足なので、届いたかを見る目標に入れる
            if (heavyGrab) {
                for (int e = boxes; e < count; e++) grabbedEffectors_.Add (e);
            }
            if (count == 0 && !lightGrab) return false;
            MarkFixedBones ();
            // 重い点をつかんで Pin の点を効かせている間は、反復を増やす（kLightIterations 倍）。Pin の点に挟まれた背骨が詰め切れず、
            // 届く所でも届かないとみなして腰の Pin を外していた（10-28 実測で、腹・胸を硬さ 0.6〜0.8 にした定義で首を前へ 5mm 動かすと、1 倍では 1.9mm 残った）
            data_.iterations = Mathf.Max (1, settings_.iterations) * (pins ? kLightIterations : 1);
            data_.rootPin = Mathf.Clamp01 (settings_.hipsPin);
            SetContacts (rootPosition.y, rootInverse * Vector3.up);
            if (count > 0) RunSolver (count, solvePositions_, solveRotations_);
            // 届かなければ、まず根への道の上の Pin の点だけを外し、それでも届かなければ残りも外す
            if (pins && !Reached (grabbedEffectors_) && (!PullPins (false, pull, scale, rootInverse, rootPosition) || !Reached (grabbedEffectors_))) {
                PullPins (true, pull, scale, rootInverse, rootPosition);
            }

            // 2 回目: 1 回目の姿勢から、動かない点（Free の重い点・Pin の点）とその向こうを留めて、つかんだ点で引く
            if (lightGrab) {
                for (int i = 0; i < bodies_.Count; i++) {
                    heavyPositions_[i] = count > 0 ? (Vector3)data_.positions[i] : solvePositions_[i];
                    heavyRotations_[i] = count > 0 ? (Quaternion)data_.rotations[i] : solveRotations_[i];
                }
                // 留める動かない点を先に並べる（同じ骨にほかの点があっても、動かない点を先に満たす）
                count = AddHeldEffectors (pull);
                foreach (Point point in points_) {
                    float positionWeight, rotationWeight;
                    if (!GetWeights (point, pull, out positionWeight, out rotationWeight)) continue;
                    // 留めた点・動かない骨の上の点は、自分の目標を使わない
                    if (held_.Contains (point) || frozen_[point.body]) continue;
                    // つかんでいる点は、Pin の点・留めた重い点とぶつかったら譲る（kLightPull）。強さ 1 のままだと、両方を満たそうとして関節が離れたまま反復が終わり、
                    // 仕上げで骨をつなぎ直したときに、留めた重い点の骨が親に付いて動いた（10-27 実測で、左肩を動かすと首と頭が 2.3mm 動いた）
                    if (grabbed_.Contains (point)) positionWeight = Mathf.Min (positionWeight, kLightPull);
                    data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight, scale);
                }
                count = AddFootBoxes (count, rootInverse, rootPosition);
                MarkFixedBones ();
                LockHeld ();
                data_.iterations = Mathf.Max (1, settings_.iterations) * kLightIterations;
                RunSolver (count, heavyPositions_, heavyRotations_);
                SnapHeld ();
            }

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

        /// <summary>
        /// 軽い点か（Point.light。S88）。つかんで動かすと、Free の重い点を引かない。
        /// 向けるだけの点（向きを混ぜる膝の Pole・重い点のある骨の向きの点）は、今までどおり骨を回して向けるだけ
        /// </summary>
        static bool IsLight (Point point)
        {
            return point.light;
        }

        /// <summary>1 回目に解いた目標（data_.effectors の並び）が、どれも届いたか（kReachTolerance）</summary>
        bool Reached (List<int> effectors)
        {
            foreach (int e in effectors) {
                if (data_.residuals[e] > kReachTolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// 重い点をつかんで、Pin の点に阻まれて届かなかったとき（S88）、今の姿勢から Lock でない Pin の点（PullsPin）を外して解き直す。
        /// all が false なら、つかんだ点の骨から根（腰）へたどる道の上の骨の点だけを外す（首をつかめば背骨と腰、足をつかめば脚と腰）。
        /// 全部を外すと、届かないことと関係のない両手・両足の Pin の点まで外れて、体ごと付いて動いた（10-28 ユーザー報告「首の点を動かすと、
        /// 簡単に腰の点が動く。さらに腰の点の動きにあわせて、両手両足の点が動く」）。道の上を外しても届かなければ、all で残りも外す。
        /// 外した点は、今の姿勢の形のまま（関節の硬さは解き直す元の姿勢へ戻すばね）、つかんだ点に付いて動く。
        /// つかんだ点が根から離れていると、根の Pin の点は最後に置かれるので、届かない分がつかんだ点に残る
        /// （10-27 実測で、腰を Pin にしたキーで首を 5cm 持ち上げると、腰は 4mm しか上がらず、首は 6cm 届かなかった）。
        /// 外す点が無ければ解き直さず false
        /// </summary>
        bool PullPins (bool all, float pull, float scale, Quaternion rootInverse, Vector3 rootPosition)
        {
            bool[] path = new bool[bodies_.Count];
            foreach (Point point in grabbed_) {
                int body = IsFootBoxed (point) ? point.floorGroup : point.body;
                for (int b = body; b >= 0 && !path[b]; b = bodyParents_[b]) path[b] = true;
            }
            bool released = false;
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                // 重さ 0 の Pin の点は、1 回目から外してある
                if (!point.aim && GetWeights (point, pull, out positionWeight, out rotationWeight) && Releases (point, all, path)) released = true;
            }
            if (!released) return false;
            for (int i = 0; i < bodies_.Count; i++) {
                heavyPositions_[i] = data_.positions[i];
                heavyRotations_[i] = data_.rotations[i];
            }
            int count = 0;
            grabbedEffectors_.Clear ();
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (point, pull, out positionWeight, out rotationWeight)) continue;
                if (Releases (point, all, path) || (point.aim && PullsPin (point))) continue;
                if (grabbed_.Contains (point)) grabbedEffectors_.Add (count);
                data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight, scale);
            }
            int boxes = count;
            count = AddFootBoxes (count, rootInverse, rootPosition);
            for (int e = boxes; e < count; e++) grabbedEffectors_.Add (e);
            if (count == 0) return false;
            MarkFixedBones ();
            RunSolver (count, heavyPositions_, heavyRotations_);
            return true;
        }

        /// <summary>
        /// 軽い点をつかんでいる間、動かせない点を壁にするか（S89）。つかんだ点がどれも、動かせない点の骨か、その先の骨にあるとき（力が先から伝わってくるとき）だけ。
        /// 根の側（胸など）や、ほかの枝（反対の手）もつかんでいれば壁にしない: 胸だけをつかむと、両肩の点が壁になって胸が動けなかった
        /// （10-28 ユーザー報告「passiveとふつうの点とを複数選択して移動するとpassiveの点が動く」）
        /// </summary>
        bool PassiveApplies (Point point)
        {
            bool any = false;
            foreach (Point grabbed in grabbed_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (grabbed, Mathf.Clamp01 (settings_.pull), out positionWeight, out rotationWeight) && !IsFootBoxed (grabbed)) continue;
                int body = IsFootBoxed (grabbed) ? grabbed.floorGroup : grabbed.body;
                if (body < 0) continue;
                if (body != point.body && !IsBelow (body, point.body)) return false;
                any = true;
            }
            return any;
        }

        /// <summary>PullPins で外す Pin の点か（all でなければ、根への道の上の骨の点だけ）</summary>
        bool Releases (Point point, bool all, bool[] path)
        {
            return PullsPin (point) && (all || path[point.body]);
        }

        /// <summary>
        /// 重い点をつかんで動かしている間に、この Pin の点を引けるようにするか（S88。重さ 0 なら目標に使わない。重さ 1 なら、届かなければ PullPins で外す）。Lock の点（動かさないと決めた点）と、
        /// 向きを混ぜる点（膝の Pole。S34。向きを決める点で、引く物ではない）は今のまま。引かれた点は、離したときに骨の今の場所で Pin のまま付け直す（SyncToBone）
        /// </summary>
        bool PullsPin (Point point)
        {
            return !grabbed_.Contains (point) && point.values.locked <= 0.5f && point.blendBody < 0;
        }

        /// <summary>Floor Clamp の足の箱で動かしている足の点か（点としては解かない。AddFootBoxes）</summary>
        bool IsFootBoxed (Point point)
        {
            return point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup);
        }

        /// <summary>解くときに使える点か（解く骨に乗っていて、足の箱・床に運ばれる点でない）</summary>
        bool IsUsable (Point point)
        {
            // 足の箱で動かしている足の点は使わない（足の骨を箱の位置と向きに留める。AddFootBoxes）
            return point.body >= 0 && !carried_.Contains (point) && !IsFootBoxed (point);
        }

        /// <summary>今効かせる点の、位置と向きの固定の強さ（設定の pull を掛けた値）。どちらも 0 なら false</summary>
        bool GetWeights (Point point, float pull, out float positionWeight, out float rotationWeight)
        {
            positionWeight = rotationWeight = 0;
            if (point.passive || !IsUsable (point) || !IsSolved (point)) return false;
            positionWeight = Mathf.Clamp01 (point.values.positionWeight) * pull;
            rotationWeight = point.hasRotation ? Mathf.Clamp01 (point.values.rotationWeight) * pull : 0;
            // Floor Clamp の間は、同じ骨に位置を固定した点がほかにもあれば、向きは点の位置で決める（向きの固定を使わない）
            if (floorClamp && solveOnly_ == null && point.contact && rotationWeight > 0 && HasOtherPositionPin (point, pull)) rotationWeight = 0;
            return positionWeight > 0 || rotationWeight > 0;
        }

        /// <summary>
        /// 点の目標。どの点も「骨の上の点を目標へ寄せる」だけ（S26。種類で解き方を変えない。同じ骨の点どうしは剛体として拘束しあう）。
        /// 軽い点はふつうの点として渡し、向けるだけになるのは向きを混ぜる点だけ
        /// </summary>
        FullBodyIkEffector PointEffector (Point point, float positionWeight, float rotationWeight, float scale)
        {
            return new FullBodyIkEffector {
                bone = point.body,
                offset = point.offset,
                priority = point.values.locked > 0.5f ? 1 : 0,
                position = point.control.localPosition * scale,
                rotation = point.control.localRotation * point.restRootRotation,
                positionWeight = positionWeight,
                rotationWeight = rotationWeight,
                aim = point.aim && !IsLight (point) ? 1 : 0,
                blendBone = point.blendBody,
                blend = point.blendBody >= 0 ? point.blend : 0,
                blendOffset = point.blendOffset,
                blendRest = point.blendRest,
            };
        }

        /// <summary>
        /// 軽い点をつかんで動かしている間（S88）、動かない点を今の場所（heavyPositions_ / heavyRotations_。1 回目の姿勢）へ留める目標を、先頭から入れる。入れた数を返す。
        /// 動かない点は、Free の重い点（重さ 1）と、つかんでいない Pin・Lock の点（位置の強さ 1。重さに関係なく）と、動かせない点（passive。S89。付け根から離れていても軸まわりに回れる。AxisTurns）。
        /// 強さが 1 より小さい Pin の点と、向きを混ぜる点（膝の Pole）は、動かない点にしない（今までどおり自分の目標で、つかんだ点と引き合う）。
        /// 動かない点は計算の上で動かない物で、その向こうの点は動かない（10-27 ユーザー指示「計算上動かないからその向こう側の制御点は動かない」）:
        /// - つかんだ点の骨から、動かない点のある骨を越えずに届かない骨は、向こう側の骨として動かない骨にする（frozen_）
        /// - 動かない点のある骨は、点のまわりに回っても向こう側が動かないときだけ回れる（Turns）。回れなければ向きも留める
        ///   （首が重く頭が軽いとき、頭の点を動かすと首が回る。腰が重いとき、膝を動かしても腰は回らない。背骨と反対の脚が付いて動くため）
        /// </summary>
        int AddHeldEffectors (float pull)
        {
            int n = bodies_.Count;
            held_.Clear ();
            bool[] seeds = new bool[n];
            bool[] walls = new bool[n];
            bool[] oriented = new bool[n];
            bool[] offAxis = new bool[n];
            int[] heldCount = new int[n];
            for (int i = 0; i < n; i++) heldOrigin_[i] = heldRotation_[i] = frozen_[i] = false;
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (grabbed_.Contains (point) && GetWeights (point, pull, out positionWeight, out rotationWeight)) seeds[point.body] = true;
            }
            foreach (Point point in points_) {
                if (!IsUsable (point) || grabbed_.Contains (point) || seeds[point.body]) continue;
                float positionWeight, rotationWeight;
                bool orientedPoint;
                if (point.passive) {
                    // 動かせない点（S89）は、重さに関係なく Pin の点と同じに留まる（向きは持たない）。力が先から伝わってこなければ、Free の軽い点と同じに引かれる
                    if (!PassiveApplies (point)) continue;
                    orientedPoint = false;
                }
                else if (GetWeights (point, pull, out positionWeight, out rotationWeight)) {
                    if (positionWeight < 1 || point.blendBody >= 0) continue;
                    orientedPoint = rotationWeight > 0;
                }
                else {
                    // Free の軽い点は引かれる
                    if (point.aim) continue;
                    orientedPoint = point.hasRotation;
                }
                held_.Add (point);
                heldCount[point.body]++;
                walls[point.body] = true;
                if (orientedPoint) oriented[point.body] = true;
                if (point.offset.sqrMagnitude > kEpsilon * kEpsilon) offAxis[point.body] = true;
            }

            // つかんだ点の骨から、動かない点のある骨で止まりながらたどる。entry は動かない点のある骨へ入ってきた隣の骨（2 つ以上の側から入れば -2）
            bool[] reached = new bool[n];
            int[] entry = new int[n];
            Stack<int> open = new Stack<int> ();
            for (int i = 0; i < n; i++) {
                entry[i] = -1;
                if (!seeds[i]) continue;
                reached[i] = true;
                open.Push (i);
            }
            while (open.Count > 0) {
                int b = open.Pop ();
                for (int m = 0; m < n; m++) {
                    if (m != bodyParents_[b] && bodyParents_[m] != b) continue;
                    if (walls[m] && !seeds[m]) {
                        reached[m] = true;
                        entry[m] = entry[m] == -1 || entry[m] == b ? b : -2;
                        continue;
                    }
                    if (reached[m]) continue;
                    reached[m] = true;
                    open.Push (m);
                }
            }
            for (int i = 0; i < n; i++) {
                if (reached[i]) continue;
                frozen_[i] = heldOrigin_[i] = heldRotation_[i] = true;
            }

            int count = 0;
            foreach (Point point in points_) {
                if (!held_.Contains (point)) continue;
                int body = point.body;
                if (frozen_[body]) continue;
                // 付け根から離れた点を 1 つだけ留めた骨は、付け根と点を結ぶ軸まわりに回せる（AxisTurns。骨は留めた点を中心に回る、の基盤どおり）
                bool axisTurn = heldCount[body] == 1 && !oriented[body] && AxisTurns (body, entry[body]);
                bool rotation = !heldRotation_[body] && !axisTurn && !Turns (body, entry[body], oriented[body], offAxis[body]);
                if (rotation) heldRotation_[body] = true;
                // 向きも留めた骨は、付け根の場所も決まる。向きを留めない骨は、付け根の点を留めたときと、軸まわりに回すとき（付け根は動かない親に付いている）
                if (rotation || axisTurn || point.offset.sqrMagnitude <= kEpsilon * kEpsilon) heldOrigin_[body] = true;
                data_.effectors[count++] = new FullBodyIkEffector {
                    bone = body,
                    offset = point.offset,
                    priority = 1,
                    position = heavyPositions_[body] + heavyRotations_[body] * point.offset,
                    rotation = heavyRotations_[body],
                    positionWeight = 1,
                    rotationWeight = rotation ? 1 : 0,
                    blendBone = -1,
                };
            }
            return count;
        }

        /// <summary>
        /// 動かない点のある骨 body が、点のまわりに回れるか（AddHeldEffectors）。side はつかんだ点の側の隣の骨。
        /// 回ると向こう側（side 以外の隣の骨）が動くなら回れない: 子の骨は付いて回り、親の骨は点が付け根から離れていれば付け根ごと動く。
        /// 向きを持つ点（手・足・腰・頭）は、向きも動かない
        /// </summary>
        bool Turns (int body, int side, bool oriented, bool offAxis)
        {
            if (side < 0 || oriented) return false;
            int parent = bodyParents_[body];
            if (parent >= 0 && parent != side && offAxis) return false;
            for (int c = body + 1; c < bodies_.Count; c++) {
                if (bodyParents_[c] == body && c != side) return false;
            }
            return true;
        }

        /// <summary>
        /// 動かない点を 1 つだけ留めた骨 body が、付け根と点を結ぶ軸まわりに回れるか（AddHeldEffectors）。side はつかんだ点の側の隣の骨。
        /// 骨は留めた点を中心に回る（全身 IK の基盤）が、付け根も動かない親に付いていれば、留めた所が 2 つ（付け根と点）になり、2 点を結ぶ軸まわりだけが残る。
        /// 親が向こう側（動かない骨）で、つかんだ点の側が子で、ほかに子が無ければ回れる（ほかの子は軸まわりに回ると動いてしまう）。
        /// 鎖骨の付け根から前後にずらした点なら、軸は体の前後を向き、肩が上下に回る。点が付け根にあれば Turns が付け根まわりに回す
        /// </summary>
        bool AxisTurns (int body, int side)
        {
            int parent = bodyParents_[body];
            if (side < 0 || parent < 0 || !frozen_[parent] || bodyParents_[side] != body) return false;
            for (int c = body + 1; c < bodies_.Count; c++) {
                if (bodyParents_[c] == body && c != side) return false;
            }
            return true;
        }

        /// <summary>
        /// 軽い点をつかんでいる間に、重い点・Pin の点を留めたことで動けなくなる骨を、動かない骨（FullBodyIkData.fixedBones）にする。
        /// 骨の動きを止めるもの（足場）は、付け根（親の骨が場所も向きも決まっていれば動かない）と、子の骨の付け根（留めた骨・動けない骨の付け根）。
        /// 足場が 2 つ以上ある骨は、剛体として動けない（2 点を結ぶ軸まわりのねじりは残るが、ほかの子が付いて回るので止める）。これを広がらなくなるまで繰り返す。
        /// 反復で解くソルバーは、こうした挟まれた骨を少し曲げた所で止まり、仕上げでつなぎ直すと留めた骨がずれた
        /// （10-27。鎖骨の無いキャラで、胸を軽くして肩を動かすと、首・右上腕・肘で挟まれた胸が 9mm 曲がり、重い首が 0.5mm 動いた）。
        /// 動かない骨にするのは、挟まれた軽い点の骨・点の無い骨だけ（重い点の骨は目標で留めてある）
        /// </summary>
        void LockHeld ()
        {
            int n = bodies_.Count;
            bool[] locked = new bool[n];
            for (int i = 0; i < n; i++) locked[i] = heldOrigin_[i] && heldRotation_[i];
            for (bool grew = true; grew;) {
                grew = false;
                for (int b = 0; b < n; b++) {
                    if (locked[b] || heldOrigin_[b] || heldRotation_[b]) continue;
                    int p = bodyParents_[b];
                    int anchors = p >= 0 && locked[p] ? 1 : 0;
                    for (int c = b + 1; c < n && anchors < 2; c++) {
                        if (bodyParents_[c] == b && (locked[c] || heldOrigin_[c])) anchors++;
                    }
                    if (anchors < 2) continue;
                    locked[b] = true;
                    grew = true;
                }
            }
            // 動かない点の骨は、目標で留めてある（動かない骨にすると、足を留めた膝の点が膝をほとんど動かせなかった）。向こう側の骨は動かない骨にする
            for (int i = 0; i < n; i++) {
                if (frozen_[i] || (locked[i] && !heldOrigin_[i])) data_.fixedBones[i] = 1;
            }
        }

        /// <summary>
        /// 軽い点をつかんでいる間に留めた動かない点の骨とその向こうの骨を、1 回目の場所と向き（heavyPositions_ / heavyRotations_）へぴったり戻す。
        /// ソルバーは最後に骨を根から順につなぎ直すので、反復で詰めきれなかった関節のずれが、留めた骨の側へ寄って残る
        /// （10-27 実測で、胸を軽くして肩を動かすと、重い首が 0.5mm 動いた）。重い点は動かない物として解きたいので、
        /// 留めた骨ごとに、根の側の次に留めた骨までの間の骨（軽い点の骨・点の無い骨）を回して合わせる（CCD）。蝶番の骨は蝶番の軸まわりにだけ回す。
        /// 首と反対の上腕のように、同じ骨（上胸）から留めた骨が何本も出ているときは、1 本ずつ合わせると前に合わせた骨がずれるので、
        /// 全部の留めた骨を何周か回して一緒に詰める（留めた骨の場所は 1 回目の姿勢で全部満たせるので、詰まっていく）。合わせた分のずれは、軽い点の側に出る
        /// </summary>
        void SnapHeld ()
        {
            int n = bodies_.Count;
            Vector3[] positions = new Vector3[n];
            Quaternion[] rotations = new Quaternion[n];
            for (int i = 0; i < n; i++) {
                positions[i] = data_.positions[i];
                rotations[i] = data_.rotations[i];
            }
            for (int iteration = 0; iteration < kSnapHeldIterations; iteration++) {
                bool off = false;
                for (int h = 0; h < n; h++) {
                    if (!heldOrigin_[h]) continue;
                    Vector3 target = heavyPositions_[h];
                    if ((positions[h] - target).sqrMagnitude <= 1e-14f) continue;
                    off = true;
                    for (int c = bodyParents_[h]; c >= 0 && !heldOrigin_[c] && !heldRotation_[c]; c = bodyParents_[c]) {
                        Vector3 from = positions[h] - positions[c];
                        Vector3 to = target - positions[c];
                        Quaternion turn;
                        int p = bodyParents_[c];
                        if ((data_.bones[c].hinge != 0 || data_.bones[c].axisLock != 0) && p >= 0) {
                            // 蝶番と、軸まわりにだけ回る骨（S89。軸は自分の骨の枠で持つ）は、その軸まわりにだけ回す
                            Vector3 axis = data_.bones[c].axisLock != 0 ? rotations[c] * (Vector3)data_.bones[c].lockAxis : rotations[p] * (Vector3)data_.bones[c].hingeAxis;
                            Vector3 a = Vector3.ProjectOnPlane (from, axis), b = Vector3.ProjectOnPlane (to, axis);
                            if (a.sqrMagnitude < 1e-12f || b.sqrMagnitude < 1e-12f) continue;
                            turn = Quaternion.AngleAxis (Vector3.SignedAngle (a, b, axis), axis);
                        }
                        else {
                            if (from.sqrMagnitude < 1e-12f || to.sqrMagnitude < 1e-12f) continue;
                            turn = Quaternion.FromToRotation (from, to);
                        }
                        RotateSubtree (positions, rotations, c, turn);
                    }
                }
                if (!off) break;
            }
            for (int h = 0; h < n; h++) {
                if (heldRotation_[h]) RotateSubtree (positions, rotations, h, heavyRotations_[h] * Quaternion.Inverse (rotations[h]));
            }
            for (int i = 0; i < n; i++) {
                data_.positions[i] = positions[i];
                data_.rotations[i] = rotations[i];
            }
        }

        /// <summary>骨 body とその先を、body の付け根まわりに turn だけ回す（ルート空間の位置と向き）</summary>
        void RotateSubtree (Vector3[] positions, Quaternion[] rotations, int body, Quaternion turn)
        {
            Vector3 pivot = positions[body];
            for (int k = body; k < bodies_.Count; k++) {
                if (k != body && !IsBelow (k, body)) continue;
                positions[k] = pivot + turn * (positions[k] - pivot);
                rotations[k] = Quaternion.Normalize (turn * rotations[k]);
            }
        }

        /// <summary>解く骨 i が、解く骨 bone の先（子・孫）にあるか</summary>
        bool IsBelow (int i, int bone)
        {
            for (int k = bodyParents_[i]; k >= 0; k = bodyParents_[k]) {
                if (k == bone) return true;
            }
            return false;
        }

        /// <summary>足の箱（Floor Clamp で動かしている足）の目標を足す。足す前の数を受け取り、足した後の数を返す</summary>
        int AddFootBoxes (int count, Quaternion rootInverse, Vector3 rootPosition)
        {
            if (solveOnly_ != null) return count;
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
            return count;
        }

        /// <summary>先頭から count 個の目標で、元の姿勢（positions / rotations。ルート空間）から解く</summary>
        void RunSolver (int count, Vector3[] positions, Quaternion[] rotations)
        {
            for (int i = 0; i < bodies_.Count; i++) {
                data_.positions[i] = positions[i];
                data_.rotations[i] = rotations[i];
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
                if (other != point && !other.passive && other.body == point.body && !carried_.Contains (other) && Mathf.Clamp01 (other.values.positionWeight) * pull >= 1) return true;
            }
            return false;
        }
    }

}
