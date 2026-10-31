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
            return errors;
        }
    }

    /// <summary>
    /// 持ち物を編集用の体に当てたもの（S91）。物が導き、手が従う: 物の姿勢をキーに持ち、持っている手の目標を毎回「物の姿勢 × 握り」で作る。
    /// 先に物を決めてから手を合わせる順なので、解く順が輪にならず、ソルバーは今のまま使える。
    ///
    /// 物の姿勢の値（＝編集用クリップに保存する値。Controls/Object/名前 の Transform）:
    /// - localRotation = 枠の骨の基準姿勢からの回りを戻した、ルートから見た物の向き
    /// - localPosition = 枠の骨から物までの向き（同じ回りを戻した） / humanScale
    /// 枠が Chest なら、腰や胸が動いても物は胸に付いて動く。手首の点の値は持っている間は打たず、ここで作る。
    ///
    /// 解き方は Prepare → 全身 IK → Finish の順。Prepare は、物を動かしているときだけ両手を「つかんだ点」にして全身を引かせる。
    /// それ以外は手を解かずに体を決め、Finish で決まった枠から手の目標を作って手足だけを解く。手が届かないときは物の方を体へ寄せる。
    ///
    /// 物の上の点（S91e）はふつうの全身 IK の点で、状態・キー・選び方・ハンドルは変わらない。物は剛体 1 つなので、ソルバーへ渡さずここで物の姿勢を決める（FitToPoints）。
    /// 重さの決まりは体の点と同じ: 重い点をつかむと物は回らずに動き、体も付いてくる。軽い点をつかむと、重い点を留めたまま物が回り、動くのは腕だけ
    /// </summary>
    public sealed class HeldObjectRig
    {
        /// <summary>物を体へ寄せるのを始める、握りと手の差（メートル）</summary>
        const float kReachTolerance = 0.001f;
        /// <summary>手が届くまで物を寄せて解き直す回数の上限</summary>
        const int kReachPasses = 6;

        /// <summary>物を持つ手 1 つ</summary>
        public sealed class Hand
        {
            internal Entry entry;
            internal bool left;
            public BodyPointId id;
            public Transform control;
            /// <summary>乗っている骨</summary>
            public Transform bone;
            /// <summary>鎖骨（無いキャラは null）。持っている間は動かさない</summary>
            internal Transform shoulder;
            internal Vector3 gripPosition;
            internal Quaternion gripRotation;
            /// <summary>同じ骨の上のほかの点（持っている間は効かせない）</summary>
            internal List<BodyPointId> others = new List<BodyPointId> ();

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
            /// <summary>今の姿勢を物の上の点で決めたか（Finish の中だけ）</summary>
            internal bool fitted;
            /// <summary>動かしたがまだキーを打っていないか</summary>
            public bool keysPending;

            public string name
            {
                get { return definition.name; }
            }

            /// <summary>物のワールドの姿勢を置いて、つかんだ状態にする。解くまで骨は動かない</summary>
            public void DragTo (Vector3 worldPosition, Quaternion worldRotation)
            {
                Transform root = rig.rig_.root.transform;
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

            /// <summary>手が hand の場所へ来るように物を動かす（持っている手の点をつかむ操作）</summary>
            public void DragHandTo (Hand hand, Vector3 worldPosition)
            {
                GetWorldPose (out Vector3 position, out Quaternion rotation);
                DragTo (worldPosition - rotation * hand.gripPosition, rotation);
            }

            /// <summary>手の点を中心に、物を delta（ワールド）だけ回す（持っている手の点を回す操作）</summary>
            public void TurnAboutHand (Hand hand, Quaternion delta)
            {
                GetWorldPose (out Vector3 position, out Quaternion rotation);
                TurnAbout (position + rotation * hand.gripPosition, delta);
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
                    Transform parent = definition.parentBone == HumanBodyBones.LastBone ? animator.transform : animator.GetBoneTransform (definition.parentBone);
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
            Entry entry = new Entry {
                rig = this,
                definition = definition,
                control = control,
                values = control.gameObject.AddComponent<HeldObjectControl> (),
                editing = editing,
                display = display,
                restLeftHold = definition.leftGrip != null && definition.leftGrip.hold ? 1 : 0,
                restRightHold = definition.rightGrip != null && definition.rightGrip.hold ? 1 : 0,
            };
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
                shoulder = rig_.GetEditingBone (left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder),
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
            entry.leftConstraint = FindWeight (entry.definition.leftGrip);
            entry.rightConstraint = FindWeight (entry.definition.rightGrip);
            ApplyConstraintWeights (entry);
        }

        RigProxies.Weight FindWeight (HeldObjectGrip grip)
        {
            if (grip == null || string.IsNullOrEmpty (grip.constraint) || rig_.rigProxies == null) return null;
            Transform owner = rig_.FindControl (RigPaths.kGame + "/" + grip.constraint);
            if (owner != null) {
                foreach (RigProxies.Weight weight in rig_.rigProxies.weights) {
                    if (weight.owner == owner && typeof (RigConstraintProxy).IsAssignableFrom (weight.proxyType)) return weight;
                }
            }
            rig_.binding.notes.Add (Tr ("HELD_OBJECT_CONSTRAINT_NOT_FOUND", grip.constraint));
            return null;
        }

        static void ApplyConstraintWeights (Entry entry)
        {
            if (entry.leftConstraint != null) entry.leftConstraint.value = entry.values.leftHold;
            if (entry.rightConstraint != null) entry.rightConstraint.value = entry.values.rightHold;
        }

        /// <summary>持っているかの値を基準へ戻す</summary>
        public void ResetHolds ()
        {
            foreach (Entry entry in entries_) {
                entry.values.leftHold = entry.restLeftHold;
                entry.values.rightHold = entry.restRightHold;
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
                ApplyConstraintWeights (entry);
            }
        }

        /// <summary>つかんで動かすのをやめる（クリップから姿勢を当て直したとき）</summary>
        public void EndDrag ()
        {
            foreach (Entry entry in entries_) {
                entry.dragging = false;
                entry.pulling = false;
                entry.direct = false;
            }
        }

        /// <summary>物の姿勢（ルート空間）。つかんでいれば置いた姿勢、そうでなければキーの値を今の枠の骨に乗せた姿勢</summary>
        void GetPose (Entry entry, out Vector3 position, out Quaternion rotation)
        {
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

        /// <summary>手首の点を、物の姿勢と握りから作った目標へ置く</summary>
        void PlaceHand (Hand hand, Vector3 position, Quaternion rotation)
        {
            hand.control.localPosition = (position + rotation * hand.gripPosition) / rig_.humanScale;
            hand.control.localRotation = Quaternion.Normalize (rotation * hand.gripRotation * Quaternion.Inverse (body_));
        }

        /// <summary>
        /// 全身 IK を解く前に呼ぶ。物をつかんでいれば、持っている手を物の目標へ置いて「つかんだ点」にする（全身が付いてくる）。
        /// 枠がルートの物は、手をそのまま置いた場所に留める（体が動いても物は動かない）。
        /// それ以外の手は解かない（手首の点のキーも、同じ骨のほかの点も効かせない。手は Finish で合わせる）
        /// </summary>
        /// <param name="only">物の上の点のうち、効かせる点（キーの間の Locked の点。null なら動かした後で、固定している点を全部効かせる）</param>
        public void Prepare (FullBodyRig body, ICollection<Transform> only = null)
        {
            fitOnly_ = only;
            fixedShoulders_.Clear ();
            foreach (Entry entry in entries_) BeginPointDrag (body, entry);
            foreach (Hand hand in hands_) {
                if (!hand.held) continue;
                if (hand.shoulder != null) fixedShoulders_.Add (hand.shoulder);
                Entry entry = hand.entry;
                foreach (BodyPointId other in hand.others) {
                    body.SetPositionWeight (other, 0);
                    body.SetRotationWeight (other, 0);
                }
                bool grabbed = entry.dragging && entry.pulling;
                bool pulls = grabbed || entry.frameBone == null;
                float weight = pulls ? 1 : 0;
                body.SetPositionWeight (hand.id, weight);
                body.SetRotationWeight (hand.id, weight);
                body.SetLocked (hand.id, !grabbed && pulls);
                if (!pulls) continue;
                ResolvePose (body, entry);
                PlaceHand (hand, entry.position, entry.rotation);
                if (grabbed) body.MarkGrabbed (hand.id);
            }
            body.SetHeldFixedBones (fixedShoulders_);
        }

        readonly List<Transform> fixedShoulders_ = new List<Transform> ();
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
        /// 全身 IK を解いた後に呼ぶ。決まった枠で物の姿勢を求め、持っている手を手足だけ解いて握りへ合わせ、物を置く。
        /// 手が届かないときは、届かない分だけ物を体へ寄せて解き直す（手を握りから外さない）。つかんでいた物は、寄せた後の姿勢を値に残す
        /// </summary>
        public void Finish (FullBodyRig body, float weight)
        {
            List<Transform> controls = null;
            foreach (Hand hand in hands_) {
                if (!hand.held) continue;
                body.UnmarkGrabbed (hand.id);
                (controls ??= new List<Transform> ()).Add (hand.control);
            }
            bool edited = fitOnly_ == null;
            foreach (Entry entry in entries_) entry.fitted = ResolvePose (body, entry);
            if (controls != null && weight > 0) {
                for (int pass = 0; pass < kReachPasses; pass++) {
                    foreach (Hand hand in hands_) {
                        if (!hand.held) continue;
                        PlaceHand (hand, hand.entry.position, hand.entry.rotation);
                        body.SetPositionWeight (hand.id, 1);
                        body.SetRotationWeight (hand.id, 1);
                        body.SetLocked (hand.id, false);
                    }
                    body.SolveOnly (controls, weight, true);
                    if (weight < 1 || !MoveTowardHands (body)) break;
                }
            }
            Place ();
            // 点で決めた姿勢も、動かした後なら値に残す（打つキーは解いた姿勢。キーの間で Lock の点に留めた姿勢はクリップの値を変えない）
            foreach (Entry entry in entries_) {
                if (entry.dragging || (edited && entry.fitted)) StoreValues (entry);
            }
            fitOnly_ = null;
        }

        /// <summary>
        /// いちばん届いていない手の差だけ、物を体へ寄せる。寄せる物が無ければ false。
        /// 差の平均だと、届いていた手まで握りから離れて、手が片方ずつ交互に外れる
        /// </summary>
        bool MoveTowardHands (FullBodyRig body)
        {
            bool moved = false;
            Transform root = rig_.root.transform;
            foreach (Entry entry in entries_) {
                Vector3 shift = Vector3.zero;
                foreach (Hand hand in new[] { entry.right, entry.left }) {
                    if (hand == null || !hand.held || !body.TryGetBonePointWorld (hand.id, out Vector3 reached)) continue;
                    Vector3 gap = root.InverseTransformPoint (reached) - (entry.position + entry.rotation * hand.gripPosition);
                    if (gap.sqrMagnitude > shift.sqrMagnitude) shift = gap;
                }
                if (shift.magnitude <= kReachTolerance) continue;
                entry.position += shift;
                moved = true;
            }
            return moved;
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

        /// <summary>手の握りのワールドの姿勢</summary>
        public void GetGrip (Hand hand, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            Transform root = rig_.root.transform;
            worldPosition = root.TransformPoint (hand.entry.position + hand.entry.rotation * hand.gripPosition);
            worldRotation = root.rotation * hand.entry.rotation * hand.gripRotation;
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

        /// <summary>今の値と枠で物の姿勢を求め直す（全身 IK を通さない姿勢。編集用リグを解いた直後）</summary>
        public void Refresh ()
        {
            foreach (Entry entry in entries_) GetPose (entry, out entry.position, out entry.rotation);
            Place ();
        }

        /// <summary>編集用の体の物を、今の物の姿勢へ置く</summary>
        void Place ()
        {
            Transform root = rig_.root.transform;
            foreach (Entry entry in entries_) {
                entry.editing.SetPositionAndRotation (root.TransformPoint (entry.position), root.rotation * entry.rotation);
            }
        }
    }

}
