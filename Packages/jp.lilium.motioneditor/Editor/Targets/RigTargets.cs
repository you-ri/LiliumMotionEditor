using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 編集用リグのつかむ対象を作る。FK は骨ごと、IK は組ごと、ゲームの Rig のターゲットは 1 つずつ
    /// </summary>
    public static class RigTargets
    {
        public static List<PoseTarget> Create (PreviewStage stage)
        {
            List<PoseTarget> targets = Create (stage != null ? stage.editingRig : null, stage != null ? stage.editingRigSolver : null);
            // 全身 IK の点（S25。リグの定義が全身 IK のときだけ）
            if (stage != null && stage.fullBody != null) targets.AddRange (BodyPointTarget.Create (stage.editingRig, stage.fullBody));
            // 物の持ち替え（キャラの設定の parentSwitches）の物。表示モデルの上でつかんで、握りを動かす
            if (stage != null) targets.AddRange (ParentSwitchTarget.Create (stage));
            return targets;
        }

        public static List<PoseTarget> Create (EditingRig rig, EditingRigSolver solver)
        {
            List<PoseTarget> targets = new List<PoseTarget> ();
            if (rig == null || rig.root == null || solver == null) return targets;

            BoneShapes shapes = new BoneShapes (rig);
            foreach (RigBinding.Fk fk in rig.binding.fk) {
                if (!fk.enabled) continue;
                Transform control = rig.FindControl (fk.path);
                Transform bone = rig.GetEditingBone (fk.bone);
                if (control == null || bone == null) continue;
                targets.Add (new FkTarget (rig, solver, shapes, control, bone, fk.bone, fk.control.bone.ToString (), fk.control.position));
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                if (!extra.enabled) continue;
                Transform control = rig.FindControl (extra.path);
                Transform bone = rig.GetEditingBone (extra.bone);
                if (control == null || bone == null) continue;
                targets.Add (new FkTarget (rig, solver, shapes, control, bone, extra.bone, extra.control.name, extra.control.position));
            }
            foreach (RigBinding.Ik ik in rig.binding.ik) {
                if (!ik.enabled) continue;
                Transform chain = rig.FindControl (ik.path);
                if (chain == null) continue;
                targets.Add (new IkTarget (rig, solver, shapes, chain, ik));
            }
            if (rig.rigProxies != null) {
                foreach (RigProxies.Source source in rig.rigProxies.sources) {
                    targets.Add (new RigSourceTarget (rig, source));
                }
            }
            return targets;
        }
    }

    /// <summary>
    /// 骨の見た目（ハンドルの形）。骨の向きは子の骨へ向かう線で、骨の軸（ローカルの回転）とは別に持つ。
    /// 基準姿勢（リグを作った直後）で決める
    /// </summary>
    sealed class BoneShapes
    {
        const float kMinLength = 0.02f;

        struct Shape
        {
            /// <summary>骨の回転に後ろから掛けると、+Z が子へ向く</summary>
            public Quaternion adjust;
            public float length;
        }

        readonly Dictionary<Transform, Shape> shapes_ = new Dictionary<Transform, Shape> ();
        readonly EditingRig rig_;
        readonly HashSet<Transform> controlled_ = new HashSet<Transform> ();

        public BoneShapes (EditingRig rig)
        {
            rig_ = rig;
            foreach (RigBinding.Fk fk in rig.binding.fk) {
                Transform bone = fk.enabled ? rig.GetEditingBone (fk.bone) : null;
                if (bone != null) controlled_.Add (bone);
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                Transform bone = extra.enabled ? rig.GetEditingBone (extra.bone) : null;
                if (bone != null) controlled_.Add (bone);
            }
        }

        public Quaternion GetRotation (Transform bone)
        {
            return bone.rotation * Get (bone).adjust;
        }

        /// <summary>
        /// 骨がワールドで worldRotation を向いているときの見た目の回転（表示モデルの骨の上に出すとき）
        /// </summary>
        public Quaternion GetRotation (Transform bone, Quaternion worldRotation)
        {
            return worldRotation * Get (bone).adjust;
        }

        /// <summary>
        /// 見た目の回転から骨の回転へ戻す
        /// </summary>
        public Quaternion ToBoneRotation (Transform bone, Quaternion shapeRotation)
        {
            return shapeRotation * Quaternion.Inverse (Get (bone).adjust);
        }

        public float GetLength (Transform bone)
        {
            return Get (bone).length;
        }

        Shape Get (Transform bone)
        {
            Shape shape;
            if (!shapes_.TryGetValue (bone, out shape)) {
                shape = Compute (bone);
                shapes_.Add (bone, shape);
            }
            return shape;
        }

        /// <summary>
        /// 子の骨（コントロールのある骨のうち一番近いもの）へ向ける。子が複数なら、親から続く向きに一番近いもの。
        /// 子が無い（手先・頭など）ときは、親から伸びてきた向きに親の半分の長さ
        /// </summary>
        Shape Compute (Transform bone)
        {
            Transform root = rig_.root.transform;
            Vector3 position = RestWorld (bone);
            Transform parent = NearestControlledAncestor (bone);
            Vector3 incoming = parent != null ? position - RestWorld (parent) : root.up;

            List<Transform> children = new List<Transform> ();
            CollectControlledChildren (bone, children);
            Vector3 direction = Vector3.zero;
            float length = 0;
            float best = float.MinValue;
            foreach (Transform child in children) {
                Vector3 toChild = RestWorld (child) - position;
                if (toChild.sqrMagnitude < 1e-8f) continue;
                float score = Vector3.Dot (toChild.normalized, incoming.normalized);
                if (score <= best) continue;
                best = score;
                direction = toChild.normalized;
                length = toChild.magnitude;
            }
            if (direction == Vector3.zero) {
                direction = incoming.sqrMagnitude > 1e-8f ? incoming.normalized : root.up;
                length = parent != null ? incoming.magnitude * 0.5f : 0.1f;
            }
            length = Mathf.Max (kMinLength, length);

            Vector3 up = Mathf.Abs (Vector3.Dot (direction, root.up)) > 0.9f
                ? root.TransformDirection (rig_.bodyForward)
                : root.up;
            Quaternion restRotation = root.rotation * rig_.GetRestRootRotation (bone);
            return new Shape {
                adjust = Quaternion.Inverse (restRotation) * Quaternion.LookRotation (direction, up),
                length = length,
            };
        }

        Vector3 RestWorld (Transform bone)
        {
            return rig_.root.transform.TransformPoint (rig_.GetRestRootPosition (bone));
        }

        Transform NearestControlledAncestor (Transform bone)
        {
            for (Transform t = bone.parent; t != null && t != rig_.root.transform; t = t.parent) {
                if (controlled_.Contains (t)) return t;
            }
            return null;
        }

        void CollectControlledChildren (Transform bone, List<Transform> result)
        {
            foreach (Transform child in bone) {
                if (controlled_.Contains (child)) result.Add (child);
                else CollectControlledChildren (child, result);
            }
        }
    }

    /// <summary>
    /// 骨を直接回す（FK）。値は親から見た基準姿勢からの差で、コントロール（Controls/FK/骨）に持つ。
    /// ハンドルは編集用の骨の上に出し、回したら骨から値を捉え直す
    /// </summary>
    sealed class FkTarget : PoseTarget
    {
        readonly EditingRig rig_;
        readonly EditingRigSolver solver_;
        readonly BoneShapes shapes_;
        readonly Transform control_;
        readonly Transform bone_;
        readonly Transform display_;
        readonly string label_;
        readonly bool position_;
        readonly int jointHash_;
        readonly int boneHash_;

        public FkTarget (EditingRig rig, EditingRigSolver solver, BoneShapes shapes, Transform control, Transform bone, Transform display, string label, bool position)
        {
            rig_ = rig;
            solver_ = solver;
            shapes_ = shapes;
            control_ = control;
            bone_ = bone;
            display_ = display;
            label_ = label;
            position_ = position;
            jointHash_ = ("MktFkJoint" + control.GetEntityId ()).GetHashCode ();
            boneHash_ = ("MktFkBone" + control.GetEntityId ()).GetHashCode ();
        }

        public override GameObject gameObject
        {
            get { return control_ != null ? control_.gameObject : null; }
        }

        public override string label
        {
            get { return label_; }
        }

        /// <summary>
        /// 一覧は表示モデルの骨の階層で並べる
        /// </summary>
        public override Transform anchor
        {
            get { return display_; }
        }

        public override Vector3 framePosition
        {
            get { return bone_ != null ? bone_.position : Vector3.zero; }
        }

        public override TransformChannels GetSpinChannels (bool pole)
        {
            return position_ ? TransformChannels.Position | TransformChannels.Rotation : TransformChannels.Rotation;
        }

        public override string GetSpinLabel (bool pole)
        {
            return label_ + " (" + bone_.name + ")";
        }

        public override void SpinMove (Vector3 delta, bool pole)
        {
            if (!position_) return;
            BeginEdit ();
            bone_.localPosition += delta;
            solver_.CaptureFk (bone_);
        }

        public override void SpinRotateLocal (Quaternion delta, bool pole)
        {
            BeginEdit ();
            bone_.localRotation = bone_.localRotation * delta;
            solver_.CaptureFk (bone_);
        }

        public override void SpinRotateWorld (Quaternion delta, bool pole)
        {
            BeginEdit ();
            bone_.rotation = delta * bone_.rotation;
            solver_.CaptureFk (bone_);
        }

        /// <summary>
        /// この骨を IK が動かしているなら、今の見た目のまま FK へ切り替える
        /// </summary>
        void BeginEdit ()
        {
            string chain = solver_.FindIkChainOf (bone_);
            if (chain != null) solver_.SwitchToFk (chain);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (bone_ == null) return;

            if (position_) {
                Vector3 position = PoseHandleUtility.DoJointHandle (jointHash_, control_, bone_.position, shapes_.GetRotation (bone_));
                if (position != bone_.position) {
                    BeginEdit ();
                    bone_.position = position;
                    solver_.CaptureFk (bone_);
                    GUI.changed = true;
                }
            }

            Quaternion shape = shapes_.GetRotation (bone_);
            Quaternion rotated = PoseHandleUtility.DoBoneHandle (boneHash_, control_, bone_.position, shape, shapes_.GetLength (bone_));
            if (rotated != shape) {
                BeginEdit ();
                bone_.rotation = shapes_.ToBoneRotation (bone_, rotated);
                solver_.CaptureFk (bone_);
                GUI.changed = true;
            }
        }

        public override void WriteKeys (CurveWriter writer)
        {
            writer.Transform (control_, position_ ? TransformChannels.Position | TransformChannels.Rotation : TransformChannels.Rotation);
        }

        public override bool supportsDisplayHandles
        {
            get { return display_ != null; }
        }

        public override Transform displayGrabBone
        {
            get { return display_; }
        }

        /// <summary>
        /// 表示の骨を動かした分を、編集用の骨へ同じだけ（回転は骨の軸まわりの差、位置はワールドの差）写す。
        /// 間の段が骨ごとの値をそのまま運ぶ（Rig の重み 0・Humanoid の往復）なら、前進し直すと表示の骨が狙いに届く
        /// </summary>
        public override bool OnDisplayHandleGUI (bool selected, out DisplayGoal goal)
        {
            goal = new DisplayGoal { bone = display_, rotation = display_ != null ? display_.rotation : Quaternion.identity, position = display_ != null ? display_.position : Vector3.zero };
            if (bone_ == null || display_ == null) return false;
            goal.shapeAdjust = Quaternion.Inverse (display_.rotation) * shapes_.GetRotation (bone_, display_.rotation);
            goal.length = shapes_.GetLength (bone_);
            bool changed = false;

            if (position_) {
                Vector3 position = PoseHandleUtility.DoJointHandle (jointHash_, control_, display_.position, shapes_.GetRotation (bone_, display_.rotation));
                if (position != display_.position) {
                    BeginEdit ();
                    bone_.position += position - display_.position;
                    solver_.CaptureFk (bone_);
                    goal.position = position;
                    goal.hasPosition = true;
                    changed = true;
                }
            }

            Quaternion shape = shapes_.GetRotation (bone_, display_.rotation);
            Quaternion rotated = PoseHandleUtility.DoBoneHandle (boneHash_, control_, display_.position, shape, shapes_.GetLength (bone_));
            if (rotated != shape) {
                Quaternion target = shapes_.ToBoneRotation (bone_, rotated);
                Quaternion delta = Quaternion.Inverse (display_.rotation) * target;
                BeginEdit ();
                bone_.localRotation = bone_.localRotation * delta;
                solver_.CaptureFk (bone_);
                goal.rotation = target;
                goal.hasRotation = true;
                changed = true;
            }
            if (changed) GUI.changed = true;
            return changed;
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (AnimationUtility.CalculateTransformPath (control_, rig_.root.transform));
        }

        /// <summary>
        /// メッシュのクリックは表示モデルの骨で返ってくる
        /// </summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
            if (display_ != null) result.Add (display_);
        }

        public override void ResetPose ()
        {
            control_.localRotation = Quaternion.identity;
            if (position_) control_.localPosition = Vector3.zero;
        }

        float humanScale
        {
            get { return rig_.humanScale > 1e-6f ? rig_.humanScale : 1; }
        }

        public Transform bone
        {
            get { return bone_; }
        }

        public override bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            position = control_.localPosition * humanScale;
            rotation = control_.localRotation;
            return true;
        }

        public override void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            BeginEdit ();
            if ((channels & TransformChannels.Rotation) != 0) control_.localRotation = Quaternion.Normalize (rotation);
            if (position_ && (channels & TransformChannels.Position) != 0) control_.localPosition = position / humanScale;
        }

        public override void ResetValues (bool pole, TransformChannels channels)
        {
            BeginEdit ();
            if ((channels & TransformChannels.Rotation) != 0) control_.localRotation = Quaternion.identity;
            if (position_ && (channels & TransformChannels.Position) != 0) control_.localPosition = Vector3.zero;
        }

        public override string mirrorKey
        {
            get { return label_; }
        }

        public override bool MirrorFrom (PoseTarget source)
        {
            FkTarget fk = source as FkTarget;
            return fk != null && solver_.MirrorFk (fk.bone_, bone_);
        }
    }

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

            // 選んでいる間は手首・足首にギズモ（Transform パネルのモードで移動 / 回転。骨の形のハンドルは出さない）
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
            get { return rig_.humanScale > 1e-6f ? rig_.humanScale : 1; }
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

        /// <summary>足の転がしを持つ脚か（Transform パネルに roll / bank / twist の行を出す）</summary>
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

    /// <summary>
    /// ゲームの Rig が読むターゲット（上半身の傾きの元・足の IK の目標など）。値は代理（Controls/Game/...）に持ち、
    /// ハンドルは編集用の体の中の複製の上に出す。動かしたら複製から代理へ値を戻す
    /// </summary>
    sealed class RigSourceTarget : PoseTarget
    {
        static readonly Color kColor = new Color (1, 0.8f, 0, 0.25f);

        readonly EditingRig rig_;
        readonly RigProxies.Source source_;
        readonly int moveHash_;
        readonly int rotateHash_;

        public RigSourceTarget (EditingRig rig, RigProxies.Source source)
        {
            rig_ = rig;
            source_ = source;
            moveHash_ = ("MktRigSourceMove" + source.proxy.GetEntityId ()).GetHashCode ();
            rotateHash_ = ("MktRigSourceRotate" + source.proxy.GetEntityId ()).GetHashCode ();
        }

        public override GameObject gameObject
        {
            get { return source_.proxy != null ? source_.proxy.gameObject : null; }
        }

        public override string label
        {
            get { return "Rig " + source_.label; }
        }

        public override Transform anchor
        {
            get { return source_.display; }
        }

        public override Vector3 framePosition
        {
            get { return source_.editing != null ? source_.editing.position : Vector3.zero; }
        }

        float handleLength
        {
            get { return 0.15f * Mathf.Max (0.1f, rig_.humanScale); }
        }

        public override TransformChannels GetSpinChannels (bool pole)
        {
            return TransformChannels.Position | TransformChannels.Rotation;
        }

        public override void SpinMove (Vector3 delta, bool pole)
        {
            source_.editing.localPosition += delta;
            rig_.rigProxies.Capture (source_);
        }

        public override void SpinRotateLocal (Quaternion delta, bool pole)
        {
            source_.editing.localRotation = source_.editing.localRotation * delta;
            rig_.rigProxies.Capture (source_);
        }

        public override void SpinRotateWorld (Quaternion delta, bool pole)
        {
            source_.editing.rotation = delta * source_.editing.rotation;
            rig_.rigProxies.Capture (source_);
        }

        public override void OnHandleGUI (bool selected)
        {
            Transform editing = source_.editing;
            if (editing == null) return;

            PoseHandleUtility.color = kColor;
            Vector3 position = editing.position;
            Quaternion rotation = editing.rotation;
            Vector3 moved = PoseHandleUtility.DoJointHandle (moveHash_, source_.proxy, position, rotation);
            if (moved != position) {
                editing.position = moved;
                rig_.rigProxies.Capture (source_);
                GUI.changed = true;
            }

            // 向きの棒は選んでいるときだけ出す（胸などの骨のハンドルと重なるため）
            if (!selected) {
                PoseHandleUtility.color = PoseHandleUtility.StandardColor;
                return;
            }
            Quaternion rotated = PoseHandleUtility.DoBoneHandle (rotateHash_, source_.proxy, editing.position, rotation, handleLength);
            if (rotated != rotation) {
                editing.rotation = rotated;
                rig_.rigProxies.Capture (source_);
                GUI.changed = true;
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
        }

        public override void WriteKeys (CurveWriter writer)
        {
            writer.Transform (source_.proxy, TransformChannels.Position | TransformChannels.Rotation);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (AnimationUtility.CalculateTransformPath (source_.proxy, rig_.root.transform));
        }

        /// <summary>
        /// メッシュを持たないので、クリックでは選ばない（Picker と表示域のハンドルで選ぶ）
        /// </summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
        }

        public override void ResetPose ()
        {
            rig_.rigProxies.ResetSource (source_);
        }

        float humanScale
        {
            get { return rig_.humanScale > 1e-6f ? rig_.humanScale : 1; }
        }

        public override bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            source_.proxy.GetLocalPositionAndRotation (out position, out rotation);
            position *= humanScale;
            return true;
        }

        public override void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            if ((channels & TransformChannels.Position) != 0) source_.proxy.localPosition = position / humanScale;
            if ((channels & TransformChannels.Rotation) != 0) source_.proxy.localRotation = Quaternion.Normalize (rotation);
        }

        public override void ResetValues (bool pole, TransformChannels channels)
        {
            if ((channels & TransformChannels.Position) != 0) source_.proxy.localPosition = source_.defaultPosition;
            if ((channels & TransformChannels.Rotation) != 0) source_.proxy.localRotation = source_.defaultRotation;
        }
    }

}
