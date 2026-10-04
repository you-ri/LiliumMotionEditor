using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 開いているシーンに置いたキャラのアニメーションを、そのシーンの中で編集する窓（S15d。Scene ビューを継承）。
    ///
    /// - 見えているのは開いているシーンそのもの（エフェクト・背景・ライト・カメラはシーンと Timeline 窓が出す）。
    /// - 姿勢はモーションエディタ（プレビュー窓）が裏で作り、シーンのキャラの骨へ写す（SceneMirror。編集をやめると元に戻る）。
    /// - この窓では、編集しているキャラの骨のハンドルだけを受け付ける。クリック選択・矩形選択・組み込みの移動ハンドルは出さない。
    /// 姿勢の計算と状態はモーションエディタの窓（プレビュー窓）が持つ。画面に出ているものがあればそれを使い、無ければこの窓が見えないプレビュー窓を
    /// 作って持つ。後から画面に出ている窓が現れたらそちらへ切り替え、見えない窓は捨てる（2 つが同じシーンのキャラへ写して取り合わないように）
    /// </summary>
    public class SceneWindow : SceneView
    {
        PreviewWindow source_;
        bool toolsHidden_;
        /// <summary>この窓が作った見えないプレビュー窓（開いているものが無かったとき）</summary>
        [SerializeField] PreviewWindow ownedSource_;

        [MenuItem ("Window/Lilium Motion Editor/Motion Editor In Scene (Experimental)")]
        static void Open ()
        {
            SceneWindow window = CreateWindow<SceneWindow> (typeof (SceneView));
            window.titleContent = new GUIContent ("Motion Scene", Icons.Load ("MotionScene"));
            window.Show ();
        }

        public override void OnEnable ()
        {
            // パネル（Overlay）は作られるときに扱う窓を決める。base.OnEnable の中で作られるので、その前に今ある窓へ結ぶ。
            // 見えない窓はここでは作らない（再読み込み直後はレイアウトの窓がまだ戻っていないことがある。最初の描画で決める）
            Bind (FindShownSource () ?? (ownedSource_ != null ? ownedSource_ : null));
            base.OnEnable ();
            titleContent = new GUIContent ("Motion Scene", Icons.Load ("MotionScene"));
            beforeSceneGui += BeforeSceneGui;
            duringSceneGui += DuringSceneGui;
        }

        /// <summary>
        /// 窓を閉じたとき。シーンのキャラの編集はやめる（写した姿勢を戻す。見えない窓が写し続けないように）。
        /// SceneView.OnDestroy は上書きできないので隠して、元を呼ぶ（Unity は名前で呼ぶ）
        /// </summary>
        public new void OnDestroy ()
        {
            if (source_ != null && source_.sceneTarget != null) source_.ClearSceneTarget ();
            base.OnDestroy ();
        }

        static readonly System.Reflection.FieldInfo kParent = typeof (EditorWindow).GetField ("m_Parent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>画面に出ている窓か（親のビューを持つ）。EditorWindow.m_Parent は内部なので名前で読む。読めなければ出ているとみなす</summary>
        static bool IsShown (EditorWindow window)
        {
            if (window == null) return false;
            return kParent == null || kParent.GetValue (window) as Object != null;
        }

        /// <summary>画面に出ているモーションエディタの窓（無ければ null）</summary>
        static PreviewWindow FindShownSource ()
        {
            return Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault (IsShown);
        }

        /// <summary>
        /// 姿勢を作る窓を決める。画面に出ている窓を優先し、無ければ前に作った見えない窓、それも無ければ作る。
        /// 替わったらパネルを作り直し、使わなくなった見えない窓は捨てる
        /// </summary>
        void ResolveSource ()
        {
            PreviewWindow next = FindShownSource ();
            if (next == null) next = source_ != null ? source_ : ownedSource_;
            if (next == null) {
                ownedSource_ = CreateInstance<PreviewWindow> ();
                ownedSource_.hideFlags = HideFlags.DontSave;
                next = ownedSource_;
            }
            if (next == source_) return;
            Bind (next);
            RebuildPanels ();
            RetireHiddenSources ();
        }

        /// <summary>
        /// この窓が作った見えない窓を、使わなくなったら捨てる。捨てる前にシーンのキャラの編集をやめさせる（写した姿勢を戻す）。
        /// 自分が作っていない窓は捨てない（再読み込み直後は、画面に出ている窓もまだ親を持たず、出ていないように見えることがある）
        /// </summary>
        void RetireHiddenSources ()
        {
            PreviewWindow owned = ownedSource_;
            if (owned == null || owned == source_ || IsShown (owned)) return;
            if (owned.sceneTarget != null) owned.ClearSceneTarget ();
            ownedSource_ = null;
            Object.DestroyImmediate (owned);
        }

        public override void OnDisable ()
        {
            beforeSceneGui -= BeforeSceneGui;
            duringSceneGui -= DuringSceneGui;
            Bind (null);
            RestoreTools ();
            base.OnDisable ();
        }

        /// <summary>姿勢を作っているプレビュー窓（最初に見つかったもの）</summary>
        public PreviewWindow source
        {
            get { return source_; }
        }

        void Bind (PreviewWindow window)
        {
            if (source_ == window) return;
            if (source_ != null) source_.viewRepainted -= Repaint;
            source_ = window;
            if (source_ != null) source_.viewRepainted += Repaint;
        }

        /// <summary>
        /// 組み込みの移動・回転ハンドル（Hierarchy で選んだ物を動かす）を、この窓を描く間だけ隠す
        /// </summary>
        void BeforeSceneGui (SceneView view)
        {
            if (view != this) return;
            // 姿勢を作る窓を決める（閉じられた・画面に出ている窓が現れた、なら切り替えてパネルを作り直す）
            ResolveSource ();
            if (Event.current.type == EventType.Layout) OverlayWindows.KeepInside (this, kPanelIds);
            if (!Tools.hidden) {
                Tools.hidden = true;
                toolsHidden_ = true;
            }
        }

        /// <summary>
        /// 編集しているキャラの骨のハンドルを描いて扱い、どのハンドルにも当たらなかったクリックを奪う（選択・矩形選択をさせない）。
        /// 当たらなかった左クリックは、キャラのメッシュを指していればその骨を選ぶ
        /// </summary>
        void DuringSceneGui (SceneView view)
        {
            if (view != this) return;
            RestoreTools ();

            bool editing = source_ != null && source_.sceneTarget != null && source_.stage != null;
            if (editing) source_.DoExternalHandles ();

            Event ev = Event.current;
            int id = GUIUtility.GetControlID (FocusType.Passive);
            HandleUtility.AddDefaultControl (id);
            if (editing && ev.GetTypeForControl (id) == EventType.MouseDown && ev.button == 0 && !ev.alt && HandleUtility.nearestControl == id) {
                source_.SelectByRay (HandleUtility.GUIPointToWorldRay (ev.mousePosition));
                ev.Use ();
            }

            DrawPanel ();
        }

        /// <summary>
        /// 左上: 編集しているキャラと、選んでいるキャラを編集対象にするボタン
        /// </summary>
        void DrawPanel ()
        {
            Handles.BeginGUI ();
            GUILayout.BeginArea (new Rect (8, 8, Mathf.Min (position.width - 16, 640), 100));
            if (source_ == null) {
                GUILayout.Label (Tr ("SCENE_WINDOW_NOT_READY"), EditorStyles.whiteLabel);
            } else {
                string status = source_.sceneTargetStatus;
                GUILayout.Label (status ?? Tr ("SCENE_WINDOW_SELECT_HINT"), EditorStyles.whiteLabel);
                GUILayout.BeginHorizontal ();
                GameObject candidate = PreviewWindow.FindSceneTargetRoot (Selection.activeGameObject);
                using (new EditorGUI.DisabledScope (candidate == null || candidate == source_.sceneTarget)) {
                    if (GUILayout.Button (candidate != null ? Tr ("SCENE_WINDOW_EDIT_SELECTED_NAMED", candidate.name) : Tr ("SCENE_WINDOW_EDIT_SELECTED"), GUILayout.ExpandWidth (false))) {
                        source_.SetSceneTarget (candidate);
                    }
                }
                using (new EditorGUI.DisabledScope (source_.sceneTarget == null && source_.sceneTargetStatus == null)) {
                    if (GUILayout.Button (Tr ("SCENE_WINDOW_STOP_EDITING"), GUILayout.ExpandWidth (false))) source_.ClearSceneTarget ();
                }
                GUILayout.EndHorizontal ();
                if (source_.sceneTarget != null) {
                    // Timeline 窓への追従と、今キーを打てない理由（バインドされたトラック・ブレンド中など）
                    GUIStyle warn = new GUIStyle (EditorStyles.whiteLabel);
                    warn.normal.textColor = new Color (1f, 0.8f, 0.3f);
                    if (source_.followStatus != null) GUILayout.Label (source_.followStatus, source_.followWarning ? warn : EditorStyles.whiteLabel);
                    string block = source_.timelineEditBlockReason;
                    if (block != null) GUILayout.Label (Tr ("SCENE_WINDOW_CANNOT_EDIT", block), warn);
                }
            }
            GUILayout.EndArea ();
            Handles.EndGUI ();
        }

        static readonly System.Reflection.MethodInfo kRebuildContent = typeof (UnityEditor.Overlays.Overlay).GetMethod (
            "RebuildContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// パネルの中身を作り直す（扱う窓が替わったとき）。Overlay.RebuildContent は内部 API なので名前で呼ぶ。呼べなければ、窓を開き直せば作り直される
        /// </summary>
        void RebuildPanels ()
        {
            if (kRebuildContent == null) return;
            foreach (string id in kPanelIds) {
                UnityEditor.Overlays.Overlay overlay;
                if (!TryGetOverlay (id, out overlay) || overlay == null) continue;
                try {
                    kRebuildContent.Invoke (overlay, new object[] { true });
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
        }

        static readonly string[] kPanelIds = {
            "mkt-scene-preview-toolbar", "mkt-scene-layers", "mkt-scene-transform", "mkt-scene-picker", "mkt-scene-timeline",
            "mkt-scene-animbank", "mkt-scene-curves", "mkt-scene-stacker", "mkt-scene-properties", "mkt-scene-rig-values",
        };

        void RestoreTools ()
        {
            if (!toolsHidden_) return;
            Tools.hidden = false;
            toolsHidden_ = false;
        }
    }

}
