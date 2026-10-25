using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Motion Scene（開いているシーンのキャラをその場で編集する窓。S15d）のショートカット。中身はプレビュー窓の同じ操作を呼ぶ（2 窓構成）。
    /// Scene ビューのキーとぶつかるもの（F: 選んだ物に寄る・F1〜F3: 視点・V: 頂点スナップ）は割り当てない（視点は Scene ビューのものを使う）。
    /// 姿勢のコピー・貼り付けと全選択（Ctrl+C / Ctrl+V / Ctrl+A）は、プレビュー窓と同じく Edit メニューのコマンドで受ける（SceneWindow）
    /// </summary>
    public partial class PreviewWindow
    {
        [Shortcut ("Lilium Motion Editor (Scene)/Play", typeof (SceneWindow), KeyCode.Space)]
        static void ScenePlayShortcut (ShortcutArguments args)
        {
            Run (args, window => window.TogglePlay ());
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Key All", typeof (SceneWindow), KeyCode.K)]
        static void SceneKeyAllShortcut (ShortcutArguments args)
        {
            Run (args, window => window.KeyAll ());
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Previous Frame", typeof (SceneWindow), KeyCode.LeftArrow)]
        static void ScenePreviousFrameShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame - 1));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Next Frame", typeof (SceneWindow), KeyCode.RightArrow)]
        static void SceneNextFrameShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame + 1));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Back 10 Frames", typeof (SceneWindow), KeyCode.LeftArrow, ShortcutModifiers.Shift)]
        static void SceneBack10FramesShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame - 10));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Forward 10 Frames", typeof (SceneWindow), KeyCode.RightArrow, ShortcutModifiers.Shift)]
        static void SceneForward10FramesShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame + 10));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Previous Key", typeof (SceneWindow), KeyCode.A)]
        static void ScenePreviousKeyShortcut (ShortcutArguments args)
        {
            Run (args, window => window.GoToKey (-1));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Next Key", typeof (SceneWindow), KeyCode.D)]
        static void SceneNextKeyShortcut (ShortcutArguments args)
        {
            Run (args, window => window.GoToKey (1));
        }

        [Shortcut ("Lilium Motion Editor (Scene)/First Frame", typeof (SceneWindow), KeyCode.Home)]
        static void SceneFirstFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = OverlayWindows.Resolve (args.context as EditorWindow);
            if (window == null) return;
            if (window.isPickerFocused) window.SelectPickerEdge (true);
            else window.SetFrame (0);
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Last Key Frame", typeof (SceneWindow), KeyCode.End)]
        static void SceneLastKeyFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = OverlayWindows.Resolve (args.context as EditorWindow);
            if (window == null) return;
            if (window.isPickerFocused) window.SelectPickerEdge (false);
            else window.SetFrame (window.GetLastKeyFrame ());
        }

        // W / E / R は Unity の Tools/Move・Rotate・Scale（全体のショートカット）より窓のものが勝つ。
        // 右ドラッグで飛んでいる間の W / E（Fly Mode）は優先の文脈なので、そちらが勝つ
        [Shortcut ("Lilium Motion Editor (Scene)/Spinner Move", typeof (SceneWindow), KeyCode.W)]
        static void SceneSpinnerMoveShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Move);
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Spinner Rotate", typeof (SceneWindow), KeyCode.E)]
        static void SceneSpinnerRotateShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Rotate);
        }

        [Shortcut ("Lilium Motion Editor (Scene)/Spinner Scale", typeof (SceneWindow), KeyCode.R)]
        static void SceneSpinnerScaleShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Scale);
        }
    }

}
