using System;
using UnityEditor;
using UnityEditor.EditorTools;

#if UNITY_6000_6_OR_NEWER
namespace Lilium
{

    /// <summary>
    /// リグエディタの既定のツールコンテキスト。コントロールを選ぶ操作と視点の操作はここで扱う（PoseToolContext と同じ形）
    /// </summary>
    [UnityEngine.Icon (Icons.kFolder + "RigValues.png")]
    [EditorToolContext ("Rig", null, targetToolOwner = typeof (RigEditorWindow))]
    public class RigEditorToolContext : EditorToolContext
    {
        /// <summary>
        /// 組み込みの移動・回転・拡大ツールは出さない（そのままだと開いているシーンの選択物を動かすハンドルになる）
        /// </summary>
        protected override Type GetEditorToolType (Tool tool)
        {
            return null;
        }

        public override void OnToolGUI (EditorWindow window)
        {
            RigEditorWindow rigEditor = window as RigEditorWindow;
            if (rigEditor != null) rigEditor.DoViewportToolGUI ();
        }
    }

}
#endif
