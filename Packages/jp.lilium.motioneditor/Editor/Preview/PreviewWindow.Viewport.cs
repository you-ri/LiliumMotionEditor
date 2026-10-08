using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 表示域の操作。視点（Alt+ドラッグ・中ボタン・ホイール）、枠で点を選ぶ、prefab・クリップのドラッグ＆ドロップ、
    /// 表示域の隅の情報、視点のプリセット（横・上・正面）と選んだ物への注視
    /// </summary>
    public partial class PreviewWindow
    {
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
                        SelectByMeshClick (ev.mousePosition, rect, ev.modifiers);
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
                            // ギズモを出すハンドルの印は、選ぶ前に外す。メッシュで骨の FK を選んだときは、SelectPicked がその骨に出し直す
                            // （選んだ後に外すと、線を押して選んだときと違って回転のギズモが出ない）
                            PoseHandleUtility.focusControl = 0;
                            if (boxMoved_) SelectPointsInBox (rect, ev.modifiers);
                            else SelectByMeshClick (boxStart_, rect, ev.modifiers);
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
                    // ハンドルの上に来たときの色を変える。ハンドルの上でなければ、メッシュの下の物（押したら選ぶ物）を強調する
                    bool hoverChanged = UpdateMeshHover (mouseInView && GUIUtility.hotControl == 0 && HandleUtility.nearestControl == 0, ev.mousePosition, rect);
                    if (mouseInView || hoverChanged) RepaintView ();
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
        /// 枠の中にある全身 IK の点をまとめて選ぶ。Shift なら今の選択に足す。Ctrl（Mac は Command）なら、枠の中の点の子階層の物も選ぶ
        /// </summary>
        void SelectPointsInBox (Rect rect, EventModifiers modifiers)
        {
            bool add = (modifiers & EventModifiers.Shift) != 0;
            bool children = (modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            if (stage_ == null || rect.width < 1 || rect.height < 1) return;
            Rect box = boxRect;
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
            if (children) {
                foreach (GameObject go in selected.ToArray ()) {
                    foreach (PoseTarget child in ChildTargets (targets_, FindTarget (go), IsPickable)) {
                        if (!selected.Contains (child.gameObject)) selected.Add (child.gameObject);
                    }
                }
            }
            SelectObjects (selected);
        }

        /// <summary>引いた枠（表示域の中の座標）</summary>
        Rect boxRect
        {
            get { return Rect.MinMaxRect (Mathf.Min (boxStart_.x, boxEnd_.x), Mathf.Min (boxStart_.y, boxEnd_.y), Mathf.Max (boxStart_.x, boxEnd_.x), Mathf.Max (boxStart_.y, boxEnd_.y)); }
        }

        /// <summary>引いている枠（表示域の中の座標。引いていなければ null）</summary>
        Rect? draggingBox
        {
            get {
                if (!boxSelecting_ || !boxMoved_) return null;
                return boxRect;
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
                text = Tr ("PREVIEW_WINDOW_DROP_PREFAB");
            }
            else {
                text = prefab_.name;
                if (editingClip_ != null) {
                    text += "   " + editingClip_.name + (targetClip != editingClip_ && targetClip != null ? " → " + targetClip.name : "") + "   " + clock_.Describe ();
                    if (clock_.isHeld) text += Tr ("PREVIEW_WINDOW_PLAYBACK_HELD");
                    else if (!clock_.isPlaying && !clock_.onGrid) text += Tr ("PREVIEW_WINDOW_KEY_ON_ROUNDED_FRAME");
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
                    Tr ("PREVIEW_WINDOW_MUTED_CONSTRAINTS", string.Join (Tr ("PREVIEW_WINDOW_LIST_SEPARATOR"), stage_.mutedConstraints)), style);
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
                    warning = Tr ("PREVIEW_WINDOW_HUMANOID_BASE_READ_ONLY");
                    warningType = MessageType.Info;
                }
            }
            else if (clipProblem_ != null) {
                warning = Tr ("PREVIEW_WINDOW_CLIP_NOT_EDITABLE", clipProblem_);
                warningType = MessageType.Error;
            }
            else if (editingClip_ != null && strayCurveCount > 0) {
                warning = Tr ("PREVIEW_WINDOW_STRAY_CURVES", strayCurveCount);
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
    }

}
