using UnityEngine;
using UnityEditor;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 段に合成するクリップ（Generic Pose / Humanoid Pose）と、Generic Pose の段の取り込み。
    /// 骨を直接動かすクリップを置くと合成して画面に出し、Import で合成した姿勢を編集用リグの値に直して編集するクリップへ書く
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// Humanoid Pose の段に合成する既存の Humanoid のクリップ
        /// </summary>
        [SerializeField] AnimationClip humanoidClip_;

        public AnimationClip humanoidClip
        {
            get { return humanoidClip_; }
        }

        public void SetHumanoidClip (AnimationClip clip)
        {
            if (clip == humanoidClip_) return;
            Undo.RecordObject (this, "Motion Editor Humanoid Clip");
            humanoidClip_ = clip;
            keyFrames_ = null;
            // H土台 の切り替えは Editing Rig の段がクリップの有無で出し分けるので、段を作り直す。
            // H土台 にしていれば土台も替わるので、重ねている Override も探し直す
            RefreshClipState ();
            BuildStack ();
            WriteToTopOverrideOnHumanoidBase ();
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 取り込む Generic のクリップ（Generic Pose の段）
        /// </summary>
        [SerializeField] AnimationClip genericClip_;

        public AnimationClip genericClip
        {
            get { return genericClip_; }
        }

        public void SetGenericClip (AnimationClip clip)
        {
            if (clip == genericClip_) return;
            Undo.RecordObject (this, "Motion Editor Generic Clip");
            genericClip_ = clip;
            keyFrames_ = null;
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 窓へ落としたクリップ。編集用リグのカーブが無く骨のカーブがあるもの（旧形式など）は Generic Pose の段（並びにあるとき）へ、
        /// それ以外は Editing Rig の段の元へ置く（Humanoid のクリップは読み取り専用の土台として開く。S20）
        /// </summary>
        void DropClip (AnimationClip clip)
        {
            if (!clip.humanMotion && poseStack_.Find (LayerKind.GenericPose) != null && GenericImport.GetProblem (clip) == null) SetGenericClip (clip);
            else SetClip (clip);
        }

        /// <summary>
        /// 今のフレームの姿勢を編集用リグへ反映できない理由（できるなら null）。
        /// クリップが無くても、その段までの姿勢（Humanoid に直した姿勢）を写せる
        /// </summary>
        public string GetHumanoidApplyProblem ()
        {
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_CHARACTER");
            if (importPreviewing_) return Tr ("PREVIEW_WINDOW_IMPORT_PREVIEWING");
            if (useHumanoidBase) {
                // 読み取り専用の土台へは書けない。Override を書き出し先にしていればそこへ打つ
                if (overrideWriteLayer == null) return Tr ("PREVIEW_WINDOW_IMPORT_HUMANOID_BASE_READ_ONLY");
            }
            else {
                if (editingClip_ == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_WRITE_TARGET");
                if (clipProblem_ != null) return Tr ("PREVIEW_WINDOW_IMPORT_WRITE_TARGET_NOT_EDITABLE", clipProblem_);
            }
            PoseLayer humanoid = poseStack_.Find (LayerKind.HumanoidAnimation);
            if (humanoid == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_HUMANOID_POSE_LAYER");
            if (!humanoid.enabled) return Tr ("PREVIEW_WINDOW_IMPORT_HUMANOID_POSE_LAYER_DISABLED");
            if (!humanoid.active) return Tr ("PREVIEW_WINDOW_IMPORT_HUMANOID_POSE_LAYER_INACTIVE");
            PoseLayer rig = poseStack_.Find (LayerKind.EditingRig);
            if (rig == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_EDITING_RIG_LAYER");
            if (!rig.enabled) return Tr ("PREVIEW_WINDOW_IMPORT_EDITING_RIG_LAYER_DISABLED");
            if (rig.clipWeight < 1) return Tr ("PREVIEW_WINDOW_IMPORT_EDITING_RIG_CLIP_WEIGHT");
            return null;
        }

        /// <summary>
        /// **今のフレームだけ**、Humanoid Pose の段まで通した姿勢を編集用リグの値に直してキーを打つ。
        /// クリップ全体を取り込むのは、Editing Rig の段の「取込」（PickImportClip）
        /// </summary>
        public bool ApplyHumanoidPoseToRig ()
        {
            string problem = GetHumanoidApplyProblem ();
            if (problem != null) {
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_CANNOT_APPLY", problem), true);
                return false;
            }

            try {
                float time = CurrentTime ();
                poseStack_.EvaluateUntil (poseStack_.Find (LayerKind.HumanoidAnimation), time);
                // 表示モデルの骨を編集用の体へ写して、操作値として捉える
                stage_.editingRig.SyncFromDisplay ();
                foreach (RigBinding.Ik ik in stage_.editingRig.binding.ik) {
                    if (ik.enabled) stage_.editingRigSolver.SetIkWeight (ik.chain.name, 0);
                }
                stage_.editingRigSolver.Capture ();
                AddKeyAllCurves ();
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_APPLIED", currentFrame), false);
                return true;
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_APPLY_FAILED", e.Message), true);
                return false;
            }
            finally {
                keyFrames_ = null;
                SamplePose ();
                RaiseStateChanged ();
            }
        }

        /// <summary>
        /// 取込のクリップ選びを始められない理由（始められるなら null）。選んだクリップの良し悪しは選んだ後に見る
        /// </summary>
        public string GetPickImportProblem ()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return Tr ("PREVIEW_WINDOW_IMPORT_NOT_IN_PLAY_MODE");
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_CHARACTER");
            if (AnimBank.GetFolder (model) == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_ANIM_BANK_FOLDER");
            if (poseStack_.Find (LayerKind.EditingRig) == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_EDITING_RIG_LAYER");
            return null;
        }

        /// <summary>取込の候補を試し見している（取込の窓が開いている間）。編集用クリップの代わりに候補を土台として開いている</summary>
        [SerializeField] bool importPreviewing_;
        /// <summary>試し見を始める前に開いていたクリップ（やめたら戻す）</summary>
        [SerializeField] AnimationClip importPreviousClip_;
        [SerializeField] int importPreviousFrame_;

        public bool importPreviewing
        {
            get { return importPreviewing_; }
        }

        /// <summary>
        /// 取込: 候補の一覧の窓（ClipImportWindow）を開く。一覧で選んだ Humanoid のクリップを試し見し（タイムラインで好きなコマを見られる）、
        /// 「取り込む」で編集用クリップへ書き出して開く。「やめる」・窓を閉じると元のクリップへ戻す
        /// </summary>
        public void PickImportClip ()
        {
            string problem = GetPickImportProblem ();
            if (problem != null) {
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_CANNOT_IMPORT", problem), true);
                return;
            }
            ClipImportWindow.Open (this);
        }

        /// <summary>
        /// 試し見を始める。今のクリップを覚えておく（何度呼んでも最初のものを覚えたまま）
        /// </summary>
        public void BeginImportPreview ()
        {
            if (importPreviewing_) return;
            importPreviewing_ = true;
            importPreviousClip_ = editingClip_;
            importPreviousFrame_ = currentFrame;
            RaiseStateChanged ();
        }

        /// <summary>
        /// 候補を土台として開いて見せる。時刻はそのまま（候補を替えても同じコマで見比べられる）
        /// </summary>
        public void PreviewImportClip (AnimationClip clip)
        {
            if (!importPreviewing_ || clip == null || clip == editingClip_) return;
            SetClip (clip);
        }

        /// <summary>
        /// 試し見している候補を取り込んで開く。取り込めなかったら試し見を続ける
        /// </summary>
        public bool CommitImportPreview ()
        {
            if (!importPreviewing_) return false;
            if (!useHumanoidBase) {
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_NO_HUMANOID_CLIP_SELECTED"), true);
                return false;
            }
            importPreviewing_ = false;
            if (ImportBaseClip ()) {
                importPreviousClip_ = null;
                return true;
            }
            importPreviewing_ = true;
            RaiseStateChanged ();
            return false;
        }

        /// <summary>
        /// 試し見をやめて、始める前に開いていたクリップとフレームへ戻す
        /// </summary>
        public void CancelImportPreview ()
        {
            if (!importPreviewing_) return;
            importPreviewing_ = false;
            AnimationClip previous = importPreviousClip_;
            importPreviousClip_ = null;
            if (editingClip_ != previous) SetClip (previous);
            SetFrame (importPreviousFrame_);
            RaiseStateChanged ();
        }

        /// <summary>
        /// 読み取り専用の Humanoid の元を編集用クリップへ取り込めない理由（取り込めるなら null）
        /// </summary>
        public string GetImportBaseProblem ()
        {
            string problem = GetPickImportProblem ();
            if (problem != null) return problem;
            if (!useHumanoidBase) return Tr ("PREVIEW_WINDOW_IMPORT_BASE_NOT_HUMANOID");
            return null;
        }

        /// <summary>
        /// 取込（S20）: 読み取り専用の Humanoid の元（効いている Override 込み）を、新しい編集用クリップへ全フレーム書き出して開き直す。
        /// 置き場所は AnimBank の先頭のフォルダ、名前は元のクリップの名前（同じ名前があれば番号を付ける）。元のクリップと Override のファイルは変えない
        /// </summary>
        public bool ImportBaseClip ()
        {
            string problem = GetImportBaseProblem ();
            if (problem != null) {
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_CANNOT_IMPORT", problem), true);
                return false;
            }

            // 段の出力を読むので、Humanoid Pose の段は 👁 を入れ、見比べ用の参照は混ぜない。Editing Rig の段も 👁 を入れて通す（読み終えたら戻す）
            PoseLayer humanoid = poseStack_.Find (LayerKind.HumanoidAnimation);
            PoseLayer rig = poseStack_.Find (LayerKind.EditingRig);
            // Humanoid Pose の段は置いていないこともある（S21）
            bool humanoidEnabled = humanoid != null && humanoid.enabled;
            float humanoidWeight = humanoid != null ? humanoid.clipWeight : 0;
            bool rigEnabled = rig.enabled;
            if (humanoid != null) {
                humanoid.enabled = true;
                humanoid.clipWeight = 0;
            }
            rig.enabled = true;

            AnimationClip source = editingClip_;
            AnimationClip destination = null;
            try {
                destination = AnimBank.Create (AnimBank.GetFolder (model), source.name);
                PoseImport.Result result = HumanoidImport.Import (source, destination, stage_.editingRig, stage_.editingRigSolver,
                    time => poseStack_.EvaluateThroughHumanoid (time), targets_);
                AssetDatabase.SaveAssetIfDirty (destination);
                string text = Tr ("PREVIEW_WINDOW_IMPORT_IMPORTED_BASE", source.name, destination.name, result.frameCount, result.milliseconds.ToString ("0"),
                    (result.maxError * 1000).ToString ("0.00"));
                if (bakeOverrides.Count > 0) text += Tr ("PREVIEW_WINDOW_IMPORT_WITH_OVERRIDES", bakeOverrides.Count);
                if (result.notes.Count > 0) {
                    text += Tr ("PREVIEW_WINDOW_IMPORT_NOTES_COUNT", result.notes.Count);
                    Debug.Log (Tr ("PREVIEW_WINDOW_IMPORT_LOG_IMPORTED_NOTES", source.name, destination.name, string.Join ("\n- ", result.notes)), destination);
                }
                SetBakeStatus (text, false);
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_IMPORT_FAILED", e.Message), true);
                return false;
            }
            finally {
                if (humanoid != null) {
                    humanoid.enabled = humanoidEnabled;
                    humanoid.clipWeight = humanoidWeight;
                }
                rig.enabled = rigEnabled;
                if (stage_ != null) stage_.InvalidateClipCurves ();
            }
            // 取り込んだクリップを開き直す（以降は普通の編集用クリップ）
            SetClip (destination);
            return true;
        }

        public string GetImportProblem ()
        {
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_CHARACTER");
            if (genericClip_ == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_GENERIC_CLIP");
            if (overrideBlocksBaseWrite != null) return overrideBlocksBaseWrite;
            string problem = GenericImport.GetProblem (genericClip_);
            if (problem != null) return problem;
            if (editingClip_ == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_WRITE_TARGET");
            if (clipProblem_ != null) return Tr ("PREVIEW_WINDOW_IMPORT_WRITE_TARGET_NOT_EDITABLE", clipProblem_);
            PoseLayer generic = poseStack_.Find (LayerKind.GenericPose);
            if (generic == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_GENERIC_POSE_LAYER");
            if (!generic.active) return Tr ("PREVIEW_WINDOW_IMPORT_GENERIC_POSE_LAYER_INACTIVE");
            PoseLayer rig = poseStack_.Find (LayerKind.EditingRig);
            if (rig == null) return Tr ("PREVIEW_WINDOW_IMPORT_NO_EDITING_RIG_LAYER");
            if (!rig.enabled) return Tr ("PREVIEW_WINDOW_IMPORT_EDITING_RIG_LAYER_DISABLED");
            if (rig.clipWeight < 1) return Tr ("PREVIEW_WINDOW_IMPORT_EDITING_RIG_CLIP_WEIGHT");
            return null;
        }

        /// <summary>
        /// Generic のクリップを取り込む。書き出し先に編集用リグのカーブがあれば確かめてから置き換える。
        /// 取り込んだら Generic Pose の段は空にする（取り込んだ姿勢は Editing Rig の段から出る）
        /// </summary>
        public bool ImportGenericClip ()
        {
            return ImportGenericClip (true);
        }

        /// <param name="confirm">false なら確認のダイアログを出さない（スクリプトから使うとき。ダイアログはエディタを止める）</param>
        public bool ImportGenericClip (bool confirm)
        {
            string problem = GetImportProblem ();
            if (problem != null) {
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_CANNOT_IMPORT", problem), true);
                return false;
            }
            bool hasKeys = AnimationUtility.GetCurveBindings (editingClip_).Any (EditingClip.IsRigBinding);
            if (confirm && hasKeys && !EditorUtility.DisplayDialog ("Motion Editor",
                Tr ("PREVIEW_WINDOW_IMPORT_REPLACE_KEYS_CONFIRM", editingClip_.name, genericClip_.name), Tr ("PREVIEW_WINDOW_IMPORT_REPLACE"), Tr ("PREVIEW_WINDOW_CANCEL"))) {
                return false;
            }

            AnimationClip source = genericClip_;
            PoseLayer generic = poseStack_.Find (LayerKind.GenericPose);
            try {
                Undo.RecordObject (this, "Import Generic Clip");
                // 書き出し先の元の値も段の流れに入る（合成の相手）。値は最後にまとめて書くので、取り込み中は元の値が読める
                PoseImport.Result result = GenericImport.Import (source, editingClip_, stage_.editingRig, stage_.editingRigSolver,
                    time => poseStack_.EvaluateUntil (generic, time), targets_);
                genericClip_ = null;
                string text = Tr ("PREVIEW_WINDOW_IMPORT_IMPORTED_GENERIC", source.name, result.frameCount, result.milliseconds.ToString ("0"),
                    (result.maxError * 1000).ToString ("0.00"));
                if (result.notes.Count > 0) {
                    text += Tr ("PREVIEW_WINDOW_IMPORT_NOTES_COUNT", result.notes.Count);
                    Debug.Log (Tr ("PREVIEW_WINDOW_IMPORT_LOG_IMPORTED_NOTES", source.name, editingClip_.name, string.Join ("\n- ", result.notes)), editingClip_);
                }
                SetBakeStatus (text, false);
                return true;
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus (Tr ("PREVIEW_WINDOW_IMPORT_IMPORT_FAILED", e.Message), true);
                return false;
            }
            finally {
                if (stage_ != null) stage_.InvalidateClipCurves ();
                keyFrames_ = null;
                strayCurveCount_ = -1;
                SamplePose ();
                RaiseStateChanged ();
            }
        }
    }

}
