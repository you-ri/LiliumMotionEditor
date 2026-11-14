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

        /// <summary>再生を止めたら、クリップの格子へ揃える（止めた後に打つキーと見た目を合わせる）</summary>
        [Test]
        public void StopSnapsToTheClipGrid ()
        {
            Clock clock = new Clock ();
            clock.SetTime (12.4 / 60);
            clock.Play (0);
            clock.Stop ();
            Assert.AreEqual (12, clock.frame, 1e-4f);
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
