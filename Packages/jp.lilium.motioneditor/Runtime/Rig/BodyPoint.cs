using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点（Controls/Body/&lt;骨&gt;）に付ける数値。クリップのカーブ（float）で固定の強さを持つ。
    /// 点の位置と向きは、この GameObject の Transform が持つ（位置はルートから見た位置 / humanScale、向きはルートから見た基準姿勢からの差）。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class BodyPoint : MonoBehaviour
    {
        /// <summary>
        /// 位置の固定の強さ。0 で効かない、1 で点の位置に留める
        /// </summary>
        [Range (0, 1)]
        public float positionWeight;

        /// <summary>
        /// 向きの固定の強さ。0 で効かない、1 で点の向きに留める（向きを持つ点だけ）
        /// </summary>
        [Range (0, 1)]
        public float rotationWeight;

        /// <summary>
        /// 完全固定（0 か 1）。1 の点は、置いた場所から動かさない（体が届かなくても点はそこに残る）。
        /// 0 の点は、編集のたびに実際の骨の位置へ合わせ直される（点が体の形から離れていかない）。
        /// 解き方には効かない（どちらも点の位置へ寄せる）。編集するときの扱いだけが違う
        /// </summary>
        [Range (0, 1)]
        public float locked;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kPositionWeightProperty = "positionWeight";
        public const string kRotationWeightProperty = "rotationWeight";
        public const string kLockedProperty = "locked";

        /// <summary>持っている数値の数（並びは kProperties）</summary>
        public const int kValueCount = 3;

        public static readonly string[] kProperties = { kPositionWeightProperty, kRotationWeightProperty, kLockedProperty };

        public float GetValue (int index)
        {
            switch (index) {
                case 0: return positionWeight;
                case 1: return rotationWeight;
                case 2: return locked;
            }
            return 0;
        }

        public void SetValue (int index, float value)
        {
            switch (index) {
                case 0: positionWeight = value; break;
                case 1: rotationWeight = value; break;
                case 2: locked = value; break;
            }
        }

        /// <summary>プロパティ名の並びの位置（無ければ -1）</summary>
        public static int IndexOf (string property)
        {
            return System.Array.IndexOf (kProperties, property);
        }
    }

}
