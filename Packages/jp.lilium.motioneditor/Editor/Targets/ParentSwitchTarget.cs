using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 持ち替えの物（<see cref="ParentSwitchDefinition"/> の物）をつかむ対象。表示モデルの物の上にハンドルを出し、
    /// 動かしたら今の持ち手ノード（物の親）から見たローカル姿勢を握りへ入れる。キーは握り（Controls/Props/...）に打つ。
    ///
    /// ドラッグ中は編集用リグの段がクリップを当て直さない（EvaluateKeepingValues）ので、表示モデルの握りに入れた値がそのまま残り、
    /// グラフ（握りを物へ写す → Rig で持ち手ノードを親へ付ける）を通って物が動く。
    /// 左右反転などで物が見えている位置と違うとき（<see cref="ParentSwitch.blockReason"/>）はハンドルを出さない
    /// </summary>
    sealed class ParentSwitchTarget : PoseTarget
    {
        static readonly Color kColor = new Color (0.3f, 0.9f, 1f, 0.35f);
        static readonly string[] kPositionAxes = { "x", "y", "z" };
        static readonly string[] kRotationAxes = { "x", "y", "z", "w" };

        readonly PreviewStage stage_;
        readonly ParentSwitch.Bound bound_;
        readonly int moveHash_;
        readonly int rotateHash_;

        ParentSwitchTarget (PreviewStage stage, ParentSwitch.Bound bound)
        {
            stage_ = stage;
            bound_ = bound;
            moveHash_ = ("MktParentSwitchMove" + bound.definition.name).GetHashCode ();
            rotateHash_ = ("MktParentSwitchRotate" + bound.definition.name).GetHashCode ();
        }

        /// <summary>キャラの設定にある持ち替えのうち、表示モデルへ結び付けられたもの</summary>
        public static List<PoseTarget> Create (PreviewStage stage)
        {
            List<PoseTarget> result = new List<PoseTarget> ();
            CharacterSettings settings = stage.sourcePrefab != null ? SettingsLookup.Find (stage.sourcePrefab) : null;
            GameObject root = stage.animator != null ? stage.animator.gameObject : null;
            if (settings == null || settings.parentSwitches == null || root == null) return result;
            foreach (ParentSwitchDefinition parentSwitch in settings.parentSwitches) {
                string error;
                ParentSwitch.Bound bound = ParentSwitch.Bind (root, parentSwitch, out error);
                if (bound == null || bound.obj.parent == null) continue;
                result.Add (new ParentSwitchTarget (stage, bound));
            }
            return result;
        }

        Transform hold
        {
            get { return bound_.obj.parent; }
        }

        bool blocked
        {
            get { return ParentSwitch.blockReason != null && ParentSwitch.blockReason (stage_.model) != null; }
        }

        public override GameObject gameObject
        {
            get { return bound_.obj != null ? bound_.obj.gameObject : null; }
        }

        public override string label
        {
            get { return "持ち替え " + bound_.definition.name; }
        }

        public override Transform anchor
        {
            get { return bound_.obj; }
        }

        float handleLength
        {
            get { return 0.2f * Mathf.Max (0.1f, stage_.editingRig != null ? stage_.editingRig.humanScale : 1); }
        }

        /// <summary>物をワールドの姿勢へ置く（握りを今の持ち手ノードからのローカルにする）</summary>
        void SetWorld (Vector3 position, Quaternion rotation)
        {
            Transform parent = hold;
            bound_.grip.SetLocalPositionAndRotation (
                Quaternion.Inverse (parent.rotation) * (position - parent.position),
                Quaternion.Inverse (parent.rotation) * rotation);
            bound_.obj.SetPositionAndRotation (position, rotation);
        }

        public override TransformChannels GetSpinChannels (bool pole)
        {
            return TransformChannels.Position | TransformChannels.Rotation;
        }

        public override void SpinMove (Vector3 delta, bool pole)
        {
            // delta は動かすものの親（持ち手ノード）から見た変化
            SetWorld (bound_.obj.position + hold.rotation * delta, bound_.obj.rotation);
        }

        public override void SpinRotateLocal (Quaternion delta, bool pole)
        {
            SetWorld (bound_.obj.position, bound_.obj.rotation * delta);
        }

        public override void SpinRotateWorld (Quaternion delta, bool pole)
        {
            SetWorld (bound_.obj.position, delta * bound_.obj.rotation);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (bound_.obj == null || blocked) return;

            PoseHandleUtility.color = kColor;
            Vector3 position = bound_.obj.position;
            Quaternion rotation = bound_.obj.rotation;
            Vector3 moved = PoseHandleUtility.DoJointHandle (moveHash_, bound_.obj, position, rotation);
            if (moved != position) {
                SetWorld (moved, rotation);
                GUI.changed = true;
            }
            if (!selected) {
                PoseHandleUtility.color = PoseHandleUtility.StandardColor;
                return;
            }
            Quaternion rotated = PoseHandleUtility.DoBoneHandle (rotateHash_, bound_.obj, bound_.obj.position, rotation, handleLength);
            if (rotated != rotation) {
                SetWorld (bound_.obj.position, rotated);
                GUI.changed = true;
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
        }

        public override void WriteKeys (CurveWriter writer)
        {
            string path = RigPaths.Props (bound_.definition.gripPath);
            Vector3 position = bound_.grip.localPosition;
            Quaternion rotation = bound_.grip.localRotation;
            for (int i = 0; i < 3; i++) writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalPosition." + kPositionAxes[i]), position[i]);
            float[] q = { rotation.x, rotation.y, rotation.z, rotation.w };
            for (int i = 0; i < 4; i++) writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + kRotationAxes[i]), q[i]);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            result.Add (RigPaths.Props (bound_.definition.gripPath));
        }

        /// <summary>物のメッシュをクリックしたら選ぶ</summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
            if (bound_.obj == null) return;
            result.AddRange (bound_.obj.GetComponentsInChildren<Transform> (true));
        }

        public override bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            bound_.grip.GetLocalPositionAndRotation (out position, out rotation);
            return true;
        }

        public override void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            if ((channels & TransformChannels.Position) != 0) bound_.grip.localPosition = position;
            if ((channels & TransformChannels.Rotation) != 0) bound_.grip.localRotation = Quaternion.Normalize (rotation);
        }

        /// <summary>
        /// 握りを基準（prefab の握りの値＝既定の持ち手で普通に持つ位置）へ戻す。今の親の手に収まる
        /// （右手の置き場は左手の鏡像なので、同じ値で右手にも収まる）。キーは打たない
        /// </summary>
        public override void ResetValues (bool pole, TransformChannels channels)
        {
            Pose rest;
            if (!TryGetRestGrip (out rest)) return;
            if ((channels & TransformChannels.Position) != 0) bound_.grip.localPosition = rest.position;
            if ((channels & TransformChannels.Rotation) != 0) bound_.grip.localRotation = rest.rotation;
        }

        public override void ResetPose ()
        {
            ResetValues (false, TransformChannels.Position | TransformChannels.Rotation);
        }

        /// <summary>prefab（複製元）の握りの local の値</summary>
        bool TryGetRestGrip (out Pose rest)
        {
            rest = Pose.identity;
            GameObject prefab = stage_.sourcePrefab;
            Animator animator = prefab != null ? prefab.GetComponentInChildren<Animator> (true) : null;
            Transform grip = animator != null ? animator.transform.Find (bound_.definition.gripPath) : null;
            if (grip == null) return false;
            rest = new Pose (grip.localPosition, grip.localRotation);
            return true;
        }
    }

}
