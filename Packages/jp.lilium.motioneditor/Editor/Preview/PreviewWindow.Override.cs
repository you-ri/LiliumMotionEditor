using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 元のクリップ（Base Layer）の上に Override のクリップを重ねる（S14）。元は書き換えず、書き込み先に Override を選ぶと、
    /// キーを打つ・動かす・消す・貼り付ける操作はすべて Override のクリップへ入る（Additive なら元からの差分として）。
    /// 書き込み先より上の層は、編集中は効かせない（効かせたまま書くと、上の層の分まで書き込み先へ入ってしまう）
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>編集中のクリップに重ねている Override（下から順。1 枚が 1 段）。どの元に重ねるか・合成の仕方は各クリップの .meta にもある</summary>
        [SerializeField] List<OverrideClip> overrides_ = new List<OverrideClip> ();
        /// <summary>overrides_ を読んだときの元のクリップ（替わったら読み直す）</summary>
        [SerializeField] AnimationClip overridesBase_;
        /// <summary>
        /// 旧形式の「H土台」のチェック（S14。Humanoid Pose の段のクリップを土台にしていた）。
        /// 今は Editing Rig の段に Humanoid のクリップを開けば土台になるので、読み込んだ窓の状態を 1 回だけ移す（MigrateHumanoidBase）
        /// </summary>
        [SerializeField] bool useHumanoidBase_;

        /// <summary>
        /// Editing Rig の段に Humanoid のクリップ（FBX の中のクリップなど）を開いている。そのクリップは読み取り専用の土台で、
        /// フレームごとに操作値へ直して使う。書き込みは Override へ（S20）
        /// </summary>
        public bool useHumanoidBase
        {
            get { return editingClip_ != null && editingClip_.humanMotion; }
        }

        /// <summary>
        /// 旧形式の窓の状態（H土台 のチェック＋Humanoid Pose の段のクリップ）を、Editing Rig の段のクリップへ移す
        /// </summary>
        void MigrateHumanoidBase ()
        {
            if (!useHumanoidBase_) return;
            useHumanoidBase_ = false;
            if (humanoidClip_ == null) return;
            editingClip_ = humanoidClip_;
            humanoidClip_ = null;
        }

        /// <summary>
        /// Humanoid を土台にしていて Override があるなら、書き込み先を一番上の Override にする。
        /// 土台のクリップへは書かない（編集用クリップが無い）ので、開き直したときに書き込み先が Editing Rig のままだとキーを打つ先が無い
        /// </summary>
        void WriteToTopOverrideOnHumanoidBase ()
        {
            if (!useHumanoidBase || overrides_.Count == 0 || poseStack_ == null || overrideWriteLayer != null) return;
            OverrideLayer top = FindOverrideLayer (overrides_.Count - 1);
            if (top != null) SetWriteLayer (top);
        }

        /// <summary>
        /// 土台のクリップ（Override を重ねる相手）。Editing Rig の段に開いているクリップ（編集用クリップか、読み取り専用の Humanoid のクリップ）
        /// </summary>
        public AnimationClip baseClip
        {
            get { return editingClip_; }
        }

        public IReadOnlyList<OverrideClip> overrides
        {
            get { return overrides_; }
        }

        /// <summary>書き込み先（✏）に選んでいる Override の段（元のクリップへ書くなら null）</summary>
        OverrideLayer overrideWriteLayer
        {
            get { return poseStack_ != null ? poseStack_.writeLayer as OverrideLayer : null; }
        }

        /// <summary>何枚目の Override の段か（段の並びに無ければ null）</summary>
        OverrideLayer FindOverrideLayer (int index)
        {
            return poseStack_ != null ? poseStack_.Find<OverrideLayer> (l => l.index == index) : null;
        }

        public int overrideWriteIndex
        {
            get {
                OverrideLayer layer = overrideWriteLayer;
                return layer != null ? layer.index : -1;
            }
        }

        /// <summary>
        /// キーを打つ・動かす・消すクリップ。書き込み先が Override ならそのクリップ、そうでなければ元のクリップ
        /// </summary>
        public AnimationClip targetClip
        {
            get {
                OverrideLayer layer = overrideWriteLayer;
                return layer != null ? layer.overrideClip : editingClip_;
            }
        }

        bool IsOverrideClip (AnimationClip clip)
        {
            if (clip == null) return false;
            foreach (OverrideClip entry in overrides_) {
                if (entry.clip == clip) return true;
            }
            return false;
        }

        /// <summary>
        /// 元のクリップが替わったら、それに重ねている Override を探し直す。書き込み先は一番上の層にする
        /// </summary>
        void LoadOverrides ()
        {
            if (overridesBase_ == baseClip && overrides_ != null) {
                overrides_.RemoveAll (l => l == null || l.clip == null);
                return;
            }
            overridesBase_ = baseClip;
            overrides_ = baseClip != null ? ReadOverrides (baseClip) : new List<OverrideClip> ();
        }

        /// <summary>
        /// 元のクリップに重ねる Override を読む。キャラの設定にあればそれ、無ければ Override のクリップの .meta の印から（以前の置き方・設定の無いキャラ）
        /// </summary>
        List<OverrideClip> ReadOverrides (AnimationClip source)
        {
            CharacterSettings settings = characterSettings;
            if (settings != null) {
                string guid, name;
                OverrideBaseKey (source, out guid, out name);
                List<OverrideClip> result = new List<OverrideClip> ();
                bool any = false;
                foreach (OverrideRecord record in settings.layers.overrides) {
                    if (record == null || record.baseClip != guid || (record.baseName ?? "") != name) continue;
                    any = true;
                    AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (AssetDatabase.GUIDToAssetPath (record.clip));
                    if (clip == null) continue;
                    OverrideMode mode;
                    if (!System.Enum.TryParse (record.mode, out mode)) mode = OverrideMode.Additive;
                    result.Add (new OverrideClip { clip = clip, mode = mode, order = result.Count });
                }
                if (any) return result;
            }
            return OverrideFiles.FindFor (source);
        }

        static void OverrideBaseKey (AnimationClip source, out string guid, out string name)
        {
            guid = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (source));
            name = AssetDatabase.IsSubAsset (source) ? source.name : "";
        }

        /// <summary>
        /// 今の Override の並び（どのクリップ・合成の仕方・順）を保存する。キャラの設定があればそこへ（.meta の印は消して移す）、
        /// 無ければ各クリップの .meta へ（窓を閉じても消えないように）
        /// </summary>
        void SaveOverrides ()
        {
            AnimationClip source = baseClip;
            if (source == null) return;
            for (int i = 0; i < overrides_.Count; i++) overrides_[i].order = i;
            CharacterSettings settings = characterSettings;
            if (settings == null) {
                foreach (OverrideClip entry in overrides_) OverrideFiles.Save (entry, source);
                return;
            }
            Undo.RecordObject (settings, "Motion Editor Overrides");
            string guid, name;
            OverrideBaseKey (source, out guid, out name);
            List<OverrideRecord> records = settings.layers.overrides;
            records.RemoveAll (r => r == null || (r.baseClip == guid && (r.baseName ?? "") == name));
            foreach (OverrideClip entry in overrides_) {
                if (entry == null || entry.clip == null) continue;
                records.Add (new OverrideRecord {
                    baseClip = guid,
                    baseName = name,
                    clip = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (entry.clip)),
                    mode = entry.mode.ToString (),
                });
                // 以前の置き方（.meta の印）は消す（キャラの設定が正になる）
                if (OverrideFiles.IsOverride (entry.clip)) OverrideFiles.Unlink (entry.clip);
            }
            SaveCharacterSettings ();
        }

        /// <summary>Override を足せない理由（足せるなら null）</summary>
        public string GetAddOverrideProblem ()
        {
            AnimationClip source = baseClip;
            if (source == null) return "元のクリップを開いてください";
            if (!EditorUtility.IsPersistent (source)) return "保存したクリップにだけ重ねられる";
            return useHumanoidBase ? null : clipProblem_;
        }

        /// <summary>
        /// 新しい Override のクリップを作って一番上に重ね、書き込み先にする
        /// </summary>
        public void AddOverride ()
        {
            if (GetAddOverrideProblem () != null) return;
            OverrideClip entry = OverrideFiles.Create (baseClip, characterSettings == null);
            if (entry == null) return;
            // 一番下（Humanoid Pose のすぐ上）に重ねる
            overrides_.Add (entry);
            SaveOverrides ();
            AfterOverrideChange (true);
            // 足した段を書き出し先にする（つかむ段も一緒に移る）
            OverrideLayer added = FindOverrideLayer (overrides_.Count - 1);
            if (added != null) SetWriteLayer (added);
        }

        /// <summary>
        /// 層のクリップを置き換える。null で層を外す（ファイルは消さず、元に重ねる印だけ消す）
        /// </summary>
        public void SetOverrideClip (int index, AnimationClip clip)
        {
            if (index < 0 || index >= overrides_.Count) return;
            OverrideClip entry = overrides_[index];
            if (clip == entry.clip) return;
            if (clip == null || clip == baseClip) {
                OverrideFiles.Unlink (entry.clip);
                overrides_.RemoveAt (index);
            } else {
                OverrideFiles.Unlink (entry.clip);
                entry.clip = clip;
            }
            SaveOverrides ();
            AfterOverrideChange (true);
        }

        /// <summary>
        /// Override の段を外す。deleteAsset を立てるとクリップのファイルも消す（Undo では戻せないので確かめてから）
        /// </summary>
        public void RemoveOverride (int index, bool deleteAsset)
        {
            if (index < 0 || index >= overrides_.Count) return;
            AnimationClip clip = overrides_[index].clip;
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            if (deleteAsset && !string.IsNullOrEmpty (path)) {
                if (!EditorUtility.DisplayDialog ("Motion Editor", path + " を消します（Undo では戻せません）。", "消す", "やめる")) return;
            }
            OverrideFiles.Unlink (clip);
            overrides_.RemoveAt (index);
            SaveOverrides ();
            if (deleteAsset && !string.IsNullOrEmpty (path)) AssetDatabase.DeleteAsset (path);
            AfterOverrideChange (true);
            SetBakeStatus (deleteAsset ? "Override を消した: " + path : "Override を外した（ファイルは残る）: " + path, false);
        }

        /// <summary>
        /// Override の段を 1 つ上（-1）か下（+1）へ動かせない理由（動かせるなら null）。
        /// Override は編集用リグの値に重ねる段なので、Editing Rig と Humanoid Pose の間でだけ並べ替える
        /// </summary>
        public string GetMoveOverrideProblem (int index, int direction)
        {
            if (index < 0 || index >= overrides_.Count) return "段が無い";
            if (index + direction < 0) return "Editing Rig より上には置けない（Override は元のクリップに重ねる）";
            if (index + direction >= overrides_.Count) return "Humanoid Pose より下には置けない（Override は編集用リグの値に重ねる段なので、値のある Humanoid Pose より上だけ）";
            return null;
        }

        /// <summary>
        /// Override の段を隣の Override と入れ替える。重ねる順は各クリップの .meta に書く（S21）。書き出し先（✏）は動かした段に付いていく
        /// </summary>
        public void MoveOverride (int index, int direction)
        {
            if (GetMoveOverrideProblem (index, direction) != null) return;
            int target = index + direction;
            int write = overrideWriteIndex;
            OverrideClip moved = overrides_[index];
            overrides_[index] = overrides_[target];
            overrides_[target] = moved;
            SaveOverrides ();
            AfterOverrideChange (true);
            int newWrite = write == index ? target : write == target ? index : write;
            if (newWrite >= 0 && newWrite != write) {
                OverrideLayer layer = FindOverrideLayer (newWrite);
                if (layer != null) SetWriteLayer (layer);
            }
        }

        /// <summary>合成の仕方（Additive / Override）を変える（.meta にも書く）</summary>
        public void SetOverrideMode (int index, OverrideMode mode)
        {
            if (index < 0 || index >= overrides_.Count) return;
            OverrideClip entry = overrides_[index];
            if (entry.mode == mode) return;
            entry.mode = mode;
            SaveOverrides ();
            AfterOverrideChange (true);
        }

        void AfterOverrideChange (bool rebuildStack)
        {
            keyFrames_ = null;
            ghostsDirty_ = true;
            if (stage_ != null) stage_.InvalidateClipCurves ();
            // 段の並びが変わる（Override の段は 1 枚 1 段）
            if (rebuildStack) BuildStack ();
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 書き込み先が Override のとき、打つ値（狙いの値）をクリップに入れる値へ直す口を書き出し口に付ける。
        /// 元の値は、姿勢を作ったときに書き込み先の層の手前で控えたもの（PreviewStage.ApplyOverrides）
        /// </summary>
        void AttachOverrideStore (CurveWriter writer)
        {
            OverrideLayer layer = overrideWriteLayer;
            if (layer == null || stage_ == null) return;
            OverrideMode mode = layer.mode;

            PreviewStage stage = stage_;
            writer.storePosition = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StorePosition (mode, position, value) : value;
            };
            writer.storeRotation = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StoreRotation (mode, rotation, value) : value;
            };
            writer.storeScale = (path, value) => {
                Vector3 position;
                Quaternion rotation;
                Vector3 scale;
                return stage.TryGetOverrideSource (path, out position, out rotation, out scale) ? OverrideMath.StoreScale (mode, scale, value) : value;
            };
            writer.storeFloat = (binding, value) => {
                float source;
                return stage.TryGetOverrideSourceFloat (binding.path, binding.type, binding.propertyName, out source)
                    ? OverrideMath.StoreFloat (mode, Lilium.IkControl.IsAdditive (binding.propertyName), source, value)
                    : value;
            };
        }

        /// <summary>焼く元のクリップ（土台）</summary>
        public AnimationClip bakeSource
        {
            get { return baseClip; }
        }

        /// <summary>
        /// 焼くときに重ねる Override のクリップ（効いている段だけ。下から順。S14）。
        /// ✏ の位置は見ない（焼くのは「元＋効いている Override」の見た目どおりの姿勢）
        /// </summary>
        public List<AnimationClip> bakeOverrides
        {
            get {
                List<AnimationClip> result = new List<AnimationClip> ();
                if (poseStack_ == null) return result;
                foreach (PoseLayer layer in poseStack_.layers) {
                    OverrideLayer overrideLayer = layer as OverrideLayer;
                    if (overrideLayer == null || overrideLayer.overrideClip == null) continue;
                    if (!overrideLayer.enabled || overrideLayer.weight <= 0) continue;
                    result.Add (overrideLayer.overrideClip);
                }
                return result;
            }
        }

        /// <summary>
        /// 焼くときに、そのフレームの姿勢（操作値）を作る。土台（編集用クリップか Humanoid のクリップ）を当て、効いている Override を重ねる。
        /// 解くのは焼く側（HumanoidBaker）
        /// </summary>
        void ApplyBakePose (float time)
        {
            if (stage_ == null) return;
            stage_.ResetControlsToRest ();
            if (useHumanoidBase) stage_.SampleHumanoidAsControls (editingClip_, time);
            else if (editingClip_ != null) stage_.SampleClip (editingClip_, time);
            // 全身 IK の点の状態（キーの間で Locked の点を解くか）は元のクリップで決める
            stage_.MaskBodyPointsForBake (useHumanoidBase ? null : editingClip_, time);
            if (poseStack_ == null) return;
            foreach (PoseLayer layer in poseStack_.layers) {
                OverrideLayer overrideLayer = layer as OverrideLayer;
                if (overrideLayer == null || overrideLayer.overrideClip == null) continue;
                if (!overrideLayer.enabled || overrideLayer.weight <= 0) continue;
                stage_.ApplyOverride (overrideLayer.overrideClip, overrideLayer.mode, overrideLayer.weight, time);
            }
        }

        /// <summary>統合できない理由（できるなら null）</summary>
        public string GetMergeOverridesProblem ()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Play Mode 中は統合しない";
            if (useHumanoidBase) return "元が Humanoid のクリップ（読み取り専用）なので統合できない。Bake で新しいクリップに出すか、取込で編集用クリップにする";
            if (editingClip_ == null) return "元のクリップが無い";
            if (clipProblem_ != null) return clipProblem_;
            if (!EditorUtility.IsPersistent (editingClip_)) return "保存したクリップにだけ統合できる";
            if (bakeOverrides.Count == 0) return "効いている Override が無い";
            return null;
        }

        /// <summary>
        /// 効いている Override を元のクリップへ焼き込む（統合。S14）。合成した値をフレームごとに全部打ってから、
        /// 無くても同じになるキーを削る。焼き込んだ Override のクリップは消す（Undo では戻せないので、先に確かめる）
        /// </summary>
        public bool MergeOverrides ()
        {
            return MergeOverrides (true);
        }

        /// <param name="confirm">焼き込む前に対象を見せて確かめる（テストや自動化からは false で呼ぶ）</param>
        internal bool MergeOverrides (bool confirm)
        {
            string problem = GetMergeOverridesProblem ();
            if (problem != null) {
                SetBakeStatus ("統合できない: " + problem, true);
                return false;
            }

            List<AnimationClip> merged = bakeOverrides;
            int last = 0;
            foreach (int frame in ClipKeyUtility.GetKeyFrames (editingClip_)) last = Mathf.Max (last, frame);
            foreach (AnimationClip clip in merged) {
                foreach (int frame in ClipKeyUtility.GetKeyFrames (clip)) last = Mathf.Max (last, frame);
            }
            string names = string.Join ("\n", merged.ConvertAll (c => AssetDatabase.GetAssetPath (c)));
            if (confirm && !EditorUtility.DisplayDialog ("Motion Editor",
                merged.Count + " 枚の Override を " + editingClip_.name + " へ焼き込みます（0〜" + last + "F を打ち直します）。\n"
                + "焼き込んだ Override のクリップは消えます（Undo では戻せません）。\n\n" + names, "統合する", "やめる")) {
                return false;
            }

            int removed;
            try {
                Undo.IncrementCurrentGroup ();
                Undo.RecordObject (editingClip_, "Merge Override");
                Transform root = stage_ != null && stage_.editingRig != null && stage_.editingRig.root != null ? stage_.editingRig.root.transform : null;
                int frameCount = last + 1;
                // 書きながら同じクリップを読むと差分が二重に乗るので、全フレームぶん集めてから一度に書く（取り込みと同じ手順）
                Dictionary<EditorCurveBinding, float[]> values = new Dictionary<EditorCurveBinding, float[]> ();
                HashSet<string> rotationPaths = new HashSet<string> ();
                for (int frame = 0; frame < frameCount; frame++) {
                    // 画面と同じ作り方（土台＋効いている Override）で姿勢を作る
                    ApplyBakePose (frame / clock_.rate);
                    if (stage_ != null) stage_.Solve ();
                    using (CurveWriter writer = CurveWriter.Begin (null, root, frame, null)) {
                        foreach (PoseTarget target in targets_) target.WriteKeys (writer);
                        if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
                        foreach (string path in writer.rotationPaths) rotationPaths.Add (path);
                        foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) {
                            float[] curve;
                            if (!values.TryGetValue (pair.Key, out curve)) {
                                curve = new float[frameCount];
                                values.Add (pair.Key, curve);
                            }
                            curve[frame] = pair.Value;
                        }
                    }
                }
                PoseImport.WriteCollected (editingClip_, values, rotationPaths, frameCount, clock_.rate);
                removed = OverrideMerge.ReduceKeys (editingClip_);
            }
            catch (System.Exception exception) {
                Debug.LogException (exception);
                SetBakeStatus ("統合できなかった: " + exception.Message, true);
                return false;
            }

            // 焼き込んだ Override は外して消す
            foreach (AnimationClip clip in merged) {
                OverrideFiles.Unlink (clip);
                AssetDatabase.DeleteAsset (AssetDatabase.GetAssetPath (clip));
            }
            overrides_.RemoveAll (entry => entry == null || entry.clip == null);
            SaveOverrides ();
            overridesBase_ = null;
            LoadOverrides ();
            AssetDatabase.SaveAssetIfDirty (editingClip_);
            AfterOverrideChange (true);
            SetBakeStatus ("統合: " + merged.Count + " 枚を " + editingClip_.name + " へ焼き込んだ（0〜" + last + "F を打ち直し、要らないキーを " + removed + " 本削った）", false);
            return true;
        }

        /// <summary>元のクリップへ書く操作（取り込み・プロパティ）を、書き込み先が Override のときに止める理由</summary>
        string overrideBlocksBaseWrite
        {
            get { return overrideWriteLayer != null ? "書き込み先が Override なので使えない（元のクリップへ書く操作。書き込み先を元に戻すと使える）" : null; }
        }
    }

}
