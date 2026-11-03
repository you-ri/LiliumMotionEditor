using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Lilium
{

    // 角度制限（S95）。定義の値（体の向きの軸ごと・度）を、ソルバーの枠（x がひねり・y と z が振り・ラジアン）にする
    public sealed partial class FullBodyRig
    {
        /// <summary>制限しない軸の範囲（ラジアン）。ひねりも振りも ±π に収まるので、これより広ければ効かない</summary>
        const float kFreeLimit = 100;

        /// <summary>
        /// 骨 bone の角度制限の枠と基準（S95）。frame は親の骨の枠から見た制限の枠（x がひねり、y・z が振り）、rest は T ポーズでの親から見た向き、
        /// twist は体の向きの軸のうち、ひねりにした軸（0 右・1 上・2 前。T ポーズの骨の向き＝子の付け根の平均への向きにいちばん近い軸。子が無ければ親からの向き）。
        /// 制限の枠の軸は、ひねりの軸から体の軸を巡回の順に並べる（x 右なら y 上・z 前、y 上なら z 前・x 右、z 前なら x 右・y 上）
        /// </summary>
        internal static bool TryGetLimitFrame (EditingRig rig, Transform bone, Transform parent, out Quaternion frame, out Quaternion rest, out int twist)
        {
            frame = rest = Quaternion.identity;
            twist = 0;
            if (rig == null || bone == null || parent == null) return false;
            HashSet<Transform> editing = new HashSet<Transform> (rig.editingBones);
            Vector3 origin = rig.GetRestRootPosition (bone);
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (Transform child in bone) {
                if (!editing.Contains (child)) continue;
                sum += rig.GetRestRootPosition (child) - origin;
                count++;
            }
            Vector3 direction = count > 0 ? sum / count : origin - rig.GetRestRootPosition (parent);
            if (direction.sqrMagnitude < 1e-12f) direction = rig.bodyUp;
            Vector3 body = new Vector3 (Vector3.Dot (direction, rig.bodyRight), Vector3.Dot (direction, rig.bodyUp), Vector3.Dot (direction, rig.bodyForward));
            twist = Mathf.Abs (body.x) >= Mathf.Abs (body.y) && Mathf.Abs (body.x) >= Mathf.Abs (body.z) ? 0 : Mathf.Abs (body.y) >= Mathf.Abs (body.z) ? 1 : 2;
            Vector3 second = BodyAxis (rig, (twist + 1) % 3);
            Vector3 third = BodyAxis (rig, (twist + 2) % 3);
            Quaternion parentRest = rig.GetRestRootRotation (parent);
            // LookRotation は z を third・y を second に向け、x は second × third（巡回の順なので、ひねりの軸）になる
            frame = Quaternion.Inverse (parentRest) * Quaternion.LookRotation (third, second);
            rest = Quaternion.Inverse (parentRest) * rig.GetRestRootRotation (bone);
            return true;
        }

        static Vector3 BodyAxis (EditingRig rig, int axis)
        {
            return axis == 0 ? rig.bodyRight : axis == 1 ? rig.bodyUp : rig.bodyForward;
        }

        /// <summary>ソルバーの枠の値（x ひねり・y z 振り）を、体の向きの軸の順（x 右・y 上・z 前）に並べ替える</summary>
        internal static Vector3 LimitToBodyAxes (Vector3 frameValues, int twist)
        {
            Vector3 body = Vector3.zero;
            for (int k = 0; k < 3; k++) body[(twist + k) % 3] = frameValues[k];
            return body;
        }

        /// <summary>定義の角度制限を、骨の設定に入れる。制限が無い・枠が決まらなければ入れない</summary>
        void SetLimit (ref FullBodyIkBone bone, EditRigDefinition.JointLimit limit, Transform self, Transform parent)
        {
            if (limit == null || !limit.any) return;
            if (!TryGetLimitFrame (rig_, self, parent, out Quaternion frame, out Quaternion rest, out int twist)) return;
            float slack = Mathf.Max (0, limit.slack) * Mathf.Deg2Rad;
            float3 min = float3.zero, max = float3.zero, slacks = float3.zero;
            for (int k = 0; k < 3; k++) {
                EditRigDefinition.AxisLimit axis = limit[(twist + k) % 3];
                switch (axis.mode) {
                    case EditRigDefinition.LimitMode.Range:
                        min[k] = Mathf.Min (axis.min, axis.max) * Mathf.Deg2Rad;
                        max[k] = Mathf.Max (axis.min, axis.max) * Mathf.Deg2Rad;
                        slacks[k] = slack;
                        break;
                    case EditRigDefinition.LimitMode.Locked:
                        // 回さない（遊びも使わない）
                        break;
                    default:
                        min[k] = -kFreeLimit;
                        max[k] = kFreeLimit;
                        break;
                }
            }
            bone.limit = 1;
            bone.limitFrame = frame;
            bone.limitRest = rest;
            bone.limitMin = min;
            bone.limitMax = max;
            bone.limitSlack = slacks;
        }
    }

}
