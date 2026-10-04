using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// カーブエディタ（S5）の窓の側。選んでいる物のカーブの一覧と、カーブを直接書き換えるときの前後（Undo の記録・解き直し）
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// カーブエディタに出すカーブの組（選んでいる物ごと）
        /// </summary>
        public sealed class CurveOwner
        {
            public string label;
            public List<EditorCurveBinding> bindings = new List<EditorCurveBinding> ();
        }

        /// <summary>
        /// 選んでいる物のカーブ（クリップにあるものだけ）。選んでいなければ空
        /// </summary>
        public List<CurveOwner> GetSelectedCurves ()
        {
            List<CurveOwner> owners = new List<CurveOwner> ();
            AnimationClip clip = targetClip;
            if (clip == null) return owners;
            EditorCurveBinding[] all = AnimationUtility.GetCurveBindings (clip);
            foreach (PoseTarget target in selection_.Select (FindTarget).Where (t => t != null).Distinct ()) {
                HashSet<string> paths = new HashSet<string> ();
                target.CollectKeyedPaths (paths);
                CurveOwner owner = new CurveOwner { label = target.label };
                foreach (EditorCurveBinding binding in all) {
                    if (paths.Contains (binding.path)) owner.bindings.Add (binding);
                }
                if (owner.bindings.Count > 0) owners.Add (owner);
            }
            // Properties のパネルで選んでいるプロパティ（カーブがあれば）
            if (selectedProperty_.HasValue) {
                EditorCurveBinding binding = PropertyPlayer.ToClip (selectedProperty_.Value);
                if (all.Contains (binding)) {
                    CurveOwner owner = new CurveOwner { label = "Property" };
                    owner.bindings.Add (binding);
                    owners.Add (owner);
                }
            }
            return owners;
        }

        /// <summary>
        /// カーブを書き換えてよいか（書けない理由があれば、その文）
        /// </summary>
        public string GetCurveEditProblem ()
        {
            if (editingClip_ == null) return Tr ("PREVIEW_WINDOW_CURVES_OPEN_CLIP");
            if (clipProblem_ != null) return clipProblem_;
            if (CanEditClip ()) return null;
            return timelineEditBlockReason ?? Tr ("PREVIEW_WINDOW_CURVES_WRITE_LAYER_INACTIVE");
        }

        /// <summary>
        /// カーブを書き換える前（Undo の記録）
        /// </summary>
        public void BeginCurveEdit (string undoName)
        {
            // 前の操作（Spinner の入力など）と同じ Undo にまとめない
            Undo.IncrementCurrentGroup ();
            RecordClipUndo (undoName);
        }

        /// <summary>
        /// カーブを書き換えた後。姿勢を作り直す（キーの一覧は OnCurveWasModified が捨てる）
        /// </summary>
        public void EndCurveEdit ()
        {
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }

        /// <summary>
        /// 今の時刻（秒）。カーブエディタの再生位置の線
        /// </summary>
        public float currentTime
        {
            get { return CurrentTime (); }
        }
    }

}
