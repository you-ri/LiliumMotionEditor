using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 焼いたクリップの置き場所と、元のクリップとの対応。アセットを作るので、使い捨てのフォルダに作って最後に消す
    /// </summary>
    public class HumanoidOutputTests
    {
        const string kFolder = "Assets/__MktHumanoidOutputTest";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktHumanoidOutputTest");
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
            AssetDatabase.DeleteAsset (kFolder);
        }

        static AnimationClip CreateClipAsset (string name)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/" + name);
            return clip;
        }

        [Test]
        public void OutputPathDropsRigSuffix ()
        {
            Assert.AreEqual ("Assets/Motions/Attack.anim", HumanoidOutput.GetOutputPath ("Assets/Motions/Attack.rig.anim"));
            Assert.AreEqual ("Assets/Motions/Attack.humanoid.anim", HumanoidOutput.GetOutputPath ("Assets/Motions/Attack.anim"));
            Assert.AreEqual ("Assets/Motions/New Motion.humanoid.anim", HumanoidOutput.GetOutputPath ("Assets/Motions/New Motion.anim"));
            Assert.IsNull (HumanoidOutput.GetOutputPath ((string)null));
            Assert.IsNull (HumanoidOutput.GetOutputPath (new AnimationClip ()), "アセットでないクリップ");
        }

        /// <summary>
        /// 名前のある Output の段（S21）は 名前.&lt;段の名前&gt;.anim へ焼く。名前が空なら今までどおり
        /// </summary>
        [Test]
        public void NamedOutputGetsItsOwnFile ()
        {
            AnimationClip clip = CreateClipAsset ("Attack.rig.anim");
            Assert.AreEqual (kFolder + "/Attack.anim", HumanoidOutput.GetOutputPath (clip, false, ""));
            Assert.AreEqual (kFolder + "/Attack.output2.anim", HumanoidOutput.GetOutputPath (clip, false, "output2"));
            Assert.AreEqual (kFolder + "/Attack.edited.output2.anim", HumanoidOutput.GetOutputPath (clip, true, "output2"));
        }

        [Test]
        public void ForeignClipIsNotOverwritten ()
        {
            AnimationClip source = CreateClipAsset ("Walk.rig.anim");
            string output = HumanoidOutput.GetOutputPath (source);
            Assert.AreEqual (kFolder + "/Walk.anim", output);
            Assert.IsNull (HumanoidOutput.GetProblem (source, output), "まだ無い");

            CreateClipAsset ("Walk.anim");
            StringAssert.Contains ("上書きしない", HumanoidOutput.GetProblem (source, output));

            HumanoidOutput.WriteLink (output, source, null);
            Assert.IsNull (HumanoidOutput.GetProblem (source, output), "このクリップから作ったもの");
            Assert.AreEqual (source, HumanoidOutput.FindSource (AssetDatabase.LoadAssetAtPath<AnimationClip> (output)));

            AnimationClip other = CreateClipAsset ("Run.rig.anim");
            HumanoidOutput.WriteLink (output, other, null);
            StringAssert.Contains ("別のクリップ", HumanoidOutput.GetProblem (source, output));

            StringAssert.Contains ("アセットでない", HumanoidOutput.GetProblem (null, null));
        }

        /// <summary>
        /// 焼いた版の状態。無い → 最新 → 未保存の変更 → 保存後は古い、別のキャラで焼いたもの・他人のクリップ
        /// </summary>
        [Test]
        public void StatusFollowsSourceChanges ()
        {
            Assert.AreEqual (LayerClipState.None, HumanoidOutput.GetStatus (null, null, null).state);

            GameObject temp = new GameObject ("Model");
            created_.Add (temp);
            GameObject model = PrefabUtility.SaveAsPrefabAsset (temp, kFolder + "/Model.prefab");
            GameObject other = PrefabUtility.SaveAsPrefabAsset (temp, kFolder + "/Other.prefab");

            AnimationClip source = CreateClipAsset ("Jump.rig.anim");
            AssetDatabase.SaveAssetIfDirty (source);
            LayerClipStatus status = HumanoidOutput.GetStatus (source, model, null);
            Assert.AreEqual (LayerClipState.None, status.state);
            StringAssert.Contains ("未作成", status.text);
            Assert.AreEqual (LayerClipState.Blocked, HumanoidOutput.GetStatus (source, model, "キャラが無い").state, "焼けないなら理由を出す");

            string output = HumanoidOutput.GetOutputPath (source);
            CreateClipAsset ("Jump.anim");
            Assert.AreEqual (LayerClipState.Blocked, HumanoidOutput.GetStatus (source, model, null).state, "他人のクリップ");

            HumanoidOutput.WriteLink (output, source, model);
            status = HumanoidOutput.GetStatus (source, model, null);
            Assert.AreEqual (LayerClipState.Ready, status.state, status.text);
            StringAssert.Contains ("別のキャラ", HumanoidOutput.GetStatus (source, other, null).text);

            AnimationUtility.SetEditorCurve (source, EditorCurveBinding.FloatCurve ("Controls", typeof (Transform), "m_LocalPosition.x"), AnimationCurve.Constant (0, 1, 2));
            EditorUtility.SetDirty (source);
            status = HumanoidOutput.GetStatus (source, model, null);
            Assert.AreEqual (LayerClipState.Stale, status.state);
            StringAssert.Contains ("保存していない", status.text);

            AssetDatabase.SaveAssetIfDirty (source);
            status = HumanoidOutput.GetStatus (source, model, null);
            Assert.AreEqual (LayerClipState.Stale, status.state);
            StringAssert.Contains ("変わった", status.text);

            HumanoidOutput.WriteLink (output, source, model);
            Assert.AreEqual (LayerClipState.Ready, HumanoidOutput.GetStatus (source, model, null).state, "焼き直せば最新");
        }

        /// <summary>
        /// 焼いて保存する。焼き直しても GUID と、利用者が変えたクリップの設定は残る
        /// </summary>
        [Test]
        public void BakeToAssetKeepsGuidAndSettings ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            disposables_.Add (rig);
            EditingRigSolver solver = new EditingRigSolver (rig);
            solver.Capture ();
            ClipSampler sampler = new ClipSampler (rig.animator);
            disposables_.Add (sampler);
            HumanoidBaker baker = new HumanoidBaker (rig, solver, sampler.Sample);
            disposables_.Add (baker);

            AnimationClip source = CreateClipAsset ("Idle.rig.anim");
            string path;
            HumanoidBaker.Result result = HumanoidOutput.BakeToAsset (baker, source, null, out path);
            Assert.AreEqual (kFolder + "/Idle.anim", path);
            Assert.AreEqual (1, result.frameCount);
            AnimationClip output = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            Assert.IsNotNull (output);
            Assert.IsTrue (output.humanMotion);
            Assert.AreEqual (source, HumanoidOutput.FindSource (output));
            string guid = AssetDatabase.AssetPathToGUID (path);

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (output);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings (output, settings);
            AssetDatabase.SaveAssetIfDirty (output);

            HumanoidOutput.BakeToAsset (baker, source, null, out path);
            Assert.AreEqual (guid, AssetDatabase.AssetPathToGUID (path));
            Assert.IsTrue (AnimationUtility.GetAnimationClipSettings (AssetDatabase.LoadAssetAtPath<AnimationClip> (path)).loopTime, "設定は残る");
        }
    }

}
