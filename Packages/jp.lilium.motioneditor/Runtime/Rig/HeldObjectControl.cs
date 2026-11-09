using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 物を付ける先 1 つ分の値（Controls/Object/&lt;物の名前&gt;/&lt;付ける先の名前&gt;。S104）。クリップのカーブ（float）で、付けているかと、付けた位置を持つ。
    /// 物の位置と向きは、物のコントロール（Controls/Object/&lt;物の名前&gt;）の Transform が持つ（親の枠から見た値。HeldObjectRig）。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class HeldObjectControl : MonoBehaviour
    {
        /// <summary>この先に付けているか（0 か 1）</summary>
        [Range (0, 1)]
        public float attached;

        /// <summary>
        /// 付けた位置を、物の設定の位置でなく gripPosition・gripRotation にするか（0 か 1。S99）。
        /// 付けるボタンを押した瞬間の、付ける先の枠の場所と向きを残したもの。前後のキーで同じ先に付いていれば、キーの間は位置を補間する
        /// </summary>
        public float gripKeyed;
        /// <summary>付ける先の枠の場所（物のローカル。メートル）</summary>
        public Vector3 gripPosition;
        /// <summary>付ける先の枠の向き（物のローカル。オイラー角。枠の向きは「物の向き × これ」）</summary>
        public Vector3 gripRotation;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kAttachedProperty = "attached";
        public const string kGripKeyedProperty = "gripKeyed";
        public const string kGripPositionProperty = "gripPosition";
        public const string kGripRotationProperty = "gripRotation";
    }

}
