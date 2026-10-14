using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// リグの定義と、キャラへの割り当て。Avatar はテストの中で骨を組んで作る（パッケージの外のアセットに頼らない）
    /// </summary>
    public class RigBindingTests : RigTestFixture
    {
        static readonly HumanBodyBones[] kRequired = {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = CreateDefinition ();
        }

        Animator CreateHumanoid (bool withChest, params string[] extraBones)
        {
            return TestSkeleton.CreateHumanoid (created_, withChest, extraBones);
        }

        [Test]
        public void DefaultDefinitionCoversAllHumanBones ()
        {
            CollectionAssert.IsEmpty (definition_.Validate ());
            Assert.AreEqual ((int)HumanBodyBones.LastBone, definition_.fkControls.Count);
            Assert.AreEqual (HumanTrait.RequiredBoneCount, definition_.fkControls.Count (c => c.required));
            CollectionAssert.AreEquivalent (new[] { HumanBodyBones.Hips }, definition_.fkControls.Where (c => c.position).Select (c => c.bone));
        }

        [Test]
        public void MinimalSkeletonEnablesRequiredAndDisablesOptional ()
        {
            RigBinding binding = RigBinding.Bind (definition_, CreateHumanoid (false));

            CollectionAssert.IsEmpty (binding.errors);
            foreach (RigBinding.Fk fk in binding.fk) {
                Assert.AreEqual (kRequired.Contains (fk.control.bone), fk.enabled, fk.control.bone.ToString ());
                if (!fk.enabled) StringAssert.Contains (fk.control.bone.ToString (), fk.disabledReason);
            }
            Assert.IsNotEmpty (binding.points);
            Assert.IsTrue (binding.points.All (c => c.enabled));
            Assert.AreEqual ("bone_LeftHand", binding.FindPoint (HumanBodyBones.LeftHand).bone.name);
            // キャラに無い骨の付け根の点は、親の骨へ乗せずに作らない
            Assert.IsNull (binding.FindPoint (HumanBodyBones.Neck));
            // 背骨をまとめた胸の点は、ある背骨へ乗る
            Assert.AreEqual (HumanBodyBones.Spine, binding.FindPoint (HumanBodyBones.Chest).humanBone);
        }

        [Test]
        public void OptionalBonesAreEnabledWhenPresent ()
        {
            RigBinding binding = RigBinding.Bind (definition_, CreateHumanoid (true));

            Assert.IsTrue (binding.FindFk (HumanBodyBones.Chest).enabled);
            Assert.IsTrue (binding.FindFk (HumanBodyBones.Neck).enabled);
            Assert.IsFalse (binding.FindFk (HumanBodyBones.UpperChest).enabled);
            Assert.AreEqual ("bone_Chest", binding.FindFk (HumanBodyBones.Chest).bone.name);
        }

        [Test]
        public void PathsAreFixedAndIndependentOfBoneNames ()
        {
            RigBinding binding = RigBinding.Bind (definition_, CreateHumanoid (false));

            Assert.AreEqual ("Controls/FK/LeftHand", binding.FindFk (HumanBodyBones.LeftHand).path);
            Assert.AreEqual ("Controls/FK/LeftThumbProximal", binding.FindFk (HumanBodyBones.LeftThumbProximal).path);
            Assert.AreEqual ("Controls/Body/LeftHand", binding.FindPoint (HumanBodyBones.LeftHand).path);
            Assert.AreEqual ("Controls/Body/HeadDirection", RigPaths.BodyDirection (HumanBodyBones.Head));
            Assert.IsFalse (binding.controls.Any (c => c.path.Contains ("bone_")));
        }

        [Test]
        public void NonHumanAnimatorDisablesEverything ()
        {
            GameObject go = new GameObject ("Generic");
            created_.Add (go);
            Animator animator = go.AddComponent<Animator> ();

            RigBinding binding = RigBinding.Bind (definition_, animator);

            Assert.IsNotEmpty (binding.errors);
            Assert.IsTrue (binding.fk.All (c => !c.enabled));
            Assert.IsEmpty (binding.points, "点の乗る骨が無い");
            // 無効でもコントロールは残す（共有クリップのカーブを消さない）
            Assert.AreEqual (definition_.fkControls.Count, binding.fk.Count);
        }

        [Test]
        public void ExtraControlsAreFoundByPattern ()
        {
            definition_.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Weapon", pattern = "^weapon_" });
            definition_.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Tail", pattern = "^tail" });
            definition_.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Ring", pattern = "^ring" });

            RigBinding binding = RigBinding.Bind (definition_, CreateHumanoid (false, "weapon_L", "ring1", "ring2"));

            RigBinding.Extra weapon = binding.extras.Single (e => e.control.name == "Weapon");
            Assert.IsTrue (weapon.enabled);
            Assert.AreEqual ("weapon_L", weapon.bone.name);
            Assert.AreEqual ("Controls/Extra/Weapon", weapon.path);

            Assert.IsFalse (binding.extras.Single (e => e.control.name == "Tail").enabled);

            RigBinding.Extra ring = binding.extras.Single (e => e.control.name == "Ring");
            Assert.AreEqual ("ring1", ring.bone.name);
            Assert.IsTrue (binding.notes.Any (n => n.StartsWith ("Ring")));
        }

        [Test]
        public void InvalidDefinitionIsReported ()
        {
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "LeftHand", bone = HumanBodyBones.LeftHand });
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "Bad/Name", bone = HumanBodyBones.Head });
            definition_.fkControls.Add (new EditRigDefinition.FkControl { bone = HumanBodyBones.Spine });
            definition_.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Broken", pattern = "(" });

            List<string> errors = definition_.Validate ();

            Assert.IsTrue (errors.Any (e => e.Contains ("LeftHand") && e.Contains ("名前が重複")), string.Join (" / ", errors));
            Assert.IsTrue (errors.Any (e => e.Contains ("Bad/Name")));
            Assert.IsTrue (errors.Any (e => e.Contains ("Spine") && e.Contains ("骨が重複")));
            Assert.IsTrue (errors.Any (e => e.Contains ("Broken")));
        }
    }

}
