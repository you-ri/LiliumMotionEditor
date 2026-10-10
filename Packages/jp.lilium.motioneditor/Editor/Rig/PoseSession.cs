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

        /// <summary>つかむ対象（FK・IK・全身 IK の点・持ち替えの物）。窓の対象と同じ作り方</summary>
        public IReadOnlyList<PoseTarget> targets
        {
            get { return targets_; }
        }

        /// <summary>キャラを置けなかった理由（置けたら null）</summary>
        public string error
        {
            get { return stage != null ? stage.error : null; }
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
            get { return clip_ != null && clip_.frameRate > 0 ? clip_.frameRate : 60; }
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
            if (stage == null || stage.editingRig == null) return error ?? Tr ("POSE_SESSION_NO_CHARACTER");
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
        /// 今の姿勢（解いた後）を今のフレームのキーにする。全身 IK のキャラは窓で点を離したときと同じく、全身の骨と動かした点を打つ。
        /// そうでなければ、つかめる物全部の今の値を打つ。1 回の Undo で戻る。打った後、そのフレームの姿勢をクリップから作り直す
        /// </summary>
        /// <returns>打てなかった理由（打てたら null）</returns>
        public string CommitPose (string undoName = "Key Pose")
        {
            string problem = GetWriteProblem ();
            if (problem != null) return problem;

            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            if (stage.fullBody != null) {
                PoseCommit.WriteFullBodyPose (stage, targets_, () => BeginCurves (undoName));
            }
            else {
                using (CurveWriter writer = BeginCurves (undoName)) {
                    foreach (PoseTarget target in targets_) target.WriteKeys (writer);
                    RigProxies proxies = stage.editingRig.rigProxies;
                    if (proxies != null) proxies.WriteWeightKeys (writer);
                }
            }
            // サイクルキーの印があれば、最後のキーを先頭のキーに合わせ直す（窓はキーが変わった知らせで合わせる）
            AnimationClip written = targetClip;
            if (ClipCycle.Load (written)) {
                Undo.RecordObject (written, undoName);
                ClipCycle.Sync (written);
            }
            Undo.SetCurrentGroupName (undoName);
            Undo.CollapseUndoOperations (group);
            stage.InvalidateClipCurves ();
            Evaluate (frame);
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
            if (stage == null || stage.editingRig == null) return error ?? Tr ("POSE_SESSION_NO_CHARACTER");
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
            stage.ResetControlsToRest ();
            if (useHumanoidBase) stage.SampleHumanoidAsControls (clip_, time);
            else stage.SampleClip (clip_, time);
            stage.MaskBodyPointsForBake (useHumanoidBase ? null : clip_, time);
            foreach (OverrideClip entry in overrides_) {
                if (entry.clip != null) stage.ApplyOverride (entry.clip, entry.mode, 1, time, entry.mask);
            }
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
            public List<string> notes = new List<string> ();
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
        /// <returns>取り込めなかった理由（取り込めたら null）</returns>
        public string ImportOverride (AnimationClip source, AnimationClip destination, OverrideMode mode, bool lockFeet, out OverrideImportResult result)
        {
            result = new OverrideImportResult ();
            if (stage == null || stage.editingRig == null) return error ?? Tr ("POSE_SESSION_NO_CHARACTER");
            if (clip_ == null) return Tr ("POSE_SESSION_NO_CLIP");
            string problem = HumanoidImport.GetProblem (source);
            if (problem != null) return problem;
            if (destination == null || destination == clip_ || destination == source) return Tr ("POSE_SESSION_BAD_IMPORT_DESTINATION");
            if (lockFeet && stage.fullBody == null) return Tr ("POSE_DOCUMENT_NOT_FULL_BODY");

            EditingRig rig = stage.editingRig;
            Transform root = rig.root.transform;
            float rate = clip_.frameRate > 0 ? clip_.frameRate : 60;
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
                Dictionary<EditorCurveBinding, float[]> target = ReadCurves (imported, result.frameCount, rate);

                // 2. 足を床に付ける
                if (lockFeet) result.lockedFrames = LockFeet (imported, target, result.frameCount, rate);

                // 3. 元のクリップとの差分にする
                Dictionary<EditorCurveBinding, float[]> stored = new Dictionary<EditorCurveBinding, float[]> ();
                foreach (EditorCurveBinding binding in target.Keys) stored[binding] = new float[result.frameCount];
                List<IGrouping<string, EditorCurveBinding>> transforms = target.Keys.Where (b => b.type == typeof (Transform)).GroupBy (b => b.path + "|" + b.propertyName.Substring (0, b.propertyName.LastIndexOf ('.'))).ToList ();
                for (int f = 0; f < result.frameCount; f++) {
                    stage.ResetControlsToRest ();
                    if (useHumanoidBase) stage.SampleHumanoidAsControls (clip_, f / rate);
                    else stage.SampleClip (clip_, f / rate);
                    foreach (IGrouping<string, EditorCurveBinding> group in transforms) StoreTransform (group, root.gameObject, target, stored, f, mode);
                    foreach (EditorCurveBinding binding in target.Keys) {
                        if (binding.type == typeof (Transform)) continue;
                        float current;
                        if (!AnimationUtility.GetFloatValue (root.gameObject, binding, out current)) current = 0;
                        stored[binding][f] = OverrideMath.StoreFloat (mode, Lilium.IkControl.IsAdditive (binding.propertyName), current, target[binding][f]);
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

        /// <summary>位置・回転・大きさのどれか 1 組（x/y/z[/w]）を、今のコントロールの値（元）との差分にする</summary>
        static void StoreTransform (IGrouping<string, EditorCurveBinding> group, GameObject root, Dictionary<EditorCurveBinding, float[]> target,
            Dictionary<EditorCurveBinding, float[]> stored, int f, OverrideMode mode)
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
                value[i] = target.TryGetValue (bindings[i], out array) ? array[f] : source[i];
            }
            float[] result = new float[4];
            if (count == 4) {
                Quaternion q = OverrideMath.StoreRotation (mode, Quaternion.Normalize (new Quaternion (source[0], source[1], source[2], source[3])),
                    Quaternion.Normalize (new Quaternion (value[0], value[1], value[2], value[3])));
                result[0] = q.x; result[1] = q.y; result[2] = q.z; result[3] = q.w;
            }
            else {
                Vector3 s = new Vector3 (source[0], source[1], source[2]);
                Vector3 v = new Vector3 (value[0], value[1], value[2]);
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
        /// 点ごとに見るので、かかとが先に上がりつま先が残る、のような転がしも残る。target（編集用リグの値）の該当フレームを書き換える。
        /// 解き直したフレームの数を返す
        /// </summary>
        int LockFeet (AnimationClip imported, Dictionary<EditorCurveBinding, float[]> target, int frameCount, float rate)
        {
            EditingRig rig = stage.editingRig;
            FullBodyRig body = stage.fullBody;
            float scale = rig.humanScale > 0 ? rig.humanScale : 1;
            HashSet<Transform> feet = new HashSet<Transform> ();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }) {
                Transform t = rig.GetEditingBone (bone);
                if (t != null) feet.Add (t);
            }
            List<BodyPointId> points = body.pointIds.Where (id => feet.Contains (body.GetBone (id))).ToList ();

            // 足の点の場所（フレームごと）
            Vector3[,] positions = new Vector3[frameCount, points.Count];
            for (int f = 0; f < frameCount; f++) {
                ApplyImported (imported, f / rate);
                for (int p = 0; p < points.Count; p++) body.TryGetBonePointWorld (points[p], out positions[f, p]);
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
                        body.SetPointPositionWorld (points[p], anchors[f, p].Value);
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
