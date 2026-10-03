using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// キーの書き込み（1 か所に集めたもの）とポーズの貼り付け
    /// </summary>
    public class ClipWriteTests
    {
        static readonly EditorCurveBinding kBinding = EditorCurveBinding.FloatCurve ("Hips", typeof (Transform), "m_LocalPosition.x");
        static readonly EditorCurveBinding kOtherBinding = EditorCurveBinding.FloatCurve ("Hips/Spine", typeof (Transform), "m_LocalPosition.y");

        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = 60 };
        }

        [TearDown]
        public void TearDown ()
        {
            Object.DestroyImmediate (clip_);
        }

        void SetKey (EditorCurveBinding binding, float frame, float value)
        {
            using (CurveWriter writer = CurveWriter.Begin (clip_, null, frame, "Test")) {
                writer.Set (binding, value);
            }
        }

        Keyframe[] Keys (EditorCurveBinding binding)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, binding);
            return curve != null ? curve.keys : new Keyframe[0];
        }

        /// <summary>
        /// タイムラインのキーのコピー・貼り付け（Ctrl+C / Ctrl+V）。一番前のフレームからのずれを保って今のフレームへ貼り、
        /// トラックの絞り込みの外のカーブは写さず、行き先のキーは上書きする
        /// </summary>
        [Test]
        public void TimelineClipboardPastesSelectedKeysRelativeToFirstFrame ()
        {
            SetKey (kBinding, 2, 1);
            SetKey (kBinding, 5, 2);
            SetKey (kOtherBinding, 5, 7);
            SetKey (kBinding, 21, 9);
            try {
                // Hips のトラックの 2F と 5F を選んだ（Hips/Spine は選んでいない）
                System.Predicate<EditorCurveBinding> hips = b => b.path == "Hips";
                var keys = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, System.Predicate<EditorCurveBinding>>> {
                    new System.Collections.Generic.KeyValuePair<int, System.Predicate<EditorCurveBinding>> (2, hips),
                    new System.Collections.Generic.KeyValuePair<int, System.Predicate<EditorCurveBinding>> (5, hips),
                };
                Assert.AreEqual (2, TimelineClipboard.Copy (clip_, keys));
                Assert.AreEqual (2, TimelineClipboard.Paste (clip_, 18));

                Keyframe[] pasted = Keys (kBinding);
                Assert.AreEqual (new[] { 2, 5, 18, 21 }, System.Array.ConvertAll (pasted, k => Mathf.RoundToInt (k.time * 60)));
                Assert.AreEqual (1, pasted[2].value);
                // 21F にあったキーは 5F のキーで上書き
                Assert.AreEqual (2, pasted[3].value);
                // 選んでいないカーブは写さない
                Assert.AreEqual (1, Keys (kOtherBinding).Length);
            }
            finally {
                TimelineClipboard.Clear ();
            }
        }

        [Test]
        public void FirstKeyDoesNotAddNegativeTimeKey ()
        {
            SetKey (kBinding, 10, 1.5f);

            Keyframe[] keys = Keys (kBinding);
            Assert.AreEqual (1, keys.Length);
            Assert.AreEqual (10 / 60f, keys[0].time, 1e-6f);
            Assert.AreEqual (1.5f, keys[0].value);
        }

        [Test]
        public void KeyAtSameFrameIsReplaced ()
        {
            SetKey (kBinding, 0, 1);
            SetKey (kBinding, 5, 2);
            SetKey (kBinding, 5, 3);

            Keyframe[] keys = Keys (kBinding);
            Assert.AreEqual (2, keys.Length);
            Assert.AreEqual (3, keys[1].value);
        }

        [Test]
        public void PasteWritesAllCopiedCurvesIncludingMissingOnes ()
        {
            SetKey (kBinding, 0, 1);
            SetKey (kBinding, 10, 3);
            SetKey (kOtherBinding, 0, 7);
            PoseClipboard.Copy (clip_, 5);

            AnimationClip other = new AnimationClip { frameRate = 60 };
            try {
                using (CurveWriter writer = CurveWriter.Begin (other, null, 20, "Paste")) {
                    PoseClipboard.Paste (writer);
                }
                AnimationCurve curve = AnimationUtility.GetEditorCurve (other, kBinding);
                Assert.IsNotNull (curve);
                Assert.AreEqual (1, curve.length);
                Assert.AreEqual (20 / 60f, curve.keys[0].time, 1e-6f);
                Assert.AreEqual (2, curve.keys[0].value, 1e-4f);
                Assert.IsNotNull (AnimationUtility.GetEditorCurve (other, kOtherBinding));
            }
            finally {
                Object.DestroyImmediate (other);
            }
        }

        /// <summary>
        /// 選んだ物のポーズのコピー（選んでいるときの C）。絞り込んだカーブだけを写し、貼り先のほかのカーブは触らない
        /// </summary>
        [Test]
        public void FilteredCopyPastesOnlySelectedCurves ()
        {
            SetKey (kBinding, 0, 1);
            SetKey (kBinding, 10, 3);
            SetKey (kOtherBinding, 0, 7);
            Assert.AreEqual (1, PoseClipboard.Copy (clip_, 5, b => b.path == "Hips"));

            using (CurveWriter writer = CurveWriter.Begin (clip_, null, 20, "Paste")) {
                PoseClipboard.Paste (writer);
            }

            Keyframe[] pasted = Keys (kBinding);
            Assert.AreEqual (3, pasted.Length);
            Assert.AreEqual (20 / 60f, pasted[2].time, 1e-6f);
            Assert.AreEqual (2, pasted[2].value, 1e-4f);
            // 選んでいないカーブにはキーを打たない
            Assert.AreEqual (1, Keys (kOtherBinding).Length);
        }

        [Test]
        public void PasteOverwritesKeyAtTargetFrame ()
        {
            SetKey (kBinding, 0, 1);
            PoseClipboard.Copy (clip_, 0);
            SetKey (kBinding, 0, 9);

            using (CurveWriter writer = CurveWriter.Begin (clip_, null, 0, "Paste")) {
                PoseClipboard.Paste (writer);
            }

            Keyframe[] keys = Keys (kBinding);
            Assert.AreEqual (1, keys.Length);
            Assert.AreEqual (1, keys[0].value);
        }
    }

}
