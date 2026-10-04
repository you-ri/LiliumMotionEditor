using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 姿勢を入れて、手足の IK ゴールを Unity 自身の計算（AnimationHumanStream.GetGoal*FromPose）で求めるジョブ。
    /// 求めたゴールを一度ストリームへ書き、クリップと同じ持ち方（GetGoalLocal*）で読み出す
    /// </summary>
    struct HumanoidGoalJob : IAnimationJob
    {
        public NativeArray<MuscleHandle> muscleHandles;
        public NativeArray<float> muscles;
        public Vector3 bodyPosition;
        public Quaternion bodyRotation;
        /// <summary>姿勢を入れる。false なら入力（クリップ）の姿勢のまま求める</summary>
        public bool writePose;
        /// <summary>姿勢から求めたゴール（ワールド）</summary>
        public NativeArray<Vector3> positions;
        public NativeArray<Quaternion> rotations;
        /// <summary>ストリームにあったゴール（ワールド。入力がクリップなら、そのクリップのゴール）</summary>
        public NativeArray<Vector3> inputPositions;
        public NativeArray<Quaternion> inputRotations;
        /// <summary>[0] 体の位置（ワールド）・[1] x に humanScale</summary>
        public NativeArray<Vector3> body;
        public NativeArray<Quaternion> bodyRotations;

        public void ProcessRootMotion (AnimationStream stream)
        {
        }

        public void ProcessAnimation (AnimationStream stream)
        {
            if (!stream.isHumanStream) return;
            AnimationHumanStream human = stream.AsHuman ();
            if (writePose) {
                for (int i = 0; i < muscleHandles.Length && i < muscles.Length; i++) human.SetMuscle (muscleHandles[i], muscles[i]);
                human.bodyLocalPosition = bodyPosition;
                human.bodyLocalRotation = bodyRotation;
            }
            body[0] = human.bodyPosition;
            body[1] = new Vector3 (human.humanScale, 0, 0);
            bodyRotations[0] = human.bodyRotation;
            for (int i = 0; i < HumanoidGoals.kGoals.Length; i++) {
                AvatarIKGoal goal = HumanoidGoals.kGoals[i];
                inputPositions[i] = human.GetGoalPosition (goal);
                inputRotations[i] = human.GetGoalRotation (goal);
                positions[i] = human.GetGoalPositionFromPose (goal);
                rotations[i] = human.GetGoalRotationFromPose (goal);
            }
        }
    }

    /// <summary>
    /// Humanoid の姿勢から、手足の IK ゴール（クリップの LeftFootT / LeftFootQ など）を作る。
    /// FBX の取り込みが焼くのと同じく姿勢から求めるので、焼いた姿勢と食い違わない。
    /// 表示モデルの Animator には窓のグラフが載っているので、計算用に Animator ごと複製して使う（同じ Animator に別のグラフを流すと束縛が戻される）。
    /// 複製は表示モデルの外に置き、描画とスクリプトを止める
    /// </summary>
    public sealed class HumanoidGoals : System.IDisposable
    {
        internal static readonly AvatarIKGoal[] kGoals = { AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot, AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand };
        /// <summary>クリップのカーブの名前（kGoals と同じ並び。位置は名前 + "T"、向きは名前 + "Q"）</summary>
        public static readonly string[] kCurveNames = { "LeftFoot", "RightFoot", "LeftHand", "RightHand" };

        GameObject clone_;
        Animator animator_;
        PlayableGraph graph_;
        AnimationScriptPlayable job_;
        NativeArray<MuscleHandle> muscleHandles_;
        NativeArray<float> muscles_;
        NativeArray<Vector3> positions_;
        NativeArray<Quaternion> rotations_;
        NativeArray<Vector3> inputPositions_;
        NativeArray<Quaternion> inputRotations_;
        NativeArray<Vector3> body_;
        NativeArray<Quaternion> bodyRotations_;
        int[] muscleSources_;

        /// <summary>最後に求めたときの体の位置（ワールド）・向き・humanScale</summary>
        public Vector3 bodyPosition { get { return body_[0]; } }
        public Quaternion bodyRotation { get { return bodyRotations_[0]; } }
        public float humanScale { get { return body_[1].x; } }

        /// <summary>作れない理由（作れたなら null）</summary>
        public string error { get; private set; }

        /// <param name="source">Humanoid の Avatar を持つ Animator（表示モデル）。複製して使い、元には触らない</param>
        public HumanoidGoals (Animator source)
        {
            if (source == null || source.avatar == null || !source.avatar.isValid || !source.avatar.isHuman) {
                error = Tr ("HUMANOID_GOALS_NO_HUMANOID_AVATAR");
                return;
            }
            MuscleHandle[] handles;
            if (!PoseGraph.MapMuscles (out handles, out muscleSources_)) {
                error = Tr ("HUMANOID_GOALS_CANNOT_MAP_MUSCLES");
                return;
            }

            // 表示モデルの外（同じシーンの一番上）に同じ置き方で置く。モデルの子にすると、段の検出や骨の選択が複製の部品まで拾う
            // 親を付けて作ると元と同じシーンに入る（付けずに作ると開いているシーンに入る）。作ってから親を外す
            clone_ = Object.Instantiate (source.gameObject, source.transform.parent, false);
            clone_.transform.SetParent (null, true);
            clone_.hideFlags = HideFlags.HideAndDontSave;
            clone_.name = source.gameObject.name + " (IK Goals)";
            // 計算に要るのは Animator だけ。描画とスクリプトは止める（プレビューに映ったり、ゲームの処理が走ったりしないように）
            foreach (Renderer renderer in clone_.GetComponentsInChildren<Renderer> (true)) renderer.enabled = false;
            foreach (MonoBehaviour behaviour in clone_.GetComponentsInChildren<MonoBehaviour> (true)) behaviour.enabled = false;
            animator_ = clone_.GetComponent<Animator> ();
            animator_.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator_.applyRootMotion = false;

            muscleHandles_ = new NativeArray<MuscleHandle> (handles, Allocator.Persistent);
            muscles_ = new NativeArray<float> (handles.Length, Allocator.Persistent);
            positions_ = new NativeArray<Vector3> (kGoals.Length, Allocator.Persistent);
            rotations_ = new NativeArray<Quaternion> (kGoals.Length, Allocator.Persistent);
            inputPositions_ = new NativeArray<Vector3> (kGoals.Length, Allocator.Persistent);
            inputRotations_ = new NativeArray<Quaternion> (kGoals.Length, Allocator.Persistent);
            body_ = new NativeArray<Vector3> (2, Allocator.Persistent);
            bodyRotations_ = new NativeArray<Quaternion> (1, Allocator.Persistent);

            graph_ = PlayableGraph.Create ("Mkt IK Goals");
            graph_.SetTimeUpdateMode (DirectorUpdateMode.Manual);
            job_ = AnimationScriptPlayable.Create (graph_, new HumanoidGoalJob {
                muscleHandles = muscleHandles_,
                muscles = muscles_,
                positions = positions_,
                rotations = rotations_,
                inputPositions = inputPositions_,
                inputRotations = inputRotations_,
                body = body_,
                bodyRotations = bodyRotations_,
            });
            AnimationPlayableOutput output = AnimationPlayableOutput.Create (graph_, "Mkt IK Goals", animator_);
            output.SetSourcePlayable (job_);
        }

        /// <summary>
        /// 姿勢（HumanPoseHandler.GetHumanPose を Animator の GameObject を原点・無回転に置いて読んだ値）の IK ゴールを、クリップの持ち方で求める
        /// </summary>
        /// <param name="positions">kGoals と同じ並びで 4 つ</param>
        /// <param name="rotations">kGoals と同じ並びで 4 つ</param>
        public void Compute (ref HumanPose pose, Vector3[] positions, Quaternion[] rotations)
        {
            if (error != null) throw new System.InvalidOperationException (error);
            for (int i = 0; i < muscleSources_.Length; i++) {
                int source = muscleSources_[i];
                muscles_[i] = source < pose.muscles.Length ? pose.muscles[source] : 0;
            }
            HumanoidGoalJob job = job_.GetJobData<HumanoidGoalJob> ();
            job.writePose = true;
            job.bodyPosition = pose.bodyPosition;
            job.bodyRotation = pose.bodyRotation;
            job_.SetJobData (job);
            graph_.Evaluate (0);
            ToClip (positions, rotations, positions_, rotations_);
        }

        /// <summary>
        /// クリップを再生した姿勢の IK ゴールを求める。あわせて、クリップに入っているゴールを同じ持ち方で返す（検算用。カーブの値と一致する）
        /// </summary>
        public void ComputeFromClip (AnimationClip clip, float time, Vector3[] positions, Quaternion[] rotations, Vector3[] clipPositions, Quaternion[] clipRotations)
        {
            if (error != null) throw new System.InvalidOperationException (error);
            AnimationClipPlayable playable = AnimationClipPlayable.Create (graph_, clip);
            try {
                playable.SetApplyFootIK (false);
                playable.SetTime (time);
                job_.AddInput (playable, 0, 1);
                HumanoidGoalJob job = job_.GetJobData<HumanoidGoalJob> ();
                job.writePose = false;
                job_.SetJobData (job);
                graph_.Evaluate (0);
                ToClip (positions, rotations, positions_, rotations_);
                ToClip (clipPositions, clipRotations, inputPositions_, inputRotations_);
            }
            finally {
                job_.DisconnectInput (0);
                job_.SetInputCount (0);
                playable.Destroy ();
            }
        }

        /// <summary>
        /// ゴールの向きからクリップの向きへの、ゴールごとの一定の回転（kGoals と同じ並び）。
        /// Unity の持ち方で Avatar によらない（2026-09-28 に 2 体のキャラの FBX の取り込み結果から実測。手と足で軸の取り方が違う）
        /// </summary>
        internal static readonly Quaternion[] kClipRotations = {
            new Quaternion (-0.5f, 0.5f, -0.5f, 0.5f),
            new Quaternion (-0.5f, 0.5f, -0.5f, 0.5f),
            new Quaternion (-0.70710678f, 0, -0.70710678f, 0),
            new Quaternion (0, -0.70710678f, 0, 0.70710678f),
        };

        /// <summary>
        /// ストリームのゴール（ワールド）をクリップの持ち方へ直す。
        /// 位置は体の位置・向きから見て humanScale で割った値、向きは体の向きから見た向きにゴールごとの一定の回転を掛けた値。
        /// 足は、ストリームのゴール（足裏）からゴールの向きで足裏の高さ（feetBottomHeight × humanScale）だけ下げた点（足首）
        /// </summary>
        void ToClip (Vector3[] positions, Quaternion[] rotations, NativeArray<Vector3> fromPositions, NativeArray<Quaternion> fromRotations)
        {
            Quaternion inverse = Quaternion.Inverse (bodyRotations_[0]);
            Vector3 body = body_[0];
            float scale = body_[1].x > 0 ? body_[1].x : 1;
            for (int i = 0; i < kGoals.Length; i++) {
                Vector3 position = fromPositions[i];
                Quaternion rotation = fromRotations[i];
                float foot = kGoals[i] == AvatarIKGoal.LeftFoot ? animator_.leftFeetBottomHeight : kGoals[i] == AvatarIKGoal.RightFoot ? animator_.rightFeetBottomHeight : 0;
                position += rotation * new Vector3 (0, -foot * scale, 0);
                if (positions != null) positions[i] = inverse * (position - body) / scale;
                if (rotations != null) rotations[i] = inverse * rotation * kClipRotations[i];
            }
        }

        public void Dispose ()
        {
            if (graph_.IsValid ()) graph_.Destroy ();
            if (muscleHandles_.IsCreated) muscleHandles_.Dispose ();
            if (muscles_.IsCreated) muscles_.Dispose ();
            if (positions_.IsCreated) positions_.Dispose ();
            if (rotations_.IsCreated) rotations_.Dispose ();
            if (inputPositions_.IsCreated) inputPositions_.Dispose ();
            if (inputRotations_.IsCreated) inputRotations_.Dispose ();
            if (body_.IsCreated) body_.Dispose ();
            if (bodyRotations_.IsCreated) bodyRotations_.Dispose ();
            if (clone_ != null) Object.DestroyImmediate (clone_);
            clone_ = null;
        }
    }

}
