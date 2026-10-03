using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Lilium
{

    /// <summary>
    /// 姿勢を入れるグラフ（S4）。Rigging はここでは組まないので、「入れた骨の値がそのまま出る」ことと
    /// 後段の受け口が呼ばれることを見る
    /// </summary>
    public class PoseGraphTests
    {
        sealed class Hook : IPoseGraphHook
        {
            public int calls;
            public int updates;
            public GameObject model;
            public Animator animator;
            public Pose rest;
            public Transform restOf;
            /// <summary>false なら何も足さない（input をそのまま返す）</summary>
            public bool append = true;
            public Playable appended;

            public System.Type layerType { get { return null; } }

            public Playable Append (PoseGraphContext context)
            {
                calls++;
                model = context.model;
                animator = context.animator;
                if (restOf != null) rest = context.GetRestLocalPose (restOf);
                if (!append) return context.input;
                appended = AnimationScriptPlayable.Create (context.graph, new PassJob ());
                appended.AddInput (context.input, 0, 1);
                return appended;
            }

            public void Update (Playable appended, PoseGraphState state)
            {
                updates++;
            }

            public int releases;

            public void Release (Playable appended)
            {
                releases++;
            }
        }

        /// <summary>差す位置を選ぶ受け口。受け取った前段（context.input）を覚える</summary>
        sealed class StagedHook : IPoseGraphHookStage
        {
            public PoseGraphHookStage stage { get; set; }
            public Playable input;
            public Playable appended;

            public System.Type layerType { get { return null; } }

            public Playable Append (PoseGraphContext context)
            {
                input = context.input;
                appended = AnimationScriptPlayable.Create (context.graph, new PassJob ());
                appended.AddInput (context.input, 0, 1);
                return appended;
            }

            public void Update (Playable appended, PoseGraphState state) { }
            public void Release (Playable appended) { }
        }

        struct PassJob : IAnimationJob
        {
            public void ProcessRootMotion (AnimationStream stream) { }
            public void ProcessAnimation (AnimationStream stream) { }
        }

        static GameObject CreateModel (out Animator animator, out Transform bone)
        {
            GameObject root = new GameObject ("Model");
            animator = root.AddComponent<Animator> ();
            bone = new GameObject ("Bone").transform;
            bone.SetParent (root.transform, false);
            return root;
        }

        [Test]
        public void InjectedBoneValuesSurviveTheGraph ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            PoseGraph graph = null;
            try {
                graph = PoseGraph.Create (animator, root, new Transform[] { root.transform, bone });
                Assert.IsNotNull (graph, "骨が 1 本でもあれば作れる");
                Assert.IsFalse (graph.hasRig, "Rig を持たないキャラ");

                bone.localRotation = Quaternion.Euler (0, 30, 0);
                bone.localPosition = new Vector3 (0, 1, 0);
                graph.Evaluate ();

                Assert.AreEqual (0f, Quaternion.Angle (Quaternion.Euler (0, 30, 0), bone.localRotation), 0.01f, "入れた向きがそのまま出る");
                Assert.AreEqual (1f, bone.localPosition.y, 0.001f);
            }
            finally {
                if (graph != null) graph.Dispose ();
                Object.DestroyImmediate (root);
            }
        }

        [Test]
        public void WithoutBonesThereIsNoGraph ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            try {
                // Animator 自身はストリームの根なので、骨として数えない
                Assert.IsNull (PoseGraph.Create (animator, root, new Transform[] { root.transform }));
                Assert.IsNull (PoseGraph.Create (null, root, new Transform[] { bone }));
            }
            finally {
                Object.DestroyImmediate (root);
            }
        }

        [Test]
        public void HooksCanAppendToTheGraph ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            Hook hook = new Hook ();
            PoseGraph graph = null;
            try {
                PoseGraphHooks.Register (hook);
                graph = PoseGraph.Create (animator, root, new Transform[] { bone });
                Assert.AreEqual (1, hook.calls, "グラフを作るときに 1 回呼ばれる");
                Assert.AreSame (root, hook.model);

                graph.Evaluate ();
                Assert.AreEqual (1, hook.updates, "解く前に受け口が呼ばれる");

                graph.Dispose ();
                graph = null;
                Assert.AreEqual (1, hook.releases, "グラフを捨てるときに片付けが呼ばれる");
            }
            finally {
                PoseGraphHooks.Unregister (hook);
                if (graph != null) graph.Dispose ();
                Object.DestroyImmediate (root);
            }
        }

        /// <summary>
        /// Rig の前に差す受け口（BeforeRig）は、登録が後でも Rig の後ろの受け口（既定）より前に入る
        /// </summary>
        [Test]
        public void BeforeRigHooksComeFirst ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            StagedHook after = new StagedHook { stage = PoseGraphHookStage.AfterRig };
            StagedHook before = new StagedHook { stage = PoseGraphHookStage.BeforeRig };
            PoseGraph graph = null;
            try {
                PoseGraphHooks.Register (after);
                PoseGraphHooks.Register (before);
                graph = PoseGraph.Create (animator, root, new Transform[] { bone });
                Assert.IsTrue (before.appended.IsValid () && after.appended.IsValid ());
                Assert.AreEqual (before.appended.GetHandle (), after.input.GetHandle (), "Rig の後ろの受け口は、Rig の前の受け口の後に続く");
                Assert.AreNotEqual (after.appended.GetHandle (), before.input.GetHandle ());
            }
            finally {
                PoseGraphHooks.Unregister (after);
                PoseGraphHooks.Unregister (before);
                if (graph != null) graph.Dispose ();
                Object.DestroyImmediate (root);
            }
        }

        /// <summary>
        /// 足さなかった受け口（input をそのまま返した）には、前段の Playable を渡さない（片付けで前段を壊さない）
        /// </summary>
        [Test]
        public void HooksThatDoNotAppendAreNotUpdated ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            Hook hook = new Hook { append = false };
            PoseGraph graph = null;
            try {
                PoseGraphHooks.Register (hook);
                graph = PoseGraph.Create (animator, root, new Transform[] { bone });
                Assert.AreEqual (1, hook.calls);
                graph.Evaluate ();
                graph.Dispose ();
                graph = null;
                Assert.AreEqual (0, hook.updates);
                Assert.AreEqual (0, hook.releases);
            }
            finally {
                PoseGraphHooks.Unregister (hook);
                if (graph != null) graph.Dispose ();
                Object.DestroyImmediate (root);
            }
        }

        /// <summary>
        /// 受け口には、グラフが書き出す Animator と、窓が覚えている基準姿勢が渡る
        /// </summary>
        [Test]
        public void HooksReceiveAnimatorAndRestPose ()
        {
            Animator animator;
            Transform bone;
            GameObject root = CreateModel (out animator, out bone);
            Hook hook = new Hook { restOf = bone };
            PoseGraph graph = null;
            try {
                PoseGraphHooks.Register (hook);
                bone.localPosition = new Vector3 (5, 0, 0);
                Pose rest = new Pose (new Vector3 (0, 1, 0), Quaternion.Euler (0, 90, 0));
                graph = PoseGraph.Create (animator, root, new Transform[] { bone }, t => rest);
                Assert.AreSame (animator, hook.animator);
                Assert.AreEqual (rest.position, hook.rest.position, "今の値ではなく基準姿勢");
                Assert.AreEqual (0f, Quaternion.Angle (rest.rotation, hook.rest.rotation), 0.01f);
            }
            finally {
                PoseGraphHooks.Unregister (hook);
                if (graph != null) graph.Dispose ();
                Object.DestroyImmediate (root);
            }
        }
    }

}
