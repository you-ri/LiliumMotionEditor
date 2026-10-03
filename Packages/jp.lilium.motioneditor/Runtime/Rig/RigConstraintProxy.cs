using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// ゲームの拘束（TwoBoneIK など）の重みの代理。拘束が Rig と同じ GameObject にあるときは、層の代理と同じ GameObject に並ぶ。
    /// 編集用の体（プレビューシーン）にだけ付く
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class RigConstraintProxy : MonoBehaviour
    {
        [Range (0, 1)]
        public float weight = 1;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kWeightProperty = "weight";
    }

}
