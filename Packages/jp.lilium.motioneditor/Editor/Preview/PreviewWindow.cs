using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Lilium.MotionEditorLocalization;

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
            /// <summary>
            /// キーの間（keys[k] から keys[k + 1]）ごとの補間（S80。混在なら null）。キーの間の矢印の形を変える
            /// </summary>
            public KeyInterpolation?[] interpolations;
            /// <summary>キーの間ごとの外部の動きの状態（S82。元のクリップへ書いているときだけ。null なら外部の動きは見せない）</summary>
            public BetweenClips.State[] between;
            /// <summary>
            /// keys と同じ並びのキーの色（全身 IK の点の、そのフレームの状態の色）。null なら既定の色
            /// </summary>
            public Color[] keyColors;
        }

        /// <summary>タイムラインの下端（Layers パネルの一覧）の最小の高さ</summary>
        const float kMinLayersHeight = 120;
        const float kMinLayersWidth = 200;
        /// <summary>タイムラインのトラック一覧の最小の高さ</summary>
        const float kMinTimelineTracksHeight = 60;
        static readonly int kViewportHash = "MktPreviewViewport".GetHashCode ();
        /// <summary>浮かせて置くパネルの既定の場所（OverlayWindows.PlaceDefaults）</summary>
        static readonly KeyValuePair<string, Vector2>[] kFloatingDefaults = {
            new KeyValuePair<string, Vector2> ("mkt-picker", OverlayWindows.kPickerDefault),
            new KeyValuePair<string, Vector2> ("mkt-transform", OverlayWindows.kControllerDefault),
        };

        [SerializeField] GameObject prefab_;
        [SerializeField] AnimationClip editingClip_;
        /// <summary>今の世界（編集用リグ）を作ったときの定義。キャラの設定・プロジェクト設定で差し替えられたら作り直す</summary>
        EditRigDefinition stageRigDefinition_;
        /// <summary>
        /// 時計（クリップの秒・キーの格子・再生）。姿勢は秒で当て、キーは格子に丸めて打つ
        /// </summary>
        [SerializeField] Clock clock_ = new Clock ();
        [SerializeField] float handleSize_ = 0.03f;
        /// <summary>Controller パネルの Spinner の Move で、ドラッグ量に掛ける倍率（1 で 1 ピクセル 2.5mm）</summary>
        [SerializeField] float spinMoveScale_ = 1;
        [SerializeField] bool showBones_ = true;
        /// <summary>全身 IK の点（固定の印も）を出すか。切ると選んでいる物だけになる</summary>
        [SerializeField] bool showIk_ = true;
        [SerializeField] bool showFloor_ = true;
        [SerializeField] ShowTracksFlag showTracks_ = ShowTracksFlag.Selected;
        /// <summary>タイムライン オーバーレイのトラック一覧の高さ。下端のハンドルで変える</summary>
        [SerializeField] float timelineTracksHeight_ = 118;
        /// <summary>キーの間を補間せずに見せる（コマ送り。タイムラインの Interpolate を切ったとき）</summary>
        [SerializeField] bool stepped_;
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
        /// <summary>既定の置き場所を当てるかを見終えた（OverlayWindows.PlaceDefaults）</summary>
        bool overlayDefaultsChecked_;
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

        [MenuItem ("Window/Lilium Motion Editor/Motion Editor")]
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
            get { return RigDefinitionAssets.Resolve (characterSettings); }
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
            if (settings.prefab != null && settings.prefab != prefab) notes.Add (Tr ("PREVIEW_WINDOW_SETTINGS_OF_OTHER_PREFAB", settings.name, settings.prefab.name));
            if (current != null) notes.Add (Tr ("PREVIEW_WINDOW_CURRENT_SETTINGS_DETACHED", current.name));
            if (notes.Count > 0 && !EditorUtility.DisplayDialog (Tr ("PREVIEW_WINDOW_REASSIGN_SETTINGS_TITLE"), Tr ("PREVIEW_WINDOW_REASSIGN_SETTINGS_MESSAGE", string.Join ("\n", notes), prefab.name, settings.name), Tr ("PREVIEW_WINDOW_REASSIGN"), Tr ("PREVIEW_WINDOW_CANCEL"))) {
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
            if (!EditorUtility.DisplayDialog ("Motion Editor", Tr ("PREVIEW_WINDOW_REMOVE_STRAY_CURVES_CONFIRM", editingClip_.name, strayCurveCount), Tr ("PREVIEW_WINDOW_DELETE"), Tr ("PREVIEW_WINDOW_CANCEL"))) return;

            // 書き込み先が Override でも、消すのは編集用クリップのカーブ（そちらを記録しないと Undo で戻らない）
            RecordClipUndo ("Remove Stray Curves", editingClip_);
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

        /// <summary>選んでいる物（同じ物を指す GameObject が 2 つ選ばれていても 1 つ）</summary>
        IEnumerable<PoseTarget> SelectedTargets ()
        {
            return selection_.Select (FindTarget).Where (t => t != null).Distinct ();
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

        /// <summary>Controller パネルの Spinner の Move で、ドラッグ量に掛ける倍率（窓ごとの設定）</summary>
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

        /// <summary>全身 IK の点（固定の印も）を出すか</summary>
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

        /// <summary>
        /// キーの間を補間して見せるか（タイムラインの Interpolate）。切ると、今のフレーム以前のいちばん近いキーの姿勢のまま、次のキーで切り替わる（コマ送り）。
        /// 見せ方だけを変える: キーもカーブも書き換えず、ゲーム用の焼き・検査・書き出しはいつもの補間のまま
        /// </summary>
        public bool interpolate
        {
            get { return !stepped_; }
            set {
                if (stepped_ == !value) return;
                stepped_ = !value;
                SamplePose ();
                RaiseStateChanged ();
            }
        }

        /// <summary>
        /// 姿勢を作る時刻。コマ送りのときは、今のフレーム以前のいちばん近いキーのフレーム（最初のキーより前は最初のキー。キーが無ければそのまま）
        /// </summary>
        float PoseTime (float time)
        {
            if (!stepped_) return time;
            int[] keys = GetKeyFrames ();
            if (keys.Length == 0) return time;
            float rate = (float)clock_.rate;
            float frame = time * rate;
            int held = keys[0];
            foreach (int key in keys) {
                if (key <= frame + 1e-3f) held = key;
                else break;
            }
            return held / rate;
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

        public bool IsSelected (GameObject go)
        {
            return selection_.Contains (go);
        }

        public void Select (GameObject go)
        {
            // ハンドルを押したときだけ、Shift（今の選択に足す）と Ctrl（子階層もまとめて選ぶ）を見る。
            // ハンドルはドラッグの間も押した物を選び直すので、押した後は、選んでいる物の 1 つなら選択を保つ（まとめて選んだまま動かせる）
            if (handlesDrawing_ && go != null) {
                if (handlePressed_ && (handleModifiers_ & (EventModifiers.Shift | EventModifiers.Control | EventModifiers.Command)) != 0) {
                    SelectWithModifiers (go, handleModifiers_);
                    return;
                }
                if (!handlePressed_ && selection_.Contains (go)) return;
            }
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

        // ハンドルを描いている間か・その出来事がマウスを押したところか・そのときの修飾キー（DoHandles が入れる）
        bool handlesDrawing_;
        bool handlePressed_;
        EventModifiers handleModifiers_;

        /// <summary>
        /// 修飾キーを見て選ぶ。Shift なら今の選択に足す（押した物が選んであっても外さない）、Ctrl（Mac は Command）なら押した物の子階層の物もまとめて選ぶ。
        /// 押した物が先頭（今の対象）になり、全身 IK の点なら中心の点になる
        /// </summary>
        void SelectWithModifiers (GameObject go, EventModifiers modifiers)
        {
            List<GameObject> selected = new List<GameObject> ();
            if ((modifiers & EventModifiers.Shift) != 0) selected.AddRange (selection_);
            selected.Remove (go);
            selected.Insert (0, go);
            if ((modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0) {
                foreach (PoseTarget child in ChildTargets (targets_, FindTarget (go), IsPickable)) {
                    if (!selected.Contains (child.gameObject)) selected.Add (child.gameObject);
                }
            }
            SelectObjects (selected);
            if (selected.Count > 1 && FindTarget (go) is BodyPointTarget) {
                pivotPoint_ = go;
                RaiseStateChanged ();
            }
        }

        /// <summary>
        /// parent の子階層にある物（表示モデルの骨の階層で、parent の骨より先に付いている物）。pickable で今つかめる物に絞る。
        /// 骨の付け根の点・骨（FK）なら、同じ骨に付いているほかの物（骨の上の点・その骨から始まる IK）も含む。
        /// 骨の付け根から離れた点（かかと・顔の前など）なら、その骨より先の物だけ
        /// </summary>
        internal static List<PoseTarget> ChildTargets (IList<PoseTarget> targets, PoseTarget parent, System.Func<PoseTarget, bool> pickable)
        {
            List<PoseTarget> children = new List<PoseTarget> ();
            Transform anchor = parent != null ? parent.anchor : null;
            if (anchor == null) return children;
            BodyPointTarget parentPoint = parent as BodyPointTarget;
            bool sameBone = parentPoint == null || parentPoint.isOnBoneOrigin;
            foreach (PoseTarget target in targets) {
                if (target == parent || target.gameObject == null || target.anchor == null) continue;
                bool below = target.anchor == anchor ? sameBone : target.anchor.IsChildOf (anchor);
                if (below && (pickable == null || pickable (target))) children.Add (target);
            }
            return children;
        }

        /// <summary>今ハンドルを出していてつかめる物か（DoHandles の出し分けと同じ）</summary>
        bool IsPickable (PoseTarget target)
        {
            if (target is BodyPointTarget) return isPointGrab && !isDisplayGrab && showIk_ && CanEditPoints ();
            FkTarget fk = target as FkTarget;
            if (isPointGrab && fk != null && !fk.fullBodyHandle) return false;
            if (!showBones_) return false;
            if (isDisplayGrab && !target.supportsDisplayHandles && !(target is RigSourceTarget)) return false;
            return CanEditTarget (target);
        }

        /// <summary>
        /// 今つかめる物（ハンドルを出している物）を全部選ぶ（Ctrl+A）。全身 IK の点をつかむ間は全部の点になる
        /// （点に関する骨も選んだことになるので、続けてコピーすると全身の姿勢を写せる。RelatedFkTargets）
        /// </summary>
        public void SelectAll ()
        {
            SelectObjects (targets_.Where (IsPickable).Select (t => t.gameObject));
            RepaintView ();
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
            Transform root = editingRoot;
            // 書き込み先が Override なら、そのクリップへ元からの差分として書く（S14）。サイクルキーのフレームなら先頭のキーへ打つ（S32）
            CurveWriter writer = CurveWriter.Begin (targetClip, root, KeyFrameToWrite (), undoName);
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
            if (IsPastCycleKey ()) return false;
            if (GetMaskProblem (target) != null) return false;
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
            if (IsPastCycleKey ()) return false;
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
            if (IsPastCycleKey ()) return false;
            if (autoKey || !CanEditClip ()) return true;
            foreach (PoseTarget target in targets_) {
                if (IsSelected (target.gameObject) && !CanEditTarget (target)) return false;
            }
            return true;
        }

        /// <summary>
        /// キーが無くて動かせなかったことを知らせる（キーの自動追加が切のとき）。サイクルキーより後ろで動かせなかったときは、そのことを知らせる
        /// </summary>
        void NotifyNoKey ()
        {
            string masked = null;
            foreach (PoseTarget target in targets_) {
                if (IsSelected (target.gameObject) && (masked = GetMaskProblem (target)) != null) break;
            }
            ShowNotification (new GUIContent (masked ?? (IsPastCycleKey () ? Tr ("PREVIEW_WINDOW_PAST_CYCLE_KEY") : Tr ("PREVIEW_WINDOW_NO_KEY_CANNOT_MOVE"))), 1.5);
        }

        /// <summary>
        /// 書き出し先（✏）の Override の段のマスクの外なので動かせない理由（動かせるなら null。S17）。
        /// 動かしてもキーを打てず、離すと元に戻るため、つかませない
        /// </summary>
        public string GetMaskProblem (PoseTarget target)
        {
            OverrideLayer layer = overrideWriteLayer;
            ChannelMask mask = layer != null ? layer.mask : null;
            if (!IsOutsideMask (stage_, mask, target)) return null;
            return Tr ("PREVIEW_WINDOW_OUTSIDE_MASK", target.label, mask.name, layer.label);
        }

        static readonly List<string> maskPaths_ = new List<string> ();

        /// <summary>
        /// 物がマスクの外か。物のキーのパスが 1 つでもマスクの中なら中。
        /// キーのパスを持たない物は外にしない
        /// </summary>
        internal static bool IsOutsideMask (PreviewStage stage, ChannelMask mask, PoseTarget target)
        {
            if (mask == null || target == null || stage == null) return false;
            maskPaths_.Clear ();
            target.CollectKeyedPaths (maskPaths_);
            if (maskPaths_.Count == 0) return false;
            foreach (string path in maskPaths_) {
                if (stage.InMask (mask, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.x"))) return false;
            }
            return true;
        }

        /// <summary>
        /// キーが無くて動かせなかったハンドルの操作を、つかむ前の操作値へ戻して知らせる。
        /// クリップを当て直すだけでは戻らない: カーブの無い値（Reset で打たない腰の位置など）は当て直しで今のまま残る
        /// </summary>
        void RevertManipulation (PreviewStage.ControlState before)
        {
            if (before != null) {
                stage_.RestoreControlState (before);
                SolveAfterManipulate ();
            }
            else {
                SamplePose ();
            }
            NotifyNoKey ();
            RepaintView ();
        }

        /// <summary>
        /// クリップを書き換える前の Undo の記録。アセットでないクリップ（メモリ上だけの物）は記録しない。
        /// 記録すると、どのシーンにも属さない物として開いているシーンが変更扱いになる
        /// </summary>
        /// <param name="clip">書き換えるクリップ（null なら書き込み先 targetClip）</param>
        void RecordClipUndo (string name, AnimationClip clip = null)
        {
            if (clip == null) clip = targetClip;
            if (EditorUtility.IsPersistent (clip)) {
                Undo.RecordObject (clip, name);
            }
        }

        /// <summary>
        /// クリップのカーブを書き換えた後。キーの一覧と残像を捨て、段が控えているカーブを読み直させて、姿勢を当て直す
        /// </summary>
        /// <summary>
        /// クリップを直に書き換える操作の共通の流れ: 前の操作と Undo を分けて clip を記録し、edit で書き換え、書き換えたら姿勢を当て直す。
        /// 記録するのは書き換えるクリップそのもの（書き込み先が Override でも編集用クリップを書き換える操作があり、
        /// 書き込み先を記録すると Undo で戻らなかった）
        /// </summary>
        void EditClip (AnimationClip clip, string undoName, System.Func<bool> edit)
        {
            Undo.IncrementCurrentGroup ();
            RecordClipUndo (undoName, clip);
            if (edit ()) AfterClipEdit ();
        }

        public void AfterClipEdit ()
        {
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }

        void OnEnable ()
        {
            titleContent = new GUIContent ("Motion Editor", Icons.Load ("Motion"));
            // マウスを動かすたびに描き直して、ハンドル・メッシュの下の物をすぐ強調する
            // （受け取らないと、ほかの理由で描き直されるまで、止めてしばらくしないと強調されない）
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;
            // 姿勢が変わったら、メッシュの当たりに使い回している焼いたメッシュを捨てる
            poseSampled -= InvalidatePickMeshes;
            poseSampled += InvalidatePickMeshes;
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
#if UNITY_6000_6_OR_NEWER
            viewport_ = ToolViewport.Create (rootVisualElement, "MktViewport", null);
#else
            viewport_ = ToolViewport.Create (rootVisualElement, "MktViewport", OnViewportGUI);
#endif
            RegisterEditCommands (this, () => this);
        }

#if !UNITY_6000_6_OR_NEWER
        /// <summary>表示域の操作（ツールの仕組みが無い版。ToolViewport.BeginHandles）</summary>
        void OnViewportGUI ()
        {
            if (ToolViewport.BeginHandles (handlesCamera, viewport_)) DoViewportToolGUI ();
        }
#endif

        /// <summary>
        /// 表示域の大きさ（左上が原点）。絵・ハンドル・カメラのアスペクトをここに合わせる
        /// </summary>
        Rect ViewportRect ()
        {
            return ToolViewport.LocalRect (viewport_);
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
        /// 別の窓で、どのハンドルにも当たらなかったクリック。メッシュを指していればその骨を選び、何も無ければ選択を外す。
        /// Shift・Ctrl は表示域のクリックと同じ
        /// </summary>
        internal void SelectByRay (Ray ray, EventModifiers modifiers)
        {
            if (stage_ == null) return;
            PoseTarget target = FindPickTarget (stage_.PickBone (ray));
            // 表示域のクリック（PickTargetAt）と同じく、キーの自動追加が切で今のフレームにキーの無い物は選ばない
            if (target != null && !CanEditTarget (target)) target = null;
            SelectPicked (target, modifiers);
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
            // prefab を選び直したら、開いているシーンのキャラの編集はやめる
            ForgetSceneTarget ();
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
        /// 今のフレームの姿勢を作り直して描き直す（段のパラメータなど、値を外から触ったとき）
        /// </summary>
        public void RefreshPose ()
        {
            ghostsDirty_ = true;
            SamplePose ();
        }

        /// <summary>
        /// 段を今のキャラとクリップから作り直し、保存しておいた選択（書き出す段・つかむ段・隠している段）を戻す
        /// </summary>
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
            poseStack_.EvaluateFrom (poseStack_.Find (LayerKind.EditingRig), PoseTime (CurrentTime ()));
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
            // キーを打つクリップが替わるので、キーの一覧を作り直す（前の書き込み先のキーがタイムラインに残っていた）
            keyFrames_ = null;
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
            ApplyFloorClamp ();
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
            // コマ送りのときは、キャラの姿勢だけを前のキーで止める（演出は今の時刻のまま）
            poseStack_.Evaluate (PoseTime (time));
            EvaluateContexts (time);
            RepaintView ();
            if (poseSampled != null) poseSampled ();
        }

        void OnUndoRedo ()
        {
            keyFrames_ = null;
            RestoreCycleAfterUndo ();
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
                if (clip == targetClip) MarkCycleDirty ();
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
            // サイクルキー: 先頭のキーを直したら最後のキーも合わせる（S32。合わせて書いた分の知らせでは合わせ直さない）
            SyncCycleAfterKeysChanged ();
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
        void OnGUI ()
        {
            if (stage_ == null) RebuildStage ();
            if (Event.current.type == EventType.Layout) {
                if (!overlayDefaultsChecked_) overlayDefaultsChecked_ = OverlayWindows.PlaceDefaults (this, kFloatingDefaults, "mkt-preview-view", false);
                OverlayWindows.KeepInside (this, OverlayWindows.PanelIds (typeof (PreviewWindow)));
                OverlayWindows.FoldMenuInToolbar (this);
            }
            // 窓の外へ出たら、メッシュの下の物の強調を外す（外では MouseMove が来ない）
            if (Event.current.type == EventType.MouseLeaveWindow && UpdateMeshHover (false, Vector2.zero, Rect.zero)) RepaintView ();
            HandleEditCommandGUI ();
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
            System.Predicate<EditorCurveBinding> exclude = InterpolationExclude ();
            tracks.Add (new TimelineTrack { label = keyed.name, keys = allKeys, filter = null, interpolations = KeyInterpolations.GetRanges (keyed, allKeys, null, exclude), between = BetweenStates (keyed, allKeys) });

            foreach (PoseTarget target in GetTrackTargets ()) {
                HashSet<string> paths = new HashSet<string> ();
                target.CollectKeyedPaths (paths);
                if (paths.Count == 0) continue;

                System.Predicate<EditorCurveBinding> filter = binding => paths.Contains (binding.path);
                int[] keys = GetTargetKeyFrames (target);
                BodyPointTarget point = target as BodyPointTarget;
                Color[] keyColors = point != null ? keys.Select (f => point.GetKeyColor (keyed, f)).ToArray () : null;
                tracks.Add (new TimelineTrack {
                    label = target.label, keys = keys, filter = filter, keyColors = keyColors,
                    interpolations = KeyInterpolations.GetRanges (keyed, keys, filter, exclude),
                    between = BetweenStates (keyed, keys),
                });
            }
            return tracks;
        }

        /// <summary>キーの間ごとの外部の動きの状態（S82）。外部の動きは元のクリップに効くので、元のクリップのトラックのときだけ</summary>
        BetweenClips.State[] BetweenStates (AnimationClip keyed, int[] keys)
        {
            if (keyed != editingClip_ || keys.Length < 2 || !BetweenClips.HasAny (keyed)) return null;
            BetweenClips.State[] states = new BetweenClips.State[keys.Length - 1];
            for (int k = 0; k + 1 < keys.Length; k++) states[k] = BetweenClips.GetState (keyed, keys[k], keys[k + 1]);
            return states;
        }

        IEnumerable<PoseTarget> GetTrackTargets ()
        {
            if (showTracks_ == ShowTracksFlag.All) return targets_;

            return SelectedTargets ();
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
    }

}
