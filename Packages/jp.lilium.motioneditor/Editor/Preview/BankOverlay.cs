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
    /// AnimBank・PoseBank のパネルの共通部分。キャラのフォルダの一覧（フォルダが 2 つ以上・サブフォルダは見出しで分ける）、
    /// フォルダの選び直し（「…」）、名前を変える欄、知らせの行。行を押したときの意味（開く・打つ）とボタンは派生が決める
    /// </summary>
    abstract class BankOverlay : MotionOverlay
    {
        const int kRowHeight = 18;
        const float kWidth = 170;

        protected ListView list_;
        protected readonly List<Row> rows_ = new List<Row> ();
        Label folder_;
        TextField rename_;
        Label message_;

        protected sealed class Row
        {
            /// <summary>組（サブフォルダ）の見出しなら null</summary>
            public string path;
            public string text;
            public int depth;
        }

        /// <summary>このキャラの一覧に並べるフォルダ（先頭が作る場所）</summary>
        protected abstract List<string> GetFolders (GameObject model);
        /// <summary>作る場所を選び直す（null で既定へ戻す）</summary>
        protected abstract void SetFolder (GameObject model, string folder);
        protected abstract List<BankEntry> ListFolder (string folder);
        protected abstract string BaseName (string path);
        /// <summary>名前を変える。できなければ理由（できたら null）</summary>
        protected abstract string Rename (AnimationClip clip, string name);
        /// <summary>Rename で名前を変える物（無ければ null）</summary>
        protected abstract AnimationClip renameTarget { get; }
        protected abstract string chooseFolderTooltip { get; }
        /// <summary>フォルダを選ぶ窓の見出し</summary>
        protected abstract string folderPanelTitle { get; }
        protected abstract float listHeight { get; }
        /// <summary>一覧の下に並べる物（ボタンなど）</summary>
        protected abstract void AddControls (VisualElement root);
        protected abstract void BindRow (Label label, Row row, int index);
        /// <summary>一覧を並べ直した後。selectedPath は並べ直す前に選んでいた物</summary>
        protected abstract void AfterRefresh (string selectedPath);

        /// <summary>行を作ったとき（押したときの処理を付ける、など）</summary>
        protected virtual void SetUpRow (Label label)
        {
        }

        /// <summary>窓の通知を受け始める（on）・やめる。派生で足すときは base を呼ぶ（stateChanged とプロジェクトの変更で一覧を作り直す）</summary>
        protected override void Subscribe (bool on)
        {
            window_.stateChanged -= Refresh;
            EditorApplication.projectChanged -= Refresh;
            if (!on) return;
            window_.stateChanged += Refresh;
            EditorApplication.projectChanged += Refresh;
        }

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = kWidth;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            window_ = ResolveWindow ();
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
            Button choose = new Button (ChooseFolder) { text = "…", tooltip = chooseFolderTooltip };
            choose.style.width = 20;
            header.Add (choose);
            root.Add (header);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = (element, index) => {
                    if (index >= 0 && index < rows_.Count) BindRow ((Label)element, rows_[index], index);
                },
                itemsSource = rows_,
            };
            list_.style.height = listHeight;
            root.Add (list_);

            AddControls (root);

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
            message_.style.display = DisplayStyle.None;
            root.Add (message_);

            Refresh ();
            Resubscribe ();
            return root;
        }

        protected static void AddButton (VisualElement parent, string text, string tooltip, System.Action action)
        {
            Button button = PanelUI.FlexButton (text, tooltip, action);
            button.style.paddingLeft = 1;
            button.style.paddingRight = 1;
            parent.Add (button);
        }

        VisualElement MakeRow ()
        {
            Label label = new Label ();
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            SetUpRow (label);
            return label;
        }

        /// <summary>行の字下げと、見出し（組・フォルダ）の書き方</summary>
        protected static void StyleRow (Label label, Row row, FontStyle itemStyle)
        {
            label.style.paddingLeft = 2 + row.depth * 10;
            label.style.unityFontStyleAndWeight = row.path == null ? FontStyle.Italic : itemStyle;
            label.style.color = row.path == null ? new StyleColor (new Color (0.65f, 0.65f, 0.65f)) : new StyleColor (StyleKeyword.Null);
        }

        List<string> folders
        {
            get { return window_ != null ? GetFolders (window_.model) : new List<string> (); }
        }

        /// <summary>作る場所（フォルダの並びの先頭。キャラが無ければ null）</summary>
        protected string folder
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
                foreach (BankEntry entry in ListFolder (path)) {
                    if (entry.group != group) {
                        group = entry.group;
                        if (group.Length > 0) rows_.Add (new Row { text = group + "/", depth = indent });
                    }
                    rows_.Add (new Row { path = entry.path, text = entry.name, depth = indent + (entry.group.Length > 0 ? 1 : 0) });
                }
            }
        }

        /// <summary>
        /// 一覧を作り直す（キャラ・クリップ・選択が替わったとき、プロジェクトのファイルが変わったとき）
        /// </summary>
        protected void Refresh ()
        {
            if (window_ == null || list_ == null) return;
            List<string> list = folders;
            string path = list.Count > 0 ? list[0] : null;
            folder_.text = path ?? Tr ("POSE_BANK_OVERLAY_SELECT_CHARACTER");
            folder_.tooltip = list.Count > 0 ? (list.Count > 1 ? Tr ("POSE_BANK_OVERLAY_FOLDER_LIST_TOOLTIP", path, string.Join ("\n", list)) : Tr ("POSE_BANK_OVERLAY_FOLDER_TOOLTIP", path)) : null;

            int selected = list_.selectedIndex;
            string selectedPath = selected >= 0 && selected < rows_.Count ? rows_[selected].path : null;
            rows_.Clear ();
            AddRows (list);
            list_.Rebuild ();
            AfterRefresh (selectedPath);
        }

        /// <summary>一覧で path の行を選ぶ（無ければ選択を外す）。選び直しの通知は出さない</summary>
        protected void SelectPath (string path)
        {
            int index = path != null ? rows_.FindIndex (r => r.path == path) : -1;
            if (index >= 0) list_.SetSelectionWithoutNotify (new[] { index });
            else list_.ClearSelection ();
        }

        void ChooseFolder ()
        {
            if (window_.model == null) return;
            if (window_.characterSettings == null
                && EditorUtility.DisplayDialog ("Motion Editor", Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG"), Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG_CREATE"), Tr ("POSE_BANK_OVERLAY_SETTINGS_DIALOG_DONT_CREATE"))) {
                window_.CreateCharacterSettings ();
            }
            string start = folder ?? "Assets";
            string chosen = EditorUtility.OpenFolderPanel (folderPanelTitle, AssetDatabase.IsValidFolder (start) ? start : "Assets", "");
            if (string.IsNullOrEmpty (chosen)) {
                SetFolder (window_.model, null);
            }
            else {
                string project = Path.GetFullPath (Application.dataPath + "/..").Replace ('\\', '/').TrimEnd ('/') + "/";
                chosen = chosen.Replace ('\\', '/');
                if (!chosen.StartsWith (project)) {
                    ShowMessage (Tr ("POSE_BANK_OVERLAY_CHOOSE_FOLDER_IN_PROJECT"), true);
                    return;
                }
                SetFolder (window_.model, chosen.Substring (project.Length));
            }
            ShowMessage (null, false);
            Refresh ();
        }

        protected void BeginRename ()
        {
            AnimationClip clip = renameTarget;
            if (clip == null) return;
            rename_.SetValueWithoutNotify (BaseName (AssetDatabase.GetAssetPath (clip)));
            rename_.style.display = DisplayStyle.Flex;
            rename_.schedule.Execute (() => {
                rename_.Focus ();
                rename_.SelectAll ();
            });
        }

        void CommitRename (string value)
        {
            AnimationClip clip = renameTarget;
            if (rename_.style.display == DisplayStyle.None || clip == null) return;
            string error = Rename (clip, value);
            ShowMessage (error != null ? Tr ("POSE_BANK_OVERLAY_CANNOT_RENAME", error) : null, true);
            EndRename ();
            Refresh ();
            SelectPath (AssetDatabase.GetAssetPath (clip));
        }

        void EndRename ()
        {
            rename_.style.display = DisplayStyle.None;
        }

        /// <summary>知らせの行（null で隠す）。warning なら注意の色</summary>
        protected void ShowMessage (string text, bool warning)
        {
            message_.text = text ?? "";
            message_.style.color = warning ? new StyleColor (new Color (1, 0.6f, 0.4f)) : new StyleColor (new Color (0.65f, 0.65f, 0.65f));
            message_.style.display = string.IsNullOrEmpty (text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }

}
