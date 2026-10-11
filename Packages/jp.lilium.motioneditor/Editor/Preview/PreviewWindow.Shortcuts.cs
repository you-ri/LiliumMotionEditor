using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// ショートカット（この窓にフォーカスがあるときだけ効く。Edit > Shortcuts で変えられる）。中身は窓の操作を呼ぶだけ。
    /// Motion Scene の窓のものは PreviewWindow.SceneShortcuts
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// ショートカットを、受けた窓で行う。Motion Scene の窓が受けたら、その窓が映しているプレビュー窓で行う
        /// </summary>
        static void Run (ShortcutArguments args, System.Action<PreviewWindow> action)
        {
            PreviewWindow window = OverlayWindows.Resolve (args.context as EditorWindow);
            if (window != null) action (window);
        }

        [Shortcut ("Lilium Motion Editor/Frame Selected", typeof (PreviewWindow), KeyCode.F)]
        static void FrameSelectedShortcut (ShortcutArguments args)
        {
            Run (args, window => window.FrameSelection ());
        }

        [Shortcut ("Lilium Motion Editor/Side View", typeof (PreviewWindow), KeyCode.F1)]
        static void SideViewShortcut (ShortcutArguments args)
        {
            Run (args, window => window.LookFrom (ViewPreset.Side));
        }

        [Shortcut ("Lilium Motion Editor/Top View", typeof (PreviewWindow), KeyCode.F2)]
        static void TopViewShortcut (ShortcutArguments args)
        {
            Run (args, window => window.LookFrom (ViewPreset.Top));
        }

        [Shortcut ("Lilium Motion Editor/Front View", typeof (PreviewWindow), KeyCode.F3)]
        static void FrontViewShortcut (ShortcutArguments args)
        {
            Run (args, window => window.LookFrom (ViewPreset.Front));
        }

        [Shortcut ("Lilium Motion Editor/Play", typeof (PreviewWindow), KeyCode.Space)]
        static void PlayShortcut (ShortcutArguments args)
        {
            Run (args, window => window.TogglePlay ());
        }

        [Shortcut ("Lilium Motion Editor/Copy Pose", typeof (PreviewWindow), KeyCode.C)]
        static void CopyPoseShortcut (ShortcutArguments args)
        {
            Run (args, window => window.CopyPose ());
        }

        [Shortcut ("Lilium Motion Editor/Paste Pose", typeof (PreviewWindow), KeyCode.V)]
        static void PastePoseShortcut (ShortcutArguments args)
        {
            Run (args, window => window.PastePose ());
        }

        // Unity の Animation 窓でキーを打つのと同じ K
        [Shortcut ("Lilium Motion Editor/Key All", typeof (PreviewWindow), KeyCode.K)]
        static void KeyAllShortcut (ShortcutArguments args)
        {
            Run (args, window => window.KeyAll ());
        }

        [Shortcut ("Lilium Motion Editor/Previous Frame", typeof (PreviewWindow), KeyCode.LeftArrow)]
        static void PreviousFrameShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame - 1));
        }

        [Shortcut ("Lilium Motion Editor/Next Frame", typeof (PreviewWindow), KeyCode.RightArrow)]
        static void NextFrameShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame + 1));
        }

        [Shortcut ("Lilium Motion Editor/Back 10 Frames", typeof (PreviewWindow), KeyCode.LeftArrow, ShortcutModifiers.Shift)]
        static void Back10FramesShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame - 10));
        }

        [Shortcut ("Lilium Motion Editor/Forward 10 Frames", typeof (PreviewWindow), KeyCode.RightArrow, ShortcutModifiers.Shift)]
        static void Forward10FramesShortcut (ShortcutArguments args)
        {
            Run (args, window => window.SetFrame (window.currentFrame + 10));
        }

        [Shortcut ("Lilium Motion Editor/Previous Key", typeof (PreviewWindow), KeyCode.Comma)]
        static void PreviousKeyShortcut (ShortcutArguments args)
        {
            Run (args, window => window.GoToKey (-1));
        }

        [Shortcut ("Lilium Motion Editor/Next Key", typeof (PreviewWindow), KeyCode.Period)]
        static void NextKeyShortcut (ShortcutArguments args)
        {
            Run (args, window => window.GoToKey (1));
        }

        /// <summary>
        /// Home / End は、骨の一覧（Picker）にフォーカスがあるときは一覧の先頭・末尾へ、無ければ最初のフレーム・最後のキーへ
        /// </summary>
        [Shortcut ("Lilium Motion Editor/First Frame", typeof (PreviewWindow), KeyCode.Home)]
        static void FirstFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window == null) return;

            if (window.isPickerFocused) window.SelectPickerEdge (true);
            else window.SetFrame (0);
        }

        [Shortcut ("Lilium Motion Editor/Last Key Frame", typeof (PreviewWindow), KeyCode.End)]
        static void LastKeyFrameShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window == null) return;

            if (window.isPickerFocused) window.SelectPickerEdge (false);
            else window.SetFrame (window.GetLastKeyFrame ());
        }

        // R は Unity と同じく Scale のモードに使うので L（Lock）
        [Shortcut ("Lilium Motion Editor/Toggle Point Lock", typeof (PreviewWindow), KeyCode.L)]
        static void TogglePointLockShortcut (ShortcutArguments args)
        {
            Run (args, window => window.ToggleSelectedPointsLocked ());
        }

        // Move / Rot / Scale は Unity の Tools/Move・Rotate・Scale と同じ W / E / R。
        // あちらは全体のショートカットなので、窓にフォーカスがある間は窓のものが勝つ（ぶつかりの確認は出ない）
        [Shortcut ("Lilium Motion Editor/Spinner Move", typeof (PreviewWindow), KeyCode.W)]
        static void SpinnerMoveShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Move);
        }

        [Shortcut ("Lilium Motion Editor/Spinner Rotate", typeof (PreviewWindow), KeyCode.E)]
        static void SpinnerRotateShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Rotate);
        }

        [Shortcut ("Lilium Motion Editor/Spinner Scale", typeof (PreviewWindow), KeyCode.R)]
        static void SpinnerScaleShortcut (ShortcutArguments args)
        {
            Run (args, window => window.spinnerMode = SpinnerMode.Scale);
        }
    }

}
