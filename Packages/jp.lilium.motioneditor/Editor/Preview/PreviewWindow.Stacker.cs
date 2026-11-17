using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Stacker（S12）の窓の側。組（骨のまとまり）ごとのポーズキーの一覧・挿入・削除・間隔、ループ（サイクルキー。S32）、選んでいる物の前後のフレームの軌跡（ゴースト）。
    /// 自分で作った組はクリップごとに .meta へ保存する（Stacker.Settings）。選んでいる組とゴーストはこの PC の設定（EditorPrefs）。パネルは StackerOverlay
    /// </summary>
    public partial class PreviewWindow
    {
        public const string kGroupAll = "All";
        public const string kGroupBody = "Body";
        public const string kGroupHands = "Hands";
        public const string kGroupRig = "Rig";
        public const string kGroupFace = "Face";
        public const string kGroupProps = "Props";
        const string kBlendShape = "blendShape.";
        static readonly Color kGhostPrevious = new Color (0.35f, 0.65f, 1f, 1);
        static readonly Color kGhostNext = new Color (1f, 0.6f, 0.3f, 1);
        static readonly Color kGhostCurrent = new Color (1f, 1f, 1f, 0.95f);
        /// <summary>軌跡の濃さ。離れるほど薄くする</summary>
        const float kGhostAlphaNear = 0.95f;
        const float kGhostAlphaFar = 0.2f;
        const float kGhostLineWidth = 3;
        /// <summary>軌跡の点の大きさ（ハンドルの大きさに対する比）。キーのフレームは大きくする</summary>
        const float kGhostDotSize = 0.012f;
        const float kGhostKeyDotSize = 0.028f;
        /// <summary>1 回の描画で軌跡の位置を取るのに使ってよい時間。足りなければ次の描画で続きを取る</summary>
        const double kGhostBudgetSeconds = 0.03;

        Stacker.Settings stacker_ = new Stacker.Settings ();
        AnimationClip stackerClip_;
        bool stackerLoaded_;

        /// <summary>組・ループ・ゴーストの設定が変わったとき（Stacker のパネルが作り直す）</summary>
        public event System.Action stackerChanged;

        Stacker.Settings stacker
        {
            get {
                if (!stackerLoaded_ || stackerClip_ != editingClip_) {
                    stackerClip_ = editingClip_;
                    stackerLoaded_ = true;
                    stacker_ = Stacker.Load (editingClip_);
                }
                return stacker_;
            }
        }

        void SaveStacker ()
        {
            Stacker.Save (editingClip_, stacker_);
            RaiseStackerChanged ();
        }

        /// <summary>見え方の設定（選んでいる組・ゴースト）を変えたとき。クリップには書かない</summary>
        void RaiseStackerChanged ()
        {
            if (stackerChanged != null) stackerChanged ();
            RepaintView ();
        }

        /// <summary>
        /// 選べる組。組み込み（All・Body・Hands・Rig。中身の無いものは出さない）と、自分で作った組
        /// </summary>
        public List<string> stackerGroupNames
        {
            get {
                List<string> names = new List<string> { kGroupAll };
                foreach (string builtin in new[] { kGroupBody, kGroupHands, kGroupRig }) {
                    if (GroupTargets (builtin).Any ()) names.Add (builtin);
                }
                // 任意のプロパティ（S6）はクリップにカーブがあるときだけ
                if (editingClip_ != null) {
                    EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings (editingClip_);
                    if (bindings.Any (IsFaceBinding)) names.Add (kGroupFace);
                    if (bindings.Any (IsPropertyBinding)) names.Add (kGroupProps);
                }
                foreach (Stacker.Group group in stacker.groups) {
                    if (!names.Contains (group.name)) names.Add (group.name);
                }
                // マスク（S17）も組として選べる（Override の段と同じ範囲でキーを打つ・ずらす）
                foreach (ChannelMask mask in stackerMasks) {
                    string name = MaskGroupName (mask);
                    if (!names.Contains (name)) names.Add (name);
                }
                return names;
            }
        }

        const string kMaskGroupSuffix = " (Mask)";

        static string MaskGroupName (ChannelMask mask)
        {
            return mask.name + kMaskGroupSuffix;
        }

        /// <summary>組として選べるマスク: 同梱の既定のマスクと、Override の段に付けているマスク</summary>
        IEnumerable<ChannelMask> stackerMasks
        {
            get {
                List<ChannelMask> result = ChannelMaskFiles.LoadDefaults ();
                foreach (OverrideClip entry in overrides_) {
                    if (entry != null && entry.mask != null && !result.Contains (entry.mask)) result.Add (entry.mask);
                }
                return result;
            }
        }

        /// <summary>組の名前がマスクの組なら、そのマスク（違えば null）</summary>
        ChannelMask FindMaskGroup (string name)
        {
            if (name == null || !name.EndsWith (kMaskGroupSuffix)) return null;
            return stackerMasks.FirstOrDefault (m => MaskGroupName (m) == name);
        }

        public string stackerGroup
        {
            get {
                string selected = Stacker.selectedGroup;
                return stackerGroupNames.Contains (selected) ? selected : kGroupAll;
            }
            set {
                if (Stacker.selectedGroup == value) return;
                Stacker.selectedGroup = value;
                RaiseStackerChanged ();
            }
        }

        /// <summary>自分で作った組か（消したり作り直したりできる）</summary>
        public bool isCustomStackerGroup
        {
            get { return FindCustomGroup (stackerGroup) != null; }
        }

        /// <summary>
        /// Loop のトグル。サイクルキー（S32）の付け外しと同じ（タイムラインの最後のキーの右クリックと同じ印）。
        /// キーが 2 フレーム未満などで付けられなかったときも、パネルを今の印に合わせ直す
        /// </summary>
        public bool stackerLoop
        {
            get { return cycle; }
            set {
                SetCycle (value);
                if (stackerChanged != null) stackerChanged ();
            }
        }

        public bool stackerGhost
        {
            get { return Stacker.ghost; }
            set {
                if (Stacker.ghost == value) return;
                Stacker.ghost = value;
                RaiseStackerChanged ();
            }
        }

        /// <summary>軌跡を出す幅（前後それぞれ何フレームまで）</summary>
        public int stackerGhostRange
        {
            get { return Stacker.ghostRange; }
            set {
                if (Stacker.ghostRange == Stacker.ClampGhostRange (value)) return;
                Stacker.ghostRange = value;
                RaiseStackerChanged ();
            }
        }

        Stacker.Group FindCustomGroup (string name)
        {
            return stacker.groups.FirstOrDefault (g => g.name == name);
        }

        static bool IsFinger (PoseTarget target)
        {
            string key = target.mirrorKey ?? target.label;
            return kFingerNames.Any (word => key.Contains (word));
        }

        IEnumerable<PoseTarget> GroupTargets (string name)
        {
            switch (name) {
                case kGroupAll: return targets_;
                case kGroupBody: return targets_.Where (t => (t is FkTarget && !IsFinger (t)) || t is BodyPointTarget);
                case kGroupHands: return targets_.Where (t => t is FkTarget && IsFinger (t));
                case kGroupRig: return targets_.Where (t => t is RigSourceTarget);
            }
            Stacker.Group group = FindCustomGroup (name);
            if (group != null) return targets_.Where (t => group.targets.Contains (t.label));
            ChannelMask mask = FindMaskGroup (name);
            if (mask != null) return targets_.Where (t => !IsOutsideMask (stage_, mask, t));
            return Enumerable.Empty<PoseTarget> ();
        }

        static bool IsPropertyBinding (EditorCurveBinding binding)
        {
            return RigPaths.FromProps (binding.path) != null;
        }

        static bool IsFaceBinding (EditorCurveBinding binding)
        {
            return IsPropertyBinding (binding) && binding.propertyName.StartsWith (kBlendShape);
        }

        /// <summary>
        /// 組のカーブ。All は全部（null）。Rig の組は Rig の重みのカーブも含める。Face はブレンドシェイプ、Props は任意のプロパティ全部
        /// </summary>
        System.Predicate<EditorCurveBinding> GroupFilter (string name)
        {
            if (name == kGroupAll) return null;
            if (name == kGroupFace) return IsFaceBinding;
            if (name == kGroupProps) return IsPropertyBinding;
            ChannelMask mask = FindMaskGroup (name);
            if (mask != null && stage_ != null) return binding => stage_.InMask (mask, binding);
            HashSet<string> paths = new HashSet<string> ();
            foreach (PoseTarget target in GroupTargets (name)) target.CollectKeyedPaths (paths);
            if (name == kGroupRig) return binding => paths.Contains (binding.path) || binding.path.StartsWith (RigPaths.kGame);
            return binding => paths.Contains (binding.path);
        }

        /// <summary>
        /// 今の組のポーズキー（昇順）
        /// </summary>
        public int[] stackerKeys
        {
            get { return Stacker.GetPoseKeys (targetClip, GroupFilter (stackerGroup)); }
        }

        /// <summary>
        /// 今のフレームがポーズキーなら、その後ろに同じ姿勢のポーズキーを足して後ろを押し出す（足したキーへ移る）。
        /// ポーズキーでなければ、今のフレームに組のキーを打つ
        /// </summary>
        public void StackerInsert ()
        {
            if (!CanEditClip ()) return;
            System.Predicate<EditorCurveBinding> filter = GroupFilter (stackerGroup);
            int frame = currentFrame;
            if (System.Array.IndexOf (Stacker.GetPoseKeys (targetClip, filter), frame) >= 0) {
                int inserted = -1;
                EditClip (targetClip, "Stacker Insert", () => {
                    inserted = Stacker.InsertAfter (targetClip, frame, filter);
                    return true;
                });
                if (inserted >= 0) SetFrame (inserted);
                return;
            }
            WriteGroupKeys (stackerGroup, "Stacker Key");
            AfterClipEdit ();
        }

        /// <summary>
        /// 組の選択に関係なく、今のフレームで全部（体・手・Rig の重み・表情・任意のプロパティ）にキーを打つ。
        /// キーの自動追加が切のときは、これを押してからそのフレームを編集する
        /// </summary>
        public void KeyAll ()
        {
            if (!CanEditClip ()) return;
            WriteGroupKeys (kGroupAll, "Key All");
            AfterClipEdit ();
        }

        /// <summary>
        /// 今のフレームに組のキーを今の姿勢のまま打つ
        /// </summary>
        void WriteGroupKeys (string group, string undoName)
        {
            int frame = currentFrame;
            ChannelMask mask = FindMaskGroup (group);
            using (CurveWriter writer = BeginCurves (undoName)) {
                // マスクの組は、物の中のマスク外のカーブも打たない
                if (mask != null && stage_ != null) {
                    System.Predicate<EditorCurveBinding> layerFilter = writer.filter;
                    writer.filter = binding => stage_.InMask (mask, binding) && (layerFilter == null || layerFilter (binding));
                }
                foreach (PoseTarget target in GroupTargets (group)) target.WriteKeys (writer);
                if (group == kGroupAll || group == kGroupRig || mask != null) {
                    if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
                }
            }
            // 任意のプロパティは今の値のままキーにする（All・Face・Props。マスクの組はマスクの中だけ）
            if (group == kGroupAll || group == kGroupFace || group == kGroupProps || mask != null) {
                System.Predicate<EditorCurveBinding> propertyFilter = group == kGroupFace ? (System.Predicate<EditorCurveBinding>)IsFaceBinding : IsPropertyBinding;
                if (mask != null && stage_ != null) propertyFilter = binding => IsPropertyBinding (binding) && stage_.InMask (mask, binding);
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (targetClip)) {
                    if (!propertyFilter (binding)) continue;
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (targetClip, binding);
                    CurveEdit.SetKey (targetClip, binding, frame, curve.Evaluate (frame / (float)clock_.rate));
                }
            }
        }

        /// <summary>
        /// 今のフレームのポーズキーを消し、後ろを詰める
        /// </summary>
        public void StackerDelete ()
        {
            if (!CanEditClip ()) return;
            EditClip (targetClip, "Stacker Delete", () => Stacker.Delete (targetClip, currentFrame, GroupFilter (stackerGroup)));
        }

        /// <summary>
        /// frame のポーズキーから次までの間隔を変える（後ろのキーを全部ずらす）
        /// </summary>
        public void StackerSetInterval (int frame, int interval)
        {
            if (!CanEditClip ()) return;
            EditClip (targetClip, "Stacker Interval", () => Stacker.SetInterval (targetClip, frame, interval, GroupFilter (stackerGroup)));
        }

        /// <summary>
        /// 選んでいる物で組を作る（同じ名前があれば中身を置き換える）
        /// </summary>
        public void StackerSaveGroup (string name)
        {
            name = name != null ? name.Trim () : "";
            if (name.Length == 0 || name == kGroupAll || name == kGroupBody || name == kGroupHands || name == kGroupRig || name.EndsWith (kMaskGroupSuffix)) return;
            List<string> members = SelectedTargets ().Select (t => t.label).ToList ();
            if (members.Count == 0) return;
            Stacker.Group group = FindCustomGroup (name);
            if (group == null) stacker.groups.Add (group = new Stacker.Group { name = name });
            group.targets = members;
            Stacker.selectedGroup = name;
            SaveStacker ();
        }

        public void StackerDeleteGroup ()
        {
            Stacker.Group group = FindCustomGroup (stackerGroup);
            if (group == null) return;
            stacker.groups.Remove (group);
            Stacker.selectedGroup = kGroupAll;
            SaveStacker ();
        }

        // ---- ゴースト（選んでいる制御点・骨の前後のフレームの軌跡） ----

        /// <summary>選んだ物の前後のフレームの位置。ghostTargets_ と同じ並び</summary>
        readonly Dictionary<int, Vector3[]> ghostFrames_ = new Dictionary<int, Vector3[]> ();
        readonly List<PoseTarget> ghostTargets_ = new List<PoseTarget> ();
        readonly List<int> ghostWanted_ = new List<int> ();
        readonly List<int> ghostOrder_ = new List<int> ();
        /// <summary>キーや段が変わって取り直すフレーム</summary>
        readonly HashSet<int> ghostStale_ = new HashSet<int> ();
        bool ghostsDirty_ = true;

        /// <summary>
        /// 選んでいる物の、今のフレームの前後（幅は stackerGhostRange）の位置を 1 フレームずつ取る。キーや段が変わったら全部取り直す。
        /// 取るときは段の並びをそのフレームで通し、終わったら今のフレームへ戻す。再生中は出さない。
        /// 前は表示モデルを焼いて重ねていたが、重なって動きが読み取りにくかった。動かしている点の通り道だけを線で出す
        /// </summary>
        void UpdateGhosts ()
        {
            if (stage_ == null || editingClip_ == null || !stackerGhost || clock_.isPlaying) {
                ClearGhosts ();
                return;
            }
            // 選び直したら取り直す（フレームごとの位置は選んだ物の並びで持つ）
            List<PoseTarget> selected = SelectedTargets ().ToList ();
            if (!selected.SequenceEqual (ghostTargets_)) {
                ClearGhosts ();
                ghostTargets_.AddRange (selected);
            }
            // キーや段が変わった: 取り直すまでは前の位置を出しておく（つかんで動かしている間に、遠いフレームの線が消えないように）
            if (ghostsDirty_) {
                ghostStale_.UnionWith (ghostFrames_.Keys);
                ghostsDirty_ = false;
            }
            if (ghostTargets_.Count == 0) return;

            CollectGhostFrames (currentFrame, ghostWanted_);
            // 幅から外れたフレームを捨てる
            foreach (int stale in ghostFrames_.Keys.Where (f => !ghostWanted_.Contains (f)).ToList ()) {
                ghostFrames_.Remove (stale);
                ghostStale_.Remove (stale);
            }

            // 近いフレームから取る。時間が足りなければ残りは次の描画で
            double started = EditorApplication.timeSinceStartup;
            bool evaluated = false;
            bool pending = false;
            float rate = (float)clock_.rate;
            foreach (int wanted in ghostWanted_) {
                if (ghostFrames_.ContainsKey (wanted) && !ghostStale_.Contains (wanted)) continue;
                if (evaluated && EditorApplication.timeSinceStartup - started > kGhostBudgetSeconds) {
                    pending = true;
                    break;
                }
                poseStack_.Evaluate (PoseTime (wanted / rate));
                Vector3[] positions = new Vector3[ghostTargets_.Count];
                for (int i = 0; i < positions.Length; i++) positions[i] = ghostTargets_[i].framePosition;
                ghostFrames_[wanted] = positions;
                ghostStale_.Remove (wanted);
                evaluated = true;
            }
            // 今のフレームの姿勢へ戻す（イベントは出さない。描画の途中なので）
            if (evaluated) poseStack_.Evaluate (PoseTime (CurrentTime ()));
            if (evaluated || pending) RepaintView ();
        }

        /// <summary>
        /// 今のフレームの前後のフレーム（近い順。前と後ろを交互に）。クリップの頭から最後のキーまでに収める
        /// </summary>
        void CollectGhostFrames (int frame, List<int> result)
        {
            result.Clear ();
            int last = GetLastKeyFrame ();
            int range = stackerGhostRange;
            for (int distance = 1; distance <= range; distance++) {
                if (frame - distance >= 0 && frame - distance <= last) result.Add (frame - distance);
                if (frame + distance <= last) result.Add (frame + distance);
            }
        }

        void ClearGhosts ()
        {
            ghostFrames_.Clear ();
            ghostTargets_.Clear ();
            ghostStale_.Clear ();
        }

        /// <summary>
        /// ハンドルの前に描く（Repaint のとき）。選んだ物ごとに前後のフレームの位置を線でつなぎ、フレームごとに点を打つ。
        /// 前のフレームは青、後ろは橙で、離れるほど薄い。その物のキーのフレームは点を大きくする（キーの間の動きの速さが点の間隔で見える）
        /// </summary>
        void DrawGhostTrails ()
        {
            if (ghostTargets_.Count == 0 || ghostFrames_.Count == 0 || !stackerGhost || clock_.isPlaying) return;
            int frame = currentFrame;
            int range = stackerGhostRange;
            ghostOrder_.Clear ();
            foreach (int f in ghostFrames_.Keys) {
                if (Mathf.Abs (f - frame) <= range) ghostOrder_.Add (f);
            }
            // 今のフレームは今の位置（つかんで動かしている間もついてくる）
            ghostOrder_.Add (frame);
            ghostOrder_.Sort ();
            if (ghostOrder_.Count < 2) return;

            Color handleColor = Handles.color;
            Vector3[] points = new Vector3[ghostOrder_.Count];
            Color[] colors = new Color[ghostOrder_.Count];
            for (int t = 0; t < ghostTargets_.Count; t++) {
                PoseTarget target = ghostTargets_[t];
                if (target.gameObject == null) continue;
                int[] keys = GetTargetKeyFrames (target);
                for (int i = 0; i < ghostOrder_.Count; i++) {
                    int f = ghostOrder_[i];
                    points[i] = f == frame ? target.framePosition : ghostFrames_[f][t];
                    colors[i] = GhostColor (f, frame, range);
                }
                Handles.color = Color.white;
                Handles.DrawAAPolyLine (kGhostLineWidth, colors, points);
                for (int i = 0; i < ghostOrder_.Count; i++) {
                    int f = ghostOrder_[i];
                    if (f == frame) continue;
                    bool key = System.Array.IndexOf (keys, f) >= 0;
                    Handles.color = colors[i];
                    float size = HandleUtility.GetHandleSize (points[i]) * (key ? kGhostKeyDotSize : kGhostDotSize);
                    Handles.DotHandleCap (0, points[i], Quaternion.identity, size, EventType.Repaint);
                }
            }
            Handles.color = handleColor;
        }

        static Color GhostColor (int frame, int current, int range)
        {
            if (frame == current) return kGhostCurrent;
            int distance = Mathf.Abs (frame - current);
            Color color = frame < current ? kGhostPrevious : kGhostNext;
            color.a = range <= 1 ? kGhostAlphaNear : Mathf.Lerp (kGhostAlphaNear, kGhostAlphaFar, (distance - 1) / (float)(range - 1));
            return color;
        }

        void DisposeGhosts ()
        {
            ClearGhosts ();
        }
    }

}
