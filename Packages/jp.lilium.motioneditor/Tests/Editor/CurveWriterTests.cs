using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// キーの書き出し口（CurveWriter）
    /// </summary>
    public class CurveWriterTests
    {
        readonly List<Object> created_ = new List<Object> ();
        AnimationClip clip_;
        Transform root_;
        Transform bone_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = 60 };
            created_.Add (clip_);
            root_ = new GameObject ("Root").transform;
            created_.Add (root_.gameObject);
            Transform controls = new GameObject ("Controls").transform;
            controls.SetParent (root_, false);
            bone_ = new GameObject ("Bone").transform;
            bone_.SetParent (controls, false);
        }

        [TearDown]
        public void TearDown ()
        {
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        AnimationCurve Curve (string path, System.Type type, string property)
        {
            return AnimationUtility.GetEditorCurve (clip_, EditorCurveBinding.FloatCurve (path, type, property));
        }

        void Write (float frame, System.Action<CurveWriter> write)
        {
            using (CurveWriter writer = CurveWriter.Begin (clip_, root_, frame, "Test")) {
                write (writer);
            }
        }

        /// <summary>
        /// キーの自動追加が切のとき: 今のフレームにキーがある物（パス）だけ書き、キーの無い物・フレームには打たない。
        /// キーのある物なら、カーブの無いチャンネル（ここでは回転）も書く
        /// </summary>
        [Test]
        public void OnlyExistingKeysSkipsCurvesWithoutKeyAtFrame ()
        {
            bone_.localPosition = new Vector3 (1, 2, 3);
            Write (5, w => w.Transform (bone_, TransformChannels.Position));

            Transform other = new GameObject ("Other").transform;
            other.SetParent (bone_.parent, false);
            bone_.localPosition = new Vector3 (4, 5, 6);
            other.localPosition = new Vector3 (7, 8, 9);
            int written = 0, skipped = 0;
            using (CurveWriter writer = CurveWriter.Begin (clip_, root_, 5, "Test")) {
                writer.onlyExistingKeys = true;
                writer.Transform (bone_, TransformChannels.Position | TransformChannels.Rotation);
                writer.Transform (other, TransformChannels.Position);
                writer.Dispose ();
                written = writer.writtenCount;
                skipped = writer.skippedCount;
            }
            Assert.AreEqual (7, written);
            Assert.AreEqual (3, skipped);
            Assert.IsNotNull (Curve ("Controls/Bone", typeof (Transform), "m_LocalRotation.w"));
            Assert.AreEqual (5, Curve ("Controls/Bone", typeof (Transform), "m_LocalPosition.y").keys[0].value, 1e-6f);
            Assert.IsNull (Curve ("Controls/Other", typeof (Transform), "m_LocalPosition.y"));

            // 別のフレーム（キーが無い）には打たない
            bone_.localPosition = Vector3.zero;
            using (CurveWriter writer = CurveWriter.Begin (clip_, root_, 10, "Test")) {
                writer.onlyExistingKeys = true;
                writer.Transform (bone_, TransformChannels.Position);
            }
            Assert.AreEqual (1, Curve ("Controls/Bone", typeof (Transform), "m_LocalPosition.y").length);
        }

        [Test]
        public void WritesTransformChannelsWithPathFromRoot ()
        {
            bone_.localPosition = new Vector3 (1, 2, 3);
            bone_.localRotation = Quaternion.Euler (10, 20, 30);
            Write (5, w => w.Transform (bone_, TransformChannels.Position | TransformChannels.Rotation));

            string[] properties = AnimationUtility.GetCurveBindings (clip_).Select (b => b.path + ":" + b.propertyName).OrderBy (x => x).ToArray ();
            CollectionAssert.AreEqual (new[] {
                "Controls/Bone:m_LocalPosition.x", "Controls/Bone:m_LocalPosition.y", "Controls/Bone:m_LocalPosition.z",
                "Controls/Bone:m_LocalRotation.w", "Controls/Bone:m_LocalRotation.x", "Controls/Bone:m_LocalRotation.y", "Controls/Bone:m_LocalRotation.z",
            }, properties);
            AnimationCurve y = Curve ("Controls/Bone", typeof (Transform), "m_LocalPosition.y");
            Assert.AreEqual (1, y.length);
            Assert.AreEqual (5 / 60f, y.keys[0].time, 1e-6f);
            Assert.AreEqual (2, y.keys[0].value, 1e-6f);
        }

        [Test]
        public void WritesFloatOfComponent ()
        {
            Write (0, w => w.Float (bone_, typeof (IkControl), IkControl.kIkWeightProperty, 0.25f));
            Write (10, w => w.Float (bone_, typeof (IkControl), IkControl.kIkWeightProperty, 1));

            AnimationCurve curve = Curve ("Controls/Bone", typeof (IkControl), IkControl.kIkWeightProperty);
            Assert.IsNotNull (curve);
            Assert.AreEqual (2, curve.length);
            Assert.AreEqual (0.25f, curve.keys[0].value);
            Assert.AreEqual (1, curve.keys[1].value);
        }

        /// <summary>
        /// キーの間を広げても（近くに打ったキーを遠くへ動かす・間のキーを消す）、キーの間で値が行き過ぎない。
        /// 打ったときの両隣で接線を固定すると、2 フレーム間隔の傾きが 90 フレーム続いて大きく外れていた
        /// </summary>
        [Test]
        public void KeysGetAutoTangents_SoWideningTheGapDoesNotOvershoot ()
        {
            string path = "Controls/Bone";
            Write (0, w => w.Float (bone_, typeof (IkControl), IkControl.kIkWeightProperty, 0));
            Write (2, w => w.Float (bone_, typeof (IkControl), IkControl.kIkWeightProperty, 0.25f));
            AnimationCurve curve = Curve (path, typeof (IkControl), IkControl.kIkWeightProperty);
            for (int i = 0; i < curve.length; i++) {
                Assert.AreEqual (AnimationUtility.TangentMode.ClampedAuto, AnimationUtility.GetKeyLeftTangentMode (curve, i));
                Assert.AreEqual (AnimationUtility.TangentMode.ClampedAuto, AnimationUtility.GetKeyRightTangentMode (curve, i));
            }

            // 2F のキーを 90F へ動かす
            ClipKeyUtility.MoveKeysAtFrame (clip_, 2, 90, false);
            curve = Curve (path, typeof (IkControl), IkControl.kIkWeightProperty);
            for (int f = 0; f <= 90; f += 5) {
                float value = curve.Evaluate (f / 60f);
                Assert.GreaterOrEqual (value, -1e-4f, f + "F で下へ行き過ぎない");
                Assert.LessOrEqual (value, 0.25f + 1e-4f, f + "F で上へ行き過ぎない");
            }

            // 間に打ったキーを消しても同じ
            Write (45, w => w.Float (bone_, typeof (IkControl), IkControl.kIkWeightProperty, 0.24f));
            ClipKeyUtility.RemoveKeysAtFrame (clip_, 45);
            curve = Curve (path, typeof (IkControl), IkControl.kIkWeightProperty);
            Assert.AreEqual (0.125f, curve.Evaluate (0.75f), 0.01f, "真ん中はおよそ半分");
        }

        [Test]
        public void SameFrameIsReplacedAndNothingIsWrittenUntilDisposed ()
        {
            bone_.localPosition = Vector3.one;
            Write (3, w => w.Transform (bone_, TransformChannels.Position));

            CurveWriter writer = CurveWriter.Begin (clip_, root_, 3, "Test");
            bone_.localPosition = Vector3.one * 2;
            writer.Transform (bone_, TransformChannels.Position);
            Assert.AreEqual (1, Curve ("Controls/Bone", typeof (Transform), "m_LocalPosition.x").keys[0].value, "Dispose までは書かない");
            writer.Dispose ();
            writer.Dispose ();

            AnimationCurve x = Curve ("Controls/Bone", typeof (Transform), "m_LocalPosition.x");
            Assert.AreEqual (1, x.length);
            Assert.AreEqual (2, x.keys[0].value);
        }

        [Test]
        public void RotationSignFollowsNeighborKey ()
        {
            Quaternion q = Quaternion.Euler (0, 170, 0);
            bone_.localRotation = q;
            Write (0, w => w.Transform (bone_, TransformChannels.Rotation));

            // 同じ向きの近い回転を、符号が逆の表し方で打つ
            Quaternion next = Quaternion.Euler (0, 190, 0);
            Quaternion flipped = new Quaternion (-next.x, -next.y, -next.z, -next.w);
            Quaternion first = new Quaternion (
                Curve ("Controls/Bone", typeof (Transform), "m_LocalRotation.x").keys[0].value,
                Curve ("Controls/Bone", typeof (Transform), "m_LocalRotation.y").keys[0].value,
                Curve ("Controls/Bone", typeof (Transform), "m_LocalRotation.z").keys[0].value,
                Curve ("Controls/Bone", typeof (Transform), "m_LocalRotation.w").keys[0].value);
            if (Quaternion.Dot (first, flipped) > 0) flipped = next;
            Assert.That (Quaternion.Dot (first, flipped), Is.LessThan (0), "前提: 逆の側で打つ");

            CurveWriter writer = CurveWriter.Begin (clip_, root_, 10, "Test");
            string path = writer.PathOf (bone_);
            writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.x"), flipped.x);
            writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.y"), flipped.y);
            writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.z"), flipped.z);
            writer.Set (EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.w"), flipped.w);
            writer.Dispose ();

            Quaternion second = new Quaternion (
                Curve (path, typeof (Transform), "m_LocalRotation.x").keys[1].value,
                Curve (path, typeof (Transform), "m_LocalRotation.y").keys[1].value,
                Curve (path, typeof (Transform), "m_LocalRotation.z").keys[1].value,
                Curve (path, typeof (Transform), "m_LocalRotation.w").keys[1].value);
            Assert.That (Quaternion.Dot (first, second), Is.GreaterThan (0));
            Assert.That (Quaternion.Angle (next, second), Is.LessThan (0.01f), "向きは変わらない");
        }

        [Test]
        public void SetTargetConvertsTransformButSetKeepsRaw ()
        {
            // Override のように「狙いの値 → クリップに入れる値」を直す口（ここでは x を 2 倍）
            string path = "Controls/Bone";
            EditorCurveBinding x = EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalPosition.x");
            EditorCurveBinding weight = EditorCurveBinding.FloatCurve (path, typeof (IkControl), IkControl.kIkWeightProperty);
            using (CurveWriter writer = CurveWriter.Begin (clip_, root_, 0, "Test")) {
                writer.storePosition = (p, v) => new Vector3 (v.x * 2, v.y, v.z);
                writer.SetTarget (x, 1.5f);
                writer.SetTarget (weight, 0.5f);
            }
            Assert.AreEqual (3, Curve (path, typeof (Transform), "m_LocalPosition.x").keys[0].value, 1e-6f, "SetTarget は変換を通す");
            Assert.AreEqual (0.5f, Curve (path, typeof (IkControl), IkControl.kIkWeightProperty).keys[0].value, 1e-6f, "数値はそのまま");

            using (CurveWriter writer = CurveWriter.Begin (clip_, root_, 10, "Test")) {
                writer.storePosition = (p, v) => new Vector3 (v.x * 2, v.y, v.z);
                writer.Set (x, 1.5f);
            }
            Assert.AreEqual (1.5f, Curve (path, typeof (Transform), "m_LocalPosition.x").keys[1].value, 1e-6f, "Set は変換しない");
        }

        [Test]
        public void MemoryClipDoesNotDirtyScene ()
        {
            bool dirty = UnityEngine.SceneManagement.SceneManager.GetActiveScene ().isDirty;
            Write (0, w => w.Transform (bone_, TransformChannels.Rotation));
            Assert.AreEqual (dirty, UnityEngine.SceneManagement.SceneManager.GetActiveScene ().isDirty);
        }
    }

}
