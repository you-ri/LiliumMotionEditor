using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 任意のプロパティ（S6）の窓の側。表示モデルの部品の数値を列挙し、値を入れると今のフレームにキーを打つ。
    /// キーは書き込み先（✏）のクリップの Controls/Props/&lt;表示モデルのパス&gt; に入り、焼いた Humanoid 版ではゲーム prefab のパスへ写る。
    /// 書き込み先が Override の段なら、段の合成の仕方に合わせた値（Additive は手前からの差分）で入る（S17）
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
        /// 書き込み先のクリップにカーブがあるプロパティ（表示モデルから見たバインディング）
        /// </summary>
        public HashSet<EditorCurveBinding> keyedProperties
        {
            get {
                HashSet<EditorCurveBinding> result = new HashSet<EditorCurveBinding> ();
                AnimationClip clip = targetClip;
                if (clip == null) return result;
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
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
            AnimationClip clip = targetClip;
            if (clip == null || !CanEditClip () || propertyRoot == null) return;
            string masked = GetPropertyMaskProblem (display);
            if (masked != null) {
                ShowNotification (new GUIContent (masked), 1.5);
                SamplePose ();
                return;
            }
            EditorCurveBinding binding = PropertyPlayer.ToClip (display);
            // キーの自動追加が切: 今のフレームにキーが無ければ打たずに、表示をクリップの値へ戻す（キーは Key ボタンで打つ）
            if (edit && !autoKey) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve == null || ClipKeyUtility.FindKeyAtFrame (curve, clip.frameRate, currentFrame) < 0) {
                    NotifyNoKey ();
                    SamplePose ();
                    return;
                }
            }
            EditClip (clip, "Set Property", () => {
                CurveEdit.SetKey (clip, binding, currentFrame, StorePropertyValue (display, value));
                return true;
            });
        }

        /// <summary>
        /// 書き込み先が Override の段なら、見せたい値を段に入れる値へ直す（Additive の小数は、段の手前の値からの差分）
        /// </summary>
        float StorePropertyValue (EditorCurveBinding display, float value)
        {
            OverrideLayer layer = overrideWriteLayer;
            if (layer == null || stage_ == null) return value;
            float source;
            if (!stage_.TryGetOverridePropertySource (display, out source)) return value;
            return OverrideMath.StoreFloat (layer.mode, PropertyPlayer.IsAdditive (propertyRoot, display), source, value);
        }

        /// <summary>
        /// 書き込み先の Override の段のマスクの外なので、そのプロパティにキーを打てない理由（打てるなら null。S17）
        /// </summary>
        public string GetPropertyMaskProblem (EditorCurveBinding display)
        {
            OverrideLayer layer = overrideWriteLayer;
            ChannelMask mask = layer != null ? layer.mask : null;
            if (mask == null || stage_ == null || stage_.InMask (mask, PropertyPlayer.ToClip (display))) return null;
            return Tr ("PREVIEW_WINDOW_OUTSIDE_MASK", display.propertyName, mask.name, layer.label);
        }

        /// <summary>
        /// そのプロパティに値を入れてキーを打てない理由（打てるなら null）
        /// </summary>
        public string GetPropertyEditProblem (EditorCurveBinding display)
        {
            return GetCurveEditProblem () ?? GetPropertyMaskProblem (display);
        }

        /// <summary>
        /// そのプロパティのカーブを書き込み先のクリップから消す（表示モデルの値は手前の段の値へ戻る）
        /// </summary>
        public void RemovePropertyCurve (EditorCurveBinding display)
        {
            AnimationClip clip = targetClip;
            if (clip == null || !CanEditClip ()) return;
            EditClip (clip, "Remove Property", () => {
                AnimationUtility.SetEditorCurve (clip, PropertyPlayer.ToClip (display), null);
                return true;
            });
        }
    }

}
