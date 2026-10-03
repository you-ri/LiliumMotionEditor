using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// Stacker（S12）のキー操作: 組のポーズキーの挿入・削除・間隔の変更・ループ。組の外のキーは動かない
    /// </summary>
    public class StackerTests
    {
        const string kFolder = "Assets/__MktStackerTest";
        static readonly EditorCurveBinding kArm = EditorCurveBinding.FloatCurve ("Controls/FK/LeftUpperArm", typeof (Transform), "m_LocalRotation.x");
        static readonly EditorCurveBinding kLeg = EditorCurveBinding.FloatCurve ("Controls/FK/LeftUpperLeg", typeof (Transform), "m_LocalRotation.x");
        static readonly EditorCurveBinding kHand = EditorCurveBinding.FloatCurve ("Controls/FK/LeftIndexProximal", typeof (Transform), "m_LocalRotation.x");
        static readonly System.Predicate<EditorCurveBinding> kBody = b => b.path != kHand.path;

        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = 60 };
            // 体: 0 / 10 / 30、手: 5 / 20
            Set (kArm, (0, 0.1f), (10, 0.2f), (30, 0.3f));
            Set (kLeg, (0, 1f), (30, 3f));
            Set (kHand, (5, 5f), (20, 7f));
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
            foreach ((int frame, float value) key in keys) curve.AddKey (key.frame / 60f, key.value);
            AnimationUtility.SetEditorCurve (clip_, binding, curve);
        }

        int[] Frames (EditorCurveBinding binding)
        {
            return ClipKeyUtility.GetKeyFrames (clip_, b => b == binding);
        }

        float Value (EditorCurveBinding binding, int frame)
        {
            return AnimationUtility.GetEditorCurve (clip_, binding).Evaluate (frame / 60f);
        }

        [Test]
        public void PoseKeysAreTheUnionInTheGroup ()
        {
            Assert.AreEqual (new[] { 0, 10, 30 }, Stacker.GetPoseKeys (clip_, kBody));
            Assert.AreEqual (new[] { 0, 5, 10, 20, 30 }, Stacker.GetPoseKeys (clip_, null), "All は全部");
        }

        [Test]
        public void InsertCopiesThePoseAndPushesLaterKeys ()
        {
            Assert.AreEqual (30, Stacker.InsertAfter (clip_, 10, kBody), "次までの間隔（20）の後ろに足す");
            Assert.AreEqual (new[] { 0, 10, 30, 50 }, Frames (kArm), "元の 30 は 50 へ押し出す");
            Assert.AreEqual (0.2f, Value (kArm, 30), 1e-5f, "足したキーは元のキーと同じ値");
            Assert.AreEqual (new[] { 0, 50 }, Frames (kLeg), "組の中の別のカーブも押し出す");
            Assert.AreEqual (new[] { 5, 20 }, Frames (kHand), "組の外は動かない");
            Assert.AreEqual (-1, Stacker.InsertAfter (clip_, 11, kBody), "ポーズキーでないフレーム");
            Assert.AreEqual (60, Stacker.InsertAfter (clip_, 50, kBody), "最後のキーの後ろは既定の間隔");
        }

        [Test]
        public void DeleteClosesTheGap ()
        {
            Assert.IsTrue (Stacker.Delete (clip_, 10, kBody));
            Assert.AreEqual (new[] { 0, 10 }, Frames (kArm), "30 のキーが 10 へ詰まる");
            Assert.AreEqual (0.3f, Value (kArm, 10), 1e-5f);
            Assert.AreEqual (new[] { 0, 10 }, Frames (kLeg));
            Assert.AreEqual (new[] { 5, 20 }, Frames (kHand));
            Assert.IsFalse (Stacker.Delete (clip_, 7, kBody));
        }

        [Test]
        public void SetIntervalShiftsFollowingKeys ()
        {
            Assert.IsTrue (Stacker.SetInterval (clip_, 0, 4, kBody));
            Assert.AreEqual (new[] { 0, 4, 24 }, Frames (kArm));
            Assert.AreEqual (new[] { 0, 24 }, Frames (kLeg));
            Assert.IsFalse (Stacker.SetInterval (clip_, 24, 5, kBody), "最後のキーには次が無い");
            Assert.IsFalse (Stacker.SetInterval (clip_, 0, 0, kBody));
        }

        [Test]
        public void LoopCopiesFirstPoseToLast ()
        {
            Assert.IsTrue (Stacker.SyncLoop (clip_, kBody));
            Assert.AreEqual (0.1f, Value (kArm, 30), 1e-5f);
            Assert.AreEqual (1f, Value (kLeg, 30), 1e-5f);
            Assert.AreEqual (7f, Value (kHand, 20), 1e-5f, "組の外は触らない");
            Assert.IsFalse (Stacker.SyncLoop (clip_, kBody), "同じなら書かない");
        }

        [Test]
        public void SettingsAreStoredInTheMeta ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktStackerTest");
            AnimationClip clip = new AnimationClip ();
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.rig.anim");
            Stacker.Settings settings = Stacker.Load (clip);
            Assert.AreEqual ("All", settings.selected);
            settings.groups.Add (new Stacker.Group { name = "Arms", targets = { "LeftUpperArm", "IK LeftArm" } });
            settings.loop = true;
            Assert.IsTrue (Stacker.Save (clip, settings));

            Stacker.Settings loaded = Stacker.Load (clip);
            Assert.IsTrue (loaded.loop);
            Assert.AreEqual ("Arms", loaded.groups[0].name);
            Assert.AreEqual (2, loaded.groups[0].targets.Count);
            Assert.IsFalse (Stacker.Save (new AnimationClip (), settings), "アセットでないクリップ");
        }

        [Test]
        public void GhostRangeIsClampedAndOldSettingsGetTheDefault ()
        {
            Assert.AreEqual (Stacker.kGhostRangeDefault, Stacker.ClampGhostRange (0), "幅を持たない古い設定");
            Assert.AreEqual (1, Stacker.ClampGhostRange (1));
            Assert.AreEqual (16, Stacker.ClampGhostRange (40));
            Stacker.Settings old = JsonUtility.FromJson<Stacker.Settings> ("{\"loop\":true,\"ghost\":true}");
            Assert.AreEqual (Stacker.kGhostRangeDefault, Stacker.ClampGhostRange (old.ghostRange));
            Stacker.Settings wide = JsonUtility.FromJson<Stacker.Settings> (JsonUtility.ToJson (new Stacker.Settings { ghostRange = 12 }));
            Assert.AreEqual (12, wide.ghostRange);
        }
    }

}
