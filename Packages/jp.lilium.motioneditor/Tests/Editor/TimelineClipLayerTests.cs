using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// Timeline Clip の段（S15b）。演出の中でのクリップの置き方から、時計の写像・ブレンド中の扱い・逆方向の種別を決める。
    /// Timeline の型は使わない（読み出しは Timeline のアセンブリ側。実物での確認は仕様書の測定に残す）
    /// </summary>
    public class TimelineClipLayerTests
    {
        /// <summary>
        /// 演出 Y のクリップ: 演出の 0.5s から 0.4167s、抜けのブレンド 0.05s（重みは線形で落ちるとする）
        /// </summary>
        static ContextClip ClipWithBlendOut ()
        {
            ContextClip clip = new ContextClip {
                track = new ContextTrack { name = "Self Animation", typeName = "AnimationTrack" },
                index = 1,
                start = 0.5,
                duration = 0.4166667,
                clipIn = 0,
                timeScale = 1,
                blendOut = 0.05,
            };
            clip.weightAt = t => Mathf.Clamp01 ((float)((clip.end - t) / clip.blendOut));
            return clip;
        }

        [Test]
        public void ClipMapsMasterTimeLikeTimeline ()
        {
            // 演出 X: 演出 0s からクリップの 0.2333s を 0.1 倍
            ContextClip clip = new ContextClip { start = 0, duration = 2.0833, clipIn = 0.2333333, timeScale = 0.1 };
            Assert.AreEqual (0.3333333, clip.LocalFromMaster (1), 1e-6);
            Assert.AreEqual (1, clip.MasterFromLocal (clip.LocalFromMaster (1)), 1e-9);
            Assert.AreEqual (0, clip.MasterFromLocal (0.2333333), 1e-6);
        }

        [Test]
        public void BlendRegionBlocksEditingAndTheInversePath ()
        {
            ContextClip clip = ClipWithBlendOut ();
            TimelineClipLayer layer = new TimelineClipLayer (() => clip, 5);
            PoseStack stack = PoseStack.FromLayers (new PoseLayer[] { layer });
            string reason;

            // 演出 0.7s = クリップ 0.2s（ブレンドの外）
            layer.Evaluate (0.2f);
            Assert.IsTrue (layer.active);
            Assert.IsFalse (layer.blending);
            Assert.IsNull (layer.editBlockReason);
            Assert.AreEqual (InverseKind.Exact, stack.StepKind (layer, out reason), "姿勢に手を加えないので厳密");

            // 演出 0.8833s = クリップ 0.3833s（抜けのブレンド中）
            layer.Evaluate (0.3833333f);
            Assert.IsTrue (layer.blending);
            Assert.AreEqual (0.67f, layer.mix, 0.02f);
            StringAssert.Contains ("ブレンド中", layer.editBlockReason);
            Assert.AreEqual (InverseKind.None, stack.StepKind (layer, out reason));
            StringAssert.Contains ("ブレンド中", reason);

            // 区間の外は、演出ではこのクリップが出ていないだけで、クリップそのものは直せる
            layer.Evaluate (0.6f);
            Assert.IsTrue (layer.outside);
            Assert.IsNull (layer.editBlockReason);
            StringAssert.Contains ("区間の外", layer.note);
        }

        [Test]
        public void HiddenLayerPassesThroughEvenWhileBlending ()
        {
            ContextClip clip = ClipWithBlendOut ();
            TimelineClipLayer layer = new TimelineClipLayer (() => clip, 5);
            PoseStack stack = PoseStack.FromLayers (new PoseLayer[] { layer });
            layer.Evaluate (0.3833333f);
            layer.enabled = false;
            string reason;
            Assert.IsNull (layer.editBlockReason, "👁 を落とした段はキーを止めない");
            Assert.AreEqual (InverseKind.Exact, stack.StepKind (layer, out reason), "素通し");
        }

        [Test]
        public void OverrideTrackAndMutedTrackAreReported ()
        {
            ContextClip clip = ClipWithBlendOut ();
            clip.overrideTrack = true;
            TimelineClipLayer layer = new TimelineClipLayer (() => clip, 5);
            layer.Evaluate (0.2f);
            Assert.AreEqual (InverseKind.None, layer.inverse);
            StringAssert.Contains ("上書き", layer.inverseReason);

            clip.overrideTrack = false;
            clip.muted = true;
            Assert.IsFalse (layer.canEvaluate, "ミュートのトラックは素通し");
            StringAssert.Contains ("ミュート", layer.note);

            TimelineClipLayer unlinked = new TimelineClipLayer (() => null, 3);
            Assert.IsFalse (unlinked.canEvaluate);
            StringAssert.Contains ("結び付けていない", unlinked.note);
        }

        /// <summary>
        /// 自動で結び付けるときは、焼いた版 → Humanoid Pose のクリップ → 編集中のクリップの順に、同じクリップを使う演出のクリップを探す。
        /// 同じクリップが何度も出るなら先頭
        /// </summary>
        [Test]
        public void MatchingPrefersTheEarlierSourceAndTheEarliestUse ()
        {
            AnimationClip baked = new AnimationClip { name = "baked" };
            AnimationClip humanoid = new AnimationClip { name = "humanoid" };
            try {
                List<ContextClip> candidates = new List<ContextClip> {
                    new ContextClip { clip = humanoid, start = 0.2 },
                    new ContextClip { clip = baked, start = 1.0 },
                    new ContextClip { clip = baked, start = 0.5 },
                };
                ContextClip found = PreviewWindow.FindMatchingTimelineClip (candidates, new[] { baked, humanoid });
                Assert.AreSame (candidates[2], found);
                found = PreviewWindow.FindMatchingTimelineClip (candidates, new AnimationClip[] { null, humanoid });
                Assert.AreSame (candidates[0], found);
                Assert.IsNull (PreviewWindow.FindMatchingTimelineClip (candidates, new[] { new AnimationClip () }));
            }
            finally {
                Object.DestroyImmediate (baked);
                Object.DestroyImmediate (humanoid);
            }
        }
    }

}
