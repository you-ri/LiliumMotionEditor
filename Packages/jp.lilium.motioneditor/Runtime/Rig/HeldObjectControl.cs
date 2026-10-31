using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 持ち物のコントロール（Controls/Object/&lt;名前&gt;）に付ける数値。クリップのカーブ（float）で、手ごとに持っているかと、差しているかを持つ（0 か 1）。
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

        /// <summary>今この場所に差しているか（0 か 1。差す場所のコントロール Controls/Object/&lt;名前&gt;/&lt;場所&gt; でだけ使う）</summary>
        public float socket;

        /// <summary>左手の握りを、設定の場所から柄に沿ってずらす量（メートル）。キーの間も補間する</summary>
        public float leftSlide;

        /// <summary>右手の握りを、設定の場所から柄に沿ってずらす量（メートル）。キーの間も補間する</summary>
        public float rightSlide;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kLeftHoldProperty = "leftHold";
        public const string kRightHoldProperty = "rightHold";
        public const string kSocketProperty = "socket";
        public const string kLeftSlideProperty = "leftSlide";
        public const string kRightSlideProperty = "rightSlide";
    }

}
