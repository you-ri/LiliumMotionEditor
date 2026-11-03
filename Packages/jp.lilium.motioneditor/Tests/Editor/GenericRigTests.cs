using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Humanoid でないキャラ（S93）: 開く・追加コントロールの FK と全身 IK・Generic の焼き・Generic の取り込み・骨格違いの使い回し
    /// </summary>
    public class GenericRigTests : RigTestFixture
    {
        const string kFolder = "Assets/__MktGenericRigTest";
        const float kPositionTolerance = 1e-3f;
        const float kAngleTolerance = 0.2f;

        [SetUp]
        public void CreateFolder ()
        {
            if (!AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.CreateFolder ("Assets", "__MktGenericRigTest");
        }

        [TearDown]
        public void DeleteFolder ()
        {
            DestroyCreated ();
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
        }

        EditRigDefinition CreateBoneDefinition (Animator animator)
        {
            EditRigDefinition definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            created_.Add (definition);
            definition.SetFromBones (animator);
            return definition;
        }

        GameObject Place (Animator animator)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            animator.transform.SetParent (prefab.transform, false);
            return prefab;
        }

        PoseSession Open (Animator animator, EditRigDefinition definition, string clipName)
        {
            PoseSession session = Keep (new PoseSession (Place (animator), definition, false));
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/" + clipName + ".rig.anim");
            session.clip = clip;
            return session;
        }

        static PoseDocument One (PoseDocument rest, string name, string state, Vector3 offset)
        {
            return new PoseDocument {
                frame = rest.frame,
                points = { new PosePoint { name = name, state = state, position = rest.Find (name).position + offset } },
            };
        }

        static void AssertSameBones (Transform expectedRoot, Transform actualRoot, IEnumerable<string> names, string message)
        {
            foreach (string name in names) {
                Transform expected = expectedRoot.GetComponentsInChildren<Transform> (true).First (t => t.name == name);
                Transform actual = actualRoot.GetComponentsInChildren<Transform> (true).First (t => t.name == name);
                Assert.Less (Vector3.Distance (expectedRoot.InverseTransformPoint (expected.position), actualRoot.InverseTransformPoint (actual.position)), kPositionTolerance, message + " " + name);
                Assert.Less (Quaternion.Angle (Quaternion.Inverse (expectedRoot.rotation) * expected.rotation, Quaternion.Inverse (actualRoot.rotation) * actual.rotation), kAngleTolerance, message + " " + name);
            }
        }

        static readonly string[] kBones = { "root", "hip", "chest", "leg1_L", "leg2_L", "foot_L", "arm1_R", "arm2_R", "hand_R", "tail2", "head" };

        [Test]
        public void BoneDefinitionBindsWithoutErrors ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            EditRigDefinition definition = CreateBoneDefinition (animator);

            Assert.IsEmpty (definition.Validate ());
            RigBinding binding = RigBinding.Bind (definition, animator);

            Assert.IsEmpty (binding.errors, string.Join ("\n", binding.errors));
            Assert.IsEmpty (binding.notes, string.Join ("\n", binding.notes));
            Assert.IsTrue (binding.extras.All (e => e.enabled));
            Assert.AreEqual (binding.extras.Count, binding.points.Count);
            Assert.IsTrue (binding.points.All (p => p.humanBone == HumanBodyBones.LastBone && !p.onObject));
            Assert.AreEqual (animator.transform.Find ("root"), binding.extras.Single (e => e.position).bone);
        }

        [Test]
        public void PatternMatchesTheWholeBoneName ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            EditRigDefinition definition = CreateBoneDefinition (animator);
            RigBinding binding = RigBinding.Bind (definition, animator);

            Assert.AreEqual ("tail", binding.extras.Single (e => e.control.name == "tail").bone.name, "tail2 に当たらない");
        }

        [Test]
        public void UnsolvedTopBoneIsNoted ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            EditRigDefinition definition = CreateBoneDefinition (animator);
            definition.extraControls.First (c => c.name == "root").fullBodySolve = false;
            definition.bodyPoints.RemoveAll (p => p.name == "root");

            RigBinding binding = RigBinding.Bind (definition, animator);

            Assert.IsTrue (binding.notes.Any (n => n.Contains ("hip") && n.Contains ("chest")), "根が 2 本: " + string.Join ("\n", binding.notes));
        }

        [Test]
        public void PointOnUnknownControlIsAnError ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            EditRigDefinition definition = CreateBoneDefinition (animator);
            definition.bodyPoints[0].control = "Nowhere";

            Assert.IsTrue (definition.Validate ().Any (e => e.Contains ("Nowhere")));
        }

        [Test]
        public void SolvedExtrasChangeTheHashOnlyWhenPresent ()
        {
            EditRigDefinition plain = CreateDefinition ();
            string before = plain.GetFullBodyHash ();
            plain.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Tail", pattern = "Tail" });
            Assert.AreEqual (before, plain.GetFullBodyHash (), "解かない追加コントロールは変えない");
            plain.extraControls[0].fullBodySolve = true;
            Assert.AreNotEqual (before, plain.GetFullBodyHash ());
        }

        [Test]
        public void OwnWeightChangesTheHashOnlyWhenOn ()
        {
            // 重さを自分の骨の分だけにする骨（S96）。入れない定義のハッシュは、項目を足す前と同じ
            EditRigDefinition plain = CreateDefinition ();
            plain.extraControls.Add (new EditRigDefinition.ExtraControl { name = "Tail", pattern = "Tail", fullBodySolve = true });
            string before = plain.GetFullBodyHash ();
            plain.extraControls[0].ownWeightOnly = true;
            string extra = plain.GetFullBodyHash ();
            Assert.AreNotEqual (before, extra, "追加コントロール");
            plain.FindFkControl (HumanBodyBones.Spine).ownWeightOnly = true;
            Assert.AreNotEqual (extra, plain.GetFullBodyHash (), "FK");
        }

        [TestCase (GenericAvatar.None)]
        [TestCase (GenericAvatar.Generic)]
        [TestCase (GenericAvatar.GenericWithRootNode)]
        public void OpensAndSolvesPoints (GenericAvatar avatar)
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_, avatar);
            PoseSession session = Open (animator, CreateBoneDefinition (animator), "Solve");

            Assert.IsNull (session.stage.error);
            Assert.IsNotNull (session.stage.fullBody);
            Assert.IsNull (session.stage.fullBody != null ? null : "全身 IK が無い");

            PoseDocument rest = PoseDocument.Read (session, 0, out string problem);
            Assert.IsNull (problem);
            Vector3 hand = rest.Find ("hand_R").position;
            PoseDocument planted = new PoseDocument { frame = 0 };
            foreach (string foot in new[] { "foot_L", "foot_R", "hand_L" }) {
                planted.points.Add (new PosePoint { name = foot, state = "Locked", position = rest.Find (foot).position });
            }
            planted.points.Add (new PosePoint { name = "hand_R", state = "Pin", position = hand + new Vector3 (0, 0.15f, 0.1f) });
            Assert.IsNull (PoseDocument.Write (session, planted));

            PoseDocument read = PoseDocument.Read (session, 0, out problem);
            Assert.Less (Vector3.Distance (read.Find ("hand_R").position, hand + new Vector3 (0, 0.15f, 0.1f)), 0.02f, "引いた手");
            foreach (string foot in new[] { "foot_L", "foot_R", "hand_L" }) {
                Assert.Less (Vector3.Distance (read.Find (foot).position, rest.Find (foot).position), 0.02f, "固定した足 " + foot);
            }
            Assert.IsTrue (AnimationUtility.GetCurveBindings (session.clip).Any (b => b.path == "Controls/Extra/hand_R" || b.path.StartsWith ("Controls/Body/hand_R")));
        }

        [Test]
        public void LintRunsWithoutHips ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            PoseSession session = Open (animator, CreateBoneDefinition (animator), "Lint");
            PoseDocument rest = PoseDocument.Read (session, 0, out string _);
            Assert.IsNull (PoseDocument.Write (session, One (rest, "head", "Pin", new Vector3 (0, 0.1f, 0))));
            session.Evaluate (10);
            Assert.IsNull (PoseDocument.Write (session, new PoseDocument { frame = 10, points = { new PosePoint { name = "head", state = "Pin", position = rest.Find ("head").position } } }));

            MotionLintReport report = MotionLint.Run (session, 0, 10);

            Assert.IsNull (string.IsNullOrEmpty (report.error) ? null : report.error);
        }

        [TestCase (GenericAvatar.None)]
        [TestCase (GenericAvatar.Generic)]
        [TestCase (GenericAvatar.GenericWithRootNode)]
        public void BakedClipReplaysTheSolvedPose (GenericAvatar avatar)
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_, avatar);
            PoseSession session = Open (animator, CreateBoneDefinition (animator), "Bake");
            PoseDocument rest = PoseDocument.Read (session, 0, out string _);
            Assert.IsNull (PoseDocument.Write (session, One (rest, "head", "Pin", new Vector3 (0, 0.1f, 0.05f))));
            PoseDocument moved = new PoseDocument { frame = 20, points = { new PosePoint { name = "root", state = "Pin", position = rest.Find ("root").position + new Vector3 (0, 0.3f, 0) } } };
            Assert.IsNull (PoseDocument.Write (session, moved));

            string error = session.Bake (null, out string path, out HumanoidBaker.Result result);

            Assert.IsNull (error);
            Assert.AreEqual (kFolder + "/Bake.anim", path);
            AnimationClip baked = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            Assert.IsFalse (baked.humanMotion);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (baked);
            Assert.IsFalse (bindings.Any (b => b.type == typeof (Animator)), "Humanoid のカーブは無い");
            Assert.IsTrue (bindings.Any (b => b.path == "root" && b.propertyName == "m_LocalPosition.y"), "一番上の骨の位置");
            Assert.IsFalse (bindings.Any (b => b.path == "root/hip" && b.propertyName.StartsWith ("m_LocalPosition")), "位置を持たない骨");

            // ゲームと同じく Playable で再生する
            Animator player = TestSkeleton.CreateQuadruped (created_, avatar);
            ClipSampler sampler = Keep (new ClipSampler (player));
            foreach (int frame in new[] { 0, 10, 20 }) {
                session.Evaluate (frame);
                sampler.Sample (baked, frame / 60f);
                AssertSameBones (session.stage.editingRig.root.transform, player.transform, kBones, "フレーム " + frame);
            }
        }

        /// <summary>Generic のクリップ: root の位置と各骨の回転を動かす。Animator 型のカーブ（RootT）も入れる（FBX のクリップと同じ形）</summary>
        AnimationClip CreateSourceClip (Animator animator)
        {
            AnimationClip clip = new AnimationClip { frameRate = 30 };
            created_.Add (clip);
            void Rotation (string name, Vector3 euler)
            {
                Transform bone = animator.GetComponentsInChildren<Transform> (true).First (t => t.name == name);
                string path = AnimationUtility.CalculateTransformPath (bone, animator.transform);
                Quaternion start = bone.localRotation, end = bone.localRotation * Quaternion.Euler (euler);
                string[] axes = { "x", "y", "z", "w" };
                for (int i = 0; i < 4; i++) {
                    AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), AnimationCurve.Linear (0, start[i], 20 / 30f, end[i]));
                }
            }
            Rotation ("hip", new Vector3 (-20, 0, 5));
            Rotation ("chest", new Vector3 (15, 10, 0));
            Rotation ("leg1_L", new Vector3 (40, 0, 0));
            Rotation ("leg2_L", new Vector3 (-30, 0, 0));
            Rotation ("arm1_R", new Vector3 (-35, 0, 10));
            Rotation ("head", new Vector3 (0, 30, 0));
            Rotation ("tail", new Vector3 (10, 40, 0));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("root", typeof (Transform), "m_LocalPosition.y"), AnimationCurve.Linear (0, 0, 20 / 30f, 0.5f));
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.y"), AnimationCurve.Linear (0, 0, 20 / 30f, 0.5f));
            return clip;
        }

        [TestCase (GenericAvatar.None)]
        [TestCase (GenericAvatar.GenericWithRootNode)]
        public void ImportThenBakeReproducesTheSource (GenericAvatar avatar)
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_, avatar);
            AnimationClip source = CreateSourceClip (animator);
            PoseSession session = Open (animator, CreateBoneDefinition (animator), "Import");

            Assert.IsNull (session.ImportGeneric (source, out PoseImport.Result result));

            Assert.AreEqual (41, result.frameCount, "60fps の格子");
            Assert.Less (result.maxError, 1e-3f);
            Assert.IsTrue (result.notes.Any (n => n.Contains ("1")), "Animator のカーブは取り込まない: " + string.Join ("\n", result.notes));
            Assert.IsNull (session.Bake (null, out string path, out HumanoidBaker.Result _));
            AnimationClip baked = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);

            Animator expected = TestSkeleton.CreateQuadruped (created_, avatar);
            Animator actual = TestSkeleton.CreateQuadruped (created_, avatar);
            ClipSampler expectedSampler = Keep (new ClipSampler (expected));
            ClipSampler actualSampler = Keep (new ClipSampler (actual));
            foreach (int frame in new[] { 0, 13, 40 }) {
                expectedSampler.Sample (source, frame / 60f);
                actualSampler.Sample (baked, frame / 60f);
                AssertSameBones (expected.transform, actual.transform, kBones, "フレーム " + frame);
            }
        }

        /// <summary>
        /// 同じ編集用クリップを、骨の軸・途中の骨が違う骨格で開く。コントロールの名前がそろっていれば、名前のある骨の基準姿勢からの差は同じになる
        /// </summary>
        [Test]
        public void EditingClipCarriesAcrossSkeletons ()
        {
            Animator from = TestSkeleton.CreateQuadruped (created_);
            AnimationClip source = CreateSourceClip (from);
            PoseSession sourceSession = Open (from, CreateBoneDefinition (from), "Carry");
            Assert.IsNull (sourceSession.ImportGeneric (source, out PoseImport.Result _));
            sourceSession.Save ();

            Animator to = TestSkeleton.CreateQuadruped (created_, GenericAvatar.None, true, 1.3f, 40);
            EditRigDefinition definition = CreateBoneDefinition (to);
            definition.extraControls.RemoveAll (c => c.name == "neck");
            definition.bodyPoints.RemoveAll (p => p.name == "neck");
            PoseSession target = Keep (new PoseSession (Place (to), definition, false));
            target.clip = sourceSession.clip;

            foreach (int frame in new[] { 0, 20, 40 }) {
                sourceSession.Evaluate (frame);
                target.Evaluate (frame);
                EditingRig a = sourceSession.stage.editingRig, b = target.stage.editingRig;
                foreach (string name in new[] { "hip", "chest", "leg1_L", "leg2_L", "arm1_R", "tail", "head" }) {
                    Transform boneA = a.editingBones.First (t => t.name == name), boneB = b.editingBones.First (t => t.name == name);
                    Quaternion deltaA = Quaternion.Inverse (a.root.transform.rotation) * boneA.rotation * Quaternion.Inverse (a.GetRestRootRotation (boneA));
                    Quaternion deltaB = Quaternion.Inverse (b.root.transform.rotation) * boneB.rotation * Quaternion.Inverse (b.GetRestRootRotation (boneB));
                    Assert.Less (Quaternion.Angle (deltaA, deltaB), kAngleTolerance, "フレーム " + frame + " " + name);
                }
            }
        }

        [Test]
        public void BodyScaleChangesTheHashOnlyWhenSet ()
        {
            EditRigDefinition plain = CreateDefinition ();
            string before = plain.GetFullBodyHash ();
            plain.bodyScale = 0;
            Assert.AreEqual (before, plain.GetFullBodyHash ());
            plain.bodyScale = 0.8f;
            Assert.AreNotEqual (before, plain.GetFullBodyHash ());
        }

        /// <summary>
        /// 体の大きさ（S94）の違うキャラで同じ編集用クリップを開くと、場所の動き（一番上の骨の位置）が大きさの比で伸び縮みする。回転は変わらない
        /// </summary>
        [Test]
        public void PositionsScaleWithBodyScaleAcrossSkeletons ()
        {
            Animator from = TestSkeleton.CreateQuadruped (created_);
            AnimationClip source = CreateSourceClip (from);
            EditRigDefinition fromDefinition = CreateBoneDefinition (from);
            fromDefinition.bodyScale = 0.5f;
            PoseSession sourceSession = Open (from, fromDefinition, "Scale");
            Assert.AreEqual (0.5f, sourceSession.stage.editingRig.humanScale);
            Assert.IsNull (sourceSession.ImportGeneric (source, out PoseImport.Result _));
            sourceSession.Save ();

            Animator to = TestSkeleton.CreateQuadruped (created_);
            EditRigDefinition toDefinition = CreateBoneDefinition (to);
            toDefinition.bodyScale = 1.5f;
            PoseSession target = Keep (new PoseSession (Place (to), toDefinition, false));
            target.clip = sourceSession.clip;

            foreach (int frame in new[] { 0, 20, 40 }) {
                sourceSession.Evaluate (frame);
                target.Evaluate (frame);
                EditingRig a = sourceSession.stage.editingRig, b = target.stage.editingRig;
                Transform rootA = a.editingBones.First (t => t.name == "root"), rootB = b.editingBones.First (t => t.name == "root");
                Vector3 moveA = a.root.transform.InverseTransformPoint (rootA.position) - a.GetRestRootPosition (rootA);
                Vector3 moveB = b.root.transform.InverseTransformPoint (rootB.position) - b.GetRestRootPosition (rootB);
                Assert.Less (Vector3.Distance (moveA * 3, moveB), kPositionTolerance, "フレーム " + frame);
                Transform legA = a.editingBones.First (t => t.name == "leg1_L"), legB = b.editingBones.First (t => t.name == "leg1_L");
                Assert.Less (Quaternion.Angle (legA.rotation * Quaternion.Inverse (a.GetRestRootRotation (legA)), legB.rotation * Quaternion.Inverse (b.GetRestRootRotation (legB))), kAngleTolerance, "フレーム " + frame);
            }
        }

        [Test]
        public void CommandsImportGenericClip ()
        {
            Animator animator = TestSkeleton.CreateQuadruped (created_);
            AnimationClip source = CreateSourceClip (animator);
            string path = kFolder + "/Source.anim";
            created_.Remove (source);
            AssetDatabase.CreateAsset (source, path);
            GameObject prefabObject = Place (animator);
            string prefabPath = kFolder + "/Quadruped.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset (prefabObject, prefabPath);
            Animator saved = prefab.GetComponentInChildren<Animator> ();
            EditRigDefinition definition = ScriptableObject.CreateInstance<EditRigDefinition> ();
            definition.SetFromBones (saved);
            string definitionPath = kFolder + "/Rig.asset";
            AssetDatabase.CreateAsset (definition, definitionPath);
            try {
                string open = MotionEditorCommands.Run (new MotionEditorRequest { command = "open", prefab = prefabPath, definition = definitionPath, clip = kFolder + "/Cmd.rig.anim", create = true });
                StringAssert.Contains ("\"success\":true", open);
                string import = MotionEditorCommands.Run (new MotionEditorRequest { command = "importGeneric", source = path });
                StringAssert.Contains ("\"success\":true", import);
                StringAssert.Contains ("maxError", import);
                string missing = MotionEditorCommands.Run (new MotionEditorRequest { command = "importGeneric", source = path, sourceName = "Nothing" });
                StringAssert.Contains ("\"success\":false", missing);
                string describe = MotionEditorCommands.Run (new MotionEditorRequest { command = "describe" });
                StringAssert.Contains ("\"bone\":\"leg1_L\"", describe);
            }
            finally {
                MotionEditorCommands.Close ();
            }
        }
    }

}
