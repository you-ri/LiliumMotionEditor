using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// パッケージの外の項目（ClipExtraData）: クリップの .meta に名前ごとに持ち、パッケージの印を保存しても消えない
    /// </summary>
    public class ClipExtraDataTests
    {
        const string kFolder = "Assets/__MktClipExtraDataTest";

        [System.Serializable]
        class Note
        {
            public string text;
            public int number;
        }

        [TearDown]
        public void TearDown ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        [Test]
        public void ExtrasSurviveThePackageSettings ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktClipExtraDataTest");
            AnimationClip clip = new AnimationClip ();
            AssetDatabase.CreateAsset (clip, kFolder + "/Walk.rig.anim");
            Assert.IsNull (ClipExtraData.Load<Note> (clip, "a"), "まだ無い");

            Assert.IsTrue (ClipExtraData.Save (clip, "a", new Note { text = "walk \"fast\"", number = 7 }));
            Assert.IsTrue (ClipExtraData.Save (clip, "b", new Note { text = "other" }));
            Assert.IsTrue (ClipCycle.Save (clip, true));
            Note a = ClipExtraData.Load<Note> (clip, "a");
            Assert.AreEqual ("walk \"fast\"", a.text, "パッケージの印を保存しても残る");
            Assert.AreEqual (7, a.number);
            Assert.IsTrue (ClipCycle.Load (clip), "項目を書いてもパッケージの印は残る");

            ClipExtraData.Save (clip, "a", new Note { text = "run" });
            Assert.AreEqual ("run", ClipExtraData.Load<Note> (clip, "a").text, "書き直す");
            Assert.AreEqual ("other", ClipExtraData.Load<Note> (clip, "b").text, "ほかの名前は残る");
            ClipExtraData.Save<Note> (clip, "a", null);
            Assert.IsNull (ClipExtraData.Load<Note> (clip, "a"), "null で消す");
            Assert.IsNotNull (ClipExtraData.Load<Note> (clip, "b"));

            Assert.IsFalse (ClipExtraData.Save (new AnimationClip (), "a", new Note ()), "アセットでないクリップ");
            Assert.IsNull (ClipExtraData.Load<Note> (new AnimationClip (), "a"));
        }
    }

}
