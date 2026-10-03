using NUnit.Framework;

namespace Lilium
{

    /// <summary>
    /// 窓の時計（マスター時刻 ↔ クリップの秒 ↔ キーの格子、再生中につかんだら止める）
    /// </summary>
    public class ClockTests
    {
        [Test]
        public void FrameMapsToSecondsOnGrid ()
        {
            Clock clock = new Clock ();
            clock.SetFrame (30);
            Assert.AreEqual (0.5, clock.time, 1e-9);
            Assert.AreEqual (30, clock.keyFrame);
            Assert.IsTrue (clock.onGrid);
            Assert.AreEqual ("30", clock.Describe ());

            clock.SetFrame (-5);
            Assert.AreEqual (0, clock.time, "マイナスには行かない");
        }

        [Test]
        public void OffGridTimeRoundsTheKeyFrameAndSaysSo ()
        {
            Clock clock = new Clock ();
            clock.SetTime (12.4 / 60);
            Assert.AreEqual (12.4f, clock.frame, 1e-4f);
            Assert.AreEqual (12, clock.keyFrame);
            Assert.IsFalse (clock.onGrid);
            Assert.AreEqual ("12.4 → 12", clock.Describe ());

            clock.SetTime (12.6 / 60);
            Assert.AreEqual (13, clock.keyFrame, "キーは近い方の格子へ");
        }

        [Test]
        public void GridFollowsClipFrameRate ()
        {
            Clock clock = new Clock ();
            clock.rate = 30;
            clock.SetFrame (15);
            Assert.AreEqual (0.5, clock.time, 1e-9);
            clock.rate = 60;
            Assert.AreEqual (30, clock.keyFrame, "時刻は秒で持つので、格子を変えても同じ時刻");
            clock.rate = 0;
            Assert.AreEqual (60, clock.rate, "0 は既定に戻す");
        }

        [Test]
        public void MasterTimeMapsThroughOffsetAndSpeed ()
        {
            Clock clock = new Clock ();
            clock.SetMapping (2, 0.5);
            Assert.AreEqual (0.5, clock.LocalFromMaster (3), 1e-9);
            Assert.AreEqual (3, clock.MasterFromLocal (0.5), 1e-9);

            clock.SetMasterTime (4);
            Assert.AreEqual (1, clock.time, 1e-9);
            Assert.AreEqual (4, clock.masterTime, 1e-9);
        }

        [Test]
        public void PlaybackAdvancesAndLoops ()
        {
            Clock clock = new Clock ();
            clock.SetFrame (10);
            clock.Play (100);
            Assert.IsTrue (clock.Tick (100.1, 1));
            Assert.AreEqual (16, clock.frame, 1e-3f);

            Assert.IsTrue (clock.Tick (101.0, 1));
            Assert.AreEqual (10, clock.frame, 1e-3f, "長さ 1 秒で頭へ戻る");
        }

        /// <summary>
        /// 演出に合わせて再生するとき（S15b）: 演出の速さ（クリップの速さ）で進み、演出の頭から終わりまでを回る
        /// </summary>
        [Test]
        public void PlaybackFollowsTheMasterSpeedWithinARange ()
        {
            Clock clock = new Clock ();
            // 演出 X: 演出 0s でクリップ 0.2333s、0.1 倍
            clock.SetMapping (-2.3333333, 0.1, 60);
            clock.SetMasterTime (0);
            double start = clock.LocalFromMaster (0), end = clock.LocalFromMaster (2);
            clock.Play (100);
            Assert.IsTrue (clock.Tick (101, start, end));
            Assert.AreEqual (1, clock.masterTime, 1e-6, "実時間 1 秒で演出が 1 秒進む");
            Assert.AreEqual (start + 0.1, clock.time, 1e-6, "クリップは 0.1 秒だけ進む");
            Assert.IsTrue (clock.Tick (102.5, start, end));
            Assert.AreEqual (0.5, clock.masterTime, 1e-6, "演出の終わりで頭へ戻る");
        }

        /// <summary>
        /// 演出の格子を持つとき（スローの演出に合わせているとき）は、止めたら演出のフレームへ揃える。
        /// クリップの格子へ揃えると、0.1 倍では演出の時刻が最大 5F 飛ぶ
        /// </summary>
        [Test]
        public void StopSnapsToTheMasterGridWhenFollowingASlowClip ()
        {
            Clock clock = new Clock ();
            clock.SetMapping (-2.3333333, 0.1, 60);
            clock.SetMasterTime (0);
            clock.Play (100);
            clock.Tick (100.52, 0, 10);
            clock.Stop ();
            Assert.AreEqual (0.5166667, clock.masterTime, 1e-5, "演出の 31F へ揃う");
            Assert.IsFalse (clock.onGrid, "クリップの格子には乗らない（キーは丸めた所に打つ）");

            // 演出に合わせているときは、クリップが始まる前（ローカルがマイナス）も演出の時刻を保つ
            clock.SetMapping (0.5, 1, 60);
            clock.SetMasterTime (0.3);
            Assert.AreEqual (0.3, clock.masterTime, 1e-9);
            Assert.AreEqual (0, clock.keyFrame, "キーはクリップの頭に打つ");

            clock.SetMapping (0, 1);
            clock.SetTime (12.4 / 60);
            clock.Play (0);
            clock.Stop ();
            Assert.AreEqual (12, clock.frame, 1e-4f, "演出の格子が無ければ今までどおりクリップの格子へ");
        }

        [Test]
        public void HoldStopsTheClockAndReleaseContinuesFromThere ()
        {
            Clock clock = new Clock ();
            clock.Play (0);
            clock.Tick (0.21, 10);
            Assert.AreEqual (12.6f, clock.frame, 1e-3f);

            clock.Hold ();
            Assert.IsTrue (clock.isHeld);
            Assert.IsFalse (clock.Tick (5, 10), "止めている間は進めない（姿勢も当て直さない）");
            Assert.AreEqual (12.6f, clock.frame, 1e-3f);
            Assert.AreEqual (13, clock.keyFrame, "つかんでいる間のキーは丸めたフレームへ");

            clock.Release (5);
            Assert.IsTrue (clock.isPlaying);
            Assert.IsFalse (clock.isHeld);
            clock.Tick (5.1, 10);
            Assert.AreEqual (18.6f, clock.frame, 1e-3f, "止めた所から続ける（止めていた時間は飛ばさない）");
        }

        [Test]
        public void HoldDoesNothingWhenStopped ()
        {
            Clock clock = new Clock ();
            clock.SetFrame (5);
            clock.Hold ();
            Assert.IsFalse (clock.isHeld);
            clock.Release (1);
            Assert.IsFalse (clock.isPlaying);
            Assert.IsFalse (clock.Tick (2, 10));
        }

        [Test]
        public void StopSnapsToTheShownFrame ()
        {
            Clock clock = new Clock ();
            clock.Play (0);
            clock.Tick (0.21, 10);
            clock.Stop ();
            Assert.IsFalse (clock.isPlaying);
            Assert.IsTrue (clock.onGrid, "止めたら見た目とキーの位置をそろえる");
            Assert.AreEqual (13, clock.keyFrame);
        }

        [Test]
        public void SettingTheFrameStopsPlayback ()
        {
            Clock clock = new Clock ();
            clock.Play (0);
            clock.Hold ();
            clock.SetFrame (3);
            Assert.IsFalse (clock.isPlaying);
            Assert.IsFalse (clock.isHeld);
            Assert.AreEqual (3, clock.keyFrame);
        }
    }

}
