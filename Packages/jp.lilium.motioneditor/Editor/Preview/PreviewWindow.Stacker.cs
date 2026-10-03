using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Stacker（S12）の窓の側。組（骨のまとまり）ごとのポーズキーの一覧・挿入・削除・間隔、ループ、前後のフレームの残像（ゴースト）。
    /// 組と設定はクリップごとに .meta へ保存する（Stacker.Settings）。パネルは StackerOverlay
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
        static readonly string[] kFingerWords = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static readonly Color kGhostPrevious = new Color (0.35f, 0.65f, 1f, 1);
        static readonly Color kGhostNext = new Color (1f, 0.6f, 0.3f, 1);
        /// <summary>隣のフレームの残像の濃さ。離れるほど薄くする</summary>
        const float kGhostAlphaNear = 0.18f;
        const float kGhostAlphaFar = 0.03f;
        /// <summary>1 回の描画で残像を焼くのに使ってよい時間。足りなければ次の描画で続きを焼く</summary>
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
                    ghostsDirty_ = true;
                }
                return stacker_;
            }
        }

        void SaveStacker ()
        {
            Stacker.Save (editingClip_, stacker_);
            ghostsDirty_ = true;
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
                return names;
            }
        }

        public string stackerGroup
        {
            get {
                string selected = stacker.selected;
                return stackerGroupNames.Contains (selected) ? selected : kGroupAll;
            }
            set {
                if (stacker.selected == value) return;
                stacker.selected = value;
                SaveStacker ();
            }
        }

        /// <summary>自分で作った組か（消したり作り直したりできる）</summary>
        public bool isCustomStackerGroup
        {
            get { return FindCustomGroup (stackerGroup) != null; }
        }

        public bool stackerLoop
        {
            get { return stacker.loop; }
            set {
                if (stacker.loop == value) return;
                stacker.loop = value;
                SaveStacker ();
                if (value) SyncStackerLoop ("Stacker Loop");
            }
        }

        public bool stackerGhost
        {
            get { return stacker.ghost; }
            set {
                if (stacker.ghost == value) return;
                stacker.ghost = value;
                SaveStacker ();
            }
        }

        /// <summary>残像を出す幅（前後それぞれ何フレームまで。1〜16）</summary>
        public int stackerGhostRange
        {
            get { return Stacker.ClampGhostRange (stacker.ghostRange); }
            set {
                value = Stacker.ClampGhostRange (value);
                if (stacker.ghostRange == value) return;
                stacker.ghostRange = value;
                SaveStacker ();
            }
        }

        Stacker.Group FindCustomGroup (string name)
        {
            return stacker.groups.FirstOrDefault (g => g.name == name);
        }

        static bool IsFinger (PoseTarget target)
        {
            string key = target.mirrorKey ?? target.label;
            return kFingerWords.Any (word => key.Contains (word));
        }

        IEnumerable<PoseTarget> GroupTargets (string name)
        {
            switch (name) {
                case kGroupAll: return targets_;
                case kGroupBody: return targets_.Where (t => (t is FkTarget && !IsFinger (t)) || t is IkTarget || t is BodyPointTarget);
                case kGroupHands: return targets_.Where (t => t is FkTarget && IsFinger (t));
                case kGroupRig: return targets_.Where (t => t is RigSourceTarget);
            }
            Stacker.Group group = FindCustomGroup (name);
            if (group == null) return Enumerable.Empty<PoseTarget> ();
            return targets_.Where (t => group.targets.Contains (t.label));
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
                RecordClipUndo ("Stacker Insert");
                int inserted = Stacker.InsertAfter (targetClip, frame, filter);
                AfterStackerEdit ("Stacker Insert");
                if (inserted >= 0) SetFrame (inserted);
                return;
            }
            WriteGroupKeys (stackerGroup, "Stacker Key");
            AfterStackerEdit ("Stacker Key");
        }

        /// <summary>
        /// 組の選択に関係なく、今のフレームで全部（体・手・Rig の重み・表情・任意のプロパティ）にキーを打つ。
        /// キーの自動追加が切のときは、これを押してからそのフレームを編集する
        /// </summary>
        public void KeyAll ()
        {
            if (!CanEditClip ()) return;
            WriteGroupKeys (kGroupAll, "Key All");
            AfterStackerEdit ("Key All");
        }

        /// <summary>
        /// 今のフレームに組のキーを今の姿勢のまま打つ
        /// </summary>
        void WriteGroupKeys (string group, string undoName)
        {
            int frame = currentFrame;
            using (CurveWriter writer = BeginCurves (undoName)) {
                foreach (PoseTarget target in GroupTargets (group)) target.WriteKeys (writer);
                if (group == kGroupAll || group == kGroupRig) {
                    if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
                }
            }
            // 任意のプロパティは今の値のままキーにする（All・Face・Props）
            if (group == kGroupAll || group == kGroupFace || group == kGroupProps) {
                System.Predicate<EditorCurveBinding> propertyFilter = group == kGroupFace ? (System.Predicate<EditorCurveBinding>)IsFaceBinding : IsPropertyBinding;
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
            RecordClipUndo ("Stacker Delete");
            if (Stacker.Delete (targetClip, currentFrame, GroupFilter (stackerGroup))) AfterStackerEdit ("Stacker Delete");
        }

        /// <summary>
        /// frame のポーズキーから次までの間隔を変える（後ろのキーを全部ずらす）
        /// </summary>
        public void StackerSetInterval (int frame, int interval)
        {
            if (!CanEditClip ()) return;
            RecordClipUndo ("Stacker Interval");
            if (Stacker.SetInterval (targetClip, frame, interval, GroupFilter (stackerGroup))) AfterStackerEdit ("Stacker Interval");
        }

        void AfterStackerEdit (string undoName)
        {
            if (stacker.loop) Stacker.SyncLoop (targetClip, GroupFilter (stackerGroup));
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            SamplePose ();
        }

        /// <summary>
        /// 選んでいる物で組を作る（同じ名前があれば中身を置き換える）
        /// </summary>
        public void StackerSaveGroup (string name)
        {
            name = name != null ? name.Trim () : "";
            if (name.Length == 0 || name == kGroupAll || name == kGroupBody || name == kGroupHands || name == kGroupRig) return;
            List<string> members = selection_.Select (FindTarget).Where (t => t != null).Select (t => t.label).ToList ();
            if (members.Count == 0) return;
            Stacker.Group group = FindCustomGroup (name);
            if (group == null) stacker.groups.Add (group = new Stacker.Group { name = name });
            group.targets = members;
            stacker.selected = name;
            SaveStacker ();
        }

        public void StackerDeleteGroup ()
        {
            Stacker.Group group = FindCustomGroup (stackerGroup);
            if (group == null) return;
            stacker.groups.Remove (group);
            stacker.selected = kGroupAll;
            SaveStacker ();
        }

        /// <summary>
        /// ループが入っていれば、先頭のポーズキーを末尾へ写す（キーが変わるたびに呼ぶ。値が同じなら何もしない）
        /// </summary>
        void SyncStackerLoop (string undoName)
        {
            if (!stackerLoaded_ || editingClip_ == null || !stacker.loop || !CanEditClip ()) return;
            System.Predicate<EditorCurveBinding> filter = GroupFilter (stackerGroup);
            RecordClipUndo (undoName);
            if (Stacker.SyncLoop (targetClip, filter)) {
                keyFrames_ = null;
                if (stage_ != null) stage_.InvalidateClipCurves ();
                SamplePose ();
            }
        }

        // ---- ゴースト（前後のフレームの残像。オニオンスキン） ----

        struct GhostMesh
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            /// <summary>焼いたメッシュ（使い回しの置き場へ返す）。false は MeshFilter の共有メッシュ</summary>
            public bool baked;
        }

        /// <summary>フレームごとに焼いた残像。前後の幅から外れたフレームは返し、足りないフレームだけ焼き足す</summary>
        readonly Dictionary<int, List<GhostMesh>> ghostFrames_ = new Dictionary<int, List<GhostMesh>> ();
        readonly List<Mesh> ghostPool_ = new List<Mesh> ();
        readonly List<int> ghostWanted_ = new List<int> ();
        Material ghostMaterial_;
        Material ghostDepth_;
        MaterialPropertyBlock ghostBlock_;
        bool ghostsDirty_ = true;
        int ghostFrame_ = -1;

        /// <summary>
        /// 今のフレームの前後（幅は stackerGhostRange）を 1 フレームずつ焼く。キーや段が変わったら全部焼き直す。
        /// 焼くときは段の並びをそのフレームで通し、終わったら今のフレームへ戻す。再生中は出さない
        /// </summary>
        void UpdateGhosts ()
        {
            if (stage_ == null || editingClip_ == null || !stacker.ghost || clock_.isPlaying) {
                ClearGhosts ();
                return;
            }
            if (ghostsDirty_) {
                ClearGhosts ();
                ghostsDirty_ = false;
            }

            int frame = currentFrame;
            ghostFrame_ = frame;
            CollectGhostFrames (frame, ghostWanted_);
            // 幅から外れたフレームを返す
            foreach (int stale in ghostFrames_.Keys.Where (f => !ghostWanted_.Contains (f)).ToList ()) {
                ReleaseGhostFrame (stale);
            }

            // 近いフレームから焼く。時間が足りなければ残りは次の描画で
            double started = EditorApplication.timeSinceStartup;
            bool evaluated = false;
            bool pending = false;
            float rate = (float)clock_.rate;
            foreach (int wanted in ghostWanted_) {
                if (ghostFrames_.ContainsKey (wanted)) continue;
                if (evaluated && EditorApplication.timeSinceStartup - started > kGhostBudgetSeconds) {
                    pending = true;
                    break;
                }
                poseStack_.Evaluate (wanted / rate);
                List<(Mesh, Matrix4x4)> baked = new List<(Mesh, Matrix4x4)> ();
                // 空いているメッシュ（置き場）を頭から使い、使った分は置き場から外す
                stage_.BakeDisplayMeshes (ghostPool_, 0, baked);
                List<GhostMesh> meshes = new List<GhostMesh> ();
                foreach ((Mesh mesh, Matrix4x4 matrix) item in baked) {
                    bool owned = ghostPool_.Contains (item.mesh);
                    if (owned) ghostPool_.Remove (item.mesh);
                    meshes.Add (new GhostMesh { mesh = item.mesh, matrix = item.matrix, baked = owned });
                }
                ghostFrames_[wanted] = meshes;
                evaluated = true;
            }
            // 今のフレームの姿勢へ戻す（イベントは出さない。描画の途中なので）
            if (evaluated) poseStack_.Evaluate (CurrentTime ());
            if (pending) RepaintView ();
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

        void ReleaseGhostFrame (int frame)
        {
            List<GhostMesh> meshes;
            if (!ghostFrames_.TryGetValue (frame, out meshes)) return;
            foreach (GhostMesh ghost in meshes) {
                if (ghost.baked && ghost.mesh != null) ghostPool_.Add (ghost.mesh);
            }
            ghostFrames_.Remove (frame);
        }

        void ClearGhosts ()
        {
            foreach (int frame in ghostFrames_.Keys.ToList ()) ReleaseGhostFrame (frame);
            ghostFrame_ = -1;
        }

        /// <summary>
        /// 描く直前に呼ぶ。残像を半透明で重ねる（前のフレームは青、後ろは橙。離れるほど薄い）
        /// </summary>
        void DrawGhosts ()
        {
            if (ghostFrames_.Count == 0 || ghostFrame_ < 0) return;
            if (ghostMaterial_ == null) ghostMaterial_ = CreateGhostMaterial (false);
            if (ghostDepth_ == null) ghostDepth_ = CreateGhostMaterial (true);
            if (ghostBlock_ == null) ghostBlock_ = new MaterialPropertyBlock ();
            int range = stackerGhostRange;
            // 遠いフレームから描く（近いものが上に乗る）
            foreach (KeyValuePair<int, List<GhostMesh>> pair in ghostFrames_.OrderByDescending (p => Mathf.Abs (p.Key - ghostFrame_))) {
                int distance = Mathf.Abs (pair.Key - ghostFrame_);
                if (distance == 0 || distance > range) continue;
                Color color = pair.Key < ghostFrame_ ? kGhostPrevious : kGhostNext;
                color.a = range <= 1 ? kGhostAlphaNear : Mathf.Lerp (kGhostAlphaNear, kGhostAlphaFar, (distance - 1) / (float)(range - 1));
                ghostBlock_.SetColor ("_Color", color);
                foreach (GhostMesh ghost in pair.Value) {
                    if (ghost.mesh == null) continue;
                    for (int sub = 0; sub < ghost.mesh.subMeshCount; sub++) {
                        stage_.renderUtility.DrawMesh (ghost.mesh, ghost.matrix, ghostDepth_, sub);
                        stage_.renderUtility.DrawMesh (ghost.mesh, ghost.matrix, ghostMaterial_, sub, ghostBlock_);
                    }
                }
            }
        }

        /// <summary>
        /// 残像は 2 回に分けて描く。先に奥行きだけを書き（色は変えない）、次に一番手前の面だけを半透明で塗る。
        /// 奥行きを書かずに塗ると、服の下の体など重なった面が全部塗り重なって濃くなる
        /// </summary>
        static Material CreateGhostMaterial (bool depthOnly)
        {
            Material material = new Material (Shader.Find ("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor ("_Color", kGhostPrevious);
            material.SetInt ("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
            if (depthOnly) {
                material.SetInt ("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                material.SetInt ("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                material.SetInt ("_ZWrite", 1);
                material.SetInt ("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                // キャラ（不透明）の後に描く。体の奥に入った残像は隠れる
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else {
                material.SetInt ("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt ("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt ("_ZWrite", 0);
                material.SetInt ("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Equal);
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1;
            }
            return material;
        }

        void DisposeGhosts ()
        {
            ClearGhosts ();
            foreach (Mesh mesh in ghostPool_) {
                if (mesh != null) Object.DestroyImmediate (mesh);
            }
            ghostPool_.Clear ();
            if (ghostMaterial_ != null) Object.DestroyImmediate (ghostMaterial_);
            if (ghostDepth_ != null) Object.DestroyImmediate (ghostDepth_);
        }
    }

}
