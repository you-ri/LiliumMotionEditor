using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 持ち物の FK（S91）。骨の FK と同じく View の Bones で出す。動かすと持っている手と体が付いてくる。
    /// キーは物の姿勢（Controls/Object/名前）と、手ごとの持っているか。離すと姿勢全体のキーを打つ。
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

        float handleLength
        {
            get { return 0.2f * Mathf.Max (0.1f, rig_.humanScale); }
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
            Quaternion rotation = display.rotation;
            PoseHandleUtility.DoObjectHandles (moveHash_, rotateHash_, entry_.control, display, handleLength, kColor, selected,
                position => entry_.DragTo (position, rotation),
                rotated => entry_.DragTo (display.position, rotated));
        }

        public override void WriteKeys (CurveWriter writer)
        {
            writer.Transform (entry_.control, TransformChannels.Position | TransformChannels.Rotation);
            entry_.keysPending = false;
            // 持っているかは元のクリップだけから読む（Override の段へ書く間は打たない。切り替えは元のクリップに打つ）
            if (writer.storePosition != null) return;
            writer.Float (entry_.control, typeof (HeldObjectControl), HeldObjectControl.kLeftHoldProperty, entry_.values.leftHold);
            writer.Float (entry_.control, typeof (HeldObjectControl), HeldObjectControl.kRightHoldProperty, entry_.values.rightHold);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (RigPaths.Object (entry_.name));
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
