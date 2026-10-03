using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 文脈（技の演出など、Timeline で一緒に動く物）。**時計は窓が正**で、フレームを動かすたびに文脈へその時刻を入れる（S15a）。
    /// 自キャラの姿勢は段の並びが作るので、文脈には渡さない（二重に書くと、どちらが勝つか読めなくなる）。
    ///
    /// 演出の中のクリップと結び付けると（S15b）、演出の時刻 ↔ クリップの時刻をそのクリップの置き方（開始・頭出し・速さ）で写す。
    /// 窓の時計はクリップの時刻（端数あり）のまま持ち、キーはクリップの格子に丸めて打つ。演出の時刻はツールバーの欄で 1F ずつ動かせる
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>一緒に見る演出の prefab（Director を持つもの）</summary>
        [SerializeField] GameObject contextPrefab_;
        /// <summary>演出の何フレーム目を、編集しているクリップの 0F に合わせるか（演出のクリップと結び付けていないとき）</summary>
        [SerializeField] int contextOffset_;
        /// <summary>
        /// 演出のどのクリップとして見るか（ContextClip.key）。空なら自動（編集中のクリップ・その焼いた版・Humanoid Pose のクリップと同じものを探す）、
        /// kNoTimelineClip なら結び付けない
        /// </summary>
        [SerializeField] string timelineClipKey_;

        const string kNoTimelineClip = "-";

        ContextClip timelineClip_;

        public GameObject contextPrefab
        {
            get { return contextPrefab_; }
        }

        public int contextOffset
        {
            get { return contextOffset_; }
        }

        /// <summary>
        /// 一緒に見る演出を選び直す。置き方が変わるので世界を作り直す
        /// </summary>
        public void SetContext (GameObject prefab)
        {
            if (prefab == contextPrefab_) return;
            Undo.RecordObject (this, "Motion Editor Context");
            contextPrefab_ = prefab;
            // 別の演出のクリップを指したままにしない
            timelineClipKey_ = null;
            RebuildStage ();
        }

        public void SetContextOffset (int frames)
        {
            if (frames == contextOffset_) return;
            Undo.RecordObject (this, "Motion Editor Context Offset");
            contextOffset_ = frames;
            keyFrames_ = null;
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 文脈に置いたものの中身（トラックと注意）。パネルに出す
        /// </summary>
        public IReadOnlyList<ContextInfo> contextInfos
        {
            get {
                // Motion Scene では、Timeline 窓が開いている開いているシーンの演出（読むだけ。S15d）。prefab の演出は置かない
                if (sceneTarget_ != null) return sceneContext_ != null ? new[] { sceneContext_ } : System.Array.Empty<ContextInfo> ();
                return stage_ != null ? stage_.contextInfos : (IReadOnlyList<ContextInfo>)System.Array.Empty<ContextInfo> ();
            }
        }

        /// <summary>演出の長さ（秒。置いていなければ 0）</summary>
        double contextDuration
        {
            get {
                double result = 0;
                foreach (ContextInfo info in contextInfos) result = System.Math.Max (result, info.duration);
                return result;
            }
        }

        // ---- 演出のクリップとの結び付け（S15b） ----

        /// <summary>
        /// 演出のどのクリップとして見ているか（無ければ null）。Timeline Clip の段がこれを読む
        /// </summary>
        public ContextClip timelineClip
        {
            get { return timelineClip_; }
        }

        /// <summary>
        /// 自動で選んでいるか（手で選んでいない）
        /// </summary>
        public bool timelineClipAuto
        {
            get { return string.IsNullOrEmpty (timelineClipKey_); }
        }

        /// <summary>
        /// 演出にあるアニメーションのクリップ（選べるもの）
        /// </summary>
        public List<ContextClip> timelineClipCandidates
        {
            get { return contextInfos.SelectMany (info => info.animationClips).ToList (); }
        }

        public int timelineClipCandidateCount
        {
            get { return contextInfos.Sum (info => info.animationClips.Count ()); }
        }

        /// <summary>
        /// 演出のどのクリップとして見るかを選ぶ。null で自動、kNoTimelineClip（"-"）で結び付けない
        /// </summary>
        public void SetTimelineClip (string key)
        {
            if (key == timelineClipKey_) return;
            Undo.RecordObject (this, "Motion Editor Timeline Clip");
            timelineClipKey_ = key;
            SamplePose ();
            RaiseStackChanged ();
            RaiseStateChanged ();
        }

        public void SetTimelineClipNone ()
        {
            SetTimelineClip (kNoTimelineClip);
        }

        /// <summary>
        /// 結び付けているクリップを決め直す。手で選んでいればそれ、無ければ編集中のクリップと同じ物を演出の中から探す
        /// </summary>
        void ResolveTimelineClip ()
        {
            ContextClip previous = timelineClip_;
            timelineClip_ = FindTimelineClip ();
            if (previous == timelineClip_) return;
            // 段の説明とツールバー（結び付けているクリップ・演出の時刻の欄）を合わせる
            RaiseStackChanged ();
            RaiseStateChanged ();
        }

        ContextClip FindTimelineClip ()
        {
            if (stage_ == null || timelineClipKey_ == kNoTimelineClip) return null;
            List<ContextClip> candidates = timelineClipCandidates;
            if (candidates.Count == 0) return null;
            if (!string.IsNullOrEmpty (timelineClipKey_)) {
                ContextClip chosen = candidates.FirstOrDefault (c => c.key == timelineClipKey_);
                if (chosen != null) return chosen;
            }
            return FindMatchingTimelineClip (candidates, TimelineMatchClips ());
        }

        /// <summary>
        /// 演出のクリップと突き合わせる物: 焼いた Humanoid 版（ゲームが使う）→ Humanoid Pose の段のクリップ → 編集中のクリップ → Generic Pose の段のクリップ
        /// </summary>
        List<AnimationClip> TimelineMatchClips ()
        {
            List<AnimationClip> result = new List<AnimationClip> ();
            if (editingClip_ != null) {
                string path = HumanoidOutput.GetOutputPath (editingClip_);
                AnimationClip baked = string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
                if (baked != null) result.Add (baked);
            }
            if (humanoidClip_ != null) result.Add (humanoidClip_);
            if (editingClip_ != null) result.Add (editingClip_);
            if (genericClip_ != null) result.Add (genericClip_);
            return result;
        }

        /// <summary>
        /// 突き合わせる物の順に、同じクリップを使っている演出のクリップを探す（同じクリップが何度も出るなら先頭）
        /// </summary>
        internal static ContextClip FindMatchingTimelineClip (IList<ContextClip> candidates, IEnumerable<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips) {
                if (clip == null) continue;
                ContextClip found = candidates.Where (c => c.clip == clip).OrderBy (c => c.start).FirstOrDefault ();
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 写像に使っているクリップ（Timeline Clip の段の 👁 を落としていれば null。オフセットで合わせる）
        /// </summary>
        ContextClip activeTimelineClip
        {
            get {
                if (timelineClip_ == null || poseStack_ == null) return null;
                PoseLayer layer = poseStack_.Find (LayerKind.TimelineClip);
                return layer != null && layer.active ? timelineClip_ : null;
            }
        }

        /// <summary>
        /// 今の時刻でキーを打てない理由（Timeline のブレンド中など。打てるなら null）
        /// </summary>
        public string timelineEditBlockReason
        {
            get {
                TimelineClipLayer layer = poseStack_ != null ? poseStack_.Find (LayerKind.TimelineClip) as TimelineClipLayer : null;
                return layer != null ? layer.editBlockReason : null;
            }
        }

        /// <summary>
        /// 時計の写像を今の結び付けに合わせる。結び付けていればそのクリップの置き方、無ければオフセット（フレーム）だけずらす
        /// </summary>
        void UpdateClockMapping ()
        {
            ContextClip link = activeTimelineClip;
            if (link != null) {
                clock_.SetMapping (link.MasterFromLocal (0), link.timeScale, contextFrameRate);
            } else {
                clock_.SetMapping (contextOffset_ / (double)clock_.rate, 1);
            }
        }

        /// <summary>演出のフレームの細かさ（置いていなければ 60）</summary>
        public double contextFrameRate
        {
            get {
                IReadOnlyList<ContextInfo> infos = contextInfos;
                return infos.Count > 0 && infos[0].frameRate > 0 ? infos[0].frameRate : 60;
            }
        }

        /// <summary>
        /// 演出のクリップと結び付けて、演出の時刻で動かしているか
        /// </summary>
        public bool followsTimelineClip
        {
            get { return activeTimelineClip != null; }
        }

        /// <summary>
        /// 結び付けているクリップが演出で使われている区間（クリップのフレーム）と、その中のブレンドの区間。結び付けていなければ false
        /// </summary>
        public bool TryGetTimelineClipFrames (out float start, out float end, out float blendInEnd, out float blendOutStart)
        {
            start = end = blendInEnd = blendOutStart = 0;
            ContextClip link = activeTimelineClip;
            if (link == null) return false;
            float rate = clock_.rate;
            start = (float)(link.LocalFromMaster (link.start) * rate);
            end = (float)(link.LocalFromMaster (link.end) * rate);
            blendInEnd = (float)(link.LocalFromMaster (link.start + link.blendIn) * rate);
            blendOutStart = (float)(link.LocalFromMaster (link.end - link.blendOut) * rate);
            return true;
        }

        /// <summary>
        /// 今の演出の時刻（演出のフレーム。端数は丸める）
        /// </summary>
        public int masterFrame
        {
            get { return Mathf.RoundToInt ((float)(clock_.MasterFromLocal (CurrentTime ()) * contextFrameRate)); }
        }

        /// <summary>
        /// 演出のフレームへ移る。クリップの時刻は写像で決まる（スローなら端数になり、キーは丸めた所に打つ）
        /// </summary>
        public void SetMasterFrame (int frame)
        {
            UpdateClockMapping ();
            clock_.SetMasterTime (frame / contextFrameRate);
            SamplePose ();
            RaiseStateChanged ();
        }

        void AddContext ()
        {
            if (stage_ == null || contextPrefab_ == null) return;
            // Motion Scene では開いているシーンの演出を使う（見えているのはシーンなので、裏に演出を置いても映らない）
            if (sceneTarget_ != null) return;
            stage_.AddContext (contextPrefab_);
            // 出せなかった理由（Director が無い・Timeline が入っていない）は黙らせない
            List<string> notes = new List<string> ();
            foreach (ContextInfo info in stage_.contextInfos) notes.AddRange (info.notes);
            if (notes.Count > 0) SetBakeStatus (contextPrefab_.name + ": " + string.Join (" / ", notes), false);
        }

        /// <summary>
        /// 文脈をそのフレームの姿にする。演出の時刻は時計の写像（結び付けたクリップの置き方、無ければ クリップの時刻 ＋ オフセット）
        /// </summary>
        void EvaluateContexts (float time)
        {
            if (stage_ == null || stage_.contextInfos.Count == 0) return;
            stage_.EvaluateContexts (clock_.MasterFromLocal (time));
        }

        /// <summary>
        /// 演出の終わりまで動かせるようにする（クリップより長いときのため）
        /// </summary>
        int ContextLastFrame ()
        {
            double duration = contextDuration;
            if (duration <= 0) return 0;
            UpdateClockMapping ();
            return Mathf.Max (0, Mathf.CeilToInt ((float)(clock_.LocalFromMaster (duration) * clock_.rate) - Clock.kGridEpsilon));
        }

        /// <summary>
        /// 再生で回る範囲（クリップのローカル秒）。演出のクリップと結び付けていれば演出の頭から終わりまで、無ければクリップの頭から最後のキーまで
        /// </summary>
        void PlayRange (out double start, out double end)
        {
            double duration = contextDuration;
            if (followsTimelineClip && duration > 0) {
                // クリップが始まる前（ローカルがマイナス）も回す。姿勢はクリップの頭で止まる
                start = clock_.LocalFromMaster (0);
                end = clock_.LocalFromMaster (duration);
                return;
            }
            start = 0;
            end = (GetLastKeyFrame () + 1) / (double)clock_.rate;
        }
    }

}
