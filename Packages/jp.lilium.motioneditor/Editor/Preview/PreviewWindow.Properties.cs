using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 任意のプロパティ（S6）の窓の側。表示モデルの部品の数値を列挙し、値を入れると今のフレームにキーを打つ。
    /// キーは編集用クリップの Controls/Props/&lt;表示モデルのパス&gt; に入り、焼いた Humanoid 版ではゲーム prefab のパスへ写る
    /// </summary>
    public partial class PreviewWindow
    {
        List<PropertyPlayer.Candidate> propertyCandidates_;
        GameObject propertyCandidatesFor_;
        EditorCurveBinding? selectedProperty_;

        /// <summary>選んでいるプロパティが替わったとき（カーブエディタが一覧を作り直す）</summary>
        public event System.Action propertySelectionChanged;

        GameObject propertyRoot
        {
            get { return stage_ != null && stage_.animator != null ? stage_.animator.gameObject : null; }
        }

        /// <summary>
        /// キーにできる数値の一覧（表示モデルから。キャラを作り直すまで覚えておく）
        /// </summary>
        public List<PropertyPlayer.Candidate> propertyCandidates
        {
            get {
                GameObject root = propertyRoot;
                if (propertyCandidates_ == null || propertyCandidatesFor_ != root) {
                    propertyCandidatesFor_ = root;
                    propertyCandidates_ = PropertyPlayer.ListCandidates (root);
                }
                return propertyCandidates_;
            }
        }

        /// <summary>
        /// クリップにカーブがあるプロパティ（表示モデルから見たバインディング）
        /// </summary>
        public HashSet<EditorCurveBinding> keyedProperties
        {
            get {
                HashSet<EditorCurveBinding> result = new HashSet<EditorCurveBinding> ();
                if (editingClip_ == null) return result;
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (editingClip_)) {
                    EditorCurveBinding display;
                    if (PropertyPlayer.TryToDisplay (binding, out display)) result.Add (display);
                }
                return result;
            }
        }

        public EditorCurveBinding? selectedProperty
        {
            get { return selectedProperty_; }
            set {
                if (selectedProperty_.Equals (value)) return;
                selectedProperty_ = value;
                if (propertySelectionChanged != null) propertySelectionChanged ();
            }
        }

        public bool TryGetPropertyValue (EditorCurveBinding display, out float value)
        {
            value = 0;
            GameObject root = propertyRoot;
            return root != null && PropertyPlayer.TryGetValue (root, display, out value);
        }

        /// <summary>
        /// 値を入れて、今のフレームにキーを打つ
        /// </summary>
        /// <param name="edit">値の欄を動かしたとき。Key ボタンから打つときは false（キーの自動追加が切でもキーを作る）</param>
        public void SetPropertyKey (EditorCurveBinding display, float value, bool edit = true)
        {
            if (!CanEditClip () || propertyRoot == null || overrideBlocksBaseWrite != null) return;
            // キーの自動追加が切: 今のフレームにキーが無ければ打たずに、表示をクリップの値へ戻す（キーは Key ボタンで打つ）
            if (edit && !autoKey) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (editingClip_, PropertyPlayer.ToClip (display));
                if (curve == null || ClipKeyUtility.FindKeyAtFrame (curve, editingClip_.frameRate, currentFrame) < 0) {
                    NotifyNoKey ();
                    SamplePose ();
                    return;
                }
            }
            Undo.IncrementCurrentGroup ();
            RecordClipUndo ("Set Property");
            CurveEdit.SetKey (editingClip_, PropertyPlayer.ToClip (display), currentFrame, value);
            AfterPropertyEdit ();
        }

        /// <summary>
        /// そのプロパティのカーブを消す（表示モデルの値は元へ戻る）
        /// </summary>
        public void RemovePropertyCurve (EditorCurveBinding display)
        {
            if (!CanEditClip () || overrideBlocksBaseWrite != null) return;
            Undo.IncrementCurrentGroup ();
            RecordClipUndo ("Remove Property");
            AnimationUtility.SetEditorCurve (editingClip_, PropertyPlayer.ToClip (display), null);
            AfterPropertyEdit ();
        }

        void AfterPropertyEdit ()
        {
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }
    }

}
