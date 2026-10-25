using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// タイムラインで選んだキーの間の矢印の補間（S86）。骨のトラックの矢印はその骨のカーブだけ、いくつ選んでも 1 回の Undo で戻る
    /// </summary>
    public class IntervalInterpolationTests
    {
        const string kFolder = "Assets/__MktIntervalInterpolationTests";
        static readonly int[] kKeys = { 0, 30, 60 };

        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        PreviewWindow window_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktIntervalInterpolationTests");
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            display.transform.SetParent (prefab_.transform, false);
        }

        [TearDown]
        public void TearDown ()
        {
            if (window_ != null) Object.DestroyImmediate (window_);
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            AssetDatabase.DeleteAsset (kFolder);
        }

        /// <summary>0・30・60F に全身のキーのあるクリップを開いた窓</summary>
        AnimationClip OpenClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.anim");
            using (PoseSession session = new PoseSession (prefab_, definition_)) {
                session.clip = clip;
                foreach (int frame in kKeys) {
                    session.Evaluate (frame);
                    Assert.IsNull (session.CommitPose ());
                }
            }
            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (prefab_);
            window_.SetClip (clip);
            return clip;
        }

        [Test]
        public void BoneInterval_ChangesOnlyThatBonesCurves ()
        {
            AnimationClip clip = OpenClip ();
            string spine = RigPaths.Fk (HumanBodyBones.Spine);
            System.Predicate<EditorCurveBinding> spineOnly = b => b.path == spine;
            System.Predicate<EditorCurveBinding> others = b => b.path != spine;

            window_.SetInterpolation (new[] { new PreviewWindow.Interval { start = 0, end = 30, filter = spineOnly } }, KeyInterpolation.Linear);

            CollectionAssert.AreEqual (new KeyInterpolation?[] { KeyInterpolation.Linear, KeyInterpolation.Curve }, KeyInterpolations.GetRanges (clip, kKeys, spineOnly), "背骨の 0-30 だけリニア");
            CollectionAssert.AreEqual (new KeyInterpolation?[] { KeyInterpolation.Curve, KeyInterpolation.Curve }, KeyInterpolations.GetRanges (clip, kKeys, others), "ほかの骨は変わらない");
            Assert.IsNull (KeyInterpolations.GetSegment (clip, 0), "クリップの区間としては混在");
        }

        [Test]
        public void SeveralClipIntervals_ChangeTogether_AndUndoAtOnce ()
        {
            AnimationClip clip = OpenClip ();
            PreviewWindow.Interval[] both = {
                new PreviewWindow.Interval { start = 0, end = 30 },
                new PreviewWindow.Interval { start = 30, end = 60 },
            };
            window_.SetInterpolation (both, KeyInterpolation.Constant);
            CollectionAssert.AreEqual (new KeyInterpolation?[] { KeyInterpolation.Constant, KeyInterpolation.Constant }, KeyInterpolations.GetRanges (clip, kKeys, null), "両方ステップ");

            Undo.PerformUndo ();
            CollectionAssert.AreEqual (new KeyInterpolation?[] { KeyInterpolation.Curve, KeyInterpolation.Curve }, KeyInterpolations.GetRanges (clip, kKeys, null), "1 回の Undo で両方戻る");
        }
    }

}
