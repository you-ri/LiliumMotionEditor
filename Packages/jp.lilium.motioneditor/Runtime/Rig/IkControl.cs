using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// IK の組のコントロール（Controls/IK/&lt;組&gt;）に付ける数値。クリップのカーブ（float）で切り替える。
    /// 編集用の体（プレビューシーン）にだけ付く。
    /// GameObject に付けるので Editor アセンブリには置けない（Unity が Editor のスクリプトを付けさせない）
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class IkControl : MonoBehaviour
    {
        /// <summary>
        /// FK と IK の混ぜ具合。0 で FK（骨の回転のコントロール）、1 で IK（目標とヒント）
        /// </summary>
        [Range (0, 1)]
        public float ikWeight;

        /// <summary>
        /// 足の転がし（Reverse Foot。脚だけ）の前後の角度（度）。正で踵が上がる（母趾球、折れ角を越えるとつま先の先まわり）、負でつま先が上がる（踵まわり）
        /// </summary>
        public float roll;

        /// <summary>足の転がしの左右の傾き（度）。体の前の軸まわり。正で体の右側が上がる（左の縁まわり）</summary>
        public float bank;

        /// <summary>足の転がしのひねり（度）。体の上の軸まわり（母趾球まわり）</summary>
        public float twist;

        /// <summary>
        /// クリップのカーブのプロパティ名
        /// </summary>
        public const string kIkWeightProperty = "ikWeight";
        public const string kRollProperty = "roll";
        public const string kBankProperty = "bank";
        public const string kTwistProperty = "twist";

        /// <summary>持っている数値の数（並びは kProperties）</summary>
        public const int kValueCount = 4;

        public static readonly string[] kProperties = { kIkWeightProperty, kRollProperty, kBankProperty, kTwistProperty };

        public float GetValue (int index)
        {
            switch (index) {
                case 0: return ikWeight;
                case 1: return roll;
                case 2: return bank;
                case 3: return twist;
            }
            return 0;
        }

        public void SetValue (int index, float value)
        {
            switch (index) {
                case 0: ikWeight = value; break;
                case 1: roll = value; break;
                case 2: bank = value; break;
                case 3: twist = value; break;
            }
        }

        /// <summary>プロパティ名の並びの位置（無ければ -1）</summary>
        public static int IndexOf (string property)
        {
            return System.Array.IndexOf (kProperties, property);
        }

        /// <summary>
        /// 足し合わせで重ねる値か（足の転がしの角度）。IK の重みは置き換える
        /// </summary>
        public static bool IsAdditive (string property)
        {
            return property == kRollProperty || property == kBankProperty || property == kTwistProperty;
        }
    }

}
