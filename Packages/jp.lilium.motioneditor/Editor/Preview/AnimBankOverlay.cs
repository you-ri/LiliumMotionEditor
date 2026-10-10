using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// AnimBank（S10）。このキャラのフォルダにある編集用クリップの一覧。クリックで開き、New・Dup・Rename・Del で管理する。
    /// 編集中のクリップは ▶、保存していない変更があれば * が付く。フォルダは「…」で選び直せる（キャラごとに覚える）
    /// </summary>
    [Icon (Icons.kFolder + "AnimBank.png")]
    [Overlay (typeof (PreviewWindow), "mkt-animbank", "AnimBank", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    class AnimBankOverlay : BankOverlay
    {
        protected override List<string> GetFolders (GameObject model)
        {
            return AnimBank.GetFolders (model);
        }

        protected override void SetFolder (GameObject model, string folder)
        {
            AnimBank.SetFolder (model, folder);
        }

        protected override List<BankEntry> ListFolder (string folder)
        {
            return AnimBank.List (folder);
        }

        protected override string BaseName (string path)
        {
            return AnimBank.BaseName (path);
        }

        protected override string Rename (AnimationClip clip, string name)
        {
            return AnimBank.Rename (clip, name);
        }

        protected override string chooseFolderTooltip { get { return Tr ("ANIM_BANK_OVERLAY_CHOOSE_FOLDER_TOOLTIP"); } }
        protected override string folderPanelTitle { get { return "Motion Folder"; } }
        protected override float listHeight { get { return 200; } }

        /// <summary>編集中のクリップ（アセットでなければ null）</summary>
        protected override AnimationClip renameTarget
        {
            get { return window_.clip != null && EditorUtility.IsPersistent (window_.clip) ? window_.clip : null; }
        }

        protected override void AddControls (VisualElement root)
        {
            list_.selectionChanged += OnSelectionChanged;

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            AddButton (buttons, "New", Tr ("ANIM_BANK_OVERLAY_NEW_TOOLTIP"), CreateClip);
            AddButton (buttons, "Dup", Tr ("ANIM_BANK_OVERLAY_DUP_TOOLTIP"), DuplicateClip);
            AddButton (buttons, "Rename", Tr ("ANIM_BANK_OVERLAY_RENAME_TOOLTIP"), BeginRename);
            AddButton (buttons, "Del", Tr ("ANIM_BANK_OVERLAY_DELETE_TOOLTIP"), DeleteClip);
            root.Add (buttons);
        }

        protected override void Subscribe (bool on)
        {
            window_.keysChanged -= RefreshMarks;
            if (on) window_.keysChanged += RefreshMarks;
        }

        protected override void BindRow (Label label, Row row, int index)
        {
            string current = window_ != null && window_.clip != null ? AssetDatabase.GetAssetPath (window_.clip) : null;
            bool editing = row.path != null && row.path == current;
            bool dirty = editing && EditorUtility.IsDirty (window_.clip);
            label.text = row.path == null ? row.text : (editing ? "▶ " : "   ") + row.text + (dirty ? " *" : "");
            StyleRow (label, row, editing ? FontStyle.Bold : FontStyle.Normal);
            label.tooltip = row.path;
        }

        /// <summary>編集中のクリップの行を選ぶ</summary>
        protected override void AfterRefresh (string selectedPath)
        {
            SelectPath (window_.clip != null ? AssetDatabase.GetAssetPath (window_.clip) : null);
        }

        /// <summary>
        /// キーを打ったとき（保存していない印だけ直す）
        /// </summary>
        void RefreshMarks ()
        {
            if (list_ != null) list_.RefreshItems ();
        }

        void OnSelectionChanged (IEnumerable<object> selected)
        {
            int index = list_.selectedIndex;
            if (index < 0 || index >= rows_.Count || rows_[index].path == null) return;
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (rows_[index].path);
            if (clip != null && clip != window_.clip) window_.SetClip (clip);
        }

        void CreateClip ()
        {
            string path = folder;
            if (path == null) {
                ShowMessage (Tr ("POSE_BANK_OVERLAY_SELECT_CHARACTER"), true);
                return;
            }
            AnimationClip clip = AnimBank.Create (path);
            window_.SetClip (clip);
            ShowMessage (null, true);
            Refresh ();
        }

        void DuplicateClip ()
        {
            if (window_.clip == null) return;
            AnimationClip clip = AnimBank.Duplicate (window_.clip);
            if (clip == null) {
                ShowMessage (Tr ("ANIM_BANK_OVERLAY_CANNOT_DUPLICATE"), true);
                return;
            }
            window_.SetClip (clip);
            ShowMessage (null, true);
            Refresh ();
        }

        void DeleteClip ()
        {
            AnimationClip clip = renameTarget;
            if (clip == null) return;
            string path = AssetDatabase.GetAssetPath (clip);
            if (!EditorUtility.DisplayDialog ("Delete Motion Clip", Tr ("ANIM_BANK_OVERLAY_DELETE_DIALOG", path), "Delete", "Cancel")) return;
            window_.SetClip (null);
            if (!AnimBank.Delete (clip)) ShowMessage (Tr ("POSE_BANK_OVERLAY_COULD_NOT_DELETE", path), true);
            else ShowMessage (null, true);
            Refresh ();
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "AnimBank.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-animbank", "AnimBank", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    sealed class AnimBankOverlayScene : AnimBankOverlay
    {
    }

}
