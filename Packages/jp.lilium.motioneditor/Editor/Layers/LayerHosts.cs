using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    // 段のクリップと操作の持ち主（窓）。段は作り直すので、クリップそのものと焼く・取り込む処理は窓が持ち、段はここを通して見せる。
    // **1 つの段が受け取るのは 1 本だけ**になるよう、段ごとに分けている。窓は全部をまとめた ILayerHost を実装する

    /// <summary>
    /// 段のクリップを流れに合成する重み（Editing Rig / Generic Pose / Humanoid Pose 共通）
    /// </summary>
    public interface IClipWeightHost
    {
        /// <summary>段のクリップを合成する重みを変える（PoseLayer.clipWeight。Undo と保存と当て直しは持ち主が行う）</summary>
        void SetClipWeight (PoseLayer layer, float weight);
    }

    /// <summary>
    /// 元のクリップに重ねる Override の並び（Editing Rig の段と Override の段が読む）
    /// </summary>
    public interface IOverrideListHost
    {
        /// <summary>元のクリップに重ねる Override のクリップ（下から順。S14。1 枚が 1 段）</summary>
        IReadOnlyList<OverrideClip> overrides { get; }
    }

    /// <summary>
    /// Editing Rig の段: 編集するクリップと、それに重ねた Override の統合
    /// </summary>
    public interface IEditClipHost : IClipWeightHost, IOverrideListHost
    {
        /// <summary>編集するクリップ</summary>
        AnimationClip clip { get; }
        /// <summary>編集できない理由（編集できるなら null）</summary>
        string clipProblem { get; }
        /// <summary>編集用リグ以外を指すカーブの数</summary>
        int strayCurveCount { get; }
        void SetClip (AnimationClip clip);
        void CreateClip ();
        void RemoveStrayCurves ();

        /// <summary>Editing Rig の段に Humanoid のクリップを開いているか（読み取り専用の土台。フレームごとに操作値へ変換して使う。S14・S20）</summary>
        bool useHumanoidBase { get; }
        /// <summary>読み取り専用の Humanoid の元を編集用クリップへ取り込めない理由（取り込めるなら null）</summary>
        string GetImportBaseProblem ();
        /// <summary>読み取り専用の Humanoid の元を、新しい編集用クリップへ書き出して開き直す（取込。S20）</summary>
        bool ImportBaseClip ();
        /// <summary>取込のクリップ選びを始められない理由（始められるなら null）</summary>
        string GetPickImportProblem ();
        /// <summary>取込: 候補の一覧の窓で Humanoid のクリップを選んで試し見させ、新しい編集用クリップへ取り込んで開く</summary>
        void PickImportClip ();
        /// <summary>取込の候補を試し見している（開いているのは候補のクリップで、キーは打たない）</summary>
        bool importPreviewing { get; }

        /// <summary>効いている Override を元のクリップへ焼き込めない理由（できるなら null。S14）</summary>
        string GetMergeOverridesProblem ();
        /// <summary>効いている Override を元のクリップへ焼き込む（統合）</summary>
        bool MergeOverrides ();
    }

    /// <summary>
    /// Override の段: 1 枚ずつの差し替え・並べ替え・外す
    /// </summary>
    public interface IOverrideHost : IOverrideListHost
    {
        /// <summary>書き込み先の Override は何枚目か（-1 は元のクリップ）</summary>
        int overrideWriteIndex { get; }
        /// <summary>Layers の構造を編集中か（段の追加・削除・移動の操作を出す。S21）</summary>
        bool layerStructureEditing { get; }
        /// <summary>層のクリップを置き換える。null で層を外す</summary>
        void SetOverrideClip (int index, AnimationClip clip);
        void SetOverrideMode (int index, OverrideMode mode);
        /// <summary>Override の段を外す（deleteAsset でクリップのファイルも消す）</summary>
        void RemoveOverride (int index, bool deleteAsset);
        /// <summary>Override の段を 1 つ上（-1）か下（+1）の Override と入れ替える（S21）</summary>
        void MoveOverride (int index, int direction);
        string GetMoveOverrideProblem (int index, int direction);
    }

    /// <summary>
    /// Generic Pose の段: 骨を直接動かすクリップ（取り込み元）
    /// </summary>
    public interface IGenericHost : IClipWeightHost
    {
        /// <summary>Generic Pose の段に合成するクリップ（骨のカーブ）</summary>
        AnimationClip genericClip { get; }
        void SetGenericClip (AnimationClip clip);
        /// <summary>Generic のクリップを取り込めない理由（取り込めるなら null）</summary>
        string GetImportProblem ();
        /// <summary>Generic Pose の段の出力（クリップを合成した姿勢）を編集用リグの値に直して、編集するクリップへ書く</summary>
        bool ImportGenericClip ();
    }

    /// <summary>
    /// Humanoid Pose の段: 見比べる参照と、その姿勢の反映
    /// </summary>
    public interface IHumanoidHost : IClipWeightHost
    {
        /// <summary>Humanoid Pose の段に合成して見比べるクリップ（参照。既存の Humanoid のクリップ）</summary>
        AnimationClip humanoidClip { get; }
        void SetHumanoidClip (AnimationClip clip);
        /// <summary>今のフレームの姿勢を編集用リグへ反映できない理由（できるなら null）</summary>
        string GetHumanoidApplyProblem ();
        /// <summary>今のフレームの姿勢を編集用リグの値に直してキーを打つ</summary>
        bool ApplyHumanoidPoseToRig ();
        /// <summary>Humanoid Pose の段を置くか外すか（S21）</summary>
        void SetHumanoidPoseLayer (bool value);
    }

    /// <summary>
    /// Output の段: 焼いて書き出す・並びの中で動かす・参照の差し替え
    /// </summary>
    public interface IOutputHost
    {
        /// <summary>Output の段が何を焼くかの説明（段の位置による）</summary>
        string DescribeOutput (OutputLayer output);
        /// <summary>Output の段が焼くクリップの置き場所（決まらなければ null）</summary>
        string GetOutputPath (OutputLayer output);
        LayerClipStatus GetOutputStatus (OutputLayer output);
        /// <summary>Output の段を焼けない理由（焼けるなら null）</summary>
        string GetOutputBakeProblem (OutputLayer output);
        /// <summary>Output の段の位置まで段を通した姿勢を焼いて保存する</summary>
        bool BakeOutput (OutputLayer output);
        /// <summary>Output の段を外す（焼いたクリップは消さない）</summary>
        void RemoveOutput (OutputLayer output);
        /// <summary>Output の段を並びの中で 1 つ上（-1）か下（+1）へ動かす</summary>
        void MoveOutput (OutputLayer output, int direction);
        string GetMoveOutputProblem (OutputLayer output, int direction);
        /// <summary>クリップを保存したら Output の段を焼き直すか</summary>
        bool autoBake { get; set; }

        /// <summary>一緒に焼く Override のクリップ（効いている段だけ。S14）</summary>
        List<AnimationClip> bakeOverrides { get; }
        /// <summary>参照を差し替えられない理由（できるなら null。S14）</summary>
        string GetSwapProblem ();
        /// <summary>ゲームが使っているクリップへの参照を「元＋Override」を焼いたクリップへ差し替える（back で元へ戻す）</summary>
        bool SwapClipReferences (bool back);
    }

    /// <summary>
    /// 段の並びを組み立てる相手（窓）。段ごとの持ち主を全部まとめ、どの段を置くかを決める情報を足したもの
    /// </summary>
    public interface ILayerHost : IEditClipHost, IOverrideHost, IGenericHost, IHumanoidHost, IOutputHost
    {
        /// <summary>Humanoid Pose の段を置くか（S21。既定は置かない）</summary>
        bool humanoidPoseLayer { get; }
        /// <summary>焼いて書き出す段（Output）の並び（S21）</summary>
        IReadOnlyList<OutputEntry> outputEntries { get; }
        /// <summary>一緒に見ている演出の、どのクリップとして見ているか（S15b。無ければ null）</summary>
        ContextClip timelineClip { get; }
        /// <summary>一緒に見ている演出にあるアニメーションのクリップの数（0 なら Timeline Clip の段を作らない）</summary>
        int timelineClipCandidateCount { get; }
    }

}
