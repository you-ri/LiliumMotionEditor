using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 物の持ち替え（<see cref="ParentSwitchDefinition"/>）の窓の側。キャラの設定にある持ち替えを表示モデルへ結び付け、
    /// 今のフレームで親を切り替えるキーを打つ（<see cref="ParentSwitch"/>）
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>このキャラの設定にある持ち替え（無ければ空）</summary>
        public IReadOnlyList<ParentSwitchDefinition> parentSwitches
        {
            get {
                CharacterSettings settings = characterSettings;
                if (settings == null || settings.parentSwitches == null) return System.Array.Empty<ParentSwitchDefinition> ();
                return settings.parentSwitches;
            }
        }

        /// <summary>表示モデルへ結び付けた持ち替え。結び付けられなければ null と理由</summary>
        public ParentSwitch.Bound BindParentSwitch (ParentSwitchDefinition definition, out string error)
        {
            return ParentSwitch.Bind (propertyRoot, definition, out error);
        }

        /// <summary>今のフレームの親（いちばん重いもの。無ければ -1）</summary>
        public int GetCurrentParent (ParentSwitch.Bound bound)
        {
            GameObject root = propertyRoot;
            return root != null && bound != null ? ParentSwitch.Current (root, bound) : -1;
        }

        /// <summary>打てない理由（打てるなら null）</summary>
        public string parentSwitchBlockReason
        {
            get {
                if (!CanEditClip () || propertyRoot == null) return Tr ("PREVIEW_WINDOW_PARENT_SWITCH_CANNOT_EDIT_CLIP");
                if (overrideBlocksBaseWrite != null) return overrideBlocksBaseWrite;
                if (ParentSwitch.blockReason != null && stage_ != null) return ParentSwitch.blockReason (stage_.model);
                return null;
            }
        }

        /// <summary>
        /// 今のフレームで、物の親を source に切り替えるキーを打つ。今のフレームの見た目（物のワールド姿勢）は保つ。
        /// 打つのは今のフレームと 1 つ前だけで、後ろのフレームは触らない（握りがそのまま新しい親の基準になり、物は新しい親に付いて動く）
        /// </summary>
        public void SwitchParent (ParentSwitch.Bound bound, int source)
        {
            if (bound == null || source < 0 || source >= bound.weights.Length) return;
            string blocked = parentSwitchBlockReason;
            if (blocked != null) {
                ShowNotification (new GUIContent (blocked), 1.5);
                return;
            }
            int frame = currentFrame;

            // 表示モデルを今のフレームの姿勢にしてから読む
            SamplePose ();
            Undo.IncrementCurrentGroup ();
            RecordClipUndo ("Switch Parent");
            string note = ParentSwitch.Switch (editingClip_, propertyRoot, bound, frame, source);
            AfterPropertyEdit ();
            if (note != null) ShowNotification (new GUIContent (note), 2.5);
        }
    }

}
