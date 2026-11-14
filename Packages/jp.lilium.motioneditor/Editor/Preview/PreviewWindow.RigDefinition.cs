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

        /// <summary>持ち物の設定が直されたとき（リグエディタ・インスペクタ・Undo）。中身が変わっていれば作り直す（キャラの設定はほかの項目でも知らせが来る）</summary>
        void OnHeldObjectsChanged (CharacterSettings settings)
        {
            if (settings == null || settings != characterSettings) return;
            RebuildIfHeldObjectsChanged ();
        }

        /// <summary>バリアントが直されたとき（S92）。このキャラのバリアントで、今のクリップの持つ物が変わっていれば作り直す</summary>
        void OnRigVariantChanged (RigVariant variant)
        {
            if (variant == null || stage_ == null) return;
            RebuildIfHeldObjectsChanged ();
        }

        /// <summary>今のクリップの持つ物（ベースの物 + 印のバリアントの物）が、今の世界と違えば作り直す（まとめて 1 回）</summary>
        void RebuildIfHeldObjectsChanged ()
        {
            if (stage_ == null || rigDefinitionRebuildQueued_) return;
            RigSetup current = RigVariants.Resolve (settingsPrefab, RigVariants.GetClipVariant (editingClip_), rigDefinition);
            if (stage_.setup != null && current.key == stage_.setup.key) return;
            rigDefinitionRebuildQueued_ = true;
            EditorApplication.delayCall += RebuildForRigDefinition;
        }

        void SubscribeRigDefinition ()
        {
            EditRigDefinition.changed -= OnRigDefinitionChanged;
            EditRigDefinition.changed += OnRigDefinitionChanged;
            CharacterSettings.heldObjectsChanged -= OnHeldObjectsChanged;
            CharacterSettings.heldObjectsChanged += OnHeldObjectsChanged;
            RigVariant.changed -= OnRigVariantChanged;
            RigVariant.changed += OnRigVariantChanged;
        }

        void UnsubscribeRigDefinition ()
        {
            EditRigDefinition.changed -= OnRigDefinitionChanged;
            CharacterSettings.heldObjectsChanged -= OnHeldObjectsChanged;
            RigVariant.changed -= OnRigVariantChanged;
            EditorApplication.delayCall -= RebuildForRigDefinition;
            rigDefinitionRebuildQueued_ = false;
        }
    }

}
