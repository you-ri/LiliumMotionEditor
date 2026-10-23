using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// Kimodo の骨格（SOMA。77 関節と包みの Root）の外部の骨格の定義を、Kimodo が出した BVH から作る。
    /// 骨の長さなどの Kimodo のデータは同梱せず、関節の名前と Humanoid の骨の対応だけを持つ。
    /// BVH は kimodo_gen --bvh --bvh_standard_tpose で出したもの（基準姿勢が T ポーズ。既定の基準姿勢では Avatar が崩れる）
    /// </summary>
    public static class KimodoSkeleton
    {
        public static readonly Dictionary<string, HumanBodyBones> kSomaBones = new Dictionary<string, HumanBodyBones> {
            { "Hips", HumanBodyBones.Hips },
            { "Spine1", HumanBodyBones.Spine },
            { "Spine2", HumanBodyBones.Chest },
            { "Chest", HumanBodyBones.UpperChest },
            { "Neck1", HumanBodyBones.Neck },
            { "Head", HumanBodyBones.Head },
            { "LeftShoulder", HumanBodyBones.LeftShoulder },
            { "LeftArm", HumanBodyBones.LeftUpperArm },
            { "LeftForeArm", HumanBodyBones.LeftLowerArm },
            { "LeftHand", HumanBodyBones.LeftHand },
            { "RightShoulder", HumanBodyBones.RightShoulder },
            { "RightArm", HumanBodyBones.RightUpperArm },
            { "RightForeArm", HumanBodyBones.RightLowerArm },
            { "RightHand", HumanBodyBones.RightHand },
            { "LeftLeg", HumanBodyBones.LeftUpperLeg },
            { "LeftShin", HumanBodyBones.LeftLowerLeg },
            { "LeftFoot", HumanBodyBones.LeftFoot },
            { "LeftToeBase", HumanBodyBones.LeftToes },
            { "RightLeg", HumanBodyBones.RightUpperLeg },
            { "RightShin", HumanBodyBones.RightLowerLeg },
            { "RightFoot", HumanBodyBones.RightFoot },
            { "RightToeBase", HumanBodyBones.RightToes },
        };

        /// <summary>
        /// BVH（ファイルのパス）から SOMA の外部の骨格の定義を作り、assetPath（Assets/....asset）に保存する。作れなければ理由を返す
        /// </summary>
        public static string Create (string bvhPath, string assetPath)
        {
            Bvh bvh = Bvh.Parse (File.ReadAllText (bvhPath), out string error);
            if (bvh == null) return error;
            ExternalSkeleton skeleton = ExternalSkeleton.FromBvh (bvh);
            skeleton.MapHumanBones (kSomaBones);
            // 作れるか先に確かめる（何も保存しない）
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out error);
            if (instance == null) {
                Object.DestroyImmediate (skeleton);
                return error;
            }
            instance.Dispose ();
            string folder = Path.GetDirectoryName (assetPath);
            if (!string.IsNullOrEmpty (folder)) Directory.CreateDirectory (folder);
            AssetDatabase.CreateAsset (skeleton, assetPath);
            AssetDatabase.SaveAssets ();
            return null;
        }

        [MenuItem ("Tools/Lilium Motion Editor/Kimodo/Create SOMA Skeleton From BVH...")]
        static void CreateFromMenu ()
        {
            string bvhPath = EditorUtility.OpenFilePanel ("Kimodo BVH (made with --bvh_standard_tpose)", "", "bvh");
            if (string.IsNullOrEmpty (bvhPath)) return;
            string assetPath = EditorUtility.SaveFilePanelInProject ("SOMA Skeleton", "Kimodo SOMA Skeleton", "asset", "Where to save the external skeleton");
            if (string.IsNullOrEmpty (assetPath)) return;
            string error = Create (bvhPath, assetPath);
            if (error != null) EditorUtility.DisplayDialog ("Kimodo", error, "OK");
            else Selection.activeObject = AssetDatabase.LoadAssetAtPath<ExternalSkeleton> (assetPath);
        }
    }

}
