using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 姿勢を 4 方向（正面・右・上・右斜め前）から画像（PNG）に書き出す（S39）。画像を読める AI が姿勢を目で確かめるのに使う。
    /// 描く世界（PoseSession を render で作ったもの。窓と同じ PreviewStage）で撮るので、床とグリッドも写る。
    /// 視点はキャラ全体が入るように寄せる（窓の OrbitCamera.Frame と同じ）。向きは体の向き基準なので、キャラの置き方によらない。
    /// 画面の無い Unity（-nographics）では撮れない
    /// </summary>
    public static class PoseCapture
    {
        /// <summary>
        /// 撮る方向（名前・体の向き基準の、カメラから見る向き・見下ろす角度・平行投影か）。
        /// 正面・右・上は平行投影（遠近が付かないので、足が床の線に乗っているか・左右がそろっているかをそのまま比べられる）。
        /// 斜め前は立体の感じを見るので遠近を付ける
        /// </summary>
        static readonly (string name, Vector3 look, float pitch, bool orthographic)[] kViews = {
            ("front", new Vector3 (0, 0, -1), 0, true),
            ("right", new Vector3 (-1, 0, 0), 0, true),
            ("top", new Vector3 (0, 0, 1), 89.9f, true),
            ("quarter", new Vector3 (-1, 0, -1), 20, false),
        };

        /// <summary>撮れない理由（撮れるなら null）</summary>
        public static string GetProblem (PoseSession session)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return Tr ("POSE_CAPTURE_NO_GRAPHICS");
            string character = PoseSession.CharacterProblem (session);
            if (character != null) return character;
            if (session.stage.headless) return Tr ("POSE_CAPTURE_HEADLESS");
            return null;
        }

        /// <summary>
        /// frame の姿勢を 4 方向から撮って folder へ PNG で書く（名前_フレーム_方向.png）。書いたパスを返す。撮れなければ null と理由
        /// </summary>
        /// <param name="session">描く世界の PoseSession（new PoseSession (prefab, definition, true)）</param>
        /// <param name="size">画像の一辺（ピクセル）</param>
        public static List<string> Capture (PoseSession session, int frame, string folder, int size, out string problem)
        {
            problem = GetProblem (session);
            if (problem != null) return null;
            size = Mathf.Clamp (size, 64, 2048);
            Directory.CreateDirectory (folder);

            session.Evaluate (frame);
            PreviewStage stage = session.stage;
            EditingRig rig = stage.editingRig;
            Transform root = rig.root.transform;
            Quaternion toWorld = root.rotation * Quaternion.LookRotation (rig.bodyForward, rig.bodyUp);
            Bounds bounds = stage.GetBounds ();
            // 骨の位置も入れる（メッシュの無いキャラ・メッシュの枠が姿勢に付いてこないキャラでも全身が入るように）
            foreach (Transform bone in rig.editingBones) {
                if (bone != null) bounds.Encapsulate (bone.position);
            }
            string baseName = session.clip != null ? session.clip.name : (stage.sourcePrefab != null ? stage.sourcePrefab.name : "Pose");
            List<string> paths = new List<string> ();
            Camera camera = stage.camera;
            float savedAspect = camera.aspect;
            bool savedOrthographic = camera.orthographic;
            float savedSize = camera.orthographicSize;
            try {
                foreach ((string name, Vector3 look, float pitch, bool orthographic) in kViews) {
                    OrbitCamera view = new OrbitCamera ();
                    view.LookAlong (toWorld * look);
                    view.pitch = pitch;
                    view.Frame (bounds.center, bounds.extents.magnitude * 1.05f, 1);
                    view.Apply (camera);
                    camera.aspect = 1;
                    camera.orthographic = orthographic;
                    // 平行投影は、キャラの範囲を画面の縦横に写した大きさに合わせる（少し余白を付ける）
                    if (orthographic) camera.orthographicSize = ProjectedHalfSize (bounds, view.rotation) * 1.1f;
                    stage.UpdateLights (view.rotation);

                    stage.renderUtility.BeginStaticPreview (new Rect (0, 0, size, size));
                    stage.renderUtility.Render (true, false);
                    Texture2D image = stage.renderUtility.EndStaticPreview ();
                    try {
                        string path = Path.Combine (folder, baseName + "_" + frame + "_" + name + ".png");
                        File.WriteAllBytes (path, image.EncodeToPNG ());
                        paths.Add (Path.GetFullPath (path).Replace ('\\', '/'));
                    }
                    finally {
                        Object.DestroyImmediate (image);
                    }
                }
            }
            finally {
                camera.aspect = savedAspect;
                camera.orthographic = savedOrthographic;
                camera.orthographicSize = savedSize;
            }
            return paths;
        }

        /// <summary>範囲（箱）の角を、カメラの右と上の向きに写したときの、中心からの最大の距離</summary>
        static float ProjectedHalfSize (Bounds bounds, Quaternion rotation)
        {
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            float half = 0.01f;
            for (int i = 0; i < 8; i++) {
                Vector3 corner = new Vector3 ((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
                Vector3 offset = Vector3.Scale (corner, bounds.extents);
                half = Mathf.Max (half, Mathf.Abs (Vector3.Dot (offset, right)), Mathf.Abs (Vector3.Dot (offset, up)));
            }
            return half;
        }
    }

}
