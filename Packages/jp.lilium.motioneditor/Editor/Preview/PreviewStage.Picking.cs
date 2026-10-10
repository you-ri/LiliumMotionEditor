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
    /// 表示域のクリックで骨を選ぶ。見えているメッシュにレイを当て、当たった所を一番動かしている骨を返す
    /// </summary>
    public sealed partial class PreviewStage
    {
        /// <summary>
        /// 見えているメッシュにレイを当てて、当たった場所を一番動かしている骨を返す（当たらなければ null）。
        /// スキンのメッシュは今の姿勢で焼いて（BakeMesh）から三角形と交差させ、当たった面の頂点のウェイトが一番大きい骨を採る。
        /// 骨を持たないメッシュ（武器など）は、その Transform を返す
        /// </summary>
        public Transform PickBone (Ray ray)
        {
            // 押したときは、今の見た目で焼き直す
            pickMeshesValid_ = false;
            return PickBoneInternal (ray);
        }

        /// <summary>
        /// PickBone と同じだが、前に焼いたメッシュを使い回す（マウスを動かすたびに、押したら選ぶ物を探して強調するため。
        /// 毎回焼くと重い）。姿勢を変えたら InvalidatePickMeshes で捨てる
        /// </summary>
        public Transform PickBoneCached (Ray ray)
        {
            return PickBoneInternal (ray);
        }

        /// <summary>使い回している焼いたメッシュを捨てる（姿勢を変えたとき）</summary>
        public void InvalidatePickMeshes ()
        {
            pickMeshesValid_ = false;
        }

        /// <summary>当たりを見るメッシュ（骨を持つメッシュは今の見た目で焼いた頂点。レンダラーの Transform から見た位置）</summary>
        sealed class PickMesh
        {
            public SkinnedMeshRenderer skin;
            public Transform transform;
            public Vector3[] vertices;
            public int[] triangles;
        }

        readonly List<PickMesh> pickMeshes_ = new List<PickMesh> ();
        bool pickMeshesValid_;

        void BuildPickMeshes ()
        {
            pickMeshes_.Clear ();
            pickMeshesValid_ = true;
            if (model == null) return;
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> ()) {
                if (!skin.enabled || skin.sharedMesh == null) continue;
                Mesh baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try {
                    skin.BakeMesh (baked);
                    Vector3[] vertices = baked.vertices;
                    if (vertices.Length == 0) continue;
                    pickMeshes_.Add (new PickMesh { skin = skin, transform = skin.transform, vertices = vertices, triangles = skin.sharedMesh.triangles });
                }
                finally {
                    Object.DestroyImmediate (baked);
                }
            }
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter> ()) {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer> ();
                if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
                pickMeshes_.Add (new PickMesh { transform = filter.transform, vertices = filter.sharedMesh.vertices, triangles = filter.sharedMesh.triangles });
            }
        }

        /// <summary>
        /// 表示モデルの今の見た目を焼いて、描く位置と一緒に result へ足す（Stacker のゴースト）。
        /// メッシュは pool の used 番目から使い回し（足りなければ作る）、使った数を返す。骨を持たないメッシュは元のメッシュをそのまま描く
        /// </summary>
        public int BakeDisplayMeshes (List<Mesh> pool, int used, List<(Mesh, Matrix4x4)> result)
        {
            if (model == null) return used;
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer> ()) {
                if (!skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null) continue;
                if (used >= pool.Count) pool.Add (new Mesh { hideFlags = HideFlags.HideAndDontSave });
                Mesh mesh = pool[used++];
                skin.BakeMesh (mesh);
                // 焼いたメッシュはレンダラーの Transform から見た位置（大きさは焼かない）
                result.Add ((mesh, skin.transform.localToWorldMatrix));
            }
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter> ()) {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer> ();
                if (renderer == null || !renderer.enabled || !filter.gameObject.activeInHierarchy || filter.sharedMesh == null) continue;
                result.Add ((filter.sharedMesh, filter.transform.localToWorldMatrix));
            }
            return used;
        }

        Transform PickBoneInternal (Ray ray)
        {
            if (model == null) return null;
            if (!pickMeshesValid_) BuildPickMeshes ();

            Transform picked = null;
            float distance = float.MaxValue;
            foreach (PickMesh mesh in pickMeshes_) {
                if (mesh.transform == null) continue;
                MeshHit hit;
                if (!RaycastMesh (ray, mesh.transform, mesh.vertices, mesh.triangles, out hit) || hit.distance >= distance) continue;
                // 骨を持つメッシュは、当たった所を一番動かしている骨。持たないメッシュはその Transform
                Transform bone = mesh.skin != null ? FindHeaviestBone (mesh.skin, mesh.triangles, hit) : mesh.transform;
                if (bone == null) continue;
                picked = bone;
                distance = hit.distance;
            }
            return picked;
        }

        struct MeshHit
        {
            public int triangle;
            /// <summary>当たった点の重心座標（頂点 1・2 の重み）</summary>
            public float u;
            public float v;
            public float distance;
        }

        /// <summary>
        /// レイを Transform のローカルへ持っていって、全部の三角形と交差させる。一番手前の当たりを返す
        /// </summary>
        static bool RaycastMesh (Ray ray, Transform transform, Vector3[] vertices, int[] triangles, out MeshHit hit)
        {
            hit = new MeshHit { triangle = -1, distance = float.MaxValue };

            Matrix4x4 toLocal = transform.worldToLocalMatrix;
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            Vector3 origin = toLocal.MultiplyPoint3x4 (ray.origin);
            Vector3 direction = toLocal.MultiplyVector (ray.direction);

            for (int i = 0; i + 2 < triangles.Length; i += 3) {
                float t, u, v;
                if (!IntersectTriangle (origin, direction, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]], out t, out u, out v)) continue;

                // 拡縮があるとローカルの t は距離にならないので、ワールドへ戻して測る
                float worldDistance = Vector3.Distance (ray.origin, toWorld.MultiplyPoint3x4 (origin + direction * t));
                if (worldDistance >= hit.distance) continue;
                hit = new MeshHit { triangle = i, u = u, v = v, distance = worldDistance };
            }
            return hit.triangle >= 0;
        }

        /// <summary>
        /// Möller–Trumbore。裏面にも当てる（見えている面が裏向きのモデルがあるため）
        /// </summary>
        static bool IntersectTriangle (Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
        {
            t = u = v = 0;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 p = Vector3.Cross (direction, ac);
            float determinant = Vector3.Dot (ab, p);
            if (Mathf.Abs (determinant) < 1e-9f) return false;

            float inverse = 1 / determinant;
            Vector3 toA = origin - a;
            u = Vector3.Dot (toA, p) * inverse;
            if (u < 0 || u > 1) return false;

            Vector3 q = Vector3.Cross (toA, ab);
            v = Vector3.Dot (direction, q) * inverse;
            if (v < 0 || u + v > 1) return false;

            t = Vector3.Dot (ac, q) * inverse;
            return t > 0;
        }

        /// <summary>
        /// 当たった面の 3 頂点のウェイトを重心座標で混ぜて、一番大きい骨を返す
        /// </summary>
        static Transform FindHeaviestBone (SkinnedMeshRenderer skin, int[] triangles, MeshHit hit)
        {
            BoneWeight[] weights = skin.sharedMesh.boneWeights;
            Transform[] bones = skin.bones;
            if (weights.Length == 0 || bones.Length == 0) return null;

            Dictionary<int, float> total = new Dictionary<int, float> ();
            float[] coefficients = { 1 - hit.u - hit.v, hit.u, hit.v };
            for (int i = 0; i < 3; i++) {
                int vertex = triangles[hit.triangle + i];
                if (vertex >= weights.Length) continue;
                BoneWeight weight = weights[vertex];
                Add (total, weight.boneIndex0, weight.weight0 * coefficients[i]);
                Add (total, weight.boneIndex1, weight.weight1 * coefficients[i]);
                Add (total, weight.boneIndex2, weight.weight2 * coefficients[i]);
                Add (total, weight.boneIndex3, weight.weight3 * coefficients[i]);
            }

            int best = -1;
            float bestWeight = 0;
            foreach (KeyValuePair<int, float> pair in total) {
                if (pair.Value <= bestWeight) continue;
                best = pair.Key;
                bestWeight = pair.Value;
            }
            return best >= 0 && best < bones.Length ? bones[best] : null;
        }

        static void Add (Dictionary<int, float> total, int index, float weight)
        {
            if (weight <= 0) return;
            float current;
            total.TryGetValue (index, out current);
            total[index] = current + weight;
        }
    }

}
