using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lilium
{

    /// <summary>
    /// リグのバリアントとクリップの印（S92a）。キャラ・持ち物・バリアント・クリップはアセットにして、引き当てを本物の道で通す
    /// </summary>
    public class RigVariantTests : RigTestFixture
    {
        const string kFolder = "Assets/__MktRigVariantTest";
        const string kSword = "Sword";
        const float kPoseTolerance = 1e-5f;

        GameObject hero_;
        GameObject swordModel_;
        EditRigDefinition definition_;
        CharacterSettings settings_;
        PreviewWindow window_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktRigVariantTest");

            // 人型のキャラを prefab のアセットにする（Avatar もアセットにしないと prefab から外れる）
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            Avatar avatar = display.avatar;
            created_.Remove (avatar);
            AssetDatabase.CreateAsset (avatar, kFolder + "/Hero Avatar.asset");
            GameObject root = new GameObject ("Hero");
            created_.Add (root);
            display.transform.SetParent (root.transform, false);
            hero_ = PrefabUtility.SaveAsPrefabAsset (root, kFolder + "/Hero.prefab");

            GameObject model = new GameObject ("Sword Model");
            created_.Add (model);
            new GameObject ("Blade").transform.SetParent (model.transform, false);
            swordModel_ = PrefabUtility.SaveAsPrefabAsset (model, kFolder + "/Sword Model.prefab");

            definition_ = EditRigDefinition.CreateDefault ();
            // 既定の定義は一時の物（保存しない）として作られるので、アセットにする前に外す
            definition_.hideFlags = HideFlags.None;
            UseFreeDefaults (definition_);
            AssetDatabase.CreateAsset (definition_, kFolder + "/Hero Rig Definition.asset");

            settings_ = ScriptableObject.CreateInstance<CharacterSettings> ();
            settings_.prefab = hero_;
            settings_.overrideRigDefinition = true;
            settings_.rigDefinition = definition_;
            AssetDatabase.CreateAsset (settings_, kFolder + "/Hero Motion Editor.asset");
            Invalidate ();
        }

        [TearDown]
        public void TearDown ()
        {
            if (window_ != null) Object.DestroyImmediate (window_);
            DestroyCreated ();
            AssetDatabase.DeleteAsset (kFolder);
            Invalidate ();
        }

        static void Invalidate ()
        {
            SettingsLookup.Invalidate ();
            RigVariants.Invalidate ();
        }

        HeldObjectDefinition CreateSword (string name = kSword)
        {
            return new HeldObjectDefinition {
                name = name,
                prefab = swordModel_,
                frame = HeldObjectFrame.Chest,
                restPosition = new Vector3 (0, -0.1f, 0.3f),
                restRotation = Vector3.zero,
                attachments = new List<HeldObjectAttachment> {
                    new HeldObjectAttachment { point = EditRigDefinition.kRightHandAttach, position = new Vector3 (0, 0.08f, -0.05f), rotation = Vector3.zero },
                    new HeldObjectAttachment { point = EditRigDefinition.kLeftHandAttach, position = new Vector3 (0, -0.08f, -0.05f), rotation = Vector3.zero },
                },
            };
        }

        RigVariant CreateVariant (string variantName, params HeldObjectDefinition[] objects)
        {
            RigVariant variant = ScriptableObject.CreateInstance<RigVariant> ();
            variant.prefab = hero_;
            variant.variantName = variantName;
            variant.heldObjects.AddRange (objects);
            AssetDatabase.CreateAsset (variant, AssetDatabase.GenerateUniqueAssetPath (kFolder + "/Hero " + variantName + " Rig Variant.asset"));
            Invalidate ();
            return variant;
        }

        AnimationClip CreateClip (string name, string variantName = null)
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/" + name + ".rig.anim");
            if (variantName != null) Assert.IsTrue (RigVariants.SetClipVariant (clip, variantName));
            return clip;
        }

        static string Meta (AnimationClip clip)
        {
            return AssetImporter.GetAtPath (AssetDatabase.GetAssetPath (clip)).userData;
        }

        // ---- 引き当て ----

        [Test]
        public void Resolve_AddsVariantObjects_AndReplacesSameName ()
        {
            HeldObjectDefinition baseSword = CreateSword ();
            HeldObjectDefinition sheath = CreateSword ("Sheath");
            settings_.heldObjects.AddRange (new[] { baseSword, sheath });
            HeldObjectDefinition variantSword = CreateSword ();
            HeldObjectDefinition shield = CreateSword ("Shield");
            CreateVariant (kSword, variantSword, shield);

            RigSetup plain = RigVariants.Resolve (hero_, "");
            CollectionAssert.AreEqual (new[] { baseSword, sheath }, plain.heldObjects, "ベースはキャラの設定の物");
            Assert.AreSame (definition_, plain.definition);
            Assert.AreEqual (settings_.GetHeldObjectsKey (), plain.heldObjectsKey, "ベースの判定の値はバリアントを入れる前と同じ");

            RigSetup sword = RigVariants.Resolve (hero_, kSword);
            Assert.AreSame (variantSword, sword.heldObjects[0], "同じ名前はバリアントの物を丸ごと使う（元の項目の参照）");
            Assert.AreSame (sheath, sword.heldObjects[1], "ベースの物は流れてくる");
            Assert.AreSame (shield, sword.heldObjects[2], "バリアントの物を足す");
            Assert.IsFalse (sword.missing);
            Assert.AreNotEqual (plain.key, sword.key);

            sheath.restPosition = new Vector3 (0, 0, 0.5f);
            Assert.AreSame (sheath, RigVariants.Resolve (hero_, kSword).heldObjects[1], "ベースの物を直すと上書きしていないバリアントへ流れる");
        }

        [Test]
        public void Resolve_MissingVariant_OpensWithBaseObjects ()
        {
            settings_.heldObjects.Add (CreateSword ("Sheath"));
            RigSetup setup = RigVariants.Resolve (hero_, "Axe");
            Assert.IsTrue (setup.missing);
            Assert.AreEqual ("Axe", setup.variantName);
            Assert.AreEqual (RigVariants.Resolve (hero_, "").key, setup.key, "ベースと同じ世界");
        }

        [Test]
        public void DuplicateVariantNames_UseTheFirstAndWarn ()
        {
            RigVariant first = CreateVariant (kSword, CreateSword ());
            RigVariant second = ScriptableObject.CreateInstance<RigVariant> ();
            second.prefab = hero_;
            second.variantName = kSword;
            // 警告はパスで 1 回にしているので、前の実行と重ならない名前にする
            AssetDatabase.CreateAsset (second, kFolder + "/Z" + System.Guid.NewGuid ().ToString ("N") + ".asset");
            Invalidate ();
            LogAssert.Expect (LogType.Warning, new Regex (kSword));
            Assert.AreSame (first, RigVariants.Find (hero_, kSword), "パスの順で先のもの");
            Assert.AreEqual (1, RigVariants.FindAll (hero_).Count);
        }

        // ---- クリップの印 ----

        [Test]
        public void ClipMark_IsReadWithoutWriting_AndKeptOffBakedClips ()
        {
            AnimationClip plain = CreateClip ("Plain");
            string before = Meta (plain);
            Assert.AreEqual ("", RigVariants.GetClipVariant (plain), "印の無いクリップはベース");
            RigVariants.Resolve (hero_, plain);
            Assert.AreEqual (before, Meta (plain), "開くだけでは .meta を書かない");

            AnimationClip sword = CreateClip ("Swing", kSword);
            Assert.AreEqual (kSword, RigVariants.GetClipVariant (sword));
            Assert.IsTrue (RigVariants.SetClipVariant (sword, ""));
            Assert.AreEqual ("", RigVariants.GetClipVariant (sword), "空にすると印を消す");

            // 焼いた版（Link を持つクリップ）には書かない
            AnimationClip baked = new AnimationClip ();
            AssetDatabase.CreateAsset (baked, kFolder + "/Plain.anim");
            HumanoidOutput.WriteLink (kFolder + "/Plain.anim", plain, hero_);
            string link = AssetImporter.GetAtPath (kFolder + "/Plain.anim").userData;
            Assert.IsFalse (RigVariants.SetClipVariant (baked, kSword));
            Assert.AreEqual (link, AssetImporter.GetAtPath (kFolder + "/Plain.anim").userData, "焼いた版の対応を壊さない");
            Assert.AreEqual ("", RigVariants.GetClipVariant (baked));
        }

        [Test]
        public void ClipProblem_ForMissingVariant_OtherVariant_AndUnknownObjectCurves ()
        {
            CreateVariant (kSword, CreateSword ());
            AnimationClip plain = CreateClip ("Plain");
            AnimationClip sword = CreateClip ("Swing", kSword);
            AnimationClip axe = CreateClip ("Chop", "Axe");
            RigSetup plainSetup = RigVariants.Resolve (hero_, "");
            RigSetup swordSetup = RigVariants.Resolve (hero_, kSword);

            Assert.IsNull (RigVariants.GetClipProblem (plain, plainSetup));
            Assert.IsNull (RigVariants.GetClipProblem (sword, swordSetup));
            Assert.IsNotNull (RigVariants.GetClipProblem (sword, plainSetup), "印と違うリグ");
            Assert.IsNotNull (RigVariants.GetClipProblem (axe, RigVariants.Resolve (hero_, axe)), "無いバリアント");

            // キーの無いカーブ（物を外したときの名残）は姿勢に効かないので数えない
            AnimationUtility.SetEditorCurve (plain, EditorCurveBinding.FloatCurve (RigPaths.Object (kSword) + "/RightHand", typeof (HeldObjectControl), "attached"), new AnimationCurve ());
            Assert.IsNull (RigVariants.GetClipProblem (plain, plainSetup), "キーの無いカーブ");
            // 素のクリップに剣のキー（剣のリグで打ったものを貼った・印を消した）
            AnimationUtility.SetEditorCurve (plain, EditorCurveBinding.FloatCurve (RigPaths.Object (kSword), typeof (Transform), "m_LocalPosition.x"), AnimationCurve.Constant (0, 1, 0));
            StringAssert.Contains (kSword, RigVariants.GetClipProblem (plain, plainSetup), "今のリグに無い物のキー");
            int curves = AnimationUtility.GetCurveBindings (plain).Length;
            Assert.Greater (curves, 0);
            Assert.AreEqual (curves, AnimationUtility.GetCurveBindings (plain).Length, "カーブは自動で消さない");
        }

        // ---- 書く・焼くを断る ----

        [Test]
        public void Session_RefusesKeysAndBake_WhenClipVariantDiffers ()
        {
            CreateVariant (kSword, CreateSword ());
            AnimationClip sword = CreateClip ("Swing", kSword);
            using (PoseSession session = new PoseSession (hero_, RigVariants.Resolve (hero_, ""))) {
                session.clip = sword;
                session.Evaluate (0);
                Assert.IsNotNull (session.GetWriteProblem (), "印と違うリグのセッションでは打てない");
                Assert.IsNotNull (session.CommitPose ());
                string outputPath;
                HumanoidBaker.Result result;
                Assert.IsNotNull (session.Bake (null, out outputPath, out result), "焼けない");
                Assert.AreEqual (0, AnimationUtility.GetCurveBindings (sword).Length, "カーブが増えない");
            }
            using (PoseSession session = new PoseSession (hero_, RigVariants.Resolve (hero_, sword))) {
                session.clip = sword;
                session.Evaluate (0);
                Assert.IsNull (session.GetWriteProblem (), "印のバリアントのセッションなら打てる");
                Assert.IsNotNull (session.stage.heldObjects);
            }
        }

        // ---- 焼いた版の判定 ----

        [Test]
        public void BakedKey_FollowsTheClipVariant ()
        {
            RigVariant variant = CreateVariant (kSword, CreateSword ());
            AnimationClip plain = CreateClip ("Plain");
            AnimationClip sword = CreateClip ("Swing", kSword);
            string plainKey = LinkDefinition (plain);
            string swordKey = LinkDefinition (sword);
            Assert.AreEqual ("", plainKey, "持ち物の無いベースのクリップは前と同じ空");
            Assert.AreNotEqual ("", swordKey);

            variant.heldObjects[0].attachments[0].position = new Vector3 (0, 0.1f, -0.05f);
            Assert.AreNotEqual (swordKey, LinkDefinition (sword), "剣の握りを直すと剣のクリップだけ古い");
            Assert.AreEqual (plainKey, LinkDefinition (plain));
        }

        string LinkDefinition (AnimationClip source)
        {
            string output = kFolder + "/" + source.name + " Baked.anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip> (output) == null) AssetDatabase.CreateAsset (new AnimationClip (), output);
            HumanoidOutput.WriteLink (output, source, hero_);
            return HumanoidOutput.ReadLink (output).definition ?? "";
        }

        // ---- 窓 ----

        [Test]
        public void Window_FollowsClipVariant_AndRebuildsOnlyWhenObjectsDiffer ()
        {
            CreateVariant (kSword, CreateSword ());
            AnimationClip walk = CreateClip ("Walk");
            AnimationClip idle = CreateClip ("Idle");
            AnimationClip swing = CreateClip ("Swing", kSword);
            AnimationClip slash = CreateClip ("Slash", kSword);

            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (hero_);
            window_.SetClip (walk);
            Assert.IsNull (window_.stage.heldObjects, "印の無いクリップは物を持たない");
            List<Vector3> walkPose = Bones (window_.stage);
            PreviewStage plainStage = window_.stage;
            window_.SetClip (idle);
            Assert.AreSame (plainStage, window_.stage, "同じバリアントのクリップへは世界を作り直さない");

            window_.SetClip (swing);
            Assert.IsNotNull (window_.stage.heldObjects, "剣のクリップは剣を持つ");
            Assert.AreEqual (kSword, window_.rigSetup.variantName);
            Assert.IsFalse (window_.isClipReadOnly, "印のバリアントで開いたので書ける");
            List<Vector3> swingPose = Bones (window_.stage);
            PreviewStage swordStage = window_.stage;
            window_.SetClip (slash);
            Assert.AreSame (swordStage, window_.stage);

            using (PoseSession single = new PoseSession (hero_, RigVariants.Resolve (hero_, kSword))) {
                single.clip = swing;
                single.Evaluate (0);
                AssertSame (Bones (single.stage), swingPose, "剣のクリップは剣のリグ 1 つのときと同じ");
            }
            window_.SetClip (walk);
            AssertSame (walkPose, Bones (window_.stage), "素のクリップへ戻すと同じ姿勢");
            window_.SetClip (swing);
            AssertSame (swingPose, Bones (window_.stage), "剣のクリップへ戻すと同じ姿勢");
        }

        [Test]
        public void Window_NewClipInheritsVariant_AndRefusesMissingVariant ()
        {
            CreateVariant (kSword, CreateSword ());
            AnimationClip swing = CreateClip ("Swing", kSword);
            AnimationClip chop = CreateClip ("Chop", "Axe");

            window_ = ScriptableObject.CreateInstance<PreviewWindow> ();
            window_.SetModel (hero_);
            window_.SetClip (swing);
            AnimationClip created = CreateClip ("New Motion");
            window_.InheritRigVariant (created);
            Assert.AreEqual (kSword, RigVariants.GetClipVariant (created), "新しいクリップは今のバリアントを継ぐ");

            window_.SetClip (chop);
            Assert.IsTrue (window_.isClipReadOnly, "無いバリアントの印は見られるが書けない");
            StringAssert.Contains ("Axe", window_.clipProblem);
            AnimationClip other = CreateClip ("Other");
            window_.InheritRigVariant (other);
            Assert.AreEqual ("", RigVariants.GetClipVariant (other), "無いバリアントの印は継がない");
        }

        static List<Vector3> Bones (PreviewStage stage)
        {
            return stage.animator.GetComponentsInChildren<Transform> ().Where (t => t.GetComponent<SkinnedMeshRenderer> () == null).Select (t => t.position).ToList ();
        }

        static void AssertSame (List<Vector3> expected, List<Vector3> actual, string message)
        {
            Assert.AreEqual (expected.Count, actual.Count, message + ": 骨の数");
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i], actual[i]), kPoseTolerance, message + ": " + i + " 番目");
            }
        }
    }

}
