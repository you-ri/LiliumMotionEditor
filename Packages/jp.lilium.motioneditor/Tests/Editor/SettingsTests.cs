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
            LayerSelection.Delete (prefab_);
            AssetDatabase.DeleteAsset (kFolder);
            SettingsLookup.Invalidate ();
        }

        /// <summary>
        /// ✏・✋ の選択はキャラごとにこの PC の設定へ持つ。無いうちはキャラの設定の値で始める
        /// </summary>
        [Test]
        public void LayerSelectionIsStoredPerPrefabInPrefs ()
        {
            LayerSelection.Delete (prefab_);
            LayerState fallback = new LayerState { write = "Override:0", manipulate = "FullBodyIk" };
            string write, manipulate;
            LayerSelection.Load (prefab_, fallback, out write, out manipulate);
            Assert.AreEqual ("Override:0", write, "この PC に無ければキャラの設定の値");
            Assert.AreEqual ("FullBodyIk", manipulate);

            LayerSelection.Save (prefab_, "EditingRig", "DisplayBone");
            LayerSelection.Load (prefab_, fallback, out write, out manipulate);
            Assert.AreEqual ("EditingRig", write);
            Assert.AreEqual ("DisplayBone", manipulate);
            Assert.AreEqual ("Override:0", fallback.write, "キャラの設定は変えない");

            // prefab がアセットでなければ持たない
            LayerSelection.Load (null, fallback, out write, out manipulate);
            Assert.AreEqual ("Override:0", write);
        }

        /// <summary>
        /// Auto Key はこの PC の設定。切り替えてもプロジェクト設定（共有するアセット）は変わらず、窓へ知らせる
        /// </summary>
        [Test]
        public void AutoKeyIsStoredInPrefs ()
        {
            bool saved = MotionEditorPrefs.autoKey;
            int changed = 0;
            System.Action count = () => changed++;
            MotionEditorPrefs.changed += count;
            try {
                string project = EditorJsonUtility.ToJson (ProjectSettings.instance);
                MotionEditorPrefs.autoKey = !saved;
                Assert.AreEqual (!saved, MotionEditorPrefs.autoKey);
                Assert.AreEqual (1, changed, "窓へ知らせる");
                MotionEditorPrefs.autoKey = !saved;
                Assert.AreEqual (1, changed, "同じ値では知らせない");
                Assert.AreEqual (project, EditorJsonUtility.ToJson (ProjectSettings.instance), "プロジェクト設定は変えない");
                StringAssert.DoesNotContain ("autoKey", project);
            } finally {
                MotionEditorPrefs.changed -= count;
                MotionEditorPrefs.autoKey = saved;
            }
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
        /// フォルダはプロジェクト設定だけから引く（S108）。キャラの設定に古い上書きが入っていても読まない。先頭は無くても入り、2 つ目からは実在するものだけ
        /// </summary>
        [Test]
        public void FoldersComeFromProjectOnly ()
        {
            AssetDatabase.CreateFolder (kFolder, "Common");
            ProjectSettings project = ProjectSettings.instance;
            project.poseBankFolders.AddRange (new[] { "{prefabFolder}/Poses", kFolder + "/Common", kFolder + "/Missing", kFolder + "/Common" });

            List<string> fromProject = SettingsLookup.ResolveFolders (prefab_, false, project);
            CollectionAssert.AreEqual (new[] { kFolder + "/Poses", kFolder + "/Common" }, fromProject, "先頭は無くても入る・無いフォルダと重複は飛ばす");
            CollectionAssert.AreEqual (new[] { kFolder + "/Motions" }, SettingsLookup.ResolveFolders (prefab_, true, project), "AnimBank は何も指定が無いのでパッケージの既定");

            CharacterSettings character = ScriptableObject.CreateInstance<CharacterSettings> ();
            character.prefab = prefab_;
#pragma warning disable 0618
            character.overridePoseBankFolders = true;
            character.poseBankFolders.Add ("{prefabFolder}/Mine");
#pragma warning restore 0618
            // 警告はパスで 1 回にしているので、前の実行と重ならない名前にする
            string path = kFolder + "/Character" + System.Guid.NewGuid ().ToString ("N") + ".asset";
            AssetDatabase.CreateAsset (character, path);
            SettingsLookup.Invalidate ();
            LogAssert.Expect (LogType.Warning, new Regex (Regex.Escape (path)));
            Assert.AreSame (character, SettingsLookup.Find (prefab_));
            CollectionAssert.AreEqual (fromProject, SettingsLookup.GetPoseBankFolders (prefab_), "キャラの設定の上書きは読まない");
        }

        /// <summary>
        /// 作る場所の選び直しはこの PC にだけ覚え、並びの先頭の代わりになる。キャラの設定へは書かない（S108）
        /// </summary>
        [Test]
        public void ChosenFolderStaysOnThisPc ()
        {
            AssetDatabase.CreateFolder (kFolder, "Common");
            ProjectSettings project = ProjectSettings.instance;
            project.animBankFolders.AddRange (new[] { "{prefabFolder}/Motions", kFolder + "/Common" });
            CharacterSettings settings = SettingsLookup.Create (prefab_, null, null);
            string before = EditorJsonUtility.ToJson (settings);

            AnimBank.SetFolder (prefab_, kFolder + "/Mine");
            CollectionAssert.AreEqual (new[] { kFolder + "/Mine", kFolder + "/Common" }, SettingsLookup.GetAnimBankFolders (prefab_), "選んだ場所が先頭。無い本来の先頭は並べない");
            Assert.AreEqual (before, EditorJsonUtility.ToJson (settings), "キャラの設定は変わらない");
            Assert.IsFalse (settings.hasObsoleteOverrides);

            AnimBank.SetFolder (prefab_, null);
            CollectionAssert.AreEqual (new[] { kFolder + "/Motions", kFolder + "/Common" }, SettingsLookup.GetAnimBankFolders (prefab_), "取り消すとプロジェクト設定の並び");
        }

        /// <summary>
        /// キャラの設定を作ると prefab から引き当てられ、今の値（Layers）を取り込む。上書きの項目は空のまま（S108）
        /// </summary>
        [Test]
        public void CreateImportsCurrentValues ()
        {
            ProjectSettings project = ProjectSettings.instance;
            project.characterSettingsFolder = "{prefabFolder}/Settings";
            project.nameSuffixes.Add ("_Variant");
            project.autoBake = false;
            AnimBank.SetFolder (prefab_, kFolder + "/OldMotions");
            LayerState layers = new LayerState { write = "Override:0" };
            layers.values.Add (new LayerValues { id = "HumanoidAnimation", enabled = false });

            Assert.IsNull (SettingsLookup.Find (prefab_));
            CharacterSettings settings = SettingsLookup.Create (prefab_, layers, null);

            Assert.AreEqual (kFolder + "/Settings/Hero Motion Editor.asset", AssetDatabase.GetAssetPath (settings));
            Assert.AreSame (settings, SettingsLookup.Find (prefab_), "prefab から引き当てられる");
            Assert.AreEqual ("Override:0", settings.layers.write);
            Assert.IsFalse (settings.layers.Find ("HumanoidAnimation").enabled);
            Assert.AreNotSame (layers, settings.layers, "写しを持つ");
            Assert.IsFalse (settings.hasObsoleteOverrides, "フォルダと Auto Bake の上書きを取り込まない");
            Assert.AreEqual (kFolder + "/OldMotions", AnimBank.GetFolder (prefab_), "この PC で選んだ場所はそのまま効く");
        }

        /// <summary>
        /// 古い上書きの入ったキャラの設定は、引くとアセットごとに 1 回だけ警告する。入っていなければ警告しない（S108）。
        /// 見るのはこのテストのアセットの警告だけ（キャラの設定を探す間に、プロジェクトの中の別のアセットの警告が Unity から出ることがある。
        /// 古いプラグインの .meta の警告が出るプロジェクトでは、LogAssert.NoUnexpectedReceived で落ちていた）
        /// </summary>
        [Test]
        public void ObsoleteOverridesWarnOncePerAsset ()
        {
            List<string> warnings = new List<string> ();
            Application.LogCallback collect = (message, stack, type) => {
                if (type == LogType.Warning && message.Contains (kFolder)) warnings.Add (message);
            };
            Application.logMessageReceived += collect;
            try {
                CharacterSettings clean = ScriptableObject.CreateInstance<CharacterSettings> ();
                clean.prefab = prefab_;
                AssetDatabase.CreateAsset (clean, kFolder + "/Clean.asset");
                SettingsLookup.Invalidate ();
                Assert.AreSame (clean, SettingsLookup.Find (prefab_));
                CollectionAssert.IsEmpty (warnings, "上書きが無ければ警告しない");
                AssetDatabase.DeleteAsset (kFolder + "/Clean.asset");

                // 警告はパスで 1 回にしているので、前の実行と重ならない名前にする
                string path = kFolder + "/Old" + System.Guid.NewGuid ().ToString ("N") + ".asset";
                CharacterSettings old = ScriptableObject.CreateInstance<CharacterSettings> ();
                old.prefab = prefab_;
#pragma warning disable 0618
                old.overrideAutoBake = true;
                old.autoBake = false;
#pragma warning restore 0618
                AssetDatabase.CreateAsset (old, path);
                SettingsLookup.Invalidate ();
                Assert.AreSame (old, SettingsLookup.Find (prefab_));
                Assert.AreEqual (1, warnings.Count, "上書きがあれば警告する");
                StringAssert.Contains (path, warnings[0]);
                SettingsLookup.Invalidate ();
                Assert.AreSame (old, SettingsLookup.Find (prefab_));
                Assert.AreEqual (1, warnings.Count, "2 回目は警告しない");
            } finally {
                Application.logMessageReceived -= collect;
            }
        }

        /// <summary>
        /// 窓の Auto（Output の段）はプロジェクト設定を読み書きし、キャラの設定に古い上書きがあっても使わない（S108）
        /// </summary>
        [Test]
        public void AutoBakeIsTheProjectValue ()
        {
            // 窓の切り替えはプロジェクト設定をファイルへ保存するので、SetUp で変えた値を戻してから始め、終わりに元の値で保存し直す
            EditorJsonUtility.FromJsonOverwrite (projectBackup_, ProjectSettings.instance);
            ProjectSettings project = ProjectSettings.instance;
            bool original = project.autoBake;
            PreviewWindow window = ScriptableObject.CreateInstance<PreviewWindow> ();
            try {
                Assert.AreEqual (original, window.autoBake);
                window.autoBake = !original;
                Assert.AreEqual (!original, project.autoBake, "切り替えはプロジェクト設定へ書く");
                Assert.AreEqual (!original, window.autoBake);
            }
            finally {
                window.autoBake = original;
                Object.DestroyImmediate (window);
            }
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
        /// パッケージの既定のアセットは、SetDefault の中身を調整した物。骨の並びと点は SetDefault のものを全部持つ（SetDefault で点を足したらアセットにも足す）
        /// </summary>
        [Test]
        public void PackageDefaultRigDefinitionCoversSetDefault ()
        {
            EditRigDefinition packageDefault = EditRigDefinition.packageDefault;
            Assert.IsNotNull (packageDefault, EditRigDefinition.kPackageDefaultPath + " が読めない");
            EditRigDefinition expected = EditRigDefinition.CreateDefault ();
            try {
                CollectionAssert.AreEqual (expected.fkControls.ConvertAll (c => c.bone), packageDefault.fkControls.ConvertAll (c => c.bone), "FK の骨");
                List<string> names = packageDefault.bodyPoints.ConvertAll (p => p.name);
                foreach (EditRigDefinition.BodyPoint point in expected.bodyPoints) {
                    CollectionAssert.Contains (names, point.name, "全身 IK の点");
                }
                CollectionAssert.AreEqual (expected.attachPoints.ConvertAll (p => p.name), packageDefault.attachPoints.ConvertAll (p => p.name), "付ける先");
                Assert.IsEmpty (packageDefault.Validate (), "定義の誤り");
            } finally {
                Object.DestroyImmediate (expected);
            }
        }

        /// <summary>
        /// 新しく作る定義（メニュー・Reset・複製元の無い複製）は、パッケージの既定のアセットと同じ中身で始める
        /// </summary>
        [Test]
        public void SetPackageDefaultCopiesThePackageAsset ()
        {
            EditRigDefinition packageDefault = EditRigDefinition.packageDefault;
            Assert.IsNotNull (packageDefault, EditRigDefinition.kPackageDefaultPath + " が読めない");
            EditRigDefinition definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            try {
                definition.SetPackageDefault ();
                Assert.AreEqual (JsonUtility.ToJson (packageDefault), JsonUtility.ToJson (definition));
            } finally {
                Object.DestroyImmediate (definition);
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
