using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// プレビュー窓の中の世界。PreviewRenderUtility の別シーンに、キャラ（表示モデル）・編集用の体・
    /// 文脈（Timeline で一緒に動く物）・床とグリッド・ライトを置く。
    /// 開いているシーンには何も置かない。Dispose で丸ごと捨てる
    /// </summary>
    public sealed class PreviewStage : System.IDisposable
    {
        /// <summary>
        /// キャラに残すコンポーネント。見た目以外（ゲームのロジック・Animation Rigging・揺れもの・音・パーティクル・物理など）は外す
        /// </summary>
        static readonly System.Type[] kVisualComponents = {
            typeof (Transform), typeof (Animator), typeof (SkinnedMeshRenderer), typeof (MeshRenderer), typeof (MeshFilter),
        };

        /// <summary>
        /// 置いた物の剥がし方
        /// </summary>
        public enum StripPolicy
        {
            /// <summary>編集するキャラ。見た目と段（Rigging・[PoseLayer]）だけ残す</summary>
            EditTarget,
            /// <summary>文脈（エフェクト・相手など）。見た目に加えて Director と Timeline 関連を残し、ゲームのロジックだけ外す</summary>
            Context,
        }

        static readonly Color kBackgroundColor = new Color (0.19f, 0.2f, 0.22f);
        static readonly Color kAmbientColor = new Color (0.42f, 0.42f, 0.46f);
        static readonly Color kFloorColor = new Color (0.27f, 0.28f, 0.3f);
        static readonly Color kGridMinorColor = new Color (1, 1, 1, 0.06f);
        static readonly Color kGridMajorColor = new Color (1, 1, 1, 0.16f);
        static readonly Color kGridXAxisColor = new Color (0.86f, 0.24f, 0.11f, 0.7f);
        static readonly Color kGridZAxisColor = new Color (0.23f, 0.48f, 0.97f, 0.7f);
        const float kFloorExtent = 10;
        const float kGridExtent = 5;
        const float kGridStep = 0.5f;

        readonly List<GameObject> roots_ = new List<GameObject> ();
        readonly List<Object> assets_ = new List<Object> ();
        readonly List<GameObject> contexts_ = new List<GameObject> ();
        readonly List<ContextInfo> contextInfos_ = new List<ContextInfo> ();
        readonly EditRigDefinition definition_;
        readonly bool ownsDefinition_;
        GameObject contextRoot_;
        readonly Scene scene_;
        readonly GameObject floor_;
        RenderTexture handlesTarget_;
        ClipSampler clipSampler_;
        // Generic Pose の段のクリップ（骨のカーブ）を当てる道具。編集用クリップと交互に当てても束縛を作り直さないよう分ける
        ClipSampler genericSampler_;
        // Humanoid Pose の段のクリップ（既存の Humanoid のクリップ）を表示モデルへ当てる道具。初めて使うときに作る
        ClipSampler humanoidSampler_;
        // 合成するときに、クリップを当てる前の姿勢を覚えておく場所
        Vector3[] blendPositions_ = new Vector3[0];
        Quaternion[] blendRotations_ = new Quaternion[0];
        Vector3[] blendScales_ = new Vector3[0];
        HumanPose blendPose_;
        // 合成に使う編集用の骨の並び・コントロールの基準の値（作った直後の値）
        List<Transform> editingBoneList_;
        Transform[] controlTransforms_ = new Transform[0];
        /// <summary>コントロールの Transform → controlTransforms_ の番号</summary>
        readonly Dictionary<Transform, int> controlIndex_ = new Dictionary<Transform, int> ();
        /// <summary>書き込み先の Override の層の手前で控えた値（S14。書き込み先が元のクリップなら null）</summary>
        ControlState overrideSource_;
        Vector3[] controlRestPositions_ = new Vector3[0];
        Quaternion[] controlRestRotations_ = new Quaternion[0];
        Vector3[] controlRestScales_ = new Vector3[0];
        Lilium.IkControl[] ikControls_ = new Lilium.IkControl[0];
        /// <summary>全身 IK の点（S25）と、その固定の強さの基準（点ごとに BodyPoint.kValueCount 個ずつ並べる）</summary>
        Lilium.BodyPoint[] bodyPoints_ = new Lilium.BodyPoint[0];
        float[] bodyPointRest_ = new float[0];
        /// <summary>IK の組の数値の基準（組ごとに IkControl.kValueCount 個ずつ並べる）</summary>
        float[] ikRest_ = new float[0];
        Lilium.RigLayerProxy[] layerProxies_ = new Lilium.RigLayerProxy[0];
        float[] layerProxyRest_ = new float[0];
        Lilium.RigConstraintProxy[] constraintProxies_ = new Lilium.RigConstraintProxy[0];
        float[] constraintProxyRest_ = new float[0];
        Vector3[] controlSavedPositions_ = new Vector3[0];
        Quaternion[] controlSavedRotations_ = new Quaternion[0];
        Vector3[] controlSavedScales_ = new Vector3[0];
        // 焼いた後の姿勢を表示モデルへ当てる道具（Humanoid の段）。初めて使うときに作る
        HumanoidBaker humanoidReader_;
        // ゲームの Rigging を通すグラフ（姿勢を入れる → Rig）。初めて使うときに作る
        PoseGraph poseGraph_;
        bool poseGraphFailed_;
        // このフレームに Humanoid の段が姿勢を作ったか（作っていなければ Rig は表示モデルから読む）
        bool humanoidPoseValid_;
        // このフレームに Rig の段が申告した重み（段ごと）。申告が無い層は 0 で通す
        readonly Dictionary<RigLayerInfo, float> rigWeights_ = new Dictionary<RigLayerInfo, float> ();
        // このフレームに評価へ入ったゲーム側の段（Component の型）。後段の受け口へ渡す
        readonly HashSet<System.Type> evaluatedLayers_ = new HashSet<System.Type> ();
        // 終端でまとめて解くときに、重みを 0 に戻す層
        IReadOnlyList<RigLayerInfo> rigLayers_;
        HumanPoseHandler humanoidWriter_;
        HumanPose humanoidPose_;
        Transform[] poseTransforms_ = new Transform[0];
        Vector3[] restPositions_ = new Vector3[0];
        Quaternion[] restRotations_ = new Quaternion[0];
        Vector3[] restScales_ = new Vector3[0];

        public PreviewRenderUtility renderUtility { get; private set; }
        /// <summary>
        /// ツールのハンドル用のカメラ（ISupportsEditorTools.handlesCamera）。描画用のカメラはレンダーテクスチャへ描くので、
        /// ハンドルは出力先を持たないこちらで窓へ直接描く。位置と画角は窓が描画用と揃える
        /// </summary>
        public Camera handlesCamera { get; private set; }
        public GameObject model { get; private set; }
        /// <summary>
        /// 複製元の prefab（アセット）
        /// </summary>
        public GameObject sourcePrefab { get; private set; }
        public Animator animator { get; private set; }
        /// <summary>
        /// 編集用の体（骨の複製とコントロール）。編集用クリップはここへ当てる
        /// </summary>
        public EditingRig editingRig { get; private set; }
        public EditingRigSolver editingRigSolver { get; private set; }
        /// <summary>
        /// 全身 IK（S25）。リグの定義が全身 IK のときだけある（無ければ null）
        /// </summary>
        public FullBodyRig fullBody { get; private set; }
        public string error { get; private set; }

        public Camera camera
        {
            get { return renderUtility.camera; }
        }

        /// <summary>プレビューの世界のシーン（Scene ビュー型の窓が映す。S15d）</summary>
        public Scene scene
        {
            get { return scene_; }
        }

        public bool showFloor
        {
            get { return floor_.activeSelf; }
            set { floor_.SetActive (value); }
        }

        /// <summary>
        /// 体の正面（ワールド）。キャラが無ければ +Z
        /// </summary>
        public Vector3 bodyForward
        {
            get {
                if (editingRig == null || editingRig.root == null) return Vector3.forward;
                return editingRig.root.transform.TransformDirection (editingRig.bodyForward);
            }
        }

        /// <param name="definition">編集用リグの定義。null なら既定の定義を一時の物として作る（パッケージの既定のアセットが読めないときなど）</param>
        /// <param name="placement">
        /// キャラを置く親の場所（ワールドの位置・向き・大きさ）。開いているシーンのキャラを編集するとき（S15d）に、そのキャラの親を渡す。
        /// 骨のハンドルが本物の骨の上に出るよう、複製を同じ場所に置く。null なら原点
        /// </param>
        public PreviewStage (GameObject prefab, EditRigDefinition definition = null, Transform placement = null)
        {
            placement_ = placement;
            ownsDefinition_ = definition == null;
            sourcePrefab = prefab;
            definition_ = definition != null ? definition : EditRigDefinition.CreateDefault ();
            renderUtility = new PreviewRenderUtility ();
            scene_ = renderUtility.camera.gameObject.scene;

            SetupCameraAndLights ();
            handlesCamera = CreateRoot ("Handles Camera").AddComponent<Camera> ();
            handlesCamera.enabled = false;
            floor_ = CreateFloor ();
            if (prefab != null) CreateModel (prefab);
        }

        readonly Transform placement_;

        public void Dispose ()
        {
            if (editingRig != null) {
                editingRig.Dispose ();
                editingRig = null;
            }
            editingRigSolver = null;
            if (fullBody != null) fullBody.Dispose ();
            fullBody = null;
            if (poseGraph_ != null) {
                poseGraph_.Dispose ();
                poseGraph_ = null;
            }
            if (humanoidReader_ != null) {
                humanoidReader_.Dispose ();
                humanoidReader_ = null;
            }
            if (humanoidWriter_ != null) {
                humanoidWriter_.Dispose ();
                humanoidWriter_ = null;
            }
            if (clipSampler_ != null) {
                clipSampler_.Dispose ();
                clipSampler_ = null;
            }
            if (genericSampler_ != null) {
                genericSampler_.Dispose ();
                genericSampler_ = null;
            }
            if (humanoidSampler_ != null) {
                humanoidSampler_.Dispose ();
                humanoidSampler_ = null;
            }
            contexts_.Clear ();
            contextInfos_.Clear ();
            foreach (GameObject root in roots_) {
                if (root != null) Object.DestroyImmediate (root);
            }
            roots_.Clear ();
            foreach (Object asset in assets_) {
                if (asset != null) Object.DestroyImmediate (asset);
            }
            assets_.Clear ();
            if (handlesTarget_ != null) {
                handlesTarget_.Release ();
                Object.DestroyImmediate (handlesTarget_);
            }
            if (ownsDefinition_ && definition_ != null) Object.DestroyImmediate (definition_);
            renderUtility.Cleanup ();
        }

        /// <summary>
        /// 文脈の物（Timeline で一緒に動くエフェクト・相手など）を置く。編集対象とは剥がし方が違う（Director と Timeline 関連を残す）。
        /// ステージを捨てると一緒に消える
        /// </summary>
        public GameObject AddContext (GameObject prefab)
        {
            if (prefab == null) return null;
            if (contextRoot_ == null) contextRoot_ = CreateRoot ("Context");

            GameObject holder = CreateGameObject (prefab.name);
            holder.transform.SetParent (contextRoot_.transform, false);
            holder.SetActive (false);
            GameObject instance = Object.Instantiate (prefab, holder.transform, false);
            instance.name = prefab.name;
            StripComponents (instance, StripPolicy.Context);
            holder.SetActive (true);
            contexts_.Add (instance);
            // 時計（Timeline）は手回しにして、バインドの決め方を拡張点に通す
            ContextInfo info = ContextProbe.Prepare (instance, prefab);
            if (info != null) contextInfos_.Add (info);
            return instance;
        }

        public IReadOnlyList<GameObject> contexts
        {
            get { return contexts_; }
        }

        /// <summary>
        /// 置いた文脈の中身（時計の長さ・トラック・注意）
        /// </summary>
        public IReadOnlyList<ContextInfo> contextInfos
        {
            get { return contextInfos_; }
        }

        /// <summary>
        /// 一番長い文脈の長さ（秒）。0 なら時計を持つ文脈が無い
        /// </summary>
        public double contextDuration
        {
            get {
                double result = 0;
                foreach (ContextInfo info in contextInfos_) result = System.Math.Max (result, info.duration);
                return result;
            }
        }

        /// <summary>
        /// 文脈をその時刻（秒）の姿にする。時計は窓が正なので、窓が姿勢を作るたびに呼ぶ
        /// </summary>
        public void EvaluateContexts (double time)
        {
            foreach (ContextInfo info in contextInfos_) ContextProbe.Evaluate (info.instance, time);
        }

        /// <summary>
        /// ハンドル用カメラに、描画には使わない出力先を持たせる。出力先の無いカメラは pixelRect が Game ビューの大きさで
        /// 切り詰められ（Game ビューが小さいと高さ 0 になり）、ハンドルも、同じ IMGUIContainer の後の描画も消える。
        /// 出力先があれば pixelRect はその大きさまで入り、描く先は今の描画先（窓）のまま変わらない
        /// </summary>
        /// <param name="width">窓（ホストビュー）の幅（ピクセル）。Screen.width</param>
        public void EnsureHandlesTarget (int width, int height)
        {
            if (handlesTarget_ != null && handlesTarget_.width >= width && handlesTarget_.height >= height) return;

            if (handlesTarget_ != null) {
                handlesCamera.targetTexture = null;
                handlesTarget_.Release ();
                Object.DestroyImmediate (handlesTarget_);
            }
            // 窓を広げるたびに作り直さないよう、256 単位で切り上げる
            int w = Mathf.Max (256, Mathf.CeilToInt (width / 256f) * 256);
            int h = Mathf.Max (256, Mathf.CeilToInt (height / 256f) * 256);
            handlesTarget_ = new RenderTexture (w, h, 0, RenderTextureFormat.R8) { name = "MktHandlesTarget", hideFlags = HideFlags.HideAndDontSave };
            handlesTarget_.Create ();
            handlesCamera.targetTexture = handlesTarget_;
        }

        /// <summary>
        /// ライトはカメラについて回る（どこから見ても手前の左上から当たる）
        /// </summary>
        public void UpdateLights (Quaternion viewRotation)
        {
            Light[] lights = renderUtility.lights;
            lights[0].transform.rotation = viewRotation * Quaternion.Euler (35, 35, 0);
            lights[1].transform.rotation = viewRotation * Quaternion.Euler (10, 200, 0);
        }

        /// <summary>
        /// クリップを当てる前に基準の姿勢へ戻す（カーブの無い骨が前の姿勢のまま残らないように）
        /// </summary>
        public void RestorePose ()
        {
            for (int i = 0; i < poseTransforms_.Length; i++) {
                Transform t = poseTransforms_[i];
                if (t == null) continue;
                t.localPosition = restPositions_[i];
                t.localRotation = restRotations_[i];
                t.localScale = restScales_[i];
            }
            if (editingRig != null) editingRig.ResetBonesToRest ();
            if (fullBody != null) fullBody.ClearInput ();
            rigWeights_.Clear ();
            evaluatedLayers_.Clear ();
            humanoidPoseValid_ = false;
        }

        /// <summary>
        /// ストリームを通した姿勢（Rigging・ゲームの後段）を出せるか
        /// </summary>
        public bool canApplyRig
        {
            get { return animator != null && !poseGraphFailed_ && (RigProbe.createPreview != null || PoseGraphHooks.hooks.Count > 0); }
        }

        /// <summary>
        /// ゲーム側の段（[PoseLayer] の Component）が、このフレームの評価に入ったことを申告する
        /// </summary>
        public void MarkLayerEvaluated (System.Type type)
        {
            if (type != null) evaluatedLayers_.Add (type);
        }

        /// <summary>
        /// この段（Rig の層）の重みを、このフレームの分として申告する。申告が無い層は 0 で通る（👁 を落とした層など）
        /// </summary>
        public void SetRigLayerWeight (RigLayerInfo info, float weight)
        {
            if (info != null) rigWeights_[info] = Mathf.Clamp01 (weight);
        }

        /// <summary>
        /// 今の姿勢に、表示モデルの Rigging を掛ける。
        /// **拘束は AnimationStream に働く**ので、Transform に書いた姿勢をグラフへ入れ直してから解く（PoseGraph）
        /// </summary>
        /// <param name="layers">この表示モデルの Rig の層（申告の無い層を 0 にするため）</param>
        public void ApplyRig (IReadOnlyList<RigLayerInfo> layers)
        {
            rigLayers_ = layers;
        }

        /// <summary>
        /// 表示の骨をつかんでいるとき（✋ = Display Pose）の、選んでいる表示モデルの骨。
        /// その骨（か親）を動かす拘束は、解く間だけ重み 0 にする（S13b。逆の無い拘束を通る逃げ道）。保存データの重みは変えない
        /// </summary>
        public System.Func<IEnumerable<Transform>> grabbedBones;

        readonly List<string> mutedConstraints_ = new List<string> ();
        readonly List<(RigConstraintInfo info, float weight)> mutedRestore_ = new List<(RigConstraintInfo, float)> ();

        /// <summary>最後に解いたとき、つかんでいる骨のために切った拘束（「Rig の層 / 拘束」）</summary>
        public IReadOnlyList<string> mutedConstraints
        {
            get { return mutedConstraints_; }
        }

        void MuteGrabbedConstraints ()
        {
            mutedConstraints_.Clear ();
            mutedRestore_.Clear ();
            if (grabbedBones == null || rigLayers_ == null) return;
            List<Transform> bones = null;
            foreach (RigLayerInfo info in rigLayers_) {
                float layerWeight;
                if (info == null || !rigWeights_.TryGetValue (info, out layerWeight) || layerWeight <= 0) continue;
                foreach (RigConstraintInfo constraint in info.constraints) {
                    if (constraint.setWeight == null || constraint.getWeight == null) continue;
                    if (bones == null) bones = grabbedBones ().Where (b => b != null).ToList ();
                    if (bones.Count == 0) return;
                    if (!bones.Any (constraint.Moves)) continue;
                    float weight = constraint.getWeight ();
                    if (weight <= 0) continue;
                    mutedRestore_.Add ((constraint, weight));
                    mutedConstraints_.Add (info.label + " / " + constraint.label);
                    constraint.setWeight (0);
                }
            }
        }

        void RestoreMutedConstraints ()
        {
            foreach ((RigConstraintInfo info, float weight) item in mutedRestore_) item.info.setWeight (item.weight);
            mutedRestore_.Clear ();
        }

        /// <summary>
        /// 申告された値（Rig の重み・ゲーム側の段）でストリームを 1 回通す。**段の並びの終端（Display Pose）で呼ぶ**。
        /// 途中の段で解くと、段の順番と実際の処理の順番がずれる（後段の反転が Rig の段の中で起きてしまう）
        /// </summary>
        public bool FlushPoseGraph ()
        {
            if (!canApplyRig) return false;
            if (poseGraph_ == null) {
                poseGraph_ = PoseGraph.Create (animator, model, poseTransforms_, GetRestLocalPose);
                if (poseGraph_ == null) {
                    poseGraphFailed_ = true;
                    return false;
                }
                poseGraph_.isLayerActive = evaluatedLayers_.Contains;
            }

            if (rigLayers_ != null) {
                foreach (RigLayerInfo info in rigLayers_) {
                    if (info == null || info.setWeight == null) continue;
                    float weight;
                    info.setWeight (rigWeights_.TryGetValue (info, out weight) ? weight : 0);
                }
            }

            // Humanoid の段が作った姿勢がそのまま表示に残っていれば、その値を渡す（表示モデルから読み直すと、
            // Humanoid の往復の分だけねじりがずれる。ゲームの経路との差は読み直すと 1.97°、渡すと 0.00°。2026-09-18 実測）。
            // 後ろの段が表示の骨を書き換えたら読み直す
            MuteGrabbedConstraints ();
            try {
                if (humanoidPoseValid_) poseGraph_.Evaluate (ref humanoidPose_);
                else poseGraph_.Evaluate ();
            }
            finally {
                RestoreMutedConstraints ();
            }
            return true;
        }

        /// <summary>
        /// 表示モデルの骨の基準姿勢（キャラを置いた直後の local の値）。知らない骨は今の値
        /// </summary>
        Pose GetRestLocalPose (Transform transform)
        {
            int index = System.Array.IndexOf (poseTransforms_, transform);
            if (index >= 0) return new Pose (restPositions_[index], restRotations_[index]);
            transform.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            return new Pose (position, rotation);
        }

        readonly PropertyPlayer properties_ = new PropertyPlayer ();

        /// <summary>
        /// 編集用クリップの任意のプロパティ（Controls/Props/...）を、time の値で表示モデルへ入れる（S6）
        /// </summary>
        public void ApplyPropertyCurves (AnimationClip clip, float time)
        {
            if (animator != null) properties_.Apply (animator.gameObject, clip, time);
        }

        /// <summary>任意のプロパティで今入れている値を控える（編集用の体を表示モデルへ写し直す前）</summary>
        public List<KeyValuePair<EditorCurveBinding, float>> CapturePropertyValues ()
        {
            return animator != null ? properties_.Capture (animator.gameObject) : null;
        }

        /// <summary><see cref="CapturePropertyValues"/> で控えた値を入れ直す</summary>
        public void RestorePropertyValues (List<KeyValuePair<EditorCurveBinding, float>> values)
        {
            if (animator != null) PropertyPlayer.Put (animator.gameObject, values);
        }

        /// <summary>
        /// 表示モデルの骨を段が書き換えた。Humanoid の段が作った姿勢はもう表示と合わないので、終端では表示から読み直す
        /// </summary>
        public void InvalidateHumanoidPose ()
        {
            humanoidPoseValid_ = false;
        }

        /// <summary>
        /// クリップを編集用の体（コントロール）へ当てる。グラフで流すので、Transform も数値のカーブも当たる（ClipSampler）。
        /// カーブの組が変わったら InvalidateClipCurves で作り直す
        /// </summary>
        public void SampleClip (AnimationClip clip, float time)
        {
            if (clipSampler_ == null || clip == null) return;
            clipSampler_.Sample (clip, time);
        }

        public void InvalidateClipCurves ()
        {
            if (clipSampler_ != null) clipSampler_.Invalidate ();
            if (genericSampler_ != null) genericSampler_.Invalidate ();
            if (humanoidSampler_ != null) humanoidSampler_.Invalidate ();
        }

        /// <summary>
        /// コントロールの基準の値（作った直後の値）を覚える。Editing Rig のクリップの合成の相手になる
        /// </summary>
        void CaptureControlRest ()
        {
            if (editingRig == null || editingRig.controlsRoot == null) return;
            Transform controls = editingRig.controlsRoot;
            controlTransforms_ = controls.GetComponentsInChildren<Transform> (true);
            controlIndex_.Clear ();
            for (int i = 0; i < controlTransforms_.Length; i++) controlIndex_[controlTransforms_[i]] = i;
            controlRestPositions_ = controlTransforms_.Select (t => t.localPosition).ToArray ();
            controlRestRotations_ = controlTransforms_.Select (t => t.localRotation).ToArray ();
            controlRestScales_ = controlTransforms_.Select (t => t.localScale).ToArray ();
            ikControls_ = controls.GetComponentsInChildren<Lilium.IkControl> (true);
            ikRest_ = ReadIkValues ();
            bodyPoints_ = controls.GetComponentsInChildren<Lilium.BodyPoint> (true);
            bodyPointRest_ = ReadPointValues ();
            layerProxies_ = controls.GetComponentsInChildren<Lilium.RigLayerProxy> (true);
            layerProxyRest_ = layerProxies_.Select (c => c.weight).ToArray ();
            constraintProxies_ = controls.GetComponentsInChildren<Lilium.RigConstraintProxy> (true);
            constraintProxyRest_ = constraintProxies_.Select (c => c.weight).ToArray ();
        }

        /// <summary>
        /// 編集用クリップを重みで合成してコントロールへ当てる（Editing Rig の段）。
        /// 1 はこれまでどおり上書き（カーブの無いコントロールは今の値のまま）。1 未満は、コントロールを基準の値へ戻してから混ぜる
        /// </summary>
        public void BlendDataClip (AnimationClip clip, float time, float weight)
        {
            if (weight >= 1) {
                // 全身 IK の点はカーブの無い値を残さない（S25）。前に開いていたクリップや、打たなかった操作の固定が残ると、
                // 点のキーが無いクリップで素通しにならない
                RestoreBodyPoints ();
                SampleClip (clip, time);
                MaskBodyPoints (clip, time);
                return;
            }
            RestoreControls ();
            if (weight <= 0 || clip == null) return;

            SampleClip (clip, time);
            for (int i = 0; i < controlTransforms_.Length; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation (out Vector3 p, out Quaternion q);
                t.SetLocalPositionAndRotation (Vector3.Lerp (controlRestPositions_[i], p, weight), Quaternion.Slerp (controlRestRotations_[i], q, weight));
                t.localScale = Vector3.Lerp (controlRestScales_[i], t.localScale, weight);
            }
            for (int i = 0; i < ikControls_.Length; i++) {
                if (ikControls_[i] == null) continue;
                for (int v = 0; v < Lilium.IkControl.kValueCount; v++) {
                    ikControls_[i].SetValue (v, Mathf.Lerp (ikRest_[i * Lilium.IkControl.kValueCount + v], ikControls_[i].GetValue (v), weight));
                }
            }
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null) continue;
                for (int v = 0; v < Lilium.BodyPoint.kValueCount; v++) {
                    bodyPoints_[i].SetValue (v, Mathf.Lerp (bodyPointRest_[i * Lilium.BodyPoint.kValueCount + v], bodyPoints_[i].GetValue (v), weight));
                }
            }
            for (int i = 0; i < layerProxies_.Length; i++) {
                if (layerProxies_[i] != null) layerProxies_[i].weight = Mathf.Lerp (layerProxyRest_[i], layerProxies_[i].weight, weight);
            }
            for (int i = 0; i < constraintProxies_.Length; i++) {
                if (constraintProxies_[i] != null) constraintProxies_[i].weight = Mathf.Lerp (constraintProxyRest_[i], constraintProxies_[i].weight, weight);
            }
        }

        /// <summary>
        /// 書き込み先の Override の段が、自分を重ねる直前の値を控える（S14。書くときの元になる）
        /// </summary>
        public void CaptureOverrideSource ()
        {
            overrideSource_ = editingRig != null && editingRig.root != null ? CaptureControlState () : null;
        }

        /// <summary>控えを捨てる（姿勢を作り始めるとき）</summary>
        public void ClearOverrideSource ()
        {
            overrideSource_ = null;
        }

        /// <summary>
        /// Override のクリップを、今のコントロールの値へ重ねる（S14）
        /// </summary>
        public void ApplyOverride (AnimationClip clip, OverrideMode mode, float weight, float time)
        {
            if (editingRig == null || editingRig.root == null) return;
            OverrideApplier.Apply (clip, mode, weight, time, editingRig.root.transform);
        }

        /// <summary>
        /// 書き込み先の Override の層の手前の値（パスは編集用の体のルートから）。控えが無い・コントロールでなければ false
        /// </summary>
        public bool TryGetOverrideSource (string path, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            if (overrideSource_ == null || editingRig == null || editingRig.root == null) return false;
            Transform root = editingRig.root.transform;
            Transform t = path.Length == 0 ? root : root.Find (path);
            int index;
            if (t == null || !controlIndex_.TryGetValue (t, out index)) return false;
            position = overrideSource_.positions[index];
            rotation = overrideSource_.rotations[index];
            scale = overrideSource_.scales[index];
            return true;
        }

        /// <summary>
        /// 書き込み先の Override の層の手前の、IK の組・全身 IK の点の数値（パスは編集用の体のルートから）。控えが無い・どちらでもなければ false
        /// </summary>
        public bool TryGetOverrideSourceFloat (string path, System.Type type, string property, out float value)
        {
            value = 0;
            if (overrideSource_ == null || editingRig == null || editingRig.root == null) return false;
            if (type == typeof (Lilium.BodyPoint)) return TryGetOverrideSourcePoint (path, property, out value);
            if (type != typeof (Lilium.IkControl)) return false;
            int v = Lilium.IkControl.IndexOf (property);
            Transform root = editingRig.root.transform;
            Transform t = path.Length == 0 ? root : root.Find (path);
            if (v < 0 || t == null) return false;
            for (int i = 0; i < ikControls_.Length; i++) {
                if (ikControls_[i] == null || ikControls_[i].transform != t) continue;
                value = overrideSource_.ikValues[i * Lilium.IkControl.kValueCount + v];
                return true;
            }
            return false;
        }

        bool TryGetOverrideSourcePoint (string path, string property, out float value)
        {
            value = 0;
            int v = Lilium.BodyPoint.IndexOf (property);
            Transform root = editingRig.root.transform;
            Transform t = path.Length == 0 ? root : root.Find (path);
            if (v < 0 || t == null || overrideSource_.pointValues == null) return false;
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null || bodyPoints_[i].transform != t) continue;
                value = overrideSource_.pointValues[i * Lilium.BodyPoint.kValueCount + v];
                return true;
            }
            return false;
        }

        /// <summary>
        /// 操作値（コントロールの Transform と FK/IK の切替）の控え。表示の骨をつかんで書いた結果が狙いから遠ざかったとき、元へ戻すのに使う
        /// </summary>
        public sealed class ControlState
        {
            public Vector3[] positions;
            public Quaternion[] rotations;
            public Vector3[] scales;
            /// <summary>IK の組の数値（組ごとに IkControl.kValueCount 個ずつ）</summary>
            public float[] ikValues;
            /// <summary>全身 IK の点の固定の強さ（点ごとに BodyPoint.kValueCount 個ずつ）</summary>
            public float[] pointValues;
        }

        float[] ReadPointValues ()
        {
            int n = Lilium.BodyPoint.kValueCount;
            float[] values = new float[bodyPoints_.Length * n];
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null) continue;
                for (int v = 0; v < n; v++) values[i * n + v] = bodyPoints_[i].GetValue (v);
            }
            return values;
        }

        void WritePointValues (float[] values)
        {
            int n = Lilium.BodyPoint.kValueCount;
            if (values == null || values.Length != bodyPoints_.Length * n) return;
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null) continue;
                for (int v = 0; v < n; v++) bodyPoints_[i].SetValue (v, values[i * n + v]);
            }
        }

        float[] ReadIkValues ()
        {
            int n = Lilium.IkControl.kValueCount;
            float[] values = new float[ikControls_.Length * n];
            for (int i = 0; i < ikControls_.Length; i++) {
                if (ikControls_[i] == null) continue;
                for (int v = 0; v < n; v++) values[i * n + v] = ikControls_[i].GetValue (v);
            }
            return values;
        }

        void WriteIkValues (float[] values)
        {
            int n = Lilium.IkControl.kValueCount;
            for (int i = 0; i < ikControls_.Length; i++) {
                if (ikControls_[i] == null) continue;
                for (int v = 0; v < n; v++) ikControls_[i].SetValue (v, values[i * n + v]);
            }
        }

        public ControlState CaptureControlState ()
        {
            ControlState state = new ControlState {
                positions = new Vector3[controlTransforms_.Length],
                rotations = new Quaternion[controlTransforms_.Length],
                scales = new Vector3[controlTransforms_.Length],
                ikValues = ReadIkValues (),
                pointValues = ReadPointValues (),
            };
            for (int i = 0; i < controlTransforms_.Length; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation (out state.positions[i], out state.rotations[i]);
                state.scales[i] = t.localScale;
            }
            return state;
        }

        public void RestoreControlState (ControlState state)
        {
            if (state == null || state.positions.Length != controlTransforms_.Length || state.ikValues.Length != ikControls_.Length * Lilium.IkControl.kValueCount) return;
            for (int i = 0; i < controlTransforms_.Length; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.SetLocalPositionAndRotation (state.positions[i], state.rotations[i]);
                t.localScale = state.scales[i];
            }
            WriteIkValues (state.ikValues);
            WritePointValues (state.pointValues);
        }

        void SaveControls ()
        {
            int count = controlTransforms_.Length;
            if (controlSavedPositions_.Length != count) {
                controlSavedPositions_ = new Vector3[count];
                controlSavedRotations_ = new Quaternion[count];
                controlSavedScales_ = new Vector3[count];
            }
            for (int i = 0; i < count; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation (out controlSavedPositions_[i], out controlSavedRotations_[i]);
                controlSavedScales_[i] = t.localScale;
            }
        }

        void LoadControls ()
        {
            for (int i = 0; i < controlTransforms_.Length; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.SetLocalPositionAndRotation (controlSavedPositions_[i], controlSavedRotations_[i]);
                t.localScale = controlSavedScales_[i];
            }
        }

        /// <summary>コントロールを基準の値へ戻す（焼くときに、フレームごとの値が前のフレームから残らないように）</summary>
        public void ResetControlsToRest ()
        {
            RestoreControls ();
        }

        /// <summary>
        /// 全身 IK の点の状態（Pin / Locked）は、次にその点のキーを打つまで引き継ぐ（IK / FK の切り替えと同じ。キーの間で補間しない）。
        /// クリップを当てた後、状態を「その時刻以前でいちばん近いキーの値」に入れ直す。
        /// その時刻ちょうどのキーが無い点は、位置を引き継がない: 後で、その時刻の骨の位置に置き直す（SnapCarriedPoints）。
        /// 引き継ぐのは「留まる・動かさない」という状態だけで、前のキーフレームの場所へ体を引っ張ることはしない
        /// </summary>
        void MaskBodyPoints (AnimationClip clip, float time)
        {
            carriedPoints_.Clear ();
            lockedSpanPoints_.Clear ();
            if (bodyPoints_.Length == 0 || editingRig == null || editingRig.root == null) return;
            if (bodyPointPaths_ == null || bodyPointPaths_.Length != bodyPoints_.Length) {
                bodyPointPaths_ = new string[bodyPoints_.Length];
                for (int i = 0; i < bodyPoints_.Length; i++) {
                    bodyPointPaths_[i] = bodyPoints_[i] != null ? AnimationUtility.CalculateTransformPath (bodyPoints_[i].transform, editingRig.root.transform) : null;
                }
            }
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null) continue;
                bool keyed;
                EditingClip.GetPointState (clip, bodyPointPaths_[i], time, pointState_, out keyed);
                bool active = false;
                for (int v = 0; v < Lilium.BodyPoint.kValueCount; v++) {
                    bodyPoints_[i].SetValue (v, pointState_[v]);
                    active |= pointState_[v] > 0;
                }
                if (!active || keyed) continue;
                // 前後のキーで Locked の点は、キーの間でも位置のカーブの補間した場所に留める（全身 IK の段が解く）
                if (EditingClip.IsLockedSpan (clip, bodyPointPaths_[i], time)) {
                    lockedSpanPoints_.Add (bodyPoints_[i].transform);
                    AddKeyPoseGap (clip, i, time);
                }
                else carriedPoints_.Add (bodyPoints_[i].transform);
            }
        }

        /// <summary>
        /// キーの間で解く Locked の点の行き先へ、前後のキーの姿勢での「骨の上の点と点のキーの差」を、時刻で混ぜて足す。
        /// キーの姿勢（骨のキー）は、点を離したときに解いた姿勢で、ほかの点との兼ね合いで点のキーまで届いていないことがある
        /// （手と足を両方 Locked にした、など）。差を足さないと、キーのフレームだけ骨のキーの姿勢に戻り、キーの間は点の場所まで
        /// 曲げるので、キーをまたぐたびに手足が跳ねる（2026-10-01 実測で 1 フレームに 6°）。
        /// 差を足すと、キーのフレームの行き先はキーの姿勢の点そのものになり、キーの間は前後のキーの姿勢へなめらかにつながる
        /// </summary>
        void AddKeyPoseGap (AnimationClip clip, int index, float time)
        {
            if (buildingKeyPoseGaps_) return;
            int previous, next;
            EditingClip.GetPointKeyBounds (clip, bodyPointPaths_[index], time, out previous, out next);
            KeyPoseGaps gaps = GetKeyPoseGaps (clip);
            Vector3 fromPosition, toPosition;
            Quaternion fromRotation, toRotation;
            if (gaps == null || !gaps.TryGet (previous, index, out fromPosition, out fromRotation)) return;
            if (next < 0 || !gaps.TryGet (next, index, out toPosition, out toRotation)) {
                toPosition = fromPosition;
                toRotation = fromRotation;
            }
            float u = next > previous ? Mathf.Clamp01 ((time * clip.frameRate - previous) / (next - previous)) : 0;
            Transform control = bodyPoints_[index].transform;
            control.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            control.SetLocalPositionAndRotation (
                position + Vector3.Lerp (fromPosition, toPosition, u),
                Quaternion.Slerp (fromRotation, toRotation, u) * rotation);
        }

        /// <summary>点の状態のキーのあるフレームごとの、キーの姿勢での骨の上の点と点のキーの差（コントロールのローカルの値）</summary>
        sealed class KeyPoseGaps
        {
            public AnimationClip clip;
            public int version;
            public readonly Dictionary<int, Vector3[]> positions = new Dictionary<int, Vector3[]> ();
            public readonly Dictionary<int, Quaternion[]> rotations = new Dictionary<int, Quaternion[]> ();

            public bool TryGet (int frame, int index, out Vector3 position, out Quaternion rotation)
            {
                Vector3[] p;
                Quaternion[] r;
                if (frame < 0 || !positions.TryGetValue (frame, out p) || !rotations.TryGetValue (frame, out r)) {
                    position = Vector3.zero;
                    rotation = Quaternion.identity;
                    return false;
                }
                position = p[index];
                rotation = r[index];
                return true;
            }
        }

        KeyPoseGaps keyPoseGaps_;
        bool buildingKeyPoseGaps_;

        /// <summary>
        /// キーの姿勢での差を測る（クリップが書き換わったら測り直す）。Locked のキーを持つ点のキーのあるフレームごとに、
        /// クリップを当てて編集用リグを解き、骨の上の点を読む。測った後は、編集用の体を基準姿勢へ戻しておく
        /// （呼んだ側が、この後でクリップを当て直す）
        /// </summary>
        KeyPoseGaps GetKeyPoseGaps (AnimationClip clip)
        {
            if (fullBody == null || editingRig == null || clip == null) return null;
            int version = EditingClip.GetCurveVersion (clip);
            if (keyPoseGaps_ != null && keyPoseGaps_.clip == clip && keyPoseGaps_.version == version) return keyPoseGaps_;
            KeyPoseGaps gaps = new KeyPoseGaps { clip = clip, version = version };
            // 今のコントロールの値（クリップを当てた後、点の状態を入れた後）を控えて、測った後に戻す
            ControlState saved = CaptureControlState ();
            float[] savedLayerWeights = layerProxies_.Select (c => c != null ? c.weight : 0).ToArray ();
            float[] savedConstraintWeights = constraintProxies_.Select (c => c != null ? c.weight : 0).ToArray ();
            HashSet<Transform> savedSpan = new HashSet<Transform> (lockedSpanPoints_);
            HashSet<Transform> savedCarried = new HashSet<Transform> (carriedPoints_);
            buildingKeyPoseGaps_ = true;
            try {
                foreach (int frame in EditingClip.GetLockedPointKeyFrames (clip)) {
                    editingRig.ResetBonesToRest ();
                    fullBody.ClearInput ();
                    RestoreBodyPoints ();
                    float time = frame / clip.frameRate;
                    SampleClip (clip, time);
                    MaskBodyPoints (clip, time);
                    Solve ();
                    Vector3[] positions = new Vector3[bodyPoints_.Length];
                    Quaternion[] rotations = new Quaternion[bodyPoints_.Length];
                    for (int i = 0; i < bodyPoints_.Length; i++) {
                        rotations[i] = Quaternion.identity;
                        if (bodyPoints_[i] == null) continue;
                        Transform control = bodyPoints_[i].transform;
                        if (!fullBody.TryGetCapturedValues (control, out Vector3 position, out Quaternion rotation)) continue;
                        control.GetLocalPositionAndRotation (out Vector3 keyPosition, out Quaternion keyRotation);
                        positions[i] = position - keyPosition;
                        rotations[i] = rotation * Quaternion.Inverse (keyRotation);
                    }
                    gaps.positions[frame] = positions;
                    gaps.rotations[frame] = rotations;
                }
            }
            finally {
                buildingKeyPoseGaps_ = false;
                editingRig.ResetBonesToRest ();
                fullBody.ClearInput ();
                RestoreControlState (saved);
                for (int i = 0; i < layerProxies_.Length; i++) {
                    if (layerProxies_[i] != null) layerProxies_[i].weight = savedLayerWeights[i];
                }
                for (int i = 0; i < constraintProxies_.Length; i++) {
                    if (constraintProxies_[i] != null) constraintProxies_[i].weight = savedConstraintWeights[i];
                }
                lockedSpanPoints_.Clear ();
                lockedSpanPoints_.UnionWith (savedSpan);
                carriedPoints_.Clear ();
                carriedPoints_.UnionWith (savedCarried);
            }
            keyPoseGaps_ = gaps;
            return gaps;
        }

        /// <summary>
        /// キーの間で全身 IK を解く点（前後の点のキーで Locked のまま）。クリップを当てた後に決まる（S25d）
        /// </summary>
        public ICollection<Transform> lockedSpanPoints
        {
            get { return lockedSpanPoints_; }
        }

        readonly HashSet<Transform> lockedSpanPoints_ = new HashSet<Transform> ();

        /// <summary>
        /// 状態だけ引き継いだ点（その時刻ちょうどのキーが無い Pin / Locked の点）を、今の骨の位置と向きに置く。
        /// 編集用リグを解いた後（全身 IK の段）で呼ぶ
        /// </summary>
        public void SnapCarriedPoints ()
        {
            if (fullBody != null && carriedPoints_.Count > 0) fullBody.CapturePoints (carriedPoints_);
        }

        readonly HashSet<Transform> carriedPoints_ = new HashSet<Transform> ();
        readonly float[] pointState_ = new float[Lilium.BodyPoint.kValueCount];
        string[] bodyPointPaths_;

        /// <summary>
        /// 全身 IK の点（位置・向き・固定の強さ）を基準の値へ戻す
        /// </summary>
        void RestoreBodyPoints ()
        {
            for (int i = 0; i < bodyPoints_.Length; i++) {
                if (bodyPoints_[i] == null) continue;
                int index;
                if (!controlIndex_.TryGetValue (bodyPoints_[i].transform, out index)) continue;
                bodyPoints_[i].transform.SetLocalPositionAndRotation (controlRestPositions_[index], controlRestRotations_[index]);
            }
            WritePointValues (bodyPointRest_);
            carriedPoints_.Clear ();
            lockedSpanPoints_.Clear ();
        }

        void RestoreControls ()
        {
            for (int i = 0; i < controlTransforms_.Length; i++) {
                Transform t = controlTransforms_[i];
                if (t == null) continue;
                t.SetLocalPositionAndRotation (controlRestPositions_[i], controlRestRotations_[i]);
                t.localScale = controlRestScales_[i];
            }
            WriteIkValues (ikRest_);
            WritePointValues (bodyPointRest_);
            for (int i = 0; i < layerProxies_.Length; i++) {
                if (layerProxies_[i] != null) layerProxies_[i].weight = layerProxyRest_[i];
            }
            for (int i = 0; i < constraintProxies_.Length; i++) {
                if (constraintProxies_[i] != null) constraintProxies_[i].weight = constraintProxyRest_[i];
            }
        }

        /// <summary>
        /// 骨を直接動かす Generic のクリップを、編集用の骨の今の姿勢へ重みで合成する（Generic Pose の段のクリップ）。
        /// カーブの無い骨は今の姿勢のまま。Animator の GameObject のカーブ（ルートモーション）は当てない
        /// </summary>
        public void BlendGenericClip (AnimationClip clip, float time, float weight)
        {
            if (genericSampler_ == null || clip == null || weight <= 0 || editingRig == null || editingRig.root == null) return;
            Transform root = editingRig.root.transform;
            root.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            Vector3 scale = root.localScale;
            if (editingBoneList_ == null) editingBoneList_ = new List<Transform> (editingRig.editingBones);
            List<Transform> bones = editingBoneList_;
            if (weight < 1) Remember (bones);
            // 同じ Animator に編集用クリップのグラフもあると、こちらを流したときにコントロールが束縛時の値へ戻される（09-17 実測）。値を守る
            SaveControls ();
            genericSampler_.Sample (clip, time);
            LoadControls ();
            root.SetLocalPositionAndRotation (position, rotation);
            root.localScale = scale;
            if (weight < 1) Mix (bones, weight);
        }

        /// <summary>
        /// 既存の Humanoid のクリップを、流れてきた姿勢（編集用の体を Humanoid に直したもの）へ重みで合成して表示モデルへ当てる。
        /// muscle と体の位置・向きを混ぜ、人型でない骨（クリップにカーブのあるもの）は Transform で混ぜる。
        /// Animator の GameObject は動かさない（ルートモーションは出さない）
        /// </summary>
        /// <summary>
        /// Humanoid のクリップをそのフレームの操作値（コントロールの値）にする（S14 の土台）。
        /// クリップを表示モデルへ当て、その骨を編集用の体へ写して操作値として捉える（手足は FK）。ルートの移動は当てない（その場）。
        /// この後 Override を重ねて解くので、元のクリップを直せば土台も付いていく
        /// </summary>
        public bool SampleHumanoidAsControls (AnimationClip clip, float time)
        {
            if (clip == null || !clip.humanMotion || editingRig == null || editingRigSolver == null || !canPreviewHumanoid) return false;
            if (humanoidSampler_ == null) humanoidSampler_ = new ClipSampler (animator);
            Transform root = animator.transform;
            root.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            humanoidSampler_.Sample (clip, time);
            root.SetLocalPositionAndRotation (position, rotation);
            humanoidPoseValid_ = false;
            // Humanoid のクリップに全身 IK の点のキーは無い（点は Override から入る）
            RestoreBodyPoints ();
            editingRig.SyncFromDisplay ();
            foreach (RigBinding.Ik ik in editingRig.binding.ik) {
                if (ik.enabled) editingRigSolver.SetIkWeight (ik.chain.name, 0);
            }
            editingRigSolver.Capture ();
            return true;
        }

        /// <summary>
        /// muscle の値（Humanoid の姿勢。手の形など一部だけでよい）を今の姿勢に重ね、その muscle が動かす骨の FK の操作値として捉える（PoseBank）。
        /// 今の姿勢を Humanoid に直して表示モデルへ当て、muscle を差し替えて当て直し、該当する骨だけを編集用の体へ写す。
        /// 返すのは値を捉えた FK のコントロールのパス（＝キーを打つカーブのパス）。当てられなければ null
        /// </summary>
        public HashSet<string> ApplyMusclesAsControls (IReadOnlyDictionary<int, float> muscles)
        {
            if (muscles == null || editingRigSolver == null || !ApplyHumanoidPose ()) return null;
            humanoidPoseValid_ = false;
            return PoseBank.ApplyMuscles (editingRig, editingRigSolver, humanoidWriter_, humanoidPose_, muscles);
        }

        public void BlendHumanoidClip(AnimationClip clip, float time, float weight)
        {
            if (clip == null || weight <= 0 || !canPreviewHumanoid) return;
            if (humanoidSampler_ == null) humanoidSampler_ = new ClipSampler (animator);
            Transform root = animator.transform;
            root.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);

            HumanPose upstream = new HumanPose ();
            bool blend = weight < 1 && ApplyHumanoidPose ();
            if (blend) {
                upstream = humanoidPose_;
                upstream.muscles = (float[])humanoidPose_.muscles.Clone ();
                Remember (poseTransforms_);
            }
            humanoidSampler_.Sample (clip, time);
            // 表示に出るのはクリップを混ぜた後の姿勢。Rig へはそれを表示モデルから読ませる
            humanoidPoseValid_ = false;
            root.SetLocalPositionAndRotation (position, rotation);
            if (!blend) return;

            // クリップの muscle は、骨を混ぜる前に読む（混ぜた後に読むと二重に混ざる）
            root.SetLocalPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                humanoidWriter_.GetHumanPose (ref blendPose_);
                // 人型でない骨は Transform で混ぜる（人型の骨はこの後 SetHumanPose で決まる）
                root.SetLocalPositionAndRotation (position, rotation);
                Mix (poseTransforms_, weight);
                root.SetLocalPositionAndRotation (Vector3.zero, Quaternion.identity);
                blendPose_.bodyPosition = Vector3.Lerp (upstream.bodyPosition, blendPose_.bodyPosition, weight);
                blendPose_.bodyRotation = Quaternion.Slerp (upstream.bodyRotation, blendPose_.bodyRotation, weight);
                for (int i = 0; i < blendPose_.muscles.Length && i < upstream.muscles.Length; i++) {
                    blendPose_.muscles[i] = Mathf.Lerp (upstream.muscles[i], blendPose_.muscles[i], weight);
                }
                humanoidWriter_.SetHumanPose (ref blendPose_);
            }
            finally {
                root.SetLocalPositionAndRotation (position, rotation);
            }
        }

        void Remember (IReadOnlyList<Transform> transforms)
        {
            if (blendPositions_.Length < transforms.Count) {
                blendPositions_ = new Vector3[transforms.Count];
                blendRotations_ = new Quaternion[transforms.Count];
                blendScales_ = new Vector3[transforms.Count];
            }
            for (int i = 0; i < transforms.Count; i++) {
                Transform t = transforms[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation (out blendPositions_[i], out blendRotations_[i]);
                blendScales_[i] = t.localScale;
            }
        }

        void Mix (IReadOnlyList<Transform> transforms, float weight)
        {
            for (int i = 0; i < transforms.Count; i++) {
                Transform t = transforms[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation (out Vector3 p, out Quaternion q);
                t.SetLocalPositionAndRotation (Vector3.Lerp (blendPositions_[i], p, weight), Quaternion.Slerp (blendRotations_[i], q, weight));
                t.localScale = Vector3.Lerp (blendScales_[i], t.localScale, weight);
            }
        }

        /// <summary>
        /// Humanoid のクリップを焼く道具。クリップは窓と同じグラフで当てる。使い終わったら Dispose
        /// </summary>
        /// <param name="apply">そのフレームの姿勢（操作値）を作る関数（S14。元＋Override）。渡さなければクリップを当てる</param>
        public HumanoidBaker CreateHumanoidBaker (System.Action<float> apply = null)
        {
            return new HumanoidBaker (editingRig, editingRigSolver, SampleClipForBake, apply);
        }

        /// <summary>
        /// 焼くとき（全身 IK の段を通さずに焼く経路）に、キーの間で前後のキーが Locked の点だけを解く（S25d）。
        /// 編集用リグを解いた後に呼ぶ。点の状態は、クリップを当てたときに決めてある（SampleClipForBake・MaskBodyPointsForBake）
        /// </summary>
        public void SolveLockedSpan (float weight)
        {
            if (fullBody == null || weight <= 0 || lockedSpanPoints_.Count == 0) return;
            fullBody.CaptureInput ();
            fullBody.SolveOnly (lockedSpanPoints_, weight, true);
        }

        /// <summary>
        /// クリップを自前で当てて焼くとき（Override を混ぜる）に、全身 IK の点の状態をそのクリップの時刻のものにする
        /// </summary>
        public void MaskBodyPointsForBake (AnimationClip clip, float time)
        {
            if (clip != null) MaskBodyPoints (clip, time);
            else lockedSpanPoints_.Clear ();
        }

        /// <summary>
        /// 焼くときにクリップを当てる。全身 IK の点はカーブの無い値を残さない（画面で固定したままの点が、焼いた版に混ざらないように）
        /// </summary>
        void SampleClipForBake (AnimationClip clip, float time)
        {
            RestoreBodyPoints ();
            SampleClip (clip, time);
            MaskBodyPoints (clip, time);
        }

        /// <summary>
        /// 焼いた後の姿勢を見せられるか（表示モデルが Humanoid で、編集用の体がある）
        /// </summary>
        public bool canPreviewHumanoid
        {
            get { return editingRig != null && editingRig.root != null && animator != null && animator.isHuman; }
        }

        /// <summary>
        /// 編集用の体の姿勢を Humanoid（muscle）へ直して、表示モデルへ当てる。焼いたクリップをゲームで再生したときと同じ姿勢になる
        /// （Humanoid の可動範囲で丸められ、ねじりは配り直される）。人型でない骨は触らない（前の段の同期のまま）
        /// </summary>
        public bool ApplyHumanoidPose ()
        {
            if (!canPreviewHumanoid) return false;
            if (humanoidReader_ == null) {
                humanoidReader_ = CreateHumanoidBaker ();
                humanoidWriter_ = new HumanPoseHandler (animator.avatar, animator.transform);
                humanoidPose_ = new HumanPose ();
            }
            if (humanoidReader_.error != null) return false;

            humanoidReader_.ReadPose (ref humanoidPose_);
            humanoidPoseValid_ = true;
            // 読むときと同じく、表示モデルの Animator の GameObject を原点・無回転に置いて当てる
            Transform root = animator.transform;
            root.GetPositionAndRotation (out Vector3 position, out Quaternion rotation);
            root.SetPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                humanoidWriter_.SetHumanPose (ref humanoidPose_);
            }
            finally {
                root.SetPositionAndRotation (position, rotation);
            }
            return true;
        }

        /// <summary>
        /// 編集用リグを解く（コントロールの値 → 編集用の骨）。
        /// 解いたあと、使っていない側（FK の間は IK の目標、IK の間は FK の差分）を今の姿勢に合わせる。
        /// こうしておくと FK と IK の値がいつも同じ姿勢を指すので、どのフレームで切り替えても・重みを動かしても姿勢が飛ばない
        /// </summary>
        public void Solve ()
        {
            if (editingRigSolver == null) return;
            // 全身 IK が曲げた分を戻してから解く（S25。コントロールの無い骨に残ると、解き直すたびに積み重なる）
            if (fullBody != null) fullBody.Unsolve ();
            editingRigSolver.Solve ();
            editingRigSolver.SyncIkFk ();
        }

        /// <summary>
        /// 編集用の体を表示モデルへ写す（姿勢の段が、編集用の体を書き換えた後に呼ぶ）。
        /// 写すと、任意のプロパティで入れた Transform の値（武器の握りなど）が編集用の体の値へ戻るので、控えて入れ直す。
        /// 後ろの段（Humanoid Pose・Rig）は表示から読み直す
        /// </summary>
        public void ShowEditingBody ()
        {
            if (editingRig == null) return;
            List<KeyValuePair<EditorCurveBinding, float>> properties = CapturePropertyValues ();
            editingRig.SyncToDisplay ();
            RestorePropertyValues (properties);
            InvalidateHumanoidPose ();
        }

        /// <summary>
        /// 見えているメッシュにレイを当てて、当たった場所を一番動かしている骨を返す（当たらなければ null）。
        /// スキンのメッシュは今の姿勢で焼いて（BakeMesh）から三角形と交差させ、当たった面の頂点のウェイトが一番大きい骨を採る。
        /// 骨を持たないメッシュ（武器など）は、その Transform を返す
        /// </summary>
        public Transform PickBone (Ray ray)
        {
            return PickBoneInternal (ray);
        }

        /// <summary>
        /// 表示モデルの今の見た目を焼いて、描く位置と一緒に result へ足す（Stacker のゴースト）。
        /// メッシュは pool の used 番目から使い回し（足りなければ作る）、使った数を返す。骨を持たないメッシュは元のメッシュをそのまま描く
        /// </summary>
        public int BakeDisplayMeshes (List<Mesh> pool, int used, List<(Mesh, Matrix4x4)> result)
        {
            if (model == null) return used;
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> ()) {
                if (!skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null) continue;
                if (used >= pool.Count) pool.Add (new Mesh { hideFlags = HideFlags.HideAndDontSave });
                Mesh mesh = pool[used++];
                skin.BakeMesh (mesh);
                // 焼いたメッシュはレンダラーの Transform から見た位置（大きさは焼かない）
                result.Add ((mesh, skin.transform.localToWorldMatrix));
            }
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter> ()) {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer> ();
                if (renderer == null || !renderer.enabled || !filter.gameObject.activeInHierarchy || filter.sharedMesh == null) continue;
                result.Add ((filter.sharedMesh, filter.transform.localToWorldMatrix));
            }
            return used;
        }

        Transform PickBoneInternal (Ray ray)
        {
            if (model == null) return null;

            Transform picked = null;
            float distance = float.MaxValue;

            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> ()) {
                if (!skin.enabled || skin.sharedMesh == null) continue;

                Mesh baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try {
                    skin.BakeMesh (baked);
                    Vector3[] vertices = baked.vertices;
                    if (vertices.Length == 0) continue;

                    int[] triangles = skin.sharedMesh.triangles;
                    MeshHit hit;
                    // 焼いたメッシュはレンダラーの Transform から見た位置
                    if (!RaycastMesh (ray, skin.transform, vertices, triangles, out hit) || hit.distance >= distance) continue;

                    Transform bone = FindHeaviestBone (skin, triangles, hit);
                    if (bone == null) continue;
                    picked = bone;
                    distance = hit.distance;
                }
                finally {
                    Object.DestroyImmediate (baked);
                }
            }

            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter> ()) {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer> ();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;

                MeshHit hit;
                if (!RaycastMesh (ray, filter.transform, filter.sharedMesh.vertices, filter.sharedMesh.triangles, out hit) || hit.distance >= distance) continue;
                picked = filter.transform;
                distance = hit.distance;
            }

            return picked;
        }

        struct MeshHit
        {
            public int triangle;
            /// <summary>当たった点の重心座標（頂点 1・2 の重み）</summary>
            public float u;
            public float v;
            public float distance;
        }

        /// <summary>
        /// レイを Transform のローカルへ持っていって、全部の三角形と交差させる。一番手前の当たりを返す
        /// </summary>
        static bool RaycastMesh (Ray ray, Transform transform, Vector3[] vertices, int[] triangles, out MeshHit hit)
        {
            hit = new MeshHit { triangle = -1, distance = float.MaxValue };

            Matrix4x4 toLocal = transform.worldToLocalMatrix;
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            Vector3 origin = toLocal.MultiplyPoint3x4 (ray.origin);
            Vector3 direction = toLocal.MultiplyVector (ray.direction);

            for (int i = 0; i + 2 < triangles.Length; i += 3) {
                float t, u, v;
                if (!IntersectTriangle (origin, direction, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]], out t, out u, out v)) continue;

                // 拡縮があるとローカルの t は距離にならないので、ワールドへ戻して測る
                float worldDistance = Vector3.Distance (ray.origin, toWorld.MultiplyPoint3x4 (origin + direction * t));
                if (worldDistance >= hit.distance) continue;
                hit = new MeshHit { triangle = i, u = u, v = v, distance = worldDistance };
            }
            return hit.triangle >= 0;
        }

        /// <summary>
        /// Möller–Trumbore。裏面にも当てる（見えている面が裏向きのモデルがあるため）
        /// </summary>
        static bool IntersectTriangle (Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
        {
            t = u = v = 0;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 p = Vector3.Cross (direction, ac);
            float determinant = Vector3.Dot (ab, p);
            if (Mathf.Abs (determinant) < 1e-9f) return false;

            float inverse = 1 / determinant;
            Vector3 toA = origin - a;
            u = Vector3.Dot (toA, p) * inverse;
            if (u < 0 || u > 1) return false;

            Vector3 q = Vector3.Cross (toA, ab);
            v = Vector3.Dot (direction, q) * inverse;
            if (v < 0 || u + v > 1) return false;

            t = Vector3.Dot (ac, q) * inverse;
            return t > 0;
        }

        /// <summary>
        /// 当たった面の 3 頂点のウェイトを重心座標で混ぜて、一番大きい骨を返す
        /// </summary>
        static Transform FindHeaviestBone (SkinnedMeshRenderer skin, int[] triangles, MeshHit hit)
        {
            BoneWeight[] weights = skin.sharedMesh.boneWeights;
            Transform[] bones = skin.bones;
            if (weights.Length == 0 || bones.Length == 0) return null;

            Dictionary<int, float> total = new Dictionary<int, float> ();
            float[] coefficients = { 1 - hit.u - hit.v, hit.u, hit.v };
            for (int i = 0; i < 3; i++) {
                int vertex = triangles[hit.triangle + i];
                if (vertex >= weights.Length) continue;
                BoneWeight weight = weights[vertex];
                Add (total, weight.boneIndex0, weight.weight0 * coefficients[i]);
                Add (total, weight.boneIndex1, weight.weight1 * coefficients[i]);
                Add (total, weight.boneIndex2, weight.weight2 * coefficients[i]);
                Add (total, weight.boneIndex3, weight.weight3 * coefficients[i]);
            }

            int best = -1;
            float bestWeight = 0;
            foreach (KeyValuePair<int, float> pair in total) {
                if (pair.Value <= bestWeight) continue;
                best = pair.Key;
                bestWeight = pair.Value;
            }
            return best >= 0 && best < bones.Length ? bones[best] : null;
        }

        static void Add (Dictionary<int, float> total, int index, float weight)
        {
            if (weight <= 0) return;
            float current;
            total.TryGetValue (index, out current);
            total[index] = current + weight;
        }

        public Bounds GetBounds ()
        {
            Renderer[] renderers = model != null ? model.GetComponentsInChildren<Renderer> () : new Renderer[0];
            if (renderers.Length == 0) return new Bounds (new Vector3 (0, 1, 0), new Vector3 (1, 2, 1));

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers) {
                bounds.Encapsulate (renderer.bounds);
            }
            return bounds;
        }

        /// <summary>
        /// プレビューシーンに GameObject を作る。PreviewRenderUtility が自分のカメラを作るのと同じく、隠して作ってから移すので、
        /// 開いているシーンの Hierarchy には出ない
        /// </summary>
        GameObject CreateGameObject (string name)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags (name, HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene (go, scene_);
            go.hideFlags = HideFlags.DontSave;
            return go;
        }

        GameObject CreateRoot (string name)
        {
            GameObject go = CreateGameObject (name);
            roots_.Add (go);
            return go;
        }

        void SetupCameraAndLights ()
        {
            Camera camera = renderUtility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = kBackgroundColor;
            renderUtility.ambientColor = kAmbientColor;

            Light[] lights = renderUtility.lights;
            lights[0].intensity = 1.1f;
            lights[0].color = Color.white;
            lights[1].intensity = 0.5f;
            lights[1].color = new Color (0.75f, 0.8f, 1f);
        }

        GameObject CreateFloor ()
        {
            Shader shader = Shader.Find ("Hidden/Internal-Colored");

            GameObject floor = CreateRoot ("Floor");
            AddMesh (floor, CreateFloorMesh (), CreateMaterial (shader, true));

            GameObject grid = CreateGameObject ("Grid");
            grid.transform.SetParent (floor.transform, false);
            // 床と重なってちらつかないように少し浮かせる
            grid.transform.localPosition = new Vector3 (0, 0.001f, 0);
            AddMesh (grid, CreateGridMesh (), CreateMaterial (shader, false));
            return floor;
        }

        Material CreateMaterial (Shader shader, bool opaque)
        {
            Material material = new Material (shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetInt ("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt ("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt ("_Cull", (int)CullMode.Off);
            material.SetInt ("_ZWrite", opaque ? 1 : 0);
            material.SetInt ("_ZTest", (int)CompareFunction.LessEqual);
            material.renderQueue = opaque ? (int)RenderQueue.Geometry : (int)RenderQueue.Transparent;
            assets_.Add (material);
            return material;
        }

        void AddMesh (GameObject go, Mesh mesh, Material material)
        {
            assets_.Add (mesh);
            go.AddComponent<MeshFilter> ().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer> ();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static Mesh CreateFloorMesh ()
        {
            float e = kFloorExtent;
            Mesh mesh = new Mesh { name = "MktPreviewFloor", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices (new List<Vector3> { new Vector3 (-e, 0, -e), new Vector3 (-e, 0, e), new Vector3 (e, 0, e), new Vector3 (e, 0, -e) });
            mesh.SetColors (Enumerable.Repeat (kFloorColor, 4).ToList ());
            mesh.SetTriangles (new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        /// <summary>
        /// 0.5m 間隔の線（1m ごとに濃く）。原点を通る線は X 軸を赤、Z 軸を青にする
        /// </summary>
        static Mesh CreateGridMesh ()
        {
            List<Vector3> vertices = new List<Vector3> ();
            List<Color> colors = new List<Color> ();
            int count = Mathf.RoundToInt (kGridExtent / kGridStep);
            for (int i = -count; i <= count; i++) {
                float p = i * kGridStep;
                Color color = (i % 2 == 0) ? kGridMajorColor : kGridMinorColor;
                AddLine (vertices, colors, new Vector3 (p, 0, -kGridExtent), new Vector3 (p, 0, kGridExtent), i == 0 ? kGridZAxisColor : color);
                AddLine (vertices, colors, new Vector3 (-kGridExtent, 0, p), new Vector3 (kGridExtent, 0, p), i == 0 ? kGridXAxisColor : color);
            }

            Mesh mesh = new Mesh { name = "MktPreviewGrid", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices (vertices);
            mesh.SetColors (colors);
            mesh.SetIndices (Enumerable.Range (0, vertices.Count).ToArray (), MeshTopology.Lines, 0);
            return mesh;
        }

        static void AddLine (List<Vector3> vertices, List<Color> colors, Vector3 start, Vector3 end, Color color)
        {
            vertices.Add (start);
            vertices.Add (end);
            colors.Add (color);
            colors.Add (color);
        }

        void CreateModel (GameObject prefab)
        {
            GameObject holder = CreateRoot ("Model");
            // 開いているシーンのキャラを編集するときは、その親と同じ場所に置く（編集用の体は作るときに表示モデルの置き場所を写す）
            if (placement_ != null) {
                holder.transform.SetPositionAndRotation (placement_.position, placement_.rotation);
                holder.transform.localScale = placement_.lossyScale;
            }
            // 親を無効にしてから複製する。ゲーム用コンポーネントの Awake / OnEnable を走らせずに外せる
            holder.SetActive (false);
            model = Object.Instantiate (prefab, holder.transform, false);
            model.name = prefab.name;
            StripComponents (model, StripPolicy.EditTarget);
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> (true)) {
                // 骨を動かしても枠が追従しないので、カメラの外と判定されて消えないようにする
                skin.updateWhenOffscreen = true;
            }
            holder.SetActive (true);

            animator = model.GetComponentsInChildren<Animator> (true).FirstOrDefault (a => a.avatar != null && a.avatar.isHuman);
            if (animator == null) {
                error = Tr ("PREVIEW_STAGE_NO_HUMANOID_ANIMATOR", prefab.name);
                return;
            }

            // 基準姿勢は prefab の初期姿勢（T ポーズ）1 つ。表示のリセットと FK の差分の基準は同じ値を使う
            CaptureRestPose ();
            editingRig = EditingRig.Create (definition_, animator, CreateGameObject, RigProbe.Describe (model));
            editingRigSolver = new EditingRigSolver (editingRig);
            // 全身 IK（S25）。リグの定義が全身 IK で、点が 1 つでも置けたときだけ
            if (editingRig.binding.solver == RigSolver.FullBodyIk && editingRig.binding.points.Count > 0) {
                fullBody = new FullBodyRig (editingRig, definition_);
            }
            clipSampler_ = new ClipSampler (editingRig.animator);
            genericSampler_ = new ClipSampler (editingRig.animator);
            // コントロールの初期値は基準姿勢から取る（FK の差分は 0、IK の目標は今の手足の先）
            editingRigSolver.Capture ();
            CaptureControlRest ();
            foreach (string problem in editingRig.errors) {
                Debug.LogWarning ("MotionEditor: " + prefab.name + ": " + problem);
            }
        }

        void CaptureRestPose ()
        {
            poseTransforms_ = animator.GetComponentsInChildren<Transform> (true);
            restPositions_ = poseTransforms_.Select (t => t.localPosition).ToArray ();
            restRotations_ = poseTransforms_.Select (t => t.localRotation).ToArray ();
            restScales_ = poseTransforms_.Select (t => t.localScale).ToArray ();
        }

        /// <summary>
        /// 残すもの以外のコンポーネントを外す（StripPolicy）。RequireComponent で頼られているものは、頼っている側を先に外す
        /// </summary>
        static void StripComponents (GameObject root, StripPolicy policy)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform> (true)) {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript (t.gameObject);
            }

            List<Component> pending = root.GetComponentsInChildren<Component> (true).Where (c => !IsKeptComponent (c, policy)).ToList ();
            bool removed = true;
            while (pending.Count > 0 && removed) {
                removed = false;
                for (int i = 0; i < pending.Count; i++) {
                    Component component = pending[i];
                    // 他を外したときに一緒に消えたもの
                    if (component == null) {
                        pending.RemoveAt (i--);
                        removed = true;
                        continue;
                    }
                    if (IsRequiredByOthers (component)) continue;

                    Object.DestroyImmediate (component);
                    pending.RemoveAt (i--);
                    removed = true;
                }
            }

            // 外せなかったもの（依存が循環しているなど）と、段として残したものは止めておく。
            // 残した段はこちらから呼ぶときだけ動かす（edit-mode で勝手に走らせない）
            foreach (Component component in root.GetComponentsInChildren<Component> (true)) {
                if (IsVisualComponent (component)) continue;
                // 文脈の Director は時計を窓から回すので止めない（S15a）
                if (policy == StripPolicy.Context && IsTimelineComponent (component)) continue;
                Behaviour behaviour = component as Behaviour;
                if (behaviour != null) behaviour.enabled = false;
            }
        }

        /// <summary>
        /// 外さずに残すもの。見た目（メッシュ・Animator）に加えて、**段として出すもの**（Rigging の部品・[PoseLayer]）。
        /// 段のコンポーネントを外すと「キャラに付いていない」ことになり、その段がスタックから消えてしまう
        /// </summary>
        /// <summary>
        /// 剥がさずに残すものを外から足す受け口。ゲーム固有の設定（左右の対応表など）を読む必要があるコンポーネントを残す。
        /// 残したものは止めてある（こちらから呼ぶときだけ動く）ので、置いても edit-mode で走らない
        /// </summary>
        public static void KeepComponent (System.Func<Component, bool> predicate)
        {
            if (predicate != null && !keepPredicates_.Contains (predicate)) keepPredicates_.Add (predicate);
        }

        static readonly List<System.Func<Component, bool>> keepPredicates_ = new List<System.Func<Component, bool>> ();

        static bool IsKeptByHook (Component component)
        {
            foreach (System.Func<Component, bool> predicate in keepPredicates_) {
                try {
                    if (predicate (component)) return true;
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            return false;
        }

        static bool IsKeptComponent (Component component, StripPolicy policy)
        {
            if (IsVisualComponent (component)) return true;
            if (IsKeptByHook (component)) return true;
            switch (policy) {
                case StripPolicy.Context:
                    return IsTimelineComponent (component) || component is ParticleSystem || component is ParticleSystemRenderer;
                default:
                    return RigProbe.IsRigComponent (component) || LayerTypes.IsPoseLayer (component);
            }
        }

        /// <summary>
        /// Director と、Timeline の仕組みに属するもの（名前空間で見る。Timeline パッケージへは依存しない）
        /// </summary>
        static bool IsTimelineComponent (Component component)
        {
            if (component is UnityEngine.Playables.PlayableDirector) return true;
            string ns = component.GetType ().Namespace;
            return ns != null && (ns == "UnityEngine.Timeline" || ns.StartsWith ("UnityEngine.Timeline."));
        }

        static bool IsVisualComponent (Component component)
        {
            return component == null || kVisualComponents.Contains (component.GetType ());
        }

        static bool IsRequiredByOthers (Component component)
        {
            foreach (Component other in component.GetComponents<Component> ()) {
                if (other == null || other == component) continue;
                foreach (RequireComponent require in other.GetType ().GetCustomAttributes (typeof (RequireComponent), true)) {
                    if (IsType (require.m_Type0, component) || IsType (require.m_Type1, component) || IsType (require.m_Type2, component)) return true;
                }
            }
            return false;
        }

        static bool IsType (System.Type type, Component component)
        {
            return type != null && type.IsInstanceOfType (component);
        }
    }

}
