using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 編集用リグを組むテストの土台。テストで作った物は控えておき、TearDown でまとめて捨てる
    /// （テストは自分でアセットを作って消す。デモ素材に頼らない）
    /// </summary>
    public abstract class RigTestFixture
    {
        /// <summary>TearDown で DestroyImmediate する物（アセットとして保存した物は消さない。置いたフォルダごと各テストが消す）</summary>
        protected readonly List<Object> created_ = new List<Object> ();
        /// <summary>TearDown で Dispose する物（作った順の逆に捨てる）</summary>
        protected readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        /// <summary>
        /// 作った物を捨てる。続けて別の後片付けをしたいテストクラスは、自分の TearDown から先に呼んでよい（2 度呼んでも何もしない）
        /// </summary>
        [TearDown]
        public void DestroyCreated ()
        {
            // 後から作った物ほど前の物を使っている（全身 IK はリグを、サンプラーはリグの Animator を使う）ので、逆の順に捨てる。
            // GameObject などを消すのは、それを使う物を捨て終えてから
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null && !EditorUtility.IsPersistent (o)) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        /// <summary>
        /// TearDown で Dispose する（作った順の逆に捨てる）
        /// </summary>
        protected T Keep<T> (T disposable) where T : System.IDisposable
        {
            disposables_.Add (disposable);
            return disposable;
        }

        /// <summary>空のクリップ。TearDown で捨てる</summary>
        protected AnimationClip NewClip (float frameRate = 60)
        {
            AnimationClip clip = new AnimationClip { frameRate = frameRate };
            created_.Add (clip);
            return clip;
        }

        /// <summary>全身 IK を解くテストの組（キャラ・編集用リグ・FK を解く物・全身 IK）</summary>
        protected sealed class FullBodySetup
        {
            public Animator display;
            public EditingRig rig;
            public EditingRigSolver solver;
            public FullBodyRig body;
            /// <summary>体の前（ワールド）</summary>
            public Vector3 forward;
            public Vector3 up = Vector3.up;
        }

        /// <summary>display から編集用リグと全身 IK を組む。TearDown で捨てる</summary>
        protected FullBodySetup CreateFullBody (Animator display, EditRigDefinition definition)
        {
            FullBodySetup s = new FullBodySetup { display = display };
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            s.rig = CreateEditingRig (definition, display);
            s.solver = new EditingRigSolver (s.rig);
            s.solver.Capture ();
            s.body = Keep (new FullBodyRig (s.rig, definition));
            s.forward = s.rig.root.transform.TransformDirection (s.rig.bodyForward).normalized;
            return s;
        }

        protected static Transform Bone (FullBodySetup s, HumanBodyBones bone)
        {
            return s.rig.GetEditingBone (bone);
        }

        /// <summary>点を、今の骨の位置から offset だけ動かした所に固定する</summary>
        protected static void Pin (FullBodySetup s, HumanBodyBones bone, Vector3 offset, bool rotation = false)
        {
            Pin (s, bone, offset, 1, rotation);
        }

        /// <summary>点を、今の骨の位置から offset だけ動かした所に、位置の強さ weight で固定する</summary>
        protected static void Pin (FullBodySetup s, HumanBodyBones bone, Vector3 offset, float weight, bool rotation = false)
        {
            s.body.CapturePoint (bone);
            s.body.SetPointPositionWorld (bone, Bone (s, bone).position + offset);
            s.body.SetPositionWeight (bone, weight);
            if (rotation) s.body.SetRotationWeight (bone, 1);
        }

        /// <summary>窓と同じく、キーの姿勢（基準姿勢）へ戻して、編集用リグ → 全身 IK の順に解く</summary>
        protected static void Solve (FullBodySetup s)
        {
            s.body.Unsolve ();
            s.rig.ResetBonesToRest ();
            s.solver.Solve ();
            s.body.CaptureInput ();
            s.body.Solve ();
        }

        /// <summary>編集用リグの骨のワールドの姿勢（同じキャラの 2 つの姿勢を AssertSamePose で比べる）</summary>
        protected static List<Pose> WorldPose (PoseSession session)
        {
            return session.stage.editingRig.editingBones.Select (t => new Pose (t.position, t.rotation)).ToList ();
        }

        /// <summary>骨ごとに、位置の差が position（m）以下・向きの差が angle（度）以下</summary>
        protected static void AssertSamePose (List<Pose> expected, List<Pose> actual, string message, float position, float angle)
        {
            Assert.AreEqual (expected.Count, actual.Count, message + ": 骨の数");
            for (int i = 0; i < expected.Count; i++) {
                Assert.LessOrEqual (Vector3.Distance (expected[i].position, actual[i].position), position, message + ": " + i + " 番目の骨の位置");
                Assert.LessOrEqual (Quaternion.Angle (expected[i].rotation, actual[i].rotation), angle, message + ": " + i + " 番目の骨の向き");
            }
        }

        /// <summary>
        /// キャラ（TestSkeleton で作った物）を prefab に入れ、窓を開かずに編集する PoseSession を開いて空のクリップを当てる。TearDown で捨てる
        /// </summary>
        protected PoseSession OpenSession (Animator display, EditRigDefinition definition, bool render = false)
        {
            return OpenSession (display, new RigSetup (definition), render);
        }

        /// <summary>定義と持つ物を決めて開く（S92。テストの人型は prefab のアセットではないので、キャラの設定・バリアントを引けない）</summary>
        protected PoseSession OpenSession (Animator display, RigSetup setup, bool render = false)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = Keep (new PoseSession (prefab, setup, render));
            session.clip = NewClip ();
            return session;
        }

        /// <summary>
        /// 既定のリグの定義。TearDown で捨てる。点の既定の状態はどれも Free にする（UseFreeDefaults）
        /// </summary>
        protected EditRigDefinition CreateDefinition ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            UseFreeDefaults (definition);
            return definition;
        }

        /// <summary>
        /// 点の既定の状態（defaultState）を、どれも Free にする。既定の定義の足首の点は Pin（11-08）だが、解き方を確かめるテストは
        /// キーの無い点が Free の前提で書いてある（足首が留まると、腰を引いたときなどの結果が変わる）。既定の状態そのものは PointDefaultStateTests で確かめる
        /// </summary>
        internal static void UseFreeDefaults (EditRigDefinition definition)
        {
            foreach (EditRigDefinition.BodyPoint point in definition.bodyPoints) point.defaultState = BodyPointState.Free;
        }

        /// <summary>膝・肘の Pole の、脛・前腕の向きを混ぜる割合（S34・S43）</summary>
        protected const float kPoleBlend = 0.5f;

        /// <summary>
        /// 膝・肘の Pole を定義へ足す。既定の一覧には置かない（S56）が、重さ 0 の点を Pole として解く仕組みは残っているので、それを確かめるテストで足す。
        /// 膝の Pole は太ももに乗せて膝の前、肘の Pole は上腕に乗せて肘の後ろに置き、ずれの向きは脛・前腕の向きと半分ずつ混ぜる（S34・S43）
        /// </summary>
        protected static void AddPoles (EditRigDefinition definition)
        {
            foreach (string side in new[] { "Left", "Right" }) {
                bool left = side == "Left";
                HumanBodyBones shin = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                HumanBodyBones forearm = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
                definition.bodyPoints.Add (new EditRigDefinition.BodyPoint {
                    name = side + "KneePole", bone = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, origin = shin,
                    offset = new Vector3 (0, 0, EditRigDefinition.kDirectionLength), weight = 0, link = shin.ToString (), blendBone = shin, blend = kPoleBlend,
                });
                definition.bodyPoints.Add (new EditRigDefinition.BodyPoint {
                    name = side + "ElbowPole", bone = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, origin = forearm,
                    offset = new Vector3 (0, 0, -EditRigDefinition.kDirectionLength), weight = 0, link = forearm.ToString (), blendBone = forearm, blend = kPoleBlend,
                });
            }
        }

        /// <summary>AddPoles で足した膝の Pole（thigh は太ももの骨）</summary>
        protected static BodyPointId KneePole (HumanBodyBones thigh)
        {
            return new BodyPointId (RigPaths.SideOf (thigh) + "KneePole");
        }

        /// <summary>AddPoles で足した肘の Pole（upperArm は上腕の骨）</summary>
        protected static BodyPointId ElbowPole (HumanBodyBones upperArm)
        {
            return new BodyPointId (RigPaths.SideOf (upperArm) + "ElbowPole");
        }

        /// <summary>
        /// テスト用の人型を作り、prefab の中に置く（PlaceInPrefab）。toes・fingers・eyes は TestSkeleton の作り分けのまま
        /// （どれも胸と首が付き、fingers と eyes は size を使わない）。fingers と toes は両方付けられる（足の点と手のひらの点を両方使うとき。
        /// このときは toeOut も使わない）。littleFingers は fingers のときだけ、小指も足す（手のひらの小指側の点が要るとき）。
        /// twistFrames なら骨の軸の向きをばらばらにする（位置は変えない。体の形は同じで骨の軸だけ違うキャラ）。
        /// shoulders なら胸と首と両肩（鎖骨）を持つ人型（ほかの作り分けより先に見る）
        /// </summary>
        protected Animator CreateCharacter (bool withChest = true, float size = 1, bool toes = false, float toeOut = 0,
            bool fingers = false, bool eyes = false, bool twistFrames = false, bool littleFingers = false, bool shoulders = false)
        {
            Animator display = shoulders ? TestSkeleton.CreateHumanoidWithShoulders (created_)
                : eyes ? TestSkeleton.CreateHumanoidWithEyes (created_)
                : fingers ? TestSkeleton.CreateHumanoidWithFingers (created_, toes, littleFingers)
                : toes ? TestSkeleton.CreateHumanoidWithToes (created_, size, toeOut)
                : TestSkeleton.CreateHumanoid (created_, withChest, size);
            PlaceInPrefab (display);
            if (twistFrames) TwistFrames (display.GetBoneTransform (HumanBodyBones.Hips));
            return display;
        }

        /// <summary>
        /// キャラの prefab でよくあるように、Animator の GameObject を prefab の子にして Y270 に回して置く
        /// （ルートの向きが編集用リグへ写ることも、ついでに確かめられる）
        /// </summary>
        protected Animator PlaceInPrefab (Animator display, Vector3 prefabPosition = default)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            prefab.transform.position = prefabPosition;
            display.transform.SetParent (prefab.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, 270, 0);
            return display;
        }

        /// <summary>
        /// 編集用リグを組む。TearDown で Dispose する
        /// </summary>
        protected EditingRig CreateEditingRig (EditRigDefinition definition, Animator display, IList<RigLayerInfo> rigLayers = null)
        {
            return Keep (EditingRig.Create (definition, display, name => new GameObject (name), rigLayers));
        }

        static void TwistFrames (Transform hips)
        {
            // 回す前に全部の骨の位置を取っておく（親を回すと孫まで動くので、先に記録しないと形が崩れる）
            Transform[] bones = hips.GetComponentsInChildren<Transform> ();
            Dictionary<Transform, Vector3> positions = bones.ToDictionary (t => t, t => t.position);
            foreach (Transform bone in bones) {
                bone.rotation = Random.rotation;
                foreach (Transform child in bone) child.position = positions[child];
            }
        }

        /// <summary>
        /// 骨の姿勢を記録する（ルートから見た値）
        /// </summary>
        protected static Dictionary<Transform, (Vector3, Quaternion)> Snapshot (EditingRig rig)
        {
            Transform root = rig.root.transform;
            return rig.editingBones.ToDictionary (b => b, b => (root.InverseTransformPoint (b.position), Quaternion.Inverse (root.rotation) * b.rotation));
        }

        protected static void AssertSamePose (Dictionary<Transform, (Vector3, Quaternion)> expected, EditingRig rig, float positionTolerance, float angleTolerance)
        {
            Dictionary<Transform, (Vector3, Quaternion)> actual = Snapshot (rig);
            foreach (KeyValuePair<Transform, (Vector3, Quaternion)> pair in expected) {
                (Vector3 position, Quaternion rotation) = actual[pair.Key];
                Assert.That (Vector3.Distance (pair.Value.Item1, position), Is.LessThan (positionTolerance), pair.Key.name + " の位置");
                Assert.That (Quaternion.Angle (pair.Value.Item2, rotation), Is.LessThan (angleTolerance), pair.Key.name + " の回転");
            }
        }
    }

}
