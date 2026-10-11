using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// リグエディタの一覧（S45a）。定義の全コントロールが、キャラに当てた結果どおりに並ぶか。
    /// 窓は画面なしで作れないので、一覧の中身だけをここで確かめる
    /// </summary>
    public class RigControlListTests : RigTestFixture
    {
        const float kTolerance = 1e-5f;

        static IEnumerable<RigControlEntry> OfKind (IEnumerable<RigControlEntry> entries, RigControlKind kind)
        {
            return entries.Where (e => e.kind == kind);
        }

        [Test]
        public void FullBodyDefinitionListsEveryControl ()
        {
            EditRigDefinition definition = CreateDefinition ();
            EditingRig rig = CreateEditingRig (definition, CreateCharacter ());
            List<RigControlEntry> entries = RigControlList.Build (rig);

            Assert.That (OfKind (entries, RigControlKind.Fk).Count (), Is.EqualTo (definition.fkControls.Count));
            // 全身 IK の定義では IK の組を使わないので、理由つきの「使われない」で並ぶ
            List<RigControlEntry> chains = OfKind (entries, RigControlKind.Ik).ToList ();
            Assert.That (chains.Count, Is.EqualTo (definition.ikChains.Count));
            Assert.That (chains.All (e => e.unused && e.disabledReason == RigBinding.kFullBodyIkReason), Is.True);

            List<RigControlEntry> points = OfKind (entries, RigControlKind.Point).ToList ();
            Assert.That (points.Count, Is.EqualTo (rig.binding.points.Count));
            Assert.That (points.Count, Is.GreaterThan (0));
            Assert.That (points.All (e => e.enabled && e.position.HasValue), Is.True);

            // 脚の組 2 つの支点 7 種類。全身 IK の定義では足の点の置き場所に使うので使える
            List<RigControlEntry> pivots = OfKind (entries, RigControlKind.Pivot).ToList ();
            Assert.That (pivots.Count, Is.EqualTo (2 * ReverseFoot.kPivotCount));
            Assert.That (pivots.All (e => e.enabled && e.position.HasValue), Is.True);

            Assert.That (entries.Select (e => e.id).Distinct ().Count (), Is.EqualTo (entries.Count), "選択に使う名前が重ならない");
        }

        [Test]
        public void PointsAndJointsSitOnTheCharacter ()
        {
            EditRigDefinition definition = CreateDefinition ();
            Animator display = CreateCharacter ();
            EditingRig rig = CreateEditingRig (definition, display);
            List<RigControlEntry> entries = RigControlList.Build (rig);

            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.LeftHand, HumanBodyBones.RightFoot }) {
                Vector3 expected = display.GetBoneTransform (bone).position;
                RigControlEntry fk = entries.Single (e => e.id == "Fk/" + bone);
                Assert.That (Vector3.Distance (fk.position.Value, expected), Is.LessThan (kTolerance), bone + " の FK");
                // 骨の付け根そのものの点（既定の一覧での名前は骨の名前）
                RigControlEntry point = entries.Single (e => e.id == "Point/" + bone);
                Assert.That (Vector3.Distance (point.position.Value, expected), Is.LessThan (kTolerance), bone + " の点");
            }
            // FK は親の FK の骨から線を引く
            RigControlEntry hand = entries.Single (e => e.id == "Fk/" + HumanBodyBones.LeftHand);
            Assert.That (hand.line.Length, Is.EqualTo (2));
            Assert.That (Vector3.Distance (hand.line[0], display.GetBoneTransform (HumanBodyBones.LeftLowerArm).position), Is.LessThan (kTolerance));
        }

        [Test]
        public void TwoBoneDefinitionUsesChainsAndFootRoll ()
        {
            EditRigDefinition definition = CreateDefinition ();
            definition.solver = RigSolver.TwoBoneIk;
            definition.ikChains.Single (c => c.name == "LeftLeg").reverseFoot = false;
            Animator display = CreateCharacter ();
            EditingRig rig = CreateEditingRig (definition, display);
            List<RigControlEntry> entries = RigControlList.Build (rig);

            Assert.That (OfKind (entries, RigControlKind.Point), Is.Empty);
            RigControlEntry arm = entries.Single (e => e.id == "Ik/LeftArm");
            Assert.That (arm.enabled, Is.True);
            Assert.That (arm.line.Length, Is.EqualTo (3), "根 → 中 → 先");
            Assert.That (Vector3.Distance (arm.position.Value, display.GetBoneTransform (HumanBodyBones.LeftHand).position), Is.LessThan (kTolerance));

            // 腕の組には支点が無い。足の転がしを切った脚の支点は「使われない」
            Assert.That (OfKind (entries, RigControlKind.Pivot).Any (e => e.id.StartsWith ("Pivot/LeftArm/")), Is.False);
            Assert.That (OfKind (entries, RigControlKind.Pivot).Where (e => e.id.StartsWith ("Pivot/RightLeg/")).All (e => e.enabled), Is.True);
            Assert.That (OfKind (entries, RigControlKind.Pivot).Where (e => e.id.StartsWith ("Pivot/LeftLeg/")).All (e => !e.enabled && e.unused), Is.True);
        }

        [Test]
        public void MissingBoneIsListedWithReason ()
        {
            EditingRig rig = CreateEditingRig (CreateDefinition (), CreateCharacter (withChest: false));
            RigControlEntry chest = RigControlList.Build (rig).Single (e => e.id == "Fk/" + HumanBodyBones.Chest);

            Assert.That (chest.enabled, Is.False);
            Assert.That (chest.unused, Is.False, "骨が無いのは、解き方で使わないのとは別");
            Assert.That (chest.disabledReason, Is.Not.Empty);
            Assert.That (chest.position.HasValue, Is.False);
        }

        [Test]
        public void ExtraControlsFollowTheirBones ()
        {
            EditRigDefinition definition = CreateDefinition ();
            definition.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Sword", pattern = "^Sword$" });
            definition.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Shield", pattern = "^Shield$" });
            Animator display = PlaceInPrefab (TestSkeleton.CreateHumanoid (created_, true, "Sword"));
            EditingRig rig = CreateEditingRig (definition, display);
            List<RigControlEntry> entries = RigControlList.Build (rig);

            RigControlEntry sword = entries.Single (e => e.id == "Extra/Sword");
            Assert.That (sword.enabled, Is.True);
            Transform swordBone = display.GetComponentsInChildren<Transform> ().Single (t => t.name == "Sword");
            Assert.That (Vector3.Distance (sword.position.Value, swordBone.position), Is.LessThan (kTolerance));
            RigControlEntry shield = entries.Single (e => e.id == "Extra/Shield");
            Assert.That (shield.enabled, Is.False);
            Assert.That (shield.position.HasValue, Is.False);
        }

        [Test]
        public void DefinitionErrorsComeFirst ()
        {
            EditRigDefinition definition = CreateDefinition ();
            definition.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Prop", pattern = "^Prop$" });
            definition.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Prop", pattern = "^Prop$" });
            Animator display = PlaceInPrefab (TestSkeleton.CreateHumanoid (created_, true, "Prop", "Prop"));
            EditingRig rig = CreateEditingRig (definition, display);

            int errorCount;
            List<string> problems = RigControlList.CollectProblems (rig, out errorCount);
            Assert.That (errorCount, Is.GreaterThan (0), "名前の重なりは定義の誤り");
            Assert.That (problems.Take (errorCount), Is.EquivalentTo (definition.Validate ().Distinct ()));
            // 同じパターンに骨が 2 本当たったのは、誤りではなく気にしておくこと（誤りの後ろに並ぶ）
            Assert.That (problems.Count, Is.GreaterThan (errorCount));
        }

        [Test]
        public void EntriesPointAtTheirDefinitionItems ()
        {
            EditRigDefinition definition = CreateDefinition ();
            EditingRig rig = CreateEditingRig (definition, CreateCharacter ());
            List<RigControlEntry> entries = RigControlList.Build (rig, definition);
            UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject (definition);

            Assert.That (entries.All (e => e.propertyPath != null), Is.True);
            Assert.That (entries.All (e => serialized.FindProperty (e.propertyPath) != null), Is.True);
            // パネルはこの項目の欄を出すので、同じ物を指していること
            RigControlEntry hand = entries.Single (e => e.id == "Fk/" + HumanBodyBones.LeftHand);
            Assert.That (serialized.FindProperty (hand.propertyPath + ".bone").enumNames[serialized.FindProperty (hand.propertyPath + ".bone").enumValueIndex], Is.EqualTo ("LeftHand"));
            RigControlEntry point = entries.Single (e => e.id == "Point/" + HumanBodyBones.Hips);
            Assert.That (serialized.FindProperty (point.propertyPath + ".name").stringValue, Is.EqualTo ("Hips"));
            RigControlEntry pivot = entries.First (e => e.kind == RigControlKind.Pivot && e.id.StartsWith ("Pivot/LeftLeg/"));
            Assert.That (serialized.FindProperty (pivot.propertyPath + ".name").stringValue, Is.EqualTo ("LeftLeg"), "支点は脚の組の項目");
            // 定義を渡さなければ、項目は指さない
            Assert.That (RigControlList.Build (rig).All (e => e.propertyPath == null), Is.True);
        }
    }

}
