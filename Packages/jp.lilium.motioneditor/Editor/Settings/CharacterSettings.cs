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

        [Tooltip ("Parent switches for held objects (a weapon in the right hand / left hand / free, etc.). Key the game's constraint weights and grips with the Rig Values buttons and the grab handles")]
        public List<ParentSwitchDefinition> parentSwitches = new List<ParentSwitchDefinition> ();

        [Tooltip ("Objects the character holds (a two-handed sword, etc.). Set them up in the Rig Editor. The object's pose is keyed, and the hands follow its grips")]
        public List<HeldObjectDefinition> heldObjects = new List<HeldObjectDefinition> ();

        /// <summary>持ち物の設定を直した（インスペクタ・Undo）。使っている窓が編集用リグを作り直す</summary>
        public static event System.Action<CharacterSettings> heldObjectsChanged;

        void OnValidate ()
        {
            NotifyHeldObjectsChanged (this);
        }

        /// <summary>持ち物の設定を直したことを知らせる（スクリプトから書き換えたとき。インスペクタでの変更は OnValidate が知らせる）</summary>
        public static void NotifyHeldObjectsChanged (CharacterSettings settings)
        {
            if (heldObjectsChanged != null && settings != null) heldObjectsChanged (settings);
        }

        /// <summary>持ち物の設定の中身を表す文字列（窓が、直されたかを見分ける。同じ中身なら作り直さない）</summary>
        public string GetHeldObjectsKey ()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder ();
            foreach (HeldObjectDefinition definition in heldObjects) text.Append (JsonUtility.ToJson (definition)).Append ('|');
            return text.ToString ();
        }
    }

}
