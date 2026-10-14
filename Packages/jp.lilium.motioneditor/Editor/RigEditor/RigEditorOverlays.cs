using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEditor.UIElements;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// リグエディタの上端のツールバー（見るキャラ・直す定義・定義の複製・作り直し）
    /// </summary>
    [Icon (Icons.kFolder + "RigValues.png")]
    [Overlay (typeof (RigEditorWindow), "lilium-rig-editor-toolbar", "Rig Editor", defaultDisplay = true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top)]
    class RigEditorToolbarOverlay : Overlay, ICreateHorizontalToolbar
    {
        RigEditorWindow window_;
        ObjectField model_;
        ObjectField definition_;
        EditorToolbarButton duplicate_;

        public override VisualElement CreatePanelContent ()
        {
            return CreateHorizontalToolbarContent ();
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            window_ = containerWindow as RigEditorWindow;
            if (window_ == null) return toolbar;

            model_ = new ObjectField { objectType = typeof (GameObject), allowSceneObjects = false, tooltip = Tr ("RIG_EDITOR_MODEL_TOOLTIP") };
            model_.style.width = 180;
            model_.RegisterValueChangedCallback (e => window_.SetModel (e.newValue as GameObject));
            toolbar.Add (model_);

            // 空にすると、キャラが使う定義に付いていく（欄にはその定義を出す）
            definition_ = new ObjectField { objectType = typeof (EditRigDefinition), allowSceneObjects = false, tooltip = Tr ("RIG_EDITOR_DEFINITION_TOOLTIP") };
            definition_.style.width = 200;
            definition_.RegisterValueChangedCallback (e => {
                EditRigDefinition chosen = e.newValue as EditRigDefinition;
                // キャラが使う定義をそのまま選び直しただけなら、付いていくままにする
                window_.SetDefinition (chosen != null && (window_.definitionChosen || chosen != window_.definition) ? chosen : null);
                Refresh ();
            });
            toolbar.Add (definition_);

            // 定義の欄の横に置く（パッケージの既定など、ここで直せない定義は複製して直す）
            duplicate_ = new EditorToolbarButton (ShowDuplicateMenu) { tooltip = Tr ("RIG_EDITOR_DUPLICATE_TOOLTIP") };
            toolbar.Add (duplicate_);

            toolbar.Add (new EditorToolbarButton ("Reload", window_.Rebuild) { tooltip = Tr ("RIG_EDITOR_RELOAD_TOOLTIP") });

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            return toolbar;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) window_.stateChanged -= Refresh;
            base.OnWillBeDestroyed ();
        }

        void Refresh ()
        {
            if (window_ == null || model_ == null) return;
            model_.SetValueWithoutNotify (window_.model);
            EditRigDefinition definition = window_.definition;
            definition_.SetValueWithoutNotify (definition);
            // 定義のアセットが無いときは、既定の中身を保存して直す
            duplicate_.text = definition == null || !EditorUtility.IsPersistent (definition) ? Tr ("RIG_EDITOR_SAVE") : Tr ("RIG_EDITOR_DUPLICATE");
            duplicate_.SetEnabled (window_.model != null);
        }

        /// <summary>複製をどこに使うかを選ぶ</summary>
        void ShowDuplicateMenu ()
        {
            GenericMenu menu = new GenericMenu ();
            GameObject model = window_.model;
            if (model != null && EditorUtility.IsPersistent (model)) {
                menu.AddItem (new GUIContent (Tr ("RIG_EDITOR_ASSIGN_CHARACTER", model.name)), false, () => window_.DuplicateDefinition (RigDefinitionAssets.AssignTarget.Character));
            }
            menu.AddItem (new GUIContent (Tr ("RIG_EDITOR_ASSIGN_PROJECT")), false, () => window_.DuplicateDefinition (RigDefinitionAssets.AssignTarget.Project));
            menu.AddItem (new GUIContent (Tr ("RIG_EDITOR_ASSIGN_NONE")), false, () => window_.DuplicateDefinition (RigDefinitionAssets.AssignTarget.None));
            menu.DropDown (duplicate_.worldBound);
        }
    }

    /// <summary>
    /// リグエディタの左の一覧。定義のコントロールを種類ごとに並べ、表示域の選択と合わせる。
    /// 上に定義の誤り・キャラ全体の問題を、下に選んだコントロールの使えない理由を出す
    /// </summary>
    [Icon (Icons.kFolder + "Picker.png")]
    [Overlay (typeof (RigEditorWindow), "lilium-rig-editor-controls", "Controls", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    class RigEditorControlsOverlay : Overlay
    {
        const int kRowHeight = 18;
        const float kWidth = 220;

        /// <summary>一覧の 1 行。見出し（種類）の行は entry が null</summary>
        sealed class Row
        {
            public RigControlKind kind;
            public RigControlEntry entry;
            public int count;
        }

        RigEditorWindow window_;
        VisualElement problems_;
        TreeView tree_;
        Label detail_;
        readonly Dictionary<string, int> idByEntry_ = new Dictionary<string, int> ();
        bool syncing_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = kWidth;
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            root.style.height = 420;
            window_ = containerWindow as RigEditorWindow;
            if (window_ == null) return root;

            problems_ = new VisualElement ();
            root.Add (problems_);

            tree_ = new TreeView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = () => new Label { style = { unityTextAlign = TextAnchor.MiddleLeft } },
                bindItem = BindRow,
            };
            tree_.style.flexGrow = 1;
            tree_.selectionChanged += OnTreeSelectionChanged;
            root.Add (tree_);

            detail_ = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2, marginLeft = 2, marginRight = 2 } };
            root.Add (detail_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            window_.selectionChanged -= SyncSelection;
            window_.selectionChanged += SyncSelection;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.selectionChanged -= SyncSelection;
            }
            base.OnWillBeDestroyed ();
        }

        void Refresh ()
        {
            if (window_ == null || tree_ == null) return;

            problems_.Clear ();
            for (int i = 0; i < window_.problems.Count; i++) {
                problems_.Add (new HelpBox (window_.problems[i], i < window_.errorCount ? HelpBoxMessageType.Error : HelpBoxMessageType.Warning));
            }

            // 見出しの行の番号は負、コントロールの行は 0 から。作り直しても同じコントロールは同じ番号にはならないので、選択は名前で合わせ直す
            idByEntry_.Clear ();
            List<TreeViewItemData<Row>> roots = new List<TreeViewItemData<Row>> ();
            int nextId = 0;
            foreach (IGrouping<RigControlKind, RigControlEntry> group in window_.entries.GroupBy (e => e.kind).OrderBy (g => g.Key)) {
                List<TreeViewItemData<Row>> children = new List<TreeViewItemData<Row>> ();
                foreach (RigControlEntry entry in group) {
                    idByEntry_[entry.id] = nextId;
                    children.Add (new TreeViewItemData<Row> (nextId++, new Row { kind = entry.kind, entry = entry }));
                }
                roots.Add (new TreeViewItemData<Row> (-1 - (int)group.Key, new Row { kind = group.Key, count = children.Count }, children));
            }
            tree_.SetRootItems (roots);
            tree_.Rebuild ();
            tree_.ExpandAll ();
            SyncSelection ();
        }

        void BindRow (VisualElement element, int index)
        {
            Label label = (Label)element;
            Row row = tree_.GetItemDataForIndex<Row> (index);
            if (row.entry == null) {
                label.text = RigControlList.KindLabel (row.kind) + " (" + row.count + ")";
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.color = StyleKeyword.Null;
                label.tooltip = null;
                return;
            }
            RigControlEntry entry = row.entry;
            label.text = entry.label;
            label.style.unityFontStyleAndWeight = FontStyle.Normal;
            // 使えるものは種類の色、使えないものは灰色
            label.style.color = entry.enabled ? (StyleColor)RigEditorWindow.KindColor (entry.kind) : new StyleColor (new Color (0.55f, 0.55f, 0.55f));
            label.tooltip = entry.disabledReason;
        }

        void OnTreeSelectionChanged (IEnumerable<object> items)
        {
            if (syncing_ || window_ == null) return;
            Row row = items.OfType<Row> ().FirstOrDefault ();
            // 見出しを選んだときは、表示域の選択は変えない
            if (row == null || row.entry == null) return;
            window_.Select (row.entry.id);
        }

        /// <summary>表示域で選んだものを一覧でも選び、下に理由を出す</summary>
        void SyncSelection ()
        {
            if (window_ == null || tree_ == null) return;
            RigControlEntry selected = window_.selectedEntry;
            syncing_ = true;
            try {
                int id;
                if (selected != null && idByEntry_.TryGetValue (selected.id, out id)) {
                    tree_.SetSelectionById (id);
                    tree_.ScrollToItemById (id);
                }
                else {
                    tree_.ClearSelection ();
                }
            }
            finally {
                syncing_ = false;
            }
            detail_.text = selected == null ? ""
                : selected.disabledReason != null ? selected.label + ": " + selected.disabledReason
                : selected.label;
        }
    }

    /// <summary>
    /// リグエディタの右のパネル（S45b）。選んだコントロールの値を定義の欄で直す（選んでいなければ定義全体の解き方）。
    /// 値は欄を変えるたびに定義へ書き、窓とモーションエディタはまとめて 1 回作り直す（インスペクタで直したときと同じ）。
    /// パッケージの既定など直せない定義では欄を触れなくし、理由を出す（複製はツールバーの定義の欄の横）。下に、その定義を使っているキャラを出し、押すとそのキャラに替える
    /// </summary>
    [Icon (Icons.kFolder + "Properties.png")]
    [Overlay (typeof (RigEditorWindow), "lilium-rig-editor-inspector", "Inspector", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Top)]
    class RigEditorInspectorOverlay : Overlay
    {
        const float kWidth = 300;

        RigEditorWindow window_;
        HelpBox readOnly_;
        Label title_;
        VisualElement fields_;
        Foldout users_;
        /// <summary>今の欄を作った定義と項目。変わったときだけ欄を作り直す（値を変えるたびに作り直すと、打っている欄から外れる）</summary>
        EditRigDefinition boundDefinition_;
        string boundPath_;
        bool boundReadOnly_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = kWidth;
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            root.style.maxHeight = 520;
            window_ = containerWindow as RigEditorWindow;
            if (window_ == null) return root;

            readOnly_ = new HelpBox ("", HelpBoxMessageType.Info);
            root.Add (readOnly_);

            ScrollView scroll = new ScrollView (ScrollViewMode.Vertical);
            scroll.style.flexShrink = 1;
            title_ = new Label { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4, marginBottom = 2 } };
            scroll.Add (title_);
            fields_ = new VisualElement ();
            scroll.Add (fields_);
            users_ = new Foldout { text = Tr ("RIG_EDITOR_USERS"), value = true };
            users_.style.marginTop = 6;
            scroll.Add (users_);
            root.Add (scroll);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            window_.selectionChanged -= Refresh;
            window_.selectionChanged += Refresh;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.selectionChanged -= Refresh;
            }
            base.OnWillBeDestroyed ();
        }

        void Refresh ()
        {
            if (window_ == null || fields_ == null) return;
            EditRigDefinition definition = window_.definition;
            string reason = window_.model != null ? window_.readOnlyReason : null;
            readOnly_.text = reason ?? "";
            readOnly_.style.display = reason != null ? DisplayStyle.Flex : DisplayStyle.None;

            RigControlEntry selected = window_.selectedEntry;
            title_.text = selected != null ? RigControlList.KindLabel (selected.kind) + ": " + selected.label : Tr ("RIG_EDITOR_DEFINITION_SETTINGS");
            string path = selected != null ? selected.propertyPath : null;
            if (definition != boundDefinition_ || path != boundPath_ || (reason != null) != boundReadOnly_) {
                boundDefinition_ = definition;
                boundPath_ = path;
                boundReadOnly_ = reason != null;
                BuildFields (definition, selected, path);
            }
            fields_.SetEnabled (reason == null);
            RefreshUsers (definition);
        }

        void BuildFields (EditRigDefinition definition, RigControlEntry selected, string path)
        {
            fields_.Unbind ();
            fields_.Clear ();
            if (definition == null) return;
            SerializedObject serialized = new SerializedObject (definition);
            SerializedProperty property = path != null ? serialized.FindProperty (path) : null;
            if (property != null) {
                property.isExpanded = true;
                fields_.Add (new PropertyField (property, selected.label));
            }
            else {
                // 選んでいないとき（と、定義に項目の無いもの）は、定義全体の解き方
                fields_.Add (new PropertyField (serialized.FindProperty ("solver")));
                SerializedProperty fullBody = serialized.FindProperty ("fullBody");
                fullBody.isExpanded = true;
                fields_.Add (new PropertyField (fullBody));
            }
            fields_.Bind (serialized);
        }

        void RefreshUsers (EditRigDefinition definition)
        {
            users_.Clear ();
            foreach (RigDefinitionAssets.User user in window_.users) {
                GameObject prefab = user.prefab;
                string text = user.viaProject ? Tr ("RIG_EDITOR_USER_VIA_PROJECT", prefab.name) : Tr ("RIG_EDITOR_USER_VIA_SETTINGS", prefab.name);
                Button button = new Button (() => window_.SetModel (prefab)) { text = text };
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                // 今出しているキャラは押せない（押しても同じ）
                button.SetEnabled (prefab != window_.model);
                users_.Add (button);
            }
            if (window_.users.Count == 0) users_.Add (new Label (Tr ("RIG_EDITOR_USERS_NONE")));
            if (definition != null && definition == ProjectSettings.instance.defaultRigDefinition) {
                users_.Add (new Label (Tr ("RIG_EDITOR_USERS_PROJECT_DEFAULT_NOTE")) { style = { whiteSpace = WhiteSpace.Normal, marginTop = 2 } });
            }
        }
    }

}
