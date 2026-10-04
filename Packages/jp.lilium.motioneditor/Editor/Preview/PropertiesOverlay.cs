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
    /// 任意のプロパティ（S6）。表示モデルの部品の数値（表示の ON/OFF・ブレンドシェイプ・フィールドなど）を検索して選び、
    /// 値を入れると今のフレームにキーを打つ。● はクリップにカーブがある物。選んだ物はカーブエディタにも出る
    /// </summary>
    [Icon (Icons.kFolder + "Properties.png")]
    [Overlay (typeof (PreviewWindow), "mkt-properties", "Properties", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    class PropertiesOverlay : Overlay
    {
        const int kRowHeight = 18;

        PreviewWindow window_;
        TextField search_;
        Toggle keyedOnly_;
        ListView list_;
        Label selectedLabel_;
        FloatField value_;
        Button key_;
        Button remove_;
        readonly List<PropertyPlayer.Candidate> rows_ = new List<PropertyPlayer.Candidate> ();
        HashSet<EditorCurveBinding> keyed_ = new HashSet<EditorCurveBinding> ();

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            root.style.width = 240;
            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => 240);
            window_ = OverlayWindows.Resolve (containerWindow);
            if (window_ == null) return root;

            search_ = new TextField { tooltip = Tr ("PROPERTIES_OVERLAY_SEARCH_TOOLTIP") };
            search_.RegisterValueChangedCallback (e => Refresh ());
            root.Add (search_);
            keyedOnly_ = new Toggle (Tr ("PROPERTIES_OVERLAY_KEYED_ONLY"));
            keyedOnly_.RegisterValueChangedCallback (e => Refresh ());
            root.Add (keyedOnly_);

            list_ = new ListView {
                fixedItemHeight = kRowHeight,
                selectionType = SelectionType.Single,
                makeItem = () => {
                    Label label = new Label ();
                    label.style.unityTextAlign = TextAnchor.MiddleLeft;
                    label.style.overflow = Overflow.Hidden;
                    label.style.textOverflow = TextOverflow.Ellipsis;
                    return label;
                },
                bindItem = (element, index) => {
                    if (index < 0 || index >= rows_.Count) return;
                    PropertyPlayer.Candidate row = rows_[index];
                    Label label = (Label)element;
                    label.text = (keyed_.Contains (row.binding) ? "● " : "   ") + row.label;
                    label.tooltip = row.label;
                },
                itemsSource = rows_,
            };
            list_.style.height = 180;
            list_.selectionChanged += OnSelectionChanged;
            root.Add (list_);

            selectedLabel_ = new Label ();
            selectedLabel_.style.fontSize = 10;
            selectedLabel_.style.whiteSpace = WhiteSpace.Normal;
            root.Add (selectedLabel_);

            VisualElement row2 = new VisualElement ();
            row2.style.flexDirection = FlexDirection.Row;
            value_ = new FloatField { isDelayed = true, tooltip = Tr ("PROPERTIES_OVERLAY_VALUE_TOOLTIP") };
            value_.style.flexGrow = 1;
            value_.RegisterValueChangedCallback (e => {
                if (window_.selectedProperty.HasValue) window_.SetPropertyKey (window_.selectedProperty.Value, e.newValue);
            });
            row2.Add (value_);
            key_ = new Button (KeyCurrent) { text = "Key", tooltip = Tr ("PROPERTIES_OVERLAY_KEY_TOOLTIP") };
            row2.Add (key_);
            remove_ = new Button (() => {
                if (window_.selectedProperty.HasValue) window_.RemovePropertyCurve (window_.selectedProperty.Value);
            }) { text = "×", tooltip = Tr ("PROPERTIES_OVERLAY_REMOVE_TOOLTIP") };
            row2.Add (remove_);
            root.Add (row2);

            Refresh ();
            window_.stateChanged += Refresh;
            window_.keysChanged += Refresh;
            window_.poseSampled += RefreshValue;
            return root;
        }

        public override void OnWillBeDestroyed ()
        {
            if (window_ != null) {
                window_.stateChanged -= Refresh;
                window_.keysChanged -= Refresh;
                window_.poseSampled -= RefreshValue;
            }
            base.OnWillBeDestroyed ();
        }

        void Refresh ()
        {
            if (window_ == null || list_ == null) return;
            keyed_ = window_.keyedProperties;
            string[] words = (search_.value ?? "").Split (new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            rows_.Clear ();
            IEnumerable<PropertyPlayer.Candidate> candidates = window_.propertyCandidates
                .Where (c => !keyedOnly_.value || keyed_.Contains (c.binding))
                .Where (c => words.All (w => c.label.IndexOf (w, System.StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy (c => keyed_.Contains (c.binding) ? 0 : 1);
            rows_.AddRange (candidates);
            list_.RefreshItems ();
            int index = window_.selectedProperty.HasValue ? rows_.FindIndex (r => r.binding == window_.selectedProperty.Value) : -1;
            if (index >= 0) list_.SetSelectionWithoutNotify (new[] { index });
            else list_.ClearSelection ();
            RefreshValue ();
        }

        void RefreshValue ()
        {
            if (window_ == null || value_ == null) return;
            bool has = window_.selectedProperty.HasValue;
            string problem = window_.GetCurveEditProblem ();
            selectedLabel_.text = !has ? Tr ("PROPERTIES_OVERLAY_SELECT_PROPERTY") : problem != null ? Tr ("PROPERTIES_OVERLAY_CANNOT_WRITE", problem) : "";
            float value = 0;
            bool readable = has && window_.TryGetPropertyValue (window_.selectedProperty.Value, out value);
            if (readable) {
                VisualElement focused = value_.focusController != null ? value_.focusController.focusedElement as VisualElement : null;
                if (focused == null || (focused != value_ && !value_.Contains (focused))) value_.SetValueWithoutNotify (value);
            }
            value_.SetEnabled (readable && problem == null);
            key_.SetEnabled (readable && problem == null);
            remove_.SetEnabled (has && keyed_.Contains (window_.selectedProperty.Value) && problem == null);
        }

        void OnSelectionChanged (IEnumerable<object> selected)
        {
            int index = list_.selectedIndex;
            window_.selectedProperty = index >= 0 && index < rows_.Count ? rows_[index].binding : (EditorCurveBinding?)null;
            RefreshValue ();
        }

        void KeyCurrent ()
        {
            if (!window_.selectedProperty.HasValue) return;
            float value;
            if (window_.TryGetPropertyValue (window_.selectedProperty.Value, out value)) window_.SetPropertyKey (window_.selectedProperty.Value, value, false);
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Properties.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-properties", "Properties", defaultDisplay = false,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom)]
    sealed class PropertiesOverlayScene : PropertiesOverlay
    {
    }

}
