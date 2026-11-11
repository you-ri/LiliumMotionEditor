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

    /// <summary>
    /// Humanoid のクリップを窓で読むときの受け口（任意）。焼きの受け口（IHumanoidBakeHook）と同じオブジェクトに実装し、HumanoidBakeHooks に登録する。
    /// 焼いたクリップを PostProcess で左右反転するキャラは、既存の Humanoid のクリップ（ゲームが今流しているもの）も左右反転して読む。
    /// そうすると窓の見え方が焼いた後のゲームの見え方とそろい、取り込んで焼くと元のクリップに戻る
    /// </summary>
    public interface IHumanoidReadHook
    {
        /// <summary>model（キャラの prefab）では、Humanoid のクリップを左右反転して読むか</summary>
        bool MirrorsHumanoidClips (GameObject model);
    }

    public static class HumanoidBakeHooks
    {
        static readonly HookList<IHumanoidBakeHook> hooks_ = new HookList<IHumanoidBakeHook> ();

        public static IReadOnlyList<IHumanoidBakeHook> hooks
        {
            get { return hooks_.items; }
        }

        /// <summary>
        /// 受け口を足す（[InitializeOnLoad] の静的コンストラクタなどから呼ぶ）。同じものは 1 回だけ
        /// </summary>
        public static void Register (IHumanoidBakeHook hook)
        {
            hooks_.Register (hook);
        }

        public static void Unregister (IHumanoidBakeHook hook)
        {
            hooks_.Unregister (hook);
        }

        /// <summary>model では Humanoid のクリップを左右反転して読むか（登録した受け口のうち IHumanoidReadHook のどれかが反転すると答えたら）</summary>
        public static bool MirrorsHumanoidClips (GameObject model)
        {
            if (model == null) return false;
            foreach (IHumanoidBakeHook hook in hooks_.items) {
                IHumanoidReadHook read = hook as IHumanoidReadHook;
                if (read == null) continue;
                try {
                    if (read.MirrorsHumanoidClips (model)) return true;
                }
                catch (System.Exception e) {
                    Debug.LogException (e);
                }
            }
            return false;
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
