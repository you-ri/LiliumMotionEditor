using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

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
            get { return rig_.humanScale; }
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
