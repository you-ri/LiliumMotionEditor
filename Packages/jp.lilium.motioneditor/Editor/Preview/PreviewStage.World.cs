using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 世界の部品を作る。カメラとライト・床とグリッド・材質、キャラの複製と、プレビューで動かさないコンポーネントを剥がす（StripPolicy）
    /// </summary>
    public sealed partial class PreviewStage
    {
        /// <summary>
        /// プレビューシーンに GameObject を作る。PreviewRenderUtility が自分のカメラを作るのと同じく、隠して作ってから移すので、
        /// 開いているシーンの Hierarchy には出ない
        /// </summary>
        GameObject CreateGameObject (string name)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags (name, HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene (go, scene_);
            go.hideFlags = HideFlags.DontSave;
            return go;
        }

        GameObject CreateRoot (string name)
        {
            GameObject go = CreateGameObject (name);
            roots_.Add (go);
            return go;
        }

        void SetupCameraAndLights ()
        {
            Camera camera = renderUtility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = kBackgroundColor;
            renderUtility.ambientColor = kAmbientColor;

            Light[] lights = renderUtility.lights;
            lights[0].intensity = 1.1f;
            lights[0].color = Color.white;
            lights[1].intensity = 0.5f;
            lights[1].color = new Color (0.75f, 0.8f, 1f);
        }

        GameObject CreateFloor ()
        {
            Shader shader = Shader.Find ("Hidden/Internal-Colored");

            GameObject floor = CreateRoot ("Floor");
            AddMesh (floor, CreateFloorMesh (), CreateMaterial (shader, true));

            GameObject grid = CreateGameObject ("Grid");
            grid.transform.SetParent (floor.transform, false);
            // 床と重なってちらつかないように少し浮かせる
            grid.transform.localPosition = new Vector3 (0, 0.001f, 0);
            AddMesh (grid, CreateGridMesh (), CreateMaterial (shader, false));
            return floor;
        }

        Material CreateMaterial (Shader shader, bool opaque)
        {
            Material material = new Material (shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetInt ("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt ("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt ("_Cull", (int)CullMode.Off);
            material.SetInt ("_ZWrite", opaque ? 1 : 0);
            material.SetInt ("_ZTest", (int)CompareFunction.LessEqual);
            material.renderQueue = opaque ? (int)RenderQueue.Geometry : (int)RenderQueue.Transparent;
            assets_.Add (material);
            return material;
        }

        void AddMesh (GameObject go, Mesh mesh, Material material)
        {
            assets_.Add (mesh);
            go.AddComponent<MeshFilter> ().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer> ();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static Mesh CreateFloorMesh ()
        {
            float e = kFloorExtent;
            Mesh mesh = new Mesh { name = "MktPreviewFloor", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices (new List<Vector3> { new Vector3 (-e, 0, -e), new Vector3 (-e, 0, e), new Vector3 (e, 0, e), new Vector3 (e, 0, -e) });
            mesh.SetColors (Enumerable.Repeat (kFloorColor, 4).ToList ());
            mesh.SetTriangles (new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        /// <summary>
        /// 0.5m 間隔の線（1m ごとに濃く）。原点を通る線は X 軸を赤、Z 軸を青にする
        /// </summary>
        static Mesh CreateGridMesh ()
        {
            List<Vector3> vertices = new List<Vector3> ();
            List<Color> colors = new List<Color> ();
            int count = Mathf.RoundToInt (kGridExtent / kGridStep);
            for (int i = -count; i <= count; i++) {
                float p = i * kGridStep;
                Color color = (i % 2 == 0) ? kGridMajorColor : kGridMinorColor;
                AddLine (vertices, colors, new Vector3 (p, 0, -kGridExtent), new Vector3 (p, 0, kGridExtent), i == 0 ? kGridZAxisColor : color);
                AddLine (vertices, colors, new Vector3 (-kGridExtent, 0, p), new Vector3 (kGridExtent, 0, p), i == 0 ? kGridXAxisColor : color);
            }

            Mesh mesh = new Mesh { name = "MktPreviewGrid", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices (vertices);
            mesh.SetColors (colors);
            mesh.SetIndices (Enumerable.Range (0, vertices.Count).ToArray (), MeshTopology.Lines, 0);
            return mesh;
        }

        static void AddLine (List<Vector3> vertices, List<Color> colors, Vector3 start, Vector3 end, Color color)
        {
            vertices.Add (start);
            vertices.Add (end);
            colors.Add (color);
            colors.Add (color);
        }

        void CreateModel (GameObject prefab)
        {
            GameObject holder = CreateRoot ("Model");
            // 開いているシーンのキャラを編集するときは、その親と同じ場所に置く（編集用の体は作るときに表示モデルの置き場所を写す）
            if (placement_ != null) {
                holder.transform.SetPositionAndRotation (placement_.position, placement_.rotation);
                holder.transform.localScale = placement_.lossyScale;
            }
            // 親を無効にしてから複製する。ゲーム用コンポーネントの Awake / OnEnable を走らせずに外せる
            holder.SetActive (false);
            model = Object.Instantiate (prefab, holder.transform, false);
            model.name = prefab.name;
            StripComponents (model, StripPolicy.EditTarget);
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> (true)) {
                // 骨を動かしても枠が追従しないので、カメラの外と判定されて消えないようにする
                skin.updateWhenOffscreen = true;
                // 描くたびに骨の行列を取り直す。Unity が裏にある（ほかのアプリに焦点がある）間はエディタのフレームが間引かれ、
                // スキンの行列が更新されないまま描かれる。骨は動いているのに絵のキャラだけ止まり、点やハンドルとずれる（10-10 実測）
                skin.forceMatrixRecalculationPerRender = true;
            }
            holder.SetActive (true);

            animator = model.GetComponentsInChildren<Animator> (true).FirstOrDefault (a => a.avatar != null && a.avatar.isHuman);
            if (animator == null) {
                error = Tr ("PREVIEW_STAGE_NO_HUMANOID_ANIMATOR", prefab.name);
                return;
            }

            // 基準姿勢は prefab の初期姿勢（T ポーズ）1 つ。表示のリセットと FK の差分の基準は同じ値を使う
            CaptureRestPose ();
            editingRig = EditingRig.Create (definition_, animator, CreateGameObject, RigProbe.Describe (model));
            editingRigSolver = new EditingRigSolver (editingRig);
            // 全身 IK（S25）。点が 1 つでも置けたときだけ
            if (editingRig.binding.points.Count > 0) {
                fullBody = new FullBodyRig (editingRig, definition_);
            }
            clipSampler_ = new ClipSampler (editingRig.animator);
            genericSampler_ = new ClipSampler (editingRig.animator);
            // コントロールの初期値は基準姿勢から取る（FK の差分は 0）
            editingRigSolver.Capture ();
            CaptureControlRest ();
            foreach (string problem in editingRig.errors) {
                Debug.LogWarning ("MotionEditor: " + prefab.name + ": " + problem);
            }
        }

        void CaptureRestPose ()
        {
            poseTransforms_ = animator.GetComponentsInChildren<Transform> (true);
            restPositions_ = poseTransforms_.Select (t => t.localPosition).ToArray ();
            restRotations_ = poseTransforms_.Select (t => t.localRotation).ToArray ();
            restScales_ = poseTransforms_.Select (t => t.localScale).ToArray ();
        }

        /// <summary>
        /// 残すもの以外のコンポーネントを外す（StripPolicy）。RequireComponent で頼られているものは、頼っている側を先に外す
        /// </summary>
        static void StripComponents (GameObject root, StripPolicy policy)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform> (true)) {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript (t.gameObject);
            }

            List<Component> pending = root.GetComponentsInChildren<Component> (true).Where (c => !IsKeptComponent (c, policy)).ToList ();
            bool removed = true;
            while (pending.Count > 0 && removed) {
                removed = false;
                for (int i = 0; i < pending.Count; i++) {
                    Component component = pending[i];
                    // 他を外したときに一緒に消えたもの
                    if (component == null) {
                        pending.RemoveAt (i--);
                        removed = true;
                        continue;
                    }
                    if (IsRequiredByOthers (component)) continue;

                    Object.DestroyImmediate (component);
                    pending.RemoveAt (i--);
                    removed = true;
                }
            }

            // 外せなかったもの（依存が循環しているなど）と、段として残したものは止めておく。
            // 残した段はこちらから呼ぶときだけ動かす（edit-mode で勝手に走らせない）
            foreach (Component component in root.GetComponentsInChildren<Component> (true)) {
                if (IsVisualComponent (component)) continue;
                // 文脈の Director は時計を窓から回すので止めない（S15a）
                if (policy == StripPolicy.Context && IsTimelineComponent (component)) continue;
                Behaviour behaviour = component as Behaviour;
                if (behaviour != null) behaviour.enabled = false;
            }
        }

        /// <summary>
        /// 剥がさずに残すものを外から足す受け口。ゲーム固有の設定（左右の対応表など）を読む必要があるコンポーネントを残す。
        /// 残したものは止めてある（こちらから呼ぶときだけ動く）ので、置いても edit-mode で走らない
        /// </summary>
        public static void KeepComponent (System.Func<Component, bool> predicate)
        {
            keepPredicates_.Register (predicate);
        }

        static readonly HookList<System.Func<Component, bool>> keepPredicates_ = new HookList<System.Func<Component, bool>> ();

        static bool IsKeptByHook (Component component)
        {
            foreach (System.Func<Component, bool> predicate in keepPredicates_.items) {
                try {
                    if (predicate (component)) return true;
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            return false;
        }

        /// <summary>
        /// 外さずに残すもの。見た目（メッシュ・Animator）に加えて、**段として出すもの**（Rigging の部品・[PoseLayer]）。
        /// 段のコンポーネントを外すと「キャラに付いていない」ことになり、その段がスタックから消えてしまう
        /// </summary>
        static bool IsKeptComponent (Component component, StripPolicy policy)
        {
            if (IsVisualComponent (component)) return true;
            if (IsKeptByHook (component)) return true;
            switch (policy) {
                case StripPolicy.Context:
                    return IsTimelineComponent (component) || component is ParticleSystem || component is ParticleSystemRenderer;
                default:
                    return RigProbe.IsRigComponent (component) || LayerTypes.IsPoseLayer (component);
            }
        }

        /// <summary>
        /// Director と、Timeline の仕組みに属するもの（名前空間で見る。Timeline パッケージへは依存しない）
        /// </summary>
        static bool IsTimelineComponent (Component component)
        {
            if (component is UnityEngine.Playables.PlayableDirector) return true;
            string ns = component.GetType ().Namespace;
            return ns != null && (ns == "UnityEngine.Timeline" || ns.StartsWith ("UnityEngine.Timeline."));
        }

        static bool IsVisualComponent (Component component)
        {
            return component == null || kVisualComponents.Contains (component.GetType ());
        }

        static bool IsRequiredByOthers (Component component)
        {
            foreach (Component other in component.GetComponents<Component> ()) {
                if (other == null || other == component) continue;
                foreach (RequireComponent require in other.GetType ().GetCustomAttributes (typeof (RequireComponent), true)) {
                    if (IsType (require.m_Type0, component) || IsType (require.m_Type1, component) || IsType (require.m_Type2, component)) return true;
                }
            }
            return false;
        }

        static bool IsType (System.Type type, Component component)
        {
            return type != null && type.IsInstanceOfType (component);
        }
    }

}
