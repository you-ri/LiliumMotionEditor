using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 窓の時計。時刻を 1 か所で持ち、次の写像を受け持つ（S1 段 8）。
    /// - マスター時刻（Timeline など外の時計。S15a）↔ クリップのローカル秒: local = (master - masterOffset) * speed
    /// - クリップのローカル秒 ↔ キーの格子（クリップの frameRate。新規は 60）: 姿勢はローカル秒でそのまま当て、キーは格子に丸めて打つ
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
        [SerializeField] double masterOffset_;
        [SerializeField] double speed_ = 1;
        /// <summary>マスター時刻の格子（1 秒あたりのフレーム数）。0 ならマスターの格子は持たない（止めたらクリップの格子へ揃える）</summary>
        [SerializeField] double masterRate_;

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
        /// マスター時刻からクリップのローカル秒への写像（Timeline のクリップの開始・速度）
        /// </summary>
        public void SetMapping (double masterOffset, double speed)
        {
            SetMapping (masterOffset, speed, 0);
        }

        /// <param name="masterRate">マスターの格子。0 より大きいと、止めたときにマスターの格子へ揃える（スローの演出で、クリップの格子へ揃えると演出の時刻が大きく飛ぶため）</param>
        public void SetMapping (double masterOffset, double speed, double masterRate)
        {
            masterOffset_ = masterOffset;
            speed_ = speed != 0 ? speed : 1;
            masterRate_ = masterRate > 0 ? masterRate : 0;
        }

        /// <summary>マスターの格子（無ければ 0）</summary>
        public double masterRate
        {
            get { return masterRate_; }
        }

        /// <summary>1 秒あたり、マスター時刻に対してクリップのローカル秒が進む量</summary>
        public double speed
        {
            get { return speed_; }
        }

        public double LocalFromMaster (double master)
        {
            return (master - masterOffset_) * speed_;
        }

        public double MasterFromLocal (double local)
        {
            return local / speed_ + masterOffset_;
        }

        public double masterTime
        {
            get { return MasterFromLocal (time_); }
        }

        /// <summary>
        /// 外の時計に合わせる（再生は止める）
        /// </summary>
        public void SetMasterTime (double master)
        {
            // マスターの格子を持つ（演出に合わせている）ときは、クリップが始まる前（ローカルがマイナス）も時刻をそのまま持つ。
            // 姿勢はクリップの頭で止まり（Timeline の Hold と同じ）、演出は正しい時刻になる
            if (masterRate_ > 0) {
                Stop ();
                time_ = LocalFromMaster (master);
                return;
            }
            SetTime (LocalFromMaster (master));
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
            if (masterRate_ > 0) {
                time_ = LocalFromMaster (System.Math.Round (masterTime * masterRate_) / masterRate_);
            } else {
                time_ = keyFrame / (double)rate_;
            }
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
            return Tick (now, 0, length);
        }

        /// <summary>
        /// 再生中なら時刻を進める。start〜end（ローカル秒）の間を回る（演出に合わせているときは、演出の長さぶん）
        /// </summary>
        public bool Tick (double now, double start, double end)
        {
            if (!playing_ || held_) return false;
            double t = playStartTime_ + (now - playStartNow_) * speed_;
            double length = end - start;
            if (length <= 0) {
                time_ = System.Math.Max (0, start);
                return true;
            }
            double offset = (t - start) % length;
            if (offset < 0) offset += length;
            time_ = start + offset;
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
