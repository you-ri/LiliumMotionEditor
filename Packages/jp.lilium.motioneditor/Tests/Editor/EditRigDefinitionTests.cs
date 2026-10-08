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
            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.Jaw).fullBodyPoint);
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
                "LeftKneePole", "RightKneePole", "LeftHeel", "RightSoleBall", "LeftLittleToeSide", "RightToeTip", "LeftPalmIndex", "RightPalmLittle" }) {
                Assert.Contains (name, names, name);
            }
            Assert.IsTrue (definition_.bodyPoints.First (p => p.name == "LeftHand").rotation, "手は向きを持つ");
            Assert.IsFalse (definition_.bodyPoints.First (p => p.name == "LeftLowerArm").rotation, "肘は位置だけ");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name == "Jaw"), "解かない骨には置かない");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name == "LeftToes" || p.name == "RightToes"), "つま先の骨の付け根には置かない");
            Assert.IsEmpty (definition_.Validate ());
        }

        // ---- 古い版の定義の版上げ ----

        [Test]
        public void Version1Definition_GetsNewDefaults_ButKeepsEditedValues ()
        {
            EditRigDefinition.FkControl shoulder = definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftShoulder);
            EditRigDefinition.FkControl spine = definition_.fkControls.First (c => c.bone == HumanBodyBones.Spine);
            definition_.version = 1;
            definition_.fullBody.iterations = 20;
            definition_.fullBody.hipsPin = 0.3f;
            shoulder.stiffness = 0.3f;
            spine.stiffness = 0.33f;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.AreEqual (new EditRigDefinition.FullBodySettings ().iterations, definition_.fullBody.iterations);
            Assert.AreEqual (new EditRigDefinition.FullBodySettings ().hipsPin, definition_.fullBody.hipsPin, 1e-6f);
            Assert.AreEqual (0.5f, shoulder.stiffness, 1e-6f);
            Assert.AreEqual (0.33f, spine.stiffness, 1e-6f, "直してあった値は変えない");

            definition_.version = 1;
            definition_.fullBody.iterations = 35;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (35, definition_.fullBody.iterations, "直してあった回数は変えない");
        }

        [Test]
        public void Version2Definition_GetsBodyPointsFromOldSettings ()
        {
            definition_.bodyPoints.Clear ();
            definition_.fullBody.directionPoints = false;
            definition_.version = 2;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.IsTrue (definition_.bodyPoints.Any (p => p.name == "LeftHeel"), "骨の上の点は作る");
            Assert.IsFalse (definition_.bodyPoints.Any (p => p.name.EndsWith ("Direction")), "向きの点を置かない設定だった");
            Assert.IsEmpty (definition_.Validate ());
        }

        [Test]
        public void Version3Definition_ShowsUnsolvedBonesWhilePointGrab ()
        {
            // 版 3 までの定義には fullBodyHandle が無い（読むと false）。今の定義で解かない骨を出すようにする
            foreach (EditRigDefinition.FkControl control in definition_.fkControls) control.fullBodyHandle = false;
            definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftThumbProximal).fullBodySolve = true;
            definition_.version = 3;

            definition_.UpgradeIfNeeded ();

            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.IsTrue (definition_.fkControls.First (c => c.bone == HumanBodyBones.RightLittleDistal).fullBodyHandle);
            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftThumbProximal).fullBodyHandle, "解くようにした骨は点が付くので出さない");
            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.Hips).fullBodyHandle);
        }

        [Test]
        public void Version4Definition_ResetsBodyPointsToDefault ()
        {
            // 版 5 で足の点を作り直した。後方互換は保たないので、版 4 までの点の一覧は今の既定に置き換わる（足した点も消える）
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "Nose", bone = HumanBodyBones.Head, offset = Vector3.forward * 0.1f });
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "LeftToes", bone = HumanBodyBones.LeftToes });
            definition_.bodyPoints.RemoveAll (p => p.name.EndsWith ("SoleBall") || p.name.EndsWith ("LittleToeSide") || p.name.EndsWith ("KneePole"));
            definition_.version = 4;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            string[] names = definition_.bodyPoints.Select (p => p.name).ToArray ();
            Assert.Contains ("LeftSoleBall", names);
            Assert.Contains ("RightLittleToeSide", names);
            Assert.Contains ("RightKneePole", names);
            Assert.IsFalse (names.Contains ("Nose"), "足した点は消える");
            Assert.IsFalse (names.Contains ("LeftToes"), "つま先の骨の付け根の点は消える");
            Assert.AreEqual (0, definition_.bodyPoints.First (p => p.name == "HeadDirection").pullStrength, "向きの点は今の既定（引く強さ 0）");
            Assert.IsEmpty (definition_.Validate ());
        }

        [Test]
        public void OldVersionDefinition_GetsDefaultsFilled ()
        {
            // S23 で作った定義アセットには全身 IK の項目が無い（読むと 0 や既定の初期値のまま）
            foreach (EditRigDefinition.FkControl control in definition_.fkControls) {
                control.fullBodySolve = true;
                control.fullBodyPoint = true;
                control.stiffness = 0;
            }
            // solver の項目も無いので、初期値の 2 本骨 IK で読まれる
            definition_.solver = RigSolver.TwoBoneIk;
            definition_.version = 0;

            definition_.UpgradeIfNeeded ();

            Assert.AreEqual (EditRigDefinition.kVersion, definition_.version);
            Assert.AreEqual (0.95f, definition_.fkControls.First (c => c.bone == HumanBodyBones.Hips).stiffness, 1e-6f);
            Assert.IsFalse (definition_.fkControls.First (c => c.bone == HumanBodyBones.LeftThumbProximal).fullBodySolve);
            Assert.AreEqual (RigSolver.TwoBoneIk, definition_.solver, "古い定義は今の IK のまま");

            // 今の版で直した値は、読み直しても上書きしない
            definition_.fkControls.First (c => c.bone == HumanBodyBones.Hips).stiffness = 0.4f;
            definition_.UpgradeIfNeeded ();
            Assert.AreEqual (0.4f, definition_.fkControls.First (c => c.bone == HumanBodyBones.Hips).stiffness, 1e-6f);
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
            Assert.AreEqual (EditRigDefinition.kVersion, copy.version);
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
    }

}
