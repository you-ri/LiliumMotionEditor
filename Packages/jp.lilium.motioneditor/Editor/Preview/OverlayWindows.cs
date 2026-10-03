using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// パネル（Overlay）が扱うモーションエディタの窓を決める。パネルはプレビュー窓にも Motion Scene（S15d）にも載り、
    /// Motion Scene に載ったときは、姿勢を作っているプレビュー窓を扱う（2 窓構成）
    /// </summary>
    static class OverlayWindows
    {
        public static PreviewWindow Resolve (EditorWindow container)
        {
            PreviewWindow preview = container as PreviewWindow;
            if (preview != null) return preview;
            SceneWindow scene = container as SceneWindow;
            if (scene != null && scene.source != null) return scene.source;
            return Resources.FindObjectsOfTypeAll<PreviewWindow> ().FirstOrDefault ();
        }

        /// <summary>画面内に残す幅（見出しの左端からこの幅は見えるようにする）</summary>
        const float kVisibleWidth = 80f;
        /// <summary>画面内に残す高さ（見出しの帯）</summary>
        const float kVisibleHeight = 24f;

        /// <summary>
        /// 浮いているパネルを、見出しがつかめる位置まで窓の中へ押し戻す。Unity はパネルを窓の外まで動かせてしまい、
        /// 見出しが外に出るとマウスで戻せなくなる（窓を縮めたときも同じ）。左端と上端は窓の中、右と下は見出しの一部が残るまで
        /// </summary>
        public static void KeepInside (EditorWindow window, IEnumerable<string> ids)
        {
            if (window == null) return;
            Vector2 size = window.position.size;
            if (size.x < 1 || size.y < 1) return;
            float maxX = Mathf.Max (0f, size.x - kVisibleWidth);
            float maxY = Mathf.Max (0f, size.y - kVisibleHeight);
            foreach (string id in ids) {
                Overlay overlay;
                if (!window.TryGetOverlay (id, out overlay) || overlay == null) continue;
                if (!overlay.displayed || !overlay.floating) continue;
                Vector2 position = overlay.floatingPosition;
                Vector2 clamped = new Vector2 (Mathf.Clamp (position.x, 0f, maxX), Mathf.Clamp (position.y, 0f, maxY));
                if (clamped != position) overlay.floatingPosition = clamped;
            }
        }
    }

}
