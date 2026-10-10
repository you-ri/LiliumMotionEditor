using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.IO;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 姿勢を 4 方向から画像に書き出す（S39）
    /// </summary>
    public class PoseCaptureTests
    {
        const string kFolder = "Temp/__MktPoseCaptureTest";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            if (Directory.Exists (kFolder)) Directory.Delete (kFolder, true);
        }

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            if (Directory.Exists (kFolder)) Directory.Delete (kFolder, true);
        }

        /// <summary>テストの骨（メッシュが無い）の胸に、写る箱を付けたキャラ</summary>
        GameObject CreatePrefab ()
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            GameObject box = GameObject.CreatePrimitive (PrimitiveType.Cube);
            Object.DestroyImmediate (box.GetComponent<Collider> ());
            box.transform.SetParent (display.GetBoneTransform (HumanBodyBones.Chest), false);
            box.transform.localScale = new Vector3 (0.4f, 0.5f, 0.3f);
            return prefab;
        }

        [Test]
        public void Capture_WritesFourViews_WithTheCharacterInTheMiddle ()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore ("画面の無い Unity では撮れない");
            int scenes = SceneManager.sceneCount;
            PoseSession session = new PoseSession (CreatePrefab (), definition_, true);
            disposables_.Add (session);
            Assert.IsNull (PoseCapture.GetProblem (session));

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();
            string problem;
            List<string> paths = PoseCapture.Capture (session, 0, kFolder, 256, out problem);
            watch.Stop ();

            Assert.IsNull (problem);
            Assert.AreEqual (4, paths.Count);
            Assert.Less (watch.Elapsed.TotalSeconds, 1.0, "4 枚で 1 秒以内");
            foreach (string path in paths) {
                Assert.IsTrue (File.Exists (path), path);
                Texture2D image = new Texture2D (2, 2);
                created_.Add (image);
                Assert.IsTrue (image.LoadImage (File.ReadAllBytes (path)), path);
                Assert.AreEqual (256, image.width);
                Assert.AreEqual (256, image.height);
                // 箱（胸）が写っている。真ん中あたりの色が、隅（背景か床）の色と違う
                Color corner = image.GetPixel (2, image.height - 3);
                Color center = image.GetPixel (image.width / 2, image.height / 2);
                bool found = false;
                for (int dy = -40; dy <= 40 && !found; dy += 4) {
                    Color c = image.GetPixel (image.width / 2, image.height / 2 + dy);
                    found = Mathf.Abs (c.r - corner.r) + Mathf.Abs (c.g - corner.g) + Mathf.Abs (c.b - corner.b) > 0.1f;
                }
                Assert.IsTrue (found, path + " の真ん中にキャラが写る（真ん中 " + center + "・隅 " + corner + "）");
            }
            StringAssert.EndsWith ("_0_front.png", paths[0]);
            Assert.AreEqual (scenes, SceneManager.sceneCount, "開いているシーンを増やさない");
            Assert.IsFalse (session.stage.camera.orthographic, "撮った後はカメラを元に戻す");
        }

        [Test]
        public void HeadlessSession_ExplainsWhyItCannotCapture ()
        {
            PoseSession session = new PoseSession (CreatePrefab (), definition_);
            disposables_.Add (session);
            string problem;
            Assert.IsNull (PoseCapture.Capture (session, 0, kFolder, 256, out problem));
            Assert.IsNotNull (problem);
            Assert.IsFalse (Directory.Exists (kFolder), "撮れないときは何も書かない");
        }
    }

}
