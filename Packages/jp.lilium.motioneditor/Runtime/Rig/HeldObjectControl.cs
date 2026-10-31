using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 持ち物のコントロール（Controls/Object/&lt;名前&gt;）に付ける数値。クリップのカーブ（float）で、手ごとに持っているかを持つ（0 か 1）。
    /// 物の位置と向きは、この GameObject の Transform が持つ（親の枠から見た値。HeldObjectRig）。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class HeldObjectControl : MonoBehaviour
    {
        /// <summary>左手で持っているか（0 か 1）</summary>
        [Range (0, 1)]
        public float leftHold;

        /// <summary>右手で持っているか（0 か 1）</summary>
        [Range (0, 1)]
        public float rightHold;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kLeftHoldProperty = "leftHold";
        public const string kRightHoldProperty = "rightHold";
    }

}
