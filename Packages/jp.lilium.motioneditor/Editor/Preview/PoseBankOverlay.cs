using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// PoseBank（AnimBank の姿勢版）。このキャラのフォルダにある姿勢の一覧。
    /// 行をクリックすると、その姿勢を今のフレームへ打つ。骨を選んでいればその骨のぶんだけ（手の指だけ、など）、何も選んでいなければ姿勢全部。
    /// Save で今の姿勢（全部）を新しい姿勢として保存し、Update で一覧で選んでいる姿勢を今の姿勢に置き換える
    /// </summary>
    [Icon (Icons.kFolder + "PoseBank.png")]
    // 既定は上端のツールバーの左（AnimBank の次）。パネルだけなのでボタンに畳まれる
    [Overlay (typeof (PreviewWindow), "mkt-posebank", "PoseBank", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top, defaultDockIndex = 2)]
    class PoseBankOverlay : BankOverlay
    {
        Label scope_;

        protected override List<string> GetFolders (GameObject model)
        {
            return PoseBank.GetFolders (model);
        }

        protected override void SetFolder (GameObject model, string folder)
        {
            PoseBank.SetFolder (model, folder);
        }

        protected override List<BankEntry> ListFolder (string folder)
        {
            return PoseBank.List (folder);
        }

        protected override string BaseName (string path)
        {
            return PoseBank.BaseName (path);
        }

        protected override string Rename (AnimationClip clip, string name)
        {
            return PoseBank.Rename (clip, name);
        }

        protected override string chooseFolderTooltip { get { return Tr ("POSE_BANK_OVERLAY_CHOOSE_FOLDER_TOOLTIP"); } }
        protected override string folderPanelTitle { get { return "Pose Folder"; } }
        protected override float listHeight { get { return 160; } }

        /// <summary>一覧で選んでいる姿勢（見出しや未選択なら null）</summary>
        protected override AnimationClip renameTarget
        {
            get {
                int index = list_ != null ? list_.selectedIndex : -1;
                if (index < 0 || index >= rows_.Count || rows_[index].path == null) return null;
                return AssetDatabase.LoadAssetAtPath<AnimationClip> (rows_[index].path);
            }
        }

        protected override void AddControls (VisualElement root)
        {
            scope_ = new Label ();
            scope_.style.fontSize = 10;
            scope_.style.color = new Color (0.65f, 0.65f, 0.65f);
            root.Add (scope_);

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            AddButton (buttons, "Save", Tr ("POSE_BANK_OVERLAY_SAVE_TOOLTIP"), SavePose);
            AddButton (buttons, "Update", Tr ("POSE_BANK_OVERLAY_UPDATE_TOOLTIP"), UpdatePose);
            AddButton (buttons, "Rename", Tr ("POSE_BANK_OVERLAY_RENAME_TOOLTIP"), BeginRename);
            AddButton (buttons, "Del", Tr ("POSE_BANK_OVERLAY_DELETE_TOOLTIP"), DeletePose);
            root.Add (buttons);
        }

        protected override void SetUpRow (Label label)
        {
            // 選び直しでなくクリックごとに打つ（同じ姿勢をフレームを変えて何度も貼るため）。↑↓ で選び直しただけでは打たない
            label.RegisterCallback<ClickEvent> (e => {
                if (e.button == 0 && label.userData is int index) ApplyPose (index);
            });
        }

        protected override void BindRow (Label label, Row row, int index)
        {
            label.userData = index;
            label.text = row.text;
            StyleRow (label, row, FontStyle.Normal);
            label.tooltip = row.path != null ? Tr ("POSE_BANK_OVERLAY_ROW_TOOLTIP", row.path) : null;
        }

        /// <summary>選んでいた姿勢は選んだまま。打つ範囲（選んでいる骨の数）を出し直す</summary>
        protected override void AfterRefresh (string selectedPath)
        {
            SelectPath (selectedPath);
            int count = window_.selectedTargetCount;
            scope_.text = count > 0 ? Tr ("POSE_BANK_OVERLAY_SCOPE_SELECTED", count) : Tr ("POSE_BANK_OVERLAY_SCOPE_ALL");
        }

        void ApplyPose (int index)
        {
            if (index < 0 || index >= rows_.Count || rows_[index].path == null) return;
            AnimationClip pose = AssetDatabase.LoadAssetAtPath<AnimationClip> (rows_[index].path);
            int applied;
            string error = window_.ApplyPose (pose, out applied);
            if (error != null) ShowMessage (Tr ("POSE_BANK_OVERLAY_CANNOT_APPLY", error), true);
            else ShowMessage (Tr ("POSE_BANK_OVERLAY_APPLIED", rows_[index].text, applied), false);
        }

        void SavePose ()
        {
            string path = folder;
            Dictionary<EditorCurveBinding, float> values = window_.CapturePose ();
            if (path == null || values == null) {
                ShowMessage (Tr ("POSE_BANK_OVERLAY_SELECT_CHARACTER"), true);
                return;
            }
            AnimationClip pose = PoseBank.Create (path, values);
            ShowMessage (null, false);
            Refresh ();
            // 作ったら続けて名前を付けられるように
            SelectPath (AssetDatabase.GetAssetPath (pose));
            BeginRename ();
        }

        void UpdatePose ()
        {
            AnimationClip pose = renameTarget;
            if (pose == null) {
                ShowMessage (Tr ("POSE_BANK_OVERLAY_SELECT_POSE_IN_LIST"), true);
                return;
            }
            if (PoseBank.IsHumanoidPose (pose)) {
                // 置き換えると編集用リグの値の姿勢になり、他のキャラへ貼れる Humanoid の姿勢が失われる
                ShowMessage (Tr ("POSE_BANK_OVERLAY_CANNOT_UPDATE_HUMANOID"), true);
                return;
            }
            Dictionary<EditorCurveBinding, float> values = window_.CapturePose ();
            if (values == null) return;
            string name = PoseBank.BaseName (AssetDatabase.GetAssetPath (pose));
            if (!EditorUtility.DisplayDialog ("Update Pose", Tr ("POSE_BANK_OVERLAY_UPDATE_DIALOG", name), "Update", "Cancel")) return;
            PoseBank.Overwrite (pose, values);
            ShowMessage (Tr ("POSE_BANK_OVERLAY_UPDATED", name), false);
        }

        void DeletePose ()
        {
            AnimationClip pose = renameTarget;
            if (pose == null) return;
            string path = AssetDatabase.GetAssetPath (pose);
            if (!EditorUtility.DisplayDialog ("Delete Pose", Tr ("POSE_BANK_OVERLAY_DELETE_DIALOG", path), "Delete", "Cancel")) return;
            if (!PoseBank.Delete (pose)) ShowMessage (Tr ("POSE_BANK_OVERLAY_COULD_NOT_DELETE", path), true);
            else ShowMessage (null, false);
            Refresh ();
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "PoseBank.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-posebank", "PoseBank", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top, defaultDockIndex = 0)]
    sealed class PoseBankOverlayScene : PoseBankOverlay
    {
    }

}
