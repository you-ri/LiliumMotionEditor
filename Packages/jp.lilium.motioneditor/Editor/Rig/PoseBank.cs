using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// PoseBank（AnimBank の姿勢版）。1 つの姿勢を 1 つのクリップ（*.pose.anim。編集用リグの値を 0 秒に 1 キーずつ）として、
    /// キャラごとのフォルダに置く。フォルダの決め方は AnimBank と同じで、既定は「キャラの prefab のフォルダ/Poses」。
    /// 貼るときは値の一部（選んでいる所のカーブ）だけを選べる（手の形だけを貼る、など）
    /// </summary>
    public static class PoseBank
    {
        public const string kDefaultFolderName = "Poses";
        /// <summary>ファイル名の拡張子の前に付ける印（Fist.pose.anim の「.pose」）</summary>
        public const string kSuffix = ".pose";
        const string kPrefsKey = "MikotoMotionEditor.PoseBank.";

        public struct Entry
        {
            public string path;
            /// <summary>表示名（.pose を除いたもの）</summary>
            public string name;
            /// <summary>フォルダから見たサブフォルダ（直下なら空）</summary>
            public string group;
        }

        /// <summary>
        /// 既定の置き場所を決める口（AnimBank.defaultFolder と同じ使い方）。null を返すとパッケージの既定（prefab のフォルダ/Poses）
        /// </summary>
        public static System.Func<GameObject, string> defaultFolder;

        /// <summary>このキャラの姿勢を作る場所（フォルダの並びの先頭）。prefab が無ければ null</summary>
        public static string GetFolder (GameObject prefab)
        {
            List<string> folders = GetFolders (prefab);
            return folders.Count > 0 ? folders[0] : null;
        }

        /// <summary>このキャラの一覧に並べるフォルダ（先頭が作る場所。共通の姿勢のフォルダも並ぶ）</summary>
        public static List<string> GetFolders (GameObject prefab)
        {
            return SettingsLookup.GetPoseBankFolders (prefab);
        }

        /// <summary>作る場所を選び直す（null で既定へ戻す）。キャラの設定があればそこへ書き、無ければこの PC にだけ覚える</summary>
        public static void SetFolder (GameObject prefab, string folder)
        {
            CharacterSettings settings = SettingsLookup.Find (prefab);
            if (settings != null) {
                AnimBank.SetFirstFolder (settings, ref settings.overridePoseBankFolders, settings.poseBankFolders, ProjectSettings.instance.poseBankFolders, folder);
                return;
            }
            AnimBank.SetFolder (prefab, kPrefsKey, folder);
        }

        /// <summary>設定が無いときの既定（この PC で選んだもの → 拡張点 → prefab のフォルダ/Poses）</summary>
        internal static string GetDefaultFolder (GameObject prefab)
        {
            return AnimBank.GetFolder (prefab, kPrefsKey, defaultFolder, kDefaultFolderName);
        }

        /// <summary>この PC で選んだフォルダ（EditorPrefs。無ければ null）</summary>
        internal static string GetSavedFolder (GameObject prefab)
        {
            return AnimBank.GetSavedFolder (prefab, kPrefsKey);
        }

        public static bool IsPosePath (string path)
        {
            return !string.IsNullOrEmpty (path) && path.EndsWith (kSuffix + EditingClip.kExtension, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// フォルダの下（サブフォルダも）の姿勢。組（サブフォルダ）→ 名前の順
        /// </summary>
        public static List<Entry> List (string folder)
        {
            List<Entry> result = new List<Entry> ();
            if (string.IsNullOrEmpty (folder) || !AssetDatabase.IsValidFolder (folder)) return result;
            foreach (string guid in AssetDatabase.FindAssets ("t:AnimationClip", new[] { folder })) {
                string path = AssetDatabase.GUIDToAssetPath (guid);
                if (!IsPosePath (path)) continue;
                string directory = Path.GetDirectoryName (path).Replace ('\\', '/');
                result.Add (new Entry {
                    path = path,
                    name = BaseName (path),
                    group = directory.Length > folder.Length ? directory.Substring (folder.Length + 1) : "",
                });
            }
            result.Sort ((a, b) => {
                int group = string.CompareOrdinal (a.group, b.group);
                return group != 0 ? group : EditorUtility.NaturalCompare (a.name, b.name);
            });
            return result;
        }

        public static string BaseName (string path)
        {
            string name = Path.GetFileNameWithoutExtension (path);
            return name.EndsWith (kSuffix, System.StringComparison.OrdinalIgnoreCase) ? name.Substring (0, name.Length - kSuffix.Length) : name;
        }

        public static string MakePath (string folder, string baseName)
        {
            return folder + "/" + baseName + kSuffix + EditingClip.kExtension;
        }

        static bool Exists (string folder, string baseName)
        {
            return AssetDatabase.LoadMainAssetAtPath (MakePath (folder, baseName)) != null || File.Exists (MakePath (folder, baseName));
        }

        /// <summary>
        /// 値を新しい姿勢として保存する。名前が使われていれば番号を上げる。フォルダが無ければ作る
        /// </summary>
        public static AnimationClip Create (string folder, IReadOnlyDictionary<EditorCurveBinding, float> values, string baseName = "New Pose")
        {
            AnimBank.EnsureFolder (folder);
            string name = AnimBank.NextName (baseName, n => Exists (folder, n));
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            Write (clip, values);
            AssetDatabase.CreateAsset (clip, MakePath (folder, name));
            return clip;
        }

        /// <summary>
        /// 姿勢を今の値で置き換える（前の値は残さない）
        /// </summary>
        public static void Overwrite (AnimationClip pose, IReadOnlyDictionary<EditorCurveBinding, float> values)
        {
            if (pose == null) return;
            Undo.RecordObject (pose, "Update Pose");
            Write (pose, values);
            AssetDatabase.SaveAssetIfDirty (pose);
        }

        static void Write (AnimationClip clip, IReadOnlyDictionary<EditorCurveBinding, float> values)
        {
            clip.ClearCurves ();
            EditorCurveBinding[] bindings = new EditorCurveBinding[values.Count];
            AnimationCurve[] curves = new AnimationCurve[values.Count];
            int i = 0;
            foreach (KeyValuePair<EditorCurveBinding, float> pair in values) {
                bindings[i] = pair.Key;
                curves[i] = new AnimationCurve (new Keyframe (0, pair.Value));
                i++;
            }
            AnimationUtility.SetEditorCurves (clip, bindings, curves);
        }

        /// <summary>
        /// 姿勢の値（0 秒の値）。filter で残すカーブを選ぶ（null なら全部）
        /// </summary>
        public static Dictionary<EditorCurveBinding, float> Read (AnimationClip pose, System.Predicate<EditorCurveBinding> filter = null)
        {
            Dictionary<EditorCurveBinding, float> result = new Dictionary<EditorCurveBinding, float> ();
            if (pose == null) return result;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (pose)) {
                if (filter != null && !filter (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (pose, binding);
                if (curve == null || curve.length == 0) continue;
                result[binding] = curve.Evaluate (0);
            }
            return result;
        }

        /// <summary>
        /// Humanoid の姿勢か（muscle の値を持つ。手の形だけ、など一部でもよい）。編集用リグの値の姿勢と違い、体型の違うキャラへそのまま貼れる
        /// </summary>
        public static bool IsHumanoidPose (AnimationClip pose)
        {
            return pose != null && pose.humanMotion;
        }

        static Dictionary<string, int> muscleByAttribute_;

        /// <summary>
        /// Humanoid の姿勢の muscle の値（0 秒の値。HumanTrait.MuscleName の番号で）。
        /// クリップの指の名前は MuscleName と書き方が違う（"LeftHand.Index.1 Stretched" と "Left Index 1 Stretched"）ので両方で引く
        /// </summary>
        public static Dictionary<int, float> ReadMuscles (AnimationClip pose)
        {
            if (muscleByAttribute_ == null) {
                muscleByAttribute_ = new Dictionary<string, int> ();
                string[] names = HumanTrait.MuscleName;
                string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
                for (int i = 0; i < names.Length; i++) {
                    string name = names[i];
                    muscleByAttribute_[name] = i;
                    foreach (string side in new[] { "Left", "Right" }) {
                        foreach (string finger in fingers) {
                            string prefix = side + " " + finger + " ";
                            if (!name.StartsWith (prefix)) continue;
                            string rest = name.Substring (prefix.Length);
                            // "1 Stretched" → ".1 Stretched"、"Spread" → ".Spread"
                            muscleByAttribute_[side + "Hand." + finger + "." + rest] = i;
                        }
                    }
                }
            }
            Dictionary<int, float> result = new Dictionary<int, float> ();
            foreach (KeyValuePair<EditorCurveBinding, float> pair in Read (pose, b => b.path.Length == 0 && b.type == typeof (Animator))) {
                if (muscleByAttribute_.TryGetValue (pair.Key.propertyName, out int index)) result[index] = pair.Value;
            }
            return result;
        }

        /// <summary>
        /// muscle の値を今の姿勢（current。表示モデルの今の Humanoid の姿勢）に重ねて表示モデルへ当て、
        /// その muscle が動かす骨だけを編集用の体へ写して FK の操作値として捉える。
        /// 返すのは値を捉えた FK のコントロールのパス（＝キーを打つカーブのパス）。writer は表示モデルの Animator の HumanPoseHandler
        /// </summary>
        public static HashSet<string> ApplyMuscles (EditingRig rig, EditingRigSolver solver, HumanPoseHandler writer, HumanPose current, IReadOnlyDictionary<int, float> muscles)
        {
            HumanPose pose = current;
            pose.muscles = (float[])current.muscles.Clone ();
            foreach (KeyValuePair<int, float> pair in muscles) {
                if (pair.Key >= 0 && pair.Key < pose.muscles.Length) pose.muscles[pair.Key] = pair.Value;
            }
            // 姿勢を読んだときと同じく、Animator の GameObject を原点・無回転に置いて当てる
            Transform root = rig.displayAnimator.transform;
            root.GetPositionAndRotation (out Vector3 position, out Quaternion rotation);
            root.SetPositionAndRotation (Vector3.zero, Quaternion.identity);
            try {
                writer.SetHumanPose (ref pose);
            }
            finally {
                root.SetPositionAndRotation (position, rotation);
            }

            HashSet<string> paths = new HashSet<string> ();
            foreach (int muscle in muscles.Keys) {
                int bone = HumanTrait.BoneFromMuscle (muscle);
                if (bone < 0) continue;
                RigBinding.Fk fk = rig.binding.FindFk ((HumanBodyBones)bone);
                if (fk == null || !fk.enabled || paths.Contains (fk.path)) continue;
                Transform editing = rig.GetEditingBone (fk.bone);
                if (editing == null) continue;
                fk.bone.GetLocalPositionAndRotation (out Vector3 p, out Quaternion q);
                editing.SetLocalPositionAndRotation (p, q);
                if (solver.CaptureFk (editing)) paths.Add (fk.path);
            }
            return paths;
        }

        /// <summary>
        /// 名前を変える（.pose は付け直す）。できなければ理由を返す（できたら null）
        /// </summary>
        public static string Rename (AnimationClip pose, string newBaseName)
        {
            string path = AssetDatabase.GetAssetPath (pose);
            if (string.IsNullOrEmpty (path)) return Tr ("POSE_BANK_NOT_AN_ASSET_POSE");
            newBaseName = newBaseName != null ? newBaseName.Trim () : "";
            if (newBaseName.EndsWith (kSuffix, System.StringComparison.OrdinalIgnoreCase)) {
                newBaseName = newBaseName.Substring (0, newBaseName.Length - kSuffix.Length);
            }
            if (newBaseName.Length == 0) return Tr ("ANIM_BANK_NAME_EMPTY");
            if (newBaseName.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0) return Tr ("ANIM_BANK_INVALID_FILE_NAME_CHARS");
            if (newBaseName == BaseName (path)) return null;
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            if (Exists (folder, newBaseName)) return Tr ("POSE_BANK_DUPLICATE_NAME");
            string error = AssetDatabase.RenameAsset (path, newBaseName + kSuffix);
            return string.IsNullOrEmpty (error) ? null : error;
        }

        /// <summary>
        /// ゴミ箱へ移す（OS のゴミ箱から戻せる）
        /// </summary>
        public static bool Delete (AnimationClip pose)
        {
            string path = AssetDatabase.GetAssetPath (pose);
            return !string.IsNullOrEmpty (path) && AssetDatabase.MoveAssetToTrash (path);
        }
    }

}
