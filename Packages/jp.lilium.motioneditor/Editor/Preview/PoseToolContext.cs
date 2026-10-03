using System;
using UnityEditor;
using UnityEditor.EditorTools;

#if UNITY_6000_6_OR_NEWER
namespace Lilium
{

    /// <summary>
    /// プレビュー窓の既定のツールコンテキスト。骨ハンドル（選択・回転・IK ゴールの移動）と視点の操作はここで扱う。
    /// 道具（EditorTool）は targetContext = typeof (PoseToolContext) で載せると、この窓の Tools オーバーレイに出る
    /// </summary>
    [UnityEngine.Icon (Icons.kFolder + "PoseTool.png")]
    [EditorToolContext ("Pose", null, targetToolOwner = typeof (PreviewWindow))]
    public class PoseToolContext : EditorToolContext
    {
        /// <summary>
        /// 組み込みの移動・回転・拡大ツールはまだ差し替えていないので出さない
        /// （そのままだと開いているシーンの選択物を動かすハンドルになる）
        /// </summary>
        protected override Type GetEditorToolType (Tool tool)
        {
            return null;
        }

        public override void OnToolGUI (EditorWindow window)
        {
            PreviewWindow previewWindow = window as PreviewWindow;
            if (previewWindow != null) previewWindow.DoViewportToolGUI ();
        }
    }

}
#endif
