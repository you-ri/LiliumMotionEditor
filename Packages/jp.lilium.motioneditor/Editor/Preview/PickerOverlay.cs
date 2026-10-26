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
    /// Picker。骨と全身 IK の点が骨のつながりの順に並ぶ。クリックで選び、↑↓ / Home / End で移せる。
    /// 表示域でメッシュをクリックしたときの選択（PreviewWindow.SelectByMeshClick）とも一致する
    /// </summary>
    [Icon (Icons.kFolder + "Picker.png")]
    // 既定は浮かせて右上に置く（場所は OverlayWindows.kPickerDefault）
    [Overlay (typeof (PreviewWindow), "mkt-picker", "Picker", defaultDisplay = true,
        defaultDockZone = DockZone.Floating, defaultDockPosition = DockPosition.Top)]
    class PickerOverlay : MotionOverlay
    {
        const int kRowHeight = 18;

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
            window_ = ResolveWindow ();
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

            // パネルを動かす・ドックする・たたむと、Unity は中身を作り直す（ここがまた呼ばれる）。前の一覧を覚えたままだと、
            // Refresh が「一覧は替わっていない」と見て新しい ListView に中身を渡さず、一覧が空になった（10-26 不具合）
            entries_ = null;
            Refresh ();
            Resubscribe ();
            return root;
        }

        protected override void Subscribe (bool on)
        {
            window_.stateChanged -= Refresh;
            if (!on) return;
            window_.stateChanged += Refresh;
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
            return PanelUI.CompactButton (text, tooltip, clicked, kRowHeight);
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
        defaultDockZone = DockZone.Floating, defaultDockPosition = DockPosition.Top)]
    sealed class PickerOverlayScene : PickerOverlay
    {
    }

}
