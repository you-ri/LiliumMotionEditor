using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// キーの操作（前後のキーへ移る・全部に打つ・移す・消す・コピーと貼り付け・姿勢のコピーと貼り付け・基準姿勢へ戻す）。
    /// トラックに出すキーの一覧は本体（GetKeyFrames・BuildTracks）
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// 前（direction = -1）か次（+1）のキーへ移る
        /// </summary>
        public void GoToKey (int direction)
        {
            int[] keys = GetKeyFrames ().Where (f => f >= 0).ToArray ();
            int target = direction < 0
                ? keys.Where (f => f < currentFrame).DefaultIfEmpty (currentFrame).Max ()
                : keys.Where (f => f > currentFrame).DefaultIfEmpty (currentFrame).Min ();
            SetFrame (target);
        }

        /// <summary>
        /// 今の姿勢を、つかめるもの全部ぶん今のフレームにキーとして打つ
        /// </summary>
        /// <param name="edit">操作の後に打つとき。キーの自動追加が切なら、キーのあるカーブだけ書き換え、
        /// キーの無い物が操作の巻き添えで動いた分（IK を解き直した先など）はクリップの姿勢へ戻す</param>
        void AddKeyAllCurves (bool edit = false)
        {
            if (!CanEditClip ()) return;

            using (CurveWriter writer = BeginCurves ("Key Pose", edit)) {
                foreach (PoseTarget target in targets_) {
                    target.WriteKeys (writer);
                }
                // ゲームの Rig の重みも同じフレームに打つ（チャンネルの組をそろえておく）
                if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
            }
            keyFrames_ = null;
            if (edit && !autoKey) SamplePose ();
        }

        /// <param name="filter">動かすカーブ（骨のトラックならその骨のカーブ）。null ならクリップ全体</param>
        public void MoveKeys (int frame, int movedFrame, bool copy, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (!CanEditClip ()) return;

            RecordClipUndo (copy ? "Copy Keys" : "Move Keys");
            ClipKeyUtility.MoveKeysAtFrame (targetClip, frame, movedFrame, copy, filter);
            keyFrames_ = null;
            SamplePose ();
        }

        /// <param name="filter">消すカーブ（骨のトラックならその骨のカーブ）。null ならクリップ全体</param>
        public void RemoveKeys (int frame, System.Predicate<EditorCurveBinding> filter = null)
        {
            if (!CanEditClip ()) return;

            RecordClipUndo ("Remove Keys");
            ClipKeyUtility.RemoveKeysAtFrame (targetClip, frame, filter);
            keyFrames_ = null;
            SamplePose ();
        }

        /// <summary>
        /// タイムラインで選んだキーをまとめて消す（1 回の Undo で戻る）。filter はトラックのもの（null ならクリップ全体）
        /// </summary>
        public void RemoveKeys (IList<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys)
        {
            if (!CanEditClip () || keys == null || keys.Count == 0) return;

            RecordClipUndo ("Remove Keys");
            foreach (KeyValuePair<int, System.Predicate<EditorCurveBinding>> key in keys) {
                ClipKeyUtility.RemoveKeysAtFrame (targetClip, key.Key, key.Value);
            }
            keyFrames_ = null;
            SamplePose ();
        }

        /// <summary>
        /// タイムラインで選んだキーを覚える（Ctrl+C）。filter はトラックのもの（null ならクリップ全体）
        /// </summary>
        /// <returns>覚えたキーの数（カーブ単位）</returns>
        public int CopyKeys (IList<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> keys)
        {
            return TimelineClipboard.Copy (targetClip, keys);
        }

        /// <summary>覚えたキーを貼れるか</summary>
        public bool canPasteKeys
        {
            get { return !TimelineClipboard.isEmpty && CanEditClip (); }
        }

        /// <summary>
        /// 覚えたキーを今のフレームを先頭にして貼る（Ctrl+V。1 回の Undo で戻る）。行き先にあるキーは上書きする。
        /// ポーズの貼り付けと同じく明示的に打つ操作なので、キーの自動追加が切でも貼る
        /// </summary>
        /// <returns>貼ったキーの数（カーブ単位）</returns>
        public int PasteKeys ()
        {
            if (!canPasteKeys) return 0;

            RecordClipUndo ("Paste Keys");
            int count = TimelineClipboard.Paste (targetClip, currentFrame);
            AfterClipEdit ();
            return count;
        }

        /// <summary>
        /// 選んでいる骨のキーを keepFrame の 1 つだけにする（値はそのフレームの姿勢）。keepFrame が負なら全部消す。
        /// 読込（毎フレームにキーが入る）の後で、指など一部だけを少ないキーで打ち直すため
        /// </summary>
        public void ClearSelectedKeys (int keepFrame)
        {
            if (!CanEditClip ()) return;
            HashSet<string> paths = new HashSet<string> ();
            foreach (PoseTarget target in SelectedTargets ()) target.CollectKeyedPaths (paths);
            if (paths.Count == 0) return;

            RecordClipUndo (keepFrame < 0 ? "Clear Keys" : "Keep One Key");
            ClipKeyUtility.KeepOnlyKeyAtFrame (targetClip, keepFrame, binding => paths.Contains (binding.path));
            AfterClipEdit ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 今のフレームのポーズをコピーする（Ctrl+C）。対象（骨・手足の IK・全身 IK の点など）を選んでいれば、選んだ物（複数可）の
        /// カーブだけを写す。貼る（Ctrl+V）と写した物にだけキーを打つので、腕だけ別のフレームの形にする、などができる。
        /// 全身 IK の点を選んでいれば、点に関する骨も選んだことにして写す（RelatedFkTargets）。
        /// 何も選んでいなければクリップ全体を写す
        /// </summary>
        public void CopyPose ()
        {
            if (targetClip == null) return;
            int count = SelectedTargets ().Count ();
            HashSet<string> paths = CollectSelectedPaths ();
            // キーの無いフレームの全身 IK の点は、出している場所を写す。Override へ書いているときは、出している値は差分でないので読まない
            Transform root = overrideWriteLayer == null ? editingRoot : null;
            if (count == 0) {
                PoseClipboard.Copy (targetClip, currentFrame, null, root);
                ShowNotification (new GUIContent (Tr ("PREVIEW_WINDOW_COPIED_WHOLE_POSE")), 0.8);
                return;
            }

            // 選んだ物にカーブが無くても、前にコピーしたものは捨てる（V で古い物を貼らないように）
            if (PoseClipboard.Copy (targetClip, currentFrame, binding => paths.Contains (binding.path), root) == 0) {
                ShowNotification (new GUIContent (Tr ("PREVIEW_WINDOW_SELECTION_HAS_NO_KEYS")), 1.5);
                return;
            }
            ShowNotification (new GUIContent (Tr ("PREVIEW_WINDOW_COPIED_SELECTION_POSE", count)), 0.8);
        }

        public void PastePose ()
        {
            if (!CanEditClip () || PoseClipboard.isEmpty) return;

            using (CurveWriter writer = BeginCurves ("Paste Pose")) {
                PoseClipboard.Paste (writer);
            }
            AfterClipEdit ();
        }

        /// <summary>
        /// 骨の向きを基準の姿勢に戻して、キーを打つ
        /// </summary>
        public void ResetPose ()
        {
            if (stage_ == null) return;
            foreach (PoseTarget target in targets_) {
                target.ResetPose ();
            }
            // Rig の重みとターゲットは prefab の値へ
            if (rigProxies != null) rigProxies.ResetToDefaults ();
            SolveAfterManipulate ();
            // 全身 IK の点は、基準へ戻して固定を外した値（強さ 0）もキーに打つ（点を離したときと同じ流れ）。
            // 姿勢全体のキーは固定している点しか打たないので、打たずにおくと、クリップに残った点のキー（元の場所に固定）で
            // 読み直したとき、骨は基準姿勢なのに点は元の場所に固定されたまま残る
            if (stage_.fullBody != null && CanEditClip ()) {
                CommitFullBodyPose ();
            }
            else {
                AddKeyAllCurves ();
                // 打てなかった点の印は捨てる（残すと、後でほかのフレームでキーを打つときに一緒に打たれる）
                foreach (PoseTarget target in targets_) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null) point.ClearPendingKeys ();
                }
            }
            RepaintView ();
        }
    }

}
