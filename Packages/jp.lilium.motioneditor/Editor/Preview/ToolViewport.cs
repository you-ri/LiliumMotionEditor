using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// ツールのハンドルを回す表示域（PreviewWindow と RigEditorWindow で共通）。
    /// 6.6 以降は、Unity がツールのハンドル用に入れる IMGUIContainer（flexGrow 1）をそのまま使い、窓全体に広げる。
    /// ツールの仕組み（ISupportsEditorTools）が無い 6.0 では、代わりの IMGUIContainer を置き、ハンドル用のカメラを当てて同じ操作を回す
    /// </summary>
    static class ToolViewport
    {
        /// <summary>ISupportsEditorTools の窓に Unity が入れる、ツールのハンドルを描く IMGUIContainer の名前</summary>
        const string kContainerName = "EditorToolsIMGUIContainer";

        /// <param name="fallbackName">6.6 で Unity の器が見つからなかったときに置く物の名前</param>
        /// <param name="onGUI">6.0 で代わりの器が回す GUI（6.6 では Unity がツールコンテキストを呼ぶので使わない）</param>
        public static VisualElement Create (VisualElement root, string fallbackName, System.Action onGUI)
        {
#if UNITY_6000_6_OR_NEWER
            VisualElement viewport = root.Q<IMGUIContainer> (kContainerName);
            if (viewport != null) return viewport;
            Debug.LogWarning (Tr ("PREVIEW_WINDOW_TOOL_CONTAINER_MISSING"));
            viewport = new VisualElement { name = fallbackName };
#else
            VisualElement viewport = new IMGUIContainer (onGUI) { name = kContainerName };
#endif
            viewport.style.flexGrow = 1;
            root.Add (viewport);
            return viewport;
        }

        /// <summary>表示域の大きさ（左上が原点）。絵・ハンドル・カメラのアスペクトをここに合わせる</summary>
        public static Rect LocalRect (VisualElement viewport)
        {
            if (viewport == null) return Rect.zero;
            Rect content = viewport.contentRect;
            return float.IsNaN (content.width) || float.IsNaN (content.height) ? Rect.zero : new Rect (0, 0, content.width, content.height);
        }

        /// <summary>
        /// ワールドの位置を、描画のカメラで表示域の位置（左上が原点。rect は表示域の大きさ）にする。カメラの後ろなら false。
        /// ビューポート座標は左下が原点なので上下を返す
        /// </summary>
        public static bool WorldToView (Camera camera, Vector3 world, Rect rect, out Vector2 point)
        {
            Vector3 viewport = camera.WorldToViewportPoint (world);
            point = new Vector2 (viewport.x * rect.width, (1 - viewport.y) * rect.height);
            return viewport.z > 0;
        }

        /// <summary>点から線分までの距離（表示域の上で線を押せたかを見る）</summary>
        public static float DistanceToSegment (Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 delta = end - start;
            float t = delta.sqrMagnitude > 1e-6f ? Mathf.Clamp01 (Vector2.Dot (point - start, delta) / delta.sqrMagnitude) : 0;
            return Vector2.Distance (point, start + delta * t);
        }

#if !UNITY_6000_6_OR_NEWER
        /// <summary>
        /// ツールの仕組みが無い版で、6.6 以降で Unity がツールコンテキストを呼ぶ前にすることを代わりにする（ハンドルのカメラを表示域に合わせる）。
        /// Handles.SetCamera (rect, camera) は、出力先を持つカメラだと pixelRect を出力先の全面のままにする。
        /// それだと骨が出力先（256 単位で切り上げた大きさ）へ引き伸ばされて描かれ、キャラの絵からずれるので、自分で入れる。
        /// マウスの光線（GUIPointToWorldRay）や WorldToGUIPoint は、この IMGUIContainer の中では表示域の左上を原点にし、
        /// 上下を pixelRect の高さで返す（pixelRect.y を引かない）。pixelRect は原点に置いた表示域の大きさにして、
        /// 描く先（GL のビューポート）だけ窓の中の本当の位置にする。カメラが無ければ false（ツールの GUI を回さない）
        /// </summary>
        public static bool BeginHandles (Camera camera, VisualElement viewport)
        {
            if (camera == null) return false;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            Rect local = LocalRect (viewport);
            camera.pixelRect = new Rect (0, 0, local.width * ppp, local.height * ppp);
            Handles.SetCamera (camera);
            if (Event.current.type == EventType.Repaint) {
                Rect view = viewport.worldBound;
                GL.Viewport (new Rect (view.x * ppp, Screen.height - view.yMax * ppp, view.width * ppp, view.height * ppp));
            }
            return true;
        }
#endif
    }

}
