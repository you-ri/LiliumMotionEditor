using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;

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
    /// 定義の「重さ」が 0 の点（軽い点。S88）をつかんで動かすと、つかんだ点が引き、引かれる点は状態と重さで決まる: 軽い点は Free でも Pin でも引かれ、
    /// 重い点（1 の点）は Free でも Pin でも今の場所に留まる。Lock の点は重さに関係なく留まる。重い点を動かすとき・キーから解き直すときは、今までどおり Free の点は重さに関係なく引かれる。
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
    public sealed partial class FullBodyRig : System.IDisposable
    {
        const float kEpsilon = 1e-6f;
        /// <summary>軽い点をつかんでいる間の、つかんだ点の位置の強さの上限。重い点・Pin の点（強さ 1）より少しだけ弱くし、ぶつかったらつかんでいる点が譲る</summary>
        const float kLightPull = 0.999f;
        /// <summary>
        /// 軽い点をつかんでいる間の反復の回数（定義の iterations の何倍か）。重い点を留めたまま軽い点を引くと、関節のずれが詰まりにくい（10-27 実測で、1 倍では首が 1.45mm、4 倍で 0.5mm）。
        /// 残りは SnapHeld で合わせる
        /// </summary>
        const int kLightIterations = 4;

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
            /// <summary>重さ 0 の点か（定義の重さが 0）。軽い点（light）でなければ、骨を回して点を目標の方へ向けるだけの点（S28。目標の aim）</summary>
            public bool weightless;
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
                        weightless = point.definition != null && point.definition.weight <= 0,
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
            keptPositions_ = new Vector3[bodies_.Count];
            keptRotations_ = new Quaternion[bodies_.Count];
            pathTurns_ = new Quaternion[bodies_.Count];
            heavyRotations_ = new Quaternion[bodies_.Count];
            heavyPositions_ = new Vector3[bodies_.Count];
            heldOrigin_ = new bool[bodies_.Count];
            heldRotation_ = new bool[bodies_.Count];
            frozen_ = new bool[bodies_.Count];
            grabbedBodies_ = new bool[bodies_.Count];
            branchShifts_ = new Vector3[points_.Count];
            yieldedPositions_ = new Vector3[bodies_.Count];
            yieldedRotations_ = new Quaternion[bodies_.Count];
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
                point.light = point.weightless && point.blendBone == null && point.body >= 0 && !points_.Any (p => !p.weightless && !p.passive && p.body == point.body);
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
    }

}
