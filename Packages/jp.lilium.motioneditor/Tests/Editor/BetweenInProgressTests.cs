using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 外で作っているキーの間（S85）。窓は作る側から区間を受け取り、タイムラインはその区間の波線を流す
    /// </summary>
    public class BetweenInProgressTests
    {
        const string kFolder = "Assets/__MktBetweenInProgressTests";

        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        PreviewWindow window_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktBetweenInProgressTests");
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

        [Test]
        public void SetBetweenInProgress_MarksOverlappingIntervals_AndNotifiesOnlyOnChange ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.anim");
            using (PoseSession session = new PoseSession (prefab_, definition_)) {
                session.clip = clip;
                foreach (int frame in new[] { 0, 30, 60 }) {
                    session.Evaluate (frame);
                    Assert.IsNull (session.CommitPose ());
                }
            }
            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (prefab_);
            window_.SetClip (clip);
            int changes = 0;
            window_.stateChanged += () => changes++;
            Assert.AreEqual (0, window_.betweenInProgress.Count, "初めは無い");
            Assert.IsFalse (window_.IsBetweenInProgress (0, 30));

            BetweenClips.Range first = new BetweenClips.Range { start = 0, end = 30 };
            window_.SetBetweenInProgress (new[] { first });
            Assert.AreEqual (1, changes, "入れたら知らせる");
            Assert.IsTrue (window_.IsBetweenInProgress (0, 30));
            Assert.IsFalse (window_.IsBetweenInProgress (30, 60), "作っていない区間");
            Assert.IsTrue (window_.IsBetweenInProgress (0, 60), "キーの少ない骨のトラックの区間は、掛かれば作っている");

            window_.SetBetweenInProgress (new[] { first });
            Assert.AreEqual (1, changes, "同じなら知らせない");

            window_.SetBetweenInProgress (null);
            Assert.AreEqual (2, changes);
            Assert.AreEqual (0, window_.betweenInProgress.Count);
            Assert.IsFalse (window_.IsBetweenInProgress (0, 30), "空にしたら作っていない");
        }
    }

}
