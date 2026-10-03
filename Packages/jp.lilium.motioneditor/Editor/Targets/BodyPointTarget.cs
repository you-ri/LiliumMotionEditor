using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点（S25）。状態は丸の色で表す（選び方は丸の外の輪の色で表す）。関節の上の丸をつかんで動かすと、全身が付いてくる。
    ///
    /// 点の状態は 3 つ。状態は、次にその点のキーを打つまで先のフレームへ引き継ぐ（IK / FK の切り替えと同じ）。
    /// 引き継ぐのは状態だけで、場所は引き継がない（キーの無いフレームでは、そのフレームの骨の位置に置く）:
    /// - Free（青）: 体に付いて動く
    /// - Pin（オレンジ）: 動かした点。動かした場所に留まる。手を離すと、実際の骨の位置へ合わせ直す（体が届かない所へ引いても、点が体の形から離れない）
    /// - Locked（赤）: 置いた場所から動かさない。丸を右ボタンで付け外しする。いくつか選んでいるときは、選んでいる点をまとめて付け外しする
    ///
    /// 点はリグ定義の点の一覧（骨の上の好きな場所）から作る。点の種類（関節・顔の前・かかとなど）で扱いは変わらない（S26）:
    /// 動かすと、その点が届くように骨ごと引かれる。同じ骨の上の点どうしは剛体として拘束しあう（2 点で骨が置かれ、3 点で向きまで決まる）。
    /// 点を 1 つ選んでギズモで回すと、その点を中心に骨を FK で回す（子の点は骨に付いて回る。固定している子の点は、置いた場所も同じだけ回す。
    /// 骨の付け根から離れた点は、その場に留めて骨の付け根を全身 IK で動かす）。
    /// いくつか選んでいるときは、その後に押した 1 つ（中心の点）にギズモを出し、選んでいる点だけを中心の点のまわりで回す・動かす。
    /// 向きを持つ点は、回すと向きも回る。
    /// 点どうしは線でつながる（丸との間に隙間を空ける）。
    /// 手を離すと、解いた姿勢が骨のキーとして今のフレームに打たれる（キーの間は骨の値の補間で動く）。
    /// 点のキーは、動かした点・固定した点にだけ打つ（キーフレームへ戻ったときに、同じ点から直せる）
    /// </summary>
    public enum BodyPointState
    {
        /// <summary>体に付いて動く（青）</summary>
        Free,
        /// <summary>動かした場所に留まる。手を離すと、実際の骨の位置へ合わせ直す（オレンジ）</summary>
        Pin,
        /// <summary>置いた場所から動かさない（赤）</summary>
        Locked,
    }

    public sealed class BodyPointTarget : PoseTarget
    {
        /// <summary>点どうしをつなぐ線の太さ（ピクセル）と、丸との間に空ける隙間（丸の中心から。ハンドルの大きさの何倍か）</summary>
        const float kLinkWidth = 4.5f;
        const float kLinkGap = 1.7f;
        static readonly Color kLinkColor = new Color (0.8f, 0.85f, 0.9f, 0.55f);
        /// <summary>点をつかまない間に出す、固定した点の印の大きさ（ハンドルの大きさの何倍か）</summary>
        const float kMarkScale = 0.6f;
        static readonly Color kPinnedColor = new Color (1f, 0.55f, 0.1f, 0.8f);
        static readonly Color kFreeColor = new Color (0.2f, 0.8f, 1f, 0.35f);
        static readonly Color kLockedColor = new Color (1f, 0.2f, 0.15f, 0.85f);
        /// <summary>
        /// 丸の外に出す輪。丸との隙間（ハンドルの大きさの何倍か）と線の太さ（ピクセル）。
        /// 色で選び方を表す（丸の色は状態のためにとってある）: 選ぼうとしている（マウスが乗っている・枠に入っている）/ 選んで動かせる
        /// </summary>
        const float kRingGap = 0.4f;
        const float kRingWidth = 2.5f;
        static readonly Color kRingHoverColor = new Color (1f, 1f, 1f, 0.7f);
        static readonly Color kRingSelectedColor = new Color (0.3f, 1f, 0.45f, 1f);

        readonly EditingRig rig_;
        readonly FullBodyRig body_;
        readonly BoneShapes shapes_;
        readonly BodyPointId human_;
        /// <summary>線でつなぐ相手の点（無ければ null。腰など）</summary>
        BodyPointTarget link_;
        /// <summary>関節の点でないか（骨の付け根から離れた、骨の上の点。骨から線でつないで描く）</summary>
        readonly bool offsetPoint_;
        readonly HumanBodyBones humanBone_;
        readonly Transform control_;
        readonly Transform bone_;
        readonly Transform displayBone_;
        readonly bool hasRotation_;
        readonly int moveHash_;
        /// <summary>動かしたがまだキーを打っていない値（位置と位置の固定の強さ）</summary>
        bool positionDirty_;
        /// <summary>動かしたがまだキーを打っていない値（向きと向きの固定の強さ）</summary>
        bool rotationDirty_;
        /// <summary>完全固定を切り替えたがまだキーを打っていない</summary>
        bool lockedDirty_;
        bool contextClicked_;

        /// <summary>選んだときに移動 / 回転のギズモを出すか（いくつか選んでいる間は、中心の点にだけ出す。窓が描く前に入れる）</summary>
        public bool showGizmo = true;

        /// <summary>引いている選択の枠に入っているか（窓が描く前に入れる。離すと選ばれる点に、選ぼうとしている輪を出す）</summary>
        public bool inBox;

        Vector3 dragDelta_;

        /// <summary>
        /// 丸をつかんで動かした量（ワールド。読むと消える）。いくつか選んでいるとき、窓がほかの選んでいる点を同じだけ動かす
        /// </summary>
        public Vector3 ConsumeDragDelta ()
        {
            Vector3 delta = dragDelta_;
            dragDelta_ = Vector3.zero;
            return delta;
        }

        /// <summary>点を delta（ワールド）だけ動かす。Free の点は、今の骨の位置から動かして Pin にする</summary>
        public void MoveBy (Vector3 delta)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinPosition (position + delta);
        }

        Quaternion rotateDelta_ = Quaternion.identity;

        /// <summary>
        /// 回転のギズモで回した量（ワールド。前から掛ける差。読むと消える）。点は自分では回さず、窓が回し方を決める
        /// （1 つだけ選んでいれば骨を FK で回して子の点ごと、いくつか選んでいれば選んだ点だけをこの点を中心に）
        /// </summary>
        public Quaternion ConsumeRotateDelta ()
        {
            Quaternion delta = rotateDelta_;
            rotateDelta_ = Quaternion.identity;
            return delta;
        }

        /// <summary>
        /// 点を pivot を中心に delta（ワールド）だけ回す。向きを持つ点は向きも回す。
        /// pinFree なら Free の点も、回した場所に置いて Pin にする。そうでなければ、固定している値だけ回す（Free の点は骨に付いて動く）
        /// </summary>
        public void RotateAround (Vector3 pivot, Quaternion delta, bool pinFree)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            if (pinFree || positionPinned) PinPosition (pivot + delta * (position - pivot));
            if (hasRotation_ && (pinFree || rotationPinned)) PinRotation (delta * rotation);
        }

        /// <summary>乗る骨の付け根にある点か（付け根から離れた所に置いた点でない）</summary>
        public bool isOnBoneOrigin
        {
            get { return !offsetPoint_; }
        }

        /// <summary>
        /// 体ごと回す: 今の骨の上の点（目標ではない）を、pivotOnBone を pivot に重ねて、pivot を中心に delta だけ回した場所に置く。向きを持つ点は向きも今の骨から回す。
        /// 目標から回すと、届いていない点（同じ足の Pin と Locked が食い違っているなど）の食い違いが回した後も残り、Locked の点が中心からずれていく。
        /// 骨の上の点から回すと、回した点どうしは体の上で矛盾しない（剛体として回る）。
        /// pinFree なら Free の点も Pin にする。そうでなければ、固定している値だけ置き直す
        /// </summary>
        public void RotateBodyAround (Vector3 pivot, Vector3 pivotOnBone, Quaternion delta, bool pinFree)
        {
            if (pinFree || positionPinned) PinPosition (pivot + delta * (bonePoint - pivotOnBone));
            if (hasRotation_ && (pinFree || rotationPinned)) PinRotation (delta * bone_.rotation);
        }

        /// <summary>点が付いている骨の上の点の今の位置（目標ではない）</summary>
        public Vector3 bonePosition
        {
            get { return bonePoint; }
        }

        /// <summary>点を動かす編集用の骨</summary>
        public Transform editingBone
        {
            get { return bone_; }
        }

        /// <summary>右ボタンで押されたか（読むと消える）。窓が、選んでいる点をまとめて固定 / 解除する</summary>
        public bool ConsumeContextClick ()
        {
            bool clicked = contextClicked_;
            contextClicked_ = false;
            return clicked;
        }

        /// <summary>
        /// 全身 IK の点の対象を作る（全身 IK の定義でなければ空）
        /// </summary>
        public static List<PoseTarget> Create (EditingRig rig, FullBodyRig body)
        {
            List<PoseTarget> targets = new List<PoseTarget> ();
            if (rig == null || rig.root == null || body == null) return targets;
            BoneShapes shapes = new BoneShapes (rig);
            foreach (RigBinding.Point point in rig.binding.points) {
                if (!point.enabled || !body.HasPoint (new BodyPointId (point.name))) continue;
                targets.Add (new BodyPointTarget (rig, body, shapes, point));
            }
            // 線でつなぐ相手。関節の点は、骨の階層を上へたどって最初に当たる関節の点。骨の上の点は、その骨の関節の点
            Dictionary<Transform, BodyPointTarget> joints = new Dictionary<Transform, BodyPointTarget> ();
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = (BodyPointTarget)target;
                if (!point.offsetPoint_) joints[point.bone_] = point;
            }
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = (BodyPointTarget)target;
                Transform root = rig.root.transform;
                for (Transform t = point.offsetPoint_ ? point.bone_ : point.bone_.parent; t != null && t != root && point.link_ == null; t = t.parent) {
                    joints.TryGetValue (t, out point.link_);
                }
            }
            return targets;
        }

        BodyPointTarget (EditingRig rig, FullBodyRig body, BoneShapes shapes, RigBinding.Point point)
        {
            rig_ = rig;
            body_ = body;
            shapes_ = shapes;
            human_ = new BodyPointId (point.name);
            humanBone_ = point.humanBone;
            offsetPoint_ = rig.GetPointRestOffset (point).sqrMagnitude > 1e-10f;
            control_ = body.GetControl (human_);
            bone_ = body.GetBone (human_);
            displayBone_ = point.bone;
            hasRotation_ = point.hasRotation;
            moveHash_ = ("MktBodyPointMove" + control_.GetEntityId ()).GetHashCode ();
        }

        /// <summary>点が乗る骨</summary>
        public HumanBodyBones bone
        {
            get { return humanBone_; }
        }

        /// <summary>点の名前（リグ定義の点の一覧の名前）</summary>
        public BodyPointId id
        {
            get { return human_; }
        }

        /// <summary>点が付いている「骨の上の点」の今の位置（付け根の点は骨の位置、離れた点は定義の場所）</summary>
        Vector3 bonePoint
        {
            get {
                Vector3 position;
                return body_.TryGetBonePointWorld (human_, out position) ? position : bone_.position;
            }
        }

        /// <summary>ギズモの軸の向き（骨の見た目の軸。骨の上の点は、骨から点への向き）</summary>
        Quaternion GetShape (Vector3 position, Quaternion rotation)
        {
            if (offsetPoint_) {
                Vector3 toward = position - bone_.position;
                return toward.sqrMagnitude > 1e-10f ? Quaternion.LookRotation (toward) : Quaternion.identity;
            }
            return rotation * Quaternion.Inverse (bone_.rotation) * shapes_.GetRotation (bone_);
        }

        /// <summary>位置か向きを固定しているか</summary>
        public bool pinned
        {
            get { return positionPinned || rotationPinned; }
        }

        public bool positionPinned
        {
            get { return body_.GetPositionWeight (human_) > 0; }
        }

        public bool rotationPinned
        {
            get { return hasRotation_ && body_.GetRotationWeight (human_) > 0; }
        }

        /// <summary>完全固定か（置いた場所から動かさない。手を離しても骨の位置へ合わせ直さない）</summary>
        public bool locked
        {
            get { return pinned && body_.GetLocked (human_); }
        }

        /// <summary>動かしてまだキーを打っていない値があるか</summary>
        public bool hasPendingKeys
        {
            get { return positionDirty_ || rotationDirty_ || lockedDirty_; }
        }

        Color fillColor
        {
            get { return locked ? kLockedColor : pinned ? kPinnedColor : kFreeColor; }
        }

        public override GameObject gameObject
        {
            get { return control_ != null ? control_.gameObject : null; }
        }

        public override string label
        {
            get { return "Point " + human_; }
        }

        public override Transform anchor
        {
            get { return displayBone_; }
        }

        public override Vector3 framePosition
        {
            get {
                Vector3 position;
                Quaternion rotation;
                GetPoint (out position, out rotation);
                return position;
            }
        }

        /// <summary>
        /// 点の位置と向き。固定していない間は、骨の今の位置と向きに出す（つかむとそこから固定が始まる）
        /// </summary>
        void GetPoint (out Vector3 position, out Quaternion rotation)
        {
            Vector3 pointPosition;
            Quaternion pointRotation;
            body_.TryGetPointWorld (human_, out pointPosition, out pointRotation);
            position = positionPinned ? pointPosition : bonePoint;
            rotation = rotationPinned ? pointRotation : bone_.rotation;
        }

        /// <summary>
        /// 位置を固定して、点を position へ置く。固定していなかった点は、向きの値も今の骨に合わせておく（後で向きを固定したときに飛ばない）
        /// </summary>
        void PinPosition (Vector3 position)
        {
            if (!pinned) body_.CapturePoint (human_);
            body_.SetPointPositionWorld (human_, position);
            body_.SetPositionWeight (human_, 1);
            positionDirty_ = true;
        }

        void PinRotation (Quaternion rotation)
        {
            if (!hasRotation_) return;
            if (!pinned) body_.CapturePoint (human_);
            body_.SetPointRotationWorld (human_, rotation);
            body_.SetRotationWeight (human_, 1);
            rotationDirty_ = true;
        }

        /// <summary>
        /// 固定を付け外しする。外すときは位置も向きも外す。付けるときは、今の骨の位置で位置だけ固定する
        /// </summary>
        public void TogglePin ()
        {
            if (pinned) {
                rotationDirty_ = rotationPinned;
                lockedDirty_ = body_.GetLocked (human_);
                body_.SetPositionWeight (human_, 0);
                body_.SetRotationWeight (human_, 0);
                body_.SetLocked (human_, false);
                positionDirty_ = true;
            }
            else {
                PinPosition (bonePoint);
            }
        }

        /// <summary>
        /// 完全固定を付け外しする。固定していない点は、今の骨の位置で固定してから完全固定にする。外しても固定は残る
        /// </summary>
        public void ToggleLock ()
        {
            if (!pinned) PinPosition (bonePoint);
            body_.SetLocked (human_, !body_.GetLocked (human_));
            lockedDirty_ = true;
        }

        /// <summary>今のフレームでの点の状態</summary>
        public BodyPointState state
        {
            get { return locked ? BodyPointState.Locked : pinned ? BodyPointState.Pin : BodyPointState.Free; }
        }

        /// <summary>
        /// 点の状態を変える。Free の点を Pin / Locked にするときは、今の骨の位置に置く。Locked を Pin にすると、置いた場所のまま Pin になる
        /// （次に手を離したときに、骨の位置へ合わせ直される）
        /// </summary>
        public void SetState (BodyPointState value)
        {
            if (value == state) return;
            switch (value) {
                case BodyPointState.Free:
                    SetLocked (false);
                    break;
                case BodyPointState.Locked:
                    SetLocked (true);
                    break;
                default:
                    if (!pinned) PinPosition (bonePoint);
                    if (body_.GetLocked (human_)) {
                        body_.SetLocked (human_, false);
                        lockedDirty_ = true;
                    }
                    break;
            }
        }

        /// <summary>
        /// 固定する（置いた場所から動かさない点にする）/ 固定を外す（何もしていない点に戻す）。
        /// 固定するとき、まだ動かしていない点は今の骨の位置に置く
        /// </summary>
        public void SetLocked (bool on)
        {
            if (on) {
                if (!pinned) PinPosition (bonePoint);
                if (!body_.GetLocked (human_)) {
                    body_.SetLocked (human_, true);
                    lockedDirty_ = true;
                }
            }
            else if (pinned) {
                TogglePin ();
            }
        }

        /// <summary>
        /// 固定した点（完全固定でないもの）を、実際の骨の位置へ合わせ直す。手を離したときに呼ぶ。合わせ直したら true（キーを打つ値が増える）
        /// </summary>
        public bool SyncToBone ()
        {
            bool position, rotation;
            body_.SyncPointToBone (human_, out position, out rotation);
            positionDirty_ |= position;
            rotationDirty_ |= rotation;
            return position || rotation;
        }

        /// <summary>左右の相手を探す名前（Point LeftHand ⇔ Point RightHand。体の中心の点は自分）</summary>
        public override string mirrorKey
        {
            get { return label; }
        }

        /// <summary>
        /// 左右の相手（体の中心の点なら自分）の今の状態と場所を、左右反転してこの点に入れる。
        /// Free の点の反転は Free（体に付いて動く）。Pin / Locked は、反転した場所に置いて同じ状態にする。向きを留めていれば向きも反転する
        /// </summary>
        public override bool MirrorFrom (PoseTarget source)
        {
            BodyPointTarget other = source as BodyPointTarget;
            // 相手は名前の左右を入れ替えた点（体の中心の点は自分）
            if (other == null) return false;
            string mirrored = MirrorNames.Swap (human_.name) ?? human_.name;
            if (other.human_.name != mirrored) return false;
            BodyPointState from = other.state;
            bool rotation = other.rotationPinned && hasRotation_;
            Vector3 position;
            Quaternion delta;
            other.TryGetValues (false, out position, out delta);
            if (from == BodyPointState.Free) {
                SetState (BodyPointState.Free);
                return true;
            }
            Vector3 normal;
            float center;
            rig_.GetMirrorPlane (out normal, out center);
            position -= 2 * (Vector3.Dot (position, normal) - center) * normal;
            SetValues (false, TransformChannels.Position, position, Quaternion.identity);
            if (rotation) {
                SetValues (false, TransformChannels.Rotation, position, EditingRigSolver.MirrorRotation (delta, normal));
            }
            else if (rotationPinned) {
                body_.SetRotationWeight (human_, 0);
                rotationDirty_ = true;
            }
            bool locked = from == BodyPointState.Locked;
            if (body_.GetLocked (human_) != locked) {
                body_.SetLocked (human_, locked);
                lockedDirty_ = true;
            }
            return true;
        }

        public override TransformChannels GetSpinChannels (bool pole)
        {
            return hasRotation_ ? TransformChannels.Position | TransformChannels.Rotation : TransformChannels.Position;
        }

        public override void SpinMove (Vector3 delta, bool pole)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinPosition (position + rig_.root.transform.TransformVector (delta));
        }

        public override void SpinRotateLocal (Quaternion delta, bool pole)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinRotation (rotation * delta);
        }

        public override void SpinRotateWorld (Quaternion delta, bool pole)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinRotation (delta * rotation);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (bone_ == null || control_ == null) return;

            // 右ボタン: 丸の上で、固定を付け外しする（プレビュー窓では動かさずに離したとき。動かすと視点の回転）
            Event ev = Event.current;
            if (!ev.alt && PoseHandleUtility.IsContextClick (ev, moveHash_)) {
                contextClicked_ = true;
                ev.Use ();
                GUI.changed = true;
                return;
            }

            // 状態は丸の色で出す。選び方は丸の外の輪で出す（選択の色に塗り替えると状態が分からなくなる）
            PoseHandleUtility.color = fillColor;
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            Quaternion shape = GetShape (position, rotation);

            DrawLink (position);
            DrawRing (position, selected);

            // 関節の丸（つかんで動かすと位置を固定する）
            Vector3 moved = PoseHandleUtility.DoJointHandle (moveHash_, control_, position, rotation, false, false);
            if (moved != position) {
                dragDelta_ += moved - position;
                PinPosition (moved);
                position = moved;
                GUI.changed = true;
            }

            // 選んでいる間はギズモ（Transform パネルのモードで移動 / 回転）。
            // 動かした量は丸をつかんだときと同じく窓へ渡す（いくつか選んでいれば、ほかの選んでいる点も同じだけ動かす）。
            // 回した量は窓へ渡すだけで、ここでは回さない（位置だけの点も、骨を FK で回して回せる）
            if (selected && showGizmo && PoseHandleUtility.goalRotate) {
                Quaternion turned;
                if (PoseHandleUtility.DoRotateGizmo (moveHash_, position, shape, out turned)) {
                    rotateDelta_ = turned * rotateDelta_;
                    GUI.changed = true;
                }
            }
            else if (selected && showGizmo) {
                Vector3 gizmoPosition = PoseHandles.PositionHandle (position, PoseHandleUtility.GizmoAxes (shape));
                if (gizmoPosition != position) {
                    dragDelta_ += gizmoPosition - position;
                    PinPosition (gizmoPosition);
                    GUI.changed = true;
                }
            }

            // 固定した点が届いていないときは、骨までの線を出す
            if (Event.current.type == EventType.Repaint && positionPinned) {
                Vector3 pointPosition;
                Quaternion pointRotation;
                body_.TryGetPointWorld (human_, out pointPosition, out pointRotation);
                Vector3 onBone = bonePoint;
                if ((pointPosition - onBone).sqrMagnitude > 1e-6f) {
                    Handles.color = PoseHandles.Fade (new Color (1, 0.85f, 0.2f, 0.8f));
                    Handles.DrawDottedLine (onBone, pointPosition, 3);
                }
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
        }

        /// <summary>
        /// 丸の外に少し隙間を空けて輪を描く。選んでいる点は選んだ色、選ぼうとしている点（マウスが乗っている・枠に入っている）は乗った色。
        /// どちらでもなければ描かない。マウスが乗った黄色の縁取りは丸の側（PoseHandles.JointHandle）が描く
        /// </summary>
        void DrawRing (Vector3 position, bool selected)
        {
            if (Event.current.type != EventType.Repaint) return;
            int hot = GUIUtility.hotControl;
            bool hover = HandleUtility.nearestControl == moveHash_ && (hot == 0 || hot == moveHash_);
            if (!selected && !hover && !inBox) return;
            Color saved = Handles.color;
            Handles.color = PoseHandles.Fade (selected ? kRingSelectedColor : kRingHoverColor);
            Vector3 normal = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
            Handles.DrawWireDisc (position, normal, EditorHost.current.handleSize * (1 + kRingGap), kRingWidth);
            Handles.color = saved;
        }

        /// <summary>
        /// つなぐ相手の点まで、少し太い線を引く（体の形が点と線で見える）。丸に重ならないよう、両端に隙間を空ける
        /// </summary>
        void DrawLink (Vector3 position)
        {
            if (Event.current.type != EventType.Repaint || link_ == null) return;
            Vector3 delta = link_.framePosition - position;
            float length = delta.magnitude;
            float gap = EditorHost.current.handleSize * kLinkGap;
            if (length <= gap * 2 + 1e-5f) return;
            Vector3 direction = delta / length;
            Color saved = Handles.color;
            Handles.color = PoseHandles.Fade (kLinkColor);
            Handles.DrawAAPolyLine (kLinkWidth, position + direction * gap, position + direction * (length - gap));
            Handles.color = saved;
        }

        /// <summary>
        /// 点をつかまない間（✋ が全身 IK の段でない）の表示。固定している点だけ、状態の色の小さい印を描く（操作は受けない）
        /// </summary>
        public void DrawPinned ()
        {
            if (Event.current.type != EventType.Repaint || bone_ == null || !pinned) return;
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            Color saved = Handles.color;
            Handles.color = fillColor;
            if (offsetPoint_) DrawLink (position);
            Vector3 normal = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
            Handles.DrawSolidDisc (position, normal, EditorHost.current.handleSize * kMarkScale);
            Handles.color = saved;
        }

        /// <summary>
        /// 姿勢全体にキーを打つとき（Key All・骨を動かした後）。固定している点の値だけ打つ
        /// </summary>
        public override void WriteKeys (CurveWriter writer)
        {
            if (positionPinned) WritePosition (writer);
            if (rotationPinned) WriteRotation (writer);
            if (locked) WriteLocked (writer);
        }

        /// <summary>
        /// 動かした点のキーを打つ（位置を動かしたら位置と位置の固定の強さ、回したら向きと向きの固定の強さ。固定を外したときは強さ 0 も打つ）
        /// </summary>
        public void WritePendingKeys (CurveWriter writer)
        {
            if (positionDirty_) WritePosition (writer);
            if (rotationDirty_) WriteRotation (writer);
            if (lockedDirty_) WriteLocked (writer);
            positionDirty_ = false;
            rotationDirty_ = false;
            lockedDirty_ = false;
        }

        /// <summary>動かした値を打たずに捨てる（姿勢をクリップから当て直したとき）</summary>
        public void ClearPendingKeys ()
        {
            positionDirty_ = false;
            rotationDirty_ = false;
            lockedDirty_ = false;
        }

        void WriteLocked (CurveWriter writer)
        {
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kLockedProperty, body_.GetLocked (human_) ? 1 : 0);
        }

        void WritePosition (CurveWriter writer)
        {
            writer.Transform (control_, TransformChannels.Position);
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, body_.GetPositionWeight (human_));
        }

        void WriteRotation (CurveWriter writer)
        {
            if (!hasRotation_) return;
            writer.Transform (control_, TransformChannels.Rotation);
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kRotationWeightProperty, body_.GetRotationWeight (human_));
        }

        public override void WriteSpinKeys (CurveWriter writer, bool pole, TransformChannels channels)
        {
            WritePendingKeys (writer);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (AnimationUtility.CalculateTransformPath (control_, rig_.root.transform));
        }

        /// <summary>
        /// メッシュのクリックでは選ばない（骨は FK の対象が受け持つ）
        /// </summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
        }

        float humanScale
        {
            get { return rig_.humanScale > 1e-6f ? rig_.humanScale : 1; }
        }

        /// <summary>
        /// 数値欄に出す値。位置はルートから見た点の位置（メートル）、向きはルートから見た基準姿勢からの差。
        /// 固定していない間は、今の骨の位置と向き
        /// </summary>
        public override bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            Vector3 world;
            Quaternion worldRotation;
            GetPoint (out world, out worldRotation);
            position = root.InverseTransformPoint (world);
            rotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation) * worldRotation * Quaternion.Inverse (rig_.GetRestRootRotation (bone_)));
            return true;
        }

        public override void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            if ((channels & TransformChannels.Position) != 0) PinPosition (root.TransformPoint (position));
            if ((channels & TransformChannels.Rotation) != 0) PinRotation (root.rotation * Quaternion.Normalize (rotation) * rig_.GetRestRootRotation (bone_));
        }

        /// <summary>
        /// 基準の値（基準姿勢の骨の位置・向きの差なし）へ戻す。固定の強さは変えない
        /// </summary>
        public override void ResetValues (bool pole, TransformChannels channels)
        {
            Vector3 position;
            Quaternion rotation;
            if (!body_.TryGetPointRestValues (human_, out position, out rotation)) return;
            if ((channels & TransformChannels.Position) != 0) {
                control_.localPosition = position;
                positionDirty_ = true;
            }
            if (hasRotation_ && (channels & TransformChannels.Rotation) != 0) {
                control_.localRotation = rotation;
                rotationDirty_ = true;
            }
        }

        /// <summary>
        /// 基準へ戻す（Reset）。点を基準姿勢の骨の上へ戻し、固定を外す
        /// </summary>
        public override void ResetPose ()
        {
            Vector3 position;
            Quaternion rotation;
            if (!body_.TryGetPointRestValues (human_, out position, out rotation)) return;
            control_.localPosition = position;
            control_.localRotation = rotation;
            rotationDirty_ = rotationPinned;
            positionDirty_ = positionPinned;
            lockedDirty_ = body_.GetLocked (human_);
            body_.SetPositionWeight (human_, 0);
            body_.SetRotationWeight (human_, 0);
            body_.SetLocked (human_, false);
        }
    }

}
