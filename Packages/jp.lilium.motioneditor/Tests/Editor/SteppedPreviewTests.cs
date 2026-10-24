using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// タイムラインの Interpolate を切ると、キーの間を補間せずに見せる（コマ送り）。キーは書き換えない
    /// </summary>
    public class SteppedPreviewTests
    {
        const string kFolder = "Assets/__MktSteppedPreviewTests";

        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        PreviewWindow window_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktSteppedPreviewTests");
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

        /// <summary>0・30・60F にキー。30F だけ背骨を 40° 曲げる</summary>
        AnimationClip BentClip (out string spine)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/Bend.anim");
            using (PoseSession session = new PoseSession (prefab_, definition_)) {
                session.clip = clip;
                foreach (int frame in new[] { 0, 30, 60 }) {
                    session.Evaluate (frame);
                    Assert.IsNull (session.CommitPose ());
                }
            }
            spine = RigPaths.Fk (HumanBodyBones.Spine);
            Quaternion rest = CurveEdit.EvaluateRotation (clip, spine, 0);
            CurveEdit.SetRotationKey (clip, spine, 30, rest * Quaternion.Euler (40, 0, 0));
            AssetDatabase.SaveAssets ();
            return clip;
        }

        Quaternion SpineAt (string spine, int frame)
        {
            window_.SetFrame (frame);
            return CurveEdit.FindPath (window_.stage.editingRig.root.transform, spine).localRotation;
        }

        [Test]
        public void InterpolateOff_HoldsThePreviousKey_WithoutChangingTheKeys ()
        {
            AnimationClip clip = BentClip (out string spine);
            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (prefab_);
            window_.SetClip (clip);
            Assert.IsTrue (window_.interpolate, "既定は補間する");
            Quaternion first = SpineAt (spine, 0);
            Quaternion bent = SpineAt (spine, 30);
            Assert.Greater (Quaternion.Angle (first, bent), 30, "確かめたい入力になっている");
            float between = Quaternion.Angle (first, SpineAt (spine, 15));
            Assert.Greater (between, 5, "補間するとキーの間は途中の姿勢");
            Assert.Less (between, 35);

            Dictionary<EditorCurveBinding, Keyframe[]> before = AnimationUtility.GetCurveBindings (clip).ToDictionary (b => b, b => AnimationUtility.GetEditorCurve (clip, b).keys);
            window_.interpolate = false;
            Assert.LessOrEqual (Quaternion.Angle (first, SpineAt (spine, 15)), 0.01f, "切ると前のキーの姿勢のまま");
            Assert.LessOrEqual (Quaternion.Angle (first, SpineAt (spine, 29)), 0.01f, "次のキーの直前まで");
            Assert.LessOrEqual (Quaternion.Angle (bent, SpineAt (spine, 30)), 0.01f, "キーのフレームで切り替わる");
            Assert.LessOrEqual (Quaternion.Angle (bent, SpineAt (spine, 45)), 0.01f);
            foreach (KeyValuePair<EditorCurveBinding, Keyframe[]> pair in before) {
                CollectionAssert.AreEqual (pair.Value, AnimationUtility.GetEditorCurve (clip, pair.Key).keys, pair.Key.propertyName + " のキーは変わらない");
            }

            window_.interpolate = true;
            Assert.AreEqual (between, Quaternion.Angle (first, SpineAt (spine, 15)), 0.01f, "入れ直すと補間に戻る");
        }
    }

}
