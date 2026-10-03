using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップを編集用の体へ当てる。Avatar の無い Animator にグラフで流すので、Transform のカーブも
    /// コンポーネントの数値（IkControl.ikWeight・Rig の代理など）もそのまま当たる。
    /// 当たるのはカーブのある値だけで、カーブの無いコントロールは今の値のまま。
    /// カーブの組が変わったら Invalidate で作り直す（Animator の束縛はグラフを作るときに決まる）。
    /// エディタでは Animator.Update がグラフを流さないので、時刻を入れて Evaluate する
    /// </summary>
    public sealed class ClipSampler : System.IDisposable
    {
        readonly Animator animator_;
        PlayableGraph graph_;
        AnimationClipPlayable playable_;
        AnimationClip clip_;

        public ClipSampler (Animator animator)
        {
            animator_ = animator;
        }

        public bool isValid
        {
            get { return graph_.IsValid (); }
        }

        public void Sample (AnimationClip clip, float time)
        {
            if (animator_ == null || clip == null) return;
            if (clip != clip_ || !graph_.IsValid ()) Build (clip);
            if (!graph_.IsValid ()) return;

            playable_.SetTime (time);
            graph_.Evaluate (0);
        }

        /// <summary>
        /// クリップのカーブが書き換わったとき。次の Sample で作り直す
        /// </summary>
        public void Invalidate ()
        {
            Destroy ();
        }

        void Build (AnimationClip clip)
        {
            Destroy ();
            clip_ = clip;
            // 前のクリップの束縛を捨てる。姿勢は毎回基準へ戻してから当てるので、ここで戻っても困らない
            animator_.Rebind ();
            // 複製したモデルのカリング（CullUpdateTransforms など）が残っていると、プレビューでは評価が黙って飛ばされる（キャラ C）
            animator_.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            graph_ = PlayableGraph.Create ("ClipSampler");
            graph_.SetTimeUpdateMode (DirectorUpdateMode.Manual);
            playable_ = AnimationClipPlayable.Create (graph_, clip);
            // 足の IK などの後処理は編集用の体には要らない
            playable_.SetApplyFootIK (false);
            AnimationPlayableOutput output = AnimationPlayableOutput.Create (graph_, "Clip", animator_);
            output.SetSourcePlayable (playable_);
        }

        void Destroy ()
        {
            if (graph_.IsValid ()) graph_.Destroy ();
            clip_ = null;
        }

        public void Dispose ()
        {
            Destroy ();
        }
    }

}
