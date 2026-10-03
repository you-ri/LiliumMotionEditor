using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// Stacker（S12）。組（All・Body・Hands・Rig と自分で作った組）ごとのポーズキーを並べ、
    /// 次までの間隔（フレーム）を直すと後ろのキーがまとめてずれる。Insert は今のポーズキーの後ろに同じ姿勢を足して押し出し、
    /// Delete は消して詰める。Ghost で前後のフレームの残像（1 フレームずつ、幅は 1〜16）、Loop で先頭のポーズキーを末尾へ写す
    /// </summary>
    [Icon (Icons.kFolder + "Stacker.png")]
    [Overlay (typeof (PreviewWindow), "mkt-stacker", "Stacker", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    class StackerOverlay : Overlay
    {
        static readonly Color kCurrentColor = new Color (0.29f, 0.45f, 0.69f, 0.45f);
        const int kRowHeight = 20;

        PreviewWindow window_;
        DropdownField group_;
        Button deleteGroup_;
        TextField groupName_;
        Toggle ghost_;
        SliderInt ghostRange_;
        Toggle loop_;
        ListView list_;
        Label message_;
        int[] keys_ = new int[0];
        readonly List<int> indices_ = new List<int> ();

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 190;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 190);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            VisualElement groupRow = new VisualElement ();
            groupRow.style.flexDirection = FlexDirection.Row;
            group_ = new DropdownField { tooltip = "組（骨のまとまり）。ポーズキーはこの組のカーブのキーをまとめたもの" };
            group_.style.flexGrow = 1;
            group_.style.marginLeft = 0;
            group_.RegisterValueChangedCallback (e => window_.stackerGroup = e.newValue);
            groupRow.Add (group_);
            Button add = new Button (BeginSaveGroup) { text = "+", tooltip = "選んでいる物で組を作る（同じ名前なら中身を置き換える）" };
            add.style.width = 20;
            groupRow.Add (add);
            deleteGroup_ = new Button (() => window_.StackerDeleteGroup ()) { text = "−", tooltip = "自分で作った組を消す" };
            deleteGroup_.style.width = 20;
            groupRow.Add (deleteGroup_);
            root.Add (groupRow);

            groupName_ = new TextField { isDelayed = true };
            groupName_.style.display = DisplayStyle.None;
            groupName_.RegisterValueChangedCallback (e => {
                if (groupName_.style.display == DisplayStyle.None) return;
                groupName_.style.display = DisplayStyle.None;
                window_.StackerSaveGroup (e.newValue);
            });
            groupName_.RegisterCallback<KeyDownEvent> (e => {
                if (e.keyCode == KeyCode.Escape) groupName_.style.display = DisplayStyle.None;
            }, TrickleDown.TrickleDown);
            root.Add (groupName_);

            VisualElement toggles = new VisualElement ();
            toggles.style.flexDirection = FlexDirection.Row;
            ghost_ = new Toggle ("Ghost") { tooltip = "前（青）と後ろ（橙）のフレームの姿を 1 フレームずつ半透明で重ねる（離れるほど薄い）" };
            ghost_.RegisterValueChangedCallback (e => window_.stackerGhost = e.newValue);
            ghost_.labelElement.style.minWidth = 0;
            ghost_.labelElement.style.width = 38;
            toggles.Add (ghost_);
            loop_ = new Toggle ("Loop") { tooltip = "先頭のポーズキーを末尾へ写しておく（先頭を直すと末尾も合う）" };
            loop_.RegisterValueChangedCallback (e => window_.stackerLoop = e.newValue);
            loop_.labelElement.style.minWidth = 0;
            loop_.labelElement.style.width = 32;
            loop_.style.marginLeft = 10;
            toggles.Add (loop_);
            root.Add (toggles);

            // 残像の幅（前後それぞれ何フレームまで）
            ghostRange_ = new SliderInt ("幅", 1, Stacker.kGhostRangeMax) { showInputField = true, tooltip = "残像を出すフレームの幅（前後それぞれ）" };
            ghostRange_.labelElement.style.minWidth = 0;
            ghostRange_.labelElement.style.width = 20;
            ghostRange_.RegisterValueChangedCallback (e => window_.stackerGhostRange = e.newValue);
            root.Add (ghostRange_);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.None,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = indices_,
            };
            list_.style.height = 160;
            root.Add (list_);

            VisualElement buttons = new VisualElement ();
            buttons.style.flexDirection = FlexDirection.Row;
            Button keyAll = new Button (() => window_.KeyAll ()) { text = "Key All", tooltip = "組の選択に関係なく、今のフレームで全部（体・手・Rig の重み・表情・任意のプロパティ）にキーを打つ" };
            keyAll.style.flexGrow = 1;
            buttons.Add (keyAll);
            Button insert = new Button (() => window_.StackerInsert ()) { text = "Insert", tooltip = "今のポーズキーの後ろに同じ姿勢を足して後ろを押し出す（ポーズキーでないフレームでは、組のキーを打つ）" };
            insert.style.flexGrow = 1;
            buttons.Add (insert);
            Button delete = new Button (() => window_.StackerDelete ()) { text = "Delete", tooltip = "今のポーズキーを消して後ろを詰める" };
            delete.style.flexGrow = 1;
            buttons.Add (delete);
            root.Add (buttons);

            message_ = new Label ();
            message_.style.fontSize = 10;
            message_.style.whiteSpace = WhiteSpace.Normal;
            message_.style.color = new Color (0.7f, 0.7f, 0.7f);
            root.Add (message_);

            Refresh ();
            window_.stateChanged += Refresh;
            window_.stackerChanged += Refresh;
            window_.keysChanged += Refresh;
            window_.poseSampled += RefreshCurrent;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.stackerChanged -= Refresh;
                window_.keysChanged -= Refresh;
                window_.poseSampled -= RefreshCurrent;
            }
            base.OnWillBeDestroyed ();
        }

        VisualElement MakeRow ()
        {
            VisualElement row = new VisualElement ();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            Label label = new Label { name = "frame" };
            label.style.flexGrow = 1;
            label.style.paddingLeft = 4;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add (label);
            IntegerField interval = new IntegerField { name = "interval", isDelayed = true, tooltip = "次のポーズキーまでのフレーム数。変えると後ろのキーがまとめてずれる" };
            interval.style.width = 44;
            interval.RegisterValueChangedCallback (e => {
                int index = (int)interval.userData;
                if (index >= 0 && index + 1 < keys_.Length && e.newValue >= 1) window_.StackerSetInterval (keys_[index], e.newValue);
                else interval.SetValueWithoutNotify (e.previousValue);
            });
            row.Add (interval);
            row.RegisterCallback<ClickEvent> (e => {
                if (e.target is VisualElement element && (element == interval || interval.Contains (element))) return;
                int index = (int)row.userData;
                if (index >= 0 && index < keys_.Length) window_.SetFrame (keys_[index]);
            });
            return row;
        }

        void BindRow (VisualElement element, int index)
        {
            if (index < 0 || index >= keys_.Length) return;
            element.userData = index;
            Label label = element.Q<Label> ("frame");
            label.text = "#" + (index + 1) + "   " + keys_[index] + "F";
            IntegerField interval = element.Q<IntegerField> ("interval");
            interval.userData = index;
            bool hasNext = index + 1 < keys_.Length;
            interval.SetEnabled (hasNext);
            interval.SetValueWithoutNotify (hasNext ? keys_[index + 1] - keys_[index] : 0);
            element.style.backgroundColor = keys_[index] == window_.clock.keyFrame ? new StyleColor (kCurrentColor) : new StyleColor (StyleKeyword.Null);
        }

        void Refresh ()
        {
            if (window_ == null || list_ == null) return;
            List<string> names = window_.stackerGroupNames;
            group_.choices = names;
            group_.SetValueWithoutNotify (window_.stackerGroup);
            deleteGroup_.SetEnabled (window_.isCustomStackerGroup);
            ghost_.SetValueWithoutNotify (window_.stackerGhost);
            ghostRange_.SetValueWithoutNotify (window_.stackerGhostRange);
            ghostRange_.SetEnabled (window_.stackerGhost);
            loop_.SetValueWithoutNotify (window_.stackerLoop);

            keys_ = window_.stackerKeys;
            indices_.Clear ();
            for (int i = 0; i < keys_.Length; i++) indices_.Add (i);
            list_.RefreshItems ();
            message_.text = window_.clip == null ? "クリップを開いてください" : keys_.Length == 0 ? "この組にはキーが無い" : "";
        }

        /// <summary>
        /// フレームが動いたとき（今のポーズキーの色だけ直す）
        /// </summary>
        void RefreshCurrent ()
        {
            if (list_ != null) list_.RefreshItems ();
        }

        void BeginSaveGroup ()
        {
            groupName_.SetValueWithoutNotify (window_.isCustomStackerGroup ? window_.stackerGroup : "Group");
            groupName_.style.display = DisplayStyle.Flex;
            groupName_.schedule.Execute (() => {
                groupName_.Focus ();
                groupName_.SelectAll ();
            });
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Stacker.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-stacker", "Stacker", defaultDisplay = true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    sealed class StackerOverlayScene : StackerOverlay
    {
    }

}
