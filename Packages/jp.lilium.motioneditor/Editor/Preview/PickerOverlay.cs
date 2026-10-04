using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Picker。骨と手足の IK が骨のつながりの順に並ぶ。クリックで選び、↑↓ / Home / End で移せる。
    /// 表示域でメッシュをクリックしたときの選択（PreviewWindow.SelectByMeshClick）とも一致する
    /// </summary>
    [Icon (Icons.kFolder + "Picker.png")]
    [Overlay (typeof (PreviewWindow), "mkt-picker", "Picker", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    class PickerOverlay : Overlay
    {
        const int kRowHeight = 18;

        PreviewWindow window_;
        ListView list_;
        IList<PreviewWindow.PickerEntry> entries_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 170;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 170);
            // ボタンが 3 段あるぶん、一覧の高さが今までと同じくらいになるようにしておく
            root.style.height = 360;
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            root.Add (MakeSelectButtons ());

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                // Ctrl / Shift でも複数選べる（ボタンの All / Right / Left と同じ選択）
                selectionType = SelectionType.Multiple,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            list_.style.flexGrow = 1;
            list_.selectionChanged += OnSelectionChanged;
            // ↑↓ は ListView 自身が動かす（横取りすると、キーから作られる移動イベントと二重に効いて 2 行飛ぶ）。Home / End だけ受ける
            list_.RegisterCallback<KeyDownEvent> (OnKeyDown, TrickleDown.TrickleDown);
            root.Add (list_);
            // Home / End をタイムラインでなく一覧に効かせるため、窓にこのパネルを教える
            window_.SetPickerList (list_);

            Refresh ();
            window_.stateChanged -= Refresh;
            window_.stateChanged += Refresh;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) window_.stateChanged -= Refresh;
            base.OnWillBeDestroyed ();
        }

        /// <summary>
        /// 一覧の上のボタンの段（よく使う選択をまとめて出す）
        /// </summary>
        VisualElement MakeSelectButtons ()
        {
            VisualElement column = new VisualElement ();
            column.style.marginBottom = 2;

            VisualElement sides = MakeButtonRow ();
            sides.Add (MakeSelectButton ("All", Tr ("PICKER_OVERLAY_ALL_TOOLTIP"), () => window_.SelectPickerAll ()));
            sides.Add (MakeSelectButton ("Right", Tr ("PICKER_OVERLAY_RIGHT_TOOLTIP"), () => window_.SelectPickerSide (true)));
            sides.Add (MakeSelectButton ("Left", Tr ("PICKER_OVERLAY_LEFT_TOOLTIP"), () => window_.SelectPickerSide (false)));
            column.Add (sides);

            VisualElement body = MakeButtonRow ();
            body.Add (MakeSelectButton (Tr ("PICKER_OVERLAY_UPPER_BODY"), Tr ("PICKER_OVERLAY_UPPER_BODY_TOOLTIP"), () => window_.SelectPickerBody (true)));
            body.Add (MakeSelectButton (Tr ("PICKER_OVERLAY_LOWER_BODY"), Tr ("PICKER_OVERLAY_LOWER_BODY_TOOLTIP"), () => window_.SelectPickerBody (false)));
            column.Add (body);

            VisualElement fingers = MakeButtonRow ();
            fingers.Add (MakeSelectButton (Tr ("PICKER_OVERLAY_RIGHT_FINGERS"), Tr ("PICKER_OVERLAY_RIGHT_FINGERS_TOOLTIP"), () => window_.SelectPickerFingers (true)));
            fingers.Add (MakeSelectButton (Tr ("PICKER_OVERLAY_LEFT_FINGERS"), Tr ("PICKER_OVERLAY_LEFT_FINGERS_TOOLTIP"), () => window_.SelectPickerFingers (false)));
            column.Add (fingers);

            return column;
        }

        static VisualElement MakeButtonRow ()
        {
            VisualElement row = new VisualElement ();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 1;
            return row;
        }

        static Button MakeSelectButton (string text, string tooltip, System.Action clicked)
        {
            Button button = new Button (clicked) { text = text, tooltip = tooltip };
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.height = kRowHeight;
            button.style.marginLeft = 0;
            button.style.marginRight = 1;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            return button;
        }

        static VisualElement MakeRow ()
        {
            Label label = new Label ();
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            return label;
        }

        void BindRow (VisualElement element, int index)
        {
            if (entries_ == null || index < 0 || index >= entries_.Count) return;

            PreviewWindow.PickerEntry entry = entries_[index];
            Label label = (Label)element;
            label.text = entry.label;
            // 骨のつながりの深さで字下げする
            label.style.paddingLeft = 4 + entry.depth * 8;
        }

        void OnSelectionChanged (IEnumerable<object> selected)
        {
            if (window_ == null) return;

            List<int> indices = list_.selectedIndices.ToList ();
            // 1 行だけなら今までどおり（そこへスクロールし、Spinner などの対象になる）
            if (indices.Count <= 1) {
                window_.SelectPickerIndex (list_.selectedIndex);
                return;
            }
            window_.SelectPickerIndices (indices);
        }

        void OnKeyDown (KeyDownEvent evt)
        {
            if (window_ == null) return;

            switch (evt.keyCode) {
                case KeyCode.Home: window_.SelectPickerEdge (true); break;
                case KeyCode.End: window_.SelectPickerEdge (false); break;
                default: return;
            }
            evt.StopPropagation ();
        }

        /// <summary>
        /// 窓の側で選択やキャラが替わったとき。一覧そのものが替わったときだけ作り直す
        /// </summary>
        void Refresh ()
        {
            if (window_ == null || list_ == null) return;

            if (!ReferenceEquals (entries_, window_.pickerEntries)) {
                entries_ = window_.pickerEntries;
                list_.itemsSource = (System.Collections.IList)entries_;
                list_.Rebuild ();
            }

            // 窓が選んでいる物を一覧へ写す（複数選んでいればその全部）
            List<int> indices = new List<int> ();
            for (int i = 0; i < entries_.Count; i++) {
                PoseTarget target = entries_[i].target;
                if (target != null && window_.IsSelected (target.gameObject)) indices.Add (i);
            }
            if (indices.Count == 0) {
                list_.SetSelectionWithoutNotify (Enumerable.Empty<int> ());
                return;
            }
            if (list_.selectedIndices.OrderBy (i => i).SequenceEqual (indices)) return;

            list_.SetSelectionWithoutNotify (indices);
            list_.ScrollToItem (indices[0]);
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Picker.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-picker", "Picker", defaultDisplay = true,
        defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top)]
    sealed class PickerOverlayScene : PickerOverlay
    {
    }

}
