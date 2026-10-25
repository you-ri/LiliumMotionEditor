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

        /// <summary>frame のキーから次のキーまでを mode にする（1 回の Undo で戻る）。外部の動きの区間なら、外部の動きから外す（S82）</summary>
        public void SetInterpolation (int frame, KeyInterpolation mode)
        {
            if (!CanSetInterpolation (frame) || !KeyInterpolations.TryGetSegment (targetClip, frame, out int start, out int end)) return;
            SetInterpolation (new[] { new Interval { start = start, end = end } }, mode);
        }

        /// <summary>
        /// タイムラインで選んだキーの間（S86）。filter が null ならクリップの区間（クリップ全体のトラック）、
        /// あれば骨のトラックの、その骨のカーブだけのその骨のキーの間
        /// </summary>
        public struct Interval
        {
            public int start;
            public int end;
            public System.Predicate<EditorCurveBinding> filter;
        }

        /// <summary>
        /// キーの間をまとめて mode にする（S86。1 回の Undo で戻る）。クリップの区間で外部の動きなら、外部の動きから外す（S82）。
        /// 骨のキーの間は外部の動きの印を変えない（印はクリップの区間に付く）
        /// </summary>
        public void SetInterpolation (IList<Interval> intervals, KeyInterpolation mode)
        {
            if (!CanEditClip () || intervals == null || intervals.Count == 0) return;
            AnimationClip clip = targetClip;
            System.Predicate<EditorCurveBinding> exclude = InterpolationExclude ();
            if (clip == editingClip_) {
                foreach (Interval interval in intervals) {
                    if (interval.filter == null) BetweenClips.SetSegment (clip, new BetweenClips.Range { start = interval.start, end = interval.end }, false);
                }
            }
            EditClip (clip, "Key Interpolation", () => {
                int written = 0;
                foreach (Interval interval in intervals) written += KeyInterpolations.SetRange (clip, interval.start, interval.end, mode, interval.filter, exclude);
                return written > 0;
            });
            AfterClipEdit ();
            RaiseStateChanged ();
        }

        /// <summary>元のクリップの、frame のキーから次のキーまでの外部の動きの状態（S82）</summary>
        public BetweenClips.State GetBetweenState (int frame)
        {
            AnimationClip clip = editingClip_;
            if (clip == null || !KeyInterpolations.TryGetSegment (clip, frame, out int start, out int end)) return BetweenClips.State.None;
            return BetweenClips.GetState (clip, start, end);
        }

        /// <summary>
        /// frame のキーから次のキーまでを外部の動きにできるか（S82）: 元のクリップへ書いていて（外部の動きは元のクリップに効く）、結果のファイルがあり、区間がある
        /// </summary>
        public bool CanSetExternal (int frame)
        {
            return CanEditClip () && overrideWriteLayer == null && editingClip_ != null
                && KeyInterpolations.TryGetSegment (editingClip_, frame, out int start, out int end) && BetweenClips.Find (editingClip_) != null;
        }

        /// <summary>
        /// frame のキーから次のキーまでを外部の動きにする（S82。区間の印は .meta なので Undo では戻らない）。
        /// 下の補間が一定・リニアなら曲線に戻す（外部の動きはキーの近くで下の補間へ混ぜるため。こちらは Undo で戻る）
        /// </summary>
        public void SetExternal (int frame)
        {
            SetExternal (new[] { frame });
        }

        /// <summary>いくつかの区間（始まりのキーのフレーム）をまとめて外部の動きにする（S86。下の補間を曲線に戻すのは 1 回の Undo）</summary>
        public void SetExternal (IList<int> frames)
        {
            List<BetweenClips.Range> ranges = new List<BetweenClips.Range> ();
            foreach (int frame in frames) {
                if (CanSetExternal (frame) && KeyInterpolations.TryGetSegment (editingClip_, frame, out int start, out int end)) {
                    ranges.Add (new BetweenClips.Range { start = start, end = end });
                }
            }
            if (ranges.Count == 0) return;
            AnimationClip clip = editingClip_;
            System.Predicate<EditorCurveBinding> exclude = InterpolationExclude ();
            EditClip (clip, "Key Interpolation", () => {
                int written = 0;
                foreach (BetweenClips.Range range in ranges) written += KeyInterpolations.SetCurveUnderExternal (clip, range.start, exclude);
                return written > 0;
            });
            foreach (BetweenClips.Range range in ranges) BetweenClips.SetSegment (clip, range, true);
            AfterClipEdit ();
            RaiseStateChanged ();
        }

        [System.NonSerialized] List<BetweenClips.Range> betweenInProgress_;
        static readonly List<BetweenClips.Range> kNoRanges = new List<BetweenClips.Range> ();

        /// <summary>
        /// 外で今作っている元のクリップのキーの間（S85。外部のモデルで作るときなど）。タイムラインはこの区間の矢印を流れる波線にして、作っている最中だと見せる。
        /// 窓は作り方を知らないので、作る側が始めたときに入れ、終わったら空にする（窓の状態として保存しない。再読み込みの後は作る側が入れ直す）
        /// </summary>
        public IReadOnlyList<BetweenClips.Range> betweenInProgress
        {
            get { return betweenInProgress_ ?? kNoRanges; }
        }

        public void SetBetweenInProgress (IEnumerable<BetweenClips.Range> ranges)
        {
            List<BetweenClips.Range> next = ranges != null ? new List<BetweenClips.Range> (ranges) : new List<BetweenClips.Range> ();
            if (System.Linq.Enumerable.SequenceEqual (next, betweenInProgress)) return;
            betweenInProgress_ = next;
            RaiseStateChanged ();
        }

        /// <summary>キー from から to までが、外で作っている区間に掛かるか（S85。外部の動きは元のクリップに効くので、元のクリップへ書いているときだけ）</summary>
        public bool IsBetweenInProgress (int from, int to)
        {
            if (betweenInProgress.Count == 0 || targetClip != editingClip_) return false;
            foreach (BetweenClips.Range range in betweenInProgress) {
                if (from < range.end && range.start < to) return true;
            }
            return false;
        }

        /// <summary>frame のキーから次のキーまでの外部の動きを、元のクリップのキーにできるか（S82）</summary>
        public bool CanBakeBetween (int frame)
        {
            BetweenClips.State state = GetBetweenState (frame);
            return CanEditClip () && overrideWriteLayer == null && (state == BetweenClips.State.Fresh || state == BetweenClips.State.Stale);
        }

        /// <summary>frame のキーから次のキーまでの外部の動きを、元のクリップのキーにする（S82。PoseSession.BakeBetween。キーは Undo で戻る）</summary>
        public string BakeBetween (int frame)
        {
            if (!CanBakeBetween (frame) || stage_ == null || stage_.sourcePrefab == null) return null;
            CommitHandleKeys ();
            Undo.IncrementCurrentGroup ();
            string error;
            using (PoseSession session = new PoseSession (stage_.sourcePrefab, rigDefinition)) {
                session.clip = editingClip_;
                error = session.BakeBetween (frame);
            }
            AfterClipEdit ();
            RaiseStateChanged ();
            return error;
        }
    }

}
