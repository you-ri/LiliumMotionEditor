using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 窓を開かずに、キャラの姿勢を作ってキーを打つ（S35。AI や外部の道具から使う土台）。
    /// 描かない世界（PreviewStage.CreateHeadless）と段の並び（PoseStack）を持ち、窓と同じ段を通して姿勢を作り、
    /// 窓と同じ決まり（PoseCommit）でキーを打つ。窓の中の物を使い回すので、窓で作る姿勢・打つキーと食い違わない。
    ///
    /// 段は Editing Rig・Override・全身 IK と、キャラに組まれている Rig・ゲーム側の段・表示（窓と同じ）。
    /// Humanoid Pose・Output の段は置かない（見比べる参照・焼いて書き出す口は窓の物）。
    /// 書き込み先が元のクリップの間は、Override を効かせない（窓と同じ。効かせたまま書くと Override の分まで元へ入る）
    /// </summary>
    public sealed class PoseSession : System.IDisposable
    {
        readonly Host host_;
        readonly List<OverrideClip> overrides_ = new List<OverrideClip> ();
        readonly List<PoseTarget> targets_;
        AnimationClip clip_;
        int overrideWriteIndex_ = -1;

        /// <param name="prefab">編集するキャラ（Humanoid の Animator を持つ物）。複製して使うので、元は変えない</param>
        /// <param name="definition">編集用リグの定義。null ならパッケージの既定の定義を一時の物として作る</param>
        public PoseSession (GameObject prefab, EditRigDefinition definition = null)
            : this (prefab, definition, false)
        {
        }

        /// <param name="render">
        /// 描く世界で作る（画像を撮るとき。PoseCapture）。描くには画面のある Unity が要る（-nographics では PreviewRenderUtility を作れない）
        /// </param>
        public PoseSession (GameObject prefab, EditRigDefinition definition, bool render)
            : this (prefab, RigVariants.Resolve (prefab, "", definition), render)
        {
        }

        /// <param name="setup">
        /// 定義と持つ物（RigVariants.Resolve。S92）。開くクリップの印のバリアントと違えば、キーを打つ・焼くを断る（作り漏らした一時セッションが違うリグで書くのを防ぐ）
        /// </param>
        public PoseSession (GameObject prefab, RigSetup setup, bool render = false)
        {
            this.setup = setup ?? new RigSetup (null);
            definition = this.setup.definition;
            stage = render ? new PreviewStage (prefab, this.setup) : PreviewStage.CreateHeadless (prefab, this.setup);
            host_ = new Host (this);
            targets_ = stage.editingRig != null ? RigTargets.Create (stage) : new List<PoseTarget> ();
            Rebuild ();
        }

        public void Dispose ()
        {
            foreach (PoseTarget target in targets_) target.Dispose ();
            targets_.Clear ();
            if (stage != null) stage.Dispose ();
            stage = null;
            stack = null;
        }

        /// <summary>描かない世界（編集用の体・表示モデル）</summary>
        public PreviewStage stage { get; private set; }

        /// <summary>開いたときに渡した編集用リグの定義（null ならパッケージの既定）</summary>
        public EditRigDefinition definition { get; private set; }

        /// <summary>開いたときのリグの組み（定義・持つ物・バリアントの名前。S92）</summary>
        public RigSetup setup { get; private set; }

        /// <summary>
        /// 今のクリップを、このセッションのリグで書けない理由（書けるなら null）。印のバリアントが違う・無い、今のリグに無い物のカーブがある（S92）
        /// </summary>
        string variantProblem
        {
            get { return useHumanoidBase ? null : RigVariants.GetClipProblem (clip_, setup); }
        }

        /// <summary>段の並び。窓と同じ物で、姿勢はこれを上から通して作る</summary>
        public PoseStack stack { get; private set; }

        /// <summary>つかむ対象（FK・全身 IK の点・持ち替えの物）。窓の対象と同じ作り方</summary>
        public IReadOnlyList<PoseTarget> targets
        {
            get { return targets_; }
        }

        /// <summary>キャラを置けなかった理由（置けたら null）</summary>
        public string error
        {
            get { return stage != null ? stage.error : null; }
        }

        /// <summary>キャラが無くて姿勢を作れない理由（キャラを置けていれば null）</summary>
        public string characterProblem
        {
            get { return stage == null || stage.editingRig == null ? error ?? Tr ("POSE_SESSION_NO_CHARACTER") : null; }
        }

        /// <summary>session が無いときも引ける characterProblem（窓を開かずに使う口が、最初に見る）</summary>
        internal static string CharacterProblem (PoseSession session)
        {
            return session != null ? session.characterProblem : Tr ("POSE_SESSION_NO_CHARACTER");
        }

        /// <summary>今のフレーム（最後に Evaluate したフレーム）</summary>
        public int frame { get; private set; }

        /// <summary>
        /// Editing Rig の段に開くクリップ。編集用クリップ（*.rig.anim）か、読み取り専用の土台にする Humanoid のクリップ（書くのは Override へ）
        /// </summary>
        public AnimationClip clip
        {
            get { return clip_; }
            set {
                if (clip_ == value) return;
                clip_ = value;
                Rebuild ();
            }
        }

        /// <summary>Humanoid のクリップを土台に開いているか（S20。書き込み先は Override だけ）</summary>
        public bool useHumanoidBase
        {
            get { return clip_ != null && clip_.humanMotion; }
        }

        /// <summary>元のクリップに重ねる Override（下から順）</summary>
        public IReadOnlyList<OverrideClip> overrides
        {
            get { return overrides_; }
        }

        /// <summary>Override を一番上に重ねる。段の並びを作り直す（書き込み先は変えない）。mask があれば、その範囲にだけ効く（S17）</summary>
        public void AddOverride (AnimationClip overrideClip, OverrideMode mode = OverrideMode.Additive, ChannelMask mask = null)
        {
            if (overrideClip == null) return;
            overrides_.Add (new OverrideClip { clip = overrideClip, mode = mode, mask = mask });
            Rebuild ();
        }

        /// <summary>
        /// 書き込み先の Override は何枚目か（-1 は元のクリップ）。書き込み先より上の Override は、姿勢を作るときに効かない（窓と同じ）。
        /// 元と全部の Override を重ねた姿勢を見るときは、一番上の Override を書き込み先にする
        /// </summary>
        public int overrideWriteIndex
        {
            get {
                OverrideLayer layer = writeOverride;
                return layer != null ? layer.index : -1;
            }
            set {
                overrideWriteIndex_ = value;
                ApplyWriteLayer ();
            }
        }

        OverrideLayer writeOverride
        {
            get { return stack != null ? stack.writeLayer as OverrideLayer : null; }
        }

        /// <summary>キーを打つクリップ。書き込み先が Override ならそのクリップ、そうでなければ元のクリップ</summary>
        public AnimationClip targetClip
        {
            get {
                OverrideLayer layer = writeOverride;
                return layer != null ? layer.overrideClip : clip_;
            }
        }

        float frameRate
        {
            get { return CurveEdit.FrameRateOf (clip_); }
        }

        float time
        {
            get { return frame / frameRate; }
        }

        void Rebuild ()
        {
            stack = PoseStack.Build (stage, host_);
            ApplyWriteLayer ();
            if (stage != null) stage.InvalidateClipCurves ();
        }

        void ApplyWriteLayer ()
        {
            if (stack == null) return;
            PoseLayer write = overrideWriteIndex_ >= 0
                ? stack.Find<OverrideLayer> (l => l.index == overrideWriteIndex_)
                : stack.Find (LayerKind.EditingRig);
            string reason;
            if (write != null) stack.TrySetWrite (write, out reason);
        }

        /// <summary>
        /// そのフレームの姿勢を作る（基準の姿勢へ戻してから、段を上から通す）
        /// </summary>
        public void Evaluate (int frame)
        {
            this.frame = frame;
            if (stack != null) stack.Evaluate (time);
        }

        /// <summary>
        /// 操作値や全身 IK の点を直に動かした後に、Editing Rig の段から下を解き直す（動かした値はクリップで上書きしない。窓のハンドルの後と同じ）
        /// </summary>
        public void SolveAfterEdit ()
        {
            if (stack != null) stack.EvaluateFrom (stack.Find (LayerKind.EditingRig), time);
        }

        /// <summary>今のフレームにキーを打てない理由（打てるなら null）。窓と同じ条件（PoseCommit.GetWriteProblem）に、✋ からの経路とサイクルキーを足したもの</summary>
        public string GetWriteProblem ()
        {
            string problem = characterProblem
                ?? PoseCommit.GetWriteProblem (stack, targetClip, writeOverride == null ? (EditingClip.GetProblem (clip_) ?? variantProblem) : null);
            if (problem != null) return problem;
            string reason;
            if (!stack.CanWrite (stack.writeLayer, out reason)) return reason ?? Tr ("POSE_STACK_CANNOT_WRITE");
            AnimationClip target = targetClip;
            if (PoseCommit.IsPastCycleKey (frame, ClipCycle.Load (target), ClipCycle.GetFrames (target))) return Tr ("PREVIEW_WINDOW_PAST_CYCLE_KEY");
            return null;
        }

        /// <summary>
        /// 今のクリップ・今のフレームへの書き出し口（窓の BeginCurves と同じ）。書き込み先が Override なら元からの差分として書き、
        /// サイクルキーのフレームなら先頭のキーへ打つ。書けるかは先に GetWriteProblem で見る
        /// </summary>
        public CurveWriter BeginCurves (string undoName)
        {
            AnimationClip target = targetClip;
            return PoseCommit.BeginCurves (stage, stack, target, frame, ClipCycle.Load (target), ClipCycle.GetFrames (target), undoName);
        }

        /// <summary>
        /// 今の姿勢（解いた後）を今のフレームのキーにする。窓で点を離したときと同じく、全身の骨と動かした点を打つ。
        /// 全身 IK の点が 1 つも置けなかったキャラでは、つかめる物全部の今の値を打つ。1 回の Undo で戻る。打った後、そのフレームの姿勢をクリップから作り直す
        /// </summary>
        /// <returns>打てなかった理由（打てたら null）</returns>
        public string CommitPose (string undoName = "Key Pose")
        {
            string problem = GetWriteProblem ();
            if (problem != null) return problem;

            WriteWithUndo (undoName, () => {
                if (stage.fullBody != null) {
                    PoseCommit.WriteFullBodyPose (stage, targets_, () => BeginCurves (undoName));
                    return;
                }
                using (CurveWriter writer = BeginCurves (undoName)) {
                    PoseCommit.WriteAllKeys (writer, targets_, stage.editingRig.rigProxies);
                }
            });
            return null;
        }

        /// <summary>
        /// 書き込み先のクリップの、frame のキーから次のキーまでを mode にする（S80）。持ち替えのカーブ（重み・握り）は変えない。1 回の Undo で戻る
        /// </summary>
        /// <returns>変えられなかった理由（変えたら null）</returns>
        public string SetInterpolation (int frame, KeyInterpolation mode, string undoName = "Key Interpolation")
        {
            AnimationClip clip = targetClip;
            if (clip == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (!KeyInterpolations.TryGetSegment (clip, frame, out int start, out int end)) return Tr ("POSE_SESSION_NO_SEGMENT", frame);
            System.Predicate<EditorCurveBinding> exclude = SwitchCurves ();
            WriteWithUndo (undoName, () => {
                ClipUndo.Record (clip, undoName);
                KeyInterpolations.SetSegment (clip, frame, mode, exclude);
            });
            // 曲線・リニア・一定を選んだら、外部の補間からは外す（S82）
            if (clip == clip_) BetweenClips.SetSegment (clip_, new BetweenClips.Range { start = start, end = end }, false);
            return null;
        }

        /// <summary>元のクリップの、frame のキーから次のキーまでの外部の補間の状態（S82）</summary>
        public BetweenClips.State GetBetweenState (int frame)
        {
            return BetweenClips.GetStateAt (clip_, frame);
        }

        /// <summary>
        /// 元のクリップの、frame のキーから次のキーまでを外部の補間にする（on）・外す（S82）。外部の補間は元のクリップにだけ効くので、
        /// 書き込み先が Override のときは付けない。結果のファイルが無ければ付けない。区間の印は .meta なので Undo では戻らない
        /// </summary>
        /// <returns>変えられなかった理由（変えたら null）</returns>
        public string SetExternal (int frame, bool on)
        {
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (!KeyInterpolations.TryGetSegment (clip_, frame, out int start, out int end)) return Tr ("POSE_SESSION_NO_SEGMENT", frame);
            if (on && overrideWriteIndex >= 0) return Tr ("BETWEEN_CLIPS_WRITE_TO_BASE");
            if (on && BetweenClips.Find (clip_) == null) return Tr ("BETWEEN_CLIPS_NO_RESULT");
            // 下の補間が一定・リニアなら曲線に戻す（外部の動きはキーの近くで下の補間へ混ぜる。KeyInterpolations.SetCurveUnderExternal）
            if (on) CurveUnderExternal (new[] { new BetweenClips.Range { start = start, end = end } }, "Key Interpolation");
            BetweenClips.SetSegment (clip_, new BetweenClips.Range { start = start, end = end }, on);
            stage.InvalidateClipCurves ();
            Evaluate (this.frame);
            return null;
        }

        /// <summary>元のクリップの区間の下の補間を曲線に戻す（外部の補間にする前）。Undo は undoName で記録する（変えなければ残らない）</summary>
        internal void CurveUnderExternal (IEnumerable<BetweenClips.Range> ranges, string undoName)
        {
            System.Predicate<EditorCurveBinding> exclude = SwitchCurves ();
            ClipUndo.Record (clip_, undoName);
            int written = 0;
            foreach (BetweenClips.Range range in ranges) written += KeyInterpolations.SetCurveUnderExternal (clip_, range.start, exclude);
            if (written > 0) stage.InvalidateClipCurves ();
        }

        /// <summary>
        /// 元のクリップの、frame のキーから次のキーまでの外部の補間を、元のクリップのキーにする（S82）。区間の中の毎フレームに、
        /// 今見えている操作値（外部の結果を混ぜた後。結果のファイルにある Transform のカーブだけ）をキーとして打ち、その区間を外部の補間から外す。
        /// 結果のファイルが無くても同じ姿勢になるので、ほかのプロジェクトへ写せる。キーは Undo で戻る（区間の印は .meta なので戻らない）
        /// </summary>
        /// <returns>焼けなかった理由（焼けたら null）</returns>
        public string BakeBetween (int frame, string undoName = "Bake Between")
        {
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (!KeyInterpolations.TryGetSegment (clip_, frame, out int start, out int end)) return Tr ("POSE_SESSION_NO_SEGMENT", frame);
            BetweenClips.State state = BetweenClips.GetState (clip_, start, end);
            AnimationClip between = BetweenClips.Find (clip_);
            if (between == null || state != BetweenClips.State.Fresh && state != BetweenClips.State.Stale) return Tr ("BETWEEN_CLIPS_NOT_EXTERNAL", start, end);

            float rate = clip_.frameRate;
            Transform root = stage.editingRig.root.transform;
            // 結果のファイルにある Transform の値（パスと、位置・回転・大きさ）
            List<(string path, string property)> channels = AnimationUtility.GetCurveBindings (between)
                .Where (b => b.type == typeof (Transform))
                .Select (b => (b.path, b.propertyName.Substring (0, b.propertyName.LastIndexOf ('.'))))
                .Distinct ().ToList ();
            Dictionary<EditorCurveBinding, AnimationCurve> curves = new Dictionary<EditorCurveBinding, AnimationCurve> ();
            AnimationCurve CurveOf (EditorCurveBinding binding)
            {
                if (!curves.TryGetValue (binding, out AnimationCurve curve)) curves[binding] = curve = AnimationUtility.GetEditorCurve (clip_, binding) ?? new AnimationCurve ();
                return curve;
            }
            // 回転は前のフレームと同じ側の四元数にそろえる（補間で遠回りしない）
            Dictionary<string, Quaternion> previous = new Dictionary<string, Quaternion> ();
            for (int f = start + 1; f < end; f++) {
                float time = f / rate;
                stage.ResetControlsToRest ();
                stage.SampleBase (clip_, time);
                foreach ((string path, string property) in channels) {
                    Transform target = CurveEdit.FindPath (root, path);
                    if (target == null) continue;
                    float[] values;
                    if (property == CurveEdit.kRotation) {
                        Quaternion q = target.localRotation;
                        if (!previous.TryGetValue (path, out Quaternion before)) before = CurveEdit.EvaluateRotation (clip_, path, start / rate);
                        if (Quaternion.Dot (before, q) < 0) q = new Quaternion (-q.x, -q.y, -q.z, -q.w);
                        previous[path] = q;
                        values = new[] { q.x, q.y, q.z, q.w };
                    }
                    else {
                        Vector3 v = property == CurveEdit.kPosition ? target.localPosition : target.localScale;
                        values = new[] { v.x, v.y, v.z };
                    }
                    for (int c = 0; c < values.Length; c++) {
                        AnimationCurve curve = CurveOf (CurveEdit.TransformBinding (path, property, c));
                        int index = ClipKeyUtility.FindKeyAtFrame (curve, rate, f);
                        if (index >= 0) curve.RemoveKey (index);
                        index = curve.AddKey (new Keyframe (time, values[c]));
                        AnimationUtility.SetKeyLeftTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                        AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.ClampedAuto);
                    }
                }
            }
            ClipUndo.Record (clip_, undoName);
            foreach (AnimationCurve curve in curves.Values) CurveEdit.RefreshTangents (curve);
            AnimationUtility.SetEditorCurves (clip_, curves.Keys.ToArray (), curves.Values.ToArray ());
            BetweenClips.SetSegment (clip_, new BetweenClips.Range { start = start, end = end }, false);
            stage.InvalidateClipCurves ();
            Evaluate (this.frame);
            return null;
        }

        /// <summary>書き込み先のクリップの、frame のキーから次のキーまでの補間（混在・区間が無ければ null。S80）</summary>
        public KeyInterpolation? GetInterpolation (int frame)
        {
            return KeyInterpolations.GetSegment (targetClip, frame, SwitchCurves ());
        }

        /// <summary>書き込み先のクリップの区間と補間を全部（S80）</summary>
        public List<KeyInterpolations.Segment> GetInterpolations ()
        {
            return KeyInterpolations.GetSegments (targetClip, SwitchCurves ());
        }

        /// <summary>キャラの設定の持ち替えのカーブか（区間の補間で外す）。設定はカーブごとに引かず、1 回だけ引く</summary>
        System.Predicate<EditorCurveBinding> SwitchCurves ()
        {
            CharacterSettings settings = SettingsLookup.Find (stage.sourcePrefab);
            return ParentSwitch.SwitchCurveFilter (settings != null ? settings.parentSwitches : null);
        }

        /// <summary>
        /// キーを打つ流れの共通の後ろ半分: write で打ち、サイクルキーの印があれば最後のキーを先頭のキーに合わせ直す
        /// （窓はキーが変わった知らせで合わせる）。1 回の Undo にまとめ、そのフレームの姿勢をクリップから作り直す
        /// </summary>
        void WriteWithUndo (string undoName, System.Action write)
        {
            PoseCommit.InUndoGroup (undoName, () => {
                write ();
                AnimationClip written = targetClip;
                if (ClipCycle.Load (written)) {
                    ClipUndo.Record (written, undoName);
                    ClipCycle.Sync (written);
                }
            });
            stage.InvalidateClipCurves ();
            Evaluate (frame);
        }

        /// <summary>
        /// muscle の値（Humanoid の姿勢の一部。手の形など）を今の姿勢に重ね、その muscle が動かす骨の FK を今のフレームのキーにする
        /// （窓で PoseBank の Humanoid の姿勢を貼るのと同じ。S42）。ほかの骨・全身 IK の点のキーは変えない。1 回の Undo で戻る。
        /// 姿勢を打つ（CommitPose）と指もそのときの値でキーになるので、手の形は姿勢を打った後に打つ
        /// </summary>
        /// <returns>打てなかった理由（打てたら null）</returns>
        public string WriteMuscles (IReadOnlyDictionary<int, float> muscles, string undoName = "Write Muscles")
        {
            string problem = GetWriteProblem ();
            if (problem != null) return problem;
            Dictionary<EditorCurveBinding, float> values;
            problem = PoseCommit.CaptureMuscles (stage, targets_, muscles, frame, null, out values);
            if (problem != null) return problem;

            WriteWithUndo (undoName, () => {
                using (CurveWriter writer = BeginCurves (undoName)) {
                    foreach (KeyValuePair<EditorCurveBinding, float> pair in values) writer.SetTarget (pair.Key, pair.Value);
                }
            });
            return null;
        }

        /// <summary>
        /// ゲーム用の Humanoid のクリップを焼いて保存する（窓の Output の段で、段を通さずに焼くときと同じ。S2・S14・S25d）。
        /// 元のクリップと Override を全部重ね、キーの間で前後のキーが Locked の点は全身 IK で解く。焼き先は元のクリップの隣
        /// （HumanoidOutput.GetOutputPath。Override があれば .edited）。焼いた後、今のフレームの姿勢を作り直す
        /// </summary>
        /// <param name="outputName">焼いて書き出す名前（空なら元の名前）</param>
        /// <returns>焼けなかった理由（焼けたら null）</returns>
        public string Bake (string outputName, out string outputPath, out HumanoidBaker.Result result)
        {
            outputPath = null;
            result = default (HumanoidBaker.Result);
            string character = characterProblem;
            if (character != null) return character;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (!useHumanoidBase) {
                string problem = EditingClip.GetProblem (clip_) ?? variantProblem;
                if (problem != null) return problem;
            }
            List<AnimationClip> layers = overrides_.Where (o => o.clip != null).Select (o => o.clip).ToList ();
            if (useHumanoidBase && layers.Count == 0) return Tr ("PREVIEW_WINDOW_OUTPUT_NOTHING_TO_BAKE");

            try {
                result = stage.BakeToAsset (clip_, layers, propertyLayers, outputName, layers.Count > 0 || useHumanoidBase ? ApplyBakePose : (System.Action<float>)null,
                    stack != null ? stack.fullBodyWeight : 0, null, out outputPath);
                return null;
            }
            catch (System.Exception e) {
                return e.Message;
            }
            finally {
                stage.InvalidateClipCurves ();
                Evaluate (frame);
            }
        }

        /// <summary>焼くときに任意のプロパティへ重ねる段（全部の Override。S17）</summary>
        List<OverrideProperties.Layer> propertyLayers
        {
            get {
                return overrides_.Where (o => o.clip != null)
                    .Select (o => new OverrideProperties.Layer { clip = o.clip, mode = o.mode, weight = 1, mask = o.mask }).ToList ();
            }
        }

        /// <summary>焼くときの、そのフレームの操作値（元＋全部の Override。窓の ApplyBakePose と同じ）</summary>
        void ApplyBakePose (float time)
        {
            stage.ApplyBakePose (clip_, useHumanoidBase, propertyLayers, time);
        }

        /// <summary>取り込み（ImportOverride）の結果</summary>
        [System.Serializable]
        public sealed class OverrideImportResult
        {
            public int frameCount;
            /// <summary>間引いた後の Override のキーの数と、間引く前の数</summary>
            public int keyCount;
            public int keyCountBeforeReduce;
            /// <summary>足を床に付けたフレームの数（lockFeet のとき）</summary>
            public int lockedFrames;
            /// <summary>元のクリップのキーのフレームで、取り込んだ動きが元の姿勢からどれだけずれていたか（寄せる前。S75）</summary>
            public List<KeyDeviation> keyDeviations = new List<KeyDeviation> ();
            public List<string> notes = new List<string> ();
        }

        /// <summary>キーのフレームでの、取り込んだ動きと元の姿勢のずれ（骨の場所の差の最大と、腰の場所の差。メートル）</summary>
        [System.Serializable]
        public sealed class KeyDeviation
        {
            public int frame;
            public float distance;
            public string bone;
            public float hips;
        }

        /// <summary>取り込み（ImportOverride）の指定</summary>
        public sealed class OverrideImportOptions
        {
            /// <summary>足が低くて止まっている区間を床に留める（足が滑る入力のため。足を留めている元に掛けると跳ねる）</summary>
            public bool lockFeet;
            /// <summary>指のコントロールも取り込む（false なら元の手の形を残す。S74）</summary>
            public bool fingers = true;
            /// <summary>BVH: 最初のフレームの体の向きと地面の上の場所を元に合わせる（S74）</summary>
            public bool alignRoot = true;
            /// <summary>元のクリップのキーのフレームでは差を 0 にし（元のキーの姿勢）、前後 keepKeysBlend 秒でなめらかに差を戻す（S75）</summary>
            public bool keepKeys;
            public float keepKeysBlend = 0.25f;
        }

        /// <summary>
        /// 外で作った Humanoid のクリップ（学習済みモデルの出力など）を、今の元のクリップに重ねる Override として destination へ書く（S40）。
        /// 元のクリップは変えない。destination の編集用リグのカーブは置き換える。段の並びには足さない（呼ぶ側で AddOverride する）。
        /// 1. 外のクリップを編集用リグの値にする（HumanoidImport。ルートの移動は腰へ移す。手足は FK）
        /// 2. lockFeet なら、足の点が低くて止まっている区間を接地とみなし、その間に足が滑っている・床に沈んでいる区間だけ、
        ///    最初の場所（床より下なら床の上）に点を留めて、その脚だけを全身 IK で解き直す。
        ///    動画から取った動きのように足が滑る入力のためのもの。足がもう留まっている動きはそのまま
        /// 3. 元のクリップの値との差分（Additive）か置き換える値（Override）にして書き、変わらないキーを間引く
        /// </summary>
        /// <returns>取り込めなかった理由（取り込めたら null）</returns>
        public string ImportOverride (AnimationClip source, AnimationClip destination, OverrideMode mode, OverrideImportOptions options, out OverrideImportResult result)
        {
            return new OverrideImporter (this).Import (source, destination, mode, options, out result);
        }

        /// <summary>
        /// 骨を直接動かす Generic のクリップを、今の編集用クリップの値へ取り込む（S93）。そのクリップの編集用リグのカーブは置き換える。
        /// カーブの無い骨は基準姿勢のまま。段は通さない（窓の Generic Pose の段と違い、今のクリップの値と合成しない）
        /// </summary>
        /// <returns>取り込めなかった理由（取り込めたら null）</returns>
        public string ImportGeneric (AnimationClip source, out PoseImport.Result result)
        {
            result = default;
            string character = characterProblem;
            if (character != null) return character;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            string problem = GenericImport.GetProblem (source);
            if (problem != null) return problem;
            try {
                ClipUndo.Record (clip_, "Import Generic Clip");
                result = GenericImport.Import (source, clip_, stage.editingRig, stage.editingRigSolver, time => {
                    stage.editingRig.ResetBonesToRest ();
                    stage.BlendGenericClip (source, time, 1);
                }, targets_);
                return null;
            }
            catch (System.Exception e) {
                return e.Message;
            }
            finally {
                RestoreFrame ();
            }
        }

        /// <summary>取り込みの計算で動かした体を、今のフレームの姿勢へ戻す</summary>
        internal void RestoreFrame ()
        {
            stage.InvalidateClipCurves ();
            Evaluate (frame);
        }

        /// <summary>
        /// キーの姿勢を外部の骨格（学習済みモデルの骨など）へ写して BVH にする（S73）。姿勢は焼いた後のもの（Humanoid の可動範囲で丸めた後）で、
        /// 外部の骨格の Avatar に Humanoid のまま当てるので、体型の違いは Humanoid が合わせる。BVH の 1 フレームが frames の 1 つ（1 フレームの秒数はクリップの格子）。
        /// frames を省くとキーを打つクリップのキーのフレーム。終わったら今のフレームの姿勢へ戻す
        /// </summary>
        /// <returns>書き出せなかった理由（書き出せたら null）</returns>
        public string ExportToSkeleton (ExternalSkeleton skeleton, IList<int> frames, out Bvh bvh, out List<int> exported)
        {
            bvh = null;
            exported = new List<int> ();
            string character = characterProblem;
            if (character != null) return character;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (skeleton == null) return Tr ("EXTERNAL_SKELETON_NO_JOINTS");
            if (!stage.canPreviewHumanoid) return Tr ("POSE_SESSION_NO_CHARACTER");
            exported = frames != null && frames.Count > 0 ? frames.Distinct ().OrderBy (f => f).ToList () : ClipCycle.GetFrames (targetClip).ToList ();
            if (exported.Count == 0) return Tr ("POSE_SESSION_NO_KEYS_TO_EXPORT");
            if (exported[0] < 0) return Tr ("MOTION_EDITOR_COMMANDS_NEGATIVE_FRAME");

            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            if (instance == null) return error;
            int current = frame;
            try {
                Bvh result = skeleton.CreateBvh (1 / frameRate);
                HumanPose pose = new HumanPose ();
                foreach (int f in exported) {
                    Evaluate (f);
                    if (!stage.ReadHumanPose (ref pose)) return Tr ("POSE_SESSION_NO_CHARACTER");
                    instance.handler.SetHumanPose (ref pose);
                    skeleton.Capture (instance, result);
                }
                bvh = result;
                return null;
            }
            finally {
                instance.Dispose ();
                Evaluate (current);
            }
        }

        /// <summary>
        /// 外部の骨格の BVH を、元のクリップの外部の補間の結果（BetweenClips。`<名前>.between.anim`）として書き、segments の区間に使う印を付ける（S81）。
        /// 結果は編集用リグの値そのもの（Override の置き換えと同じ値）。指は書かない（打った手の形を残す）。最初のフレームの体は元のクリップに合わせる（alignRoot）。
        /// 区間は元のクリップの隣り合うキーの間であること。結果のファイルの書き換えは Undo で戻る（区間の印は .meta なので戻らない）。
        /// 計算を少しずつ回すときは BeginImportBetween（結果は同じ）
        /// </summary>
        /// <returns>書けなかった理由（書けたら null）</returns>
        public string ImportBetween (Bvh bvh, ExternalSkeleton skeleton, IList<BetweenClips.Range> segments, out OverrideImportResult result)
        {
            result = null;
            using (BetweenImport import = BeginImportBetween (bvh, skeleton, segments, out string problem)) {
                if (problem != null) return problem;
                return import.Commit (segments, out result);
            }
        }

        /// <summary>
        /// 少しずつ回す ImportBetween（BeginImportBetween）。Step をエディタの更新ごとに呼び、終わったら（true）Commit で結果のファイルへ書く。
        /// 計算の間はこのセッションの中の体を動かすだけで、アセットは書かない（書くのは Commit の 1 回。Undo もそこで 1 回）。
        /// 取り込みは 10 秒の動きで 0.5 秒ほどかかり、一度に回すとその間エディタが止まるので分けた（10-30）。やめるときは Dispose
        /// </summary>
        public sealed class BetweenImport : System.IDisposable
        {
            readonly OverrideImporter importer_;
            readonly Stepper stepper_;
            readonly OverrideImporter.ComputedOverride computed_;

            internal BetweenImport (OverrideImporter importer, IEnumerator routine, OverrideImporter.ComputedOverride computed)
            {
                importer_ = importer;
                stepper_ = new Stepper (routine);
                computed_ = computed;
            }

            /// <summary>計算が終わったか（できなかったときも）</summary>
            public bool done
            {
                get { return stepper_.done; }
            }

            /// <summary>seconds 秒ぶん計算を進める。終わった（できなかったときも）なら true</summary>
            public bool Step (double seconds)
            {
                if (stepper_.done) return true;
                try {
                    return stepper_.Step (seconds);
                }
                catch (System.Exception e) {
                    computed_.error = e.Message;
                    return true;
                }
            }

            /// <summary>
            /// 計算した動きを結果のファイルへ書き、segments を外部の動きの区間にする。計算が残っていれば先に回し切る。
            /// segments は始めたときと違ってよい（計算の間にキーが動いたら、呼ぶ側が使える区間に絞る）
            /// </summary>
            /// <returns>書けなかった理由（書けたら null）</returns>
            public string Commit (IList<BetweenClips.Range> segments, out OverrideImportResult result)
            {
                Step (double.PositiveInfinity);
                return importer_.CommitBetween (computed_, segments, out result);
            }

            public void Dispose ()
            {
                stepper_.Dispose ();
            }
        }

        /// <summary>ImportBetween の計算を始める（Step で少しずつ回し、Commit で書く）。始められなければ null と理由</summary>
        public BetweenImport BeginImportBetween (Bvh bvh, ExternalSkeleton skeleton, IList<BetweenClips.Range> segments, out string problem)
        {
            return new OverrideImporter (this).BeginBetween (bvh, skeleton, segments, out problem);
        }

        /// <summary>
        /// 外部の骨格（skeleton）の BVH を、今の元のクリップに重ねる Override として destination へ書く（S74）。
        /// BVH の各フレームを外部の骨格の Avatar で Humanoid の姿勢にし、一時の Humanoid のクリップ（RootT・RootQ・muscle）にして、
        /// .anim と同じ取り込み（ImportOverride）へ流す。指は既定で取り込まない（モデルの骨格に指の関節が無いことが多く、打った手の形を残す）。
        /// alignRoot なら、最初のフレームの体の向き（鉛直軸まわり）と地面の上の場所を、元のクリップの最初のフレームへ合わせる（クリップ全体で 1 回。
        /// Kimodo などは最初の腰を原点・+Z 向きにそろえて出すため。キーごとに寄せると腰が速く動いて足が滑る）
        /// </summary>
        /// <param name="options">null なら指は取り込まない</param>
        public string ImportOverride (Bvh bvh, ExternalSkeleton skeleton, AnimationClip destination, OverrideMode mode, OverrideImportOptions options, out OverrideImportResult result)
        {
            return new OverrideImporter (this).Import (bvh, skeleton, destination, mode, options, out result);
        }

        /// <summary>書き換えたクリップ（元と Override）をディスクへ保存する。保存したパスを返す</summary>
        public List<string> Save ()
        {
            List<string> saved = new List<string> ();
            foreach (AnimationClip target in new[] { clip_ }.Concat (overrides_.Select (o => o.clip))) {
                if (target == null || !EditorUtility.IsPersistent (target)) continue;
                AssetDatabase.SaveAssetIfDirty (target);
                saved.Add (AssetDatabase.GetAssetPath (target));
            }
            return saved;
        }

        /// <summary>
        /// 段が読むクリップと Override の並び（窓の代わり）。Layers のパネルの行に出す窓の操作（取込・焼く・差し替え）は持たないので、
        /// 段は行を作らない。Generic Pose・Humanoid Pose・Output の段は置かない
        /// </summary>
        sealed class Host : ILayerSource
        {
            readonly PoseSession session_;

            public Host (PoseSession session)
            {
                session_ = session;
            }

            public AnimationClip clip { get { return session_.clip_; } }
            public bool useHumanoidBase { get { return session_.useHumanoidBase; } }
            public IReadOnlyList<OverrideClip> overrides { get { return session_.overrides_; } }
            public int overrideWriteIndex { get { return session_.overrideWriteIndex; } }
            public AnimationClip genericClip { get { return null; } }
            public AnimationClip humanoidClip { get { return null; } }
            public bool humanoidPoseLayer { get { return false; } }
            public IReadOnlyList<OutputEntry> outputEntries { get { return null; } }
        }
    }

}
