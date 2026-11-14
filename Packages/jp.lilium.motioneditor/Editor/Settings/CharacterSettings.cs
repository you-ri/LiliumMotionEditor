using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// キャラへの結び付け（S19・S108）。キャラの prefab を指すアセットで、git で共有される。
    /// 使い方（フォルダ・Auto Bake など）はプロジェクト設定（ProjectSettings）だけが持ち、ここはキャラに属するもの
    /// （使う定義・Layers の状態・持ち替え・持ち物）だけを持つ。リグの形は定義（EditRigDefinition）に、装備は Rig Variant（S92）に分ける
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/Character Settings", fileName = "Motion Editor Settings")]
    public sealed class CharacterSettings : ScriptableObject
    {
        /// <summary>この設定のキャラ（窓に入れる prefab）</summary>
        public GameObject prefab;

        // S108 でやめた上書き。読まない。値が入っていたかを移す前に見られるよう 1 リリース残し（SettingsLookup が警告する）、次のリリースで消す
        [System.Obsolete ("S108: folders come from the project settings only"), HideInInspector]
        public bool overrideAnimBankFolders;
        [System.Obsolete ("S108: folders come from the project settings only"), HideInInspector]
        public List<string> animBankFolders = new List<string> ();
        [System.Obsolete ("S108: folders come from the project settings only"), HideInInspector]
        public bool overridePoseBankFolders;
        [System.Obsolete ("S108: folders come from the project settings only"), HideInInspector]
        public List<string> poseBankFolders = new List<string> ();

        public bool overrideRigDefinition;
        public EditRigDefinition rigDefinition;

        [System.Obsolete ("S108: Auto Bake comes from the project settings only"), HideInInspector]
        public bool overrideAutoBake;
        [System.Obsolete ("S108: Auto Bake comes from the project settings only"), HideInInspector]
        public bool autoBake = true;

        /// <summary>S108 でやめた上書き（フォルダ・Auto Bake）が入っているか（入っていれば値は使われない）</summary>
        public bool hasObsoleteOverrides
        {
            get {
#pragma warning disable 0618
                return overrideAnimBankFolders || overridePoseBankFolders || overrideAutoBake;
#pragma warning restore 0618
            }
        }

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
            return RigSetup.MakeHeldObjectsKey (heldObjects);
        }
    }

}
