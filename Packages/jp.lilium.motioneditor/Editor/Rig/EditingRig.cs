using UnityEngine;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 編集用の体。表示モデルの Animator の GameObject と同じ名前・同じ位置向きのルートに、骨の階層を名前と相対パスのまま複製し
    /// （メッシュやコンポーネントは持たない）、定義から決まる固定名のコントロール（Controls/...）を並べる。
    /// ルートには Avatar の無い Animator を付け、編集用クリップはこの階層だけを指す。
    /// 表示モデルへは SyncToDisplay で骨の値を写す
    /// </summary>
    public sealed class EditingRig : System.IDisposable
    {
        /// <summary>
        /// 複製した骨と、表示モデルの骨の組
        /// </summary>
        struct BonePair
        {
            public Transform editing;
            public Transform display;
            public Vector3 restPosition;
            public Quaternion restRotation;
            public Vector3 restScale;
            /// <summary>基準姿勢での、編集用ルートから見た回転と位置</summary>
            public Quaternion restRootRotation;
            public Vector3 restRootPosition;
        }

        readonly List<BonePair> bones_ = new List<BonePair> ();
        readonly Dictionary<Transform, Transform> editingByDisplay_ = new Dictionary<Transform, Transform> ();
        readonly Dictionary<Transform, int> indexByEditing_ = new Dictionary<Transform, int> ();
        readonly Dictionary<string, Transform> controls_ = new Dictionary<string, Transform> ();

        public GameObject root { get; private set; }
        public Animator animator { get; private set; }
        public Animator displayAnimator { get; private set; }
        public RigBinding binding { get; private set; }
        /// <summary>
        /// コントロールをまとめる GameObject（Controls）
        /// </summary>
        public Transform controlsRoot { get; private set; }
        /// <summary>
        /// ゲームの Rig の値の代理（Controls/Game/...）。Rig が無ければ空
        /// </summary>
        public RigProxies rigProxies { get; private set; }
        /// <summary>
        /// 表示モデルの体の大きさ。全身 IK の点を保存するときに割る（割れるよう、人型でない・0 のときは 1）
        /// </summary>
        public float humanScale { get; private set; } = 1;
        /// <summary>
        /// 作れたが気にしておくこと（割り当ての誤りも含む）
        /// </summary>
        public readonly List<string> errors = new List<string> ();

        internal int boneCount
        {
            get { return bones_.Count; }
        }

        /// <summary>
        /// 編集用の骨（親が子より先に来る順）
        /// </summary>
        public IEnumerable<Transform> editingBones
        {
            get {
                foreach (BonePair pair in bones_) yield return pair.editing;
            }
        }

        /// <summary>
        /// 体の向き（編集用ルートから見た右・上・前）。基準姿勢の肩（無ければ脚）の並びから決める
        /// </summary>
        public Vector3 bodyRight { get; private set; }
        public Vector3 bodyUp { get; private set; }
        public Vector3 bodyForward { get; private set; }

        /// <summary>
        /// 体の向き（x 右・y 上・z 前）で書いた向きを、編集用ルートから見た向きにする
        /// </summary>
        public Vector3 BodyToRoot (Vector3 body)
        {
            return bodyRight * body.x + bodyUp * body.y + bodyForward * body.z;
        }

        /// <param name="createGameObject">GameObject を作る関数（プレビューシーンに隠して作るなど）。名前を受け取る</param>
        /// <param name="rigLayers">表示モデルに組まれている Rig（RigProbe.Describe）。値の代理を作る</param>
        public static EditingRig Create (EditRigDefinition definition, Animator displayAnimator, System.Func<string, GameObject> createGameObject, IList<RigLayerInfo> rigLayers = null)
        {
            EditingRig rig = new EditingRig ();
            rig.displayAnimator = displayAnimator;
            rig.binding = RigBinding.Bind (definition, displayAnimator);
            rig.errors.AddRange (rig.binding.errors);
            if (displayAnimator == null) {
                rig.rigProxies = new RigProxies ();
                return rig;
            }

            Transform source = displayAnimator.transform;
            rig.humanScale = displayAnimator.isHuman && displayAnimator.humanScale > 1e-6f ? displayAnimator.humanScale : 1;
            rig.root = createGameObject (source.name);
            rig.root.transform.SetPositionAndRotation (source.position, source.rotation);
            rig.root.transform.localScale = source.lossyScale;

            foreach (Transform child in source) {
                if (child.name == RigPaths.kRoot) {
                    rig.errors.Add (Tr ("EDITING_RIG_ROOT_NAME_CONFLICT", RigPaths.kRoot));
                    continue;
                }
                rig.CloneBones (child, rig.root.transform, createGameObject);
            }
            rig.CaptureRestInRootSpace ();
            rig.CreateControls (createGameObject);
            rig.rigProxies = RigProxies.Create (rig, rigLayers, path => rig.CreateControl (path, createGameObject));

            // 編集用クリップを流す Animator。Avatar を持たせないので Generic として Transform と数値のカーブがそのまま当たる。
            // プレビューのカメラは描く瞬間しか動かないので、画面外として評価を省かれないようにする
            rig.animator = rig.root.AddComponent<Animator> ();
            rig.animator.avatar = null;
            rig.animator.applyRootMotion = false;
            rig.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return rig;
        }

        void CloneBones (Transform source, Transform parent, System.Func<string, GameObject> createGameObject)
        {
            Transform clone = createGameObject (source.name).transform;
            clone.SetParent (parent, false);
            clone.SetLocalPositionAndRotation (source.localPosition, source.localRotation);
            clone.localScale = source.localScale;

            bones_.Add (new BonePair {
                editing = clone,
                display = source,
                restPosition = source.localPosition,
                restRotation = source.localRotation,
                restScale = source.localScale,
            });
            editingByDisplay_[source] = clone;
            indexByEditing_[clone] = bones_.Count - 1;

            foreach (Transform child in source) {
                CloneBones (child, clone, createGameObject);
            }
        }

        /// <summary>
        /// 有効なコントロールだけ GameObject を作る（無効のものはカーブがあっても当たらない。カーブは消さない）。
        /// 値（FK の差分・全身 IK の点）は段 4 の解決が入れる。ここでは基準姿勢での初期値（差分なし）にしておく
        /// </summary>
        void CreateControls (System.Func<string, GameObject> createGameObject)
        {
            controlsRoot = createGameObject (RigPaths.kRoot).transform;
            controlsRoot.SetParent (root.transform, false);

            foreach (RigBinding.BoneControl control in binding.boneControls) {
                if (control.enabled) CreateControl (control.path, createGameObject);
            }
            // 全身 IK の点（S25）。初期値は基準姿勢の骨の位置（ルートから見た位置 / humanScale）と、向きの差なし。固定の強さは 0
            foreach (RigBinding.Point point in binding.points) {
                if (!point.enabled) continue;
                Transform control = CreateControl (point.path, createGameObject);
                control.gameObject.AddComponent<BodyPoint> ();
                control.localPosition = (GetRestRootPosition (GetEditingBone (point.bone)) + GetPointRestOffset (point)) / humanScale;
            }
        }

        /// <summary>
        /// 左右反転の面の法線（ルートから見た体の右）と、面の位置（その向きで測った体の中心。メートル）
        /// </summary>
        public void GetMirrorPlane (out Vector3 normal, out float center)
        {
            normal = bodyRight;
            Transform left = GetEditingBone (HumanBodyBones.LeftUpperArm) ?? GetEditingBone (HumanBodyBones.LeftUpperLeg);
            Transform right = GetEditingBone (HumanBodyBones.RightUpperArm) ?? GetEditingBone (HumanBodyBones.RightUpperLeg);
            center = left != null && right != null
                ? Vector3.Dot ((GetRestRootPosition (left) + GetRestRootPosition (right)) * 0.5f, normal)
                : 0;
        }

        /// <summary>
        /// 全身 IK の点が、乗る骨の付け根からどれだけ離れているか（基準姿勢で。ルートから見た向き・メートル）。
        /// 定義の基準の場所（origin・toward・along・across・axes・ground・radius）に、定義のずれ（体の向き・humanScale に対する割合）を足した所
        /// </summary>
        public Vector3 GetPointRestOffset (RigBinding.Point point)
        {
            float scale = humanScale;
            EditRigDefinition.BodyPoint definition = point.definition;
            if (definition == null) return Vector3.zero;
            if (point.onObject) return GetRestRootRotation (GetEditingBone (point.bone)) * point.objectOffset;
            return GetAnchorRestOffset (point) + BodyToRoot (definition.offset) * scale;
        }

        /// <summary>
        /// GetPointRestOffset のうち、乗る骨と混ぜる相手の骨（RigBinding.Point.blendBone）の向きを混ぜて回す分（S34。定義のずれ。基準の場所は乗る骨に付いたまま）。
        /// 混ぜない点は 0
        /// </summary>
        public Vector3 GetPointBlendRestOffset (RigBinding.Point point)
        {
            float scale = humanScale;
            EditRigDefinition.BodyPoint definition = point.definition;
            if (definition == null || point.blendBone == null || !definition.blends) return Vector3.zero;
            return BodyToRoot (definition.offset) * scale;
        }

        Vector3 GetAnchorRestOffset (RigBinding.Point point)
        {
            Transform host = GetEditingBone (point.bone);
            EditRigDefinition.BodyPoint definition = point.definition;
            Transform origin = definition.origin == HumanBodyBones.LastBone ? host : GetEditingBone (definition.origin);
            // 基準の骨・先の骨の無い点は、割り当て（RigBinding）が作らない
            if (host == null || origin == null) return Vector3.zero;
            Vector3 start = GetRestRootPosition (origin);
            Vector3 d = Vector3.zero;
            if (definition.toward != HumanBodyBones.LastBone) {
                Transform toward = GetEditingBone (definition.toward);
                d = toward != null ? KeepAxes (GetRestRootPosition (toward) - start, definition.axes) : BodyToRoot (definition.fallback) * humanScale;
            }
            Vector3 place = start + d * definition.along + Vector3.Cross (bodyUp, d) * definition.across;
            // 地面へ落とす点は、床との当たりの球の下の端が地面に着く高さ（半径の分だけ上）に置く
            if (definition.ground) place += bodyUp * (Mathf.Max (0, definition.radius) * humanScale - Vector3.Dot (place, bodyUp));
            return place - GetRestRootPosition (host);
        }

        /// <summary>ルートから見た向きのうち、体の向き（x 右・y 上・z 前）の axes が 1 の成分だけを残す</summary>
        Vector3 KeepAxes (Vector3 value, Vector3 axes)
        {
            return bodyRight * (Vector3.Dot (value, bodyRight) * axes.x) + bodyUp * (Vector3.Dot (value, bodyUp) * axes.y)
                + bodyForward * (Vector3.Dot (value, bodyForward) * axes.z);
        }

        /// <summary>
        /// パスの途中の GameObject も作る（Controls/Game/Rig/Arm なら Game と Rig も）
        /// </summary>
        internal Transform CreateControl (string path, System.Func<string, GameObject> createGameObject)
        {
            Transform existing;
            if (controls_.TryGetValue (path, out existing)) return existing;

            int slash = path.LastIndexOf ('/');
            string parentPath = path.Substring (0, slash);
            Transform parent = parentPath == RigPaths.kRoot ? controlsRoot : CreateControl (parentPath, createGameObject);

            Transform control = createGameObject (path.Substring (slash + 1)).transform;
            control.SetParent (parent, false);
            controls_.Add (path, control);
            return control;
        }

        readonly List<Transform> bakedBones_ = new List<Transform> ();

        /// <summary>
        /// コントロールは持たないが、Humanoid へ焼くときに Transform を焼く骨（持ち物。表示モデルの骨）
        /// </summary>
        public IReadOnlyList<Transform> bakedBones
        {
            get { return bakedBones_; }
        }

        internal void AddBakedBone (Transform displayBone)
        {
            bakedBones_.Add (displayBone);
        }

        /// <summary>
        /// 骨を作った直後（基準姿勢）に、ルートから見た値と体の向きを取っておく
        /// </summary>
        void CaptureRestInRootSpace ()
        {
            Transform rootTransform = root.transform;
            Quaternion inverseRoot = Quaternion.Inverse (rootTransform.rotation);
            for (int i = 0; i < bones_.Count; i++) {
                BonePair pair = bones_[i];
                pair.restRootRotation = inverseRoot * pair.editing.rotation;
                pair.restRootPosition = rootTransform.InverseTransformPoint (pair.editing.position);
                bones_[i] = pair;
            }

            bodyUp = Vector3.up;
            Vector3 right = Vector3.right;
            Transform leftSide = GetEditingBone (HumanBodyBones.LeftUpperArm) ?? GetEditingBone (HumanBodyBones.LeftUpperLeg);
            Transform rightSide = GetEditingBone (HumanBodyBones.RightUpperArm) ?? GetEditingBone (HumanBodyBones.RightUpperLeg);
            if (leftSide != null && rightSide != null) {
                Vector3 across = GetRestRootPosition (rightSide) - GetRestRootPosition (leftSide);
                across -= bodyUp * Vector3.Dot (across, bodyUp);
                if (across.sqrMagnitude > 1e-8f) right = across.normalized;
            }
            bodyRight = right;
            // Unity は左手系なので、右 × 上 が前
            bodyForward = Vector3.Cross (bodyRight, bodyUp).normalized;
        }

        /// <summary>
        /// 基準姿勢での、ルートから見た骨の回転（FK の差分の基準）
        /// </summary>
        public Quaternion GetRestRootRotation (Transform editingBone)
        {
            int index;
            return editingBone != null && indexByEditing_.TryGetValue (editingBone, out index) ? bones_[index].restRootRotation : Quaternion.identity;
        }

        public Vector3 GetRestRootPosition (Transform editingBone)
        {
            int index;
            return editingBone != null && indexByEditing_.TryGetValue (editingBone, out index) ? bones_[index].restRootPosition : Vector3.zero;
        }

        /// <summary>
        /// コントロールの Transform（パスは RigPaths）。無効のコントロールは null
        /// </summary>
        public Transform FindControl (string path)
        {
            Transform control;
            return controls_.TryGetValue (path, out control) ? control : null;
        }

        /// <summary>
        /// 表示モデルの骨に対応する、編集用の骨
        /// </summary>
        public Transform GetEditingBone (Transform displayBone)
        {
            Transform bone;
            return displayBone != null && editingByDisplay_.TryGetValue (displayBone, out bone) ? bone : null;
        }

        /// <summary>
        /// 人型の骨に対応する編集用の骨（キャラに無ければ null）
        /// </summary>
        public Transform GetEditingBone (HumanBodyBones bone)
        {
            RigBinding.Fk fk = binding.FindFk (bone);
            return fk != null ? GetEditingBone (fk.bone) : null;
        }

        /// <summary>
        /// 編集用の骨を基準姿勢（prefab の初期姿勢＝T ポーズ）に戻す
        /// </summary>
        public void ResetBonesToRest ()
        {
            foreach (BonePair pair in bones_) {
                if (pair.editing == null) continue;
                pair.editing.SetLocalPositionAndRotation (pair.restPosition, pair.restRotation);
                pair.editing.localScale = pair.restScale;
            }
        }

        /// <summary>
        /// 基準姿勢での骨のローカル回転（FK の差分の基準）
        /// </summary>
        public bool TryGetRestRotation (Transform editingBone, out Quaternion rotation)
        {
            int index;
            if (editingBone != null && indexByEditing_.TryGetValue (editingBone, out index)) {
                rotation = bones_[index].restRotation;
                return true;
            }
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// 基準姿勢での骨のローカル位置
        /// </summary>
        public bool TryGetRestPosition (Transform editingBone, out Vector3 position)
        {
            int index;
            if (editingBone != null && indexByEditing_.TryGetValue (editingBone, out index)) {
                position = bones_[index].restPosition;
                return true;
            }
            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// 編集用の骨の値を表示モデルの骨へ写す（humanoid / rig builder を素通しにしたときの前進解決）
        /// </summary>
        public void SyncToDisplay ()
        {
            foreach (BonePair pair in bones_) {
                if (pair.editing == null || pair.display == null) continue;
                pair.editing.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                pair.display.SetLocalPositionAndRotation (position, rotation);
                pair.display.localScale = pair.editing.localScale;
            }
        }

        /// <summary>
        /// 表示モデルの骨の値を編集用の体へ写す（既存のモーションを読み込むときの入口。SyncToDisplay の逆）
        /// </summary>
        public void SyncFromDisplay ()
        {
            foreach (BonePair pair in bones_) {
                if (pair.editing == null || pair.display == null) continue;
                pair.display.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                pair.editing.SetLocalPositionAndRotation (position, rotation);
                pair.editing.localScale = pair.display.localScale;
            }
        }

        public void Dispose ()
        {
            if (root != null) Object.DestroyImmediate (root);
            root = null;
            animator = null;
            rigProxies = new RigProxies ();
            bones_.Clear ();
            editingByDisplay_.Clear ();
            indexByEditing_.Clear ();
            controls_.Clear ();
        }
    }

}
