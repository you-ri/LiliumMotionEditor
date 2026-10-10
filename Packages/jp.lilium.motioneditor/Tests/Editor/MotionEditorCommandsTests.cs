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
            // キャラの prefab（Avatar もアセットにしてから保存する。でないと prefab の Animator から Avatar が外れる）
            GameObject temp = new GameObject ("Hero");
            created_.Add (temp);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
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
