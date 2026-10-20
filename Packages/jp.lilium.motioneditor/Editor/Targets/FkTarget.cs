using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

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
        readonly bool fullBodyHandle_;
        readonly int jointHash_;
        readonly int boneHash_;

        public FkTarget (EditingRig rig, EditingRigSolver solver, BoneShapes shapes, Transform control, Transform bone, Transform display, string label, bool position, bool fullBodyHandle)
        {
            rig_ = rig;
            solver_ = solver;
            shapes_ = shapes;
            control_ = control;
            bone_ = bone;
            display_ = display;
            label_ = label;
            position_ = position;
            fullBodyHandle_ = fullBodyHandle;
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

        public override TransformChannels GetSpinChannels ()
        {
            return position_ ? TransformChannels.Position | TransformChannels.Rotation : TransformChannels.Rotation;
        }

        public override string GetSpinLabel ()
        {
            return label_ + " (" + bone_.name + ")";
        }

        public override void SpinMove (Vector3 delta)
        {
            if (!position_) return;
            bone_.localPosition += delta;
            solver_.CaptureFk (bone_);
        }

        public override void SpinRotateLocal (Quaternion delta)
        {
            bone_.localRotation = bone_.localRotation * delta;
            solver_.CaptureFk (bone_);
        }

        public override void SpinRotateWorld (Quaternion delta)
        {
            bone_.rotation = delta * bone_.rotation;
            solver_.CaptureFk (bone_);
        }

        /// <summary>位置を持つ骨（腰）を delta（ワールド）だけ動かす。位置を持たない骨は動かさない</summary>
        public void MoveWorld (Vector3 delta)
        {
            if (!position_) return;
            bone_.position += delta;
            solver_.CaptureFk (bone_);
        }

        /// <summary>
        /// 骨を from（ワールド）を中心に delta だけ回し、from を to へ重ねる。位置を持つ骨（腰）は付け根もそのとおりに動かす。
        /// 位置を持たない骨は付け根を動かせないので、付け根を中心に回すだけ（SpinRotateWorld と同じ）
        /// </summary>
        public void RotateAroundWorld (Vector3 from, Vector3 to, Quaternion delta)
        {
            if (position_) bone_.position = to + delta * (bone_.position - from);
            bone_.rotation = delta * bone_.rotation;
            solver_.CaptureFk (bone_);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (bone_ == null) return;

            if (position_) {
                Vector3 position = PoseHandleUtility.DoJointHandle (jointHash_, control_, bone_.position, shapes_.GetRotation (bone_));
                if (position != bone_.position) {
                    bone_.position = position;
                    solver_.CaptureFk (bone_);
                    GUI.changed = true;
                }
            }

            Quaternion shape = shapes_.GetRotation (bone_);
            Quaternion rotated = PoseHandleUtility.DoBoneHandle (boneHash_, control_, bone_.position, shape, shapes_.GetLength (bone_));
            if (rotated != shape) {
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
            get { return rig_.humanScale; }
        }

        public Transform bone
        {
            get { return bone_; }
        }

        /// <summary>全身 IK の点をつかむ間も、ハンドルを出すか（リグ定義の fullBodyHandle）</summary>
        public bool fullBodyHandle
        {
            get { return fullBodyHandle_; }
        }

        public override bool TryGetValues (out Vector3 position, out Quaternion rotation)
        {
            position = control_.localPosition * humanScale;
            rotation = control_.localRotation;
            return true;
        }

        public override void SetValues (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            Quaternion current = CurrentValue ();
            if ((channels & TransformChannels.Rotation) != 0) control_.localRotation = Quaternion.Normalize (rotation);
            if (position_ && (channels & TransformChannels.Position) != 0) control_.localPosition = position / humanScale;
            FollowControl (current);
        }

        public override void ResetValues (TransformChannels channels)
        {
            Quaternion current = CurrentValue ();
            if ((channels & TransformChannels.Rotation) != 0) control_.localRotation = Quaternion.identity;
            if (position_ && (channels & TransformChannels.Position) != 0) control_.localPosition = Vector3.zero;
            FollowControl (current);
        }

        /// <summary>今の骨の見た目を FK の値にしたもの（コントロールの値は、全身 IK が骨を動かした分だけ見た目とずれていることがある）</summary>
        Quaternion CurrentValue ()
        {
            Quaternion rotation;
            Vector3 position;
            return solver_.TryGetCurrentFkValue (bone_, out rotation, out position) ? rotation : control_.localRotation;
        }

        /// <summary>
        /// コントロールに入れた値の姿勢へ、骨もすぐ動かす（解き直すのを待たない）。窓が骨の動いた量を見て、全身 IK の固定している点を一緒に動かすため。
        /// 値は「親の今の姿勢から見た、基準姿勢からの差」なので、今の見た目の値 current から入れた値への差を骨に掛ければよい
        /// （向き = ルート × 親の差 × 値 × 基準の向き。位置 = ルートから見た基準の位置 + 値 × 体の大きさ）
        /// </summary>
        void FollowControl (Quaternion current)
        {
            Quaternion rest = rig_.GetRestRootRotation (bone_);
            bone_.rotation = bone_.rotation * Quaternion.Inverse (rest) * Quaternion.Inverse (current) * control_.localRotation * rest;
            if (position_) bone_.position = rig_.root.transform.TransformPoint (rig_.GetRestRootPosition (bone_) + control_.localPosition * humanScale);
        }

        /// <summary>
        /// 回転のギズモを出す（ハンドルを押さずに選んだとき。全身 IK の点をつかむ間に、点をつなぐ線から骨を選んだ）。
        /// ギズモは最後に押したハンドルに出るので、この骨のハンドルを押したことにする
        /// </summary>
        public void FocusHandle ()
        {
            PoseHandleUtility.focusControl = boneHash_;
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

}
