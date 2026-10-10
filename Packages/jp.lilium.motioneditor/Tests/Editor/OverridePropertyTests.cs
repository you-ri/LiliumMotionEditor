using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 任意のプロパティ（Controls/Props/...）を Override の段で重ねる（S17）
    /// </summary>
    public class OverridePropertyTests
    {
        const float kTolerance = 1e-4f;
        const string kFace = "Face";
        const string kShape = "Mouth_A";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;
        GameObject prefab_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab_.transform, false);
            // ブレンドシェイプを持つ顔（元の値は 0）
            Mesh mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame (kShape, 100, new[] { Vector3.forward, Vector3.forward, Vector3.forward }, null, null);
            created_.Add (mesh);
            GameObject face = new GameObject (kFace);
            face.transform.SetParent (display.transform, false);
            face.AddComponent<SkinnedMeshRenderer> ().sharedMesh = mesh;
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
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        static EditorCurveBinding Shape
        {
            get { return EditorCurveBinding.FloatCurve (RigPaths.Props (kFace), typeof (SkinnedMeshRenderer), "blendShape." + kShape); }
        }

        static void SetShape (AnimationClip clip, float value)
        {
            AnimationUtility.SetEditorCurve (clip, Shape, AnimationCurve.Constant (0, 1, value));
        }

        PoseSession Open (AnimationClip clip)
        {
            PoseSession session = new PoseSession (prefab_, definition_);
            disposables_.Add (session);
            session.clip = clip;
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            return session;
        }

        static float Shown (PoseSession session)
        {
            return session.stage.animator.transform.Find (kFace).GetComponent<SkinnedMeshRenderer> ().GetBlendShapeWeight (0);
        }

        [Test]
        public void OverrideLayer_LayersProperties ([Values (false, true)] bool baseHasCurve)
        {
            AnimationClip clip = NewClip ();
            if (baseHasCurve) SetShape (clip, 20);
            float source = baseHasCurve ? 20 : 0;
            PoseSession session = Open (clip);
            AnimationClip layer = NewClip ();
            SetShape (layer, 10);
            session.AddOverride (layer, OverrideMode.Additive);
            session.overrideWriteIndex = 0;

            session.Evaluate (10);
            Assert.AreEqual (source + 10, Shown (session), kTolerance, "Additive は手前の値に足す");
            float captured;
            Assert.IsTrue (session.stage.TryGetOverridePropertySource (EditorCurveBinding.FloatCurve (kFace, typeof (SkinnedMeshRenderer), "blendShape." + kShape), out captured));
            Assert.AreEqual (source, captured, kTolerance, "書き込み先の段の手前の値を控える（キーを打つときの差分の元）");

            session.stack.Find<OverrideLayer> ().weight = 0.5f;
            session.Evaluate (10);
            Assert.AreEqual (source + 5, Shown (session), kTolerance, "段の重み");

            // 段を効かせなくすると、手前の値へ戻る
            session.overrideWriteIndex = -1;
            session.Evaluate (10);
            Assert.AreEqual (source, Shown (session), kTolerance);
        }

        [Test]
        public void OverrideMode_ReplacesAndMaskFilters ()
        {
            AnimationClip clip = NewClip ();
            SetShape (clip, 20);
            PoseSession session = Open (clip);
            AnimationClip layer = NewClip ();
            SetShape (layer, 40);
            ChannelMask mask = ScriptableObject.CreateInstance<ChannelMask> ();
            created_.Add (mask);
            session.AddOverride (layer, OverrideMode.Override, mask);
            session.overrideWriteIndex = 0;

            session.Evaluate (10);
            Assert.AreEqual (40, Shown (session), kTolerance, "Override は置き換える");

            mask.properties.Add (new ChannelMask.PropertyRule { property = "blendShape.Mouth_*", include = false });
            session.Evaluate (10);
            Assert.AreEqual (20, Shown (session), kTolerance, "マスクの外は重ねない");
        }

        [Test]
        public void BakeSource_ComposesThePropertyCurves ()
        {
            AnimationClip clip = NewClip ();
            SetShape (clip, 20);
            PoseSession session = Open (clip);
            AnimationClip layer = NewClip ();
            AnimationUtility.SetEditorCurve (layer, Shape, AnimationCurve.Linear (0, 0, 1, 10));
            List<OverrideProperties.Layer> layers = new List<OverrideProperties.Layer> {
                new OverrideProperties.Layer { clip = layer, mode = OverrideMode.Additive, weight = 1 },
            };

            AnimationClip baked = OverrideProperties.CreateBakeSource (clip, layers, session.stage);
            created_.Add (baked);
            Assert.IsNotNull (baked);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (baked, Shape);
            Assert.IsNotNull (curve);
            foreach (float time in new[] { 0f, 0.25f, 0.5f, 1f }) {
                Assert.AreEqual (20 + 10 * time, curve.Evaluate (time), 1e-2f, time + " 秒");
            }

            Assert.IsNull (OverrideProperties.CreateBakeSource (clip, new List<OverrideProperties.Layer> (), session.stage), "段がプロパティを持たなければ元のまま読む");
        }
    }

}
