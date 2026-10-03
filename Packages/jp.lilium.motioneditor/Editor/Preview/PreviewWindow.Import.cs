using UnityEngine;
using UnityEditor;
using System.Linq;

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
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return "キャラが無い";
            if (importPreviewing_) return "取込の候補を試し見している";
            if (useHumanoidBase) {
                // 読み取り専用の土台へは書けない。Override を書き出し先にしていればそこへ打つ
                if (overrideWriteLayer == null) return "元が読み取り専用の Humanoid のクリップ（+Override で Override を足すか、取込で編集用クリップにする）";
            }
            else {
                if (editingClip_ == null) return "書き出し先が無い（Editing Rig の段で New）";
                if (clipProblem_ != null) return "書き出し先を編集できない: " + clipProblem_;
            }
            PoseLayer humanoid = poseStack_.Find (LayerKind.HumanoidAnimation);
            if (humanoid == null) return "Humanoid Pose の段が無い（表示モデルが Humanoid ではない）";
            if (!humanoid.enabled) return "Humanoid Pose の段の有効（👁）を落としている（反映は段の出力を写すので入れる）";
            if (!humanoid.active) return "Humanoid Pose の段が処理に入っていない（段の出力を写すため）";
            PoseLayer rig = poseStack_.Find (LayerKind.EditingRig);
            if (rig == null) return "Editing Rig の段が無い";
            if (!rig.enabled) return "Editing Rig の段の有効（👁）を落としている";
            if (rig.clipWeight < 1) return "Editing Rig のクリップの合成が 100% でない";
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
                SetBakeStatus ("反映できない: " + problem, true);
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
                SetBakeStatus ("反映: " + currentFrame + "F の姿勢を Editing Rig のクリップへ書いた（手足は FK）", false);
                return true;
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus ("反映できなかった: " + e.Message, true);
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
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Play Mode 中は取り込まない";
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return "キャラが無い";
            if (AnimBank.GetFolder (model) == null) return "置き場所（AnimBank のフォルダ）が決まらない";
            if (poseStack_.Find (LayerKind.EditingRig) == null) return "Editing Rig の段が無い";
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
                SetBakeStatus ("取り込めない: " + problem, true);
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
                SetBakeStatus ("取り込めない: Humanoid のクリップを選んでいない", true);
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
            if (!useHumanoidBase) return "元が Humanoid のクリップでない";
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
                SetBakeStatus ("取り込めない: " + problem, true);
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
                string text = "取込: " + source.name + " → " + destination.name + "（" + result.frameCount + "F・" + result.milliseconds.ToString ("0") + "ms・戻りのずれ "
                    + (result.maxError * 1000).ToString ("0.00") + "mm）";
                if (bakeOverrides.Count > 0) text += "  Override " + bakeOverrides.Count + " 枚込み";
                if (result.notes.Count > 0) {
                    text += "  注意 " + result.notes.Count + " 件";
                    Debug.Log ("MotionEditor: " + source.name + " を " + destination.name + " へ取り込んだ。注意:\n- " + string.Join ("\n- ", result.notes), destination);
                }
                SetBakeStatus (text, false);
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus ("取り込めなかった: " + e.Message, true);
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
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return "キャラが無い";
            if (genericClip_ == null) return "Generic のクリップが無い";
            if (overrideBlocksBaseWrite != null) return overrideBlocksBaseWrite;
            string problem = GenericImport.GetProblem (genericClip_);
            if (problem != null) return problem;
            if (editingClip_ == null) return "書き出し先が無い（Editing Rig の段で New）";
            if (clipProblem_ != null) return "書き出し先を編集できない: " + clipProblem_;
            PoseLayer generic = poseStack_.Find (LayerKind.GenericPose);
            if (generic == null) return "Generic Pose の段が並びに無い";
            if (!generic.active) return "Generic Pose の段が処理に入っていない（段の出力を書くため）";
            PoseLayer rig = poseStack_.Find (LayerKind.EditingRig);
            if (rig == null) return "Editing Rig の段が無い";
            if (!rig.enabled) return "Editing Rig の段の有効（👁）を落としている";
            if (rig.clipWeight < 1) return "Editing Rig のクリップの合成が 100% でない";
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
                SetBakeStatus ("取り込めない: " + problem, true);
                return false;
            }
            bool hasKeys = AnimationUtility.GetCurveBindings (editingClip_).Any (EditingClip.IsRigBinding);
            if (confirm && hasKeys && !EditorUtility.DisplayDialog ("Motion Editor",
                editingClip_.name + " のキーを、" + genericClip_.name + " を合成した姿勢から取り込んだ値で置き換えます。", "置き換える", "やめる")) {
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
                string text = "取り込み: " + source.name + "（" + result.frameCount + "F・" + result.milliseconds.ToString ("0") + "ms・戻りのずれ "
                    + (result.maxError * 1000).ToString ("0.00") + "mm）";
                if (result.notes.Count > 0) {
                    text += "  注意 " + result.notes.Count + " 件";
                    Debug.Log ("MotionEditor: " + source.name + " を " + editingClip_.name + " へ取り込んだ。注意:\n- " + string.Join ("\n- ", result.notes), editingClip_);
                }
                SetBakeStatus (text, false);
                return true;
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus ("取り込めなかった: " + e.Message, true);
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
