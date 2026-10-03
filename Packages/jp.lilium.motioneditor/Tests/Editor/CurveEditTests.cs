using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// カーブエディタ（S5）の書き換え: 数値のキー・回転を角度で直す・接線・移動・削除
    /// </summary>
    public class CurveEditTests
    {
        const string kPath = "Controls/FK/LeftUpperArm";
        static readonly EditorCurveBinding kWeight = EditorCurveBinding.FloatCurve ("Controls/IK/LeftArm", typeof (Component), "ikWeight");

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

        /// <summary>
        /// 選んでいる骨のキーを 1 つだけにする / 全部消す（読込の後で指だけ打ち直す）。他の骨のカーブは変えない
        /// </summary>
        [Test]
        public void KeepOnlyKeyAtFrameLeavesOtherCurves ()
        {
            EditorCurveBinding finger = EditorCurveBinding.FloatCurve ("Controls/FK/LeftIndexProximal", typeof (Transform), "m_LocalRotation.x");
            EditorCurveBinding arm = EditorCurveBinding.FloatCurve (kPath, typeof (Transform), "m_LocalRotation.x");
            AnimationUtility.SetEditorCurve (clip_, finger, AnimationCurve.Linear (0, 0, 1, 0.6f));
            AnimationUtility.SetEditorCurve (clip_, arm, AnimationCurve.Linear (0, 0, 1, 1));
            System.Predicate<EditorCurveBinding> fingers = b => b.path == finger.path;

            Assert.AreEqual (1, ClipKeyUtility.KeepOnlyKeyAtFrame (clip_, 30, fingers));
            Keyframe[] keys = AnimationUtility.GetEditorCurve (clip_, finger).keys;
            Assert.AreEqual (1, keys.Length);
            Assert.AreEqual (0.5f, keys[0].time, 1e-5f);
            Assert.AreEqual (0.3f, keys[0].value, 1e-5f, "そのフレームの値");
            Assert.AreEqual (2, AnimationUtility.GetEditorCurve (clip_, arm).length, "選んでいない骨はそのまま");

            ClipKeyUtility.KeepOnlyKeyAtFrame (clip_, -1, fingers);
            Assert.IsNull (AnimationUtility.GetEditorCurve (clip_, finger), "負のフレームならカーブごと消す");
            Assert.IsNotNull (AnimationUtility.GetEditorCurve (clip_, arm));
        }

        [Test]
        public void SetKeyAddsOrUpdates ()
        {
            CurveEdit.SetKey (clip_, kWeight, 0, 0);
            CurveEdit.SetKey (clip_, kWeight, 30, 1);
            CurveEdit.SetKey (clip_, kWeight, 30, 0.5f);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, kWeight);
            Assert.AreEqual (2, curve.length);
            Assert.AreEqual (0.5f, curve.Evaluate (0.5f), 1e-5f);
            Assert.That (curve.Evaluate (0.25f), Is.InRange (0f, 0.5f), "ClampedAuto で行き過ぎない");
        }

        [Test]
        public void RotationAngleEditKeepsOtherAxes ()
        {
            CurveEdit.SetRotationKey (clip_, kPath, 0, Quaternion.Euler (10, 20, 30));
            CurveEdit.SetRotationAngle (clip_, kPath, 0, 1, 170, new Vector3 (10, 20, 30));
            Vector3 euler = EulerAngles.Closest (CurveEdit.EvaluateRotation (clip_, kPath, 0), new Vector3 (10, 170, 30));
            Assert.That (Vector3.Distance (new Vector3 (10, 170, 30), euler), Is.LessThan (0.01f), euler.ToString ());
            Assert.AreEqual (4, AnimationUtility.GetCurveBindings (clip_).Length, "4 本とも書く");
        }

        [Test]
        public void RotationKeysStayOnTheSameHemisphere ()
        {
            CurveEdit.SetRotationKey (clip_, kPath, 0, Quaternion.Euler (0, 170, 0));
            CurveEdit.SetRotationKey (clip_, kPath, 10, Quaternion.Euler (0, -170, 0));
            // 補間の途中は 180° を通る（-10° 側を遠回りしない）
            Vector3 middle = EulerAngles.Closest (CurveEdit.EvaluateRotation (clip_, kPath, 5 / 60f), new Vector3 (0, 180, 0));
            Assert.AreEqual (180, middle.y, 1f);
        }

        [Test]
        public void MoveRemoveAndTangents ()
        {
            CurveEdit.SetKey (clip_, kWeight, 0, 0);
            CurveEdit.SetKey (clip_, kWeight, 10, 1);
            CurveEdit.SetKey (clip_, kWeight, 20, 0);
            CurveEdit.MoveKey (clip_, kWeight, 10, 15, 0.8f);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, kWeight);
            Assert.AreEqual (new[] { 0, 15, 20 }, ClipKeyUtility.GetKeyFrames (clip_, b => b == kWeight));
            Assert.AreEqual (0.8f, curve.Evaluate (15 / 60f), 1e-5f);

            Assert.IsTrue (CurveEdit.SetTangentMode (clip_, kWeight, 0, AnimationUtility.TangentMode.Constant));
            curve = AnimationUtility.GetEditorCurve (clip_, kWeight);
            Assert.AreEqual (0f, curve.Evaluate (10 / 60f), 1e-5f, "Constant は次のキーまで平ら");

            CurveEdit.SetRotationKey (clip_, kPath, 5, Quaternion.Euler (0, 30, 0));
            CurveEdit.MoveKey (clip_, CurveEdit.RotationBinding (kPath, 2), 5, 8, null);
            Assert.AreEqual (new[] { 8 }, ClipKeyUtility.GetKeyFrames (clip_, b => b.path == kPath), "回転は 4 本とも動く");
            CurveEdit.RemoveKey (clip_, CurveEdit.RotationBinding (kPath, 0), 8);
            Assert.IsEmpty (ClipKeyUtility.GetKeyFrames (clip_, b => b.path == kPath));
        }
    }

}
