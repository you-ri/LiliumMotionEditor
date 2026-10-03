using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 全身 IK（S25）の配線: リグの定義のモード・点のコントロール・素通し・元の姿勢からの解き直し・焼きへの接続。
    /// 解く中身（硬さ・角度制限）は S25a-2 で別に確かめる
    /// </summary>
    public class FullBodyTests
    {
        const float kPositionTolerance = 1e-4f;
        const float kAngleTolerance = 0.01f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            Random.InitState (4321);
        }

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        T Keep<T> (T disposable) where T : System.IDisposable
        {
            disposables_.Add (disposable);
            return disposable;
        }

        sealed class Setup
        {
            public Animator display;
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
        }

        /// <param name="fullBody">リグの定義を全身 IK にする</param>
        /// <param name="twistFrames">骨の軸の向きをばらばらにする（体の形は同じで骨の軸だけ違うキャラ）</param>
        Setup Create (bool fullBody = true, bool withChest = true, bool twistFrames = false, bool fingers = false, float size = 1)
        {
            definition_.solver = fullBody ? RigSolver.FullBodyIk : RigSolver.TwoBoneIk;
            Setup s = new Setup ();
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            s.display = fingers ? TestSkeleton.CreateHumanoidWithFingers (created_) : TestSkeleton.CreateHumanoid (created_, withChest, size);
            s.display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            s.display.transform.SetParent (prefab.transform, false);
            s.display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            if (twistFrames) TwistFrames (s.display.GetBoneTransform (HumanBodyBones.Hips));

            s.rig = Keep (EditingRig.Create (definition_, s.display, name => new GameObject (name)));
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition_));
            return s;
        }

        static void TwistFrames (Transform hips)
        {
            Transform[] bones = hips.GetComponentsInChildren<Transform> ();
            Dictionary<Transform, Vector3> positions = bones.ToDictionary (t => t, t => t.position);
            foreach (Transform bone in bones) {
                bone.rotation = Random.rotation;
                foreach (Transform child in bone) child.position = positions[child];
            }
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

        // ---- リグの定義のモード ----

        [Test]
        public void DefaultDefinition_UsesFullBodyIk ()
        {
            Assert.AreEqual (RigSolver.FullBodyIk, definition_.solver, "新しく作る定義の既定は全身 IK");
        }

        [Test]
        public void TwoBoneIkDefinition_UsesIkChains_AndHasNoPoints ()
        {
            Setup s = Create (fullBody: false);

            Assert.AreEqual (4, s.solver.ikCount, "IK の組は今までどおり");
            Assert.AreEqual (0, s.rig.binding.points.Count, "今の IK の定義では点を並べない");
            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.LeftHand)));
            Assert.AreEqual (0, s.body.pointCount);
            Assert.IsNull (s.rig.root.transform.Find (RigPaths.kBody), "Controls/Body を作らない");
        }

        [Test]
        public void FullBodyDefinition_DisablesIkChains_AndCreatesPoints ()
        {
            Setup s = Create (fullBody: true, fingers: true);

            Assert.AreEqual (0, s.solver.ikCount, "IK の組は解かない");
            foreach (RigBinding.Ik ik in s.rig.binding.ik) {
                Assert.IsFalse (ik.enabled, ik.chain.name);
                Assert.AreEqual (RigBinding.kFullBodyIkReason, ik.disabledReason);
                Assert.IsNull (s.rig.FindControl (ik.path), "IK の組のコントロールを作らない: " + ik.chain.name);
            }
            Assert.AreEqual (4, definition_.ikChains.Count, "IK の組の定義は消さない");

            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightFoot, HumanBodyBones.LeftLowerArm }) {
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
            // 必須の骨だけのキャラ（胸・首が無い）でも、ある骨の点だけで組める
            Setup s = Create (fullBody: true, withChest: false);

            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.Chest)));
            Assert.IsNull (s.rig.FindControl (RigPaths.Body (HumanBodyBones.Neck)));
            Assert.IsTrue (s.body.HasPoint (HumanBodyBones.Spine));
            Assert.IsTrue (s.body.HasPoint (HumanBodyBones.LeftHand));
        }

        [Test]
        public void PointRestValues_AreBonePositions_WithZeroWeights ()
        {
            Setup s = Create (fullBody: true, size: 1.5f);

            foreach (BodyPointId bone in s.body.pointIds) {
                Vector3 position;
                Quaternion rotation;
                Assert.IsTrue (s.body.TryGetPointWorld (bone, out position, out rotation));
                Assert.AreEqual (0, s.body.GetPositionWeight (bone), "固定の強さは 0 から始まる: " + bone);
                RigBinding.Point bound = s.rig.binding.FindPoint (bone.name);
                if (bound.definition.anchor != BodyPointAnchor.BoneOrigin || bound.definition.offset != Vector3.zero) continue;
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

        // ---- リグの定義 ----

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
        public void Targets_FullBodyDefinitionHasPointsAndNoIkTargets ()
        {
            Setup s = Create ();
            List<PoseTarget> targets = RigTargets.Create (s.rig, s.solver);
            Assert.IsFalse (targets.Any (t => t.label.StartsWith ("IK ")), "全身 IK の定義では IK の組をつかむ対象が出ない");

            List<PoseTarget> points = BodyPointTarget.Create (s.rig, s.body);
            Assert.AreEqual (s.body.pointCount, points.Count);
            Assert.IsTrue (points.All (t => t.gameObject != null && t.anchor != null));

            // 今の IK の定義では点の対象は出ない
            Setup two = Create (fullBody: false);
            Assert.AreEqual (0, BodyPointTarget.Create (two.rig, two.body).Count);
            Assert.AreEqual (4, RigTargets.Create (two.rig, two.solver).Count (t => t.label.StartsWith ("IK ")));
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
            hand.SetValues (false, TransformChannels.Position, root.InverseTransformPoint (wanted), Quaternion.identity);
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

            hand.SpinRotateWorld (Quaternion.Euler (0, 30, 0), false);
            Assert.IsTrue (hand.rotationPinned, "回すと向きが固定される");
            Assert.IsFalse (hand.positionPinned, "回しただけでは位置は固定されない");
            // 位置だけの点は回せない
            elbow.SpinRotateWorld (Quaternion.Euler (0, 30, 0), false);
            Assert.IsFalse (elbow.pinned);
            Assert.AreEqual (TransformChannels.Position, elbow.GetSpinChannels (false));

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
            hand.SetValues (false, TransformChannels.Position, root.InverseTransformPoint (far), Quaternion.identity);
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
            hand.SetValues (false, TransformChannels.Position, root.InverseTransformPoint (far), Quaternion.identity);
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
            hand.SetValues (false, TransformChannels.Position, root.InverseTransformPoint (wanted), Quaternion.identity);
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
            foot.SetValues (false, TransformChannels.Position, s.rig.root.transform.InverseTransformPoint (footPlaced), Quaternion.identity);
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
            Assert.AreEqual (TransformChannels.Position, head.GetSpinChannels (false), "向きの点は回せない");

            // Pin にすると、骨の前の点の位置に置かれる
            Transform bone = Bone (s, HumanBodyBones.Head);
            Vector3 onBone;
            s.body.TryGetBonePointWorld (id, out onBone);
            float distance = Vector3.Distance (onBone, bone.position);
            Assert.Greater (distance, 0.1f);
            head.SetState (BodyPointState.Pin);
            Assert.LessOrEqual (s.body.GetResidual (id), kPositionTolerance);

            // 遠くへ動かして解くと、ほかの点と同じく体ごと引かれる。手を離すと、点は骨の上の点（骨から決まった距離）へ合わせ直される
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
            Setup s = Create ();
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
            left.TryGetValues (false, out position, out rotation);
            left.SetValues (false, TransformChannels.Position | TransformChannels.Rotation, position + new Vector3 (0.05f, 0.1f, 0.15f), Quaternion.Euler (10, 20, 30));
            left.SetState (BodyPointState.Locked);
            Assert.IsTrue (right.MirrorFrom (left));
            Vector3 leftPosition, rightPosition;
            Quaternion leftRotation, rightRotation;
            left.TryGetValues (false, out leftPosition, out leftRotation);
            right.TryGetValues (false, out rightPosition, out rightRotation);
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
            hips.TryGetValues (false, out position, out rotation);
            Vector3 moved = position + normal * 0.07f;
            hips.SetValues (false, TransformChannels.Position, moved, Quaternion.identity);
            Assert.IsTrue (hips.MirrorFrom (hips));
            hips.TryGetValues (false, out position, out rotation);
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
            definition_.solver = RigSolver.FullBodyIk;
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
            // 前のクリップの値が残らないよう、点を基準へ戻してから当てる（ステージは RestoreControls がやる）
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
            Assert.IsFalse (IkControl.IsAdditive (BodyPoint.kPositionWeightProperty), "固定の強さは足し合わせでなく置き換え");

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
