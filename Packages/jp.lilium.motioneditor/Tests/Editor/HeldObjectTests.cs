using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 持ち物（S91・S104）。物の姿勢をキーに持ち、付けている骨はどのフレームでも付けた位置に付く。
    /// 付ける先はリグ定義の一覧（既定は右手・左手・腰）で、どの先も同じ決まりで解く
    /// </summary>
    public class HeldObjectTests : RigTestFixture
    {
        /// <summary>握りの残差の上限（メートル）</summary>
        const float kGripTolerance = 0.001f;
        /// <summary>切り替えのフレームで跳ばない差の上限（メートル）</summary>
        const float kJumpTolerance = 1e-4f;
        const string kName = "Sword";
        const string kRight = EditRigDefinition.kRightHandAttach;
        const string kLeft = EditRigDefinition.kLeftHandAttach;
        const string kHips = EditRigDefinition.kHipsAttach;

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            RigTestFixture.UseFreeDefaults (definition_);
            // キーの無いクリップでは両手で持つ（既定の定義は腰。腰で持つテストは AttachAtRest で変える）
            AttachAtRest (kRight, kLeft);
        }

        /// <summary>リグ定義で、キーの無いクリップで付けておく先をこれだけにする</summary>
        void AttachAtRest (params string[] points)
        {
            foreach (EditRigDefinition.AttachPoint point in definition_.attachPoints) point.attachedAtRest = System.Array.IndexOf (points, point.name) >= 0;
        }

        [TearDown]
        public void TearDown ()
        {
            DestroyCreated ();
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
                attachments = new List<HeldObjectAttachment> {
                    new HeldObjectAttachment { point = kRight, position = new Vector3 (0, 0.08f, -0.05f), rotation = Vector3.zero },
                    new HeldObjectAttachment { point = kLeft, position = new Vector3 (0, -0.08f, -0.05f), rotation = Vector3.zero },
                },
            };
        }

        PoseSession Open (params HeldObjectDefinition[] objects)
        {
            return Open (true, objects);
        }

        /// <param name="keyObjectRotation">
        /// 物の姿勢の点を、位置も向きも固定した Pin にするキーをクリップの頭に打つ（デモのクリップと同じ。キーの無い物は Free なので、
        /// 物の位置と向きのキーが効かない）。false ならキーの無い物
        /// </param>
        PoseSession Open (bool keyObjectRotation, params HeldObjectDefinition[] objects)
        {
            PoseSession session = OpenSession (TestSkeleton.CreateHumanoid (created_, true, 1), new RigSetup (definition_, objects));
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

        static HeldObjectRig.Entry Entry (PoseSession session)
        {
            return session.stage.heldObjects.entries[0];
        }

        static HeldObjectRig.Attachment Attach (PoseSession session, string point)
        {
            return Entry (session).FindAttachment (point);
        }

        /// <summary>付けているかの値だけを変える（付けた位置は今のまま）</summary>
        static void SetAttached (PoseSession session, string point, bool attached)
        {
            Attach (session, point).values.attached = attached ? 1 : 0;
        }

        /// <summary>物の設定の、その手の決まった位置（物のローカル）</summary>
        static Vector3 SettingsGrip (HeldObjectRig.Entry entry, HumanBodyBones hand)
        {
            return entry.definition.FindAttachment (hand == HumanBodyBones.RightHand ? kRight : kLeft).position;
        }

        /// <summary>手首の骨と、握りの場所の差（メートル）</summary>
        static float GripGap (PoseSession session, HumanBodyBones hand)
        {
            HeldObjectRig.Entry entry = Entry (session);
            Vector3 target = entry.editing.TransformPoint (SettingsGrip (entry, hand));
            return Vector3.Distance (session.stage.editingRig.GetEditingBone (hand).position, target);
        }

        /// <summary>
        /// 手首と、キャラの設定の握りの差のうち、柄の軸まわりの回りに依らない分（柄に沿った場所の差と、柄からの距離の差の大きい方。メートル）。
        /// 握りは柄の軸まわりに自由なので（S100）、物と腕を鎖で解くと、手は柄のまわりで回った所を握る
        /// </summary>
        static float HandleGap (PoseSession session, HumanBodyBones hand)
        {
            HeldObjectRig.Entry entry = Entry (session);
            Vector3 grip = SettingsGrip (entry, hand);
            Vector3 axis = entry.definition.gripAxis.normalized;
            Vector3 at = entry.editing.InverseTransformPoint (session.stage.editingRig.GetEditingBone (hand).position);
            float along = Mathf.Abs (Vector3.Dot (at - grip, axis));
            float radial = Mathf.Abs (Vector3.ProjectOnPlane (at, axis).magnitude - Vector3.ProjectOnPlane (grip, axis).magnitude);
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

        // ---- S91f・S104 付け替え（片手・両手・腰） ----

        /// <summary>腰に差す決まった位置を持つ物。両手の届く腰の前（届かない場所は SnapOutOfReach_ のテスト）</summary>
        HeldObjectDefinition CreateObjectWithHips (Vector3? at = null)
        {
            HeldObjectDefinition definition = CreateObject ();
            // 物の場所は腰から体の向きで at（向きは腰の基準のまま）。付ける先の枠（腰の付け根・体の向き）から物を見た所の逆を、物から見た枠にする
            definition.attachments.Add (new HeldObjectAttachment { point = kHips, position = -(at ?? new Vector3 (0, 0.2f, 0.25f)), rotation = Vector3.zero });
            return definition;
        }

        static Transform Bone (PoseSession session, HumanBodyBones bone)
        {
            return session.stage.editingRig.GetEditingBone (bone);
        }

        /// <summary>今のフレームで付け替えて（CLI と同じ経路）キーを打つ。snap なら先に決まった位置へ寄せる</summary>
        static void WriteAttach (PoseSession session, int frame, string attach, string snap = null, List<PoseGrip> grip = null)
        {
            PoseDocument document = new PoseDocument { frame = frame };
            document.objects.Add (new PoseObject { name = kName, attach = attach, snap = snap, grip = grip });
            Assert.IsNull (PoseDocument.Write (session, document), "書ける");
        }

        /// <summary>腰へ寄せてから腰だけに付ける（差す）</summary>
        static void WriteToHips (PoseSession session, int frame)
        {
            WriteAttach (session, frame, kHips, kHips);
        }

        /// <summary>両手を物の設定の位置で付ける</summary>
        static List<PoseGrip> SettingsGrips ()
        {
            return new List<PoseGrip> { new PoseGrip { point = kRight, useSettings = true }, new PoseGrip { point = kLeft, useSettings = true } };
        }

        [Test]
        public void OneHandThenTwoHands_ByAttachKeys ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteAttach (session, 20, kRight);
            WriteAttach (session, 40, kRight + "," + kLeft, null, SettingsGrips ());

            session.Evaluate (30);
            Assert.IsFalse (Attach (session, kLeft).attached, "片手");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は握りに付く");
            session.Evaluate (40);
            AssertHolding (session, "両手に戻した");
        }

        [Test]
        public void OnHips_ObjectStaysOnBone_WhenHipsMove ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            WriteToHips (session, 0);
            HeldObjectRig.Entry entry = Entry (session);
            Assert.IsFalse (Attach (session, kLeft).attached, "両手を外す");
            Assert.IsFalse (Attach (session, kRight).attached, "両手を外す");
            Assert.IsTrue (Attach (session, kHips).attached, "腰に付ける");
            Transform hips = Bone (session, HumanBodyBones.Hips);
            Vector3 local = hips.InverseTransformPoint (entry.editing.position);

            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.2f, -0.1f, 0.1f));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (local, hips.InverseTransformPoint (entry.editing.position)), kJumpTolerance, "腰に付いて動く");
        }

        [Test]
        public void OnHips_MovingObjectPullsTheHips ()
        {
            // S104: どの付ける先も同じ決まり。腰に付けた物を動かすと、手に付けた物と同じく付けた骨（腰）が引かれる（S91f の「差した物は動かない」はやめた）
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            WriteToHips (session, 0);
            session.Evaluate (0);
            Transform hips = Bone (session, HumanBodyBones.Hips);
            Vector3 before = hips.position;
            Vector3 local = hips.InverseTransformPoint (Entry (session).editing.position);
            MoveObject (session, new Vector3 (0.1f, 0, 0));
            Assert.Greater (Vector3.Distance (before, hips.position), 0.05f, "腰が引かれる");
            Assert.LessOrEqual (Vector3.Distance (local, hips.InverseTransformPoint (Entry (session).editing.position)), kGripTolerance, "物は腰に付いたまま");
        }

        [Test]
        public void SnappingToHips_HandsDoNotJump_AndObjectDoesNotJump ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteToHips (session, 30);
            session.Evaluate (30);
            Vector3 hand = Bone (session, HumanBodyBones.RightHand).position;
            Vector3 sword = Entry (session).editing.position;
            Assert.IsTrue (Attach (session, kHips).attached);

            session.Evaluate (29);
            Assert.IsTrue (Attach (session, kRight).attached, "前のフレームは手に付いている（段差）");
            Assert.IsFalse (Attach (session, kHips).attached, "前のフレームは腰に付いていない（段差）");
            AssertHolding (session, "付け替える前のキーの間");
            // 30 フレームで腰まで動くので、1 フレームの動きの大きさは許す
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), 0.05f, "物が跳ばない");
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.RightHand).position), 0.05f, "手が跳ばない");
        }

        [Test]
        public void LeavingHips_ObjectStartsFromHips_AndHandsHold ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            WriteToHips (session, 0);
            session.Evaluate (20);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (20);
            Vector3 sword = Entry (session).editing.position;

            WriteAttach (session, 40, kRight + "," + kLeft, null, SettingsGrips ());
            session.Evaluate (40);
            Assert.IsFalse (Attach (session, kHips).attached, "腰から外した");
            Assert.IsTrue (Attach (session, kRight).attached, "手に付けた");
            AssertHolding (session, "外したフレーム");
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kGripTolerance, "腰の場所から動き始める");

            session.Evaluate (39);
            Assert.IsTrue (Attach (session, kHips).attached, "前のフレームは腰に付いている（段差）");
        }

        [Test]
        public void HandCanHoldObjectOnHips_ObjectStaysOnHips ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            WriteToHips (session, 0);
            WriteAttach (session, 20, kHips + "," + kRight, null, new List<PoseGrip> { new PoseGrip { point = kRight, useSettings = true } });
            session.Evaluate (20);
            Assert.IsTrue (Attach (session, kHips).attached, "腰に付けたまま");
            // 回らない先（腰）が骨の木の親で、手は輪の関節で物に付く
            session.stage.heldObjects.GetSettingsWorld (Attach (session, kHips), out Vector3 onHips, out Quaternion onHipsRotation);
            Assert.LessOrEqual (Vector3.Distance (onHips, Entry (session).editing.position), kGripTolerance, "物は腰の決まった位置から動かない");
            Assert.LessOrEqual (Quaternion.Angle (onHipsRotation, Entry (session).editing.rotation), 0.1f, "向きも腰の決まった位置のまま");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), 0.05f, "手が柄へ行く");
        }

        [Test]
        public void DefaultDefinition_ClipsWithoutKeys_ObjectOnHips_CharacterInTPose ()
        {
            // 11-09 ユーザー指示「ディフォルトは腰。あとキャラクターはTポーズにする」。既定の定義のまま（手は付けない）
            definition_.attachPoints = EditRigDefinition.CreateDefaultAttachPoints ();
            CollectionAssert.AreEqual (new[] { kHips }, definition_.attachPoints.Where (p => p.attachedAtRest).Select (p => p.name).ToArray (), "既定は腰だけ");
            PoseSession session = Open (CreateObjectWithHips ());
            HeldObjectRig.Entry entry = Entry (session);
            Transform root = session.stage.editingRoot;
            session.stage.editingRig.ResetBonesToRest ();
            Dictionary<Transform, (Vector3, Quaternion)> rest = new Dictionary<Transform, (Vector3, Quaternion)> ();
            foreach (Transform bone in root.GetComponentsInChildren<Transform> (true)) {
                if (!bone.IsChildOf (entry.editing) && !bone.IsChildOf (session.stage.editingRig.controlsRoot)) rest[bone] = (bone.localPosition, bone.localRotation);
            }
            session.Evaluate (0);
            Assert.IsTrue (Attach (session, kHips).attached, "腰に付ける");
            Assert.IsFalse (Attach (session, kRight).attached, "手は付けない");
            Assert.IsFalse (Attach (session, kLeft).attached, "手は付けない");
            foreach (KeyValuePair<Transform, (Vector3, Quaternion)> pair in rest) {
                Assert.LessOrEqual (Vector3.Distance (pair.Value.Item1, pair.Key.localPosition), 1e-4f, pair.Key.name + " の場所が T ポーズのまま");
                Assert.LessOrEqual (Quaternion.Angle (pair.Value.Item2, pair.Key.localRotation), 0.01f, pair.Key.name + " の向きが T ポーズのまま");
            }
            session.stage.heldObjects.GetSettingsWorld (Attach (session, kHips), out Vector3 onHips, out Quaternion unused);
            Assert.LessOrEqual (Vector3.Distance (onHips, entry.editing.position), kGripTolerance, "物は腰の決まった位置");
        }

        [Test]
        public void RestAttachment_ClipsWithoutKeys_SitOnHipsWithFreeHands ()
        {
            // キーの無いクリップで付けておく先はリグ定義の付ける先で決める（11-09 ユーザー指示「rig define に持ち位置のデフォルトを設定できるようにする。デフォルトは腰」）
            HeldObjectDefinition definition = CreateObjectWithHips ();
            AttachAtRest (kHips);
            PoseSession session = Open (definition);
            session.Evaluate (0);
            Assert.IsTrue (Attach (session, kHips).attached);
            Assert.IsFalse (Attach (session, kRight).attached, "手は付けない");
            Assert.IsFalse (Attach (session, kLeft).attached, "手は付けない");
            session.stage.heldObjects.GetSettingsWorld (Attach (session, kHips), out Vector3 onHips, out Quaternion unused);
            Assert.LessOrEqual (Vector3.Distance (onHips, Entry (session).editing.position), kGripTolerance, "腰の決まった位置");
        }

        [Test]
        public void AttachCurves_ArePerAttachPoint_GripCurvesOnlyWhenUsed ()
        {
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (session.clip);
            foreach (string point in new[] { kRight, kLeft, kHips }) {
                Assert.IsTrue (bindings.Any (b => b.path == RigPaths.ObjectAttach (kName, point) && b.propertyName == HeldObjectControl.kAttachedProperty), point + " の付けているかを打つ");
            }
            Assert.IsEmpty (bindings.Where (b => b.propertyName == HeldObjectControl.kGripKeyedProperty), "付けるボタンを使っていなければ位置のカーブは作らない");
        }

        [Test]
        public void KeysBeforeAttachPointWasAdded_AreNotAttached ()
        {
            // 付ける先を足す前に打ったキーを、その先のカーブの無いキーとして作る
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (60);
            Assert.IsNull (session.CommitPose ());
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve (RigPaths.ObjectAttach (kName, kHips), typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty);
            AnimationUtility.SetEditorCurve (session.clip, binding, null);
            session.stage.InvalidateClipCurves ();

            WriteToHips (session, 30);
            AnimationCurve curve = AnimationUtility.GetEditorCurve (session.clip, binding);
            Assert.AreEqual (1, curve.length, "腰のカーブは付けたキーだけ");
            session.Evaluate (45);
            Assert.IsTrue (Attach (session, kHips).attached, "次のキーまでは腰に付いている");
            session.Evaluate (60);
            Assert.IsFalse (Attach (session, kHips).attached, "後ろのキーは物の付け方のまま");
            AssertHolding (session, "後ろのキー");
        }

        [Test]
        public void AttachPointsAreKeyedByName_ReorderingKeepsKeys ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            WriteToHips (session, 0);
            AnimationClip clip = session.clip;

            definition_.attachPoints.Reverse ();
            PoseSession reordered = Open (CreateObjectWithHips ());
            reordered.clip = clip;
            reordered.Evaluate (0);
            Assert.IsTrue (Attach (reordered, kHips).attached, "並べ替えてもキーの意味は変わらない");
            Assert.IsFalse (Attach (reordered, kRight).attached, "並べ替えてもキーの意味は変わらない");
        }

        [Test]
        public void SnapOutOfReach_GapIsReported ()
        {
            // 腕より遠い所
            PoseSession session = Open (CreateObjectWithHips (new Vector3 (0, 0, -3)));
            session.Evaluate (0);
            Point (session, HumanBodyBones.Hips).SetLocked (true);
            HeldObjectRig.Entry entry = Entry (session);
            entry.SnapTo (Attach (session, kHips));
            session.SolveAfterEdit ();
            Assert.Greater (entry.snapGap, 0.5f, "届かない差を知らせる");

            PoseDocument document = new PoseDocument { frame = 0 };
            document.objects.Add (new PoseObject { name = kName, snap = kHips });
            Assert.IsNull (PoseDocument.Solve (session, document));
            CollectionAssert.Contains (PoseDocument.FindMovedObjects (document, PoseDocument.ReadCurrent (session), 0.01f), kName, "CLI の unreached に出る");
        }

        /// <summary>左手だけで持った物の右手を IK で動かし（Pin）、寄せる前の右手と物の姿勢を返す（寄せる先の右手は、キーの FK とは別の所にある）</summary>
        static void MoveRightHandAway (PoseSession session, Vector3 move, out Vector3 hand, out Vector3 settings, out Quaternion settingsRotation)
        {
            SetAttached (session, kRight, false);
            session.SolveAfterEdit ();
            Point (session, HumanBodyBones.RightHand).MoveBy (move);
            session.SolveAfterEdit ();
            hand = Bone (session, HumanBodyBones.RightHand).position;
            session.stage.heldObjects.GetSettingsWorld (Attach (session, kRight), out settings, out settingsRotation);
        }

        [Test]
        public void SnappingToHand_ObjectComesToHand_HandStays ()
        {
            // 11-09 ユーザー指示「寄せる ボタンは object 側が寄るように」。前は物の目標を解く前の右手（キーの FK）で決めていて、右手が 10cm のうち 9.6cm 物の方へ戻った
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            MoveRightHandAway (session, new Vector3 (0.1f, 0, 0), out Vector3 hand, out Vector3 settings, out Quaternion settingsRotation);
            HeldObjectRig.Entry entry = Entry (session);
            entry.SnapTo (Attach (session, kRight));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.RightHand).position), kGripTolerance, "右手は動かない");
            Assert.LessOrEqual (Vector3.Distance (settings, entry.editing.position), kGripTolerance, "物が右手の決まった位置へ来る");
            Assert.LessOrEqual (Quaternion.Angle (settingsRotation, entry.editing.rotation), 0.1f, "向きも右手の決まった位置");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.LeftHand), kGripTolerance, "付けている左手は付いてくる");
        }

        [Test]
        public void SnappingToHand_OtherHandOutOfReach_DoesNotPullTheHand ()
        {
            // 付けている左手が Pin で物まで届かなくても、物を左手の方へ戻さない（前は物と一緒に右手が左手の方へ 38cm 引かれた）
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Point (session, HumanBodyBones.LeftHand).SetState (BodyPointState.Pin);
            Point (session, HumanBodyBones.Hips).SetState (BodyPointState.Pin);
            MoveRightHandAway (session, new Vector3 (0.1f, 0, 0), out Vector3 hand, out Vector3 settings, out Quaternion unused);
            HeldObjectRig.Entry entry = Entry (session);
            entry.SnapTo (Attach (session, kRight));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.RightHand).position), kGripTolerance, "右手は動かない");
            Assert.LessOrEqual (Vector3.Distance (settings, entry.editing.position), kGripTolerance, "物が右手の決まった位置へ来る");
            Assert.Greater (entry.snapGap, 0.05f, "左手が届かない差を知らせる");
        }

        [Test]
        public void SnappingToHand_HeldAtAKeyedGrip_MovesOnlyTheObject ()
        {
            // 11-09 ユーザー報告「右手の寄せるを押すと届かないと出て姿勢が崩れる。この場合は剣が右手に移動するだけ」。
            // 離れた所で右手に付けた（キーで付けた位置）物を右手に寄せると、寄せた後もその位置が残り、右手がその位置まで引かれた
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            SetAttached (session, kLeft, false);
            SetAttached (session, kRight, false);
            session.SolveAfterEdit ();
            HeldObjectRig.Entry entry = Entry (session);
            entry.GetWorldPose (out Vector3 start, out Quaternion rotation);
            entry.DragTo (start + new Vector3 (0.3f, -0.4f, 0.2f), rotation);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            Vector3 hand = Bone (session, HumanBodyBones.RightHand).position;
            session.stage.heldObjects.SetAttached (Attach (session, kRight), true);
            session.SolveAfterEdit ();
            Assert.IsTrue (Attach (session, kRight).gripKeyed, "離れた所で付けた");
            Assert.IsNull (session.CommitPose ());

            session.stage.heldObjects.GetSettingsWorld (Attach (session, kRight), out Vector3 settings, out Quaternion unused);
            entry.SnapTo (Attach (session, kRight));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (entry.snapGap, kGripTolerance, "届かないと知らせない");
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.IsFalse (Attach (session, kRight).gripKeyed, "物の設定の位置で付く");
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.RightHand).position), kGripTolerance, "右手は動かない");
            Assert.LessOrEqual (Vector3.Distance (settings, entry.editing.position), kGripTolerance, "物が右手の決まった位置へ来る");
        }

        [Test]
        public void AttachingAtCurrent_ObjectAndBonesDoNotMove ()
        {
            // S104 の決めたこと 5: 付けるボタン（SetAttached）は押した瞬間の位置で付ける。物も骨も動かない
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            Vector3 sword = Entry (session).editing.position;
            Vector3 hips = Bone (session, HumanBodyBones.Hips).position;
            session.stage.heldObjects.SetAttached (Attach (session, kHips), true);
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kJumpTolerance, "物は動かない");
            Assert.LessOrEqual (Vector3.Distance (hips, Bone (session, HumanBodyBones.Hips).position), kJumpTolerance, "腰は動かない");
            AssertHolding (session, "腰にも付けた後");

            session.stage.heldObjects.SetAttached (Attach (session, kRight), false);
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kJumpTolerance, "外しても物は跳ばない");
        }

        [Test]
        public void MeasuringKeyGaps_KeepsThisFramesAttachments ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            // 前後のキーで Locked の点があると、キーの間でキーの差を測り直す（全部のキーを当てる）
            Point (session, HumanBodyBones.LeftFoot).SetLocked (true);
            Assert.IsNull (session.CommitPose ());
            WriteToHips (session, 30);
            session.stage.InvalidateClipCurves ();

            session.Evaluate (15);
            Assert.IsFalse (Attach (session, kHips).attached, "最後のキーの付け方が残らない");
            Assert.IsTrue (Attach (session, kRight).attached, "最後のキーの付け方が残らない");
            AssertHolding (session, "測り直した直後のフレーム");
        }

        [Test]
        public void UnknownAttachPoint_IsRefused_AndDuplicateNamesAreReported ()
        {
            PoseSession session = Open (CreateObjectWithHips ());
            session.Evaluate (0);
            foreach (PoseObject item in new[] { new PoseObject { name = kName, attach = "Back" }, new PoseObject { name = kName, snap = "Back" }, new PoseObject { name = kName, grip = new List<PoseGrip> { new PoseGrip { point = "Back" } } } }) {
                PoseDocument document = new PoseDocument { frame = 0 };
                document.objects.Add (item);
                StringAssert.Contains ("Back", PoseDocument.Write (session, document), "無い付ける先は断る");
            }

            HeldObjectDefinition definition = CreateObjectWithHips ();
            definition.attachments.Add (new HeldObjectAttachment { point = kHips });
            Assert.IsNotEmpty (definition.Validate (), "同じ名前を知らせる");
            definition_.attachPoints.Add (new EditRigDefinition.AttachPoint { name = kHips });
            Assert.IsNotEmpty (definition_.Validate (), "定義の同じ名前を知らせる");
        }

        [Test]
        public void MissingBone_AttachPointIsSkipped_WithNote ()
        {
            definition_.attachPoints.Add (new EditRigDefinition.AttachPoint { name = "Tail", bone = HumanBodyBones.LastBone });
            PoseSession session = Open (CreateObject ());
            Assert.IsNull (Attach (session, "Tail"), "骨の無い先は使わない");
            CollectionAssert.Contains (session.stage.editingRig.binding.notes, Tr ("HELD_OBJECT_NO_ATTACH_BONE", kName, "Tail"));
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
            AnimationCurve curve = AnimationUtility.GetEditorCurve (session.clip, EditorCurveBinding.FloatCurve (RigPaths.ObjectAttach (kName, kLeft), typeof (HeldObjectControl), HeldObjectControl.kAttachedProperty));
            Assert.IsNotNull (curve, "付けているかのキーも打つ");
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

            SetAttached (session, kLeft, false);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Assert.IsFalse (Attach (session, kLeft).attached, "左手は離した");
            Assert.LessOrEqual (Vector3.Distance (hand, session.stage.editingRig.GetEditingBone (HumanBodyBones.LeftHand).position), kGripTolerance, "離した手");
            Assert.LessOrEqual (Vector3.Distance (sword, Entry (session).editing.position), kGripTolerance, "物");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手は持ったまま");

            session.Evaluate (15);
            Assert.IsTrue (Attach (session, kLeft).attached, "前のキーまでは持っている（段差）");
        }

        [Test]
        public void GrabAtCurrent_HoldsWhereTheHandIs_AndFollowsTheObject ()
        {
            // S99: 付けるボタン（SetObjectAttached）と同じ経路。押した瞬間の手首の場所と向きを、物から見た握りにして拘束する。
            // 枠がルートの物は体が動いても動かないので、持ってもワールドで手が留まる
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            Vector3 hand = GrabMovedLeftHand (session, out Quaternion turn);
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.LeftHand).position), kGripTolerance, "持っても手は動かない");
            // 持っている手の点もふつうの点なので（S102）、持った後はキーの無い腕から解き直す。手の Pin の点は場所だけなので、向きは 1.0° ほど動く（11-07 実測）
            Assert.LessOrEqual (Quaternion.Angle (turn, Bone (session, HumanBodyBones.LeftHand).rotation), 1.5f, "向きもほとんど動かない");
            Assert.Greater (GripGap (session, HumanBodyBones.LeftHand), 0.03f, "キャラの設定の握りへ動かない");
            Assert.IsNull (session.CommitPose ());

            session.Evaluate (30);
            Assert.IsTrue (Attach (session, kLeft).gripKeyed, "握りはキーから戻る");
            Assert.LessOrEqual (Vector3.Distance (hand, Bone (session, HumanBodyBones.LeftHand).position), kGripTolerance, "キーから戻った手");
            Vector3 local = Entry (session).editing.InverseTransformPoint (Bone (session, HumanBodyBones.LeftHand).position);
            MoveObject (session, new Vector3 (0.03f, 0.02f, 0));
            Assert.LessOrEqual (Vector3.Distance (local, Entry (session).editing.InverseTransformPoint (Bone (session, HumanBodyBones.LeftHand).position)), kGripTolerance, "物を動かしても握った所のまま付いてくる");
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.RightHand), kGripTolerance, "右手はキャラの設定の握りのまま");
            session.Evaluate (15);
            Assert.IsFalse (Attach (session, kLeft).gripKeyed, "持つ前のキーの間は、キーで持った握りではない（段で切り替わる）");
        }

        [Test]
        public void GrabAtCurrent_WhileHolding_SlidesTheGripBetweenKeys ()
        {
            // 11-08 ユーザー報告「棒の持ち位置をずらした状態で 0 と 30 にキーを打ったら、再生すると動きが変」。前後のキーで同じ手が持っていれば、
            // キーの間は握りを補間する（手が柄の上を滑る）。段で切り替えると、キーの間は前の握りのまま物と手のキーが補間され、手が引かれて次のキーで跳んだ
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            SetAttached (session, kLeft, false);
            Point (session, HumanBodyBones.LeftHand).MoveBy (new Vector3 (0, 0.03f, 0.06f));
            session.SolveAfterEdit ();
            session.stage.heldObjects.SetAttached (Attach (session, kLeft), true);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Vector3 keyedGrip = Attach (session, kLeft).heldGripPosition;
            Vector3 settingsGrip = SettingsGrip (Entry (session), HumanBodyBones.LeftHand);
            Assert.Greater (Vector3.Distance (keyedGrip, settingsGrip), 0.03f, "握り直した");

            Vector3 previous = Vector3.zero;
            for (int frame = 0; frame <= 30; frame++) {
                session.Evaluate (frame);
                HeldObjectRig.Attachment left = Attach (session, kLeft);
                Vector3 grip = Entry (session).editing.TransformPoint (left.heldGripPosition);
                Vector3 hand = Bone (session, HumanBodyBones.LeftHand).position;
                Assert.LessOrEqual (Vector3.Distance (grip, hand), kGripTolerance, frame + " フレームで手が握りに付く");
                if (frame > 0) Assert.Less (Vector3.Distance (previous, hand), 0.01f, frame + " フレームで手が跳ばない");
                previous = hand;
                if (frame != 15) continue;
                Assert.IsTrue (left.gripKeyed);
                Assert.Greater (Vector3.Distance (left.heldGripPosition, settingsGrip), 0.01f, "キーの間は握りが滑る（前のキーの握りでない）");
                Assert.Greater (Vector3.Distance (left.heldGripPosition, keyedGrip), 0.01f, "キーの間は握りが滑る（次のキーの握りでない）");
            }
            session.Evaluate (0);
            Assert.IsFalse (Attach (session, kLeft).gripKeyed, "前のキーはキャラの設定の握りのまま");
        }

        [Test]
        public void GrabAtCurrent_ChestFrame_KeepsTheGripOnTheObject ()
        {
            // 枠が胸の物は、手の Pin（ワールドに留める）が物への拘束に変わると胸が少し戻り、物と手が一緒に動く。握った所は物の上で変わらない
            PoseSession session = Open (CreateObject ());
            GrabMovedLeftHand (session, out Quaternion _);
            Vector3 grip = Entry (session).editing.TransformPoint (Attach (session, kLeft).heldGripPosition);
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
            SetAttached (session, kLeft, false);
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

        // ---- 握りの中で回る先（AttachJoint.Hinge。11-09 ユーザー指示「手首のひねり（roll）は固定だが、手の甲から見た yaw 角は ±45° の制限に」） ----

        const float kHingeRange = 45;
        /// <summary>テストの物の回る軸（枠の向き。テストの物は位置の向きが 0 なので、物のローカルでも同じ。柄の軸 y に直交する）</summary>
        static readonly Vector3 kHingeAxis = Vector3.forward;

        /// <summary>両手を Hinge の先にする</summary>
        void UseHinge ()
        {
            foreach (EditRigDefinition.AttachPoint point in definition_.attachPoints) {
                if (point.name != kRight && point.name != kLeft) continue;
                point.joint = EditRigDefinition.AttachJoint.Hinge;
                point.hingeAxis = kHingeAxis;
                point.hingeRange = kHingeRange;
            }
        }

        /// <summary>手首を回らなくする（手首で向けられない分を、握りの中の回りで向けるか見る）</summary>
        void LockWrists ()
        {
            foreach (HumanBodyBones hand in new[] { HumanBodyBones.RightHand, HumanBodyBones.LeftHand }) {
                EditRigDefinition.JointLimit wrist = definition_.FindFkControl (hand).limit;
                for (int axis = 0; axis < 3; axis++) wrist[axis].mode = EditRigDefinition.LimitMode.Locked;
            }
        }

        /// <summary>
        /// 物を右手の決まった位置へ寄せて付け（both なら左手も決まった位置で付ける）、キーを打つ。手は握った向きのまま（握りの中の回りは 0）から始める。
        /// 寄せずに体の前の決まった場所から手を伸ばすと、柄の軸まわりに回れない握りでは手の向きを物に合わせきれず、握りの中の回りが初めから範囲の端に当たる
        /// </summary>
        static void SnapToHands (PoseSession session, bool both)
        {
            WriteAttach (session, 0, both ? kRight + "," + kLeft : kRight, kRight, both ? SettingsGrips () : null);
            session.Evaluate (0);
            session.SolveAfterEdit ();
        }

        /// <summary>握りの中の回る中心（柄の軸の上で握りに一番近い点。物のローカル）</summary>
        static Vector3 HingePivot (HeldObjectRig.Entry entry, HumanBodyBones hand)
        {
            Vector3 axis = entry.definition.gripAxis.normalized;
            return axis * Vector3.Dot (SettingsGrip (entry, hand), axis);
        }

        /// <summary>手から見た物の向きと、回る中心の場所（手の枠）</summary>
        static void HandToObject (PoseSession session, HumanBodyBones hand, out Quaternion relative, out Vector3 pivot)
        {
            HeldObjectRig.Entry entry = Entry (session);
            Transform bone = Bone (session, hand);
            relative = Quaternion.Inverse (bone.rotation) * entry.editing.rotation;
            pivot = Quaternion.Inverse (bone.rotation) * (entry.editing.TransformPoint (HingePivot (entry, hand)) - bone.position);
        }

        /// <summary>
        /// 手から見た物の向きが、はじめ（start）から回る軸まわりにどれだけ回ったか（yaw。度）と、軸の外へどれだけ回ったか（度）。
        /// 回る中心の場所が、手から見てどれだけずれたか（メートル）
        /// </summary>
        static void MeasureHinge (PoseSession session, HumanBodyBones hand, Quaternion start, Vector3 startPivot, out float yaw, out float off, out float pivotGap)
        {
            HandToObject (session, hand, out Quaternion relative, out Vector3 pivot);
            Quaternion delta = Quaternion.Inverse (start) * relative;
            if (delta.w < 0) delta = new Quaternion (-delta.x, -delta.y, -delta.z, -delta.w);
            yaw = 2 * Mathf.Atan2 (Vector3.Dot (new Vector3 (delta.x, delta.y, delta.z), kHingeAxis), delta.w) * Mathf.Rad2Deg;
            off = Quaternion.Angle (delta, Quaternion.AngleAxis (yaw, kHingeAxis));
            pivotGap = Vector3.Distance (pivot, startPivot);
        }

        [Test]
        public void Hinge_DoesNotRollAboutTheGripAxis_TheWristTurnsWithTheObject ()
        {
            // 柄の軸まわり（roll）には握りの中で回らない。物を柄の軸まわりに回すと、手首が一緒に回る（Twist の先では手首は回らない: HeldHand_TwistsFreelyAroundTheGripAxis）
            UseHinge ();
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            SnapToHands (session, false);
            HandToObject (session, HumanBodyBones.RightHand, out Quaternion start, out Vector3 startPivot);
            Transform hand = Bone (session, HumanBodyBones.RightHand);
            Quaternion before = hand.rotation;
            Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;

            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            entry.DragTo (position, Quaternion.AngleAxis (40, axis) * rotation);
            session.SolveAfterEdit ();
            MeasureHinge (session, HumanBodyBones.RightHand, start, startPivot, out float yaw, out float off, out float pivotGap);
            Assert.LessOrEqual (off, 0.5f, "手から見て、物は回る軸の外へ回らない（柄の軸まわりに回らない）");
            Assert.LessOrEqual (Mathf.Abs (yaw), kHingeRange + 0.5f, "回る軸まわりは範囲の中");
            Assert.LessOrEqual (pivotGap, kGripTolerance, "回る中心は手の同じ所");
            Assert.Greater (Quaternion.Angle (before, hand.rotation), 20f, "手首が物と一緒に回る");
        }

        [Test]
        public void Hinge_LockedWrist_ObjectTurnsInTheGrip_WithinTheRange ()
        {
            // 手首で向けられないとき、物は握りの中で回る軸まわりに回る。範囲（±45°）の先へは回らない（物の方が譲る）
            UseHinge ();
            LockWrists ();
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            SnapToHands (session, false);
            HandToObject (session, HumanBodyBones.RightHand, out Quaternion start, out Vector3 startPivot);
            foreach (float angle in new[] { 30f, 80f }) {
                entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
                Vector3 axis = entry.editing.TransformDirection (kHingeAxis);
                Quaternion dragged = Quaternion.AngleAxis (angle, axis) * entry.editing.rotation;
                entry.DragTo (position, dragged);
                session.SolveAfterEdit ();
                MeasureHinge (session, HumanBodyBones.RightHand, start, startPivot, out float yaw, out float off, out float pivotGap);
                Assert.LessOrEqual (off, 0.5f, angle + "°: 回る軸の外へ回らない");
                Assert.LessOrEqual (Mathf.Abs (yaw), kHingeRange + 0.5f, angle + "°: 範囲の中");
                Assert.Greater (Mathf.Abs (yaw), 15f, angle + "°: 握りの中で回った");
                Assert.LessOrEqual (pivotGap, kGripTolerance, angle + "°: 回る中心は手の同じ所");
            }
        }

        [Test]
        public void Hinge_FreeWrist_WristTurnsFirst ()
        {
            // 握りの中の回りは要るときだけ（11-09 ユーザー決定）。手首で向けられる向きなら、握りの中ではほとんど回らない
            UseHinge ();
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            SnapToHands (session, false);
            HandToObject (session, HumanBodyBones.RightHand, out Quaternion start, out Vector3 startPivot);
            entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
            Vector3 axis = entry.editing.TransformDirection (kHingeAxis);
            entry.DragTo (position, Quaternion.AngleAxis (30, axis) * entry.editing.rotation);
            session.SolveAfterEdit ();
            MeasureHinge (session, HumanBodyBones.RightHand, start, startPivot, out float yaw, out float off, out float pivotGap);
            Assert.LessOrEqual (Mathf.Abs (yaw), 5f, "握りの中ではほとんど回らない");
            Assert.LessOrEqual (off, 0.5f, "回る軸の外へ回らない");
            Assert.LessOrEqual (pivotGap, kGripTolerance, "回る中心は手の同じ所");
        }

        [Test]
        public void Hinge_TwoHands_BothHandsStayInTheRange_AndKeepTheGrips ()
        {
            // もう片方の手（輪の関節）も同じ決まり: 柄の軸まわりに回らず、回る軸まわりは範囲の中、回る中心は手の同じ所
            UseHinge ();
            LockWrists ();
            definition_.fullBody.hipsPin = 1;
            PoseSession session = Open (CreateObject ());
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            SnapToHands (session, true);
            HumanBodyBones[] hands = { HumanBodyBones.RightHand, HumanBodyBones.LeftHand };
            Quaternion[] starts = new Quaternion[2];
            Vector3[] pivots = new Vector3[2];
            for (int h = 0; h < 2; h++) HandToObject (session, hands[h], out starts[h], out pivots[h]);
            foreach (Vector3 local in new[] { Vector3.right, Vector3.forward, Vector3.up }) {
                entry.GetWorldPose (out Vector3 position, out Quaternion rotation);
                entry.DragTo (position, rotation * Quaternion.AngleAxis (40, local));
                session.SolveAfterEdit ();
                for (int h = 0; h < 2; h++) {
                    MeasureHinge (session, hands[h], starts[h], pivots[h], out float yaw, out float off, out float pivotGap);
                    Assert.LessOrEqual (off, 0.5f, local + ": " + hands[h] + " は回る軸の外へ回らない");
                    Assert.LessOrEqual (Mathf.Abs (yaw), kHingeRange + 0.5f, local + ": " + hands[h] + " は範囲の中");
                    Assert.LessOrEqual (pivotGap, kGripTolerance, local + ": " + hands[h] + " は握りに付く");
                }
            }
        }

        [Test]
        public void Hinge_DraggingTipAimPoint_TurnsTheObjectWithoutRolling_AndKeepsTheGrip ()
        {
            // 物の先（向けるだけの点）を引くと、手首と握りの中の回りで向ける（握りの中は手首で向けきれない分だけ）。どちらも柄の軸まわりには回さない
            UseHinge ();
            PoseSession session = Open (CreateObjectWithPoints ());
            session.Evaluate (0);
            SnapToHands (session, false);
            HandToObject (session, HumanBodyBones.RightHand, out Quaternion start, out Vector3 startPivot);
            Quaternion rotation = Entry (session).editing.rotation;
            BodyPointTarget tip = ObjectPoint (session, kTip);
            tip.MoveTo (tip.framePosition + new Vector3 (0.15f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Angle (Entry (session).editing.rotation * Vector3.up, rotation * Vector3.up), 3f, "柄の軸が引いた方へ向く");
            MeasureHinge (session, HumanBodyBones.RightHand, start, startPivot, out float yaw, out float off, out float pivotGap);
            Assert.LessOrEqual (off, 0.5f, "回る軸の外へ回らない");
            Assert.LessOrEqual (Mathf.Abs (yaw), kHingeRange + 0.5f, "範囲の中");
            Assert.LessOrEqual (pivotGap, kGripTolerance, "回る中心は手の同じ所");
        }

        /// <summary>柄の軸の線（物の原点を通る）の、手首にいちばん近い所を手首から見た場所</summary>
        static Vector3 HandleOnHand (HeldObjectRig.Entry entry, Transform hand)
        {
            Vector3 axis = entry.editing.TransformDirection (entry.definition.gripAxis).normalized;
            Vector3 origin = entry.editing.position;
            Vector3 nearest = origin + axis * Vector3.Dot (hand.position - origin, axis);
            return Quaternion.Inverse (hand.rotation) * (nearest - hand.position);
        }

        /// <summary>左手を放して動かし、その場で持つ（付けるボタンと同じ順）。持つ前の手首の場所を返す</summary>
        static Vector3 GrabMovedLeftHand (PoseSession session, out Quaternion turn)
        {
            session.Evaluate (0);
            SetAttached (session, kLeft, false);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Point (session, HumanBodyBones.LeftHand).MoveBy (new Vector3 (0, 0.03f, 0.06f));
            session.SolveAfterEdit ();
            Vector3 hand = Bone (session, HumanBodyBones.LeftHand).position;
            turn = Bone (session, HumanBodyBones.LeftHand).rotation;
            session.stage.heldObjects.SetAttached (Attach (session, kLeft), true);
            session.SolveAfterEdit ();
            return hand;
        }

        [Test]
        public void PoseDocument_GripCurrent_ReadsBack_AndUseSettingsReturns ()
        {
            PoseSession session = Open (CreateObject (HeldObjectFrame.Root));
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 0, objects = new List<PoseObject> { new PoseObject { name = kName, attach = kRight } } }));
            PoseDocument rest = PoseDocument.Read (session, 0, out string _);
            Vector3 moved = rest.Find ("LeftHand").position + new Vector3 (0, 0.03f, 0.06f);
            // 手を動かして、その手で握るのを 1 つの要求に書く
            PoseDocument grab = new PoseDocument { frame = 30 };
            grab.points.Add (new PosePoint { name = "LeftHand", state = "Pin", position = moved });
            grab.objects.Add (new PoseObject { name = kName, attach = kRight + "," + kLeft, grip = new List<PoseGrip> { new PoseGrip { point = kLeft, current = true } } });
            Assert.IsNull (PoseDocument.Write (session, grab), "書ける");

            PoseDocument read = PoseDocument.Read (session, 30, out string _);
            Assert.LessOrEqual (Vector3.Distance (moved, read.Find ("LeftHand").position), 0.002f, "動かした所で握る");
            PoseGrip grip = read.objects[0].grip.Single ();
            Assert.AreEqual (kLeft, grip.point, "キーで握った手だけ返す");
            Assert.Greater (GripGap (session, HumanBodyBones.LeftHand), 0.03f, "キャラの設定の握りではない");

            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 60, objects = new List<PoseObject> { new PoseObject { name = kName, grip = new List<PoseGrip> { new PoseGrip { point = kLeft, useSettings = true } } } } }));
            session.Evaluate (60);
            Assert.LessOrEqual (GripGap (session, HumanBodyBones.LeftHand), kGripTolerance, "キャラの設定の握りへ戻る");
            Assert.IsEmpty (PoseDocument.Read (session, 60, out string _).objects[0].grip, "設定の握りの手は返さない");
            StringAssert.Contains ("Up", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = kName, grip = new List<PoseGrip> { new PoseGrip { point = "Up" } } } } }));
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
            Assert.IsTrue (Attach (session, kRight).attached);
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
            // キーの無いクリップでは Free（11-10 ユーザー指示。11-07 からは位置だけの Pin だった）。Free の物は物の姿勢に留まらず、体と手に付いて動く
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Free, ObjectTarget (session).state, "キーの無いクリップでは Free");
            ObjectTarget (session).SetState (BodyPointState.Pin);
            Assert.IsNull (session.CommitPose ());
            Assert.IsTrue (AnimationUtility.GetCurveBindings (session.clip).Any (b => b.path == RigPaths.ObjectPose (kName)), "状態のキーを打つ");
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "Pin がキーから戻る");
            ObjectTarget (session).SetState (BodyPointState.Free);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Free, ObjectTarget (session).state, "Free もキーから戻る");
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
        public void MovingObjectWithoutStateKeys_PinsPositionOnly_SoItTurnsThePinnedWrist ()
        {
            // キーの無い物は Free。動かすと、体の点と同じく位置だけの Pin になる（向きは回したときに固定する）。手首を Pin にして物を動かすと、手首が回る
            // （11-07 ユーザー報告「手首を pin にとめて持ち物 stick を動かすと、手首の角度が固定された状態で動く。通常の IK 制御では回転するはず」）
            PoseSession session = Open (false, CreateObject (HeldObjectFrame.Root));
            session.Evaluate (0);
            HeldObjectRig.Entry entry = Entry (session);
            SetAttached (session, kLeft, false);
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.AreEqual (BodyPointState.Free, entry.poseState, "キーの無い物は Free");
            Transform wrist = session.stage.editingRig.GetEditingBone (HumanBodyBones.RightHand);
            Quaternion before = wrist.rotation;
            MoveObject (session, new Vector3 (0, 0.1f, 0));
            Assert.AreEqual (BodyPointState.Pin, entry.poseState, "動かした物は Pin");
            Assert.AreEqual (0, entry.poseValues.rotationWeight, "向きは固定しない");
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
                SetAttached (session, kLeft, false);
                // キーの無い物は Free なので、Pin にする（位置だけ固定）
                ObjectTarget (session).SetState (BodyPointState.Pin);
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
            SetAttached (session, kLeft, false);
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
            SetAttached (session, kLeft, false);
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
            SetAttached (session, kLeft, false);
            // キーの無い物は Free なので、Pin にする（位置だけ固定）
            ObjectTarget (session).SetState (BodyPointState.Pin);
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "物は Pin");
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
            SetAttached (session, kLeft, false);
            // キーの無い物は Free なので、Pin にする（位置だけ固定）
            ObjectTarget (session).SetState (BodyPointState.Pin);
            Point (session, HumanBodyBones.RightHand).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Assert.AreEqual (BodyPointState.Pin, ObjectTarget (session).state, "物は Pin");
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
            Assert.AreEqual (kRight + "," + kLeft, item.attach);
            AssertHolding (session, "書いた後");
            StringAssert.Contains ("\"objects\":[", read.ToCompactJson (), "丸めた形にも出す");
        }

        [Test]
        public void PoseDocument_ChangesAttachments_AsSteps ()
        {
            PoseSession session = Open (CreateObject ());
            string both = kRight + "," + kLeft;
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 0, objects = new List<PoseObject> { new PoseObject { name = kName, attach = both } } }));
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 30, objects = new List<PoseObject> { new PoseObject { name = kName, attach = kRight } } }));
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 60, objects = new List<PoseObject> { new PoseObject { name = kName, attach = PoseDocument.kAttachNone } } }));
            Assert.AreEqual (both, PoseDocument.Read (session, 29, out string _).objects[0].attach, "切り替えの前");
            Assert.AreEqual (kRight, PoseDocument.Read (session, 30, out string _).objects[0].attach, "切り替えのフレーム");
            Assert.AreEqual (kRight, PoseDocument.Read (session, 45, out string _).objects[0].attach, "次のキーまで続く");
            Assert.AreEqual (PoseDocument.kAttachNone, PoseDocument.Read (session, 60, out string _).objects[0].attach, "全部外す");
        }

        [Test]
        public void PoseDocument_RefusesUnknownObjectAndAttachPoint ()
        {
            PoseSession session = Open (CreateObject ());
            StringAssert.Contains ("Nope", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = "Nope" } } }));
            StringAssert.Contains ("Sideways", PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = kName, attach = "Sideways" } } }));
            StringAssert.Contains (kHips, PoseDocument.Check (session, new PoseDocument { objects = new List<PoseObject> { new PoseObject { name = kName, snap = kHips } } }), "決まった位置の無い先へは寄せられない");
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
        public void StoreSettings_KeepsGripAfterRebuild ()
        {
            HeldObjectDefinition definition = CreateObject ();
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig rig = session.stage.heldObjects;
            HeldObjectRig.Attachment hand = Attach (session, kLeft);
            rig.GetSettingsHandle (hand, out Vector3 position, out Quaternion rotation);
            Vector3 placed = position + new Vector3 (0, 0.01f, 0.02f);
            // 柄の軸（+Y）まわりは手首に任せるので、軸を傾ける向きに回す
            Quaternion turned = Quaternion.Euler (15, 0, 0) * rotation;
            rig.SetSettingsHandle (hand, placed, turned);
            rig.StoreSettings (hand);

            PoseSession rebuilt = Open (definition);
            rebuilt.Evaluate (0);
            session.stage.heldObjects.GetSettingsHandle (hand, out Vector3 before, out Quaternion unused);
            HeldObjectRig rebuiltRig = rebuilt.stage.heldObjects;
            rebuiltRig.GetSettingsHandle (Attach (rebuilt, kLeft), out Vector3 after, out Quaternion afterRotation);
            Vector3 local = Quaternion.Inverse (Entry (rebuilt).editing.rotation) * (after - Entry (rebuilt).editing.position);
            Vector3 expected = Quaternion.Inverse (Entry (session).editing.rotation) * (before - Entry (session).editing.position);
            Assert.LessOrEqual (Vector3.Distance (expected, local), 1e-4f, "物から見た握りの場所");
            Assert.LessOrEqual (GripGap (rebuilt, HumanBodyBones.LeftHand), kGripTolerance, "手は新しい握りに付く");
        }

        // ---- S91c 焼く ----

        [Test]
        public void StoreSettings_OnHips_PlacesObjectAtSameSpotAfterRebuild ()
        {
            // 回らない先（腰）のハンドルは物そのもの: 動かすと、腰は動かず物が動く
            HeldObjectDefinition definition = CreateObjectWithHips ();
            AttachAtRest (kHips);
            PoseSession session = Open (definition);
            session.Evaluate (0);
            HeldObjectRig rig = session.stage.heldObjects;
            HeldObjectRig.Attachment hips = Attach (session, kHips);
            rig.GetSettingsHandle (hips, out Vector3 position, out Quaternion rotation);
            Assert.LessOrEqual (Vector3.Distance (position, Entry (session).editing.position), kGripTolerance, "ハンドルは物の場所");
            Vector3 placed = position + new Vector3 (0.02f, -0.03f, 0.01f);
            Quaternion turned = Quaternion.Euler (0, 30, 10) * rotation;
            rig.SetSettingsHandle (hips, placed, turned);
            rig.StoreSettings (hips);

            PoseSession rebuilt = Open (definition);
            rebuilt.Evaluate (0);
            Assert.LessOrEqual (Vector3.Distance (placed, Entry (rebuilt).editing.position), kGripTolerance, "置いた場所");
            Assert.LessOrEqual (Quaternion.Angle (turned, Entry (rebuilt).editing.rotation), 0.1f, "置いた向き");
        }

        [Test]
        public void Constraint_NotFound_IsReported ()
        {
            HeldObjectDefinition definition = CreateObject ();
            definition.FindAttachment (kLeft).constraint = "NotThere/Constraint";
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

        [Test]
        public void Bake_WhileOnHipsWithBakeParent_CurvesAreLocalToHips ()
        {
            // 11-09 ユーザー指示「腰にアタッチした際は、hips を親ボーンにする」。腰に差している間は腰から見た値、手で持っている間は親の骨（右手）から見た値
            HeldObjectDefinition definition = CreateObjectWithHips ();
            definition.FindAttachment (kHips).bakeParent = true;
            PoseSession session = Open (definition);
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            WriteToHips (session, 30);
            AnimationClip baked = NewClip ();
            using (HumanoidBaker baker = session.stage.CreateHumanoidBaker ()) {
                baker.afterSolve = () => session.stage.SolveLockedSpan (1);
                baker.Bake (session.clip, baked);
            }
            HeldObjectRig.Entry entry = Entry (session);
            string path = AnimationUtility.CalculateTransformPath (entry.display, session.stage.animator.transform);

            foreach ((int frame, bool onHips) in new[] { (10, false), (40, true) }) {
                session.Evaluate (frame);
                Assert.AreEqual (onHips, Attach (session, kHips).attached);
                Transform parent = onHips ? Bone (session, HumanBodyBones.Hips) : entry.editing.parent;
                Vector3 position = parent.InverseTransformPoint (entry.editing.position);
                Quaternion rotation = Quaternion.Inverse (parent.rotation) * entry.editing.rotation;
                float time = frame / baked.frameRate;
                Vector3 bakedPosition = new Vector3 (BakedValue (baked, path, "m_LocalPosition.x", time), BakedValue (baked, path, "m_LocalPosition.y", time), BakedValue (baked, path, "m_LocalPosition.z", time));
                Quaternion bakedRotation = new Quaternion (BakedValue (baked, path, "m_LocalRotation.x", time), BakedValue (baked, path, "m_LocalRotation.y", time),
                    BakedValue (baked, path, "m_LocalRotation.z", time), BakedValue (baked, path, "m_LocalRotation.w", time));
                Assert.LessOrEqual (Vector3.Distance (position, bakedPosition), kGripTolerance, frame + " フレームの場所");
                Assert.LessOrEqual (Quaternion.Angle (rotation, Quaternion.Normalize (bakedRotation)), 0.1f, frame + " フレームの向き");
            }
            Assert.Greater (Vector3.Distance (entry.editing.localPosition, Bone (session, HumanBodyBones.Hips).InverseTransformPoint (entry.editing.position)), 0.05f, "右手から見た値とは違う");
        }

        [Test]
        public void DetachedInWorld_StaysWhenHipsMove_AndDoesNotJumpWhenDetaching ()
        {
            // 11-09 ユーザー報告「いまの状態だと hips を動かすと剣がついてくる」。どこにも付けていない間は、枠の骨（胸）でなくワールドに留まる
            HeldObjectDefinition definition = CreateObject ();
            definition.detachedInWorld = true;
            PoseSession session = Open (definition);
            HeldObjectRig.Entry entry = Entry (session);
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Vector3 before = entry.editing.position;
            entry.DetachAll ();
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (before, entry.editing.position), kJumpTolerance, "外しても動かない");
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Vector3 detached = entry.editing.position;
            Assert.LessOrEqual (Vector3.Distance (before, detached), kGripTolerance, "外したキーで同じ所");
            session.Evaluate (29);
            Assert.IsTrue (entry.anyAttached, "前のフレームは付いている");
            Assert.LessOrEqual (Vector3.Distance (detached, entry.editing.position), 0.01f, "外すキーの前で跳ばない");

            session.Evaluate (30);
            Assert.IsTrue (entry.inWorld);
            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.2f, -0.1f, 0.1f));
            session.SolveAfterEdit ();
            Assert.LessOrEqual (Vector3.Distance (detached, entry.editing.position), kGripTolerance, "腰を動かしても物はワールドに留まる");
        }

        [Test]
        public void Detached_FollowsTheFrameBone_WithoutDetachedInWorld ()
        {
            // 既定（detachedInWorld が切）では、今までどおり枠の骨（胸）に付いて動く
            PoseSession session = Open (CreateObject ());
            HeldObjectRig.Entry entry = Entry (session);
            session.Evaluate (0);
            entry.DetachAll ();
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (0);
            Vector3 detached = entry.editing.position;
            Point (session, HumanBodyBones.Hips).MoveBy (new Vector3 (0.2f, 0, 0));
            session.SolveAfterEdit ();
            Assert.Greater (Vector3.Distance (detached, entry.editing.position), 0.1f, "胸に付いて動く");
        }

        [Test]
        public void Bake_WhileDetachedInWorld_CurvesAreInWorld ()
        {
            // 11-09 ユーザー指示「どれにもアタッチしてない場合はワールドに」。どこにも付けていない間は Animator の GameObject から見た値
            HeldObjectDefinition definition = CreateObject ();
            definition.detachedInWorld = true;
            PoseSession session = Open (definition);
            session.Evaluate (0);
            Assert.IsNull (session.CommitPose ());
            session.Evaluate (30);
            Entry (session).DetachAll ();
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());
            AnimationClip baked = NewClip ();
            using (HumanoidBaker baker = session.stage.CreateHumanoidBaker ()) {
                baker.afterSolve = () => session.stage.SolveLockedSpan (1);
                baker.Bake (session.clip, baked);
            }
            HeldObjectRig.Entry entry = Entry (session);
            string path = AnimationUtility.CalculateTransformPath (entry.display, session.stage.animator.transform);

            foreach ((int frame, bool detached) in new[] { (10, false), (40, true) }) {
                session.Evaluate (frame);
                Assert.AreEqual (detached, !entry.anyAttached);
                Transform parent = detached ? session.stage.editingRoot : entry.editing.parent;
                Vector3 position = parent.InverseTransformPoint (entry.editing.position);
                Quaternion rotation = Quaternion.Inverse (parent.rotation) * entry.editing.rotation;
                float time = frame / baked.frameRate;
                Vector3 bakedPosition = new Vector3 (BakedValue (baked, path, "m_LocalPosition.x", time), BakedValue (baked, path, "m_LocalPosition.y", time), BakedValue (baked, path, "m_LocalPosition.z", time));
                Quaternion bakedRotation = new Quaternion (BakedValue (baked, path, "m_LocalRotation.x", time), BakedValue (baked, path, "m_LocalRotation.y", time),
                    BakedValue (baked, path, "m_LocalRotation.z", time), BakedValue (baked, path, "m_LocalRotation.w", time));
                Assert.LessOrEqual (Vector3.Distance (position, bakedPosition), kGripTolerance, frame + " フレームの場所");
                Assert.LessOrEqual (Quaternion.Angle (rotation, Quaternion.Normalize (bakedRotation)), 0.1f, frame + " フレームの向き");
            }
            Assert.Greater (Vector3.Distance (entry.editing.localPosition, session.stage.editingRoot.InverseTransformPoint (entry.editing.position)), 0.05f, "右手から見た値とは違う");
        }

        static float BakedValue (AnimationClip clip, string path, string property, float time)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), property));
            Assert.IsNotNull (curve, path + " " + property);
            return curve.Evaluate (time);
        }
    }

}
