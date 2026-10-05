using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// サイクルキー（S32）: 最後のキーを先頭のキーに合わせ、つなぎ目で値も傾きも続くようにする。印は .meta に Stacker の設定と並べて持つ（前の Stacker の Loop は印として読む）
    /// </summary>
    public class ClipCycleTests
    {
        const string kFolder = "Assets/__MktClipCycleTest";
        const float kRate = 60;
        static readonly EditorCurveBinding kArm = EditorCurveBinding.FloatCurve ("Controls/FK/LeftUpperArm", typeof (Transform), "m_LocalPosition.x");
        static readonly EditorCurveBinding kHand = EditorCurveBinding.FloatCurve ("Controls/FK/LeftIndexProximal", typeof (Transform), "m_LocalPosition.y");
        const string kHips = "Controls/FK/Hips";

        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = kRate };
        }

        [TearDown]
        public void TearDown ()
        {
            Object.DestroyImmediate (clip_);
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        void Set (EditorCurveBinding binding, params (int frame, float value)[] keys)
        {
            AnimationCurve curve = new AnimationCurve ();
            foreach ((int frame, float value) key in keys) {
                int index = curve.AddKey (key.frame / kRate, key.value);
                AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
            }
            AnimationUtility.SetEditorCurve (clip_, binding, curve);
        }

        AnimationCurve Curve (EditorCurveBinding binding)
        {
            return AnimationUtility.GetEditorCurve (clip_, binding);
        }

        float Value (EditorCurveBinding binding, int frame)
        {
            return Curve (binding).Evaluate (frame / kRate);
        }

        [Test]
        public void SyncCopiesTheFirstKeyToTheLastKey ()
        {
            // 腕: 0 / 10 / 30、指: 5 / 20（先頭にも最後にもキーが無い）
            Set (kArm, (0, 1f), (10, 2f), (30, 3f));
            Set (kHand, (5, 5f), (20, 7f));
            Assert.IsTrue (ClipCycle.Sync (clip_));
            Assert.AreEqual (1f, Value (kArm, 30), 1e-5f, "最後のキーは先頭のキーの値");
            Assert.AreEqual (3, Curve (kHand).length, "先頭にキーの無いカーブにも最後のキーを打つ");
            Assert.AreEqual (5f, Value (kHand, 30), 1e-5f, "先頭の時刻の値（最初のキーより前は最初のキーの値）");
            Assert.AreEqual (new[] { 0, 5, 10, 20, 30 }, ClipCycle.GetFrames (clip_), "キーのフレームは増えない");
            Assert.IsFalse (ClipCycle.Sync (clip_), "合っていれば書かない");

            // 先頭のキーを直すと、最後のキーも合わせ直す
            CurveEdit.SetKey (clip_, kArm, 0, 1.5f);
            Assert.IsTrue (ClipCycle.Sync (clip_));
            Assert.AreEqual (1.5f, Value (kArm, 30), 1e-5f);
        }

        [Test]
        public void TangentsContinueAcrossTheSeam ()
        {
            // 先頭は山でも谷でもない（0.5 → 1 と上がり、最後の 1 つ前の 0 から先頭へも上がる）
            Set (kArm, (0, 0.5f), (10, 1f), (20, 0f), (30, 9f));
            Assert.IsTrue (ClipCycle.Sync (clip_));
            AnimationCurve curve = Curve (kArm);
            float slope = (1f - 0f) / (20 / kRate);
            Assert.AreEqual (slope, curve[0].outTangent, 1e-3f, "次のキーと、最後の 1 つ前のキーを結ぶ傾き");
            Assert.AreEqual (slope, curve[curve.length - 1].inTangent, 1e-3f, "最後のキーも同じ傾き");
            // つなぎ目の前後で、値の変わる速さが同じ
            const float step = 0.1f / kRate;
            float atStart = (curve.Evaluate (step) - curve.Evaluate (0)) / step;
            float atEnd = (curve.Evaluate (30 / kRate) - curve.Evaluate (30 / kRate - step)) / step;
            Assert.AreEqual (atStart, atEnd, 0.05f);
            Assert.IsFalse (ClipCycle.Sync (clip_), "合っていれば書かない");
        }

        [Test]
        public void TangentIsFlatWhenTheFirstKeyIsAPeak ()
        {
            Set (kArm, (0, 2f), (10, 1f), (20, 0f), (30, 9f));
            ClipCycle.Sync (clip_);
            AnimationCurve curve = Curve (kArm);
            Assert.AreEqual (0f, curve[0].outTangent, 1e-5f, "先頭が山なら平ら（行き過ぎない）");
            Assert.AreEqual (0f, curve[curve.length - 1].inTangent, 1e-5f);
        }

        [Test]
        public void ConstantKeysStayConstant ()
        {
            Set (kArm, (0, 1f), (10, 2f), (30, 3f));
            AnimationCurve curve = Curve (kArm);
            AnimationUtility.SetKeyLeftTangentMode (curve, 0, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode (curve, 0, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetEditorCurve (clip_, kArm, curve);
            ClipCycle.Sync (clip_);
            curve = Curve (kArm);
            Assert.AreEqual (AnimationUtility.TangentMode.Constant, AnimationUtility.GetKeyRightTangentMode (curve, 0), "先頭の接線の種類は変えない");
            Assert.AreEqual (AnimationUtility.TangentMode.Constant, AnimationUtility.GetKeyLeftTangentMode (curve, curve.length - 1), "最後のキーは先頭と同じ種類");
            Assert.AreEqual (1f, Value (kArm, 30), 1e-5f);
        }

        [Test]
        public void RotationIsCopiedOnTheSideOfThePreviousKey ()
        {
            // 先頭は単位回転、最後の 1 つ前は単位回転と内積が負の側で持っている四元数
            Quaternion previous = Quaternion.AngleAxis (60, Vector3.up);
            previous = new Quaternion (-previous.x, -previous.y, -previous.z, -previous.w);
            float[] start = { 0, 0, 0, 1 };
            float[] middle = { previous.x, previous.y, previous.z, previous.w };
            for (int a = 0; a < 4; a++) Set (CurveEdit.RotationBinding (kHips, a), (0, start[a]), (20, middle[a]), (30, middle[a]));
            Assert.IsTrue (ClipCycle.Sync (clip_));
            Assert.AreEqual (-1f, Value (CurveEdit.RotationBinding (kHips, 3), 30), 1e-5f, "前のキーと同じ側（w が負）にそろえる");
            Quaternion end = CurveEdit.EvaluateRotation (clip_, kHips, 30 / kRate);
            Assert.Less (Quaternion.Angle (Quaternion.identity, end), 0.01f, "向きは先頭と同じ");
            // 前のキーとの間で遠回りしない（真ん中が 2 つのキーの間の角度に収まる）
            Quaternion between = CurveEdit.EvaluateRotation (clip_, kHips, 25 / kRate);
            Assert.Less (Quaternion.Angle (Quaternion.identity, between), 60.01f);
            Assert.IsFalse (ClipCycle.Sync (clip_), "合っていれば書かない（回転のカーブも）");
        }

        [Test]
        public void RangeNeedsTwoFrames ()
        {
            int first, last;
            Assert.IsFalse (ClipCycle.TryGetRange (new[] { 10 }, out first, out last), "キーが 1 フレームだけ");
            Assert.IsFalse (ClipCycle.TryGetRange (new int[0], out first, out last));
            Assert.IsTrue (ClipCycle.TryGetRange (new[] { -5, 0, 10, 30 }, out first, out last));
            Assert.AreEqual (0, first, "負のフレームは見ない");
            Assert.AreEqual (30, last);
            Set (kArm, (10, 1f));
            Assert.IsFalse (ClipCycle.Sync (clip_), "合わせる相手が無い");
        }

        [Test]
        public void CycleFollowsTheLastKeyButNeverOverwritesAnExistingKey ()
        {
            int[] previous = { 0, 10, 30 };
            Assert.IsTrue (ClipCycle.CanFollow (previous, 30));
            Assert.IsTrue (ClipCycle.CanFollow (previous, 40), "後ろへ動かした・伸ばした");
            Assert.IsTrue (ClipCycle.CanFollow (previous, 20), "前へ動かしたが、前に無かったフレーム");
            Assert.IsFalse (ClipCycle.CanFollow (previous, 10), "サイクルキーを消した: 前からあったキーを上書きしない");
            Assert.IsTrue (ClipCycle.CanFollow (null, 10), "前が分からない");
        }

        [Test]
        public void MarkIsStoredInTheMetaNextToTheStackerSettings ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktClipCycleTest");
            AnimationClip clip = new AnimationClip ();
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.rig.anim");
            Assert.IsFalse (ClipCycle.Load (clip));
            Stacker.Settings settings = Stacker.Load (clip);
            settings.ghost = true;
            Assert.IsTrue (Stacker.Save (clip, settings));
            Assert.IsTrue (ClipCycle.Save (clip, true));
            Assert.IsTrue (ClipCycle.Load (clip));
            Assert.IsTrue (Stacker.Load (clip).ghost, "Stacker の設定は残る");
            settings.selected = "Body";
            Stacker.Save (clip, settings);
            Assert.IsTrue (ClipCycle.Load (clip), "Stacker の設定を保存しても印は残る");
            Assert.IsTrue (ClipCycle.Save (clip, false));
            Assert.IsFalse (ClipCycle.Load (clip));
            Assert.IsFalse (ClipCycle.Save (new AnimationClip (), true), "アセットでないクリップ");

            // 印の無かったころの .meta（Stacker の設定だけ）も読める。前の Loop（stacker.loop）は印として読む
            AssetImporter importer = AssetImporter.GetAtPath (kFolder + "/Walk.rig.anim");
            importer.userData = "{\"stacker\":{\"selected\":\"Body\",\"loop\":false}}";
            Assert.IsFalse (ClipCycle.Load (clip));
            Assert.AreEqual ("Body", Stacker.Load (clip).selected);
            importer.userData = "{\"stacker\":{\"selected\":\"Body\",\"loop\":true}}";
            Assert.IsTrue (ClipCycle.Load (clip), "前の Loop はサイクルキー");
            Assert.IsFalse (Stacker.Load (clip).loop, "Stacker の側からは外す");
            // 印を外して保存すれば、前の Loop も残らない
            ClipCycle.Save (clip, false);
            Assert.IsFalse (ClipCycle.Load (clip));
            StringAssert.Contains ("\"loop\":false", importer.userData);
        }
    }

}
