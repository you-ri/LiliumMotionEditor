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
            return Open (true, objects);
        }

        /// <param name="keyObjectRotation">
        /// 物の姿勢の点を、位置も向きも固定した Pin にするキーをクリップの頭に打つ（デモのクリップと同じ。S102 から、キーの無い物は位置だけの Pin なので、
        /// 物の向きのキーが効かない）。false ならキーの無い物
        /// </param>
        PoseSession Open (bool keyObjectRotation, params HeldObjectDefinition[] objects)
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
            if (keyObjectRotation) {
                foreach (HeldObjectDefinition held in objects) KeyObjectState (session.clip, held.name, 1, 1, 0);
            }
            return session;
        }

        /// <summary>物の姿勢の点の状態のキーを、クリップの頭に打つ</summary>
        static void KeyObjectState (AnimationClip clip, string name, float position, float rotation, float locked)
        {
            foreach ((string property, float value) in new[] { (BodyPoint.kPositionWeightProperty, position), (BodyPoint.kRotationWeightProperty, rotation), (BodyPoint.kLockedProperty, locked) }) {
                AnimationCurve curve = new AnimationCurve (new Keyframe (0, value));
                AnimationUtility.SetKeyLeftTangentMode (curve, 0, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode (curve, 0, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (RigPaths.ObjectPose (name), typeof (BodyPoint), property), curve);
            }
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

        /// <summary>
        /// 手首と、キャラの設定の握りの差のうち、柄の軸まわりの回りに依らない分（柄に沿った場所の差と、柄からの距離の差の大きい方。メートル）。
        /// 握りは柄の軸まわりに自由なので（S100）、物と腕を鎖で解くと、手は柄のまわりで回った所を握る
        /// </summary>
        static float HandleGap (PoseSession session, HumanBodyBones hand)
        {
            HeldObjectRig.Entry entry = Entry (session);
            HeldObjectGrip grip = hand == HumanBodyBones.RightHand ? entry.definition.rightGrip : entry.definition.leftGrip;
            Vector3 axis = entry.definition.gripAxis.normalized;
            Vector3 at = entry.editing.InverseTransformPoint (session.stage.editingRig.GetEditingBone (hand).position);
            float along = Mathf.Abs (Vector3.Dot (at - grip.position, axis));
            float radial = Mathf.Abs (Vector3.ProjectOnPlane (at, axis).magnitude - Vector3.ProjectOnPlane (grip.position, axis).magnitude);
            return Mathf.Max (along, radial);
        }

        /// <summary>
        /// 両手とも柄を握っている（柄に沿った場所と柄からの距離で見る）。握りは柄の軸まわりに自由なので（S100）、物を手の骨の子にして解くと（S102）、
        /// 手は腕の楽な向きへ柄のまわりを少し回った所を握る
        /// </summary>
        static void AssertHolding (PoseSession session, string message)
        {
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, message + ": 右手");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.LeftHand), kGripTolerance, message + ": 左手");
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

        // ---- S91f 片手・両手・差す ----

        const string kHip = "Hip";

        HeldObjectDefinition CreateObjectWithSocket ()
        {
            HeldObjectDefinition definition = CreateObject ();
            // 両手の届く、腰の前（届かない場所は SocketOutOfReach_ のテスト）
            definition.sockets.Add (new HeldObjectSocket { name = kHip, bone = HumanBodyBones.Hips, position = new Vector3 (0, 0.2f, 0.25f) });
            return definition;
        }

        static Transform Bone (PoseSession session, HumanBodyBones bone)
        {
            return session.stage.editingRig.GetEditingBone (bone);
        }

        /// <summary>今のフレームで差して（CLI と同じ経路）キーを打つ</summary>
        static void WriteSocket (PoseSession session, int frame, string socket, string hold = "")
        {
            PoseDocument document = new PoseDocument { frame = frame };
            document.objects.Add (new PoseObject { name = kName, socket = socket, hold = hold });
            Assert.IsNull (PoseDocument.Write (session, document), "書ける");
        }

        [Test]
        public void OneHandThenTwoHands_ByHoldKeys ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            PoseDocument oneHand = new PoseDocument { frame = 20 };
            oneHand.objects.Add (new PoseObject { name = kName, hold = "Right" });
            Assert.IsNull (PoseDocument.Write (session, oneHand));
            PoseDocument twoHands = new PoseDocument { frame = 40 };
            twoHands.objects.Add (new PoseObject { name = kName, hold = "Both" });
            Assert.IsNull (PoseDocument.Write (session, twoHands));

            session.Evaluate (30);
            Assert.AreEqual (0, Entry (session).values.leftHold, "片手");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は握りに付く");
            session.Evaluate (40);
            AssertHolding (session, "両手に戻した");
        }

        [Test]
        public void InSocket_ObjectStaysOnBone_WhenHipsMove ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            WriteSocket (session, 0, kHip);
            HeldObjectRig.Entry entry = Entry (session);
            Assert.AreEqual (0, entry.values.leftHold, "両手を放す");
            Assert.AreEqual (0, entry.values.rightHold, "両手を放す");
            Transform hips = Bone (session, HumanBodyBones.Hips);
            Vector3 local = hips.InverseTransformPoint (entry.editing.position);

            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.2f, -0.1f, 0.1f));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (local, hips.InverseTransformPoint (entry.editing.position)), kJumpTolerance, "腰に付いて動く");
        }

        [Test]
        public void EnteringSocket_HandsDoNotJump_AndObjectDoesNotJump ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteSocket (session, 30, kHip);
            session.Evaluate (30);
            Vector3 hand = Bone (session, HumanBodyBones.RightHand).position;
            Vector3 sword = Entry (session).editing.position;
            Assert.AreEqual (kHip, Entry (session).socket.definition.name);

            session.Evaluate (29);
            Assert.AreEqual (1, Entry (session).values.rightHold, "前のフレームは持っている（段差）");
            Assert.IsNull (Entry (session).socket, "前のフレームは差していない（段差）");
            AssertHolding (session, "差す前のキーの間");
            // 30 フレームで腰まで動くので、1 フレームの動きの大きさは許す
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), 0.05f, "物が跳ばない");
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.RightHand).position), 0.05f, "手が跳ばない");
        }

        [Test]
        public void LeavingSocket_ObjectStartsFromSocket_AndHandsHold ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            WriteSocket (session, 0, kHip);
            session.Evaluate (20);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (20);
            Vector3 sword = Entry (session).editing.position;

            WriteSocket (session, 40, HeldObjectDefinition.kNoSocketName);
            session.Evaluate (40);
            Assert.IsNull (Entry (session).socket, "抜いた");
            Assert.AreEqual (1, Entry (session).values.rightHold, "持っている手が無ければ握りで持つ");
            AssertHolding (session, "抜いたフレーム");
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kGripTolerance, "差していた場所から動き始める");

            session.Evaluate (39);
            Assert.IsNotNull (Entry (session).socket, "前のフレームは差している（段差）");
        }

        [Test]
        public void HandCanHoldObjectInSocket_ObjectStays ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            WriteSocket (session, 0, kHip);
            WriteSocket (session, 20, "", "Right");
            session.Evaluate (20);
            Assert.AreEqual (kHip, Entry (session).socket.definition.name, "差したまま");
            // 差している物は差す骨（腰）に固定（S102）。手が物へ伸びると、Free の腰は体の点の決まりどおり付いてくるので、物は腰と一緒に動く
            session.stage.heldObjects.GetSocketWorld (Entry (session).socket, out Vector3 socket, out Quaternion socketRotation);
            Assert.LessOrEqual (Vector3.Distance (socket, Entry (session).editing.position), kGripTolerance, "物は差す場所から動かない");
            Assert.LessOrEqual (Quaternion.Angle (socketRotation, Entry (session).editing.rotation), 0.1f, "向きも差す場所のまま");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), 0.05f, "手が柄へ行く");
        }

        [Test]
        public void InSocket_DraggingDoesNothing_AndPointsAreIgnored ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            WriteSocket (session, 0, kHip);
            Vector3 sword = Entry (session).editing.position;
            MoveObject (session, new Vector3 (0.3f, 0, 0));
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kJumpTolerance, "骨に固定で動かない");
        }

        [Test]
        public void RestSocket_ClipsWithoutKeys_SitInSocketWithFreeHands ()
        {
            HeldObjectDefinition definition = CreateObjectWithSocket ();
            definition.restSocket = kHip;
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            Assert.AreEqual (kHip, entry.socket.definition.name);
            Assert.AreEqual (0, entry.values.rightHold, "手は持たない");
            Assert.AreEqual (0, entry.values.leftHold, "手は持たない");
        }

        [Test]
        public void NoSockets_NoSocketCurve ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (session.clip).Where (b => b.propertyName == HeldObjectControl.kSocketProperty), "差す場所の無い物のカーブの組は変えない");
        }

        [Test]
        public void KeysBeforeSocketWasAdded_AreNotInSocket ()
        {
            // 差す場所を足す前に打ったキーを、場所のカーブの無いキーとして作る
            HeldObjectDefinition definition = CreateObjectWithSocket ();
            PoseSession session = Open (definition);
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (60);
            Assert.IsNull (session.CommitPose ());
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve (RigPaths.ObjectSocket (kName, kHip), typeof (HeldObjectControl), HeldObjectControl.kSocketProperty);
            AnimationUtility.SetEditorCurve (session.clip, binding, null);
            session.stage.InvalidateClipCurves ();

            WriteSocket (session, 30, kHip);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (session.clip, binding);
            Assert.AreEqual (1, curve.length, "場所のカーブは差したキーだけ");
            session.Evaluate (45);
            Assert.AreEqual (kHip, Entry (session).socket.definition.name, "次のキーまでは差している");
            session.Evaluate (60);
            Assert.IsNull (Entry (session).socket, "後ろのキーは物の持ち方のまま");
            AssertHolding (session, "後ろのキー");
        }

        [Test]
        public void SocketsAreKeyedByName_ReorderingKeepsKeys ()
        {
            HeldObjectDefinition definition = CreateObjectWithSocket ();
            definition.sockets.Add (new HeldObjectSocket { name = "Back", bone = HumanBodyBones.Chest, position = new Vector3 (0, 0, -0.15f) });
            PoseSession session = Open (definition);
            session.Evaluate (0);
            WriteSocket (session, 0, kHip);
            AnimationClip clip = session.clip;

            definition.sockets.Reverse ();
            PoseSession reordered = Open (definition);
            reordered.clip = clip;
            reordered.Evaluate (0);
            Assert.AreEqual (kHip, Entry (reordered).socket.definition.name, "並べ替えてもキーの意味は変わらない");
        }

        [Test]
        public void SocketOutOfReach_ObjectGoesExactlyThere_AndGapIsReported ()
        {
            HeldObjectDefinition definition = CreateObject ();
            // 腕より遠い所
            definition.sockets.Add (new HeldObjectSocket { name = kHip, bone = HumanBodyBones.Hips, position = new Vector3 (0, 0, -3) });
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.BringTo (entry.sockets[0]);
            session.SolveAfterEdit ();
            Assert.Greater (entry.bringGap, 0.5f, "届かない差を知らせる");
            session.stage.heldObjects.GetSocketWorld (entry.sockets[0], out Vector3 socket, out Quaternion unused);
            Assert.LessOrEqual (Vector3.Distance (socket, entry.editing.position), kGripTolerance, "物は体へ寄らず、差す場所まで行く");

            PoseDocument document = new PoseDocument { frame = 0 };
            document.objects.Add (new PoseObject { name = kName, socket = kHip });
            Assert.IsNull (PoseDocument.Solve (session, document));
            CollectionAssert.Contains (PoseDocument.FindMovedObjects (document, PoseDocument.ReadCurrent (session), 0.01f), kName, "CLI の unreached に出る");
        }

        /// <summary>左手が、ずらした握りに付いているときの差（メートル）</summary>
        static float SlidGap (PoseSession session, float slide)
        {
            HeldObjectRig.Entry entry = Entry (session);
            Vector3 target = entry.editing.TransformPoint (entry.definition.leftGrip.position + entry.definition.gripAxis.normalized * slide);
            return Vector3.Distance (session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand).position, target);
        }

        static void WriteSlide (PoseSession session, int frame, float left)
        {
            PoseDocument document = new PoseDocument { frame = frame };
            document.objects.Add (new PoseObject { name = kName, slide = new List<PoseGripSlide> { new PoseGripSlide { hand = "Left", value = left } } });
            Assert.IsNull (PoseDocument.Write (session, document), "書ける");
        }

        [Test]
        public void GripSlide_HandFollowsAlongHandle_AndInterpolatesBetweenKeys ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteSlide (session, 30, 0.06f);

            session.Evaluate (30);
            Assert.AreEqual (0.06f, Entry (session).values.leftSlide, 1e-5f, "打った値");
            Assert.LessOrEqual (SlidGap (session, 0.06f), kGripTolerance, "手がずらした握りへ行く");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "もう片方の手は元の握り");

            session.Evaluate (15);
            float middle = Entry (session).values.leftSlide;
            Assert.AreEqual (0.03f, middle, 1e-4f, "キーの間は滑る");
            Assert.LessOrEqual (SlidGap (session, middle), kGripTolerance, "キーの間も手が握りに付く");

            session.Evaluate (0);
            Assert.AreEqual (0, Entry (session).values.leftSlide, 1e-5f, "カーブを足す前に打ったキーは 0 のまま");
            Assert.AreEqual (0, Entry (session).values.rightSlide, "もう片方の手はずれない");
        }

        [Test]
        public void GripSlide_NoSlide_NoCurve_AndKeysKeepValue ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (session.clip).Where (b => b.propertyName == HeldObjectControl.kLeftSlideProperty), "ずらしていないクリップのカーブの組は変えない");

            WriteSlide (session, 30, 0.06f);
            // 書かないキーは今の値のまま打つ
            PoseDocument next = new PoseDocument { frame = 50 };
            next.objects.Add (new PoseObject { name = kName });
            Assert.IsNull (PoseDocument.Write (session, next));
            session.Evaluate (50);
            Assert.AreEqual (0.06f, Entry (session).values.leftSlide, 1e-5f);
            PoseDocument read = PoseDocument.Read (session, 50, out string problem);
            Assert.IsNull (problem);
            Assert.AreEqual (0.06f, read.objects[0].slide[0].value, 1e-5f, "読み返せる");
        }

        [Test]
        public void GripSlide_MovingHeldHand_MovesObject_KeepingTheSlide ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteSlide (session, 30, -0.05f);
            session.Evaluate (30);
            MoveObject (session, new Vector3 (0.05f, 0.02f, 0));
            Assert.LessOrEqual (SlidGap (session, -0.05f), kGripTolerance, "物を動かしても、ずらした握りに付く");
        }

        [Test]
        public void GripSlide_UnknownHand_IsRefused ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            PoseDocument document = new PoseDocument { frame = 0 };
            document.objects.Add (new PoseObject { name = kName, slide = new List<PoseGripSlide> { new PoseGripSlide { hand = "Both", value = 0.1f } } });
            Assert.IsNotNull (PoseDocument.Write (session, document));
        }

        [Test]
        public void MeasuringKeyGaps_KeepsThisFramesHold ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            // 前後のキーで Locked の点があると、キーの間でキーの差を測り直す（全部のキーを当てる）
            Point (session, HumanBodyBones.LeftFoot).SetLocked (true);
            Assert.IsNull (session.CommitPose ());
            WriteSocket (session, 30, kHip);
            session.stage.InvalidateClipCurves ();

            session.Evaluate (15);
            Assert.IsNull (Entry (session).socket, "最後のキーの持ち方が残らない");
            Assert.AreEqual (1, Entry (session).values.rightHold, "最後のキーの持ち方が残らない");
            AssertHolding (session, "測り直した直後のフレーム");
        }

        [Test]
        public void UnknownSocket_IsRefused_AndDuplicateNamesAreReported ()
        {
            PoseSession session = Open (CreateObjectWithSocket ());
            session.Evaluate (0);
            PoseDocument document = new PoseDocument { frame = 0 };
            document.objects.Add (new PoseObject { name = kName, socket = "Back" });
            Assert.IsNotNull (PoseDocument.Write (session, document), "無い場所は断る");

            HeldObjectDefinition definition = CreateObjectWithSocket ();
            definition.sockets.Add (new HeldObjectSocket { name = kHip });
            Assert.IsNotEmpty (definition.Validate (), "同じ名前を知らせる");
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
        public void GrabbingHeavyPoint_PullsBody_AndHandsKeepHolding ()
        {
            // S102: 物は持つ手の骨の子なので、重い点を引くと体の点と同じく手・腕・体が付いてくる。
            // 物の向きは物の点の向きを留めていなければ腕で決まり、届かない所まで引くと腕と一直線になるよう回る（S91e の「物は回らない」ではない）
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            Vector3 hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position;
            BodyPointTarget center = ObjectPoint (session, kCenter);
            Vector3 target = center.framePosition + new Vector3 (0, 0, 0.5f);
            center.MoveTo (target);
            session.SolveAfterEdit ();
            Assert.Less (Vector3.Distance (OnObject (session, kCenterOffset), target), 0.25f, "重心は目標へ寄る");
            Assert.Greater (Vector3.Distance (session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position, hips), 0.01f, "重い点は体を引く");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.LeftHand), kGripTolerance, "左手は柄に付く");
        }

        [Test]
        public void GrabbingLightPointOnHandleAxis_TurnsObjectAtTheWrist_AndLeavesBody ()
        {
            // S102: 物の上の点は体の点と同じ決まり。重さ 0 の点は、重い点（Center）のある骨では向けるだけの点で、ほかの骨（腕・体）を引かない。
            // 物は手に対して柄の軸まわりにしか回れないので、向けきれない分は親の手首を回して向ける（11-07 ユーザー決定「1」）。柄の軸の上の点（Tip）を引くと、手首が曲がって物が向きを変える
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            // 比べる基準は、何も動かさずに解いた姿勢（キーの無いクリップは、キーのフレームの手足だけの解き方と、動かした後の全身の解き方で腰が少し違う）
            session.SolveAfterEdit ();
            Vector3 hips = session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position;
            Quaternion rotation = Entry (session).editing.rotation;
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.MoveTo (tip.framePosition + new Vector3 (0.15f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Angle (Entry (session).editing.rotation * Vector3.up, rotation * Vector3.up), 3f, "柄の軸が引いた方へ向く");
            Assert.Less (Vector3.Distance (session.stage.editingRig.GetEditingBone (HumanBodyBones.Hips).position, hips), 1e-4f, "軽い点は腰を引かない");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.LeftHand), kGripTolerance, "左手は柄に付く");
        }

        [Test]
        public void LockedTip_IsOnlyAnAimPoint_HeavyPointStillMovesObject ()
        {
            // S102: 物の上の点は体の点と同じ決まり。重さ 0 の点は、重い点のある骨では向けるだけの点なので、Lock にしても場所は留めない
            // （S91e では物を物の点で決めていたので、Lock の先は動かなかった）。重い点を動かせば物は付いてくる
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.SetState (BodyPointState.Locked);
            session.SolveAfterEdit ();
            BodyPointTarget center = ObjectPoint (session, kCenter);
            Vector3 target = center.framePosition + new Vector3 (0.05f, 0, 0);
            center.MoveTo (target);
            session.SolveAfterEdit ();
            Assert.Less (Vector3.Distance (OnObject (session, kCenterOffset), target), 0.002f, "重心は目標へ行く");
            AssertHolding (session, "動かした後も持っている");
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
            // 届かないときは物と腕を 1 つの鎖で解くので、手は柄のまわりで回った所を握ることがある（S101）。柄に沿った場所と柄からの距離で見る
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "届かない所: 右手");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.LeftHand), kGripTolerance, "届かない所: 左手");
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
        public void GrabAtCurrent_HoldsWhereTheHandIs_AndFollowsTheObject ()
        {
            // S99: 持つボタン（SetObjectHold）と同じ経路。押した瞬間の手首の場所と向きを、物から見た握りにして拘束する。
            // 枠がルートの物は体が動いても動かないので、持ってもワールドで手が留まる
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            Vector3 hand = GrabMovedLeftHand (session, out Quaternion turn);
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.LeftHand).position), kGripTolerance, "持っても手は動かない");
            // 持っている手の点もふつうの点なので（S102）、持った後はキーの無い腕から解き直す。手の Pin の点は場所だけなので、向きは 1.0° ほど動く（11-07 実測）
            Assert.LessOrEqual (Quaternion.Angle (turn, Bone (session, HumanBodyBones.LeftHand).rotation), 1.5f, "向きもほとんど動かない");
            Assert.Greater (GripGap (session, HumanBodyBones.LeftHand), 0.03f, "キャラの設定の握りへ動かない");
            Assert.IsNull (session.CommitPose ());

            session.Evaluate (30);
            Assert.IsTrue (Entry (session).left.gripKeyed, "握りはキーから戻る");
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.LeftHand).position), kGripTolerance, "キーから戻った手");
            Vector3 local = Entry (session).editing.InverseTransformPoint (Bone (session, HumanBodyBones.LeftHand).position);
            MoveObject (session, new Vector3 (0.03f, 0.02f, 0));
            Assert.LessOrEqual (Vector3.Distance (local, Entry (session).editing.InverseTransformPoint (Bone (session, HumanBodyBones.LeftHand).position)), kGripTolerance, "物を動かしても握った所のまま付いてくる");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手はキャラの設定の握りのまま");
            session.Evaluate (15);
            Assert.IsFalse (Entry (session).left.gripKeyed, "持つ前のキーの間は、キーで持った握りではない（段で切り替わる）");
        }

        [Test]
        public void GrabAtCurrent_ChestFrame_KeepsTheGripOnTheObject ()
        {
            // 枠が胸の物は、手の Pin（ワールドに留める）が物への拘束に変わると胸が少し戻り、物と手が一緒に動く。握った所は物の上で変わらない
            PoseSession session = Open (CreateObject ());
            GrabMovedLeftHand (session, out Quaternion _);
            Vector3 grip = Entry (session).editing.TransformPoint (Entry (session).left.heldGripPosition);
            Assert.LessOrEqual (Vector3.Distance (grip, Bone (session, HumanBodyBones.LeftHand).position), kGripTolerance, "押した瞬間の握りに付く");
            Assert.Greater (GripGap (session, HumanBodyBones.LeftHand), 0.03f, "キャラの設定の握りへ動かない");
        }

        [Test]
        public void HeldHand_TwistsFreelyAroundTheGripAxis ()
        {
            // 11-05: 握りは柄の軸まわりにだけ自由。物を柄の軸まわりに回すと柄が手の中で回り、手首は一緒に回らない。
            // 柄は手に対して同じ所を通る（向きだけ自由にして場所を物に留めると、手首の点が柄のまわりを回って、柄が手の別の所を通った）
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            Transform hand = Bone (session, HumanBodyBones.RightHand);
            Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Vector3 onHand = Quaternion.Inverse (hand.rotation) * axis;
            Vector3 line = HandleOnHand (entry, hand);
            Quaternion before = hand.rotation;

            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry.DragTo (position, Quaternion.AngleAxis (40, axis) * rotation);
            session.SolveAfterEdit ();
            Vector3 turned = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Assert.LessOrEqual (Vector3.Angle (turned, axis), 0.5f, "物は柄の軸まわりに回った");
            Assert.Greater (Quaternion.Angle (Quaternion.AngleAxis (40, axis) * before, hand.rotation), 20f, "手首は物と一緒に回らない");
            Assert.LessOrEqual (Vector3.Angle (hand.rotation * onHand, turned), 0.5f, "柄の軸は手に対して変わらない");
            Assert.LessOrEqual (Vector3.Distance (line, HandleOnHand (entry, hand)), kGripTolerance, "柄は手の同じ所を通る");

            // 柄の軸を傾けると、手も一緒に傾く
            entry.GetWorldPose (out position, out rotation);
            entry.DragTo (position, Quaternion.AngleAxis (25, Vector3.forward) * rotation);
            session.SolveAfterEdit ();
            turned = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Assert.Greater (Vector3.Angle (turned, axis), 20f, "柄の軸が傾いた");
            Assert.LessOrEqual (Vector3.Angle (hand.rotation * onHand, turned), 0.5f, "手も柄の軸に合わせて傾く");
            Assert.LessOrEqual (Vector3.Distance (line, HandleOnHand (entry, hand)), kGripTolerance, "傾けても柄は手の同じ所を通る");
        }

        [Test]
        public void TurnBeyondTheWrist_ObjectYields_HandKeepsTheGrip ()
        {
            // S101 の A: 腕で作れない向きへ物を回すと、手首は制限で止まり、物の方が手の作れた向きまで譲る（柄は手の同じ所を通り、柄の軸も手に合う）。
            // 前は場所だけ寄せたので、手の向きが握りから外れたまま残った（腕のデバッグ用キャラで 38.6°）。
            // 腕のひねり（体の右の軸 x）を上腕・前腕で止め、手首のひねりを ±3°（遊び 15°）にして、物を前腕の向きのまわりに 60° 回す
            foreach (HumanBodyBones arm in new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm }) {
                definition_.FindFkControl (arm).limit.x.mode = EditRigDefinition.LimitMode.Locked;
            }
            EditRigDefinition.JointLimit wrist = definition_.FindFkControl (HumanBodyBones.RightHand).limit;
            wrist.x.mode = EditRigDefinition.LimitMode.Range;
            wrist.x.min = -3;
            wrist.x.max = 3;
            wrist.slack = 15;
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            session.SolveAfterEdit ();
            Transform hand = Bone (session, HumanBodyBones.RightHand);
            Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Vector3 onHand = Quaternion.Inverse (hand.rotation) * axis;
            Vector3 line = HandleOnHand (entry, hand);

            // 前腕の向きのまわり（手首のひねり）に回す
            Vector3 forearm = (hand.position - Bone (session, HumanBodyBones.RightLowerArm).position).normalized;
            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            Quaternion dragged = Quaternion.AngleAxis (60, forearm) * rotation;
            entry.DragTo (position, dragged);
            session.SolveAfterEdit ();
            Vector3 turned = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Assert.Greater (Vector3.Angle (dragged * entry.definition.gripAxis.normalized, turned), 20f, "物は腕で作れる向きまで譲る");
            Assert.Greater (Vector3.Angle (axis, turned), 3f, "物は少しは回る");
            Assert.LessOrEqual (Vector3.Angle (hand.rotation * onHand, turned), 0.5f, "柄の軸は手に合う");
            Assert.LessOrEqual (Vector3.Distance (line, HandleOnHand (entry, hand)), kGripTolerance, "柄は手の同じ所を通る");
        }

        [Test]
        public void TwoHands_TurnBeyondTheWrists_BothHandsKeepTheGrips ()
        {
            // 腕のひねりを止め、手首のひねりを狭くした定義で両手の物を回しても、両手とも柄に付く（S101 の B。S102 では物を右手の子・左手を輪の関節にして解く）。
            // いちばん届いていない手へ物を合わせ直すだけ（S101 の A）だと、片方の手がぴったり、もう片方が最大 78mm・33° 外れた
            definition_.fullBody.hipsPin = 1;
            foreach (HumanBodyBones arm in new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm }) {
                definition_.FindFkControl (arm).limit.x.mode = EditRigDefinition.LimitMode.Locked;
            }
            foreach (HumanBodyBones hand in new[] { HumanBodyBones.RightHand, HumanBodyBones.LeftHand }) {
                EditRigDefinition.JointLimit wrist = definition_.FindFkControl (hand).limit;
                wrist.x.mode = EditRigDefinition.LimitMode.Range;
                wrist.x.min = -3;
                wrist.x.max = 3;
                wrist.slack = 15;
            }
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            foreach (Vector3 local in new[] { Vector3.right, Vector3.forward }) {
                entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
                Quaternion dragged = rotation * Quaternion.AngleAxis (60, local);
                entry.DragTo (position, dragged);
                session.SolveAfterEdit ();
                Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, local + ": 右手は握りに付く");
                Assert.LessOrEqual (HandleGap (session, HumanBodyBones.LeftHand), kGripTolerance, local + ": 左手は握りに付く");
                // S102 からは、両手とも柄の軸まわりに回れるので、この向きは腕で作れ、物はほとんど譲らない（S101 では 1 回目に 5° より多く譲った）
            }
        }

        /// <summary>柄の軸の線（物の原点を通る）の、手首にいちばん近い所を手首から見た場所</summary>
        static Vector3 HandleOnHand (HeldObjectRig.Entry entry, Transform hand)
        {
            Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Vector3 origin = entry.editing.position;
            Vector3 nearest = origin + axis * Vector3.Dot (hand.position - origin, axis);
            return Quaternion.Inverse (hand.rotation) * (nearest - hand.position);
        }

        /// <summary>左手を放して動かし、その場で持つ（持つボタンと同じ順）。持つ前の手首の場所を返す</summary>
        static Vector3 GrabMovedLeftHand (PoseSession session, out Quaternion turn)
        {
            session.Evaluate (0);
            Entry (session).values.leftHold = 0;
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Point (session, HumanBodyBones.LeftHand).MoveBy (new Vector3 (0, 0.03f, 0.06f));
            session.SolveAfterEdit ();
            Vector3 hand = Bone (session, HumanBodyBones.LeftHand).position;
            turn = Bone (session, HumanBodyBones.LeftHand).rotation;
            Assert.IsTrue (session.stage.heldObjects.GrabAtCurrent (Entry (session).left, session.stage.fullBody));
            Entry (session).values.leftHold = 1;
            session.SolveAfterEdit ();
            return hand;
        }

        [Test]
        public void PoseDocument_GripCurrent_ReadsBack_AndUseSettingsReturns ()
        {
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 0, objects = new List<PoseObject> { new PoseObject { name = kName, hold = "Right" } } }));
            PoseDocument rest = PoseDocument.Read (session, 0, out string _);
            Vector3 moved = rest.Find ("LeftHand").position + new Vector3 (0, 0.03f, 0.06f);
            // 手を動かして、その手で握るのを 1 つの要求に書く
            PoseDocument grab = new PoseDocument { frame = 30 };
            grab.points.Add (new PosePoint { name = "LeftHand", state = "Pin", position = moved });
            grab.objects.Add (new PoseObject { name = kName, hold = "Both", grip = new List<PoseGrip> { new PoseGrip { hand = "Left", current = true } } });
            Assert.IsNull (PoseDocument.Write (session, grab), "書ける");

            PoseDocument read = PoseDocument.Read (session, 30, out string _);
            Assert.LessOrEqual (Vector3.Distance (moved, read.Find ("LeftHand").position), 0.002f, "動かした所で握る");
            PoseGrip grip = read.objects[0].grip.Single ();
            Assert.AreEqual ("Left", grip.hand, "キーで握った手だけ返す");
            Assert.Greater (GripGap (session, HumanBodyBones.LeftHand), 0.03f, "キャラの設定の握りではない");

            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 60, objects = new List<PoseObject> { new PoseObject { name = kName, grip = new List<PoseGrip> { new PoseGrip { hand = "Left", useSettings = true } } } } }));
            session.Evaluate (60);
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.LeftHand), kGripTolerance, "キャラの設定の握りへ戻る");
            Assert.IsEmpty (PoseDocument.Read (session, 60, out string _).objects[0].grip, "設定の握りの手は返さない");
            StringAssert.Contains ("Up", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = kName, grip = new List<PoseGrip> { new PoseGrip { hand = "Up" } } } } }));
        }

        [Test]
        public void HeldHand_IsAnOrdinaryPoint_FreeObjectFollowsTheHand ()
        {
            // S102: 持っている手の点もふつうの点（11-07 ユーザー報告「右手で持つが有効だと、手首の制御点をいじっても手首の回転が弱くなる」→ 特別な扱いをやめた）。
            // 物は握りの関節で手に付き、Free の物は手に付いて動く
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            ObjectTarget (session).SetState (BodyPointState.Free);
            BodyPointTarget hand = Point (session, HumanBodyBones.RightHand);
            Assert.IsTrue (hand.held);
            Vector3 before = Entry (session).editing.position;
            hand.MoveBy (new Vector3 (0.05f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Distance (before, Entry (session).editing.position), 0.03f, "手を動かすと物が付いてくる");
            AssertHolding (session, "手を動かした後");

            Transform bone = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Quaternion turn = Quaternion.AngleAxis (30, Vector3.up);
            Quaternion wanted = turn * bone.rotation;
            hand.SpinRotateWorld (turn);
            session.SolveAfterEdit ();
            Assert.Less (Quaternion.Angle (wanted, bone.rotation), 1f, "手首の点を回すと、手首がそのとおり回る");
            AssertHolding (session, "手首を回した後");

            Assert.IsNull (session.CommitPose ());
            string path = RigPaths.BodyPoint (HumanBodyBones.RightHand.ToString ());
            Assert.IsNotEmpty (AnimationUtility.GetCurveBindings (session.clip).Where (b => b.path == path), "持っている手の点もキーを打つ");
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

        [Test]
        public void ObjectState_IsKeyed_AndFreeObjectFollowsTheBody ()
        {
            // S102: 持ち物の姿勢は全身 IK の点で、体の点と同じ状態を持つ（11-07 ユーザー指示「持ち物 Stick も制御点に」「体の点の決まりどおりに」）。
            // キーの無いクリップでは Pin。Free にすると物の姿勢に留まらず、体と手に付いて動く
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "キーの無いクリップでは Pin");
            ObjectTarget (session).SetState (BodyPointState.Free);
            Assert.IsNull (session.CommitPose ());
            Assert.IsTrue (AnimationUtility.GetCurveBindings (session.clip).Any (b => b.path == RigPaths.ObjectPose (kName)), "状態のキーを打つ");
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Free, ObjectTarget (session).state, "状態がキーから戻る");
            Vector3 before = Entry (session).editing.position;
            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.2f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Distance (before, Entry (session).editing.position), 0.1f, "Free の物は体に付いて動く");
            AssertHolding (session, "持ったまま");

            ObjectTarget (session).SetState (BodyPointState.Locked);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Locked, ObjectTarget (session).state, "Lock もキーから戻る");
        }

        [Test]
        public void ObjectWithoutStateKeys_PinsPositionOnly_SoMovingItTurnsThePinnedWrist ()
        {
            // S102: キーの無い物は、体の点と同じく位置だけの Pin（11-07 ユーザー決定）。手首を Pin にして物を動かすと、手首が回る
            // （11-07 ユーザー報告「手首を pin にとめて持ち物 stick を動かすと、手首の角度が固定された状態で動く。通常の IK 制御では回転するはず」）
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Transform wrist = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Quaternion before = wrist.rotation;
            MoveObject (session, new Vector3 (0, 0.1f, 0));
            Assert.Greater (Quaternion.Angle (before, wrist.rotation), 5f, "手首が回る");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
        }

        [Test]
        public void DraggingAimPoint_TurnsObjectAboutItsPinnedOrigin_OtherPinnedAimPointIsPulled ()
        {
            // S102: 物の姿勢の点を Pin（位置の強さ 1・重さ 1）にして、両端の重さ 0 の点（向けるだけの点）を Pin にしたまま片端を引くと、
            // 物の原点は動かず、物は原点まわりに回る。もう片端の Pin の点は引かれる（体の点と同じく、重さ 0 の点は Pin でも引かれる）。
            // 11-07 ユーザー報告「棒の端をドラッグすると、持ち物は位置は動かないはずなのに（Pin で重さ 1）動く」: 手首まわりに回していて原点が 7〜31mm 動き、
            // 前の端を引いても後ろの端の Pin の点が物を元の向きへ戻していた
            foreach (string grabbed in new[] { kTip, kPommel }) {
                PoseSession session = Open (false, CreateObjectWithPoints ());
                session.Evaluate (0);
                HeldObjectRig.Entry entry = Entry (session);
                entry.values.leftHold = 0;
                ObjectPoint (session, kTip).SetState (BodyPointState.Pin);
                ObjectPoint (session, kPommel).SetState (BodyPointState.Pin);
                session.SolveAfterEdit ();
                // 窓と同じく、状態を変えたらキーを打つ（打つまでは、状態を変えた点もつかんだ点のまま）
                Assert.IsNull (session.CommitPose ());
                session.Evaluate (0);
                Vector3 origin = entry.editing.position;
                Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis);
                BodyPointTarget point = ObjectPoint (session, grabbed);
                Vector3 start = point.framePosition;
                for (int step = 1; step <= 5; step++) {
                    // 前へ引く（横へは、テストの人型の手首の角度制限でほとんど回れない）
                    point.MoveTo (start + new Vector3 (0, 0, 0.01f * step));
                    session.SolveAfterEdit ();
                }
                Assert.Less (Vector3.Distance (origin, entry.editing.position), 1e-3f, grabbed + ": Pin の物の原点は動かない");
                Assert.Greater (Vector3.Angle (axis, entry.editing.TransformDirection (entry.definition.gripAxis)), 1f, grabbed + ": 物は原点まわりに回る");
                Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, grabbed + ": 右手は柄に付く");
            }
        }

        [Test]
        public void DraggingObjectInSmallSteps_DoesNotRollItInTheHand ()
        {
            // S102: 向きを留めていない物を、窓のドラッグと同じく少しずつ（毎回いまの向きのまま）動かしても、手の中で柄の軸まわりに回らない
            // （11-07 ユーザー報告「持ち物 stick を動かすと、表示しているボーンが回転しまくる。見た目は回転していないのに」。丸い棒が手の中で ±170° 回った）
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            session.SolveAfterEdit ();
            Transform wrist = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Quaternion wristStart = wrist.rotation;
            Quaternion inHand = Quaternion.Inverse (wrist.rotation) * entry.editing.rotation;
            entry.GetWorldPose (out Vector3 start, out Quaternion unused);
            float worst = 0;
            for (int step = 1; step <= 30; step++) {
                float t = step * 0.1f;
                entry.GetWorldPose (out Vector3 unusedPosition, out Quaternion rotation);
                entry.DragTo (start + new Vector3 (0.08f * Mathf.Sin (t), 0.05f * (1 - Mathf.Cos (t)), 0.04f * Mathf.Sin (t * 0.5f)), rotation);
                session.SolveAfterEdit ();
                worst = Mathf.Max (worst, Quaternion.Angle (inHand, Quaternion.Inverse (wrist.rotation) * entry.editing.rotation));
            }
            Assert.Greater (Quaternion.Angle (wristStart, wrist.rotation), 5f, "手首はキーの向きから回った（回った分だけ、前は物が手の中で回った）");
            Assert.LessOrEqual (worst, 1f, "手の中で回らない");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
        }

        [Test]
        public void DraggingHoldingHand_DoesNotSpinObjectInTheHand_OrRewriteItsKey ()
        {
            // S102: 物の上の点を Pin にした物を持った手首を少しずつ引いても、物は手の中で柄の軸まわりに回らず、物のキーも離すまで変わらない。
            // 解くたびに解いた物の姿勢をキーに残していて、次に解くときの元の姿勢が前の答えになり、物が手の中で回り続けた
            // （11-08 ユーザー報告「手首を動かしていると、たまに棒の姿勢がワープする」）
            PoseSession session = Open (false, CreateObjectWithPoints ());
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            ObjectTarget (session).SetState (BodyPointState.Free);
            ObjectPoint (session, kName + ".Tip").SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Transform wrist = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Quaternion inHand = Quaternion.Inverse (wrist.rotation) * entry.editing.rotation;
            Quaternion key = entry.control.localRotation;
            BodyPointTarget hand = Point (session, HumanBodyBones.RightHand);
            Vector3 from = hand.framePosition;
            float worst = 0;
            for (int step = 1; step <= 30; step++) {
                float t = step * 0.1f;
                hand.MoveTo (from + new Vector3 (0.06f * Mathf.Sin (t), 0.04f * (1 - Mathf.Cos (t)), 0.03f * Mathf.Sin (t * 0.5f)));
                session.SolveAfterEdit ();
                worst = Mathf.Max (worst, Quaternion.Angle (inHand, Quaternion.Inverse (wrist.rotation) * entry.editing.rotation));
                Assert.LessOrEqual (Quaternion.Angle (key, entry.control.localRotation), 0.01f, "引いている間は物のキーを変えない（step " + step + "）");
            }
            Assert.LessOrEqual (worst, 1f, "手の中で回らない");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
        }

        [Test]
        public void DraggingHoldingHand_PinnedObjectStaysAsFarAsItCan_WithoutJumping ()
        {
            // S88 / S102: つかんだ点と離れの変わらない重さ 1 の Pin の点（持った物の原点の点など）は、体の点の Pin と同じく、留まれる分は留まり、届かない分だけ引かれる。
            // 手足の長さで運ぶ量を決める形では、こうした点は必ず付いて動くとみなす一方、1 回目に解いたとき手首を大きく回して届くと留まり、
            // 手首を引くたびに物が 2 つの姿勢の間を跳んだ（11-07 ユーザー報告「手首を動かしていると、たまに棒の姿勢がワープする」）
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "物は Pin");
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            BodyPointTarget hand = Point (session, HumanBodyBones.RightHand);
            Vector3 from = hand.framePosition;
            Vector3 origin = entry.editing.position;
            Vector3 previous = origin;
            float worst = 0;
            for (int step = 1; step <= 40; step++) {
                float t = step * 0.06f;
                hand.MoveTo (from + new Vector3 (0.12f * Mathf.Sin (t), 0.08f * (1 - Mathf.Cos (t)), 0.1f * Mathf.Sin (t * 0.7f)));
                session.SolveAfterEdit ();
                worst = Mathf.Max (worst, Vector3.Distance (previous, entry.editing.position));
                previous = entry.editing.position;
            }
            Assert.LessOrEqual (worst, 0.02f, "物の原点は跳ばない（手首は 1 回 1cm ほど動く）");
            Assert.Less (Vector3.Distance (origin, entry.editing.position), Vector3.Distance (from, hand.framePosition), "物の原点は、手首ほどは動かない（留まれる分は留まる）");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
        }

        [Test]
        public void DraggingHoldingHand_ThenKeying_KeepsHandAndObjectWhereTheyWereDragged ()
        {
            // S102: 物を持った手首を引いて離しても（キーを打ってクリップから当て直しても）、手首と物は引いた所に残る。
            // 物の値は、体の点と同じく、キーを打つときに解いた物の骨の姿勢へ合わせ直す
            // （11-07 ユーザー報告「手首を動かすと、きちんと動くのだが、ドラッグを離すと元の位置に戻っている」。元の場所の物の Pin が手首を引き戻した）
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            entry.values.leftHold = 0;
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "物は Pin");
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Transform wrist = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Vector3 start = wrist.position;
            BodyPointTarget hand = Point (session, HumanBodyBones.RightHand);
            Vector3 from = hand.framePosition;
            for (int step = 1; step <= 5; step++) {
                hand.MoveTo (from + new Vector3 (0.02f * step, 0.01f * step, 0));
                session.SolveAfterEdit ();
            }
            Vector3 dragged = wrist.position;
            Vector3 objectDragged = entry.editing.position;
            Assert.Greater (Vector3.Distance (start, dragged), 0.03f, "手首は引いた方へ動く");

            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.LessOrEqual (Vector3.Distance (dragged, wrist.position), 0.002f, "キーを打って当て直しても、手首は戻らない");
            Assert.LessOrEqual (Vector3.Distance (objectDragged, entry.editing.position), 0.002f, "物も戻らない");
            Assert.LessOrEqual (HandleGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は柄に付く");
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
            // 両手の物を、腕を伸ばした姿勢（キーの無いクリップ）から置くと、左手を合わせきれない分だけ物が手の方へ寄る（S102。手は握りから外さない。11-07 実測で 2.0mm）
            Assert.LessOrEqual (Vector3.Distance (chestSpot, item.position), 0.003f, "書いた場所");
            // 向きも同じく、手首の角度制限が勝つ分だけずれる（11-07 実測で 0.95°）
            Assert.LessOrEqual (Vector3.Angle (Vector3.up, item.aim.forward), 1.5f, "物の +Z の向き");
            Assert.LessOrEqual (Vector3.Angle (Vector3.back, item.aim.up), 1.5f, "物の +Y の向き");
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
            // 柄の軸（+Y）まわりは手首に任せるので、軸を傾ける向きに回す
            Quaternion turned = Quaternion.Euler (15, 0, 0) * rotation;
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
