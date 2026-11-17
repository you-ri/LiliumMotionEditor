using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// この PC だけの設定（EditorPrefs。バージョン管理しない）。人の作業の仕方で変える値を置く
    /// （プロジェクト設定・キャラの設定に置くと、切り替えるたびに共有するアセットが変わってしまう）
    /// </summary>
    static class MotionEditorPrefs
    {
        const string kAutoKey = "LiliumMotionEditor.AutoKey";

        /// <summary>値を変えたとき。窓はこれで表示（タイムラインのスイッチ・動かせる物）を合わせる</summary>
        public static event System.Action changed;

        /// <summary>
        /// 操作したらキーを打つ（入）。切なら、今のフレームにキーがある物しか動かせず、操作でキーは増えない（誤って触ってもクリップが変わらない）。
        /// キーは Stacker の Key All・Insert、Properties の Key などで打つ。前はプロジェクト設定にあった（11-18 に移した）
        /// </summary>
        public static bool autoKey
        {
            get { return EditorPrefs.GetBool (kAutoKey, false); }
            set {
                if (autoKey == value) return;
                EditorPrefs.SetBool (kAutoKey, value);
                if (changed != null) changed ();
            }
        }
    }

}
