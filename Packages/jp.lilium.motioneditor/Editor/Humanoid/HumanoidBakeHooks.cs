using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 焼くときの情報（受け口に渡す）
    /// </summary>
    public sealed class HumanoidBakeContext
    {
        /// <summary>編集しているキャラの prefab（アセット）。窓以外から焼くときは null のことがある</summary>
        public GameObject model;
        /// <summary>プレビューに置いた表示モデルの Animator（チャンネルのパスの基準）</summary>
        public Animator displayAnimator;
        public AnimationClip source;
        /// <summary>焼いた結果について利用者へ伝えること</summary>
        public readonly List<string> notes = new List<string> ();
    }

    /// <summary>
    /// 焼きの受け口。パッケージの外（ゲーム側）がキャラごとの事情を足す
    /// </summary>
    public interface IHumanoidBakeHook
    {
        /// <summary>
        /// このチャンネルを焼くか。ゲームのコードが毎フレーム書く値（狙いの重みなど）は焼かない（焼くと Animator がゲームの値を潰す）。
        /// binding はゲーム prefab の Animator から見たパス
        /// </summary>
        bool ShouldBake (HumanoidBakeContext context, EditorCurveBinding binding);

        /// <summary>
        /// 焼いた後の加工（キャラの向きに合わせた左右反転など）
        /// </summary>
        void PostProcess (HumanoidBakeContext context, AnimationClip clip);
    }

    public static class HumanoidBakeHooks
    {
        static readonly List<IHumanoidBakeHook> hooks_ = new List<IHumanoidBakeHook> ();

        public static IReadOnlyList<IHumanoidBakeHook> hooks
        {
            get { return hooks_; }
        }

        /// <summary>
        /// 受け口を足す（[InitializeOnLoad] の静的コンストラクタなどから呼ぶ）。同じものは 1 回だけ
        /// </summary>
        public static void Register (IHumanoidBakeHook hook)
        {
            if (hook != null && !hooks_.Contains (hook)) hooks_.Add (hook);
        }

        public static void Unregister (IHumanoidBakeHook hook)
        {
            hooks_.Remove (hook);
        }

        internal static bool ShouldBake (IList<IHumanoidBakeHook> hooks, HumanoidBakeContext context, EditorCurveBinding binding)
        {
            foreach (IHumanoidBakeHook hook in hooks) {
                try {
                    if (!hook.ShouldBake (context, binding)) return false;
                }
                catch (System.Exception e) {
                    Debug.LogException (e);
                }
            }
            return true;
        }

        internal static void PostProcess (IList<IHumanoidBakeHook> hooks, HumanoidBakeContext context, AnimationClip clip)
        {
            foreach (IHumanoidBakeHook hook in hooks) {
                try {
                    hook.PostProcess (context, clip);
                }
                catch (System.Exception e) {
                    Debug.LogException (e);
                    context.notes.Add (Tr ("HUMANOID_BAKE_HOOKS_POST_PROCESS_EXCEPTION", hook.GetType ().Name, e.Message));
                }
            }
        }
    }

}
