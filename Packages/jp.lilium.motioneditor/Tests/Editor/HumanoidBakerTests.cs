using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Lilium
{

    /// <summary>
    /// Humanoid への焼き（S2）
    /// </summary>
    public class HumanoidBakerTests
    {
        static readonly HumanBodyBones[] kCompared = {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

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
            public ClipSampler sampler;
            public HumanoidBaker baker;
        }

        /// <param name="rotated">prefab の中で Animator の GameObject を回して置く（Y270）</param>
        /// <param name="rigLayers">Rig の中身（表示モデルを作った後に組む）</param>
        Setup Create (bool rotated, System.Func<Animator, List<RigLayerInfo>> rigLayers = null)
        {
            Setup s = new Setup ();
            s.display = TestSkeleton.CreateHumanoid (created_, true);
            s.display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (rotated) {
                GameObject prefab = new GameObject ("Prefab");
                created_.Add (prefab);
                prefab.transform.position = new Vector3 (2, 0, -1);
                s.display.transform.SetParent (prefab.transform, false);
                s.display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            }
            s.rig = Keep (EditingRig.Create (definition_, s.display, name => new GameObject (name), rigLayers != null ? rigLayers (s.display) : null));
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.sampler = Keep (new ClipSampler (s.rig.animator));
            s.baker = Keep (new HumanoidBaker (s.rig, s.solver, s.sampler.Sample));
            Assert.IsNull (s.baker.error);
            return s;
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            return clip;
        }

        /// <summary>
        /// 0F は基準姿勢、10F は背中・頭・脚を曲げて腰を動かした姿勢（Humanoid の可動範囲に収まる大きさ）
        /// </summary>
        AnimationClip CreateEditingClip (Setup s)
        {
            AnimationClip clip = NewClip ();
            Transform root = s.rig.root.transform;
            Transform[] controls = {
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.Hips)),
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.Spine)),
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.Head)),
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperLeg)),
                s.rig.FindControl (RigPaths.Fk (HumanBodyBones.RightLowerLeg)),
            };
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 0, "Test")) {
                foreach (Transform c in controls) writer.Transform (c, TransformChannels.Position | TransformChannels.Rotation);
            }

            controls[0].localPosition = new Vector3 (0.05f, -0.1f, 0.2f);
            controls[0].localRotation = Quaternion.Euler (0, 10, 0);
            controls[1].localRotation = Quaternion.Euler (15, 0, 0);
            controls[2].localRotation = Quaternion.Euler (-10, 20, 0);
            controls[3].localRotation = Quaternion.Euler (-30, 0, 0);
            controls[4].localRotation = Quaternion.Euler (40, 0, 0);
            using (CurveWriter writer = CurveWriter.Begin (clip, root, 10, "Test")) {
                foreach (Transform c in controls) writer.Transform (c, TransformChannels.Position | TransformChannels.Rotation);
            }
            s.sampler.Invalidate ();
            return clip;
        }

        static Vector3 RootSpace (Transform root, Transform bone)
        {
            return root.InverseTransformPoint (bone.position);
        }

        [Test]
        public void ClipMuscleNamesFollowUnityNaming ()
        {
            Assert.AreEqual ("LeftHand.Thumb.1 Stretched", HumanoidBaker.ToClipMuscleName ("Left Thumb 1 Stretched"));
            Assert.AreEqual ("RightHand.Little.Spread", HumanoidBaker.ToClipMuscleName ("Right Little Spread"));
            Assert.AreEqual ("Spine Front-Back", HumanoidBaker.ToClipMuscleName ("Spine Front-Back"));
            Assert.AreEqual ("Left Arm Down-Up", HumanoidBaker.ToClipMuscleName ("Left Arm Down-Up"));
            Assert.AreEqual (HumanTrait.MuscleCount, HumanoidBaker.clipMuscleNames.Distinct ().Count ());
        }

        [Test]
        public void CurvesAreLinearAndConstantOnesAreShort ()
        {
            AnimationCurve constant = HumanoidBaker.MakeCurve (new[] { 2f, 2f, 2f }, 60);
            Assert.AreEqual (2, constant.length);
            Assert.AreEqual (2 / 60f, constant.keys[1].time, 1e-6f);

            AnimationCurve ramp = HumanoidBaker.MakeCurve (new[] { 0f, 1f, 0f }, 60);
            Assert.AreEqual (3, ramp.length);
            Assert.AreEqual (0.5f, ramp.Evaluate (0.5f / 60), 1e-4f, "フレームの間は直線");
            Assert.AreEqual (0.5f, ramp.Evaluate (1.5f / 60), 1e-4f);
        }

        /// <summary>
        /// 焼いたクリップを表示モデルの Animator で再生すると、編集用の体と同じ姿勢になる
        /// </summary>
        [Test]
        public void BakedClipReproducesEditingPose ()
        {
            Setup s = Create (true);
            AnimationClip source = CreateEditingClip (s);
            AnimationClip baked = NewClip ();

            HumanoidBaker.Result result = s.baker.Bake (source, baked);

            Assert.AreEqual (11, result.frameCount);
            Assert.AreEqual (7 + HumanTrait.MuscleCount + 4 * 7, result.curveCount, "体・muscle・手足の IK ゴール");
            Assert.IsTrue (baked.humanMotion, "muscle のカーブがあるので Humanoid のクリップになる");
            Assert.That (AnimationUtility.GetCurveBindings (baked).All (b => b.type == typeof (Animator) && b.path == ""));

            // 体の移動と向きを姿勢に焼き込んで再生する（既定では水平の移動がルートモーションになり、Root Motion を切った Animator では捨てられる）
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (baked);
            settings.loopBlendOrientation = settings.loopBlendPositionY = settings.loopBlendPositionXZ = true;
            settings.keepOriginalOrientation = settings.keepOriginalPositionY = settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings (baked, settings);

            ClipSampler player = Keep (new ClipSampler (s.display));
            Transform editingRoot = s.rig.root.transform;
            Transform displayRoot = s.display.transform;
            float worstPosition = 0;
            float worstAngle = 0;
            foreach (int frame in new[] { 0, 5, 10 }) {
                float time = frame / 60f;
                s.rig.ResetBonesToRest ();
                s.sampler.Sample (source, time);
                s.solver.Solve ();
                player.Sample (baked, time);

                foreach (HumanBodyBones bone in kCompared) {
                    Transform editing = s.rig.GetEditingBone (bone);
                    Transform display = s.display.GetBoneTransform (bone);
                    worstPosition = Mathf.Max (worstPosition, Vector3.Distance (RootSpace (editingRoot, editing), RootSpace (displayRoot, display)));
                    Quaternion editingRotation = Quaternion.Inverse (editingRoot.rotation) * editing.rotation;
                    Quaternion displayRotation = Quaternion.Inverse (displayRoot.rotation) * display.rotation;
                    worstAngle = Mathf.Max (worstAngle, Quaternion.Angle (editingRotation, displayRotation));
                }
            }

            Transform hips = s.display.GetBoneTransform (HumanBodyBones.Hips);
            Assert.That (RootSpace (displayRoot, hips).z, Is.GreaterThan (0.1f), "腰の移動がクリップに入っている（対照）");
            Assert.That (worstPosition, Is.LessThan (0.001f), "骨の位置（ルートから見た）");
            Assert.That (worstAngle, Is.LessThan (0.5f), "骨の向き");
        }

        /// <summary>
        /// Animator の GameObject が prefab の中で回っていても、体の位置と向きはルートから見た値になる
        /// </summary>
        [Test]
        public void RootChannelsDoNotDependOnPlacement ()
        {
            Setup plain = Create (false);
            Setup rotated = Create (true);
            AnimationClip plainClip = NewClip ();
            AnimationClip rotatedClip = NewClip ();

            plain.baker.Bake (NewClip (), plainClip);
            rotated.baker.Bake (NewClip (), rotatedClip);

            foreach (string property in new[] { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" }) {
                EditorCurveBinding binding = EditorCurveBinding.FloatCurve ("", typeof (Animator), property);
                float a = AnimationUtility.GetEditorCurve (plainClip, binding).Evaluate (0);
                float b = AnimationUtility.GetEditorCurve (rotatedClip, binding).Evaluate (0);
                Assert.AreEqual (a, b, 1e-4f, property);
            }
            EditorCurveBinding y = EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.y");
            Assert.That (AnimationUtility.GetEditorCurve (plainClip, y).Evaluate (0), Is.GreaterThan (0.1f), "重心は床より上");
        }

        /// <summary>
        /// 任意のプロパティ（S6）は、カーブのままゲーム prefab から見たパスへ写る
        /// </summary>
        [Test]
        public void PropertyCurvesAreCopiedToGamePaths ()
        {
            Setup s = Create (false);
            AnimationClip source = NewClip ();
            EditorCurveBinding property = EditorCurveBinding.FloatCurve (RigPaths.Props ("Body/Face"), typeof (SkinnedMeshRenderer), "blendShape.Smile");
            AnimationUtility.SetEditorCurve (source, property, AnimationCurve.Linear (0, 0, 0.5f, 100));
            AnimationClip baked = NewClip ();
            s.baker.Bake (source, baked);

            EditorCurveBinding expected = EditorCurveBinding.FloatCurve ("Body/Face", typeof (SkinnedMeshRenderer), "blendShape.Smile");
            AnimationCurve curve = AnimationUtility.GetEditorCurve (baked, expected);
            Assert.IsNotNull (curve, "ゲームのパスへ写る");
            Assert.AreEqual (50, curve.Evaluate (0.25f), 0.01f);
            Assert.IsNull (AnimationUtility.GetEditorCurve (baked, property), "Controls/Props のパスは残さない");
        }

        /// <summary>
        /// 土台が Humanoid のクリップ（H土台）なら、焼いて作らないカーブ（武器の骨・Animator の数値など）をそのまま写す。
        /// Humanoid の姿勢から作り直すもの（muscle・IK ゴール）と、受け口が除いたものは写さない
        /// </summary>
        [Test]
        public void HumanoidSourceCarriesCurvesItDoesNotBake ()
        {
            Setup s = Create (false);
            // H土台 と同じく、姿勢は外から作る（ここでは基準姿勢のまま）
            HumanoidBaker baker = Keep (new HumanoidBaker (s.rig, s.solver, s.sampler.Sample, time => { }));
            AnimationClip source = NewClip ();
            EditorCurveBinding muscle = EditorCurveBinding.FloatCurve ("", typeof (Animator), HumanoidBaker.clipMuscleNames[0]);
            EditorCurveBinding goal = EditorCurveBinding.FloatCurve ("", typeof (Animator), "LeftHandT.x");
            EditorCurveBinding parameter = EditorCurveBinding.FloatCurve ("", typeof (Animator), "Blend");
            EditorCurveBinding weapon = EditorCurveBinding.FloatCurve ("Hips/Weapon", typeof (Transform), "m_LocalRotation.z");
            EditorCurveBinding excluded = EditorCurveBinding.FloatCurve ("Hips/Excluded", typeof (Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve (source, muscle, AnimationCurve.Constant (0, 0.5f, 0.3f));
            AnimationUtility.SetEditorCurve (source, goal, AnimationCurve.Constant (0, 0.5f, 0.7f));
            AnimationUtility.SetEditorCurve (source, parameter, AnimationCurve.Linear (0, 0, 0.5f, 1));
            AnimationUtility.SetEditorCurve (source, weapon, AnimationCurve.Linear (0, 0, 0.5f, -0.25f));
            AnimationUtility.SetEditorCurve (source, excluded, AnimationCurve.Constant (0, 0.5f, 1));
            Assert.IsTrue (source.humanMotion, "muscle のカーブがあるので Humanoid のクリップ（対照）");

            AnimationClip baked = NewClip ();
            HumanoidBaker.Result result = baker.Bake (source, baked, null, new IHumanoidBakeHook[] { new FakeHook { excludedPath = "Hips/Excluded" } });

            Assert.AreEqual (-0.125f, AnimationUtility.GetEditorCurve (baked, weapon).Evaluate (0.25f), 1e-5f, "武器の骨のカーブはそのまま");
            Assert.AreEqual (0.5f, AnimationUtility.GetEditorCurve (baked, parameter).Evaluate (0.25f), 1e-5f, "Animator の数値もそのまま");
            AnimationCurve bakedGoal = AnimationUtility.GetEditorCurve (baked, goal);
            Assert.IsNotNull (bakedGoal, "IK ゴールは焼いた姿勢から作る");
            Assert.AreNotEqual (0.7f, bakedGoal.Evaluate (0.25f), "土台の IK ゴールは写さない");
            Assert.IsNull (AnimationUtility.GetEditorCurve (baked, excluded), "受け口が除いたものは写さない");
            Assert.AreNotEqual (0.3f, AnimationUtility.GetEditorCurve (baked, muscle).Evaluate (0.25f), "muscle は焼いた姿勢の値（土台の値ではない）");
            Assert.That (result.notes.Any (n => n.Contains ("そのまま写した")));
        }

        [Test]
        public void MissingAvatarIsReported ()
        {
            GameObject character = new GameObject ("NoAvatar");
            created_.Add (character);
            new GameObject ("bone").transform.SetParent (character.transform, false);
            Animator animator = character.AddComponent<Animator> ();
            EditingRig rig = Keep (EditingRig.Create (definition_, animator, name => new GameObject (name)));
            HumanoidBaker baker = Keep (new HumanoidBaker (rig, new EditingRigSolver (rig), (clip, time) => { }));
            Assert.IsNotNull (baker.error);
            Assert.Throws<System.InvalidOperationException> (() => baker.Bake (NewClip (), NewClip ()));
        }

        sealed class FakeHook : IHumanoidBakeHook
        {
            public string excludedPath;
            public int postProcessed;

            public bool ShouldBake (HumanoidBakeContext context, EditorCurveBinding binding)
            {
                return binding.path != excludedPath;
            }

            public void PostProcess (HumanoidBakeContext context, AnimationClip clip)
            {
                postProcessed++;
                Assert.IsNotNull (context.displayAnimator);
            }
        }

        static List<RigLayerInfo> UpperRig (Animator display)
        {
            Transform rig = new GameObject ("Upper Rig").transform;
            rig.SetParent (display.transform, false);
            Transform source = new GameObject ("Source").transform;
            source.SetParent (rig, false);
            source.localPosition = new Vector3 (0, 1.2f, 0.1f);
            Transform ik = new GameObject ("Hand IK").transform;
            ik.SetParent (rig, false);
            Transform target = new GameObject ("Target").transform;
            target.SetParent (ik, false);

            RigLayerInfo layer = new RigLayerInfo {
                label = "Upper Rig", active = true, weight = 0.7f, transform = rig,
                weightType = typeof (Light), weightProperty = "m_Weight", setWeight = v => { },
            };
            RigConstraintInfo over = new RigConstraintInfo {
                label = "Override", transform = rig, weight = 1,
                weightType = typeof (AudioSource), weightProperty = "m_Weight", setWeight = v => { },
            };
            over.sources.Add (source);
            RigConstraintInfo twoBone = new RigConstraintInfo {
                label = "TwoBoneIK", transform = ik, weight = 0.5f,
                weightType = typeof (AudioSource), weightProperty = "m_Weight", setWeight = v => { },
            };
            twoBone.sources.Add (target);
            layer.constraints.Add (over);
            layer.constraints.Add (twoBone);
            return new List<RigLayerInfo> { layer };
        }

        /// <summary>
        /// Rig の代理はゲーム prefab のパスへ寄せて焼く。キーの無いチャンネルも基準の値で焼き、受け口が除いたものは焼かない
        /// </summary>
        [Test]
        public void RigValuesAreBakedToGamePathsAndHooksApply ()
        {
            Setup s = Create (true, UpperRig);
            RigProxies proxies = s.rig.rigProxies;
            Assert.AreEqual (2, proxies.sources.Count);

            AnimationClip source = NewClip ();
            proxies.rigs[0].weight.value = 0.25f;
            proxies.sources[0].proxy.localPosition = new Vector3 (0.1f, 0.5f, 0);
            using (CurveWriter writer = CurveWriter.Begin (source, s.rig.root.transform, 5, "Test")) {
                proxies.WriteWeightKeys (writer);
                writer.Transform (proxies.sources[0].proxy, TransformChannels.Position);
            }
            proxies.ResetToDefaults ();
            s.sampler.Invalidate ();

            FakeHook hook = new FakeHook { excludedPath = "Upper Rig/Hand IK" };
            AnimationClip baked = NewClip ();
            HumanoidBaker.Result result = s.baker.Bake (source, baked, null, new IHumanoidBakeHook[] { hook });

            System.Func<string, System.Type, string, AnimationCurve> curve = (path, type, property) =>
                AnimationUtility.GetEditorCurve (baked, EditorCurveBinding.FloatCurve (path, type, property));
            Assert.AreEqual (0.25f, curve ("Upper Rig", typeof (Light), "m_Weight").Evaluate (5 / 60f), 1e-5f, "層の重み");
            Assert.AreEqual (1, curve ("Upper Rig", typeof (AudioSource), "m_Weight").Evaluate (0), 1e-5f, "キーの無い拘束の重みも焼く");
            Assert.AreEqual (0.5f * s.rig.humanScale, curve ("Upper Rig/Source", typeof (Transform), "m_LocalPosition.y").Evaluate (5 / 60f), 1e-4f, "位置は humanScale を掛けて戻す");
            Assert.IsNotNull (curve ("Upper Rig/Source", typeof (Transform), "m_LocalRotation.w"));
            Assert.IsNull (curve ("Upper Rig/Hand IK", typeof (AudioSource), "m_Weight"), "受け口が除いた");
            Assert.IsNotNull (curve ("Upper Rig/Hand IK/Target", typeof (Transform), "m_LocalPosition.x"), "除いたのはそのパスだけ");
            Assert.That (result.notes.Any (n => n.Contains ("Upper Rig/Hand IK")));
            Assert.AreEqual (1, hook.postProcessed);
            Assert.That (AnimationUtility.GetCurveBindings (baked).All (b => !b.path.StartsWith (RigPaths.kRoot)), "代理のパスは残さない");
            Assert.IsTrue (baked.humanMotion);
        }

        /// <summary>
        /// ストリームの上で左右を反転する（Unity フォーラムで広く使われている MirrorPose の手順）。
        /// 背骨・首・頭の左右とねじりの符号を反転し、手足と指を入れ替え、体の位置の x と向きの y・z を反転する
        /// </summary>
        struct MirrorJob : IAnimationJob
        {
            public void ProcessRootMotion (AnimationStream stream)
            {
            }

            public void ProcessAnimation (AnimationStream stream)
            {
                if (!stream.isHumanStream) return;
                AnimationHumanStream human = stream.AsHuman ();
                for (int i = 0; i < (int)BodyDof.LastBodyDof; i++) {
                    if (i % 3 != 0) Negate (human, new MuscleHandle ((BodyDof)i));
                }
                foreach (HeadDof dof in new[] { HeadDof.NeckLeftRight, HeadDof.NeckRollLeftRight, HeadDof.HeadLeftRight, HeadDof.HeadRollLeftRight, HeadDof.JawLeftRight }) {
                    Negate (human, new MuscleHandle (dof));
                }
                for (int i = 0; i < (int)ArmDof.LastArmDof; i++) Swap (human, new MuscleHandle (HumanPartDof.LeftArm, (ArmDof)i), new MuscleHandle (HumanPartDof.RightArm, (ArmDof)i));
                for (int i = 0; i < (int)LegDof.LastLegDof; i++) Swap (human, new MuscleHandle (HumanPartDof.LeftLeg, (LegDof)i), new MuscleHandle (HumanPartDof.RightLeg, (LegDof)i));
                for (int f = 0; f < 5; f++) {
                    for (int i = 0; i < (int)FingerDof.LastFingerDof; i++) {
                        Swap (human, new MuscleHandle (HumanPartDof.LeftThumb + f, (FingerDof)i), new MuscleHandle (HumanPartDof.RightThumb + f, (FingerDof)i));
                    }
                }
                Vector3 p = human.bodyLocalPosition;
                human.bodyLocalPosition = new Vector3 (-p.x, p.y, p.z);
                Vector3 e = human.bodyLocalRotation.eulerAngles;
                human.bodyLocalRotation = Quaternion.Euler (e.x, -e.y, -e.z);
            }

            static void Negate (AnimationHumanStream human, MuscleHandle handle)
            {
                human.SetMuscle (handle, -human.GetMuscle (handle));
            }

            static void Swap (AnimationHumanStream human, MuscleHandle a, MuscleHandle b)
            {
                float t = human.GetMuscle (a);
                human.SetMuscle (a, human.GetMuscle (b));
                human.SetMuscle (b, t);
            }
        }

        static void BakeIntoPose (AnimationClip clip)
        {
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (clip);
            settings.loopBlendOrientation = settings.loopBlendPositionY = settings.loopBlendPositionXZ = true;
            settings.keepOriginalOrientation = settings.keepOriginalPositionY = settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings (clip, settings);
        }

        /// <summary>
        /// クリップを反転したものを再生すると、ゲームの反転（MirrorPose）を通したのと同じ姿勢になる
        /// </summary>
        [Test]
        public void MirroredClipMatchesUnityMirrorPose ()
        {
            Setup s = Create (false);
            AnimationClip source = CreateEditingClip (s);
            AnimationClip baked = NewClip ();
            s.baker.Bake (source, baked, null, new IHumanoidBakeHook[0]);
            AnimationClip mirrored = Object.Instantiate (baked);
            created_.Add (mirrored);
            HumanoidMirror.MirrorClip (mirrored);
            BakeIntoPose (baked);
            BakeIntoPose (mirrored);

            Animator display = s.display;
            Transform root = display.transform;
            Dictionary<HumanBodyBones, Vector3> expected = new Dictionary<HumanBodyBones, Vector3> ();

            PlayableGraph graph = PlayableGraph.Create ("MirrorTest");
            try {
                graph.SetTimeUpdateMode (DirectorUpdateMode.Manual);
                AnimationClipPlayable clip = AnimationClipPlayable.Create (graph, baked);
                clip.SetApplyFootIK (false);
                AnimationScriptPlayable job = AnimationScriptPlayable.Create (graph, new MirrorJob (), 1);
                job.ConnectInput (0, clip, 0);
                job.SetInputWeight (0, 1);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create (graph, "Mirror", display);
                output.SetSourcePlayable (job);
                display.Rebind ();
                clip.SetTime (10 / 60f);
                graph.Evaluate (0);
                foreach (HumanBodyBones bone in kCompared) expected[bone] = RootSpace (root, display.GetBoneTransform (bone));
            }
            finally {
                graph.Destroy ();
            }

            ClipSampler player = Keep (new ClipSampler (display));
            player.Sample (mirrored, 10 / 60f);
            float worst = 0;
            string worstName = "";
            foreach (HumanBodyBones bone in kCompared) {
                float d = Vector3.Distance (expected[bone], RootSpace (root, display.GetBoneTransform (bone)));
                if (d > worst) {
                    worst = d;
                    worstName = bone.ToString ();
                }
            }
            Assert.That (Mathf.Abs (expected[HumanBodyBones.Head].x), Is.GreaterThan (0.01f), "左右に傾いた姿勢で比べている（対照）");
            Assert.That (worst, Is.LessThan (0.001f), worstName);
        }

        [Test]
        public void MirroringTwiceRestoresTheClip ()
        {
            Setup s = Create (false);
            AnimationClip baked = NewClip ();
            s.baker.Bake (CreateEditingClip (s), baked, null, new IHumanoidBakeHook[0]);
            AnimationClip twice = Object.Instantiate (baked);
            created_.Add (twice);
            HumanoidMirror.MirrorClip (twice);
            HumanoidMirror.MirrorClip (twice);

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (baked)) {
                AnimationCurve a = AnimationUtility.GetEditorCurve (baked, binding);
                AnimationCurve b = AnimationUtility.GetEditorCurve (twice, binding);
                Assert.IsNotNull (b, binding.propertyName);
                // IK ゴールの向きは q と -q が同じ向きなので、成分ではなく下で角度を比べる
                if (IsGoalRotation (binding.propertyName)) continue;
                Assert.AreEqual (a.Evaluate (5 / 60f), b.Evaluate (5 / 60f), 1e-5f, binding.propertyName);
            }
            foreach (string name in HumanoidGoals.kCurveNames) {
                Assert.That (Quaternion.Angle (GoalRotation (baked, name, 5 / 60f), GoalRotation (twice, name, 5 / 60f)), Is.LessThan (0.01f), name);
            }
            Assert.AreEqual (System.Array.IndexOf (HumanTrait.MuscleName, "Right Arm Down-Up"), HumanoidMirror.PartnerOf (System.Array.IndexOf (HumanTrait.MuscleName, "Left Arm Down-Up")));
            Assert.IsTrue (HumanoidMirror.IsNegated (System.Array.IndexOf (HumanTrait.MuscleName, "Spine Twist Left-Right")));
            Assert.IsFalse (HumanoidMirror.IsNegated (System.Array.IndexOf (HumanTrait.MuscleName, "Spine Front-Back")));
        }

        static bool IsGoalRotation (string property)
        {
            foreach (string name in HumanoidGoals.kCurveNames) {
                if (property.StartsWith (name + "Q.")) return true;
            }
            return false;
        }

        static Quaternion GoalRotation (AnimationClip clip, string name, float time)
        {
            System.Func<string, float> value = axis => AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve ("", typeof (Animator), name + "Q." + axis)).Evaluate (time);
            return Quaternion.Normalize (new Quaternion (value ("x"), value ("y"), value ("z"), value ("w")));
        }

        /// <summary>
        /// 焼いた IK ゴールは、焼いたクリップを再生した姿勢から Unity が求めるゴールと一致する（FBX の取り込みと同じ作り方）
        /// </summary>
        [Test]
        public void BakedGoalsMatchTheBakedPose ()
        {
            Setup s = Create (true);
            AnimationClip baked = NewClip ();
            s.baker.Bake (CreateEditingClip (s), baked, null, new IHumanoidBakeHook[0]);
            BakeIntoPose (baked);

            HumanoidGoals goals = Keep (new HumanoidGoals (s.display));
            Assert.IsNull (goals.error);
            Vector3[] fromPose = new Vector3[4];
            Quaternion[] fromPoseRotations = new Quaternion[4];
            Vector3[] inClip = new Vector3[4];
            Quaternion[] inClipRotations = new Quaternion[4];
            float worstPosition = 0;
            float worstAngle = 0;
            float moved = 0;
            Vector3 first = Vector3.zero;
            foreach (int frame in new[] { 0, 5, 10 }) {
                goals.ComputeFromClip (baked, frame / 60f, fromPose, fromPoseRotations, inClip, inClipRotations);
                if (frame == 0) first = inClip[0];
                moved = Mathf.Max (moved, Vector3.Distance (first, inClip[0]));
                for (int i = 0; i < 4; i++) {
                    worstPosition = Mathf.Max (worstPosition, Vector3.Distance (fromPose[i], inClip[i]));
                    worstAngle = Mathf.Max (worstAngle, Quaternion.Angle (fromPoseRotations[i], inClipRotations[i]));
                }
            }
            Assert.That (moved, Is.GreaterThan (0.01f), "足のゴールが動く姿勢で比べている（対照）");
            Assert.That (worstPosition, Is.LessThan (0.001f), "ゴールの位置");
            Assert.That (worstAngle, Is.LessThan (0.1f), "ゴールの向き");
        }

        /// <summary>
        /// 反転したクリップの IK ゴールは、Unity の Mirror で再生したときのゴールと一致する
        /// </summary>
        [Test]
        public void MirroredGoalsMatchUnityMirror ()
        {
            Setup s = Create (false);
            AnimationClip baked = NewClip ();
            s.baker.Bake (CreateEditingClip (s), baked, null, new IHumanoidBakeHook[0]);
            BakeIntoPose (baked);
            AnimationClip mirrored = Object.Instantiate (baked);
            created_.Add (mirrored);
            HumanoidMirror.MirrorClip (mirrored);
            AnimationClip unityMirror = Object.Instantiate (baked);
            created_.Add (unityMirror);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (unityMirror);
            settings.mirror = true;
            AnimationUtility.SetAnimationClipSettings (unityMirror, settings);

            HumanoidGoals goals = Keep (new HumanoidGoals (s.display));
            Vector3[] expected = new Vector3[4];
            Quaternion[] expectedRotations = new Quaternion[4];
            Vector3[] actual = new Vector3[4];
            Quaternion[] actualRotations = new Quaternion[4];
            float worstPosition = 0;
            float worstAngle = 0;
            foreach (int frame in new[] { 0, 5, 10 }) {
                goals.ComputeFromClip (unityMirror, frame / 60f, null, null, expected, expectedRotations);
                goals.ComputeFromClip (mirrored, frame / 60f, null, null, actual, actualRotations);
                for (int i = 0; i < 4; i++) {
                    worstPosition = Mathf.Max (worstPosition, Vector3.Distance (expected[i], actual[i]));
                    worstAngle = Mathf.Max (worstAngle, Quaternion.Angle (expectedRotations[i], actualRotations[i]));
                }
            }
            Assert.That (worstPosition, Is.LessThan (0.001f), "ゴールの位置");
            Assert.That (worstAngle, Is.LessThan (0.1f), "ゴールの向き");
        }
    }

}
