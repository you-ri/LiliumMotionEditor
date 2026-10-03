using System.Collections.Generic;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// キャラに組まれている Animation Rigging の 1 層
    /// </summary>
    public sealed class RigLayerInfo
    {
        public string label;
        /// <summary>その層が有効か（ゲーム中にコードから有効化される層もある）</summary>
        public bool active;
        public float weight;
        public readonly List<RigConstraintInfo> constraints = new List<RigConstraintInfo> ();

        /// <summary>Rig の GameObject（重みのカーブが指す先）</summary>
        public Transform transform;
        /// <summary>重みを持つコンポーネントの型と、そのカーブのプロパティ名（焼くときの行き先）</summary>
        public System.Type weightType;
        public string weightProperty;
        /// <summary>表示モデルの Rig へ重みを入れる</summary>
        public System.Action<float> setWeight;
        /// <summary>表示モデルの Rig の今の重み（クリップから配られた値）</summary>
        public System.Func<float> getWeight;
    }

    /// <summary>
    /// 拘束 1 つ。逆方向に解けるかは、拘束の型から自動で判定する
    /// </summary>
    public sealed class RigConstraintInfo
    {
        public string label;
        public InverseKind inverse;
        public string reason;

        /// <summary>拘束の GameObject</summary>
        public Transform transform;
        public float weight;
        public System.Type weightType;
        public string weightProperty;
        public System.Action<float> setWeight;
        /// <summary>表示モデルの拘束の今の重み</summary>
        public System.Func<float> getWeight;
        /// <summary>拘束が読む Transform（ターゲット・ヒントなど。拘束される骨は含まない）</summary>
        public readonly List<Transform> sources = new List<Transform> ();
        /// <summary>拘束が動かす骨（sources 以外の参照）</summary>
        public readonly List<Transform> constrained = new List<Transform> ();

        /// <summary>bone（かその親）を動かすか</summary>
        public bool Moves (Transform bone)
        {
            if (bone == null) return false;
            foreach (Transform target in constrained) {
                if (target != null && bone.IsChildOf (target)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 表示モデルの Rigging を、窓が持つグラフの後ろに足す（S4）。中身は Animation Rigging のアセンブリ側
    /// </summary>
    public interface IRigPreview
    {
        /// <summary>入ってきた姿勢の後ろに Rig を足して、新しい根を返す</summary>
        UnityEngine.Playables.Playable Build (UnityEngine.Playables.PlayableGraph graph, UnityEngine.Playables.Playable input);

        /// <summary>解く前に、Rig の重みとターゲットをグラフへ写す</summary>
        void Update (UnityEngine.Playables.PlayableGraph graph);

        /// <summary>プレビューを終える（Rig が作った物を片付ける）</summary>
        void Stop ();
    }

    /// <summary>
    /// Animation Rigging を覗く口。パッケージ本体は Animation Rigging を参照しない（無い環境でも動く）。
    /// 入っているときだけ LiliumMotionEditor.Rigging.Editor アセンブリが describe を入れる
    /// </summary>
    public static class RigProbe
    {
        /// <summary>
        /// 表示モデルに組まれている Rig の層を返す。組まれていなければ空
        /// </summary>
        public static System.Func<GameObject, List<RigLayerInfo>> describe;

        /// <summary>
        /// Rigging の部品（RigBuilder・Rig・拘束）かどうか。
        /// プレビューの複製から要らないコンポーネントを外すときに、これらを残すために使う
        /// </summary>
        public static System.Func<Component, bool> isRigComponent;

        /// <summary>
        /// 表示モデルの Rigging をグラフへ足す道具を作る。Rig が無ければ null
        /// </summary>
        public static System.Func<GameObject, IRigPreview> createPreview;

        public static bool available
        {
            get { return describe != null; }
        }

        public static IRigPreview CreatePreview (GameObject model)
        {
            return createPreview == null || model == null ? null : createPreview (model);
        }

        public static bool IsRigComponent (Component component)
        {
            return component != null && isRigComponent != null && isRigComponent (component);
        }

        public static List<RigLayerInfo> Describe (GameObject model)
        {
            if (describe == null || model == null) return new List<RigLayerInfo> ();
            return describe (model) ?? new List<RigLayerInfo> ();
        }
    }

}
