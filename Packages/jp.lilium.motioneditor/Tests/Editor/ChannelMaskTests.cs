using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 段のマスク（S17）。部位と決まりで効く範囲を決め、Override の合成と、Override へのキーの書き込みを絞る
    /// </summary>
    public class ChannelMaskTests
    {
        const string kFolder = "Assets/__MktChannelMaskTest";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
        }

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null && !EditorUtility.IsPersistent (o)) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        ChannelMask NewMask ()
        {
            ChannelMask mask = ScriptableObject.CreateInstance<ChannelMask> ();
            created_.Add (mask);
            return mask;
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        static EditorCurveBinding Rotation (string path)
        {
            return EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation.x");
        }

        bool Contains (ChannelMask mask, EditorCurveBinding binding)
        {
            return mask.Contains (binding, new ChannelMaskParts (definition_).PartOf (binding.path));
        }

        [Test]
        public void Contains_UsesBodyPartsThenRules ()
        {
            ChannelMask mask = NewMask ();
            mask.SetBodyPart (AvatarMaskBodyPart.RightArm, false);

            Assert.IsTrue (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.LeftHand))));
            Assert.IsFalse (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.RightHand))), "部位を外した骨");
            Assert.IsFalse (Contains (mask, Rotation (RigPaths.BodyPoint ("RightPalmIndex"))), "全身 IK の点は点の骨の部位");
            Assert.IsTrue (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.RightIndexProximal))), "指は Fingers の部位");
            Assert.IsTrue (Contains (mask, Rotation (RigPaths.Game ("Rig", "Target"))), "部位の無い操作値は含める");

            // パスの決まりは部位より後に効き、子にも効く
            mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.Fk (HumanBodyBones.RightHand), include = true });
            mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kGame, include = false });
            Assert.IsTrue (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.RightHand))));
            Assert.IsFalse (Contains (mask, Rotation (RigPaths.Game ("Rig", "Target"))), "子にも効く");

            // プロパティの決まり（任意のプロパティは表示モデルから見たパス。パターンを使える）
            mask.properties.Add (new ChannelMask.PropertyRule { type = "SkinnedMeshRenderer", property = "blendShape.Mouth_*", include = false });
            EditorCurveBinding mouth = EditorCurveBinding.FloatCurve (RigPaths.Props ("Body/Face"), typeof (SkinnedMeshRenderer), "blendShape.Mouth_A");
            EditorCurveBinding eye = EditorCurveBinding.FloatCurve (RigPaths.Props ("Body/Face"), typeof (SkinnedMeshRenderer), "blendShape.Eye_Close");
            Assert.IsFalse (Contains (mask, mouth));
            Assert.IsTrue (Contains (mask, eye));
            mask.properties.Add (new ChannelMask.PropertyRule { path = "Body/*", property = "blendShape.Mouth_A", include = true });
            Assert.IsTrue (Contains (mask, mouth), "後の決まりが勝つ");
        }

        [Test]
        public void Apply_LayersOnlyInsideTheMask ()
        {
            GameObject root = new GameObject ("Root");
            created_.Add (root);
            Transform left = CreatePath (root.transform, RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            Transform right = CreatePath (root.transform, RigPaths.Fk (HumanBodyBones.RightUpperArm));
            AnimationClip layer = NewClip ();
            Quaternion delta = Quaternion.Euler (0, 0, 30);
            SetRotation (layer, RigPaths.Fk (HumanBodyBones.LeftUpperArm), delta);
            SetRotation (layer, RigPaths.Fk (HumanBodyBones.RightUpperArm), delta);
            ChannelMask mask = NewMask ();
            mask.SetBodyPart (AvatarMaskBodyPart.RightArm, false);

            OverrideApplier.Apply (layer, OverrideMode.Additive, 1, 0, root.transform, mask, new ChannelMaskParts (definition_));

            Assert.Less (Quaternion.Angle (delta, left.localRotation), 0.01f, "マスクの中は重なる");
            Assert.Less (Quaternion.Angle (Quaternion.identity, right.localRotation), 0.01f, "マスクの外は重ならない");
        }

        [Test]
        public void Write_KeysOnlyInsideTheMask ()
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition_);
            disposables_.Add (session);
            session.clip = NewClip ();
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());

            ChannelMask mask = NewMask ();
            mask.SetBodyPart (AvatarMaskBodyPart.RightArm, false);
            AnimationClip layer = NewClip ();
            session.AddOverride (layer, OverrideMode.Additive, mask);
            session.overrideWriteIndex = 0;
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (layer);
            Assert.IsNotEmpty (bindings);
            Assert.IsTrue (bindings.Any (b => b.path == RigPaths.Fk (HumanBodyBones.LeftUpperArm)), "マスクの中は打つ");
            ChannelMaskParts parts = new ChannelMaskParts (definition_);
            foreach (EditorCurveBinding binding in bindings) {
                Assert.IsTrue (mask.Contains (binding, parts.PartOf (binding.path)), binding.path + " はマスクの外なので打たない");
            }
        }

        [Test]
        public void TargetsOutsideTheMask_CannotBeGrabbed ()
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition_);
            disposables_.Add (session);
            ChannelMask mask = NewMask ();
            mask.SetBodyPart (AvatarMaskBodyPart.RightArm, false);

            PoseTarget right = FindFk (session, HumanBodyBones.RightLowerArm);
            PoseTarget left = FindFk (session, HumanBodyBones.LeftLowerArm);
            Assert.IsTrue (PreviewWindow.IsOutsideMask (session.stage, mask, right));
            Assert.IsFalse (PreviewWindow.IsOutsideMask (session.stage, mask, left));
            Assert.IsFalse (PreviewWindow.IsOutsideMask (session.stage, null, right), "マスクが無ければ全部つかめる");
        }

        [Test]
        public void ImportAvatarMask_MapsBodyPartsAndBones ()
        {
            Animator model = TestSkeleton.CreateHumanoid (created_, true, "Weapon");
            Transform root = model.transform.root;
            AvatarMask source = new AvatarMask ();
            created_.Add (source);
            source.SetHumanoidBodyPartActive (AvatarMaskBodyPart.RightArm, false);
            Transform leftHand = model.GetBoneTransform (HumanBodyBones.LeftHand);
            Transform rightHand = model.GetBoneTransform (HumanBodyBones.RightHand);
            Transform weapon = leftHand.Find ("Weapon");
            Assert.IsNotNull (weapon);
            Transform[] transforms = { leftHand, rightHand, weapon };
            bool[] active = { false, false, true };
            source.transformCount = transforms.Length;
            for (int i = 0; i < transforms.Length; i++) {
                source.SetTransformPath (i, AnimationUtility.CalculateTransformPath (transforms[i], root));
                source.SetTransformActive (i, active[i]);
            }

            ChannelMask mask = NewMask ();
            mask.paths.Add (new ChannelMask.PathRule { pattern = "Old" });
            List<string> notes = new List<string> ();
            ChannelMaskAvatar.Import (source, mask, root.gameObject, notes);

            Assert.IsFalse (mask.GetBodyPart (AvatarMaskBodyPart.RightArm));
            Assert.IsTrue (mask.GetBodyPart (AvatarMaskBodyPart.LeftArm));
            Assert.AreEqual (1, mask.paths.Count, "部位の値と違う骨だけ決まりにする（前の決まりは置き換える）");
            Assert.AreEqual (RigPaths.Fk (HumanBodyBones.LeftHand), mask.paths[0].pattern);
            Assert.IsFalse (mask.paths[0].include);
            Assert.IsFalse (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.LeftHand))));
            Assert.IsTrue (Contains (mask, Rotation (RigPaths.Fk (HumanBodyBones.LeftLowerArm))));
            Assert.AreEqual (1, notes.Count, "Humanoid の骨でないパス（武器）は読まずに知らせる: " + string.Join (" / ", notes));

            // キャラが無ければ部位だけ読み、骨のパスを読まなかったことを知らせる
            notes.Clear ();
            ChannelMaskAvatar.Import (source, mask, null, notes);
            Assert.IsEmpty (mask.paths);
            Assert.AreEqual (1, notes.Count);
        }

        [Test]
        public void ExportAvatarMask_WritesBodyPartsAndReportsTheRest ()
        {
            ChannelMask mask = NewMask ();
            mask.SetBodyPart (AvatarMaskBodyPart.LeftLeg, false);
            mask.SetBodyPart (AvatarMaskBodyPart.RightLeg, false);
            AvatarMask destination = new AvatarMask ();
            created_.Add (destination);
            List<string> notes = new List<string> ();

            ChannelMaskAvatar.Export (mask, destination, notes);
            for (int i = 0; i < ChannelMask.kPartCount; i++) {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                Assert.AreEqual (mask.GetBodyPart (part), destination.GetHumanoidBodyPartActive (part), part.ToString ());
            }
            Assert.IsEmpty (notes, "部位だけなら全部移る");

            mask.paths.Add (new ChannelMask.PathRule { pattern = RigPaths.kGame, include = false });
            mask.properties.Add (new ChannelMask.PropertyRule { property = "blendShape.*" });
            ChannelMaskAvatar.Export (mask, destination, notes);
            Assert.AreEqual (2, notes.Count, "移らない決まり（パス・プロパティ）を知らせる");
        }

        [Test]
        public void DefaultMasks_AreShippedAndCoverTheirParts ()
        {
            List<ChannelMask> defaults = ChannelMaskFiles.LoadDefaults ();
            Assert.AreEqual (ChannelMaskFiles.kDefaultNames.Length, defaults.Count, "同梱した既定のマスクが読める");
            foreach (ChannelMask shipped in defaults) {
                ChannelMask expected = NewMask ();
                ChannelMaskFiles.FillDefault (expected, shipped.name);
                Assert.AreEqual (JsonUtility.ToJson (expected), JsonUtility.ToJson (shipped), shipped.name + " は FillDefault と同じ中身");
            }
            ChannelMask body = defaults[0], upper = defaults[1], hands = defaults[2], face = defaults[3];
            EditorCurveBinding leg = Rotation (RigPaths.Fk (HumanBodyBones.LeftUpperLeg));
            EditorCurveBinding hips = Rotation (RigPaths.Fk (HumanBodyBones.Hips));
            EditorCurveBinding arm = Rotation (RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            EditorCurveBinding hand = Rotation (RigPaths.Fk (HumanBodyBones.LeftHand));
            EditorCurveBinding finger = Rotation (RigPaths.Fk (HumanBodyBones.LeftIndexProximal));
            EditorCurveBinding mouth = EditorCurveBinding.FloatCurve (RigPaths.Props ("Body/Face"), typeof (SkinnedMeshRenderer), "blendShape.Mouth_A");
            EditorCurveBinding visible = EditorCurveBinding.FloatCurve (RigPaths.Props ("Body/Hat"), typeof (MeshRenderer), "m_Enabled");

            Assert.IsTrue (Contains (body, leg) && Contains (body, arm) && Contains (body, finger));
            Assert.IsFalse (Contains (body, mouth), "Body は表情を含めない");
            Assert.IsTrue (Contains (upper, arm) && Contains (upper, finger));
            Assert.IsFalse (Contains (upper, leg) || Contains (upper, hips) || Contains (upper, mouth), "Upper Body は脚・腰・表情を除く");
            Assert.IsTrue (Contains (hands, finger));
            Assert.IsFalse (Contains (hands, hand) || Contains (hands, arm) || Contains (hands, mouth), "Hands は指だけ");
            Assert.IsTrue (Contains (face, mouth));
            Assert.IsFalse (Contains (face, arm) || Contains (face, visible), "Face はブレンドシェイプだけ");
        }

        [Test]
        public void OverrideFiles_SaveTheMask ()
        {
            AssetDatabase.CreateFolder ("Assets", "__MktChannelMaskTest");
            AnimationClip source = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (source, kFolder + "/Source.anim");
            ChannelMask mask = ScriptableObject.CreateInstance<ChannelMask> ();
            AssetDatabase.CreateAsset (mask, kFolder + "/Mask.asset");
            OverrideClip entry = OverrideFiles.Create (source);
            entry.mask = mask;
            Assert.IsTrue (OverrideFiles.Save (entry, source));

            List<OverrideClip> found = OverrideFiles.FindFor (source);
            Assert.AreEqual (1, found.Count);
            Assert.AreEqual (mask, found[0].mask);

            entry.mask = null;
            OverrideFiles.Save (entry, source);
            Assert.IsNull (OverrideFiles.FindFor (source)[0].mask, "外すと全部に効く");
        }

        static PoseTarget FindFk (PoseSession session, HumanBodyBones bone)
        {
            return session.targets.First (t => {
                if (!(t is FkTarget)) return false;
                List<string> paths = new List<string> ();
                t.CollectKeyedPaths (paths);
                return paths.Contains (RigPaths.Fk (bone));
            });
        }

        static Transform CreatePath (Transform root, string path)
        {
            Transform parent = root;
            foreach (string name in path.Split ('/')) {
                Transform child = parent.Find (name);
                if (child == null) {
                    child = new GameObject (name).transform;
                    child.SetParent (parent, false);
                }
                parent = child;
            }
            return parent;
        }

        static void SetRotation (AnimationClip clip, string path, Quaternion value)
        {
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), AnimationCurve.Constant (0, 1, value[i]));
            }
        }
    }

}
