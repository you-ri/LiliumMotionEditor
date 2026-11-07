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
        /// 左手の握りを、キャラの設定の握りでなく leftGripPosition・leftGripRotation にするか（0 か 1。S99）。
        /// 持つときの手首の場所と向きを残したもの（持つボタンを押した瞬間の手で持つ）。キーの間で補間しない
        /// </summary>
        public float leftGripKeyed;
        /// <summary>左手の握りの場所（物のローカル。メートル）</summary>
        public Vector3 leftGripPosition;
        /// <summary>左手の握りの向き（物のローカル。オイラー角。手首の点の目標の向きは「物の向き × これ」）</summary>
        public Vector3 leftGripRotation;
        /// <summary>右手の握りを、キャラの設定の握りでなく rightGripPosition・rightGripRotation にするか（0 か 1。S99）</summary>
        public float rightGripKeyed;
        public Vector3 rightGripPosition;
        public Vector3 rightGripRotation;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kLeftHoldProperty = "leftHold";
        public const string kRightHoldProperty = "rightHold";
        public const string kSocketProperty = "socket";
        public const string kLeftSlideProperty = "leftSlide";
        public const string kRightSlideProperty = "rightSlide";
        public const string kLeftGripKeyedProperty = "leftGripKeyed";
        public const string kLeftGripPositionProperty = "leftGripPosition";
        public const string kLeftGripRotationProperty = "leftGripRotation";
        public const string kRightGripKeyedProperty = "rightGripKeyed";
        public const string kRightGripPositionProperty = "rightGripPosition";
        public const string kRightGripRotationProperty = "rightGripRotation";
    }

}
