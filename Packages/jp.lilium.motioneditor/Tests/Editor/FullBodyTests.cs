using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 全身 IK（S25）の配線: リグの定義のモード・点のコントロール・素通し・元の姿勢からの解き直し・焼きへの接続。
    /// 解く中身（硬さ・角度制限）は S25a-2 で別に確かめる
    /// </summary>
    public class FullBodyTests : RigTestFixture
    {
        const float kPositionTolerance = 1e-4f;
        const float kAngleTolerance = 0.01f;

        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = CreateDefinition ();
            Random.InitState (4321);
        }

        sealed class Setup
        {
            public Animator display;
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
        }

        /// <param name="twistFrames">骨の軸の向きをばらばらにする（体の形は同じで骨の軸だけ違うキャラ）</param>
        /// <param name="toes">つま先の骨を足す（足の点はつま先の骨のあるキャラにだけできる）</param>
        Setup Create (bool withChest = true, bool twistFrames = false, bool fingers = false, float size = 1, bool toes = false)
        {
            Setup s = new Setup ();
            s.display = CreateCharacter (withChest, size, toes, fingers: fingers, twistFrames: twistFrames);
            s.display.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            s.rig = CreateEditingRig (definition_, s.display);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition_));
            return s;
        }

        static Transform Bone (Setup s, HumanBodyBones bone)
        {
            return s.rig.GetEditingBone (bone);
        }

        static List<KeyValuePair<Vector3, Quaternion>> Snapshot (Setup s)
        {
            return s.rig.editingBones.Select (t => new KeyValuePair<Vector3, Quaternion> (t.localPosition, t.localRotation)).ToList ();
        }

        static void AssertSamePose (List<KeyValuePair<Vector3, Quaternion>> expected, Setup s, string message, float positionTolerance = 0, float angleTolerance = 0)
        {
            int i = 0;
            foreach (Transform t in s.rig.editingBones) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].Key, t.localPosition), positionTolerance, message + ": " + t.name + " の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].Value, t.localRotation), angleTolerance, message + ": " + t.name + " の回転");
                i++;
            }
        }

        /// <summary>FK で姿勢を付けて解く（元の姿勢を T ポーズ以外にする）</summary>
        static void PoseFk (Setup s)
        {
            s.rig.FindControl (RigPaths.Fk (HumanBodyBones.Spine)).localRotation = Quaternion.Euler (10, 20, 5);
            s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm)).localRotation = Quaternion.Euler (0, 30, -40);
            s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftLowerArm)).localRotation = Quaternion.Euler (0, 50, 0);
            s.rig.FindControl (RigPaths.Fk (HumanBodyBones.RightUpperLeg)).localRotation = Quaternion.Euler (-30, 0, 0);
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
        }

        // ---- 点の割り当て ----

        [Test]
        public void NoBodyPoints_CreatesNoPointControls ()
        {
            definition_.bodyPoints.Clear ();
            Setup s = Create ();

            Assert.AreEqual (0, s.rig.binding.points.Count, "点の無い定義では点を並べない");
            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftHand)));
            Assert.AreEqual (0, s.body.pointCount);
            Assert.IsNull (s.rig.root.transform.Find (RigPaths.kBody), "Controls/Body を作らない");
        }

        [Test]
        public void DefaultDefinition_CreatesPoints ()
        {
            Setup s = Create (fingers: true);

            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightFoot, HumanBodyBones.LeftLowerArm }) {
                Transform control = s.rig.FindControl (RigPaths.Body (bone));
                Assert.IsNotNull (control, "点がある: " + bone);
                Assert.IsNotNull (control.GetComponent<BodyPoint> (), "固定の強さの受け皿が付いている: " + bone);
                Assert.IsTrue (s.body.HasPoint (bone));
            }
            Assert.IsNotNull (Bone (s, HumanBodyBones.LeftThumbProximal), "指の骨はある");
            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftThumbProximal)), "指には点を置かない");
            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftToes)), "キャラに無い骨の点は出ない");

            Assert.IsTrue (s.body.PointHasRotation (HumanBodyBones.LeftHand));
            Assert.IsTrue (s.body.PointHasRotation (HumanBodyBones.Hips));
            Assert.IsFalse (s.body.PointHasRotation (HumanBodyBones.LeftLowerArm), "肘の点は位置だけ");
        }

        [Test]
        public void Points_FollowBonesWithoutChest ()
        {
            // 必須の骨だけのキャラ（胸・首が無い）でも、ある骨の点だけで組める。背骨をまとめた胸の点は、ある背骨（Spine）に乗る
            Setup s = Create (withChest: false);

            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.Neck)));
            Assert.IsFalse (s.body.HasPoint (HumanBodyBones.Spine), "背骨の骨ごとの点は無い");
            Assert.IsTrue (s.body.HasPoint (HumanBodyBones.Chest));
            Assert.AreEqual (Bone (s, HumanBodyBones.Spine), s.body.GetBone (HumanBodyBones.Chest));
            // お腹の点も Spine に乗る（背骨が 1 本でも、胸とお腹の点の名前はそろう）
            BodyPointId stomach = new BodyPointId (EditRigDefinition.kStomachPoint);
            Assert.IsTrue (s.body.HasPoint (stomach));
            Assert.AreEqual (Bone (s, HumanBodyBones.Spine), s.body.GetBone (stomach));
            Assert.IsTrue (s.body.HasPoint (HumanBodyBones.LeftHand));
        }

        [Test]
        public void PointRestValues_AreBonePositions_WithZeroWeights ()
        {
            Setup s = Create (size: 1.5f);

            foreach (BodyPointId bone in s.body.pointIds) {
                Vector3 position;
                Quaternion rotation;
                Assert.IsTrue (s.body.TryGetPointWorld (bone, out position, out rotation));
                Assert.AreEqual (0, s.body.GetPositionWeight (bone), "固定の強さは 0 から始まる: " + bone);
                RigBinding.Point bound = s.rig.binding.FindPoint (bone.name);
                if (!bound.definition.onBoneOrigin) continue;
                Assert.LessOrEqual (Vector3.Distance (s.body.GetBone (bone).position, position), kPositionTolerance, "点は基準姿勢の骨の上: " + bone);
                Assert.LessOrEqual (Quaternion.Angle (s.body.GetBone (bone).rotation, rotation), kAngleTolerance, "点の向きは基準姿勢の骨の向き: " + bone);
                Assert.AreEqual (0, s.body.GetPositionWeight (bone), "固定の強さは 0 から始まる: " + bone);
                Assert.AreEqual (0, s.body.GetRotationWeight (bone));
            }
            // 保存値は体の大きさで割ってある
            Transform hand = s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftHand));
            Vector3 rootRelative = s.rig.root.transform.InverseTransformPoint (Bone (s, HumanBodyBones.LeftHand).position);
            Assert.LessOrEqual (Vector3.Distance (rootRelative / s.rig.humanScale, hand.localPosition), kPositionTolerance);
            Assert.LessOrEqual (Quaternion.Angle (Quaternion.identity, hand.localRotation), kAngleTolerance, "向きは基準姿勢からの差で持つ（基準姿勢では差なし）");
        }

        // ---- 素通しと解き直し ----

        [Test]
        public void NoActivePoint_LeavesPoseUntouched ()
        {
            Setup s = Create ();
            PoseFk (s);
            List<KeyValuePair<Vector3, Quaternion>> before = Snapshot (s);

            Assert.IsFalse (s.body.hasActivePoint);
            // 点の位置だけ動かしても、固定の強さが 0 なら効かない
            s.body.SetPointPositionWorld (HumanBodyBones.LeftHand, Bone (s, HumanBodyBones.LeftHand).position + new Vector3 (0, 0.3f, 0));
            s.body.Solve ();

            AssertSamePose (before, s, "固定している点が無ければ何もしない");
        }

        [Test]
        public void PositionPoint_MovesBoneToTarget ()
        {
            Setup s = Create ();
            PoseFk (s);
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            // 右腕は T ポーズでまっすぐ。届く範囲へ寄せる
            Vector3 target = hand.position + s.rig.root.transform.TransformDirection (s.rig.BodyToRoot (new Vector3 (-0.1f, 0.05f, 0.15f)));

            s.body.SetPointPositionWorld (HumanBodyBones.RightHand, target);
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 1);
            Assert.IsTrue (s.body.hasActivePoint);
            s.body.Solve ();

            Assert.LessOrEqual (Vector3.Distance (target, hand.position), 1e-3f, "固定した点へ届く");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.RightHand), 1e-3f);
        }

        [Test]
        public void LayerWeight_BlendsBetweenInputAndSolved ()
        {
            Setup s = Create ();
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 start = hand.position;
            Vector3 target = start + new Vector3 (0, 0.2f, 0);
            s.body.SetPointPositionWorld (HumanBodyBones.RightHand, target);
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 1);

            s.body.CaptureInput ();
            s.body.Solve (0);
            Assert.LessOrEqual (Vector3.Distance (start, hand.position), 0, "重み 0 は素通し");

            s.body.RestoreInput ();
            s.body.Solve (0.5f);
            float half = Vector3.Distance (start, hand.position);
            s.body.RestoreInput ();
            s.body.Solve (1);
            float full = Vector3.Distance (start, hand.position);
            Assert.Greater (half, 0.01f, "重み 0.5 で動く");
            Assert.Less (half, full, "重み 0.5 は重み 1 より手前");
        }

        [Test]
        public void SolveFromCapturedInput_IsRepeatable_AndReturnsWhenReleased ()
        {
            Setup s = Create ();
            PoseFk (s);
            List<KeyValuePair<Vector3, Quaternion>> input = Snapshot (s);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Vector3 target = hand.position + new Vector3 (0.05f, 0.1f, 0.05f);
            s.body.SetPointPositionWorld (HumanBodyBones.LeftHand, target);
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);

            s.body.CaptureInput ();
            s.body.Solve ();
            List<KeyValuePair<Vector3, Quaternion>> first = Snapshot (s);

            // 解いた結果からではなく、控えた元の姿勢から解き直す。同じ入力なら同じ結果
            Assert.IsTrue (s.body.RestoreInput ());
            s.body.Solve ();
            AssertSamePose (first, s, "同じ入力なら同じ結果");

            // 固定を外せば、元の姿勢に戻る
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 0);
            s.body.RestoreInput ();
            s.body.Solve ();
            AssertSamePose (input, s, "固定を外すと元の姿勢");
        }

        /// <summary>
        /// 編集用リグを解き直す前に、全身 IK が曲げた分を戻す。編集用リグはコントロールの無い骨を書かないので、
        /// 戻さないと曲げた分が残って積み重なる（キャラ A で腰の親が回り続けて、体が大きくずれた。2026-09-30）
        /// </summary>
        [Test]
        public void Unsolve_RestoresInput_OnlyAfterSolve ()
        {
            Setup s = Create ();
            PoseFk (s);
            List<KeyValuePair<Vector3, Quaternion>> input = Snapshot (s);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            s.body.SetPointPositionWorld (HumanBodyBones.LeftHand, hand.position + new Vector3 (0.05f, 0.1f, 0.05f));
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);

            s.body.CaptureInput ();
            s.body.Solve ();
            List<KeyValuePair<Vector3, Quaternion>> solved = Snapshot (s);
            s.body.Unsolve ();
            AssertSamePose (input, s, "解いた後は元の姿勢へ戻る");

            // 解いていない間は何もしない（元の姿勢の後で骨を動かしても、巻き戻さない）
            Quaternion moved = Quaternion.Euler (0, 0, 20) * hand.localRotation;
            hand.localRotation = moved;
            s.body.Unsolve ();
            Assert.LessOrEqual (Quaternion.Angle (moved, hand.localRotation), 0, "解いていなければ戻さない");
            hand.localRotation = input[s.rig.editingBones.ToList ().IndexOf (hand)].Value;

            // 戻す → 編集用リグを解く → 全身 IK を解く、を繰り返しても同じ姿勢
            for (int i = 0; i < 10; i++) {
                s.body.Unsolve ();
                s.solver.Solve ();
                s.body.CaptureInput ();
                s.body.Solve ();
            }
            AssertSamePose (solved, s, "解き直しを繰り返しても同じ姿勢", 1e-5f, 0.01f);
        }

        [Test]
        public void RestoreInput_WithoutCapture_ReturnsFalse ()
        {
            Setup s = Create ();
            Assert.IsFalse (s.body.RestoreInput ());
            s.body.CaptureInput ();
            Assert.IsTrue (s.body.RestoreInput ());
            s.body.ClearInput ();
            Assert.IsFalse (s.body.RestoreInput ());
        }

        // ---- 位置と向きの固定は別 ----

        [Test]
        public void RotationWeight_IsSeparateFromPositionWeight ()
        {
            Setup s = Create ();
            Transform hand = Bone (s, HumanBodyBones.LeftHand);
            Quaternion restLocal = hand.localRotation;
            Quaternion wanted = Quaternion.Euler (0, 40, 30) * hand.rotation;
            s.body.SetPointRotationWorld (HumanBodyBones.LeftHand, wanted);

            // 位置だけ固定: 手の向きは前腕に付いたまま
            s.body.SetPointPositionWorld (HumanBodyBones.LeftHand, hand.position + new Vector3 (0, 0.1f, 0.1f));
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);
            s.body.CaptureInput ();
            s.body.Solve ();
            Assert.LessOrEqual (Quaternion.Angle (restLocal, hand.localRotation), kAngleTolerance, "向きの固定が 0 なら、手は前腕に付いて回るだけ");

            // 向きも固定
            s.body.SetRotationWeight (HumanBodyBones.LeftHand, 1);
            s.body.RestoreInput ();
            s.body.Solve ();
            Assert.LessOrEqual (Quaternion.Angle (wanted, hand.rotation), kAngleTolerance, "向きを固定すると点の向きになる");

            // 位置だけの点（肘）は向きの固定を持てない
            s.body.SetRotationWeight (HumanBodyBones.LeftLowerArm, 1);
            Assert.AreEqual (0, s.body.GetRotationWeight (HumanBodyBones.LeftLowerArm));
        }

        [Test]
        public void PointRotation_MeansSameDirection_ForDifferentBoneAxes ()
        {
            // 向きは基準姿勢からの差で持つので、骨の軸の取り方が違うキャラでも同じ値で同じ向きになる
            Quaternion value = Quaternion.Euler (20, 45, -30);
            Quaternion[] deltas = new Quaternion[2];
            for (int i = 0; i < 2; i++) {
                Setup s = Create (twistFrames: i == 1);
                Transform hand = Bone (s, HumanBodyBones.LeftHand);
                Quaternion rest = hand.rotation;
                s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftHand)).localRotation = value;
                s.body.SetRotationWeight (HumanBodyBones.LeftHand, 1);
                s.body.Solve ();
                // ルートから見た、基準姿勢からの向きの変化
                Quaternion root = s.rig.root.transform.rotation;
                deltas[i] = Quaternion.Inverse (root) * hand.rotation * Quaternion.Inverse (rest) * root;
            }
            Assert.LessOrEqual (Quaternion.Angle (deltas[0], deltas[1]), kAngleTolerance);
            Assert.LessOrEqual (Quaternion.Angle (value, deltas[0]), kAngleTolerance);
        }

        [Test]
        public void CapturePoint_PutsPointOnCurrentBone ()
        {
            Setup s = Create ();
            PoseFk (s);
            Transform hand = Bone (s, HumanBodyBones.LeftHand);

            Assert.IsTrue (s.body.CapturePoint (HumanBodyBones.LeftHand));
            Vector3 position;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (hand.position, position), kPositionTolerance);
            Assert.LessOrEqual (Quaternion.Angle (hand.rotation, rotation), kAngleTolerance);

            // 今の場所で固定しても姿勢は変わらない
            List<KeyValuePair<Vector3, Quaternion>> before = Snapshot (s);
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);
            s.body.SetRotationWeight (HumanBodyBones.LeftHand, 1);
            s.body.Solve ();
            AssertSamePose (before, s, "今の場所で固定しても動かない", 1e-4f, 0.05f);
        }

        // ---- つかむ対象とキー ----

        static BodyPointTarget PointTarget (List<PoseTarget> targets, HumanBodyBones bone)
        {
            return targets.OfType<BodyPointTarget> ().First (t => t.bone == bone && t.isOnBoneOrigin);
        }

        static List<EditorCurveBinding> Bindings (AnimationClip clip)
        {
            return AnimationUtility.GetCurveBindings (clip).ToList ();
        }

        static float Value (AnimationClip clip, string path, System.Type type, string property, float time = 0)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, type, property));
            Assert.IsNotNull (curve, path + " " + property);
            return curve.Evaluate (time);
        }

        [Test]
        public void Targets_OnePerPoint ()
        {
            Setup s = Create ();
            List<PoseTarget> points = BodyPointTarget.Create (s.rig, s.body);
            Assert.AreEqual (s.body.pointCount, points.Count);
            Assert.IsTrue (points.All (t => t.gameObject != null && t.anchor != null));

            // 点の無い定義では点の対象は出ない
            definition_.bodyPoints.Clear ();
            Setup none = Create ();
            Assert.AreEqual (0, BodyPointTarget.Create (none.rig, none.body).Count);
        }

        [Test]
        public void FootPoints_AreLinkedLikeAFootRig ()
        {
            // 踵・足裏の中心は足首へ、小指側とつま先の先は足裏の中心へ線でつなぐ（足の点はつま先の骨のあるキャラにだけできる）
            Setup s = Create (toes: true);
            List<BodyPointTarget> points = BodyPointTarget.Create (s.rig, s.body).OfType<BodyPointTarget> ().ToList ();
            System.Func<string, BodyPointTarget> named = name => points.Single (p => p.id.name == name);
            foreach (string side in new[] { "Left", "Right" }) {
                BodyPointTarget ankle = named (side + "Foot");
                BodyPointTarget ball = named (side + "SoleBall");
                Assert.AreSame (ankle, named (side + "Heel").link, side + " 踵 → 足首");
                Assert.AreSame (ankle, ball.link, side + " 足裏の中心 → 足首");
                Assert.AreSame (ball, named (side + "LittleToeSide").link, side + " 小指側 → 足裏の中心");
                Assert.AreSame (ball, named (side + "ToeTip").link, side + " つま先の先 → 足裏の中心");
            }
        }

        [Test]
        public void MovingPoint_PinsPosition_AndKeysOnlyThatPoint ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            Transform root = s.rig.root.transform;
            Vector3 wanted = Bone (s, HumanBodyBones.LeftHand).position + new Vector3 (0, 0.1f, 0.05f);

            Assert.IsFalse (hand.pinned);
            Assert.IsFalse (hand.hasPendingKeys);
            hand.SetValues (TransformChannels.Position, root.InverseTransformPoint (wanted), Quaternion.identity);
            Assert.IsTrue (hand.positionPinned, "動かすと位置が固定される");
            Assert.IsFalse (hand.rotationPinned, "位置を動かしただけでは向きは固定されない");
            Assert.IsTrue (hand.hasPendingKeys);

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 5, "Test")) {
                foreach (PoseTarget target in targets) ((BodyPointTarget)target).WritePendingKeys (writer);
            }
            Assert.IsFalse (hand.hasPendingKeys);

            string path = RigPaths.Body (HumanBodyBones.LeftHand);
            List<EditorCurveBinding> bindings = Bindings (clip);
            Assert.AreEqual (4, bindings.Count, "位置 3 本と位置の固定の強さだけ: " + string.Join (", ", bindings.Select (b => b.path + ":" + b.propertyName)));
            Assert.IsTrue (bindings.All (b => b.path == path), "骨のカーブも、ほかの点のカーブも増えない");
            Assert.AreEqual (1, Value (clip, path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, 5 / 60f), 1e-6f);
            Vector3 stored = new Vector3 (
                Value (clip, path, typeof (Transform), "m_LocalPosition.x", 5 / 60f),
                Value (clip, path, typeof (Transform), "m_LocalPosition.y", 5 / 60f),
                Value (clip, path, typeof (Transform), "m_LocalPosition.z", 5 / 60f));
            Assert.LessOrEqual (Vector3.Distance (root.InverseTransformPoint (wanted) / s.rig.humanScale, stored), kPositionTolerance, "保存値はルートから見た位置 / 体の大きさ");
        }

        [Test]
        public void RotatingPoint_PinsRotation_AndKeysRotationToo ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.RightHand);
            BodyPointTarget elbow = PointTarget (targets, HumanBodyBones.RightLowerArm);
            Transform root = s.rig.root.transform;

            hand.SpinRotateWorld (Quaternion.Euler (0, 30, 0));
            Assert.IsTrue (hand.rotationPinned, "回すと向きが固定される");
            Assert.IsFalse (hand.positionPinned, "回しただけでは位置は固定されない");
            // 位置だけの点は回せない
            elbow.SpinRotateWorld (Quaternion.Euler (0, 30, 0));
            Assert.IsFalse (elbow.pinned);
            Assert.AreEqual (TransformChannels.Position, elbow.GetSpinChannels ());

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 0, "Test")) {
                foreach (PoseTarget target in targets) ((BodyPointTarget)target).WritePendingKeys (writer);
            }
            string path = RigPaths.Body (HumanBodyBones.RightHand);
            List<EditorCurveBinding> bindings = Bindings (clip);
            Assert.AreEqual (5, bindings.Count, "向き 4 本と向きの固定の強さだけ: " + string.Join (", ", bindings.Select (b => b.path + ":" + b.propertyName)));
            Assert.IsTrue (bindings.All (b => b.path == path));
            Assert.AreEqual (1, Value (clip, path, typeof (BodyPoint), BodyPoint.kRotationWeightProperty), 1e-6f);
        }

        [Test]
        public void KeyAll_WritesOnlyPinnedPoints_AndUnpinWritesZeroWeight ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            Transform root = s.rig.root.transform;
            string path = RigPaths.Body (HumanBodyBones.LeftFoot);

            // 固定している点が無ければ、姿勢全体のキー（Key All）で点のカーブは 1 本も立たない
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 0, "Test")) {
                foreach (PoseTarget target in targets) target.WriteKeys (writer);
            }
            Assert.AreEqual (0, Bindings (clip).Count);

            // 今の場所で固定（輪の右ボタン）
            foot.TogglePin ();
            Assert.IsTrue (foot.positionPinned);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 0, "Test")) {
                foreach (PoseTarget target in targets) target.WriteKeys (writer);
            }
            Assert.IsTrue (Bindings (clip).All (b => b.path == path), "固定している点だけ打つ");
            Assert.AreEqual (4, Bindings (clip).Count);

            // 固定を外すと、強さ 0 のキーを打つ（打たないと、キーの値がその先も続くので外れない）
            foot.TogglePin ();
            Assert.IsFalse (foot.pinned);
            Assert.IsTrue (foot.hasPendingKeys);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 10, "Test")) {
                foot.WritePendingKeys (writer);
            }
            Assert.AreEqual (1, Value (clip, path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, 0), 1e-6f);
            Assert.AreEqual (0, Value (clip, path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, 10 / 60f), 1e-6f);
        }

        [Test]
        public void ReleasedPoint_SyncsToBone_UnlessLocked ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            Transform root = s.rig.root.transform;
            Transform bone = Bone (s, HumanBodyBones.LeftHand);
            string path = RigPaths.Body (HumanBodyBones.LeftHand);

            // 腰を完全固定にして、手を体が届かない所へ引く
            hips.ToggleLock ();
            Assert.IsTrue (hips.locked);
            Vector3 far = bone.position + (bone.position - Bone (s, HumanBodyBones.Hips).position).normalized * 2f;
            hand.SetValues (TransformChannels.Position, root.InverseTransformPoint (far), Quaternion.identity);
            s.body.CaptureInput ();
            s.body.Solve ();
            Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftHand), 0.5f, "届かない");

            // 手を離すと、点は実際の骨の位置へ合わせ直される。完全固定の点は動かない
            Vector3 hipsPoint;
            Quaternion hipsRotation;
            s.body.TryGetPointWorld (HumanBodyBones.Hips, out hipsPoint, out hipsRotation);
            Assert.IsTrue (hand.SyncToBone ());
            Assert.IsFalse (hips.SyncToBone (), "完全固定の点は合わせ直さない");
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kPositionTolerance, "点が骨の上に来る");
            Vector3 hipsAfter;
            s.body.TryGetPointWorld (HumanBodyBones.Hips, out hipsAfter, out hipsRotation);
            Assert.AreEqual (0, Vector3.Distance (hipsPoint, hipsAfter), 0f);
            Assert.IsFalse (hand.SyncToBone (), "もう合っているので直さない");

            // 打たれるキーは、骨の位置（届かなかった遠い位置ではない）
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 0, "Test")) {
                foreach (PoseTarget target in targets) ((BodyPointTarget)target).WritePendingKeys (writer);
            }
            Vector3 stored = new Vector3 (
                Value (clip, path, typeof (Transform), "m_LocalPosition.x"),
                Value (clip, path, typeof (Transform), "m_LocalPosition.y"),
                Value (clip, path, typeof (Transform), "m_LocalPosition.z"));
            Assert.LessOrEqual (Vector3.Distance (root.InverseTransformPoint (bone.position) / s.rig.humanScale, stored), kPositionTolerance);
            Assert.AreEqual (1, Value (clip, RigPaths.Body (HumanBodyBones.Hips), typeof (BodyPoint), BodyPoint.kLockedProperty), 1e-6f, "完全固定はキーで持つ");
            Assert.IsFalse (Bindings (clip).Any (b => b.path == path && b.propertyName == BodyPoint.kLockedProperty), "完全固定にしていない点には、そのカーブを立てない");

            // 完全固定の点は、届かない所に置いても合わせ直さない
            hand.ToggleLock ();
            hand.SetValues (TransformChannels.Position, root.InverseTransformPoint (far), Quaternion.identity);
            s.body.RestoreInput ();
            s.body.Solve ();
            Assert.IsFalse (hand.SyncToBone ());
            Assert.Greater (s.body.GetResidual (HumanBodyBones.LeftHand), 0.5f, "点は置いた場所に残る");

            // 完全固定を外すと固定は残り、次に手を離したときに合わせ直される。固定ごと外すと完全固定も外れる
            hand.ToggleLock ();
            Assert.IsTrue (hand.pinned);
            Assert.IsFalse (hand.locked);
            Assert.IsTrue (hand.SyncToBone ());
            hips.TogglePin ();
            Assert.IsFalse (s.body.GetLocked (HumanBodyBones.Hips));
        }

        [Test]
        public void ResetValues_ReturnsPointToLocalRest_NotRootRest ()
        {
            // 点の Reset は、点の骨を親から見た基準姿勢に戻した所へ置く（ルートから見た基準の値へは飛ばない）
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget headOffset = targets.OfType<BodyPointTarget> ().First (t => t.bone == HumanBodyBones.Head && !t.isOnBoneOrigin);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            Transform head = Bone (s, HumanBodyBones.Head);
            Transform handBone = Bone (s, HumanBodyBones.LeftHand);

            // 体を回して下げ、腕・手首・頭を曲げた姿勢（しゃがんで振り向いたような姿勢）
            hips.localPosition += new Vector3 (0, -0.3f, 0.2f);
            hips.localRotation *= Quaternion.Euler (0, 90, 0);
            Bone (s, HumanBodyBones.LeftLowerArm).localRotation *= Quaternion.Euler (0, 0, 40);
            handBone.localRotation *= Quaternion.Euler (30, 0, 0);
            head.localRotation *= Quaternion.Euler (40, 0, 0);
            Vector3 handBefore = handBone.position;

            hand.SetState (BodyPointState.Pin);
            headOffset.SetState (BodyPointState.Pin);
            hand.ResetValues (TransformChannels.All);
            headOffset.ResetValues (TransformChannels.All);
            Vector3 handPoint, headPoint;
            Quaternion handRotation, headRotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out handPoint, out handRotation);
            s.body.TryGetPointWorld (headOffset.id, out headPoint, out headRotation);
            Assert.IsTrue (hand.hasPendingKeys);
            Assert.IsTrue (hand.positionPinned, "固定の強さは変えない");
            Assert.LessOrEqual (Vector3.Distance (handBefore, handPoint), kPositionTolerance, "骨の付け根の点は、今の場所から動かない");

            // 骨を親から見た基準姿勢へ戻すと、点と骨が重なる
            foreach (Transform bone in new[] { handBone, head }) {
                Quaternion rest;
                Assert.IsTrue (s.rig.TryGetRestRotation (bone, out rest));
                bone.localRotation = rest;
            }
            Assert.Less (Quaternion.Angle (handBone.rotation, handRotation), kAngleTolerance, "手首はまっすぐ（前腕から見た基準）");
            Vector3 headBonePoint;
            s.body.TryGetBonePointWorld (headOffset.id, out headBonePoint);
            Assert.LessOrEqual (Vector3.Distance (headBonePoint, headPoint), kPositionTolerance, "頭の点は、首から見た基準の向きの頭の上");
            Vector3 rootRest;
            Quaternion unused;
            s.body.TryGetPointRestValues (HumanBodyBones.LeftHand, out rootRest, out unused);
            Assert.Greater (Vector3.Distance (s.rig.root.transform.TransformPoint (rootRest * s.rig.humanScale), handPoint), 0.1f, "ルートから見た基準の値（体から離れた所）へは置かない");
        }

        [Test]
        public void ResetValues_OfWholeLeg_MatchesLegResetFromRoot ()
        {
            // 脚の点（股関節・膝・膝の Pole・足首・足の点）をまとめて戻すと、太もも・脛・足を根から順に基準へ戻した脚の上にそろう
            // （足の点はつま先の骨のあるキャラにだけできる。膝の Pole は既定の一覧に無いので足す）
            AddPoles (definition_);
            Setup s = Create (toes: true);
            HumanBodyBones[] legBones = { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot };
            List<BodyPointTarget> leg = BodyPointTarget.Create (s.rig, s.body).OfType<BodyPointTarget> ()
                .Where (p => legBones.Contains (p.bone)).ToList ();
            Assert.Greater (leg.Count (p => !p.isOnBoneOrigin), 2, "足の点・膝の Pole がある");

            Bone (s, HumanBodyBones.Hips).localRotation *= Quaternion.Euler (0, 30, 0);
            Bone (s, HumanBodyBones.LeftUpperLeg).localRotation *= Quaternion.Euler (-60, 20, 0);
            Bone (s, HumanBodyBones.LeftLowerLeg).localRotation *= Quaternion.Euler (80, 0, 0);
            Bone (s, HumanBodyBones.LeftFoot).localRotation *= Quaternion.Euler (-20, 0, 15);

            HashSet<Transform> resetBones = new HashSet<Transform> (leg.Select (p => p.editingBone));
            foreach (BodyPointTarget point in leg) {
                point.SetState (BodyPointState.Pin);
                point.ResetValues (TransformChannels.All, resetBones);
            }

            foreach (HumanBodyBones bone in legBones) {
                Transform t = Bone (s, bone);
                Quaternion rest;
                Assert.IsTrue (s.rig.TryGetRestRotation (t, out rest));
                t.localRotation = rest;
            }
            foreach (BodyPointTarget point in leg) {
                Vector3 position, bonePoint;
                Quaternion rotation;
                s.body.TryGetPointWorld (point.id, out position, out rotation);
                s.body.TryGetBonePointWorld (point.id, out bonePoint);
                Assert.LessOrEqual (Vector3.Distance (bonePoint, position), kPositionTolerance, point.label + " の位置");
                if (s.body.PointHasRotation (point.id)) {
                    Assert.Less (Quaternion.Angle (point.editingBone.rotation, rotation), kAngleTolerance, point.label + " の向き");
                }
            }
        }

        [Test]
        public void SetLocked_PinsAtBone_AndReleaseClearsEverything ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            Transform root = s.rig.root.transform;

            // 何もしていない点を固定: 今の骨の位置に置いて、動かさない点にする
            foot.SetLocked (true);
            Assert.IsTrue (foot.locked);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftFoot), kPositionTolerance);
            // 動かした点を固定: 置いた場所のまま
            Vector3 wanted = Bone (s, HumanBodyBones.LeftHand).position + new Vector3 (0, 0.1f, 0.05f);
            hand.SetValues (TransformChannels.Position, root.InverseTransformPoint (wanted), Quaternion.identity);
            Assert.IsFalse (hand.locked);
            hand.SetLocked (true);
            Assert.IsTrue (hand.locked);
            Vector3 point;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (wanted, point), kPositionTolerance);

            // 外すと、何もしていない点に戻る（強さ 0 のキーを打つ）
            foot.SetLocked (false);
            Assert.IsFalse (foot.pinned);
            Assert.IsFalse (foot.locked);
            Assert.IsTrue (foot.hasPendingKeys);
            foot.SetLocked (false);
            Assert.IsFalse (foot.pinned, "外れている点を外しても変わらない");
        }

        [Test]
        public void MoveBy_MovesFreeAndPlacedPointsByTheSameAmount ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            Vector3 handStart = Bone (s, HumanBodyBones.LeftHand).position;
            Vector3 footPlaced = Bone (s, HumanBodyBones.LeftFoot).position + new Vector3 (0.1f, 0, 0);
            foot.SetValues (TransformChannels.Position, s.rig.root.transform.InverseTransformPoint (footPlaced), Quaternion.identity);
            foot.SetState (BodyPointState.Locked);

            Vector3 delta = new Vector3 (0.02f, 0.05f, -0.03f);
            hand.MoveBy (delta);
            foot.MoveBy (delta);
            hand.MoveBy (delta);
            foot.MoveBy (delta);

            Vector3 point;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (handStart + delta * 2, point), kPositionTolerance, "Free の点は骨の位置から動いて Pin になる");
            Assert.AreEqual (BodyPointState.Pin, hand.state);
            s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (footPlaced + delta * 2, point), kPositionTolerance, "置いてある点は、置いた場所から動く");
            Assert.AreEqual (BodyPointState.Locked, foot.state, "Locked のまま動く");
        }

        [Test]
        public void MovingParentWithChildrenSelected_PinsOnlyParent ()
        {
            // 腰と子階層の点をまとめて選び（Ctrl で子階層を選ぶのと同じ）、腰をつかんで動かす:
            // 腰だけが Pin になり、子の Free の点は Free のまま体に付いて動く。Pin の子の点は同じだけ動く
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            List<BodyPointTarget> selected = new List<BodyPointTarget> { hips };
            selected.AddRange (PreviewWindow.ChildTargets (targets, hips, null).OfType<BodyPointTarget> ());
            Assert.Contains (hand, selected);
            foot.SetState (BodyPointState.Pin);
            Vector3 footPlaced;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out footPlaced, out rotation);
            Vector3 handStart = Bone (s, HumanBodyBones.LeftHand).position;

            Vector3 delta = new Vector3 (0.05f, 0.03f, -0.04f);
            hips.MoveBy (delta);
            List<BodyPointTarget> moved = PreviewWindow.PointsMovedWith (targets, selected, hips);
            Assert.IsFalse (moved.Contains (hand), "子の Free の点は動かさない");
            Assert.Contains (foot, moved, "子の Pin の点は一緒に動かす");
            foreach (BodyPointTarget point in moved) point.MoveBy (delta);

            Assert.AreEqual (BodyPointState.Pin, hips.state, "つかんだ親は Pin");
            foreach (BodyPointTarget point in selected) {
                if (point != hips && point != foot) Assert.AreEqual (BodyPointState.Free, point.state, point.label + " は Free のまま");
            }
            Vector3 footNow;
            s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out footNow, out rotation);
            Assert.LessOrEqual (Vector3.Distance (footPlaced + delta, footNow), kPositionTolerance, "Pin の子の点は同じだけ動く");

            s.body.CaptureInput ();
            s.body.Solve ();
            Assert.LessOrEqual (Vector3.Distance (handStart + delta, Bone (s, HumanBodyBones.LeftHand).position), 1e-3f, "Free の子は体に付いて動く");
        }

        [Test]
        public void PointsMovedWith_SkipsOnlyFreePointsWhoseParentIsSelected ()
        {
            // 判定だけ（解かない）。選んだ中に親の点がある Free の点だけ一緒に動かさない（踵・足裏の点を使うので、つま先の骨のあるキャラで）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            BodyPointTarget head = PointTarget (targets, HumanBodyBones.Head);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget knee = PointTarget (targets, HumanBodyBones.LeftLowerLeg);
            BodyPointTarget ankle = PointTarget (targets, HumanBodyBones.LeftFoot);
            System.Func<string, BodyPointTarget> named = name => targets.OfType<BodyPointTarget> ().Single (p => p.id.name == name);
            BodyPointTarget heel = named ("LeftHeel");
            BodyPointTarget ball = named ("LeftSoleBall");
            // 背骨をまとめた胸の点は骨の付け根から離れた点なので、名前で引く
            BodyPointTarget chest = named ("Chest");

            // 子をつかむと、Free の親は一緒に動く
            CollectionAssert.AreEquivalent (new[] { hips }, PreviewWindow.PointsMovedWith (targets, new[] { hips, hand }, hand), "手をつかむと腰も動く");
            // 付け根の点の子には同じ骨の上の点が入る。骨の上の点の子には入らない
            CollectionAssert.IsEmpty (PreviewWindow.PointsMovedWith (targets, new[] { ankle, heel, ball }, ankle), "足首をつかむと踵・足裏は体に付いて動く");
            CollectionAssert.AreEquivalent (new[] { ankle }, PreviewWindow.PointsMovedWith (targets, new[] { ankle, heel, ball }, heel), "踵をつかむと足首は動く");
            // 連鎖: 腰（Pin）・胸（つかむ）・頭（Free）
            hips.SetState (BodyPointState.Pin);
            CollectionAssert.AreEquivalent (new[] { hips }, PreviewWindow.PointsMovedWith (targets, new[] { hips, chest, head }, chest), "頭は胸の子なので動かさない");
            // Locked の子は一緒に動く
            hips.SetState (BodyPointState.Free);
            ankle.SetState (BodyPointState.Locked);
            CollectionAssert.AreEquivalent (new[] { ankle }, PreviewWindow.PointsMovedWith (targets, new[] { hips, knee, ankle }, hips), "膝は Free のまま、Locked の足首は動く");
        }

        [Test]
        public void RelatedFkTargets_AllPointsCoverAllBones ()
        {
            // 点に関する骨: 骨は根の方へたどって最初に当たる点の乗る骨の点に属する。全部の点を選べば全部の骨（指も）になる
            Setup s = Create (fingers: true, toes: true);
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            targets.AddRange (BodyPointTarget.Create (s.rig, s.body));
            List<FkTarget> fks = targets.OfType<FkTarget> ().ToList ();
            List<BodyPointTarget> points = targets.OfType<BodyPointTarget> ().ToList ();
            CollectionAssert.AreEquivalent (fks, PreviewWindow.RelatedFkTargets (targets, points), "全部の点を選べば全部の骨");

            List<FkTarget> hand = PreviewWindow.RelatedFkTargets (targets, new[] { PointTarget (targets, HumanBodyBones.LeftHand) });
            Assert.IsTrue (hand.Any (t => t.bone == Bone (s, HumanBodyBones.LeftHand)), "点の乗る骨");
            Assert.IsTrue (hand.Any (t => t.bone == Bone (s, HumanBodyBones.LeftIndexProximal)), "点の無い指は手の点に属する");
            Assert.IsFalse (hand.Any (t => t.bone == Bone (s, HumanBodyBones.LeftLowerArm)), "親の骨は入らない");
            Assert.IsFalse (hand.Any (t => t.bone == Bone (s, HumanBodyBones.RightHand)), "ほかの枝は入らない");

            // 同じ骨の上の点（手のひらの点）でも、その骨に属する骨が入る
            BodyPointTarget palm = points.Single (p => p.id.name == "LeftPalmIndex");
            CollectionAssert.AreEquivalent (hand, PreviewWindow.RelatedFkTargets (targets, new[] { palm }));
        }

        [Test]
        public void FloorClamp_KeepsMovedPointsAboveFloor ([Values (false, true)] bool clamp)
        {
            // Floor Clamp が入っていれば、床（ワールドの Y = 0）より下へ動かした点は床の高さに置く。横の動きはそのまま（床の上を滑る）
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            foot.floorClamp = clamp;
            hand.floorClamp = clamp;
            Vector3 footStart = Bone (s, HumanBodyBones.LeftFoot).position;
            Vector3 handStart = Bone (s, HumanBodyBones.LeftHand).position;
            Vector3 delta = new Vector3 (0.05f, -(footStart.y + 0.1f), 0.02f);
            foot.MoveBy (delta);
            hand.MoveBy (new Vector3 (0, -0.1f, 0));

            Vector3 point;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out point, out rotation);
            Vector3 expected = footStart + delta;
            if (clamp) expected.y = 0;
            Assert.LessOrEqual (Vector3.Distance (expected, point), kPositionTolerance, clamp ? "床で止まり、横へは動く" : "床より下へ動く");
            if (clamp) {
                foot.MoveBy (new Vector3 (0.05f, -0.05f, 0.05f));
                Vector3 again;
                s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out again, out rotation);
                Assert.LessOrEqual (Vector3.Distance (expected + new Vector3 (0.05f, 0, 0.05f), again), kPositionTolerance, "床に着いた点をさらに斜め下へ動かすと、床の上を滑る");
            }
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (handStart + new Vector3 (0, -0.1f, 0), point), kPositionTolerance, "床より上の点はそのまま動く");
        }

        [Test]
        public void FloorClamp_MovesSelectedPointsAsOneBody ([Values (false, true)] bool clamp)
        {
            // いくつか選んで 1 つをつかんで動かすと、Floor Clamp は選んだ点をひとかたまりとして床に当てる:
            // 手をつかんで下げても、先に足が床に着いたらかたまりはそれ以上下がらない。横へは同じだけ動く（床の上を滑る）
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            foreach (BodyPointTarget target in targets.OfType<BodyPointTarget> ()) target.floorClamp = clamp;
            Vector3 handStart = Bone (s, HumanBodyBones.LeftHand).position;
            Vector3 footStart = Bone (s, HumanBodyBones.LeftFoot).position;
            Assert.Greater (handStart.y, footStart.y + 0.1f, "手は足より高い所から始める");

            // 窓と同じ流れ: つかんだ点は自分で動き（自分だけで床に当てる）、窓がかたまりとして掛け直す
            Vector3 delta = new Vector3 (0.05f, -(footStart.y + 0.1f), 0.02f);
            hand.MoveBy (delta);
            PreviewWindow.MoveSelectedPoints (targets, new[] { hand, foot }, hand, handStart, delta);

            Vector3 expected = delta;
            if (clamp) expected.y = -footStart.y;
            Vector3 point;
            Quaternion rotation;
            s.body.TryGetPointWorld (HumanBodyBones.LeftHand, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (handStart + expected, point), kPositionTolerance, clamp ? "つかんだ手は、足が床に着いた所で止まる" : "つかんだ手はそのまま動く");
            s.body.TryGetPointWorld (HumanBodyBones.LeftFoot, out point, out rotation);
            Assert.LessOrEqual (Vector3.Distance (footStart + expected, point), kPositionTolerance, clamp ? "足は床に着いて、横へは同じだけ動く" : "足は床より下へ動く");

            if (clamp) {
                // 床に着いたかたまりをさらに斜め下へ動かすと、床の上を滑る
                Vector3 more = new Vector3 (0.03f, -0.05f, -0.04f);
                Vector3 handFrom = hand.framePosition;
                Vector3 footFrom = foot.framePosition;
                hand.MoveBy (more);
                PreviewWindow.MoveSelectedPoints (targets, new[] { hand, foot }, hand, handFrom, more);
                Vector3 slide = new Vector3 (more.x, 0, more.z);
                Assert.LessOrEqual (Vector3.Distance (handFrom + slide, hand.framePosition), kPositionTolerance, "手は高さを変えずに滑る");
                Assert.LessOrEqual (Vector3.Distance (footFrom + slide, foot.framePosition), kPositionTolerance, "足は床の上を滑る");
            }
        }

        [Test]
        public void FloorClamp_KeepsSolvedContactPointsAboveFloor ([Values (false, true)] bool clamp)
        {
            // 足首を Pin にして腰を下げると、膝が曲がって脛が前へ倒れ、脛に付いて回る足のつま先が床へ潜る。
            // Floor Clamp が入っていれば、解いた姿勢の接地の点（足裏の点）を床の上に留める（10-21）。足首と腰は置いた場所のまま
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            BodyPointTarget left = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointTarget right = PointTarget (targets, HumanBodyBones.RightFoot);
            BodyPointId[] contacts = s.body.pointIds.Where (s.body.IsContact).ToArray ();
            Assert.GreaterOrEqual (contacts.Length, 8, "両足の接地の点");
            foreach (BodyPointTarget target in targets.OfType<BodyPointTarget> ()) target.floorClamp = clamp;
            s.body.floorClamp = clamp;
            left.SetState (BodyPointState.Pin);
            right.SetState (BodyPointState.Pin);
            Vector3 ankle = left.framePosition;
            Vector3 hipsTarget = hips.framePosition + new Vector3 (0, -0.25f, 0);
            hips.MoveTo (hipsTarget);

            s.body.CaptureInput ();
            s.body.Solve ();

            float lowest = float.MaxValue;
            string lowestName = null;
            foreach (BodyPointId id in contacts) {
                Vector3 position;
                s.body.TryGetBonePointWorld (id, out position);
                // 床との当たりの球の下の端で見る
                position.y -= s.body.GetRadius (id);
                if (position.y < lowest) {
                    lowest = position.y;
                    lowestName = id.name;
                }
            }
            if (clamp) Assert.GreaterOrEqual (lowest, -2e-3f, "床より下へ潜らない: " + lowestName);
            else Assert.Less (lowest, -0.01f, "Floor Clamp が無ければ、つま先が床へ潜る");
            Assert.LessOrEqual (Vector3.Distance (ankle, Bone (s, HumanBodyBones.LeftFoot).position), 1e-3f, "足首は置いた場所のまま");
            Assert.LessOrEqual (Vector3.Distance (hipsTarget, Bone (s, HumanBodyBones.Hips).position), 1e-3f, "腰は下げた場所");

            // 前後のキーで Locked の点だけを留めるとき（再生・フレームの移動）は当てない
            if (clamp) {
                s.body.RestoreInput ();
                s.body.SolveOnly (new HashSet<Transform> { s.body.GetControl (HumanBodyBones.Hips), s.body.GetControl (HumanBodyBones.LeftFoot), s.body.GetControl (HumanBodyBones.RightFoot) });
                float sunk = float.MaxValue;
                foreach (BodyPointId id in contacts) {
                    Vector3 position;
                    s.body.TryGetBonePointWorld (id, out position);
                    sunk = Mathf.Min (sunk, position.y - s.body.GetRadius (id));
                }
                Assert.Less (sunk, -0.01f, "SolveOnly では床に当てない");
            }
        }

        [Test]
        public void FloorClamp_MovesFeetWithoutJitter ([Values (BodyPointState.Free, BodyPointState.Locked)] BodyPointState feet, [Values (0, 1, 2, 3)] int direction)
        {
            // 腰の点をつかんで少しずつ動かすと、Floor Clamp は足をひとかたまりのまま床に着ける（Free の足は真上へ持ち上げ、足首を固定した足は足首まわりに回す）。
            // 一番深い点 1 つだけを床に留めていたときは、どの点が一番深いかが入れ替わるたびに足がその点まわりに回り、1 歩で 180° 跳ねていた（10-21）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            BodyPointId[] contacts = s.body.pointIds.Where (s.body.IsContact).ToArray ();
            s.body.floorClamp = true;
            PointTarget (targets, HumanBodyBones.LeftFoot).SetState (feet);
            PointTarget (targets, HumanBodyBones.RightFoot).SetState (feet);
            Vector3 start = hips.framePosition;
            Vector3 step = direction == 0 ? new Vector3 (0, -0.004f, 0) : direction == 1 ? new Vector3 (0.004f, -0.002f, 0)
                : direction == 2 ? new Vector3 (0, -0.003f, 0.003f) : new Vector3 (0, -0.003f, -0.003f);
            s.body.CaptureInput ();
            List<KeyValuePair<Vector3, Quaternion>> previous = null;
            for (int i = 0; i <= 80; i++) {
                hips.MoveTo (start + step * i);
                s.body.RestoreInput ();
                s.body.Solve ();
                foreach (BodyPointId id in contacts) {
                    Vector3 position;
                    s.body.TryGetBonePointWorld (id, out position);
                    Assert.GreaterOrEqual (position.y - s.body.GetRadius (id), -2e-3f, "床より下へ潜らない: " + id.name + " step " + i);
                }
                List<KeyValuePair<Vector3, Quaternion>> pose = Snapshot (s);
                // 1 歩目はまっすぐな脚が曲がり始めるので大きく回る（足首を Pin にして下げたときも同じ）。2 歩目からは少しずつ
                if (previous != null && i >= 2) {
                    for (int b = 0; b < pose.Count; b++) {
                        Assert.LessOrEqual (Quaternion.Angle (pose[b].Value, previous[b].Value), 8f, "1 歩で跳ねない: " + s.rig.editingBones.ElementAt (b).name + " step " + i);
                    }
                }
                previous = pose;
            }
        }

        [Test]
        public void SolePoints_HaveCollisionRadius_AndRestOnTheGround ()
        {
            // 標準の定義の足裏の点は床との当たりの球を持ち、基準姿勢で球の下の端が地面に着く高さ（半径）に置く（10-22）。足首の点は球を持たない
            Setup s = Create (toes: true);
            float scale = s.rig.humanScale;
            BodyPointId[] soles = s.body.pointIds.Where (id => id.name.EndsWith ("Heel") || id.name.EndsWith ("SoleBall") || id.name.EndsWith ("LittleToeSide") || id.name.EndsWith ("ToeTip")).ToArray ();
            Assert.AreEqual (8, soles.Length, "左右の足裏の点");
            foreach (BodyPointId id in soles) {
                Assert.AreEqual (EditRigDefinition.kSoleRadius * scale, s.body.GetRadius (id), 1e-6f, id.name);
                Vector3 rest;
                Quaternion rotation;
                s.body.TryGetPointRestValues (id, out rest, out rotation);
                Assert.AreEqual (EditRigDefinition.kSoleRadius * scale, rest.y * scale, 1e-4f, "球の下の端が地面に着く: " + id.name);
            }
            Assert.AreEqual (0, s.body.GetRadius (HumanBodyBones.LeftFoot), "足首の点は球を持たない");
            Assert.AreEqual (0, s.body.GetRadius (HumanBodyBones.LeftHand), "手の点は球を持たない");
        }

        [Test]
        public void FloorClamp_StopsMovedSolePointAtItsRadius ()
        {
            // 床との当たりの球を持つ点を床より下へ動かすと、球の下の端が床に着く高さ（半径）で止まる。横へはそのまま動く
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget heel = targets.OfType<BodyPointTarget> ().Single (p => p.id.name == "LeftHeel");
            heel.floorClamp = true;
            Assert.Greater (heel.floorRadius, 0);
            Vector3 start = heel.framePosition;
            heel.MoveBy (new Vector3 (0.03f, -0.1f, 0.02f));
            Assert.LessOrEqual (Vector3.Distance (new Vector3 (start.x + 0.03f, heel.floorRadius, start.z + 0.02f), heel.framePosition), kPositionTolerance);
        }

        [Test]
        public void FloorClamp_LowersAnkleWithTheWholeFoot ([Values (false, true)] bool raiseFirst)
        {
            // 足首を床より下へ動かすと、足が床に平らに着く高さ（基準姿勢の足首の高さ）で止まる。横へは床の上を滑る。
            // 足首だけを床まで下げられると、足裏を床の上に置くには足をつま先が上を向くまで回すしかなく、足が起きた（10-22）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget ankle = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointId[] soles = s.body.pointIds.Where (id => id.name.StartsWith ("Left") && s.body.IsContact (id) && s.body.GetRadius (id) > 0).ToArray ();
            Assert.AreEqual (4, soles.Length, "左足の足裏の点");
            foreach (BodyPointTarget target in targets.OfType<BodyPointTarget> ()) target.floorClamp = true;
            s.body.floorClamp = true;
            Vector3 start = ankle.framePosition;
            Quaternion footStart = Bone (s, HumanBodyBones.LeftFoot).rotation;
            s.body.CaptureInput ();
            if (raiseFirst) {
                ankle.MoveBy (new Vector3 (0, 0.1f, 0));
                s.body.RestoreInput ();
                s.body.Solve ();
            }
            // ドラッグと同じく少しずつ下げる
            Vector3 from = ankle.framePosition;
            for (int i = 0; i < 30; i++) {
                ankle.MoveBy (new Vector3 (0.001f, -0.01f, 0.0005f));
                s.body.RestoreInput ();
                s.body.Solve ();
            }

            Vector3 moved = ankle.framePosition - from;
            Assert.AreEqual (0.03f, moved.x, 1e-3f, "横へは床の上を滑る");
            Assert.AreEqual (0.015f, moved.z, 1e-3f, "横へは床の上を滑る");
            float lowest = float.MaxValue;
            foreach (BodyPointId id in soles) {
                Vector3 position;
                s.body.TryGetBonePointWorld (id, out position);
                float bottom = position.y - s.body.GetRadius (id);
                Assert.GreaterOrEqual (bottom, -2e-3f, "足裏の球は床に潜らない: " + id.name);
                lowest = Mathf.Min (lowest, bottom);
            }
            Assert.AreEqual (0, lowest, 2e-3f, "足は床まで下がる（足裏の球が床に着く）");
            Assert.AreEqual (start.y, ankle.framePosition.y, 1e-3f, "足首は、足が床に平らに着く高さ（基準姿勢の高さ）で止まる");
            if (!raiseFirst) {
                Assert.LessOrEqual (Quaternion.Angle (footStart, Bone (s, HumanBodyBones.LeftFoot).rotation), 1f, "足は起きない（向きを変えずに下がる）");
            }
        }

        [Test]
        public void FloorClamp_RollsRotationLockedFootFlat ([Values (false, true)] bool pinSoles)
        {
            // 向きを固定した足首（つま先を下げた向き）を下ろすと、足裏が床に潜らないよう向きの値ごと回り（つま先が着いたまま踵が下りる）、
            // 足が平らになる高さ（基準姿勢の足首の高さ）で止まる。向きを固定した足は解くときに回せないので、動かすときに向きの値を回す（10-22）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget ankle = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointId[] soles = s.body.pointIds.Where (id => id.name.StartsWith ("Left") && s.body.IsContact (id) && s.body.GetRadius (id) > 0).ToArray ();
            foreach (BodyPointTarget target in targets.OfType<BodyPointTarget> ()) target.floorClamp = true;
            s.body.floorClamp = true;
            Vector3 start = ankle.framePosition;
            s.body.CaptureInput ();
            ankle.MoveBy (new Vector3 (0, 0.15f, 0));
            ankle.SpinRotateWorld (Quaternion.AngleAxis (30, s.rig.root.transform.rotation * s.rig.bodyRight));
            Assert.IsTrue (ankle.rotationPinned, "向きを固定した");
            s.body.RestoreInput ();
            s.body.Solve ();
            Vector3 toe;
            s.body.TryGetBonePointWorld (new BodyPointId ("LeftToeTip"), out toe);
            Assert.Less (toe.y, ankle.framePosition.y - 0.03f, "つま先を下げた向き");
            // 足裏の点を Pin にしておいても、足首をつかんで動かす間は足と一緒に動く（離したときに今の場所で Pin のまま付け直す）
            List<BodyPointTarget> solePoints = targets.OfType<BodyPointTarget> ().Where (p => soles.Contains (p.id)).ToList ();
            if (pinSoles) {
                foreach (BodyPointTarget sole in solePoints) sole.SetState (BodyPointState.Pin);
            }

            for (int i = 0; i < 30; i++) {
                ankle.MoveBy (new Vector3 (0, -0.01f, 0));
                if (pinSoles) Assert.IsTrue (s.body.IsFootCarried (soles[0]), "Pin の足裏の点は足と一緒に動く");
                s.body.RestoreInput ();
                s.body.Solve ();
                foreach (BodyPointId id in soles) {
                    Vector3 position;
                    s.body.TryGetBonePointWorld (id, out position);
                    Assert.GreaterOrEqual (position.y - s.body.GetRadius (id), -2e-3f, "足裏の球は床に潜らない: " + id.name + " step " + i);
                }
            }
            Assert.AreEqual (start.y, ankle.framePosition.y, 1e-3f, "足首は、足が床に平らに着く高さで止まる");
            foreach (BodyPointId id in soles) {
                Vector3 position;
                s.body.TryGetBonePointWorld (id, out position);
                Assert.LessOrEqual (position.y - s.body.GetRadius (id), 5e-3f, "足は平らに床に着く: " + id.name);
            }
            Assert.IsTrue (ankle.rotationPinned, "向きの固定は残る");
            if (pinSoles) {
                // 離したとき（キーを打つ前に骨の場所へ付け直す）
                foreach (BodyPointTarget sole in solePoints) sole.SyncToBone ();
                s.body.ClearFootCarry ();
                foreach (BodyPointTarget sole in solePoints) {
                    Assert.AreEqual (BodyPointState.Pin, sole.state, "Pin のまま: " + sole.id.name);
                    Vector3 bone;
                    s.body.TryGetBonePointWorld (sole.id, out bone);
                    Assert.LessOrEqual (Vector3.Distance (bone, sole.framePosition), 1e-4f, "今の場所に付け直す: " + sole.id.name);
                }
            }
        }

        [Test]
        public void FloorClamp_LiftingAnkle_TurnsFootAboutLockedSoleBall ()
        {
            // 足首の向きを固定し、足裏の中心を Locked にして足首を持ち上げると、足裏の中心を中心に足首の向きの値が回り、踵が付いてくる（つま先立ちになる）。
            // 向きを固定したままだと足が回れず、踵が付いてこなかった（10-22）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget ankle = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointTarget ball = targets.OfType<BodyPointTarget> ().Single (p => p.id.name == "LeftSoleBall");
            BodyPointId heel = new BodyPointId ("LeftHeel");
            foreach (BodyPointTarget target in targets.OfType<BodyPointTarget> ()) target.floorClamp = true;
            s.body.floorClamp = true;
            ankle.SpinRotateWorld (Quaternion.identity);
            Assert.IsTrue (ankle.rotationPinned, "足首の向きを固定した");
            ball.SetState (BodyPointState.Locked);
            Vector3 ballStart = ball.framePosition;
            Vector3 heelStart;
            s.body.TryGetBonePointWorld (heel, out heelStart);
            float ankleStart = Bone (s, HumanBodyBones.LeftFoot).position.y;
            s.body.CaptureInput ();
            for (int i = 0; i < 10; i++) {
                ankle.MoveBy (new Vector3 (0, 0.01f, 0));
                s.body.RestoreInput ();
                s.body.Solve ();
            }
            Vector3 heelEnd, ballEnd;
            s.body.TryGetBonePointWorld (heel, out heelEnd);
            s.body.TryGetBonePointWorld (ball.id, out ballEnd);
            Assert.Greater (heelEnd.y - heelStart.y, 0.05f, "踵が付いてくる");
            Assert.LessOrEqual (Vector3.Distance (ballStart, ballEnd), 2e-3f, "足裏の中心は留まる");
            // 足首と足裏の中心の距離（足の長さ）は変わらないので、真上へ上げた所にぴったりは届かない。足首が上がればよい
            Assert.Greater (Bone (s, HumanBodyBones.LeftFoot).position.y - ankleStart, 0.05f, "足首が上がる");
        }

        [Test]
        public void RotatingPoint_CarriesPinnedAndLockedPoints ()
        {
            // 点を 1 つ選んで回すと骨を FK で回し、その骨より先の固定している点（Pin・Locked）も選んだ点を中心に一緒に回る（10-19）。
            // ほかの枝の固定している点は動かない。状態はそのまま
            Setup s = Create ();
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            targets.AddRange (BodyPointTarget.Create (s.rig, s.body));
            BodyPointTarget shoulder = PointTarget (targets, HumanBodyBones.LeftUpperArm);
            BodyPointTarget elbow = PointTarget (targets, HumanBodyBones.LeftLowerArm);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            elbow.SetState (BodyPointState.Pin);
            hand.SetState (BodyPointState.Locked);
            hand.SetValues (TransformChannels.Rotation, Vector3.zero, Quaternion.Euler (0, 20, 0));
            foot.SetState (BodyPointState.Locked);
            Vector3 elbowPosition, handPosition, footPosition;
            Quaternion elbowRotation, handRotation, footRotation;
            s.body.TryGetPointWorld (elbow.id, out elbowPosition, out elbowRotation);
            s.body.TryGetPointWorld (hand.id, out handPosition, out handRotation);
            s.body.TryGetPointWorld (foot.id, out footPosition, out footRotation);
            Transform fk = s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            Quaternion fkRotation = fk.localRotation;
            Vector3 pivot = Bone (s, HumanBodyBones.LeftUpperArm).position;
            Quaternion delta = Quaternion.AngleAxis (30, Vector3.up);

            PreviewWindow.RotatePointFk (targets, shoulder, delta);
            Assert.Greater (Quaternion.Angle (fkRotation, fk.localRotation), 29f, "回した点の骨は FK で回る");
            Vector3 position;
            Quaternion rotation;
            s.body.TryGetPointWorld (elbow.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (pivot + delta * (elbowPosition - pivot), position), kPositionTolerance, "Pin の肘は付いて回る");
            s.body.TryGetPointWorld (hand.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (pivot + delta * (handPosition - pivot), position), kPositionTolerance, "Locked の手は付いて回る");
            Assert.Less (Quaternion.Angle (delta * handRotation, rotation), kAngleTolerance, "向きを固定した手は向きも回る");
            s.body.TryGetPointWorld (foot.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (footPosition, position), 1e-5f, "ほかの枝の Locked の足は動かない");
            Assert.LessOrEqual (Quaternion.Angle (footRotation, rotation), 0.01f);
            Assert.AreEqual (BodyPointState.Pin, elbow.state);
            Assert.AreEqual (BodyPointState.Locked, hand.state);
        }

        [Test]
        public void LinkBone_IsTheBoneUnderTheLine ()
        {
            // 点をつなぐ線に当たる骨: 関節の点は相手（親の側）の点の骨、骨の上の点はその点の骨。腰はつなぐ相手が無い
            // （踵の点を使うので、つま先の骨のあるキャラで）
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            System.Func<string, BodyPointTarget> named = name => targets.OfType<BodyPointTarget> ().Single (p => p.id.name == name);
            Assert.AreSame (Bone (s, HumanBodyBones.LeftUpperArm), PointTarget (targets, HumanBodyBones.LeftLowerArm).linkBone, "肘 → 肩の線は上腕");
            Assert.AreSame (Bone (s, HumanBodyBones.LeftLowerLeg), PointTarget (targets, HumanBodyBones.LeftFoot).linkBone, "足首 → 膝の線は下腿");
            Assert.AreSame (Bone (s, HumanBodyBones.LeftFoot), named ("LeftHeel").linkBone, "踵 → 足首の線は足");
            Assert.IsNull (PointTarget (targets, HumanBodyBones.Hips).linkBone, "腰はつなぐ相手が無い");
            // 関節の点の無い背骨は、背骨をまとめた胸の点が関節の代わりになる（首・上腕の線が腰まで飛ばない）
            Transform chest = s.body.GetBone (HumanBodyBones.Chest);
            Assert.AreSame (chest, PointTarget (targets, HumanBodyBones.Neck).linkBone, "首 → 胸の点の線は胸の骨");
            Assert.AreSame (chest, PointTarget (targets, HumanBodyBones.LeftUpperArm).linkBone, "上腕 → 胸の点の線は胸の骨（肩は点を持たない）");
            // 背骨は腰 → お腹 → 胸とつなぐ
            BodyPointTarget stomach = named ("Stomach");
            Assert.AreSame (stomach, named ("Chest").link, "胸 → お腹");
            Assert.AreSame (PointTarget (targets, HumanBodyBones.Hips), stomach.link, "お腹 → 腰");
        }

        [Test]
        public void FkMovedFromLink_CarriesPinnedChildren_KeepsLocked ()
        {
            // 線から選んだ骨（上腕）を FK で回す: 上腕より先の Pin の点は骨と一緒に回り、Locked の点・Free の点・ほかの枝の点は置いた所のまま
            // （Free の点に手のひらの点を使うので、指の骨のあるキャラで）
            Setup s = Create (fingers: true);
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            targets.AddRange (BodyPointTarget.Create (s.rig, s.body));
            Transform upperArm = Bone (s, HumanBodyBones.LeftUpperArm);
            FkTarget fk = targets.OfType<FkTarget> ().Single (t => t.bone == upperArm);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget elbow = PointTarget (targets, HumanBodyBones.LeftLowerArm);
            BodyPointTarget foot = PointTarget (targets, HumanBodyBones.LeftFoot);
            BodyPointTarget palm = targets.OfType<BodyPointTarget> ().Single (p => p.id.name == "LeftPalmIndex");
            hand.SetState (BodyPointState.Pin);
            hand.SetValues (TransformChannels.Rotation, Vector3.zero, Quaternion.Euler (0, 20, 0));
            elbow.SetState (BodyPointState.Locked);
            foot.SetState (BodyPointState.Pin);
            Vector3 handPosition, elbowPosition, footPosition;
            Quaternion handRotation, rotation;
            s.body.TryGetPointWorld (hand.id, out handPosition, out handRotation);
            s.body.TryGetPointWorld (elbow.id, out elbowPosition, out rotation);
            s.body.TryGetPointWorld (foot.id, out footPosition, out rotation);

            Pose before = new Pose (upperArm.position, upperArm.rotation);
            Quaternion delta = Quaternion.AngleAxis (40, Vector3.forward);
            fk.SpinRotateWorld (delta);
            Assert.IsTrue (PreviewWindow.CarryPinnedPoints (targets, new Dictionary<Transform, Pose> { { upperArm, before } }));

            Vector3 position;
            s.body.TryGetPointWorld (hand.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (before.position + delta * (handPosition - before.position), position), kPositionTolerance, "Pin の手は骨と一緒に回る");
            Assert.Less (Quaternion.Angle (delta * handRotation, rotation), kAngleTolerance, "向きを固定した手は向きも回る");
            s.body.TryGetPointWorld (elbow.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (elbowPosition, position), kPositionTolerance, "Locked の肘は動かない");
            s.body.TryGetPointWorld (foot.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (footPosition, position), kPositionTolerance, "ほかの枝の Pin の点は動かない");
            Assert.AreEqual (BodyPointState.Free, palm.state, "Free の点は Free のまま（体に付いて動く）");
            Assert.AreEqual (BodyPointState.Locked, elbow.state);
        }

        [Test]
        public void FkMovedTogether_CarriesPinnedPointsOnce ()
        {
            // 上腕と前腕をまとめて動かした（Reset で腕を戻したなど）: 手は、たどって最初に当たる動かした骨（前腕）の変化だけで動く（上腕の分を二重に掛けない）
            Setup s = Create ();
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            targets.AddRange (BodyPointTarget.Create (s.rig, s.body));
            Transform upperArm = Bone (s, HumanBodyBones.LeftUpperArm);
            Transform lowerArm = Bone (s, HumanBodyBones.LeftLowerArm);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            hand.SetState (BodyPointState.Pin);
            Vector3 handPosition;
            Quaternion rotation;
            s.body.TryGetPointWorld (hand.id, out handPosition, out rotation);
            Dictionary<Transform, Pose> moved = new Dictionary<Transform, Pose> {
                { upperArm, new Pose (upperArm.position, upperArm.rotation) },
                { lowerArm, new Pose (lowerArm.position, lowerArm.rotation) },
            };
            Pose lowerBefore = moved[lowerArm];
            targets.OfType<FkTarget> ().Single (t => t.bone == upperArm).SpinRotateWorld (Quaternion.AngleAxis (30, Vector3.forward));
            targets.OfType<FkTarget> ().Single (t => t.bone == lowerArm).SpinRotateWorld (Quaternion.AngleAxis (45, Vector3.up));
            PreviewWindow.CarryPinnedPoints (targets, moved);

            Vector3 expected = lowerArm.position + lowerArm.rotation * Quaternion.Inverse (lowerBefore.rotation) * (handPosition - lowerBefore.position);
            Vector3 position;
            s.body.TryGetPointWorld (hand.id, out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (expected, position), kPositionTolerance, "手は前腕に付いた所");
        }

        [Test]
        public void FkSetValues_MovesBoneRightAway_LikeSolve ()
        {
            // 数値欄・Reset で FK の値を入れると、骨もすぐその値の姿勢になる（解き直した後と同じ。点を一緒に動かす量を骨から読むため）
            Setup s = Create ();
            PoseFk (s);
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            Transform upperArm = Bone (s, HumanBodyBones.LeftUpperArm);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            FkTarget arm = targets.OfType<FkTarget> ().Single (t => t.bone == upperArm);
            FkTarget pelvis = targets.OfType<FkTarget> ().Single (t => t.bone == hips);
            pelvis.SetValues (TransformChannels.Position | TransformChannels.Rotation, new Vector3 (0.05f, -0.1f, 0.02f), Quaternion.Euler (10, 0, 5));
            arm.SetValues (TransformChannels.Rotation, Vector3.zero, Quaternion.Euler (20, 30, -10));
            Pose hipsNow = new Pose (hips.position, hips.rotation);
            Pose armNow = new Pose (upperArm.position, upperArm.rotation);

            s.solver.Solve ();
            Assert.LessOrEqual (Vector3.Distance (hips.position, hipsNow.position), kPositionTolerance, "腰の位置");
            Assert.Less (Quaternion.Angle (hips.rotation, hipsNow.rotation), kAngleTolerance, "腰の向き");
            Assert.LessOrEqual (Vector3.Distance (upperArm.position, armNow.position), kPositionTolerance, "上腕の位置");
            Assert.Less (Quaternion.Angle (upperArm.rotation, armNow.rotation), kAngleTolerance, "上腕の向き");

            arm.ResetValues (TransformChannels.All);
            armNow = new Pose (upperArm.position, upperArm.rotation);
            s.solver.Solve ();
            Assert.Less (Quaternion.Angle (upperArm.rotation, armNow.rotation), kAngleTolerance, "Reset でも同じ");
        }

        [Test]
        public void ChildTargets_FollowBoneHierarchy ()
        {
            // Ctrl で押した物の子階層（S30）: 骨の付け根の点・骨は、その骨より先と同じ骨の上の物。付け根から離れた点は、その骨より先の物だけ
            Setup s = Create ();
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            targets.AddRange (BodyPointTarget.Create (s.rig, s.body));
            System.Func<string, BodyPointTarget> named = name => targets.OfType<BodyPointTarget> ().FirstOrDefault (t => t.id.name == name);
            System.Func<PoseTarget, bool> points = t => t is BodyPointTarget;

            List<PoseTarget> arm = PreviewWindow.ChildTargets (targets, PointTarget (targets, HumanBodyBones.LeftUpperArm), points);
            foreach (string name in new[] { "LeftLowerArm", "LeftHand" }) Assert.Contains (named (name), arm, name);
            // 手のひらの点は、このキャラに置ける物だけ
            foreach (BodyPointTarget palm in new[] { named ("LeftPalmIndex"), named ("LeftPalmLittle") }.Where (p => p != null)) Assert.Contains (palm, arm, palm.id.name);
            foreach (string name in new[] { "LeftUpperArm", "RightHand", "RightLowerArm", "Head", "Hips" }) Assert.IsFalse (arm.Contains (named (name)), name + " は子階層でない");

            List<PoseTarget> head = PreviewWindow.ChildTargets (targets, PointTarget (targets, HumanBodyBones.Head), points);
            Assert.Contains (named ("HeadDirection"), head, "付け根の点は、同じ骨の上の点も子");
            Assert.Contains (named ("HeadTop"), head);
            Assert.IsEmpty (PreviewWindow.ChildTargets (targets, named ("HeadDirection"), points), "付け根から離れた点は、同じ骨の上の点を子にしない");

            // 骨（FK）も、表示モデルの骨の階層でたどる。pickable で今つかめる物に絞る
            FkTarget elbow = targets.OfType<FkTarget> ().First (t => t.bone == Bone (s, HumanBodyBones.LeftLowerArm));
            List<PoseTarget> bones = PreviewWindow.ChildTargets (targets, elbow, t => t is FkTarget);
            Assert.IsTrue (bones.OfType<FkTarget> ().Any (t => t.bone == Bone (s, HumanBodyBones.LeftHand)), "手の骨は肘の子");
            Assert.IsFalse (bones.OfType<FkTarget> ().Any (t => t.bone == Bone (s, HumanBodyBones.LeftUpperArm)), "上腕は肘の子でない");
            Assert.IsTrue (bones.All (t => t is FkTarget), "pickable に合う物だけ");
        }

        [Test]
        public void DirectionTarget_PinsOnBonePoint_AndSyncsBackToFixedDistance ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget[] directions = targets.OfType<BodyPointTarget> ().Where (t => t.id.name.EndsWith ("Direction")).ToArray ();
            Assert.AreEqual (3, directions.Length, "腰・胸・頭の向きの点");
            BodyPointTarget head = directions.First (t => t.bone == HumanBodyBones.Head);
            BodyPointId id = head.id;
            Assert.AreNotEqual (PointTarget (targets, HumanBodyBones.Head).gameObject, head.gameObject, "頭の点とは別のコントロール");
            Assert.AreEqual (RigPaths.BodyDirection (HumanBodyBones.Head), AnimationUtility.CalculateTransformPath (head.gameObject.transform, s.rig.root.transform));
            Assert.AreEqual (TransformChannels.Position, head.GetSpinChannels (), "向きの点は回せない");

            // Pin にすると、骨の前の点の位置に置かれる
            Transform bone = Bone (s, HumanBodyBones.Head);
            Vector3 onBone;
            s.body.TryGetBonePointWorld (id, out onBone);
            float distance = Vector3.Distance (onBone, bone.position);
            Assert.Greater (distance, 0.1f);
            head.SetState (BodyPointState.Pin);
            Assert.LessOrEqual (s.body.GetResidual (id), kPositionTolerance);

            // 遠くへ動かして解く（既定の引く強さ 0 では、頭が点の方を向くだけ。S28）。手を離すと、点は骨の上の点（骨から決まった距離）へ合わせ直される
            Vector3 side = Vector3.Cross (Vector3.up, onBone - bone.position).normalized;
            head.MoveBy (side * 1.5f);
            s.body.CaptureInput ();
            s.body.Solve ();
            Vector3 target;
            Quaternion rotation;
            head.SyncToBone ();
            s.body.TryGetPointWorld (id, out target, out rotation);
            Assert.AreEqual (distance, Vector3.Distance (target, bone.position), 1e-4f, "点が骨から決まった距離に戻る");
            Assert.LessOrEqual (s.body.GetResidual (id), kPositionTolerance);

            // キーは向きの点のコントロールにだけ打たれる
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            using (CurveWriter writer = CurveWriter.Begin (clip, s.rig.root.transform, 0, "Test")) {
                foreach (PoseTarget t in targets) ((BodyPointTarget)t).WritePendingKeys (writer);
            }
            Assert.IsTrue (Bindings (clip).All (b => b.path == RigPaths.BodyDirection (HumanBodyBones.Head)));
            Assert.AreEqual (1, Value (clip, RigPaths.BodyDirection (HumanBodyBones.Head), typeof (BodyPoint), BodyPoint.kPositionWeightProperty), 1e-6f);
        }

        [Test]
        public void MirrorFrom_CopiesStateAndMirroredPlace ()
        {
            // 最後に踵の点を使うので、つま先の骨のあるキャラで
            Setup s = Create (toes: true);
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget left = PointTarget (targets, HumanBodyBones.LeftHand);
            BodyPointTarget right = PointTarget (targets, HumanBodyBones.RightHand);
            Assert.AreEqual ("Point RightHand", MirrorNames.Swap (left.mirrorKey), "左右の相手が名前で引ける");
            Vector3 normal;
            float center;
            s.rig.GetMirrorPlane (out normal, out center);
            System.Func<Vector3, Vector3> mirror = p => p - 2 * (Vector3.Dot (p, normal) - center) * normal;

            // 左手を Locked にして動かし、回す。右手へ反転すると、反転した場所・向きで Locked になる
            Vector3 position;
            Quaternion rotation;
            left.TryGetValues (out position, out rotation);
            left.SetValues (TransformChannels.Position | TransformChannels.Rotation, position + new Vector3 (0.05f, 0.1f, 0.15f), Quaternion.Euler (10, 20, 30));
            left.SetState (BodyPointState.Locked);
            Assert.IsTrue (right.MirrorFrom (left));
            Vector3 leftPosition, rightPosition;
            Quaternion leftRotation, rightRotation;
            left.TryGetValues (out leftPosition, out leftRotation);
            right.TryGetValues (out rightPosition, out rightRotation);
            Assert.LessOrEqual (Vector3.Distance (mirror (leftPosition), rightPosition), 1e-4f, "場所は左右反転");
            Assert.LessOrEqual (Quaternion.Angle (EditingRigSolver.MirrorRotation (leftRotation, normal), rightRotation), 0.01f, "向きも左右反転");
            Assert.AreEqual (BodyPointState.Locked, right.state);
            Assert.IsTrue (right.rotationPinned);
            Assert.IsTrue (right.hasPendingKeys, "反転した点はキーを打つ");

            // Free の点の反転は Free
            left.SetState (BodyPointState.Free);
            Assert.IsTrue (right.MirrorFrom (left));
            Assert.AreEqual (BodyPointState.Free, right.state);

            // 体の中心の点（腰）は自分を反転する
            BodyPointTarget hips = PointTarget (targets, HumanBodyBones.Hips);
            Assert.IsNull (MirrorNames.Swap (hips.mirrorKey), "中心の点は相手が自分");
            hips.TryGetValues (out position, out rotation);
            Vector3 moved = position + normal * 0.07f;
            hips.SetValues (TransformChannels.Position, moved, Quaternion.identity);
            Assert.IsTrue (hips.MirrorFrom (hips));
            hips.TryGetValues (out position, out rotation);
            Assert.LessOrEqual (Vector3.Distance (mirror (moved), position), 1e-4f);
            Assert.AreEqual (BodyPointState.Pin, hips.state);

            // 種類の違う点からは反転しない
            BodyPointTarget heel = targets.OfType<BodyPointTarget> ().First (t => t.id.name.EndsWith ("Heel"));
            Assert.IsFalse (heel.MirrorFrom (right));
        }

        [Test]
        public void SetState_MovesBetweenFreePinAndLocked ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = BodyPointTarget.Create (s.rig, s.body);
            BodyPointTarget hand = PointTarget (targets, HumanBodyBones.LeftHand);
            Assert.AreEqual (BodyPointState.Free, hand.state);

            hand.SetState (BodyPointState.Pin);
            Assert.AreEqual (BodyPointState.Pin, hand.state);
            Assert.LessOrEqual (s.body.GetResidual (HumanBodyBones.LeftHand), kPositionTolerance, "Free から Pin にすると、今の骨の位置に置く");
            hand.SetState (BodyPointState.Locked);
            Assert.AreEqual (BodyPointState.Locked, hand.state);
            hand.SetState (BodyPointState.Pin);
            Assert.AreEqual (BodyPointState.Pin, hand.state, "Locked から Pin にすると、留めたまま Locked だけ外れる");
            Assert.IsTrue (hand.hasPendingKeys);
            hand.SetState (BodyPointState.Free);
            Assert.AreEqual (BodyPointState.Free, hand.state);
            hand.SetState (BodyPointState.Locked);
            Assert.AreEqual (BodyPointState.Locked, hand.state, "Free からそのまま Locked にできる");
        }

        [Test]
        public void PointState_CarriesForwardUntilNextKey_WithoutInterpolation ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            string hand = RigPaths.Body (HumanBodyBones.LeftHand);
            // 5F で Pin、10F で Locked、20F で Free
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (hand, typeof (BodyPoint), BodyPoint.kPositionWeightProperty),
                new AnimationCurve (new Keyframe (5 / 60f, 1), new Keyframe (20 / 60f, 0)));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (hand, typeof (BodyPoint), BodyPoint.kLockedProperty),
                new AnimationCurve (new Keyframe (10 / 60f, 1), new Keyframe (20 / 60f, 0)));
            float[] values = new float[BodyPoint.kValueCount];
            bool keyed;

            EditingClip.GetPointState (clip, hand, 0, values, out keyed);
            Assert.AreEqual (0, values[0], "最初のキーより前は Free");
            Assert.IsFalse (keyed);
            EditingClip.GetPointState (clip, hand, 5 / 60f, values, out keyed);
            Assert.AreEqual (1, values[0]);
            Assert.AreEqual (0, values[2]);
            Assert.IsTrue (keyed, "キーを打ったフレーム");
            EditingClip.GetPointState (clip, hand, 8 / 60f, values, out keyed);
            Assert.AreEqual (1, values[0], "次のキーまで引き継ぐ");
            Assert.AreEqual (0, values[2], "Locked は 10F から（キーの間で補間しない）");
            Assert.IsFalse (keyed, "キーの無いフレーム（場所は骨の位置に置き直す）");
            EditingClip.GetPointState (clip, hand, 15.5f / 60f, values, out keyed);
            Assert.AreEqual (1, values[0], "20F へ向けて薄れない");
            Assert.AreEqual (1, values[2]);
            Assert.IsFalse (keyed);
            EditingClip.GetPointState (clip, hand, 20 / 60f, values, out keyed);
            Assert.AreEqual (0, values[0], "Free のキーから先は Free");
            Assert.AreEqual (0, values[2]);
            EditingClip.GetPointState (clip, hand, 30 / 60f, values, out keyed);
            Assert.AreEqual (0, values[0]);

            Assert.IsTrue (EditingClip.HasPointKey (clip, hand, 10 / 60f));
            Assert.IsFalse (EditingClip.HasPointKey (clip, hand, 11 / 60f));
            EditingClip.GetPointState (clip, RigPaths.Body (HumanBodyBones.Head), 8 / 60f, values, out keyed);
            Assert.AreEqual (0, values[0], "キーの無い点は Free");

            // キーを足すと作り直す
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (hand, typeof (BodyPoint), BodyPoint.kPositionWeightProperty),
                new AnimationCurve (new Keyframe (5 / 60f, 1), new Keyframe (7 / 60f, 0), new Keyframe (20 / 60f, 0)));
            EditingClip.GetPointState (clip, hand, 8 / 60f, values, out keyed);
            Assert.AreEqual (0, values[0]);
        }

        [Test]
        public void LockedSpan_IsOnlyBetweenKeysThatAreBothLocked ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            string foot = RigPaths.Body (HumanBodyBones.LeftFoot);
            string hand = RigPaths.Body (HumanBodyBones.LeftHand);
            string head = RigPaths.Body (HumanBodyBones.Head);
            System.Action<string, string, Keyframe[]> set = (path, property, keys) =>
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (BodyPoint), property), new AnimationCurve (keys));
            // 足: 0F Locked → 30F Locked → 60F Free
            set (foot, BodyPoint.kPositionWeightProperty, new[] { new Keyframe (0, 1), new Keyframe (0.5f, 1), new Keyframe (1, 0) });
            set (foot, BodyPoint.kLockedProperty, new[] { new Keyframe (0, 1), new Keyframe (0.5f, 1), new Keyframe (1, 0) });
            // 手: 0F Pin だけ（Locked でない）
            set (hand, BodyPoint.kPositionWeightProperty, new[] { new Keyframe (0, 1), new Keyframe (0.5f, 1) });
            // 頭: 0F Locked のまま次のキーが無い
            set (head, BodyPoint.kPositionWeightProperty, new[] { new Keyframe (0, 1) });
            set (head, BodyPoint.kLockedProperty, new[] { new Keyframe (0, 1) });

            Assert.IsFalse (EditingClip.IsLockedSpan (clip, foot, 0), "キーのあるフレームは解かない（キーの姿勢そのもの）");
            Assert.IsTrue (EditingClip.IsLockedSpan (clip, foot, 15 / 60f), "前後とも Locked");
            Assert.IsTrue (EditingClip.IsLockedSpan (clip, foot, 10.5f / 60f), "フレームの途中も");
            Assert.IsFalse (EditingClip.IsLockedSpan (clip, foot, 30 / 60f));
            Assert.IsFalse (EditingClip.IsLockedSpan (clip, foot, 45 / 60f), "次のキーで Free になる間は解かない");
            Assert.IsFalse (EditingClip.IsLockedSpan (clip, foot, 70 / 60f), "Free のキーの後");
            Assert.IsFalse (EditingClip.IsLockedSpan (clip, hand, 15 / 60f), "Pin の点は解かない");
            Assert.IsTrue (EditingClip.IsLockedSpan (clip, head, 15 / 60f), "次のキーが無ければ Locked が続く");
            Assert.IsFalse (EditingClip.IsLockedSpan (clip, RigPaths.Body (HumanBodyBones.RightFoot), 15 / 60f), "キーの無い点");
        }

        [Test]
        public void SolveOnly_UsesOnlyTheGivenPoints ()
        {
            Setup s = Create ();
            Transform left = Bone (s, HumanBodyBones.LeftHand);
            Transform right = Bone (s, HumanBodyBones.RightHand);
            Vector3 down = -Vector3.up * 0.15f;
            Vector3 leftTarget = left.position + down;
            Vector3 rightTarget = right.position + down;
            s.body.CapturePoint (HumanBodyBones.LeftHand);
            s.body.SetPointPositionWorld (HumanBodyBones.LeftHand, leftTarget);
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);
            s.body.SetLocked (HumanBodyBones.LeftHand, true);
            s.body.CapturePoint (HumanBodyBones.RightHand);
            s.body.SetPointPositionWorld (HumanBodyBones.RightHand, rightTarget);
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 1);

            s.body.CaptureInput ();
            s.body.SolveOnly (new HashSet<Transform> { s.body.GetControl (HumanBodyBones.LeftHand) });
            Assert.LessOrEqual (Vector3.Distance (leftTarget, left.position), 1e-3f, "渡した点は届く");
            Assert.Greater (Vector3.Distance (rightTarget, right.position), 0.1f, "渡していない点は効かない（右手は点へ引かれず、胴に付いて動くだけ）");
            Assert.AreEqual (1, s.body.GetPositionWeight (HumanBodyBones.RightHand), "点の値は変えない");

            s.body.RestoreInput ();
            s.body.Solve ();
            // T ポーズ（腕が伸びきっている）の両手は、数 mm 届かない差が残ることがある（既知）
            Assert.LessOrEqual (Vector3.Distance (rightTarget, right.position), 5e-3f, "ふつうに解けば両方届く");
        }

        [Test]
        public void SolveOnlyLimbs_MovesOnlyTheLockedLimb ()
        {
            // 左足を Locked で元の場所に置き、腰を（FK で）前へ動かした姿勢から解く。脚だけが曲がり、腰・背骨・腕は FK のまま
            Setup s = Create ();
            Transform foot = Bone (s, HumanBodyBones.LeftFoot);
            Vector3 planted = foot.position;
            s.body.CapturePoint (HumanBodyBones.LeftFoot);
            s.body.SetPositionWeight (HumanBodyBones.LeftFoot, 1);
            s.body.SetLocked (HumanBodyBones.LeftFoot, true);
            Transform hips = Bone (s, HumanBodyBones.Hips);
            hips.position += s.rig.root.transform.TransformDirection (s.rig.bodyForward) * 0.1f - Vector3.up * 0.05f;
            HumanBodyBones[] kept = { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightFoot };
            Vector3[] positions = kept.Select (b => Bone (s, b).position).ToArray ();
            Quaternion[] rotations = kept.Select (b => Bone (s, b).rotation).ToArray ();
            Quaternion thigh = Bone (s, HumanBodyBones.LeftUpperLeg).rotation;

            s.body.CaptureInput ();
            s.body.SolveOnly (new HashSet<Transform> { s.body.GetControl (HumanBodyBones.LeftFoot) }, 1, true);
            Assert.LessOrEqual (Vector3.Distance (planted, foot.position), 1e-3f, "左足は置いた場所に留まる");
            Assert.Greater (Quaternion.Angle (thigh, Bone (s, HumanBodyBones.LeftUpperLeg).rotation), 1f, "左脚が曲がる");
            for (int i = 0; i < kept.Length; i++) {
                Assert.LessOrEqual (Vector3.Distance (positions[i], Bone (s, kept[i]).position), 1e-5f, kept[i] + " は FK のまま（位置）");
                Assert.LessOrEqual (Quaternion.Angle (rotations[i], Bone (s, kept[i]).rotation), 0.01f, kept[i] + " は FK のまま（向き）");
            }

            // 手足に限らなければ、腰も一緒に動く（編集中の全身 IK）
            s.body.RestoreInput ();
            s.body.SolveOnly (new HashSet<Transform> { s.body.GetControl (HumanBodyBones.LeftFoot) });
            Assert.Greater (Vector3.Distance (positions[0], hips.position), 1e-4f, "全身で解くと腰も動く");
        }

        [Test]
        public void LockedSpan_JoinsKeyPoseWhereKeyMissesThePoint ()
        {
            // 0F と 20F で左足を Locked（同じ場所）。20F の骨のキーは脚を上げていて、足は点のキーまで届いていない
            // （ほかの点との兼ね合いで届かなかったキー）。キーの間は、20F へ向けてキーの姿勢へなめらかにつながる
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, 1);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PreviewStage stage = Keep (new PreviewStage (prefab, definition_));
            Transform pointControl = stage.fullBody.GetControl (HumanBodyBones.LeftFoot);
            Transform thighControl = stage.editingRig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperLeg));
            Vector3 point = pointControl.localPosition;
            Quaternion thighRest = thighControl.localRotation;
            Quaternion thighUp = thighRest * Quaternion.Euler (-40, 0, 0);

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            float end = 20 / 60f;
            string path = RigPaths.Body (HumanBodyBones.LeftFoot);
            System.Action<string, System.Type, string, float, float> set = (p, type, property, a, b) =>
                AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (p, type, property), AnimationCurve.Linear (0, a, end, b));
            set (path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, 1, 1);
            set (path, typeof (BodyPoint), BodyPoint.kLockedProperty, 1, 1);
            set (path, typeof (Transform), "m_LocalPosition.x", point.x, point.x);
            set (path, typeof (Transform), "m_LocalPosition.y", point.y, point.y);
            set (path, typeof (Transform), "m_LocalPosition.z", point.z, point.z);
            string thigh = RigPaths.Fk (HumanBodyBones.LeftUpperLeg);
            set (thigh, typeof (Transform), "m_LocalRotation.x", thighRest.x, thighUp.x);
            set (thigh, typeof (Transform), "m_LocalRotation.y", thighRest.y, thighUp.y);
            set (thigh, typeof (Transform), "m_LocalRotation.z", thighRest.z, thighUp.z);
            set (thigh, typeof (Transform), "m_LocalRotation.w", thighRest.w, thighUp.w);

            Transform foot = stage.editingRig.GetEditingBone (HumanBodyBones.LeftFoot);
            System.Func<float, Vector3> sample = frame => {
                stage.RestorePose ();
                stage.BlendDataClip (clip, frame / 60f, 1);
                stage.Solve ();
                stage.SolveLockedSpan (1);
                return foot.position;
            };
            Vector3 planted = sample (0);
            Vector3 keyed = sample (20);
            Assert.Greater (Vector3.Distance (planted, keyed), 0.1f, "20F のキーの姿勢では、足は点のキーから離れている");
            Assert.IsTrue (stage.lockedSpanPoints.Count == 0, "キーのフレームでは解かない");

            // キーの間は、前後のキーの姿勢の足の場所を時刻で混ぜた所（点のキーは同じ場所なので、差の分だけ動く）
            Assert.LessOrEqual (Vector3.Distance (Vector3.Lerp (planted, keyed, 0.5f), sample (10)), 2e-3f, "中間は前後のキーの姿勢の間");
            Assert.IsTrue (stage.lockedSpanPoints.Contains (pointControl));
            // キーの手前で跳ねない（1 フレームの動きは、全体の動きを等分した程度）
            float step = Vector3.Distance (planted, keyed) / 20;
            Vector3 previous = sample (0);
            for (float f = 0.5f; f <= 20; f += 0.5f) {
                Vector3 current = sample (f);
                Assert.LessOrEqual (Vector3.Distance (previous, current), step, f + "F で足が跳ねない");
                previous = current;
            }
        }

        // ---- クリップ・Override・焼き ----

        static void SetPointCurves (AnimationClip clip, Setup s, HumanBodyBones bone, Vector3 worldStart, Vector3 worldEnd, float weight)
        {
            string path = RigPaths.Body (bone);
            Transform root = s.rig.root.transform;
            Vector3 a = root.InverseTransformPoint (worldStart) / s.rig.humanScale;
            Vector3 b = root.InverseTransformPoint (worldEnd) / s.rig.humanScale;
            float end = 10 / 60f;
            clip.SetCurve (path, typeof (Transform), "localPosition.x", AnimationCurve.Linear (0, a.x, end, b.x));
            clip.SetCurve (path, typeof (Transform), "localPosition.y", AnimationCurve.Linear (0, a.y, end, b.y));
            clip.SetCurve (path, typeof (Transform), "localPosition.z", AnimationCurve.Linear (0, a.z, end, b.z));
            clip.SetCurve (path, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, AnimationCurve.Constant (0, end, weight));
        }

        [Test]
        public void ClipDrivesPoints_AndKeylessClipIsPassThrough ()
        {
            Setup s = Create ();
            ClipSampler sampler = Keep (new ClipSampler (s.rig.animator));
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 start = hand.position;
            Vector3 end = start + s.rig.root.transform.TransformDirection (s.rig.BodyToRoot (new Vector3 (-0.1f, 0.1f, 0.1f)));

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            SetPointCurves (clip, s, HumanBodyBones.RightHand, start, end, 1);

            // クリップの値が点と固定の強さへ入り、全身 IK がそこへ寄せる
            sampler.Sample (clip, 10 / 60f);
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            Assert.AreEqual (1, s.body.GetPositionWeight (HumanBodyBones.RightHand), 1e-6f, "固定の強さのカーブが受け皿に入る");
            s.body.Solve ();
            Assert.LessOrEqual (Vector3.Distance (end, hand.position), 1e-3f);

            // キーの間は位置が補間される
            sampler.Sample (clip, 5 / 60f);
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            s.body.Solve ();
            Assert.LessOrEqual (Vector3.Distance (Vector3.Lerp (start, end, 0.5f), hand.position), 1e-3f, "キーの間は点の位置が補間される");

            // 点のキーが無いクリップは、全身 IK を通しても FK だけの姿勢と同じ
            AnimationClip plain = new AnimationClip { frameRate = 60 };
            created_.Add (plain);
            Quaternion q = Quaternion.Euler (0, 30, -40);
            string fk = RigPaths.Fk (HumanBodyBones.LeftUpperArm);
            plain.SetCurve (fk, typeof (Transform), "localRotation.x", AnimationCurve.Constant (0, 1, q.x));
            plain.SetCurve (fk, typeof (Transform), "localRotation.y", AnimationCurve.Constant (0, 1, q.y));
            plain.SetCurve (fk, typeof (Transform), "localRotation.z", AnimationCurve.Constant (0, 1, q.z));
            plain.SetCurve (fk, typeof (Transform), "localRotation.w", AnimationCurve.Constant (0, 1, q.w));
            sampler.Invalidate ();
            // 前のクリップの値が残らないよう、点を基準へ戻してから当てる（ステージは ResetControlsToRest がやる）
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 0);
            sampler.Sample (plain, 0);
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            List<KeyValuePair<Vector3, Quaternion>> fkOnly = Snapshot (s);
            s.body.Solve ();
            AssertSamePose (fkOnly, s, "点のキーが無いクリップは素通し");
        }

        [Test]
        public void OverrideSupportsPointWeights_AsReplacement ()
        {
            EditorCurveBinding position = EditorCurveBinding.FloatCurve (RigPaths.Body (HumanBodyBones.LeftHand), typeof (BodyPoint), BodyPoint.kPositionWeightProperty);
            EditorCurveBinding rotation = EditorCurveBinding.FloatCurve (RigPaths.Body (HumanBodyBones.LeftHand), typeof (BodyPoint), BodyPoint.kRotationWeightProperty);
            Assert.IsTrue (OverrideApplier.IsSupportedFloat (position));
            Assert.IsTrue (OverrideApplier.IsSupportedFloat (rotation));

            // 元が 1 のクリップを、Override の 0 で外せる
            Setup s = Create ();
            s.body.SetPositionWeight (HumanBodyBones.LeftHand, 1);
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            AnimationUtility.SetEditorCurve (clip, position, AnimationCurve.Constant (0, 1, 0));
            OverrideApplier.Invalidate (clip);
            OverrideApplier.Apply (clip, OverrideMode.Additive, 1, 0, s.rig.root.transform);
            Assert.AreEqual (0, s.body.GetPositionWeight (HumanBodyBones.LeftHand), 1e-6f);
        }

        [Test]
        public void Bake_GoesThroughFullBody_AndKeylessClipBakesTheSame ()
        {
            Setup s = Create ();
            ClipSampler sampler = Keep (new ClipSampler (s.rig.animator));
            Transform hand = Bone (s, HumanBodyBones.RightHand);
            Vector3 start = hand.position;
            Vector3 end = start + s.rig.root.transform.TransformDirection (s.rig.BodyToRoot (new Vector3 (-0.1f, 0.15f, 0.1f)));

            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            SetPointCurves (clip, s, HumanBodyBones.RightHand, start, end, 1);

            AnimationClip without = new AnimationClip { frameRate = 60 };
            AnimationClip with = new AnimationClip { frameRate = 60 };
            created_.Add (without);
            created_.Add (with);
            using (HumanoidBaker baker = new HumanoidBaker (s.rig, s.solver, sampler.Sample)) {
                baker.Bake (clip, without);
            }
            using (HumanoidBaker baker = new HumanoidBaker (s.rig, s.solver, sampler.Sample)) {
                baker.afterSolve = () => s.body.Solve ();
                baker.Bake (clip, with);
            }

            // 全身 IK を通さないと腕は T ポーズのまま（焼いた版に効かない）。通すと最後のフレームで腕が曲がる
            float Muscle (AnimationClip baked, string name, float time)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (baked, EditorCurveBinding.FloatCurve ("", typeof (Animator), name));
                Assert.IsNotNull (curve, name);
                return curve.Evaluate (time);
            }
            const string kMuscle = "Right Arm Down-Up";
            float t = 10 / 60f;
            Assert.AreEqual (Muscle (without, kMuscle, 0), Muscle (without, kMuscle, t), 1e-4f, "全身 IK を通さなければ腕は動かない");
            Assert.Greater (Mathf.Abs (Muscle (with, kMuscle, t) - Muscle (with, kMuscle, 0)), 0.01f, "全身 IK を通すと、焼いた版で腕が動く");

            // 点のキーが無いクリップは、全身 IK を通しても通さなくても同じに焼ける
            AnimationClip plain = new AnimationClip { frameRate = 60 };
            created_.Add (plain);
            Quaternion q = Quaternion.Euler (0, 30, -40);
            string fk = RigPaths.Fk (HumanBodyBones.LeftUpperArm);
            plain.SetCurve (fk, typeof (Transform), "localRotation.x", AnimationCurve.Linear (0, 0, t, q.x));
            plain.SetCurve (fk, typeof (Transform), "localRotation.y", AnimationCurve.Linear (0, 0, t, q.y));
            plain.SetCurve (fk, typeof (Transform), "localRotation.z", AnimationCurve.Linear (0, 0, t, q.z));
            plain.SetCurve (fk, typeof (Transform), "localRotation.w", AnimationCurve.Linear (0, 1, t, q.w));
            sampler.Invalidate ();
            s.body.SetPositionWeight (HumanBodyBones.RightHand, 0);
            AnimationClip plainA = new AnimationClip { frameRate = 60 };
            AnimationClip plainB = new AnimationClip { frameRate = 60 };
            created_.Add (plainA);
            created_.Add (plainB);
            using (HumanoidBaker baker = new HumanoidBaker (s.rig, s.solver, sampler.Sample)) {
                baker.Bake (plain, plainA);
            }
            using (HumanoidBaker baker = new HumanoidBaker (s.rig, s.solver, sampler.Sample)) {
                baker.afterSolve = () => s.body.Solve ();
                baker.Bake (plain, plainB);
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (plainA)) {
                AnimationCurve a = AnimationUtility.GetEditorCurve (plainA, binding);
                AnimationCurve b = AnimationUtility.GetEditorCurve (plainB, binding);
                Assert.IsNotNull (b, binding.propertyName);
                for (int f = 0; f <= 10; f++) {
                    Assert.AreEqual (a.Evaluate (f / 60f), b.Evaluate (f / 60f), 0f, binding.propertyName + " @" + f);
                }
            }
        }
    }

}
