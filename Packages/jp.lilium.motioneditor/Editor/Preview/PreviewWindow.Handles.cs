using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 骨・IK・全身 IK の点のハンドル（DoHandles）と、離したときのキー。表示の骨をつかんだときの狙いと残差（狙いの残像・Layers の矢印の色）
    /// </summary>
    public partial class PreviewWindow
    {
        void DoHandles ()
        {
            // 窓が複数開いていると入れ替わるので、描く直前に自分を入れる
            EditorHost.current = this;
            // 全身 IK の点のギズモは Controller パネルのモードに従う（Rot なら回転、それ以外は移動）
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
            // ただし点の無い骨（指など）の FK は、リグ定義の fullBodyHandle で出す。
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
            // 押したときの修飾キーで選び方を変える（Select）。ハンドルが押した出来事を使うと種類が Used に変わるので、先に控える
            handlesDrawing_ = true;
            handlePressed_ = ev.type == EventType.MouseDown;
            handleModifiers_ = ev.modifiers;
            // 点をつかむ間に FK で骨を動かしたら、子の Pin の点を一緒に動かす（動かす前の骨を控える）
            List<FkBefore> fkBefore = pointGrab && ev.type != EventType.Layout && ev.type != EventType.Repaint
                ? CaptureFkBefore (selection_.Select (FindTarget)) : null;
            Dictionary<Transform, FkTarget> fkByBone = pointGrab ? FkTargetsByBone () : null;
            FkTarget linkClicked = null;
            PoseHandleUtility.meshHover = meshHover_;
            EditorGUI.BeginChangeCheck ();
            try {
                foreach (PoseTarget target in targets_) {
                    bool selected = IsSelected (target.gameObject);
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null) {
                        // 点をつなぐ線を押すと、線に当たる骨の FK を選ぶ
                        FkTarget linkFk = null;
                        Transform linkBone = point.linkBone;
                        if (fkByBone != null && linkBone != null) fkByBone.TryGetValue (linkBone, out linkFk);
                        point.linkPickable = linkFk != null && CanEditTarget (linkFk);
                        point.linkSelected = linkFk != null && IsSelected (linkFk.gameObject);
                        point.linkHovered = linkFk != null && meshHover_ != null && linkFk.gameObject == meshHover_;
                        // 点は View の IK で出し入れする。切っている間も選んでいる点は出す（骨を隠しているときと同じく、そのまま動かせるように）
                        if (pointGrab && !displayGrab && (showIk_ || selected)) {
                            PoseHandles.locked = pointsLocked || GetMaskProblem (point) != null;
                            // いくつか選んでいる間は、中心の点にだけギズモを出す（選んだ点の数だけ重なって出る）
                            point.showGizmo = selection_.Count <= 1 || point.gameObject == pivotPoint_;
                            point.inBox = box.HasValue && IsInBox (stage_.camera, viewRect, box.Value, point.framePosition);
                            point.OnHandleGUI (selected);
                            if (point.ConsumeContextClick ()) contextPoint_ = point;
                            if (point.ConsumeLinkClick () && linkFk != null) linkClicked = linkFk;
                            // いくつか選んでいる点の 1 つをつかんで動かした: ほかの選んでいる点も同じだけ動かす
                            Vector3 draggedFrom;
                            Vector3 dragged = point.ConsumeDragDelta (out draggedFrom);
                            if (dragged != Vector3.zero && selected && selection_.Count > 1) {
                                MoveSelectedPoints (targets_, GetSelectedPoints (), point, draggedFrom, dragged);
                            }
                            // ギズモで回した: いくつか選んでいれば選んだ点だけをこの点のまわりで、1 つなら骨を FK で回す（その骨より先の固定している点も一緒に回る）
                            Quaternion turned = point.ConsumeRotateDelta ();
                            // ドラッグの始めの姿勢へ戻し、始めからの合計で回し直す（ねじれを積み上げない。PointTurn）
                            if (turned != Quaternion.identity) {
                                Quaternion total = pointTurn_.Add (stage_, point, turned, SolveAfterManipulate);
                                if (selected && selection_.Count > 1) RotateSelectedPoints (point, total);
                                else RotatePointFk (targets_, point, total);
                                pointTurned = true;
                            }
                        }
                        else if (showIk_) point.DrawPinned ();
                        continue;
                    }
                    // 点の無い骨（指など）は、リグ定義で出すと決めた物だけ出す（隠すと見えず動かせない）
                    FkTarget fk = target as FkTarget;
                    // 線から選んだ骨の FK は出す（回転のギズモで回せる）
                    if (pointGrab && fk != null && !fk.fullBodyHandle && !selected) continue;
                    // 骨を隠している間は、選んでいる物だけ出す（選択したまま見た目を確かめ、そのまま動かせるように）
                    if (!showBones_ && !selected) continue;
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
                handlesDrawing_ = false;
            }
            bool changed = EditorGUI.EndChangeCheck ();
            if (displayGrab && ev.type == EventType.Repaint) DrawAimGhost ();
            if (linkClicked != null) {
                handlesDrawing_ = true;
                try {
                    Select (linkClicked.gameObject);
                }
                finally {
                    handlesDrawing_ = false;
                }
                linkClicked.FocusHandle ();
                RepaintView ();
            }
            // FK で骨を動かした: 子の Pin の点を一緒に動かす（動かした点は、点を動かしたときと同じ流れでキーを打つ）。
            // 点を回したときは除く: 回し方が固定している点も動かしてあり、ドラッグの始めへ戻した分や回した骨の分を
            // 「FK で動かした」と見て、選んでいる骨の先の Pin の点をもう一度ずらしてしまう
            if (changed && !pointTurned) CarryPinnedPoints (fkBefore);

            // 再生中につかんだら止め、離したら続ける
            if (changed && clock_.isPlaying) {
                handleHold_ = true;
                HoldClock ();
            }
            if (GUIUtility.hotControl == 0) pointTurn_.End ();
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
                        ClearPendingPointKeys ();
                        RevertManipulation (before);
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
                    RevertManipulation (before);
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
                        displayGrabStatus_ = Tr ("PREVIEW_WINDOW_DISPLAY_GRAB_REVERTED", goal.bone.name);
                        displayGrabWarning_ = true;
                        SetDisplayResidual (goal, beforeAngle, beforeDistance);
                        RepaintView ();
                        return;
                    }
                    displayGrabWarning_ = angle > kDisplayResidualAngle || distance > kDisplayResidualDistance;
                    displayGrabStatus_ = Tr ("PREVIEW_WINDOW_DISPLAY_GRAB_RESIDUAL", goal.bone.name, angle.ToString ("F2"), (distance * 1000).ToString ("F1"))
                        + (displayGrabWarning_ ? Tr ("PREVIEW_WINDOW_DISPLAY_GRAB_NOT_REACHED") : "");
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
            CommitPose (points);
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
    }

}
