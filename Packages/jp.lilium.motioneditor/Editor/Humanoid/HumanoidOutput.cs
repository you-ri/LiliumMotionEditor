using UnityEngine;
using UnityEditor;
using System.IO;

namespace Lilium
{

    /// <summary>
    /// 焼いた Humanoid のクリップの置き場所と、元のクリップ・キャラとの対応。
    /// - 置き場所は元のクリップと同じフォルダ。`Attack.rig.anim` → `Attack.anim`、`.rig` の無い名前なら `Attack.humanoid.anim`
    /// - 対応（元のクリップとキャラの GUID）は出力の .meta の userData に書く。AnimationClip は利用者のデータを持てないため。
    ///   Timeline など Humanoid 側から編集用のクリップを引くとき（S3・S15a）もこれを読む
    /// - 対応の無い既存のクリップ（手で作ったもの・ゲームのもの）は上書きしない
    /// </summary>
    public static class HumanoidOutput
    {
        public const string kHumanoidSuffix = ".humanoid";
        /// <summary>Override を重ねて焼いたクリップの名前（S14）。`Attack.rig.anim` ＋ Override → `Attack.edited.anim`</summary>
        public const string kEditedSuffix = ".edited";

        [System.Serializable]
        public sealed class Link
        {
            /// <summary>元の編集用クリップの GUID</summary>
            public string source;
            /// <summary>焼いたときのキャラ（prefab）の GUID</summary>
            public string model;
            /// <summary>
            /// 焼いたときの元のファイルの中身のハッシュ。保存していない変更を含めて焼いたときは kUnsaved、古い対応では空
            /// </summary>
            public string sourceHash;
            /// <summary>一緒に焼いた Override のクリップの GUID（S14。無ければ空）</summary>
            public string[] overrides;
            /// <summary>焼いたときの Override のファイルの中身のハッシュ（つないだもの）</summary>
            public string overridesHash;
            /// <summary>
            /// 焼いたときのリグの定義のうち、姿勢を変える設定のハッシュ（全身 IK の設定。S25）。2 本骨 IK の定義・古い対応では空
            /// </summary>
            public string definition;
        }

        public const string kUnsaved = "unsaved";

        /// <summary>
        /// 焼いて保存した後（元のクリップ・焼いたクリップ）。焼いたクリップを別の形へ変換し直す側（ゲームの変換など）が受ける。
        /// エディタの中で保存したアセットは取り込み直されないので、AssetPostprocessor では拾えない
        /// </summary>
        public static event System.Action<AnimationClip, AnimationClip> baked;

        static readonly System.Collections.Generic.Dictionary<string, HashCache> hashCache_ = new System.Collections.Generic.Dictionary<string, HashCache> ();

        struct HashCache
        {
            public long ticks;
            public long length;
            public string hash;
        }

        /// <summary>
        /// 焼いたクリップの置き場所。元がアセットでなければ null
        /// </summary>
        public static string GetOutputPath (AnimationClip source)
        {
            return GetOutputPath (source, false);
        }

        public static string GetOutputPath (AnimationClip source, bool edited)
        {
            return GetOutputPath (SourcePath (source), edited);
        }

        /// <param name="outputName">焼いて書き出す段（Output）の名前（S21）。空なら 名前.anim、あれば 名前.&lt;outputName&gt;.anim</param>
        public static string GetOutputPath (AnimationClip source, bool edited, string outputName)
        {
            return WithOutputName (GetOutputPath (source, edited), outputName);
        }

        static string WithOutputName (string path, string outputName)
        {
            if (string.IsNullOrEmpty (path) || string.IsNullOrEmpty (outputName)) return path;
            return path.Substring (0, path.Length - EditingClip.kExtension.Length) + "." + outputName + EditingClip.kExtension;
        }

        /// <summary>
        /// 出力の名前を決めるための元のパス。FBX の中のクリップ（サブアセット）は、FBX と同じフォルダにクリップの名前で置いたものとして扱う
        /// （同じ FBX の別のテイクと出力が重ならないように。S20）
        /// </summary>
        static string SourcePath (AnimationClip source)
        {
            string path = source != null ? AssetDatabase.GetAssetPath (source) : null;
            if (string.IsNullOrEmpty (path) || !AssetDatabase.IsSubAsset (source)) return path;
            return Path.GetDirectoryName (path).Replace ('\\', '/') + "/" + source.name + EditingClip.kExtension;
        }

        /// <param name="path">元のクリップのアセットのパス</param>
        public static string GetOutputPath (string path)
        {
            return GetOutputPath (path, false);
        }

        /// <param name="edited">Override を重ねて焼く（元の焼いた版を残し、別のクリップに出す。S14）</param>
        public static string GetOutputPath (string path, bool edited)
        {
            if (string.IsNullOrEmpty (path)) return null;

            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            string name = Path.GetFileNameWithoutExtension (path);
            bool rig = name.EndsWith (EditingClip.kSuffix, System.StringComparison.OrdinalIgnoreCase);
            string baseName = rig ? name.Substring (0, name.Length - EditingClip.kSuffix.Length) : name;
            if (edited) baseName += kEditedSuffix;
            else if (!rig) baseName += kHumanoidSuffix;
            return folder + "/" + baseName + EditingClip.kExtension;
        }

        /// <summary>
        /// 出力先に書けない理由（書けるなら null）
        /// </summary>
        public static string GetProblem (AnimationClip source, string outputPath)
        {
            if (string.IsNullOrEmpty (outputPath)) return "クリップがアセットでないので置き場所が決まらない（保存してから焼く）";
            if (AssetDatabase.GetAssetPath (source) == outputPath) return "出力先が元のクリップと同じ";

            Object existing = AssetDatabase.LoadMainAssetAtPath (outputPath);
            if (existing == null) return null;
            if (!(existing is AnimationClip)) return "出力先に別の種類のアセットがある: " + outputPath;

            Link link = ReadLink (outputPath);
            string sourceGuid = AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (source));
            if (link == null) return "出力先にエディタが作っていないクリップがあるので上書きしない: " + outputPath;
            if (link.source != sourceGuid) return "出力先は別のクリップから作られたもの: " + outputPath;
            return null;
        }

        public static Link ReadLink (string outputPath)
        {
            AssetImporter importer = AssetImporter.GetAtPath (outputPath);
            if (importer == null || string.IsNullOrEmpty (importer.userData)) return null;
            try {
                Link link = JsonUtility.FromJson<Link> (importer.userData);
                return link != null && !string.IsNullOrEmpty (link.source) ? link : null;
            }
            catch (System.ArgumentException) {
                return null;
            }
        }

        public static void WriteLink (string outputPath, AnimationClip source, GameObject model)
        {
            WriteLink (outputPath, source, model, null);
        }

        /// <param name="overrides">一緒に焼いた Override のクリップ（S14。無ければ null）</param>
        /// <param name="definitionHash">リグの定義のうち、姿勢を変える設定のハッシュ（EditRigDefinition.GetFullBodyHash。無ければ null）</param>
        public static void WriteLink (string outputPath, AnimationClip source, GameObject model, System.Collections.Generic.IList<AnimationClip> overrides, string definitionHash = null)
        {
            AssetImporter importer = AssetImporter.GetAtPath (outputPath);
            if (importer == null) return;
            string sourcePath = AssetDatabase.GetAssetPath (source);
            Link link = new Link {
                source = AssetDatabase.AssetPathToGUID (sourcePath),
                model = model != null ? AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (model)) : "",
                // 保存していない変更を含めて焼いたら、ファイルの中身とは対応しない
                sourceHash = EditorUtility.IsDirty (source) ? kUnsaved : ComputeFileHash (sourcePath),
                overrides = OverrideGuids (overrides),
                overridesHash = OverridesHash (overrides),
                definition = definitionHash ?? "",
            };
            string json = JsonUtility.ToJson (link);
            if (importer.userData == json) return;
            importer.userData = json;
            AssetDatabase.WriteImportSettingsIfDirty (outputPath);
            // .meta を書いただけでは、アセットのデータベースは書く前の .meta の時刻を覚えたまま。
            // 取り込みワーカーが「Build asset version error ... modification time」を出し続けるので、取り込み直して揃える。
            // 焼いたクリップは保存済みで呼ぶこと（取り込み直すとファイルの中身が読み直される）
            AssetDatabase.ImportAsset (outputPath);
        }

        static string[] OverrideGuids (System.Collections.Generic.IList<AnimationClip> overrides)
        {
            if (overrides == null || overrides.Count == 0) return new string[0];
            string[] result = new string[overrides.Count];
            for (int i = 0; i < overrides.Count; i++) {
                result[i] = overrides[i] != null ? AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (overrides[i])) : "";
            }
            return result;
        }

        /// <summary>一緒に焼いた Override のファイルの中身のハッシュ（つないだもの）。保存していない変更があれば kUnsaved</summary>
        public static string OverridesHash (System.Collections.Generic.IList<AnimationClip> overrides)
        {
            if (overrides == null || overrides.Count == 0) return "";
            string result = "";
            foreach (AnimationClip clip in overrides) {
                if (clip == null) continue;
                if (EditorUtility.IsDirty (clip)) return kUnsaved;
                result += ComputeFileHash (AssetDatabase.GetAssetPath (clip)) + "/";
            }
            return result;
        }

        /// <summary>
        /// ファイルの中身のハッシュ。書き込み時刻と大きさが同じ間は覚えておく（行の状態を何度も見るため）
        /// </summary>
        public static string ComputeFileHash (string path)
        {
            if (string.IsNullOrEmpty (path) || !File.Exists (path)) return "";
            FileInfo info = new FileInfo (path);
            HashCache cache;
            if (hashCache_.TryGetValue (path, out cache) && cache.ticks == info.LastWriteTimeUtc.Ticks && cache.length == info.Length) return cache.hash;

            cache = new HashCache {
                ticks = info.LastWriteTimeUtc.Ticks,
                length = info.Length,
                hash = Hash128.Compute (File.ReadAllBytes (path)).ToString (),
            };
            hashCache_[path] = cache;
            return cache.hash;
        }

        /// <summary>
        /// 焼いた版の状態（無い・最新・古い・書けない）
        /// </summary>
        /// <param name="model">いま編集しているキャラ</param>
        /// <param name="bakeProblem">いま焼けない理由（窓の状態も含む）。焼けるなら null</param>
        public static LayerClipStatus GetStatus (AnimationClip source, GameObject model, string bakeProblem)
        {
            return GetStatus (source, model, bakeProblem, null);
        }

        /// <param name="overrides">一緒に焼く Override のクリップ（S14）</param>
        public static LayerClipStatus GetStatus (AnimationClip source, GameObject model, string bakeProblem, System.Collections.Generic.IList<AnimationClip> overrides)
        {
            return GetStatus (source, model, bakeProblem, overrides, null);
        }

        /// <param name="outputName">焼いて書き出す段（Output）の名前（S21）</param>
        /// <param name="definitionHash">今のリグの定義の、姿勢を変える設定のハッシュ（EditRigDefinition.GetFullBodyHash。無ければ null）</param>
        public static LayerClipStatus GetStatus (AnimationClip source, GameObject model, string bakeProblem, System.Collections.Generic.IList<AnimationClip> overrides, string outputName, string definitionHash = null)
        {
            if (source == null) return new LayerClipStatus (LayerClipState.None, "編集するクリップが無い");
            bool edited = overrides != null && overrides.Count > 0;
            string outputPath = GetOutputPath (source, edited, outputName);
            if (string.IsNullOrEmpty (outputPath)) return new LayerClipStatus (LayerClipState.None, "編集するクリップを保存すると置き場所が決まる");

            string name = Path.GetFileName (outputPath);
            string problem = GetProblem (source, outputPath);
            if (problem != null) return new LayerClipStatus (LayerClipState.Blocked, "焼けない: " + problem);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip> (outputPath) == null) {
                return bakeProblem != null
                    ? new LayerClipStatus (LayerClipState.Blocked, "未作成: " + name + "（焼けない: " + bakeProblem + "）")
                    : new LayerClipStatus (LayerClipState.None, "未作成: " + name + "（Bake で作る）");
            }

            Link link = ReadLink (outputPath);
            string overridesHash = OverridesHash (overrides);
            if (link != null && overridesHash == kUnsaved) {
                return new LayerClipStatus (LayerClipState.Stale, "Override に保存していない変更がある（保存か Bake で焼き直す）");
            }
            if (link != null && (link.overridesHash ?? "") != overridesHash) {
                return new LayerClipStatus (LayerClipState.Stale, "Override が変わった（Bake で焼き直す）");
            }
            string modelGuid = model != null ? AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (model)) : "";
            if (!string.IsNullOrEmpty (link.model) && !string.IsNullOrEmpty (modelGuid) && link.model != modelGuid) {
                GameObject baked = AssetDatabase.LoadAssetAtPath<GameObject> (AssetDatabase.GUIDToAssetPath (link.model));
                return new LayerClipStatus (LayerClipState.Stale, "別のキャラ（" + (baked != null ? baked.name : "不明") + "）で焼いた");
            }
            if ((link.definition ?? "") != (definitionHash ?? "")) {
                return new LayerClipStatus (LayerClipState.Stale, "リグの定義（全身 IK の設定）が変わった（Bake で焼き直す）");
            }
            if (EditorUtility.IsDirty (source)) {
                return new LayerClipStatus (LayerClipState.Stale, "保存していない変更がある（保存か Bake で焼き直す）");
            }
            if (string.IsNullOrEmpty (link.sourceHash)) {
                return new LayerClipStatus (LayerClipState.Stale, "いつの内容で焼いたか分からない（Bake で焼き直す）");
            }
            if (link.sourceHash == kUnsaved) {
                return new LayerClipStatus (LayerClipState.Stale, "保存前の内容で焼いた（保存か Bake で焼き直す）");
            }
            if (link.sourceHash != ComputeFileHash (AssetDatabase.GetAssetPath (source))) {
                return new LayerClipStatus (LayerClipState.Stale, "焼いた後に編集するクリップが変わった（Bake で焼き直す）");
            }
            return new LayerClipStatus (LayerClipState.Ready, "最新");
        }

        /// <summary>
        /// 焼いたクリップから、元の編集用クリップを引く（無ければ null）
        /// </summary>
        public static AnimationClip FindSource (AnimationClip output)
        {
            string path = output != null ? AssetDatabase.GetAssetPath (output) : null;
            Link link = string.IsNullOrEmpty (path) ? null : ReadLink (path);
            return link != null ? AssetDatabase.LoadAssetAtPath<AnimationClip> (AssetDatabase.GUIDToAssetPath (link.source)) : null;
        }

        /// <summary>
        /// ループとルートの移動の扱い（Bake Into Pose・Based Upon）の設定を写す。
        /// 高さと向きの補正（level・orientationOffsetY）と左右反転は写さない。読み込んだ姿勢にもう入っているので、写すと二重に効く
        /// </summary>
        public static void CopyClipSettings (AnimationClip from, AnimationClip to)
        {
            if (from == null || to == null) return;
            AnimationClipSettings source = AnimationUtility.GetAnimationClipSettings (from);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings (to);
            settings.loopTime = source.loopTime;
            settings.loopBlend = source.loopBlend;
            settings.cycleOffset = source.cycleOffset;
            settings.loopBlendOrientation = source.loopBlendOrientation;
            settings.loopBlendPositionY = source.loopBlendPositionY;
            settings.loopBlendPositionXZ = source.loopBlendPositionXZ;
            settings.keepOriginalOrientation = source.keepOriginalOrientation;
            settings.keepOriginalPositionY = source.keepOriginalPositionY;
            settings.keepOriginalPositionXZ = source.keepOriginalPositionXZ;
            settings.heightFromFeet = source.heightFromFeet;
            AnimationUtility.SetAnimationClipSettings (to, settings);
        }

        /// <summary>
        /// 焼いて出力先へ書く。出力先のクリップが既にあればそのまま中身を置き換える（GUID と、ループ・ルートの扱いなどの設定は残る）。
        /// 新しく作るときは、編集用クリップの設定（読み込んだときに元のクリップから写したもの）を引き継ぐ
        /// </summary>
        public static HumanoidBaker.Result BakeToAsset (HumanoidBaker baker, AnimationClip source, GameObject model, out string outputPath)
        {
            return BakeToAsset (baker, source, model, null, out outputPath);
        }

        /// <param name="overrides">一緒に焼く Override のクリップ（S14）。1 つでもあれば `<元>.edited.anim` へ出す</param>
        public static HumanoidBaker.Result BakeToAsset (HumanoidBaker baker, AnimationClip source, GameObject model, System.Collections.Generic.IList<AnimationClip> overrides, out string outputPath)
        {
            return BakeToAsset (baker, source, model, overrides, null, out outputPath);
        }

        /// <param name="outputName">焼いて書き出す段（Output）の名前（S21）。空なら 名前.anim</param>
        public static HumanoidBaker.Result BakeToAsset (HumanoidBaker baker, AnimationClip source, GameObject model, System.Collections.Generic.IList<AnimationClip> overrides, string outputName, out string outputPath, string definitionHash = null)
        {
            bool edited = overrides != null && overrides.Count > 0;
            outputPath = GetOutputPath (source, edited, outputName);
            string problem = baker.error ?? GetProblem (source, outputPath);
            if (problem != null) throw new System.InvalidOperationException (problem);

            AnimationClip output = AssetDatabase.LoadAssetAtPath<AnimationClip> (outputPath);
            bool created = output == null;
            if (created) output = new AnimationClip ();

            HumanoidBaker.Result result = baker.Bake (source, output, model);
            if (created) {
                CopyClipSettings (source, output);
                AssetDatabase.CreateAsset (output, outputPath);
            }
            else {
                EditorUtility.SetDirty (output);
            }
            // 対応は焼いたクリップを保存してから書く（WriteLink は取り込み直すので、先に書くと保存前の中身が読み直されて消える）
            AssetDatabase.SaveAssetIfDirty (output);
            WriteLink (outputPath, source, model, overrides, definitionHash);
            if (baked != null) {
                // 受け側の失敗で焼いた結果を失敗扱いにしない
                try {
                    baked (source, output);
                }
                catch (System.Exception e) {
                    Debug.LogException (e);
                }
            }
            return result;
        }
    }

}
