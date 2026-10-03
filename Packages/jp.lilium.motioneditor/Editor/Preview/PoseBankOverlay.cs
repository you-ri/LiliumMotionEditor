using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.IO;

namespace Lilium
{

    /// <summary>
    /// PoseBank（AnimBank の姿勢版）。このキャラのフォルダにある姿勢の一覧。
    /// 行をクリックすると、その姿勢を今のフレームへ打つ。骨を選んでいればその骨のぶんだけ（手の指だけ、など）、何も選んでいなければ姿勢全部。
    /// Save で今の姿勢（全部）を新しい姿勢として保存し、Update で一覧で選んでいる姿勢を今の姿勢に置き換える
    /// </summary>
    [Icon (Icons.kFolder + "PoseBank.png")]
    [Overlay (typeof (PreviewWindow), "mkt-posebank", "PoseBank", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    class PoseBankOverlay : Overlay
    {
        const int kRowHeight = 18;

        PreviewWindow window_;
        Label folder_;
        ListView list_;
        TextField rename_;
        Label scope_;
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
            Button choose = new Button (ChooseFolder) { text = "…", tooltip = "このキャラの姿勢を作る場所を選ぶ（キャラの設定に書く。取り消すとプロジェクト設定の値に戻す）" };
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
            list_.style.height = 160;
            root.Add (list_);

            scope_ = new Label ();
            scope_.style.fontSize = 10;
            scope_.style.color = new Color (0.65f, 0.65f, 0.65f);
            root.Add (scope_);

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            AddButton (buttons, "Save", "今の姿勢（全部）を新しい姿勢として保存する", SavePose);
            AddButton (buttons, "Update", "一覧で選んでいる姿勢を、今の姿勢（全部）で置き換える", UpdatePose);
            AddButton (buttons, "Rename", "一覧で選んでいる姿勢の名前を変える", BeginRename);
            AddButton (buttons, "Del", "一覧で選んでいる姿勢をゴミ箱へ移す", DeletePose);
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
            message_.style.display = DisplayStyle.None;
            root.Add (message_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            EditorApplication.projectChanged -= Refresh;
            EditorApplication.projectChanged += Refresh;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) window_.stateChanged -= Refresh;
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

        VisualElement MakeRow ()
        {
            Label label = new Label ();
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            // 選び直しでなくクリックごとに打つ（同じ姿勢をフレームを変えて何度も貼るため）。↑↓ で選び直しただけでは打たない
            label.RegisterCallback<ClickEvent> (e => {
                if (e.button == 0 && label.userData is int index) ApplyPose (index);
            });
            return label;
        }

        void BindRow (VisualElement element, int index)
        {
            if (index < 0 || index >= rows_.Count) return;
            Row row = rows_[index];
            Label label = (Label)element;
            label.userData = index;
            label.text = row.text;
            label.style.paddingLeft = 2 + row.depth * 10;
            label.style.unityFontStyleAndWeight = row.path == null ? FontStyle.Italic : FontStyle.Normal;
            label.style.color = row.path == null ? new StyleColor (new Color (0.65f, 0.65f, 0.65f)) : new StyleColor (StyleKeyword.Null);
            label.tooltip = row.path != null ? row.path + "\nクリックで今のフレームへ打つ（骨を選んでいればその骨だけ）" : null;
        }

        /// <summary>一覧に並べるフォルダ（先頭が作る場所。キャラの設定・プロジェクト設定で決まる）</summary>
        List<string> folders
        {
            get { return window_ != null ? PoseBank.GetFolders (window_.model) : new List<string> (); }
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
                foreach (PoseBank.Entry entry in PoseBank.List (path)) {
                    if (entry.group != group) {
                        group = entry.group;
                        if (group.Length > 0) rows_.Add (new Row { text = group + "/", depth = indent });
                    }
                    rows_.Add (new Row { path = entry.path, text = entry.name, depth = indent + (entry.group.Length > 0 ? 1 : 0) });
                }
            }
        }

        /// <summary>一覧で選んでいる姿勢（見出しや未選択なら null）</summary>
        AnimationClip selectedPose
        {
            get {
                int index = list_ != null ? list_.selectedIndex : -1;
                if (index < 0 || index >= rows_.Count || rows_[index].path == null) return null;
                return AssetDatabase.LoadAssetAtPath<AnimationClip> (rows_[index].path);
            }
        }

        /// <summary>
        /// 一覧を作り直す（キャラ・選択が替わったとき、プロジェクトのファイルが変わったとき）。選んでいた姿勢は選んだまま
        /// </summary>
        void Refresh ()
        {
            if (window_ == null || list_ == null) return;
            List<string> list = folders;
            string path = list.Count > 0 ? list[0] : null;
            folder_.text = path ?? "キャラを選んでください";
            folder_.tooltip = list.Count > 0 ? "作る場所: " + path + (list.Count > 1 ? "\n並べるフォルダ:\n" + string.Join ("\n", list) : "") : null;

            int selected = list_.selectedIndex;
            string selectedPath = selected >= 0 && selected < rows_.Count ? rows_[selected].path : null;
            rows_.Clear ();
            AddRows (list);
            list_.Rebuild ();
            SelectPath (selectedPath);

            int count = window_.selectedTargetCount;
            scope_.text = count > 0 ? "貼る先: 選んでいる " + count + " 個" : "貼る先: 姿勢全部（何も選んでいない）";
        }

        void SelectPath (string path)
        {
            int index = path != null ? rows_.FindIndex (r => r.path == path) : -1;
            if (index >= 0) list_.SetSelectionWithoutNotify (new[] { index });
            else list_.ClearSelection ();
        }

        void ApplyPose (int index)
        {
            if (index < 0 || index >= rows_.Count || rows_[index].path == null) return;
            AnimationClip pose = AssetDatabase.LoadAssetAtPath<AnimationClip> (rows_[index].path);
            int applied;
            string error = window_.ApplyPose (pose, out applied);
            if (error != null) ShowMessage ("打てない: " + error, true);
            else ShowMessage (rows_[index].text + " を打った（" + applied + " カーブ）", false);
        }

        void ChooseFolder ()
        {
            if (window_.model == null) return;
            if (window_.characterSettings == null
                && EditorUtility.DisplayDialog ("Motion Editor", "フォルダの指定はキャラの設定に保存します（チームで共有されます）。このキャラの設定を作りますか？\n作らないと、この PC だけに覚えます。", "作る", "作らない")) {
                window_.CreateCharacterSettings ();
            }
            string start = folder ?? "Assets";
            string chosen = EditorUtility.OpenFolderPanel ("Pose Folder", AssetDatabase.IsValidFolder (start) ? start : "Assets", "");
            if (string.IsNullOrEmpty (chosen)) {
                PoseBank.SetFolder (window_.model, null);
            }
            else {
                string project = Path.GetFullPath (Application.dataPath + "/..").Replace ('\\', '/').TrimEnd ('/') + "/";
                chosen = chosen.Replace ('\\', '/');
                if (!chosen.StartsWith (project)) {
                    ShowMessage ("プロジェクトの中のフォルダを選んでください", true);
                    return;
                }
                PoseBank.SetFolder (window_.model, chosen.Substring (project.Length));
            }
            ShowMessage (null, false);
            Refresh ();
        }

        void SavePose ()
        {
            string path = folder;
            Dictionary<EditorCurveBinding, float> values = window_.CapturePose ();
            if (path == null || values == null) {
                ShowMessage ("キャラを選んでください", true);
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
            AnimationClip pose = selectedPose;
            if (pose == null) {
                ShowMessage ("一覧で姿勢を選んでください", true);
                return;
            }
            if (PoseBank.IsHumanoidPose (pose)) {
                // 置き換えると編集用リグの値の姿勢になり、他のキャラへ貼れる Humanoid の姿勢が失われる
                ShowMessage ("Humanoid の姿勢は置き換えられない（Save で新しく保存してください）", true);
                return;
            }
            Dictionary<EditorCurveBinding, float> values = window_.CapturePose ();
            if (values == null) return;
            string name = PoseBank.BaseName (AssetDatabase.GetAssetPath (pose));
            if (!EditorUtility.DisplayDialog ("Update Pose", name + " を今の姿勢で置き換えます。", "Update", "Cancel")) return;
            PoseBank.Overwrite (pose, values);
            ShowMessage (name + " を置き換えた", false);
        }

        void BeginRename ()
        {
            AnimationClip pose = selectedPose;
            if (pose == null) return;
            rename_.SetValueWithoutNotify (PoseBank.BaseName (AssetDatabase.GetAssetPath (pose)));
            rename_.style.display = DisplayStyle.Flex;
            rename_.schedule.Execute (() => {
                rename_.Focus ();
                rename_.SelectAll ();
            });
        }

        void CommitRename (string value)
        {
            AnimationClip pose = selectedPose;
            if (rename_.style.display == DisplayStyle.None || pose == null) return;
            string error = PoseBank.Rename (pose, value);
            ShowMessage (error != null ? "名前を変えられない: " + error : null, true);
            EndRename ();
            Refresh ();
            SelectPath (AssetDatabase.GetAssetPath (pose));
        }

        void EndRename ()
        {
            rename_.style.display = DisplayStyle.None;
        }

        void DeletePose ()
        {
            AnimationClip pose = selectedPose;
            if (pose == null) return;
            string path = AssetDatabase.GetAssetPath (pose);
            if (!EditorUtility.DisplayDialog ("Delete Pose", path + "\n\nをゴミ箱へ移します。", "Delete", "Cancel")) return;
            if (!PoseBank.Delete (pose)) ShowMessage ("消せなかった: " + path, true);
            else ShowMessage (null, false);
            Refresh ();
        }

        void ShowMessage (string text, bool warning)
        {
            message_.text = text ?? "";
            message_.style.color = warning ? new StyleColor (new Color (1, 0.6f, 0.4f)) : new StyleColor (new Color (0.65f, 0.65f, 0.65f));
            message_.style.display = string.IsNullOrEmpty (text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "PoseBank.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-posebank", "PoseBank", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    sealed class PoseBankOverlayScene : PoseBankOverlay
    {
    }

}
