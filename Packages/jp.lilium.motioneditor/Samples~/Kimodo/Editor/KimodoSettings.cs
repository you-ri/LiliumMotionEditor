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
        public string prompt = "a person walks forward at a normal pace";
        public string mode = "fullbody";
        /// <summary>結果の使い方（KimodoJob.kOverride: Override として重ねる / kBetween: キーの間の補間。S83）</summary>
        public string target = KimodoJob.kOverride;

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

}
