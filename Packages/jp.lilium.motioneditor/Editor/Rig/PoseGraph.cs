using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 今の骨の値をグラフへ入れるジョブ。**拘束は AnimationStream に働く**ので、Transform へ書いた姿勢は
    /// 解いた瞬間に消える。姿勢をストリームへ入れる経路はここだけ（S4）。
    ///
    /// 人型の骨は **muscle**（ゲームがクリップで流すのと同じ持ち方）、人型でない骨（武器など）は Transform の値で入れる。
    /// 人型の骨を Transform で書くと、ゲーム側の後段（muscle を反転する左右反転）が効かず、ねじりも配り直される（2026-09-18 実測）
    /// </summary>
    public struct TransformInjectJob : IAnimationJob
    {
        public NativeArray<TransformStreamHandle> handles;
        public NativeArray<Vector3> positions;
        public NativeArray<Quaternion> rotations;
        public NativeArray<Vector3> scales;
        /// <summary>Humanoid の muscle（HumanTrait の並び）。人型のキャラだけ中身が入る</summary>
        public NativeArray<MuscleHandle> muscleHandles;
        public NativeArray<float> muscles;
        public Vector3 bodyPosition;
        public Quaternion bodyRotation;
        public bool writeBody;

        public void ProcessRootMotion (AnimationStream stream)
        {
        }

        public void ProcessAnimation (AnimationStream stream)
        {
            // 人型の骨は muscle で決まる。体の位置・向きも一緒に入れる
            if (stream.isHumanStream && muscleHandles.Length > 0) {
                AnimationHumanStream human = stream.AsHuman ();
                for (int i = 0; i < muscleHandles.Length && i < muscles.Length; i++) human.SetMuscle (muscleHandles[i], muscles[i]);
                if (writeBody) {
                    human.bodyLocalPosition = bodyPosition;
                    human.bodyLocalRotation = bodyRotation;
                }
            }
            for (int i = 0; i < handles.Length; i++) {
                handles[i].SetLocalPosition (stream, positions[i]);
                handles[i].SetLocalRotation (stream, rotations[i]);
                handles[i].SetLocalScale (stream, scales[i]);
            }
        }
    }

    /// <summary>
    /// グラフの後ろに処理を足す受け口。ゲーム固有の後段（左右反転など）はパッケージに持たないので、ゲーム側がここで足す
    /// </summary>
    public interface IPoseGraphHook
    {
        /// <summary>
        /// この後段が属する段（[PoseLayer] を付けた Component の型）。null なら常に有効。
        /// 段の 👁 を落としたときに、この後段も素通しになる
        /// </summary>
        System.Type layerType { get; }

        /// <summary>入ってきた根（context.input）の後ろに処理を足して、新しい根を返す（足さないなら input をそのまま返す）</summary>
        Playable Append (PoseGraphContext context);

        /// <summary>解く直前。足した Playable に、そのときの表示の設定（左右の向きなど）を入れる</summary>
        void Update (Playable appended, PoseGraphState state);

        /// <summary>グラフを捨てるとき。足した処理が持っている物（NativeArray など）を片付ける</summary>
        void Release (Playable appended);
    }

    /// <summary>後段をグラフのどこへ差すか</summary>
    public enum PoseGraphHookStage
    {
        /// <summary>ゲームの Rigging の後ろ（既定。左右反転など、Rig の結果に掛ける処理）</summary>
        AfterRig,
        /// <summary>姿勢を入れた直後、ゲームの Rigging の前（Rig の拘束が読む骨を先に決める処理）</summary>
        BeforeRig,
    }

    /// <summary>
    /// 差す位置を選ぶ後段。これを実装しない後段は Rig の後ろ（<see cref="PoseGraphHookStage.AfterRig"/>）に入る。
    /// 同じ位置の後段どうしは登録順
    /// </summary>
    public interface IPoseGraphHookStage : IPoseGraphHook
    {
        PoseGraphHookStage stage { get; }
    }

    /// <summary>
    /// 後段を足すときに渡すもの
    /// </summary>
    public struct PoseGraphContext
    {
        public PlayableGraph graph;
        /// <summary>ここまでの根。この後ろに足す</summary>
        public Playable input;
        /// <summary>表示モデル（窓が複製したキャラ）</summary>
        public GameObject model;
        /// <summary>グラフが書き出す Animator。ストリームの束縛（BindStreamTransform）はこれで行う</summary>
        public Animator animator;
        internal System.Func<Transform, Pose> getRest;

        /// <summary>
        /// 骨の基準姿勢（キャラを置いた直後の local の位置と向き）。表示モデルの骨は窓が動かしているので、
        /// ゲームが組み立て時に読む値（基準姿勢）はこちらから取る。窓が知らない骨は今の値
        /// </summary>
        public Pose GetRestLocalPose (Transform transform)
        {
            if (transform == null) return Pose.identity;
            if (getRest != null) return getRest (transform);
            transform.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            return new Pose (position, rotation);
        }
    }

    /// <summary>
    /// 解くときの状態。ゲーム固有の後段（左右反転など）が見る。
    /// 左右反転のような**ゲームの設定は、ゲーム側の段（Component）の値を見る**ので、ここには持たない
    /// </summary>
    public struct PoseGraphState
    {
        /// <summary>この後段の段が、このフレームの評価に入っているか（👁 を落とすと false）</summary>
        public bool active;
        /// <summary>表示モデル（窓が複製したキャラ）</summary>
        public GameObject model;
        /// <summary>グラフが書き出す Animator</summary>
        public Animator animator;
    }

    public static class PoseGraphHooks
    {
        static readonly List<IPoseGraphHook> hooks_ = new List<IPoseGraphHook> ();

        public static void Register (IPoseGraphHook hook)
        {
            if (hook != null && !hooks_.Contains (hook)) hooks_.Add (hook);
        }

        public static void Unregister (IPoseGraphHook hook)
        {
            hooks_.Remove (hook);
        }

        public static IReadOnlyList<IPoseGraphHook> hooks
        {
            get { return hooks_; }
        }

        internal static PoseGraphHookStage StageOf (IPoseGraphHook hook)
        {
            IPoseGraphHookStage staged = hook as IPoseGraphHookStage;
            return staged != null ? staged.stage : PoseGraphHookStage.AfterRig;
        }

        internal static Playable Append (PoseGraphContext context, List<KeyValuePair<IPoseGraphHook, Playable>> appended, PoseGraphHookStage stage = PoseGraphHookStage.AfterRig)
        {
            foreach (IPoseGraphHook hook in hooks_) {
                if (StageOf (hook) != stage) continue;
                try {
                    Playable next = hook.Append (context);
                    // 足さなかった（input をそのまま返した）受け口は、後で Update / Release を呼ばない
                    if (!next.IsValid () || next.GetHandle () == context.input.GetHandle ()) continue;
                    context.input = next;
                    if (appended != null) appended.Add (new KeyValuePair<IPoseGraphHook, Playable> (hook, next));
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            return context.input;
        }

        internal static void Update (List<KeyValuePair<IPoseGraphHook, Playable>> appended, GameObject model, Animator animator, System.Func<System.Type, bool> isLayerActive)
        {
            foreach (KeyValuePair<IPoseGraphHook, Playable> pair in appended) {
                try {
                    System.Type layer = pair.Key.layerType;
                    bool active = layer == null || isLayerActive == null || isLayerActive (layer);
                    pair.Key.Update (pair.Value, new PoseGraphState { active = active, model = model, animator = animator });
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
        }

        internal static void Release (List<KeyValuePair<IPoseGraphHook, Playable>> appended)
        {
            foreach (KeyValuePair<IPoseGraphHook, Playable> pair in appended) {
                try {
                    pair.Key.Release (pair.Value);
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            appended.Clear ();
        }
    }

    /// <summary>
    /// 表示モデルの姿勢を作るグラフ。**姿勢を入れる → ゲームの Rigging を掛ける**の順で、ゲームの実行時と同じ並び。
    /// Rigging を持たないキャラでは姿勢を入れるだけ。後段は受け口から足す（Rig の前: <see cref="PoseGraphHookStage.BeforeRig"/>、後ろ: 左右反転など）
    /// </summary>
    public sealed class PoseGraph : System.IDisposable
    {
        readonly Animator animator_;
        IRigPreview preview_;
        readonly Transform[] bones_;
        readonly GameObject model_;
        readonly List<KeyValuePair<IPoseGraphHook, Playable>> appended_ = new List<KeyValuePair<IPoseGraphHook, Playable>> ();

        /// <summary>その段（Component の型）がこのフレームの評価に入っているか。後段の受け口へ渡す</summary>
        public System.Func<System.Type, bool> isLayerActive;

        PlayableGraph graph_;
        AnimationScriptPlayable inject_;
        NativeArray<TransformStreamHandle> handles_;
        NativeArray<Vector3> positions_;
        NativeArray<Quaternion> rotations_;
        NativeArray<Vector3> scales_;
        NativeArray<MuscleHandle> muscleHandles_;
        NativeArray<float> muscles_;
        /// <summary>muscleHandles_[i] に入れる値が HumanPose.muscles の何番目か</summary>
        int[] muscleSources_ = new int[0];
        HumanPoseHandler humanReader_;
        HumanPose humanPose_;

        /// <summary>
        /// グラフを作る。Animator が無いときは null
        /// </summary>
        /// <param name="bones">毎回ストリームへ入れる骨（表示モデルの Animator の下にあるもの）。人型の骨は muscle で入れるので除く</param>
        /// <param name="getRest">骨の基準姿勢（local）。後段が組み立て時に読む。null なら今の値</param>
        public static PoseGraph Create (Animator animator, GameObject model, IReadOnlyList<Transform> bones, System.Func<Transform, Pose> getRest = null)
        {
            if (animator == null || bones == null) return null;
            // 人型の骨は muscle で入れる（Transform で書くと、ゲーム側の反転（muscle を反転する）が効かなくなる）
            HashSet<Transform> skip = new HashSet<Transform> ();
            if (animator.isHuman && animator.avatar != null) {
                foreach (HumanBodyBones bone in System.Enum.GetValues (typeof (HumanBodyBones))) {
                    if (bone == HumanBodyBones.LastBone) continue;
                    Transform t = animator.GetBoneTransform (bone);
                    if (t != null) skip.Add (t);
                }
            }
            List<Transform> valid = new List<Transform> ();
            foreach (Transform bone in bones) {
                // Animator 自身の Transform はストリームの根なので入れない
                if (bone == null || bone == animator.transform || !bone.IsChildOf (animator.transform)) continue;
                if (skip.Contains (bone)) continue;
                valid.Add (bone);
            }
            bool human = animator.isHuman && animator.avatar != null;
            if (valid.Count == 0 && !human) return null;

            PoseGraph graph = null;
            try {
                graph = new PoseGraph (animator, model, valid, getRest);
                return graph;
            } catch (System.Exception exception) {
                // 途中で落ちても、確保した物（NativeArray・グラフ）は返す
                if (graph != null) graph.Dispose ();
                Debug.LogException (exception);
                return null;
            }
        }

        PoseGraph (Animator animator, GameObject model, List<Transform> bones, System.Func<Transform, Pose> getRest)
        {
            animator_ = animator;
            model_ = model;
            bones_ = bones.ToArray ();
            // カリングが入っていると、グラフを流しても骨が動かない（画面の外扱いで黙って飛ばされる）
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            handles_ = new NativeArray<TransformStreamHandle> (bones_.Length, Allocator.Persistent);
            positions_ = new NativeArray<Vector3> (bones_.Length, Allocator.Persistent);
            rotations_ = new NativeArray<Quaternion> (bones_.Length, Allocator.Persistent);
            scales_ = new NativeArray<Vector3> (bones_.Length, Allocator.Persistent);
            for (int i = 0; i < bones_.Length; i++) handles_[i] = animator.BindStreamTransform (bones_[i]);
            CreateMuscles (animator);

            try {
                Build (model, getRest);
            } catch {
                // 確保した物（NativeArray・グラフ）を返してから投げ直す
                Dispose ();
                throw;
            }
        }

        void Build (GameObject model, System.Func<Transform, Pose> getRest)
        {
            graph_ = PlayableGraph.Create ("Mkt Pose Graph");
            graph_.SetTimeUpdateMode (DirectorUpdateMode.Manual);
            inject_ = AnimationScriptPlayable.Create (graph_, new TransformInjectJob {
                handles = handles_,
                positions = positions_,
                rotations = rotations_,
                scales = scales_,
                muscleHandles = muscleHandles_,
                muscles = muscles_,
            });

            // 姿勢を入れる → Rig の前の後段 → ゲームの Rigging → Rig の後ろの後段
            PoseGraphContext context = new PoseGraphContext {
                graph = graph_,
                input = inject_,
                model = model,
                animator = animator_,
                getRest = getRest,
            };
            Playable root = PoseGraphHooks.Append (context, appended_, PoseGraphHookStage.BeforeRig);
            preview_ = RigProbe.CreatePreview (model);
            if (preview_ != null) root = preview_.Build (graph_, root);
            context.input = root;
            root = PoseGraphHooks.Append (context, appended_, PoseGraphHookStage.AfterRig);

            AnimationPlayableOutput output = AnimationPlayableOutput.Create (graph_, "Mkt Pose", animator_);
            output.SetSourcePlayable (root);
        }

        /// <summary>
        /// muscle の受け口を作る。**MuscleHandle の並びは HumanTrait と違う**（キャラ A で 95 本中 40 本ずれていた）ので、名前で対応させる
        /// </summary>
        void CreateMuscles (Animator animator)
        {
            if (!animator.isHuman || animator.avatar == null) return;
            MuscleHandle[] ordered;
            if (!MapMuscles (out ordered, out muscleSources_)) return;

            muscleHandles_ = new NativeArray<MuscleHandle> (ordered, Allocator.Persistent);
            muscles_ = new NativeArray<float> (ordered.Length, Allocator.Persistent);
            humanReader_ = new HumanPoseHandler (animator.avatar, animator.transform);
            humanPose_ = new HumanPose ();
        }

        /// <summary>
        /// ストリームの muscle の受け口と、それぞれに入れる HumanPose.muscles の番号を名前で対応させる。
        /// 対応しないものがあればエラーを出して false（1 本でも落ちると左右反転の結果が非対称になるので、黙って進めない）
        /// </summary>
        internal static bool MapMuscles (out MuscleHandle[] handles, out int[] sources)
        {
            MuscleHandle[] all = new MuscleHandle[MuscleHandle.muscleHandleCount];
            MuscleHandle.GetMuscleHandles (all);
            string[] names = HumanTrait.MuscleName;
            Dictionary<string, int> index = new Dictionary<string, int> ();
            for (int i = 0; i < names.Length; i++) index[Normalize (names[i])] = i;

            List<MuscleHandle> ordered = new List<MuscleHandle> ();
            List<int> targets = new List<int> ();
            HashSet<int> used = new HashSet<int> ();
            List<string> lost = new List<string> ();
            foreach (MuscleHandle handle in all) {
                int target;
                if (!index.TryGetValue (Normalize (handle.name), out target) || !used.Add (target)) {
                    lost.Add (handle.name);
                    continue;
                }
                ordered.Add (handle);
                targets.Add (target);
            }
            handles = ordered.ToArray ();
            sources = targets.ToArray ();
            if (lost.Count > 0) {
                Debug.LogError ("MotionEditor: " + Tr ("POSE_GRAPH_UNMAPPED_MUSCLES", lost.Count, string.Join (Tr ("GENERIC_IMPORT_LIST_SEPARATOR"), lost)));
                handles = new MuscleHandle[0];
                sources = new int[0];
                return false;
            }
            return handles.Length > 0;
        }

        /// <summary>
        /// muscle の名前をそろえる。**MuscleHandle と HumanTrait は名前の付け方が違う**
        /// （指は "LeftHand.Thumb.1 Stretched" と "Left Thumb 1 Stretched"。2026-09-18 実測で 95 本中 40 本）
        /// </summary>
        static string Normalize (string name)
        {
            // 指は "LeftHand.Thumb.1 Stretched" と "Left Thumb 1 Stretched"、足指は "LeftFoot.Thumb..." と "Left Toes..."。
            // 記号と空白を落とすだけでは合わないので、先頭の手足の書き方だけそろえる
            string text = name
                .Replace ("LeftHand.", "Left ")
                .Replace ("RightHand.", "Right ")
                .Replace ("LeftFoot.", "Left ")
                .Replace ("RightFoot.", "Right ")
                .Replace (".", " ");
            System.Text.StringBuilder result = new System.Text.StringBuilder (text.Length);
            foreach (char c in text) {
                if (char.IsLetterOrDigit (c)) result.Append (char.ToLowerInvariant (c));
            }
            return result.ToString ();
        }

        /// <summary>Rigging を通しているか（キャラが Rig を持ち、Animation Rigging が入っている）</summary>
        public bool hasRig
        {
            get { return preview_ != null; }
        }

        /// <summary>
        /// 今の表示モデルから姿勢を読んで 1 回解く
        /// </summary>
        public void Evaluate ()
        {
            ReadDisplayPose ();
            EvaluateInternal ();
        }

        /// <summary>
        /// 表示モデルから姿勢を読む。ステージと同じく Animator の GameObject を原点・無回転に置いて読む
        /// （置いたまま読むと、体の位置・向きに Animator の置き方が混ざる。prefab の中で Y270° 回っているキャラがいる）
        /// </summary>
        void ReadDisplayPose ()
        {
            if (humanReader_ == null) return;
            Transform root = animator_.transform;
            root.GetPositionAndRotation (out Vector3 position, out Quaternion rotation);
            root.SetPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                humanReader_.GetHumanPose (ref humanPose_);
            }
            finally {
                root.SetPositionAndRotation (position, rotation);
            }
        }

        /// <summary>
        /// 渡された姿勢で 1 回解く。Humanoid の段が作った姿勢をそのまま渡すと、表示モデルから読み直す分の
        /// ねじれの取り落とし（Humanoid の往復）が 1 回減る。姿勢は Animator の GameObject を原点・無回転に置いたときの値
        /// </summary>
        public void Evaluate (ref HumanPose pose)
        {
            if (humanReader_ != null && pose.muscles != null && pose.muscles.Length > 0) humanPose_ = pose;
            else ReadDisplayPose ();
            EvaluateInternal ();
        }

        /// <summary>
        /// 骨の値と muscle を入れて 1 回解く。Rig の重みは表示モデルの Rig に入っている値をそのまま使う
        /// </summary>
        void EvaluateInternal ()
        {
            if (!graph_.IsValid () || animator_ == null) return;
            for (int i = 0; i < bones_.Length; i++) {
                Transform bone = bones_[i];
                if (bone == null) continue;
                bone.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                positions_[i] = position;
                rotations_[i] = rotation;
                scales_[i] = bone.localScale;
            }

            if (humanReader_ != null) {
                for (int i = 0; i < muscleSources_.Length; i++) {
                    int source = muscleSources_[i];
                    if (source < humanPose_.muscles.Length) muscles_[i] = humanPose_.muscles[source];
                }
                // ストリームの体の位置・向きは Animator から見た値。原点に置いて読んだ値がそのまま入る
                TransformInjectJob job = inject_.GetJobData<TransformInjectJob> ();
                job.writeBody = true;
                job.bodyPosition = humanPose_.bodyPosition;
                job.bodyRotation = humanPose_.bodyRotation;
                inject_.SetJobData (job);
            }

            if (preview_ != null) preview_.Update (graph_);
            if (appended_.Count > 0) PoseGraphHooks.Update (appended_, model_, animator_, isLayerActive);
            graph_.Evaluate (0);
        }

        public void Dispose ()
        {
            PoseGraphHooks.Release (appended_);
            if (preview_ != null) preview_.Stop ();
            if (graph_.IsValid ()) graph_.Destroy ();
            if (humanReader_ != null) {
                humanReader_.Dispose ();
                humanReader_ = null;
            }
            if (muscleHandles_.IsCreated) muscleHandles_.Dispose ();
            if (muscles_.IsCreated) muscles_.Dispose ();
            if (handles_.IsCreated) handles_.Dispose ();
            if (positions_.IsCreated) positions_.Dispose ();
            if (rotations_.IsCreated) rotations_.Dispose ();
            if (scales_.IsCreated) scales_.Dispose ();
        }
    }

}
