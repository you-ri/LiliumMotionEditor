using UnityEditor;
using UnityEditor.Overlays;

namespace Lilium
{

    /// <summary>
    /// モーションエディタの窓に載るパネル（Overlay）の共通の基底。窓の知らせの付け外しを Subscribe の 1 か所に書く:
    /// 作るときに Resubscribe で付け直し、作り直す前に Unsubscribe で外し、消すときに基底が外す
    /// （付けるところと外すところを別々に書いていたので、片方だけ直すと知らせが残った）
    /// </summary>
    abstract class HostOverlay<TWindow> : Overlay where TWindow : EditorWindow
    {
        protected TWindow window_;

        /// <summary>扱う窓を引き当てる（既定は載っている窓）</summary>
        protected virtual TWindow ResolveWindow ()
        {
            return containerWindow as TWindow;
        }

        /// <summary>窓の知らせを付ける（on）・外す。外してから付けるように書く（何度呼んでも二重にならない）</summary>
        protected abstract void Subscribe (bool on);

        /// <summary>知らせを付け直す（中身を作った後）</summary>
        protected void Resubscribe ()
        {
            if (window_ == null) return;
            Subscribe (false);
            Subscribe (true);
        }

        /// <summary>知らせを外す（中身を作り直す前。前の中身への知らせを残さない）</summary>
        protected void Unsubscribe ()
        {
            if (window_ != null) Subscribe (false);
        }

        public override void OnWillBeDestroyed ()
        {
            Unsubscribe ();
            base.OnWillBeDestroyed ();
        }
    }

    /// <summary>
    /// プレビュー窓のパネル。Motion Scene（S15d）に載ったときは、姿勢を作っているプレビュー窓を扱う（OverlayWindows.Resolve）
    /// </summary>
    abstract class MotionOverlay : HostOverlay<PreviewWindow>
    {
        protected override PreviewWindow ResolveWindow ()
        {
            return OverlayWindows.Resolve (containerWindow);
        }
    }

}
