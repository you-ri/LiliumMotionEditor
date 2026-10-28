using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Kimodo の窓の設定（S77）。マシンと人ごとに違う（kimodo のフォルダ・Hugging Face のキャッシュ）ので、
    /// プロジェクトの UserSettings に置く（リポジトリに入らない）。Hugging Face のトークンはここに持たない（hf auth login がキャッシュに置く）
    /// </summary>
    [FilePath ("UserSettings/LiliumKimodoSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class KimodoSettings : ScriptableSingleton<KimodoSettings>
    {
        public static readonly string[] kModes = { "fullbody", "ends", "root" };
        /// <summary>動かし方: Kimodo を入れた Python の環境で動かす（既定）か、Docker のコンテナで動かすか</summary>
        public const string kPython = "python";
        public const string kDocker = "docker";

        public string runner = kPython;
        /// <summary>Kimodo を入れた Python の環境の python（Windows なら .venv\Scripts\python.exe）</summary>
        public string pythonPath = "";
        /// <summary>kimodo を git clone したフォルダ（中に kimodo/ がある。Docker で動かすとき）</summary>
        public string kimodoFolder = "";
        public string image = "kimodo:1.0";
        /// <summary>Hugging Face のキャッシュのフォルダ（空ならユーザーのフォルダの .cache/huggingface）</summary>
        public string huggingFaceCache = "";
        /// <summary>SOMA の外部の骨格のアセットの GUID（KimodoSkeleton で作ったもの）</summary>
        public string skeletonGuid = "";
        /// <summary>最後に書いた動きの説明。説明をまだ持っていないクリップは、これから始める（説明はクリップごと。KimodoClipSettings）</summary>
        public string prompt = "a person walks forward at a normal pace";
        public string mode = "fullbody";
        /// <summary>
        /// 結果の使い方（KimodoJob.kBetween: キーの間の補間（既定）/ kOverride: Override として重ねる。S83）。
        /// 窓のボタンは「生成」（キーの間を作る）なので、押したら補間になるのを既定にする（Override だと ✏ を移さないと見えず、補間になっていないように見えた）
        /// </summary>
        public string target = KimodoJob.kBetween;
        /// <summary>
        /// キーの間の補間として使うとき、キーが変わるたびに作り直すか（S85）。既定は入（10-25 ユーザー指示。
        /// 切だと、キーを編集しても作られず、トグルに気づかないと動かないように見えた）。1 回で GPU を 30 秒〜2 分使うので、パネルで切れる
        /// </summary>
        public bool auto = true;

        public ExternalSkeleton skeleton
        {
            get {
                string path = string.IsNullOrEmpty (skeletonGuid) ? null : AssetDatabase.GUIDToAssetPath (skeletonGuid);
                return string.IsNullOrEmpty (path) ? null : AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (path);
            }
            set {
                skeletonGuid = value != null ? AssetDatabase.AssetPathToGUID (AssetDatabase.GetAssetPath (value)) : "";
            }
        }

        public string huggingFaceFolder
        {
            get {
                if (!string.IsNullOrEmpty (huggingFaceCache)) return huggingFaceCache;
                return Path.Combine (System.Environment.GetFolderPath (System.Environment.SpecialFolder.UserProfile), ".cache", "huggingface");
            }
        }

        public void Save ()
        {
            Save (true);
        }
    }

    /// <summary>
    /// クリップごとの動きの説明とシード。クリップの .meta に持つ（ClipExtraData）ので、クリップと一緒にリポジトリに入り、ほかの PC でも同じ動きを作れる
    /// </summary>
    [System.Serializable]
    public sealed class KimodoClipSettings
    {
        const string kName = "kimodo";

        public string prompt;
        /// <summary>
        /// 乱数の種を決めて kimodo_gen に --seed で渡すか。既定は切（毎回違う動きになる）。同じ説明・キー・合わせる所なら同じ動きを作り直せる
        /// （cuDNN の計算順でごくわずかに違うことはある）
        /// </summary>
        public bool useSeed;
        public int seed;

        /// <summary>kimodo_gen に渡す種（使わないときは -1。NumPy の種は負にできないので、負の値は 0 にする）</summary>
        public int seedToPass
        {
            get { return useSeed ? Mathf.Max (0, seed) : -1; }
        }

        /// <summary>クリップの設定（まだ無ければ、最後に書いた説明から始める）</summary>
        public static KimodoClipSettings Load (AnimationClip clip)
        {
            KimodoClipSettings settings = clip != null ? ClipExtraData.Load<KimodoClipSettings> (clip, kName) : null;
            return settings ?? new KimodoClipSettings { prompt = KimodoSettings.instance.prompt };
        }

        public void Save (AnimationClip clip)
        {
            if (clip != null) ClipExtraData.Save (clip, kName, this);
            KimodoSettings settings = KimodoSettings.instance;
            if (settings.prompt == prompt) return;
            settings.prompt = prompt;
            settings.Save ();
        }
    }

}
