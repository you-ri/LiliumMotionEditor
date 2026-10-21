using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// ゲームの Rig の重みの代理（層の RigLayerProxy・拘束の RigConstraintProxy）。中身は同じで、カーブは型で分けて持つ（型は残す）。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    public abstract class RigWeightProxy : MonoBehaviour
    {
        [Range (0, 1)]
        public float weight = 1;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kWeightProperty = "weight";
    }

}
