using NUnit.Framework;
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
        /// <summary>TearDown で DestroyImmediate する物</summary>
        protected readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

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
                if (o != null) Object.DestroyImmediate (o);
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

        /// <summary>
        /// 既定のリグの定義（ソルバーは全身 IK）。TearDown で捨てる
        /// </summary>
        protected EditRigDefinition CreateDefinition ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            return definition;
        }

        /// <summary>
        /// テスト用の人型を作り、prefab の中に置く（PlaceInPrefab）。toes・fingers・eyes は TestSkeleton の作り分けのまま
        /// （どれも胸と首が付き、fingers と eyes は size を使わない）。fingers と toes は両方付けられる（足の点と手のひらの点を両方使うとき。
        /// このときは toeOut も使わない）。littleFingers は fingers のときだけ、小指も足す（手のひらの小指側の点が要るとき）。
        /// twistFrames なら骨の軸の向きをばらばらにする（位置は変えない。体の形は同じで骨の軸だけ違うキャラ）
        /// </summary>
        protected Animator CreateCharacter (bool withChest = true, float size = 1, bool toes = false, float toeOut = 0,
            bool fingers = false, bool eyes = false, bool twistFrames = false, bool littleFingers = false)
        {
            Animator display = eyes ? TestSkeleton.CreateHumanoidWithEyes (created_)
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
