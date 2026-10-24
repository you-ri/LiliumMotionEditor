using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 窓のキーを外部の骨格へ書き出し、外部の BVH を窓の Override として取り込む口（S77）
    /// </summary>
    public class ExternalWindowTests
    {
        const string kFolder = "Assets/__MktExternalWindowTests";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        HumanPose rest_;
        PreviewWindow window_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktExternalWindowTests");
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoidWithFingers (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab_.transform, false);
            rest_ = new HumanPose ();
            using (HumanPoseHandler handler = new HumanPoseHandler (display.avatar, display.transform)) handler.GetHumanPose (ref rest_);
        }

        [TearDown]
        public void TearDown ()
        {
            if (window_ != null) Object.DestroyImmediate (window_);
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            AssetDatabase.DeleteAsset (kFolder);
        }

        AnimationClip NewAsset (string name)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/" + name + ".anim");
            return clip;
        }

        /// <summary>元のクリップ（0・30・60F に基準姿勢のキー）のアセット</summary>
        AnimationClip BaseClip ()
        {
            AnimationClip clip = NewAsset ("Base");
            using (PoseSession session = new PoseSession (prefab_, definition_)) {
                session.clip = clip;
                foreach (int frame in new[] { 0, 30, 60 }) {
                    session.Evaluate (frame);
                    Assert.IsNull (session.CommitPose ());
                }
            }
            AssetDatabase.SaveAssets ();
            return clip;
        }

        PreviewWindow OpenWindow (AnimationClip clip)
        {
            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (prefab_);
            window_.SetClip (clip);
            Assert.IsNotNull (window_.stage, "窓にキャラが出ている");
            return window_;
        }

        /// <summary>1 秒の Humanoid の動き（腕と背骨と脚を amount だけ揺らす）を、外部の骨格の BVH（30fps）にする（モデルの出力の代わり）</summary>
        Bvh Generated (ExternalSkeleton skeleton, float amount)
        {
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            Assert.IsNull (error);
            disposables_.Add (instance);
            Bvh bvh = skeleton.CreateBvh (1 / 30f);
            string[] names = { "Spine Front-Back", "Left Arm Down-Up", "Right Forearm Stretch", "Left Upper Leg Front-Back" };
            int[] muscles = names.Select (n => System.Array.IndexOf (HumanTrait.MuscleName, n)).ToArray ();
            for (int f = 0; f <= 30; f++) {
                HumanPose pose = new HumanPose {
                    bodyPosition = rest_.bodyPosition,
                    bodyRotation = Quaternion.identity,
                    muscles = (float[]) rest_.muscles.Clone (),
                };
                foreach (int m in muscles) pose.muscles[m] = amount * Mathf.Sin (f / 30f * Mathf.PI);
                instance.handler.SetHumanPose (ref pose);
                skeleton.Capture (instance, bvh);
            }
            return Bvh.Parse (bvh.ToText (), out error);
        }

        static PoseSession.OverrideImportOptions KeepKeys ()
        {
            return new PoseSession.OverrideImportOptions { fingers = false, keepKeys = true };
        }

        List<Quaternion> Pose (int frame)
        {
            window_.SetFrame (frame);
            return window_.stage.editingRig.editingBones.Select (t => t.rotation).ToList ();
        }

        static float MaxAngle (List<Quaternion> a, List<Quaternion> b)
        {
            return a.Zip (b, Quaternion.Angle).Max ();
        }

        [Test]
        public void ImportOverride_AddsTheLayer_WritesToIt_AndShowsTheMotion ()
        {
            PreviewWindow window = OpenWindow (BaseClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            List<Quaternion> before = Pose (15);

            AnimationClip layer = NewAsset ("Base.generated.override");
            Assert.IsNull (window.ImportOverride (Generated (skeleton, 0.4f), skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));
            Assert.AreEqual (1, window.overrides.Count, "Override の段に足す");
            Assert.AreEqual (layer, window.overrides[0].clip);
            Assert.AreEqual (0, window.overrideWriteIndex, "✏ を取り込んだ段へ移す（元へ書いていると上の段は効かない）");
            List<Quaternion> first = Pose (15);
            Assert.Greater (MaxAngle (before, first), 5, "取り込んだ動きが見える");
            Assert.LessOrEqual (MaxAngle (Pose (30), PoseOfBaseAt (30)), 0.1f, "キーのフレームは元のキーの姿勢");

            // 同じクリップへもう一度（別の動きを）取り込んでも、段は増えず、表示が古いままにならない
            Assert.IsNull (window.ImportOverride (Generated (skeleton, -0.4f), skeleton, layer, OverrideMode.Additive, KeepKeys (), out result));
            Assert.AreEqual (1, window.overrides.Count, "同じクリップなら段を増やさない");
            Assert.AreEqual (0, window.overrideWriteIndex);
            Assert.Greater (MaxAngle (first, Pose (15)), 5, "取り込み直した動きが見える");
        }

        /// <summary>元のクリップだけ（✏ を元へ）のときの姿勢。✏ は戻しておく</summary>
        List<Quaternion> PoseOfBaseAt (int frame)
        {
            int write = window_.overrideWriteIndex;
            Assert.IsTrue (window_.SetOverrideWrite (-1));
            List<Quaternion> pose = Pose (frame);
            Assert.IsTrue (window_.SetOverrideWrite (write));
            return pose;
        }

        [Test]
        public void ImportBetween_ShowsTheMotion_WithoutAddingALayer ()
        {
            PreviewWindow window = OpenWindow (BaseClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            List<Quaternion> before = Pose (15);
            BetweenClips.Range range = new BetweenClips.Range { start = 0, end = 30 };
            Assert.IsNull (window.ImportBetween (Generated (skeleton, 0.4f), skeleton, new[] { range }, out PoseSession.OverrideImportResult result));
            Assert.AreEqual (0, window.overrides.Count, "Override の段には足さない");
            Assert.AreEqual (-1, window.overrideWriteIndex, "✏ は元のまま");
            Assert.Greater (MaxAngle (before, Pose (15)), 5, "区間の中は外部の動き（✏ を移さなくても見える）");
            Assert.LessOrEqual (MaxAngle (before, Pose (45)), 0.01f, "区間の外は元のまま");
        }

        [Test]
        public void Window_SwitchesAndBakesTheExternalSegment ()
        {
            PreviewWindow window = OpenWindow (BaseClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsFalse (window.CanSetExternal (0), "結果のファイルが無ければ選べない");
            Assert.IsNull (window.ImportBetween (Generated (skeleton, 0.4f), skeleton, new[] { new BetweenClips.Range { start = 0, end = 30 } }, out PoseSession.OverrideImportResult result));
            Assert.AreEqual (BetweenClips.State.Fresh, window.GetBetweenState (0));
            Assert.IsTrue (window.CanSetExternal (30));
            window.SetExternal (30);
            Assert.AreEqual (BetweenClips.State.Fresh, window.GetBetweenState (30));
            List<PreviewWindow.TimelineTrack> tracks = window.BuildTracks ();
            CollectionAssert.AreEqual (new[] { BetweenClips.State.Fresh, BetweenClips.State.Fresh }, tracks[0].between, "トラックのキーの間に外部の動きの状態");

            window.SetInterpolation (30, KeyInterpolation.Curve);
            Assert.AreEqual (BetweenClips.State.None, window.GetBetweenState (30), "曲線を選ぶと外れる");
            List<Quaternion> before = Pose (15);
            Assert.IsTrue (window.CanBakeBetween (0));
            Assert.IsNull (window.BakeBetween (0));
            Assert.AreEqual (BetweenClips.State.None, window.GetBetweenState (0));
            Assert.LessOrEqual (MaxAngle (before, Pose (15)), 0.1f, "キーにしても同じ姿勢");
        }

        [Test]
        public void ImportOverride_LeavesNoPlayableGraph_AfterTheWindowCloses ()
        {
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = Generated (skeleton, 0.4f);
            AnimationClip clip = BaseClip ();
            AnimationClip layer = NewAsset ("Base.generated.override");
            int graphs = 0;
            System.Action<UnityEngine.Playables.PlayableGraph> created = g => graphs++;
            System.Action<UnityEngine.Playables.PlayableGraph> destroying = g => graphs--;
            UnityEditor.Playables.Utility.graphCreated += created;
            UnityEditor.Playables.Utility.destroyingGraph += destroying;
            try {
                PreviewWindow window = OpenWindow (clip);
                Assert.IsNull (window.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));
                Pose (15);
                Object.DestroyImmediate (window);
                window_ = null;
            }
            finally {
                UnityEditor.Playables.Utility.graphCreated -= created;
                UnityEditor.Playables.Utility.destroyingGraph -= destroying;
            }
            Assert.AreEqual (0, graphs, "窓を閉じた後に PlayableGraph が残らない");
        }

        [Test]
        public void SetOverrideWrite_ToTheBase_HidesTheLayerWhileEditing ()
        {
            PreviewWindow window = OpenWindow (BaseClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            List<Quaternion> before = Pose (15);
            AnimationClip layer = NewAsset ("Base.generated.override");
            Assert.IsNull (window.ImportOverride (Generated (skeleton, 0.4f), skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));

            Assert.IsTrue (window.SetOverrideWrite (-1));
            Assert.AreEqual (-1, window.overrideWriteIndex, "✏ を元へ");
            Assert.LessOrEqual (MaxAngle (before, Pose (15)), 0.01f, "元を直す間は、上の Override は効かない");
            Assert.IsTrue (window.SetOverrideWrite (0));
            Assert.Greater (MaxAngle (before, Pose (15)), 5, "Override へ戻すと見える");
            Assert.IsFalse (window.SetOverrideWrite (3), "無い段へは移さない");
        }

        [Test]
        public void ImportOverride_IsUndone_ByUndo ()
        {
            PreviewWindow window = OpenWindow (BaseClip ());
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            AnimationClip layer = NewAsset ("Base.generated.override");
            Undo.IncrementCurrentGroup ();
            Assert.IsNull (window.ImportOverride (Generated (skeleton, 0.4f), skeleton, layer, OverrideMode.Additive, KeepKeys (), out PoseSession.OverrideImportResult result));
            Assert.Greater (AnimationUtility.GetCurveBindings (layer).Length, 0, "取り込んだカーブがある");
            Undo.PerformUndo ();
            Assert.AreEqual (0, AnimationUtility.GetCurveBindings (layer).Length, "Undo で取り込む前（空）に戻る");
        }

        [Test]
        public void ExportKeys_UsesTheBaseKeys_EvenWhenWritingToAnOverride ()
        {
            AnimationClip clip = BaseClip ();
            // 密なキーの Override（前に取り込んだ動きの代わり）を重ね、✏ をそこへ
            AnimationClip dense = NewAsset ("Base.dense.override");
            using (PoseSession session = new PoseSession (prefab_, definition_)) {
                session.clip = clip;
                session.AddOverride (dense, OverrideMode.Additive);
                session.overrideWriteIndex = 0;
                for (int frame = 0; frame <= 60; frame += 5) {
                    session.Evaluate (frame);
                    Assert.IsNull (session.CommitPose ());
                }
            }
            PreviewWindow window = OpenWindow (clip);
            Assert.AreEqual (0, window.AddOverride (dense, OverrideMode.Additive));
            Assert.AreEqual (0, window.overrideWriteIndex);

            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsNull (window.ExportKeysToSkeleton (skeleton, out Bvh bvh, out MotionEditorExport export));
            CollectionAssert.AreEqual (new[] { 0, 30, 60 }, export.frames, "✏ のクリップでなく元のクリップのキー");
            Assert.AreEqual (3, bvh.frames.Count);
            Assert.AreEqual (60, export.lastFrame);
            Assert.AreEqual (60, export.frameRate);
            Assert.AreEqual (0, window.overrideWriteIndex, "書き出しで窓の ✏ は変わらない");
        }
    }

}
