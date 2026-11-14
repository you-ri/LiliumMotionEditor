using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// リグのバリアント（S92）。同じキャラの、装備を足したリグ（剣を持つなど）。キャラの設定（ベース）との差分だけを持つ。
    /// クリップは、どのバリアントで作ったかを名前の印で覚える（ClipUserData.rigVariant。体型の違うキャラでクリップを共有するため GUID でなく名前）。
    /// 持つ物は「ベースの物 + バリアントの物」で、同じ名前の物はバリアントの方を丸ごと使う（握りと基準の場所は組で意味を持つので、項目ごとには上書きしない）
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/Rig Variant", fileName = "Rig Variant")]
    public sealed class RigVariant : ScriptableObject
    {
        [Tooltip ("Character (the prefab put in the Motion Editor), pointed to the same way as in the character settings")]
        public GameObject prefab;

        [Tooltip ("Name stored in clips made with this variant. Clips find their variant by this name, so characters with different bodies can share clips. Renaming it leaves existing clips pointing to the old name")]
        public string variantName;

        [Tooltip ("Objects added on top of the character settings' objects. An object with the same name as one in the character settings replaces it")]
        public List<HeldObjectDefinition> heldObjects = new List<HeldObjectDefinition> ();

        /// <summary>バリアントを直した（インスペクタ・Undo・スクリプト）。使っている窓が編集用リグを作り直す</summary>
        public static event System.Action<RigVariant> changed;

        void OnValidate ()
        {
            // キャラ・名前を変えると引き当ての結果も変わる
            RigVariants.Invalidate ();
            NotifyChanged (this);
        }

        /// <summary>バリアントを直したことを知らせる（スクリプトから書き換えたとき。インスペクタでの変更は OnValidate が知らせる）</summary>
        public static void NotifyChanged (RigVariant variant)
        {
            if (changed != null && variant != null) changed (variant);
        }
    }

}
