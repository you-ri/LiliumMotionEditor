using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// リグの定義と、キャラへの割り当て。Avatar はテストの中で骨を組んで作る（パッケージの外のアセットに頼らない）
    /// </summary>
    public class RigBindingTests
    {
        static readonly HumanBodyBones[] kRequired = {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            // 2 本骨 IK の組を見るテストなので、既定（全身 IK）から切り替える
            definition_.solver = RigSolver.TwoBoneIk;
            created_.Add (definition_);
        }

        [TearDown]
        public void TearDown ()
        {
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
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
        public void DefaultIkChainsUseOnlyRequiredBones ()
        {
            Assert.AreEqual (4, definition_.ikChains.Count);
            foreach (EditRigDefinition.IkChain chain in definition_.ikChains) {
                foreach (HumanBodyBones bone in new[] { chain.root, chain.mid, chain.tip }) {
                    Assert.IsTrue (HumanTrait.RequiredBone ((int)bone), chain.name + " の " + bone + " が必須の骨でない");
                }
            }
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
            Assert.IsTrue (binding.ik.All (c => c.enabled));
            Assert.AreEqual ("bone_LeftUpperArm", binding.FindIk ("LeftArm").root.name);
            Assert.AreEqual ("bone_LeftHand", binding.FindIk ("LeftArm").tip.name);
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
            Assert.AreEqual ("Controls/IK/LeftArm", binding.FindIk ("LeftArm").path);
            Assert.AreEqual ("Controls/IK/LeftArm/Target", RigPaths.IkTarget ("LeftArm"));
            Assert.AreEqual ("Controls/IK/LeftArm/Hint", RigPaths.IkHint ("LeftArm"));
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
            Assert.IsTrue (binding.ik.All (c => !c.enabled));
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
            definition_.ikChains.Add (new EditRigDefinition.IkChain {
                name = "LeftArm", root = HumanBodyBones.Spine, mid = HumanBodyBones.Head, tip = HumanBodyBones.Head,
            });
            definition_.ikChains.Add (new EditRigDefinition.IkChain {
                name = "Bad/Name", root = HumanBodyBones.Spine, mid = HumanBodyBones.Chest, tip = HumanBodyBones.Head,
            });
            definition_.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Broken", pattern = "(" });

            List<string> errors = definition_.Validate ();

            Assert.IsTrue (errors.Any (e => e.Contains ("LeftArm") && e.Contains ("名前が重複")));
            Assert.IsTrue (errors.Any (e => e.Contains ("Bad/Name")));
            Assert.IsTrue (errors.Any (e => e.Contains ("骨が重複")));
            Assert.IsTrue (errors.Any (e => e.Contains ("Broken")));
        }

        [Test]
        public void ChainOutOfOrderIsDisabled ()
        {
            definition_.ikChains.Add (new EditRigDefinition.IkChain {
                name = "Reversed", root = HumanBodyBones.LeftHand, mid = HumanBodyBones.LeftLowerArm, tip = HumanBodyBones.LeftUpperArm,
            });

            RigBinding binding = RigBinding.Bind (definition_, CreateHumanoid (false));

            RigBinding.Ik reversed = binding.FindIk ("Reversed");
            Assert.IsFalse (reversed.enabled);
            StringAssert.Contains ("の下に無い", reversed.disabledReason);
        }
    }

}
