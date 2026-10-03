using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Humanoid 版を焼く（S2）。手動の Bake と、クリップを保存したときの自動の焼き直し
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// クリップを保存したら Humanoid 版を焼き直す
        /// </summary>
        [SerializeField] bool autoBake_ = true;
        string bakeStatus_;
        bool bakeFailed_;

        /// <summary>保存したら焼き直すか。キャラの設定があればその値（上書きしていなければプロジェクト設定）、無ければ窓の中の値</summary>
        public bool autoBake
        {
            get {
                CharacterSettings settings = characterSettings;
                if (settings == null) return autoBake_;
                return settings.overrideAutoBake ? settings.autoBake : ProjectSettings.instance.autoBake;
            }
            set {
                if (autoBake == value) return;
                CharacterSettings settings = characterSettings;
                if (settings != null) {
                    Undo.RecordObject (settings, "Motion Editor Auto Bake");
                    settings.autoBake = value;
                    settings.overrideAutoBake = true;
                    SaveCharacterSettings ();
                }
                else {
                    Undo.RecordObject (this, "Motion Editor Auto Bake");
                    autoBake_ = value;
                }
                RaiseStateChanged ();
            }
        }

        /// <summary>
        /// 最後に焼いた結果（焼いていなければ null）
        /// </summary>
        public string bakeStatus
        {
            get { return bakeStatus_; }
        }

        public bool bakeFailed
        {
            get { return bakeFailed_; }
        }

        /// <summary>
        /// 焼いたクリップの置き場所（いちばん上の Output の段。Output が無ければ名前の無い置き場所。クリップがアセットでなければ null）
        /// </summary>
        public string humanoidOutputPath
        {
            get {
                OutputLayer first = poseStack_ != null ? poseStack_.layers.OfType<OutputLayer> ().FirstOrDefault () : null;
                return first != null ? GetOutputPath (first) : HumanoidOutput.GetOutputPath (baseClip, bakeOverrides.Count > 0);
            }
        }

        /// <summary>
        /// 焼けない理由（いちばん上の Output の段。焼けるなら null）
        /// </summary>
        public string GetBakeProblem ()
        {
            OutputLayer first = poseStack_ != null ? poseStack_.layers.OfType<OutputLayer> ().FirstOrDefault () : null;
            return first != null ? GetOutputBakeProblem (first) : "Output の段が無い（Layers の上端の「+Output」で足す）";
        }

        /// <summary>
        /// Output の段を全部焼く（S21）。焼けない段があれば false
        /// </summary>
        public bool BakeHumanoid ()
        {
            List<OutputLayer> outputs = outputLayers;
            if (outputs.Count == 0) {
                SetBakeStatus ("Humanoid に焼けない: " + GetBakeProblem (), true);
                return false;
            }
            bool ok = true;
            foreach (OutputLayer output in outputs) ok &= BakeOutput (output);
            return ok;
        }

        /// <summary>
        /// ゲームが使っているクリップ（差し替えの元）。編集用クリップを土台にしているときは「元だけを焼いた版」、
        /// Humanoid のクリップを土台にしているときはそのクリップ
        /// </summary>
        public AnimationClip gameClip
        {
            get {
                if (useHumanoidBase) return editingClip_;
                string path = HumanoidOutput.GetOutputPath (editingClip_, false);
                return string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            }
        }

        /// <summary>焼いた「元＋Override」のクリップ（無ければ null）</summary>
        public AnimationClip editedClip
        {
            get {
                string path = bakeOverrides.Count > 0 ? humanoidOutputPath : null;
                return string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            }
        }

        /// <summary>参照を差し替えられない理由（できるなら null）</summary>
        public string GetSwapProblem ()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Play Mode 中は差し替えない";
            if (bakeOverrides.Count == 0) return "Override が無い";
            if (gameClip == null) return "ゲームが使っているクリップが分からない（先に元のクリップを焼く）";
            if (editedClip == null) return "「元＋Override」を焼いていない（Bake で作る）";
            return null;
        }

        /// <summary>
        /// 技や Timeline から、ゲームが使っているクリップへの参照を「元＋Override」を焼いたクリップへ差し替える（S14）。
        /// アセットの書き換えは Undo で戻せないので、対象の一覧を見せて確かめてから行う。back を立てると元へ戻す
        /// </summary>
        public bool SwapClipReferences (bool back)
        {
            string problem = GetSwapProblem ();
            if (problem != null) {
                SetBakeStatus ("差し替えられない: " + problem, true);
                return false;
            }
            AnimationClip from = back ? editedClip : gameClip;
            AnimationClip to = back ? gameClip : editedClip;
            ClipReferences.Plan plan = ClipReferences.Find (from);
            if (plan.assets.Count == 0) {
                string note = "差し替える参照が見つからない: " + from.name;
                if (plan.skipped.Count > 0) note += "（触らないもの " + plan.skipped.Count + " 件）";
                SetBakeStatus (note, plan.skipped.Count > 0);
                if (plan.skipped.Count > 0) Debug.Log ("MotionEditor: 触らないもの:\n- " + string.Join ("\n- ", plan.skipped));
                return false;
            }

            const int kShow = 12;
            string list = string.Join ("\n", plan.assets.GetRange (0, Mathf.Min (kShow, plan.assets.Count)));
            if (plan.assets.Count > kShow) list += "\n… ほか " + (plan.assets.Count - kShow) + " 件";
            if (plan.skipped.Count > 0) list += "\n\n触らないもの " + plan.skipped.Count + " 件（Console に出す）";
            if (!EditorUtility.DisplayDialog ("Motion Editor",
                from.name + " への参照を " + to.name + " へ差し替えます（Undo では戻せません）。\n"
                + "保存していない変更があるアセットは見つかりません（先に保存してください）。\n\n" + list, "差し替える", "やめる")) {
                return false;
            }
            if (plan.skipped.Count > 0) Debug.Log ("MotionEditor: 触らないもの:\n- " + string.Join ("\n- ", plan.skipped));

            List<string> changed = new List<string> ();
            int count = ClipReferences.Replace (from, to, plan.assets, changed);
            AssetDatabase.Refresh ();
            SetBakeStatus ("参照を差し替えた: " + from.name + " → " + to.name + "（" + count + " 件）", false);
            if (changed.Count > 0) Debug.Log ("MotionEditor: 参照を差し替えた（" + from.name + " → " + to.name + "）:\n- " + string.Join ("\n- ", changed));
            return count > 0;
        }

        void SetBakeStatus (string text, bool failed)
        {
            bakeStatus_ = text;
            bakeFailed_ = failed;
            RaiseStateChanged ();
            RepaintView ();
        }

        /// <summary>
        /// クリップが保存されたとき（HumanoidSaveWatcher）。開いているクリップなら焼き直す
        /// </summary>
        internal void OnClipsSaved (HashSet<string> paths)
        {
            if (!autoBake || editingClip_ == null || stage_ == null) return;
            // 開いているクリップか、重ねている Override のクリップが保存されたら焼き直す（Humanoid の土台は保存されないので、Override の保存で焼く）
            bool saved = paths.Contains (AssetDatabase.GetAssetPath (editingClip_));
            foreach (OverrideClip entry in overrides_) {
                if (entry != null && entry.clip != null && paths.Contains (AssetDatabase.GetAssetPath (entry.clip))) saved = true;
            }
            if (!saved) return;
            // Output の段を全部焼き直す。焼けない段（Override の無い Humanoid の土台・編集できないクリップ）は飛ばす
            foreach (OutputLayer output in outputLayers) {
                if (GetOutputBakeProblem (output) == null) BakeOutput (output);
            }
        }
    }

}
