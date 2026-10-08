using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

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
                targets.Add (new FkTarget (rig, solver, shapes, control, bone, fk.bone, fk.control.bone.ToString (), fk.control.position, fk.control.fullBodyHandle));
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                if (!extra.enabled) continue;
                Transform control = rig.FindControl (extra.path);
                Transform bone = rig.GetEditingBone (extra.bone);
                if (control == null || bone == null) continue;
                targets.Add (new FkTarget (rig, solver, shapes, control, bone, extra.bone, extra.control.name, extra.control.position, extra.control.fullBodyHandle));
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

}
