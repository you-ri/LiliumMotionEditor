using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// PoseBank の窓の側。今の姿勢（画面に出ている編集用リグの値）を取り出すのと、保存した姿勢を今のフレームへ打つのと
    /// </summary>
    public partial class PreviewWindow
    {
        Transform editingRoot
        {
            get { return stage_ != null && stage_.editingRig != null && stage_.editingRig.root != null ? stage_.editingRig.root.transform : null; }
        }

        /// <summary>
        /// 今の姿勢の値（全部の対象と、ゲームの Rig の重み）。キーを打つときと同じカーブの組。キャラが無ければ null。
        /// Override に書いているときも、差分でなく画面に出ている値を取る
        /// </summary>
        public Dictionary<EditorCurveBinding, float> CapturePose ()
        {
            Transform root = editingRoot;
            if (root == null) return null;
            Dictionary<EditorCurveBinding, float> result = new Dictionary<EditorCurveBinding, float> ();
            using (CurveWriter writer = CurveWriter.Begin (null, root, currentFrame, null)) {
                foreach (PoseTarget target in targets_) {
                    target.WriteKeys (writer);
                }
                if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
                foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) result[pair.Key] = pair.Value;
            }
            return result;
        }

        /// <summary>選んでいる対象の数（PoseBank が「どこへ貼るか」を出す）</summary>
        public int selectedTargetCount
        {
            get { return selection_.Select (FindTarget).Where (t => t != null).Distinct ().Count (); }
        }

        /// <summary>
        /// 選んでいる対象がキーを打つカーブのパス。何も選んでいなければ null（＝全部）
        /// </summary>
        HashSet<string> SelectedKeyedPaths ()
        {
            List<PoseTarget> selected = selection_.Select (FindTarget).Where (t => t != null).Distinct ().ToList ();
            if (selected.Count == 0) return null;
            HashSet<string> paths = new HashSet<string> ();
            foreach (PoseTarget target in selected) target.CollectKeyedPaths (paths);
            return paths;
        }

        /// <summary>
        /// 保存した姿勢を今のフレームへ打つ。選んでいる物があればそのカーブだけ（手の指だけ、など）、無ければ姿勢全部。
        /// このキャラに無い物を指すカーブは打たない。打てなければ理由を返す（打てたら null。applied に打った本数）
        /// </summary>
        public string ApplyPose (AnimationClip pose, out int applied)
        {
            applied = 0;
            if (pose == null) return null;
            Transform root = editingRoot;
            if (root == null) return Tr ("PREVIEW_WINDOW_POSE_BANK_SELECT_CHARACTER");
            if (!CanEditClip ()) return GetCurveEditProblem () ?? Tr ("PREVIEW_WINDOW_POSE_BANK_CANNOT_WRITE_NOW");

            HashSet<string> paths = SelectedKeyedPaths ();
            Dictionary<EditorCurveBinding, float> values;
            if (PoseBank.IsHumanoidPose (pose)) {
                string error = CaptureHumanoidPose (pose, paths, out values);
                if (error != null) return error;
            }
            else {
                values = PoseBank.Read (pose, binding =>
                    (paths == null || paths.Contains (binding.path)) && (binding.path.Length == 0 || root.Find (binding.path) != null));
            }
            if (values.Count == 0) {
                return paths == null ? Tr ("PREVIEW_WINDOW_POSE_BANK_NO_VALUES_FOR_CHARACTER") : Tr ("PREVIEW_WINDOW_POSE_BANK_NO_VALUES_FOR_SELECTION");
            }

            // 画面に出す値として打つ（Override に書いているときは差分に直る）
            using (CurveWriter writer = BeginCurves ("Apply Pose " + PoseBank.BaseName (AssetDatabase.GetAssetPath (pose)))) {
                foreach (KeyValuePair<EditorCurveBinding, float> pair in values) {
                    writer.SetTarget (pair.Key, pair.Value);
                }
            }
            applied = values.Count;
            keyFrames_ = null;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
            return null;
        }

        /// <summary>
        /// Humanoid の姿勢（muscle の値）を今の姿勢に重ねて、動いた骨の FK の操作値にする。
        /// 打つのは姿勢が動かす骨（手の形なら指）だけで、選んでいる物があればさらにその中だけ
        /// </summary>
        string CaptureHumanoidPose (AnimationClip pose, HashSet<string> selected, out Dictionary<EditorCurveBinding, float> values)
        {
            values = new Dictionary<EditorCurveBinding, float> ();
            if (stage_ == null || !stage_.canPreviewHumanoid) return Tr ("PREVIEW_WINDOW_POSE_BANK_DISPLAY_NOT_HUMANOID");
            Dictionary<int, float> muscles = PoseBank.ReadMuscles (pose);
            if (muscles.Count == 0) return Tr ("PREVIEW_WINDOW_POSE_BANK_NO_HUMANOID_VALUES");

            HashSet<string> moved = stage_.ApplyMusclesAsControls (muscles);
            if (moved == null) return Tr ("PREVIEW_WINDOW_POSE_BANK_CANNOT_APPLY_HUMANOID");
            if (moved.Count == 0) return Tr ("PREVIEW_WINDOW_POSE_BANK_NO_MOVED_BONES");
            Dictionary<EditorCurveBinding, float> captured = CapturePose ();
            if (captured == null) return Tr ("PREVIEW_WINDOW_POSE_BANK_SELECT_CHARACTER");
            foreach (KeyValuePair<EditorCurveBinding, float> pair in captured) {
                if (!moved.Contains (pair.Key.path)) continue;
                if (selected != null && !selected.Contains (pair.Key.path)) continue;
                values[pair.Key] = pair.Value;
            }
            return null;
        }
    }

}
