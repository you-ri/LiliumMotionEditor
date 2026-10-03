using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lilium
{

    /// <summary>
    /// キャラの設定とプロジェクト設定（S19）
    /// </summary>
    public class SettingsTests
    {
        const string kFolder = "Assets/__MktSettingsTest";
        string projectBackup_;
        GameObject prefab_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktSettingsTest");
            // プロジェクト設定は 1 つしか無いので、値を控えて後で戻す（保存はしない）
            projectBackup_ = EditorJsonUtility.ToJson (ProjectSettings.instance);
            ProjectSettings project = ProjectSettings.instance;
            project.animBankFolders.Clear ();
            project.poseBankFolders.Clear ();
            project.nameSuffixes.Clear ();
            project.characterSettingsFolder = SettingsLookup.kPrefabFolderToken;
            project.autoBake = true;
            project.rigDefinition = null;

            GameObject go = new GameObject ("Hero_Variant");
            prefab_ = PrefabUtility.SaveAsPrefabAsset (go, kFolder + "/Hero_Variant.prefab");
            Object.DestroyImmediate (go);
            SettingsLookup.Invalidate ();
        }

        [TearDown]
        public void TearDown ()
        {
            EditorJsonUtility.FromJsonOverwrite (projectBackup_, ProjectSettings.instance);
            EditorPrefs.DeleteKey (AnimBank.kPrefsKey + AssetDatabase.AssetPathToGUID (kFolder + "/Hero_Variant.prefab"));
            AssetDatabase.DeleteAsset (kFolder);
            SettingsLookup.Invalidate ();
        }

        [Test]
        public void ExpandReplacesNameAndPrefabFolder ()
        {
            ProjectSettings project = ProjectSettings.instance;
            project.nameSuffixes.Add ("_Variant");
            Assert.AreEqual ("Hero", SettingsLookup.CharacterName (prefab_, project), "接尾辞を除く");
            Assert.AreEqual ("Assets/Characters/Hero/Motions", SettingsLookup.Expand ("Assets/Characters/{name}/Motions/", prefab_, project));
            Assert.AreEqual (kFolder + "/Poses", SettingsLookup.Expand ("{prefabFolder}/Poses", prefab_, project));
        }

        /// <summary>
        /// フォルダはキャラの設定（上書きしたときだけ）→ プロジェクト設定の順。先頭は無くても入り、2 つ目からは実在するものだけ
        /// </summary>
        [Test]
        public void FoldersFollowCharacterThenProject ()
        {
            AssetDatabase.CreateFolder (kFolder, "Common");
            ProjectSettings project = ProjectSettings.instance;
            project.poseBankFolders.AddRange (new[] { "{prefabFolder}/Poses", kFolder + "/Common", kFolder + "/Missing", kFolder + "/Common" });

            List<string> fromProject = SettingsLookup.ResolveFolders (prefab_, false, project, null);
            CollectionAssert.AreEqual (new[] { kFolder + "/Poses", kFolder + "/Common" }, fromProject, "先頭は無くても入る・無いフォルダと重複は飛ばす");

            CharacterSettings character = ScriptableObject.CreateInstance<CharacterSettings> ();
            try {
                character.prefab = prefab_;
                character.poseBankFolders.Add ("{prefabFolder}/Mine");
                CollectionAssert.AreEqual (fromProject, SettingsLookup.ResolveFolders (prefab_, false, project, character), "上書きしていなければプロジェクト設定");
                character.overridePoseBankFolders = true;
                CollectionAssert.AreEqual (new[] { kFolder + "/Mine" }, SettingsLookup.ResolveFolders (prefab_, false, project, character), "上書きすればキャラの設定");
                CollectionAssert.AreEqual (new[] { kFolder + "/Motions" }, SettingsLookup.ResolveFolders (prefab_, true, project, character), "AnimBank は何も指定が無いのでパッケージの既定");
            }
            finally {
                Object.DestroyImmediate (character);
            }
        }

        /// <summary>
        /// キャラの設定を作ると prefab から引き当てられ、今の値と、この PC で選んでいたフォルダを取り込む。プロジェクト設定と同じ値は上書きにしない
        /// </summary>
        [Test]
        public void CreateImportsCurrentValues ()
        {
            ProjectSettings project = ProjectSettings.instance;
            project.characterSettingsFolder = "{prefabFolder}/Settings";
            project.nameSuffixes.Add ("_Variant");
            AnimBank.SetFolder (prefab_, kFolder + "/OldMotions");
            LayerState layers = new LayerState { write = "Override:0" };
            layers.values.Add (new LayerValues { id = "HumanoidAnimation", enabled = false });

            Assert.IsNull (SettingsLookup.Find (prefab_));
            CharacterSettings settings = SettingsLookup.Create (prefab_, layers, null, true);

            Assert.AreEqual (kFolder + "/Settings/Hero Motion Editor.asset", AssetDatabase.GetAssetPath (settings));
            Assert.AreSame (settings, SettingsLookup.Find (prefab_), "prefab から引き当てられる");
            Assert.AreEqual ("Override:0", settings.layers.write);
            Assert.IsFalse (settings.layers.Find ("HumanoidAnimation").enabled);
            Assert.AreNotSame (layers, settings.layers, "写しを持つ");
            Assert.IsFalse (settings.overrideAutoBake, "プロジェクト設定と同じ値は上書きにしない");
            Assert.IsTrue (settings.overrideAnimBankFolders);
            CollectionAssert.AreEqual (new[] { kFolder + "/OldMotions" }, settings.animBankFolders, "この PC で選んでいたフォルダを取り込む");
            Assert.AreEqual (kFolder + "/OldMotions", AnimBank.GetFolder (prefab_));

            AnimBank.SetFolder (prefab_, kFolder + "/NewMotions");
            Assert.AreEqual (kFolder + "/NewMotions", settings.animBankFolders[0], "設定があれば選び直しは設定へ書く");
        }

        [Test]
        public void DuplicateSettingsUseTheFirstAndWarn ()
        {
            CharacterSettings a = ScriptableObject.CreateInstance<CharacterSettings> ();
            a.prefab = prefab_;
            AssetDatabase.CreateAsset (a, kFolder + "/A.asset");
            CharacterSettings b = ScriptableObject.CreateInstance<CharacterSettings> ();
            b.prefab = prefab_;
            AssetDatabase.CreateAsset (b, kFolder + "/B" + System.Guid.NewGuid ().ToString ("N") + ".asset");
            SettingsLookup.Invalidate ();

            LogAssert.Expect (LogType.Warning, new Regex ("同じキャラ.*2 つある"));
            Assert.AreSame (a, SettingsLookup.Find (prefab_), "パスの順で先のもの");
        }

        /// <summary>
        /// パッケージの既定のアセットは SetDefault と同じ中身（SetDefault を直したらアセットも作り直す）
        /// </summary>
        [Test]
        public void PackageDefaultRigDefinitionMatchesSetDefault ()
        {
            EditRigDefinition packageDefault = EditRigDefinition.packageDefault;
            Assert.IsNotNull (packageDefault, EditRigDefinition.kPackageDefaultPath + " が読めない");
            EditRigDefinition expected = EditRigDefinition.CreateDefault ();
            try {
                Assert.AreEqual (EditorJsonUtility.ToJson (expected), EditorJsonUtility.ToJson (packageDefault));
            } finally {
                Object.DestroyImmediate (expected);
            }
        }

        [Test]
        public void EmptyProjectRigDefinitionUsesPackageDefault ()
        {
            ProjectSettings project = ProjectSettings.instance;
            Assert.AreSame (EditRigDefinition.packageDefault, project.defaultRigDefinition, "空ならパッケージの既定");

            EditRigDefinition custom = ScriptableObject.CreateInstance<EditRigDefinition> ();
            try {
                project.rigDefinition = custom;
                Assert.AreSame (custom, project.defaultRigDefinition, "入れればそれを使う");
            } finally {
                Object.DestroyImmediate (custom);
            }
        }
    }

}
