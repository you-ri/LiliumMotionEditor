using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// ゲームの Rig の層の重みの代理（Controls/Game/&lt;Rig&gt;）。クリップのカーブ（float）で動かし、表示モデルの Rig へ写す。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class RigLayerProxy : MonoBehaviour
    {
        [Range (0, 1)]
        public float weight = 1;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kWeightProperty = "weight";
    }

}
