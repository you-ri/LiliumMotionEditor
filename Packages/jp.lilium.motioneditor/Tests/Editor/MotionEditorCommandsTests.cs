using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 窓を開かずにモーションを作る操作口（S38）。JSON の要求で開く・読む・書く・検査・焼く・保存ができ、誤りは例外でなく理由で返る
    /// </summary>
    public class MotionEditorCommandsTests
    {
        const string kFolder = "Assets/__MktCommandsTest";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        string prefabPath_;

        [System.Serializable]
        sealed class Head
        {
            public bool success;
            public string command;
            public string error;
        }

        [System.Serializable]
        sealed class PoseResponse
        {
            public bool success;
            public string error;
            public PoseDocument result;
        }

        [System.Serializable]
        sealed class DescribeResponse
        {
            public bool success;
            public string error;
            public MotionEditorDescription result;
        }

        [System.Serializable]
        sealed class LintResponse
        {
            public bool success;
            public string error;
            public MotionLintReport result;
        }

        [System.Serializable]
        sealed class BakeResponse
        {
            public bool success;
            public string error;
            public BakeResult result;
        }

        [System.Serializable]
        sealed class BakeResult
        {
            public string path;
            public int frameCount;
        }

        [System.Serializable]
        sealed class BatchResponse
        {
            public List<Head> responses;
        }

        [SetUp]
        public void SetUp ()
        {
            MotionEditorCommands.Close ();
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktCommandsTest");
            // キャラの prefab（Avatar もアセットにしてから保存する。でないと prefab の Animator から Avatar が外れる）。
            // 足を踵・つま先の先・小指側で置くので、つま先の骨のあるキャラにする（足の点はつま先の骨のあるキャラにだけできる）
            GameObject temp = new GameObject ("Hero");
            created_.Add (temp);
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
            display.transform.SetParent (temp.transform, false);
            Avatar avatar = display.avatar;
            AssetDatabase.CreateAsset (avatar, kFolder + "/Hero.asset");
            created_.Remove (avatar);
            prefabPath_ = kFolder + "/Hero.prefab";
            PrefabUtility.SaveAsPrefabAsset (temp, prefabPath_);
        }

        [TearDown]
        public void TearDown ()
        {
            MotionEditorCommands.Close ();
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null && !EditorUtility.IsPersistent (o)) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        static T Run<T> (MotionEditorRequest request)
        {
            return JsonUtility.FromJson<T> (MotionEditorCommands.Run (JsonUtility.ToJson (request)));
        }

        static void AssertFailed (string response, string message)
        {
            Head head = JsonUtility.FromJson<Head> (response);
            Assert.IsFalse (head.success, message + ": " + response);
            Assert.IsNotEmpty (head.error, message);
            StringAssert.Contains ("\"result\":null", response, message);
        }

        /// <summary>足を 3 点で Locked にしてしゃがむ姿勢（PoseDocument）</summary>
        static PoseDocument Squat (PoseDocument rest, int frame, float down)
        {
            PoseDocument document = new PoseDocument { frame = frame, humanScale = rest.humanScale };
            foreach (string side in new[] { "Left", "Right" }) {
                foreach (string point in new[] { "Heel", "ToeTip", "LittleToeSide" }) {
                    document.points.Add (new PosePoint { name = side + point, state = "Locked", position = rest.Find (side + point).position });
                }
            }
            document.points.Add (new PosePoint { name = "Hips", state = "Pin", position = rest.Find ("Hips").position + new Vector3 (0, -down, 0.02f) });
            document.points.Add (new PosePoint { name = "LeftHand", state = "Pin", position = rest.Find ("LeftHand").position + new Vector3 (0.1f, down, 0.2f) });
            return document;
        }

        [Test]
        public void OpenWriteLintBakeSave_WorksWithoutTheWindow ()
        {
            string clipPath = kFolder + "/Squat.rig.anim";
            DescribeResponse opened = Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = clipPath, create = true });
            Assert.IsTrue (opened.success, opened.error);
            Assert.IsTrue (opened.result.fullBody);
            Assert.AreEqual (clipPath, opened.result.clip);
            Assert.IsTrue (opened.result.points.Any (p => p.name == "LeftHand"));
            Assert.IsNotNull (AssetDatabase.LoadAssetAtPath<AnimationClip> (clipPath), "無ければ作る");

            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            Assert.IsTrue (rest.success, rest.error);
            foreach ((int frame, float down) in new[] { (0, 0f), (30, 0.12f), (60, 0f) }) {
                PoseResponse written = Run<PoseResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, frame, down) });
                Assert.IsTrue (written.success, written.error);
                Assert.AreEqual (frame, written.result.frame, "書いたフレームの姿勢を返す");
            }
            DescribeResponse described = Run<DescribeResponse> (new MotionEditorRequest { command = "describe" });
            CollectionAssert.AreEqual (new[] { 0, 30, 60 }, described.result.keyFrames);
            Assert.AreEqual (60, described.result.lastFrame);

            LintResponse lint = Run<LintResponse> (new MotionEditorRequest { command = "lint", start = 0 });
            Assert.IsTrue (lint.success, lint.error);
            Assert.AreEqual (60, lint.result.endFrame, "end を省くとクリップの最後まで");
            Assert.IsEmpty (lint.result.issues.Where (i => i.kind != MotionLint.kMuscleRange), "しゃがむだけなら足は滑らず沈まない");

            BakeResponse baked = Run<BakeResponse> (new MotionEditorRequest { command = "bake" });
            Assert.IsTrue (baked.success, baked.error);
            AnimationClip humanoid = AssetDatabase.LoadAssetAtPath<AnimationClip> (baked.result.path);
            Assert.IsNotNull (humanoid, baked.result.path);
            Assert.IsTrue (humanoid.humanMotion, "Humanoid のクリップ");
            Assert.AreEqual (61, baked.result.frameCount);

            // 描く世界（窓と同じ作り）で焼いたものと同じ
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject> (prefabPath_);
            PoseSession rendered = new PoseSession (prefab, ProjectSettings.instance.defaultRigDefinition, true);
            disposables_.Add (rendered);
            rendered.clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (clipPath);
            string renderedPath;
            HumanoidBaker.Result result;
            Assert.IsNull (rendered.Bake ("Rendered", out renderedPath, out result));
            AnimationClip expected = AssetDatabase.LoadAssetAtPath<AnimationClip> (renderedPath);
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (expected)) {
                AnimationCurve a = AnimationUtility.GetEditorCurve (expected, binding);
                AnimationCurve b = AnimationUtility.GetEditorCurve (humanoid, binding);
                Assert.IsNotNull (b, binding.propertyName);
                for (int f = 0; f <= 60; f += 5) Assert.AreEqual (a.Evaluate (f / 60f), b.Evaluate (f / 60f), 1e-4f, binding.propertyName + " " + f + "F");
            }

            Head saved = JsonUtility.FromJson<Head> (MotionEditorCommands.Run ("{\"command\":\"save\"}"));
            Assert.IsTrue (saved.success, saved.error);
            Assert.IsFalse (EditorUtility.IsDirty (rendered.clip), "保存した");

            Assert.IsTrue (JsonUtility.FromJson<Head> (MotionEditorCommands.Run ("{\"command\":\"close\"}")).success);
            Assert.IsNull (MotionEditorCommands.session);
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"readPose\"}"), "閉じた後");
        }

        [Test]
        public void BadRequests_ReturnReasonsWithoutThrowing ()
        {
            AssertFailed (MotionEditorCommands.Run ("{not json"), "読めない JSON");
            AssertFailed (MotionEditorCommands.Run ("{}"), "command が無い");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"readPose\"}"), "開く前");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"open\",\"prefab\":\"Assets/Nothing.prefab\",\"clip\":\"Assets/Nothing.rig.anim\"}"), "無い prefab");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"open\",\"prefab\":\"" + prefabPath_ + "\",\"clip\":\"" + kFolder + "/Missing.rig.anim\"}"), "create なしで無いクリップ");
            Assert.IsNull (MotionEditorCommands.session, "開けなかったら何も持たない");

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Walk.rig.anim", create = true }).success);
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"jump\"}"), "無い command");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"writePose\"}"), "姿勢が無い");
            MotionEditorRequest bad = new MotionEditorRequest { command = "writePose", pose = new PoseDocument { frame = 5 } };
            bad.pose.points.Add (new PosePoint { name = "Tail", state = "Pin" });
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (bad)), "無い点");
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (MotionEditorCommands.session.clip), "誤った要求では書かない");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"lint\",\"start\":10,\"end\":5}"), "逆の範囲");
        }

        [System.Serializable]
        sealed class HandResponse
        {
            public bool success;
            public string error;
            public MotionEditorHand result;
        }

        static float MuscleValue (MotionEditorHand hand, string name)
        {
            MotionEditorMuscle muscle = hand.muscles.FirstOrDefault (m => m.name == name);
            Assert.IsNotNull (muscle, name);
            return muscle.value;
        }

        /// <summary>指のあるキャラの prefab（足の点で立たせるので、つま先も付ける）のパス</summary>
        string CreateHandsPrefab ()
        {
            GameObject temp = new GameObject ("Hands");
            created_.Add (temp);
            Animator display = TestSkeleton.CreateHumanoidWithFingers (created_, toes: true);
            display.transform.SetParent (temp.transform, false);
            AssetDatabase.CreateAsset (display.avatar, kFolder + "/Hands.asset");
            created_.Remove (display.avatar);
            string prefab = kFolder + "/Hands.prefab";
            PrefabUtility.SaveAsPrefabAsset (temp, prefab);
            return prefab;
        }

        /// <summary>左手の人差し指だけを握るプリセット（右手には反対の手の値を写す）のパス</summary>
        static string CreateFistPreset ()
        {
            AnimationClip fist = new AnimationClip { frameRate = 60 };
            foreach (string attribute in new[] { "LeftHand.Index.1 Stretched", "LeftHand.Index.2 Stretched", "LeftHand.Index.3 Stretched" }) {
                AnimationUtility.SetEditorCurve (fist, EditorCurveBinding.FloatCurve ("", typeof (Animator), attribute), AnimationCurve.Constant (0, 0, -1));
            }
            string presetPath = kFolder + "/Fist.pose.anim";
            AssetDatabase.CreateAsset (fist, presetPath);
            return presetPath;
        }

        [Test]
        public void WriteHand_AppliesPresetAndMuscles_ToTheChosenHandOnly ()
        {
            string prefab = CreateHandsPrefab ();
            string presetPath = CreateFistPreset ();

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/Hands.rig.anim", create = true }).success);
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            foreach (int frame in new[] { 0, 30 }) {
                Assert.IsTrue (Run<PoseResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, frame, 0) }).success);
            }
            HandResponse before = Run<HandResponse> (new MotionEditorRequest { command = "readHand", frame = 30 });
            Assert.IsTrue (before.success, before.error);
            Assert.AreEqual (40, before.result.muscles.Count, "両手の指の muscle");

            MotionEditorRequest write = new MotionEditorRequest { command = "writeHand", frame = 30, preset = presetPath, hand = "Right" };
            write.muscles.Add (new MotionEditorMuscle { name = "Index 3 Stretched", value = 0.2f });
            HandResponse written = Run<HandResponse> (write);
            Assert.IsTrue (written.success, written.error);
            Assert.AreEqual (-1, MuscleValue (written.result, "Right Index 1 Stretched"), 0.02f, "左手のプリセットを右手へ");
            Assert.AreEqual (-1, MuscleValue (written.result, "Right Index 2 Stretched"), 0.02f);
            Assert.AreEqual (0.2f, MuscleValue (written.result, "Right Index 3 Stretched"), 0.02f, "個別の値はプリセットの後に当てる");
            Assert.AreEqual (MuscleValue (before.result, "Left Index 1 Stretched"), MuscleValue (written.result, "Left Index 1 Stretched"), 0.02f, "選ばなかった手は変えない");
            Assert.AreEqual (MuscleValue (before.result, "Right Middle 1 Stretched"), MuscleValue (written.result, "Right Middle 1 Stretched"), 0.02f, "プリセットに無い指は変えない");
            HandResponse first = Run<HandResponse> (new MotionEditorRequest { command = "readHand", frame = 0 });
            Assert.AreEqual (MuscleValue (before.result, "Right Index 1 Stretched"), MuscleValue (first.result, "Right Index 1 Stretched"), 0.02f, "ほかのフレームは変えない");

            // 焼いたクリップに手の形が入る
            BakeResponse baked = Run<BakeResponse> (new MotionEditorRequest { command = "bake" });
            Assert.IsTrue (baked.success, baked.error);
            AnimationCurve index = AnimationUtility.GetEditorCurve (AssetDatabase.LoadAssetAtPath<AnimationClip> (baked.result.path),
                EditorCurveBinding.FloatCurve ("", typeof (Animator), "RightHand.Index.1 Stretched"));
            Assert.IsNotNull (index);
            Assert.AreEqual (-1, index.Evaluate (0.5f), 0.02f);

            // 誤りは理由を返し、何も打たない
            AnimationClip clip = MotionEditorCommands.session.clip;
            int keys = AnimationUtility.GetCurveBindings (clip).Sum (b => AnimationUtility.GetEditorCurve (clip, b).length);
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"writeHand\",\"frame\":15,\"preset\":\"" + presetPath + "\",\"hand\":\"Tail\"}"), "無い手");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"writeHand\",\"frame\":15,\"preset\":\"NoSuchPreset\"}"), "無いプリセット");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"writeHand\",\"frame\":15,\"muscles\":[{\"name\":\"Left Arm Down-Up\",\"value\":0}]}"), "指でない muscle");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"writeHand\",\"frame\":15}"), "手の形が無い");
            Assert.AreEqual (keys, AnimationUtility.GetCurveBindings (clip).Sum (b => AnimationUtility.GetEditorCurve (clip, b).length), "誤った要求では打たない");
        }

        [System.Serializable]
        sealed class WriteResponse
        {
            public bool success;
            public string error;
            public WriteResult result;
        }

        [System.Serializable]
        sealed class WriteResult
        {
            public int frame;
            public List<PosePoint> points;
            public List<string> unreached;
            public List<MotionLintIssue> issues;
            public List<PoseSnap> snapped;

            public PosePoint Find (string name)
            {
                return points.FirstOrDefault (p => p.name == name);
            }
        }

        static Dictionary<EditorCurveBinding, Keyframe[]> Curves (AnimationClip clip)
        {
            return AnimationUtility.GetCurveBindings (clip).ToDictionary (b => b, b => AnimationUtility.GetEditorCurve (clip, b).keys);
        }

        static void AssertSameCurves (Dictionary<EditorCurveBinding, Keyframe[]> expected, AnimationClip clip, string message)
        {
            Dictionary<EditorCurveBinding, Keyframe[]> actual = Curves (clip);
            CollectionAssert.AreEquivalent (expected.Keys, actual.Keys, message + ": カーブの組");
            foreach (KeyValuePair<EditorCurveBinding, Keyframe[]> pair in expected) {
                Keyframe[] keys = actual[pair.Key];
                Assert.AreEqual (pair.Value.Length, keys.Length, message + ": " + pair.Key.path + " " + pair.Key.propertyName);
                for (int i = 0; i < keys.Length; i++) {
                    Assert.AreEqual (pair.Value[i].time, keys[i].time, 1e-6f, message);
                    Assert.AreEqual (pair.Value[i].value, keys[i].value, 1e-6f, message + ": " + pair.Key.path + " " + pair.Key.propertyName);
                }
            }
        }

        [Test]
        public void WritePose_ReturnsResidualAndUnreachedPoints ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Reach.rig.anim", create = true }).success);
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            float scale = rest.result.humanScale;

            WriteResponse reached = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 0, 0) });
            Assert.IsTrue (reached.success, reached.error);
            foreach (PosePoint point in reached.result.points) {
                if (point.state == "Free") Assert.AreEqual (0, point.residual, point.name + ": Free の点は 0");
                else Assert.LessOrEqual (point.residual, 0.001f * scale, point.name + ": 届く所に置いた点");
            }
            Assert.IsEmpty (reached.result.unreached);

            // 伸ばした左手を、さらに 30cm 外へ引く（腰を Pin、足を Locked にしているので届かない）
            PoseDocument far = Squat (rest.result, 30, 0);
            far.Find ("LeftHand").position = rest.result.Find ("LeftHand").position + new Vector3 (-0.3f, 0, 0);
            // 試しに解いてから打つ（同じ姿勢から解くので同じ差になる。打った後に解き直すと、解き始めの姿勢が違うので差も変わる）
            WriteResponse dry = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = far, dryRun = true });
            WriteResponse unreached = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = far });
            Assert.IsTrue (unreached.success, unreached.error);
            float residual = unreached.result.Find ("LeftHand").residual;
            Assert.Greater (residual, 0.01f * scale, "届かなかった差");
            Assert.LessOrEqual (residual, 0.3f + 0.001f, "引いた分より離れない");
            CollectionAssert.Contains (unreached.result.unreached, "LeftHand");
            Assert.AreEqual (residual, dry.result.Find ("LeftHand").residual, 1e-4f, "試しに解いても同じ差");
            CollectionAssert.Contains (dry.result.unreached, "LeftHand");
        }

        [Test]
        public void WritePose_ReturnsFloorAndMuscleIssuesAtThatFrame ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Issues.rig.anim", create = true }).success);
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });

            // 腰だけ 10cm 下げる。足は Free なので体ごと下がり、床に沈む
            PoseDocument sunk = new PoseDocument { frame = 0 };
            sunk.points.Add (new PosePoint { name = "Hips", state = "Pin", position = rest.result.Find ("Hips").position + new Vector3 (0, -0.1f, 0) });
            WriteResponse floor = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = sunk });
            Assert.IsTrue (floor.success, floor.error);
            Assert.IsTrue (floor.result.issues.Any (i => i.kind == MotionLint.kFloorPenetration && i.target.Contains ("Heel")), "沈んだ足の点");
            Assert.IsTrue (floor.result.issues.All (i => i.frame == 0 && i.endFrame == 0), "そのフレームだけ");

            // 左腕を FK で回しきってキーにし、腰だけを置き直す（Free の腕は FK のまま）
            PoseSession session = MotionEditorCommands.session;
            session.Evaluate (10);
            Transform arm = session.stage.editingRig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            arm.localRotation *= Quaternion.Euler (0, 0, 170);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            PoseResponse bent = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 10 });
            PoseDocument hips = new PoseDocument { frame = 10 };
            hips.points.Add (new PosePoint { name = "Hips", state = "Pin", position = bent.result.Find ("Hips").position });
            WriteResponse muscle = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = hips });
            Assert.IsTrue (muscle.success, muscle.error);
            List<string> expected = MotionLint.Run (session, 10, 10).issues.Where (i => i.kind == MotionLint.kMuscleRange).Select (i => i.target).ToList ();
            Assert.IsNotEmpty (expected);
            CollectionAssert.AreEquivalent (expected, muscle.result.issues.Where (i => i.kind == MotionLint.kMuscleRange).Select (i => i.target), "検査と同じ muscle");
        }

        [Test]
        public void DryRun_ReturnsTheSolvedPose_WithoutKeying ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Dry.rig.anim", create = true }).success);
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            foreach ((int frame, float down) in new[] { (0, 0f), (30, 0.12f) }) {
                Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, frame, down) }).success);
            }
            AnimationClip clip = MotionEditorCommands.session.clip;
            Dictionary<EditorCurveBinding, Keyframe[]> before = Curves (clip);
            PoseResponse between = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 15 });

            WriteResponse dry = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 15, 0.06f), dryRun = true });
            Assert.IsTrue (dry.success, dry.error);
            Assert.AreEqual (15, dry.result.frame);
            AssertSameCurves (before, clip, "試しに解いてもキーを打たない");
            PoseResponse after = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 15 });
            foreach (PosePoint point in between.result.points) {
                Assert.AreEqual (point.state, after.result.Find (point.name).state, point.name + ": 置いた点を残さない");
                Assert.LessOrEqual (Vector3.Distance (point.position, after.result.Find (point.name).position), 1e-4f, point.name);
            }

            WriteResponse written = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 15, 0.06f) });
            Assert.IsTrue (written.success, written.error);
            foreach (PosePoint point in written.result.points) {
                PosePoint solved = dry.result.Find (point.name);
                Assert.AreEqual (point.state, solved.state, point.name);
                Assert.LessOrEqual (Vector3.Distance (point.position, solved.position), 1e-4f, point.name + ": 試しに解いた姿勢と、打った姿勢が同じ");
                if (point.hasRotation) Assert.LessOrEqual (Quaternion.Angle (Quaternion.Euler (point.rotation), Quaternion.Euler (solved.rotation)), 0.01f, point.name);
            }
        }

        [Test]
        public void Undo_RestoresTheClip_BeforeTheLastWrite ()
        {
            string prefab = CreateHandsPrefab ();
            string presetPath = CreateFistPreset ();
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/Undo.rig.anim", create = true }).success);
            AnimationClip clip = MotionEditorCommands.session.clip;
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 0, 0) }).success);
            Dictionary<EditorCurveBinding, Keyframe[]> first = Curves (clip);
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 30, 0.12f) }).success);
            Dictionary<EditorCurveBinding, Keyframe[]> second = Curves (clip);
            Assert.IsTrue (Run<HandResponse> (new MotionEditorRequest { command = "writeHand", frame = 30, preset = presetPath }).success);

            DescribeResponse undone = Run<DescribeResponse> (new MotionEditorRequest { command = "undo" });
            Assert.IsTrue (undone.success, undone.error);
            AssertSameCurves (second, clip, "手の形を戻す");
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "undo" }).success);
            AssertSameCurves (first, clip, "続けて姿勢を戻す");
            undone = Run<DescribeResponse> (new MotionEditorRequest { command = "undo" });
            Assert.IsTrue (undone.success, undone.error);
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (clip), "開いたときの空のクリップへ戻る");
            Assert.IsEmpty (undone.result.keyFrames);
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"undo\"}"), "戻す書き込みが無い");
        }

        [Test]
        public void Undo_Refuses_WhenTheClipChangedAfterTheWrite ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Changed.rig.anim", create = true }).success);
            AnimationClip clip = MotionEditorCommands.session.clip;
            PoseResponse rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 });
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest.result, 0, 0) }).success);

            // 窓で直したのと同じく、操作口を通さずにカーブを書き換える
            EditorCurveBinding binding = AnimationUtility.GetCurveBindings (clip).First ();
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            curve.AddKey (0.5f, 1);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
            Dictionary<EditorCurveBinding, Keyframe[]> edited = Curves (clip);

            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"undo\"}"), "書いた後に変わったクリップ");
            AssertSameCurves (edited, clip, "ユーザーの直しを消さない");
        }

        [Test]
        public void Describe_ReturnsRestPositionsAndBoneLengths_EvenAfterKeys ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Rest.rig.anim", create = true }).success);
            // 空のクリップの 0 フレームは T ポーズ
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest, 0, 0.12f) }).success);

            DescribeResponse described = Run<DescribeResponse> (new MotionEditorRequest { command = "describe" });
            Assert.IsTrue (described.success, described.error);
            Dictionary<string, MotionEditorPointInfo> points = described.result.points.ToDictionary (p => p.name);
            foreach (PosePoint point in rest.points) {
                Assert.LessOrEqual (Vector3.Distance (point.position, points[point.name].rest), 1e-4f, point.name + ": キーを打った後も T ポーズの場所");
            }

            MotionEditorPointInfo elbow = points["LeftLowerArm"];
            Assert.AreEqual ("LeftUpperArm", elbow.link);
            Assert.AreEqual (Vector3.Distance (rest.Find ("LeftUpperArm").position, rest.Find ("LeftLowerArm").position), elbow.length, 1e-4f, "上腕の長さ");
            Assert.AreEqual ("LeftLowerArm", points["LeftHand"].link);
            Assert.AreEqual ("LeftLowerArm", elbow.bone);
            Assert.IsTrue (points["LeftHeel"].contact && points["LeftHeel"].ground, "足裏の点");
            Assert.IsFalse (points["Hips"].contact || points["Hips"].ground);
            Assert.AreEqual ("", points["Hips"].link, "根の点はつなぐ相手が無い");
            Assert.AreEqual (0, points["Hips"].length);
        }

        [Test]
        public void Compact_RoundsAndOmits_AndRoundTrips ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Compact.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            PoseDocument squat = Squat (rest, 30, 0.12f);
            squat.points.Add (new PosePoint { name = "Head", state = "Pin", position = rest.Find ("Head").position + new Vector3 (0, -0.1f, 0.05f), hasRotation = true, pinRotation = true, rotation = new Vector3 (20, 10, 0) });
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = squat }).success);

            string full = MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "readPose", frame = 30 }));
            string compact = MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "readPose", frame = 30, compact = true }));
            Assert.LessOrEqual (compact.Length * 2, full.Length, "半分以下: " + compact.Length + " / " + full.Length);
            PoseDocument exact = JsonUtility.FromJson<PoseResponse> (full).result;
            PoseDocument rounded = JsonUtility.FromJson<PoseResponse> (compact).result;
            Assert.AreEqual (exact.points.Count, rounded.points.Count);
            foreach (PosePoint point in exact.points) {
                PosePoint read = rounded.Find (point.name);
                Assert.AreEqual (point.state, read.state, point.name);
                if (point.state == "Free") {
                    Assert.AreEqual (Vector3.zero, read.position, point.name + ": Free の点の場所は省く");
                    continue;
                }
                Assert.LessOrEqual (Vector3.Distance (point.position, read.position), 0.001f, point.name);
                Assert.AreEqual (point.hasRotation, read.hasRotation, point.name);
                if (point.hasRotation) {
                    Assert.AreEqual (point.pinRotation, read.pinRotation, point.name);
                    Assert.LessOrEqual (Quaternion.Angle (Quaternion.Euler (point.rotation), Quaternion.Euler (read.rotation)), 0.1f, point.name);
                }
            }
            StringAssert.DoesNotContain ("-0,", compact, "-0 は 0 にする");

            // 丸めた姿勢をそのまま書き戻せる（解き直すので S36 の書き直しの許し 2mm に、丸めの分を足す）
            rounded.frame = 30;
            WriteResponse written = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = rounded, compact = true });
            Assert.IsTrue (written.success, written.error);
            Assert.IsNotNull (written.result.unreached, "compact でも unreached と issues を返す");
            Assert.IsNotNull (written.result.issues);
            PoseDocument again = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 30 }).result;
            foreach (PosePoint point in exact.points) {
                Assert.LessOrEqual (Vector3.Distance (point.position, again.Find (point.name).position), 0.003f, point.name + ": 書き戻した姿勢");
            }
        }

        [Test]
        public void PoseFile_WritesTheSamePoseAsInlineJson ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/File.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            PoseDocument squat = Squat (rest, 10, 0.12f);
            string path = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "MktPoseFileTest.json");
            string badPath = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "MktPoseFileTestBad.json");
            System.IO.File.WriteAllText (path, squat.ToJson ());
            System.IO.File.WriteAllText (badPath, "{not json");
            try {
                WriteResponse inline = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = squat, dryRun = true });
                WriteResponse file = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", poseFile = path, dryRun = true });
                Assert.IsTrue (file.success, file.error);
                foreach (PosePoint point in inline.result.points) {
                    Assert.LessOrEqual (Vector3.Distance (point.position, file.result.Find (point.name).position), 1e-5f, point.name);
                }

                AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "writePose", poseFile = path + ".missing" })), "無いファイル");
                AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "writePose", poseFile = badPath })), "読めない JSON");
                Assert.IsEmpty (AnimationUtility.GetCurveBindings (MotionEditorCommands.session.clip), "誤った要求では書かない");

                Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", poseFile = path }).success);
                CollectionAssert.AreEqual (new[] { 10 }, Run<DescribeResponse> (new MotionEditorRequest { command = "describe" }).result.keyFrames);
            }
            finally {
                System.IO.File.Delete (path);
                System.IO.File.Delete (badPath);
            }
        }

        [System.Serializable]
        sealed class MotionResponse
        {
            public bool success;
            public string error;
            public MotionResult result;
        }

        [System.Serializable]
        sealed class MotionResult
        {
            public List<KeyResult> keys;
            public MotionLintReport lint;
        }

        [System.Serializable]
        sealed class KeyResult
        {
            public int frame;
            public float maxResidual;
            public List<string> unreached;
        }

        [System.Serializable]
        sealed class ReadMotionResponse
        {
            public bool success;
            public string error;
            public MotionEditorRequest result;
        }

        static MotionEditorKey Key (PoseDocument pose, params MotionEditorHandShape[] hands)
        {
            return new MotionEditorKey { frame = pose.frame, points = pose.points, hands = hands.ToList () };
        }

        /// <summary>体と手のある 4 キー（しゃがんで立つ。10・20 フレームで右手を握り、30 フレームで人差し指を少し開く）</summary>
        static List<MotionEditorKey> SquatKeys (PoseDocument rest, string fist)
        {
            MotionEditorHandShape open = new MotionEditorHandShape { hand = "Right" };
            open.muscles.Add (new MotionEditorMuscle { name = "Index 1 Stretched", value = 0.4f });
            return new List<MotionEditorKey> {
                // 並びはフレームの順でなくてよい
                Key (Squat (rest, 20, 0.12f), new MotionEditorHandShape { preset = fist, hand = "Right" }),
                Key (Squat (rest, 0, 0)),
                Key (Squat (rest, 10, 0.06f), new MotionEditorHandShape { preset = fist, hand = "Right" }),
                Key (Squat (rest, 30, 0), open),
            };
        }

        [Test]
        public void WriteMotion_WritesTheSameCurvesAsOneByOne ()
        {
            string prefab = CreateHandsPrefab ();
            string fist = CreateFistPreset ();
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/OneByOne.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            foreach (MotionEditorKey key in SquatKeys (rest, fist).OrderBy (k => k.frame)) {
                Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = new PoseDocument { frame = key.frame, points = key.points } }).success);
                foreach (MotionEditorHandShape hand in key.hands) {
                    HandResponse written = Run<HandResponse> (new MotionEditorRequest { command = "writeHand", frame = key.frame, preset = hand.preset, hand = hand.hand, muscles = hand.muscles });
                    Assert.IsTrue (written.success, written.error);
                }
            }
            Dictionary<EditorCurveBinding, Keyframe[]> expected = Curves (MotionEditorCommands.session.clip);

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/AtOnce.rig.anim", create = true }).success);
            MotionResponse motion = Run<MotionResponse> (new MotionEditorRequest { command = "writeMotion", keys = SquatKeys (rest, fist) });
            Assert.IsTrue (motion.success, motion.error);
            CollectionAssert.AreEqual (new[] { 0, 10, 20, 30 }, motion.result.keys.Select (k => k.frame), "フレームの順に書く");
            Assert.IsTrue (motion.result.keys.All (k => k.unreached.Count == 0 && k.maxResidual <= 0.001f));
            Assert.AreEqual (30, motion.result.lint.endFrame, "最後に全体を検査する");
            AssertSameCurves (expected, MotionEditorCommands.session.clip, "1 つずつ書いたのと同じ");
        }

        [Test]
        public void WriteMotion_RejectsBadKeys_WithoutWritingAnything ()
        {
            string prefab = CreateHandsPrefab ();
            string fist = CreateFistPreset ();
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/Bad.rig.anim", create = true }).success);
            AnimationClip clip = MotionEditorCommands.session.clip;
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest, 0, 0) }).success);
            Dictionary<EditorCurveBinding, Keyframe[]> before = Curves (clip);

            PoseDocument tail = Squat (rest, 20, 0);
            tail.points.Add (new PosePoint { name = "Tail", state = "Pin" });
            PoseDocument jump = Squat (rest, 20, 0);
            jump.points[0].state = "Jump";
            foreach ((MotionEditorKey bad, string message) in new[] {
                (Key (tail), "無い点"),
                (Key (jump), "無い状態"),
                (Key (Squat (rest, 20, 0), new MotionEditorHandShape { preset = "NoSuchPreset" }), "無いプリセット"),
                (new MotionEditorKey { frame = 20 }, "点も手も無いキー"),
                (Key (Squat (rest, 10, 0)), "同じフレーム"),
                (Key (Squat (rest, -5, 0)), "マイナスのフレーム"),
            }) {
                MotionEditorRequest request = new MotionEditorRequest { command = "writeMotion", replace = true };
                request.keys.Add (Key (Squat (rest, 10, 0.06f), new MotionEditorHandShape { preset = fist }));
                request.keys.Add (bad);
                string response = MotionEditorCommands.Run (JsonUtility.ToJson (request));
                AssertFailed (response, message);
                StringAssert.Contains (bad.frame.ToString (), JsonUtility.FromJson<Head> (response).error, message + ": どのキーか分かる");
                AssertSameCurves (before, clip, message + ": 何も書かない（replace でも消さない）");
            }
        }

        [Test]
        public void WriteMotion_Replace_StartsOver_AndUndoesAtOnce ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Replace.rig.anim", create = true }).success);
            AnimationClip clip = MotionEditorCommands.session.clip;
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            MotionEditorRequest first = new MotionEditorRequest { command = "writeMotion", lint = false };
            first.keys.Add (Key (Squat (rest, 0, 0)));
            first.keys.Add (Key (Squat (rest, 30, 0.12f)));
            string written = MotionEditorCommands.Run (JsonUtility.ToJson (first));
            Assert.IsTrue (JsonUtility.FromJson<Head> (written).success, written);
            StringAssert.DoesNotContain ("\"lint\"", written, "lint を切ると検査しない");
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "undo" }).success);
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (clip), "1 回の undo で全部のキーが戻る");

            Assert.IsTrue (Run<MotionResponse> (first).success);
            Dictionary<EditorCurveBinding, Keyframe[]> two = Curves (clip);
            MotionEditorRequest again = new MotionEditorRequest { command = "writeMotion", replace = true };
            again.keys.Add (Key (Squat (rest, 15, 0.06f)));
            Assert.IsTrue (Run<MotionResponse> (again).success);
            CollectionAssert.AreEqual (new[] { 15 }, Run<DescribeResponse> (new MotionEditorRequest { command = "describe" }).result.keyFrames, "前のキーが残らない");

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "undo" }).success);
            AssertSameCurves (two, clip, "消したキーも戻る");
        }

        [Test]
        public void WriteMotion_SetsInterpolation_AndReadMotionReturnsIt ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Interpolation.rig.anim", create = true }).success);
            AnimationClip clip = MotionEditorCommands.session.clip;
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            MotionEditorRequest request = new MotionEditorRequest { command = "writeMotion", lint = false };
            MotionEditorKey first = Key (Squat (rest, 0, 0));
            first.interpolation = "linear";
            MotionEditorKey middle = Key (Squat (rest, 20, 0.06f));
            middle.interpolation = "Constant";
            request.keys.Add (first);
            request.keys.Add (middle);
            request.keys.Add (Key (Squat (rest, 40, 0)));
            MotionResponse written = Run<MotionResponse> (request);
            Assert.IsTrue (written.success, written.error);
            Assert.AreEqual (KeyInterpolation.Linear, MotionEditorCommands.session.GetInterpolation (0));
            Assert.AreEqual (KeyInterpolation.Constant, MotionEditorCommands.session.GetInterpolation (20));

            ReadMotionResponse read = Run<ReadMotionResponse> (new MotionEditorRequest { command = "readMotion" });
            Assert.IsTrue (read.success, read.error);
            CollectionAssert.AreEqual (new[] { "linear", "constant", "" }, read.result.keys.Select (k => k.interpolation ?? ""), "次のキーまでの補間を返す（最後のキーには無い）");

            Dictionary<EditorCurveBinding, Keyframe[]> before = Curves (clip);
            foreach ((string name, int frame, string message) in new[] { ("smooth", 20, "分からない補間"), ("linear", 40, "最後のキー（区間が無い）") }) {
                MotionEditorRequest bad = new MotionEditorRequest { command = "writeMotion", lint = false };
                MotionEditorKey key = Key (Squat (rest, frame, 0.03f));
                key.interpolation = name;
                bad.keys.Add (key);
                string response = MotionEditorCommands.Run (JsonUtility.ToJson (bad));
                AssertFailed (response, message);
                StringAssert.Contains (frame.ToString (), JsonUtility.FromJson<Head> (response).error, message + ": どのキーか分かる");
                AssertSameCurves (before, clip, message + ": 何も書かない");
            }
        }

        [Test]
        public void ReadMotion_RoundTripsThroughWriteMotion ()
        {
            string prefab = CreateHandsPrefab ();
            string fist = CreateFistPreset ();
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/Read.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Assert.IsTrue (Run<MotionResponse> (new MotionEditorRequest { command = "writeMotion", keys = SquatKeys (rest, fist) }).success);
            Dictionary<int, PoseDocument> poses = new[] { 0, 10, 20, 30 }.ToDictionary (f => f, f => Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = f }).result);
            Dictionary<int, MotionEditorHand> hands = new[] { 0, 10, 20, 30 }.ToDictionary (f => f, f => Run<HandResponse> (new MotionEditorRequest { command = "readHand", frame = f }).result);

            ReadMotionResponse read = Run<ReadMotionResponse> (new MotionEditorRequest { command = "readMotion" });
            Assert.IsTrue (read.success, read.error);
            CollectionAssert.AreEqual (new[] { 0, 10, 20, 30 }, read.result.keys.Select (k => k.frame));
            Assert.AreEqual (40, read.result.keys[0].hands[0].muscles.Count, "両手の指の muscle");

            MotionEditorRequest rewrite = new MotionEditorRequest { command = "writeMotion", keys = read.result.keys, replace = true };
            MotionResponse rewritten = Run<MotionResponse> (rewrite);
            Assert.IsTrue (rewritten.success, rewritten.error);
            foreach (int frame in poses.Keys) {
                PoseDocument again = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = frame }).result;
                foreach (PosePoint point in poses[frame].points) {
                    Assert.LessOrEqual (Vector3.Distance (point.position, again.Find (point.name).position), 0.003f, frame + "F " + point.name);
                }
                MotionEditorHand hand = Run<HandResponse> (new MotionEditorRequest { command = "readHand", frame = frame }).result;
                foreach (MotionEditorMuscle muscle in hands[frame].muscles) {
                    Assert.AreEqual (muscle.value, MuscleValue (hand, muscle.name), 2e-3f, frame + "F " + muscle.name);
                }
            }
        }

        /// <summary>試しに解いた姿勢（キーは打たない）</summary>
        static WriteResult DryRun (PoseDocument pose)
        {
            WriteResponse response = Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = pose, dryRun = true });
            Assert.IsTrue (response.success, response.error);
            return response.result;
        }

        static Quaternion RotationOf (WriteResult result, string name)
        {
            return Quaternion.Euler (result.Find (name).rotation);
        }

        /// <summary>足を Locked・腰を Pin にした立ち姿に、点を足す</summary>
        static PoseDocument Stand (PoseDocument rest, params PosePoint[] points)
        {
            PoseDocument pose = Squat (rest, 0, 0);
            pose.points.RemoveAll (p => p.name == "LeftHand");
            pose.points.RemoveAll (p => points.Any (q => q.name == p.name));
            pose.points.AddRange (points);
            return pose;
        }

        [Test]
        public void Aim_TurnsPointsLikeEulerAngles_FromDirections ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Aim.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Vector3 hips = rest.Find ("Hips").position;

            Quaternion turn = Quaternion.Euler (10, 30, 0);
            WriteResult euler = DryRun (Stand (rest, new PosePoint { name = "Hips", state = "Pin", position = hips, hasRotation = true, pinRotation = true, rotation = new Vector3 (10, 30, 0) }));
            WriteResult aimed = DryRun (Stand (rest, new PosePoint { name = "Hips", state = "Pin", position = hips, aim = new PoseAim { forward = turn * Vector3.forward, up = turn * Vector3.up } }));
            Assert.LessOrEqual (Quaternion.Angle (RotationOf (euler, "Hips"), RotationOf (aimed, "Hips")), 0.1f, "オイラー角で書いたのと同じ向き");

            // 右手の指を前・手のひらを下へ（T ポーズでは指が +x・手のひらが -y）
            Vector3 shoulder = rest.Find ("RightUpperArm").position;
            float arm = Vector3.Distance (shoulder, rest.Find ("RightHand").position);
            PosePoint hand = new PosePoint {
                name = "RightHand", state = "Pin", position = shoulder + new Vector3 (0.05f, -0.1f, 0.6f * arm),
                aim = new PoseAim { restForward = Vector3.right, restUp = Vector3.down, forward = Vector3.forward, up = Vector3.down },
            };
            WriteResult turned = DryRun (Stand (rest, hand));
            Quaternion q = RotationOf (turned, "RightHand");
            Assert.LessOrEqual (Vector3.Angle (q * Vector3.right, Vector3.forward), 0.5f, "指が前");
            Assert.LessOrEqual (Vector3.Angle (q * Vector3.down, Vector3.down), 0.5f, "手のひらが下");

            // 頭を場所へ向ける
            Vector3 head = rest.Find ("Head").position;
            Vector3 target = head + new Vector3 (0.3f, -0.2f, 1);
            WriteResult looked = DryRun (Stand (rest, new PosePoint { name = "Head", state = "Pin", position = head, aim = new PoseAim { target = target } }));
            Assert.LessOrEqual (Vector3.Angle (RotationOf (looked, "Head") * Vector3.forward, target - head), 0.5f, "頭が場所を向く");

            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest {
                command = "writePose", pose = Stand (rest, new PosePoint { name = "LeftLowerArm", state = "Pin", position = rest.Find ("LeftLowerArm").position, aim = new PoseAim { forward = Vector3.forward } }),
            })), "向きを持たない点の aim");
        }

        [Test]
        public void Space_PlacesPointsInAnotherPointsFrame ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Space.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Vector3 head = rest.Find ("Head").position;
            Vector3 hips = rest.Find ("Hips").position;

            // 頭を下へ向け、左手を頭の前（頭の座標）に置く
            Vector3 face = new Vector3 (-0.1f, -0.05f, 0.12f);
            WriteResult result = DryRun (Stand (rest,
                new PosePoint { name = "Head", state = "Pin", position = head, hasRotation = true, pinRotation = true, rotation = new Vector3 (30, 0, 0) },
                new PosePoint { name = "LeftHand", state = "Pin", position = face, space = "Head" }));
            Assert.LessOrEqual (Vector3.Distance (result.Find ("LeftHand").position, head + Quaternion.Euler (30, 0, 0) * face), 0.001f, "頭から見て同じ所");

            // 軸を腰の回転から頭の回転へ 0.6 寄せる
            Vector3 offset = new Vector3 (-0.25f, -0.3f, 0.2f);
            Quaternion from = Quaternion.Euler (0, 40, 0), to = Quaternion.Euler (0, -20, 0);
            WriteResult blended = DryRun (Stand (rest,
                new PosePoint { name = "Hips", state = "Pin", position = hips, hasRotation = true, pinRotation = true, rotation = from.eulerAngles },
                new PosePoint { name = "Head", state = "Pin", position = head, hasRotation = true, pinRotation = true, rotation = to.eulerAngles },
                new PosePoint { name = "LeftHand", state = "Pin", position = offset, space = "Head", spaceBlendFrom = "Hips", spaceBlend = 0.6f }));
            Assert.LessOrEqual (Vector3.Distance (blended.Find ("LeftHand").position, head + Quaternion.Slerp (from, to, 0.6f) * offset), 0.001f, "混ぜた軸");

            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest {
                command = "writePose",
                pose = Stand (rest,
                    new PosePoint { name = "Head", state = "Pin", position = Vector3.zero, space = "LeftHand" },
                    new PosePoint { name = "LeftHand", state = "Pin", position = face, space = "Head" }),
            })), "基準が連なる");
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest {
                command = "writePose", pose = Stand (rest, new PosePoint { name = "LeftHand", state = "Pin", position = face, space = "Tail" }),
            })), "無い基準");
        }

        static PoseContact Contact (string point, string mode, Vector3 at, float yaw = 0, float pitch = 0, float lift = 0)
        {
            return new PoseContact { point = point, mode = mode, at = at, yaw = yaw, pitch = pitch, lift = lift };
        }

        /// <summary>腰を Pin にして、足を接地で置いた姿勢（T ポーズの高さでは脚が伸びきって足を前へ出せないので、腰を少し下げる）</summary>
        static PoseDocument Feet (PoseDocument rest, params PoseContact[] contacts)
        {
            PoseDocument pose = new PoseDocument { frame = 0 };
            pose.points.Add (new PosePoint { name = "Hips", state = "Pin", position = rest.Find ("Hips").position + new Vector3 (0, -0.08f, 0) });
            pose.contacts.AddRange (contacts);
            return pose;
        }

        [Test]
        public void Contacts_PlaceTheWholeFoot ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Contacts.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Vector3 left = rest.Find ("LeftFoot").position;
            Vector3 right = rest.Find ("RightFoot").position;
            PoseContact stay = Contact ("RightFoot", "Flat", right);
            string[] soles = { "LeftHeel", "LeftToeTip", "LeftLittleToeSide" };
            // 足裏の点は床との当たりの球の半径だけ上にある（10-22）。床に着いた足裏の点の高さは、基準姿勢の高さ
            float ground = rest.Find ("LeftHeel").position.y;
            Assert.Greater (ground, 0.01f, "足裏の点は半径だけ浮いている");

            Vector3 at = new Vector3 (left.x - 0.03f, 0, left.z + 0.1f);
            WriteResult flat = DryRun (Feet (rest, Contact ("LeftFoot", "Flat", at, 20), stay));
            PosePoint ankle = flat.Find ("LeftFoot");
            Assert.AreEqual ("Locked", ankle.state);
            Assert.AreEqual (at.x, ankle.position.x, 0.001f);
            Assert.AreEqual (at.z, ankle.position.z, 0.001f);
            Assert.AreEqual (left.y, ankle.position.y, 0.001f, "足首は基準姿勢の高さ");
            Assert.LessOrEqual (Quaternion.Angle (RotationOf (flat, "LeftFoot"), Quaternion.AngleAxis (20, Vector3.up)), 0.5f, "足の向き");
            foreach (string sole in soles) {
                Assert.AreEqual (ground, flat.Find (sole).position.y, 0.001f, sole + ": 床");
                Assert.AreEqual ("Locked", flat.Find (sole).state, sole);
            }

            WriteResult ball = DryRun (Feet (rest, Contact ("LeftFoot", "Ball", at, 0, 30), stay));
            Assert.Greater (ball.Find ("LeftHeel").position.y, ground + 0.01f, "かかとが上がる");
            Assert.AreEqual (ground, ball.Find ("LeftToeTip").position.y, 0.001f, "つま先は床のまま");
            Assert.LessOrEqual (Quaternion.Angle (RotationOf (ball, "LeftFoot"), Quaternion.AngleAxis (30, Vector3.right)), 0.5f);

            WriteResult heel = DryRun (Feet (rest, Contact ("LeftFoot", "Heel", at, 0, 20), stay));
            Assert.Greater (heel.Find ("LeftToeTip").position.y, ground + 0.01f, "つま先が上がる");
            Assert.AreEqual (ground, heel.Find ("LeftHeel").position.y, 0.001f, "かかとは床のまま");
            Assert.LessOrEqual (Quaternion.Angle (RotationOf (heel, "LeftFoot"), Quaternion.AngleAxis (-20, Vector3.right)), 0.5f);

            WriteResult lifted = DryRun (Feet (rest, Contact ("LeftFoot", "Flat", at, 0, 0, 0.05f), stay));
            foreach (string sole in soles) Assert.AreEqual (ground + 0.05f, lifted.Find (sole).position.y, 0.001f, sole + ": 浮かせた高さ");

            PoseDocument conflict = Feet (rest, Contact ("LeftFoot", "Flat", at), stay);
            conflict.points.Add (new PosePoint { name = "LeftHeel", state = "Locked", position = Vector3.zero });
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "writePose", pose = conflict })), "points と contacts の両方");
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest { command = "writePose", pose = Feet (rest, Contact ("LeftFoot", "Tiptoe", at)) })), "無い mode");
        }

        [Test]
        public void Contacts_WorkWithoutToeBones ()
        {
            GameObject temp = new GameObject ("NoToes");
            created_.Add (temp);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            display.transform.SetParent (temp.transform, false);
            AssetDatabase.CreateAsset (display.avatar, kFolder + "/NoToes.asset");
            created_.Remove (display.avatar);
            string prefab = kFolder + "/NoToes.prefab";
            PrefabUtility.SaveAsPrefabAsset (temp, prefab);

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefab, clip = kFolder + "/NoToes.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Vector3 left = rest.Find ("LeftFoot").position;
            Vector3 at = new Vector3 (left.x, 0, left.z + 0.05f);
            WriteResult result = DryRun (Feet (rest, Contact ("LeftFoot", "Ball", at, 10, 20), Contact ("RightFoot", "Flat", rest.Find ("RightFoot").position)));
            Assert.AreEqual (at.x, result.Find ("LeftFoot").position.x, 0.02f, "ある点だけで置く");
            Assert.AreEqual ("Locked", result.Find ("LeftFoot").state);
        }

        [Test]
        public void Snap_MovesAnElbowWhereTheBonesReach ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Snap.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Vector3 shoulder = rest.Find ("LeftUpperArm").position;
            Vector3 elbow = rest.Find ("LeftLowerArm").position;
            Vector3 wrist = rest.Find ("LeftHand").position;
            float upper = Vector3.Distance (shoulder, elbow), lower = Vector3.Distance (elbow, wrist);

            // 手を肩の前へ寄せ、肘を届かない所（下の遠く）に置く
            Vector3 hand = shoulder + new Vector3 (0.05f, -0.05f, 0.7f * (upper + lower));
            Vector3 requested = shoulder + new Vector3 (-0.3f, -0.5f, 0);
            WriteResult result = DryRun (Stand (rest,
                new PosePoint { name = "LeftUpperArm", state = "Pin", position = shoulder },
                new PosePoint { name = "LeftHand", state = "Pin", position = hand },
                new PosePoint { name = "LeftLowerArm", state = "Pin", position = requested, snap = true }));
            Assert.IsNotNull (result.snapped);
            Assert.AreEqual (1, result.snapped.Count);
            PoseSnap snap = result.snapped[0];
            Assert.AreEqual ("LeftLowerArm", snap.name);
            Assert.AreEqual (upper, Vector3.Distance (snap.position, shoulder), 0.001f, "上腕の長さ");
            Assert.AreEqual (lower, Vector3.Distance (snap.position, hand), 0.001f, "前腕の長さ");
            Assert.AreEqual (Vector3.Distance (snap.position, requested), snap.distance, 0.001f);
            Vector3 axis = (hand - shoulder).normalized;
            Vector3 center = shoulder + axis * Vector3.Dot (snap.position - shoulder, axis);
            Assert.Greater (Vector3.Dot (snap.position - center, requested - center), 0, "曲がる向きは書いた場所の側");
            CollectionAssert.DoesNotContain (result.unreached, "LeftLowerArm", "寄せた所には届く");

            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (new MotionEditorRequest {
                command = "writePose", pose = Stand (rest, new PosePoint { name = "Hips", state = "Pin", position = rest.Find ("Hips").position, snap = true }),
            })), "線の親の無い点");
        }

        [System.Serializable]
        sealed class SampleResponse
        {
            public bool success;
            public string error;
            public SampleResult result;
        }

        [System.Serializable]
        sealed class SampleResult
        {
            public float frameRate;
            public int[] frames;
            public List<SamplePoint> points;
            public List<SampleMuscle> muscles;
        }

        [System.Serializable]
        sealed class SamplePoint
        {
            public string name;
            public float[] x, y, z, speed;
            public int[] contact;

            public Vector3 At (int k)
            {
                return new Vector3 (x[k], y[k], z[k]);
            }
        }

        [System.Serializable]
        sealed class SampleMuscle
        {
            public string name;
            public float[] value;
        }

        [System.Serializable]
        sealed class DeleteResponse
        {
            public bool success;
            public string error;
            public DeleteResult result;
        }

        [System.Serializable]
        sealed class DeleteResult
        {
            public List<int> keyFrames;
            public bool cycleRemoved;
        }

        /// <summary>しゃがんで立つ 3 キー（0・30・60 フレーム）</summary>
        PoseDocument OpenSquat (string clip)
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/" + clip, create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            foreach ((int frame, float down) in new[] { (0, 0f), (30, 0.12f), (60, 0f) }) {
                Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest, frame, down) }).success);
            }
            return rest;
        }

        [Test]
        public void Sample_ReturnsPositionsSpeedsAndContacts_PerFrame ()
        {
            OpenSquat ("Sample.rig.anim");
            SampleResponse all = Run<SampleResponse> (new MotionEditorRequest { command = "sample" });
            Assert.IsTrue (all.success, all.error);
            Assert.AreEqual (61, all.result.frames.Length, "省くとクリップの最後まで");
            Assert.AreEqual (60, all.result.frameRate, 1e-3f);
            PoseDocument at14 = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 14 }).result;
            PoseDocument at15 = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 15 }).result;
            foreach (PosePoint point in at15.points) {
                SamplePoint sampled = all.result.points.First (p => p.name == point.name);
                Assert.LessOrEqual (Vector3.Distance (sampled.At (15), point.position), 0.001f, point.name + ": readPose と同じ場所");
                float speed = Vector3.Distance (point.position, at14.Find (point.name).position) * 60;
                Assert.AreEqual (speed, sampled.speed[15], 1e-3f, point.name + ": 速さ");
            }
            SamplePoint heel = all.result.points.First (p => p.name == "LeftHeel");
            Assert.IsTrue (heel.contact.All (c => c == 1), "Locked の足は接地したまま");
            Assert.AreEqual (0, heel.speed.Max (), 0.01f);

            MotionEditorRequest some = new MotionEditorRequest { command = "sample", start = 0, end = 60, step = 2 };
            some.pointNames.AddRange (new[] { "LeftHeel", "Hips" });
            some.muscleNames.Add ("Spine Front-Back");
            SampleResponse picked = Run<SampleResponse> (some);
            Assert.IsTrue (picked.success, picked.error);
            Assert.AreEqual (31, picked.result.frames.Length, "2 フレームごと");
            CollectionAssert.AreEqual (new[] { "LeftHeel", "Hips" }, picked.result.points.Select (p => p.name));
            Assert.IsEmpty (picked.result.points[1].contact ?? new int[0], "接地の点でなければ接地を出さない");
            Assert.AreEqual (31, picked.result.muscles[0].value.Length);
            Assert.AreEqual (all.result.points.First (p => p.name == "Hips").speed[16], picked.result.points[1].speed[8], 0.05f, "間を空けても 1 秒あたりの速さ");

            MotionEditorRequest badPoint = new MotionEditorRequest { command = "sample" };
            badPoint.pointNames.Add ("Tail");
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (badPoint)), "無い点");
            MotionEditorRequest badMuscle = new MotionEditorRequest { command = "sample" };
            badMuscle.muscleNames.Add ("Tail Wag");
            AssertFailed (MotionEditorCommands.Run (JsonUtility.ToJson (badMuscle)), "無い muscle");
        }

        static Dictionary<EditorCurveBinding, float> ValuesAt (AnimationClip clip, float time)
        {
            return AnimationUtility.GetCurveBindings (clip).ToDictionary (b => b, b => AnimationUtility.GetEditorCurve (clip, b).Evaluate (time));
        }

        [Test]
        public void DeleteKeys_RemovesOnlyThoseFrames_AndUndoes ()
        {
            OpenSquat ("Delete.rig.anim");
            AnimationClip clip = MotionEditorCommands.session.clip;
            Dictionary<EditorCurveBinding, Keyframe[]> before = Curves (clip);
            Dictionary<EditorCurveBinding, float> first = ValuesAt (clip, 0), last = ValuesAt (clip, 1);

            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"deleteKeys\",\"frames\":[30,15]}"), "キーの無いフレーム");
            AssertSameCurves (before, clip, "何も消さない");
            AssertFailed (MotionEditorCommands.Run ("{\"command\":\"deleteKeys\"}"), "フレームが無い");

            DeleteResponse deleted = Run<DeleteResponse> (new MotionEditorRequest { command = "deleteKeys", frames = new List<int> { 30 } });
            Assert.IsTrue (deleted.success, deleted.error);
            CollectionAssert.AreEqual (new[] { 0, 60 }, deleted.result.keyFrames);
            Assert.IsFalse (deleted.result.cycleRemoved);
            foreach (KeyValuePair<EditorCurveBinding, float> pair in ValuesAt (clip, 0)) Assert.AreEqual (first[pair.Key], pair.Value, 1e-6f, "ほかのキーは変えない: " + pair.Key.propertyName);
            foreach (KeyValuePair<EditorCurveBinding, float> pair in ValuesAt (clip, 1)) Assert.AreEqual (last[pair.Key], pair.Value, 1e-6f, "ほかのキーは変えない: " + pair.Key.propertyName);

            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "undo" }).success);
            AssertSameCurves (before, clip, "undo で戻る");

            Assert.IsTrue (Run<DeleteResponse> (new MotionEditorRequest { command = "deleteKeys", all = true }).success);
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (clip), "all で全部消す");
        }

        [Test]
        public void DeleteKeys_KeepsTheCycleKey_InStep ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Cycle.rig.anim", create = true }).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            foreach ((int frame, float down) in new[] { (0, 0f), (20, 0.06f), (40, 0.12f), (60, 0f) }) {
                Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest, frame, down) }).success);
            }
            AnimationClip clip = MotionEditorCommands.session.clip;
            Assert.IsTrue (ClipCycle.Save (clip, true));
            ClipCycle.Sync (clip);

            DeleteResponse head = Run<DeleteResponse> (new MotionEditorRequest { command = "deleteKeys", frames = new List<int> { 0 } });
            Assert.IsTrue (head.success, head.error);
            Assert.IsFalse (head.result.cycleRemoved);
            Dictionary<EditorCurveBinding, float> newFirst = ValuesAt (clip, 20 / 60f);
            foreach (KeyValuePair<EditorCurveBinding, float> pair in ValuesAt (clip, 1)) {
                Assert.AreEqual (newFirst[pair.Key], pair.Value, 1e-4f, "最後のキーが新しい先頭のキーにそろう: " + pair.Key.path + " " + pair.Key.propertyName);
            }

            DeleteResponse tail = Run<DeleteResponse> (new MotionEditorRequest { command = "deleteKeys", frames = new List<int> { 60 } });
            Assert.IsTrue (tail.success, tail.error);
            Assert.IsTrue (tail.result.cycleRemoved, "サイクルキーを消すと印を外す");
            Assert.IsFalse (ClipCycle.Load (clip));
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "undo" }).success);
            Assert.IsTrue (ClipCycle.Load (clip), "undo で印も戻る");
        }

        [Test]
        public void DeleteKeys_OnAnOverride_LeavesTheBaseClip ()
        {
            OpenSquat ("Base.rig.anim");
            AnimationClip baseClip = MotionEditorCommands.session.clip;
            Dictionary<EditorCurveBinding, Keyframe[]> baseCurves = Curves (baseClip);
            MotionEditorRequest open = new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Base.rig.anim", create = true, overrideWriteIndex = 0 };
            open.overrides.Add (new MotionEditorOverride { clip = kFolder + "/Base.rig.override.anim", mode = "Additive" });
            Assert.IsTrue (Run<DescribeResponse> (open).success);
            PoseDocument rest = Run<PoseResponse> (new MotionEditorRequest { command = "readPose", frame = 0 }).result;
            Assert.IsTrue (Run<WriteResponse> (new MotionEditorRequest { command = "writePose", pose = Squat (rest, 45, 0.05f) }).success);
            AnimationClip layer = MotionEditorCommands.session.targetClip;
            Assert.AreNotEqual (baseClip, layer);
            CollectionAssert.Contains (ClipCycle.GetFrames (layer), 45);

            Assert.IsTrue (Run<DeleteResponse> (new MotionEditorRequest { command = "deleteKeys", frames = new List<int> { 45 } }).success);
            CollectionAssert.DoesNotContain (ClipCycle.GetFrames (layer), 45, "Override から消す");
            AssertSameCurves (baseCurves, baseClip, "元のクリップは変えない");
        }

        /// <summary>背骨を毎フレームのキーで回す（角度は frame → 度）</summary>
        static void SpineCurve (PoseSession session, System.Func<int, float> angle)
        {
            session.Evaluate (0);
            string path = RigPaths.Fk (HumanBodyBones.Spine);
            Quaternion rest = session.stage.editingRig.FindControl (path).localRotation;
            AnimationCurve[] curves = { new AnimationCurve (), new AnimationCurve (), new AnimationCurve (), new AnimationCurve () };
            for (int f = 0; f <= 60; f++) {
                Quaternion q = rest * Quaternion.Euler (angle (f), 0, 0);
                for (int i = 0; i < 4; i++) curves[i].AddKey (new Keyframe (f / 60f, q[i]));
            }
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                for (int k = 0; k < curves[i].length; k++) {
                    AnimationUtility.SetKeyLeftTangentMode (curves[i], k, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode (curves[i], k, AnimationUtility.TangentMode.Linear);
                }
                AnimationUtility.SetEditorCurve (session.clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), curves[i]);
            }
            session.stage.InvalidateClipCurves ();
        }

        [Test]
        public void LoopVelocity_FindsSeamsThatStopOrBounce ()
        {
            Assert.IsTrue (Run<DescribeResponse> (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Loop.rig.anim", create = true }).success);
            PoseSession session = MotionEditorCommands.session;
            Assert.IsTrue (ClipCycle.Save (session.clip, true));
            System.Func<string> seams = () => string.Join (", ", MotionLint.Run (session, 0, 60).issues.Where (i => i.kind == MotionLint.kLoopVelocity).Select (i => i.target));

            SpineCurve (session, f => 20 * Mathf.Sin (2 * Mathf.PI * f / 60));
            Assert.AreEqual ("", seams (), "1 周する動きはつなぎ目も同じ速さ");
            // 跳ねと同じ余裕（0.5°）があるので、1 フレームに 1° ほどの跳ね返りは出ない。3° ほどにする
            SpineCurve (session, f => 60 * Mathf.Sin (Mathf.PI * f / 60));
            StringAssert.Contains ("Spine", seams (), "行って戻る動きはつなぎ目で跳ね返る");
            SpineCurve (session, f => f <= 40 ? 20 * Mathf.Sin (Mathf.PI * f / 40) : 0);
            StringAssert.Contains ("Spine", seams (), "最後は止まっていて、先頭で急に動き出す");
            Assert.IsTrue (ClipCycle.Save (session.clip, false));
            Assert.AreEqual ("", seams (), "サイクルでないクリップは見ない");
        }

        [Test]
        public void RunBatch_KeepOpen_LeavesTheCharacterOpen ()
        {
            MotionEditorBatch batch = new MotionEditorBatch { keepOpen = true };
            batch.requests.Add (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Open.rig.anim", create = true });
            batch.requests.Add (new MotionEditorRequest { command = "readPose", frame = 0 });
            BatchResponse response = JsonUtility.FromJson<BatchResponse> (MotionEditorCommands.RunBatch (JsonUtility.ToJson (batch)));
            Assert.IsTrue (response.responses.All (r => r.success));
            Assert.IsNotNull (MotionEditorCommands.session, "keepOpen なら閉じない");
            Assert.IsTrue (JsonUtility.FromJson<Head> (MotionEditorCommands.Run ("{\"command\":\"describe\"}")).success, "続けて使える");
        }

        [Test]
        public void RunBatch_RunsInOrder_AndCloses ()
        {
            MotionEditorBatch batch = new MotionEditorBatch ();
            batch.requests.Add (new MotionEditorRequest { command = "open", prefab = prefabPath_, clip = kFolder + "/Batch.rig.anim", create = true });
            batch.requests.Add (new MotionEditorRequest { command = "readPose", frame = 0 });
            batch.requests.Add (new MotionEditorRequest { command = "lint" });
            batch.requests.Add (new MotionEditorRequest { command = "jump" });
            BatchResponse response = JsonUtility.FromJson<BatchResponse> (MotionEditorCommands.RunBatch (JsonUtility.ToJson (batch)));
            Assert.AreEqual (4, response.responses.Count);
            Assert.IsTrue (response.responses.Take (3).All (r => r.success), string.Join (", ", response.responses.Select (r => r.error)));
            Assert.IsFalse (response.responses[3].success, "誤った要求は理由を返し、ほかは止めない");
            Assert.AreEqual ("lint", response.responses[2].command);
            Assert.IsNull (MotionEditorCommands.session, "最後に閉じる");
        }
    }

}
