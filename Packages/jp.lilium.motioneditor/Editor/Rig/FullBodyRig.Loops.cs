using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Lilium
{

    // リグ定義の輪の関節（S105）。持っている物の輪（FullBodyRig.Held）と同じ輪の関節（FullBodyIkLoop）として解く
    public sealed partial class FullBodyRig
    {
        /// <summary>リグ定義の輪の関節 1 つ（骨 bone の付け根を、骨 to の上の anchor に留める）</summary>
        struct LoopJoint
        {
            public Transform bone;
            public Transform to;
            /// <summary>基準姿勢での bone の付け根の場所（to の骨の枠で、to の付け根から。メートル）</summary>
            public Vector3 anchor;
        }

        readonly List<LoopJoint> loopJoints_ = new List<LoopJoint> ();

        /// <summary>リグ定義の輪の関節を、編集用の骨に結び付ける（骨の並びを作る前。キャラに無い骨の関節は飛ばす）</summary>
        void ResolveLoopJoints (EditRigDefinition definition)
        {
            if (definition == null || definition.loopJoints == null) return;
            foreach (EditRigDefinition.LoopJoint joint in definition.loopJoints) {
                if (joint == null) continue;
                Transform bone = FindLoopBone (joint.bone, joint.control);
                Transform to = FindLoopBone (joint.to, joint.toControl);
                if (bone == null || to == null || bone == to) continue;
                Vector3 offset = rig_.GetRestRootPosition (bone) - rig_.GetRestRootPosition (to);
                loopJoints_.Add (new LoopJoint { bone = bone, to = to, anchor = Quaternion.Inverse (rig_.GetRestRootRotation (to)) * offset });
            }
        }

        /// <summary>輪の関節の端の骨（名前の追加コントロールがあればその骨。編集用の体）</summary>
        Transform FindLoopBone (HumanBodyBones bone, string control)
        {
            if (!string.IsNullOrEmpty (control)) {
                RigBinding.Extra extra = rig_.binding.extras.Find (e => e.control.name == control);
                return extra != null && extra.bone != null ? rig_.GetEditingBone (extra.bone) : null;
            }
            return bone >= 0 && bone < HumanBodyBones.LastBone ? rig_.GetEditingBone (bone) : null;
        }

        /// <summary>
        /// 輪の関節を計算用のデータへ入れる（解く前）。リグ定義の輪（玉の関節: 点を寄せるだけで、向きは自由）の後に、持っている物の輪を入れる。
        /// どちらも解く骨どうしのときだけ
        /// </summary>
        void LoadLoops ()
        {
            int loops = 0;
            foreach (LoopJoint joint in loopJoints_) {
                int bone = bodies_.IndexOf (joint.bone);
                int to = bodies_.IndexOf (joint.to);
                if (bone < 0 || to < 0 || loops >= data_.loops.Length) continue;
                data_.loops[loops++] = new FullBodyIkLoop {
                    boneA = bone,
                    anchorA = float3.zero,
                    boneB = to,
                    anchorB = joint.anchor,
                };
            }
            data_.loopCount = LoadHeldLoops (loops);
        }
    }

}
