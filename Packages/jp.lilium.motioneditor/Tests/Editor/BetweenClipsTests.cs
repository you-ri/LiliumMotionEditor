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
        public void Weight_IsOneInsideTheSegment_AndOffsetsMoveFromStartToEnd ()
        {
            Assert.AreEqual (0, BetweenClips.Weight (kFirst, 0));
            Assert.AreEqual (0, BetweenClips.Weight (kFirst, 30));
            Assert.AreEqual (1, BetweenClips.Weight (kFirst, 1), "キーの隣のフレームから外部の動き");
            Assert.AreEqual (1, BetweenClips.Weight (kFirst, 29));
            Assert.AreEqual (0, BetweenClips.OffsetBlend (kFirst, 0), "始まりのキーではそのキーのずれ");
            Assert.AreEqual (0.5f, BetweenClips.OffsetBlend (kFirst, 15), 1e-5f);
            Assert.AreEqual (1, BetweenClips.OffsetBlend (kFirst, 30), "終わりのキーではそのキーのずれ");
        }

        /// <summary>
        /// 区間の中の 1 フレームで、結果に両端のキーでのずれを移しながら足した回転（評価の式をテストの側で組み立てる）と、
        /// 編集用リグの回転の最大の差。キーの値は元のクリップのカーブから読む
        /// </summary>
        static float MaxAngleToCorrected (PoseSession session, AnimationClip between, BetweenClips.Range range, int frame)
        {
            session.Evaluate (frame);
            Transform root = session.stage.editingRig.root.transform;
            float blend = BetweenClips.OffsetBlend (range, frame);
            float max = 0;
            foreach (string path in AnimationUtility.GetCurveBindings (between).Where (CurveEdit.IsRotation).Select (b => b.path).Distinct ()) {
                Transform t = CurveEdit.FindPath (root, path);
                if (t == null) continue;
                Quaternion offset (int key) => Quaternion.Inverse (CurveEdit.EvaluateRotation (between, path, key / kRate)) * CurveEdit.EvaluateRotation (session.clip, path, key / kRate);
                Quaternion expected = CurveEdit.EvaluateRotation (between, path, frame / kRate) * Quaternion.Slerp (offset (range.start), offset (range.end), blend);
                max = Mathf.Max (max, Quaternion.Angle (expected, t.localRotation));
            }
            return max;
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
            foreach (int f in new[] { 1, 5, 15, 25, 29 }) {
                Assert.LessOrEqual (MaxAngleToCorrected (session, between, kFirst, f), 0.1f, f + "F は外部の動きにキーでのずれを移しながら足したもの（キーの近くも）");
            }
            Assert.LessOrEqual (MaxAngle (at45, Bones (session, 45)), 0.01f, "外部の区間でない所は元のまま");
            foreach (int key in new[] { 0, 30 }) {
                session.Evaluate (key);
                Assert.Greater (MaxAngleToBetween (session, between, key / kRate), 1, key + "F は外部の動きでない（確かめたい入力になっている）");
            }
        }

        /// <summary>0・30・60F にキー。lockedFeet なら左足の点を Locked にし、30F だけ前へ 20cm 置く（キーの間をすべる足）</summary>
        PoseSession OpenWithLockedFoot (string name, bool lockedFeet)
        {
            AnimationClip clip = new AnimationClip { frameRate = kRate };
            AssetDatabase.CreateAsset (clip, kFolder + "/" + name + ".rig.anim");
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = clip;
            foreach (int frame in new[] { 0, 30, 60 }) {
                session.Evaluate (frame);
                if (lockedFeet) {
                    GameObject control = session.stage.fullBody.GetControl (HumanBodyBones.LeftFoot).gameObject;
                    BodyPointTarget foot = session.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
                    foot.MoveTo (foot.framePosition + prefab_.transform.forward * (frame == 30 ? 0.2f : 0));
                    foot.SetLocked (true);
                    session.SolveAfterEdit ();
                }
                Assert.IsNull (session.CommitPose ());
            }
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsNull (session.ImportBetween (Generated (skeleton, 0.4f), skeleton, new[] { kFirst }, out PoseSession.OverrideImportResult result));
            return session;
        }

        /// <summary>
        /// 外部の区間の中でも、点の状態は曲線の区間と同じ決まり（10-26 ユーザー指示）: 前後のキーで Locked の足は、点の場所へ留まる。
        /// 10-24 から 10-26 までは区間の中で点を引かず、外部の動きの足が点に関わらず動いていた
        /// </summary>
        [Test]
        public void LockedPoints_FollowTheUsualRulesInsideTheSegment ()
        {
            PoseSession plain = OpenWithLockedFoot ("Plain", false);
            PoseSession locked = OpenWithLockedFoot ("Locked", true);
            Transform leftFoot (PoseSession s) => s.stage.editingRig.GetEditingBone (HumanBodyBones.LeftFoot);
            locked.Evaluate (30);
            plain.Evaluate (30);
            Assert.Greater (Vector3.Distance (leftFoot (locked).position, leftFoot (plain).position), 0.15f, "30F のキーは足を前へ置いた（確かめたい入力になっている）");

            GameObject control = locked.stage.fullBody.GetControl (HumanBodyBones.LeftFoot).gameObject;
            BodyPointTarget foot = locked.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
            float apart = 0;
            foreach (int f in new[] { 1, 5, 15, 25, 29 }) {
                locked.Evaluate (f);
                plain.Evaluate (f);
                Assert.AreEqual (BodyPointState.Locked, foot.state, f + "F の状態は前のキーのまま");
                Assert.AreEqual (1, foot.pull, f + "F で足の点は強さ 1 で引く");
                Assert.IsTrue (locked.stage.fullBody.TryGetPointWorld (HumanBodyBones.LeftFoot, out Vector3 placed, out Quaternion placedRotation));
                Assert.LessOrEqual (Vector3.Distance (leftFoot (locked).position, placed), 0.01f, f + "F で足は点の場所に留まる");
                apart = Mathf.Max (apart, Vector3.Distance (leftFoot (locked).position, leftFoot (plain).position));
            }
            Assert.Greater (apart, 0.05f, "点の無いクリップ（外部の動きのまま）とは足の場所が違う");
        }

        /// <summary>
        /// 弱く引いている点（固定の強さが 1 未満）は、骨から置いた場所へ引く強さだけ寄せた所に描き、状態も引く強さで出す（10-26 不具合
        /// 「13F の足の点が AI の足から 45cm 離れた所に Locked で出る」。そのときは AI の区間のキーの近くで強さを弱めていた。
        /// 今は区間の中は強さ 0 なので、強さ 1 未満のキーで確かめる）。選んで Locked にすると、出している所に強さ 1 で置き直す
        /// </summary>
        [Test]
        public void WeakenedPoints_AreShownWhereAndHowStronglyTheyPull ()
        {
            PoseSession session = OpenWithLockedFoot ("Locked", true);
            GameObject control = session.stage.fullBody.GetControl (HumanBodyBones.LeftFoot).gameObject;
            BodyPointTarget foot = session.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
            string path = AnimationUtility.CalculateTransformPath (control.transform, session.stage.editingRig.root.transform);
            EditorCurveBinding[] weights = AnimationUtility.GetCurveBindings (session.clip)
                .Where (b => b.path == path && b.type == typeof (BodyPoint) && b.propertyName != BodyPoint.kLockedProperty).ToArray ();
            Assert.IsNotEmpty (weights);
            // 30F（曲線の区間 30-60 の始まり）の強さを下げ、区間の中の 45F で見る（点の状態は前のキーの値のまま）
            foreach (float pull in new[] { 0.7f, 0.3f }) {
                foreach (EditorCurveBinding binding in weights) CurveEdit.SetKey (session.clip, binding, 30, pull);
                session.stage.InvalidateClipCurves ();
                session.Evaluate (45);
                // 置き場所を骨から 30cm 上へ離す（解き直さない）。全身 IK は弱く引いても体ごと置き場所まで寄せるので、解いた姿勢では骨と置き場所が重なる
                Vector3 bone = foot.bonePosition;
                session.stage.fullBody.SetPointPositionWorld (HumanBodyBones.LeftFoot, bone + Vector3.up * 0.3f);
                Assert.IsTrue (session.stage.fullBody.TryGetPointWorld (HumanBodyBones.LeftFoot, out Vector3 placed, out Quaternion placedRotation));
                Assert.Greater (Vector3.Distance (bone, placed), 0.005f, "置いた場所と骨が離れている（確かめたい入力になっている）");
                Assert.AreEqual (pull, foot.pull, 1e-4f, "引く強さ");
                Assert.LessOrEqual (Vector3.Distance (Vector3.Lerp (bone, placed, pull), foot.framePosition), 1e-4f, pull + " は引いている所に出す");
                Assert.AreEqual (pull < 0.5f ? BodyPointState.Free : BodyPointState.Locked, foot.shownState, pull + " の出す状態");
            }

            Vector3 shown = foot.framePosition;
            foot.SetState (BodyPointState.Locked);
            Assert.AreEqual (1, foot.pull, 1e-6f, "選んで Locked にすると強さ 1");
            Assert.AreEqual (BodyPointState.Locked, foot.shownState);
            Assert.LessOrEqual (Vector3.Distance (shown, foot.framePosition), 1e-3f, "出していた所に置く");
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
            Assert.LessOrEqual (MaxAngleToCorrected (session, between, kFirst, 15), 0.1f, "区間の中は外部の動きのまま（ずれは新しいキーから測る）");

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

        static List<Pose> WorldPose (PoseSession session, int frame)
        {
            session.Evaluate (frame);
            return session.stage.editingRig.editingBones.Select (b => new Pose (b.position, b.rotation)).ToList ();
        }

        static void AssertSamePose (List<Pose> expected, List<Pose> actual, string message)
        {
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, actual[i].position), 0.001f, message + ": " + i + " 番目の骨の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, actual[i].rotation), 0.1f, message + ": " + i + " 番目の骨の向き");
            }
        }

        static List<string> BetweenIssues (PoseSession session)
        {
            MotionLintReport report = MotionLint.Run (session, 0, 60);
            Assert.IsNull (report.error);
            return report.issues.Where (i => i.kind == MotionLint.kBetween).Select (i => i.target + "@" + i.frame + "-" + i.endFrame).ToList ();
        }

        [Test]
        public void SegmentState_TurnsStaleWhenKeyValuesChange_AndLostWhenKeyFramesChange ()
        {
            PoseSession session = OpenWithBetween ();
            Assert.AreEqual (BetweenClips.State.Fresh, session.GetBetweenState (0), "作った直後");
            Assert.AreEqual (BetweenClips.State.None, session.GetBetweenState (30), "外部の動きでない区間");
            CollectionAssert.IsEmpty (BetweenIssues (session));

            EditorCurveBinding hipsY = AnimationUtility.GetCurveBindings (session.clip).First (b => b.propertyName == CurveEdit.kPosition + ".y" && b.path.EndsWith ("Hips"));
            CurveEdit.SetKey (session.clip, hipsY, 30, AnimationUtility.GetEditorCurve (session.clip, hipsY).Evaluate (0.5f) - 0.05f);
            Assert.AreEqual (BetweenClips.State.Stale, session.GetBetweenState (0), "キーの値が変わった");
            Assert.IsTrue (BetweenClips.TryGetBlend (session.clip, 15 / kRate, out AnimationClip ignored, out float weight), "古くても使い続ける");
            CollectionAssert.AreEqual (new[] { "stale@0-30" }, BetweenIssues (session), "検査で知らせる");

            session.Evaluate (15);
            Assert.IsNull (session.CommitPose ());
            Assert.AreEqual (BetweenClips.State.Lost, BetweenClips.GetState (session.clip, 0, 15), "キーのフレームが変わった（区間でなくなった）");
            Assert.IsFalse (BetweenClips.TryGetBlend (session.clip, 10 / kRate, out ignored, out weight), "使わない");
            CollectionAssert.AreEqual (new[] { "lost@0-30" }, BetweenIssues (session));
        }

        [Test]
        public void SetExternal_AndChoosingACurve_SwitchTheSegment ()
        {
            PoseSession session = OpenWithBetween ();
            Assert.IsNull (session.SetExternal (30, true));
            Assert.AreEqual (BetweenClips.State.Fresh, session.GetBetweenState (30), "作ったときの指紋があるので古くない");
            Assert.IsNull (session.SetInterpolation (0, KeyInterpolation.Linear));
            Assert.AreEqual (BetweenClips.State.None, session.GetBetweenState (0), "リニアを選ぶと外部の動きから外れる");
            Assert.IsNull (session.SetExternal (30, false));
            Assert.AreEqual (BetweenClips.State.None, session.GetBetweenState (30));
            Assert.IsNotNull (session.SetExternal (60, true), "最後のキーには区間が無い");

            PoseSession other = Open2 ();
            Assert.IsNotNull (other.SetExternal (0, true), "結果のファイルが無ければ付けない");
        }

        /// <summary>
        /// 一定にしてから外部の動きに戻すと、下の補間が曲線に戻る（外部の動きはキーの近くで下の補間へ混ぜる。一定のままだと区間の終わりが前のキーの姿勢へ寄った）。
        /// 取り込みで外部の動きにするときも同じ
        /// </summary>
        [Test]
        public void MakingASegmentExternal_PutsTheCurveBackUnderIt ()
        {
            PoseSession session = OpenWithBetween ();
            Assert.AreEqual (KeyInterpolation.Curve, SegmentMode (session, 0), "初めは曲線");
            Assert.IsNull (session.SetInterpolation (0, KeyInterpolation.Constant));
            Assert.AreEqual (KeyInterpolation.Constant, SegmentMode (session, 0));
            Assert.AreEqual (BetweenClips.State.None, session.GetBetweenState (0));

            Assert.IsNull (session.SetExternal (0, true));
            Assert.AreEqual (BetweenClips.State.Fresh, session.GetBetweenState (0), "曲線に戻した後の指紋");
            Assert.AreEqual (KeyInterpolation.Curve, SegmentMode (session, 0), "外部の動きにすると曲線に戻る");

            Assert.IsNull (session.SetInterpolation (0, KeyInterpolation.Linear));
            ExternalSkeleton skeleton = BvhTests.CreateKimodoLike (created_);
            Assert.IsNull (session.ImportBetween (Generated (skeleton, 0.4f), skeleton, new[] { kFirst }, out PoseSession.OverrideImportResult result));
            Assert.AreEqual (BetweenClips.State.Fresh, session.GetBetweenState (0));
            Assert.AreEqual (KeyInterpolation.Curve, SegmentMode (session, 0), "取り込みで外部の動きにしても曲線に戻る");
        }

        static KeyInterpolation? SegmentMode (PoseSession session, int start)
        {
            return KeyInterpolations.GetSegments (session.clip).First (s => s.start == start).mode;
        }

        /// <summary>結果のファイルの無い別の元のクリップ</summary>
        PoseSession Open2 ()
        {
            AnimationClip clip = new AnimationClip { frameRate = kRate };
            AssetDatabase.CreateAsset (clip, kFolder + "/Other.rig.anim");
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = clip;
            foreach (int frame in new[] { 0, 30 }) {
                session.Evaluate (frame);
                Assert.IsNull (session.CommitPose ());
            }
            return session;
        }

        [Test]
        public void BakeBetween_KeepsThePose_WithoutTheResultFile ()
        {
            PoseSession session = OpenWithBetween ();
            int[] frames = { 3, 8, 15, 22, 27 };
            List<List<Pose>> before = frames.Select (f => WorldPose (session, f)).ToList ();

            Undo.IncrementCurrentGroup ();
            Assert.IsNull (session.BakeBetween (0));
            Assert.AreEqual (BetweenClips.State.None, session.GetBetweenState (0), "キーにした区間は外部の動きから外れる");
            Assert.Greater (ClipCycle.GetFrames (session.clip).Length, 20, "区間の中の毎フレームにキー");
            for (int i = 0; i < frames.Length; i++) AssertSamePose (before[i], WorldPose (session, frames[i]), "キーにした後 " + frames[i] + "F");

            AssetDatabase.DeleteAsset (kFolder + "/Walk.between.anim");
            BetweenClips.Invalidate ();
            session.stage.InvalidateClipCurves ();
            for (int i = 0; i < frames.Length; i++) AssertSamePose (before[i], WorldPose (session, frames[i]), "結果のファイルを消した後 " + frames[i] + "F");
            CollectionAssert.AreEqual (new[] { "missing@0-60" }, BetweenIssues (session), "結果のファイルが無いことを検査で知らせる");
        }

        [Test]
        public void AnimBank_RenameDuplicateAndDelete_CarryTheResultFile ()
        {
            PoseSession session = OpenWithBetween ();
            AnimationClip clip = session.clip;
            Assert.IsNull (AnimBank.Rename (clip, "Run"));
            Assert.AreEqual (kFolder + "/Run.between.anim", AssetDatabase.GetAssetPath (BetweenClips.Find (clip)), "名前を変えると結果のファイルも付いて行く");

            AnimationClip copy = AnimBank.Duplicate (clip);
            AnimationClip copied = BetweenClips.Find (copy);
            Assert.IsNotNull (copied, "複製にも結果のファイル");
            Assert.AreNotEqual (BetweenClips.Find (clip), copied, "別のファイル");
            CollectionAssert.AreEqual (BetweenClips.GetSegments (BetweenClips.Find (clip)), BetweenClips.GetSegments (copied));
            Assert.IsTrue (BetweenClips.TryGetBlend (copy, 15 / kRate, out AnimationClip ignored, out float weight), "複製でも外部の動き");

            string copiedPath = AssetDatabase.GetAssetPath (copied);
            Assert.IsTrue (AnimBank.Delete (copy));
            Assert.IsNull (AssetDatabase.LoadMainAssetAtPath (copiedPath), "消すと結果のファイルも一緒に移す");
            Assert.IsNotNull (BetweenClips.Find (clip), "元の結果のファイルは残る");
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
