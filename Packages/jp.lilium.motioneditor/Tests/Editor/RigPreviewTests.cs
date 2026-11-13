using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using System.Collections;

namespace Lilium
{

    /// <summary>
    /// 表示モデルの Rigging を窓のグラフへ足す道具（S4）。Animation Rigging が無ければ飛ばす。
    /// テストのアセンブリは Rigging を参照しないので、型は名前で引く
    /// </summary>
    public class RigPreviewTests
    {
        const string kRigBuilder = "UnityEngine.Animations.Rigging.RigBuilder, Unity.Animation.Rigging";
        const string kRig = "UnityEngine.Animations.Rigging.Rig, Unity.Animation.Rigging";
        const string kRigLayer = "UnityEngine.Animations.Rigging.RigLayer, Unity.Animation.Rigging";

        GameObject model_;

        [TearDown]
        public void TearDown ()
        {
            if (model_ != null) Object.DestroyImmediate (model_);
        }

        /// <summary>
        /// 再生中（Play Mode を抜ける途中を含む）に世界を作ると、RigBuilder が実行時のグラフを作って残し、窓の姿勢を上書きした
        /// （2026-11-12 開き直すと表示モデルが編集の姿勢にならない）。プレビューを始めるときに消す
        /// </summary>
        [Test]
        public void Build_ClearsTheRuntimeGraph ()
        {
            System.Type builderType = System.Type.GetType (kRigBuilder);
            System.Type rigType = System.Type.GetType (kRig);
            System.Type layerType = System.Type.GetType (kRigLayer);
            if (builderType == null || rigType == null || layerType == null || RigProbe.createPreview == null) Assert.Ignore ("Animation Rigging が無い");

            model_ = new GameObject ("Model");
            model_.AddComponent<Animator> ();
            GameObject rigObject = new GameObject ("Rig");
            rigObject.transform.SetParent (model_.transform, false);
            Component rig = rigObject.AddComponent (rigType);
            Behaviour builder = (Behaviour)model_.AddComponent (builderType);
            IList layers = (IList)builderType.GetProperty ("layers").GetValue (builder);
            layers.Add (System.Activator.CreateInstance (layerType, rig, true));
            builder.enabled = false;

            // 再生中の OnEnable と同じく、実行時のグラフを作っておく
            Assert.IsTrue ((bool)builderType.GetMethod ("Build", System.Type.EmptyTypes).Invoke (builder, null));
            Assert.IsTrue (RuntimeGraph (builderType, builder).IsValid ());

            IRigPreview preview = RigProbe.CreatePreview (model_);
            Assert.IsNotNull (preview);
            PlayableGraph graph = PlayableGraph.Create ("RigPreviewTests");
            try {
                preview.Build (graph, Playable.Create (graph));
                Assert.IsFalse (RuntimeGraph (builderType, builder).IsValid (), "実行時のグラフが表示モデルに残っている");
                preview.Stop ();
            }
            finally {
                graph.Destroy ();
            }
        }

        static PlayableGraph RuntimeGraph (System.Type builderType, Behaviour builder)
        {
            return (PlayableGraph)builderType.GetProperty ("graph").GetValue (builder);
        }
    }

}
