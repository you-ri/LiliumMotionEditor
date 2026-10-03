using System.Collections.Generic;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// プロジェクト全体の設定（S19）。ProjectSettings に置くので git で共有される。キャラの設定（CharacterSettings）で上書きしない項目はここの値になる。
    /// フォルダは置き換え文字列で書く: {name}（prefab の名前から nameSuffixes を除いたもの）・{prefabFolder}（prefab のあるフォルダ）
    /// </summary>
    [FilePath ("ProjectSettings/LiliumMotionEditorSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ProjectSettings : ScriptableSingleton<ProjectSettings>
    {
        /// <summary>AnimBank のフォルダの並び。先頭が New・Dup で作る場所。空ならパッケージの既定（拡張点 AnimBank.defaultFolder → prefab のフォルダ/Motions）</summary>
        public List<string> animBankFolders = new List<string> ();

        /// <summary>PoseBank のフォルダの並び。先頭が Save で作る場所。空ならパッケージの既定（拡張点 PoseBank.defaultFolder → prefab のフォルダ/Poses）</summary>
        public List<string> poseBankFolders = new List<string> ();

        /// <summary>{name} を作るときに prefab の名前の末尾から除く文字列（例: "_Variant"）</summary>
        public List<string> nameSuffixes = new List<string> ();

        /// <summary>キャラの設定を作るときの置き場所</summary>
        public string characterSettingsFolder = "{prefabFolder}";

        /// <summary>編集用リグの定義。空ならパッケージの既定（EditRigDefinition.packageDefault）。使うときは defaultRigDefinition を読む</summary>
        public EditRigDefinition rigDefinition;

        /// <summary>キャラの設定で上書きしないキャラが使う定義（rigDefinition、空ならパッケージの既定）</summary>
        public EditRigDefinition defaultRigDefinition
        {
            get { return rigDefinition != null ? rigDefinition : EditRigDefinition.packageDefault; }
        }

        /// <summary>編集するクリップを保存したら Humanoid 版を焼き直す</summary>
        public bool autoBake = true;

        /// <summary>
        /// 操作したらキーを打つ（入）。切なら、今のフレームにキーがある物しか動かせず、操作でキーは増えない（誤って触ってもクリップが変わらない）。
        /// キーは Stacker の Key All・Insert、Properties の Key などで打つ
        /// </summary>
        public bool autoKey = false;

        /// <summary>Layers の既定（キャラの設定が無いとき・作るときの始まりの値）</summary>
        public LayerState layers = new LayerState ();

        /// <summary>
        /// 保存したとき（設定のページ・タイムラインの Auto Key）。設定はどちらからも変えられるので、窓はこれで表示を合わせる
        /// </summary>
        public static event System.Action changed;

        public void Save ()
        {
            Save (true);
            if (changed != null) changed ();
        }
    }

}
