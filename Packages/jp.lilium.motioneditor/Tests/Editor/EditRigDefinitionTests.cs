using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// リグの定義（EditRigDefinition）そのもの: 既定の値・古い版の定義の版上げ・検証・写し・全身 IK の設定のハッシュ。
    /// キャラに組まずに確かめられるものだけをここに置く
    /// </summary>
    public class EditRigDefinitionTests : RigTestFixture
    {
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            // 既定のまま（ソルバーは全身 IK。DefaultDefinition_UsesFullBodyIk で確かめる）
            definition_ = CreateDefinition ();
        }

        // ---- 既定 ----

        [Test]
        public void DefaultDefinition_UsesFullBodyIk ()
        {
            Assert.AreEqual (RigSolver.FullBodyIk, definition_.solver, "新しく作る定義の既定は全身 IK");
        }

        [Test]
        public void Defaults_StiffnessGrowsTowardBodyCenter_AndFingersAreNotSolved ()
        {
            float Stiffness (HumanBodyBones bone)
            {
                return definition_.fkControls.First (c => c.bone == bone).stiffness;
            }

            Assert.Greater (Stiffness (HumanBodyBones.Hips), Stiffness (HumanBodyBones.Spine));
            Assert.Greater (Stiffness (HumanBodyBones.Spine), Stiffness (HumanBodyBones.Chest));
            Assert.Greater (Stiffness (HumanBodyBones.Chest), Stiffness (HumanBodyBones.LeftShoulder));
            Assert.Greater (Stiffness (HumanBodyBones.LeftShoulder), Stiffness (HumanBodyBones.LeftUpperArm));
            Assert.AreEqual (Stiffness (HumanBodyBones.LeftUpperArm), Stiffness (HumanBodyBones.RightUpperArm), "左右対称");

            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftIndexDistal).fullBodySolve);
            Assert.IsTrue (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftToes).fullBodySolve, "つま先は接地に使うので解く");

            // 全身 IK の点をつかむ間も出すのは、解かない（点の無い）骨だけ
            Assert.IsTrue (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftIndexDistal).fullBodyHandle);
            Assert.IsTrue (definition_.fkControls.First (c => c.bone == HumanBodyBones.Jaw).fullBodyHandle);
            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftHand).fullBodyHandle);
        }

        [Test]
        public void DefaultBodyPoints_Names ()
        {
            string[] names = definition_.bodyPoints.Select (p => p.name).ToArray ();
            foreach (string name in new[] { "Hips", "Head", "LeftHand", "RightFoot", "HipsDirection", "UpperChestDirection", "HeadDirection", "HeadTop",
                "LeftKneePole", "RightKneePole", "LeftElbowPole", "RightElbowPole", "LeftHeel", "RightSoleBall", "LeftLittleToeSide", "RightToeTip", "LeftPalmIndex", "RightPalmLittle" }) {
                Assert.Contains (name, names, name);
            }
            Assert.IsTrue (definition_.bodyPoints.First (p => p.name == "LeftHand").rotation, "手は向きを持つ");
            Assert.IsFalse (definition_.bodyPoints.First (p => p.name == "LeftLowerArm").rotation, "肘は位置だけ");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name == "Jaw"), "解かない骨には置かない");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name == "LeftToes" || p.name == "RightToes"), "つま先の骨の付け根には置かない");
            // 接地の点は足首と足裏の点だけ
            string[] contacts = definition_.bodyPoints.Where (p => p.contact).Select (p => p.name).OrderBy (n => n).ToArray ();
            CollectionAssert.AreEquivalent (new[] { "LeftFoot", "LeftHeel", "LeftLittleToeSide", "LeftSoleBall", "LeftToeTip",
                "RightFoot", "RightHeel", "RightLittleToeSide", "RightSoleBall", "RightToeTip" }, contacts);
            Assert.IsEmpty (definition_.Validate ());
        }

        // ---- 検証・写し・ハッシュ ----

        [Test]
        public void Validate_ReportsBadFullBodyValues ()
        {
            Assert.AreEqual (0, definition_.Validate ().Count);
            definition_.fullBody.iterations = 0;
            definition_.fkControls[0].stiffness = 1.5f;
            List<string> errors = definition_.Validate ();
            Assert.AreEqual (2, errors.Count, string.Join (" / ", errors));
        }

        [Test]
        public void CopyFrom_KeepsSolverAndFullBodyValues ()
        {
            definition_.solver = RigSolver.FullBodyIk;
            definition_.fullBody.iterations = 33;
            definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine).stiffness = 0.33f;

            EditRigDefinition copy = ScriptableObject.CreateInstance<EditRigDefinition> ();
            created_.Add (copy);
            copy.CopyFrom (definition_);

            Assert.AreEqual (RigSolver.FullBodyIk, copy.solver);
            Assert.AreEqual (33, copy.fullBody.iterations);
            Assert.AreEqual (0.33f, copy.fkControls.First (c => c.bone == HumanBodyBones.Spine).stiffness, 1e-6f);
        }

        [Test]
        public void FullBodyHash_ChangesWithSettings_AndIsEmptyForTwoBoneIk ()
        {
            string hash = definition_.GetFullBodyHash ();
            Assert.IsNotEmpty (hash);
            Assert.AreEqual (hash, definition_.GetFullBodyHash (), "同じ設定なら同じ");
            definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine).stiffness = 0.31f;
            string stiffer = definition_.GetFullBodyHash ();
            Assert.AreNotEqual (hash, stiffer, "硬さを変えると変わる");
            definition_.fullBody.hipsPin = 0.11f;
            Assert.AreNotEqual (stiffer, definition_.GetFullBodyHash (), "腰を留める強さを変えると変わる");
            definition_.solver = RigSolver.TwoBoneIk;
            Assert.IsEmpty (definition_.GetFullBodyHash (), "2 本骨 IK の定義では、全身 IK の設定は姿勢に効かない");
        }

        // ---- 変更の知らせ ----

        [Test]
        public void Undo_NotifiesChangedDefinition ()
        {
            // 窓は知らせを受けて作り直す（S23・S45b）。Undo で戻したときも OnValidate から知らせが来る
            int notified = 0;
            System.Action<EditRigDefinition> onChanged = d => { if (d == definition_) notified++; };
            EditRigDefinition.changed += onChanged;
            try {
                int before = definition_.fullBody.iterations;
                UnityEditor.Undo.IncrementCurrentGroup ();
                UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject (definition_);
                serialized.FindProperty ("fullBody.iterations").intValue = before + 5;
                serialized.ApplyModifiedProperties ();
                UnityEditor.Undo.IncrementCurrentGroup ();
                int afterEdit = notified;

                UnityEditor.Undo.PerformUndo ();

                Assert.AreEqual (before, definition_.fullBody.iterations, "Undo で戻る");
                Assert.Greater (notified, afterEdit, "Undo の後に知らせる");
            }
            finally {
                EditRigDefinition.changed -= onChanged;
            }
        }
    }

}
