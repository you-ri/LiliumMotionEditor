using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 段の説明を出す箱。行に直接書くと一覧が読みにくくなるので、段の名前に乗せている間だけ出す。
    /// 窓の一番上の層に浮かせて置くので、**パネルの外へはみ出して**読める（別窓にはしない）。
    /// 行に残すのは操作するもの（👁 / ✏ / ✋・クリップ・重み・パラメータ）と、逆に通れるか（戻せる / 戻せない）の表示だけ
    /// </summary>
    static class LayerHelpPopup
    {
        const float kWidth = 320;
        const float kMargin = 6;

        static readonly Color kBackColor = new Color (0.16f, 0.16f, 0.17f, 0.98f);
        static readonly Color kBorderColor = new Color (0, 0, 0, 0.6f);
        static readonly Color kNoteColor = new Color (0.78f, 0.78f, 0.78f);
        static readonly Color kExactColor = new Color (0.5f, 0.75f, 0.5f);
        static readonly Color kApproximateColor = new Color (0.9f, 0.75f, 0.35f);
        static readonly Color kNoneColor = new Color (0.9f, 0.45f, 0.45f);

        static VisualElement current_;
        static PoseLayer currentLayer_;

        /// <summary>
        /// 段の名前の下に出す（名前に乗ったとき）
        /// </summary>
        public static void Open (VisualElement anchor, EditorWindow host, PoseStack stack, PoseLayer layer)
        {
            if (anchor == null || host == null || layer == null) return;
            if (current_ != null && current_.parent != null && currentLayer_ == layer) return;
            Close ();

            // 窓の中身（rootVisualElement）に置くと、その上の層に描かれるパネル（Overlay）の後ろに隠れる。
            // パネルごと含む一番上の木（panel.visualTree）の最後に置いて、全部の手前に出す
            VisualElement root = anchor.panel != null ? anchor.panel.visualTree : host.rootVisualElement;
            VisualElement box = Build (stack, layer);
            box.style.position = Position.Absolute;
            box.style.width = kWidth;
            root.Add (box);
            box.BringToFront ();
            currentLayer_ = layer;
            current_ = box;

            // 置き場所は名前の下。窓の右端・下端からはみ出すときは中へ寄せる
            Rect world = anchor.worldBound;
            box.RegisterCallback<GeometryChangedEvent> (e => Place (box, root, world));
            Place (box, root, world);

            // 箱の上では何も拾わない（下の操作を邪魔しない）
            box.pickingMode = PickingMode.Ignore;
        }

        /// <summary>その段のぶんだけ消す（別の段に乗り換えた後に、前の段の離脱が来ても消さない）</summary>
        public static void Close (PoseLayer layer)
        {
            if (layer != null && currentLayer_ != layer) return;
            Close ();
        }

        public static void Close ()
        {
            if (current_ != null && current_.parent != null) current_.RemoveFromHierarchy ();
            current_ = null;
            currentLayer_ = null;
        }

        static void Place (VisualElement box, VisualElement root, Rect anchor)
        {
            float width = box.resolvedStyle.width > 0 ? box.resolvedStyle.width : kWidth;
            float height = box.resolvedStyle.height;
            float left = Mathf.Min (anchor.x, root.layout.width - width - kMargin);
            float top = anchor.yMax + 2;
            if (height > 0 && top + height > root.layout.height - kMargin) {
                // 下に入らなければ名前の上へ
                top = Mathf.Max (kMargin, anchor.y - height - 2);
            }
            box.style.left = Mathf.Max (kMargin, left);
            box.style.top = top;
        }

        static VisualElement Build (PoseStack stack, PoseLayer layer)
        {
            VisualElement box = new VisualElement ();
            box.style.backgroundColor = kBackColor;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;
            box.style.borderLeftWidth = 1;
            box.style.borderRightWidth = 1;
            box.style.borderTopWidth = 1;
            box.style.borderBottomWidth = 1;
            box.style.borderLeftColor = kBorderColor;
            box.style.borderRightColor = kBorderColor;
            box.style.borderTopColor = kBorderColor;
            box.style.borderBottomColor = kBorderColor;

            Label title = new Label (layer.label);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 4;
            box.Add (title);

            foreach (KeyValuePair<string, Color> line in Lines (stack, layer)) {
                Label label = new Label (line.Key);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = 11;
                label.style.color = line.Value;
                label.style.marginBottom = 4;
                box.Add (label);
            }
            return box;
        }

        static List<KeyValuePair<string, Color>> Lines (PoseStack stack, PoseLayer layer)
        {
            List<KeyValuePair<string, Color>> lines = new List<KeyValuePair<string, Color>> ();
            System.Action<string, Color> add = (text, color) => {
                if (!string.IsNullOrEmpty (text)) lines.Add (new KeyValuePair<string, Color> (text, color));
            };

            if (!layer.enabled) add (Tr ("LAYER_HELP_POPUP_DISABLED"), kApproximateColor);
            else if (!layer.canEvaluate) add (Tr ("LAYER_HELP_POPUP_CANNOT_EVALUATE"), kNoteColor);

            if (!string.IsNullOrEmpty (layer.failure)) add (Tr ("LAYER_HELP_POPUP_STOPPED", layer.failure), kNoneColor);
            if (layer.note != layer.failure) add (layer.note, kNoteColor);
            // 保存データの段でない段の値は、保存データから配られた値（GrabTarget.Values）
            if (!layer.isData && layer.grab == GrabTarget.Values) add (Tr ("LAYER_HELP_POPUP_VALUES_FROM_EDITING_RIG"), kNoteColor);
            add (layer.unavailableReason, kNoteColor);
            if (layer.parameters.Any (p => !p.display)) add (Tr ("LAYER_HELP_POPUP_DISPLAY_ONLY"), kNoteColor);

            if (layer.isPoseStage) {
                Color color = layer.inverse == InverseKind.None ? kNoneColor
                    : layer.inverse == InverseKind.Approximate ? kApproximateColor : kExactColor;
                string text = string.IsNullOrEmpty (layer.inverseReason) ? Tr ("LAYER_HELP_POPUP_INVERSE", PoseStack.Describe (layer.inverse))
                    : Tr ("LAYER_HELP_POPUP_INVERSE_WITH_REASON", PoseStack.Describe (layer.inverse), layer.inverseReason);
                add (text, color);
            }

            if (stack != null) {
                string reason;
                if (layer.canWrite && !stack.CanWrite (layer, out reason)) add (Tr ("LAYER_HELP_POPUP_CANNOT_WRITE", reason), kNoneColor);
                if (layer.canManipulate && !stack.CanManipulate (layer, out reason)) add (Tr ("LAYER_HELP_POPUP_CANNOT_MANIPULATE", reason), kNoneColor);
            }

            foreach (string detail in layer.details) add (detail, kNoteColor);
            if (lines.Count == 0) add (Tr ("LAYER_HELP_POPUP_NO_DESCRIPTION"), kNoteColor);
            return lines;
        }
    }

}
