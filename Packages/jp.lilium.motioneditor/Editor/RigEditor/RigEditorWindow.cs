using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// リグエディタ（S45〜）。リグ定義（EditRigDefinition）を、キャラを見ながら直す窓。
    /// リグは基準姿勢（T ポーズ）の上で直し、クリップや時刻は持たない（キーを打つのはモーションエディタ）。
    ///
    /// キャラは窓なしで姿勢を組み立てる仕組み（PoseSession）の描く世界に置く。試し動かし（S49）で、モーションエディタと同じ解き方がそのまま使える。
    /// 操作の回し方はモーションエディタ（PreviewWindow）と同じ: 6.6 以降はツールの仕組み（RigEditorToolContext）に乗せ、
    /// それより前は表示域の IMGUIContainer を窓が自分で置いて同じ操作を回す。パネルとツールバーは Overlay
    /// </summary>
#if UNITY_6000_6_OR_NEWER
    [EditorToolOwner (typeof (RigEditorToolContext))]
    public sealed class RigEditorWindow : EditorWindow, IEditorHost, ISupportsOverlays, ISupportsEditorTools
#else
    public sealed class RigEditorWindow : EditorWindow, IEditorHost, ISupportsOverlays
#endif
    {
        /// <summary>
        /// ISupportsEditorTools の窓に Unity が入れる、ツールのハンドルを描く IMGUIContainer の名前
        /// </summary>
        const string kEditorToolsContainerName = "EditorToolsIMGUIContainer";
        static readonly int kViewportHash = "LiliumRigEditorViewport".GetHashCode ();
        /// <summary>クリックでコントロールを拾う距離（表示域のピクセル）</summary>
        const float kPickRadius = 12;
        /// <summary>全身 IK の点の丸の半径（ハンドルの大きさの何倍か）</summary>
        const float kPointScale = 0.45f;
        /// <summary>骨の線の端に空ける隙間の上限（骨の長さの何倍か）</summary>
        const float kMaxLineGap = 0.3f;
        static readonly Color kDisabledLineColor = new Color (0.55f, 0.55f, 0.55f, 0.35f);

        [SerializeField] GameObject prefab_;
        /// <summary>直す定義。空ならキャラが使う定義（キャラの設定、無ければプロジェクト設定）</summary>
        [SerializeField] EditRigDefinition definition_;
        [SerializeField] OrbitCamera camera_ = new OrbitCamera ();
        [SerializeField] string selectedId_;
        [SerializeField] float handleSize_ = 0.03f;
        /// <summary>今のキャラに視点を合わせたか（キャラを替えたら合わせ直す）</summary>
        [SerializeField] bool framed_;

        PoseSession session_;
        List<RigControlEntry> entries_ = new List<RigControlEntry> ();
        List<string> problems_ = new List<string> ();
        int errorCount_;
        List<RigDefinitionAssets.User> users_ = new List<RigDefinitionAssets.User> ();
        VisualElement viewport_;
        string hoverId_;
        bool rebuildQueued_;
        int cameraDragButton_ = -1;
        bool cameraDragAlt_;

        [MenuItem ("Window/Lilium Motion Editor/Rig Editor")]
        public static RigEditorWindow Open ()
        {
            RigEditorWindow window = GetWindow<RigEditorWindow> ();
            window.Show ();
            return window;
        }

        /// <summary>キャラ・定義・一覧が変わった（Overlay が作り直す）</summary>
        public event System.Action stateChanged;

        /// <summary>選んでいるコントロールが変わった（Overlay が一覧の選択を合わせる）</summary>
        public event System.Action selectionChanged;

        public GameObject model
        {
            get { return prefab_; }
        }

        /// <summary>直している定義（欄が空ならキャラが使う定義）。キャラも定義も無ければ null</summary>
        public EditRigDefinition definition
        {
            get { return definition_ != null ? definition_ : ResolveDefinition (prefab_); }
        }

        /// <summary>定義を欄で選んだか（false ならキャラが使う定義に付いていく）</summary>
        public bool definitionChosen
        {
            get { return definition_ != null; }
        }

        internal IReadOnlyList<RigControlEntry> entries
        {
            get { return entries_; }
        }

        /// <summary>一覧の上に出す問題（先頭の errorCount 個が誤り、残りは気にしておくこと）</summary>
        internal IReadOnlyList<string> problems
        {
            get { return problems_; }
        }

        internal int errorCount
        {
            get { return errorCount_; }
        }

        internal string selectedId
        {
            get { return selectedId_; }
        }

        internal RigControlEntry selectedEntry
        {
            get { return Find (selectedId_); }
        }

        PreviewStage stage
        {
            get { return session_ != null ? session_.stage : null; }
        }

        /// <summary>キャラが使う定義（モーションエディタと同じ引き当て）</summary>
        static EditRigDefinition ResolveDefinition (GameObject prefab)
        {
            return RigDefinitionAssets.Resolve (SettingsLookup.Find (prefab));
        }

        /// <summary>今の定義をここで直せない理由（直せるなら null）。パネルの欄はこのとき触れない</summary>
        internal string readOnlyReason
        {
            get { return RigDefinitionAssets.GetReadOnlyReason (definition); }
        }

        /// <summary>今の定義を使っているキャラ（キャラの設定があるもの）</summary>
        internal IReadOnlyList<RigDefinitionAssets.User> users
        {
            get { return users_; }
        }

        /// <summary>
        /// 今の定義（無ければ既定の中身）を複製して target に割り当て、複製を直す定義にする。
        /// パッケージの既定は書き換えないので、直すときはこれで自分の定義を作る
        /// </summary>
        internal void DuplicateDefinition (RigDefinitionAssets.AssignTarget target)
        {
            EditRigDefinition copy = RigDefinitionAssets.CreateCopy (definition, prefab_, target);
            definition_ = copy;
            EditorGUIUtility.PingObject (copy);
            Rebuild ();
        }

        public void SetModel (GameObject prefab)
        {
            if (prefab_ == prefab) return;
            prefab_ = prefab;
            framed_ = false;
            Rebuild ();
        }

        /// <summary>直す定義を選ぶ。null ならキャラが使う定義に付いていく</summary>
        public void SetDefinition (EditRigDefinition definition)
        {
            if (definition_ == definition) return;
            definition_ = definition;
            Rebuild ();
        }

        internal void Select (string id)
        {
            if (selectedId_ == id) return;
            selectedId_ = id;
            if (selectionChanged != null) selectionChanged ();
            RepaintView ();
        }

        RigControlEntry Find (string id)
        {
            if (string.IsNullOrEmpty (id)) return null;
            foreach (RigControlEntry entry in entries_) {
                if (entry.id == id) return entry;
            }
            return null;
        }

        void OnEnable ()
        {
            titleContent = new GUIContent ("Rig Editor", Icons.Load ("RigValues"));
            // マウスを動かすたびに描き直して、指しているコントロールをすぐ強調する
            wantsMouseMove = true;
            EditRigDefinition.changed -= OnDefinitionChanged;
            EditRigDefinition.changed += OnDefinitionChanged;
            ProjectSettings.changed -= QueueRebuild;
            ProjectSettings.changed += QueueRebuild;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeSession;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeSession;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Rebuild ();
        }

        void OnDisable ()
        {
            EditRigDefinition.changed -= OnDefinitionChanged;
            ProjectSettings.changed -= QueueRebuild;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeSession;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.delayCall -= RebuildQueued;
            rebuildQueued_ = false;
            EditorHost.Release (this);
            DisposeSession ();
        }

        /// <summary>
        /// プレイの開始と終了の前に捨てる（ドメインの再読み込みを切っていると OnDisable が来ない）。作り直しは次の描画で行う
        /// </summary>
        void OnPlayModeStateChanged (PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode) DisposeSession ();
            Repaint ();
        }

        void OnDefinitionChanged (EditRigDefinition changed)
        {
            if (changed == definition) QueueRebuild ();
        }

        /// <summary>
        /// 作り直しを 1 回にまとめる（インスペクタで値を変えると OnValidate が続けて来る）
        /// </summary>
        void QueueRebuild ()
        {
            if (rebuildQueued_) return;
            rebuildQueued_ = true;
            EditorApplication.delayCall += RebuildQueued;
        }

        void RebuildQueued ()
        {
            rebuildQueued_ = false;
            if (this != null) Rebuild ();
        }

        /// <summary>キャラとリグを作り直し、一覧を作り直す</summary>
        public void Rebuild ()
        {
            DisposeSession ();
            entries_ = new List<RigControlEntry> ();
            problems_ = new List<string> ();
            errorCount_ = 0;
            users_ = RigDefinitionAssets.FindUsers (definition);
            if (prefab_ != null && !EditorApplication.isPlayingOrWillChangePlaymode) {
                session_ = new PoseSession (prefab_, definition, true);
                if (session_.error != null) {
                    problems_.Add (session_.error);
                    errorCount_ = 1;
                }
                EditingRig rig = stage.editingRig;
                if (rig != null) {
                    entries_ = RigControlList.Build (rig, definition);
                    int errors;
                    List<string> found = RigControlList.CollectProblems (rig, out errors);
                    problems_.InsertRange (errorCount_, found.GetRange (0, errors));
                    errorCount_ += errors;
                    problems_.AddRange (found.GetRange (errors, found.Count - errors));
                }
                if (!framed_ && stage.model != null) {
                    FrameAll ();
                    framed_ = true;
                }
            }
            if (stateChanged != null) stateChanged ();
            RepaintView ();
        }

        void DisposeSession ()
        {
            if (session_ != null) session_.Dispose ();
            session_ = null;
        }

        /// <summary>
        /// 表示域は、Unity がツールのハンドル用に入れる IMGUIContainer をそのまま使い、窓全体に広げる（PreviewWindow と同じ）。
        /// キャラの絵はその下（窓の OnGUI）に描く
        /// </summary>
        void CreateGUI ()
        {
            VisualElement root = rootVisualElement;
#if UNITY_6000_6_OR_NEWER
            viewport_ = root.Q<IMGUIContainer> (kEditorToolsContainerName);
            if (viewport_ == null) {
                Debug.LogWarning (Tr ("PREVIEW_WINDOW_TOOL_CONTAINER_MISSING"));
                viewport_ = new VisualElement { name = "LiliumRigEditorViewport" };
                viewport_.style.flexGrow = 1;
                root.Add (viewport_);
            }
#else
            viewport_ = new IMGUIContainer (OnViewportGUI) { name = kEditorToolsContainerName };
            viewport_.style.flexGrow = 1;
            root.Add (viewport_);
#endif
        }

#if !UNITY_6000_6_OR_NEWER
        /// <summary>
        /// 表示域の操作（ツールの仕組みが無い版）。6.6 以降で Unity がツールコンテキストを呼ぶ前にすることを、ここで代わりにする
        /// （ハンドルのカメラの合わせ方は PreviewWindow.OnViewportGUI と同じ）
        /// </summary>
        void OnViewportGUI ()
        {
            Camera camera = handlesCamera;
            if (camera == null) return;
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
        /// ツールのハンドル用カメラ。ツールの GUI を回す直前に Unity が読むので、ここで描画用のカメラと視点を揃える（PreviewWindow と同じ）
        /// </summary>
        public Camera handlesCamera
        {
            get {
                if (stage == null || stage.handlesCamera == null) return null;
                stage.EnsureHandlesTarget (Screen.width, Screen.height);
                camera_.Apply (stage.handlesCamera);
                Rect view = ViewportRect ();
                if (view.height > 1) stage.handlesCamera.aspect = view.width / view.height;
                return stage.handlesCamera;
            }
        }

        public float handleSize
        {
            get { return handleSize_; }
        }

        public bool IsSelected (GameObject go)
        {
            return false;
        }

        public void Select (GameObject go)
        {
        }

        public void RecordUndo (Object obj, string name)
        {
            Undo.RecordObject (obj, name);
        }

        /// <summary>表示域の大きさ（左上が原点）</summary>
        Rect ViewportRect ()
        {
            if (viewport_ == null) return Rect.zero;
            Rect content = viewport_.contentRect;
            return float.IsNaN (content.width) || float.IsNaN (content.height) ? Rect.zero : new Rect (0, 0, content.width, content.height);
        }

        float ViewportAspect ()
        {
            Rect rect = ViewportRect ();
            return rect.height > 1 ? rect.width / rect.height : 1;
        }

        void RepaintView ()
        {
            Repaint ();
            if (viewport_ != null) viewport_.MarkDirtyRepaint ();
        }

        /// <summary>
        /// キャラの絵を表示域に描く。ハンドル・オーバーレイはこの上に重なる
        /// </summary>
        void OnGUI ()
        {
            if (session_ == null && prefab_ != null && !EditorApplication.isPlayingOrWillChangePlaymode) Rebuild ();
            if (Event.current.type != EventType.Repaint || viewport_ == null) return;

            Rect rect = viewport_.ChangeCoordinatesTo (rootVisualElement, viewport_.contentRect);
            if (rect.width < 1 || rect.height < 1) return;
            if (stage == null || stage.renderUtility == null) {
                GUI.Label (rect, Tr ("RIG_EDITOR_NO_MODEL"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            camera_.Apply (stage.camera);
            stage.camera.aspect = rect.width / rect.height;
            stage.UpdateLights (camera_.rotation);
            stage.renderUtility.BeginPreview (rect, GUIStyle.none);
            stage.renderUtility.Render (true, false);
            stage.renderUtility.EndAndDrawPreview (rect);
        }

        /// <summary>
        /// 表示域の操作。ツールコンテキスト（RigEditorToolContext）か OnViewportGUI から、Handles のカメラを設定した状態で呼ばれる。
        /// 視点は右ドラッグ・Alt+左ドラッグで回す、中ボタンで動かす、ホイール・Alt+右ドラッグで寄る（モーションエディタと同じ）。
        /// 左クリックでコントロールを選ぶ
        /// </summary>
        public void DoViewportToolGUI ()
        {
            if (stage == null || viewport_ == null) return;
            EditorHost.current = this;

            Event ev = Event.current;
            Rect rect = ViewportRect ();
            int viewId = GUIUtility.GetControlID (kViewportHash, FocusType.Passive, rect);
            bool mouseInView = rect.Contains (ev.mousePosition);

            switch (ev.GetTypeForControl (viewId)) {
                case EventType.MouseMove: {
                    string hover = mouseInView ? Pick (ev.mousePosition, rect) : null;
                    if (hover != hoverId_) {
                        hoverId_ = hover;
                        RepaintView ();
                    }
                    break;
                }
                case EventType.MouseDown:
                    if (!mouseInView) break;
                    if (ev.button == 0 && !ev.alt) {
                        Select (Pick (ev.mousePosition, rect));
                    }
                    else {
                        cameraDragButton_ = ev.button;
                        cameraDragAlt_ = ev.alt;
                        GUIUtility.hotControl = viewId;
                    }
                    ev.Use ();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != viewId) break;
                    if (cameraDragButton_ == 2) camera_.Pan (ev.delta, rect.height);
                    else if (cameraDragAlt_ && cameraDragButton_ == 1) camera_.Zoom (ev.delta.y * 0.5f);
                    else camera_.Orbit (ev.delta);
                    ev.Use ();
                    RepaintView ();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != viewId) break;
                    GUIUtility.hotControl = 0;
                    cameraDragButton_ = -1;
                    ev.Use ();
                    break;
                case EventType.ScrollWheel:
                    if (!mouseInView) break;
                    camera_.Zoom (ev.delta.y);
                    ev.Use ();
                    RepaintView ();
                    break;
                case EventType.KeyDown:
                    if (ev.keyCode == KeyCode.F && !ev.alt && !ev.control && !ev.command) {
                        FrameSelected ();
                        ev.Use ();
                    }
                    break;
                case EventType.Repaint:
                    DrawControls ();
                    break;
            }
        }

        /// <summary>
        /// 表示域の位置に一番近いコントロール（kPickRadius 以内）。無ければ null。
        /// 骨（FK・追加）は線、点は丸で拾う。線は丸より少し遠いことにして、線の端と丸が近いときは丸を選ぶ（モーションエディタの点の線と同じ）。
        /// 場所の換算はモーションエディタの枠選択と同じ（描画のカメラのビューポート座標。左下が原点）
        /// </summary>
        string Pick (Vector2 mousePosition, Rect rect)
        {
            if (stage == null || rect.width < 1 || rect.height < 1) return null;
            Camera camera = stage.camera;
            camera_.Apply (camera);
            camera.aspect = rect.width / rect.height;
            string best = null;
            float bestDistance = kPickRadius;
            foreach (RigControlEntry entry in entries_) {
                float distance;
                Vector3 start, end;
                Vector2 point, startPoint, endPoint;
                if (entry.line.Length > 1) {
                    // 骨の線は根元から先それぞれへ。どれかの線に近ければその骨
                    distance = float.MaxValue;
                    for (int i = 1; i < entry.line.Length; i++) {
                        if (!TryGetLine (entry, i, out start, out end) || !ToView (camera, start, rect, out startPoint) || !ToView (camera, end, rect, out endPoint)) continue;
                        distance = Mathf.Min (distance, DistanceToSegment (mousePosition, startPoint, endPoint) + BodyPointTarget.kLinkPickBias);
                    }
                }
                else {
                    if (!entry.position.HasValue || !ToView (camera, entry.position.Value, rect, out point)) continue;
                    distance = Vector2.Distance (point, mousePosition);
                }
                if (distance < bestDistance) {
                    best = entry.id;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>ワールドの位置を表示域の位置へ（左上が原点）。カメラの後ろなら false</summary>
        static bool ToView (Camera camera, Vector3 world, Rect rect, out Vector2 point)
        {
            Vector3 viewport = camera.WorldToViewportPoint (world);
            point = new Vector2 (viewport.x * rect.width, (1 - viewport.y) * rect.height);
            return viewport.z > 0;
        }

        static float DistanceToSegment (Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 delta = end - start;
            float t = delta.sqrMagnitude > 1e-6f ? Mathf.Clamp01 (Vector2.Dot (point - start, delta) / delta.sqrMagnitude) : 0;
            return Vector2.Distance (point, start + delta * t);
        }

        /// <summary>
        /// 骨の線（根元から index 番目の先まで）の両端。点の丸がある端は、丸に重ならないよう隙間を空ける（モーションエディタの点の線と同じ）。
        /// 指のような短い骨で線が消えないよう、隙間は骨の長さの一部までに抑える
        /// </summary>
        bool TryGetLine (RigControlEntry entry, int index, out Vector3 start, out Vector3 end)
        {
            start = entry.line[0];
            end = entry.line[index];
            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length < 1e-5f) return false;
            Vector3 direction = delta / length;
            float gap = Mathf.Min (handleSize_ * kPointScale * BodyPointTarget.kLinkGap, length * kMaxLineGap);
            if (HasPointAt (start)) start += direction * gap;
            if (HasPointAt (end)) end -= direction * gap;
            return true;
        }

        bool HasPointAt (Vector3 position)
        {
            foreach (RigControlEntry entry in entries_) {
                if (entry.kind == RigControlKind.Point && entry.position.HasValue && (entry.position.Value - position).sqrMagnitude < 1e-8f) return true;
            }
            return false;
        }

        /// <summary>
        /// 定義の全コントロールを描く。体の中に埋もれても見えるよう、奥行きで隠さない。
        /// 骨（FK・追加）はモーションエディタの全身 IK の点の線と同じ見た目の線、全身 IK の点は丸。線を先に描き、丸を上に重ねる。
        /// 使えないものは灰色で薄く出す（選べば一覧に理由が出る）
        /// </summary>
        void DrawControls ()
        {
            Camera camera = stage.handlesCamera;
            if (camera == null) return;
            Vector3 facing = camera.transform.forward;
            float size = handleSize_;
            UnityEngine.Rendering.CompareFunction zTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Color color = Handles.color;

            foreach (RigControlEntry entry in entries_) {
                bool selected = entry.id == selectedId_;
                bool hover = entry.id == hoverId_;
                Handles.color = selected ? BodyPointTarget.kRingSelectedColor
                    : hover ? BodyPointTarget.kRingHoverColor
                    : entry.enabled ? BodyPointTarget.kLinkColor
                    : kDisabledLineColor;
                float width = BodyPointTarget.kLinkWidth;
                // 子が複数ある骨（腰・胸など）は、子それぞれへ線を引く
                for (int i = 1; i < entry.line.Length; i++) {
                    Vector3 start, end;
                    if (!TryGetLine (entry, i, out start, out end)) continue;
                    Handles.DrawAAPolyLine (selected || hover ? width * BodyPointTarget.kLinkHighlightWidth : width, start, end);
                }
            }
            foreach (RigControlEntry entry in entries_) {
                if (entry.kind != RigControlKind.Point || !entry.position.HasValue) continue;
                Handles.color = entry.enabled ? KindColor (entry.kind) : new Color (0.55f, 0.55f, 0.55f, 0.6f);
                Handles.DrawSolidDisc (entry.position.Value, facing, size * kPointScale);
            }

            // 指している物・選んでいる物は、最後に重ねて名前を出す（点は輪も。骨の線は上で太く描いた）
            DrawHighlight (Find (hoverId_), facing, size, new Color (1, 1, 1, 0.8f));
            DrawHighlight (selectedEntry, facing, size, new Color (1, 0.85f, 0.2f, 1));

            Handles.color = color;
            Handles.zTest = zTest;
        }

        static void DrawHighlight (RigControlEntry entry, Vector3 facing, float size, Color color)
        {
            if (entry == null) return;
            if (entry.line.Length > 1) {
                // 名前は 1 本目の線の真ん中に出す（子が複数でも 1 つだけ）
                Handles.Label ((entry.line[0] + entry.line[1]) * 0.5f, entry.label, EditorStyles.whiteMiniLabel);
                return;
            }
            if (!entry.position.HasValue) return;
            Handles.color = color;
            Handles.DrawWireDisc (entry.position.Value, facing, size * 1.1f, 2);
            Handles.Label (entry.position.Value, entry.label, EditorStyles.whiteMiniLabel);
        }

        internal static Color KindColor (RigControlKind kind)
        {
            switch (kind) {
                case RigControlKind.Fk: return new Color (0.4f, 0.8f, 1f);
                case RigControlKind.Extra: return new Color (0.8f, 0.55f, 1f);
                default: return new Color (0.35f, 1f, 0.5f);
            }
        }

        void FrameAll ()
        {
            if (stage == null) return;
            camera_.Frame (stage.GetBounds (), ViewportAspect ());
            camera_.LookAlong (Quaternion.Euler (0, 30, 0) * -stage.bodyForward);
            camera_.pitch = 8;
        }

        /// <summary>選んだコントロールに寄る（選んでいなければキャラ全体）</summary>
        internal void FrameSelected ()
        {
            RigControlEntry entry = selectedEntry;
            if (entry != null && entry.position.HasValue) camera_.Frame (entry.position.Value, 0.3f, ViewportAspect ());
            else if (stage != null) camera_.Frame (stage.GetBounds (), ViewportAspect ());
            RepaintView ();
        }
    }

}
