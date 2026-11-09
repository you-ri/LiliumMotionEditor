using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 持ち物の FK（S91）。骨の FK と同じく View の Bones で出す。動かすと付けている骨と体が付いてくる。
    /// キーは物の姿勢（Controls/Object/名前）と、付ける先ごとの付けているか（S104）。離すと姿勢全体のキーを打つ。
    /// 持っている間の物の姿勢は全身 IK の点（S102）で、体の点と同じ状態（Free / Pin / Lock）を持つ。状態は Controller の点の状態のボタンで変え、
    /// 物の姿勢の点（Controls/ObjectPose/名前）の強さと Lock の値をキーに打つ。
    /// 物の上の点（重心・先など）はふつうの全身 IK の点（BodyPointTarget。S91e）
    /// </summary>
    public sealed class HeldObjectTarget : PoseTarget
    {
        static readonly Color kColor = new Color (1, 0.8f, 0, 0.25f);

        readonly HeldObjectRig.Entry entry_;
        readonly EditingRig rig_;
        readonly int moveHash_;
        readonly int rotateHash_;

        HeldObjectTarget (EditingRig rig, HeldObjectRig.Entry entry)
        {
            rig_ = rig;
            entry_ = entry;
            moveHash_ = ("LiliumHeldObjectMove" + entry.name).GetHashCode ();
            rotateHash_ = ("LiliumHeldObjectRotate" + entry.name).GetHashCode ();
        }

        public static List<PoseTarget> Create (PreviewStage stage)
        {
            List<PoseTarget> result = new List<PoseTarget> ();
            if (stage == null || stage.heldObjects == null) return result;
            foreach (HeldObjectRig.Entry entry in stage.heldObjects.entries) result.Add (new HeldObjectTarget (stage.editingRig, entry));
            return result;
        }

        public HeldObjectRig.Entry entry
        {
            get { return entry_; }
        }

        /// <summary>物の姿勢の点の状態（体の点と同じ Free / Pin / Lock）</summary>
        public BodyPointState state
        {
            get { return entry_.poseState; }
        }

        /// <summary>物の姿勢の点の状態を変える（BodyPointTarget.SetState と同じく、Pin / Lock は今の物の姿勢で留める）</summary>
        public void SetState (BodyPointState value)
        {
            if (value != state) entry_.SetPoseState (value);
        }

        public override GameObject gameObject
        {
            get { return entry_.control != null ? entry_.control.gameObject : null; }
        }

        public override string label
        {
            get { return Tr ("HELD_OBJECT_TARGET_LABEL", entry_.name); }
        }

        public override Transform anchor
        {
            get { return entry_.display; }
        }

        public override Vector3 framePosition
        {
            get {
                entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
                return position;
            }
        }

        public override TransformChannels GetSpinChannels ()
        {
            return TransformChannels.Position | TransformChannels.Rotation;
        }

        public override void SpinMove (Vector3 delta)
        {
            entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry_.DragTo (position + rig_.root.transform.TransformVector (delta), rotation);
        }

        public override void SpinRotateLocal (Quaternion delta)
        {
            entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry_.DragTo (position, rotation * delta);
        }

        public override void SpinRotateWorld (Quaternion delta)
        {
            entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry_.DragTo (position, delta * rotation);
        }

        public override void OnHandleGUI (bool selected)
        {
            Transform display = entry_.display;
            if (display == null) return;
            // 体の点（BodyPointTarget.OnHandleGUI）と同じ出し方: 丸は状態の色、選んでいることは丸の外の輪、
            // 選んでいる間のギズモは Controller パネルのモード（Rot なら回転、それ以外は移動）。
            // 前は Rig のターゲットと同じ物のハンドルで、回転は向きの棒を押したときだけ出て、Rot にしても回転のギズモが出なかった（11-07 ユーザー報告）。
            // 動かす量は物の姿勢（GetWorldPose）に足す。表示の姿勢をそのまま渡すと、位置だけ動かしても解いた向きとキーの向きの差で向きまで Pin になる
            Vector3 shown = display.position;
            Quaternion axes = display.rotation;
            PoseHandleUtility.color = handleColor;
            try {
                if (selected && Event.current.type == EventType.Repaint) BodyPointTarget.DrawRing (shown, 1, BodyPointTarget.kRingSelectedColor);
                Vector3 moved = PoseHandleUtility.DoJointHandle (moveHash_, entry_.control, shown, axes, false, false);
                if (moved != shown) MoveBy (moved - shown);
                if (!selected) return;
                if (PoseHandleUtility.goalRotate) {
                    if (PoseHandleUtility.DoRotateGizmo (rotateHash_, shown, axes, out Quaternion turned)) {
                        entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
                        entry_.DragTo (position, turned * rotation);
                        GUI.changed = true;
                    }
                }
                else {
                    Vector3 gizmo = PoseHandles.PositionHandle (shown, PoseHandleUtility.GizmoAxes (axes));
                    if (gizmo != shown) MoveBy (gizmo - shown);
                }
            }
            finally {
                PoseHandleUtility.color = PoseHandleUtility.StandardColor;
            }
        }

        /// <summary>物の姿勢を、向きを変えずに delta（ワールド）だけ動かす</summary>
        void MoveBy (Vector3 delta)
        {
            entry_.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry_.DragTo (position + delta, rotation);
            GUI.changed = true;
        }

        /// <summary>
        /// ハンドルの色。持っている間は、物の姿勢の点の状態の色（体の点の丸と同じ。Pin なら Pin の色）。
        /// 持っていない物の姿勢は全身 IK の点でなく、状態を使わないので、Rig のターゲットと同じ物の色
        /// </summary>
        Color handleColor
        {
            get {
                if (!entry_.attached) return kColor;
                BodyPoint values = entry_.poseValues;
                bool pinned = values.positionWeight > 0 || values.rotationWeight > 0;
                return BodyPointTarget.StateColor (pinned, pinned && values.locked >= 0.5f, Mathf.Clamp01 (Mathf.Max (values.positionWeight, values.rotationWeight)));
            }
        }

        public override void WriteKeys (CurveWriter writer)
        {
            writer.Transform (entry_.control, TransformChannels.Position | TransformChannels.Rotation);
            // 物の姿勢の点の状態（体の点と同じ値。S102）
            writer.Float (entry_.poseControl, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, entry_.poseValues.positionWeight);
            writer.Float (entry_.poseControl, typeof (BodyPoint), BodyPoint.kRotationWeightProperty, entry_.poseValues.rotationWeight);
            writer.Float (entry_.poseControl, typeof (BodyPoint), BodyPoint.kLockedProperty, entry_.poseValues.locked);
            entry_.keysPending = false;
            // 付けているかは元のクリップだけから読む（Override の段へ書く間は打たない。切り替えは元のクリップに打つ）
            if (writer.storePosition != null) return;
            foreach (HeldObjectRig.Attachment attachment in entry_.attachments) {
                writer.Float (attachment.values.transform, typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty, attachment.values.attached);
                WriteGrip (writer, attachment);
            }
        }

        /// <summary>
        /// キーで付けた位置（S99）。その先で付けるボタンを使ったクリップにだけカーブを作り、あればどのキーにも打つ
        /// （使っていないクリップのカーブの組は変えない。前後のキーで同じ先に付けていれば、キーの間は位置を補間する。HeldObjectRig.ApplyGripKeys）
        /// </summary>
        static void WriteGrip (CurveWriter writer, HeldObjectRig.Attachment attachment)
        {
            if (!attachment.gripCurved && !attachment.gripKeyed) return;
            HeldObjectControl values = attachment.values;
            writer.Float (values.transform, typeof (HeldObjectControl), HeldObjectControl.kGripKeyedProperty, values.gripKeyed);
            WriteVector (writer, values.transform, HeldObjectControl.kGripPositionProperty, values.gripPosition);
            WriteVector (writer, values.transform, HeldObjectControl.kGripRotationProperty, values.gripRotation);
            attachment.gripCurved = true;
        }

        static void WriteVector (CurveWriter writer, Transform control, string property, Vector3 value)
        {
            writer.Float (control, typeof (HeldObjectControl), property + ".x", value.x);
            writer.Float (control, typeof (HeldObjectControl), property + ".y", value.y);
            writer.Float (control, typeof (HeldObjectControl), property + ".z", value.z);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (RigPaths.Object (entry_.name));
            result.Add (RigPaths.ObjectPose (entry_.name));
            foreach (HeldObjectRig.Attachment attachment in entry_.attachments) result.Add (RigPaths.ObjectAttach (entry_.name, attachment.name));
        }

        /// <summary>物のメッシュをクリックしたら選ぶ</summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
            if (entry_.display != null) result.AddRange (entry_.display.GetComponentsInChildren<Transform> (true));
        }

        /// <summary>数値欄に出す値（枠の骨から見た位置（メートル）と向き）</summary>
        public override bool TryGetValues (out Vector3 position, out Quaternion rotation)
        {
            position = entry_.control.localPosition * rig_.humanScale;
            rotation = entry_.control.localRotation;
            return true;
        }

        public override void SetValues (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            if ((channels & TransformChannels.Position) != 0) entry_.control.localPosition = position / rig_.humanScale;
            if ((channels & TransformChannels.Rotation) != 0) entry_.control.localRotation = Quaternion.Normalize (rotation);
            entry_.keysPending = true;
        }

        public override void ResetValues (TransformChannels channels)
        {
            HeldObjectDefinition definition = entry_.definition;
            if ((channels & TransformChannels.Position) != 0) entry_.control.localPosition = rig_.BodyToRoot (definition.restPosition) / rig_.humanScale;
            if ((channels & TransformChannels.Rotation) != 0) entry_.control.localRotation = Quaternion.LookRotation (rig_.bodyForward, rig_.bodyUp) * Quaternion.Euler (definition.restRotation);
            entry_.keysPending = true;
        }

        public override void ResetPose ()
        {
            ResetValues (TransformChannels.Position | TransformChannels.Rotation);
        }
    }

}
