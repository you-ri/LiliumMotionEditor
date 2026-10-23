using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 窓のキーを外部のツール（学習済みのモーション生成モデルなど）へ渡し、出てきた動きを窓の Override として戻す口（S77）。
    /// どのツールにも使えるように、外部の骨格（ExternalSkeleton）と BVH だけで受け渡す。ツールの起動はこの口を使う側（サンプルなど）が持つ
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// 元のクリップのキーの姿勢を、外部の骨格へ写して BVH にする（S73 を窓から）。
        /// ✏ の位置によらず元のクリップのキーを出す（✏ が前に取り込んだ Override にあると、その密なキーと生成結果を「キー」として渡してしまう）。
        /// 描かない世界で作るので、窓の表示は変わらない
        /// </summary>
        /// <returns>書き出せなかった理由（書き出せたら null）</returns>
        public string ExportKeysToSkeleton (ExternalSkeleton skeleton, out Bvh bvh, out MotionEditorExport export)
        {
            bvh = null;
            export = null;
            AnimationClip source = baseClip;
            if (stage_ == null || stage_.sourcePrefab == null || source == null) return Tr ("POSE_SESSION_NO_CLIP");
            CommitHandleKeys ();
            using (PoseSession session = new PoseSession (stage_.sourcePrefab, rigDefinition)) {
                // Override は重ねない（✏ は元。元へ書く間は上の段が効かないので、元のキーの姿勢そのものになる）
                session.clip = source;
                string error = session.ExportToSkeleton (skeleton, null, out bvh, out List<int> frames);
                if (error != null) return error;
                int[] keys = ClipCycle.GetFrames (source);
                export = new MotionEditorExport {
                    frameRate = source.frameRate,
                    lastFrame = Mathf.Max (keys.Length > 0 ? keys[keys.Length - 1] : 0, Mathf.RoundToInt (source.length * source.frameRate)),
                    frames = frames,
                    jointCount = bvh.joints.Count,
                };
                return null;
            }
        }

        /// <summary>
        /// 外部の骨格の BVH を、元のクリップに重ねる Override として destination へ取り込み（S74・S75）、窓の Override に足して書き込み先にする。
        /// 差は元のクリップとの差で作る（ほかの Override は重ねない）。destination の書き換えは Undo で戻せる
        /// </summary>
        /// <returns>取り込めなかった理由（取り込めたら null）</returns>
        public string ImportOverride (Bvh bvh, ExternalSkeleton skeleton, AnimationClip destination, OverrideMode mode,
                                      PoseSession.OverrideImportOptions options, out PoseSession.OverrideImportResult result)
        {
            result = null;
            AnimationClip source = baseClip;
            if (stage_ == null || stage_.sourcePrefab == null || source == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (destination == null || destination == source) return Tr ("PREVIEW_WINDOW_OVERRIDE_NO_LAYER");
            string problem = GetAddOverrideProblem ();
            if (problem != null && !IsOverrideClip (destination)) return problem;
            CommitHandleKeys ();
            Undo.RegisterCompleteObjectUndo (destination, "Motion Editor Import Override");
            using (PoseSession session = new PoseSession (stage_.sourcePrefab, rigDefinition)) {
                session.clip = source;
                string error = session.ImportOverride (bvh, skeleton, destination, mode, options, out result);
                if (error != null) return error;
            }
            AssetDatabase.SaveAssetIfDirty (destination);
            AddOverride (destination, mode);
            return null;
        }

        /// <summary>
        /// 外部の骨格の BVH を、元のクリップの外部の補間の結果として書き、segments の区間に使う（S81。PoseSession.ImportBetween を窓から）。
        /// Override の段には足さない。書いたら元のクリップを当て直す
        /// </summary>
        /// <returns>書けなかった理由（書けたら null）</returns>
        public string ImportBetween (Bvh bvh, ExternalSkeleton skeleton, IList<BetweenClips.Range> segments, out PoseSession.OverrideImportResult result)
        {
            result = null;
            AnimationClip source = baseClip;
            if (stage_ == null || stage_.sourcePrefab == null || source == null) return Tr ("POSE_SESSION_NO_CLIP");
            CommitHandleKeys ();
            using (PoseSession session = new PoseSession (stage_.sourcePrefab, rigDefinition)) {
                session.clip = source;
                string error = session.ImportBetween (bvh, skeleton, segments, out result);
                if (error != null) return error;
            }
            AfterClipEdit ();
            RaiseStateChanged ();
            return null;
        }

        /// <summary>
        /// clip を Override として重ね、書き込み先にする。既に重ねているクリップなら、その段の合成の仕方を mode にし、表示を作り直す
        /// （クリップを外で書き換えた後に、古いカーブのまま見えないように）。無ければ一番上に足す
        /// </summary>
        /// <returns>重ねた段が何枚目か（重ねられなければ -1）</returns>
        public int AddOverride (AnimationClip clip, OverrideMode mode)
        {
            if (clip == null || clip == baseClip) return -1;
            int index = overrides_.FindIndex (o => o != null && o.clip == clip);
            if (index < 0) {
                if (GetAddOverrideProblem () != null) return -1;
                overrides_.Add (new OverrideClip { clip = clip, mode = mode });
                index = overrides_.Count - 1;
                SaveOverrides ();
                AfterOverrideChange (true);
            }
            else if (overrides_[index].mode != mode) {
                overrides_[index].mode = mode;
                SaveOverrides ();
                AfterOverrideChange (true);
            }
            else {
                AfterOverrideChange (false);
            }
            SetOverrideWrite (index);
            return index;
        }

        /// <summary>
        /// 書き込み先（✏）を index 枚目の Override にする（-1 で元のクリップ）。書き込み先より上の段は編集中は効かないので、
        /// 取り込んだ動きを見るときは Override へ、元のキーを直すときは元へ移す
        /// </summary>
        /// <returns>移せたか</returns>
        public bool SetOverrideWrite (int index)
        {
            if (poseStack_ == null) return false;
            PoseLayer layer = index < 0 ? (PoseLayer) poseStack_.Find<EditingRigLayer> () : FindOverrideLayer (index);
            if (layer == null) return false;
            if (poseStack_.writeLayer != layer) SetWriteLayer (layer);
            if (poseStack_.writeLayer != layer) return false;
            // 書き込み先が変わると、効く段が変わる
            SamplePose ();
            RaiseStateChanged ();
            return true;
        }
    }
}
