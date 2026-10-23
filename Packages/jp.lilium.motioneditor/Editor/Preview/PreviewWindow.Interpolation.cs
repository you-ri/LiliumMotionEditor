using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// キーから次のキーまで（区間）の補間を選ぶ（S80）。書き込み先（✏）のクリップの区間を、曲線・リニア・一定にする。
    /// 持ち替えのカーブ（重み・握り）は段差なので変えない
    /// </summary>
    public partial class PreviewWindow
    {
        List<KeyInterpolations.Segment> interpolations_;
        AnimationClip interpolationsClip_;
        int interpolationsVersion_;

        /// <summary>持ち替えのカーブか（区間の補間で外す）。キャラの設定はカーブごとに引かず、1 回だけ引く</summary>
        System.Predicate<EditorCurveBinding> InterpolationExclude ()
        {
            IReadOnlyList<ParentSwitchDefinition> definitions = parentSwitches;
            if (definitions.Count == 0) return null;
            return binding => ParentSwitch.IsSwitchCurve (definitions, binding);
        }

        /// <summary>frame のキーから次のキーまでの補間（混在なら null）。区間が無ければ null</summary>
        public KeyInterpolation? GetInterpolation (int frame)
        {
            foreach (KeyInterpolations.Segment segment in interpolationSegments) {
                if (segment.start == frame) return segment.mode;
            }
            return null;
        }

        /// <summary>
        /// 書き込み先のクリップの区間と補間（混在なら null）。カーブが変わるまで使い回す
        /// </summary>
        public IReadOnlyList<KeyInterpolations.Segment> interpolationSegments
        {
            get {
                AnimationClip clip = targetClip;
                int version = clip != null ? EditingClip.GetCurveVersion (clip) : 0;
                if (interpolations_ == null || interpolationsClip_ != clip || interpolationsVersion_ != version) {
                    interpolations_ = KeyInterpolations.GetSegments (clip, InterpolationExclude ());
                    interpolationsClip_ = clip;
                    interpolationsVersion_ = version;
                }
                return interpolations_;
            }
        }

        /// <summary>frame のキーから次のキーまでの補間を変えられるか（次のキーがあり、書き込み先へ書ける）</summary>
        public bool CanSetInterpolation (int frame)
        {
            return CanEditClip () && KeyInterpolations.TryGetSegment (targetClip, frame, out int start, out int end);
        }

        /// <summary>frame のキーから次のキーまでを mode にする（1 回の Undo で戻る）</summary>
        public void SetInterpolation (int frame, KeyInterpolation mode)
        {
            if (!CanSetInterpolation (frame)) return;
            AnimationClip clip = targetClip;
            System.Predicate<EditorCurveBinding> exclude = InterpolationExclude ();
            EditClip (clip, "Key Interpolation", () => KeyInterpolations.SetSegment (clip, frame, mode, exclude) > 0);
            RaiseStateChanged ();
        }
    }

}
