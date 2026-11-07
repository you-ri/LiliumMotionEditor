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
    /// 物を持つ手の握り。手首の点の場所と向きを、物のローカルで持つ
    /// </summary>
    [System.Serializable]
    public sealed class HeldObjectGrip
    {
        [Tooltip ("Hold the object with this hand in clips that have no hold keys")]
        public bool hold = true;
        [Tooltip ("Where the wrist goes, in the object's local space (meters)")]
        public Vector3 position;
        [Tooltip ("Hand orientation in the object's local space (Euler angles). Zero aligns the axes that were body right, up and forward in the T-pose with the object's X, Y and Z")]
        public Vector3 rotation;
        [Tooltip ("Optional. Game rig constraint weight that gets this hand's hold when baking, written as <Rig name>/<path below the rig>")]
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
    /// 持ち物を差す場所（S91f。腰・背中など）。差している間、物は骨に固定して動く
    /// </summary>
    [System.Serializable]
    public sealed class HeldObjectSocket
    {
        [Tooltip ("Name. Clips key each socket by this name (Controls/Object/object name/name), so renaming it loses its keys")]
        public string name;
        [Tooltip ("Bone the object stays on while it's in this socket")]
        public HumanBodyBones bone = HumanBodyBones.Hips;
        [Tooltip ("Where the object goes, from the bone along body right, up and forward in the T-pose (meters)")]
        public Vector3 position;
        [Tooltip ("Object rotation, in body right, up and forward axes in the T-pose (Euler angles)")]
        public Vector3 rotation;
        [Tooltip ("Optional. Game rig constraint weight that is 1 while the object is in this socket when baking, written as <Rig name>/<path below the rig>")]
        public string constraint;
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
        [Tooltip ("Bone the object's keyed pose is relative to. Chest keeps the object with the body when the hips move")]
        public HeldObjectFrame frame = HeldObjectFrame.Chest;
        [Tooltip ("Position with no keys, from the frame bone along body right, up and forward (meters)")]
        public Vector3 restPosition = new Vector3 (0, -0.25f, 0.3f);
        [Tooltip ("Rotation with no keys, in body right, up and forward axes (Euler angles)")]
        public Vector3 restRotation = new Vector3 (35, 0, 0);
        public HeldObjectGrip rightGrip = new HeldObjectGrip ();
        public HeldObjectGrip leftGrip = new HeldObjectGrip ();
        [Tooltip ("Full-body IK points on the object. They work like the rig definition's points")]
        public List<HeldObjectPoint> points = new List<HeldObjectPoint> ();
        [Tooltip ("The handle axis, in the object's local space. Holding hands can twist freely around it, and a clip that shifts a grip slides the hand along it")]
        public Vector3 gripAxis = Vector3.up;
        [Tooltip ("Places the object can sit in (a hip, the back, etc.). Clips switch to them with keys")]
        public List<HeldObjectSocket> sockets = new List<HeldObjectSocket> ();
        [Tooltip ("Socket the object sits in, with no hand holding it, in clips that have no socket keys. Empty: not in a socket")]
        public string restSocket;

        /// <summary>差していないことを表す名前（CLI）。差す場所の名前には使えない</summary>
        public const string kNoSocketName = "None";

        /// <summary>FBX の空の名前。あれば握りの初期値にする</summary>
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

        /// <summary>物のモデルの空（Grip_R・Grip_L）から、握りの場所と向きを入れる。空が無ければ変えない</summary>
        public void LoadGrips (GameObject model)
        {
            if (model == null) return;
            LoadGrip (model.transform, kRightGripName, rightGrip);
            LoadGrip (model.transform, kLeftGripName, leftGrip);
        }

        static void LoadGrip (Transform model, string name, HeldObjectGrip grip)
        {
            Transform empty = model.Find (name);
            if (empty == null) return;
            grip.position = model.InverseTransformPoint (empty.position);
            grip.rotation = (Quaternion.Inverse (model.rotation) * empty.rotation).eulerAngles;
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
            if (sockets != null) {
                HashSet<string> names = new HashSet<string> ();
                foreach (HeldObjectSocket socket in sockets) {
                    if (socket == null || !RigPaths.IsValidName (socket.name) || socket.name == kNoSocketName || !names.Add (socket.name)) errors.Add (Tr ("HELD_OBJECT_INVALID_SOCKET_NAME", name, socket != null ? socket.name : ""));
                }
            }
            return errors;
        }
    }

    /// <summary>
    /// 持ち物を編集用の体に当てたもの（S91）。物が導き、手が従う: 物の姿勢をキーに持ち、持っている間は物の姿勢を全身 IK の見えない点の目標にする。
    ///
    /// 物の姿勢の値（＝編集用クリップに保存する値。Controls/Object/名前 の Transform）:
    /// - localRotation = 枠の骨の基準姿勢からの回りを戻した、ルートから見た物の向き
    /// - localPosition = 枠の骨から物までの向き（同じ回りを戻した） / humanScale
    /// 枠が Chest なら、腰や胸が動いても物は胸に付いて動く。
    ///
    /// 解き方は Prepare → 全身 IK → Finish の順（S102。11-07 ユーザー指示「持ったときに IK 構造を再構築する。いままで同じ IK 構造を解くだけでよくなる」）。
    /// Prepare は、持っている物を持つ手の骨の子として全身 IK の骨の組みに入れ（両手なら右手の子、左手は輪の関節）、物を手 × 握りへ置く。
    /// 物を動かせば物の姿勢の点がつかんだ点になって全身を引き、物の上の点を動かせばその点がつかんだ点になる。手を握りへ合わせ直す特別な処理は無い。
    /// Finish は、解いた物の骨から物の姿勢を読む。
    ///
    /// 物の上の点（S91e）はふつうの全身 IK の点で、状態・キー・選び方・ハンドルは変わらない。持っている間は物の骨の上の点として解き、重さの意味も体の点と同じ。
    /// 持っていない物は骨の組みに入れず、ここで物の点から物の姿勢を決める（FitToPoints）
    ///
    /// 差している間（S91f）は、物の姿勢を差す場所の骨から決める。キーの値は使わないが、差したフレームと抜いたフレームには差す場所の姿勢を物の値として打つ。
    /// 枠の違う値をカーブへ混ぜないので、差す前後のキーの間で物は手と一緒に差す場所へ動き、切り替えのフレームで跳ばない
    /// </summary>
    public sealed class HeldObjectRig
    {
        /// <summary>物を持つ手 1 つ</summary>
        public sealed class Hand
        {
            internal Entry entry;
            internal bool left;
            public BodyPointId id;
            public Transform control;
            /// <summary>乗っている骨</summary>
            public Transform bone;
            internal Vector3 gripPosition;
            internal Quaternion gripRotation;
            /// <summary>同じ骨の上のほかの点（持っている間は効かせない）</summary>
            internal List<BodyPointId> others = new List<BodyPointId> ();
            /// <summary>このクリップに握りのずれのキーがあるか（あれば、どのキーにも打つ）</summary>
            internal bool slideKeyed;
            /// <summary>このクリップにキーの握り（S99）のカーブがあるか（あれば、どのキーにも打つ）</summary>
            internal bool gripCurved;

            /// <summary>今の握りが、キーで持った握り（持つボタンを押した瞬間の手。S99）か。違えばキャラの設定の握り（gripPosition・gripRotation）</summary>
            public bool gripKeyed
            {
                get { return (left ? entry.values.leftGripKeyed : entry.values.rightGripKeyed) >= 0.5f; }
            }

            /// <summary>今の握りの場所（物のローカル。柄に沿ってずらす前）</summary>
            internal Vector3 heldGripPosition
            {
                get { return gripKeyed ? (left ? entry.values.leftGripPosition : entry.values.rightGripPosition) : gripPosition; }
            }

            /// <summary>今の握りの向き（物のローカル）</summary>
            internal Quaternion heldGripRotation
            {
                get { return gripKeyed ? Quaternion.Euler (left ? entry.values.leftGripRotation : entry.values.rightGripRotation) : gripRotation; }
            }

            /// <summary>キーの握りにする（keyed が false ならキャラの設定の握りへ戻す）</summary>
            internal void SetKeyedGrip (bool keyed, Vector3 position, Vector3 rotation)
            {
                HeldObjectControl values = entry.values;
                if (left) {
                    values.leftGripKeyed = keyed ? 1 : 0;
                    values.leftGripPosition = position;
                    values.leftGripRotation = rotation;
                }
                else {
                    values.rightGripKeyed = keyed ? 1 : 0;
                    values.rightGripPosition = position;
                    values.rightGripRotation = rotation;
                }
            }

            /// <summary>握りを柄に沿ってずらす量（メートル）</summary>
            public float slide
            {
                get { return left ? entry.values.leftSlide : entry.values.rightSlide; }
                set {
                    if (left) entry.values.leftSlide = value;
                    else entry.values.rightSlide = value;
                }
            }

            /// <summary>今の握りの場所（物のローカル。設定かキーの握りから、柄に沿ってずらした所）</summary>
            internal Vector3 gripAt
            {
                get { return heldGripPosition + entry.definition.gripAxis.normalized * slide; }
            }

            /// <summary>握りの柄の軸まわりの回り（ルート空間。Finish が解いた手首から決める。持っている間だけ使う）</summary>
            internal Quaternion twist = Quaternion.identity;

            /// <summary>
            /// 前に解いた姿勢での、手から見た物の柄の軸まわりの回り（物のローカル。手首に合わせた物の向き⁻¹ × 物の向き）。
            /// Finish が解いた手首と物から決める。hasRoll が false なら、まだ解いていない
            /// </summary>
            internal Quaternion roll = Quaternion.identity;
            internal bool hasRoll;

            /// <summary>今のフレームで持っているか</summary>
            public bool held
            {
                get { return (left ? entry.values.leftHold : entry.values.rightHold) >= 0.5f; }
            }

            public Entry owner
            {
                get { return entry; }
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

        /// <summary>物を差す場所 1 つ</summary>
        public sealed class Socket
        {
            internal Entry entry;
            public HeldObjectSocket definition;
            internal HeldObjectControl values;
            internal Transform control
            {
                get { return values.transform; }
            }
            /// <summary>キーの無いクリップでの、差しているかの値</summary>
            internal float rest;
            internal Transform bone;
            internal Quaternion boneRest = Quaternion.identity;
            /// <summary>骨の基準姿勢から見た物の場所と向き（ルート空間。リグエディタで動かす間は定義より先に変わる）</summary>
            internal Vector3 offset;
            internal Quaternion rotation = Quaternion.identity;
            internal RigProxies.Weight constraint;
        }

        /// <summary>物 1 つ</summary>
        public sealed class Entry
        {
            internal HeldObjectRig rig;
            public HeldObjectDefinition definition;
            public Transform control;
            public HeldObjectControl values;
            /// <summary>編集用の体の中の物</summary>
            public Transform editing;
            /// <summary>表示モデルの物</summary>
            public Transform display;
            internal Transform frameBone;
            internal Quaternion frameRest = Quaternion.identity;
            internal float restLeftHold;
            internal float restRightHold;
            public Hand left;
            public Hand right;
            /// <summary>物の上の全身 IK の点（S91e）</summary>
            public readonly List<ObjectPoint> points = new List<ObjectPoint> ();
            /// <summary>物を差す場所（S91f。キャラに骨の無い場所は入らない）</summary>
            public readonly List<Socket> sockets = new List<Socket> ();
            /// <summary>差す場所へ運んでいる間か（手が届かなくても物を寄せない。差す場所はずれない）</summary>
            internal bool bringing;
            /// <summary>運んでいる先の差す場所（bringing の間だけ）</summary>
            internal Socket bringTarget;
            /// <summary>差す場所へ運んだとき、握りに届かなかった手の差（メートル）。届かない分は差すフレームで手か物が跳ぶ</summary>
            public float bringGap;
            internal RigProxies.Weight leftConstraint;
            internal RigProxies.Weight rightConstraint;
            /// <summary>つかんで動かしている間の、物の姿勢（ルート空間）</summary>
            internal bool dragging;
            /// <summary>動かしている間、全身を引くか（重い点をつかんだ・物そのものを動かした。軽い点では引かない）</summary>
            internal bool pulling;
            /// <summary>物そのものを動かしているか（物の FK・手の点・CLI）。置いた姿勢が優先で、Pin の点には戻らない</summary>
            internal bool direct;
            internal Vector3 dragPosition;
            internal Quaternion dragRotation;
            /// <summary>今の物の姿勢（ルート空間。Finish が決める）</summary>
            internal Vector3 position;
            internal Quaternion rotation = Quaternion.identity;
            /// <summary>今の姿勢を物の上の点で決めたか（Prepare から Finish の間だけ）</summary>
            internal bool fitted;
            /// <summary>
            /// 物の姿勢の点（S102）のコントロールと名前と値。持っている間、物の骨の原点を物の姿勢へ寄せる全身 IK の点（点の一覧には出ない）。
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
            /// <summary>今の解き方で、物を骨の組み（持つ手の子）に入れたか（Prepare から Finish の間だけ）</summary>
            internal bool attached;
            /// <summary>動かしたがまだキーを打っていないか</summary>
            public bool keysPending;

            public string name
            {
                get { return definition.name; }
            }

            /// <summary>今のフレームで差している場所（差していなければ null）</summary>
            public Socket socket
            {
                get {
                    foreach (Socket item in sockets) {
                        if (item.values.socket >= 0.5f) return item;
                    }
                    return null;
                }
            }

            /// <summary>
            /// 差す場所へ物を動かして、持っている手を付いて来させる（まだ差さない）。腰が動くと差す場所も動くので、全身は引かない。
            /// 持っている手がここまで来てから放すので、差す前のキーの間で手が物に付いたまま動き、放すフレームで手が跳ばない
            /// </summary>
            public void BringTo (Socket target)
            {
                rig.GetSocketPose (target, out Vector3 position, out Quaternion rotation);
                ClearSockets ();
                PlaceWithoutPulling (position, rotation);
                bringing = true;
                bringTarget = target;
            }

            /// <summary>今の場所のまま差すのをやめる。今の姿勢を物の値に残すので、次のキーまでここから動く</summary>
            public void LeaveSocket ()
            {
                rig.GetPose (this, out Vector3 position, out Quaternion rotation);
                ClearSockets ();
                PlaceWithoutPulling (position, rotation);
            }

            /// <summary>どの場所にも差していない値にする（姿勢は動かさない）</summary>
            public void ClearSockets ()
            {
                foreach (Socket item in sockets) item.values.socket = 0;
                ApplyConstraintWeights (this);
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

            /// <summary>どちらの手も持っていなければ、定義の握りで持つ（差すのをやめて宙に残さない）</summary>
            public void HoldByDefaultIfFree ()
            {
                if (values.leftHold >= 0.5f || values.rightHold >= 0.5f) return;
                values.leftHold = definition.leftGrip != null && definition.leftGrip.hold ? 1 : 0;
                values.rightHold = definition.rightGrip != null && definition.rightGrip.hold ? 1 : 0;
                ApplyConstraintWeights (this);
            }

            /// <summary>差す場所へ入れて両手を放す（BringTo の解いた後に呼ぶ）</summary>
            public void EnterSocket (Socket target)
            {
                foreach (Socket item in sockets) item.values.socket = item == target ? 1 : 0;
                values.leftHold = 0;
                values.rightHold = 0;
                dragging = false;
                pulling = false;
                direct = false;
                bringing = false;
                keysPending = true;
                ApplyConstraintWeights (this);
            }

            /// <summary>
            /// 物のワールドの姿勢を置いて、つかんだ状態にする。解くまで骨は動かない。差している間は動かない（場所はリグエディタで直す）。
            /// 体の点と同じく、動かした分だけ固定する: 場所を変えたら物の姿勢の点の位置の強さを、向きを変えたら向きの強さを 1 にする（Pin）。
            /// 場所だけ動かしたときに向きまで固定すると、手首を Pin にして物を動かしても手首の角度が変わらなかった（11-07 ユーザー報告「通常の IK 制御では回転するはず」）
            /// </summary>
            public void DragTo (Vector3 worldPosition, Quaternion worldRotation)
            {
                if (socket != null) return;
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
        readonly List<Hand> hands_ = new List<Hand> ();
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
                values = control.gameObject.AddComponent<HeldObjectControl> (),
                editing = editing,
                display = display,
            };
            AddSockets (entry, createGameObject);
            // 差した物をキーの無いクリップで持たせると、手が腰へ引かれる
            bool free = entry.socket == null;
            entry.restLeftHold = free && definition.leftGrip != null && definition.leftGrip.hold ? 1 : 0;
            entry.restRightHold = free && definition.rightGrip != null && definition.rightGrip.hold ? 1 : 0;
            entry.frameBone = FindFrameBone (definition.frame);
            if (entry.frameBone != null) entry.frameRest = rig_.GetRestRootRotation (entry.frameBone);
            // 基準の値（キーの無いクリップで物を置く所）。枠の骨の付け根から、体の向きで書いた所
            control.localPosition = rig_.BodyToRoot (definition.restPosition) / rig_.humanScale;
            control.localRotation = body_ * Quaternion.Euler (definition.restRotation);
            entry.values.leftHold = entry.restLeftHold;
            entry.values.rightHold = entry.restRightHold;
            BindConstraints (entry);
            entries_.Add (entry);
            rig_.AddBakedBone (display);
            AddPoints (entry, createGameObject);
        }

        void AddSockets (Entry entry, System.Func<string, GameObject> createGameObject)
        {
            if (entry.definition.sockets == null) return;
            foreach (HeldObjectSocket source in entry.definition.sockets) {
                Transform bone = rig_.GetEditingBone (source.bone);
                if (bone == null) {
                    rig_.binding.notes.Add (Tr ("HELD_OBJECT_NO_SOCKET_BONE", entry.name, source.name, source.bone));
                    continue;
                }
                Transform control = rig_.CreateControl (RigPaths.ObjectSocket (entry.name, source.name), createGameObject);
                Socket socket = new Socket {
                    entry = entry,
                    definition = source,
                    values = control.gameObject.AddComponent<HeldObjectControl> (),
                    rest = source.name == entry.definition.restSocket ? 1 : 0,
                    bone = bone,
                    boneRest = rig_.GetRestRootRotation (bone),
                    offset = body_ * source.position,
                    rotation = body_ * Quaternion.Euler (source.rotation),
                };
                socket.values.socket = socket.rest;
                entry.sockets.Add (socket);
            }
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

        /// <summary>全身 IK の点へ手を結び付ける（全身 IK を作った後）。手首の点が無い手は持てない</summary>
        public void BindHands (FullBodyRig body)
        {
            hands_.Clear ();
            if (body == null) return;
            foreach (Entry entry in entries_) {
                entry.right = CreateHand (entry, body, HumanBodyBones.RightHand, entry.definition.rightGrip, false);
                entry.left = CreateHand (entry, body, HumanBodyBones.LeftHand, entry.definition.leftGrip, true);
                body.AddHeldPosePoint (entry.poseId, entry.poseControl, entry.editing);
            }
        }

        Hand CreateHand (Entry entry, FullBodyRig body, HumanBodyBones bone, HeldObjectGrip grip, bool left)
        {
            BodyPointId id = bone;
            if (grip == null || !body.HasPoint (id)) return null;
            Hand hand = new Hand {
                entry = entry,
                left = left,
                id = id,
                control = body.GetControl (id),
                bone = body.GetBone (id),
                gripPosition = grip.position,
                gripRotation = Quaternion.Euler (grip.rotation),
            };
            foreach (BodyPointId other in body.pointIds) {
                if (!other.Equals (id) && body.GetBone (other) == hand.bone) hand.others.Add (other);
            }
            hands_.Add (hand);
            return hand;
        }

        /// <summary>手首の点の持ち主の手（持ち物が持たない手なら null）</summary>
        public Hand FindHand (BodyPointId id)
        {
            foreach (Hand hand in hands_) {
                if (hand.id.Equals (id)) return hand;
            }
            return null;
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
        /// ゲームの拘束の重みへつなぐ（S91c）。持っているかの値を拘束の重みへ写し、焼くとゲームのパスへ行く。
        /// 手で打った重みより優先する（手が物から外れないようにする拘束なので、持っている間だけ効かせる）
        /// </summary>
        void BindConstraints (Entry entry)
        {
            entry.leftConstraint = FindWeight (entry.definition.leftGrip != null ? entry.definition.leftGrip.constraint : null);
            entry.rightConstraint = FindWeight (entry.definition.rightGrip != null ? entry.definition.rightGrip.constraint : null);
            foreach (Socket socket in entry.sockets) socket.constraint = FindWeight (socket.definition.constraint);
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

        static void ApplyConstraintWeights (Entry entry)
        {
            if (entry.leftConstraint != null) entry.leftConstraint.value = entry.values.leftHold;
            if (entry.rightConstraint != null) entry.rightConstraint.value = entry.values.rightHold;
            Socket current = entry.socket;
            foreach (Socket socket in entry.sockets) {
                if (socket.constraint != null) socket.constraint.value = socket == current ? 1 : 0;
            }
        }

        /// <summary>持っているか・差しているかの値を基準へ戻す</summary>
        public void ResetHolds ()
        {
            foreach (Entry entry in entries_) {
                entry.values.leftHold = entry.restLeftHold;
                entry.values.rightHold = entry.restRightHold;
                foreach (Socket socket in entry.sockets) socket.values.socket = socket.rest;
                entry.values.leftSlide = 0;
                entry.values.rightSlide = 0;
                entry.left?.SetKeyedGrip (false, Vector3.zero, Vector3.zero);
                entry.right?.SetKeyedGrip (false, Vector3.zero, Vector3.zero);
                ApplyConstraintWeights (entry);
            }
        }

        /// <summary>
        /// 持っているかの値を、その時刻以前でいちばん近いキーの値にする（キーの間で補間しない。キーが無ければ基準の値）
        /// </summary>
        public void ApplyHoldKeys (AnimationClip clip, float time)
        {
            if (clip == null) return;
            foreach (Entry entry in entries_) {
                string path = RigPaths.Object (entry.name);
                entry.values.leftHold = EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), HeldObjectControl.kLeftHoldProperty, time, entry.restLeftHold);
                entry.values.rightHold = EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), HeldObjectControl.kRightHoldProperty, time, entry.restRightHold);
                ApplySocketKeys (clip, entry, path, time);
                ApplySlideKeys (clip, entry, path, time);
                ApplyGripKeys (clip, entry.left, path, time);
                ApplyGripKeys (clip, entry.right, path, time);
                ApplyConstraintWeights (entry);
            }
        }

        /// <summary>
        /// 差しているかの値を、物の最後のキー（手のカーブはどのキーにもある）の時点の値にする。場所を足す前に打った物のキーには場所のカーブが無いので、そこでは差していない。
        /// 物のキーがどれも無ければ基準の値
        /// </summary>
        static void ApplySocketKeys (AnimationClip clip, Entry entry, string path, float time)
        {
            bool keyed = EditingClip.TryGetStepKey (clip, path, typeof (HeldObjectControl), HeldObjectControl.kLeftHoldProperty, time, out Keyframe last);
            float margin = 0.5f / clip.frameRate;
            foreach (Socket socket in entry.sockets) {
                bool found = EditingClip.TryGetStepKey (clip, RigPaths.ObjectSocket (entry.name, socket.definition.name), typeof (HeldObjectControl), HeldObjectControl.kSocketProperty, time, out Keyframe key);
                if (found && (!keyed || key.time >= last.time - margin)) socket.values.socket = key.value;
                else socket.values.socket = keyed ? 0 : socket.rest;
            }
        }

        /// <summary>握りのずれの値を当てる。キーの間も補間するので、手が柄の上を滑る</summary>
        static void ApplySlideKeys (AnimationClip clip, Entry entry, string path, float time)
        {
            entry.values.leftSlide = EditingClip.GetEasedValue (clip, path, typeof (HeldObjectControl), HeldObjectControl.kLeftSlideProperty, HeldObjectControl.kLeftHoldProperty, time, out bool leftKeyed);
            entry.values.rightSlide = EditingClip.GetEasedValue (clip, path, typeof (HeldObjectControl), HeldObjectControl.kRightSlideProperty, HeldObjectControl.kLeftHoldProperty, time, out bool rightKeyed);
            if (entry.left != null) entry.left.slideKeyed = leftKeyed;
            if (entry.right != null) entry.right.slideKeyed = rightKeyed;
        }

        /// <summary>
        /// キーで持った握り（S99）を当てる。持っているかと同じく、その時刻以前でいちばん近いキーの値（キーの間で補間しない。持ち替えたフレームで握りが変わる）。
        /// キーが無ければキャラの設定の握り
        /// </summary>
        static void ApplyGripKeys (AnimationClip clip, Hand hand, string path, float time)
        {
            if (hand == null) return;
            string keyed = hand.left ? HeldObjectControl.kLeftGripKeyedProperty : HeldObjectControl.kRightGripKeyedProperty;
            string position = hand.left ? HeldObjectControl.kLeftGripPositionProperty : HeldObjectControl.kRightGripPositionProperty;
            string rotation = hand.left ? HeldObjectControl.kLeftGripRotationProperty : HeldObjectControl.kRightGripRotationProperty;
            hand.gripCurved = EditingClip.TryGetStepKey (clip, path, typeof (HeldObjectControl), keyed, float.PositiveInfinity, out Keyframe _);
            if (!hand.gripCurved) {
                hand.SetKeyedGrip (false, Vector3.zero, Vector3.zero);
                return;
            }
            System.Func<string, Vector3> read = name => new Vector3 (
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".x", time, 0),
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".y", time, 0),
                EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), name + ".z", time, 0));
            hand.SetKeyedGrip (EditingClip.GetStepValue (clip, path, typeof (HeldObjectControl), keyed, time, 0) >= 0.5f, read (position), read (rotation));
        }

        /// <summary>time より後ろに物のキーがあるか（切り替えはそのキーまでしか続かない）</summary>
        public static bool HasLaterKey (AnimationClip clip, Entry entry, float time)
        {
            return EditingClip.TryGetStepKey (clip, RigPaths.Object (entry.name), typeof (HeldObjectControl), HeldObjectControl.kLeftHoldProperty, float.PositiveInfinity, out Keyframe last) && last.time > time + 0.5f / clip.frameRate;
        }

        /// <summary>つかんで動かすのをやめる（クリップから姿勢を当て直したとき）</summary>
        public void EndDrag ()
        {
            foreach (Entry entry in entries_) {
                entry.dragging = false;
                entry.pulling = false;
                entry.direct = false;
                entry.bringing = false;
                entry.bringGap = 0;
            }
        }

        /// <summary>差す場所の今の姿勢（ルート空間）。骨の基準姿勢からの動きに乗せる</summary>
        internal void GetSocketPose (Socket socket, out Vector3 position, out Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            Vector3 origin = root.InverseTransformPoint (socket.bone.position);
            Quaternion turn = Quaternion.Inverse (root.rotation) * socket.bone.rotation * Quaternion.Inverse (socket.boneRest);
            position = origin + turn * socket.offset;
            rotation = turn * socket.rotation;
        }

        /// <summary>物の姿勢（ルート空間）。差していれば差す場所、つかんでいれば置いた姿勢、そうでなければキーの値を今の枠の骨に乗せた姿勢</summary>
        void GetPose (Entry entry, out Vector3 position, out Quaternion rotation)
        {
            Socket socket = entry.socket;
            if (socket != null) {
                GetSocketPose (socket, out position, out rotation);
                return;
            }
            if (entry.dragging) {
                position = entry.dragPosition;
                rotation = entry.dragRotation;
                return;
            }
            GetFrame (entry, out Vector3 origin, out Quaternion turn);
            position = origin + turn * entry.control.localPosition * rig_.humanScale;
            rotation = turn * entry.control.localRotation;
        }

        /// <summary>枠の今の付け根と、基準姿勢からの回り（ルート空間）</summary>
        void GetFrame (Entry entry, out Vector3 origin, out Quaternion turn)
        {
            origin = Vector3.zero;
            turn = Quaternion.identity;
            if (entry.frameBone == null) return;
            Transform root = rig_.root.transform;
            origin = root.InverseTransformPoint (entry.frameBone.position);
            turn = Quaternion.Inverse (root.rotation) * entry.frameBone.rotation * Quaternion.Inverse (entry.frameRest);
        }

        /// <summary>物の原点から、今の握り（柄の軸まわりに回した後）までの差（ルート空間。rotation は物の向き）</summary>
        Vector3 GripOffset (Hand hand, Quaternion rotation)
        {
            Vector3 at = hand.gripAt;
            Vector3 axis = hand.entry.definition.gripAxis;
            if (axis.sqrMagnitude < 1e-8f) return rotation * at;
            axis.Normalize ();
            Vector3 onAxis = axis * Vector3.Dot (at, axis);
            return rotation * onAxis + hand.twist * (rotation * (at - onAxis));
        }

        /// <summary>
        /// 全身 IK を解く前に呼ぶ（S102）。持っている物は、持つ手の骨の子として骨の組みに入れ（持つ・離す・差すが変わったときだけ組み直す）、
        /// 物を手の今の姿勢 × 握りへ置いて、物の姿勢（キー・つかんで置いた姿勢・差す場所）を物の姿勢の点の目標にする。
        /// 両手で持つ物は右手の子にし、左手は輪の関節でつなぐ。持っている手の点もふつうの点（11-07 ユーザー指示。物は握りの関節で手に付き、物と手の点の状態どおりに解く）。
        /// 持っていない物は組みに入れない（Finish で物の点から姿勢を決める）
        /// </summary>
        /// <param name="only">物の上の点のうち、効かせる点（キーの間の Locked の点。null なら動かした後で、固定している点を全部効かせる）</param>
        public void Prepare (FullBodyRig body, ICollection<Transform> only = null)
        {
            fitOnly_ = only;
            foreach (Entry entry in entries_) BeginPointDrag (body, entry);
            attachments_.Clear ();
            loops_.Clear ();
            foreach (Entry entry in entries_) {
                Hand primary = PrimaryHand (body, entry);
                if (primary == null) continue;
                // 差している・差す場所へ運んでいる物は動かさない（動かない剛体にして、持っている手を全部輪でつなぐ。手だけが物へ伸びる）
                bool fixedPose = Fixed (entry);
                Socket socket = entry.socket ?? entry.bringTarget;
                Transform parent = !fixedPose ? primary.bone : socket != null && body.IsSolvedBody (socket.bone) ? socket.bone : null;
                attachments_.Add (new FullBodyRig.HeldAttachment {
                    body = entry.editing,
                    parent = parent,
                    axis = entry.definition.gripAxis,
                    fixedPose = fixedPose,
                    // 差す骨から見た物の向き（GetSocketPose の物の向き = 骨 × 骨の基準⁻¹ × 差す向き）
                    weld = fixedPose && parent != null ? Quaternion.Normalize (Quaternion.Inverse (socket.boneRest) * socket.rotation) : Quaternion.identity,
                });
            }
            body.SetHeldAttachments (attachments_);
            foreach (Entry entry in entries_) {
                Hand primary = PrimaryHand (body, entry);
                entry.attached = primary != null && body.IsHeldAttached (entry.editing);
                if (!entry.attached) {
                    body.SetPositionWeight (entry.poseId, 0);
                    body.SetRotationWeight (entry.poseId, 0);
                    body.UnmarkGrabbed (entry.poseId);
                    continue;
                }
                GetPose (entry, out entry.position, out entry.rotation);
                bool fixedPose = Fixed (entry);
                if (fixedPose) Place (entry);
                else PlaceOnHand (body, entry, primary);
                foreach (Hand hand in new[] { entry.right, entry.left }) {
                    if (hand == null || !hand.held || (hand == primary && !fixedPose)) continue;
                    loops_.Add (new FullBodyRig.HeldLoop {
                        hand = hand.id,
                        body = entry.editing,
                        grip = hand.gripAt,
                        axis = entry.definition.gripAxis,
                        toObject = Quaternion.Normalize (body_ * Quaternion.Inverse (hand.heldGripRotation)),
                    });
                }
                SetPosePoint (body, entry);
            }
            body.SetHeldLoops (loops_);
        }

        readonly List<FullBodyRig.HeldAttachment> attachments_ = new List<FullBodyRig.HeldAttachment> ();
        readonly List<FullBodyRig.HeldLoop> loops_ = new List<FullBodyRig.HeldLoop> ();

        /// <summary>動かさない物か（差している・差す場所へ運んでいる）</summary>
        static bool Fixed (Entry entry)
        {
            return entry.socket != null || entry.bringing;
        }

        /// <summary>物を子にする手（右手で持っていれば右手、そうでなければ左手）。持っていない・手の骨を解かなければ null</summary>
        static Hand PrimaryHand (FullBodyRig body, Entry entry)
        {
            foreach (Hand hand in new[] { entry.right, entry.left }) {
                if (hand != null && hand.held && body.IsSolvedBody (hand.bone)) return hand;
            }
            return null;
        }

        /// <summary>
        /// 物を、手の今の姿勢（解く前の姿勢）× 握りへ置く。柄の軸は手に合わせ、軸まわりは物の目標の向きに一番近くする（軸まわりは関節が自由に回す）。
        /// 解く前に置いた所が、手から見た物の場所と向き（ソルバーの関節）になる
        /// </summary>
        void PlaceOnHand (FullBodyRig body, Entry entry, Hand hand)
        {
            if (!body.TryGetCapturedValues (hand.control, out Vector3 local, out Quaternion wrist)) return;
            // 物の向き × 握りの向き × 体の向き⁻¹ が手首の向きになる物の向き（柄の軸まわりも手首に合わせた向き）
            Quaternion exact = Quaternion.Normalize (wrist * body_ * Quaternion.Inverse (hand.heldGripRotation));
            Vector3 axis = entry.definition.gripAxis;
            Vector3 at = hand.gripAt;
            Quaternion rotation = exact;
            Vector3 onAxis = Vector3.zero;
            if (axis.sqrMagnitude > 1e-8f) {
                axis.Normalize ();
                // 物そのもの・物の点をつかんで動かしている間、向きを留めていなければ、前に解いた姿勢の手の中の回りのままにする。
                // つかんでいる間の物の向き（置いた姿勢）は前に解いた後の向きで、解き直しはキーの手から始まる。前に解いた手に対する回りを
                // キーの手に一番近い向きで当てはめると、手がキーからねじれた分だけ解くたびに回りが足され、丸い棒が手の中で柄の軸まわりに回り続けた
                // （11-07 ユーザー報告「表示しているボーンが回転しまくる。見た目は回転していないのに」。60 回動かす間に ±170°）
                if (entry.dragging && hand.hasRoll && entry.poseValues.rotationWeight <= 0) rotation = Quaternion.Normalize (exact * hand.roll);
                else rotation = Quaternion.Normalize (Quaternion.FromToRotation (entry.rotation * axis, exact * axis) * entry.rotation);
                onAxis = axis * Vector3.Dot (at, axis);
            }
            // 握りの場所: 柄の軸の上の分は物と一緒に、柄から離れた分は手と一緒に回る
            Vector3 position = local * rig_.humanScale - rotation * onAxis - exact * (at - onAxis);
            Transform root = rig_.root.transform;
            entry.editing.SetPositionAndRotation (root.TransformPoint (position), root.rotation * rotation);
        }

        /// <summary>
        /// 物の姿勢の点の目標を入れる。状態（強さ・Lock）は、ほかの点と同じくキーと持ち物の対象で決めた値のまま（11-07 ユーザー指示「汎用的な制御にすることが重要、体の点の決まりどおりに」）。
        /// 物そのものを動かしていればつかんだ点にする（強さは動かした分だけ DragTo が入れる。体の点をつかんで動かしたときと同じ。Lock は今のまま）。
        /// 引かずに置くだけ（差す場所から抜いた所に残す）なら、つかんだ点にしない
        /// </summary>
        void SetPosePoint (FullBodyRig body, Entry entry)
        {
            entry.poseControl.localPosition = entry.position / rig_.humanScale;
            entry.poseControl.localRotation = Quaternion.Normalize (entry.rotation * Quaternion.Inverse (rig_.GetRestRootRotation (entry.editing)));
            bool direct = entry.dragging && entry.direct;
            bool grabbed = direct && entry.pulling;
            entry.fitted = !direct && AnyPointActive (body, entry);
            // 引かずに置くだけ（差す場所から抜いた所に残す）は、場所も向きもそこに留める
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
            if (entry.socket != null) return false;
            foreach (ObjectPoint point in entry.points) {
                if (body.IsGrabbed (point.id)) return true;
                if (fitOnly_ != null && !fitOnly_.Contains (point.control)) continue;
                if (body.GetPositionWeight (point.id) > 0 || body.GetRotationWeight (point.id) > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 持っている物の姿勢の点のコントロール（キーのあるフレームでも、持っている手を物に合わせて解く）を controls に足した集まり。足す物が無ければ controls
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
            if (entry.socket != null) return;
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
            if (entry.points.Count == 0 || entry.socket != null) return false;
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
        /// 全身 IK を解いた後に呼ぶ。持っている物は、解いた物の骨から物の姿勢を読む（手は物の骨の親なので、握りから外れない）。
        /// 持っていない物は、決まった枠で物の姿勢を求めて物の点を効かせる。つかんでいた物は、解いた後の姿勢を値に残す
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
                foreach (Hand hand in new[] { entry.right, entry.left }) {
                    if (hand != null && hand.held) UpdateTwist (body, hand);
                }
            }
            Place ();
            foreach (Entry entry in entries_) {
                if (entry.bringing) entry.bringGap = GetHandGap (body, entry);
            }
            // 点で決めた姿勢も、動かした後なら値に残す（打つキーは解いた姿勢。キーの間で Lock の点に留めた姿勢はクリップの値を変えない）
            // 差している間の値は使わないが、抜いたフレームと差す前のキーの間で物がここへ向かうように、打つ値は差す場所の姿勢にする
            // 持っている物は、物の上の点で姿勢を決めても値に残さない（キーを打つときに SyncToBodies で解いた物の骨の姿勢へ合わせ直す。体の骨と同じ）。
            // 解くたびに残すと、次に解くときの元の姿勢（キー）が前の答えになり、手首を引く間に物が手の中で柄の軸まわりに回り続けた
            // （11-08 ユーザー報告「手首を動かしていると、たまに棒の姿勢がワープする」。物の上の点を Pin にした棒が 1 回 20° 回った）
            foreach (Entry entry in entries_) {
                if (entry.dragging || (edited && ((entry.fitted && !entry.attached) || entry.socket != null))) StoreValues (entry);
            }
            fitOnly_ = null;
        }

        /// <summary>
        /// 握りの柄の軸まわりの回り（ルート空間。twist）を、解いた手首と物の姿勢から決め直す。手首の向き = twist × 物の向き × 握りの向き × 体の向き⁻¹
        /// </summary>
        void UpdateTwist (FullBodyRig body, Hand hand)
        {
            hand.twist = Quaternion.identity;
            hand.hasRoll = false;
            if (!body.TryGetCapturedValues (hand.control, out Vector3 unused, out Quaternion wrist)) return;
            Quaternion exact = Quaternion.Normalize (wrist * body_ * Quaternion.Inverse (hand.heldGripRotation));
            hand.twist = Quaternion.Normalize (exact * Quaternion.Inverse (hand.entry.rotation));
            hand.roll = Quaternion.Normalize (Quaternion.Inverse (exact) * hand.entry.rotation);
            hand.hasRoll = true;
        }

        /// <summary>
        /// キーを打つ前に呼ぶ（体の点の BodyPointTarget.SyncToBone と同じ）。持っている物の値（物のキー・物の姿勢の点の目標）を、解いた物の骨の姿勢へ合わせ直す。
        /// 物は手の子の骨なので、ほかの骨と同じく解いた姿勢をキーにする。物の上の点が効いているときだけ値に残していたため、手首を引いて離すと、
        /// 元の場所の物の姿勢の点（Pin）が手首を引き戻した（11-07 ユーザー報告「手首を動かすと、ドラッグを離すと元の位置に戻っている」。IkArm で 7〜20cm）。
        /// 差している・差す場所へ運んでいる物は骨に固定なので、そのまま
        /// </summary>
        public void SyncToBodies ()
        {
            foreach (Entry entry in entries_) {
                if (entry.attached && !Fixed (entry)) StoreValues (entry);
            }
        }

        /// <summary>持っている手のうち、いちばん握りに届いていない差（メートル）</summary>
        float GetHandGap (FullBodyRig body, Entry entry)
        {
            float gap = 0;
            Transform root = rig_.root.transform;
            foreach (Hand hand in new[] { entry.right, entry.left }) {
                if (hand == null || !hand.held || !body.TryGetBonePointWorld (hand.id, out Vector3 reached)) continue;
                gap = Mathf.Max (gap, Vector3.Distance (root.InverseTransformPoint (reached), entry.position + GripOffset (hand, entry.rotation)));
            }
            return gap;
        }

        /// <summary>キーの無いときの T ポーズでの物の場所（ルート空間）</summary>
        internal Vector3 GetRestPosition (Entry entry)
        {
            Vector3 origin = entry.frameBone != null ? rig_.GetRestRootPosition (entry.frameBone) : Vector3.zero;
            return origin + rig_.BodyToRoot (entry.definition.restPosition);
        }

        /// <summary>今の物の姿勢を、今の枠から見た値へ入れる</summary>
        void StoreValues (Entry entry)
        {
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

        /// <summary>手の握りを、ワールドの姿勢で置き直す（リグエディタで動かす間。定義へは StoreGrip で書く）</summary>
        public void SetGrip (Hand hand, Vector3 worldPosition, Quaternion worldRotation)
        {
            Transform root = rig_.root.transform;
            Quaternion inverse = Quaternion.Inverse (root.rotation * hand.entry.rotation);
            hand.gripPosition = Quaternion.Inverse (hand.entry.rotation) * (root.InverseTransformPoint (worldPosition) - hand.entry.position);
            hand.gripRotation = Quaternion.Normalize (inverse * worldRotation);
        }

        /// <summary>手の、キャラの設定の握りのワールドの姿勢（リグエディタで直す握り。キーで持った握りではない）</summary>
        public void GetGrip (Hand hand, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            Transform root = rig_.root.transform;
            Vector3 at = hand.gripPosition + hand.entry.definition.gripAxis.normalized * hand.slide;
            worldPosition = root.TransformPoint (hand.entry.position + hand.entry.rotation * at);
            worldRotation = root.rotation * hand.entry.rotation * hand.gripRotation;
        }

        /// <summary>
        /// 手の今の手首（骨の上の手首の点）を、物から見た握りにして、キーで持った握りにする（S99。持つボタンを押した瞬間の手で持つ）。
        /// 握りのずれは 0 に戻す（ずれの分は握りの場所に入る）。手首の点の目標は「物の姿勢 × 握り」なので、持っても手は動かない。作れたら true
        /// </summary>
        public bool GrabAtCurrent (Hand hand, FullBodyRig body)
        {
            if (hand == null || body == null || !body.TryGetCapturedValues (hand.control, out Vector3 local, out Quaternion localRotation)) return false;
            // 値から作った姿勢ではなく、Finish が決めた今の姿勢（届かない手へ寄せた分も入る。見えている物）から見て、その姿勢を値にも残す。
            // 値のままだと、持った後に解き直したとき物が寄せる前の場所へ戻り、手も一緒に動く
            Vector3 position = hand.entry.position;
            Quaternion inverse = Quaternion.Inverse (hand.entry.rotation);
            if (hand.entry.socket == null) StoreValues (hand.entry);
            hand.slide = 0;
            hand.SetKeyedGrip (true, inverse * (local * rig_.humanScale - position), Quaternion.Normalize (inverse * localRotation * body_).eulerAngles);
            hand.entry.keysPending = true;
            return true;
        }

        /// <summary>今の握りを定義へ書く。Undo と保存は呼ぶ側</summary>
        public void StoreGrip (Hand hand)
        {
            HeldObjectGrip grip = hand.left ? hand.entry.definition.leftGrip : hand.entry.definition.rightGrip;
            grip.position = hand.gripPosition;
            grip.rotation = hand.gripRotation.eulerAngles;
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

        /// <summary>差す場所の、ワールドの姿勢</summary>
        public void GetSocketWorld (Socket socket, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            GetSocketPose (socket, out Vector3 position, out Quaternion rotation);
            Transform root = rig_.root.transform;
            worldPosition = root.TransformPoint (position);
            worldRotation = root.rotation * rotation;
        }

        /// <summary>差す場所を、ワールドの姿勢で置き直す（リグエディタで動かす間。定義へは StoreSocket で書く）</summary>
        public void SetSocketWorld (Socket socket, Vector3 worldPosition, Quaternion worldRotation)
        {
            Transform root = rig_.root.transform;
            Vector3 origin = root.InverseTransformPoint (socket.bone.position);
            Quaternion turn = Quaternion.Inverse (root.rotation) * socket.bone.rotation * Quaternion.Inverse (socket.boneRest);
            Quaternion inverse = Quaternion.Inverse (turn);
            socket.offset = inverse * (root.InverseTransformPoint (worldPosition) - origin);
            socket.rotation = Quaternion.Normalize (inverse * Quaternion.Inverse (root.rotation) * worldRotation);
        }

        /// <summary>今の差す場所を定義へ書く。Undo と保存は呼ぶ側</summary>
        public void StoreSocket (Socket socket)
        {
            Quaternion inverse = Quaternion.Inverse (body_);
            socket.definition.position = inverse * socket.offset;
            socket.definition.rotation = (inverse * socket.rotation).eulerAngles;
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
