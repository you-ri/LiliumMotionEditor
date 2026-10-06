using UnityEngine;
using System.Collections.Generic;
using Lilium;
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
        /// 表示モデルの体の大きさ。IK の目標を保存するときに割る
        /// </summary>
        public float humanScale { get; private set; }
        /// <summary>
        /// 作れたが気にしておくこと（割り当ての誤りも含む）
        /// </summary>
        public readonly List<string> errors = new List<string> ();

        public int boneCount
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
            rig.humanScale = displayAnimator.isHuman ? displayAnimator.humanScale : 1;
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
        /// 値（FK の差分・IK の目標）は段 4 の解決が入れる。ここでは基準姿勢での初期値（差分なし）にしておく
        /// </summary>
        void CreateControls (System.Func<string, GameObject> createGameObject)
        {
            controlsRoot = createGameObject (RigPaths.kRoot).transform;
            controlsRoot.SetParent (root.transform, false);

            foreach (RigBinding.Fk fk in binding.fk) {
                if (fk.enabled) CreateControl (fk.path, createGameObject);
            }
            foreach (RigBinding.Ik ik in binding.ik) {
                if (!ik.enabled) continue;
                CreateControl (ik.path, createGameObject).gameObject.AddComponent<IkControl> ();
                CreateControl (RigPaths.IkTarget (ik.chain.name), createGameObject);
                CreateControl (RigPaths.IkHint (ik.chain.name), createGameObject);
            }
            foreach (RigBinding.Extra extra in binding.extras) {
                if (extra.enabled) CreateControl (extra.path, createGameObject);
            }
            // 全身 IK の点（S25）。初期値は基準姿勢の骨の位置（ルートから見た位置 / humanScale）と、向きの差なし。固定の強さは 0
            foreach (RigBinding.Point point in binding.points) {
                if (!point.enabled) continue;
                Transform control = CreateControl (point.path, createGameObject);
                control.gameObject.AddComponent<BodyPoint> ();
                control.localPosition = (GetRestRootPosition (GetEditingBone (point.bone)) + GetPointRestOffset (point)) / (humanScale > 1e-6f ? humanScale : 1);
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
        /// 定義の基準の場所に、定義のずれ（体の向き・humanScale に対する割合）を足した所:
        /// - 骨の付け根: 0
        /// - かかと・つま先の先・足裏の中心・小指側: 同じ側の脚の IK の組の、足の転がしの支点を地面（基準姿勢のルートの高さ）へ落とした所
        ///   （組の上書きも効く。組が無ければ 0）
        /// - 膝: 同じ側の下腿の骨の付け根
        /// - 目の高さ: 頭の骨の付け根の真上の、目の高さ（GetEyeHeight）
        /// - 人差し指・小指の付け根: 同じ側の指の付け根（指の骨が無ければ、前腕の向きへ前腕の長さの 0.35 倍の所から前後へ開く）
        /// </summary>
        public Vector3 GetPointRestOffset (RigBinding.Point point)
        {
            float scale = humanScale > 1e-6f ? humanScale : 1;
            EditRigDefinition.BodyPoint definition = point.definition;
            if (definition == null) return Vector3.zero;
            return GetAnchorRestOffset (point) + BodyToRoot (definition.offset) * scale;
        }

        Vector3 GetAnchorRestOffset (RigBinding.Point point)
        {
            Transform host = GetEditingBone (point.bone);
            if (host == null) return Vector3.zero;
            EditRigDefinition.BodyPoint definition = point.definition;
            bool left = RigPaths.SideOf (definition.bone) == "Left";
            switch (definition.anchor) {
                case BodyPointAnchor.Heel:
                case BodyPointAnchor.ToeTip:
                case BodyPointAnchor.SoleBall:
                case BodyPointAnchor.LittleToeSide: {
                    HumanBodyBones footBone = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                    RigBinding.Ik leg = null;
                    foreach (RigBinding.Ik ik in binding.ik) {
                        if (ik.chain.tip == footBone) leg = ik;
                    }
                    Transform foot = leg != null ? GetEditingBone (leg.tip) : null;
                    Transform shin = leg != null ? GetEditingBone (leg.mid) : null;
                    if (foot == null || shin == null) return Vector3.zero;
                    ReverseFoot shape = ReverseFoot.Create (this, shin, foot, GetEditingBone (leg.chain.toes), leg.chain);
                    Vector3 pivot = GetRestRootPosition (foot) + GetRestRootRotation (foot) * shape.GetPivot (ToFootPivot (definition.anchor));
                    // 足の点は地面（基準姿勢のルートの高さ）に置く。足の転がしの支点は Avatar の足裏の高さなので、少し浮くキャラがある
                    pivot -= bodyUp * Vector3.Dot (pivot, bodyUp);
                    return pivot - GetRestRootPosition (host);
                }
                case BodyPointAnchor.Knee: {
                    Transform knee = GetEditingBone (left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                    return knee != null ? GetRestRootPosition (knee) - GetRestRootPosition (host) : Vector3.zero;
                }
                case BodyPointAnchor.Eyes: {
                    Transform head = GetEditingBone (HumanBodyBones.Head);
                    if (head == null) return Vector3.zero;
                    return GetRestRootPosition (head) + bodyUp * GetEyeHeight () - GetRestRootPosition (host);
                }
                case BodyPointAnchor.IndexBase:
                case BodyPointAnchor.LittleBase: {
                    bool index = definition.anchor == BodyPointAnchor.IndexBase;
                    Transform hand = GetEditingBone (left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    Transform finger = GetEditingBone (index
                        ? (left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal)
                        : (left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal));
                    if (finger != null) return GetRestRootPosition (finger) - GetRestRootPosition (host);
                    // 指の骨が無い: 前腕の向きへ前腕の長さの 0.35 倍、そこから体の前後へ開く（基準姿勢では人差し指が前）
                    Transform forearm = GetEditingBone (left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                    if (forearm == null || hand == null) return Vector3.zero;
                    Vector3 along = (GetRestRootPosition (hand) - GetRestRootPosition (forearm)) * 0.35f;
                    return GetRestRootPosition (hand) - GetRestRootPosition (host) + along + BodyToRoot (Vector3.forward) * (along.magnitude * (index ? 0.4f : -0.4f));
                }
            }
            return Vector3.zero;
        }

        /// <summary>目の高さを、頭に付いたメッシュの高さ（頭の付け根からてっぺんまで）の何倍に見積もるか（デモのキャラの目のメッシュで 0.47）</summary>
        const float kEyeMeshRatio = 0.47f;
        /// <summary>メッシュも無いときの目の高さ（humanScale に対する割合。デモのキャラで 0.095）</summary>
        const float kEyeScaleRatio = 0.095f;
        float eyeHeight_ = -1;

        /// <summary>
        /// 目の高さ（基準姿勢で、頭の骨の付け根から体の上の向きに測る。メートル）。目の骨があればその高さ（両目なら平均）、
        /// 無ければ頭に付いたメッシュの高さから見積もり、メッシュも無ければ体の大きさから見積もる。キャラごとに 1 回だけ測る
        /// </summary>
        public float GetEyeHeight ()
        {
            if (eyeHeight_ >= 0) return eyeHeight_;
            Transform head = GetEditingBone (HumanBodyBones.Head);
            if (head == null) return eyeHeight_ = 0;
            Vector3 origin = GetRestRootPosition (head);
            float sum = 0;
            int count = 0;
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye }) {
                Transform eye = GetEditingBone (bone);
                if (eye == null) continue;
                sum += Vector3.Dot (GetRestRootPosition (eye) - origin, bodyUp);
                count++;
            }
            if (count > 0) return eyeHeight_ = Mathf.Max (0, sum / count);
            float top = GetHeadMeshHeight ();
            return eyeHeight_ = top > 0 ? top * kEyeMeshRatio : humanScale * kEyeScaleRatio;
        }

        /// <summary>
        /// 頭（と頭より先の骨）に半分以上の重みで付いた頂点の、頭の骨の付け根からいちばん高い所までの高さ（メートル。無ければ 0）。
        /// 表示モデルは今の姿勢とは限らないので、バインドポーズのメッシュで測る。上の向きと長さは、腰から頭への向きと長さを基準姿勢の値に合わせて決める
        /// </summary>
        float GetHeadMeshHeight ()
        {
            if (displayAnimator == null || !displayAnimator.isHuman) return 0;
            Transform displayHead = displayAnimator.GetBoneTransform (HumanBodyBones.Head);
            Transform displayHips = displayAnimator.GetBoneTransform (HumanBodyBones.Hips);
            Transform head = GetEditingBone (HumanBodyBones.Head);
            Transform hips = GetEditingBone (HumanBodyBones.Hips);
            if (displayHead == null || displayHips == null || head == null || hips == null) return 0;
            float restLength = Vector3.Distance (GetRestRootPosition (head), GetRestRootPosition (hips));
            float best = 0;
            foreach (SkinnedMeshRenderer renderer in displayAnimator.GetComponentsInChildren<SkinnedMeshRenderer> (true)) {
                Mesh mesh = renderer.sharedMesh;
                Transform[] bones = renderer.bones;
                if (mesh == null || bones == null || mesh.bindposes.Length != bones.Length) continue;
                int headIndex = System.Array.IndexOf (bones, displayHead);
                int hipsIndex = System.Array.IndexOf (bones, displayHips);
                if (headIndex < 0 || hipsIndex < 0) continue;
                Matrix4x4[] bindposes = mesh.bindposes;
                Vector3 headBind = bindposes[headIndex].inverse.MultiplyPoint3x4 (Vector3.zero);
                Vector3 hipsBind = bindposes[hipsIndex].inverse.MultiplyPoint3x4 (Vector3.zero);
                float bindLength = Vector3.Distance (headBind, hipsBind);
                if (bindLength < 1e-6f || restLength < 1e-6f) continue;
                Vector3 up = (headBind - hipsBind) / bindLength;
                bool[] onHead = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++) onHead[i] = bones[i] != null && (bones[i] == displayHead || bones[i].IsChildOf (displayHead));
                Vector3[] vertices = mesh.vertices;
                BoneWeight[] weights = mesh.boneWeights;
                if (weights.Length != vertices.Length) continue;
                for (int i = 0; i < vertices.Length; i++) {
                    BoneWeight w = weights[i];
                    float weight = (onHead[w.boneIndex0] ? w.weight0 : 0) + (onHead[w.boneIndex1] ? w.weight1 : 0)
                        + (onHead[w.boneIndex2] ? w.weight2 : 0) + (onHead[w.boneIndex3] ? w.weight3 : 0);
                    if (weight < 0.5f) continue;
                    best = Mathf.Max (best, Vector3.Dot (vertices[i] - headBind, up) * restLength / bindLength);
                }
            }
            return best;
        }

        static ReverseFoot.Pivot ToFootPivot (BodyPointAnchor anchor)
        {
            switch (anchor) {
                case BodyPointAnchor.Heel: return ReverseFoot.Pivot.Heel;
                case BodyPointAnchor.SoleBall: return ReverseFoot.Pivot.SoleBall;
                case BodyPointAnchor.LittleToeSide: return ReverseFoot.Pivot.LittleToeSide;
            }
            return ReverseFoot.Pivot.ToeTip;
        }

        /// <summary>
        /// パスの途中の GameObject も作る（Controls/IK/LeftArm/Target なら IK と LeftArm も）
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
