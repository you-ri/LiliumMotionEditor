using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// Override の層（S14）。合成の式・書くときの変換・重ね方・ファイルの印
    /// </summary>
    public class OverrideTests
    {
        const string kFolder = "Assets/__MktOverrideTests";

        [TearDown]
        public void TearDown ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            OverrideApplier.Invalidate ();
        }

        static void AssertClose (Quaternion expected, Quaternion actual, string message)
        {
            Assert.Less (Quaternion.Angle (expected, actual), 0.01f, message + " 期待 " + expected.eulerAngles + " 実際 " + actual.eulerAngles);
        }

        [Test]
        public void StoredValuesComposeBackToTheTarget ()
        {
            Quaternion source = Quaternion.Euler (10, 20, 30);
            Quaternion target = Quaternion.Euler (-40, 5, 70);
            Vector3 sourcePosition = new Vector3 (0.1f, 0.2f, 0.3f);
            Vector3 targetPosition = new Vector3 (-0.5f, 1, 2);
            Vector3 sourceScale = new Vector3 (1, 2, 0.5f);
            Vector3 targetScale = new Vector3 (2, 1, 1);
            foreach (OverrideMode mode in new[] { OverrideMode.Additive, OverrideMode.Override }) {
                AssertClose (target, OverrideMath.ComposeRotation (mode, source, OverrideMath.StoreRotation (mode, source, target), 1), mode + " の回転");
                Assert.Less (Vector3.Distance (targetPosition, OverrideMath.ComposePosition (mode, sourcePosition, OverrideMath.StorePosition (mode, sourcePosition, targetPosition), 1)), 1e-5f, mode + " の位置");
                Assert.Less (Vector3.Distance (targetScale, OverrideMath.ComposeScale (mode, sourceScale, OverrideMath.StoreScale (mode, sourceScale, targetScale), 1)), 1e-5f, mode + " の大きさ");
            }
        }

        [Test]
        public void AdditiveFollowsTheSourceAndWeightBlendsTheDelta ()
        {
            Quaternion delta = Quaternion.Euler (0, 0, 40);
            Quaternion source = Quaternion.Euler (30, 0, 0);
            // 差分は元の後ろに掛ける（元が変われば結果も付いていく）
            AssertClose (source * delta, OverrideMath.ComposeRotation (OverrideMode.Additive, source, delta, 1), "重み 1");
            AssertClose (source * Quaternion.Euler (0, 0, 20), OverrideMath.ComposeRotation (OverrideMode.Additive, source, delta, 0.5f), "重み 0.5 は差分を半分");
            AssertClose (source, OverrideMath.ComposeRotation (OverrideMode.Additive, source, delta, 0), "重み 0 は元のまま");
            Assert.AreEqual (new Vector3 (1.5f, 0, 0), OverrideMath.ComposePosition (OverrideMode.Additive, new Vector3 (1, 0, 0), new Vector3 (1, 0, 0), 0.5f));
        }

        [Test]
        public void WriterStoresTheDeltaFromTheSource ()
        {
            GameObject root = new GameObject ("Root");
            try {
                Transform control = new GameObject ("Control").transform;
                control.SetParent (root.transform, false);
                Quaternion source = Quaternion.Euler (0, 90, 0);
                Quaternion target = Quaternion.Euler (0, 90, 45);
                control.localRotation = target;
                control.localPosition = new Vector3 (1, 2, 3);

                AnimationClip clip = new AnimationClip { frameRate = 60 };
                using (CurveWriter writer = CurveWriter.Begin (clip, root.transform, 10, null)) {
                    writer.storeRotation = (path, value) => OverrideMath.StoreRotation (OverrideMode.Additive, source, value);
                    writer.storePosition = (path, value) => OverrideMath.StorePosition (OverrideMode.Additive, new Vector3 (1, 2, 0), value);
                    writer.Transform (control, TransformChannels.Position | TransformChannels.Rotation);
                }

                float time = 10 / 60f;
                Quaternion stored = new Quaternion (
                    AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalRotation.x")).Evaluate (time),
                    AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalRotation.y")).Evaluate (time),
                    AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalRotation.z")).Evaluate (time),
                    AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalRotation.w")).Evaluate (time));
                AssertClose (Quaternion.Euler (0, 0, 45), stored, "差分（元の後ろに掛ける分）が入る");
                float z = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalPosition.z")).Evaluate (time);
                Assert.AreEqual (3, z, 1e-5f, "位置は差");
                Object.DestroyImmediate (clip);
            }
            finally {
                Object.DestroyImmediate (root);
            }
        }

        [Test]
        public void PastedValuesAreWrittenAsIs ()
        {
            GameObject root = new GameObject ("Root");
            try {
                Transform control = new GameObject ("Control").transform;
                control.SetParent (root.transform, false);
                AnimationClip clip = new AnimationClip { frameRate = 60 };
                EditorCurveBinding binding = EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalPosition.x");
                using (CurveWriter writer = CurveWriter.Begin (clip, root.transform, 0, null)) {
                    writer.storePosition = (path, value) => value + Vector3.one * 100;
                    writer.Set (binding, 0.25f);
                }
                Assert.AreEqual (0.25f, AnimationUtility.GetEditorCurve (clip, binding).Evaluate (0), 1e-5f, "クリップの値をそのまま入れたものは直さない");
                Object.DestroyImmediate (clip);
            }
            finally {
                Object.DestroyImmediate (root);
            }
        }

        [Test]
        public void ApplierLayersTheClipOnTheControls ()
        {
            GameObject root = new GameObject ("Root");
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            try {
                Transform control = new GameObject ("Control").transform;
                control.SetParent (root.transform, false);
                Quaternion delta = Quaternion.Euler (0, 0, 30);
                string[] axes = { "x", "y", "z", "w" };
                for (int a = 0; a < 4; a++) {
                    AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("Control", typeof (Transform), "m_LocalRotation." + axes[a]), AnimationCurve.Constant (0, 1, delta[a]));
                }
                control.localRotation = Quaternion.Euler (20, 0, 0);
                OverrideApplier.Apply (clip, OverrideMode.Additive, 1, 0.5f, root.transform);
                AssertClose (Quaternion.Euler (20, 0, 0) * delta, control.localRotation, "Additive");

                control.localRotation = Quaternion.Euler (20, 0, 0);
                OverrideApplier.Apply (clip, OverrideMode.Additive, 0, 0.5f, root.transform);
                AssertClose (Quaternion.Euler (20, 0, 0), control.localRotation, "重み 0（ミュート）は効かない");

                OverrideApplier.Apply (clip, OverrideMode.Override, 1, 0.5f, root.transform);
                AssertClose (delta, control.localRotation, "Override は置き換え");
            }
            finally {
                Object.DestroyImmediate (root);
                Object.DestroyImmediate (clip);
            }
        }

        [Test]
        public void MergeDropsKeysThatDoNotChangeTheCurve ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            try {
                EditorCurveBinding line = EditorCurveBinding.FloatCurve ("A", typeof (Transform), "m_LocalPosition.x");
                EditorCurveBinding bend = EditorCurveBinding.FloatCurve ("B", typeof (Transform), "m_LocalPosition.x");
                // まっすぐな線を 11 フレーム全部に打つ（両端だけで足りる）
                AnimationCurve straight = new AnimationCurve ();
                AnimationCurve curved = new AnimationCurve ();
                for (int f = 0; f <= 10; f++) {
                    straight.AddKey (new Keyframe (f / 60f, f * 0.1f, 6, 6));
                    curved.AddKey (new Keyframe (f / 60f, f == 5 ? 5 : 0, 0, 0));
                }
                AnimationUtility.SetEditorCurve (clip, line, straight);
                AnimationUtility.SetEditorCurve (clip, bend, curved);

                int removed = OverrideMerge.ReduceKeys (clip);

                AnimationCurve reducedLine = AnimationUtility.GetEditorCurve (clip, line);
                AnimationCurve reducedBend = AnimationUtility.GetEditorCurve (clip, bend);
                Assert.AreEqual (2, reducedLine.length, "まっすぐな線は両端だけ残る");
                Assert.Greater (removed, 0);
                Assert.AreEqual (0.5f, reducedLine.Evaluate (5 / 60f), 1e-4f, "形は変わらない");
                Assert.IsTrue (reducedBend.length >= 3, "山のあるカーブは削りすぎない");
                Assert.AreEqual (5, reducedBend.Evaluate (5 / 60f), 1e-4f);
            }
            finally {
                Object.DestroyImmediate (clip);
            }
        }

        [Test]
        public void OverrideFilesAreFoundFromTheBaseClip ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktOverrideTests");
            AnimationClip baseClip = new AnimationClip { frameRate = 30 };
            AssetDatabase.CreateAsset (baseClip, kFolder + "/Attack.rig.anim");

            OverrideClip created = OverrideFiles.Create (baseClip);
            Assert.IsNotNull (created);
            Assert.AreEqual (kFolder + "/Attack.rig.override.anim", AssetDatabase.GetAssetPath (created.clip));
            Assert.AreEqual (30, created.clip.frameRate, "元と同じフレームレート");
            Assert.IsTrue (OverrideFiles.IsOverride (created.clip));
            Assert.IsFalse (EditingClip.IsEditingClipPath (AssetDatabase.GetAssetPath (created.clip)), "編集用クリップとして開かれない名前");

            created.mode = OverrideMode.Override;
            OverrideFiles.Save (created, baseClip);

            var found = OverrideFiles.FindFor (baseClip);
            Assert.AreEqual (1, found.Count);
            Assert.AreSame (created.clip, found[0].clip);
            Assert.AreEqual (OverrideMode.Override, found[0].mode);

            OverrideClip second = OverrideFiles.Create (baseClip);
            Assert.AreNotEqual (AssetDatabase.GetAssetPath (created.clip), AssetDatabase.GetAssetPath (second.clip), "名前が重ならない");
            Assert.AreEqual (2, OverrideFiles.FindFor (baseClip).Count);

            OverrideFiles.Unlink (created.clip);
            Assert.AreEqual (1, OverrideFiles.FindFor (baseClip).Count, "印を消した層は探さない");
        }
    }

}
