using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Lilium
{

    /// <summary>
    /// Humanoid のクリップのルートモーション（体の置き場所の移動）を取り出す。
    /// 窓の段はルートモーションを出さずに当てる（体がその場に残る）ので、移動する技を読み込むにはこの分を足し戻す。
    /// Unity の抜き方（クリップの Bake Into Pose などの設定）に合わせるため、自分で計算せず Animator を実際に進めて測る
    /// </summary>
    public static class RootMotion
    {
        /// <summary>
        /// 0 フレームからの移動を、フレームごとに 0 フレームの Animator から見た位置と向きで返す（単位は Animator の中の長さ）
        /// </summary>
        /// <param name="animator">クリップを当てる Animator（Humanoid）。Transform は元に戻す。骨の姿勢は崩れるので、呼んだ側で当て直す</param>
        public static void Sample (Animator animator, AnimationClip clip, float rate, int frameCount, out Vector3[] positions, out Quaternion[] rotations)
        {
            positions = new Vector3[frameCount];
            rotations = new Quaternion[frameCount];
            for (int f = 0; f < frameCount; f++) rotations[f] = Quaternion.identity;
            if (animator == null || clip == null || frameCount <= 1) return;

            Transform transform = animator.transform;
            transform.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            bool applyRootMotion = animator.applyRootMotion;
            PlayableGraph graph = PlayableGraph.Create ("RootMotion");
            try {
                graph.SetTimeUpdateMode (DirectorUpdateMode.Manual);
                AnimationClipPlayable playable = AnimationClipPlayable.Create (graph, clip);
                playable.SetApplyFootIK (false);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create (graph, "RootMotion", animator);
                output.SetSourcePlayable (playable);
                animator.applyRootMotion = true;

                playable.SetTime (0);
                graph.Evaluate (0);
                Matrix4x4 start = transform.worldToLocalMatrix;
                Quaternion startRotation = Quaternion.Inverse (transform.rotation);
                // 移動はフレーム間の差で出るので、先頭から順に進める（時刻を飛ばすと差が出ない）
                float step = 1 / rate;
                for (int f = 1; f < frameCount; f++) {
                    graph.Evaluate (step);
                    positions[f] = start.MultiplyPoint3x4 (transform.position);
                    rotations[f] = startRotation * transform.rotation;
                }
            }
            finally {
                graph.Destroy ();
                animator.applyRootMotion = applyRootMotion;
                transform.SetLocalPositionAndRotation (position, rotation);
            }
        }

        /// <summary>
        /// 編集用の体の腰を、ルートの移動の分だけ動かす（編集用の体のルートから見て）
        /// </summary>
        public static void Apply (EditingRig rig, Vector3 position, Quaternion rotation)
        {
            if (rig == null || rig.root == null) return;
            Transform hips = rig.GetEditingBone (HumanBodyBones.Hips);
            if (hips == null) return;
            Transform root = rig.root.transform;
            Vector3 local = root.InverseTransformPoint (hips.position);
            Quaternion localRotation = Quaternion.Inverse (root.rotation) * hips.rotation;
            hips.SetPositionAndRotation (root.TransformPoint (position + rotation * local), root.rotation * rotation * localRotation);
        }
    }

}
