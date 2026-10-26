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
    /// Humanoid Pose・Output・Timeline Clip の段は置かない（見比べる参照・焼いて書き出す口・演出は窓の物）。
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
        {
            this.definition = definition;
            stage = render ? new PreviewStage (prefab, definition) : PreviewStage.CreateHeadless (prefab, definition);
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

        /// <summary>今のフレームにキーを打てない理由（打てるなら null）。窓の CanEditClip と同じ条件</summary>
        public string GetWriteProblem ()
        {
            string character = characterProblem;
            if (character != null) return character;
            AnimationClip target = targetClip;
            if (target == null) return Tr ("POSE_SESSION_NO_CLIP");
            // 元のクリップへ書くときだけ、そのクリップが編集できる形かを見る（Override へ書くときの土台は Humanoid でもよい）
            if (writeOverride == null) {
                string problem = EditingClip.GetProblem (clip_);
                if (problem != null) return problem;
            }
            PoseLayer write = stack.writeLayer;
            if (write == null) return Tr ("POSE_STACK_CANNOT_WRITE");
            string reason;
            if (!stack.CanWrite (write, out reason)) return reason ?? Tr ("POSE_STACK_CANNOT_WRITE");
            if (write.clipWeight < 1) return Tr ("POSE_SESSION_BLENDING");
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
            OverrideLayer layer = writeOverride;
            int keyFrame = PoseCommit.KeyFrameToWrite (frame, ClipCycle.Load (target), ClipCycle.GetFrames (target), layer != null);
            CurveWriter writer = CurveWriter.Begin (target, stage.editingRig.root.transform, keyFrame, undoName);
            if (layer != null) PoseCommit.AttachOverrideStore (writer, stage, layer.mode, layer.mask);
            return writer;
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
            if (clip_ == null || !KeyInterpolations.TryGetSegment (clip_, frame, out int start, out int end)) return BetweenClips.State.None;
            return BetweenClips.GetState (clip_, start, end);
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
        void CurveUnderExternal (IEnumerable<BetweenClips.Range> ranges, string undoName)
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
            if (settings == null || settings.parentSwitches == null || settings.parentSwitches.Count == 0) return null;
            return binding => ParentSwitch.IsSwitchCurve (settings.parentSwitches, binding);
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
                string problem = EditingClip.GetProblem (clip_);
                if (problem != null) return problem;
            }
            List<AnimationClip> layers = overrides_.Where (o => o.clip != null).Select (o => o.clip).ToList ();
            if (useHumanoidBase && layers.Count == 0) return Tr ("PREVIEW_WINDOW_OUTPUT_NOTHING_TO_BAKE");

            PoseLayer fullBody = stack != null ? stack.Find (LayerKind.FullBodyIk) : null;
            float fullBodyWeight = fullBody != null && fullBody.active ? fullBody.weight : 0;
            AnimationClip properties = null;
            try {
                using (HumanoidBaker baker = stage.CreateHumanoidBaker (layers.Count > 0 || useHumanoidBase ? ApplyBakePose : (System.Action<float>)null)) {
                    baker.afterSolve = () => stage.SolveLockedSpan (fullBodyWeight);
                    properties = OverrideProperties.CreateBakeSource (clip_, propertyLayers, stage);
                    baker.propertySource = properties;
                    result = HumanoidOutput.BakeToAsset (baker, clip_, stage.sourcePrefab, layers, outputName, out outputPath);
                }
                return null;
            }
            catch (System.Exception e) {
                return e.Message;
            }
            finally {
                if (properties != null) Object.DestroyImmediate (properties);
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

        /// <summary>接地とみなす足の点の高さと 1 フレームの水平の動き（メートル。体の大きさ 1 のとき）</summary>
        const float kContactHeight = 0.03f;
        const float kContactSpeed = 0.02f;
        /// <summary>接地の区間の中で、足の点が最初の場所からこれ以上動いた（か床に沈んだ）区間だけ留める（メートル。体の大きさ 1 のとき）</summary>
        const float kContactDrift = 0.005f;
        /// <summary>間引くときの許し（四元数の成分で 0.06° 程度・位置で 0.5mm 程度）</summary>
        const float kReduceTolerance = 5e-4f;

        /// <summary>
        /// 外で作った Humanoid のクリップ（学習済みモデルの出力など）を、今の元のクリップに重ねる Override として destination へ書く（S40）。
        /// 元のクリップは変えない。destination の編集用リグのカーブは置き換える。段の並びには足さない（呼ぶ側で AddOverride する）。
        /// 1. 外のクリップを編集用リグの値にする（HumanoidImport。ルートの移動は腰へ移す。手足は FK）
        /// 2. lockFeet なら、足の点が低くて止まっている区間を接地とみなし、その間に足が滑っている・床に沈んでいる区間だけ、
        ///    最初の場所（床より下なら床の上）に点を留めて、その脚だけを全身 IK で解き直す。
        ///    動画から取った動きのように足が滑る入力のためのもの。足がもう留まっている動きはそのまま
        /// 3. 元のクリップの値との差分（Additive）か置き換える値（Override）にして書き、変わらないキーを間引く
        /// </summary>
        /// <param name="fingers">指のコントロールも取り込むか。false なら指のカーブを書かず、元の手の形を残す（S74）</param>
        /// <returns>取り込めなかった理由（取り込めたら null）</returns>
        public string ImportOverride (AnimationClip source, AnimationClip destination, OverrideMode mode, bool lockFeet, out OverrideImportResult result, bool fingers = true)
        {
            return ImportOverride (source, destination, mode, new OverrideImportOptions { lockFeet = lockFeet, fingers = fingers }, out result);
        }

        public string ImportOverride (AnimationClip source, AnimationClip destination, OverrideMode mode, OverrideImportOptions options, out OverrideImportResult result)
        {
            result = new OverrideImportResult ();
            if (options == null) options = new OverrideImportOptions ();
            bool lockFeet = options.lockFeet;
            string character = characterProblem;
            if (character != null) return character;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            string problem = HumanoidImport.GetProblem (source);
            if (problem != null) return problem;
            if (destination == null || destination == clip_ || destination == source) return Tr ("POSE_SESSION_BAD_IMPORT_DESTINATION");
            if (lockFeet && stage.fullBody == null) return Tr ("POSE_DOCUMENT_NO_POINTS");

            EditingRig rig = stage.editingRig;
            Transform root = rig.root.transform;
            float rate = CurveEdit.FrameRateOf (clip_);
            AnimationClip imported = new AnimationClip { frameRate = rate };
            List<PoseTarget> importTargets = RigTargets.Create (rig, stage.editingRigSolver);
            try {
                // 1. 外のクリップを編集用リグの値にする（表示モデルへ当てて、編集用の体へ写して捉える）。
                //    ルートの移動は HumanoidImport が腰へ移すので、表示モデルの置き場所は動かさない（当てるたびに戻す）
                Transform display = stage.animator.transform;
                display.GetLocalPositionAndRotation (out Vector3 displayPosition, out Quaternion displayRotation);
                using (ClipSampler sampler = new ClipSampler (stage.animator)) {
                    PoseImport.Result imported1 = HumanoidImport.Import (source, imported, rig, stage.editingRigSolver, time => {
                        sampler.Sample (source, time);
                        display.SetLocalPositionAndRotation (displayPosition, displayRotation);
                    }, importTargets);
                    result.frameCount = imported1.frameCount;
                    if (imported1.notes != null) result.notes.AddRange (imported1.notes);
                }
                // 元のクリップのほうが長ければ、その長さまで書く（取り込み元より後ろは下で差 0 にする。書かないとカーブの最後の差が残る）
                result.frameCount = Mathf.Max (result.frameCount, CurveEdit.FrameCount (clip_.empty ? 0 : clip_.length, rate));
                Dictionary<EditorCurveBinding, float[]> target = ReadCurves (imported, result.frameCount, rate);
                if (!options.fingers) {
                    HashSet<string> fingerPaths = new HashSet<string> (rig.binding.boneControls.Where (c => c.human.HasValue && IsFinger (c.human.Value)).Select (c => c.path));
                    foreach (EditorCurveBinding binding in target.Keys.Where (b => fingerPaths.Contains (b.path)).ToList ()) target.Remove (binding);
                }

                // 2. 足を床に付ける
                if (lockFeet) result.lockedFrames = LockFeet (imported, target, result.frameCount, rate);

                // 3. 元のクリップとの差分にする。取り込み元より後ろのフレームは元のまま（差 0）にする（最後の姿勢の差を残さない）。
                //    keepKeys なら、元のキーのフレームで差 0、前後でなめらかに差を戻す（重みは差分にする前の狙いの値に掛ける）
                int sourceFrames = Mathf.Clamp (Mathf.RoundToInt ((source.empty ? 0 : source.length) * rate) + 1, 1, result.frameCount);
                int[] baseKeys = ClipCycle.GetFrames (clip_).Where (k => k >= 0 && k < sourceFrames).ToArray ();
                result.keyDeviations = MeasureKeyDeviations (imported, baseKeys, rate, options.fingers);
                float[] weights = new float[result.frameCount];
                for (int f = 0; f < result.frameCount; f++) {
                    weights[f] = f >= sourceFrames ? 0 : options.keepKeys ? KeepKeysWeight (f, baseKeys, options.keepKeysBlend * rate) : 1;
                }
                Dictionary<EditorCurveBinding, float[]> stored = new Dictionary<EditorCurveBinding, float[]> ();
                foreach (EditorCurveBinding binding in target.Keys) stored[binding] = new float[result.frameCount];
                List<IGrouping<string, EditorCurveBinding>> transforms = target.Keys.Where (b => b.type == typeof (Transform)).GroupBy (b => b.path + "|" + b.propertyName.Substring (0, b.propertyName.LastIndexOf ('.'))).ToList ();
                for (int f = 0; f < result.frameCount; f++) {
                    stage.SampleBaseAsControls (clip_, useHumanoidBase, f / rate);
                    foreach (IGrouping<string, EditorCurveBinding> group in transforms) StoreTransform (group, root.gameObject, target, stored, f, mode, weights[f]);
                    // 数値（点の固定の強さ・Rig の代理の重み）は足し合わせず、Additive でも置き換える
                    foreach (EditorCurveBinding binding in target.Keys) {
                        if (binding.type == typeof (Transform)) continue;
                        float value = target[binding][f];
                        if (weights[f] < 1 && AnimationUtility.GetFloatValue (root.gameObject, binding, out float baseValue)) value = Mathf.Lerp (baseValue, value, weights[f]);
                        stored[binding][f] = value;
                    }
                }
                HashSet<string> rotationPaths = new HashSet<string> (target.Keys.Where (b => b.propertyName.StartsWith ("m_LocalRotation.")).Select (b => b.path));
                PoseImport.WriteCollected (destination, stored, rotationPaths, result.frameCount, rate);
                result.keyCountBeforeReduce = CountKeys (destination);
                OverrideMerge.ReduceKeys (destination, kReduceTolerance);
                result.keyCount = CountKeys (destination);
                OverrideApplier.Invalidate (destination);
                return null;
            }
            catch (System.Exception e) {
                return e.Message;
            }
            finally {
                foreach (PoseTarget t in importTargets) t.Dispose ();
                Object.DestroyImmediate (imported);
                stage.InvalidateClipCurves ();
                Evaluate (frame);
            }
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

        /// <summary>編集用リグのカーブをフレームごとの値にする</summary>
        static Dictionary<EditorCurveBinding, float[]> ReadCurves (AnimationClip clip, int frameCount, float rate)
        {
            Dictionary<EditorCurveBinding, float[]> values = new Dictionary<EditorCurveBinding, float[]> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!EditingClip.IsRigBinding (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                float[] array = new float[frameCount];
                for (int f = 0; f < frameCount; f++) array[f] = curve.Evaluate (f / rate);
                values[binding] = array;
            }
            return values;
        }

        /// <summary>
        /// 外部の骨格の BVH を、元のクリップの外部の補間の結果（BetweenClips。`<名前>.between.anim`）として書き、segments の区間に使う印を付ける（S81）。
        /// 結果は編集用リグの値そのもの（Override の置き換えと同じ値）。指は書かない（打った手の形を残す）。最初のフレームの体は元のクリップに合わせる（alignRoot）。
        /// 区間は元のクリップの隣り合うキーの間であること。結果のファイルの書き換えは Undo で戻る（区間の印は .meta なので戻らない）
        /// </summary>
        /// <returns>書けなかった理由（書けたら null）</returns>
        public string ImportBetween (Bvh bvh, ExternalSkeleton skeleton, IList<BetweenClips.Range> segments, out OverrideImportResult result)
        {
            result = null;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            string path = BetweenClips.PathFor (clip_);
            if (path == null) return Tr ("BETWEEN_CLIPS_ONLY_SAVED_CLIP");
            if (segments == null || segments.Count == 0) return Tr ("BETWEEN_CLIPS_NO_SEGMENTS");
            foreach (BetweenClips.Range range in segments) {
                string problem = BetweenClips.GetSegmentProblem (clip_, range);
                if (problem != null) return problem;
            }
            AnimationClip between = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            bool created = between == null;
            if (created) {
                between = new AnimationClip { frameRate = clip_.frameRate };
                AssetDatabase.CreateAsset (between, path);
            }
            else {
                ClipUndo.Record (between, "Import Between");
            }
            OverrideImportOptions options = new OverrideImportOptions { fingers = false, alignRoot = true };
            string error = ImportOverride (bvh, skeleton, between, OverrideMode.Override, options, out result);
            if (error != null) {
                if (created) AssetDatabase.DeleteAsset (path);
                return error;
            }
            AssetDatabase.SaveAssetIfDirty (between);
            // 区間の指紋はキーを足した後で取る（下の補間を曲線に戻すと、端にキーの無いカーブにキーが足される）
            CurveUnderExternal (segments, "Import Between");
            BetweenClips.SaveSegments (between, clip_, segments, BetweenClips.MakeStamps (clip_));
            stage.InvalidateClipCurves ();
            Evaluate (frame);
            return null;
        }

        /// <summary>
        /// 外部の骨格（skeleton）の BVH を、今の元のクリップに重ねる Override として destination へ書く（S74）。
        /// BVH の各フレームを外部の骨格の Avatar で Humanoid の姿勢にし、一時の Humanoid のクリップ（RootT・RootQ・muscle）にして、
        /// .anim と同じ取り込み（ImportOverride）へ流す。指は既定で取り込まない（モデルの骨格に指の関節が無いことが多く、打った手の形を残す）。
        /// alignRoot なら、最初のフレームの体の向き（鉛直軸まわり）と地面の上の場所を、元のクリップの最初のフレームへ合わせる（クリップ全体で 1 回。
        /// Kimodo などは最初の腰を原点・+Z 向きにそろえて出すため。キーごとに寄せると腰が速く動いて足が滑る）
        /// </summary>
        public string ImportOverride (Bvh bvh, ExternalSkeleton skeleton, AnimationClip destination, OverrideMode mode, bool lockFeet, bool alignRoot, bool fingers,
            out OverrideImportResult result)
        {
            return ImportOverride (bvh, skeleton, destination, mode, new OverrideImportOptions { lockFeet = lockFeet, alignRoot = alignRoot, fingers = fingers }, out result);
        }

        public string ImportOverride (Bvh bvh, ExternalSkeleton skeleton, AnimationClip destination, OverrideMode mode, OverrideImportOptions options, out OverrideImportResult result)
        {
            result = new OverrideImportResult ();
            if (options == null) options = new OverrideImportOptions { fingers = false };
            bool alignRoot = options.alignRoot;
            string character = characterProblem;
            if (character != null) return character;
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (bvh == null || bvh.frames.Count == 0) return Tr ("POSE_SESSION_EMPTY_BVH");
            if (skeleton == null) return Tr ("EXTERNAL_SKELETON_NO_JOINTS");
            string mismatch = skeleton.GetMismatch (bvh);
            if (mismatch != null) return mismatch;
            if (!stage.canPreviewHumanoid) return Tr ("POSE_SESSION_NO_CHARACTER");

            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            if (instance == null) return error;
            AnimationClip humanoid = null;
            int current = frame;
            try {
                // 元の最初のフレームの体の置き場所（焼いた後の姿勢。体の大きさで割った値）
                HumanPose start = new HumanPose ();
                if (alignRoot) {
                    Evaluate (0);
                    if (!stage.ReadHumanPose (ref start)) return Tr ("POSE_SESSION_NO_CHARACTER");
                }
                HumanPose pose = new HumanPose ();
                Vector3 offsetPosition = Vector3.zero;
                Quaternion offsetRotation = Quaternion.identity;
                int muscleCount = HumanTrait.MuscleCount;
                List<Keyframe>[] keys = new List<Keyframe>[7 + muscleCount];
                for (int c = 0; c < keys.Length; c++) keys[c] = new List<Keyframe> ();
                Quaternion previous = Quaternion.identity;
                for (int f = 0; f < bvh.frames.Count; f++) {
                    skeleton.Apply (instance, bvh, f);
                    instance.handler.GetHumanPose (ref pose);
                    if (f == 0 && alignRoot) {
                        Vector3 forward = start.bodyRotation * Quaternion.Inverse (pose.bodyRotation) * Vector3.forward;
                        forward.y = 0;
                        if (forward.sqrMagnitude > 1e-8f) offsetRotation = Quaternion.LookRotation (forward);
                        Vector3 delta = start.bodyPosition - offsetRotation * pose.bodyPosition;
                        offsetPosition = new Vector3 (delta.x, 0, delta.z);
                    }
                    Vector3 position = offsetPosition + offsetRotation * pose.bodyPosition;
                    Quaternion rotation = offsetRotation * pose.bodyRotation;
                    // 隣のフレームと同じ半球にそろえる（成分ごとに補間するので、符号が飛ぶと間で回る）
                    if (f > 0 && Quaternion.Dot (previous, rotation) < 0) rotation = new Quaternion (-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previous = rotation;
                    float time = f * bvh.frameTime;
                    float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
                    for (int c = 0; c < 7; c++) keys[c].Add (new Keyframe (time, values[c]));
                    for (int m = 0; m < muscleCount; m++) keys[7 + m].Add (new Keyframe (time, pose.muscles[m]));
                }
                humanoid = new AnimationClip { frameRate = 1 / bvh.frameTime, hideFlags = HideFlags.HideAndDontSave };
                string[] axes = { "x", "y", "z", "w" };
                for (int c = 0; c < keys.Length; c++) {
                    string name = c < 3 ? HumanoidBaker.kRootT + "." + axes[c] : c < 7 ? HumanoidBaker.kRootQ + "." + axes[c - 3] : HumanoidBaker.clipMuscleNames[c - 7];
                    AnimationCurve curve = new AnimationCurve (keys[c].ToArray ());
                    for (int k = 0; k < curve.length; k++) {
                        AnimationUtility.SetKeyLeftTangentMode (curve, k, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode (curve, k, AnimationUtility.TangentMode.Linear);
                    }
                    AnimationUtility.SetEditorCurve (humanoid, EditorCurveBinding.FloatCurve ("", typeof (Animator), name), curve);
                }
            }
            finally {
                instance.Dispose ();
                Evaluate (current);
            }
            try {
                return ImportOverride (humanoid, destination, mode, options, out result);
            }
            finally {
                Object.DestroyImmediate (humanoid);
            }
        }

        /// <summary>
        /// keepKeys の重み: キーのフレームで 0、キーから blendFrames 離れると 1（smoothstep。速さが途切れない）。いちばん近いキーで決める
        /// </summary>
        static float KeepKeysWeight (int f, int[] keys, float blendFrames)
        {
            float weight = 1;
            foreach (int k in keys) {
                float d = Mathf.Abs (f - k);
                float w = blendFrames <= 0 ? (d < 0.5f ? 0 : 1) : Mathf.Clamp01 (d / blendFrames);
                weight = Mathf.Min (weight, w * w * (3 - 2 * w));
            }
            return weight;
        }

        /// <summary>
        /// 元のクリップのキーのフレームで、取り込んだ動き（編集用リグの値）と元の姿勢の、骨の場所の差（S75。寄せる前）。
        /// 指を取り込まないときは、指とその先の骨を測らない（重ねても元の手の形のままなので）
        /// </summary>
        List<KeyDeviation> MeasureKeyDeviations (AnimationClip imported, int[] keys, float rate, bool fingers)
        {
            List<KeyDeviation> deviations = new List<KeyDeviation> ();
            EditingRig rig = stage.editingRig;
            HashSet<Transform> fingerBones = new HashSet<Transform> ();
            if (!fingers) {
                for (HumanBodyBones b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++) {
                    Transform t = rig.GetEditingBone (b);
                    if (t != null) fingerBones.Add (t);
                }
            }
            bool IsFingerBone (Transform t)
            {
                for (; t != null; t = t.parent) {
                    if (fingerBones.Contains (t)) return true;
                }
                return false;
            }
            List<Transform> bones = rig.editingBones.Where (t => !IsFingerBone (t)).ToList ();
            Transform hips = rig.GetEditingBone (HumanBodyBones.Hips);
            Vector3[] before = new Vector3[bones.Count];
            foreach (int k in keys) {
                Evaluate (k);
                for (int b = 0; b < bones.Count; b++) before[b] = bones[b].position;
                Vector3 baseHips = hips != null ? hips.position : Vector3.zero;
                ApplyImported (imported, k / rate);
                KeyDeviation deviation = new KeyDeviation { frame = k, bone = "" };
                for (int b = 0; b < bones.Count; b++) {
                    float d = Vector3.Distance (before[b], bones[b].position);
                    if (d > deviation.distance) {
                        deviation.distance = d;
                        deviation.bone = bones[b].name;
                    }
                }
                deviation.hips = hips != null ? Vector3.Distance (baseHips, hips.position) : 0;
                deviations.Add (deviation);
            }
            return deviations;
        }

        static bool IsFinger (HumanBodyBones bone)
        {
            return bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.RightLittleDistal;
        }

        /// <summary>
        /// 位置・回転・大きさのどれか 1 組（x/y/z[/w]）を、今のコントロールの値（元）との差分にする。
        /// weight は狙いの値の効き（0 なら元の値のまま＝差 0。回転は Slerp、位置と大きさは Lerp で元と混ぜてから差分にする）
        /// </summary>
        static void StoreTransform (IGrouping<string, EditorCurveBinding> group, GameObject root, Dictionary<EditorCurveBinding, float[]> target,
            Dictionary<EditorCurveBinding, float[]> stored, int f, OverrideMode mode, float weight = 1)
        {
            string[] axes = { "x", "y", "z", "w" };
            EditorCurveBinding first = group.First ();
            string prefix = first.propertyName.Substring (0, first.propertyName.LastIndexOf ('.') + 1);
            int count = prefix == "m_LocalRotation." ? 4 : 3;
            EditorCurveBinding[] bindings = new EditorCurveBinding[count];
            float[] source = new float[4];
            float[] value = new float[4];
            for (int i = 0; i < count; i++) {
                bindings[i] = EditorCurveBinding.FloatCurve (first.path, typeof (Transform), prefix + axes[i]);
                if (!AnimationUtility.GetFloatValue (root, bindings[i], out source[i])) source[i] = 0;
                // 組の一部しかカーブが無いときは、無い成分を元の値のままにする
                float[] array;
                value[i] = weight > 0 && target.TryGetValue (bindings[i], out array) ? array[f] : source[i];
            }
            float[] result = new float[4];
            if (count == 4) {
                Quaternion s = Quaternion.Normalize (new Quaternion (source[0], source[1], source[2], source[3]));
                Quaternion v = Quaternion.Normalize (new Quaternion (value[0], value[1], value[2], value[3]));
                Quaternion q = OverrideMath.StoreRotation (mode, s, weight < 1 ? Quaternion.Slerp (s, v, weight) : v);
                result[0] = q.x; result[1] = q.y; result[2] = q.z; result[3] = q.w;
            }
            else {
                Vector3 s = new Vector3 (source[0], source[1], source[2]);
                Vector3 v = Vector3.Lerp (s, new Vector3 (value[0], value[1], value[2]), weight);
                Vector3 r = prefix == "m_LocalScale." ? OverrideMath.StoreScale (mode, s, v) : OverrideMath.StorePosition (mode, s, v);
                result[0] = r.x; result[1] = r.y; result[2] = r.z;
            }
            for (int i = 0; i < count; i++) {
                float[] array;
                if (!stored.TryGetValue (bindings[i], out array)) stored[bindings[i]] = array = new float[stored.Values.First ().Length];
                array[f] = result[i];
            }
        }

        /// <summary>
        /// 足の点が低くて止まっている区間を接地とみなし、その区間の最初の場所（床より下なら床の上）に点を留めて全身 IK で解き直す。
        /// 点ごとに見るので、かかとが先に上がりつま先が残る、のような足の動きも残る。target（編集用リグの値）の該当フレームを書き換える。
        /// 解き直したフレームの数を返す
        /// </summary>
        int LockFeet (AnimationClip imported, Dictionary<EditorCurveBinding, float[]> target, int frameCount, float rate)
        {
            EditingRig rig = stage.editingRig;
            FullBodyRig body = stage.fullBody;
            float scale = rig.humanScale;
            List<BodyPointId> points = body.pointIds.Where (body.IsContact).ToList ();

            // 足の点の場所（フレームごと）
            Vector3[,] positions = new Vector3[frameCount, points.Count];
            for (int f = 0; f < frameCount; f++) {
                ApplyImported (imported, f / rate);
                for (int p = 0; p < points.Count; p++) {
                    body.TryGetBonePointWorld (points[p], out positions[f, p]);
                    // 接地は、床との当たりの球（定義の radius）の下の端の高さで見る
                    positions[f, p].y -= body.GetRadius (points[p]);
                }
            }
            // 接地の区間と、留める場所
            Vector3?[,] anchors = new Vector3?[frameCount, points.Count];
            for (int p = 0; p < points.Count; p++) {
                int start = -1;
                for (int f = 0; f <= frameCount; f++) {
                    bool contact = false;
                    if (f < frameCount && positions[f, p].y <= kContactHeight * scale) {
                        Vector3 a = positions[f, p];
                        Vector3 b = positions[f > 0 ? f - 1 : System.Math.Min (f + 1, frameCount - 1), p];
                        a.y = b.y = 0;
                        contact = Vector3.Distance (a, b) <= kContactSpeed * scale;
                    }
                    if (contact && start < 0) start = f;
                    if (contact || start < 0) continue;
                    // 2 フレーム以上続き、その間に足が実際に動いている（滑っている・床に沈んでいる）区間だけ留める。
                    // もう留まっている区間を解き直すと、留め始め・離すところで脚が跳ねるだけになる
                    Vector3 anchor = positions[start, p];
                    float drift = 0;
                    for (int g = start; g < f; g++) {
                        Vector3 d = positions[g, p] - anchor;
                        drift = Mathf.Max (drift, new Vector2 (d.x, d.z).magnitude, -positions[g, p].y);
                    }
                    if (f - start >= 2 && drift > kContactDrift * scale) {
                        anchor.y = Mathf.Max (anchor.y, 0);
                        for (int g = start; g < f; g++) anchors[g, p] = anchor;
                    }
                    start = -1;
                }
            }

            int locked = 0;
            List<PoseTarget> writeTargets = RigTargets.Create (rig, stage.editingRigSolver);
            try {
                for (int f = 0; f < frameCount; f++) {
                    bool any = false;
                    for (int p = 0; p < points.Count; p++) any |= anchors[f, p].HasValue;
                    if (!any) continue;
                    locked++;
                    ApplyImported (imported, f / rate);
                    // 留める点のある脚だけを解く（キーの間で Locked の点を留めるときと同じ。全身で解くと腕や背骨まで動く）
                    HashSet<Transform> controls = new HashSet<Transform> ();
                    for (int p = 0; p < points.Count; p++) {
                        if (!anchors[f, p].HasValue) continue;
                        // 留める場所は球の下の端なので、点（球の中心）は半径だけ上
                        body.SetPointPositionWorld (points[p], anchors[f, p].Value + Vector3.up * body.GetRadius (points[p]));
                        body.SetPositionWeight (points[p], 1);
                        controls.Add (body.GetControl (points[p]));
                    }
                    body.CaptureInput ();
                    body.SolveOnly (controls, 1, true);
                    stage.editingRigSolver.Capture ();
                    // 点の固定はキーにしない（全フレームが骨のキーになるので、点は要らない）
                    foreach (BodyPointId id in points) body.SetPositionWeight (id, 0);
                    using (CurveWriter writer = CurveWriter.Begin (null, rig.root.transform, f, null)) {
                        foreach (PoseTarget t in writeTargets) t.WriteKeys (writer);
                        foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) {
                            float[] array;
                            if (target.TryGetValue (pair.Key, out array)) array[f] = pair.Value;
                        }
                    }
                }
            }
            finally {
                foreach (PoseTarget t in writeTargets) t.Dispose ();
            }
            return locked;
        }

        /// <summary>取り込んだ値（編集用リグのクリップ）をその時刻の操作値にして解く</summary>
        void ApplyImported (AnimationClip imported, float time)
        {
            stage.ResetControlsToRest ();
            stage.SampleClip (imported, time);
            stage.MaskBodyPointsForBake (imported, time);
            stage.Solve ();
        }

        static int CountKeys (AnimationClip clip)
        {
            int count = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve != null) count += curve.length;
            }
            return count;
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
        /// 段の並びの持ち主（窓の代わり）。段に見せるのはクリップと Override の並びだけで、窓の操作（取込・焼く・差し替え）は持たない
        /// </summary>
        sealed class Host : ILayerHost
        {
            readonly PoseSession session_;

            public Host (PoseSession session)
            {
                session_ = session;
            }

            static string windowOnly
            {
                get { return Tr ("POSE_SESSION_WINDOW_ONLY"); }
            }

            // ---- Editing Rig ----

            public AnimationClip clip { get { return session_.clip_; } }
            public string clipProblem { get { return EditingClip.GetProblem (session_.clip_); } }
            public int strayCurveCount { get { return 0; } }
            public void SetClip (AnimationClip clip) { session_.clip = clip; }
            public void CreateClip () { }
            public void RemoveStrayCurves () { }
            public bool useHumanoidBase { get { return session_.useHumanoidBase; } }
            public string GetImportBaseProblem () { return windowOnly; }
            public bool ImportBaseClip () { return false; }
            public string GetPickImportProblem () { return windowOnly; }
            public void PickImportClip () { }
            public bool importPreviewing { get { return false; } }
            public string GetMergeOverridesProblem () { return windowOnly; }
            public bool MergeOverrides () { return false; }

            public void SetClipWeight (PoseLayer layer, float weight)
            {
                if (layer != null && layer.hasClipWeight) layer.clipWeight = Mathf.Clamp01 (weight);
            }

            // ---- Override ----

            public IReadOnlyList<OverrideClip> overrides { get { return session_.overrides_; } }
            public int overrideWriteIndex { get { return session_.overrideWriteIndex; } }
            public bool layerStructureEditing { get { return false; } }
            public void SetOverrideClip (int index, AnimationClip clip) { }
            public void SetOverrideMode (int index, OverrideMode mode) { }
            public void SetOverrideMask (int index, ChannelMask mask)
            {
                if (index >= 0 && index < session_.overrides_.Count) session_.overrides_[index].mask = mask;
            }
            public void RemoveOverride (int index, bool deleteAsset) { }
            public void MoveOverride (int index, int direction) { }
            public string GetMoveOverrideProblem (int index, int direction) { return windowOnly; }

            // ---- Generic Pose・Humanoid Pose（置かない） ----

            public AnimationClip genericClip { get { return null; } }
            public void SetGenericClip (AnimationClip clip) { }
            public string GetImportProblem () { return windowOnly; }
            public bool ImportGenericClip () { return false; }
            public AnimationClip humanoidClip { get { return null; } }
            public void SetHumanoidClip (AnimationClip clip) { }
            public string GetHumanoidApplyProblem () { return windowOnly; }
            public bool ApplyHumanoidPoseToRig () { return false; }
            public void SetHumanoidPoseLayer (bool value) { }

            // ---- Output（置かない） ----

            public string DescribeOutput (OutputLayer output) { return ""; }
            public string GetOutputPath (OutputLayer output) { return null; }
            public LayerClipStatus GetOutputStatus (OutputLayer output) { return new LayerClipStatus (LayerClipState.None, windowOnly); }
            public string GetOutputBakeProblem (OutputLayer output) { return windowOnly; }
            public bool BakeOutput (OutputLayer output) { return false; }
            public void RemoveOutput (OutputLayer output) { }
            public void MoveOutput (OutputLayer output, int direction) { }
            public string GetMoveOutputProblem (OutputLayer output, int direction) { return windowOnly; }
            public bool autoBake { get { return false; } set { } }
            public List<AnimationClip> bakeOverrides { get { return new List<AnimationClip> (); } }
            public string GetSwapProblem () { return windowOnly; }
            public bool SwapClipReferences (bool back) { return false; }

            // ---- 並び ----

            public bool humanoidPoseLayer { get { return false; } }
            public IReadOnlyList<OutputEntry> outputEntries { get { return null; } }
            public ContextClip timelineClip { get { return null; } }
            public int timelineClipCandidateCount { get { return 0; } }
        }
    }

}
