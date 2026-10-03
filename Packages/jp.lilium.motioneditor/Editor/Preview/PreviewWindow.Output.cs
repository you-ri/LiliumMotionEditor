using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 焼いて書き出す段（Output。S21）。足す・消す・並びの中で動かす。焼く中身は段の位置で決まる（その段より上を通った姿勢）
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// Layers の構造を編集する（段の追加・削除・移動）。普段の編集では構造は触らないので、入れている間だけ操作を出す（S21）
        /// </summary>
        [SerializeField] bool layerStructureEditing_;

        /// <summary>表示モデルが Humanoid か（Humanoid Pose と Output の段を置ける）</summary>
        public bool isHumanModel
        {
            get { return stage_ != null && stage_.animator != null && stage_.animator.isHuman; }
        }

        /// <summary>Humanoid Pose の段を置くか（S21。既定は置かない）</summary>
        public bool humanoidPoseLayer
        {
            get { return layerState.humanoidPose; }
        }

        public void SetHumanoidPoseLayer (bool value)
        {
            if (layerState.humanoidPose == value) return;
            Undo.RecordObject (layerStateOwner, value ? "Add Humanoid Pose" : "Remove Humanoid Pose");
            layerState.humanoidPose = value;
            SaveCharacterSettings ();
            BuildStack ();
            SamplePose ();
            RaiseStateChanged ();
        }

        public bool layerStructureEditing
        {
            get { return layerStructureEditing_; }
            set {
                if (layerStructureEditing_ == value) return;
                layerStructureEditing_ = value;
                RaiseStackChanged ();
                RaiseStateChanged ();
            }
        }

        /// <summary>Output の段の並び（キャラの設定か窓の Layers の状態）</summary>
        public IReadOnlyList<OutputEntry> outputEntries
        {
            get { return layerState.outputs; }
        }

        /// <summary>いまの段の並びにある Output の段（上から順）</summary>
        public List<OutputLayer> outputLayers
        {
            get { return poseStack_.layers.OfType<OutputLayer> ().ToList (); }
        }

        /// <summary>並びの設定を書き換える前に Undo へ記録する</summary>
        List<OutputEntry> EditOutputs (string undoName)
        {
            Undo.RecordObject (layerStateOwner, undoName);
            LayerState state = layerState;
            if (state.outputs == null) state.outputs = new List<OutputEntry> ();
            return state.outputs;
        }

        void OnOutputsChanged ()
        {
            SaveCharacterSettings ();
            BuildStack ();
            SamplePose ();
            RaiseStateChanged ();
        }

        static OutputEntry FindEntry (List<OutputEntry> entries, OutputLayer output)
        {
            return entries.FirstOrDefault (e => (e.name ?? "") == output.outputName);
        }

        public void AddOutput ()
        {
            List<OutputEntry> entries = EditOutputs ("Add Output");
            HashSet<string> used = new HashSet<string> (entries.Select (e => e.name ?? ""));
            string name = "";
            for (int n = 2; used.Contains (name); n++) name = "output" + n;
            // Humanoid Pose（無ければ Editing Rig と Override）の後ろに置く。そこに既に Output があれば、その後ろへ続ける
            PoseLayer anchor = poseStack_.layers.LastOrDefault (l => l.order <= PoseStack.kOrderHumanoid);
            string after = anchor != null ? anchor.id : LayerKind.HumanoidAnimation.ToString ();
            for (OutputEntry next; (next = entries.FirstOrDefault (e => e.after == after)) != null;) after = OutputLayer.IdFor (next);
            entries.Add (new OutputEntry { name = name, after = after });
            OnOutputsChanged ();
        }

        public void RemoveOutput (OutputLayer output)
        {
            if (output == null) return;
            List<OutputEntry> entries = EditOutputs ("Remove Output");
            OutputEntry entry = FindEntry (entries, output);
            if (entry == null) return;
            // この段の後ろに続けていた Output は、この段が続けていた段の後ろへ付け替える
            foreach (OutputEntry follower in entries) {
                if (follower.after == output.id) follower.after = entry.after;
            }
            entries.Remove (entry);
            OnOutputsChanged ();
        }

        public string GetMoveOutputProblem (OutputLayer output, int direction)
        {
            IReadOnlyList<PoseLayer> layers = poseStack_.layers;
            int index = poseStack_.IndexOf (output);
            if (index < 0) return "段の並びに無い";
            if (direction < 0 && index - 1 < 1) return "Editing Rig より上には置けない";
            if (direction < 0 && layers[index - 1] is FullBodyLayer) return "Full Body IK より上には置けない（全身 IK は編集用の体を作る段の一部）";
            if (direction > 0 && index + 1 >= layers.Count) return "いちばん下";
            return null;
        }

        /// <summary>
        /// 1 つ上か下の段と入れ替える。並びの設定は「直前の段」で持つので、入れ替えで前後が変わる Output の直前の段も付け替える
        /// </summary>
        public void MoveOutput (OutputLayer output, int direction)
        {
            if (output == null || GetMoveOutputProblem (output, direction) != null) return;
            IReadOnlyList<PoseLayer> layers = poseStack_.layers;
            int index = poseStack_.IndexOf (output);
            List<OutputEntry> entries = EditOutputs ("Move Output");
            OutputEntry entry = FindEntry (entries, output);
            if (entry == null) return;

            // この段の後ろに続けていた Output は、この段が続けていた段の後ろへ（動かしてもその場に残る）
            foreach (OutputEntry follower in entries) {
                if (follower != entry && follower.after == output.id) follower.after = entry.after;
            }
            if (direction < 0) {
                PoseLayer above = layers[index - 1];
                entry.after = layers[index - 2].id;
                // 上の段も Output なら、その段はこの段の後ろへ
                OutputLayer aboveOutput = above as OutputLayer;
                OutputEntry aboveEntry = aboveOutput != null ? FindEntry (entries, aboveOutput) : null;
                if (aboveEntry != null) aboveEntry.after = output.id;
            }
            else {
                PoseLayer below = layers[index + 1];
                entry.after = below.id;
                // 下の段の後ろに続いていた別の Output は、この段の後ろへ
                foreach (OutputEntry other in entries) {
                    if (other != entry && other.after == below.id) other.after = output.id;
                }
            }
            OnOutputsChanged ();
        }

        /// <summary>
        /// Output の段より上に、Humanoid Pose より後ろで姿勢を変える段（Rig・ゲーム側の段・Display Pose）が入っているか。
        /// 入っていれば、段の並びをそこまで通した表示モデルの骨から焼く。
        /// rig は、そのうち Rig の段が入っているか（入っていれば Rig の値は焼かない。二重に掛からないように）
        /// </summary>
        bool BakesThroughStack (OutputLayer output, out bool rig)
        {
            int index = poseStack_.IndexOf (output);
            rig = poseStack_.layers.Take (System.Math.Max (index, 0)).Any (l => l.active && l is RigBuilderLayer);
            return poseStack_.HasPoseChangeBefore (output);
        }

        public string DescribeOutput (OutputLayer output)
        {
            bool rig;
            if (!BakesThroughStack (output, out rig)) {
                return "Rig などより上なので、編集用の体から焼き、Rig の重みとターゲットは値として焼く。";
            }
            return rig
                ? "Rig を通った後なので、Rig の効果を骨に焼き、Rig の重みとターゲットは焼かない（ゲームで二重に掛からないように）。"
                : "ゲーム側の段などを通った後の表示モデルの骨から焼く。";
        }

        public string GetOutputPath (OutputLayer output)
        {
            return output != null ? HumanoidOutput.GetOutputPath (baseClip, bakeOverrides.Count > 0, output.outputName) : null;
        }

        public LayerClipStatus GetOutputStatus (OutputLayer output)
        {
            bool rig;
            // 元が Humanoid のクリップで Override が無く、Rig なども通らないなら、元そのものがゲームのクリップ（焼くものが無いのは異常ではない）
            if (useHumanoidBase && bakeOverrides.Count == 0 && !BakesThroughStack (output, out rig)) {
                return new LayerClipStatus (LayerClipState.None, "元の Humanoid のクリップそのまま（+Override で Override を足すと、元＋Override を焼ける）");
            }
            return HumanoidOutput.GetStatus (bakeSource, model, GetOutputBakeProblem (output), bakeOverrides, output.outputName);
        }

        public string GetOutputBakeProblem (OutputLayer output)
        {
            if (output == null) return "Output の段が無い";
            if (stage_ == null || stage_.editingRig == null || stage_.editingRig.root == null) return "キャラが無い";
            AnimationClip source = baseClip;
            if (source == null) return "クリップが無い";
            // Humanoid を土台にしているときは、土台のクリップ（ゲームのもの）は編集用クリップの形でなくてよい
            if (!useHumanoidBase && clipProblem_ != null) return clipProblem_;
            bool rig;
            if (useHumanoidBase && bakeOverrides.Count == 0 && !BakesThroughStack (output, out rig)) return "Override が無いので焼くものが無い（元のクリップそのまま）";
            return HumanoidOutput.GetProblem (source, GetOutputPath (output));
        }

        /// <summary>
        /// Output の段の位置まで段を通した姿勢を焼いて保存する。編集用の体は焼いている間に動くので、終わったら今のフレームを当て直す
        /// </summary>
        public bool BakeOutput (OutputLayer output)
        {
            string problem = GetOutputBakeProblem (output);
            if (problem != null) {
                SetBakeStatus ("Humanoid に焼けない: " + problem, true);
                return false;
            }

            bool rig;
            bool throughStack = BakesThroughStack (output, out rig);
            // 段を通して焼くときは、見比べ用の参照と演出の段の合成は焼かない（焼き終えたら戻す）
            List<KeyValuePair<PoseLayer, float>> clipWeights = new List<KeyValuePair<PoseLayer, float>> ();
            List<PoseLayer> hidden = new List<PoseLayer> ();
            if (throughStack) {
                foreach (PoseLayer layer in poseStack_.layers) {
                    if (layer.kind == LayerKind.HumanoidAnimation) {
                        clipWeights.Add (new KeyValuePair<PoseLayer, float> (layer, layer.clipWeight));
                        layer.clipWeight = 0;
                    }
                    if (layer.kind == LayerKind.TimelineClip && layer.enabled) {
                        hidden.Add (layer);
                        layer.enabled = false;
                    }
                }
            }

            try {
                List<AnimationClip> overrides = bakeOverrides;
                System.Action<float> apply = throughStack
                    ? time => poseStack_.EvaluateUntil (output, time)
                    : overrides.Count > 0 || useHumanoidBase ? ApplyBakePose : (System.Action<float>)null;
                using (HumanoidBaker baker = stage_.CreateHumanoidBaker (apply)) {
                    baker.readFromDisplay = throughStack;
                    // 段を通さずに焼くときも、キーの間で前後のキーが Locked の点は全身 IK で解く（画面の再生と同じ姿勢を焼く。S25d）
                    if (!throughStack) {
                        PoseLayer fullBody = poseStack_.Find (LayerKind.FullBodyIk);
                        float fullBodyWeight = fullBody != null && fullBody.active ? fullBody.weight : 0;
                        baker.afterSolve = () => stage_.SolveLockedSpan (fullBodyWeight);
                    }
                    baker.bakeRigProxies = !rig;
                    string path;
                    HumanoidBaker.Result result = HumanoidOutput.BakeToAsset (baker, baseClip, stage_.sourcePrefab, overrides, output.outputName, out path);
                    string text = "Humanoid: " + Path.GetFileName (path) + "（" + result.frameCount + "F・" + result.milliseconds.ToString ("0") + "ms）";
                    if (result.notes != null && result.notes.Count > 0) {
                        text += "  注意 " + result.notes.Count + " 件";
                        Debug.Log ("MotionEditor: " + path + " を焼いた。注意:\n- " + string.Join ("\n- ", result.notes), AssetDatabase.LoadMainAssetAtPath (path));
                    }
                    SetBakeStatus (text, false);
                }
                return true;
            }
            catch (System.Exception e) {
                Debug.LogException (e);
                SetBakeStatus ("Humanoid に焼けなかった: " + e.Message, true);
                return false;
            }
            finally {
                foreach (KeyValuePair<PoseLayer, float> pair in clipWeights) pair.Key.clipWeight = pair.Value;
                foreach (PoseLayer layer in hidden) layer.enabled = true;
                if (stage_ != null) stage_.InvalidateClipCurves ();
                SamplePose ();
            }
        }
    }

}
