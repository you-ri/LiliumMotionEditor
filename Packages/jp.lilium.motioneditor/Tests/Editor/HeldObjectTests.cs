using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 持ち物（S91）。物が導き、手が従う: 物の姿勢をキーに持ち、持っている手はどのフレームでも握りに付く
    /// </summary>
    public class HeldObjectTests
    {
        /// <summary>握りの残差の上限（メートル）</summary>
        const float kGripTolerance = 0.001f;
        /// <summary>切り替えのフレームで跳ばない差の上限（メートル）</summary>
        const float kJumpTolerance = 1e-4f;
        const string kName = "Sword";

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
            PreviewStage.heldObjectsForTest = null;
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        /// <summary>物のモデル（空の Transform だけ。テストはメッシュに頼らない）</summary>
        GameObject CreateModel ()
        {
            GameObject model = new GameObject ("Model");
            created_.Add (model);
            new GameObject ("Blade").transform.SetParent (model.transform, false);
            return model;
        }

        /// <summary>両手で持つ物の定義。腕が楽に届くよう、体の前の低い所に置く</summary>
        HeldObjectDefinition CreateObject (HeldObjectFrame frame = HeldObjectFrame.Chest)
        {
            return new HeldObjectDefinition {
                name = kName,
                prefab = CreateModel (),
                frame = frame,
                restPosition = frame == HeldObjectFrame.Root ? new Vector3 (0, 1.2f, 0.25f) : new Vector3 (0, -0.1f, 0.3f),
                restRotation = Vector3.zero,
                rightGrip = new HeldObjectGrip { position = new Vector3 (0, 0.08f, -0.05f), rotation = Vector3.zero },
                leftGrip = new HeldObjectGrip { position = new Vector3 (0, -0.08f, -0.05f), rotation = Vector3.zero },
            };
        }

        PoseSession Open (params HeldObjectDefinition[] objects)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PreviewStage.heldObjectsForTest = objects;
            PoseSession session = new PoseSession (prefab, definition_, false);
            disposables_.Add (session);
            session.clip = NewClip ();
            return session;
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        static HeldObjectRig.Entry Entry (PoseSession session)
        {
            return session.stage.heldObjects.entries[0];
        }

        /// <summary>手首の骨と、握りの場所の差（メートル）</summary>
        static float GripGap (PoseSession session, HumanBodyBones hand)
        {
            HeldObjectRig.Entry entry = Entry (session);
            HeldObjectGrip grip = hand == HumanBodyBones.RightHand ? entry.definition.rightGrip : entry.definition.leftGrip;
            Vector3 target = entry.editing.TransformPoint (grip.position);
            return Vector3.Distance (session.stage.editingRig.GetEditingBone (hand).position, target);
        }

        static void AssertHolding (PoseSession session, string message)
        {
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, message + ": 右手");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.LeftHand), kGripTolerance, message + ": 左手");
        }

        static BodyPointTarget Point (PoseSession session, HumanBodyBones bone)
        {
            GameObject control = session.stage.fullBody.GetControl (bone).gameObject;
            return session.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
        }

        static HeldObjectTarget ObjectTarget (PoseSession session)
        {
            return session.targets.OfType<HeldObjectTarget> ().First ();
        }

        static void MoveObject (PoseSession session, Vector3 delta)
        {
            HeldObjectRig.Entry entry = Entry (session);
            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry.DragTo (position + delta, rotation);
            session.SolveAfterEdit ();
        }

        // ---- S91e 物の上の点 ----

        const string kCenter = kName + ".Center";
        const string kTip = kName + ".Tip";
        const string kPommel = kName + ".Pommel";
        static readonly Vector3 kCenterOffset = new Vector3 (0, 0.2f, 0);
        static readonly Vector3 kTipOffset = new Vector3 (0, 1.1f, 0);
        static readonly Vector3 kPommelOffset = new Vector3 (0, -0.2f, 0);

        /// <summary>重心（重さ 1・向きあり）と、先と柄の下（重さ 0）を持つ物</summary>
        HeldObjectDefinition CreateObjectWithPoints ()
        {
            HeldObjectDefinition definition = CreateObject ();
            definition.points = new List<HeldObjectPoint> {
                new HeldObjectPoint { name = "Tip", position = kTipOffset, weight = 0 },
                new HeldObjectPoint { name = "Pommel", position = kPommelOffset, weight = 0 },
                new HeldObjectPoint { name = "Center", position = kCenterOffset, weight = 1, rotation = true },
            };
            return definition;
        }

        static BodyPointTarget ObjectPoint (PoseSession session, string name)
        {
            return session.targets.OfType<BodyPointTarget> ().First (t => t.id.name == name);
        }

        static Vector3 OnObject (PoseSession session, Vector3 offset)
        {
            Transform editing = Entry (session).editing;
            return editing.position + editing.rotation * offset;
        }

        [Test]
        public void ObjectPoints_AreOrdinaryPoints ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            FullBodyRig body = session.stage.fullBody;
            session.Evaluate (0);
            foreach (string name in new[] { kCenter, kTip, kPommel }) {
                BodyPointId id = new BodyPointId (name);
                Assert.IsTrue (body.HasPoint (id), name + " は全身 IK の点");
                Assert.IsNotNull (session.stage.editingRig.FindControl (RigPaths.BodyPoint (name)), name + " のコントロールは Controls/Body");
                Assert.AreEqual (BodyPointState.Free, ObjectPoint (session, name).state, "初めは Free");
                Assert.AreEqual (Entry (session), ObjectPoint (session, name).heldObject);
            }
            Assert.AreEqual (ObjectPoint (session, kCenter), ObjectPoint (session, kTip).link, "軽い点は重い点へ線でつなぐ");
            Assert.IsNull (ObjectPoint (session, kCenter).link, "重い点は手へつながない");
            Assert.IsTrue (body.TryGetBonePointWorld (new BodyPointId (kTip), out Vector3 tip));
            Assert.Less (Vector3.Distance (tip, OnObject (session, kTipOffset)), 1e-4f, "点は物の上にある");
        }

        [Test]
        public void GrabbingHeavyPoint_MovesObjectWithoutTurning_AndPullsBody ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            Quaternion rotation = Entry (session).editing.rotation;
            Vector3 hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position;
            BodyPointTarget center = ObjectPoint (session, kCenter);
            Vector3 target = center.framePosition + new Vector3 (0, 0, 0.5f);
            center.MoveTo (target);
            session.SolveAfterEdit ();
            Assert.Less (Vector3.Distance (OnObject (session, kCenterOffset), target), 0.25f, "重心は目標へ寄る");
            Assert.Less (Quaternion.Angle (Entry (session).editing.rotation, rotation), 0.01f, "物は回らない");
            Assert.Greater (Vector3.Distance (session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position, hips), 0.01f, "重い点は体を引く");
            AssertHolding (session, "動かした後も持っている");
        }

        [Test]
        public void GrabbingLightPoint_TurnsObjectAroundHeavyPoint_AndLeavesBody ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            Vector3 center = OnObject (session, kCenterOffset);
            Vector3 hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position;
            Quaternion rotation = Entry (session).editing.rotation;
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.MoveTo (tip.framePosition + new Vector3 (0.15f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Quaternion.Angle (Entry (session).editing.rotation, rotation), 1, "物は回る");
            Assert.Less (Vector3.Distance (OnObject (session, kCenterOffset), center), 0.002f, "重心は動かない");
            Assert.Less (Vector3.Distance (session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position, hips), 1e-4f, "軽い点は腰を引かない");
            AssertHolding (session, "回した後も持っている");
        }

        [Test]
        public void LockedTip_StaysWhenHeavyPointMoves ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.SetState (BodyPointState.Locked);
            session.SolveAfterEdit ();
            Vector3 locked = OnObject (session, kTipOffset);
            BodyPointTarget center = ObjectPoint (session, kCenter);
            center.MoveTo (center.framePosition + new Vector3 (0.05f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Less (Vector3.Distance (OnObject (session, kTipOffset), locked), 0.002f, "Lock の先は動かない");
        }

        [Test]
        public void ObjectPointKeys_ComeBack_AndHandsKeepHolding ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.MoveTo (tip.framePosition + new Vector3 (0.1f, 0, 0));
            session.SolveAfterEdit ();
            Quaternion rotation = Entry (session).editing.rotation;
            Assert.IsNull (session.CommitPose (), "キーを打てる");
            string path = RigPaths.BodyPoint (kTip);
            Assert.IsTrue (AnimationUtility.GetCurveBindings (session.clip).Any (b => b.path == path), "点のキーはほかの点と同じパスに打つ");
            Assert.IsTrue (AnimationUtility.GetCurveBindings (session.clip).Any (b => b.path == RigPaths.Object (kName)), "物の姿勢も打つ");

            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, ObjectPoint (session, kTip).state, "状態が戻る");
            Assert.Less (Quaternion.Angle (Entry (session).editing.rotation, rotation), 0.5f, "姿勢が戻る");
            AssertHolding (session, "戻した後も持っている");
        }

        [Test]
        public void TurningPoint_TurnsObjectAboutThatPoint ()
        {
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            BodyPointTarget center = ObjectPoint (session, kCenter);
            Vector3 before = center.framePosition;
            Quaternion rotation = Entry (session).editing.rotation;
            center.TurnHeldObject (Quaternion.Euler (0, 0, 20));
            session.SolveAfterEdit ();
            Assert.Greater (Quaternion.Angle (Entry (session).editing.rotation, rotation), 5, "物は回る");
            Assert.Less (Vector3.Distance (OnObject (session, kCenterOffset), before), 0.05f, "回す中心はほぼ動かない（届かない分だけ体へ寄る）");
        }

        [Test]
        public void LoadPoints_ReadsEmpties_CenterIsHeavy ()
        {
            GameObject model = CreateModel ();
            foreach (KeyValuePair<string, Vector3> pair in new Dictionary<string, Vector3> { { "Point_Tip", kTipOffset }, { "Point_Center", kCenterOffset } }) {
                GameObject empty = new GameObject (pair.Key);
                empty.transform.SetParent (model.transform, false);
                empty.transform.localPosition = pair.Value;
            }
            HeldObjectDefinition definition = new HeldObjectDefinition { name = kName, prefab = model };
            definition.LoadPoints (model);
            Assert.AreEqual (2, definition.points.Count);
            HeldObjectPoint center = definition.points.First (p => p.name == "Center");
            Assert.AreEqual (1, center.weight);
            Assert.IsTrue (center.rotation);
            HeldObjectPoint tip = definition.points.First (p => p.name == "Tip");
            Assert.AreEqual (0, tip.weight);
            Assert.AreEqual (kTipOffset, tip.position);
        }

        [Test]
        public void DuplicatePointNames_AreReported ()
        {
            HeldObjectDefinition definition = CreateObjectWithPoints ();
            definition.points[1].name = "Tip";
            Assert.IsNotEmpty (definition.Validate ());
        }

        // ---- S91a 物を足す ----

        [Test]
        public void Place_ClonesObjectUnderParentBone_WithControl ()
        {
            PoseSession session = Open (CreateObject ());
            Transform display = Entry (session).display;
            Assert.AreEqual (session.stage.animator.GetBoneTransform (HumanBodyBones.RightHand), display.parent, "表示モデルの右手の子");
            Assert.AreEqual (kName, display.name);
            Assert.AreEqual (session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand), Entry (session).editing.parent, "編集用の体でも右手の子");
            Assert.IsNotNull (session.stage.editingRig.FindControl (RigPaths.Object (kName)), "コントロール");
            Assert.IsEmpty (session.targets.OfType<FkTarget> ().Where (t => t.label == kName), "FK のハンドルは出さない");
        }

        [Test]
        public void NoHeldObjects_LeavesPoseAndControlsAlone ()
        {
            PoseSession session = Open ();
            Assert.IsNull (session.stage.heldObjects);
            Assert.IsNull (session.stage.editingRig.FindControl (RigPaths.kObject));
            Assert.IsEmpty (session.targets.OfType<HeldObjectTarget> ());
        }

        [Test]
        public void NoModelOrPath_ReportsAndSkips ()
        {
            HeldObjectDefinition definition = CreateObject ();
            definition.prefab = null;
            PoseSession session = Open (definition);
            Assert.IsNull (session.stage.heldObjects);
            Assert.IsNotEmpty (session.stage.editingRig.errors, "理由を出す");
        }

        [Test]
        public void ObjectFollowsFrameBone_AndSavedKeysComeBack ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            MoveObject (session, new Vector3 (0.05f, 0.02f, 0.03f));
            Vector3 moved = Entry (session).editing.position;
            Assert.IsNull (session.CommitPose (), "キーを打てる");

            session.Evaluate (0);
            Assert.LessOrEqual (Vector3.Distance (moved, Entry (session).editing.position), kGripTolerance, "打ったキーから同じ場所に戻る");
            AnimationCurve curve = AnimationUtility.GetEditorCurve (session.clip, EditorCurveBinding.FloatCurve (RigPaths.Object (kName), typeof (HeldObjectControl), HeldObjectControl.kLeftHoldProperty));
            Assert.IsNotNull (curve, "持っているかのキーも打つ");
        }

        // ---- S91b 手が付いてくる ----

        [Test]
        public void NoKeys_HandsHoldTheGrips ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            AssertHolding (session, "キーの無いクリップ");
        }

        [Test]
        public void MovingObject_PullsBothHands ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Vector3 before = Entry (session).editing.position;
            MoveObject (session, new Vector3 (0.1f, 0.05f, 0.05f));
            Assert.Greater (Vector3.Distance (before, Entry (session).editing.position), 0.05f, "物が動く");
            AssertHolding (session, "物を動かした後");
        }

        [Test]
        public void MovingHips_ChestFrameObjectFollows_HandsStayOnGrips ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.3f, 0, 0));
            session.SolveAfterEdit ();
            AssertHolding (session, "腰を動かした後");
        }

        [Test]
        public void KeyingObjectOnly_HandsFollowAtKeyAndBetweenKeys ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            // 物のキーだけを直接直す（骨のキーは元のまま）
            Transform control = Entry (session).control;
            using (CurveWriter writer = CurveWriter.Begin (session.clip, session.stage.editingRoot, 30, null)) {
                control.localPosition += new Vector3 (0.05f, 0.05f, 0);
                writer.Transform (control, TransformChannels.Position | TransformChannels.Rotation);
            }
            session.stage.InvalidateClipCurves ();
            foreach (int frame in new[] { 30, 15, 7 }) {
                session.Evaluate (frame);
                AssertHolding (session, "フレーム " + frame);
            }
        }

        [Test]
        public void OutOfReach_ObjectYields_HandsStayOnGrips ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            // 腰が Free なら体ごと付いてくるので、腰を留めて届かなくする
            Point (session, HumanBodyBones.Hips).SetLocked (true);
            HeldObjectRig.Entry entry = Entry (session);
            entry.GetWorldPose (out Vector3 start, out Quaternion rotation);
            Vector3 far = start + new Vector3 (0, 0, 4);
            entry.DragTo (far, rotation);
            session.SolveAfterEdit ();
            AssertHolding (session, "届かない所");
            Assert.Greater (Vector3.Distance (far, entry.editing.position), 0.5f, "物が体の方へ寄る");
        }

        [Test]
        public void ReleasingHand_DoesNotJump_OtherHandKeepsHolding ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Assert.IsNull (session.CommitPose ());
            Vector3 hand = session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand).position;
            Vector3 sword = Entry (session).editing.position;

            Entry (session).values.leftHold = 0;
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Assert.AreEqual (0, Entry (session).values.leftHold, "左手は離した");
            Assert.LessOrEqual (Vector3.Distance (hand, session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand).position), kGripTolerance, "離した手");
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kGripTolerance, "物");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は持ったまま");

            session.Evaluate (15);
            Assert.AreEqual (1, Entry (session).values.leftHold, "前のキーまでは持っている（段差）");
        }

        [Test]
        public void HeldHand_HasNoPointKeys_AndGrabMovesObject ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            BodyPointTarget hand = Point (session, HumanBodyBones.RightHand);
            Assert.IsTrue (hand.held);
            Vector3 before = Entry (session).editing.position;
            hand.MoveBy (new Vector3 (0.05f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Distance (before, Entry (session).editing.position), 0.03f, "手をつかむと物が動く");
            AssertHolding (session, "手をつかんだ後");
            Assert.IsNull (session.CommitPose ());
            string path = RigPaths.BodyPoint (HumanBodyBones.RightHand.ToString ());
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (session.clip).Where (b => b.path == path), "持っている手の点のキーは打たない");
        }

        [Test]
        public void RootFrame_ObjectStaysWhenHipsMove ()
        {
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            Vector3 before = Entry (session).editing.position;
            // 腕が伸びきると物が寄るので、届く範囲で動かす
            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.02f, 0, 0));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (before, Entry (session).editing.position), kGripTolerance, "ルートの物は体に付いて動かない");
            AssertHolding (session, "腰を動かした後");
        }

        // ---- 窓を開かずに書く（PoseDocument） ----

        [Test]
        public void PoseDocument_PlacesObject_AndReadsItBack ()
        {
            PoseSession session = Open (CreateObject ());
            Vector3 chestSpot = new Vector3 (0.02f, 1.1f, 0.3f);
            PoseDocument pose = new PoseDocument {
                frame = 0,
                objects = new List<PoseObject> {
                    new PoseObject { name = kName, position = chestSpot, aim = new PoseAim { forward = Vector3.up, up = Vector3.back } },
                },
            };
            Assert.IsNull (PoseDocument.Write (session, pose), "書ける");

            PoseDocument read = PoseDocument.Read (session, 0, out string problem);
            Assert.IsNull (problem);
            PoseObject item = read.objects.First (o => o.name == kName);
            Assert.LessOrEqual (Vector3.Distance (chestSpot, item.position), kGripTolerance, "書いた場所");
            Assert.LessOrEqual (Vector3.Angle (Vector3.up, item.aim.forward), 0.1f, "物の +Z の向き");
            Assert.LessOrEqual (Vector3.Angle (Vector3.back, item.aim.up), 0.1f, "物の +Y の向き");
            Assert.AreEqual ("Both", item.hold);
            AssertHolding (session, "書いた後");
            StringAssert.Contains ("\"objects\":[", read.ToCompactJson (), "丸めた形にも出す");
        }

        [Test]
        public void PoseDocument_ChangesHold_AsSteps ()
        {
            PoseSession session = Open (CreateObject ());
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 0, objects = new List<PoseObject> { new PoseObject { name = kName, hold = "Both" } } }));
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 30, objects = new List<PoseObject> { new PoseObject { name = kName, hold = "Right" } } }));
            Assert.AreEqual ("Both", PoseDocument.Read (session, 29, out string _).objects[0].hold, "切り替えの前");
            Assert.AreEqual ("Right", PoseDocument.Read (session, 30, out string _).objects[0].hold, "切り替えのフレーム");
            Assert.AreEqual ("Right", PoseDocument.Read (session, 45, out string _).objects[0].hold, "次のキーまで続く");
        }

        [Test]
        public void PoseDocument_RefusesUnknownObjectAndHold ()
        {
            PoseSession session = Open (CreateObject ());
            StringAssert.Contains ("Nope", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = "Nope" } } }));
            StringAssert.Contains ("Sideways", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = kName, hold = "Sideways" } } }));
        }

        [Test]
        public void PoseDocument_ReportsObjectMovedOutOfReach ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Point (session, HumanBodyBones.Hips).SetLocked (true);
            Assert.IsNull (session.CommitPose ());
            PoseDocument pose = new PoseDocument { frame = 0, objects = new List<PoseObject> { new PoseObject { name = kName, position = new Vector3 (0, 1.2f, 4) } } };
            Assert.IsNull (PoseDocument.Solve (session, pose));
            CollectionAssert.Contains (PoseDocument.FindMovedObjects (pose, PoseDocument.ReadCurrent (session), 0.01f), kName, "届かないので物が寄った");
        }

        // ---- S91d リグエディタで置く ----

        [Test]
        public void StoreRest_PlacesObjectAtSameSpotAfterRebuild ()
        {
            HeldObjectDefinition definition = CreateObject ();
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            Vector3 placed = position + new Vector3 (0.03f, -0.02f, 0.01f);
            Quaternion turned = Quaternion.Euler (20, 10, 0) * rotation;
            // T ポーズのまま置く（リグエディタと同じ）。枠の骨は基準姿勢のまま
            entry.DragTo (placed, turned);
            session.stage.heldObjects.Refresh ();
            session.stage.heldObjects.StoreRest (entry);

            PoseSession rebuilt = Open (definition);
            rebuilt.Evaluate (0);
            Entry (rebuilt).GetWorldPose (out Vector3 position2, out Quaternion rotation2);
            Assert.LessOrEqual (Vector3.Distance (placed, position2), 1e-4f, "置いた場所");
            Assert.LessOrEqual (Quaternion.Angle (turned, rotation2), 0.01f, "置いた向き");
        }

        [Test]
        public void StoreGrip_KeepsGripAfterRebuild ()
        {
            HeldObjectDefinition definition = CreateObject ();
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig rig = session.stage.heldObjects;
            HeldObjectRig.Hand hand = Entry (session).left;
            rig.GetGrip (hand, out Vector3 position, out Quaternion rotation);
            Vector3 placed = position + new Vector3 (0, 0.01f, 0.02f);
            Quaternion turned = Quaternion.Euler (0, 15, 0) * rotation;
            rig.SetGrip (hand, placed, turned);
            rig.StoreGrip (hand);

            PoseSession rebuilt = Open (definition);
            rebuilt.Evaluate (0);
            session.stage.heldObjects.GetGrip (hand, out Vector3 before, out Quaternion unused);
            HeldObjectRig rebuiltRig = rebuilt.stage.heldObjects;
            rebuiltRig.GetGrip (Entry (rebuilt).left, out Vector3 after, out Quaternion afterRotation);
            Vector3 local = Quaternion.Inverse (Entry (rebuilt).editing.rotation) * (after - Entry (rebuilt).editing.position);
            Vector3 expected = Quaternion.Inverse (Entry (session).editing.rotation) * (before - Entry (session).editing.position);
            Assert.LessOrEqual (Vector3.Distance (expected, local), 1e-4f, "物から見た握りの場所");
            Assert.LessOrEqual (GripGap (rebuilt, HumanBodyBones.LeftHand), kGripTolerance, "手は新しい握りに付く");
        }

        // ---- S91c 焼く ----

        [Test]
        public void Constraint_NotFound_IsReported ()
        {
            HeldObjectDefinition definition = CreateObject ();
            definition.leftGrip.constraint = "NotThere/Constraint";
            PoseSession session = Open (definition);
            CollectionAssert.Contains (session.stage.editingRig.binding.notes, Tr ("HELD_OBJECT_CONSTRAINT_NOT_FOUND", "NotThere/Constraint"), "見つからない拘束は知らせる");
        }

        [Test]
        public void Bake_WritesObjectCurves_LocalToParent ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            MoveObject (session, new Vector3 (0.05f, 0.05f, 0));
            Assert.IsNull (session.CommitPose ());
            AnimationClip baked = NewClip ();
            using (HumanoidBaker baker = session.stage.CreateHumanoidBaker ()) {
                baker.afterSolve = () => session.stage.SolveLockedSpan (1);
                baker.Bake (session.clip, baked);
            }
            Transform display = Entry (session).display;
            string path = AnimationUtility.CalculateTransformPath (display, session.stage.animator.transform);
            string[] properties = AnimationUtility.GetCurveBindings (baked).Where (b => b.path == path).Select (b => b.propertyName).ToArray ();
            CollectionAssert.Contains (properties, "m_LocalPosition.x", "位置");
            CollectionAssert.Contains (properties, "m_LocalRotation.w", "向き");
        }
    }

}
