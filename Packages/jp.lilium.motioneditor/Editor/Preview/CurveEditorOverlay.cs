using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// カーブエディタ（S5）。選んでいる物のカーブを、左の一覧で選んで右のグラフに描く。
    /// 位置・数値のカーブはそのまま、回転は保存が四元数なので角度（X/Y/Z）に直して描き、キーの値を角度で直せる（接線は数値のカーブだけ）。
    /// 操作: キーのクリックで選択（Shift で追加）・ドラッグで時刻と値、空いた所のクリックでフレーム移動、ダブルクリックでキーを足す、
    /// 右クリックで接線の種類と削除、Delete で削除、F で全体を収める、ホイールで横の拡大縮小（Ctrl で縦）、中ボタン / Alt ドラッグで移動
    /// </summary>
    [Icon (Icons.kFolder + "Curves.png")]
    [Overlay (typeof (PreviewWindow), "mkt-curves", "Curves", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom)]
    class CurveEditorOverlay : MotionOverlay, ICreateHorizontalToolbar
    {
        const float kWidth = 640;
        const float kHeight = 220;
        const float kListWidth = 160;
        static readonly Color kPanelColor = new Color (0.17f, 0.17f, 0.18f);

        ScrollView list_;
        CurveView view_;

        public override VisualElement CreatePanelContent ()
        {
            VisualElement root = new VisualElement ();
            Build (root);
            return root;
        }

        public OverlayToolbar CreateHorizontalToolbarContent ()
        {
            OverlayToolbar toolbar = new OverlayToolbar ();
            VisualElement root = new VisualElement ();
            Build (root);
            toolbar.Add (root);
            return toolbar;
        }

        void Build (VisualElement root)
        {
            Unsubscribe ();
            root.style.width = kWidth;
            root.style.height = kHeight;
            root.style.flexDirection = FlexDirection.Row;
            root.style.backgroundColor = kPanelColor;
            window_ = ResolveWindow ();
            if (window_ == null) return;

            list_ = new ScrollView ();
            list_.style.width = kListWidth;
            list_.style.flexShrink = 0;
            root.Add (list_);

            view_ = new CurveView (window_);
            IMGUIContainer graph = new IMGUIContainer (view_.OnGUI) { focusable = true };
            graph.style.flexGrow = 1;
            view_.container = graph;
            root.Add (graph);

            // ドックしたら枠の幅に合わせる（浮いているときはこの幅）
            OverlayLayout.FollowDockWidth (this, root, () => kWidth);
            Rebuild ();
            Resubscribe ();
        }

        protected override void Subscribe (bool on)
        {
            window_.stateChanged -= Rebuild;
            window_.keysChanged -= OnKeysChanged;
            window_.poseSampled -= Repaint;
            window_.propertySelectionChanged -= Rebuild;
            if (!on) return;
            window_.stateChanged += Rebuild;
            window_.keysChanged += OnKeysChanged;
            window_.poseSampled += Repaint;
            window_.propertySelectionChanged += Rebuild;
        }

        void OnKeysChanged ()
        {
            if (view_ == null) return;
            // カーブの組が変わった（キーを初めて打った骨など）ときは一覧から作り直す
            if (!view_.SameCurves (window_.GetSelectedCurves ())) Rebuild ();
            else {
                view_.Invalidate ();
                Repaint ();
            }
        }

        void Repaint ()
        {
            if (view_ != null && view_.container != null) view_.container.MarkDirtyRepaint ();
        }

        /// <summary>
        /// 選択・クリップが替わったとき。一覧を作り直す
        /// </summary>
        void Rebuild ()
        {
            if (window_ == null || list_ == null) return;
            view_.SetOwners (window_.GetSelectedCurves ());
            list_.Clear ();
            string problem = window_.GetCurveEditProblem ();
            if (view_.channels.Count == 0) {
                list_.Add (Note (window_.clip == null ? Tr ("CURVE_EDITOR_OVERLAY_OPEN_CLIP") : Tr ("CURVE_EDITOR_OVERLAY_SELECT_BONE")));
            }
            string owner = null;
            foreach (CurveView.Channel channel in view_.channels) {
                if (channel.owner != owner) {
                    owner = channel.owner;
                    Label header = new Label (owner);
                    header.style.unityFontStyleAndWeight = FontStyle.Bold;
                    header.style.paddingLeft = 2;
                    header.style.marginTop = 2;
                    list_.Add (header);
                }
                CurveView.Channel captured = channel;
                Toggle toggle = new Toggle (channel.label) { value = channel.visible };
                toggle.labelElement.style.color = channel.color;
                toggle.labelElement.style.minWidth = 0;
                toggle.labelElement.style.width = 110;
                toggle.style.marginLeft = 8;
                toggle.RegisterValueChangedCallback (e => {
                    captured.visible = e.newValue;
                    view_.Invalidate ();
                    Repaint ();
                });
                list_.Add (toggle);
            }
            if (problem != null && window_.clip != null) list_.Add (Note (Tr ("CURVE_EDITOR_OVERLAY_CANNOT_WRITE", problem)));
            view_.FrameAll ();
            Repaint ();
        }

        static Label Note (string text)
        {
            Label label = new Label (text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 10;
            label.style.color = new Color (0.7f, 0.7f, 0.7f);
            label.style.paddingLeft = 4;
            return label;
        }
    }


    /// <summary>Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）にも同じパネルを出す</summary>
    [Icon (Icons.kFolder + "Curves.png")]
    [Overlay (typeof (SceneWindow), "mkt-scene-curves", "Curves", defaultDisplay = true,
        defaultDockZone = DockZone.BottomToolbar, defaultDockPosition = DockPosition.Bottom)]
    sealed class CurveEditorOverlayScene : CurveEditorOverlay
    {
    }

}
