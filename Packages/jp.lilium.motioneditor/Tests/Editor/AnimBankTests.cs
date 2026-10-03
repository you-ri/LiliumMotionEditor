using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// AnimBank（S10）: キャラのフォルダの編集用クリップの一覧・名前の決め方・作成・複製・名前変更。使い捨てのフォルダに作って最後に消す
    /// </summary>
    public class AnimBankTests
    {
        const string kFolder = "Assets/__MktAnimBankTest";

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktAnimBankTest");
        }

        [TearDown]
        public void TearDown ()
        {
            AssetDatabase.DeleteAsset (kFolder);
        }

        [Test]
        public void NextNameIncrementsTrailingNumber ()
        {
            HashSet<string> used = new HashSet<string> { "Attack 01", "Attack 02", "Walk", "Walk 1", "Kick9" };
            Assert.AreEqual ("Attack 03", AnimBank.NextName ("Attack 01", used.Contains), "桁数を保つ");
            Assert.AreEqual ("Walk 2", AnimBank.NextName ("Walk", used.Contains), "番号が無ければ 1 から");
            Assert.AreEqual ("Kick10", AnimBank.NextName ("Kick9", used.Contains));
            Assert.AreEqual ("Run", AnimBank.NextName ("Run", used.Contains), "空いていればそのまま");
        }

        [Test]
        public void ListShowsEditingClipsGroupedBySubfolder ()
        {
            AnimBank.Create (kFolder, "Walk");
            AnimBank.Create (kFolder, "Attack 10");
            AnimBank.Create (kFolder, "Attack 2");
            AnimBank.Create (kFolder + "/Skills", "Fire");
            // ゲーム用のクリップ（.rig が無い）は出さない
            AssetDatabase.CreateAsset (new AnimationClip (), kFolder + "/Walk.anim");

            List<AnimBank.Entry> entries = AnimBank.List (kFolder);
            Assert.AreEqual (new[] { "Attack 2", "Attack 10", "Walk", "Fire" }, entries.Select (e => e.name).ToArray (), "組 → 名前（数の順）");
            Assert.AreEqual ("Skills", entries[3].group);
            Assert.AreEqual ("", entries[0].group);
            Assert.IsEmpty (AnimBank.List (kFolder + "/Missing"));
        }

        [Test]
        public void CreateAndDuplicateAvoidExistingNames ()
        {
            AnimationClip a = AnimBank.Create (kFolder);
            AnimationClip b = AnimBank.Create (kFolder);
            Assert.AreEqual (kFolder + "/New Motion.rig.anim", AssetDatabase.GetAssetPath (a));
            Assert.AreEqual (kFolder + "/New Motion 1.rig.anim", AssetDatabase.GetAssetPath (b));
            Assert.AreEqual (60, a.frameRate);

            AnimationUtility.SetEditorCurve (b, EditorCurveBinding.FloatCurve ("Controls/FK/Hips", typeof (Transform), "m_LocalRotation.x"), AnimationCurve.Constant (0, 1, 0.5f));
            AssetDatabase.SaveAssetIfDirty (b);
            AnimationClip copy = AnimBank.Duplicate (b);
            Assert.AreEqual (kFolder + "/New Motion 2.rig.anim", AssetDatabase.GetAssetPath (copy));
            Assert.AreEqual (1, AnimationUtility.GetCurveBindings (copy).Length, "中身も写る");
        }

        [Test]
        public void RenameKeepsSuffixAndMovesBakedOutput ()
        {
            AnimationClip clip = AnimBank.Create (kFolder, "Attack");
            // このクリップから焼いた版（対応を .meta に持つ）
            AnimationClip baked = new AnimationClip ();
            AssetDatabase.CreateAsset (baked, kFolder + "/Attack.anim");
            HumanoidOutput.WriteLink (kFolder + "/Attack.anim", clip, null);

            Assert.IsNull (AnimBank.Rename (clip, "Slash.rig"));
            Assert.AreEqual (kFolder + "/Slash.rig.anim", AssetDatabase.GetAssetPath (clip), ".rig は付け直す");
            Assert.AreEqual (kFolder + "/Slash.anim", AssetDatabase.GetAssetPath (baked), "焼いた版も合わせる");

            AnimBank.Create (kFolder, "Kick");
            StringAssert.Contains ("同じ名前", AnimBank.Rename (clip, "Kick"));
            StringAssert.Contains ("空", AnimBank.Rename (clip, "  "));
        }
    }

}
