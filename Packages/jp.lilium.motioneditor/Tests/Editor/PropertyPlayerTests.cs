using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 任意のプロパティ（S6）: 候補の列挙・パスの置き換え・値を入れて戻す
    /// </summary>
    public class PropertyPlayerTests
    {
        GameObject root_;
        GameObject child_;
        Light light_;
        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            root_ = new GameObject ("Model");
            root_.AddComponent<Animator> ();
            child_ = new GameObject ("Effect");
            child_.transform.SetParent (root_.transform, false);
            light_ = child_.AddComponent<Light> ();
            light_.intensity = 1;
            clip_ = new AnimationClip ();
        }

        [TearDown]
        public void TearDown ()
        {
            Object.DestroyImmediate (root_);
            Object.DestroyImmediate (clip_);
        }

        [Test]
        public void CandidatesAreNumericPropertiesOfTheModel ()
        {
            string[] labels = PropertyPlayer.ListCandidates (root_).Select (c => c.label).ToArray ();
            Assert.Contains ("Effect GameObject.m_IsActive", labels);
            Assert.Contains ("Effect Light.m_Intensity", labels);
            Assert.IsFalse (labels.Any (l => l.Contains ("Transform") || l.Contains ("Animator")), string.Join ("\n", labels));
        }

        /// <summary>
        /// 段の行の値（[LayerParameter] のフィールド）は、Properties で打ったカーブと同じバインディングに結び付く。
        /// 結び付かないと、Properties でキーを打った後に行のスライダーを動かしてもカーブの値で上書きされて動かない（2026-11-12 Face の angry）。
        /// テストのアセンブリの MonoBehaviour は付けられないので、Runtime の部品の数値のフィールドで確かめる
        /// </summary>
        [Test]
        public void MemberBinding_MatchesThePropertyCandidates ()
        {
            HeldObjectControl control = child_.AddComponent<HeldObjectControl> ();

            EditorCurveBinding display;
            Assert.IsTrue (PropertyPlayer.TryGetMemberBinding (root_, control, "attached", out display));
            Assert.AreEqual (EditorCurveBinding.FloatCurve ("Effect", typeof (HeldObjectControl), "attached"), display);
            Assert.IsTrue (PropertyPlayer.ListCandidates (root_).Any (c => c.binding == display), "Properties の一覧と同じバインディング");

            // カーブを入れると値はカーブが決める（行で値だけ入れても、次に姿勢を作ると戻る）
            AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (display), AnimationCurve.Constant (0, 1, 0.25f));
            control.attached = 1;
            new PropertyPlayer ().Apply (root_, clip_, 0);
            Assert.AreEqual (0.25f, control.attached, 1e-5f);

            // カーブにできない名前・表示モデルの外の部品は結び付かない
            Assert.IsFalse (PropertyPlayer.TryGetMemberBinding (root_, control, "NoSuchField", out display));
            GameObject outside = new GameObject ("Outside");
            try {
                Assert.IsFalse (PropertyPlayer.TryGetMemberBinding (root_, outside.AddComponent<HeldObjectControl> (), "attached", out display));
            }
            finally {
                Object.DestroyImmediate (outside);
            }
        }

        /// <summary>
        /// タイムラインの行: 任意のプロパティは 1 つ 1 行、Transform はパスごとに 1 行。体のカーブは入れない（2026-11-12 表情のキーがタイムラインに出なかった）
        /// </summary>
        [Test]
        public void KeyedProperties_AreGroupedIntoTimelineRows ()
        {
            clip_.frameRate = 30;
            AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (EditorCurveBinding.FloatCurve ("", typeof (HeldObjectControl), "attached")), AnimationCurve.Constant (0, 1, 1));
            AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (EditorCurveBinding.FloatCurve ("Effect", typeof (Light), "m_Intensity")), AnimationCurve.Linear (0, 1, 0.5f, 2));
            foreach (string axis in new[] { "x", "y", "z", "w" }) {
                AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (EditorCurveBinding.FloatCurve ("Weapon", typeof (Transform), "m_LocalRotation." + axis)), AnimationCurve.Constant (0, 1, 0));
            }
            AnimationUtility.SetEditorCurve (clip_, EditorCurveBinding.FloatCurve ("Controls/FK/Hips", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0));

            var groups = PropertyPlayer.GroupKeyedProperties (clip_);
            CollectionAssert.AreEquivalent (new[] { "HeldObjectControl.attached", "Effect Light.m_Intensity", "Weapon Transform" }, groups.Select (g => g.label).ToArray ());
            var light = groups.First (g => g.label == "Effect Light.m_Intensity");
            CollectionAssert.AreEqual (new[] { 0, 15 }, ClipKeyUtility.GetKeyFrames (clip_, light.filter), "その行のカーブのキーだけ（30fps）");
            Assert.AreEqual (4, AnimationUtility.GetCurveBindings (clip_).Count (b => groups.First (g => g.label == "Weapon Transform").filter (b)));
        }

        [Test]
        public void PathsAreMovedUnderControls ()
        {
            EditorCurveBinding display = EditorCurveBinding.FloatCurve ("Effect", typeof (Light), "m_Intensity");
            EditorCurveBinding clip = PropertyPlayer.ToClip (display);
            Assert.AreEqual ("Controls/Props/Effect", clip.path);
            Assert.IsTrue (EditingClip.IsRigBinding (clip), "編集用クリップの正規のカーブ（Clean で消えない）");
            EditorCurveBinding back;
            Assert.IsTrue (PropertyPlayer.TryToDisplay (clip, out back));
            Assert.AreEqual (display, back);
            Assert.IsFalse (PropertyPlayer.TryToDisplay (EditorCurveBinding.FloatCurve ("Controls/FK/Hips", typeof (Transform), "m_LocalRotation.x"), out back));
            Assert.AreEqual ("Controls/Props", RigPaths.Props (""));
        }

        [Test]
        public void ApplySetsValuesAndRestoresThem ()
        {
            AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (EditorCurveBinding.FloatCurve ("Effect", typeof (Light), "m_Intensity")), AnimationCurve.Linear (0, 2, 1, 4));
            AnimationUtility.SetEditorCurve (clip_, PropertyPlayer.ToClip (EditorCurveBinding.FloatCurve ("Effect", typeof (GameObject), "m_IsActive")), AnimationCurve.Constant (0, 1, 0));

            PropertyPlayer player = new PropertyPlayer ();
            player.Apply (root_, clip_, 0.5f);
            Assert.AreEqual (3, light_.intensity, 1e-4f);
            Assert.IsFalse (child_.activeSelf);

            // カーブが無くなったら元の値へ戻す
            player.Apply (root_, null, 0);
            Assert.AreEqual (1, light_.intensity, 1e-4f);
            Assert.IsTrue (child_.activeSelf);
        }

        /// <summary>
        /// Transform の回転のカーブ（4 本）は、成分ごとに正規化されずに、クリップの回転そのままで入る（武器の握りなど）。
        /// カーブが無くなったら元の回転へ戻る
        /// </summary>
        [Test]
        public void TransformRotationIsAppliedAsAWhole ()
        {
            Quaternion rest = Quaternion.Euler (10, 0, 0);
            child_.transform.localRotation = rest;
            Quaternion rotation = Quaternion.Euler (-80, 95, 30);
            string path = RigPaths.Props ("Effect");
            CurveEdit.SetRotationKey (clip_, path, 0, rotation);

            PropertyPlayer player = new PropertyPlayer ();
            player.Apply (root_, clip_, 0);
            Assert.Less (Quaternion.Angle (rotation, child_.transform.localRotation), 0.01f, "クリップの回転のまま入る");

            player.Apply (root_, null, 0);
            Assert.Less (Quaternion.Angle (rest, child_.transform.localRotation), 0.01f, "元の回転へ戻る");
        }

        /// <summary>
        /// 入れている値を控えて、骨を写し直した後（上書きされた後）に入れ直せる（動かしている間に武器の握りが基準へ戻らない）
        /// </summary>
        [Test]
        public void CapturedValuesCanBePutBack ()
        {
            Quaternion rotation = Quaternion.Euler (-80, 95, 30);
            CurveEdit.SetRotationKey (clip_, RigPaths.Props ("Effect"), 0, rotation);
            PropertyPlayer player = new PropertyPlayer ();
            player.Apply (root_, clip_, 0);

            var captured = player.Capture (root_);
            child_.transform.localRotation = Quaternion.identity;
            PropertyPlayer.Put (root_, captured);
            Assert.Less (Quaternion.Angle (rotation, child_.transform.localRotation), 0.01f);
        }
    }

}
