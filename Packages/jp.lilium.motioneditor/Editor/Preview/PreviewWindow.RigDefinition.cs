using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの定義を直したら・差し替えたら作り直す（S23）。定義はキャラの設定・プロジェクト設定で選ぶ
    /// </summary>
    public partial class PreviewWindow
    {
        bool rigDefinitionRebuildQueued_;

        /// <summary>
        /// 使う定義が替わっていたら（キャラの設定・プロジェクト設定のインスペクタで差し替えた・上書きを切り替えた）、編集用リグを作り直す
        /// </summary>
        void KeepRigDefinition ()
        {
            if (stage_ == null || rigDefinitionRebuildQueued_ || rigDefinition == stageRigDefinition_) return;
            rigDefinitionRebuildQueued_ = true;
            EditorApplication.delayCall += RebuildForRigDefinition;
        }

        /// <summary>
        /// 定義が直されたとき（インスペクタ・Undo）。この窓が使っている定義なら、編集用リグを作り直す（まとめて 1 回）
        /// </summary>
        void OnRigDefinitionChanged (EditRigDefinition definition)
        {
            if (definition == null || definition != rigDefinition || rigDefinitionRebuildQueued_) return;
            rigDefinitionRebuildQueued_ = true;
            EditorApplication.delayCall += RebuildForRigDefinition;
        }

        void RebuildForRigDefinition ()
        {
            rigDefinitionRebuildQueued_ = false;
            if (this == null) return;
            RebuildStage ();
            RepaintView ();
        }

        /// <summary>今の世界を作ったときの持ち物の設定（直されたかを見分ける）</summary>
        string stageHeldObjectsKey_;

        /// <summary>持ち物の設定が直されたとき（リグエディタ・インスペクタ・Undo）。中身が変わっていれば作り直す（キャラの設定はほかの項目でも知らせが来る）</summary>
        void OnHeldObjectsChanged (CharacterSettings settings)
        {
            if (settings == null || settings != characterSettings || rigDefinitionRebuildQueued_) return;
            if (settings.GetHeldObjectsKey () == (stageHeldObjectsKey_ ?? "")) return;
            rigDefinitionRebuildQueued_ = true;
            EditorApplication.delayCall += RebuildForRigDefinition;
        }

        void SubscribeRigDefinition ()
        {
            EditRigDefinition.changed -= OnRigDefinitionChanged;
            EditRigDefinition.changed += OnRigDefinitionChanged;
            CharacterSettings.heldObjectsChanged -= OnHeldObjectsChanged;
            CharacterSettings.heldObjectsChanged += OnHeldObjectsChanged;
        }

        void UnsubscribeRigDefinition ()
        {
            EditRigDefinition.changed -= OnRigDefinitionChanged;
            CharacterSettings.heldObjectsChanged -= OnHeldObjectsChanged;
            EditorApplication.delayCall -= RebuildForRigDefinition;
            rigDefinitionRebuildQueued_ = false;
        }
    }

}
