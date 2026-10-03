using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 足の転がし（Reverse Foot。S22）。支点が動かないこと・角度 0 で今までと同じこと・FK/IK の切り替え・左右反転・Override の重ね方
    /// </summary>
    public class ReverseFootTests
    {
        const float kPositionTolerance = 1e-3f;
        const float kAngleTolerance = 0.01f;
        const string kLeft = "LeftLeg";
        const string kRight = "RightLeg";

        readonly List<Object> created_ = new List<Object> ();
        readonly List<EditingRig> rigs_ = new List<EditingRig> ();

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
        /// キャラの prefab でよくあるように、Animator の GameObject を Y270 に回して置く。脚は IK にして、膝を曲げておく（転がしで足首が動いても届くように）
        /// </summary>
        (EditingRig, EditingRigSolver) CreateRig (bool toes = true, System.Action<EditRigDefinition> configure = null, bool bend = true, float size = 1)
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            // 2 本骨 IK の組を見るテストなので、既定（全身 IK）から切り替える
            definition.solver = RigSolver.TwoBoneIk;
            created_.Add (definition);
            if (configure != null) configure (definition);
            GameObject prefabRoot = new GameObject ("Prefab");
            created_.Add (prefabRoot);
            Animator display = toes ? TestSkeleton.CreateHumanoidWithToes (created_, size) : TestSkeleton.CreateHumanoid (created_, true, size);
            display.transform.SetParent (prefabRoot.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, 270, 0);

            EditingRig rig = EditingRig.Create (definition, display, name => new GameObject (name));
            rigs_.Add (rig);
            EditingRigSolver solver = new EditingRigSolver (rig);
            foreach (string chain in new[] { kLeft, kRight }) {
                solver.SwitchToIk (chain);
                if (!bend) continue;
                Vector3 position;
                Quaternion rotation;
                solver.TryGetIkTargetWorld (chain, out position, out rotation);
                solver.SetIkTargetWorld (chain, position + rig.root.transform.TransformDirection (rig.BodyToRoot (new Vector3 (0, 0.15f, 0.05f))), rotation);
            }
            Solve (rig, solver);
            return (rig, solver);
        }

        static void Solve (EditingRig rig, EditingRigSolver solver)
        {
            rig.ResetBonesToRest ();
            solver.Solve ();
        }

        static Transform Foot (EditingRig rig, string chain)
        {
            return rig.GetEditingBone (chain == kLeft ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
        }

        static Transform Toes (EditingRig rig, string chain)
        {
            return rig.GetEditingBone (chain == kLeft ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
        }

        static Vector3 Pivot (EditingRigSolver solver, string chain, ReverseFoot.Pivot pivot)
        {
            Vector3[] pivots = new Vector3[ReverseFoot.kPivotCount];
            Assert.IsTrue (solver.TryGetFootPivotsWorld (chain, pivots));
            return pivots[(int)pivot];
        }

        static Dictionary<Transform, (Vector3, Quaternion)> Snapshot (EditingRig rig)
        {
            return rig.editingBones.ToDictionary (b => b, b => (b.position, b.rotation));
        }

        static void AssertSamePose (Dictionary<Transform, (Vector3, Quaternion)> expected, EditingRig rig)
        {
            foreach (KeyValuePair<Transform, (Vector3, Quaternion)> pair in expected) {
                Assert.That (Vector3.Distance (pair.Value.Item1, pair.Key.position), Is.LessThan (kPositionTolerance), pair.Key.name + " の位置");
                Assert.That (Quaternion.Angle (pair.Value.Item2, pair.Key.rotation), Is.LessThan (kAngleTolerance), pair.Key.name + " の回転");
            }
        }

        [Test]
        public void LegsHaveReverseFootAndArmsDoNot ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Assert.IsTrue (solver.HasReverseFoot (kLeft));
            Assert.IsTrue (solver.HasReverseFoot (kRight));
            Assert.IsFalse (solver.HasReverseFoot ("LeftArm"));
            Assert.IsFalse (solver.HasReverseFoot ("RightArm"));
        }

        [Test]
        public void ZeroAnglesKeepPose ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            var expected = Snapshot (rig);
            solver.SetFootAngles (kLeft, new Vector3 (20, 10, 15));
            Solve (rig, solver);
            solver.SetFootAngles (kLeft, Vector3.zero);
            Solve (rig, solver);
            AssertSamePose (expected, rig);
        }

        [Test]
        public void BallRollKeepsToesPlanted ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Transform foot = Foot (rig, kLeft);
            Transform toes = Toes (rig, kLeft);
            Vector3 toesPosition = toes.position;
            Quaternion toesRotation = toes.rotation;
            Quaternion footRotation = foot.rotation;

            solver.SetFootAngles (kLeft, new Vector3 (20, 0, 0));
            Solve (rig, solver);

            Assert.That (Vector3.Distance (toesPosition, toes.position), Is.LessThan (kPositionTolerance), "つま先の付け根");
            Assert.That (Quaternion.Angle (toesRotation, toes.rotation), Is.LessThan (kAngleTolerance), "つま先の向き");
            Assert.That (Quaternion.Angle (footRotation, foot.rotation), Is.EqualTo (20).Within (0.01f), "足の回り");
            // 踵が上がる
            Vector3 up = rig.root.transform.TransformDirection (rig.BodyToRoot (Vector3.up));
            Vector3 heel = Pivot (solver, kLeft, ReverseFoot.Pivot.Heel);
            Assert.That (Vector3.Dot (foot.position - heel, up), Is.GreaterThan (0));
        }

        [Test]
        public void RollBeyondBreakPivotsAroundToeTip ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Transform toes = Toes (rig, kLeft);
            Vector3 tip = Pivot (solver, kLeft, ReverseFoot.Pivot.ToeTip);
            Vector3 local = toes.InverseTransformPoint (tip);

            solver.SetFootAngles (kLeft, new Vector3 (50, 0, 0));
            Solve (rig, solver);
            Assert.That (Vector3.Distance (tip, toes.TransformPoint (local)), Is.LessThan (kPositionTolerance), "つま先の先");
        }

        [Test]
        public void NegativeRollPivotsAroundHeel ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Transform foot = Foot (rig, kLeft);
            Vector3 heel = Pivot (solver, kLeft, ReverseFoot.Pivot.Heel);
            Vector3 local = foot.InverseTransformPoint (heel);

            solver.SetFootAngles (kLeft, new Vector3 (-20, 0, 0));
            Solve (rig, solver);
            Assert.That (Vector3.Distance (heel, foot.TransformPoint (local)), Is.LessThan (kPositionTolerance), "踵");
        }

        [TestCase (15f, ReverseFoot.Pivot.LeftEdge)]
        [TestCase (-15f, ReverseFoot.Pivot.RightEdge)]
        public void BankPivotsAroundLowerEdge (float bank, ReverseFoot.Pivot edge)
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Transform foot = Foot (rig, kLeft);
            Vector3 pivot = Pivot (solver, kLeft, edge);
            Vector3 local = foot.InverseTransformPoint (pivot);

            solver.SetFootAngles (kLeft, new Vector3 (0, bank, 0));
            Solve (rig, solver);
            Assert.That (Vector3.Distance (pivot, foot.TransformPoint (local)), Is.LessThan (kPositionTolerance), "縁");
        }

        [Test]
        public void TwistPivotsAroundBall ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            Transform toes = Toes (rig, kLeft);
            Vector3 ball = toes.position;

            solver.SetFootAngles (kLeft, new Vector3 (0, 0, 25));
            Solve (rig, solver);
            Assert.That (Vector3.Distance (ball, toes.position), Is.LessThan (kPositionTolerance), "母趾球");
        }

        [Test]
        public void SwitchToFkKeepsLookAndClearsAngles ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetFootAngles (kLeft, new Vector3 (40, 10, 15));
            Solve (rig, solver);
            var expected = Snapshot (rig);

            solver.SwitchToFk (kLeft);
            Vector3 angles;
            solver.TryGetFootAngles (kLeft, out angles);
            Assert.AreEqual (Vector3.zero, angles);
            Solve (rig, solver);
            AssertSamePose (expected, rig);

            // IK に戻しても飛ばない
            solver.SwitchToIk (kLeft);
            Solve (rig, solver);
            AssertSamePose (expected, rig);
        }

        [Test]
        public void SyncClearsAnglesWhileFk ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetIkWeight (kLeft, 0);
            solver.SetFootAngles (kLeft, new Vector3 (20, 5, 5));
            Solve (rig, solver);
            var expected = Snapshot (rig);
            solver.SyncIkFk ();

            Vector3 angles;
            solver.TryGetFootAngles (kLeft, out angles);
            Assert.AreEqual (Vector3.zero, angles);
            solver.SetIkWeight (kLeft, 1);
            Solve (rig, solver);
            AssertSamePose (expected, rig);
        }

        [Test]
        public void SyncWhileIkDoesNotDoubleToes ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetFootAngles (kLeft, new Vector3 (20, 0, 0));
            Solve (rig, solver);
            var expected = Snapshot (rig);
            for (int i = 0; i < 3; i++) {
                solver.SyncIkFk ();
                Solve (rig, solver);
            }
            AssertSamePose (expected, rig);
        }

        [Test]
        public void CaptureKeepsLookWithZeroAngles ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetFootAngles (kLeft, new Vector3 (40, -10, 20));
            Solve (rig, solver);
            var expected = Snapshot (rig);

            solver.Capture ();
            Vector3 angles;
            solver.TryGetFootAngles (kLeft, out angles);
            Assert.AreEqual (Vector3.zero, angles);
            Solve (rig, solver);
            AssertSamePose (expected, rig);
        }

        [Test]
        public void MirrorKeepsRollAndFlipsBankAndTwist ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetFootAngles (kLeft, new Vector3 (20, 10, 15));
            Solve (rig, solver);
            Assert.IsTrue (solver.MirrorIk (kLeft, kRight));

            Vector3 angles;
            solver.TryGetFootAngles (kRight, out angles);
            Assert.AreEqual (new Vector3 (20, -10, -15), angles);

            // つま先が左右対称の位置に来る（体の中心の面で鏡に映す）
            Solve (rig, solver);
            Transform root = rig.root.transform;
            Vector3 left = root.InverseTransformPoint (Toes (rig, kLeft).position);
            Vector3 right = root.InverseTransformPoint (Toes (rig, kRight).position);
            Vector3 normal = rig.bodyRight;
            Vector3 hips = rig.GetRestRootPosition (rig.GetEditingBone (HumanBodyBones.Hips));
            float center = Vector3.Dot (hips, normal);
            Vector3 mirrored = left - 2 * (Vector3.Dot (left, normal) - center) * normal;
            Assert.That (Vector3.Distance (mirrored, right), Is.LessThan (kPositionTolerance));
        }

        [Test]
        public void WithoutToesStillRollsAroundBall ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig (toes: false);
            Assert.IsTrue (solver.HasReverseFoot (kLeft));
            Transform foot = Foot (rig, kLeft);
            Vector3 ball = Pivot (solver, kLeft, ReverseFoot.Pivot.Ball);
            Vector3 local = foot.InverseTransformPoint (ball);

            solver.SetFootAngles (kLeft, new Vector3 (20, 0, 0));
            Solve (rig, solver);
            Assert.That (Vector3.Distance (ball, foot.TransformPoint (local)), Is.LessThan (kPositionTolerance), "母趾球");
        }

        [Test]
        public void OverrideAddsAnglesButReplacesWeight ()
        {
            Assert.AreEqual (12.5f, OverrideMath.ComposeFloat (OverrideMode.Additive, IkControl.IsAdditive (IkControl.kRollProperty), 10, 5, 0.5f), 1e-6f);
            Assert.AreEqual (0.75f, OverrideMath.ComposeFloat (OverrideMode.Additive, IkControl.IsAdditive (IkControl.kIkWeightProperty), 0.5f, 1, 0.5f), 1e-6f);
            Assert.AreEqual (7.5f, OverrideMath.ComposeFloat (OverrideMode.Override, IkControl.IsAdditive (IkControl.kBankProperty), 10, 5, 0.5f), 1e-6f);
            Assert.AreEqual (5, OverrideMath.StoreFloat (OverrideMode.Additive, true, 10, 15), 1e-6f);
            Assert.AreEqual (15, OverrideMath.StoreFloat (OverrideMode.Override, true, 10, 15), 1e-6f);
        }

        [Test]
        public void KeysIncludeFootAnglesOnlyForLegs ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            solver.SetFootAngles (kLeft, new Vector3 (20, 0, 0));
            using (CurveWriter writer = CurveWriter.Begin (null, rig.root.transform, 0, null)) {
                foreach (PoseTarget target in RigTargets.Create (rig, solver)) target.WriteKeys (writer);
                var rolls = writer.values.Where (p => p.Key.propertyName == IkControl.kRollProperty).ToList ();
                CollectionAssert.AreEquivalent (new[] { RigPaths.IkChain (kLeft), RigPaths.IkChain (kRight) }, rolls.Select (p => p.Key.path));
                Assert.AreEqual (20, rolls.Single (p => p.Key.path == RigPaths.IkChain (kLeft)).Value);
            }
        }

        /// <summary>
        /// Transform パネルの roll / bank / twist の行（S22b）が使う対象の口。脚だけが持ち、FK の脚に入れると IK になって効く
        /// </summary>
        [Test]
        public void IkTargetSetsFootAnglesAndSwitchesToIk ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig ();
            List<IkTarget> targets = RigTargets.Create (rig, solver).OfType<IkTarget> ().ToList ();
            IkTarget left = targets.Single (t => t.label == "IK " + kLeft);
            Assert.IsFalse (targets.Single (t => t.label == "IK LeftArm").hasFootAngles);
            Assert.IsTrue (left.hasFootAngles);

            solver.SwitchToFk (kLeft);
            Solve (rig, solver);
            Vector3 angles;
            Assert.IsTrue (left.TryGetFootAngles (out angles));
            Assert.AreEqual (Vector3.zero, angles);

            Vector3 ball = Toes (rig, kLeft).position;
            Vector3 heel = Foot (rig, kLeft).position;
            left.SetFootAngles (new Vector3 (20, 0, 0));
            Solve (rig, solver);
            Assert.AreEqual (1, solver.GetIkWeight (kLeft));
            Assert.IsTrue (left.TryGetFootAngles (out angles));
            Assert.AreEqual (new Vector3 (20, 0, 0), angles);
            // 母趾球まわりに踵が上がる（足首が上がり、母趾球は残る）
            Assert.That (Vector3.Dot (Foot (rig, kLeft).position - heel, rig.root.transform.TransformDirection (rig.BodyToRoot (Vector3.up))), Is.GreaterThan (0.001f));
            Assert.That (Vector3.Distance (ball, Toes (rig, kLeft).position), Is.LessThan (kPositionTolerance));
        }

        [Test]
        public void DefinitionPivotOverrideIsBodyAxesFromAnkle ()
        {
            Vector3 heel = new Vector3 (0.01f, -0.05f, -0.07f);
            (EditingRig rig, EditingRigSolver solver) = CreateRig (configure: d => {
                d.ikChains.Single (c => c.name == kLeft).pivots.Add (new EditRigDefinition.FootPivot { pivot = ReverseFoot.Pivot.Heel, position = heel });
            }, bend: false);
            // 目標は基準姿勢のまま（足首の向き = 基準の向き）なので、支点は足首から体の向きでずらした所
            Transform root = rig.root.transform;
            Vector3 expected = Foot (rig, kLeft).position + root.TransformDirection (rig.BodyToRoot (heel));
            Assert.That (Vector3.Distance (expected, Pivot (solver, kLeft, ReverseFoot.Pivot.Heel)), Is.LessThan (1e-4f));
        }

        [Test]
        public void DefinitionToeBreakIsUsed ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig (configure: d => {
                foreach (EditRigDefinition.IkChain chain in d.ikChains) chain.toeBreak = 10;
            });
            Transform toes = Toes (rig, kLeft);
            Quaternion before = toes.rotation;
            solver.SetFootAngles (kLeft, new Vector3 (25, 0, 0));
            Solve (rig, solver);
            // 折れ角 10° を越えた 15° は、つま先ごと回る
            Assert.That (Quaternion.Angle (before, toes.rotation), Is.EqualTo (15).Within (0.01f));
        }

        [Test]
        public void ReverseFootCanBeTurnedOffInDefinition ()
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig (configure: d => {
                d.ikChains.Single (c => c.name == kLeft).reverseFoot = false;
            });
            Assert.IsFalse (solver.HasReverseFoot (kLeft));
            Assert.IsTrue (solver.HasReverseFoot (kRight));
        }

        [Test]
        public void CopyAndDefaultMakeUsableDefinitions ()
        {
            EditRigDefinition source = EditRigDefinition.CreateDefault ();
            created_.Add (source);
            source.ikChains[2].toeBreak = 12;
            source.ikChains[2].pivots.Add (new EditRigDefinition.FootPivot { pivot = ReverseFoot.Pivot.ToeTip, position = Vector3.forward * 0.2f });

            EditRigDefinition copy = ScriptableObject.CreateInstance<EditRigDefinition> ();
            created_.Add (copy);
            copy.CopyFrom (source);
            CollectionAssert.IsEmpty (copy.Validate ());
            Assert.AreEqual (source.fkControls.Count, copy.fkControls.Count);
            Assert.AreEqual (12, copy.ikChains[2].toeBreak);
            Assert.AreEqual (Vector3.forward * 0.2f, copy.ikChains[2].pivots.Single ().position);
            // 写した物は別の物（元を直しても変わらない）
            source.ikChains[2].toeBreak = 40;
            Assert.AreEqual (12, copy.ikChains[2].toeBreak);

            copy.SetDefault ();
            CollectionAssert.IsEmpty (copy.Validate ());
            Assert.AreEqual (ReverseFoot.kDefaultToeBreak, copy.ikChains[2].toeBreak);
            Assert.IsEmpty (copy.ikChains[2].pivots);
            Assert.AreEqual (4, copy.ikChains.Count);
        }

        [Test]
        public void ValidateReportsDuplicatePivotsAndNegativeBreak ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            EditRigDefinition.IkChain leg = definition.ikChains.Single (c => c.name == kLeft);
            leg.toeBreak = -1;
            leg.pivots.Add (new EditRigDefinition.FootPivot { pivot = ReverseFoot.Pivot.Heel });
            leg.pivots.Add (new EditRigDefinition.FootPivot { pivot = ReverseFoot.Pivot.Heel });
            Assert.AreEqual (2, definition.Validate ().Count);
        }

        /// <summary>
        /// 大きいキャラ（humanScale が 1 から遠い）でも、踵と爪先の先は床（足裏の高さ）に来る。足首の真下からずれない
        /// </summary>
        [TestCase (true)]
        [TestCase (false)]
        public void SolePivotsAreOnFloorForLargeCharacter (bool toes)
        {
            (EditingRig rig, EditingRigSolver solver) = CreateRig (toes: toes, bend: false, size: 2.4f);
            Assert.That (rig.humanScale, Is.GreaterThan (1.5f), "大きいキャラになっていない");
            Transform root = rig.root.transform;
            Vector3 ankle = root.InverseTransformPoint (Foot (rig, kLeft).position);
            // 足裏の高さは Avatar の値（実寸）。無ければ足首の高さ（床はルートの高さ）
            float bottom = rig.displayAnimator.leftFeetBottomHeight;
            float floor = bottom > 1e-4f ? ankle.y - bottom : 0;
            foreach (ReverseFoot.Pivot pivot in new[] { ReverseFoot.Pivot.Heel, ReverseFoot.Pivot.ToeTip, ReverseFoot.Pivot.LeftEdge, ReverseFoot.Pivot.RightEdge }) {
                Vector3 p = root.InverseTransformPoint (Pivot (solver, kLeft, pivot));
                Assert.That (p.y, Is.EqualTo (floor).Within (1e-3f), pivot + " の高さ");
                Assert.That (new Vector2 (p.x - ankle.x, p.z - ankle.z).magnitude, Is.LessThan (1.2f), pivot + " が足首から離れすぎ");
            }
        }
    }

}
