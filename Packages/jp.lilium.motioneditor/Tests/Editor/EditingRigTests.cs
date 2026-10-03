using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 編集用の体（骨の複製とコントロール）
    /// </summary>
    public class EditingRigTests
    {
        readonly List<Object> created_ = new List<Object> ();
        readonly List<EditingRig> rigs_ = new List<EditingRig> ();
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
            foreach (EditingRig rig in rigs_) rig.Dispose ();
            rigs_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        /// <summary>
        /// キャラは prefab の中で回して置かれていることが多い（Y270 など）。ルートの向きも写ることを見るため、同じように置く
        /// </summary>
        Animator CreateCharacter (bool withChest = false, params string[] extraBones)
        {
            GameObject prefabRoot = new GameObject ("Prefab");
            created_.Add (prefabRoot);
            prefabRoot.transform.position = new Vector3 (1, 0, 2);

            Animator animator = TestSkeleton.CreateHumanoid (created_, withChest, extraBones);
            animator.transform.SetParent (prefabRoot.transform, false);
            animator.transform.localRotation = Quaternion.Euler (0, 270, 0);
            return animator;
        }

        EditingRig CreateRig (Animator display)
        {
            EditingRig rig = EditingRig.Create (definition_, display, name => new GameObject (name));
            rigs_.Add (rig);
            return rig;
        }

        static string PathOf (Transform t, Transform root)
        {
            return UnityEditor.AnimationUtility.CalculateTransformPath (t, root);
        }

        [Test]
        public void RootMatchesDisplayAnimatorPoseAndName ()
        {
            Animator display = CreateCharacter ();
            EditingRig rig = CreateRig (display);

            Assert.AreEqual (display.name, rig.root.name);
            Assert.That (Vector3.Distance (display.transform.position, rig.root.transform.position), Is.LessThan (1e-5f));
            Assert.That (Quaternion.Angle (display.transform.rotation, rig.root.transform.rotation), Is.LessThan (1e-3f));
        }

        [Test]
        public void BonesAreClonedWithSamePathsAndRestValues ()
        {
            Animator display = CreateCharacter (true, "weapon_L");
            EditingRig rig = CreateRig (display);

            Transform[] displayBones = display.GetComponentsInChildren<Transform> ().Where (t => t != display.transform).ToArray ();
            Assert.AreEqual (displayBones.Length, rig.boneCount);
            foreach (Transform bone in displayBones) {
                Transform editing = rig.GetEditingBone (bone);
                Assert.IsNotNull (editing, bone.name);
                Assert.AreEqual (PathOf (bone, display.transform), PathOf (editing, rig.root.transform));
                Assert.AreEqual (bone.localPosition, editing.localPosition);
                Assert.AreEqual (bone.localRotation, editing.localRotation);
            }
            Assert.AreEqual ("bone_LeftHand", rig.GetEditingBone (HumanBodyBones.LeftHand).name);
            Assert.IsNull (rig.GetEditingBone (HumanBodyBones.UpperChest));
        }

        [Test]
        public void CloneHasNoComponentsBesidesAnimatorOnRoot ()
        {
            Animator display = CreateCharacter ();
            display.GetBoneTransform (HumanBodyBones.Hips).gameObject.AddComponent<BoxCollider> ();
            EditingRig rig = CreateRig (display);

            // 骨の複製は Transform だけ。ほかに付くのはルートの Animator と、IK の組の切替（IkControl）だけ
            Component[] components = rig.root.GetComponentsInChildren<Component> (true)
                .Where (c => !(c is Transform) && !(c is Lilium.IkControl)).ToArray ();
            Assert.AreEqual (1, components.Length);
            Assert.AreEqual (4, rig.root.GetComponentsInChildren<Lilium.IkControl> (true).Length);
            Assert.IsTrue (rig.root.GetComponentsInChildren<Lilium.IkControl> (true).All (c => c.transform.parent.name == "IK"));
            Animator animator = components[0] as Animator;
            Assert.IsNotNull (animator);
            Assert.AreSame (rig.animator, animator);
            Assert.IsNull (animator.avatar);
            Assert.AreEqual (AnimatorCullingMode.AlwaysAnimate, animator.cullingMode);
            Assert.IsFalse (animator.applyRootMotion);
        }

        [Test]
        public void ControlsExistOnlyForEnabledBones ()
        {
            Animator display = CreateCharacter ();
            EditingRig rig = CreateRig (display);

            foreach (RigBinding.Fk fk in rig.binding.fk) {
                Transform control = rig.FindControl (fk.path);
                Assert.AreEqual (fk.enabled, control != null, fk.path);
                if (control != null) Assert.AreEqual (fk.path, PathOf (control, rig.root.transform));
            }
            foreach (RigBinding.Ik ik in rig.binding.ik) {
                Transform target = rig.FindControl (RigPaths.IkTarget (ik.chain.name));
                Transform hint = rig.FindControl (RigPaths.IkHint (ik.chain.name));
                Assert.IsNotNull (target, ik.chain.name);
                Assert.IsNotNull (hint, ik.chain.name);
                Assert.AreEqual (RigPaths.IkTarget (ik.chain.name), PathOf (target, rig.root.transform));
                // 目標は編集用ルートから見た値をそのまま持つので、途中の GameObject は動かさない
                Assert.AreEqual (Vector3.zero, target.parent.localPosition);
                Assert.AreEqual (Quaternion.identity, target.parent.localRotation);
            }
        }

        [Test]
        public void SyncCopiesEditingBonesToDisplay ()
        {
            Animator display = CreateCharacter ();
            EditingRig rig = CreateRig (display);
            Transform editingElbow = rig.GetEditingBone (HumanBodyBones.LeftLowerArm);
            Transform displayElbow = display.GetBoneTransform (HumanBodyBones.LeftLowerArm);

            editingElbow.localRotation = Quaternion.Euler (0, 45, 0);
            editingElbow.localPosition += new Vector3 (0, 0.1f, 0);
            rig.SyncToDisplay ();

            Assert.That (Quaternion.Angle (Quaternion.Euler (0, 45, 0), displayElbow.localRotation), Is.LessThan (1e-3f));
            Assert.That (Vector3.Distance (editingElbow.localPosition, displayElbow.localPosition), Is.LessThan (1e-6f));
        }

        [Test]
        public void ResetRestoresRestPose ()
        {
            Animator display = CreateCharacter ();
            EditingRig rig = CreateRig (display);
            Transform hips = rig.GetEditingBone (HumanBodyBones.Hips);
            Vector3 restPosition = hips.localPosition;
            Quaternion restRotation;
            Assert.IsTrue (rig.TryGetRestRotation (hips, out restRotation));

            hips.localPosition += Vector3.one;
            hips.localRotation = Quaternion.Euler (10, 20, 30);
            rig.ResetBonesToRest ();

            Assert.AreEqual (restPosition, hips.localPosition);
            Assert.AreEqual (restRotation, hips.localRotation);
        }

        [Test]
        public void ChildNamedControlsIsSkippedAndReported ()
        {
            Animator display = CreateCharacter ();
            new GameObject (RigPaths.kRoot).transform.SetParent (display.transform, false);

            EditingRig rig = CreateRig (display);

            Assert.IsTrue (rig.errors.Any (e => e.Contains (RigPaths.kRoot)));
            Assert.AreEqual (1, rig.root.transform.Cast<Transform> ().Count (t => t.name == RigPaths.kRoot));
            Assert.IsNotNull (rig.FindControl (RigPaths.Fk (HumanBodyBones.Hips)));
        }

        [Test]
        public void DisposeDestroysEverything ()
        {
            Animator display = CreateCharacter ();
            EditingRig rig = CreateRig (display);
            GameObject root = rig.root;
            Transform bone = rig.GetEditingBone (HumanBodyBones.Hips);

            rig.Dispose ();

            Assert.IsTrue (root == null);
            Assert.IsTrue (bone == null);
            Assert.IsNull (rig.GetEditingBone (display.GetBoneTransform (HumanBodyBones.Hips)));
        }
    }

}
