using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 外部の補間（S81）: キーの間の値を外部の動きの結果から取る。窓・PoseSession・焼き・検査が同じ動きになる
    /// </summary>
    public class BetweenClipsTests
    {
        const string kFolder = "Assets/__MktBetweenClipsTests";
        const float kRate = 60;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        HumanPose rest_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktBetweenClipsTests");
            BetweenClips.Invalidate ();
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
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null && !EditorUtility.IsPersistent (o)) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            AssetDatabase.DeleteAsset (kFolder);
            BetweenClips.Invalidate ();
        }

        /// <summary>元のクリップ（0・30・60F に基準姿勢のキー）を開いたセッション</summary>
        PoseSession Open ()
        {
            AnimationClip clip = new AnimationClip { frameRate = kRate };
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.rig.anim");
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = clip;
            foreach (int frame in new[] { 0, 30, 60 }) {
                session.Evaluate (frame);
                Assert.IsNull (session.CommitPose ());
            }
            return session;
        }

        /// <summary>1 秒の外部の動き（腕と背骨と脚を揺らす。30fps の BVH）</summary>
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
                foreach (int m in muscles) pose.muscles[m] = amount * Mathf.Sin (f / 30f * Mathf.PI * 2);
                instance.handler.SetHumanPose (ref pose);
                skeleton.Capture (instance, bvh);
            }
            return Bvh.Parse (bvh.ToText (), out error);
        }

        static readonly BetweenClips.Range kFirst = new BetweenClips.Range { start = 0, end = 30 };

        PoseSession OpenWithBetween (float amount = 0.4f)
        {
            PoseSession session = Open ();
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsNull (session.ImportBetween (Generated (skeleton, amount), skeleton, new[] { kFirst }, out PoseSession.OverrideImportResult result));
            return session;
        }

        static List<Quaternion> Bones (PoseSession session, int frame)
        {
            session.Evaluate (frame);
            return session.stage.editingRig.editingBones.Select (t => t.rotation).ToList ();
        }

        static float MaxAngle (List<Quaternion> a, List<Quaternion> b)
        {
            return a.Zip (b, Quaternion.Angle).Max ();
        }

        /// <summary>結果のファイルの回転のカーブを 1 本ずつ見て、time で編集用リグのその Transform の回転と比べる（最大の角度）</summary>
        static float MaxAngleToBetween (PoseSession session, AnimationClip between, float time)
        {
            Transform root = session.stage.editingRig.root.transform;
            float max = 0;
            foreach (string path in AnimationUtility.GetCurveBindings (between).Where (CurveEdit.IsRotation).Select (b => b.path).Distinct ()) {
                Transform t = CurveEdit.FindPath (root, path);
                if (t == null) continue;
                max = Mathf.Max (max, Quaternion.Angle (CurveEdit.EvaluateRotation (between, path, time), t.localRotation));
            }
            return max;
        }

        [Test]
        public void Weight_IsZeroAtKeys_OneInTheMiddleThird_AndCappedForLongSegments ()
        {
            Assert.AreEqual (0, BetweenClips.Weight (kFirst, 0, kRate));
            Assert.AreEqual (0, BetweenClips.Weight (kFirst, 30, kRate));
            Assert.AreEqual (0.5f, BetweenClips.Weight (kFirst, 5, kRate), 1e-5f, "30 フレームの区間は 10 フレームで混ぜる（区間の 1/3）");
            Assert.AreEqual (1, BetweenClips.Weight (kFirst, 10, kRate));
            Assert.AreEqual (1, BetweenClips.Weight (kFirst, 20, kRate), "真ん中の 1/3 は外部の動きそのもの");
            BetweenClips.Range longer = new BetweenClips.Range { start = 0, end = 120 };
            Assert.AreEqual (1, BetweenClips.Weight (longer, 15, kRate), "長い区間は 0.25 秒で混ぜ終わる");
            Assert.Less (BetweenClips.Weight (longer, 14, kRate), 1);
        }

        [Test]
        public void ImportBetween_UsesTheMotionInsideTheSegment_AndKeepsTheKeys ()
        {
            PoseSession plain = Open ();
            List<Quaternion> at15 = Bones (plain, 15);
            List<Quaternion> at45 = Bones (plain, 45);
            disposables_.Remove (plain);
            plain.Dispose ();
            AssetDatabase.DeleteAsset (kFolder + "/Walk.rig.anim");

            PoseSession session = OpenWithBetween ();
            AnimationClip between = BetweenClips.Find (session.clip);
            Assert.IsNotNull (between, "元のクリップの隣に結果のファイル");
            Assert.AreEqual (kFolder + "/Walk.between.anim", AssetDatabase.GetAssetPath (between));
            CollectionAssert.AreEqual (new[] { kFirst }, BetweenClips.GetSegments (between));
            Assert.AreEqual (0, session.overrides.Count, "Override の段には足さない");

            Assert.Greater (MaxAngle (at15, Bones (session, 15)), 5, "区間の中は外部の動き");
            Assert.LessOrEqual (MaxAngleToBetween (session, between, 15 / kRate), 0.1f, "区間の真ん中は外部の動きそのもの");
            Assert.LessOrEqual (MaxAngle (at45, Bones (session, 45)), 0.01f, "外部の区間でない所は元のまま");
            foreach (int key in new[] { 0, 30 }) {
                session.Evaluate (key);
                Assert.Greater (MaxAngleToBetween (session, between, key / kRate), 1, key + "F は外部の動きでない（確かめたい入力になっている）");
            }
        }

        [Test]
        public void KeysStay_WhenTheirValuesChange_AndTheSegmentStopsWhenAKeyIsAdded ()
        {
            PoseSession session = OpenWithBetween ();
            AnimationClip between = BetweenClips.Find (session.clip);
            List<Quaternion> before = Bones (session, 10);

            // 30F のキーの値を変える（腰を下げる）。外部の区間のまま、キーのフレームは新しいキーの値
            EditorCurveBinding hipsY = AnimationUtility.GetCurveBindings (session.clip).First (b => b.propertyName == CurveEdit.kPosition + ".y" && b.path.EndsWith ("Hips"));
            AnimationCurve curve = AnimationUtility.GetEditorCurve (session.clip, hipsY);
            float lowered = curve.Evaluate (30 / kRate) - 0.1f;
            CurveEdit.SetKey (session.clip, hipsY, 30, lowered);
            session.stage.InvalidateClipCurves ();
            session.Evaluate (30);
            Assert.AreEqual (lowered, CurveEdit.FindPath (session.stage.editingRig.root.transform, hipsY.path).localPosition.y, 1e-4f, "キーのフレームは新しいキーの値");
            session.Evaluate (15);
            Assert.LessOrEqual (MaxAngleToBetween (session, between, 15 / kRate), 0.1f, "区間の中は外部の動きのまま");

            // 区間の中にキーを足すと、その区間は使わない（元のクリップの補間に戻る）
            session.Evaluate (15);
            Assert.IsNull (session.CommitPose ());
            session.stage.InvalidateClipCurves ();
            Assert.Greater (MaxAngle (before, Bones (session, 10)), 1, "外部の動きでなくなる");
            Assert.IsFalse (BetweenClips.TryGetBlend (session.clip, 10 / kRate, out AnimationClip ignored, out float weight));
        }

        [Test]
        public void EveryPath_GivesTheSamePose ()
        {
            PoseSession session = OpenWithBetween ();
            // 窓・PoseSession（Evaluate）と検査は同じ経路。焼き（Override なし）で、焼いた muscle が姿勢と合う
            HumanPose[] expected = new HumanPose[61];
            for (int f = 0; f <= 60; f += 5) {
                session.Evaluate (f);
                expected[f] = new HumanPose ();
                Assert.IsTrue (session.stage.ReadHumanPose (ref expected[f]));
            }
            foreach (bool withOverride in new[] { false, true }) {
                if (withOverride) {
                    // 空の Override を重ねると、焼きは元を自前で当てる経路（ApplyBakePose → SampleBaseAsControls）を通る
                    AnimationClip empty = new AnimationClip { frameRate = kRate };
                    AssetDatabase.CreateAsset (empty, kFolder + "/Walk.override.anim");
                    session.AddOverride (empty, OverrideMode.Additive);
                }
                Assert.IsNull (session.Bake (withOverride ? "WithOverride" : "Plain", out string path, out HumanoidBaker.Result result));
                AnimationClip baked = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
                Assert.IsNotNull (baked, path);
                foreach (string name in new[] { "Spine Front-Back", "Left Arm Down-Up", "Left Upper Leg Front-Back" }) {
                    int index = System.Array.IndexOf (HumanTrait.MuscleName, name);
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (baked, EditorCurveBinding.FloatCurve ("", typeof (Animator), name));
                    for (int f = 0; f <= 60; f += 5) {
                        Assert.AreEqual (expected[f].muscles[index], curve.Evaluate (f / kRate), 0.01f, (withOverride ? "Override あり" : "Override なし") + " の焼き " + name + " @" + f);
                    }
                }
            }
            float moved = new[] { "Spine Front-Back", "Left Arm Down-Up", "Left Upper Leg Front-Back" }
                .Select (n => System.Array.IndexOf (HumanTrait.MuscleName, n)).Max (i => Mathf.Abs (expected[15].muscles[i] - expected[45].muscles[i]));
            Assert.Greater (moved, 0.05f, "外部の区間が焼いた版に入っている（確かめたい入力になっている）");
        }

        [Test]
        public void BoundaryKey_HasNoPop ()
        {
            PoseSession session = OpenWithBetween ();
            MotionLintReport report = MotionLint.Run (session, 0, 60);
            Assert.IsNull (report.error);
            Assert.AreEqual (0, report.Count (MotionLint.kPop), "外部の区間と曲線の区間の境目で跳ねない: " + report.ToJson (false));
        }

        [Test]
        public void ImportingABvh_LeavesNoPlayableGraph ()
        {
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = Generated (skeleton, 0.4f);
            // GetAllGraphs は壊したグラフも一覧に残すので、作った・壊した知らせで数える
            int graphs = 0;
            System.Action<UnityEngine.Playables.PlayableGraph> created = g => graphs++;
            System.Action<UnityEngine.Playables.PlayableGraph> destroying = g => graphs--;
            UnityEditor.Playables.Utility.graphCreated += created;
            UnityEditor.Playables.Utility.destroyingGraph += destroying;
            try {
                PoseSession session = Open ();
                AnimationClip layer = new AnimationClip { frameRate = kRate };
                created_.Add (layer);
                PoseSession.OverrideImportOptions options = new PoseSession.OverrideImportOptions { fingers = false, keepKeys = true };
                Assert.IsNull (session.ImportOverride (bvh, skeleton, layer, OverrideMode.Additive, options, out PoseSession.OverrideImportResult result));
                Assert.IsNull (session.ImportBetween (bvh, skeleton, new[] { kFirst }, out result));
                disposables_.Remove (session);
                session.Dispose ();
            }
            finally {
                UnityEditor.Playables.Utility.graphCreated -= created;
                UnityEditor.Playables.Utility.destroyingGraph -= destroying;
            }
            Assert.AreEqual (0, graphs, "PlayableGraph が残らない");
        }

        [Test]
        public void ImportBetween_RejectsSegmentsThatAreNotBetweenNeighboringKeys ()
        {
            PoseSession session = Open ();
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Bvh bvh = Generated (skeleton, 0.4f);
            Assert.IsNotNull (session.ImportBetween (bvh, skeleton, new[] { new BetweenClips.Range { start = 0, end = 60 } }, out PoseSession.OverrideImportResult result), "間にキーがある");
            Assert.IsNotNull (session.ImportBetween (bvh, skeleton, new BetweenClips.Range[0], out result), "区間が無い");
            Assert.IsNull (BetweenClips.Find (session.clip), "何も作らない");
        }
    }

}
