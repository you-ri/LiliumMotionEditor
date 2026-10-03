using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 一緒に見ている演出（Timeline）の中で、編集しているクリップがどう置かれているか（S15b。順番 100）。
    /// 受け持つのは次の 3 つ:
    /// - **時計の写像**（開始・頭出し・速さ）。姿勢ではなく時計の変換なので、窓の時計がこの写像で演出の時刻を出す。行には注記として出す。
    /// - **ブレンド**（前後のクリップと混ざる区間）。v1 では混ぜた姿勢は出さず、区間の中はキーを打たない・逆に通さない。
    /// - **オフセット**（トラック・クリップの位置と回転）。ゲーム側がキャラの位置を自分で決めて使わないことがあるので当てない。注記だけ出す。
    /// 姿勢そのものには手を加えないので、ブレンドの外は逆に厳密に通る
    /// </summary>
    public sealed class TimelineClipLayer : PoseLayer
    {
        readonly System.Func<ContextClip> getLink_;
        readonly int candidates_;
        ContextClip evaluated_;
        double master_;
        float mix_ = 1;

        /// <param name="getLink">演出のどのクリップとして見ているか（無ければ null）</param>
        /// <param name="candidates">演出にあるアニメーションのクリップの数</param>
        public TimelineClipLayer (System.Func<ContextClip> getLink, int candidates)
        {
            getLink_ = getLink;
            candidates_ = candidates;
        }

        ContextClip link
        {
            get { return getLink_ != null ? getLink_ () : null; }
        }

        public override LayerKind kind { get { return LayerKind.TimelineClip; } }
        public override int order { get { return PoseStack.kOrderTimelineClip; } }
        public override string label { get { return "Timeline Clip"; } }
        public override string unavailableReason { get { return "時計の写像なので値を持たない"; } }

        public override bool canEvaluate
        {
            get {
                ContextClip current = link;
                return current != null && !current.muted;
            }
        }

        /// <summary>
        /// 最後に評価した時刻が、前後のクリップと混ざる区間にあるか
        /// </summary>
        public bool blending
        {
            get { return active && evaluated_ != null && evaluated_ == link && evaluated_.Contains (master_) && mix_ < 1 - 1e-4f; }
        }

        /// <summary>
        /// 評価した時刻が、演出の中でこのクリップの区間の外か（演出ではこの時刻にこのクリップは出ていない）
        /// </summary>
        public bool outside
        {
            get { return active && evaluated_ != null && !evaluated_.Contains (master_); }
        }

        /// <summary>今の時刻の重み（区間の外は 0）</summary>
        public float mix
        {
            get { return mix_; }
        }

        /// <summary>
        /// 上書きのトラック・Avatar Mask は v1 では通さない（体の一部だけが別のクリップで上書きされる）
        /// </summary>
        string blockReason
        {
            get {
                ContextClip current = link;
                if (current == null) return null;
                if (current.track != null && !string.IsNullOrEmpty (current.track.editBlock)) return current.track.editBlock;
                if (current.overrideTrack) return "上書きのトラック（v1 では戻せない）";
                if (current.hasAvatarMask) return "Avatar Mask で体の一部だけ効く（v1 では戻せない）";
                if (blending) return "Timeline のブレンド中（" + Mathf.RoundToInt (mix_ * 100) + "%。v1 では混ぜた姿勢を出さず、この区間は編集しない）";
                return null;
            }
        }

        /// <summary>
        /// 今の時刻でキーを打てない理由（打てるなら null）
        /// </summary>
        public string editBlockReason
        {
            get { return active ? blockReason : null; }
        }

        public override InverseKind inverse
        {
            get { return blockReason != null ? InverseKind.None : InverseKind.Exact; }
        }

        public override string inverseReason
        {
            get { return blockReason ?? "姿勢には手を加えない（時計の写像だけ）"; }
        }

        protected override string description
        {
            get {
                ContextClip current = link;
                if (current == null) {
                    return candidates_ > 0
                        ? "演出のクリップと結び付けていない（ツールバーの演出の右で選ぶ）。いまは演出の時刻をオフセットで合わせている"
                        : "演出にアニメーションのクリップが無い";
                }
                string text = current.label + ": 演出の " + Seconds (current.start) + " から、クリップの " + Seconds (current.clipIn)
                    + " を " + current.timeScale.ToString ("0.###") + " 倍で再生";
                if (current.blendIn > 0 || current.blendOut > 0) text += "。ブレンド 入り " + Seconds (current.blendIn) + "・抜け " + Seconds (current.blendOut);
                if (current.muted) text += "。トラックがミュートなので素通し";
                if (!string.IsNullOrEmpty (current.offsets)) text += "。オフセット（" + current.offsets + "）はゲームの変換が使わないので当てない";
                if (outside) text += "。いまの時刻は区間の外（演出ではこのクリップは出ていない）";
                return text;
            }
        }

        static string Seconds (double seconds)
        {
            return seconds.ToString ("0.###") + "s";
        }

        /// <summary>
        /// 姿勢には手を加えず、今の時刻（クリップのローカル秒）が演出のどこにあるか・重みを控える
        /// </summary>
        public override void Evaluate (float time)
        {
            evaluated_ = link;
            if (evaluated_ == null) return;
            master_ = evaluated_.MasterFromLocal (time);
            mix_ = evaluated_.WeightAt (master_);
        }
    }

}
