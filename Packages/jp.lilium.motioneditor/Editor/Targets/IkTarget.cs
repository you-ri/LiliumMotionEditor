using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 手足の IK。目標（手・足の位置と向き）とヒント（肘・膝の向き）を動かす。
    /// 目標のコントロールは正規化した値を持つので、ハンドルはワールドの位置を解決から受け取って出す
    /// </summary>
    sealed class IkTarget : PoseTarget
    {
        static readonly Color kIkColor = new Color (1, 0, 0, 0.2f);
        static readonly Color kFkColor = new Color (0, 0, 1, 0.2f);
        /// <summary>End のリングの大きさ（ハンドルの大きさの何倍か）。目標のジョイントを囲む</summary>
        const float kRingScale = 2.5f;

        readonly EditingRig rig_;
        readonly EditingRigSolver solver_;
        readonly BoneShapes shapes_;
        readonly Transform chain_;
        readonly string name_;
        readonly Transform root_;
        readonly Transform mid_;
        readonly Transform tip_;
        readonly Transform displayRoot_;
        readonly Transform displayMid_;
        readonly Transform displayTip_;
        readonly int goalMoveHash_;
        readonly int hintHash_;
        readonly int ringHash_;
        readonly int capsuleHash_;
        readonly Vector3[] pivots_ = new Vector3[ReverseFoot.kPivotCount];

        public IkTarget (EditingRig rig, EditingRigSolver solver, BoneShapes shapes, Transform chain, RigBinding.Ik ik)
        {
            rig_ = rig;
            solver_ = solver;
            shapes_ = shapes;
            chain_ = chain;
            name_ = ik.chain.name;
            displayRoot_ = ik.root;
            displayMid_ = ik.mid;
            displayTip_ = ik.tip;
            solver.TryGetIkBones (name_, out root_, out mid_, out tip_);
            goalMoveHash_ = ("MktIkGoalMove" + chain.GetEntityId ()).GetHashCode ();
            hintHash_ = ("MktIkHint" + chain.GetEntityId ()).GetHashCode ();
            ringHash_ = ("MktIkRing" + chain.GetEntityId ()).GetHashCode ();
            capsuleHash_ = ("MktIkFootCapsule" + chain.GetEntityId ()).GetHashCode ();
        }

        /// <summary>足の転がし（Reverse Foot）を持つ脚か。持つなら、目標のジョイントと End のリングの代わりに足元のカプセルを出す</summary>
        bool reverseFoot
        {
            get { return solver_.HasReverseFoot (name_); }
        }

        /// <summary>
        /// 足元のカプセル（足の転がしのコントローラー）。足首の枠（転がす前）から踵〜爪先・足の幅で置く。
        /// 押すと選び、つかむと足元の面の上を動かし、右ボタンで IK と FK を切り替える
        /// </summary>
        PoseHandles.CapsuleResult DoFootCapsule (Vector3 anklePosition, Quaternion ankleRotation, out Vector3 delta)
        {
            delta = Vector3.zero;
            Vector3 up;
            Vector3 forward;
            if (!solver_.TryGetFootPivotsWorld (name_, anklePosition, ankleRotation, pivots_, out up, out forward)) return PoseHandles.CapsuleResult.None;
            Vector3 heel = pivots_[(int)ReverseFoot.Pivot.Heel];
            Vector3 toe = pivots_[(int)ReverseFoot.Pivot.ToeTip];
            Vector3 across = Vector3.ProjectOnPlane (pivots_[(int)ReverseFoot.Pivot.RightEdge] - pivots_[(int)ReverseFoot.Pivot.LeftEdge], up);
            // 爪先は踵と同じ高さ（足元の面）にそろえる
            toe -= up * Vector3.Dot (toe - heel, up);
            return PoseHandleUtility.DoCapsuleHandle (capsuleHash_, chain_, heel, toe, up, across.magnitude * 0.5f,
                ikActive ? PoseHandleUtility.IkRingOnColor : PoseHandleUtility.IkRingOffColor, out delta);
        }

        void ToggleIkFk ()
        {
            if (ikActive) solver_.SwitchToFk (name_);
            else solver_.SwitchToIk (name_);
        }

        /// <summary>End のリング（手首・足首を囲む腕輪）。左ボタンで選び、右ボタンの結果は呼んだ側で IK / FK を切り替える</summary>
        PoseHandles.CapsuleResult DoRing (Vector3 position, Quaternion shape)
        {
            return PoseHandleUtility.DoRingHandle (ringHash_, chain_, position, shape, EditorHost.current.handleSize * kRingScale,
                ikActive ? PoseHandleUtility.IkRingOnColor : PoseHandleUtility.IkRingOffColor);
        }

        /// <summary>
        /// 手首・足首のギズモを出すか。この IK を選んでいて、肘・膝の向き（ヒント）をつかんでいないとき（ヒントは自分の移動のギズモを出す）
        /// </summary>
        bool ShowGizmo (bool selected)
        {
            return selected && PoseHandleUtility.focusControl != hintHash_;
        }

        public override GameObject gameObject
        {
            get { return chain_ != null ? chain_.gameObject : null; }
        }

        public override string label
        {
            get { return "IK " + name_; }
        }

        public override Transform anchor
        {
            get { return displayRoot_; }
        }

        public override Vector3 framePosition
        {
            get {
                Vector3 position;
                Quaternion rotation;
                GetGoal (out position, out rotation);
                return position;
            }
        }

        public override bool hasPole
        {
            get { return true; }
        }

        bool ikActive
        {
            get { return solver_.GetIkWeight (name_) > 0; }
        }

        /// <summary>
        /// 目標の位置と向き。IK が切れている間は、手・足の今の位置に出す（つかむとそこから IK が始まる）
        /// </summary>
        void GetGoal (out Vector3 position, out Quaternion rotation)
        {
            if (ikActive && solver_.TryGetIkTargetWorld (name_, out position, out rotation)) return;
            position = tip_.position;
            rotation = tip_.rotation;
        }

        Vector3 GetHint ()
        {
            return ikActive ? solver_.GetIkHintWorld (name_) : solver_.GetPoseHintWorld (name_);
        }

        public override TransformChannels GetSpinChannels (bool pole)
        {
            return pole ? TransformChannels.Position : TransformChannels.Position | TransformChannels.Rotation;
        }

        public override string GetSpinLabel (bool pole)
        {
            return label + (pole ? " Hint" : " Goal");
        }

        public override void SpinMove (Vector3 delta, bool pole)
        {
            solver_.SwitchToIk (name_);
            Vector3 world = rig_.root.transform.TransformVector (delta);
            if (pole) {
                solver_.SetIkHintWorld (name_, solver_.GetIkHintWorld (name_) + world);
                return;
            }
            Vector3 position;
            Quaternion rotation;
            solver_.TryGetIkTargetWorld (name_, out position, out rotation);
            solver_.SetIkTargetWorld (name_, position + world, rotation);
        }

        public override void SpinRotateLocal (Quaternion delta, bool pole)
        {
            if (pole) return;
            solver_.SwitchToIk (name_);
            Vector3 position;
            Quaternion rotation;
            solver_.TryGetIkTargetWorld (name_, out position, out rotation);
            solver_.SetIkTargetWorld (name_, position, rotation * delta);
        }

        public override void SpinRotateWorld (Quaternion delta, bool pole)
        {
            if (pole) return;
            solver_.SwitchToIk (name_);
            Vector3 position;
            Quaternion rotation;
            solver_.TryGetIkTargetWorld (name_, out position, out rotation);
            solver_.SetIkTargetWorld (name_, position, delta * rotation);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (tip_ == null) return;

            PoseHandleUtility.color = ikActive ? kIkColor : kFkColor;
            Vector3 position;
            Quaternion rotation;
            GetGoal (out position, out rotation);
            bool foot = reverseFoot;
            // ギズモの軸は手・足の骨の見た目の軸（FK の骨の回転ギズモとそろえる）
            Quaternion shape = rotation * Quaternion.Inverse (tip_.rotation) * shapes_.GetRotation (tip_);

            if (foot) {
                // 足元のカプセル。つかんで動かす・右ボタンで IK / FK
                Vector3 delta;
                PoseHandles.CapsuleResult result = DoFootCapsule (position, rotation, out delta);
                if (result == PoseHandles.CapsuleResult.Dragged) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, position + delta, rotation);
                    position += delta;
                    GUI.changed = true;
                }
                else if (result == PoseHandles.CapsuleResult.ContextClicked) {
                    ToggleIkFk ();
                    GUI.changed = true;
                }
            }
            else {
                // 目標のジョイント（つかんで動かす）と End のリング。
                // リングは手首を囲む腕輪の向き（輪の軸は骨の向き。骨の見た目の回転は +Z が子へ向いていて、輪の軸も +Z）。
                // IK の入（赤）/ 切（灰）を色で出す。左ボタンで選び、右ボタンで IK と FK を切り替える（足元のカプセルと同じ）。
                // 切り替えは今の見た目を相手側へ写してから行うので姿勢は飛ばない
                // （GUI.changed を立てるので、窓がこのフレームにキーを打つ。打たないと次にスクラブした時点でクリップの重みに戻る）
                Vector3 moved = PoseHandleUtility.DoJointHandle (goalMoveHash_, chain_, position, rotation, false);
                if (moved != position) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, moved, rotation);
                    position = moved;
                    GUI.changed = true;
                }
                if (DoRing (position, shape) == PoseHandles.CapsuleResult.ContextClicked) {
                    ToggleIkFk ();
                    GUI.changed = true;
                }
            }

            // 選んでいる間は手首・足首にギズモ（Controller パネルのモードで移動 / 回転。骨の形のハンドルは出さない）
            if (ShowGizmo (selected)) {
                Vector3 gizmoPosition = position;
                Quaternion gizmoRotation = rotation;
                if (PoseHandleUtility.DoGoalGizmo (goalMoveHash_, ref gizmoPosition, ref gizmoRotation, shape)) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, gizmoPosition, gizmoRotation);
                    GUI.changed = true;
                }
            }

            if (selected) {
                Vector3 hint = GetHint ();
                Vector3 hintMoved = PoseHandleUtility.DoJointHandle (hintHash_, chain_, hint, Quaternion.identity);
                if (hintMoved != hint) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkHintWorld (name_, hintMoved);
                    GUI.changed = true;
                }
                if (Event.current.type == EventType.Repaint) {
                    Handles.color = PoseHandles.Fade (new Color (1, 1, 1, 0.4f));
                    Handles.DrawDottedLine (mid_.position, hint, 3);
                }
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
        }

        public override bool supportsDisplayHandles
        {
            get { return displayTip_ != null && displayMid_ != null; }
        }

        public override Transform displayGrabBone
        {
            get { return displayTip_; }
        }

        /// <summary>
        /// 表示モデルの手・足の上に目標のハンドルを出す（✋ = Display Pose）。動かした差（位置はワールドの差、向きはワールドで前から掛ける差）を
        /// 編集用の IK の目標へ同じだけ写す。肘・膝の向き（ヒント）も、表示の肘・膝との差の分だけずらして出す
        /// </summary>
        public override bool OnDisplayHandleGUI (bool selected, out DisplayGoal goal)
        {
            goal = new DisplayGoal { bone = displayTip_ };
            if (tip_ == null || displayTip_ == null || displayMid_ == null) return false;
            goal.position = displayTip_.position;
            goal.rotation = displayTip_.rotation;
            goal.shapeAdjust = Quaternion.Inverse (displayTip_.rotation) * shapes_.GetRotation (tip_, displayTip_.rotation);
            goal.length = shapes_.GetLength (tip_);
            bool changed = false;

            PoseHandleUtility.color = ikActive ? kIkColor : kFkColor;
            Vector3 editingPosition;
            Quaternion editingRotation;
            GetGoal (out editingPosition, out editingRotation);
            Vector3 position = displayTip_.position;
            Quaternion rotation = displayTip_.rotation;
            bool foot = reverseFoot;
            Quaternion shape = shapes_.GetRotation (tip_, rotation);

            if (foot) {
                // 足元のカプセルは、転がす前の目標を表示の足までずらした所に出す（転がしても地面に残る）
                Vector3 offset = displayTip_.position - tip_.position;
                Quaternion turn = displayTip_.rotation * Quaternion.Inverse (tip_.rotation);
                Vector3 delta;
                PoseHandles.CapsuleResult result = DoFootCapsule (editingPosition + offset, turn * editingRotation, out delta);
                if (result == PoseHandles.CapsuleResult.Dragged) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, editingPosition + delta, editingRotation);
                    goal.position = position + delta;
                    goal.hasPosition = true;
                    position += delta;
                    changed = true;
                }
                else if (result == PoseHandles.CapsuleResult.ContextClicked) {
                    ToggleIkFk ();
                    changed = true;
                }
            }
            else {
                Vector3 moved = PoseHandleUtility.DoJointHandle (goalMoveHash_, chain_, position, rotation, false);
                if (moved != position) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, editingPosition + (moved - position), editingRotation);
                    goal.position = moved;
                    goal.hasPosition = true;
                    position = moved;
                    changed = true;
                }
                if (DoRing (position, shape) == PoseHandles.CapsuleResult.ContextClicked) {
                    ToggleIkFk ();
                    changed = true;
                }
            }

            if (ShowGizmo (selected)) {
                Vector3 gizmoPosition = position;
                Quaternion gizmoRotation = rotation;
                if (PoseHandleUtility.DoGoalGizmo (goalMoveHash_, ref gizmoPosition, ref gizmoRotation, shape)) {
                    // 表示の手足で動かした差（位置はワールドの差、向きはワールドで前から掛ける差）を、編集用の目標へ同じだけ写す
                    Vector3 current;
                    Quaternion currentRotation;
                    GetGoal (out current, out currentRotation);
                    solver_.SwitchToIk (name_);
                    solver_.SetIkTargetWorld (name_, current + (gizmoPosition - position), gizmoRotation * Quaternion.Inverse (rotation) * currentRotation);
                    if (gizmoPosition != position) {
                        goal.position = gizmoPosition;
                        goal.hasPosition = true;
                    }
                    if (gizmoRotation != rotation) {
                        goal.rotation = gizmoRotation;
                        goal.hasRotation = true;
                    }
                    changed = true;
                }
            }

            if (selected) {
                // ヒントは編集用の体の肘・膝から見た点なので、表示の肘・膝との差の分だけずらして出す
                Vector3 offset = displayMid_.position - mid_.position;
                Vector3 hint = GetHint () + offset;
                Vector3 hintMoved = PoseHandleUtility.DoJointHandle (hintHash_, chain_, hint, Quaternion.identity);
                if (hintMoved != hint) {
                    solver_.SwitchToIk (name_);
                    solver_.SetIkHintWorld (name_, hintMoved - offset);
                    changed = true;
                }
                if (Event.current.type == EventType.Repaint) {
                    Handles.color = PoseHandles.Fade (new Color (1, 1, 1, 0.4f));
                    Handles.DrawDottedLine (displayMid_.position, hint, 3);
                }
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
            if (changed) GUI.changed = true;
            return changed;
        }

        public override void WriteKeys (CurveWriter writer)
        {
            writer.Transform (rig_.FindControl (RigPaths.IkTarget (name_)), TransformChannels.Position | TransformChannels.Rotation);
            writer.Transform (rig_.FindControl (RigPaths.IkHint (name_)), TransformChannels.Position);
            writer.Float (chain_, typeof (IkControl), IkControl.kIkWeightProperty, solver_.GetIkWeight (name_));
            Vector3 angles;
            if (solver_.TryGetFootAngles (name_, out angles)) {
                writer.Float (chain_, typeof (IkControl), IkControl.kRollProperty, angles.x);
                writer.Float (chain_, typeof (IkControl), IkControl.kBankProperty, angles.y);
                writer.Float (chain_, typeof (IkControl), IkControl.kTwistProperty, angles.z);
            }
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            Transform root = rig_.root.transform;
            result.Add (AnimationUtility.CalculateTransformPath (chain_, root));
            result.Add (RigPaths.IkTarget (name_));
            result.Add (RigPaths.IkHint (name_));
        }

        /// <summary>
        /// メッシュのクリックでは選ばない（腕・脚の骨は FK の対象が受け持つ）
        /// </summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
        }

        float humanScale
        {
            get { return rig_.humanScale; }
        }

        Transform targetControl
        {
            get { return rig_.FindControl (RigPaths.IkTarget (name_)); }
        }

        Transform hintControl
        {
            get { return rig_.FindControl (RigPaths.IkHint (name_)); }
        }

        /// <summary>
        /// IK が切れている間は、今の見た目の手足の先と曲がりを出す（入れると、その値で IK が始まる）
        /// </summary>
        public override bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            if (pole) {
                Vector3 hint = ikActive ? hintControl.localPosition : root.InverseTransformDirection (solver_.GetPoseHintWorld (name_) - mid_.position).normalized;
                position = hint;
                rotation = Quaternion.identity;
                return true;
            }
            Vector3 world;
            GetGoal (out world, out rotation);
            position = root.InverseTransformPoint (world);
            rotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation) * rotation);
            return true;
        }

        public override void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            solver_.SwitchToIk (name_);
            if (pole) {
                if ((channels & TransformChannels.Position) != 0 && position.sqrMagnitude > 1e-12f) hintControl.localPosition = position.normalized;
                return;
            }
            if ((channels & TransformChannels.Position) != 0) targetControl.localPosition = position / humanScale;
            if ((channels & TransformChannels.Rotation) != 0) targetControl.localRotation = Quaternion.Normalize (rotation);
        }

        public override void ResetValues (bool pole, TransformChannels channels)
        {
            Vector3 target;
            Quaternion rotation;
            Vector3 hint;
            if (!solver_.TryGetIkRestValues (name_, out target, out rotation, out hint)) return;
            solver_.SwitchToIk (name_);
            if (pole) {
                if ((channels & TransformChannels.Position) != 0) hintControl.localPosition = hint;
                return;
            }
            if ((channels & TransformChannels.Position) != 0) targetControl.localPosition = target;
            if ((channels & TransformChannels.Rotation) != 0) targetControl.localRotation = rotation;
        }

        /// <summary>足の転がしを持つ脚か（Controller パネルに roll / bank / twist の行を出す）</summary>
        public bool hasFootAngles
        {
            get { return reverseFoot; }
        }

        /// <summary>足の転がしの角度（度。x = roll・y = bank・z = twist）。FK の間は 0</summary>
        public bool TryGetFootAngles (out Vector3 angles)
        {
            return solver_.TryGetFootAngles (name_, out angles);
        }

        /// <summary>
        /// 足の転がしの角度を入れる。角度は IK の間しか効かない（FK の間は 0 に戻される）ので、IK にしてから入れる
        /// </summary>
        public void SetFootAngles (Vector3 angles)
        {
            solver_.SwitchToIk (name_);
            solver_.SetFootAngles (name_, angles);
        }

        public override string mirrorKey
        {
            get { return name_; }
        }

        public override bool MirrorFrom (PoseTarget source)
        {
            IkTarget ik = source as IkTarget;
            return ik != null && solver_.MirrorIk (ik.name_, name_);
        }
    }

}
