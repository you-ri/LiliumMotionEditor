using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 編集用リグのつかむ対象を作る。FK は骨ごと、全身 IK は点ごと、ゲームの Rig のターゲットは 1 つずつ
    /// </summary>
    public static class RigTargets
    {
        public static List<PoseTarget> Create (PreviewStage stage)
        {
            List<PoseTarget> targets = Create (stage != null ? stage.editingRig : null, stage != null ? stage.editingRigSolver : null);
            // 全身 IK の点（S25）
            if (stage != null && stage.fullBody != null) targets.AddRange (BodyPointTarget.Create (stage.editingRig, stage.fullBody));
            // 物の持ち替え（キャラの設定の parentSwitches）の物。表示モデルの上でつかんで、握りを動かす
            if (stage != null) targets.AddRange (ParentSwitchTarget.Create (stage));
            // 持ち物（S91）と、持っている手の点の結び付け
            targets.AddRange (HeldObjectTarget.Create (stage));
            if (stage != null && stage.heldObjects != null) {
                foreach (PoseTarget target in targets) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point == null) continue;
                    point.holder = stage.heldObjects.FindHand (point.id);
                    point.heldObject = stage.heldObjects.FindPointOwner (point.id);
                }
            }
            return targets;
        }

        public static List<PoseTarget> Create (EditingRig rig, EditingRigSolver solver)
        {
            List<PoseTarget> targets = new List<PoseTarget> ();
            if (rig == null || rig.root == null || solver == null) return targets;

            BoneShapes shapes = new BoneShapes (rig);
            foreach (RigBinding.BoneControl source in rig.binding.boneControls) {
                if (!source.enabled) continue;
                Transform control = rig.FindControl (source.path);
                Transform bone = rig.GetEditingBone (source.bone);
                if (control == null || bone == null) continue;
                targets.Add (new FkTarget (rig, solver, shapes, control, bone, source));
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
            foreach (RigBinding.BoneControl source in rig.binding.boneControls) {
                Transform bone = source.enabled ? rig.GetEditingBone (source.bone) : null;
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

        /// <summary>
        /// 骨の先（基準姿勢のワールド）。コントロールのある子の骨の根元すべて（腰 → 背骨・両脚、胸 → 首・両肩）。
        /// 子が無い（手先・頭など）ときは、骨の形の向きと長さだけ伸ばした所 1 つ
        /// </summary>
        public List<Vector3> GetRestEnds (Transform bone)
        {
            Vector3 position = RestWorld (bone);
            List<Transform> children = new List<Transform> ();
            CollectControlledChildren (bone, children);
            List<Vector3> ends = new List<Vector3> ();
            foreach (Transform child in children) {
                Vector3 end = RestWorld (child);
                if ((end - position).sqrMagnitude >= 1e-8f) ends.Add (end);
            }
            if (ends.Count == 0) {
                Quaternion rotation = GetRotation (bone, rig_.root.transform.rotation * rig_.GetRestRootRotation (bone));
                ends.Add (position + rotation * Vector3.forward * GetLength (bone));
            }
            return ends;
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
