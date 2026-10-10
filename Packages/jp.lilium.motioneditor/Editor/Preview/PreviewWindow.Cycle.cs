using UnityEngine;
using UnityEditor;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// サイクルキー（S32。akeytsu のサイクルマーカー）の窓の側。タイムラインで最後のキーに印を付け外しし、キーが変わるたびに
    /// 最後のキーを先頭のキーに合わせ直す（ClipCycle）。
    /// - 印はクリップの .meta に持つ。付け外しも Undo で戻せるよう、今のクリップの印を窓にも持って一緒に記録する（戻したら .meta へ書き戻す）
    /// - サイクルキーのフレームで姿勢を直すと、先頭のキーへ打つ（サイクルキーは先頭を写した物なので、直接打っても先頭へ戻される）
    /// - サイクルキーより後ろのフレームでは動かさない（打つとそこが最後のキーになり、先頭の姿勢で上書きされる）
    /// </summary>
    public partial class PreviewWindow
    {
        [SerializeField] AnimationClip cycleClip_;
        [SerializeField] bool cycle_;
        // ここから下は窓の中だけの状態。スクリプトの読み直しでは引き継がない（Unity は private のフィールドも引き継ぐので、読み込み済みのまま .meta を読み直さなくなる）
        [System.NonSerialized] bool cycleLoaded_;
        /// <summary>前に合わせたときのキーのフレーム（サイクルキーを消した・前へ動かしたのを見分ける。ClipCycle.CanFollow）</summary>
        [System.NonSerialized] int[] cycleFrames_;
        /// <summary>サイクルキーを合わせている間（合わせて書いたカーブの知らせで、また合わせ直さない）</summary>
        [System.NonSerialized] bool cycleSyncing_;
        /// <summary>合わせ直しが要る（自分で書いた以外の書き換えがあった）</summary>
        [System.NonSerialized] bool cycleDirty_;
        /// <summary>キーが変わった知らせを出すと決めたときの Undo のまとまり。後から合わせ直した分も、元の操作と 1 回の Undo で戻す</summary>
        [System.NonSerialized] int keysChangedGroup_ = -1;

        /// <summary>今のクリップ（書き込み先）に、サイクルキーの印が付いているか</summary>
        public bool cycle
        {
            get {
                LoadCycle ();
                return cycle_;
            }
        }

        /// <summary>サイクルキーのフレーム。印が無い・キーが 2 フレーム未満なら -1</summary>
        public int cycleFrame
        {
            get {
                int first, last;
                return cycle && TryGetCycleRange (out first, out last) ? last : -1;
            }
        }

        void LoadCycle ()
        {
            AnimationClip clip = targetClip;
            if (cycleLoaded_ && cycleClip_ == clip) return;
            cycleLoaded_ = true;
            cycleClip_ = clip;
            cycle_ = ClipCycle.Load (clip);
            cycleFrames_ = ClipCycle.GetFrames (clip);
        }

        /// <summary>先頭のキーと最後のキーのフレーム（窓が覚えているキーの一覧から）</summary>
        bool TryGetCycleRange (out int first, out int last)
        {
            return ClipCycle.TryGetRange (GetKeyFrames (), out first, out last);
        }

        /// <summary>frame のキーでサイクルキーを付け外しできるか（最後のキーだけ）</summary>
        public bool CanSetCycle (int frame)
        {
            int first, last;
            return CanEditClip () && TryGetCycleRange (out first, out last) && frame == last;
        }

        /// <summary>
        /// サイクルキーの印を付け外しする。付けたら、すぐ最後のキーを先頭のキーに合わせる（印と一緒に 1 回の Undo で戻る）
        /// </summary>
        public void SetCycle (bool on)
        {
            LoadCycle ();
            if (cycle_ == on || !CanEditClip ()) return;
            int first, last;
            if (on && !TryGetCycleRange (out first, out last)) return;
            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            Undo.RecordObject (this, "Cycle Key");
            cycle_ = on;
            ClipCycle.Save (cycleClip_, on);
            // 印の無い間のキーの変化は追っていないので、今のキーから見始める
            cycleFrames_ = ClipCycle.GetFrames (cycleClip_);
            if (on) SyncCycle ();
            Undo.SetCurrentGroupName (on ? "Set Cycle Key" : "Remove Cycle Key");
            Undo.CollapseUndoOperations (group);
            RaiseStateChanged ();
            RepaintView ();
        }

        /// <summary>
        /// 印が付いていれば、最後のキーを先頭のキーに合わせる（キーが変わるたびに呼ぶ。変わらなければ書かない）。
        /// サイクルキーを消した・前へ動かして前からあったキーが最後になったときは、そのキーを上書きせずに印を外す
        /// </summary>
        void SyncCycle ()
        {
            if (!cycle || !CanEditClip ()) return;
            AnimationClip clip = targetClip;
            int[] frames = ClipCycle.GetFrames (clip);
            int first, last;
            if (ClipCycle.TryGetRange (frames, out first, out last) && !ClipCycle.CanFollow (cycleFrames_, last)) {
                Undo.RecordObject (this, "Remove Cycle Key");
                cycle_ = false;
                ClipCycle.Save (cycleClip_, false);
                cycleFrames_ = frames;
                ShowNotification (new GUIContent (Tr ("PREVIEW_WINDOW_CYCLE_KEY_REMOVED", last)), 2);
                RaiseStateChanged ();
                return;
            }
            RecordClipUndo ("Cycle Key");
            bool changed;
            cycleSyncing_ = true;
            try {
                changed = ClipCycle.Sync (clip);
            }
            finally {
                cycleSyncing_ = false;
            }
            cycleFrames_ = changed ? ClipCycle.GetFrames (clip) : frames;
            if (!changed) return;
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }

        /// <summary>
        /// キーを打つフレーム。サイクルキーのフレームなら先頭のキー（サイクルキーは先頭を写した物で、直接打っても先頭へ戻される）。
        /// Override へ書くときは土台の値との差分なので、写し先を変えない
        /// </summary>
        int KeyFrameToWrite ()
        {
            return PoseCommit.KeyFrameToWrite (currentFrame, cycle, GetKeyFrames (), overrideWriteLayer != null);
        }

        /// <summary>
        /// サイクルキーより後ろのフレームか。ここでキーを打つと、そこが最後のキーになって先頭の姿勢で上書きされるので、動かさない
        /// </summary>
        bool IsPastCycleKey ()
        {
            return cycle && CanEditClip () && PoseCommit.IsPastCycleKey (currentFrame, cycle, GetKeyFrames ());
        }

        /// <summary>カーブが書き換わったとき（OnCurveWasModified から）。自分で合わせた分でなければ、次の知らせで合わせ直す</summary>
        void MarkCycleDirty ()
        {
            if (cycleSyncing_) return;
            cycleDirty_ = true;
            if (keysChangedGroup_ < 0) keysChangedGroup_ = Undo.GetCurrentGroup ();
        }

        /// <summary>キーが変わった知らせを出す前（RaiseKeysChanged から）。合わせ直した分は、元の操作の Undo にまとめる</summary>
        void SyncCycleAfterKeysChanged ()
        {
            int group = keysChangedGroup_;
            bool dirty = cycleDirty_;
            keysChangedGroup_ = -1;
            cycleDirty_ = false;
            if (!dirty) return;
            SyncCycle ();
            if (group >= 0 && group <= Undo.GetCurrentGroup ()) Undo.CollapseUndoOperations (group);
        }

        /// <summary>Undo / Redo の後。窓に持っている印を .meta へ書き戻し、キーの一覧を覚え直す（戻した姿勢は合わせ済みなので合わせ直さない）</summary>
        void RestoreCycleAfterUndo ()
        {
            cycleDirty_ = false;
            keysChangedGroup_ = -1;
            if (!cycleLoaded_ || cycleClip_ == null) return;
            if (ClipCycle.Load (cycleClip_) != cycle_) ClipCycle.Save (cycleClip_, cycle_);
            cycleFrames_ = ClipCycle.GetFrames (cycleClip_);
        }
    }

}
