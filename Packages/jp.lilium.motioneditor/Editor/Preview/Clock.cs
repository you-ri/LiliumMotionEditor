using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 窓の時計。時刻を 1 か所で持ち、クリップのローカル秒 ↔ キーの格子（クリップの frameRate。新規は 60）を受け持つ（S1 段 8）。
    /// 姿勢はローカル秒でそのまま当て、キーは格子に丸めて打つ
    ///
    /// 再生中につかむと止め（Hold）、離すと続きから再生する（Release）。止めている間は時刻が進まない（毎 tick のサンプルもしない）
    /// </summary>
    [System.Serializable]
    public sealed class Clock
    {
        /// <summary>
        /// 格子の上にいるとみなす差（フレーム）
        /// </summary>
        public const float kGridEpsilon = 1e-3f;

        [SerializeField] double time_;
        [SerializeField] float rate_ = 60;

        bool playing_;
        bool held_;
        double playStartNow_;
        double playStartTime_;

        /// <summary>
        /// クリップのローカル秒
        /// </summary>
        public double time
        {
            get { return time_; }
        }

        /// <summary>
        /// 格子の細かさ（1 秒あたりのフレーム数）
        /// </summary>
        public float rate
        {
            get { return rate_; }
            set { rate_ = value > 0 ? value : 60; }
        }

        /// <summary>
        /// 今のフレーム（端数あり）
        /// </summary>
        public float frame
        {
            get { return (float)(time_ * rate_); }
        }

        /// <summary>
        /// キーを打つフレーム（今のフレームを格子に丸めたもの）
        /// </summary>
        public int keyFrame
        {
            get { return Mathf.Max (0, Mathf.RoundToInt (frame)); }
        }

        /// <summary>
        /// 今の時刻が格子の上にあるか（無ければ、キーは丸めた所に打たれる）
        /// </summary>
        public bool onGrid
        {
            get { return Mathf.Abs (frame - Mathf.Round (frame)) < kGridEpsilon; }
        }

        public bool isPlaying
        {
            get { return playing_; }
        }

        /// <summary>
        /// 再生中につかんで止めているか
        /// </summary>
        public bool isHeld
        {
            get { return playing_ && held_; }
        }

        /// <summary>
        /// 時刻を置く（再生は止める。端数はそのまま）
        /// </summary>
        public void SetTime (double seconds)
        {
            Stop ();
            time_ = System.Math.Max (0, seconds);
        }

        /// <summary>
        /// 格子のフレームへ移る（再生は止める）
        /// </summary>
        public void SetFrame (int frame)
        {
            SetTime (Mathf.Max (0, frame) / (double)rate_);
        }

        public void Play (double now)
        {
            playing_ = true;
            held_ = false;
            playStartNow_ = now;
            playStartTime_ = time_;
        }

        /// <summary>
        /// 再生を止めて、表示中のフレーム（丸めたもの）に揃える。止めた後に打つキーと見た目が一致するように
        /// </summary>
        public void Stop ()
        {
            if (!playing_) return;
            playing_ = false;
            held_ = false;
            time_ = keyFrame / (double)rate_;
        }

        /// <summary>
        /// つかんだとき。再生中なら時刻をそこで止める
        /// </summary>
        public void Hold ()
        {
            if (!playing_ || held_) return;
            held_ = true;
        }

        /// <summary>
        /// 離したとき。止めた時刻から再生を続ける
        /// </summary>
        public void Release (double now)
        {
            if (!held_) return;
            held_ = false;
            if (playing_) Play (now);
        }

        /// <summary>
        /// 再生中なら時刻を進める。進めたら true（姿勢を当て直す）。長さ（秒）で頭へ戻る
        /// </summary>
        public bool Tick (double now, double length)
        {
            if (!playing_ || held_) return false;
            if (length <= 0) {
                time_ = 0;
                return true;
            }
            double t = (playStartTime_ + now - playStartNow_) % length;
            time_ = t < 0 ? t + length : t;
            return true;
        }

        /// <summary>
        /// 画面に出す今のフレーム。格子に乗っていなければ、キーの行き先も添える（例: 12.4 → 12）
        /// </summary>
        public string Describe ()
        {
            if (onGrid) return keyFrame.ToString ();
            return frame.ToString ("0.0") + " → " + keyFrame;
        }
    }

}
