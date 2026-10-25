using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine.UIElements;
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

        /// <summary>
        /// Ctrl+C / Ctrl+V / Ctrl+A（Unity の Edit メニューの Copy / Paste / Select All のコマンド）で、姿勢のコピー・貼り付けと全選択（10-21 ユーザー指示）。
        /// [Shortcut] で同じキーを取ると、文字欄やタイムラインのキー欄（選んだキーのコピー・貼り付け。TimebarElement）より先に取ってしまうので、
        /// コマンドで受ける。フォーカスのある要素が先に受け、受けなかったものがここまで上がってくる。キーは Edit > Shortcuts の Main Menu/Edit で変わる。
        /// オーバーレイ（パネル）は rootVisualElement の外にあるので、パネルのボタンにフォーカスがあるときも受けられるよう、窓ごとの根で受ける。
        /// Motion Scene の窓も同じ口を使う（resolve が、今コマンドを受けるプレビュー窓を返す。受けないときは null）
        /// </summary>
        internal static void RegisterEditCommands (EditorWindow host, System.Func<PreviewWindow> resolve)
        {
            VisualElement root = WindowRoot (host);
            root.RegisterCallback<ValidateCommandEvent, System.Func<PreviewWindow>> (OnValidateEditCommand, resolve);
            root.RegisterCallback<ExecuteCommandEvent, System.Func<PreviewWindow>> (OnExecuteEditCommand, resolve);
        }

        internal static void UnregisterEditCommands (EditorWindow host)
        {
            VisualElement root = WindowRoot (host);
            root.UnregisterCallback<ValidateCommandEvent, System.Func<PreviewWindow>> (OnValidateEditCommand);
            root.UnregisterCallback<ExecuteCommandEvent, System.Func<PreviewWindow>> (OnExecuteEditCommand);
        }

        /// <summary>窓ごとの根（rootVisualElement とオーバーレイを含む。パネルの根のすぐ下）</summary>
        static VisualElement WindowRoot (EditorWindow host)
        {
            VisualElement root = host.rootVisualElement;
            while (root.parent != null && root.parent.parent != null) root = root.parent;
            return root;
        }

        static void OnValidateEditCommand (ValidateCommandEvent e, System.Func<PreviewWindow> resolve)
        {
            PreviewWindow window = resolve ();
            if (window != null && window.CanRunEditCommand (e.commandName)) e.StopPropagation ();
        }

        static void OnExecuteEditCommand (ExecuteCommandEvent e, System.Func<PreviewWindow> resolve)
        {
            PreviewWindow window = resolve ();
            if (window != null && window.RunEditCommand (e.commandName)) e.StopPropagation ();
        }

        /// <summary>窓の IMGUI に来たコマンド（UI Toolkit の要素のどれにもフォーカスが無いとき。Motion Scene の窓からも呼ぶ）</summary>
        internal void HandleEditCommandGUI ()
        {
            Event ev = Event.current;
            if (ev.type == EventType.ValidateCommand && CanRunEditCommand (ev.commandName)) ev.Use ();
            else if (ev.type == EventType.ExecuteCommand && RunEditCommand (ev.commandName)) ev.Use ();
        }

        bool CanRunEditCommand (string command)
        {
            switch (command) {
                case "Copy": return targetClip != null;
                case "Paste": return CanEditClip () && !PoseClipboard.isEmpty;
                case "SelectAll": return stage_ != null;
            }
            return false;
        }

        bool RunEditCommand (string command)
        {
            if (!CanRunEditCommand (command)) return false;
            switch (command) {
                case "Copy": CopyPose (); break;
                case "Paste": PastePose (); break;
                case "SelectAll": SelectAll (); break;
            }
            return true;
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

        [Shortcut ("Lilium Motion Editor/Previous Key", typeof (PreviewWindow), KeyCode.A)]
        static void PreviousKeyShortcut (ShortcutArguments args)
        {
            Run (args, window => window.GoToKey (-1));
        }

        [Shortcut ("Lilium Motion Editor/Next Key", typeof (PreviewWindow), KeyCode.D)]
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
