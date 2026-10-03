using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Lilium;

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
        public static BodyPointId Top (HumanBodyBones bone)
        {
            return new BodyPointId (bone + "Top");
        }

        /// <summary>骨の前に出した点（既定の一覧での名前）</summary>
        public static BodyPointId Direction (HumanBodyBones bone)
        {
            return new BodyPointId (bone + "Direction");
        }

        /// <summary>かかと（foot は足首の骨。既定の一覧での名前）</summary>
        public static BodyPointId Heel (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "Heel");
        }

        /// <summary>つま先の先（foot は足首の骨。既定の一覧での名前）</summary>
        public static BodyPointId ToeTip (HumanBodyBones foot)
        {
            return new BodyPointId (RigPaths.SideOf (foot) + "ToeTip");
        }

        /// <summary>人差し指の付け根の点（hand は手首の骨。既定の一覧での名前）</summary>
        public static BodyPointId PalmIndex (HumanBodyBones hand)
        {
            return new BodyPointId (RigPaths.SideOf (hand) + "PalmIndex");
        }

        /// <summary>小指の付け根の点（hand は手首の骨。既定の一覧での名前）</summary>
        public static BodyPointId PalmLittle (HumanBodyBones hand)
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
    /// 固定した点が 1 つだけの骨は、その点まわりに回れる（向きを留めたいときは、点の向きを固定するか、同じ骨にもう 1 点固定する）
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
            /// <summary>骨の上の点の、骨の付け根からの位置（骨の枠・メートル）。関節の点は 0</summary>
            public Vector3 offset;
            public Transform control;
            public BodyPoint values;
            public Transform bone;
            public bool hasRotation;
            public Quaternion restRootRotation;
            public Vector3 restRootPosition;
            /// <summary>骨の階層での深さ</summary>
            public int depth;
            /// <summary>解く骨の並びでの番号（解く骨でなければ -1）</summary>
            public int body = -1;
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
                    points_.Add (new Point {
                        id = new BodyPointId (point.name),
                        offset = Quaternion.Inverse (restRotation) * offset,
                        control = control,
                        values = values,
                        bone = bone,
                        hasRotation = point.hasRotation,
                        restRootRotation = restRotation,
                        restRootPosition = rig.GetRestRootPosition (bone) + offset,
                        depth = Depth (bone, rig.root.transform),
                    });
                }
                // 同じ深さは定義の順（List.Sort は安定でないので、順番も比べる）
                List<Point> order = new List<Point> (points_);
                points_.Sort ((a, b) => a.depth != b.depth ? a.depth.CompareTo (b.depth) : order.IndexOf (a).CompareTo (order.IndexOf (b)));
                CreateBodies (definition);
            }
            bones_ = bones.ToArray ();
            inputPositions_ = new Vector3[bones_.Length];
            inputRotations_ = new Quaternion[bones_.Length];
        }

        /// <summary>
        /// 解く骨（定義で「解く」にした FK の骨のうち、このキャラにあるもの）を剛体の並びにする。
        /// 親は、骨の階層を上へたどって最初に当たる解く骨（間の解かない骨は、親に付いて動く）
        /// </summary>
        void CreateBodies (EditRigDefinition definition)
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

            data_ = FullBodyIkData.Create (bodies_.Count, points_.Count, Allocator.Persistent);
            solveRotations_ = new Quaternion[bodies_.Count];
            solvePositions_ = new Vector3[bodies_.Count];
            for (int i = 0; i < bodies_.Count; i++) {
                RigBinding.Fk fk = solved[order[i]];
                int parent = -1;
                for (Transform t = bodies_[i].parent; t != null && t != root && parent < 0; t = t.parent) parent = bodies_.IndexOf (t);
                bodyParents_.Add (parent);
                HumanBodyBones human = fk.control.bone;
                bool arm = human == HumanBodyBones.LeftLowerArm || human == HumanBodyBones.RightLowerArm;
                bool leg = human == HumanBodyBones.LeftLowerLeg || human == HumanBodyBones.RightLowerLeg;
                // 曲がる向きの既定は、IK の組の定義（消していないので読める）。無ければ肘は後ろ・膝は前
                Vector3 hint = arm ? Vector3.back : Vector3.forward;
                if (definition != null) {
                    foreach (EditRigDefinition.IkChain chain in definition.ikChains) {
                        if (chain.mid == human && chain.defaultHint.sqrMagnitude > kEpsilon) hint = chain.defaultHint;
                    }
                }
                // 蝶番の軸は基準姿勢で決める: 親の骨の向きと、関節が出る向きの両方に直交する向き。親の骨の枠で持つ
                Vector3 axis = Vector3.zero;
                if ((arm || leg) && parent >= 0) {
                    Vector3 upper = rig_.GetRestRootPosition (bodies_[i]) - rig_.GetRestRootPosition (bodies_[parent]);
                    Vector3 cross = Vector3.Cross (rig_.BodyToRoot (hint), upper);
                    if (cross.sqrMagnitude > kEpsilon) axis = Quaternion.Inverse (rig_.GetRestRootRotation (bodies_[parent])) * cross.normalized;
                }
                data_.bones[i] = new FullBodyIkBone {
                    parent = parent,
                    stiffness = Mathf.Clamp01 (fk.control.stiffness),
                    hinge = (arm || leg) && parent >= 0 ? 1 : 0,
                    hingeAxis = axis,
                };
            }
            foreach (Point point in points_) point.body = bodies_.IndexOf (point.bone);
        }

        public void Dispose ()
        {
            if (data_.isCreated) data_.Dispose ();
            data_ = default (FullBodyIkData);
        }

        /// <summary>計算を Burst のジョブで走らせるか。false なら同じ関数をマネージドで直に呼ぶ（テスト用。結果の差を比べる）</summary>
        internal bool useBurst = true;

        /// <summary>解く骨（剛体）の数</summary>
        public int bodyCount
        {
            get { return bodies_.Count; }
        }

        /// <summary>直前に解いたとき、計算が Burst で動いたか（切れているとマネージドで動く）</summary>
        public bool lastSolveUsedBurst { get; private set; }

        /// <summary>直前に解いたとき、曲がる向きを既定から決めた蝶番（まっすぐな肘・膝）の数</summary>
        public int lastDefaultBendCount { get; private set; }

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
            get { return rig_.humanScale > kEpsilon ? rig_.humanScale : 1; }
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
            return point.bone.position + point.bone.rotation * point.offset;
        }

        /// <summary>点が動かす編集用の骨（無ければ null）</summary>
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
            point.control.localRotation = Normalize (Quaternion.Inverse (rootTransform.rotation) * rotation * Quaternion.Inverse (point.restRootRotation));
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
            localPosition = rootTransform.InverseTransformPoint (BonePointWorld (point)) / humanScale;
            localRotation = point.hasRotation
                ? Normalize (Quaternion.Inverse (rootTransform.rotation) * point.bone.rotation * Quaternion.Inverse (point.restRootRotation))
                : point.control.localRotation;
            return true;
        }

        void CapturePoint (Point point)
        {
            point.control.localPosition = rootTransform.InverseTransformPoint (BonePointWorld (point)) / humanScale;
            if (point.hasRotation) {
                point.control.localRotation = Normalize (Quaternion.Inverse (rootTransform.rotation) * point.bone.rotation * Quaternion.Inverse (point.restRootRotation));
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
                point.control.localPosition = rootTransform.InverseTransformPoint (BonePointWorld (point)) / humanScale;
                position = true;
            }
            if (point.hasRotation && point.values.rotationWeight >= 1 && Quaternion.Angle (point.bone.rotation, PointWorldRotation (point)) > kSyncAngle) {
                point.control.localRotation = Normalize (Quaternion.Inverse (rootTransform.rotation) * point.bone.rotation * Quaternion.Inverse (point.restRootRotation));
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

        /// <summary>届かなかった差（メートル）。固定の強さが 1 の点が、解いた後にどれだけ離れているか</summary>
        public float GetResidual (BodyPointId bone)
        {
            Point point = Find (bone);
            return point != null ? Vector3.Distance (BonePointWorld (point), PointWorldPosition (point)) : 0;
        }

        static Quaternion Normalize (Quaternion q)
        {
            // 同じ回転の 2 通りの表し方のうち w ≥ 0 に揃える（キーの間で符号が飛ぶと補間が遠回りになる）
            q = Quaternion.Normalize (q);
            return q.w < 0 ? new Quaternion (-q.x, -q.y, -q.z, -q.w) : q;
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
                if (point.body < 0 || !IsSolved (point)) continue;
                float positionWeight = Mathf.Clamp01 (point.values.positionWeight) * pull;
                float rotationWeight = point.hasRotation ? Mathf.Clamp01 (point.values.rotationWeight) * pull : 0;
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
                };
            }
            if (count == 0) return false;
            data_.effectorCount = count;
            MarkFixedBones ();
            data_.iterations = Mathf.Max (1, settings_.iterations);
            data_.rootPin = Mathf.Clamp01 (settings_.hipsPin);

            if (useBurst) {
                new FullBodyIkJob { data = data_ }.Run ();
            }
            else {
                data_.status[FullBodyIkData.kStatusBurst] = 0;
                FullBodyIk.Solve (ref data_);
            }
            lastSolveUsedBurst = data_.status[FullBodyIkData.kStatusBurst] != 0;
            lastDefaultBendCount = data_.status[FullBodyIkData.kStatusDefaultBend];

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
    }

}
