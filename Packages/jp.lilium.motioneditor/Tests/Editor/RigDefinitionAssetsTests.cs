using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// リグエディタの定義の扱い（S45b）。既定の定義を守る・複製してキャラに割り当てる・定義を使うキャラの逆引き。
    /// アセットはテスト用のフォルダに作り、終わったら消す
    /// </summary>
    public class RigDefinitionAssetsTests
    {
        const string kFolder = "Assets/LiliumRigDefinitionAssetsTests";

        readonly List<string> createdPaths_ = new List<string> ();

        [SetUp]
        public void SetUp ()
        {
            if (!AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.CreateFolder ("Assets", "LiliumRigDefinitionAssetsTests");
        }

        [TearDown]
        public void TearDown ()
        {
            // キャラの設定を作り直す処理は、置き場所をプロジェクト設定から決めるので、テストのフォルダの外にできることもある
            foreach (string path in createdPaths_) {
                if (!string.IsNullOrEmpty (path) && !path.StartsWith (kFolder)) AssetDatabase.DeleteAsset (path);
            }
            createdPaths_.Clear ();
            AssetDatabase.DeleteAsset (kFolder);
            SettingsLookup.Invalidate ();
        }

        GameObject CreatePrefab (string name)
        {
            GameObject go = new GameObject (name);
            try {
                return PrefabUtility.SaveAsPrefabAsset (go, kFolder + "/" + name + ".prefab");
            }
            finally {
                Object.DestroyImmediate (go);
            }
        }

        CharacterSettings CreateSettings (GameObject prefab, EditRigDefinition overrideDefinition)
        {
            CharacterSettings settings = ScriptableObject.CreateInstance<CharacterSettings> ();
            settings.prefab = prefab;
            settings.overrideRigDefinition = overrideDefinition != null;
            settings.rigDefinition = overrideDefinition;
            AssetDatabase.CreateAsset (settings, kFolder + "/" + prefab.name + " Settings.asset");
            SettingsLookup.Invalidate ();
            return settings;
        }

        EditRigDefinition CreateDefinitionAsset (string name)
        {
            // CreateDefault は保存しない一時の物を作るので、アセットにするときは中身だけ既定にする
            EditRigDefinition definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            definition.SetDefault ();
            AssetDatabase.CreateAsset (definition, kFolder + "/" + name + ".asset");
            return definition;
        }

        [Test]
        public void PackageAndTemporaryDefinitionsAreReadOnly ()
        {
            Assert.That (RigDefinitionAssets.GetReadOnlyReason (null), Is.Not.Null);
            EditRigDefinition temporary = EditRigDefinition.CreateDefault ();
            try {
                Assert.That (RigDefinitionAssets.GetReadOnlyReason (temporary), Is.Not.Null, "アセットでない定義");
            }
            finally {
                Object.DestroyImmediate (temporary);
            }
            if (EditRigDefinition.packageDefault != null) {
                Assert.That (RigDefinitionAssets.GetReadOnlyReason (EditRigDefinition.packageDefault), Is.Not.Null, "パッケージの既定");
            }
            Assert.That (RigDefinitionAssets.GetReadOnlyReason (CreateDefinitionAsset ("Own")), Is.Null, "プロジェクトのアセットは直せる");
        }

        [Test]
        public void DuplicateGoesToTheCharacterSettings ()
        {
            GameObject prefab = CreatePrefab ("Hero");
            CharacterSettings settings = CreateSettings (prefab, null);
            EditRigDefinition source = CreateDefinitionAsset ("Source");
            source.fullBody.iterations = 7;
            string before = JsonUtility.ToJson (source);

            EditRigDefinition copy = RigDefinitionAssets.CreateCopy (source, prefab, RigDefinitionAssets.AssignTarget.Character);

            Assert.That (AssetDatabase.GetAssetPath (copy), Is.EqualTo (kFolder + "/Hero Rig Definition.asset"), "キャラの設定と同じフォルダ・キャラ名の名前");
            Assert.That (JsonUtility.ToJson (copy), Is.EqualTo (before), "中身は元と同じ");
            Assert.That (JsonUtility.ToJson (source), Is.EqualTo (before), "元は変わらない");
            Assert.That (settings.overrideRigDefinition, Is.True);
            Assert.That (settings.rigDefinition, Is.SameAs (copy));
            // モーションエディタも同じ引き当て（RigDefinitionAssets.Resolve）で複製を使う
            Assert.That (RigDefinitionAssets.Resolve (SettingsLookup.Find (prefab)), Is.SameAs (copy));
        }

        [Test]
        public void DuplicateWithoutSettingsCreatesThem ()
        {
            GameObject prefab = CreatePrefab ("Villain");
            EditRigDefinition copy = RigDefinitionAssets.CreateCopy (null, prefab, RigDefinitionAssets.AssignTarget.Character);
            CharacterSettings settings = SettingsLookup.Find (prefab);
            createdPaths_.Add (AssetDatabase.GetAssetPath (settings));
            createdPaths_.Add (AssetDatabase.GetAssetPath (copy));

            Assert.That (settings, Is.Not.Null, "キャラの設定ができる");
            Assert.That (RigDefinitionAssets.Resolve (settings), Is.SameAs (copy));
            // 元が無ければ既定の中身で始める
            EditRigDefinition expected = EditRigDefinition.CreateDefault ();
            try {
                Assert.That (JsonUtility.ToJson (copy), Is.EqualTo (JsonUtility.ToJson (expected)));
            }
            finally {
                Object.DestroyImmediate (expected);
            }
        }

        [Test]
        public void PackageDefaultIsNotChangedByDuplicating ()
        {
            EditRigDefinition packageDefault = EditRigDefinition.packageDefault;
            if (packageDefault == null) Assert.Ignore ("パッケージの既定の定義が読めない");
            string before = JsonUtility.ToJson (packageDefault);
            EditRigDefinition copy = RigDefinitionAssets.CreateCopy (packageDefault, null, RigDefinitionAssets.AssignTarget.None);
            createdPaths_.Add (AssetDatabase.GetAssetPath (copy));
            copy.fullBody.iterations += 1;

            Assert.That (JsonUtility.ToJson (packageDefault), Is.EqualTo (before));
            Assert.That (RigDefinitionAssets.GetReadOnlyReason (copy), Is.Null, "複製は直せる");
        }

        [Test]
        public void UsersAreFoundFromCharacterSettings ()
        {
            EditRigDefinition own = CreateDefinitionAsset ("Own");
            CharacterSettings overriding = CreateSettings (CreatePrefab ("A"), own);
            CharacterSettings following = CreateSettings (CreatePrefab ("B"), null);

            List<RigDefinitionAssets.User> users = RigDefinitionAssets.FindUsers (own);
            Assert.That (users.Select (u => u.settings), Is.EquivalentTo (new[] { overriding }));
            Assert.That (users[0].viaProject, Is.False);

            // 上書きしていないキャラは、プロジェクト設定（空ならパッケージの既定）の定義を使う
            EditRigDefinition projectDefault = ProjectSettings.instance.defaultRigDefinition;
            if (projectDefault == null) return;
            RigDefinitionAssets.User follower = RigDefinitionAssets.FindUsers (projectDefault).Single (u => u.settings == following);
            Assert.That (follower.viaProject, Is.True);
        }
    }

}
