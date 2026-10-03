using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップ（リグの操作値）から、ゲームで使う Humanoid のクリップを作る（S2）。
    /// 流れは「操作値 → 編集用リグの解決 → 編集用の体の姿勢 → HumanPose（muscle と体の位置・向き）」で、格子の各フレームで行う。
    ///
    /// 読むときは編集用の体のルートを原点・無回転に置く。表示モデルの Animator の GameObject は prefab の中で回っている（Y270°）ので、
    /// そのまま読むと RootT が 2〜4 m ずれる（検証 SP の結果 2）。
    /// 体の muscle のプロパティ名は HumanTrait.MuscleName と同じで、指だけ「LeftHand.Thumb.1 Stretched」の形に置き換える
    /// </summary>
    public sealed class HumanoidBaker : System.IDisposable
    {
        public const string kRootT = "RootT";
        public const string kRootQ = "RootQ";
        static readonly string[] kAxes = { "x", "y", "z", "w" };
        static readonly string[] kFingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static string[] clipMuscleNames_;

        /// <summary>
        /// 焼いた結果
        /// </summary>
        public struct Result
        {
            public int frameCount;
            public int curveCount;
            public double milliseconds;
            /// <summary>焼かなかったものなど、利用者へ伝えること</summary>
            public List<string> notes;
        }

        readonly EditingRig rig_;
        readonly EditingRigSolver solver_;
        readonly System.Action<AnimationClip, float> sample_;
        /// <summary>そのフレームの姿勢を作る（S14。元＋Override を重ねる）。null なら sample_ でクリップを当てる</summary>
        readonly System.Action<float> apply_;
        HumanPoseHandler handler_;
        HumanPose pose_;
        /// <summary>手足の IK ゴールを焼いた姿勢から求める（FBX の取り込みと同じく姿勢から作る）</summary>
        HumanoidGoals goals_;
        bool goalsFailed_;

        /// <summary>
        /// 焼けない理由（焼けるなら null）
        /// </summary>
        public string error { get; private set; }

        /// <summary>
        /// 姿勢を編集用の体でなく表示モデルの骨から読む（S21。Output の段を Rig などの後ろに置いたとき、段の並びをそこまで通した姿勢を焼く）。
        /// 追加コントロールが動かす骨（武器など）も表示モデルの骨から読む
        /// </summary>
        public bool readFromDisplay;

        /// <summary>
        /// ゲームの Rig の値（重み・ターゲット）を焼くか。Rig を通した後の姿勢を焼くときは、ゲームで二重に掛からないよう焼かない（S21）
        /// </summary>
        public bool bakeRigProxies = true;

        /// <summary>
        /// 編集用リグを解いた後、姿勢を読む前に、編集用の体へ掛ける処理（S25。全身 IK の段）。
        /// 編集用の体から読む焼き方のときだけ使う（表示モデルから読むときは、段の並びを通した時点で掛かっている）
        /// </summary>
        public System.Action afterSolve;

        HumanPoseHandler displayHandler_;

        /// <param name="sample">編集用の体へクリップを当てる関数（ステージの SampleClip）。当てるグラフは 1 つにしたいので外から受け取る</param>
        /// <param name="apply">
        /// そのフレームの姿勢（操作値）を作る関数（S14。元＋Override を重ねる）。渡すと sample は使わない
        /// </param>
        public HumanoidBaker (EditingRig rig, EditingRigSolver solver, System.Action<AnimationClip, float> sample, System.Action<float> apply = null)
        {
            rig_ = rig;
            solver_ = solver;
            sample_ = sample;
            apply_ = apply;

            Avatar avatar = rig != null && rig.displayAnimator != null ? rig.displayAnimator.avatar : null;
            if (rig == null || rig.root == null) {
                error = "編集用の体が無い";
                return;
            }
            if (avatar == null || !avatar.isValid || !avatar.isHuman) {
                error = "表示モデルに Humanoid の Avatar が無い";
                return;
            }

            // コントロール（Controls/FK/Head など）が骨と同じ名前のことがあるので、骨を探す間だけ外しておく
            Transform controls = rig.controlsRoot;
            Transform parent = controls != null ? controls.parent : null;
            if (controls != null) controls.SetParent (null, false);
            try {
                handler_ = new HumanPoseHandler (avatar, rig.root.transform);
            }
            finally {
                if (controls != null) controls.SetParent (parent, false);
            }
            pose_ = new HumanPose ();
        }

        /// <summary>
        /// IK ゴールの計算を用意する（焼くときだけ。表示モデルを複製するので、読み取りにしか使わない Baker では作らない）
        /// </summary>
        HumanoidGoals EnsureGoals ()
        {
            if (goals_ != null || goalsFailed_) return goals_;
            goals_ = new HumanoidGoals (rig_.displayAnimator);
            if (goals_.error != null) {
                goals_.Dispose ();
                goals_ = null;
                goalsFailed_ = true;
            }
            return goals_;
        }

        public void Dispose ()
        {
            if (handler_ != null) handler_.Dispose ();
            handler_ = null;
            if (displayHandler_ != null) displayHandler_.Dispose ();
            displayHandler_ = null;
            if (goals_ != null) goals_.Dispose ();
            goals_ = null;
        }

        /// <summary>
        /// クリップのカーブでの muscle の名前（HumanTrait.MuscleName と同じ並び）
        /// </summary>
        public static IReadOnlyList<string> clipMuscleNames
        {
            get {
                if (clipMuscleNames_ == null) {
                    clipMuscleNames_ = new string[HumanTrait.MuscleCount];
                    for (int i = 0; i < clipMuscleNames_.Length; i++) {
                        clipMuscleNames_[i] = ToClipMuscleName (HumanTrait.MuscleName[i]);
                    }
                }
                return clipMuscleNames_;
            }
        }

        /// <summary>
        /// 「Left Thumb 1 Stretched」→「LeftHand.Thumb.1 Stretched」、「Left Index Spread」→「LeftHand.Index.Spread」。指以外はそのまま
        /// </summary>
        public static string ToClipMuscleName (string muscleName)
        {
            string[] words = muscleName.Split (' ');
            if (words.Length < 3 || (words[0] != "Left" && words[0] != "Right") || System.Array.IndexOf (kFingers, words[1]) < 0) return muscleName;

            string head = words[0] + "Hand." + words[1] + ".";
            return words.Length == 4 ? head + words[2] + " " + words[3] : head + words[2];
        }

        /// <summary>
        /// 今の編集用の体の姿勢を HumanPose にする。体の位置・向きは編集用の体のルートから見た値
        /// </summary>
        /// <summary>
        /// 今の表示モデルの骨の姿勢を HumanPose にする。編集用の体から読むときと同じく、Animator の GameObject を原点・無回転に置いて読む
        /// </summary>
        public bool ReadDisplayPose (ref HumanPose pose)
        {
            Animator animator = rig_.displayAnimator;
            if (animator == null) return false;
            if (displayHandler_ == null) displayHandler_ = new HumanPoseHandler (animator.avatar, animator.transform);
            Transform root = animator.transform;
            root.GetPositionAndRotation (out Vector3 position, out Quaternion rotation);
            root.SetPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                displayHandler_.GetHumanPose (ref pose);
            }
            finally {
                root.SetPositionAndRotation (position, rotation);
            }
            return true;
        }

        public bool ReadPose (ref HumanPose pose)
        {
            if (handler_ == null) return false;

            Transform root = rig_.root.transform;
            root.GetPositionAndRotation (out Vector3 position, out Quaternion rotation);
            root.SetPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                handler_.GetHumanPose (ref pose);
            }
            finally {
                root.SetPositionAndRotation (position, rotation);
            }
            return true;
        }

        /// <summary>
        /// 体（muscle）以外に焼くチャンネル 1 組。位置は 3 本、回転は 4 本（符号をそろえる）、数値は 1 本
        /// </summary>
        sealed class Channel
        {
            public EditorCurveBinding[] bindings;
            public System.Action<float[]> read;
            public bool quaternion;
            public float[][] values;
        }

        /// <summary>
        /// source を 0 から最後のフレームまで格子ごとに解いて、destination を作り直す（destination のカーブは全部置き換える）。
        /// 焼くもの: 体（RootT / RootQ / muscle）、手足の IK ゴール（焼いた姿勢から求める）、追加コントロールが動かす人型でない骨の Transform、Rig の代理（ゲーム prefab のパスへ寄せる）。
        /// source が Humanoid のクリップ（H土台）なら、焼いて作らないカーブ（武器の骨など）を source からそのまま写す。
        /// 受け口（HumanoidBakeHooks）でチャンネルを除き、焼いた後に加工する。
        /// 編集用の体は最後のフレームの姿勢で終わるので、呼んだ側で当て直す
        /// </summary>
        /// <param name="model">編集しているキャラの prefab（受け口がキャラを見分けるのに使う）</param>
        /// <param name="hooks">受け口。null なら登録されているもの全部</param>
        public Result Bake (AnimationClip source, AnimationClip destination, GameObject model = null, IList<IHumanoidBakeHook> hooks = null)
        {
            if (error != null) throw new System.InvalidOperationException (error);
            if (source == null || destination == null) throw new System.ArgumentNullException (source == null ? "source" : "destination");

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();
            if (hooks == null) hooks = new List<IHumanoidBakeHook> (HumanoidBakeHooks.hooks);
            HumanoidBakeContext context = new HumanoidBakeContext {
                model = model,
                displayAnimator = rig_.displayAnimator,
                source = source,
            };
            if (rig_.rigProxies != null) context.notes.AddRange (rig_.rigProxies.notes);

            float rate = source.frameRate > 0 ? source.frameRate : 60;
            // カーブの無いクリップの長さは 1 秒と返るので、その場合は基準姿勢の 1 フレームだけ
            int frameCount = source.empty ? 1 : Mathf.Max (1, Mathf.RoundToInt (source.length * rate) + 1);
            int muscleCount = HumanTrait.MuscleCount;
            // 体の並び: RootT xyz, RootQ xyzw, muscle...
            int bodyCount = 7 + muscleCount;
            float[][] body = new float[bodyCount][];
            for (int c = 0; c < bodyCount; c++) body[c] = new float[frameCount];

            List<Channel> channels = CreateChannels (context, hooks, frameCount);
            float[] buffer = new float[4];
            // IK ゴール: ゴールごとに位置 3 本・向き 4 本
            EnsureGoals ();
            int goalCount = goals_ != null ? HumanoidGoals.kCurveNames.Length : 0;
            float[][] goalCurves = new float[goalCount * 7][];
            for (int c = 0; c < goalCurves.Length; c++) goalCurves[c] = new float[frameCount];
            Vector3[] goalPositions = new Vector3[goalCount];
            Quaternion[] goalRotations = new Quaternion[goalCount];
            if (goals_ == null) context.notes.Add ("IK ゴールを焼けない（表示モデルに Humanoid の Avatar が無い）");

            Quaternion previous = Quaternion.identity;
            for (int f = 0; f < frameCount; f++) {
                rig_.ResetBonesToRest ();
                if (apply_ != null) apply_ (f / rate);
                else sample_ (source, f / rate);
                solver_.Solve ();
                if (readFromDisplay) ReadDisplayPose (ref pose_);
                else {
                    if (afterSolve != null) afterSolve ();
                    ReadPose (ref pose_);
                }

                Vector3 t = pose_.bodyPosition;
                Quaternion q = pose_.bodyRotation;
                // 補間で遠回りしないよう、前のフレームと同じ側の四元数にそろえる
                if (f > 0 && Quaternion.Dot (previous, q) < 0) q = new Quaternion (-q.x, -q.y, -q.z, -q.w);
                previous = q;

                body[0][f] = t.x;
                body[1][f] = t.y;
                body[2][f] = t.z;
                body[3][f] = q.x;
                body[4][f] = q.y;
                body[5][f] = q.z;
                body[6][f] = q.w;
                for (int m = 0; m < muscleCount; m++) {
                    body[7 + m][f] = pose_.muscles[m];
                }
                if (goals_ != null) {
                    goals_.Compute (ref pose_, goalPositions, goalRotations);
                    for (int g = 0; g < goalCount; g++) {
                        Quaternion r = goalRotations[g];
                        int o = g * 7;
                        if (f > 0 && r.x * goalCurves[o + 3][f - 1] + r.y * goalCurves[o + 4][f - 1] + r.z * goalCurves[o + 5][f - 1] + r.w * goalCurves[o + 6][f - 1] < 0) {
                            r = new Quaternion (-r.x, -r.y, -r.z, -r.w);
                        }
                        goalCurves[o][f] = goalPositions[g].x;
                        goalCurves[o + 1][f] = goalPositions[g].y;
                        goalCurves[o + 2][f] = goalPositions[g].z;
                        goalCurves[o + 3][f] = r.x;
                        goalCurves[o + 4][f] = r.y;
                        goalCurves[o + 5][f] = r.z;
                        goalCurves[o + 6][f] = r.w;
                    }
                }

                foreach (Channel channel in channels) {
                    channel.read (buffer);
                    if (channel.quaternion && f > 0) {
                        float dot = 0;
                        for (int i = 0; i < 4; i++) dot += buffer[i] * channel.values[i][f - 1];
                        if (dot < 0) for (int i = 0; i < 4; i++) buffer[i] = -buffer[i];
                    }
                    for (int i = 0; i < channel.bindings.Length; i++) channel.values[i][f] = buffer[i];
                }
            }

            List<EditorCurveBinding> bindings = new List<EditorCurveBinding> (bodyCount + channels.Count * 4);
            List<AnimationCurve> curves = new List<AnimationCurve> (bindings.Capacity);
            for (int c = 0; c < bodyCount; c++) {
                string property = c < 3 ? kRootT + "." + kAxes[c] : c < 7 ? kRootQ + "." + kAxes[c - 3] : clipMuscleNames[c - 7];
                bindings.Add (EditorCurveBinding.FloatCurve ("", typeof (Animator), property));
                curves.Add (MakeCurve (body[c], rate));
            }
            for (int c = 0; c < goalCurves.Length; c++) {
                string name = HumanoidGoals.kCurveNames[c / 7];
                int axis = c % 7;
                string property = axis < 3 ? name + "T." + kAxes[axis] : name + "Q." + kAxes[axis - 3];
                bindings.Add (EditorCurveBinding.FloatCurve ("", typeof (Animator), property));
                curves.Add (MakeCurve (goalCurves[c], rate));
            }
            foreach (Channel channel in channels) {
                for (int i = 0; i < channel.bindings.Length; i++) {
                    bindings.Add (channel.bindings[i]);
                    curves.Add (MakeCurve (channel.values[i], rate));
                }
            }
            // 任意のプロパティ（S6）は解く必要が無いので、カーブをそのまま、ゲーム prefab から見たパスへ写す。
            // 読込で元の Humanoid のクリップから持ち越した武器の骨などのカーブ（HumanoidImport.CarryCurves）もここを通る
            List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>> referenceBindings = new List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (source)) {
                EditorCurveBinding display;
                if (!PropertyPlayer.TryToDisplay (binding, out display)) continue;
                if (!HumanoidBakeHooks.ShouldBake (hooks, context, display)) continue;
                bindings.Add (display);
                curves.Add (AnimationUtility.GetEditorCurve (source, binding));
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (source)) {
                EditorCurveBinding display;
                if (!PropertyPlayer.TryToDisplay (binding, out display)) continue;
                if (!HumanoidBakeHooks.ShouldBake (hooks, context, display)) continue;
                referenceBindings.Add (new KeyValuePair<EditorCurveBinding, EditorCurveBinding> (binding, display));
            }

            // 土台が Humanoid のクリップ（S14 の H土台）なら、焼いて作らないカーブ（武器など人型でない骨・そのほかのプロパティ）を土台からそのまま写す。
            // 写さないと、元のクリップにあった武器の動きなどが焼いた版から落ちる（2026-09-28 キャラ A で確認）
            if (source.humanMotion) CarrySourceCurves (source, context, hooks, bindings, curves, referenceBindings);

            destination.ClearCurves ();
            destination.frameRate = rate;
            AnimationUtility.SetEditorCurves (destination, bindings.ToArray (), curves.ToArray ());
            foreach (KeyValuePair<EditorCurveBinding, EditorCurveBinding> pair in referenceBindings) {
                AnimationUtility.SetObjectReferenceCurve (destination, pair.Value, AnimationUtility.GetObjectReferenceCurve (source, pair.Key));
            }
            HumanoidBakeHooks.PostProcess (hooks, context, destination);

            return new Result {
                frameCount = frameCount,
                curveCount = bindings.Count,
                milliseconds = watch.Elapsed.TotalMilliseconds,
                notes = context.notes,
            };
        }

        /// <summary>
        /// 体以外のチャンネル。パスはゲーム prefab の Animator から見たもの。
        /// どのクリップを焼いても同じ組になる（キーの無いものも基準の値で焼く）。ゲームのクロスフェードで、片方にだけあるチャンネルが既定値へ滑らないように
        /// </summary>
        List<Channel> CreateChannels (HumanoidBakeContext context, IList<IHumanoidBakeHook> hooks, int frameCount)
        {
            List<Channel> channels = new List<Channel> ();
            Transform displayRoot = rig_.displayAnimator.transform;

            // 追加コントロール（武器・尻尾など）が動かす骨
            foreach (RigBinding.Extra extra in rig_.binding.extras) {
                if (!extra.enabled || extra.bone == null) continue;
                // 表示モデルから読むときは、段の並びを通した後の骨（Rig などが動かした分も入る）
                Transform editing = readFromDisplay ? extra.bone : rig_.GetEditingBone (extra.bone);
                if (editing == null) continue;
                string path = AnimationUtility.CalculateTransformPath (extra.bone, displayRoot);
                Add (channels, context, hooks, frameCount, TransformBindings (path, "m_LocalRotation", 4), true, v => Write (v, editing.localRotation));
                if (extra.control.position) {
                    Add (channels, context, hooks, frameCount, TransformBindings (path, "m_LocalPosition", 3), false, v => Write (v, editing.localPosition));
                }
            }

            // ゲームの Rig の値（代理 → ゲーム prefab のパス）。Rig を通した後の姿勢を焼くときは焼かない（S21）
            if (!bakeRigProxies && rig_.rigProxies != null && rig_.rigProxies.channels.Count > 0) {
                context.notes.Add ("Rig を通した後の姿勢を焼いたので、Rig の重みとターゲットは焼いていない（ゲームで二重に掛からないように）");
            }
            if (bakeRigProxies && rig_.rigProxies != null) {
                foreach (RigProxies.Channel proxy in rig_.rigProxies.channels) {
                    RigProxies.Channel c = proxy;
                    switch (c.kind) {
                        case RigProxies.ChannelKind.Weight:
                            if (c.weight == null || c.gameType == null) break;
                            Add (channels, context, hooks, frameCount, new[] { EditorCurveBinding.FloatCurve (c.gamePath, c.gameType, c.gameProperty) }, false, v => v[0] = c.weight.value);
                            break;
                        case RigProxies.ChannelKind.Position:
                            Add (channels, context, hooks, frameCount, TransformBindings (c.gamePath, c.gameProperty, 3), false, v => Write (v, c.proxyTransform.localPosition * c.scale));
                            break;
                        case RigProxies.ChannelKind.Rotation:
                            Add (channels, context, hooks, frameCount, TransformBindings (c.gamePath, c.gameProperty, 4), true, v => Write (v, c.proxyTransform.localRotation));
                            break;
                    }
                }
            }
            return channels;
        }

        /// <summary>
        /// 土台の Humanoid のクリップにあって、焼いて作らないカーブを写す。
        /// Humanoid の姿勢から作り直すもの（muscle・RootT/RootQ・IK ゴール）と、もう焼いたチャンネル、受け口が除いたものは写さない
        /// </summary>
        static void CarrySourceCurves (AnimationClip source, HumanoidBakeContext context, IList<IHumanoidBakeHook> hooks,
            List<EditorCurveBinding> bindings, List<AnimationCurve> curves, List<KeyValuePair<EditorCurveBinding, EditorCurveBinding>> referenceBindings)
        {
            HashSet<EditorCurveBinding> baked = new HashSet<EditorCurveBinding> (bindings);
            int carried = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (source)) {
                if (IsHumanPoseCurve (binding) || baked.Contains (binding)) continue;
                if (!HumanoidBakeHooks.ShouldBake (hooks, context, binding)) continue;
                bindings.Add (binding);
                curves.Add (AnimationUtility.GetEditorCurve (source, binding));
                carried++;
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (source)) {
                if (!HumanoidBakeHooks.ShouldBake (hooks, context, binding)) continue;
                referenceBindings.Add (new KeyValuePair<EditorCurveBinding, EditorCurveBinding> (binding, binding));
                carried++;
            }
            if (carried > 0) context.notes.Add ("土台のクリップから、人型の姿勢でないカーブ " + carried + " 本をそのまま写した（武器の骨など）");
        }

        static HashSet<string> humanPoseProperties_;

        /// <summary>
        /// Humanoid の姿勢から作り直すカーブか（muscle・体の位置と向き・IK ゴール・ルートモーション）
        /// </summary>
        internal static bool IsHumanPoseCurve (EditorCurveBinding binding)
        {
            if (binding.type != typeof (Animator) || !string.IsNullOrEmpty (binding.path)) return false;
            if (humanPoseProperties_ == null) {
                humanPoseProperties_ = new HashSet<string> (clipMuscleNames);
                string[] vectors = { kRootT, "MotionT", "LeftFootT", "RightFootT", "LeftHandT", "RightHandT" };
                string[] rotations = { kRootQ, "MotionQ", "LeftFootQ", "RightFootQ", "LeftHandQ", "RightHandQ" };
                foreach (string name in vectors) for (int i = 0; i < 3; i++) humanPoseProperties_.Add (name + "." + kAxes[i]);
                foreach (string name in rotations) for (int i = 0; i < 4; i++) humanPoseProperties_.Add (name + "." + kAxes[i]);
            }
            return humanPoseProperties_.Contains (binding.propertyName);
        }

        static void Add (List<Channel> channels, HumanoidBakeContext context, IList<IHumanoidBakeHook> hooks, int frameCount, EditorCurveBinding[] bindings, bool quaternion, System.Action<float[]> read)
        {
            foreach (EditorCurveBinding binding in bindings) {
                if (!HumanoidBakeHooks.ShouldBake (hooks, context, binding)) {
                    context.notes.Add ("ゲーム側が持つ値なので焼かない: " + binding.path + " " + binding.type.Name + "." + binding.propertyName);
                    return;
                }
            }
            float[][] values = new float[bindings.Length][];
            for (int i = 0; i < values.Length; i++) values[i] = new float[frameCount];
            channels.Add (new Channel { bindings = bindings, read = read, quaternion = quaternion, values = values });
        }

        static EditorCurveBinding[] TransformBindings (string path, string property, int count)
        {
            EditorCurveBinding[] bindings = new EditorCurveBinding[count];
            for (int i = 0; i < count; i++) {
                bindings[i] = EditorCurveBinding.FloatCurve (path, typeof (Transform), property + "." + kAxes[i]);
            }
            return bindings;
        }

        static void Write (float[] v, Vector3 value)
        {
            v[0] = value.x;
            v[1] = value.y;
            v[2] = value.z;
        }

        static void Write (float[] v, Quaternion value)
        {
            v[0] = value.x;
            v[1] = value.y;
            v[2] = value.z;
            v[3] = value.w;
        }

        /// <summary>
        /// フレームごとの値を直線でつなぐカーブにする。全部同じ値なら両端の 2 キーだけ
        /// </summary>
        internal static AnimationCurve MakeCurve (float[] values, float rate)
        {
            int count = values.Length;
            float last = (count - 1) / rate;
            bool constant = true;
            for (int i = 1; i < count && constant; i++) {
                if (Mathf.Abs (values[i] - values[0]) > 1e-6f) constant = false;
            }
            if (constant) {
                return count > 1
                    ? new AnimationCurve (new Keyframe (0, values[0], 0, 0), new Keyframe (last, values[0], 0, 0))
                    : new AnimationCurve (new Keyframe (0, values[0], 0, 0));
            }

            Keyframe[] keys = new Keyframe[count];
            for (int i = 0; i < count; i++) {
                float inSlope = i > 0 ? (values[i] - values[i - 1]) * rate : 0;
                float outSlope = i < count - 1 ? (values[i + 1] - values[i]) * rate : 0;
                keys[i] = new Keyframe (i / rate, values[i], i > 0 ? inSlope : outSlope, i < count - 1 ? outSlope : inSlope);
            }
            return new AnimationCurve (keys);
        }
    }

}
