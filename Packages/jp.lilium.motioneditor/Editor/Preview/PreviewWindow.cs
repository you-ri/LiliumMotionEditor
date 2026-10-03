using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// タイムラインに出すトラック（全部の対象か、選んでいる対象だけか）
    /// </summary>
    public enum ShowTracksFlag : int
    {
        All,
        Selected,
    };

    /// <summary>
    /// モーション編集専用の窓。キャラは PreviewRenderUtility の別シーンに置いて描くので、開いているシーンには何も置かない。
    /// 骨ハンドル・トラック・Spinner・Picker は、つかむ対象（PoseTarget）だけを通して動かす。
    /// タイムラインは Overlay（TimelineOverlay）でドッキングできる。
    ///
    /// ツールの仕組み（ISupportsEditorTools）に乗せている。骨ハンドルと視点の操作は既定のツールコンテキスト（PoseToolContext）が受け持ち、
    /// 道具は EditorTool（targetContext = typeof (PoseToolContext)）、パネルやツールバーは [Overlay (typeof (PreviewWindow), ...)] で足す。
    /// ツールの仕組みが無い版（6.6 より前）は、表示域の IMGUIContainer を窓が自分で置いて同じ操作を回す（道具は足せない）
    /// </summary>
#if UNITY_6000_6_OR_NEWER
    [EditorToolOwner (typeof (PoseToolContext))]
    public partial class PreviewWindow : EditorWindow, IEditorHost, ILayerHost, ISupportsOverlays, ISupportsEditorTools
#else
    public partial class PreviewWindow : EditorWindow, IEditorHost, ILayerHost, ISupportsOverlays
#endif
    {
        public enum ViewPreset
        {
            Front,
            Side,
            Top,
        }

        /// <summary>
        /// タイムラインの 1 本のトラック。先頭はクリップ全体、続いて骨ごと（TimelineOverlay が描く）
        /// </summary>
        public struct TimelineTrack
        {
            public string label;
            /// <summary>
            /// キーのあるフレーム（0 以上・昇順）
            /// </summary>
            public int[] keys;
            /// <summary>
            /// このトラックが扱うカーブ。null ならクリップ全体
            /// </summary>
            public System.Predicate<EditorCurveBinding> filter;
        }

        /// <summary>タイムラインの下端（Layers パネルの一覧）の最小の高さ</summary>
        const float kMinLayersHeight = 120;
        const float kMinLayersWidth = 200;
        /// <summary>タイムラインのトラック一覧の最小の高さ</summary>
        const float kMinTimelineTracksHeight = 60;
        /// <summary>
        /// ISupportsEditorTools の窓に Unity が入れる、ツールのハンドルを描く IMGUIContainer の名前
        /// </summary>
        const string kEditorToolsContainerName = "EditorToolsIMGUIContainer";
        static readonly int kViewportHash = "MktPreviewViewport".GetHashCode ();

        [SerializeField] GameObject prefab_;
        [SerializeField] AnimationClip editingClip_;
        /// <summary>
        /// 編集用リグの定義。null なら既定の定義
        /// </summary>
        /// <summary>今の世界（編集用リグ）を作ったときの定義。キャラの設定・プロジェクト設定で差し替えられたら作り直す</summary>
        EditRigDefinition stageRigDefinition_;
        /// <summary>
        /// 時計（クリップの秒・キーの格子・再生）。姿勢は秒で当て、キーは格子に丸めて打つ
        /// </summary>
        [SerializeField] Clock clock_ = new Clock ();
        [SerializeField] float handleSize_ = 0.03f;
        /// <summary>Transform パネルの Spinner の Move で、ドラッグ量に掛ける倍率（1 で 1 ピクセル 2.5mm）</summary>
        [SerializeField] float spinMoveScale_ = 1;
        [SerializeField] bool showBones_ = true;
        /// <summary>手足の IK のコントロールを常に出すか（骨を隠していても残す）。全身 IK の点も、切ると選んでいる物だけになる</summary>
        [SerializeField] bool showIk_ = true;
        [SerializeField] bool showFloor_ = true;
        [SerializeField] ShowTracksFlag showTracks_ = ShowTracksFlag.Selected;
        /// <summary>タイムライン オーバーレイのトラック一覧の高さ。下端のハンドルで変える</summary>
        [SerializeField] float timelineTracksHeight_ = 200;
        /// <summary>タイムラインの目盛りの拡大率（1 フレームあたりのピクセル数）</summary>
        [SerializeField] float timelinePixelsPerFrame_ = 12;
        /// <summary>タイムラインの目盛りの左端のフレーム</summary>
        [SerializeField] float timelineFirstFrame_ = -2;
        /// <summary>Layers オーバーレイの一覧の高さ。下端のハンドルで変える</summary>
        [SerializeField] float layersHeight_ = 420;
        [SerializeField] float layersWidth_ = 240;
        [SerializeField] OrbitCamera camera_ = new OrbitCamera ();
        /// <summary>
        /// カメラを合わせたモデル。再読み込みで作り直したときは視点を保つ
        /// </summary>
        [SerializeField] GameObject framedPrefab_;
        [SerializeField] List<string> selectedNames_ = new List<string> ();
        /// <summary>
        /// レイヤーの状態（書き出す段 / つかむ段 / 隠している段）。段そのものは対象から作り直すので、選択だけを持つ
        /// </summary>
        [SerializeField] LayerState layerState_ = new LayerState ();

        PreviewStage stage_;
        PoseStack poseStack_ = new PoseStack ();
        /// <summary>
        /// つかめるもの（骨・IK）。窓はこれを通してだけ動かしてキーを打つ
        /// </summary>
        List<PoseTarget> targets_ = new List<PoseTarget> ();
        readonly Dictionary<GameObject, PoseTarget> targetByObject_ = new Dictionary<GameObject, PoseTarget> ();
        readonly List<GameObject> selection_ = new List<GameObject> ();
        VisualElement viewport_;
        int[] keyFrames_;
        /// <summary>
        /// 骨ごとのトラックのキー（タイムライン）。GetKeyFrames が全体のキーを作り直すときに一緒に捨てる
        /// </summary>
        readonly Dictionary<PoseTarget, int[]> trackKeys_ = new Dictionary<PoseTarget, int[]> ();
        /// <summary>
        /// 今のクリップを編集できない理由（EditingClip.GetProblem）。編集できるなら null
        /// </summary>
        string clipProblem_;
        int focusControl_;
        /// <summary>
        /// 再生中につかんで時計を止めているもの（ハンドル・Spinner）。両方離したら再生を続ける
        /// </summary>
        bool handleHold_;
        /// <summary>
        /// ハンドルで動かしたがまだキーを打っていない。打つのは離したとき（CommitHandleKeys）。
        /// ドラッグの刻みごとに打つと、クリップ全体の Undo の記録・全カーブの書き直し・キー一覧の作り直しで 1 刻み 20ms を超えてカクつく
        /// </summary>
        bool handleKeysPending_;
        /// <summary>
        /// 全身 IK の点を動かしたがまだキーを打っていない（S25）。打つのは離したときで、動かした点の値だけ（骨の角度には打たない）
        /// </summary>
        bool pointKeysPending_;
        /// <summary>
        /// いくつか選んでいる全身 IK の点のうち、その後に押した 1 つ（中心の点。無ければ null）。
        /// ギズモはこの点にだけ出し、回すと選んでいる点をこの点のまわりで回す。選び直すと消える
        /// </summary>
        GameObject pivotPoint_;
        bool spinHold_;
        int cameraDragButton_;
        bool cameraDragAlt_;
        /// <summary>視点をドラッグしている間（カーソルを画面の端で反対側へ回している）</summary>
        bool cameraDragging_;
        /// <summary>視点のドラッグを始めてからマウスが動いた量（ピクセル）。これより小さいまま離した右ボタンは、クリックとして扱う</summary>
        float cameraDragTravel_;
        const float kClickTravel = 4;

        /// <summary>
        /// キャラ・クリップが替わったとき（ツールバーのオーバーレイが表示を合わせる）
        /// </summary>
        public event System.Action stateChanged;

        /// <summary>
        /// 段の並びや選択が替わったとき。骨を選ぶたびに Layers のパネルを作り直さないよう、stateChanged と分けている
        /// </summary>
        public event System.Action stackChanged;

        /// <summary>
        /// 編集中のクリップのキーが変わったとき（ハンドル・Spinner・Reset・貼り付け・Rig の重みなど、書く経路を問わない）。
        /// 1 回の書き込みで何本ものカーブが変わるので、まとめて次の更新で 1 回だけ出す
        /// </summary>
        public event System.Action keysChanged;

        bool keysChangedQueued_;

        /// <summary>
        /// 姿勢が変わったとき（フレームの移動・再生・Undo・ハンドルや Spinner で動かした後）。値を映しているパネルが描き直す
        /// </summary>
        public event System.Action poseSampled;

        [MenuItem ("Window/Motion Editor (Preview)")]
        static void Open ()
        {
            GetWindow<PreviewWindow> ().Show ();
        }

        public PreviewStage stage
        {
            get { return stage_; }
        }

        /// <summary>
        /// 姿勢の作られ方（段の並び）。姿勢を作るのも、Layers のパネルを描くのもこれを元にする
        /// </summary>
        public PoseStack poseStack
        {
            get { return poseStack_; }
        }

        public GameObject model
        {
            get { return prefab_; }
        }

        public AnimationClip clip
        {
            get { return editingClip_; }
        }

        /// <summary>
        /// 編集用リグの定義。キャラの設定で上書きしていればその値、それ以外（設定が無いキャラ・上書きの欄が空も）はプロジェクト設定の値
        /// （空ならパッケージの既定）。直すのはキャラの設定・プロジェクト設定のインスペクタで、差し替えると窓が作り直す（KeepRigDefinition）
        /// </summary>
        public EditRigDefinition rigDefinition
        {
            get {
                CharacterSettings settings = characterSettings;
                if (settings != null && settings.overrideRigDefinition && settings.rigDefinition != null) return settings.rigDefinition;
                return ProjectSettings.instance.defaultRigDefinition;
            }
        }

        /// <summary>
        /// 設定を探すキャラの prefab（開いているシーンのキャラなら、その元の prefab）
        /// </summary>
        GameObject settingsPrefab
        {
            get {
                if (sceneTarget_ != null) return PrefabUtility.GetCorrespondingObjectFromOriginalSource (sceneTarget_);
                return prefab_;
            }
        }

        /// <summary>このキャラの設定（S19。無ければ null）</summary>
        public CharacterSettings characterSettings
        {
            get { return SettingsLookup.Find (settingsPrefab); }
        }

        /// <summary>
        /// 既にある設定のアセットを、このキャラの設定にする。ほかのキャラの設定だったとき・このキャラに別の設定があるときは、
        /// 付け替えてよいか聞く（1 キャラ 1 設定なので、相手からは外れる）。付け替えたら true
        /// </summary>
        public bool AssignCharacterSettings (CharacterSettings settings)
        {
            GameObject prefab = settingsPrefab;
            CharacterSettings current = characterSettings;
            if (prefab == null || settings == null || settings == current) return false;

            List<string> notes = new List<string> ();
            if (settings.prefab != null && settings.prefab != prefab) notes.Add ("「" + settings.name + "」は " + settings.prefab.name + " の設定です。" + settings.prefab.name + " では使われなくなります。");
            if (current != null) notes.Add ("今の設定「" + current.name + "」はこのキャラから外れます。");
            if (notes.Count > 0 && !EditorUtility.DisplayDialog ("キャラの設定を付け替える", string.Join ("\n", notes) + "\n\n" + prefab.name + " の設定を「" + settings.name + "」にしますか?", "付け替える", "やめる")) {
                return false;
            }

            if (poseStack_ != null && current != null) poseStack_.SaveState (current.layers);
            SettingsLookup.Assign (settings, prefab);
            ResetWindowSettingsFromProject ();
            overridesBase_ = null;
            RefreshClipState ();
            RebuildStage ();
            RaiseStateChanged ();
            RaiseStackChanged ();
            return true;
        }

        /// <summary>
        /// このキャラの設定を作る。今の値（Layers・編集用リグの定義・Auto）を取り込む
        /// </summary>
        public CharacterSettings CreateCharacterSettings ()
        {
            GameObject prefab = settingsPrefab;
            if (prefab == null || characterSettings != null) return characterSettings;
            if (poseStack_ != null) poseStack_.SaveState (layerState_);
            CharacterSettings settings = SettingsLookup.Create (prefab, layerState_, ProjectSettings.instance.rigDefinition, autoBake_);
            RaiseStateChanged ();
            RaiseStackChanged ();
            return settings;
        }

        /// <summary>Layers の状態の置き場所。キャラの設定があればそこ、無ければ窓の中（保存されない）</summary>
        LayerState layerState
        {
            get {
                CharacterSettings settings = characterSettings;
                return settings != null ? settings.layers : layerState_;
            }
        }

        /// <summary>Layers の状態を変えるときに Undo へ記録する物</summary>
        Object layerStateOwner
        {
            get {
                CharacterSettings settings = characterSettings;
                return settings != null ? (Object)settings : this;
            }
        }

        bool saveSettingsQueued_;

        /// <summary>
        /// キャラの設定を保存する。重みのドラッグのように続けて変わるので、次の更新でまとめて 1 回書く
        /// </summary>
        void SaveCharacterSettings ()
        {
            CharacterSettings settings = characterSettings;
            if (settings == null) return;
            EditorUtility.SetDirty (settings);
            if (saveSettingsQueued_) return;
            saveSettingsQueued_ = true;
            EditorApplication.delayCall += () => {
                saveSettingsQueued_ = false;
                if (settings != null) AssetDatabase.SaveAssetIfDirty (settings);
            };
        }

        /// <summary>
        /// キャラを替えたとき、キャラの設定が無ければ窓の中の値をプロジェクト設定で始め直す（前のキャラの値を持ち越さない）
        /// </summary>
        void ResetWindowSettingsFromProject ()
        {
            if (characterSettings != null) return;
            ProjectSettings project = ProjectSettings.instance;
            layerState_ = SettingsLookup.Clone (project.layers);
            autoBake_ = project.autoBake;
        }

        /// <summary>
        /// FBX の中のクリップなど、キーを打てないクリップを開いている
        /// </summary>
        public bool isClipReadOnly
        {
            get { return clipProblem_ != null; }
        }

        /// <summary>
        /// 今のクリップを編集できない理由（編集できるなら null）
        /// </summary>
        public string clipProblem
        {
            get { return clipProblem_; }
        }

        int strayCurveCount_ = -1;

        /// <summary>
        /// 今のクリップに残っている、編集用リグ以外を指すカーブの数（旧形式の名残。姿勢には効かない）
        /// </summary>
        public int strayCurveCount
        {
            get {
                if (strayCurveCount_ < 0) strayCurveCount_ = clipProblem_ == null ? EditingClip.CountStrayCurves (editingClip_) : 0;
                return strayCurveCount_;
            }
        }

        /// <summary>
        /// 今のクリップを編集できるかと、残ったカーブの数を数え直す
        /// </summary>
        void RefreshClipState ()
        {
            clipProblem_ = EditingClip.GetProblem (editingClip_);
            strayCurveCount_ = -1;
            // 土台のフレームの細かさに合わせる（Humanoid を土台にしていればそのクリップ。S14）
            AnimationClip source = baseClip;
            if (source != null) clock_.rate = source.frameRate;
            // 元のクリップに重ねている Override を探し直す（S14）
            LoadOverrides ();
        }

        /// <summary>
        /// 残っている旧形式のカーブを消す（Undo で戻せる）
        /// </summary>
        public void RemoveStrayCurves ()
        {
            if (!CanEditClip () || strayCurveCount == 0) return;
            if (!EditorUtility.DisplayDialog ("Motion Editor", editingClip_.name + " から、編集用リグ以外を指すカーブ " + strayCurveCount + " 本を消します。", "消す", "やめる")) return;

            RecordClipUndo ("Remove Stray Curves");
            EditingClip.RemoveStrayCurves (editingClip_);
            keyFrames_ = null;
            RefreshClipState ();
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
            RaiseStateChanged ();
        }

        public int frame
        {
            get { return currentFrame; }
        }

        public Clock clock
        {
            get { return clock_; }
        }

        /// <summary>
        /// キーを打つフレーム（今の時刻を格子に丸めたもの）
        /// </summary>
        int currentFrame
        {
            get { return clock_.keyFrame; }
        }

        /// <summary>
        /// 再生中にハンドルや Spinner でつかんだら時計を止める。離したら続きから再生する（ReleaseClock）
        /// </summary>
        void HoldClock ()
        {
            if (!clock_.isPlaying || clock_.isHeld) return;
            clock_.Hold ();
            RepaintView ();
        }

        void ReleaseClock ()
        {
            if (handleHold_ || spinHold_ || !clock_.isHeld) return;
            clock_.Release (EditorApplication.timeSinceStartup);
            RepaintView ();
        }

        public IReadOnlyList<GameObject> selection
        {
            get { return selection_; }
        }

        /// <summary>
        /// 選んでいる対象（複数選べるようになるまでは先頭の 1 つ）
        /// </summary>
        public PoseTarget selectedTarget
        {
            get { return FindTarget (selection_.FirstOrDefault (g => g != null)); }
        }

        PoseTarget FindTarget (GameObject go)
        {
            PoseTarget target;
            return go != null && targetByObject_.TryGetValue (go, out target) ? target : null;
        }

        public OrbitCamera orbitCamera
        {
            get { return camera_; }
        }

        /// <summary>
        /// ツールのハンドル用カメラ。ツールの GUI を回す直前に Unity が読むので、ここで描画用のカメラと視点を揃える。
        /// このとき Screen は窓（ホストビュー）の大きさ
        /// </summary>
        public Camera handlesCamera
        {
            get {
                if (stage_ == null) return null;
                stage_.EnsureHandlesTarget (Screen.width, Screen.height);
                ApplyView (stage_.handlesCamera);
                // 出力先（ダミー）ではなく表示域の形に合わせる。絵と同じ見え方にしないとハンドルが骨からずれる
                Rect view = ViewportRect ();
                if (view.height > 1) stage_.handlesCamera.aspect = view.width / view.height;
                return stage_.handlesCamera;
            }
        }

        public float handleSize
        {
            get { return handleSize_; }
            set {
                handleSize_ = Mathf.Max (0.001f, value);
                RepaintView ();
            }
        }

        /// <summary>Transform パネルの Spinner の Move で、ドラッグ量に掛ける倍率（窓ごとの設定）</summary>
        public float spinMoveScale
        {
            get { return spinMoveScale_ > 0 ? spinMoveScale_ : 1; }
            set { spinMoveScale_ = Mathf.Max (0.01f, value); }
        }

        public bool showBones
        {
            get { return showBones_; }
            set {
                showBones_ = value;
                RepaintView ();
            }
        }

        /// <summary>手足の IK のコントロールを常に出すか。全身 IK の点（固定の印も）の表示も兼ねる</summary>
        public bool showIk
        {
            get { return showIk_; }
            set {
                showIk_ = value;
                RepaintView ();
            }
        }

        public bool showFloor
        {
            get { return showFloor_; }
            set {
                showFloor_ = value;
                if (stage_ != null) stage_.showFloor = value;
                RepaintView ();
            }
        }

        /// <summary>Layers オーバーレイの一覧の高さ</summary>
        public float layersHeight
        {
            get { return layersHeight_; }
            set { layersHeight_ = Mathf.Max (kMinLayersHeight, value); }
        }

        /// <summary>Layers オーバーレイの幅（行の中身はこの幅に合わせて伸び縮みする）</summary>
        public float layersWidth
        {
            get { return layersWidth_; }
            set { layersWidth_ = Mathf.Max (kMinLayersWidth, value); }
        }

        /// <summary>タイムライン オーバーレイのトラック一覧の高さ</summary>
        public float timelineTracksHeight
        {
            get { return timelineTracksHeight_; }
            set { timelineTracksHeight_ = Mathf.Max (kMinTimelineTracksHeight, value); }
        }

        /// <summary>タイムラインの目盛りの拡大率（1 フレームあたりのピクセル数）</summary>
        public float timelinePixelsPerFrame
        {
            get { return timelinePixelsPerFrame_; }
            set { timelinePixelsPerFrame_ = value; }
        }

        /// <summary>タイムラインの目盛りの左端のフレーム</summary>
        public float timelineFirstFrame
        {
            get { return timelineFirstFrame_; }
            set { timelineFirstFrame_ = value; }
        }

        /// <summary>タイムラインに出すトラック（全部の対象か、選んでいる対象だけか）</summary>
        public ShowTracksFlag showTracks
        {
            get { return showTracks_; }
            set { showTracks_ = value; }
        }

        float IEditorHost.currentFrame
        {
            get { return currentFrame; }
        }

        public bool IsSelected (GameObject go)
        {
            return selection_.Contains (go);
        }

        public void Select (GameObject go)
        {
            if (selection_.Count == 1 && selection_[0] == go) return;
            // いくつか選んでいる全身 IK の点の 1 つを押したときは、選択を保つ（そのままつかんで、まとめて動かせる）。
            // 1 つだけにするときは、何も無い所を押すか、枠で選び直す
            // 押した点は中心の点になる（ギズモをその点に出し、選んでいる点をその点のまわりで回す）
            if (go != null && selection_.Count > 1 && selection_.Contains (go) && FindTarget (go) is BodyPointTarget) {
                if (pivotPoint_ != go) {
                    pivotPoint_ = go;
                    RaiseStateChanged ();
                }
                return;
            }
            pivotPoint_ = null;
            selection_.Clear ();
            if (go != null) selection_.Add (go);
            SaveSelection ();
            RefreshGrabMute ();
            // 選んだ物で表示が変わるオーバーレイ（Spinner）に知らせる
            RaiseStateChanged ();
        }

        /// <summary>
        /// まとめて選ぶ（Picker の All / Right / Left や、一覧の複数選択）。
        /// 先頭が「今の対象」になる（Spinner・Transform・超必のカメラなど 1 つだけ見る所は先頭を使う）
        /// </summary>
        public void SelectObjects (IEnumerable<GameObject> objects)
        {
            pivotPoint_ = null;
            selection_.Clear ();
            if (objects != null) {
                foreach (GameObject go in objects) {
                    if (go != null && !selection_.Contains (go)) selection_.Add (go);
                }
            }
            SaveSelection ();
            RefreshGrabMute ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 表示の骨をつかむとき（✋ = Display Pose）の、選んでいる物が動かす表示モデルの骨。
        /// その骨（か親）を動かす Rig の拘束は、選んでいる間だけ切る（PreviewStage.grabbedBones）
        /// </summary>
        IEnumerable<Transform> GrabbedDisplayBones ()
        {
            PoseLayer manipulate = poseStack_ != null ? poseStack_.manipulateLayer : null;
            if (manipulate == null || manipulate.kind != LayerKind.DisplayBone) yield break;
            foreach (GameObject go in selection_) {
                PoseTarget target = FindTarget (go);
                Transform bone = target != null ? target.displayGrabBone : null;
                if (bone != null) yield return bone;
            }
        }

        /// <summary>
        /// 表示の骨をつかんでいる間に選び直したら、切る拘束が変わるので姿勢を作り直す
        /// </summary>
        void RefreshGrabMute ()
        {
            PoseLayer manipulate = poseStack_ != null ? poseStack_.manipulateLayer : null;
            if (stage_ == null || manipulate == null || manipulate.kind != LayerKind.DisplayBone) return;
            if (stage_.mutedConstraints.Count == 0 && !GrabbedDisplayBones ().Any ()) return;
            ghostsDirty_ = true;
            SamplePose ();
        }

        /// <summary>
        /// ハンドルが動かすのはプレビューシーンの物だけなので記録しない。元に戻すのはクリップの Undo（RecordClipUndo）で、戻したら姿勢を当て直す
        /// </summary>
        void IEditorHost.RecordUndo (Object obj, string name)
        {
        }

        /// <summary>
        /// 今のクリップ・今のフレームへキーを打つ口（Undo の記録を含む）。パスの基準は編集用の体のルート
        /// </summary>
        /// <param name="edit">ハンドル・Spinner・数値欄などの操作から打つとき。キーの自動追加が切なら、キーのあるカーブだけ書き換える</param>
        CurveWriter BeginCurves (string undoName, bool edit = false)
        {
            Transform root = stage_ != null && stage_.editingRig != null && stage_.editingRig.root != null ? stage_.editingRig.root.transform : null;
            // 書き込み先が Override なら、そのクリップへ元からの差分として書く（S14）
            CurveWriter writer = CurveWriter.Begin (targetClip, root, currentFrame, undoName);
            writer.onlyExistingKeys = edit && !autoKey;
            AttachOverrideStore (writer);
            return writer;
        }

        /// <summary>
        /// キーの自動追加（プロジェクト設定）。切なら、今のフレームにキーがある物しか動かせず、操作でキーは増えない
        /// </summary>
        static bool autoKey
        {
            get { return ProjectSettings.instance.autoKey; }
        }

        /// <summary>
        /// プロジェクト設定を保存したとき。Auto Key が替わると動かせる物が変わるので、パネル（タイムラインのスイッチ・Spinner）と表示を合わせる
        /// </summary>
        void OnProjectSettingsChanged ()
        {
            RaiseStateChanged ();
            RepaintView ();
        }

        /// <summary>
        /// その物を今のフレームで動かせるか。キーの自動追加が切なら、今のフレームにその物のキーがあるときだけ
        /// </summary>
        public bool CanEditTarget (PoseTarget target)
        {
            if (target == null) return false;
            if (autoKey || !CanEditClip ()) return true;
            // 全身 IK の点は、離すと解いた全身の姿勢を打つ（S25）。点ごとのキーではなくフレームで見て、姿勢のキーがあるフレームでだけ動かす
            if (target is BodyPointTarget) return HasPoseKeyAtCurrentFrame ();
            return System.Array.IndexOf (GetTargetKeyFrames (target), currentFrame) >= 0;
        }

        /// <summary>
        /// 全身 IK の点を今のフレームで動かせるか。キーの自動追加が切なら、今のフレームに姿勢のキーがあるときだけ
        /// （離すと全身の骨のキーを打つので、キーの無いフレームで動かすとキーフレームが新しくできてしまう）
        /// </summary>
        bool CanEditPoints ()
        {
            return autoKey || !CanEditClip () || HasPoseKeyAtCurrentFrame ();
        }

        /// <summary>
        /// 今のフレームに、どれかの物（骨・点・コントローラー）のキーがあるか
        /// </summary>
        bool HasPoseKeyAtCurrentFrame ()
        {
            foreach (PoseTarget target in targets_) {
                if (System.Array.IndexOf (GetTargetKeyFrames (target), currentFrame) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 選んでいる物が全部、今のフレームで動かせるか（ハンドルで動かすのは選んでいる物）
        /// </summary>
        bool CanEditSelectedTargets ()
        {
            if (autoKey || !CanEditClip ()) return true;
            foreach (PoseTarget target in targets_) {
                if (IsSelected (target.gameObject) && !CanEditTarget (target)) return false;
            }
            return true;
        }

        /// <summary>
        /// キーが無くて動かせなかったことを知らせる（キーの自動追加が切のとき）
        /// </summary>
        void NotifyNoKey ()
        {
            ShowNotification (new GUIContent ("キーがないので動かせません"), 1.5);
        }

        /// <summary>
        /// クリップを書き換える前の Undo の記録。アセットでないクリップ（メモリ上だけの物）は記録しない。
        /// 記録すると、どのシーンにも属さない物として開いているシーンが変更扱いになる
        /// </summary>
        void RecordClipUndo (string name)
        {
            AnimationClip clip = targetClip;
            if (EditorUtility.IsPersistent (clip)) {
                Undo.RecordObject (clip, name);
            }
        }

        void OnEnable ()
        {
            titleContent = new GUIContent ("Motion Editor", Icons.Load ("Motion"));
            GLDraw.Initialize ();
            Undo.undoRedoPerformed += OnUndoRedo;
            AnimationUtility.onCurveWasModified += OnCurveWasModified;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // 窓の Update は見えている窓にしか来ないので、時計は EditorApplication.update で回す
            // （Scene ビュー型の窓で編集しているとき、この窓はタブの後ろに隠れていることがある。S15d）
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            // 姿勢が変わるたびに、編集しているシーンのキャラへ写す（S15d）
            poseSampled -= ApplySceneMirror;
            poseSampled += ApplySceneMirror;
            // 定義を直したら編集用リグを作り直す（S23）
            SubscribeRigDefinition ();
            ProjectSettings.changed -= OnProjectSettingsChanged;
            ProjectSettings.changed += OnProjectSettingsChanged;

            MigrateHumanoidBase ();
            RefreshClipState ();
            RebuildStage ();
        }

        void OnDisable ()
        {
            EndCameraDrag ();
            Undo.undoRedoPerformed -= OnUndoRedo;
            UnsubscribeRigDefinition ();
            ProjectSettings.changed -= OnProjectSettingsChanged;
            DisposeGhosts ();
            AnimationUtility.onCurveWasModified -= OnCurveWasModified;
            EditorApplication.delayCall -= RaiseKeysChanged;
            keysChangedQueued_ = false;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= Tick;
            poseSampled -= ApplySceneMirror;
            // 写した姿勢を戻す（再読み込み・窓を閉じる）
            sceneMirror_.Release ();
            EditorHost.Release (this);
            DisposeStage ();
        }

        /// <summary>
        /// スクリプトの再読み込みの前に、一時的に作った物を捨てる（作り直しは OnEnable）
        /// </summary>
        void OnBeforeAssemblyReload ()
        {
            DisposeStage ();
        }

        /// <summary>
        /// プレイの開始と終了の前に捨てる。ドメインの再読み込みを切っていると OnDisable が来ないので、
        /// ここで捨てないとグラフや Animator の束縛が残る。作り直しは次の描画（OnGUI）で行う
        /// </summary>
        void OnPlayModeStateChanged (PlayModeStateChange change)
        {
            switch (change) {
                case PlayModeStateChange.ExitingEditMode:
                case PlayModeStateChange.ExitingPlayMode:
                    clock_.Stop ();
                    DisposeStage ();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Play Mode を抜ける途中（まだ再生中の扱い）に描画で作り直された世界は、姿勢を当て損ねている。
                    // シーンのキャラへはその姿勢が写るので（S15d）、編集モードに戻ってから作り直す
                    if (stage_ != null || sceneTarget_ != null) RebuildStage ();
                    Repaint ();
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    Repaint ();
                    break;
            }
        }

        /// <summary>
        /// 表示域は、Unity がツールのハンドル用に入れる IMGUIContainer（flexGrow 1）をそのまま使い、
        /// 窓全体に広げる。キャラの絵はその下（窓の OnGUI）に描く。タイムライン・ツールバー・パネルはすべて Overlay
        /// </summary>
        void CreateGUI ()
        {
            VisualElement root = rootVisualElement;

#if UNITY_6000_6_OR_NEWER
            viewport_ = root.Q<IMGUIContainer> (kEditorToolsContainerName);
            if (viewport_ == null) {
                Debug.LogWarning ("MotionEditor: ツールのハンドル用の IMGUIContainer が見つからないので、骨ハンドルを出せません");
                viewport_ = new VisualElement { name = "MktViewport" };
                viewport_.style.flexGrow = 1;
                root.Add (viewport_);
            }
#else
            // ツールの仕組みが無い版: Unity が入れる IMGUIContainer の代わりを置き、ハンドル用のカメラを当てて同じ操作を回す
            viewport_ = new IMGUIContainer (OnViewportGUI) { name = kEditorToolsContainerName };
            viewport_.style.flexGrow = 1;
            root.Add (viewport_);
#endif
        }

#if !UNITY_6000_6_OR_NEWER
        /// <summary>
        /// 表示域の操作（ツールの仕組みが無い版）。6.6 以降で Unity がツールコンテキストを呼ぶ前にすることを、ここで代わりにする
        /// </summary>
        void OnViewportGUI ()
        {
            Camera camera = handlesCamera;
            if (camera == null) return;
            // Handles.SetCamera (rect, camera) は、出力先を持つカメラだと pixelRect を出力先の全面のままにする。
            // それだと骨が出力先（256 単位で切り上げた大きさ）へ引き伸ばされて描かれ、キャラの絵からずれるので、自分で入れる。
            // マウスの光線（GUIPointToWorldRay）や WorldToGUIPoint は、この IMGUIContainer の中では表示域の左上を原点にし、
            // 上下を pixelRect の高さで返す（pixelRect.y を引かない）。pixelRect は原点に置いた表示域の大きさにして、
            // 描く先（GL のビューポート）だけ窓の中の本当の位置にする
            float ppp = EditorGUIUtility.pixelsPerPoint;
            Rect local = ViewportRect ();
            camera.pixelRect = new Rect (0, 0, local.width * ppp, local.height * ppp);
            Handles.SetCamera (camera);
            if (Event.current.type == EventType.Repaint) {
                Rect view = viewport_.worldBound;
                GL.Viewport (new Rect (view.x * ppp, Screen.height - view.yMax * ppp, view.width * ppp, view.height * ppp));
            }
            DoViewportToolGUI ();
        }
#endif

        /// <summary>
        /// 表示域の大きさ（左上が原点）。絵・ハンドル・カメラのアスペクトをここに合わせる
        /// </summary>
        Rect ViewportRect ()
        {
            if (viewport_ == null) return Rect.zero;

            Rect content = viewport_.contentRect;
            return float.IsNaN (content.width) || float.IsNaN (content.height) ? Rect.zero : new Rect (0, 0, content.width, content.height);
        }

        /// <summary>
        /// 表示域の横 / 縦。まだ大きさが決まっていなければ 1
        /// </summary>
        float ViewportAspect ()
        {
            Rect rect = ViewportRect ();
            return rect.height > 1 ? rect.width / rect.height : 1;
        }

        /// <summary>
        /// 窓全体（OnGUI の絵と、UI Toolkit 側のハンドル・タイムライン）を描き直す
        /// </summary>
        void RepaintView ()
        {
            Repaint ();
            if (viewport_ != null) viewport_.MarkDirtyRepaint ();
            if (viewRepainted != null) viewRepainted ();
        }

        /// <summary>表示域を描き直したとき（Scene ビュー型の窓も描き直す。S15d）</summary>
        public event System.Action viewRepainted;

        /// <summary>
        /// 別の窓（Scene ビュー型の窓。S15d）の中で骨のハンドルを描いて扱う。視点はその窓のカメラ（Camera.current）
        /// </summary>
        internal void DoExternalHandles ()
        {
            EnsureStage ();
            if (stage_ == null) return;
            DoHandles ();
        }

        /// <summary>
        /// 世界がまだ無ければ作る（作り直しの中で姿勢も当てる）。作ったら true。
        /// 普段は窓の描画（OnGUI）で作るが、Motion Scene が持つ見えない窓や、タブの後ろに隠れた窓には描画が来ないため
        /// </summary>
        bool EnsureStage ()
        {
            if (stage_ != null || EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (prefab_ == null && sceneTarget_ == null) return false;
            RebuildStage ();
            return true;
        }

        /// <summary>
        /// 別の窓で、どのハンドルにも当たらなかったクリック。メッシュを指していればその骨を選び、何も無ければ選択を外す
        /// </summary>
        internal void SelectByRay (Ray ray)
        {
            if (stage_ == null) return;
            PoseTarget target = FindPickTarget (stage_.PickBone (ray));
            Select (target != null ? target.gameObject : null);
            RepaintView ();
        }

        /// <summary>
        /// 編集するキャラ（prefab）を替える。キャラは複製してプレビューの世界に置く
        /// </summary>
        public void SetModel (GameObject prefab)
        {
            CommitHandleKeys ();
            if (prefab == prefab_ && sceneTarget_ == null && string.IsNullOrEmpty (sceneTargetId_)) return;
            prefab_ = prefab;
            // prefab を選び直したら、開いているシーンのキャラの編集はやめる（写した姿勢は戻る。見失っているキャラも追わない）
            sceneMirror_.Unbind ();
            sceneTarget_ = null;
            sceneTargetId_ = null;
            sceneTargetName_ = null;
            sceneTargetLost_ = false;
            selectedNames_.Clear ();
            ResetWindowSettingsFromProject ();
            // Override の並びはキャラの設定にあるので、キャラが替わったら読み直す（S21）
            overridesBase_ = null;
            RefreshClipState ();
            RebuildStage ();
        }

        public void SetClip (AnimationClip clip)
        {
            CommitHandleKeys ();
            editingClip_ = clip;
            RefreshClipState ();
            keyFrames_ = null;
            clock_.Stop ();
            // 段はクリップも見て作るので作り直す
            BuildStack ();
            // Humanoid のクリップ（読み取り専用の土台）を開いたら、書き込み先を重ねている Override にする。
            // 取込の試し見では書かないので変えない（書き込み先はキャラの設定に残るため、戻したときにずれる）
            if (!importPreviewing_) WriteToTopOverrideOnHumanoidBase ();
            SamplePose ();
            if (stateChanged != null) stateChanged ();
        }

        /// <summary>
        /// 段を今のキャラとクリップから作り直し、保存しておいた選択（書き出す段・つかむ段・隠している段）を戻す
        /// </summary>
        /// <summary>
        /// 今のフレームの姿勢を作り直して描き直す（段のパラメータなど、値を外から触ったとき）
        /// </summary>
        public void RefreshPose ()
        {
            ghostsDirty_ = true;
            SamplePose ();
        }

        void BuildStack ()
        {
            poseStack_ = PoseStack.Build (stage_, this);
            poseStack_.LoadState (layerState);
            ghostsDirty_ = true;
            RaiseStackChanged ();
        }

        void RaiseStackChanged ()
        {
            // 段の 👁・重み・パラメータが変わると残像の姿も変わる
            ghostsDirty_ = true;
            if (stackChanged != null) stackChanged ();
        }

        /// <summary>
        /// 今のフレームの時刻（秒）
        /// </summary>
        float CurrentTime ()
        {
            // 演出だけ置いているときも時計を進められる（クリップが無くても演出は見たい）
            return editingClip_ != null || genericClip_ != null || humanoidClip_ != null || contextPrefab_ != null ? (float)clock_.time : 0;
        }

        /// <summary>
        /// 骨や操作値を直に動かした後（ハンドル・Spinner・Reset）に、Editing Rig の段から下を解き直す（動かした値はクリップで上書きしない）。
        /// 段を通さずに解くと、Editing Rig の 👁 を落としても IK が効いてしまい、画面と実態がずれる
        /// </summary>
        void SolveAfterManipulate ()
        {
            poseStack_.EvaluateFrom (poseStack_.Find (LayerKind.EditingRig), CurrentTime ());
            // 値を映しているパネル（Transform の数値欄など）も、つかんで動かしたときに追従させる
            if (poseSampled != null) poseSampled ();
        }

        /// <summary>
        /// この段の結果を処理するかどうか（Layers のパネルの目）。落とした段は素通しになる
        /// </summary>
        public void SetLayerEnabled (PoseLayer layer, bool enabled)
        {
            if (layer == null || layer.enabled == enabled) return;
            layer.enabled = enabled;
            SaveLayerState ("Motion Editor Layer Enabled");
            SamplePose ();
            RaiseStackChanged ();
        }

        public void SetWriteLayer (PoseLayer layer)
        {
            string reason;
            if (!poseStack_.TrySetWrite (layer, out reason)) return;
            SaveLayerState ("Motion Editor Write Layer");
            RaiseStackChanged ();
        }

        public void SetManipulateLayer (PoseLayer layer)
        {
            string reason;
            if (!poseStack_.TrySetManipulate (layer, out reason)) return;
            SaveLayerState ("Motion Editor Manipulate Layer");
            RaiseStackChanged ();
            // 表示の骨をつかむ・やめると、選んでいる骨に効く拘束を切る・戻す
            SamplePose ();
        }

        /// <summary>
        /// 重みとパラメータは、触っている間に行を作り直すと入力が切れるので stateChanged を出さない
        /// </summary>
        public void SetLayerWeight (PoseLayer layer, float weight)
        {
            if (layer == null || !layer.hasWeight) return;
            layer.weight = Mathf.Clamp01 (weight);
            // ドラッグの刻みごとに Undo を積むと重いので、重みは記録せずに残すだけ
            poseStack_.SaveState (layerState);
            SaveCharacterSettings ();
            ghostsDirty_ = true;
            SamplePose ();
        }

        /// <summary>
        /// ゲーム側が [LayerParameter] で出した値を変える
        /// </summary>
        public void SetLayerParameter (PoseParameter parameter, object value)
        {
            if (parameter == null || parameter.setValue == null) return;
            parameter.setValue (value);
            ghostsDirty_ = true;
            SamplePose ();
        }

        /// <summary>
        /// 段の選択はキャラの設定（無ければ窓）が持つので、それを記録すれば Undo で戻る
        /// </summary>
        void SaveLayerState (string name)
        {
            Undo.RecordObject (layerStateOwner, name);
            poseStack_.SaveState (layerState);
            SaveCharacterSettings ();
        }

        public void SetFrame (int frame)
        {
            CommitHandleKeys ();
            clock_.SetFrame (frame);
            SamplePose ();
        }

        /// <summary>
        /// プレビューの世界を作り直す（窓を開いたとき・スクリプトの再読み込み後・キャラを替えたとき）
        /// </summary>
        public void RebuildStage ()
        {
            DisposeStage ();
            // 開いているシーンのキャラを編集するときは、そのキャラから複製して同じ場所に置く
            if (sceneTarget_ != null) {
                stageRigDefinition_ = rigDefinition;
                stage_ = new PreviewStage (sceneTarget_, stageRigDefinition_, sceneTarget_.transform.parent);
            } else {
                stageRigDefinition_ = rigDefinition;
                stage_ = new PreviewStage (prefab_, stageRigDefinition_);
            }
            stage_.showFloor = showFloor_;
            stage_.grabbedBones = GrabbedDisplayBones;
            AddContext ();

            targets_ = RigTargets.Create (stage_);
            foreach (PoseTarget target in targets_) {
                if (target.gameObject != null) targetByObject_[target.gameObject] = target;
            }
            BuildStack ();
            BuildPicker ();
            RestoreSelection ();

            if (stage_.animator != null && framedPrefab_ != prefab_) {
                FrameModel ();
                framedPrefab_ = prefab_;
            }
            keyFrames_ = null;
            BindSceneMirror ();
            SamplePose ();
            if (stateChanged != null) stateChanged ();
        }

        void DisposeStage ()
        {
            CommitHandleKeys ();
            // シーンのキャラへ写した姿勢を戻す（作り直し・再読み込み・Play Mode の前）。作り直した後は BindSceneMirror で写し直す
            sceneMirror_.Release ();
            foreach (PoseTarget target in targets_) {
                target.Dispose ();
            }
            targets_ = new List<PoseTarget> ();
            targetByObject_.Clear ();
            pickerEntries_ = new List<PickerEntry> ();
            pickTransforms_.Clear ();
            // 段は壊した世界を掴んでいるので捨てる（選択は layerState_ に残る）
            poseStack_ = new PoseStack ();
            selection_.Clear ();
            trackKeys_.Clear ();
            if (stage_ != null) {
                stage_.Dispose ();
                stage_ = null;
            }
        }

        void SaveSelection ()
        {
            selectedNames_ = selection_.Where (go => go != null).Select (go => go.name).ToList ();
        }

        void RestoreSelection ()
        {
            selection_.Clear ();
            selection_.AddRange (targets_.Where (t => t.gameObject != null && selectedNames_.Contains (t.name)).Select (t => t.gameObject));
        }

        bool CanEditClip ()
        {
            if (poseStack_ == null || targetClip == null) return false;
            // 取込の候補を試し見している間は、どこへも書かない（見ているのは他人のクリップ）
            if (importPreviewing_) return false;
            // 編集用クリップへ書くときだけ、そのクリップが編集できる形かを見る（Override へ書くときの土台は Humanoid でもよい）
            if (overrideWriteLayer == null && (editingClip_ == null || clipProblem_ != null)) return false;
            // 合成の重みが 1 未満のとき・書き出す段の 👁 を落としているときは、画面の値がクリップから出ていないので書かない。
            // 段が見つからないときも書かない（判定を飛ばして書けてしまわないように）
            // （✋ からの経路は見ない。Rig Values のパネルのように、✋ を通さずに値を動かす書き込みもあるため）
            PoseLayer write = poseStack_.writeLayer;
            if (write == null || !write.canWrite || !write.enabled || write.clipWeight < 1) return false;
            // 演出の中で前後のクリップと混ざる区間は、v1 では編集しない（S15b。理由は Timeline Clip の段に出る）
            return timelineEditBlockReason == null;
        }

        /// <summary>
        /// 段のクリップの合成の重み（Layers のパネル）。重みと同じく Undo は積まずに残す
        /// </summary>
        public void SetClipWeight (PoseLayer layer, float weight)
        {
            if (layer == null || !layer.hasClipWeight) return;
            layer.clipWeight = Mathf.Clamp01 (weight);
            poseStack_.SaveState (layerState);
            SaveCharacterSettings ();
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 新しいクリップ（60fps）を作って開く。置き場所の既定は、今のクリップかキャラの prefab と同じフォルダ
        /// </summary>
        public void CreateClip ()
        {
            string source = editingClip_ != null ? AssetDatabase.GetAssetPath (editingClip_) : AssetDatabase.GetAssetPath (prefab_);
            string folder = string.IsNullOrEmpty (source) ? "Assets" : Path.GetDirectoryName (source).Replace ('\\', '/');
            // 編集用クリップは名前に .rig を付けて、ゲーム用のクリップと見分ける（Attack.rig.anim）
            string path = EditorUtility.SaveFilePanelInProject ("New Motion Clip", EditingClip.DefaultFileName ("New Motion"), "anim", "", folder);
            if (string.IsNullOrEmpty (path)) return;

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, path);
            SetClip (clip);
        }

        /// <summary>
        /// 今のフレームの姿勢にする。基準の姿勢へ戻してからクリップを当て、IK を解く
        /// </summary>
        void SamplePose ()
        {
            SamplePose (CurrentTime ());
        }

        void SamplePose (float time)
        {
            CommitHandleKeys ();
            // 窓が描かれていない（Motion Scene が持っている見えない窓・タブの後ろ）ときも、要るときに世界を作る（S15d）
            if (EnsureStage ()) return;
            if (stage_ == null || stage_.animator == null) return;
            // 演出のどのクリップとして見ているかと、時計の写像（演出の時刻）を先に決める（S15b）
            ResolveTimelineClip ();
            UpdateClockMapping ();
            // 段の並び（Layers のパネルに出ているもの）をそのまま上から通す。
            // クリップを当てるのも IK を解くのも段の側にある（AnimationClip.SampleAnimation は使わない。PreviewStage.SampleClip）
            poseStack_.Evaluate (time);
            EvaluateContexts (time);
            RepaintView ();
            if (poseSampled != null) poseSampled ();
        }

        void OnUndoRedo ()
        {
            keyFrames_ = null;
            RefreshClipState ();
            if (stage_ != null) stage_.InvalidateClipCurves ();
            // 段の選択もキャラの設定（無ければ窓）に入っているので、戻った値をスタックへ入れ直す
            poseStack_.LoadState (layerState);
            SamplePose ();
            RaiseStackChanged ();
            RaiseStateChanged ();
        }

        void OnCurveWasModified (AnimationClip clip, EditorCurveBinding binding, AnimationUtility.CurveModifiedType type)
        {
            if (clip == editingClip_) strayCurveCount_ = -1;
            // ここで姿勢を当て直すと、キーを打っている途中（コントローラーを順に書いている間）の骨が戻ってしまう。一覧だけ作り直す
            if (clip == editingClip_ || IsOverrideClip (clip)) {
                keyFrames_ = null;
                ghostsDirty_ = true;
                if (stage_ != null) stage_.InvalidateClipCurves ();
                QueueKeysChanged ();
                RepaintView ();
            }
        }

        void QueueKeysChanged ()
        {
            if (keysChangedQueued_) return;
            keysChangedQueued_ = true;
            EditorApplication.delayCall += RaiseKeysChanged;
        }

        void RaiseKeysChanged ()
        {
            keysChangedQueued_ = false;
            // Stacker のループ: 先頭のポーズキーを直したら末尾も合わせる（同じ値なら何もしないので、ここから書いても繰り返さない）
            SyncStackerLoop ("Stacker Loop");
            if (keysChanged != null) keysChanged ();
        }

        void Tick ()
        {
            // 編集しているシーンのキャラを見失った・見つけ直した（S15d）
            KeepSceneTarget ();
            // 見えない窓でも、編集しているシーンのキャラがあれば世界を作って写す（S15d）
            if (sceneTarget_ != null) EnsureStage ();
            // 開いているシーンの演出を読み直す（読むだけ。S15d）
            RefreshSceneContext ();
            // Timeline 窓の再生位置に付いていく（S15c。Motion Scene ではいつも）
            FollowTimelineWindow ();
            // シーンのキャラへ写した登録が外れていたら写し直す（S15d）
            KeepSceneMirror ();
            // キャラの設定・プロジェクト設定でリグの定義を差し替えたら作り直す
            KeepRigDefinition ();
            if (!clock_.isPlaying) return;
            if (editingClip_ == null || stage_ == null) {
                clock_.Stop ();
                return;
            }

            // 止めている間（つかんでいる間）は進めず、姿勢も当て直さない。
            // 演出のクリップと結び付けていれば、演出の速さ（クリップの速さ）で演出の頭から終わりまでを回る
            UpdateClockMapping ();
            double start, end;
            PlayRange (out start, out end);
            if (clock_.Tick (EditorApplication.timeSinceStartup, start, end)) SamplePose ();
        }

        public void TogglePlay ()
        {
            handleHold_ = false;
            spinHold_ = false;
            if (clock_.isPlaying) {
                clock_.Stop ();
                SamplePose ();
            }
            else if (editingClip_ != null) {
                clock_.Play (EditorApplication.timeSinceStartup);
            }
            RepaintView ();
        }

        /// <summary>
        /// キャラの絵を表示域に描く。UI Toolkit の要素（ハンドル・タイムライン・オーバーレイ）はこの上に重なる。
        /// 上端のツールバー帯の分は、OnGUI の原点も rootVisualElement と一緒にずれている
        /// </summary>
        /// <summary>この窓のパネル（画面の外へ出たら押し戻す）</summary>
        static readonly string[] kPanelIds = {
            "mkt-preview-toolbar", "mkt-preview-view", "mkt-layers", "mkt-transform", "mkt-picker", "mkt-timeline",
            "mkt-animbank", "mkt-curves", "mkt-stacker", "mkt-properties", "mkt-rig-values",
        };

        void OnGUI ()
        {
            if (stage_ == null) RebuildStage ();
            if (Event.current.type == EventType.Layout) OverlayWindows.KeepInside (this, kPanelIds);
            if (Event.current.type != EventType.Repaint || viewport_ == null) return;

            Rect rect = viewport_.ChangeCoordinatesTo (rootVisualElement, viewport_.contentRect);
            if (rect.width < 1 || rect.height < 1) return;

            // 視点の出どころは ApplyView の 1 か所（いつもの視点か、演出のカメラ割り）
            Quaternion viewRotation = ApplyView (stage_.camera);
            // BeginPreview は描き先の大きさを合わせるだけで aspect は直さない。合わせないと窓の形を変えたときに絵がつぶれる
            stage_.camera.aspect = rect.width / rect.height;
            stage_.UpdateLights (viewRotation);
            stage_.renderUtility.BeginPreview (rect, GUIStyle.none);
            // Stacker の残像（前後のフレーム）。Render の前に積む
            UpdateGhosts ();
            DrawGhosts ();
            stage_.renderUtility.Render (true, false);
            stage_.renderUtility.EndAndDrawPreview (rect);
            DrawViewportInfo (rect);
        }

        /// <summary>
        /// 表示域の操作。ツールコンテキスト（PoseToolContext）から、Handles のカメラを設定した状態で呼ばれる
        /// </summary>
        public void DoViewportToolGUI ()
        {
            if (stage_ == null || viewport_ == null) return;

            Event ev = Event.current;
            Rect rect = ViewportRect ();
            int viewId = GUIUtility.GetControlID (kViewportHash, FocusType.Passive, rect);
            bool mouseInView = rect.Contains (ev.mousePosition);
            // 離したのを受け取れないまま視点のドラッグが終わっていたら（窓が後ろへ回ったなど）、カーソルの回り込みを戻す
            if (cameraDragging_ && GUIUtility.hotControl != viewId) EndCameraDrag ();

            HandleDragAndDrop (rect);
            // ハンドルの右クリックは、動かさずに離したときに渡す（押した時点では視点の回転を始める）
            PoseHandleUtility.contextClickOnRelease = true;
            try {
                // 視点の操作（Alt+ドラッグ・中ボタン）は骨ハンドルより先に取る
                HandleCameraInputBeforeHandles (viewId, mouseInView);
                DoHandles ();
                HandleCameraInputAfterHandles (rect, viewId, mouseInView);
            }
            finally {
                PoseHandleUtility.contextClickOnRelease = false;
                PoseHandleUtility.contextClickReleased = false;
            }
        }

        void DoHandles ()
        {
            // 窓が複数開いていると入れ替わるので、描く直前に自分を入れる
            EditorHost.current = this;
            // IK の目標のギズモは Transform パネルのモードに従う（Rot なら回転、それ以外は移動）
            PoseHandleUtility.goalRotate = spinnerMode_ == SpinnerMode.Rotate;
            PoseHandleUtility.gizmoWorld = gizmoWorld_;
            bool displayGrab = isDisplayGrab;
            Event ev = Event.current;
            // 表示の骨をつかむときは、書いた結果が狙いから遠ざかったら戻せるように操作値を控える
            // キーの自動追加が切のときも、キーの無い物を動かしたら戻せるように控える
            bool captureBefore = displayGrab || (!autoKey && CanEditClip ());
            PreviewStage.ControlState before = captureBefore && ev.type != EventType.Repaint && ev.type != EventType.Layout ? stage_.CaptureControlState () : null;
            DisplayGoal goal = default;
            bool hasGoal = false;
            // 全身 IK の点をつかむ（✋ = Full Body IK。S25）間は、点のハンドルを出して骨（FK）のハンドルは出さない（関節の上で重なる）。
            // それ以外のときは、固定している点の印だけ描く（操作は受けない）
            bool pointGrab = isPointGrab;
            // 枠を引いている間は、枠に入っている点を「選ぼうとしている」印で出す（離すと選ぶ）
            Rect? box = draggingBox;
            Rect viewRect = ViewportRect ();
            // 点のギズモで回した（FK で骨だけ回したときは、キーを打つ値が点に残らないので別に覚える）
            bool pointTurned = false;
            // キーの自動追加が切なら、今のフレームにキーの無い物のハンドルは薄く描いてつかめなくする（動かしてもキーを打てない）。
            // 入なら触ればキーを打つので、そのまま出す
            bool pointsLocked = !CanEditPoints ();
            EditorGUI.BeginChangeCheck ();
            try {
                foreach (PoseTarget target in targets_) {
                    bool selected = IsSelected (target.gameObject);
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null) {
                        // 点は View の IK で出し入れする。切っている間も選んでいる点は出す（骨を隠しているときと同じく、そのまま動かせるように）
                        if (pointGrab && !displayGrab && (showIk_ || selected)) {
                            PoseHandles.locked = pointsLocked;
                            // いくつか選んでいる間は、中心の点にだけギズモを出す（選んだ点の数だけ重なって出る）
                            point.showGizmo = selection_.Count <= 1 || point.gameObject == pivotPoint_;
                            point.inBox = box.HasValue && IsInBox (stage_.camera, viewRect, box.Value, point.framePosition);
                            point.OnHandleGUI (selected);
                            if (point.ConsumeContextClick ()) contextPoint_ = point;
                            // いくつか選んでいる点の 1 つをつかんで動かした: ほかの選んでいる点も同じだけ動かす
                            Vector3 dragged = point.ConsumeDragDelta ();
                            if (dragged != Vector3.zero && selected && selection_.Count > 1) {
                                foreach (BodyPointTarget other in GetSelectedPoints ()) {
                                    if (other != point) other.MoveBy (dragged);
                                }
                            }
                            // ギズモで回した: いくつか選んでいれば選んだ点だけをこの点のまわりで、1 つなら骨を FK で回して子の点ごと
                            Quaternion turned = point.ConsumeRotateDelta ();
                            if (turned != Quaternion.identity) {
                                if (selected && selection_.Count > 1) RotateSelectedPoints (point, turned);
                                else RotatePointFk (point, turned);
                                pointTurned = true;
                            }
                        }
                        else if (showIk_) point.DrawPinned ();
                        continue;
                    }
                    if (pointGrab && target is FkTarget) continue;
                    // 骨を隠している間は、選んでいる物だけ出す（選択したまま見た目を確かめ、そのまま動かせるように）。
                    // 手足の IK は View の IK が入っていれば隠さない
                    if (!showBones_ && !selected && !(showIk_ && target is IkTarget)) continue;
                    PoseHandles.locked = !CanEditTarget (target);
                    if (!displayGrab) {
                        target.OnHandleGUI (selected);
                        continue;
                    }
                    DisplayGoal targetGoal;
                    if (target.supportsDisplayHandles) {
                        if (target.OnDisplayHandleGUI (selected, out targetGoal)) {
                            goal = targetGoal;
                            hasGoal = true;
                        }
                    }
                    // 値をつかむ物（ゲームの Rig のターゲット）は保存データの値なので、段を逆に通さずそのまま動かせる
                    else if (target is RigSourceTarget) target.OnHandleGUI (selected);
                }
            }
            finally {
                PoseHandles.locked = false;
            }
            bool changed = EditorGUI.EndChangeCheck ();
            if (displayGrab && ev.type == EventType.Repaint) DrawAimGhost ();

            // つかんでいるハンドルが変わったら知らせる（IK が IK と FK を切り替える）
            if (focusControl_ != GUIUtility.hotControl) {
                PoseTarget focused = selectedTarget;
                foreach (PoseTarget target in targets_) {
                    target.OnFocusChange (focused);
                }
                focusControl_ = GUIUtility.hotControl;
            }

            // 再生中につかんだら止め、離したら続ける
            if (changed && clock_.isPlaying) {
                handleHold_ = true;
                HoldClock ();
            }
            if (handleHold_ && GUIUtility.hotControl == 0) {
                handleHold_ = false;
                ReleaseClock ();
            }

            // 点を右ボタンで押した: 固定を付け外しする（選んでいる点を押したなら、選んでいる点をまとめて）
            if (contextPoint_ != null) {
                BodyPointTarget clicked = contextPoint_;
                contextPoint_ = null;
                SetPointsLocked (clicked, !clicked.locked);
                return;
            }

            if (changed) {
                // 全身 IK の点を動かした（S25）: 解き直して、離したときに姿勢のキーを打つ
                if (pointTurned || HasPendingPointKeys ()) {
                    // キーの自動追加が切で、今のフレームに姿勢のキーが無い: 動かさない（つかむ前の操作値へ戻し、動かした印も捨てる）
                    if (!CanEditPoints ()) {
                        foreach (PoseTarget target in targets_) {
                            BodyPointTarget point = target as BodyPointTarget;
                            if (point != null) point.ClearPendingKeys ();
                        }
                        if (before != null) {
                            stage_.RestoreControlState (before);
                            SolveAfterManipulate ();
                        }
                        else {
                            SamplePose ();
                        }
                        NotifyNoKey ();
                        RepaintView ();
                        return;
                    }
                    SolveAfterManipulate ();
                    pointKeysPending_ = true;
                    RepaintView ();
                    if (GUIUtility.hotControl == 0) CommitHandleKeys ();
                    return;
                }
                // キーの自動追加が切: キーの無い物は動かさない（つかむ前の操作値へ戻す）。
                // クリップを当て直すだけでは戻らない: カーブの無い値（Reset で打たない腰の位置など）は当て直しで今のまま残る
                if (!CanEditSelectedTargets ()) {
                    if (before != null) {
                        stage_.RestoreControlState (before);
                        SolveAfterManipulate ();
                    }
                    else {
                        SamplePose ();
                    }
                    NotifyNoKey ();
                    RepaintView ();
                    return;
                }
                if (hasGoal) {
                    float beforeAngle, beforeDistance;
                    MeasureGoal (goal, out beforeAngle, out beforeDistance);
                    SolveAfterManipulate ();
                    float angle, distance;
                    MeasureGoal (goal, out angle, out distance);
                    // 表示が狙いへほとんど動かなかったなら書かずに戻す。見えない値だけがクリップに溜まるため。
                    // 例: Humanoid を通ると骨の軸まわりのねじりは配り直されて表示に出ない。後段の左右反転中は、表示の骨を動かすのが反対側の編集用の骨
                    if (!Reached (beforeAngle, angle) || !Reached (beforeDistance * 100, distance * 100)) {
                        stage_.RestoreControlState (before);
                        SolveAfterManipulate ();
                        displayGrabStatus_ = "戻した: " + goal.bone.name + " の表示が狙いへ動かない（Humanoid を通ると骨の軸まわりのねじりは表示に出ない・後段の左右反転中など）";
                        displayGrabWarning_ = true;
                        SetDisplayResidual (goal, beforeAngle, beforeDistance);
                        RepaintView ();
                        return;
                    }
                    displayGrabWarning_ = angle > kDisplayResidualAngle || distance > kDisplayResidualDistance;
                    displayGrabStatus_ = goal.bone.name + " の残差 " + angle.ToString ("F2") + "° / " + (distance * 1000).ToString ("F1") + "mm"
                        + (displayGrabWarning_ ? "（近似の段を通ったので狙いに届いていない。黄色の線が狙った姿）" : "");
                    SetDisplayResidual (goal, angle, distance);
                }
                else {
                    SolveAfterManipulate ();
                }
                handleKeysPending_ = true;
                RepaintView ();
            }
            if (GUIUtility.hotControl == 0) CommitHandleKeys ();
        }

        /// <summary>
        /// ハンドルで動かした姿勢を今のフレームにキーとして打つ（離したとき）。
        /// 姿勢をクリップから当て直す前・フレームやクリップを替える前にも呼ぶ（打つ前の姿勢が消えたり、別のフレームに打たれたりしないように）
        /// </summary>
        void CommitHandleKeys ()
        {
            bool points = pointKeysPending_;
            bool handles = handleKeysPending_;
            pointKeysPending_ = false;
            handleKeysPending_ = false;
            if (stage_ == null || (!points && !handles)) return;
            // 全身 IK の点を動かした・固定している点があるフレームで骨を動かした: 解いた姿勢を骨のキーとして打つ
            if (points || hasActivePoints) {
                CommitFullBodyPose ();
                return;
            }
            AddKeyAllCurves (true);
            RepaintView ();
        }

        BodyPointTarget contextPoint_;

        /// <summary>今のフレームで、動かした点・固定した点があるか（全身 IK の定義のとき）</summary>
        bool hasActivePoints
        {
            get { return stage_ != null && stage_.fullBody != null && stage_.fullBody.hasActivePoint; }
        }

        /// <summary>選んでいる全身 IK の点（✋ が全身 IK の段のとき）</summary>
        List<BodyPointTarget> GetSelectedPoints ()
        {
            List<BodyPointTarget> points = new List<BodyPointTarget> ();
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null && IsSelected (point.gameObject)) points.Add (point);
            }
            return points;
        }

        /// <summary>
        /// 点を 1 つ選んで回した: その点を中心に、点が乗る骨を回す（S26。点の種類で変えない）。
        /// 骨は FK で回すので、子の骨と、骨に付いて動く子の点は一緒に回る。固定している子の点（同じ骨の上の点を含む）は、置いた場所と向きを同じだけ回す
        /// （回さないと、解いたときに子が元の場所へ引き戻される）。
        /// 付け根から離れた点は、FK だけでは中心が骨の付け根になるので、選んだ点をその場に留め（Pin）、骨の付け根は全身 IK で点に合わせて動かす。
        /// 骨を FK で回せないとき（FK の対象が無い）は、向きを持つ点の向きだけ回す
        /// </summary>
        void RotatePointFk (BodyPointTarget point, Quaternion delta)
        {
            Transform bone = point.editingBone;
            FkTarget fk = null;
            foreach (PoseTarget target in targets_) {
                FkTarget candidate = target as FkTarget;
                if (candidate != null && candidate.bone == bone) {
                    fk = candidate;
                    break;
                }
            }
            if (bone == null || fk == null) {
                point.RotateAround (point.framePosition, delta, true);
                return;
            }
            // 中心は選んだ点の目標（固定していなければ骨の上の点）。子の点は、今の骨の上の点を中心に重ねて回す（届いていない分の食い違いを持ち越さない）
            Vector3 pivot = point.isOnBoneOrigin ? bone.position : point.framePosition;
            Vector3 pivotOnBone = point.isOnBoneOrigin ? bone.position : point.bonePosition;
            foreach (PoseTarget target in targets_) {
                BodyPointTarget child = target as BodyPointTarget;
                if (child == null || child == point || child.editingBone == null || !child.pinned) continue;
                if (child.editingBone == bone || child.editingBone.IsChildOf (bone)) child.RotateBodyAround (pivot, pivotOnBone, delta, false);
            }
            fk.SpinRotateWorld (delta, false);
            if (point.isOnBoneOrigin) point.RotateAround (pivot, delta, false);
            else point.RotateAround (pivot, delta, true);
        }

        /// <summary>
        /// いくつか選んでいるときに中心の点で回した: 選んでいる点だけを、中心の点のまわりで回す（Free の点も、回した場所に置いて Pin にする）。
        /// 選んでいない点は動かさない（解いた体に付いて動く）
        /// </summary>
        void RotateSelectedPoints (BodyPointTarget pivotPoint, Quaternion delta)
        {
            // 中心は中心の点の目標（Locked ならその場所）。ほかの点は骨の上の点から回す（目標から回すと、届いていない分の食い違いが残る）
            Vector3 pivot = pivotPoint.framePosition;
            Vector3 pivotOnBone = pivotPoint.bonePosition;
            foreach (BodyPointTarget point in GetSelectedPoints ()) {
                if (point == pivotPoint) point.RotateAround (pivot, delta, true);
                else point.RotateBodyAround (pivot, pivotOnBone, delta, true);
            }
        }

        /// <summary>
        /// 点の固定を付け外しして、今のフレームにキーを打つ。clicked が選んでいる点の 1 つなら、選んでいる点をまとめて変える。
        /// clicked が null なら、選んでいる点すべて
        /// </summary>
        void SetPointsLocked (BodyPointTarget clicked, bool on)
        {
            if (stage_ == null || stage_.fullBody == null) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (clicked != null && !points.Contains (clicked)) {
                points.Clear ();
                points.Add (clicked);
                Select (clicked.gameObject);
            }
            if (points.Count == 0) return;
            foreach (BodyPointTarget point in points) point.SetLocked (on);
            SolveAfterManipulate ();
            CommitFullBodyPose ();
        }

        /// <summary>選んでいる全身 IK の点の数（Transform のパネルが、状態のボタンを出すかどうかに使う）</summary>
        public int selectedPointCount
        {
            get { return stage_ != null && stage_.fullBody != null ? GetSelectedPoints ().Count : 0; }
        }

        /// <summary>選んでいる点のうち、その状態の点の数</summary>
        public int CountSelectedPoints (BodyPointState state)
        {
            if (stage_ == null || stage_.fullBody == null) return 0;
            int count = 0;
            foreach (BodyPointTarget point in GetSelectedPoints ()) {
                if (point.state == state) count++;
            }
            return count;
        }

        /// <summary>
        /// 選んでいる点をまとめてその状態にして、今のフレームにキーを打つ（Transform のパネルの Free / Pin / Lock）
        /// </summary>
        public void SetSelectedPointsState (BodyPointState state)
        {
            if (stage_ == null || stage_.fullBody == null) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (points.Count == 0) return;
            foreach (BodyPointTarget point in points) point.SetState (state);
            SolveAfterManipulate ();
            CommitFullBodyPose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 選んでいる点の固定を付け外しする（ショートカット）。1 つでも固定していない点があれば全部固定し、全部固定していれば外す
        /// </summary>
        public void ToggleSelectedPointsLocked ()
        {
            if (!isPointGrab) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (points.Count == 0) return;
            SetPointsLocked (null, points.Exists (p => !p.locked));
        }

        // R は Unity と同じく Scale のモードに使うので L（Lock）
        [Shortcut ("Lilium Motion Editor/Toggle Point Lock", typeof (PreviewWindow), KeyCode.L)]
        static void TogglePointLockShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.ToggleSelectedPointsLocked ();
        }

        /// <summary>
        /// ✋ が全身 IK の段のとき（S25）。関節の点をつかんで固定する
        /// </summary>
        public bool isPointGrab
        {
            get {
                PoseLayer manipulate = poseStack_ != null ? poseStack_.manipulateLayer : null;
                return manipulate != null && manipulate.kind == LayerKind.FullBodyIk && stage_ != null && stage_.fullBody != null;
            }
        }

        bool HasPendingPointKeys ()
        {
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null && point.hasPendingKeys) return true;
            }
            return false;
        }

        /// <summary>
        /// 全身 IK で作った今の姿勢を、今のフレームのキーにする（キーフレームは全身の姿勢を持つ）。
        /// 点を離したとき・点の固定を付け外ししたとき・固定している点があるフレームで骨を動かしたときに呼ぶ。
        /// 1. 動かした点（固定していないもの）を、実際の骨の位置へ合わせ直す
        /// 2. 解いた姿勢を骨の値にして、全部の骨のキーを打つ（キーの自動追加が切でも全部の骨に打つ。切のときにハンドル・Spinner でここへ来るのは、
        ///    今のフレームに姿勢のキーがあるときだけ。キーの間は骨の値の補間で動く）
        /// 3. 動かした点・固定した点のキーを打つ（このキーフレームへ戻ったときに、同じ点から直せる。ほかのフレームでは効かない）
        /// クリップに書けないとき（クリップが無い・合成中など）は打たずに、姿勢をクリップから当て直す
        /// </summary>
        void CommitFullBodyPose ()
        {
            if (stage_ == null || stage_.fullBody == null) return;
            if (!CanEditClip ()) {
                foreach (PoseTarget target in targets_) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null) point.ClearPendingKeys ();
                }
                SamplePose ();
                return;
            }
            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) point.SyncToBone ();
            }
            // 解いた姿勢を骨の値（FK の差分・腰の位置）にする。ここからは、この姿勢が元の姿勢
            if (stage_.editingRigSolver != null) stage_.editingRigSolver.Capture ();
            stage_.fullBody.CaptureInput ();
            using (CurveWriter writer = BeginCurves ("Key Pose")) {
                foreach (PoseTarget target in targets_) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null && point.hasPendingKeys) point.WritePendingKeys (writer);
                }
                foreach (PoseTarget target in targets_) target.WriteKeys (writer);
                if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
            }
            Undo.SetCurrentGroupName ("Key Pose");
            Undo.CollapseUndoOperations (group);
            keyFrames_ = null;
            // 打ったキーから姿勢を作り直す（保存した値で見えている姿勢と同じになる）
            SamplePose ();
        }

        const float kDisplayResidualAngle = 1;
        const float kDisplayResidualDistance = 0.01f;
        static readonly Color kAimGhostColor = new Color (1f, 0.85f, 0.2f, 0.9f);
        string displayGrabStatus_;
        bool displayGrabWarning_;
        // 最後に表示の骨をつかんだときの狙いと残差（狙いの残像と、Layers の矢印の色に使う）
        DisplayGoal aimGoal_;
        int aimFrame_ = -1;
        float residualAngle_;
        float residualDistance_;
        bool hasResidual_;

        /// <summary>表示の骨をつかんで残差が出た（Layers が矢印の色を直す）</summary>
        public event System.Action displayResidualChanged;

        void SetDisplayResidual (DisplayGoal goal, float angle, float distance)
        {
            aimGoal_ = goal;
            aimFrame_ = currentFrame;
            bool changed = !hasResidual_ || Mathf.Abs (angle - residualAngle_) > 0.005f || Mathf.Abs (distance - residualDistance_) > 0.00005f;
            residualAngle_ = angle;
            residualDistance_ = distance;
            hasResidual_ = true;
            if (changed && displayResidualChanged != null) displayResidualChanged ();
        }

        /// <summary>
        /// 最後の残差（表示の骨をつかんでいて、まだ測っていなければ false）
        /// </summary>
        public bool TryGetDisplayResidual (out float angle, out float distance, out bool warning)
        {
            angle = residualAngle_;
            distance = residualDistance_;
            warning = displayGrabWarning_;
            return hasResidual_ && isDisplayGrab;
        }

        /// <summary>
        /// 狙いに届かなかったとき、狙った骨の姿を黄色の線で出す（今の骨の先から狙いの先へ点線）。同じフレームにいる間だけ
        /// </summary>
        void DrawAimGhost ()
        {
            if (!displayGrabWarning_ || !hasResidual_ || aimGoal_.bone == null || aimFrame_ != currentFrame) return;
            Transform bone = aimGoal_.bone;
            Vector3 aimPosition = aimGoal_.hasPosition ? aimGoal_.position : bone.position;
            Quaternion aimRotation = aimGoal_.hasRotation ? aimGoal_.rotation : bone.rotation;
            float size = HandleUtility.GetHandleSize (aimPosition) * 0.06f;
            Handles.color = kAimGhostColor;
            Handles.SphereHandleCap (0, aimPosition, Quaternion.identity, size, EventType.Repaint);
            if (aimGoal_.length <= 0) {
                Handles.DrawDottedLine (bone.position, aimPosition, 3);
                return;
            }
            Vector3 aimTip = aimPosition + aimRotation * aimGoal_.shapeAdjust * Vector3.forward * aimGoal_.length;
            Vector3 actualTip = bone.position + bone.rotation * aimGoal_.shapeAdjust * Vector3.forward * aimGoal_.length;
            Handles.DrawAAPolyLine (3, aimPosition, aimTip);
            Handles.SphereHandleCap (0, aimTip, Quaternion.identity, size, EventType.Repaint);
            Handles.DrawDottedLine (actualTip, aimTip, 3);
            if (aimGoal_.hasPosition) Handles.DrawDottedLine (bone.position, aimPosition, 3);
        }

        /// <summary>
        /// ✋ が Display Pose で、書き出す段まで戻せるとき。ハンドルは表示モデルの骨の上に出る
        /// </summary>
        public bool isDisplayGrab
        {
            get {
                PoseLayer manipulate = poseStack_ != null ? poseStack_.manipulateLayer : null;
                if (manipulate == null || manipulate.kind != LayerKind.DisplayBone) return false;
                string reason;
                return poseStack_.PathKind (out reason) != InverseKind.None;
            }
        }

        /// <summary>
        /// 表示の骨をつかんだときの、最後の残差（狙いと、書いて前進し直した結果の差）。まだ無ければ null
        /// </summary>
        public string displayGrabStatus
        {
            get { return isDisplayGrab ? displayGrabStatus_ : null; }
        }

        /// <summary>残差が閾値（1°・1cm）を超えたか、戻せなかった</summary>
        public bool displayGrabWarning
        {
            get { return isDisplayGrab && displayGrabWarning_; }
        }

        /// <summary>
        /// 狙った量の 4 分の 1 以上は近づいたか（狙いがごく小さいときは問わない）
        /// </summary>
        static bool Reached (float before, float after)
        {
            if (before < 0.05f) return after <= before + 0.05f;
            return before - after >= before * 0.25f;
        }

        static void MeasureGoal (DisplayGoal goal, out float angle, out float distance)
        {
            angle = goal.hasRotation && goal.bone != null ? Quaternion.Angle (goal.bone.rotation, goal.rotation) : 0;
            distance = goal.hasPosition && goal.bone != null ? Vector3.Distance (goal.bone.position, goal.position) : 0;
        }

        void HandleCameraInputBeforeHandles (int viewId, bool mouseInView)
        {
            Event ev = Event.current;
            switch (ev.GetTypeForControl (viewId)) {
                case EventType.MouseDown:
                    if (mouseInView && (ev.alt || ev.button == 2)) {
                        BeginCameraDrag (viewId, ev);
                    }
                    break;
                case EventType.MouseUp:
                    // 右ボタンを動かさずに離した: つかみを放して、ハンドルの右クリック（固定の付け外し・IK / FK の切り替え）として渡す
                    if (GUIUtility.hotControl == viewId && IsCameraClick () && ev.button == 1) {
                        GUIUtility.hotControl = 0;
                        EndCameraDrag ();
                        PoseHandleUtility.contextClickReleased = true;
                    }
                    break;
            }
        }

        /// <summary>
        /// 右ボタンを押してから、まだ動かしていない（クリックになるかもしれない）。Alt + 右（ズーム）と中ボタンは含めない
        /// </summary>
        bool IsCameraClick ()
        {
            return cameraDragging_ && !boxSelecting_ && cameraDragButton_ == 1 && !cameraDragAlt_ && cameraDragTravel_ < kClickTravel;
        }

        void HandleCameraInputAfterHandles (Rect rect, int viewId, bool mouseInView)
        {
            Event ev = Event.current;
            switch (ev.GetTypeForControl (viewId)) {
                case EventType.MouseDown:
                    if (!mouseInView) break;
                    // 右ボタンは、ハンドルの上で押しても視点の回転を始める（ハンドルの右クリックは、動かさずに離したとき）
                    if (ev.button == 1) {
                        BeginCameraDrag (viewId, ev);
                    }
                    // 全身 IK の点をつかむ間は、何も無い所から左ドラッグで枠を引いて、中の点をまとめて選ぶ。
                    // 動かさずに離したら、今までどおりのクリック（メッシュの骨を選ぶ・選択を外す）
                    else if (ev.button == 0 && isPointGrab) {
                        boxSelecting_ = true;
                        boxMoved_ = false;
                        boxStart_ = boxEnd_ = ev.mousePosition;
                        GUIUtility.hotControl = viewId;
                        ev.Use ();
                    }
                    // ハンドルに当たらなかった左クリック。メッシュを指していればその骨を選び、何も無ければ選択を外す
                    else if (ev.button == 0) {
                        SelectByMeshClick (ev.mousePosition, rect);
                        PoseHandleUtility.focusControl = 0;
                        ev.Use ();
                        RepaintView ();
                    }
                    break;
                case EventType.Repaint:
                    if (draggingBox.HasValue) {
                        Rect box = draggingBox.Value;
                        // ここは Handles のカメラ（3D）の中。表示域の四隅を、カメラのすぐ前の点に直して描く。
                        // 表示域の位置 → ビューポート座標は、クリックでメッシュを選ぶときと同じ換算（左下が原点）。
                        // HandleUtility.GUIPointToWorldRay は、この窓では縦にずれる
                        Camera boxCamera = stage_.camera;
                        float depth = boxCamera.nearClipPlane * 2;
                        System.Func<float, float, Vector3> corner = (x, y) => boxCamera.ViewportToWorldPoint (new Vector3 (x / rect.width, 1 - y / rect.height, depth));
                        Vector3[] corners = { corner (box.xMin, box.yMin), corner (box.xMax, box.yMin), corner (box.xMax, box.yMax), corner (box.xMin, box.yMax) };
                        UnityEngine.Rendering.CompareFunction zTest = Handles.zTest;
                        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
                        Handles.DrawSolidRectangleWithOutline (corners, new Color (0.3f, 0.6f, 1f, 0.15f), new Color (0.4f, 0.7f, 1f, 0.9f));
                        Handles.zTest = zTest;
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != viewId) break;
                    if (boxSelecting_) {
                        boxEnd_ = ev.mousePosition;
                        if ((boxEnd_ - boxStart_).sqrMagnitude > 16) boxMoved_ = true;
                        ev.Use ();
                        RepaintView ();
                        break;
                    }
                    // 押してすぐのわずかな動きでは回さない（ハンドルの右クリックで視点がずれないように）
                    bool click = IsCameraClick ();
                    cameraDragTravel_ += ev.delta.magnitude;
                    if (click && IsCameraClick ()) {
                        ev.Use ();
                        break;
                    }
                    if (cameraDragButton_ == 2) {
                        camera_.Pan (ev.delta, rect.height);
                    }
                    else if (cameraDragAlt_ && cameraDragButton_ == 1) {
                        camera_.Zoom (ev.delta.y * 0.5f);
                    }
                    else {
                        camera_.Orbit (ev.delta);
                    }
                    ev.Use ();
                    RepaintView ();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == viewId) {
                        GUIUtility.hotControl = 0;
                        EndCameraDrag ();
                        if (boxSelecting_) {
                            boxSelecting_ = false;
                            if (boxMoved_) SelectPointsInBox (rect, ev.shift || ev.control || ev.command);
                            else SelectByMeshClick (boxStart_, rect);
                            PoseHandleUtility.focusControl = 0;
                            RepaintView ();
                        }
                        ev.Use ();
                    }
                    // 右クリックとして渡したが、下にハンドルが無かった
                    else if (PoseHandleUtility.contextClickReleased) {
                        ev.Use ();
                    }
                    break;
                case EventType.MouseMove:
                    // ハンドルの上に来たときの色を変える
                    if (mouseInView) RepaintView ();
                    break;
                case EventType.ScrollWheel:
                    // 選んでいる骨の上のホイールはねじり（PoseHandles.BoneHandle が先に使う）。残ったものでズームする
                    if (mouseInView && GUIUtility.hotControl == 0) {
                        LeaveContextCamera ();
                        camera_.Zoom (ev.delta.y);
                        ev.Use ();
                        RepaintView ();
                    }
                    break;
            }
        }

        bool boxSelecting_;
        bool boxMoved_;
        Vector2 boxStart_;
        Vector2 boxEnd_;

        /// <summary>
        /// 枠の中にある全身 IK の点をまとめて選ぶ。add なら今の選択に足す（Shift / Ctrl）
        /// </summary>
        void SelectPointsInBox (Rect rect, bool add)
        {
            if (stage_ == null || rect.width < 1 || rect.height < 1) return;
            Rect box = Rect.MinMaxRect (Mathf.Min (boxStart_.x, boxEnd_.x), Mathf.Min (boxStart_.y, boxEnd_.y), Mathf.Max (boxStart_.x, boxEnd_.x), Mathf.Max (boxStart_.y, boxEnd_.y));
            Camera camera = stage_.camera;
            camera_.Apply (camera);
            camera.aspect = rect.width / rect.height;
            List<GameObject> selected = new List<GameObject> ();
            if (add) selected.AddRange (selection_);
            // キーの自動追加が切で、今のフレームに姿勢のキーが無ければ点は選ばない（点のハンドルも薄く出してつかめなくしている）
            bool canEdit = CanEditPoints ();
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (!canEdit || point == null || point.gameObject == null) continue;
                if (IsInBox (camera, rect, box, point.framePosition) && !selected.Contains (point.gameObject)) selected.Add (point.gameObject);
            }
            SelectObjects (selected);
        }

        /// <summary>引いている枠（表示域の中の座標。引いていなければ null）</summary>
        Rect? draggingBox
        {
            get {
                if (!boxSelecting_ || !boxMoved_) return null;
                return Rect.MinMaxRect (Mathf.Min (boxStart_.x, boxEnd_.x), Mathf.Min (boxStart_.y, boxEnd_.y), Mathf.Max (boxStart_.x, boxEnd_.x), Mathf.Max (boxStart_.y, boxEnd_.y));
            }
        }

        /// <summary>
        /// ワールドの点が枠の中に映っているか。換算はクリックでメッシュを選ぶときと同じ（HandleUtility の換算は、この窓では縦にずれる）
        /// </summary>
        static bool IsInBox (Camera camera, Rect rect, Rect box, Vector3 world)
        {
            // ビューポート座標は左下が原点
            Vector3 view = camera.WorldToViewportPoint (world);
            if (view.z <= 0) return false;
            return box.Contains (new Vector2 (view.x * rect.width, (1 - view.y) * rect.height));
        }

        void BeginCameraDrag (int viewId, Event ev)
        {
            // 演出のカメラで見ていたら、その位置からいつもの視点へ移って動かす
            LeaveContextCamera ();
            GUIUtility.hotControl = viewId;
            cameraDragButton_ = ev.button;
            cameraDragAlt_ = ev.alt;
            // カーソルが画面の端に着くと、それ以上動けずに視点が止まる（窓を最大化していると、端はパネルの上）。
            // Scene ビューと同じく、端で反対側へ回して動かし続けられるようにする
            cameraDragging_ = true;
            cameraDragTravel_ = 0;
            EditorGUIUtility.SetWantsMouseJumping (1);
            ev.Use ();
        }

        void EndCameraDrag ()
        {
            if (!cameraDragging_) return;
            cameraDragging_ = false;
            EditorGUIUtility.SetWantsMouseJumping (0);
        }

        void HandleDragAndDrop (Rect rect)
        {
            Event ev = Event.current;
            if (ev.type != EventType.DragUpdated && ev.type != EventType.DragPerform) return;
            if (!rect.Contains (ev.mousePosition)) return;

            Object dropped = DragAndDrop.objectReferences.FirstOrDefault (o => o is AnimationClip || (o is GameObject && EditorUtility.IsPersistent (o)));
            if (dropped == null) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (ev.type == EventType.DragPerform) {
                DragAndDrop.AcceptDrag ();
                if (dropped is AnimationClip) DropClip ((AnimationClip)dropped);
                else SetModel ((GameObject)dropped);
            }
            ev.Use ();
        }

        void DrawViewportInfo (Rect rect)
        {
            DrawContextViewInfo (rect);
            string text;
            if (stage_.error != null) {
                text = stage_.error;
            }
            else if (prefab_ == null) {
                text = "キャラクターの prefab を上のツールバーか、ここへドラッグしてください";
            }
            else {
                text = prefab_.name;
                if (editingClip_ != null) {
                    text += "   " + editingClip_.name + (targetClip != editingClip_ && targetClip != null ? " → " + targetClip.name : "") + "   " + clock_.Describe ();
                    if (clock_.isHeld) text += "   （つかんでいる間は再生を止めている）";
                    else if (!clock_.isPlaying && !clock_.onGrid) text += "   （キーは丸めたフレームに打つ）";
                }
            }
            GUI.Label (new Rect (rect.x + 8, rect.yMax - 22, rect.width - 16, 18), text, EditorStyles.whiteLabel);
            // 表示の骨をつかんでいるときは、最後の残差を出す（閾値を超えたら黄色）
            string grab = displayGrabStatus;
            if (grab != null) {
                GUIStyle style = new GUIStyle (EditorStyles.whiteLabel);
                if (displayGrabWarning) style.normal.textColor = new Color (1f, 0.8f, 0.3f);
                GUI.Label (new Rect (rect.x + 8, rect.yMax - 58, rect.width - 16, 18), grab, style);
            }
            // 表示の骨をつかむために切っている拘束（選んでいる間だけ。表示はその拘束を掛けない姿）
            if (isDisplayGrab && stage_ != null && stage_.mutedConstraints.Count > 0) {
                GUIStyle style = new GUIStyle (EditorStyles.whiteLabel);
                style.normal.textColor = new Color (1f, 0.8f, 0.3f);
                GUI.Label (new Rect (rect.x + 8, rect.yMax - 76, rect.width - 16, 18),
                    "選んでいる間は切っている拘束: " + string.Join ("、", stage_.mutedConstraints), style);
            }
            // 最後に焼いた結果は右下に出す（失敗は赤）
            if (bakeStatus_ != null) {
                GUIStyle style = new GUIStyle (EditorStyles.whiteLabel) { alignment = TextAnchor.MiddleRight };
                if (bakeFailed_) style.normal.textColor = new Color (1f, 0.45f, 0.4f);
                GUI.Label (new Rect (rect.x + 8, rect.yMax - 40, rect.width - 16, 18), bakeStatus_, style);
            }

            // キーが打てないときは、見落とさないように表示域の下に目立たせて出す
            string warning = null;
            MessageType warningType = MessageType.Warning;
            if (useHumanoidBase) {
                // 読み取り専用の土台。Override を書き出し先にしていればそのまま打てるので何も出さない
                if (overrideWriteLayer == null) {
                    warning = "元は Humanoid のクリップ（読み取り専用）です。キーを打つには +Override で Override を足すか、取込で編集用クリップにしてください";
                    warningType = MessageType.Info;
                }
            }
            else if (clipProblem_ != null) {
                warning = "このクリップは編集できません（キーは打たれません）: " + clipProblem_;
                warningType = MessageType.Error;
            }
            else if (editingClip_ != null && strayCurveCount > 0) {
                warning = "編集用リグ以外を指すカーブが " + strayCurveCount + " 本残っています（姿勢には効きません）。ツールバーの Clean で消せます";
            }
            if (warning != null) {
                // アイコンの分だけ幅を引いて高さを測る
                float height = Mathf.Max (38, EditorStyles.helpBox.CalcHeight (new GUIContent (warning), rect.width - 16 - 40) + 8);
                EditorGUI.HelpBox (new Rect (rect.x + 8, rect.yMax - 28 - height, rect.width - 16, height), warning, warningType);
            }
        }

        /// <summary>
        /// Front はキャラの正面から、Side は +Z 向き（XY 平面を見る）、Top は真上から
        /// </summary>
        public void LookFrom (ViewPreset preset)
        {
            LeaveContextCamera ();
            switch (preset) {
                case ViewPreset.Front:
                    camera_.LookAlong (-(stage_ != null ? stage_.bodyForward : Vector3.forward));
                    break;
                case ViewPreset.Side:
                    camera_.LookAlong (Vector3.forward);
                    break;
                case ViewPreset.Top:
                    camera_.LookDown ();
                    break;
            }
            RepaintView ();
        }

        /// <summary>
        /// 選んでいる骨、無ければキャラ全体が画面に収まるようにする
        /// </summary>
        public void FrameSelection ()
        {
            if (stage_ == null || stage_.animator == null) return;
            LeaveContextCamera ();

            PoseTarget target = selectedTarget;
            if (target != null) {
                camera_.Frame (target.framePosition, 0.3f, ViewportAspect ());
            }
            else {
                camera_.Frame (stage_.GetBounds (), ViewportAspect ());
            }
            RepaintView ();
        }

        void FrameModel ()
        {
            camera_.Frame (stage_.GetBounds (), ViewportAspect ());
            // 斜め前から少し見下ろす
            camera_.LookAlong (Quaternion.Euler (0, 30, 0) * -stage_.bodyForward);
            camera_.pitch = 8;
        }

        /// <summary>
        /// クリップ全体のキーのフレーム。作り直すときは骨ごとのトラックのキー（trackKeys_）も捨てる
        /// </summary>
        int[] GetKeyFrames ()
        {
            if (keyFrames_ == null) {
                keyFrames_ = targetClip != null ? ClipKeyUtility.GetKeyFrames (targetClip) : new int[0];
                trackKeys_.Clear ();
            }
            return keyFrames_;
        }

        /// <summary>
        /// その物（骨・コントローラー）のカーブのキーのフレーム。クリップ全体のキーを作り直すときに捨てる
        /// </summary>
        int[] GetTargetKeyFrames (PoseTarget target)
        {
            GetKeyFrames ();
            int[] keys;
            if (trackKeys_.TryGetValue (target, out keys)) return keys;
            HashSet<string> paths = new HashSet<string> ();
            target.CollectKeyedPaths (paths);
            keys = paths.Count > 0 && targetClip != null
                ? ClipKeyUtility.GetKeyFrames (targetClip, binding => paths.Contains (binding.path)).Where (f => f >= 0).ToArray ()
                : new int[0];
            trackKeys_[target] = keys;
            return keys;
        }

        public int GetLastKeyFrame ()
        {
            int[] frames = GetKeyFrames ();
            int last = frames.Length > 0 ? Mathf.Max (0, frames[frames.Length - 1]) : 0;
            // 取り込む前の Generic のクリップも最後まで見られるように
            if (genericClip_ != null && !genericClip_.empty) last = Mathf.Max (last, Mathf.RoundToInt (genericClip_.length * (float)clock_.rate));
            if (humanoidClip_ != null && !humanoidClip_.empty) last = Mathf.Max (last, Mathf.RoundToInt (humanoidClip_.length * (float)clock_.rate));
            // 一緒に見ている演出の終わりまで
            last = Mathf.Max (last, ContextLastFrame ());
            return last;
        }

        /// <summary>
        /// 先頭はクリップ全体のトラック、続いて選んだ骨（Tracks が All なら全部）のトラック（TimelineOverlay が描く）
        /// </summary>
        public List<TimelineTrack> BuildTracks ()
        {
            List<TimelineTrack> tracks = new List<TimelineTrack> ();
            AnimationClip keyed = targetClip;
            if (keyed == null || stage_ == null || stage_.animator == null) return tracks;

            int[] allKeys = GetKeyFrames ().Where (f => f >= 0).ToArray ();
            tracks.Add (new TimelineTrack { label = keyed.name, keys = allKeys, filter = null });

            foreach (PoseTarget target in GetTrackTargets ()) {
                HashSet<string> paths = new HashSet<string> ();
                target.CollectKeyedPaths (paths);
                if (paths.Count == 0) continue;

                System.Predicate<EditorCurveBinding> filter = binding => paths.Contains (binding.path);
                tracks.Add (new TimelineTrack { label = target.label, keys = GetTargetKeyFrames (target), filter = filter });
            }
            return tracks;
        }

        IEnumerable<PoseTarget> GetTrackTargets ()
        {
            if (showTracks_ == ShowTracksFlag.All) return targets_;

            return selection_.Select (FindTarget).Where (t => t != null);
        }

        /// <summary>
        /// 前（direction = -1）か次（+1）のキーへ移る
        /// </summary>
        public void GoToKey (int direction)
        {
            int[] keys = GetKeyFrames ().Where (f => f >= 0).ToArray ();
            int target = direction < 0
                ? keys.Where (f => f < currentFrame).DefaultIfEmpty (currentFrame).Max ()
                : keys.Where (f => f > currentFrame).DefaultIfEmpty (currentFrame).Min ();
            SetFrame (target);
        }

        /// <summary>
        /// 今の姿勢を、つかめるもの全部ぶん今のフレームにキーとして打つ
        /// </summary>
        /// <param name="edit">操作の後に打つとき。キーの自動追加が切なら、キーのあるカーブだけ書き換え、
        /// キーの無い物が操作の巻き添えで動いた分（IK を解き直した先など）はクリップの姿勢へ戻す</param>
        void AddKeyAllCurves (bool edit = false)
        {
            if (!CanEditClip ()) return;

            using (CurveWriter writer = BeginCurves ("Key Pose", edit)) {
                foreach (PoseTarget target in targets_) {
                    target.WriteKeys (writer);
                }
                // ゲームの Rig の重みも同じフレームに打つ（チャンネルの組をそろえておく）
                if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
            }
            keyFrames_ = null;
            if (edit && !autoKey) SamplePose ();
        }

        /// <summary>
        /// ゲームの Rig の値の代理（重み・ターゲット）。キャラが無ければ null
        /// </summary>
        public RigProxies rigProxies
        {
            get { return stage_ != null && stage_.editingRig != null ? stage_.editingRig.rigProxies : null; }
        }

        /// <summary>
        /// ゲームの Rig の重みを変えて、今のフレームにキーを打つ（Rig Values のパネル）。
        /// ドラッグ中の変更は Unity がマウスの押下単位で 1 つの Undo にまとめる
        /// </summary>
        public void SetRigWeight (RigProxies.Weight weight, float value)
        {
            if (weight == null || stage_ == null) return;
            float before = weight.value;
            weight.value = value;
            SolveAfterManipulate ();
            if (CanEditClip ()) {
                CurveWriter written;
                using (CurveWriter writer = written = BeginCurves ("Rig Weight " + weight.label, true)) {
                    weight.WriteKeys (writer);
                }
                keyFrames_ = null;
                // キーの自動追加が切でキーが無ければ、値を戻す
                if (written.writtenCount == 0 && written.skippedCount > 0) {
                    weight.value = before;
                    SolveAfterManipulate ();
                    NotifyNoKey ();
                }
            }
            RepaintView ();
        }

        /// <summary>
        /// Rig のターゲットを選ぶ（Rig Values のパネルから）
        /// </summary>
        public void SelectRigSource (RigProxies.Source source)
        {
            if (source == null || source.proxy == null) return;
            Select (source.proxy.gameObject);
            RepaintView ();
        }

        /// <param name="filter">動かすカーブ（骨のトラックならその骨のカーブ）。null ならクリップ全体</param>
        public void MoveKeys (int frame, int movedFrame, bool copy, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (!CanEditClip ()) return;

            RecordClipUndo (copy ? "Copy Keys" : "Move Keys");
            ClipKeyUtility.MoveKeysAtFrame (targetClip, frame, movedFrame, copy, filter);
            keyFrames_ = null;
            SamplePose ();
        }

        /// <param name="filter">消すカーブ（骨のトラックならその骨のカーブ）。null ならクリップ全体</param>
        public void RemoveKeys (int frame, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (!CanEditClip ()) return;

            RecordClipUndo ("Remove Keys");
            ClipKeyUtility.RemoveKeysAtFrame (targetClip, frame, filter);
            keyFrames_ = null;
            SamplePose ();
        }

        /// <summary>
        /// タイムラインで選んだキーをまとめて消す（1 回の Undo で戻る）。filter はトラックのもの（null ならクリップ全体）
        /// </summary>
        public void RemoveKeys (IList<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys)
        {
            if (!CanEditClip () || keys == null || keys.Count == 0) return;

            RecordClipUndo ("Remove Keys");
            foreach (KeyValuePair<int, System.Predicate<EditorCurveBinding>> key in keys) {
                ClipKeyUtility.RemoveKeysAtFrame (targetClip, key.Key, key.Value);
            }
            keyFrames_ = null;
            SamplePose ();
        }

        /// <summary>
        /// タイムラインで選んだキーを覚える（Ctrl+C）。filter はトラックのもの（null ならクリップ全体）
        /// </summary>
        /// <returns>覚えたキーの数（カーブ単位）</returns>
        public int CopyKeys (IList<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys)
        {
            return TimelineClipboard.Copy (targetClip, keys);
        }

        /// <summary>覚えたキーを貼れるか</summary>
        public bool canPasteKeys
        {
            get { return !TimelineClipboard.isEmpty && CanEditClip (); }
        }

        /// <summary>
        /// 覚えたキーを今のフレームを先頭にして貼る（Ctrl+V。1 回の Undo で戻る）。行き先にあるキーは上書きする。
        /// ポーズの貼り付けと同じく明示的に打つ操作なので、キーの自動追加が切でも貼る
        /// </summary>
        /// <returns>貼ったキーの数（カーブ単位）</returns>
        public int PasteKeys ()
        {
            if (!canPasteKeys) return 0;

            RecordClipUndo ("Paste Keys");
            int count = TimelineClipboard.Paste (targetClip, currentFrame);
            if (stage_ != null) stage_.InvalidateClipCurves ();
            keyFrames_ = null;
            SamplePose ();
            return count;
        }

        /// <summary>
        /// 選んでいる骨のキーを keepFrame の 1 つだけにする（値はそのフレームの姿勢）。keepFrame が負なら全部消す。
        /// 読込（毎フレームにキーが入る）の後で、指など一部だけを少ないキーで打ち直すため
        /// </summary>
        public void ClearSelectedKeys (int keepFrame)
        {
            if (!CanEditClip ()) return;
            HashSet<string> paths = new HashSet<string> ();
            foreach (PoseTarget target in selection_.Select (FindTarget).Where (t => t != null)) target.CollectKeyedPaths (paths);
            if (paths.Count == 0) return;

            RecordClipUndo (keepFrame < 0 ? "Clear Keys" : "Keep One Key");
            ClipKeyUtility.KeepOnlyKeyAtFrame (targetClip, keepFrame, binding => paths.Contains (binding.path));
            if (stage_ != null) stage_.InvalidateClipCurves ();
            keyFrames_ = null;
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 今のフレームのポーズをコピーする（C）。対象（骨・手足の IK・全身 IK の点など）を選んでいれば、選んだ物（複数可）の
        /// カーブだけを写す。貼る（V）と写した物にだけキーを打つので、腕だけ別のフレームの形にする、などができる。
        /// 何も選んでいなければクリップ全体を写す
        /// </summary>
        public void CopyPose ()
        {
            if (targetClip == null) return;
            HashSet<string> paths = new HashSet<string> ();
            int count = 0;
            foreach (PoseTarget target in selection_.Select (FindTarget).Where (t => t != null)) {
                target.CollectKeyedPaths (paths);
                count++;
            }
            if (count == 0) {
                PoseClipboard.Copy (targetClip, currentFrame);
                ShowNotification (new GUIContent ("ポーズ全体をコピー"), 0.8);
                return;
            }

            // 選んだ物にカーブが無くても、前にコピーしたものは捨てる（V で古い物を貼らないように）
            if (PoseClipboard.Copy (targetClip, currentFrame, binding => paths.Contains (binding.path)) == 0) {
                ShowNotification (new GUIContent ("選んだ物にキーがありません"), 1.5);
                return;
            }
            ShowNotification (new GUIContent ("選んだ物のポーズをコピー（" + count + " 個）"), 0.8);
        }

        public void PastePose ()
        {
            if (!CanEditClip () || PoseClipboard.isEmpty) return;

            using (CurveWriter writer = BeginCurves ("Paste Pose")) {
                PoseClipboard.Paste (writer);
            }
            keyFrames_ = null;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }

        /// <summary>
        /// 骨の向きを基準の姿勢に戻して、キーを打つ
        /// </summary>
        public void ResetPose ()
        {
            if (stage_ == null) return;
            foreach (PoseTarget target in targets_) {
                target.ResetPose ();
            }
            // Rig の重みとターゲットは prefab の値へ
            if (rigProxies != null) rigProxies.ResetToDefaults ();
            SolveAfterManipulate ();
            AddKeyAllCurves ();
            RepaintView ();
        }

        // ショートカット（この窓にフォーカスがあるときだけ効く。Edit > Shortcuts で変えられる）

        [Shortcut ("Lilium Motion Editor/Frame Selected", typeof (PreviewWindow), KeyCode.F)]
        static void FrameSelectedShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.FrameSelection ();
        }

        [Shortcut ("Lilium Motion Editor/Side View", typeof (PreviewWindow), KeyCode.F1)]
        static void SideViewShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.LookFrom (ViewPreset.Side);
        }

        [Shortcut ("Lilium Motion Editor/Top View", typeof (PreviewWindow), KeyCode.F2)]
        static void TopViewShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.LookFrom (ViewPreset.Top);
        }

        [Shortcut ("Lilium Motion Editor/Front View", typeof (PreviewWindow), KeyCode.F3)]
        static void FrontViewShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.LookFrom (ViewPreset.Front);
        }

        [Shortcut ("Lilium Motion Editor/Play", typeof (PreviewWindow), KeyCode.Space)]
        static void PlayShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.TogglePlay ();
        }

        [Shortcut ("Lilium Motion Editor/Copy Pose", typeof (PreviewWindow), KeyCode.C)]
        static void CopyPoseShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.CopyPose ();
        }

        [Shortcut ("Lilium Motion Editor/Paste Pose", typeof (PreviewWindow), KeyCode.V)]
        static void PastePoseShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.PastePose ();
        }

        [Shortcut ("Lilium Motion Editor/Previous Frame", typeof (PreviewWindow), KeyCode.LeftArrow)]
        static void PreviousFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.SetFrame (window.currentFrame - 1);
        }

        [Shortcut ("Lilium Motion Editor/Next Frame", typeof (PreviewWindow), KeyCode.RightArrow)]
        static void NextFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.SetFrame (window.currentFrame + 1);
        }

        [Shortcut ("Lilium Motion Editor/Back 10 Frames", typeof (PreviewWindow), KeyCode.LeftArrow, ShortcutModifiers.Shift)]
        static void Back10FramesShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.SetFrame (window.currentFrame - 10);
        }

        [Shortcut ("Lilium Motion Editor/Forward 10 Frames", typeof (PreviewWindow), KeyCode.RightArrow, ShortcutModifiers.Shift)]
        static void Forward10FramesShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.SetFrame (window.currentFrame + 10);
        }

        [Shortcut ("Lilium Motion Editor/Previous Key", typeof (PreviewWindow), KeyCode.Comma)]
        static void PreviousKeyShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.GoToKey (-1);
        }

        [Shortcut ("Lilium Motion Editor/Next Key", typeof (PreviewWindow), KeyCode.Period)]
        static void NextKeyShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.GoToKey (1);
        }

        /// <summary>
        /// Home / End は、骨の一覧（Picker）にフォーカスがあるときは一覧の先頭・末尾へ、無ければ最初のフレーム・最後のキーへ
        /// </summary>
        [Shortcut ("Lilium Motion Editor/First Frame", typeof (PreviewWindow), KeyCode.Home)]
        static void FirstFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window == null) return;

            if (window.isPickerFocused) window.SelectPickerEdge (true);
            else window.SetFrame (0);
        }

        [Shortcut ("Lilium Motion Editor/Last Key Frame", typeof (PreviewWindow), KeyCode.End)]
        static void LastKeyFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window == null) return;

            if (window.isPickerFocused) window.SelectPickerEdge (false);
            else window.SetFrame (window.GetLastKeyFrame ());
        }
    }

}
