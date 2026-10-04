using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.IO;
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
    class AnimBankOverlay : Overlay
    {
        const int kRowHeight = 18;

        PreviewWindow window_;
        Label folder_;
        ListView list_;
        TextField rename_;
        Label message_;
        readonly List<Row> rows_ = new List<Row> ();

        sealed class Row
        {
            /// <summary>組（サブフォルダ）の見出しなら null</summary>
            public string path;
            public string text;
            public int depth;
        }

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 170;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 170);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            VisualElement header = new VisualElement ();
            header.style.flexDirection = FlexDirection.Row;
            folder_ = new Label ();
            folder_.style.flexGrow = 1;
            folder_.style.flexShrink = 1;
            folder_.style.overflow = Overflow.Hidden;
            folder_.style.textOverflow = TextOverflow.Ellipsis;
            folder_.style.unityTextAlign = TextAnchor.MiddleLeft;
            folder_.style.fontSize = 10;
            header.Add (folder_);
            Button choose = new Button (ChooseFolder) { text = "…", tooltip = Tr ("ANIM_BANK_OVERLAY_CHOOSE_FOLDER_TOOLTIP") };
            choose.style.width = 20;
            header.Add (choose);
            root.Add (header);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = rows_,
            };
            list_.style.height = 200;
            list_.selectionChanged += OnSelectionChanged;
            root.Add (list_);

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            AddButton (buttons, "New", Tr ("ANIM_BANK_OVERLAY_NEW_TOOLTIP"), CreateClip);
            AddButton (buttons, "Dup", Tr ("ANIM_BANK_OVERLAY_DUP_TOOLTIP"), DuplicateClip);
            AddButton (buttons, "Rename", Tr ("ANIM_BANK_OVERLAY_RENAME_TOOLTIP"), BeginRename);
            AddButton (buttons, "Del", Tr ("ANIM_BANK_OVERLAY_DELETE_TOOLTIP"), DeleteClip);
            root.Add (buttons);

            rename_ = new TextField { isDelayed = true };
            rename_.style.display = DisplayStyle.None;
            rename_.RegisterValueChangedCallback (e => CommitRename (e.newValue));
            rename_.RegisterCallback<KeyDownEvent> (e => {
                if (e.keyCode == KeyCode.Escape) EndRename ();
            }, TrickleDown.TrickleDown);
            rename_.RegisterCallback<FocusOutEvent> (e => EndRename ());
            root.Add (rename_);

            message_ = new Label ();
            message_.style.whiteSpace = WhiteSpace.Normal;
            message_.style.fontSize = 10;
            message_.style.color = new Color (1, 0.6f, 0.4f);
            message_.style.display = DisplayStyle.None;
            root.Add (message_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            EditorApplication.projectChanged -= Refresh;
            EditorApplication.projectChanged += Refresh;
            window_.keysChanged -= RefreshMarks;
            window_.keysChanged += RefreshMarks;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.keysChanged -= RefreshMarks;
            }
            EditorApplication.projectChanged -= Refresh;
            base.OnWillBeDestroyed ();
        }

        static void AddButton (VisualElement parent, string text, string tooltip, System.Action action)
        {
            Button button = new Button (action) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.paddingLeft = 1;
            button.style.paddingRight = 1;
            parent.Add (button);
        }

        static VisualElement MakeRow ()
        {
            Label label = new Label ();
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            return label;
        }

        void BindRow (VisualElement element, int index)
        {
            if (index < 0 || index >= rows_.Count) return;
            Row row = rows_[index];
            Label label = (Label)element;
            string current = window_ != null && window_.clip != null ? AssetDatabase.GetAssetPath (window_.clip) : null;
            bool editing = row.path != null && row.path == current;
            bool dirty = editing && EditorUtility.IsDirty (window_.clip);
            label.text = row.path == null ? row.text : (editing ? "▶ " : "   ") + row.text + (dirty ? " *" : "");
            label.style.paddingLeft = 2 + row.depth * 10;
            label.style.unityFontStyleAndWeight = row.path == null ? FontStyle.Italic : editing ? FontStyle.Bold : FontStyle.Normal;
            label.style.color = row.path == null ? new StyleColor (new Color (0.65f, 0.65f, 0.65f)) : new StyleColor (StyleKeyword.Null);
            label.tooltip = row.path;
        }

        /// <summary>一覧に並べるフォルダ（先頭が作る場所。キャラの設定・プロジェクト設定で決まる）</summary>
        List<string> folders
        {
            get { return window_ != null ? AnimBank.GetFolders (window_.model) : new List<string> (); }
        }

        string folder
        {
            get {
                List<string> list = folders;
                return list.Count > 0 ? list[0] : null;
            }
        }

        /// <summary>
        /// フォルダごとの項目を並べる。フォルダが 2 つ以上なら、フォルダの見出しを付けて字下げする
        /// </summary>
        void AddRows (List<string> list)
        {
            bool many = list.Count > 1;
            foreach (string path in list) {
                if (many) rows_.Add (new Row { text = path.StartsWith ("Assets/") ? path.Substring ("Assets/".Length) : path, depth = 0 });
                int indent = many ? 1 : 0;
                string group = null;
                foreach (AnimBank.Entry entry in AnimBank.List (path)) {
                    if (entry.group != group) {
                        group = entry.group;
                        if (group.Length > 0) rows_.Add (new Row { text = group + "/", depth = indent });
                    }
                    rows_.Add (new Row { path = entry.path, text = entry.name, depth = indent + (entry.group.Length > 0 ? 1 : 0) });
                }
            }
        }

        /// <summary>
        /// 一覧を作り直す（キャラ・クリップが替わったとき、プロジェクトのファイルが変わったとき）
        /// </summary>
        void Refresh ()
        {
            if (window_ == null || list_ == null) return;
            List<string> list = folders;
            string path = list.Count > 0 ? list[0] : null;
            folder_.text = path ?? Tr ("POSE_BANK_OVERLAY_SELECT_CHARACTER");
            folder_.tooltip = list.Count > 0 ? (list.Count > 1 ? Tr ("POSE_BANK_OVERLAY_FOLDER_LIST_TOOLTIP", path, string.Join ("\n", list)) : Tr ("POSE_BANK_OVERLAY_FOLDER_TOOLTIP", path)) : null;
            rows_.Clear ();
            AddRows (list);
            list_.Rebuild ();

            string current = window_.clip != null ? AssetDatabase.GetAssetPath (window_.clip) : null;
            int index = rows_.FindIndex (r => r.path != null && r.path == current);
            if (index >= 0) list_.SetSelectionWithoutNotify (new[] { index });
            else list_.ClearSelection ();
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

        void ChooseFolder ()
        {
            if (window_.model == null) return;
            if (window_.characterSettings == null
                && EditorUtility.DisplayDialog ("Motion Editor", Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG"), Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG_CREATE"), Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG_DONT_CREATE"))) {
                window_.CreateCharacterSettings ();
            }
            string start = folder ?? "Assets";
            string chosen = EditorUtility.OpenFolderPanel ("Motion Folder", AssetDatabase.IsValidFolder (start) ? start : "Assets", "");
            if (string.IsNullOrEmpty (chosen)) {
                AnimBank.SetFolder (window_.model, null);
            }
            else {
                string project = Path.GetFullPath (Application.dataPath + "/..").Replace ('\\', '/').TrimEnd ('/') + "/";
                chosen = chosen.Replace ('\\', '/');
                if (!chosen.StartsWith (project)) {
                    ShowMessage (Tr ("POSE_BANK_OVERLAY_CHOOSE_FOLDER_IN_PROJECT"));
                    return;
                }
                AnimBank.SetFolder (window_.model, chosen.Substring (project.Length));
            }
            ShowMessage (null);
            Refresh ();
        }

        void CreateClip ()
        {
            string path = folder;
            if (path == null) {
                ShowMessage (Tr ("POSE_BANK_OVERLAY_SELECT_CHARACTER"));
                return;
            }
            AnimationClip clip = AnimBank.Create (path);
            window_.SetClip (clip);
            ShowMessage (null);
            Refresh ();
        }

        void DuplicateClip ()
        {
            if (window_.clip == null) return;
            AnimationClip clip = AnimBank.Duplicate (window_.clip);
            if (clip == null) {
                ShowMessage (Tr ("ANIM_BANK_OVERLAY_CANNOT_DUPLICATE"));
                return;
            }
            window_.SetClip (clip);
            ShowMessage (null);
            Refresh ();
        }

        void BeginRename ()
        {
            if (window_.clip == null || !EditorUtility.IsPersistent (window_.clip)) return;
            rename_.SetValueWithoutNotify (AnimBank.BaseName (AssetDatabase.GetAssetPath (window_.clip)));
            rename_.style.display = DisplayStyle.Flex;
            rename_.schedule.Execute (() => {
                rename_.Focus ();
                rename_.SelectAll ();
            });
        }

        void CommitRename (string value)
        {
            if (rename_.style.display == DisplayStyle.None || window_.clip == null) return;
            string error = AnimBank.Rename (window_.clip, value);
            ShowMessage (error != null ? Tr ("POSE_BANK_OVERLAY_CANNOT_RENAME", error) : null);
            EndRename ();
            Refresh ();
        }

        void EndRename ()
        {
            rename_.style.display = DisplayStyle.None;
        }

        void DeleteClip ()
        {
            AnimationClip clip = window_.clip;
            if (clip == null || !EditorUtility.IsPersistent (clip)) return;
            string path = AssetDatabase.GetAssetPath (clip);
            if (!EditorUtility.DisplayDialog ("Delete Motion Clip", Tr ("ANIM_BANK_OVERLAY_DELETE_DIALOG", path), "Delete", "Cancel")) return;
            window_.SetClip (null);
            if (!AnimBank.Delete (clip)) ShowMessage (Tr ("POSE_BANK_OVERLAY_COULD_NOT_DELETE", path));
            else ShowMessage (null);
            Refresh ();
        }

        void ShowMessage (string text)
        {
            message_.text = text ?? "";
            message_.style.display = string.IsNullOrEmpty (text) ? DisplayStyle.None : DisplayStyle.Flex;
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
