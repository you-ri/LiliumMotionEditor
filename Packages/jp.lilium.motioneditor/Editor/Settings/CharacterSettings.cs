using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// キャラごとの設定（S19）。キャラの prefab を指すアセットで、git で共有される。
    /// 「上書きする」を入れた項目だけがプロジェクト設定（ProjectSettings）より優先される。Layers の状態はいつもこのアセットに持つ
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/Character Settings", fileName = "Motion Editor Settings")]
    public sealed class CharacterSettings : ScriptableObject
    {
        /// <summary>この設定のキャラ（窓に入れる prefab）</summary>
        public GameObject prefab;

        public bool overrideAnimBankFolders;
        /// <summary>AnimBank のフォルダの並び（置き換え文字列はプロジェクト設定と同じ）。先頭が New・Dup で作る場所</summary>
        public List<string> animBankFolders = new List<string> ();

        public bool overridePoseBankFolders;
        /// <summary>PoseBank のフォルダの並び。先頭が Save で作る場所</summary>
        public List<string> poseBankFolders = new List<string> ();

        public bool overrideRigDefinition;
        public EditRigDefinition rigDefinition;

        public bool overrideAutoBake;
        public bool autoBake = true;

        /// <summary>Layers の ✏・✋・👁・重み・合成</summary>
        public LayerState layers = new LayerState ();

        [Tooltip ("物の持ち替え（武器を右手 / 左手 / フリーなど）。ゲームの拘束の重みと握りを、Rig Values のボタンとつかむハンドルで打つ")]
        public List<ParentSwitchDefinition> parentSwitches = new List<ParentSwitchDefinition> ();
    }

}
