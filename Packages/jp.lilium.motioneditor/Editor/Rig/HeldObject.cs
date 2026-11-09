using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>物の姿勢を、どの骨から見た値で持つか</summary>
    public enum HeldObjectFrame
    {
        /// <summary>キャラのルート（体が動いても物は動かない）</summary>
        Root,
        /// <summary>腰</summary>
        Hips,
        /// <summary>胸（UpperChest。無ければ Chest・Spine）。両手持ちの物は胸に対してほぼ動かないので既定にする</summary>
        Chest,
    }

    /// <summary>
    /// 物の、付ける先ごとの決まった位置（S104）。リグ定義の付ける先（EditRigDefinition.AttachPoint）を名前で指す。
    /// 無い先にも付けられる（付けるボタンを押した瞬間の位置で付く）。キーの無いクリップ・窓の「寄せる」・CLI の useSettings でここを使う
    /// </summary>
    [System.Serializable]
    public sealed class HeldObjectAttachment
    {
        [Tooltip ("Name of the attach point in the rig definition")]
        public string point;
        [Tooltip ("Where the attach point's frame is, in the object's local space (meters)")]
        public Vector3 position;
        [Tooltip ("Attach frame orientation in the object's local space (Euler angles). Zero aligns the axes that were body right, up and forward in the T-pose with the object's X, Y and Z")]
        public Vector3 rotation;
        [Tooltip ("While the object is attached here, bake its curves relative to this attach point's bone instead of the parent bone (for example a sword sheathed on the hips). The game must keep the object on this bone while it is attached here, for example with the constraint below")]
        public bool bakeParent;
        [Tooltip ("Optional. Game rig constraint weight that is 1 while the object is attached here when baking, written as <Rig name>/<path below the rig>")]
        public string constraint;
    }

    /// <summary>
    /// 持ち物の上の全身 IK の点（S91e）。リグ定義の点と同じ物で、Controls/Body/物の名前.点の名前 に値とキーを持つ
    /// </summary>
    [System.Serializable]
    public sealed class HeldObjectPoint
    {
        [Tooltip ("Name. The point is called <object name>.<name>")]
        public string name;
        [Tooltip ("Where the point is, in the object's local space (meters)")]
        public Vector3 position;
        [Range (0, 1)]
        [Tooltip ("1: a heavy point; dragging it moves the object without turning it and pulls the body along. 0: a light point; dragging it turns the object around the heavy point and moves the arms only.")]
        public int weight;
        [Tooltip ("Whether the point also has a rotation (pinning it fixes the object's rotation)")]
        public bool rotation;
    }

    /// <summary>
    /// キャラの持ち物（S91）。prefab の中のパスを指せるので、キャラの設定（<see cref="CharacterSettings.heldObjects"/>）に置く
    /// </summary>
    [System.Serializable]
    public sealed class HeldObjectDefinition
    {
        [Tooltip ("Name. Becomes the control path (Controls/Object/name) and the object's GameObject name")]
        public string name;
        [Tooltip ("Prefab or model shown in the editor preview only. Empty: use Path")]
        public GameObject prefab;
        [Tooltip ("Path of an object that is already inside the character prefab, from the Animator's GameObject. Used when Prefab is empty")]
        public string path;
        [Tooltip ("Bone that parents the object in the hierarchy. Baked curves are local to this parent, so a hand keeps the object on that hand in the game at any playback speed. Last Bone parents it to the Animator's GameObject, but the game can then play the object away from the body, because the Animator aligns the clip's body orientation and position")]
        public HumanBodyBones parentBone = HumanBodyBones.RightHand;
        [Tooltip ("While the object is attached nowhere, it stays in the world: it does not follow the frame bone when the body moves, and its curves are baked relative to the Animator's GameObject (the clip's world) instead of the parent bone, for example a sword put down on the ground. The game must take the object off the parent bone while it is attached nowhere")]
        public bool detachedInWorld;
        [Tooltip ("Bone the object's keyed pose is relative to. Chest keeps the object with the body when the hips move")]
        public HeldObjectFrame frame = HeldObjectFrame.Chest;
        [Tooltip ("Position with no keys, from the frame bone along body right, up and forward (meters)")]
        public Vector3 restPosition = new Vector3 (0, -0.25f, 0.3f);
        [Tooltip ("Rotation with no keys, in body right, up and forward axes (Euler angles)")]
        public Vector3 restRotation = new Vector3 (35, 0, 0);
        [Tooltip ("Full-body IK points on the object. They work like the rig definition's points")]
        public List<HeldObjectPoint> points = new List<HeldObjectPoint> ();
        [Tooltip ("The handle axis, in the object's local space. Attach points with the Twist joint (hands) can turn freely around it")]
        public Vector3 gripAxis = Vector3.up;
        [Tooltip ("Where the object goes on each attach point of the rig definition (optional), how to bake it there, and the game constraint to bake. Whether it is attached there in clips with no keys is set on the rig definition's attach point")]
        public List<HeldObjectAttachment> attachments = new List<HeldObjectAttachment> ();

        /// <summary>この付ける先の決まった位置（無ければ null）</summary>
        public HeldObjectAttachment FindAttachment (string point)
        {
            return attachments != null ? attachments.Find (a => a != null && a.point == point) : null;
        }

        /// <summary>この付ける先の決まった位置。無ければ足す</summary>
        public HeldObjectAttachment GetOrAddAttachment (string point)
        {
            HeldObjectAttachment found = FindAttachment (point);
            if (found != null) return found;
            attachments ??= new List<HeldObjectAttachment> ();
            found = new HeldObjectAttachment { point = point };
            attachments.Add (found);
            return found;
        }

        /// <summary>FBX の空の名前。あれば右手・左手の位置の初期値にする</summary>
        public const string kRightGripName = "Grip_R";
        public const string kLeftGripName = "Grip_L";
        /// <summary>FBX の空の名前の頭。あれば点の初期値にする（Point_Tip なら点 Tip）</summary>
        public const string kPointPrefix = "Point_";
        /// <summary>重心の点の名前。重さ 1 で向きも持つ。ほかの点は重さ 0</summary>
        public const string kCenterPointName = "Center";
        const string kTipPointName = "Tip";
        const string kPommelPointName = "Pommel";

        /// <summary>
        /// 物のモデルから点を入れ直す。空（Point_名前）があればその場所、無ければメッシュの範囲の長い向きの両端と真ん中
        /// </summary>
        public void LoadPoints (GameObject model)
        {
            if (model == null) return;
            points.Clear ();
            foreach (Transform child in model.transform) {
                if (!child.name.StartsWith (kPointPrefix)) continue;
                AddPoint (child.name.Substring (kPointPrefix.Length), model.transform.InverseTransformPoint (child.position));
            }
            if (points.Count > 0) return;

            Bounds? bounds = null;
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter> (true)) {
                if (filter.sharedMesh == null) continue;
                Bounds mesh = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++) {
                    Vector3 corner = mesh.center + Vector3.Scale (mesh.extents, new Vector3 ((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1));
                    Vector3 local = model.transform.InverseTransformPoint (filter.transform.TransformPoint (corner));
                    if (bounds.HasValue) {
                        Bounds grown = bounds.Value;
                        grown.Encapsulate (local);
                        bounds = grown;
                    }
                    else bounds = new Bounds (local, Vector3.zero);
                }
            }
            if (!bounds.HasValue) return;
            Vector3 size = bounds.Value.size;
            Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            Vector3 half = Vector3.Scale (axis, bounds.Value.extents);
            AddPoint (kTipPointName, bounds.Value.center + half);
            AddPoint (kPommelPointName, bounds.Value.center - half);
            AddPoint (kCenterPointName, bounds.Value.center);
        }

        void AddPoint (string name, Vector3 position)
        {
            bool center = name == kCenterPointName;
            points.Add (new HeldObjectPoint { name = name, position = position, weight = center ? 1 : 0, rotation = center });
        }

        /// <summary>
        /// 物のモデルの空（Grip_R・Grip_L）から、右手・左手の付ける先（既定の定義の名前）の位置を入れる。空が無ければ変えない。
        /// キーの無いクリップで付けておくかは、リグ定義の付ける先が決める（AttachPoint.attachedAtRest）
        /// </summary>
        public void LoadGrips (GameObject model)
        {
            if (model == null) return;
            LoadGrip (model.transform, kRightGripName, EditRigDefinition.kRightHandAttach);
            LoadGrip (model.transform, kLeftGripName, EditRigDefinition.kLeftHandAttach);
        }

        void LoadGrip (Transform model, string name, string point)
        {
            Transform empty = model.Find (name);
            if (empty == null) return;
            HeldObjectAttachment attachment = GetOrAddAttachment (point);
            attachment.position = model.InverseTransformPoint (empty.position);
            attachment.rotation = (Quaternion.Inverse (model.rotation) * empty.rotation).eulerAngles;
        }

        /// <summary>定義の誤り。空なら使える</summary>
        public List<string> Validate ()
        {
            List<string> errors = new List<string> ();
            if (!RigPaths.IsValidName (name)) errors.Add (Tr ("HELD_OBJECT_INVALID_NAME", name));
            if (prefab == null && string.IsNullOrEmpty (path)) errors.Add (Tr ("HELD_OBJECT_NO_MODEL", name));
            if (points != null) {
                HashSet<string> names = new HashSet<string> ();
                foreach (HeldObjectPoint point in points) {
                    if (point == null || !RigPaths.IsValidName (point.name) || !names.Add (point.name)) errors.Add (Tr ("HELD_OBJECT_INVALID_POINT_NAME", name, point != null ? point.name : ""));
                }
            }
            if (attachments != null) {
                HashSet<string> names = new HashSet<string> ();
                foreach (HeldObjectAttachment attachment in attachments) {
                    if (attachment == null || !RigPaths.IsValidName (attachment.point) || !names.Add (attachment.point)) errors.Add (Tr ("HELD_OBJECT_INVALID_ATTACHMENT", name, attachment != null ? attachment.point : ""));
                }
            }
            return errors;
        }
    }

    /// <summary>
    /// 物を編集用の体に当てたもの（S91・S104）。物の姿勢をキーに持ち、付けている間は物の姿勢を全身 IK の見えない点の目標にする。
    ///
    /// 物の姿勢の値（＝編集用クリップに保存する値。Controls/Object/名前 の Transform）:
    /// - localRotation = 枠の骨の基準姿勢からの回りを戻した、ルートから見た物の向き
    /// - localPosition = 枠の骨から物までの向き（同じ回りを戻した） / humanScale
    /// 枠が Chest なら、腰や胸が動いても物は胸に付いて動く。
    ///
    /// 付ける先（S104）はリグ定義の一覧（EditRigDefinition.attachPoints）で、どの先も同じ決まりで解く（11-09 ユーザー指示「任意のボーンにアタッチする機能として汎用化する」）。
    /// 付けているかと付けた位置は、物 × 付ける先ごとの値（Controls/Object/物の名前/付ける先の名前 の HeldObjectControl）。
    ///
    /// 解き方は Prepare → 全身 IK → Finish の順（S102。11-07 ユーザー指示「持ったときに IK 構造を再構築する。いままで同じ IK 構造を解くだけでよくなる」）。
    /// Prepare は、付けている物を付ける先の骨の子として全身 IK の骨の組みに入れ（親は 1 つ。ほかの先は輪の関節）、物を骨の枠 × 付けた位置へ置く。
    /// 関節は付ける先の種類で決まる: Twist は柄の軸まわりにだけ回り、Fixed は回らない。
    /// 物を動かせば物の姿勢の点がつかんだ点になって、付けた骨（手でも腰でも）を引く。Finish は、解いた物の骨から物の姿勢を読む。
    ///
    /// 物の上の点（S91e）はふつうの全身 IK の点で、状態・キー・選び方・ハンドルは変わらない。付けている間は物の骨の上の点として解き、重さの意味も体の点と同じ。
    /// 付けていない物は骨の組みに入れず、ここで物の点から物の姿勢を決める（FitToPoints）
    /// </summary>
    public sealed class HeldObjectRig
    {
        /// <summary>物 1 つと、付ける先 1 つの組（S104）</summary>
        public sealed class Attachment
        {
            internal Entry entry;
            /// <summary>リグ定義の付ける先</summary>
            public EditRigDefinition.AttachPoint point;
            /// <summary>物の設定の、この先の決まった位置（無ければ null）</summary>
            public HeldObjectAttachment settings;
            /// <summary>付けているか・付けた位置の値（Controls/Object/物の名前/付ける先の名前）</summary>
            public HeldObjectControl values;
            /// <summary>付ける骨（編集用の体）</summary>
            public Transform bone;
            internal Quaternion boneRest = Quaternion.identity;
            /// <summary>骨の基準姿勢での、骨の付け根から枠までの差（ルート空間。メートル）</summary>
            internal Vector3 frameOffset;
            /// <summary>骨の基準姿勢での枠の向き（ルート空間）</summary>
            internal Quaternion frameRotation = Quaternion.identity;
            /// <summary>物の設定の位置（物のローカル。リグエディタで動かす間は設定より先に変わる）</summary>
            internal Vector3 gripPosition;
            internal Quaternion gripRotation = Quaternion.identity;
            /// <summary>キーの無いクリップでの、付けているかの値</summary>
            internal float rest;
            /// <summary>このクリップにキーの位置（S99）のカーブがあるか（あれば、どのキーにも打つ）</summary>
            internal bool gripCurved;
            internal RigProxies.Weight constraint;

            /// <summary>柄の軸まわりの回り（ルート空間。Finish が解いた骨から決める。Twist の先で付けている間だけ使う）</summary>
            internal Quaternion twist = Quaternion.identity;
            /// <summary>
            /// 前に解いた姿勢での、枠から見た物の柄の軸まわりの回り（物のローカル。枠に合わせた物の向き⁻¹ × 物の向き）。
            /// Finish が解いた骨と物から決める。hasRoll が false なら、まだ解いていない
            /// </summary>
            internal Quaternion roll = Quaternion.identity;
            internal bool hasRoll;

            public string name
            {
                get { return point.name; }
            }

            public Entry owner
            {
                get { return entry; }
            }

            internal Transform control
            {
                get { return values.transform; }
            }

            /// <summary>今のフレームで付けているか</summary>
            public bool attached
            {
                get { return values.attached >= 0.5f; }
            }

            /// <summary>柄の軸まわりに回る先か（手）。違えば回らない（腰・背中）</summary>
            public bool twists
            {
                get { return point.joint == EditRigDefinition.AttachJoint.Twist; }
            }

            /// <summary>物の設定に決まった位置があるか（窓の「寄せる」）</summary>
            public bool hasSettings
            {
                get { return settings != null; }
            }

            /// <summary>今の位置が、キーで付けた位置（付けるボタンを押した瞬間の位置。S99）か。違えば物の設定の位置（gripPosition・gripRotation）</summary>
            public bool gripKeyed
            {
                get { return values.gripKeyed >= 0.5f; }
            }

            /// <summary>今の位置の場所（物のローカル）</summary>
            internal Vector3 heldGripPosition
            {
                get { return gripKeyed ? values.gripPosition : gripPosition; }
            }

            /// <summary>今の位置の向き（物のローカル）</summary>
            internal Quaternion heldGripRotation
            {
                get { return gripKeyed ? Quaternion.Euler (values.gripRotation) : gripRotation; }
            }

            /// <summary>キーの位置にする（keyed が false なら物の設定の位置へ戻す）</summary>
            internal void SetKeyedGrip (bool keyed, Vector3 position, Vector3 rotation)
            {
                values.gripKeyed = keyed ? 1 : 0;
                values.gripPosition = position;
                values.gripRotation = rotation;
            }

            /// <summary>柄の軸（物のローカル）。回らない先は 0</summary>
            internal Vector3 axis
            {
                get { return twists ? entry.definition.gripAxis : Vector3.zero; }
            }
        }

        /// <summary>物の上の全身 IK の点 1 つ</summary>
        public sealed class ObjectPoint
        {
            internal Entry entry;
            public HeldObjectPoint definition;
            public BodyPointId id;
            internal Transform control;
            /// <summary>物のローカルの場所（メートル。リグエディタで動かす間は定義より先に変わる）</summary>
            internal Vector3 offset;
            internal bool heavy;
        }

        /// <summary>物 1 つ</summary>
        public sealed class Entry
        {
            internal HeldObjectRig rig;
            public HeldObjectDefinition definition;
            public Transform control;
            /// <summary>編集用の体の中の物</summary>
            public Transform editing;
            /// <summary>表示モデルの物</summary>
            public Transform display;
            internal Transform frameBone;
            internal Quaternion frameRest = Quaternion.identity;
            /// <summary>付ける先（リグ定義の並び。キャラに骨の無い先は入らない）</summary>
            public readonly List<Attachment> attachments = new List<Attachment> ();
            /// <summary>物の上の全身 IK の点（S91e）</summary>
            public readonly List<ObjectPoint> points = new List<ObjectPoint> ();
            /// <summary>決まった位置へ寄せた（SnapTo）とき、付けている先に届かなかった差（メートル）。届かない分は、付けている骨か物が跳ぶ</summary>
            public float snapGap;
            /// <summary>寄せている間か（解いた後に snapGap を測る）</summary>
            internal bool snapping;
            /// <summary>寄せている先（snapping の間だけ）</summary>
            internal Attachment snapTarget;
            /// <summary>つかんで動かしている間の、物の姿勢（ルート空間）</summary>
            internal bool dragging;
            /// <summary>動かしている間、全身を引くか（重い点をつかんだ・物そのものを動かした。軽い点では引かない）</summary>
            internal bool pulling;
            /// <summary>物そのものを動かしているか（物の FK・CLI）。置いた姿勢が優先で、Pin の点には戻らない</summary>
            internal bool direct;
            internal Vector3 dragPosition;
            internal Quaternion dragRotation;
            /// <summary>今の物の姿勢（ルート空間。Finish が決める）</summary>
            internal Vector3 position;
            internal Quaternion rotation = Quaternion.identity;
            /// <summary>今の姿勢を物の上の点で決めたか（Prepare から Finish の間だけ）</summary>
            internal bool fitted;
            /// <summary>
            /// 物の姿勢の点（S102）のコントロールと名前と値。付けている間、物の骨の原点を物の姿勢へ寄せる全身 IK の点（点の一覧には出ない）。
            /// 状態（Free / Pin / Lock）は体の点と同じで、持ち物の対象（HeldObjectTarget）で切り替え、キーを打つ（11-07 ユーザー指示「持ち物 Stick も制御点に」）
            /// </summary>
            internal Transform poseControl;
            internal BodyPointId poseId;
            internal BodyPoint poseValues;

            /// <summary>物の姿勢の点の状態（体の点と同じ。位置か向きの強さが入っていれば Pin、さらに Lock の値が入っていれば Lock）</summary>
            public BodyPointState poseState
            {
                get {
                    bool pinned = poseValues.positionWeight > 0 || poseValues.rotationWeight > 0;
                    return !pinned ? BodyPointState.Free : poseValues.locked >= 0.5f ? BodyPointState.Locked : BodyPointState.Pin;
                }
            }

            /// <summary>
            /// 物の姿勢の点の状態を変える（体の点の SetState と同じ: Free は強さも Lock も外す。Free から Pin / Lock にするときは位置だけ固定する。
            /// 向きは物を回したときに固定する）。キーは次に物のキーを打つときに打つ
            /// </summary>
            public void SetPoseState (BodyPointState state)
            {
                if (state == BodyPointState.Free) {
                    poseValues.positionWeight = 0;
                    poseValues.rotationWeight = 0;
                    poseValues.locked = 0;
                }
                else {
                    if (poseState == BodyPointState.Free) poseValues.positionWeight = 1;
                    poseValues.locked = state == BodyPointState.Locked ? 1 : 0;
                }
                keysPending = true;
            }
            /// <summary>今の解き方で、物を骨の組み（付ける先の骨の子）に入れたか（Prepare から Finish の間だけ）</summary>
            internal bool attached;
            /// <summary>動かしたがまだキーを打っていないか</summary>
            public bool keysPending;

            public string name
            {
                get { return definition.name; }
            }

            /// <summary>付ける先を名前で引く（無ければ null）</summary>
            public Attachment FindAttachment (string point)
            {
                return attachments.Find (a => a.name == point);
            }

            /// <summary>今のフレームで、どこかに付けているか</summary>
            public bool anyAttached
            {
                get { return attachments.Exists (a => a.attached); }
            }

            /// <summary>
            /// 今、ワールドに留めているか（物の設定で detachedInWorld にして、どこにも付けていない）。物の姿勢の値は、枠の骨から見た値でなく、
            /// 基準姿勢の枠の骨の場所から見た値（ルートから見て動かない。キーの無いクリップの基準の値も同じ所に出る）
            /// </summary>
            public bool inWorld
            {
                get { return definition.detachedInWorld && !anyAttached; }
            }

            /// <summary>
            /// キーの間で、前後の物のキーの基準（ワールドか枠の骨）が違うか（ApplyAttachKeys が決める。付け外しをまたぐ区間）。
            /// その間の物の姿勢は、2 つのキーをそれぞれの基準で置いてから補間する
            /// </summary>
            internal bool spanMixed;
            internal bool spanFromWorld;
            internal Vector3 spanFromPosition;
            internal Quaternion spanFromRotation = Quaternion.identity;
            internal Vector3 spanToPosition;
            internal Quaternion spanToRotation = Quaternion.identity;
            internal float spanWeight;

            /// <summary>
            /// 物の設定の決まった位置で target に付く所へ、物を動かす（まだ付けない。付けている骨は付いてくる）。全身は引かない。
            /// 寄せる所は、呼んだときの target の骨（解いた姿勢）で決める。物の方が寄り、target の骨は動かない。
            /// 寄せている間は、物を target の骨に回らない関節で付けて解く（物は寄せた所からずれない。届かない骨は届かないまま。S91f の差すときの運び方と同じ）。
            /// 付けている骨がここまで来てから付け替えれば、前のキーの間で骨が物に付いたまま動き、付け替えのフレームで跳ばない
            /// </summary>
            public void SnapTo (Attachment target)
            {
                rig.GetSettingsPose (target, out Vector3 position, out Quaternion rotation);
                PlaceWithoutPulling (position, rotation);
                snapping = true;
                snapTarget = target;
            }

            /// <summary>どこにも付けていない値にする（姿勢は動かさない）</summary>
            public void DetachAll ()
            {
                foreach (Attachment item in attachments) item.values.attached = 0;
                ApplyConstraintWeights (this);
                // ワールドに留める物は、値の基準が変わるので今の姿勢を入れ直す（入れ直さないと、枠から見た値をワールドから見た値として読んで跳ぶ）
                if (definition.detachedInWorld) rig.StoreValues (this);
                keysPending = true;
            }

            void PlaceWithoutPulling (Vector3 position, Quaternion rotation)
            {
                dragging = true;
                pulling = false;
                direct = true;
                dragPosition = position;
                dragRotation = rotation;
                keysPending = true;
            }

            /// <summary>
            /// 物のワールドの姿勢を置いて、つかんだ状態にする。解くまで骨は動かない。
            /// 体の点と同じく、動かした分だけ固定する: 場所を変えたら物の姿勢の点の位置の強さを、向きを変えたら向きの強さを 1 にする（Pin）。
            /// 場所だけ動かしたときに向きまで固定すると、手首を Pin にして物を動かしても手首の角度が変わらなかった（11-07 ユーザー報告「通常の IK 制御では回転するはず」）
            /// </summary>
            public void DragTo (Vector3 worldPosition, Quaternion worldRotation)
            {
                Transform root = rig.rig_.root.transform;
                GetWorldPose (out Vector3 currentPosition, out Quaternion currentRotation);
                if (Vector3.Distance (currentPosition, worldPosition) > 1e-6f) poseValues.positionWeight = 1;
                if (Quaternion.Angle (currentRotation, worldRotation) > 1e-3f) poseValues.rotationWeight = 1;
                dragging = true;
                pulling = true;
                direct = true;
                dragPosition = root.InverseTransformPoint (worldPosition);
                dragRotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation) * worldRotation);
                keysPending = true;
            }

            /// <summary>物の今のワールドの姿勢</summary>
            public void GetWorldPose (out Vector3 worldPosition, out Quaternion worldRotation)
            {
                rig.GetPose (this, out Vector3 local, out Quaternion rotated);
                Transform root = rig.rig_.root.transform;
                worldPosition = root.TransformPoint (local);
                worldRotation = root.rotation * rotated;
            }

            /// <summary>ワールドの pivot を中心に、物を delta（ワールド）だけ回す</summary>
            public void TurnAbout (Vector3 pivot, Quaternion delta)
            {
                GetWorldPose (out Vector3 position, out Quaternion rotation);
                DragTo (pivot + delta * (position - pivot), delta * rotation);
            }
        }

        readonly EditingRig rig_;
        readonly List<Entry> entries_ = new List<Entry> ();
        readonly Quaternion body_;

        public IReadOnlyList<Entry> entries
        {
            get { return entries_; }
        }

        /// <summary>動かしてまだキーを打っていない物があるか</summary>
        public bool hasPendingKeys
        {
            get { return entries_.Exists (e => e.keysPending); }
        }

        public void ClearPendingKeys ()
        {
            foreach (Entry entry in entries_) entry.keysPending = false;
        }

        /// <summary>
        /// 表示モデルに物を置く（EditingRig を作る前。骨と一緒に複製させる）。置いた物を定義と同じ順で返す。置けない物は null で、理由を errors に足す
        /// </summary>
        /// <param name="instantiate">prefab を複製する関数（見た目以外のコンポーネントを剥がして返す）</param>
        public static List<Transform> Place (Animator animator, IReadOnlyList<HeldObjectDefinition> definitions, System.Func<GameObject, GameObject> instantiate, List<string> errors)
        {
            List<Transform> placed = new List<Transform> ();
            HashSet<string> names = new HashSet<string> ();
            foreach (HeldObjectDefinition definition in definitions) {
                Transform display = null;
                List<string> problems = definition != null ? definition.Validate () : new List<string> ();
                if (definition != null && !names.Add (definition.name)) problems.Add (Tr ("HELD_OBJECT_DUPLICATE_NAME", definition.name));
                if (definition != null && problems.Count == 0) {
                    Transform parent = definition.parentBone == HumanBodyBones.LastBone ? animator.transform : animator.isHuman ? animator.GetBoneTransform (definition.parentBone) : null;
                    if (parent == null) problems.Add (Tr ("HELD_OBJECT_NO_PARENT_BONE", definition.name, definition.parentBone));
                    else if (definition.prefab != null) {
                        GameObject instance = instantiate (definition.prefab);
                        instance.name = definition.name;
                        instance.transform.SetParent (parent, false);
                        instance.transform.SetLocalPositionAndRotation (Vector3.zero, Quaternion.identity);
                        display = instance.transform;
                    }
                    else {
                        display = animator.transform.Find (definition.path);
                        if (display == null) problems.Add (Tr ("HELD_OBJECT_PATH_NOT_FOUND", definition.name, definition.path));
                    }
                }
                errors.AddRange (problems);
                placed.Add (display);
            }
            return placed;
        }

        /// <param name="placed">Place が返した物（定義と同じ順）</param>
        public static HeldObjectRig Create (EditingRig rig, IReadOnlyList<HeldObjectDefinition> definitions, IReadOnlyList<Transform> placed, System.Func<string, GameObject> createGameObject)
        {
            HeldObjectRig result = new HeldObjectRig (rig);
            for (int i = 0; i < definitions.Count && i < placed.Count; i++) {
                if (placed[i] == null) continue;
                result.Add (definitions[i], placed[i], createGameObject);
            }
            return result.entries_.Count > 0 ? result : null;
        }

        HeldObjectRig (EditingRig rig)
        {
            rig_ = rig;
            body_ = Quaternion.LookRotation (rig.bodyForward, rig.bodyUp);
        }

        void Add (HeldObjectDefinition definition, Transform display, System.Func<string, GameObject> createGameObject)
        {
            Transform editing = rig_.GetEditingBone (display);
            if (editing == null) return;
            Transform control = rig_.CreateControl (RigPaths.Object (definition.name), createGameObject);
            Transform poseControl = rig_.CreateControl (RigPaths.ObjectPose (definition.name), createGameObject);
            // キーの無いクリップでは位置だけの Pin（届く間は物の場所に留まり、引かれれば付いて動く。向きは体の点と同じく、回したときに固定する。
            // 11-07 ユーザー決定。向きまで固定すると、物を動かしても手首の角度が変わらなかった）
            BodyPoint poseValues = poseControl.gameObject.AddComponent<BodyPoint> ();
            poseValues.positionWeight = 1;
            Entry entry = new Entry {
                poseControl = poseControl,
                poseValues = poseValues,
                poseId = new BodyPointId (RigPaths.ObjectPose (definition.name)),
                rig = this,
                definition = definition,
                control = control,
                editing = editing,
                display = display,
            };
            AddAttachments (entry, createGameObject);
            entry.frameBone = FindFrameBone (definition.frame);
            if (entry.frameBone != null) entry.frameRest = rig_.GetRestRootRotation (entry.frameBone);
            // 基準の値（キーの無いクリップで物を置く所）。枠の骨の付け根から、体の向きで書いた所
            control.localPosition = rig_.BodyToRoot (definition.restPosition) / rig_.humanScale;
            control.localRotation = body_ * Quaternion.Euler (definition.restRotation);
            BindConstraints (entry);
            entries_.Add (entry);
            rig_.AddBakedBone (display, fromDisplay => BakeParent (entry, fromDisplay));
            AddPoints (entry, createGameObject);
        }

        /// <summary>
        /// 焼く値をどの骨から見た値にするか: 付けている先のうち、物の設定で bakeParent にした先の骨（腰に差している剣なら腰。11-09 ユーザー指示
        /// 「腰にアタッチした際は、hips を親ボーンにする」）。いくつもあれば、物を子にする先と同じ決まり（回らない先が先、同じ種類ならリグ定義の並び）。
        /// どこにも付けていないときは、物の設定で detachedInWorld にしていればワールド（Animator の GameObject。クリップの中のワールド。11-09 ユーザー指示
        /// 「どれにもアタッチしてない場合はワールドに」）。それ以外は null（親の骨から見た値）
        /// </summary>
        Transform BakeParent (Entry entry, bool fromDisplay)
        {
            Attachment found = null;
            foreach (Attachment attachment in entry.attachments) {
                if (attachment.settings == null || !attachment.settings.bakeParent || !attachment.attached) continue;
                if (found == null || (!attachment.twists && found.twists)) found = attachment;
            }
            if (found != null) return fromDisplay ? rig_.GetDisplayBone (found.bone) : found.bone;
            if (entry.inWorld) return fromDisplay ? rig_.displayAnimator.transform : rig_.root.transform;
            return null;
        }

        /// <summary>リグ定義の付ける先ごとに、値のコントロールを作る（キャラに骨の無い先は知らせて飛ばす）</summary>
        void AddAttachments (Entry entry, System.Func<string, GameObject> createGameObject)
        {
            HashSet<string> names = new HashSet<string> ();
            foreach (EditRigDefinition.AttachPoint point in rig_.binding.attachPoints) {
                if (!RigPaths.IsValidName (point.name) || !names.Add (point.name)) continue;
                Transform bone = FindAttachBone (point);
                if (bone == null) {
                    rig_.binding.notes.Add (Tr ("HELD_OBJECT_NO_ATTACH_BONE", entry.name, point.name));
                    continue;
                }
                HeldObjectAttachment settings = entry.definition.FindAttachment (point.name);
                Transform control = rig_.CreateControl (RigPaths.ObjectAttach (entry.name, point.name), createGameObject);
                Attachment attachment = new Attachment {
                    entry = entry,
                    point = point,
                    settings = settings,
                    values = control.gameObject.AddComponent<HeldObjectControl> (),
                    bone = bone,
                    boneRest = rig_.GetRestRootRotation (bone),
                    frameOffset = rig_.BodyToRoot (point.position) * rig_.humanScale,
                    frameRotation = body_ * Quaternion.Euler (point.rotation),
                    gripPosition = settings != null ? settings.position : Vector3.zero,
                    gripRotation = settings != null ? Quaternion.Euler (settings.rotation) : Quaternion.identity,
                    // キーの無いクリップで付けておくのは、リグ定義でそうした先のうち、物の決まった位置がある先（無い先は付ける位置が決まらない）
                    rest = point.attachedAtRest && settings != null ? 1 : 0,
                };
                attachment.values.attached = attachment.rest;
                entry.attachments.Add (attachment);
            }
        }

        /// <summary>付ける先の骨（追加コントロールの名前があればその骨。編集用の体）</summary>
        Transform FindAttachBone (EditRigDefinition.AttachPoint point)
        {
            if (!string.IsNullOrEmpty (point.control)) {
                RigBinding.Extra extra = rig_.binding.extras.Find (e => e.control.name == point.control);
                return extra != null && extra.bone != null ? rig_.GetEditingBone (extra.bone) : null;
            }
            return point.bone == HumanBodyBones.LastBone ? null : rig_.GetEditingBone (point.bone);
        }

        /// <summary>
        /// 物の上の点を、リグ定義の点と同じ一覧（RigBinding.points）へ足す。全身 IK・対象・キー・CLI は、この一覧からほかの点と同じに作る
        /// </summary>
        void AddPoints (Entry entry, System.Func<string, GameObject> createGameObject)
        {
            if (entry.definition.points == null) return;
            Vector3 origin = GetRestPosition (entry);
            Quaternion rotation = body_ * Quaternion.Euler (entry.definition.restRotation);
            string center = null;
            foreach (HeldObjectPoint source in entry.definition.points) {
                if (source.weight > 0 && center == null) center = PointName (entry.definition, source);
            }
            foreach (HeldObjectPoint source in entry.definition.points) {
                string name = PointName (entry.definition, source);
                Vector3 rest = origin + rotation * source.position;
                RigBinding.Point point = new RigBinding.Point {
                    definition = new EditRigDefinition.BodyPoint {
                        name = name,
                        bone = HumanBodyBones.LastBone,
                        rotation = source.rotation,
                        weight = source.weight,
                        // 軽い点は重い点へ線でつなぐ
                        link = source.weight > 0 ? null : center,
                    },
                    name = name,
                    path = RigPaths.BodyPoint (name),
                    bone = entry.display,
                    humanBone = HumanBodyBones.LastBone,
                    hasRotation = source.rotation,
                    onObject = true,
                    objectOffset = source.position,
                    objectRest = rest,
                };
                rig_.binding.points.Add (point);
                Transform control = rig_.CreateControl (point.path, createGameObject);
                control.gameObject.AddComponent<BodyPoint> ();
                control.localPosition = rest / rig_.humanScale;
                entry.points.Add (new ObjectPoint {
                    entry = entry,
                    definition = source,
                    id = new BodyPointId (name),
                    control = control,
                    offset = source.position,
                    heavy = source.weight > 0,
                });
            }
        }

        static string PointName (HeldObjectDefinition definition, HeldObjectPoint point)
        {
            return definition.name + "." + point.name;
        }

        Transform FindFrameBone (HeldObjectFrame frame)
        {
            switch (frame) {
                case HeldObjectFrame.Hips: return rig_.GetEditingBone (HumanBodyBones.Hips);
                case HeldObjectFrame.Chest: return rig_.GetEditingBone (HumanBodyBones.UpperChest) ?? rig_.GetEditingBone (HumanBodyBones.Chest) ?? rig_.GetEditingBone (HumanBodyBones.Spine);
                default: return null;
            }
        }

        /// <summary>全身 IK へ物の姿勢の点を足す（全身 IK を作った後）</summary>
        public void BindBody (FullBodyRig body)
        {
            if (body == null) return;
            foreach (Entry entry in entries_) body.AddHeldPosePoint (entry.poseId, entry.poseControl, entry.editing);
        }

        /// <summary>点が乗っている物（物の上の点でなければ null）</summary>
        public Entry FindPointOwner (BodyPointId id)
        {
            foreach (Entry entry in entries_) {
                foreach (ObjectPoint point in entry.points) {
                    if (point.id.Equals (id)) return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// ゲームの拘束の重みへつなぐ（S91c）。付けているかの値を拘束の重みへ写し、焼くとゲームのパスへ行く。
        /// 手で打った重みより優先する（物が骨から外れないようにする拘束なので、付けている間だけ効かせる）
        /// </summary>
        void BindConstraints (Entry entry)
        {
            foreach (Attachment attachment in entry.attachments) attachment.constraint = FindWeight (attachment.settings != null ? attachment.settings.constraint : null);
            ApplyConstraintWeights (entry);
        }

        RigProxies.Weight FindWeight (string constraint)
        {
            if (string.IsNullOrEmpty (constraint) || rig_.rigProxies == null) return null;
            Transform owner = rig_.FindControl (RigPaths.kGame + "/" + constraint);
            if (owner != null) {
                foreach (RigProxies.Weight weight in rig_.rigProxies.weights) {
                    if (weight.owner == owner && typeof (RigConstraintProxy).IsAssignableFrom (weight.proxyType)) return weight;
                }
            }
            rig_.binding.notes.Add (Tr ("HELD_OBJECT_CONSTRAINT_NOT_FOUND", constraint));
            return null;
        }

        /// <summary>付けているかの値を、ゲームの拘束の重みへ写す</summary>
        internal static void ApplyConstraintWeights (Entry entry)
        {
            foreach (Attachment attachment in entry.attachments) {
                if (attachment.constraint != null) attachment.constraint.value = attachment.values.attached;
            }
        }

        /// <summary>付けているかの値と付けた位置を基準へ戻す</summary>
        public void ResetAttachments ()
        {
            foreach (Entry entry in entries_) {
                foreach (Attachment attachment in entry.attachments) {
                    attachment.values.attached = attachment.rest;
                    attachment.SetKeyedGrip (false, Vector3.zero, Vector3.zero);
                }
                entry.spanMixed = false;
                ApplyConstraintWeights (entry);
            }
        }

        /// <summary>
        /// 付けているかの値を、その時刻以前でいちばん近いキーの値にする（キーの間で補間しない。物のキーがどれも無ければ基準の値）。
        /// 付ける先を足す前に打った物のキーにはその先のカーブが無いので、そこでは付けていない: その先のカーブが物のキー（物の姿勢のカーブはどのキーにもある）より
        /// 後から始まるなら、カーブの無い物のキー（その先のカーブのキーより後ろの物のキーも）では付けていない
        /// </summary>
        public void ApplyAttachKeys (AnimationClip clip, float time)
        {
            if (clip == null) return;
            foreach (Entry entry in entries_) {
                foreach (Attachment attachment in entry.attachments) {
                    attachment.values.attached = AttachedValue (clip, attachment, time);
                    ApplyGripKeys (clip, attachment, time);
                }
                ApplyConstraintWeights (entry);
                ApplyWorldSpan (clip, entry, time);
            }
        }

        /// <summary>その時刻の、付けているかの値（ApplyAttachKeys の決まり）</summary>
        static float AttachedValue (AnimationClip clip, Attachment attachment, float time)
        {
            Entry entry = attachment.entry;
            float margin = 0.5f / clip.frameRate;
            bool keyed = TryGetObjectKey (clip, entry, time, out Keyframe last);
            bool anyObjectKey = EditingClip.TryGetNextStepKey (clip, RigPaths.Object (entry.name), typeof (Transform), kObjectKeyProperty, -1, out Keyframe firstObject);
            string path = RigPaths.ObjectAttach (entry.name, attachment.name);
            bool found = EditingClip.TryGetStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty, time, out Keyframe key);
            // 物のキーより後から足した先か（先頭の物のキーにその先のキーが無い）
            bool addedLater = anyObjectKey && EditingClip.TryGetNextStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty, -1, out Keyframe first)
                && first.time > firstObject.time + margin;
            if (found && (!addedLater || !keyed || key.time >= last.time - margin)) return key.value;
            return keyed ? 0 : attachment.rest;
        }

        /// <summary>その時刻に、どこかに付けているか</summary>
        static bool AnyAttachedAt (AnimationClip clip, Entry entry, float time)
        {
            foreach (Attachment attachment in entry.attachments) {
                if (AttachedValue (clip, attachment, time) >= 0.5f) return true;
            }
            return false;
        }

        /// <summary>
        /// どこにも付けていない間をワールドに留める物（detachedInWorld）で、前後の物のキーの片方だけがどこにも付けていないキーなら、
        /// キーの間は 2 つのキーをそれぞれの基準（ワールドか枠の骨）で置いた姿勢から補間する（GetPose）。
        /// キーの値は基準ごとに違う所から見た値なので、カーブのまま補間すると、外す・付ける前のキーの間で物が別の所を通る
        /// </summary>
        static void ApplyWorldSpan (AnimationClip clip, Entry entry, float time)
        {
            entry.spanMixed = false;
            if (!entry.definition.detachedInWorld) return;
            string path = RigPaths.Object (entry.name);
            if (!TryGetObjectKey (clip, entry, time, out Keyframe from)) return;
            if (!EditingClip.TryGetNextStepKey (clip, path, typeof (Transform), kObjectKeyProperty, time, out Keyframe to) || to.time <= from.time) return;
            bool fromWorld = !AnyAttachedAt (clip, entry, from.time);
            bool toWorld = !AnyAttachedAt (clip, entry, to.time);
            if (fromWorld == toWorld) return;
            entry.spanMixed = true;
            entry.spanFromWorld = fromWorld;
            ReadObjectKey (clip, path, from.time, out entry.spanFromPosition, out entry.spanFromRotation);
            ReadObjectKey (clip, path, to.time, out entry.spanToPosition, out entry.spanToRotation);
            entry.spanWeight = SpanWeight (clip, path, from.time, to.time, time);
        }

        /// <summary>物の姿勢のキーの値（キーの時刻ちょうど。コントロールのローカルの値）</summary>
        static void ReadObjectKey (AnimationClip clip, string path, float time, out Vector3 position, out Quaternion rotation)
        {
            System.Func<string, float> read = name => EditingClip.GetStepValue (clip, path, typeof (Transform), name, time, 0);
            position = new Vector3 (read (CurveEdit.kPosition + ".x"), read (CurveEdit.kPosition + ".y"), read (CurveEdit.kPosition + ".z"));
            Quaternion value = new Quaternion (read (CurveEdit.kRotation + ".x"), read (CurveEdit.kRotation + ".y"), read (CurveEdit.kRotation + ".z"), read (CurveEdit.kRotation + ".w"));
            rotation = value.x == 0 && value.y == 0 && value.z == 0 && value.w == 0 ? Quaternion.identity : Quaternion.Normalize (value);
        }

        /// <summary>物の姿勢のカーブ（物のキーの印。どのキーにもある）の、time 以前で一番近いキー</summary>
        static bool TryGetObjectKey (AnimationClip clip, Entry entry, float time, out Keyframe key)
        {
            return EditingClip.TryGetStepKey (clip, RigPaths.Object (entry.name), typeof (Transform), kObjectKeyProperty, time, out key);
        }

        const string kObjectKeyProperty = "m_LocalPosition.x";

        /// <summary>
        /// キーで付けた位置（S99）を当てる。値は、その時刻以前でいちばん近いキーの値（キーが無ければ物の設定の位置）。
        /// 前後のキーでこの先に付けていれば、キーの間は前のキーの位置から次のキーの位置へ補間する（手が柄の上を滑る）。
        /// 補間しないと、キーの間は前の位置のまま物だけが次のキーの姿勢へ動くので、骨が物に引かれ、次のキーで位置が変わって骨が跳んだ
        /// （11-08 ユーザー報告「棒の持ち位置をずらした状態で 0 と 30 にキーを打ったら、再生すると動きが変」。腕のデバッグ用キャラで手首が 9cm 流れて戻った）。
        /// 付け替えた（前後のキーで付けているかが違う）区間は補間しない
        /// </summary>
        static void ApplyGripKeys (AnimationClip clip, Attachment attachment, float time)
        {
            string path = RigPaths.ObjectAttach (attachment.entry.name, attachment.name);
            attachment.gripCurved = EditingClip.TryGetStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kGripKeyedProperty, float.PositiveInfinity, out Keyframe _);
            if (!attachment.gripCurved) {
                attachment.SetKeyedGrip (false, Vector3.zero, Vector3.zero);
                return;
            }
            ReadGrip (clip, attachment, path, time, out bool gripKeyed, out Vector3 position, out Quaternion rotation);
            if (EditingClip.TryGetStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty, time, out Keyframe from) && from.value >= 0.5f
                && EditingClip.TryGetNextStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty, time, out Keyframe to) && to.value >= 0.5f) {
                ReadGrip (clip, attachment, path, to.time, out bool nextKeyed, out Vector3 nextPosition, out Quaternion nextRotation);
                float weight = SpanWeight (clip, RigPaths.Object (attachment.entry.name), from.time, to.time, time);
                // キーのフレームちょうど（weight 0）は、そのキーの位置のまま
                if ((gripKeyed || nextKeyed) && weight > 0) {
                    position = Vector3.Lerp (position, nextPosition, weight);
                    rotation = Quaternion.Slerp (rotation, nextRotation, weight);
                    gripKeyed = true;
                }
            }
            attachment.SetKeyedGrip (gripKeyed, position, rotation.eulerAngles);
        }

        /// <summary>その時刻の位置（キーで付けた位置か、物の設定の位置。物のローカル）</summary>
        static void ReadGrip (AnimationClip clip, Attachment attachment, string path, float time, out bool keyed, out Vector3 position, out Quaternion rotation)
        {
            keyed = EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), HeldObjectControl.kGripKeyedProperty, time, 0) >= 0.5f;
            if (!keyed) {
                position = attachment.gripPosition;
                rotation = attachment.gripRotation;
                return;
            }
            System.Func<string, Vector3> read = name => new Vector3 (
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".x", time, 0),
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".y", time, 0),
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".z", time, 0));
            position = read (HeldObjectControl.kGripPositionProperty);
            rotation = Quaternion.Euler (read (HeldObjectControl.kGripRotationProperty));
        }

        /// <summary>
        /// キーの間（from〜to 秒）の time での、前のキーから次のキーへの進み（0〜1）。物の場所のカーブと同じ補間にする
        /// （止める区間は 0、直線は等速、曲線は緩急を付ける）
        /// </summary>
        static float SpanWeight (AnimationClip clip, string path, float from, float to, float time)
        {
            float rate = Mathf.InverseLerp (from, to, time);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), kObjectKeyProperty));
            int index = curve != null ? System.Array.FindLastIndex (curve.keys, k => k.time <= from + 0.01f / clip.frameRate) : -1;
            KeyInterpolation? mode = KeyInterpolations.Get (curve, index);
            if (mode == KeyInterpolation.Constant) return 0;
            return mode == KeyInterpolation.Linear ? rate : Mathf.SmoothStep (0, 1, rate);
        }

        /// <summary>time より後ろに物のキーがあるか（付け替えはそのキーまでしか続かない）</summary>
        public static bool HasLaterKey (AnimationClip clip, Entry entry, float time)
        {
            return TryGetObjectKey (clip, entry, float.PositiveInfinity, out Keyframe last) && last.time > time + 0.5f / clip.frameRate;
        }

        /// <summary>つかんで動かすのをやめる（クリップから姿勢を当て直したとき）</summary>
        public void EndDrag ()
        {
            foreach (Entry entry in entries_) {
                entry.dragging = false;
                entry.pulling = false;
                entry.direct = false;
                entry.snapping = false;
                entry.snapTarget = null;
                entry.snapGap = 0;
            }
        }

        /// <summary>付ける先の枠の今の姿勢（ルート空間）。骨の基準姿勢からの動きに乗せる</summary>
        internal void GetAttachFrame (Attachment attachment, out Vector3 position, out Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            Vector3 origin = root.InverseTransformPoint (attachment.bone.position);
            Quaternion turn = Quaternion.Inverse (root.rotation) * attachment.bone.rotation * Quaternion.Inverse (attachment.boneRest);
            position = origin + turn * attachment.frameOffset;
            rotation = Quaternion.Normalize (turn * attachment.frameRotation);
        }

        /// <summary>物の設定の位置で付けたときの物の姿勢（ルート空間。今の骨の枠に乗せる）</summary>
        internal void GetSettingsPose (Attachment attachment, out Vector3 position, out Quaternion rotation)
        {
            GetPoseOnFrame (attachment, attachment.gripPosition, attachment.gripRotation, out position, out rotation);
        }

        /// <summary>今の位置（キーの位置か物の設定の位置）で付けたときの物の姿勢（ルート空間。今の骨の枠に乗せる）</summary>
        void GetAttachedPose (Attachment attachment, out Vector3 position, out Quaternion rotation)
        {
            GetPoseOnFrame (attachment, attachment.heldGripPosition, attachment.heldGripRotation, out position, out rotation);
        }

        void GetPoseOnFrame (Attachment attachment, Vector3 gripPosition, Quaternion gripRotation, out Vector3 position, out Quaternion rotation)
        {
            GetAttachFrame (attachment, out Vector3 frame, out Quaternion frameRotation);
            rotation = Quaternion.Normalize (frameRotation * Quaternion.Inverse (gripRotation));
            position = frame - rotation * gripPosition;
        }

        /// <summary>物の姿勢（ルート空間）。つかんでいれば置いた姿勢、そうでなければキーの値を今の枠の骨に乗せた姿勢</summary>
        void GetPose (Entry entry, out Vector3 position, out Quaternion rotation)
        {
            if (entry.dragging) {
                position = entry.dragPosition;
                rotation = entry.dragRotation;
                return;
            }
            if (entry.spanMixed) {
                // 付け外しをまたぐキーの間: 2 つのキーをそれぞれの基準で置いてから補間する（ApplyWorldSpan）
                KeyPose (entry, entry.spanFromWorld, entry.spanFromPosition, entry.spanFromRotation, out Vector3 fromPosition, out Quaternion fromRotation);
                KeyPose (entry, !entry.spanFromWorld, entry.spanToPosition, entry.spanToRotation, out Vector3 toPosition, out Quaternion toRotation);
                position = Vector3.Lerp (fromPosition, toPosition, entry.spanWeight);
                rotation = Quaternion.Normalize (Quaternion.Slerp (fromRotation, toRotation, entry.spanWeight));
                return;
            }
            KeyPose (entry, entry.inWorld, entry.control.localPosition, entry.control.localRotation, out position, out rotation);
        }

        /// <summary>物の姿勢の値（コントロールのローカルの値）を、ワールドか枠の骨を基準に置いた姿勢（ルート空間）</summary>
        void KeyPose (Entry entry, bool world, Vector3 value, Quaternion valueRotation, out Vector3 position, out Quaternion rotation)
        {
            GetFrame (entry, world, out Vector3 origin, out Quaternion turn);
            position = origin + turn * value * rig_.humanScale;
            rotation = turn * valueRotation;
        }

        /// <summary>今の値の基準（ワールドに留めている間はワールド）の、付け根と基準姿勢からの回り（ルート空間）</summary>
        void GetFrame (Entry entry, out Vector3 origin, out Quaternion turn)
        {
            GetFrame (entry, entry.inWorld, out origin, out turn);
        }

        /// <summary>
        /// 値の基準の付け根と、基準姿勢からの回り（ルート空間）。枠の骨なら今の姿勢、ワールドなら基準姿勢の枠の骨の場所で回りなし
        /// （ルートから見て動かない。T ポーズではどちらも同じ所なので、キーの無いクリップの基準の値はどちらでも同じ所に出る）
        /// </summary>
        void GetFrame (Entry entry, bool world, out Vector3 origin, out Quaternion turn)
        {
            origin = Vector3.zero;
            turn = Quaternion.identity;
            if (entry.frameBone == null) return;
            if (world) {
                origin = rig_.GetRestRootPosition (entry.frameBone);
                return;
            }
            Transform root = rig_.root.transform;
            origin = root.InverseTransformPoint (entry.frameBone.position);
            turn = Quaternion.Inverse (root.rotation) * entry.frameBone.rotation * Quaternion.Inverse (entry.frameRest);
        }

        /// <summary>物の原点から、今の位置（柄の軸まわりに回した後）までの差（ルート空間。rotation は物の向き）</summary>
        static Vector3 GripOffset (Attachment attachment, Quaternion rotation)
        {
            Vector3 at = attachment.heldGripPosition;
            Vector3 axis = attachment.axis;
            if (axis.sqrMagnitude < 1e-8f) return rotation * at;
            axis.Normalize ();
            Vector3 onAxis = axis * Vector3.Dot (at, axis);
            return rotation * onAxis + attachment.twist * (rotation * (at - onAxis));
        }

        /// <summary>
        /// 全身 IK を解く前に呼ぶ（S102・S104）。付けている物は、付ける先の骨の子として骨の組みに入れ（付け外しが変わったときだけ組み直す）、
        /// 物を骨の今の枠 × 付けた位置へ置いて、物の姿勢（キー・つかんで置いた姿勢）を物の姿勢の点の目標にする。
        /// 何か所にも付けていれば、骨の木の親は 1 つ（ParentAttachment）で、ほかの先は輪の関節でつなぐ。付けている骨の点もふつうの点（11-07 ユーザー指示）。
        /// 付けていない物は組みに入れない（Finish で物の点から姿勢を決める）
        /// </summary>
        /// <param name="only">物の上の点のうち、効かせる点（キーの間の Locked の点。null なら動かした後で、固定している点を全部効かせる）</param>
        public void Prepare (FullBodyRig body, ICollection<Transform> only = null)
        {
            fitOnly_ = only;
            foreach (Entry entry in entries_) BeginPointDrag (body, entry);
            attachments_.Clear ();
            loops_.Clear ();
            foreach (Entry entry in entries_) {
                Attachment parent = ParentAttachment (body, entry);
                if (parent == null) continue;
                bool fixedJoint = Welded (entry, parent);
                attachments_.Add (new FullBodyRig.HeldAttachment {
                    body = entry.editing,
                    parent = parent.bone,
                    axis = fixedJoint ? Vector3.zero : parent.axis,
                    fixedPose = fixedJoint,
                    // 骨から見た物の向き（物の向き = 骨の向き × 骨の基準⁻¹ × 枠の基準の向き × 位置の向き⁻¹）
                    weld = fixedJoint ? Quaternion.Normalize (Quaternion.Inverse (parent.boneRest) * parent.frameRotation * Quaternion.Inverse (GripRotation (entry, parent))) : Quaternion.identity,
                });
            }
            body.SetHeldAttachments (attachments_);
            foreach (Entry entry in entries_) {
                Attachment parent = ParentAttachment (body, entry);
                entry.attached = parent != null && body.IsHeldAttached (entry.editing);
                if (!entry.attached) {
                    body.SetPositionWeight (entry.poseId, 0);
                    body.SetRotationWeight (entry.poseId, 0);
                    body.UnmarkGrabbed (entry.poseId);
                    continue;
                }
                GetPose (entry, out entry.position, out entry.rotation);
                // 回らない先（腰）に付けている間は、物の姿勢は骨で決まる（関節に自由が無いので、キーの値は足すことが無い）。物そのものを動かせば、置いた姿勢が骨を引く。
                // キーの値を物の姿勢の点の目標にすると、キーの間で枠（胸）の補間した姿勢へ点が物を引き、腰に付いた物から伸びる手と腕が S91f までの答えからずれた
                // （11-09 実測。デモの剣を納めるクリップの腰に付けている 130〜149 フレームで、右腕が最大 22mm）
                if (Snapping (entry, parent)) {
                    // 骨から見た物の場所（関節）は、解く前の骨の決まった位置で決める。物の目標は、押した瞬間（解いた後の骨）の決まった位置（SnapTo が置いた姿勢）。
                    // 目標も解く前の骨（キーの FK）で決めると、寄せる先の手を IK で動かしていたとき、物の目標が動かす前の手の所になり、
                    // 物は手まで来ずに手の方が引き戻された（11-09 ユーザー指示「寄せる ボタンは object 側が寄るように」。右手を 10cm 動かして寄せると、右手が 9.6cm 戻った）
                    GetSettingsPose (parent, out entry.position, out entry.rotation);
                    Place (entry);
                    GetPose (entry, out entry.position, out entry.rotation);
                }
                else {
                    if (!parent.twists && !entry.dragging) GetAttachedPose (parent, out entry.position, out entry.rotation);
                    PlaceOnParent (entry, parent);
                }
                bool snapping = Snapping (entry, parent);
                foreach (Attachment attachment in entry.attachments) {
                    if (attachment == parent || !attachment.attached || !body.IsSolvedBody (attachment.bone)) continue;
                    // 寄せている間は物が動かないので、付けている骨だけが物へ伸びる（届かなくても物を骨の方へ戻さない）
                    loops_.Add (new FullBodyRig.HeldLoop {
                        holdObject = snapping,
                        bone = attachment.bone,
                        offset = Quaternion.Inverse (attachment.boneRest) * attachment.frameOffset,
                        restRotation = attachment.boneRest,
                        body = entry.editing,
                        grip = attachment.heldGripPosition,
                        // 輪の関節は柄の軸まわりに回る。回らない先を輪でつなぐときも柄の軸を使う（輪のばねが位置の向きへ戻す）
                        axis = entry.definition.gripAxis,
                        toObject = Quaternion.Normalize (attachment.frameRotation * Quaternion.Inverse (attachment.heldGripRotation)),
                    });
                }
                SetPosePoint (body, entry);
            }
            body.SetHeldLoops (loops_);
        }

        readonly List<FullBodyRig.HeldAttachment> attachments_ = new List<FullBodyRig.HeldAttachment> ();
        readonly List<FullBodyRig.HeldLoop> loops_ = new List<FullBodyRig.HeldLoop> ();

        /// <summary>
        /// 物を子にする付ける先: 付けている先のうち、回らない先（Fixed）が回る先（Twist）より先、同じ種類ならリグ定義の並びで先のもの。
        /// 既定の並び（右手・左手・腰）では、両手なら右手の子、腰に付ければ腰の子になる（S91f までの持ち方と同じ）。付けていない・骨を解かなければ null。
        /// 寄せている間（SnapTo）は寄せる先（付けていなくても）
        /// </summary>
        static Attachment ParentAttachment (FullBodyRig body, Entry entry)
        {
            if (entry.snapping && entry.snapTarget != null && body.IsSolvedBody (entry.snapTarget.bone)) return entry.snapTarget;
            Attachment found = null;
            foreach (Attachment attachment in entry.attachments) {
                if (!attachment.attached || !body.IsSolvedBody (attachment.bone)) continue;
                if (found == null || (!attachment.twists && found.twists)) found = attachment;
            }
            return found;
        }

        /// <summary>寄せている先を親にしているか</summary>
        static bool Snapping (Entry entry, Attachment parent)
        {
            return entry.snapping && parent == entry.snapTarget;
        }

        /// <summary>親の関節が回らないか（回らない先・寄せている間）</summary>
        static bool Welded (Entry entry, Attachment parent)
        {
            return !parent.twists || Snapping (entry, parent);
        }

        /// <summary>親の付けた位置の向き（寄せている間は物の設定の位置）</summary>
        static Quaternion GripRotation (Entry entry, Attachment parent)
        {
            return Snapping (entry, parent) ? parent.gripRotation : parent.heldGripRotation;
        }

        /// <summary>
        /// 物を、付ける先の今の枠（解く前の姿勢）× 付けた位置へ置く。回る先なら柄の軸は枠に合わせ、軸まわりは物の目標の向きに一番近くする（軸まわりは関節が自由に回す）。
        /// 解く前に置いた所が、骨から見た物の場所と向き（ソルバーの関節）になる
        /// </summary>
        void PlaceOnParent (Entry entry, Attachment attachment)
        {
            GetAttachFrame (attachment, out Vector3 frame, out Quaternion frameRotation);
            // 枠が今の枠の向きになる物の向き（柄の軸まわりも枠に合わせた向き）
            Quaternion exact = Quaternion.Normalize (frameRotation * Quaternion.Inverse (attachment.heldGripRotation));
            Vector3 axis = attachment.axis;
            Vector3 at = attachment.heldGripPosition;
            Quaternion rotation = exact;
            Vector3 onAxis = Vector3.zero;
            if (axis.sqrMagnitude > 1e-8f) {
                axis.Normalize ();
                // 物そのもの・物の点をつかんで動かしている間、向きを留めていなければ、前に解いた姿勢の枠の中の回りのままにする。
                // つかんでいる間の物の向き（置いた姿勢）は前に解いた後の向きで、解き直しはキーの骨から始まる。前に解いた骨に対する回りを
                // キーの骨に一番近い向きで当てはめると、骨がキーからねじれた分だけ解くたびに回りが足され、丸い棒が手の中で柄の軸まわりに回り続けた
                // （11-07 ユーザー報告「表示しているボーンが回転しまくる。見た目は回転していないのに」。60 回動かす間に ±170°）
                if (entry.dragging && attachment.hasRoll && entry.poseValues.rotationWeight <= 0) rotation = Quaternion.Normalize (exact * attachment.roll);
                else rotation = Quaternion.Normalize (Quaternion.FromToRotation (entry.rotation * axis, exact * axis) * entry.rotation);
                onAxis = axis * Vector3.Dot (at, axis);
            }
            // 位置の場所: 柄の軸の上の分は物と一緒に、柄から離れた分は枠と一緒に回る
            Vector3 position = frame - rotation * onAxis - exact * (at - onAxis);
            Transform root = rig_.root.transform;
            entry.editing.SetPositionAndRotation (root.TransformPoint (position), root.rotation * rotation);
        }

        /// <summary>
        /// 物の姿勢の点の目標を入れる。状態（強さ・Lock）は、ほかの点と同じくキーと持ち物の対象で決めた値のまま（11-07 ユーザー指示「汎用的な制御にすることが重要、体の点の決まりどおりに」）。
        /// 物そのものを動かしていればつかんだ点にする（強さは動かした分だけ DragTo が入れる。体の点をつかんで動かしたときと同じ。Lock は今のまま）。
        /// 引かずに置くだけ（寄せる）なら、つかんだ点にしない
        /// </summary>
        void SetPosePoint (FullBodyRig body, Entry entry)
        {
            entry.poseControl.localPosition = entry.position / rig_.humanScale;
            entry.poseControl.localRotation = Quaternion.Normalize (entry.rotation * Quaternion.Inverse (rig_.GetRestRootRotation (entry.editing)));
            bool direct = entry.dragging && entry.direct;
            bool grabbed = direct && entry.pulling;
            entry.fitted = !direct && AnyPointActive (body, entry);
            // 引かずに置くだけ（寄せる）は、場所も向きもそこに留める
            if (direct && !entry.pulling) {
                entry.poseValues.positionWeight = 1;
                entry.poseValues.rotationWeight = 1;
            }
            if (grabbed) body.MarkGrabbed (entry.poseId);
            else body.UnmarkGrabbed (entry.poseId);
        }

        /// <summary>物の上の点をつかんでいるか、効かせる点（fitOnly_）に強さが入っているか</summary>
        bool AnyPointActive (FullBodyRig body, Entry entry)
        {
            foreach (ObjectPoint point in entry.points) {
                if (body.IsGrabbed (point.id)) return true;
                if (fitOnly_ != null && !fitOnly_.Contains (point.control)) continue;
                if (body.GetPositionWeight (point.id) > 0 || body.GetRotationWeight (point.id) > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 付けている物の姿勢の点のコントロール（キーのあるフレームでも、付けている骨を物に合わせて解く）を controls に足した集まり。足す物が無ければ controls
        /// </summary>
        public ICollection<Transform> WithHeldControls (ICollection<Transform> controls)
        {
            HashSet<Transform> result = null;
            foreach (Entry entry in entries_) {
                if (!entry.attached) continue;
                result ??= controls != null ? new HashSet<Transform> (controls) : new HashSet<Transform> ();
                result.Add (entry.poseControl);
                foreach (ObjectPoint point in entry.points) {
                    if (fitOnly_ == null || fitOnly_.Contains (point.control)) result.Add (point.control);
                }
            }
            return result != null ? (ICollection<Transform>)result : controls;
        }

        ICollection<Transform> fitOnly_;

        /// <summary>物の上の点をつかんでいれば、今の姿勢からつかんだ状態にする。全身を引くのは重い点をつかんだときだけ</summary>
        void BeginPointDrag (FullBodyRig body, Entry entry)
        {
            bool grabbed = false;
            bool heavy = false;
            foreach (ObjectPoint point in entry.points) {
                if (!body.IsGrabbed (point.id)) continue;
                grabbed = true;
                heavy |= point.heavy;
            }
            if (!grabbed) return;
            if (!entry.dragging) {
                GetPose (entry, out entry.dragPosition, out entry.dragRotation);
                entry.dragging = true;
            }
            entry.pulling = heavy;
            entry.direct = false;
            entry.keysPending = true;
        }

        /// <summary>物の今の姿勢を決める（キーの値か置いた姿勢に、物の上の点を効かせる）。点を効かせたら true</summary>
        bool ResolvePose (FullBodyRig body, Entry entry)
        {
            GetPose (entry, out entry.position, out entry.rotation);
            return FitToPoints (body, entry);
        }

        /// <summary>姿勢を決める点 1 つ。order の順に効かせる</summary>
        struct FitPoint
        {
            public int order;
            public Vector3 offset;
            public Vector3 target;
        }

        readonly List<FitPoint> fit_ = new List<FitPoint> ();
        static readonly System.Comparison<FitPoint> kFitOrder = (a, b) => a.order.CompareTo (b.order);
        /// <summary>同じ扱いの点を並べた順に効かせるための、扱いの間隔</summary>
        const int kFitRank = 1000;

        /// <summary>
        /// 物の上の点で物の姿勢を決める（S91e）。物は剛体なので、先の点から順に、1 つ目で場所、2 つ目でそれを結ぶ向き、3 つ目でその軸まわりが決まり、残りは使わない。
        /// 効かせる順と引かれる点は、体の点の決まり（S88）と同じ:
        /// Lock の点 → 重い点をつかんでいれば、その点 → 動かない重い点（Pin。軽い点をつかんでいる間は Free も今の場所に留まる）→ つかんだ軽い点 →
        /// 何もつかんでいなければ軽い Pin の点。何かをつかんでいる間、軽い Pin の点は引かれる（使わない）。
        /// 向きを固定した点があれば、その点を中心に向きを先に決める（残りの点では回さない）。点を効かせたら true
        /// </summary>
        bool FitToPoints (FullBodyRig body, Entry entry)
        {
            if (entry.points.Count == 0) return false;
            fit_.Clear ();
            bool grabbing = body.anyGrabbed || entry.dragging;
            bool lightGrab = entry.dragging && !entry.pulling && !entry.direct;
            bool rotated = false;
            float scale = rig_.humanScale;
            for (int i = 0; i < entry.points.Count; i++) {
                ObjectPoint point = entry.points[i];
                if (fitOnly_ != null && !fitOnly_.Contains (point.control)) continue;
                bool grabbed = body.IsGrabbed (point.id);
                bool locked = body.GetLocked (point.id);
                // 物そのものを動かしている間は、置いた姿勢が優先（Lock の点だけ留まる）。軽い Pin の点は、何かをつかんでいる間は引かれる
                bool usable = grabbed || locked || (!entry.direct && (point.heavy || !grabbing));
                float rotationWeight = body.GetRotationWeight (point.id);
                if (usable && !rotated && rotationWeight > 0) {
                    Vector3 pivot = entry.position + entry.rotation * point.offset;
                    Quaternion world = rig_.root.transform.rotation;
                    body.TryGetPointWorld (point.id, out Vector3 unused, out Quaternion target);
                    entry.rotation = Quaternion.Normalize (Quaternion.Slerp (entry.rotation, Quaternion.Inverse (world) * target, rotationWeight));
                    entry.position = pivot - entry.rotation * point.offset;
                    rotated = true;
                }
                float weight = body.GetPositionWeight (point.id);
                int rank;
                if (grabbed) rank = point.heavy ? 1 : 3;
                else if (locked && weight > 0) rank = 0;
                else if (!usable) continue;
                else if (point.heavy && (weight > 0 || lightGrab)) rank = 2;
                else if (weight > 0) rank = 4;
                else continue;
                Vector3 here = entry.position + entry.rotation * point.offset;
                fit_.Add (new FitPoint {
                    order = rank * kFitRank + i,
                    offset = point.offset,
                    target = weight > 0 ? Vector3.Lerp (here, point.control.localPosition * scale, weight) : here,
                });
            }
            if (fit_.Count == 0) return rotated;
            fit_.Sort (kFitOrder);

            entry.position += fit_[0].target - (entry.position + entry.rotation * fit_[0].offset);
            if (rotated) return true;
            Vector3 pin = fit_[0].target;
            if (fit_.Count > 1) {
                Vector3 from = entry.position + entry.rotation * fit_[1].offset - pin;
                Vector3 to = fit_[1].target - pin;
                if (from.sqrMagnitude > kFitEpsilon && to.sqrMagnitude > kFitEpsilon) TurnAboutPin (entry, pin, Quaternion.FromToRotation (from, to));
            }
            if (fit_.Count > 2) {
                Vector3 axis = entry.position + entry.rotation * fit_[1].offset - pin;
                Vector3 from = Vector3.ProjectOnPlane (entry.position + entry.rotation * fit_[2].offset - pin, axis);
                Vector3 to = Vector3.ProjectOnPlane (fit_[2].target - pin, axis);
                if (axis.sqrMagnitude > kFitEpsilon && from.sqrMagnitude > kFitEpsilon && to.sqrMagnitude > kFitEpsilon) {
                    TurnAboutPin (entry, pin, Quaternion.AngleAxis (Vector3.SignedAngle (from, to, axis), axis));
                }
            }
            return true;
        }

        const float kFitEpsilon = 1e-10f;

        static void TurnAboutPin (Entry entry, Vector3 pin, Quaternion turn)
        {
            entry.position = pin + turn * (entry.position - pin);
            entry.rotation = Quaternion.Normalize (turn * entry.rotation);
        }

        /// <summary>
        /// 全身 IK を解いた後に呼ぶ。付けている物は、解いた物の骨から物の姿勢を読む（骨は物の骨の親なので、付けた位置から外れない）。
        /// 付けていない物は、決まった枠で物の姿勢を求めて物の点を効かせる。つかんでいた物は、解いた後の姿勢を値に残す
        /// </summary>
        public void Finish (FullBodyRig body, float weight)
        {
            bool edited = fitOnly_ == null;
            Transform root = rig_.root.transform;
            foreach (Entry entry in entries_) {
                if (!entry.attached) {
                    entry.fitted = ResolvePose (body, entry);
                    continue;
                }
                body.UnmarkGrabbed (entry.poseId);
                if (weight > 0) {
                    entry.position = root.InverseTransformPoint (entry.editing.position);
                    entry.rotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation) * entry.editing.rotation);
                }
                foreach (Attachment attachment in entry.attachments) {
                    if (attachment.attached) UpdateTwist (attachment);
                }
            }
            Place ();
            foreach (Entry entry in entries_) {
                if (entry.snapping) entry.snapGap = GetAttachGap (entry);
            }
            // 点で決めた姿勢も、動かした後なら値に残す（打つキーは解いた姿勢。キーの間で Lock の点に留めた姿勢はクリップの値を変えない）
            // 付けている物は、物の上の点で姿勢を決めても値に残さない（キーを打つときに SyncToBodies で解いた物の骨の姿勢へ合わせ直す。体の骨と同じ）。
            // 解くたびに残すと、次に解くときの元の姿勢（キー）が前の答えになり、手首を引く間に物が手の中で柄の軸まわりに回り続けた
            // （11-08 ユーザー報告「手首を動かしていると、たまに棒の姿勢がワープする」。物の上の点を Pin にした棒が 1 回 20° 回った）
            foreach (Entry entry in entries_) {
                if (entry.dragging || (edited && entry.fitted && !entry.attached)) StoreValues (entry);
            }
            fitOnly_ = null;
        }

        /// <summary>
        /// 柄の軸まわりの回り（ルート空間。twist）を、解いた骨と物の姿勢から決め直す。枠の向き = twist × 物の向き × 位置の向き
        /// </summary>
        void UpdateTwist (Attachment attachment)
        {
            GetAttachFrame (attachment, out Vector3 unused, out Quaternion frameRotation);
            Quaternion exact = Quaternion.Normalize (frameRotation * Quaternion.Inverse (attachment.heldGripRotation));
            attachment.twist = Quaternion.Normalize (exact * Quaternion.Inverse (attachment.entry.rotation));
            attachment.roll = Quaternion.Normalize (Quaternion.Inverse (exact) * attachment.entry.rotation);
            attachment.hasRoll = true;
        }

        /// <summary>
        /// キーを打つ前に呼ぶ（体の点の BodyPointTarget.SyncToBone と同じ）。付けている物の値（物のキー・物の姿勢の点の目標）を、解いた物の骨の姿勢へ合わせ直す。
        /// 物は骨の子の骨なので、ほかの骨と同じく解いた姿勢をキーにする。物の上の点が効いているときだけ値に残していたため、手首を引いて離すと、
        /// 元の場所の物の姿勢の点（Pin）が手首を引き戻した（11-07 ユーザー報告「手首を動かすと、ドラッグを離すと元の位置に戻っている」。IkArm で 7〜20cm）
        /// </summary>
        public void SyncToBodies ()
        {
            foreach (Entry entry in entries_) {
                if (entry.attached) StoreValues (entry);
            }
        }

        /// <summary>付けている先のうち、いちばん付けた位置に届いていない差（メートル）</summary>
        float GetAttachGap (Entry entry)
        {
            float gap = 0;
            foreach (Attachment attachment in entry.attachments) {
                if (!attachment.attached) continue;
                GetAttachFrame (attachment, out Vector3 reached, out Quaternion unused);
                gap = Mathf.Max (gap, Vector3.Distance (reached, entry.position + GripOffset (attachment, entry.rotation)));
            }
            return gap;
        }

        /// <summary>キーの無いときの T ポーズでの物の場所（ルート空間）</summary>
        internal Vector3 GetRestPosition (Entry entry)
        {
            Vector3 origin = entry.frameBone != null ? rig_.GetRestRootPosition (entry.frameBone) : Vector3.zero;
            return origin + rig_.BodyToRoot (entry.definition.restPosition);
        }

        /// <summary>今の物の姿勢を、今の枠（ワールドに留めている間はワールド）から見た値へ入れる。入れた値がキーの間の補間より優先する</summary>
        internal void StoreValues (Entry entry)
        {
            entry.spanMixed = false;
            GetFrame (entry, out Vector3 origin, out Quaternion turn);
            Quaternion inverse = Quaternion.Inverse (turn);
            entry.control.localPosition = inverse * (entry.position - origin) / rig_.humanScale;
            entry.control.localRotation = Quaternion.Normalize (inverse * entry.rotation);
            entry.dragPosition = entry.position;
            entry.dragRotation = entry.rotation;
        }

        /// <summary>
        /// 今の物の姿勢を、定義の基準の場所へ書く（リグエディタで置いたとき。基準姿勢の枠の骨から見た、体の向きの値）。Undo と保存は呼ぶ側
        /// </summary>
        public void StoreRest (Entry entry)
        {
            Vector3 origin = entry.frameBone != null ? rig_.GetRestRootPosition (entry.frameBone) : Vector3.zero;
            Quaternion inverse = Quaternion.Inverse (body_);
            entry.definition.restPosition = inverse * (entry.position - origin);
            entry.definition.restRotation = (inverse * entry.rotation).eulerAngles;
        }

        /// <summary>
        /// 付ける先の物の設定の位置を、物を置くワールドの姿勢で置き直す（リグエディタで動かす間。定義へは StoreSettings で書く）。
        /// 付ける先の枠は動かさず、物がそこへ付いたときの物の姿勢から位置を決める
        /// </summary>
        public void SetSettingsPose (Attachment attachment, Vector3 worldPosition, Quaternion worldRotation)
        {
            Transform root = rig_.root.transform;
            GetAttachFrame (attachment, out Vector3 frame, out Quaternion frameRotation);
            Vector3 position = root.InverseTransformPoint (worldPosition);
            Quaternion inverse = Quaternion.Inverse (Quaternion.Inverse (root.rotation) * worldRotation);
            attachment.gripPosition = inverse * (frame - position);
            attachment.gripRotation = Quaternion.Normalize (inverse * frameRotation);
        }

        /// <summary>物の設定の位置で付けたときの物の、ワールドの姿勢</summary>
        public void GetSettingsWorld (Attachment attachment, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            GetSettingsPose (attachment, out Vector3 position, out Quaternion rotation);
            Transform root = rig_.root.transform;
            worldPosition = root.TransformPoint (position);
            worldRotation = root.rotation * rotation;
        }

        /// <summary>
        /// リグエディタのハンドルの姿勢: 回る先（手）は物の上の枠（動かすと、物は動かず骨が付いてくる。手首を置く所）、
        /// 回らない先（腰・背中）は物そのもの（動かすと、骨は動かず物が動く。差す所）
        /// </summary>
        public void GetSettingsHandle (Attachment attachment, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            if (!attachment.twists) {
                GetSettingsWorld (attachment, out worldPosition, out worldRotation);
                return;
            }
            Transform root = rig_.root.transform;
            Entry entry = attachment.entry;
            worldPosition = root.TransformPoint (entry.position + entry.rotation * attachment.gripPosition);
            worldRotation = root.rotation * entry.rotation * attachment.gripRotation;
        }

        /// <summary>リグエディタのハンドルで、物の設定の位置を置き直す（GetSettingsHandle と同じ姿勢。定義へは StoreSettings で書く）</summary>
        public void SetSettingsHandle (Attachment attachment, Vector3 worldPosition, Quaternion worldRotation)
        {
            if (!attachment.twists) {
                SetSettingsPose (attachment, worldPosition, worldRotation);
                return;
            }
            Transform root = rig_.root.transform;
            Entry entry = attachment.entry;
            Quaternion inverse = Quaternion.Inverse (entry.rotation);
            attachment.gripPosition = inverse * (root.InverseTransformPoint (worldPosition) - entry.position);
            attachment.gripRotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation * entry.rotation) * worldRotation);
        }

        /// <summary>今の位置を物の設定へ書く（項目が無ければ足す）。Undo と保存は呼ぶ側</summary>
        public void StoreSettings (Attachment attachment)
        {
            HeldObjectAttachment settings = attachment.entry.definition.GetOrAddAttachment (attachment.name);
            settings.position = attachment.gripPosition;
            settings.rotation = attachment.gripRotation.eulerAngles;
            attachment.settings = settings;
        }

        /// <summary>
        /// 付ける先の今の枠を、物から見た位置にして、キーで付けた位置にする（S99。付けるボタンを押した瞬間の位置で付ける。11-04 ユーザー指示
        /// 「掴むボタンを押した瞬間の位置、姿勢で拘束する。武器にかぎらず共通仕様として」）。物も骨も動かない。作れたら true
        /// </summary>
        public bool GrabAtCurrent (Attachment attachment)
        {
            if (attachment == null) return false;
            GetAttachFrame (attachment, out Vector3 frame, out Quaternion frameRotation);
            // 値から作った姿勢ではなく、Finish が決めた今の姿勢（見えている物）から見て、その姿勢を値にも残す。
            // 値のままだと、付けた後に解き直したとき物が値の場所へ戻り、骨も一緒に動く
            Entry entry = attachment.entry;
            Quaternion inverse = Quaternion.Inverse (entry.rotation);
            StoreValues (entry);
            attachment.SetKeyedGrip (true, inverse * (frame - entry.position), Quaternion.Normalize (inverse * frameRotation).eulerAngles);
            entry.keysPending = true;
            return true;
        }

        /// <summary>
        /// 付けるか外す。付けるときは今の位置で付ける（GrabAtCurrent）。今の物の姿勢を値に残すので、外しても物は跳ばない
        /// </summary>
        public void SetAttached (Attachment attachment, bool attach)
        {
            if (attachment == null) return;
            Entry entry = attachment.entry;
            if (attach) GrabAtCurrent (attachment);
            attachment.values.attached = attach ? 1 : 0;
            // 値は付け外しを変えた後の基準で入れる（ワールドに留める物は、付けると枠の骨から、外すとワールドから見た値になる）
            StoreValues (entry);
            ApplyConstraintWeights (entry);
            entry.keysPending = true;
        }

        /// <summary>物の上の点の場所（ワールド。骨の上の点の場所と同じく、今の物の姿勢に乗せた所）</summary>
        public Vector3 GetPointWorld (ObjectPoint point)
        {
            return rig_.root.transform.TransformPoint (point.entry.position + point.entry.rotation * point.offset);
        }

        /// <summary>物の上の点を、ワールドの場所へ置き直す（リグエディタで動かす間。定義へは StorePoint で書く）</summary>
        public void SetPointWorld (ObjectPoint point, Vector3 worldPosition)
        {
            point.offset = Quaternion.Inverse (point.entry.rotation) * (rig_.root.transform.InverseTransformPoint (worldPosition) - point.entry.position);
        }

        /// <summary>今の点の場所を定義へ書く。Undo と保存は呼ぶ側</summary>
        public void StorePoint (ObjectPoint point)
        {
            point.definition.position = point.offset;
        }

        /// <summary>今の値と枠で物の姿勢を求め直す（全身 IK を通さない姿勢。編集用リグを解いた直後）</summary>
        public void Refresh ()
        {
            foreach (Entry entry in entries_) GetPose (entry, out entry.position, out entry.rotation);
            Place ();
        }

        /// <summary>編集用の体の物を、今の物の姿勢へ置く</summary>
        void Place ()
        {
            foreach (Entry entry in entries_) Place (entry);
        }

        void Place (Entry entry)
        {
            Transform root = rig_.root.transform;
            entry.editing.SetPositionAndRotation (root.TransformPoint (entry.position), root.rotation * entry.rotation);
        }
    }

}
