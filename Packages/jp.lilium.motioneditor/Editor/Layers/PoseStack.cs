using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 姿勢がどう作られているかを表す段の並び。段は PoseLayer のサブクラスのインスタンスで、
    /// スタックは段が宣言した性質（書けるか・何をつかむか・逆に解けるか・姿勢のどこで働くか（phase））だけを見て、経路と UI と評価の時機を決める。
    ///
    /// **評価も UI もこの 1 つから作る。** 2 か所に書くと、画面に出ている段と実際に走っている処理がずれるため。
    /// 骨を触る処理はすべてこの中を通す（窓や道具が直に解かない）。
    ///
    /// 段が出るかどうかは対象から自動で決まる（Rigging を持たないキャラではその段が出ない）。
    /// 手で足すのは Override・Humanoid Pose・Output の段だけ（Layers の設定。LayerState）。
    /// ゲーム側の処理も [PoseLayer] を付ければ段として並ぶ（キャラに付いているものだけ）。
    ///
    /// 姿勢は上から下へ一列に流れるが、値は一列ではない。保存データ（Editing Rig の段のクリップ）が、編集用リグの操作値・
    /// Rig の重みとターゲットを、それぞれの段へ直接配る（ゲーム側の段のパラメータは、いまは表示だけで保存データからは来ない）。
    /// そのため「書き戻せるか」は段の並びの区間ではなく、つかんだものの出どころで決める（PathLayers）
    /// </summary>
    public sealed class PoseStack
    {
        /// <summary>
        /// 組み込みの段の並び順。ゲーム側の段は [PoseLayer] の order で間に入る。
        /// 90 から下は実行時グラフの sorting と同じ体系（例: クリップ 90 → Rig 1000 → 後段の処理 1110）。
        /// 89 以下は編集専用の段が入る場所
        /// </summary>
        public const int kOrderEditingRig = 0;
        /// <summary>Override の段（S14）。編集用リグのすぐ下に、足した順で並ぶ</summary>
        public const int kOrderOverride = 10;
        /// <summary>全身 IK の段（S25）。Override の後・Humanoid Pose の前</summary>
        public const int kOrderFullBody = 70;
        public const int kOrderGenericPose = 80;
        public const int kOrderHumanoid = 90;
        /// <summary>
        /// 焼いて書き出す段（S20）。**Output の位置はこの値では決まらない**（Build の最後に、並びの設定の「直前の段」の後ろへ差し込む）。
        /// 並べ替え（Sort）の後で差し込むので、Output を入れた後に Sort し直さないこと
        /// </summary>
        public const int kOrderOutput = 95;
        /// <summary>Timeline のクリップの段（時間の写像・オフセット・ブレンド）に予約</summary>
        public const int kOrderTimelineClip = 100;
        public const int kOrderRigBuilder = 1000;
        public const int kOrderDisplayBone = int.MaxValue;

        /// <summary>既定の ✏。キーは保存データに書く</summary>
        public static readonly string kDefaultWriteId = LayerKind.EditingRig.ToString ();
        /// <summary>既定の ✋。編集用リグの操作値をつかみ、同じ段のクリップへ書く</summary>
        public static readonly string kDefaultManipulateId = LayerKind.EditingRig.ToString ();

        readonly List<PoseLayer> layers_ = new List<PoseLayer> ();
        readonly List<string> notes_ = new List<string> ();
        PreviewStage stage_;
        string writeId_ = kDefaultWriteId;
        string manipulateId_ = kDefaultManipulateId;

        public IReadOnlyList<PoseLayer> layers
        {
            get { return layers_; }
        }

        /// <summary>
        /// 段にしなかったものの説明など、一覧の下に出す注意
        /// </summary>
        public IReadOnlyList<string> notes
        {
            get { return notes_; }
        }

        /// <summary>
        /// キーの書き出し先（✏）。常に 1 つ
        /// </summary>
        public PoseLayer writeLayer
        {
            get { return Find (writeId_) ?? layers_.FirstOrDefault (l => l.canWrite); }
        }

        /// <summary>
        /// つかむ対象（✋）。常に 1 つ
        /// </summary>
        public PoseLayer manipulateLayer
        {
            get { return Find (manipulateId_) ?? Find (kDefaultManipulateId) ?? writeLayer; }
        }

        public PoseLayer Find (string id)
        {
            return string.IsNullOrEmpty (id) ? null : layers_.FirstOrDefault (l => l.id == id);
        }

        public PoseLayer Find (LayerKind kind)
        {
            return layers_.FirstOrDefault (l => l.kind == kind);
        }

        /// <summary>型で探す（match を渡すとその条件も満たす最初の段）</summary>
        public T Find<T> (System.Func<T, bool> match = null) where T : PoseLayer
        {
            foreach (PoseLayer layer in layers_) {
                T typed = layer as T;
                if (typed != null && (match == null || match (typed))) return typed;
            }
            return null;
        }

        public int IndexOf (PoseLayer layer)
        {
            return layer == null ? -1 : layers_.IndexOf (layer);
        }

        /// <summary>
        /// 表示モデルと今のクリップから段を組み立てる。キャラやクリップを替えるたびに作り直す
        /// </summary>
        /// <param name="host">クリップの持ち主。段はクリップをここから読む</param>
        public static PoseStack Build (PreviewStage stage, ILayerHost host)
        {
            PoseStack stack = new PoseStack ();
            if (stage == null || stage.animator == null) return stack;

            stack.stage_ = stage;
            // クリップを持つので、解くコントロールが無いキャラでも出す
            stack.Add (new EditingRigLayer (stage, host));
            // 元のクリップに重ねる差分（S14）。1 枚が 1 段
            if (host != null) {
                for (int i = 0; i < host.overrides.Count; i++) stack.Add (new OverrideLayer (stage, host, i));
            }
            // 全身 IK（S25）。リグの定義が全身 IK のときだけ
            if (stage.fullBody != null) stack.Add (new FullBodyLayer (stage, host));
            // Generic Pose（GenericPoseLayer）は既定の並びに入れない。段の並びを選べるようにしたときに足す
            // Humanoid Pose は置くと決めたときだけ（S21。既定は置かない）
            if (stage.animator.isHuman && (host == null || host.humanoidPoseLayer)) stack.Add (new HumanoidLayer (stage, host));
            stack.AddTimelineClip (stage, host);
            stack.AddRigBuilder (stage);
            stack.AddUserLayers (stage);
            stack.Add (new DisplayBoneLayer (stage));
            stack.Sort ();
            // 焼いて書き出す段（S21）は並びの設定どおりに差し込む（書き出せるのは今は Humanoid のクリップだけ）
            if (host != null && stage.animator.isHuman) stack.InsertOutputs (host);
            return stack;
        }

        /// <summary>
        /// 焼いて書き出す段を、それぞれ「直前の段」の後ろへ差し込む。直前の段が無ければ Humanoid Pose の後、それも無ければ最後
        /// </summary>
        void InsertOutputs (ILayerHost host)
        {
            IReadOnlyList<OutputEntry> entries = host.outputEntries;
            if (entries == null) return;
            // 別の Output の後ろに続く段もあるので、直前の段が並びに入ったものから順に差し込む。最後まで決まらなければ Humanoid Pose の後ろ
            List<OutputEntry> pending = entries.Where (e => e != null).ToList ();
            while (pending.Count > 0) {
                OutputEntry entry = pending.FirstOrDefault (e => layers_.Any (l => l.id == e.after)) ?? pending[0];
                pending.Remove (entry);
                OutputLayer layer = new OutputLayer (host, entry);
                if (Find (layer.id) != null) continue;
                int index = layers_.FindIndex (l => l.id == entry.after);
                // 直前の段が無ければ、Humanoid Pose（無ければ Editing Rig と Override）の後ろ
                if (index < 0) index = layers_.FindLastIndex (l => l.order <= kOrderHumanoid);
                // 全身 IK の段（S25）は編集用の体を作る段の一部。Editing Rig・Override の後ろに置いた Output は、全身 IK の後ろへ送る
                // （全身 IK より上には置けない。置けると、固定した点の効いていない姿勢が焼かれる）
                int fullBody = layers_.FindIndex (l => l.kind == LayerKind.FullBodyIk);
                if (fullBody >= 0 && index >= 0 && index < fullBody) index = fullBody;
                layers_.Insert (index < 0 ? layers_.Count : index + 1, layer);
            }
        }

        /// <summary>
        /// 基準の姿勢へ戻し、Humanoid Pose の段まで通す。Humanoid Pose の段を置いていなければ、Editing Rig と Override まで（S21。取込で使う）
        /// </summary>
        public bool EvaluateThroughHumanoid (float time)
        {
            int index = layers_.FindIndex (l => l.kind == LayerKind.HumanoidAnimation);
            if (index < 0) index = layers_.FindLastIndex (l => l.order <= kOrderHumanoid);
            if (index < 0) return false;
            if (stage_ != null) stage_.RestorePose ();
            EvaluateRange (0, time, index + 1, false);
            return true;
        }

        /// <summary>
        /// 基準の姿勢へ戻し、上からその段まで（その段を含む）を通す（S21。Output の段の位置で焼く）。
        /// Rig とゲーム側の段は段の並びの終端でまとめて解くので、その段が終端より上なら、そこまでに入った Rig・ゲーム側の段だけでここで解く
        /// </summary>
        public bool EvaluateUntil (PoseLayer layer, float time)
        {
            int index = layers_.IndexOf (layer);
            if (index < 0) return false;
            if (stage_ != null) stage_.RestorePose ();
            EvaluateRange (0, time, index + 1, false);
            if (stage_ != null && NeedsFlush (index)) stage_.FlushPoseGraph ();
            return true;
        }

        /// <summary>
        /// その段までに、終端でまとめて解く段（Graph）が入っていて、終端（Terminal）はまだ通っていないか
        /// </summary>
        bool NeedsFlush (int index)
        {
            bool pending = false;
            for (int i = 0; i <= index && i < layers_.Count; i++) {
                PoseLayer layer = layers_[i];
                if (!layer.active) continue;
                if (layer.phase == PosePhase.Terminal) return false;
                if (layer.phase == PosePhase.Graph) pending = true;
            }
            return pending;
        }

        /// <summary>
        /// その段より上に、表示モデルのグラフで姿勢を変える段（Graph・Terminal。Rig・ゲーム側の段・Display Pose）が評価に入っているか。
        /// 入っていれば、焼くときは表示モデルの骨から読む（S21）
        /// </summary>
        public bool HasPoseChangeBefore (PoseLayer layer)
        {
            int index = layers_.IndexOf (layer);
            for (int i = 0; i < index; i++) {
                PoseLayer l = layers_[i];
                if (l.active && (l.phase == PosePhase.Graph || l.phase == PosePhase.Terminal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 段を直接渡して組む（テスト用）。評価の前処理（基準姿勢へ戻す）は行わない
        /// </summary>
        internal static PoseStack FromLayers (IEnumerable<PoseLayer> layers)
        {
            PoseStack stack = new PoseStack ();
            foreach (PoseLayer layer in layers) stack.Add (layer);
            stack.Sort ();
            return stack;
        }

        void Add (PoseLayer layer)
        {
            layers_.Add (layer);
        }

        void Sort ()
        {
            // 同じ order は足した順（OrderBy は安定）
            List<PoseLayer> sorted = layers_.OrderBy (l => l.order).ToList ();
            layers_.Clear ();
            layers_.AddRange (sorted);
        }

        /// <summary>
        /// 一緒に見ている演出にアニメーションのクリップがあれば、その中でこのクリップがどう置かれているかの段（S15b）
        /// </summary>
        void AddTimelineClip (PreviewStage stage, ILayerHost host)
        {
            if (host == null) return;
            // 演出はプレビューの世界に置いたもの（prefab）か、Motion Scene なら開いているシーンのもの（S15d）。どちらかは窓が決める
            int candidates = host.timelineClipCandidateCount;
            if (candidates == 0) return;
            Add (new TimelineClipLayer (() => host.timelineClip, candidates));
        }

        /// <summary>
        /// キャラに組まれている Animation Rigging。**Rig の層ごとに 1 段**にする（層ごとの重みに繋ぐため）。
        /// 無効（active = 0）の層は段にしない。ゲームのコードが有効にする層で、モーションからは有効にできないため
        /// </summary>
        void AddRigBuilder (PreviewStage stage)
        {
            List<RigLayerInfo> rigs = RigProbe.Describe (stage.model);
            if (rigs.Count == 0) {
                if (RigBuilderUnreadable (stage)) Add (new RigBuilderLayer (null, 0));
                return;
            }

            List<string> inactive = new List<string> ();
            List<RigLayerInfo> shown = rigs.Where (r => r.active).ToList ();
            for (int i = 0; i < rigs.Count; i++) {
                if (!rigs[i].active) {
                    inactive.Add (rigs[i].label);
                    continue;
                }
                Add (new RigBuilderLayer (rigs[i], i, stage, shown));
            }
            if (inactive.Count > 0) {
                notes_.Add ("無効の Rig の層（" + string.Join ("、", inactive) + "）は出していない。"
                    + "ゲームのコードが有効にする層で、モーションからは有効にできない");
            }
        }

        /// <summary>
        /// Rigging が組まれているのに読み取れない（パッケージが入っていない）か。
        /// 「キャラに Rig が無い」と区別がつかないまま段が消えると、原因を追えないため行を出す
        /// </summary>
        static bool RigBuilderUnreadable (PreviewStage stage)
        {
            if (RigProbe.available || stage.model == null) return false;
            return stage.model.GetComponentsInChildren<Component> (true).Any (c => c != null && c.GetType ().Name == "RigBuilder");
        }

        /// <summary>
        /// ゲーム側が [PoseLayer] で足した段。**型があるだけでは出ない。キャラに付いているものだけ**を段にする
        /// </summary>
        void AddUserLayers (PreviewStage stage)
        {
            if (stage.model == null) return;

            foreach (System.Type type in LayerTypes.poseLayerTypes) {
                foreach (Component component in stage.model.GetComponentsInChildren (type, true)) {
                    if (component == null) continue;
                    Add (new UserComponentLayer (component, type, stage.model.transform, stage));
                }
            }
        }

        internal static bool Worse (InverseKind a, InverseKind b)
        {
            return (int)a > (int)b;
        }

        public static string Describe (InverseKind kind)
        {
            switch (kind) {
                case InverseKind.Exact: return "そのまま戻せる";
                case InverseKind.Approximate: return "ずれる";
                default: return "戻せない";
            }
        }

        // ---- 評価 ----

        /// <summary>
        /// 今のフレームの姿勢を作る。まず基準の姿勢へ戻し、評価に入る段（前進があり、👁 が入っている）だけを順に通す。
        /// 👁 を落とした段は素通しになる（例: Editing Rig を落とすと、クリップも当てず解きもしない基準の姿勢が見える）
        /// </summary>
        public void Evaluate (float time)
        {
            // カーブの無い骨が前の姿勢のまま残らないように、段の前で戻す（クリップが無いときも戻す）
            if (stage_ != null) stage_.RestorePose ();
            EvaluateRange (0, time);
        }

        /// <summary>
        /// その段から下だけを解き直す。骨や操作値を直に動かした後（ハンドル・Spinner・Reset）に使う。
        /// 基準の姿勢へは戻さず、その段の値も当て直さない（いま触った値の続きから解く）
        /// </summary>
        public void EvaluateFrom (PoseLayer layer, float time)
        {
            int index = layers_.IndexOf (layer);
            if (index < 0) return;
            EvaluateRange (index, time, layers_.Count, true);
        }

        void EvaluateRange (int start, float time)
        {
            EvaluateRange (start, time, layers_.Count, false);
        }

        /// <param name="keepStartValues">開始段の値を当て直さない（その段の値を直に動かした後）。後ろの段は普段どおり</param>
        void EvaluateRange (int start, float time, int end, bool keepStartValues)
        {
            for (int i = start; i < end; i++) {
                PoseLayer layer = layers_[i];
                if (!layer.active) continue;

                try {
                    if (keepStartValues && i == start) layer.EvaluateKeepingValues (time);
                    else if (keepStartValues) layer.EvaluateAfterEdit (time);
                    else layer.Evaluate (time);
                } catch (System.Exception exception) {
                    // ゲーム側の段が投げても、後ろの段（IK など）まで巻き添えにしない。次からは飛ばす
                    layer.Fail (exception.Message);
                    Debug.LogException (exception);
                }
            }
        }

        // ---- 経路 ----

        /// <summary>
        /// その段を逆に通るときの状態。評価に入っていない段と重み 0 の段は素通し（恒等）なので、そのまま戻せる
        /// </summary>
        public InverseKind StepKind (PoseLayer layer, out string reason)
        {
            return StepKind (layer, manipulateLayer, out reason);
        }

        /// <summary>
        /// manipulate をつかむときに、その段を逆に通る状態。表示の骨をつかむときは、逆の無い Rig でも
        /// つかんだ骨に効く拘束を止めれば通れる（止めている間の表示は Rig を掛けない姿なので「近似」）
        /// </summary>
        public InverseKind StepKind (PoseLayer layer, PoseLayer manipulate, out string reason)
        {
            if (layer == null || layer.passThrough) {
                reason = layer != null && layer.active ? "重み 0 なので素通し" : "いまは素通し";
                return InverseKind.Exact;
            }
            if (layer.bypassOnDisplayGrab && manipulate != null && manipulate.phase == PosePhase.Terminal) {
                reason = kBypassReason;
                return InverseKind.Approximate;
            }
            reason = layer.inverseReason;
            return layer.inverse;
        }

        public const string kBypassReason = "表示の骨をつかむ間は、選んでいる骨（か親）を動かす拘束を一時的に切る";

        /// <summary>
        /// つかむ段から書き出す段まで、逆に通る段の並び（下から上の順）と、その経路の通りやすさ。
        /// - 書き出し先は保存データの段だけ。
        /// - 保存データの段をつかむのは、その段自身へ書くときだけ（編集用リグの操作値をそのまま触る）。
        /// - 値をつかむ段（Rig のターゲット・ゲーム側のパラメータ）は、値が保存データにあるので間の段を通らない。
        /// - 姿勢をつかむ段は、その段から上の姿勢の段をすべて逆に通る（✋ は「その段の出力をつかむ」意味なので、つかんだ段自身も含む）
        /// </summary>
        public List<PoseLayer> PathLayers (PoseLayer write, PoseLayer manipulate, out InverseKind kind, out string reason)
        {
            List<PoseLayer> path = new List<PoseLayer> ();
            kind = InverseKind.None;
            reason = null;
            if (write == null || manipulate == null) return path;

            if (!write.isData) {
                reason = "書き出し先は保存データの段だけ";
                return path;
            }
            if (manipulate.isData) {
                if (manipulate != write) {
                    reason = "別の保存データの段の値はつかめない";
                    return path;
                }
                kind = InverseKind.Exact;
                return path;
            }

            switch (manipulate.grab) {
                case GrabTarget.Values:
                    // 保存データの段でない段の値は、保存データから配られた値（GrabTarget.Values）。姿勢の段は通らない
                    kind = InverseKind.Exact;
                    return path;

                case GrabTarget.Pose:
                    kind = InverseKind.Exact;
                    int end = IndexOf (manipulate);
                    for (int i = end; i >= 0; i--) {
                        PoseLayer layer = layers_[i];
                        if (!layer.isPoseStage) continue;
                        path.Add (layer);

                        string stepReason;
                        InverseKind step = StepKind (layer, manipulate, out stepReason);
                        if (!Worse (step, kind)) continue;
                        kind = step;
                        reason = layer.label + ": " + (string.IsNullOrEmpty (stepReason) ? Describe (step) : stepReason);
                    }
                    return path;

                default:
                    reason = manipulate.label + ": つかめる物が無い";
                    return path;
            }
        }

        /// <summary>
        /// いまの ✋ から ✏ までの経路の通りやすさ
        /// </summary>
        public InverseKind PathKind (out string reason)
        {
            return PathKind (writeLayer, manipulateLayer, out reason);
        }

        public InverseKind PathKind (PoseLayer write, PoseLayer manipulate, out string reason)
        {
            InverseKind kind;
            PathLayers (write, manipulate, out kind, out reason);
            return kind;
        }

        /// <summary>
        /// その段を、いまの ✋ から ✏ への経路で逆に通るか（行の間の矢印を強く出すため）
        /// </summary>
        public bool IsOnPath (PoseLayer layer)
        {
            InverseKind kind;
            string reason;
            return PathLayers (writeLayer, manipulateLayer, out kind, out reason).Contains (layer);
        }

        public bool CanWrite (PoseLayer layer, out string reason)
        {
            reason = null;
            if (layer == null) return false;
            if (!layer.canWrite) {
                reason = string.IsNullOrEmpty (layer.unavailableReason) ? "この段には書けない" : layer.unavailableReason;
                return false;
            }
            // 落とした段は値を当てずに素通しするので、画面の姿勢はこの段のクリップから出ていない
            if (!layer.enabled) {
                reason = "有効（👁）を落としている";
                return false;
            }
            if (PathKind (layer, manipulateLayer, out reason) == InverseKind.None) {
                reason = "いまつかんでいる段から戻せない — " + reason;
                return false;
            }
            reason = null;
            return true;
        }

        public bool CanManipulate (PoseLayer layer, out string reason)
        {
            reason = null;
            if (layer == null) return false;
            if (!layer.canManipulate) {
                reason = string.IsNullOrEmpty (layer.unavailableReason) ? "この段はつかめない" : layer.unavailableReason;
                return false;
            }
            if (PathKind (writeLayer, layer, out reason) == InverseKind.None) {
                reason = "書き出す段へ戻せない — " + reason;
                return false;
            }
            reason = null;
            return true;
        }

        public bool TrySetWrite (PoseLayer layer, out string reason)
        {
            // 保存データの段の値は、その段自身へ書くときだけつかめる。✏ を移したら ✋ も一緒に移す
            string saved = manipulateId_;
            if (layer != null && layer.isData && layer.canManipulate) manipulateId_ = layer.id;
            if (!CanWrite (layer, out reason)) {
                manipulateId_ = saved;
                return false;
            }
            writeId_ = layer.id;
            return true;
        }

        public bool TrySetManipulate (PoseLayer layer, out string reason)
        {
            if (!CanManipulate (layer, out reason)) return false;
            manipulateId_ = layer.id;
            return true;
        }

        // ---- 状態の保存 ----

        public void SaveState (LayerState state)
        {
            if (state == null) return;
            state.write = writeId_;
            state.manipulate = manipulateId_;
            if (state.values == null) state.values = new List<LayerValues> ();

            // 重みは今のキャラに無い段の分も残す（キャラを行き来しても消えないように）。
            // 👁 は今ある段の分だけ（無い段の分を残すと、同じ id を使い回す段（Override:0 など）が落ちたまま出てくる）
            foreach (LayerValues values in state.values) {
                if (values != null && Find (values.id) == null) values.enabled = true;
            }
            foreach (PoseLayer layer in layers_) {
                LayerValues values = state.Find (layer.id);
                if (values == null) {
                    values = new LayerValues { id = layer.id };
                    state.values.Add (values);
                }
                values.enabled = layer.enabled;
                if (layer.hasWeight) values.weight = layer.weight;
                if (layer.hasClipWeight) values.clipWeight = layer.clipWeight;
            }
        }

        /// <summary>
        /// 保存した選択を戻す。今のキャラに無い段や、選べなくなった段は捨てる。
        /// 経路が通らない組み合わせも捨てる（不正な組のまま編集が始まらないように）
        /// </summary>
        public void LoadState (LayerState state)
        {
            if (state == null) return;

            SetSelected (state.write, ref writeId_, true);
            SetSelected (state.manipulate, ref manipulateId_, false);
            foreach (PoseLayer layer in layers_) {
                LayerValues values = state.Find (layer.id);
                // 値の無い段は 👁 を入れる（Undo で値が消えた段を、スタックを作り直さずに戻すため）。重みは値があるときだけ当てる
                layer.enabled = values == null || values.enabled;
                if (values == null) continue;
                if (layer.hasWeight) layer.weight = Mathf.Clamp01 (values.weight);
                if (layer.hasClipWeight) layer.clipWeight = Mathf.Clamp01 (values.clipWeight);
            }

            string reason;
            if (PathKind (out reason) != InverseKind.None) return;
            // 通らない組は既定の ✋ に戻し、それも通らなければ書き出す段そのものをつかむ
            manipulateId_ = kDefaultManipulateId;
            if (PathKind (out reason) == InverseKind.None) manipulateId_ = writeId_;
        }

        void SetSelected (string id, ref string field, bool forWrite)
        {
            PoseLayer layer = Find (id);
            if (layer == null || (forWrite ? !layer.canWrite : !layer.canManipulate)) return;
            field = id;
        }
    }

}
