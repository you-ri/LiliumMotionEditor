using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 段の性質を自由に決められる段。組み込みの段では「前進がまだ無い」ものを、前進がある状態で試すために使う
    /// </summary>
    sealed class FakeLayer : PoseLayer
    {
        public LayerKind fakeKind;
        public int fakeOrder;
        public string fakeLabel = "Fake";
        public string fakeId;
        public bool fakeCanEvaluate = true;
        public bool fakeCanWrite;
        public GrabTarget fakeGrab;
        public PosePhase fakePhase;
        public bool fakeCanManipulate = true;
        public bool fakeIsData;
        public bool fakeHasWeight;
        public InverseKind fakeInverse = InverseKind.Exact;
        public bool fakeBypass;
        public System.Action<float> onEvaluate;
        public System.Action<float> onEvaluateKeepingValues;

        public override LayerKind kind { get { return fakeKind; } }
        public override string idSuffix { get { return fakeId; } }
        public override int order { get { return fakeOrder; } }
        public override string label { get { return fakeLabel; } }
        public override bool canEvaluate { get { return fakeCanEvaluate; } }
        public override bool canWrite { get { return fakeCanWrite; } }
        public override GrabTarget grab { get { return fakeGrab; } }
        public override bool canManipulate { get { return fakeGrab != GrabTarget.None && fakeCanManipulate; } }
        public override PosePhase phase { get { return fakePhase; } }
        public override bool isData { get { return fakeIsData; } }
        public override bool isPoseStage { get { return true; } }
        public override bool hasWeight { get { return fakeHasWeight; } }
        public override InverseKind inverse { get { return fakeInverse; } }
        public override string inverseReason { get { return fakeLabel + " の理由"; } }
        public override bool bypassOnDisplayGrab { get { return fakeBypass; } }

        public override void Evaluate (float time)
        {
            if (onEvaluate != null) onEvaluate (time);
        }

        public override void EvaluateKeepingValues (float time)
        {
            if (onEvaluateKeepingValues != null) onEvaluateKeepingValues (time);
        }

        /// <summary>前進がある Editing Rig（値を持ち、姿勢も出す）</summary>
        public static FakeLayer EditingRig ()
        {
            return new FakeLayer {
                fakeKind = LayerKind.EditingRig, fakePhase = PosePhase.Body, fakeOrder = PoseStack.kOrderEditingRig, fakeLabel = "Editing Rig",
                fakeCanWrite = true, fakeIsData = true, fakeGrab = GrabTarget.Values,
            };
        }

        /// <summary>前進があるだけのゲーム側の段</summary>
        public static FakeLayer User (string id, int order)
        {
            return new FakeLayer { fakeKind = LayerKind.User, fakePhase = PosePhase.Graph, fakeOrder = order, fakeLabel = id, fakeId = id };
        }

        public static FakeLayer Humanoid ()
        {
            return new FakeLayer {
                fakeKind = LayerKind.HumanoidAnimation, fakePhase = PosePhase.Body, fakeOrder = PoseStack.kOrderHumanoid, fakeLabel = "Humanoid",
                fakeInverse = InverseKind.Approximate,
            };
        }

        /// <summary>前進があり、ターゲットをつかめる Rig の層</summary>
        public static FakeLayer Rig (InverseKind inverse)
        {
            return new FakeLayer {
                fakeKind = LayerKind.RigBuilder, fakePhase = PosePhase.Graph, fakeOrder = PoseStack.kOrderRigBuilder, fakeLabel = "Rig", fakeId = "Rig@0",
                fakeGrab = GrabTarget.Values, fakeHasWeight = true, fakeInverse = inverse,
            };
        }
    }

    public class PoseStackTests
    {
        static PoseStack Stack (params PoseLayer[] extra)
        {
            List<PoseLayer> layers = new List<PoseLayer> {
                new EditingRigLayer (),
                new DisplayBoneLayer (),
            };
            layers.AddRange (extra);
            return PoseStack.FromLayers (layers);
        }

        [Test]
        public void DefaultGrabsAndWritesEditingRig ()
        {
            PoseStack stack = Stack ();
            PoseLayer rig = stack.Find (LayerKind.EditingRig);

            Assert.AreSame (rig, stack.layers[0], "Editing Rig が最上段");
            Assert.AreSame (rig, stack.writeLayer);
            Assert.AreSame (rig, stack.manipulateLayer);
        }

        /// <summary>
        /// Generic Pose は既定の並びに無いが、足せば骨をつかんで Editing Rig へ書ける
        /// </summary>
        [Test]
        public void AddedGenericPoseGrabsBonesThroughEditingRig ()
        {
            GenericPoseLayer generic = new GenericPoseLayer ();
            PoseStack stack = Stack (generic, FakeLayer.Humanoid ());
            PoseLayer rig = stack.Find (LayerKind.EditingRig);
            Assert.AreEqual (1, stack.IndexOf (generic), "Editing Rig の次");
            Assert.AreSame (rig, stack.manipulateLayer, "足しても既定の ✋ は変わらない");

            string reason;
            Assert.IsTrue (stack.TrySetManipulate (generic, out reason), reason);
            InverseKind kind;
            List<PoseLayer> path = stack.PathLayers (stack.writeLayer, stack.manipulateLayer, out kind, out reason);
            Assert.AreEqual (InverseKind.Exact, kind);
            CollectionAssert.AreEqual (new PoseLayer[] { generic, rig }, path);
            Assert.IsFalse (stack.CanWrite (generic, out reason));
        }

        /// <summary>
        /// 操作値をつかんで同じ段のクリップへ書く。逆に通る段は無い（上向きの経路にならない）
        /// </summary>
        [Test]
        public void GrabbingEditingRigWritesItselfWithoutPath ()
        {
            PoseStack stack = Stack (FakeLayer.Humanoid ());
            PoseLayer rig = stack.Find (LayerKind.EditingRig);

            InverseKind kind;
            string reason;
            List<PoseLayer> path = stack.PathLayers (rig, rig, out kind, out reason);
            Assert.AreEqual (InverseKind.Exact, kind);
            Assert.IsEmpty (path);

            Assert.IsTrue (stack.TrySetManipulate (rig, out reason));
            Assert.IsFalse (stack.layers.Any (stack.IsOnPath));
        }

        [Test]
        public void OnlyEditingRigCanBeWritten ()
        {
            PoseStack stack = Stack (FakeLayer.Humanoid ());
            string reason;
            Assert.IsTrue (stack.CanWrite (stack.Find (LayerKind.EditingRig), out reason));
            Assert.IsFalse (stack.CanWrite (stack.Find (LayerKind.HumanoidAnimation), out reason));
            Assert.IsFalse (stack.CanWrite (stack.Find (LayerKind.DisplayBone), out reason));
        }

        /// <summary>
        /// 👁 を落とした段はクリップを当てないので、画面の姿勢から書き戻さない
        /// </summary>
        [Test]
        public void HiddenLayerCannotBeWritten ()
        {
            PoseStack stack = Stack ();
            PoseLayer rig = stack.Find (LayerKind.EditingRig);
            rig.enabled = false;

            string reason;
            Assert.IsFalse (stack.CanWrite (rig, out reason));
            StringAssert.Contains ("👁", reason);
        }

        /// <summary>
        /// 値を直に動かした後の解き直しは、開始段の値を当て直さない（動かした値が消える）。後ろの段は普段どおり
        /// </summary>
        [Test]
        public void EvaluateFromKeepsStartValues ()
        {
            List<string> called = new List<string> ();
            FakeLayer rig = FakeLayer.EditingRig ();
            rig.onEvaluate = t => called.Add ("Rig");
            rig.onEvaluateKeepingValues = t => called.Add ("RigKeep");
            FakeLayer after = FakeLayer.User ("After", 500);
            after.onEvaluate = t => called.Add ("After");
            after.onEvaluateKeepingValues = t => called.Add ("AfterKeep");
            PoseStack stack = PoseStack.FromLayers (new PoseLayer[] { rig, after });

            stack.EvaluateFrom (rig, 0);
            CollectionAssert.AreEqual (new[] { "RigKeep", "After" }, called);

            called.Clear ();
            stack.Evaluate (0);
            CollectionAssert.AreEqual (new[] { "Rig", "After" }, called);
        }

        [Test]
        public void DisplayBoneIsBlockedByRigWithoutInverse ()
        {
            PoseStack stack = Stack (FakeLayer.Humanoid (), FakeLayer.Rig (InverseKind.None));
            string reason;
            Assert.IsFalse (stack.CanManipulate (stack.Find (LayerKind.DisplayBone), out reason));
            StringAssert.Contains ("Rig", reason);
        }

        /// <summary>
        /// 逆の無い Rig でも、表示の骨をつかむときは効いている拘束を切って通れる（近似）。値をつかむ経路には効かない
        /// </summary>
        [Test]
        public void DisplayBoneBypassesRigWithoutInverse ()
        {
            FakeLayer rig = FakeLayer.Rig (InverseKind.None);
            rig.fakeBypass = true;
            PoseStack stack = Stack (rig);
            PoseLayer display = stack.Find (LayerKind.DisplayBone);
            string reason;
            Assert.IsTrue (stack.CanManipulate (display, out reason));
            Assert.AreEqual (InverseKind.Approximate, stack.PathKind (stack.writeLayer, display, out reason));
            StringAssert.Contains ("一時的に切る", reason);
            Assert.AreEqual (InverseKind.None, stack.StepKind (rig, stack.Find (LayerKind.EditingRig), out reason), "表示をつかまないときは逆が無いまま");
        }

        [Test]
        public void ConstraintMovesTheBoneAndItsChildren ()
        {
            GameObject root = new GameObject ("Hips");
            try {
                Transform spine = new GameObject ("Spine").transform;
                spine.SetParent (root.transform);
                Transform hand = new GameObject ("Hand").transform;
                hand.SetParent (spine);
                RigConstraintInfo constraint = new RigConstraintInfo ();
                constraint.constrained.Add (spine);
                Assert.IsTrue (constraint.Moves (spine));
                Assert.IsTrue (constraint.Moves (hand), "子は親と一緒に動く");
                Assert.IsFalse (constraint.Moves (root.transform), "親は動かさない");
                Assert.IsFalse (constraint.Moves (null));
            }
            finally {
                Object.DestroyImmediate (root);
            }
        }

        [Test]
        public void DisplayBonePathTakesWorstStep ()
        {
            PoseStack stack = Stack (FakeLayer.Humanoid (), FakeLayer.Rig (InverseKind.Exact));
            string reason;
            Assert.IsTrue (stack.CanManipulate (stack.Find (LayerKind.DisplayBone), out reason));
            Assert.AreEqual (InverseKind.Approximate,
                stack.PathKind (stack.writeLayer, stack.Find (LayerKind.DisplayBone), out reason));
        }

        [Test]
        public void ZeroWeightLayerPassesThrough ()
        {
            FakeLayer rig = FakeLayer.Rig (InverseKind.None);
            PoseStack stack = Stack (rig);
            rig.weight = 0;

            string reason;
            Assert.IsTrue (stack.CanManipulate (stack.Find (LayerKind.DisplayBone), out reason));
            InverseKind step = stack.StepKind (rig, out reason);
            Assert.AreEqual (InverseKind.Exact, step);
            StringAssert.Contains ("重み 0", reason);
        }

        /// <summary>
        /// 👁 は段ごとに複数可、既定は全部 ON。落とした段は評価をスキップし、逆に通るときも素通し
        /// </summary>
        [Test]
        public void HiddenLayerPassesThrough ()
        {
            List<string> called = new List<string> ();
            FakeLayer rig = FakeLayer.Rig (InverseKind.None);
            rig.onEvaluate = t => called.Add ("Rig");
            FakeLayer user = FakeLayer.User ("User", 500);
            user.onEvaluate = t => called.Add ("User");
            PoseStack stack = Stack (rig, user);
            Assert.IsTrue (stack.layers.All (l => l.enabled));

            string reason;
            Assert.IsFalse (stack.CanManipulate (stack.Find (LayerKind.DisplayBone), out reason), "逆の無い Rig を通る");

            rig.enabled = false;
            Assert.IsTrue (stack.CanManipulate (stack.Find (LayerKind.DisplayBone), out reason), "落とした段は素通し");
            stack.Evaluate (0);
            CollectionAssert.AreEqual (new[] { "User" }, called);

            LayerState state = new LayerState ();
            stack.SaveState (state);
            Assert.IsFalse (state.Find (rig.id).enabled);

            FakeLayer rig2 = FakeLayer.Rig (InverseKind.None);
            PoseStack again = Stack (rig2);
            again.LoadState (state);
            Assert.IsFalse (rig2.enabled);

            rig2.enabled = true;
            again.SaveState (state);
            Assert.IsTrue (state.Find (rig.id).enabled);

            // 値の無い段は 👁 を入れる（Undo で値が消えたとき、作り直さないスタックでも戻る）
            rig2.enabled = false;
            again.LoadState (new LayerState ());
            Assert.IsTrue (rig2.enabled);

            // 今のキャラに無い段の 👁 は残さない（同じ id を使い回す段が落ちたまま出てこないように）。重みは残す
            rig2.enabled = false;
            rig2.weight = 0.4f;
            again.SaveState (state);
            Stack ().SaveState (state);
            Assert.IsTrue (state.Find (rig.id).enabled);
            Assert.AreEqual (0.4f, state.Find (rig.id).weight);
        }

        [Test]
        public void GrabbingRigTargetWritesDataDirectly ()
        {
            FakeLayer humanoid = FakeLayer.Humanoid ();
            FakeLayer rig = FakeLayer.Rig (InverseKind.None);
            PoseStack stack = Stack (humanoid, rig);

            InverseKind kind;
            string reason;
            List<PoseLayer> path = stack.PathLayers (stack.writeLayer, rig, out kind, out reason);
            // 並びの区間で判定すると Humanoid（ずれる）と Rig 自身（戻せない）を通ったことになる
            Assert.AreEqual (InverseKind.Exact, kind);
            Assert.IsEmpty (path);

            Assert.IsTrue (stack.TrySetManipulate (rig, out reason));
            Assert.IsFalse (stack.IsOnPath (humanoid));
        }

        [Test]
        public void RigLayerTakesWorstConstraint ()
        {
            RigLayerInfo info = new RigLayerInfo { label = "Foot", active = true, weight = 0.5f };
            info.constraints.Add (new RigConstraintInfo { label = "A", inverse = InverseKind.Exact });
            info.constraints.Add (new RigConstraintInfo { label = "B", inverse = InverseKind.Approximate, reason = "ヒント無し" });
            RigBuilderLayer layer = new RigBuilderLayer (info, 2);

            Assert.AreEqual (InverseKind.Approximate, layer.inverse);
            StringAssert.Contains ("B", layer.inverseReason);
            Assert.AreEqual (0.5f, layer.weight);
            Assert.AreEqual ("RigBuilder:Foot@2", layer.id);
            Assert.AreEqual (PoseStack.kOrderRigBuilder + 2, layer.order);
            Assert.AreEqual (2, layer.details.Count);
            Assert.AreEqual (GrabTarget.Values, layer.grab, "値は保存データから配られる");
            Assert.AreEqual (PosePhase.Graph, layer.phase);
            Assert.IsFalse (layer.canManipulate, "ターゲットの操作は S4 まで選べない");

            info.constraints.Add (new RigConstraintInfo { label = "C", inverse = InverseKind.None });
            Assert.AreEqual (InverseKind.None, new RigBuilderLayer (info, 0).inverse);
        }

        [Test]
        public void UnreadableRigRowHasNoWeightOrGrab ()
        {
            RigBuilderLayer layer = new RigBuilderLayer (null, 0);
            Assert.IsTrue (layer.unreadable);
            Assert.IsFalse (layer.hasWeight);
            Assert.AreEqual (GrabTarget.None, layer.grab);
            Assert.AreEqual ("RigBuilder", layer.id);
        }

        [Test]
        public void LoadStateFallsBackWhenPathIsBlocked ()
        {
            FakeLayer rig = FakeLayer.Rig (InverseKind.None);
            PoseStack stack = Stack (rig);
            LayerState state = new LayerState {
                write = "EditingRig",
                manipulate = "DisplayBone",
                values = new List<LayerValues> { new LayerValues { id = rig.id, weight = 0 } },
            };

            // 重み 0 で保存された間は通る
            stack.LoadState (state);
            Assert.AreEqual (LayerKind.DisplayBone, stack.manipulateLayer.kind);

            // 重みが戻っていたら通らないので、既定に戻す
            state.values[0].weight = 1;
            stack = Stack (FakeLayer.Rig (InverseKind.None));
            stack.LoadState (state);
            Assert.AreEqual (LayerKind.EditingRig, stack.manipulateLayer.kind);
        }

        [Test]
        public void WeightsRoundTripThroughState ()
        {
            FakeLayer rig = FakeLayer.Rig (InverseKind.Exact);
            PoseStack stack = Stack (rig);
            LayerState state = new LayerState { values = new List<LayerValues> { new LayerValues { id = "RigBuilder:Other@9", weight = 0.3f } } };

            rig.weight = 0.25f;
            stack.SaveState (state);
            Assert.AreEqual (0.3f, state.Find ("RigBuilder:Other@9").weight, "今のキャラに無い段の重みも残す");
            Assert.AreEqual (0.25f, state.Find (rig.id).weight);

            FakeLayer again = FakeLayer.Rig (InverseKind.Exact);
            PoseStack.FromLayers (new PoseLayer[] { new EditingRigLayer (), again }).LoadState (state);
            Assert.AreEqual (0.25f, again.weight);
        }

        /// <summary>
        /// クリップの合成の重み（Editing Rig / Generic Pose / Humanoid Pose 共通）も段ごとに残る。クリップを持たない段には当てない
        /// </summary>
        [Test]
        public void ClipWeightsRoundTripThroughState ()
        {
            FakeClipHost host = new FakeClipHost ();
            EditingRigLayer data = new EditingRigLayer (null, host);
            GenericPoseLayer generic = new GenericPoseLayer (null, host);
            HumanoidLayer humanoid = new HumanoidLayer (null, host);
            FakeLayer rig = FakeLayer.Rig (InverseKind.Exact);
            Assert.IsTrue (data.clip.hasWeight && generic.clip.hasWeight && humanoid.clip.hasWeight);
            OutputLayer output = new OutputLayer (host);
            Assert.IsFalse (output.clip.hasWeight, "出力は合成しない");
            Assert.IsFalse (output.hasBadges || output.isPoseStage, "書き出すだけの段は 👁・✏・✋ も矢印も持たない");
            Assert.Less (humanoid.order, output.order, "Humanoid Pose のすぐ下（上の段を通った姿勢を焼く）");
            Assert.Less (output.order, rig.order, "Rig の段より上（Rig の後の見た目は焼かない）");

            data.clip.setWeight (0.5f);
            Assert.AreEqual (0.5f, data.clip.getWeight (), "重みは窓を通して段に入る");
            humanoid.clipWeight = 0;
            LayerState state = new LayerState ();
            Stack (data, generic, humanoid, rig).SaveState (state);
            Assert.AreEqual (0.5f, state.Find (data.id).clipWeight);
            Assert.AreEqual (1f, state.Find (generic.id).clipWeight);
            Assert.AreEqual (0f, state.Find (humanoid.id).clipWeight);
            state.Find (rig.id).clipWeight = 0.2f;

            EditingRigLayer data2 = new EditingRigLayer (null, host);
            HumanoidLayer humanoid2 = new HumanoidLayer (null, host);
            FakeLayer rig2 = FakeLayer.Rig (InverseKind.Exact);
            PoseStack.FromLayers (new PoseLayer[] { data2, humanoid2, rig2 }).LoadState (state);
            Assert.AreEqual (0.5f, data2.clipWeight);
            Assert.AreEqual (0f, humanoid2.clipWeight);
            Assert.AreEqual (1f, rig2.clipWeight, "クリップを持たない段にはクリップの重みを当てない");
        }

        sealed class FakeClipHost : ILayerHost
        {
            public AnimationClip clip { get { return null; } }
            public string clipProblem { get { return null; } }
            public int strayCurveCount { get { return 0; } }
            public void SetClip (AnimationClip value) { }
            public void CreateClip () { }
            public void RemoveStrayCurves () { }
            public List<AnimationClip> bakeOverrides { get { return new List<AnimationClip> (); } }
            public string GetSwapProblem () { return "テスト"; }
            public bool SwapClipReferences (bool back) { return false; }
            public bool layerStructureEditing { get { return false; } }
            public bool humanoidPoseLayer { get { return true; } }
            public void SetHumanoidPoseLayer (bool value) { }
            public void MoveOverride (int index, int direction) { }
            public string GetMoveOverrideProblem (int index, int direction) { return "テスト"; }
            public IReadOnlyList<OutputEntry> outputEntries { get { return new List<OutputEntry> (); } }
            public string DescribeOutput (OutputLayer output) { return null; }
            public string GetOutputPath (OutputLayer output) { return null; }
            public LayerClipStatus GetOutputStatus (OutputLayer output) { return new LayerClipStatus (LayerClipState.None, null); }
            public string GetOutputBakeProblem (OutputLayer output) { return "テスト"; }
            public bool BakeOutput (OutputLayer output) { return false; }
            public void RemoveOutput (OutputLayer output) { }
            public void MoveOutput (OutputLayer output, int direction) { }
            public string GetMoveOutputProblem (OutputLayer output, int direction) { return "テスト"; }
            public bool autoBake { get; set; }
            public AnimationClip humanoidClip { get { return null; } }
            public void SetHumanoidClip (AnimationClip value) { }
            public AnimationClip genericClip { get { return null; } }
            public void SetGenericClip (AnimationClip value) { }
            public void SetClipWeight (PoseLayer layer, float weight) { layer.clipWeight = weight; }
            public string GetImportProblem () { return "テスト"; }
            public bool ImportGenericClip () { return false; }
            public string GetImportBaseProblem () { return "テスト"; }
            public bool ImportBaseClip () { return false; }
            public string GetPickImportProblem () { return "テスト"; }
            public void PickImportClip () { }
            public bool importPreviewing { get { return false; } }
            public string GetHumanoidApplyProblem () { return "テスト"; }
            public bool ApplyHumanoidPoseToRig () { return false; }
            public ContextClip timelineClip { get { return null; } }
            public int timelineClipCandidateCount { get { return 0; } }
            public bool useHumanoidBase { get { return false; } }
            public IReadOnlyList<OverrideClip> overrides { get { return new OverrideClip[0]; } }
            public int overrideWriteIndex { get { return -1; } }
            public void SetOverrideClip (int index, AnimationClip clip) { }
            public void SetOverrideMode (int index, OverrideMode mode) { }
            public void RemoveOverride (int index, bool deleteAsset) { }
            public string GetMergeOverridesProblem () { return "テスト"; }
            public bool MergeOverrides () { return false; }
        }

        [Test]
        public void FailingLayerIsStoppedAndLaterLayersRun ()
        {
            List<string> called = new List<string> ();
            FakeLayer broken = new FakeLayer {
                fakeKind = LayerKind.User, fakeOrder = 500, fakeLabel = "Broken", fakeId = "Broken",
                onEvaluate = t => { throw new System.InvalidOperationException ("壊れた"); },
            };
            FakeLayer after = new FakeLayer {
                fakeKind = LayerKind.User, fakeOrder = 600, fakeLabel = "After", fakeId = "After",
                onEvaluate = t => called.Add ("After"),
            };
            PoseStack stack = Stack (broken, after);

            LogAssert.Expect (LogType.Exception, new Regex ("壊れた"));
            stack.Evaluate (0);
            stack.Evaluate (0);

            Assert.AreEqual (2, called.Count);
            Assert.IsFalse (broken.active);
            StringAssert.Contains ("壊れた", broken.note);

            // 止めた段は素通しなので、逆に通っても塞がない
            string reason;
            Assert.AreEqual (InverseKind.Exact, stack.StepKind (broken, out reason));
        }

        /// <summary>
        /// 焼くときに表示モデルの骨から読むかは、段の種類ではなく phase（Graph・Terminal）で決まる
        /// </summary>
        [Test]
        public void PoseChangeBeforeIsDecidedByPhase ()
        {
            FakeLayer rig = FakeLayer.Rig (InverseKind.Exact);
            FakeLayer below = FakeLayer.User ("Below", PoseStack.kOrderRigBuilder + 500);
            PoseStack stack = Stack (FakeLayer.Humanoid (), rig, below);
            PoseLayer humanoid = stack.Find (LayerKind.HumanoidAnimation);

            Assert.IsFalse (stack.HasPoseChangeBefore (humanoid), "Body の段だけなら編集用の体から焼く");
            Assert.IsTrue (stack.HasPoseChangeBefore (below), "Graph の段（Rig）を通った後");

            rig.enabled = false;
            Assert.IsFalse (stack.HasPoseChangeBefore (below), "落とした段は数えない");
        }

        [Test]
        public void FindByTypeAndCondition ()
        {
            FakeLayer a = FakeLayer.User ("A", 500);
            FakeLayer b = FakeLayer.User ("B", 600);
            PoseStack stack = Stack (a, b);

            Assert.IsInstanceOf<EditingRigLayer> (stack.Find<EditingRigLayer> ());
            Assert.AreSame (b, stack.Find<FakeLayer> (l => l.fakeId == "B"));
            Assert.IsNull (stack.Find<OverrideLayer> ());
        }

        /// <summary>
        /// 段を作らずに出す Output の id は、段の id と同じ（並びの「直前の段」を辿るのに使う）
        /// </summary>
        [TestCase ("")]
        [TestCase ("output2")]
        public void OutputIdForMatchesLayerId (string name)
        {
            OutputEntry entry = new OutputEntry { name = name };
            Assert.AreEqual (new OutputLayer (null, entry).id, OutputLayer.IdFor (entry));
        }

        /// <summary>
        /// 👁・✏・✋ を出すのは、値か姿勢を持つ段（書き出すだけの段には出さない）
        /// </summary>
        [Test]
        public void BadgesFollowDataOrPose ()
        {
            Assert.IsTrue (new EditingRigLayer ().hasBadges);
            Assert.IsTrue (new DisplayBoneLayer ().hasBadges);
            Assert.IsFalse (new OutputLayer ().hasBadges);
            Assert.IsTrue (new EditingRigLayer ().canWrite, "保存データの段は既定で書ける");
            Assert.IsFalse (new DisplayBoneLayer ().canWrite);
        }
    }

}
