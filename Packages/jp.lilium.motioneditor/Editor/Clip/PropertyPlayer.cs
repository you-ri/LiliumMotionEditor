using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 任意のプロパティ（S6）。表示モデルの部品の数値（表示の ON/OFF・ブレンドシェイプ・フィールドの float / int / bool）を列挙し、
    /// 編集用クリップの Controls/Props/... のカーブをその時刻の値で表示モデルへ入れる。
    ///
    /// Humanoid の Animator を持つモデルに AnimationClip.SampleAnimation で当てると、数値だけのクリップでも骨が既定の姿勢に崩れる
    /// （2026-09-18 実測: 腰が 136° 回る）ので、値は自分で入れる。材質の値と参照（材質・メッシュの差し替え）は後回し。
    /// 入れたことのある値は元の値を覚えておき、カーブが無くなったら戻す
    /// </summary>
    public sealed class PropertyPlayer
    {
        public struct Candidate
        {
            /// <summary>表示モデルから見たバインディング（パスは表示モデルの Animator から）</summary>
            public EditorCurveBinding binding;
            public string label;
        }

        readonly Dictionary<EditorCurveBinding, float> originals_ = new Dictionary<EditorCurveBinding, float> ();

        /// <summary>
        /// 表示モデルの、キーにできる数値。Transform と Animator、材質、参照、描画の内部値（AABB など）は除く
        /// </summary>
        public static List<Candidate> ListCandidates (GameObject root)
        {
            List<Candidate> result = new List<Candidate> ();
            if (root == null) return result;
            foreach (Transform t in root.GetComponentsInChildren<Transform> (true)) {
                foreach (EditorCurveBinding binding in AnimationUtility.GetAnimatableBindings (t.gameObject, root)) {
                    if (!IsCandidate (root, binding)) continue;
                    result.Add (new Candidate {
                        binding = binding,
                        label = (binding.path.Length > 0 ? binding.path + " " : "") + binding.type.Name + "." + binding.propertyName,
                    });
                }
            }
            return result;
        }

        static bool IsCandidate (GameObject root, EditorCurveBinding binding)
        {
            if (binding.isPPtrCurve || binding.type == typeof (Transform) || binding.type == typeof (Animator)) return false;
            string property = binding.propertyName;
            if (property.StartsWith ("material.") || property.StartsWith ("m_AABB") || property == "m_DirtyAABB") return false;
            System.Type type = AnimationUtility.GetEditorCurveValueType (root, binding);
            return type == typeof (float) || type == typeof (int) || type == typeof (bool);
        }

        /// <summary>
        /// 表示モデルから見たバインディングを、編集用クリップに置くバインディングへ（パスを Controls/Props/... に）
        /// </summary>
        public static EditorCurveBinding ToClip (EditorCurveBinding display)
        {
            EditorCurveBinding binding = display;
            binding.path = RigPaths.Props (display.path);
            return binding;
        }

        /// <summary>
        /// 編集用クリップのバインディングを、表示モデル（ゲーム prefab）から見たものへ。任意のプロパティでなければ false
        /// </summary>
        public static bool TryToDisplay (EditorCurveBinding clip, out EditorCurveBinding display)
        {
            display = clip;
            string path = RigPaths.FromProps (clip.path);
            if (path == null) return false;
            display.path = path;
            return true;
        }

        /// <summary>
        /// 今の値（表示モデルから）
        /// </summary>
        public static bool TryGetValue (GameObject root, EditorCurveBinding display, out float value)
        {
            return AnimationUtility.GetFloatValue (root, display, out value);
        }

        /// <summary>
        /// クリップの任意のプロパティのカーブを、time の値で表示モデルへ入れる。前に入れてカーブが無くなった値は元へ戻す
        /// </summary>
        public void Apply (GameObject root, AnimationClip clip, float time)
        {
            if (root == null) return;
            HashSet<EditorCurveBinding> applied = new HashSet<EditorCurveBinding> ();
            RotationBatch rotations = new RotationBatch ();
            if (clip != null) {
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                    EditorCurveBinding display;
                    if (!TryToDisplay (binding, out display)) continue;
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                    if (curve == null || curve.length == 0) continue;
                    if (!originals_.ContainsKey (display)) {
                        float original;
                        if (!TryGetValue (root, display, out original)) continue;
                        originals_[display] = original;
                    }
                    float value = curve.Evaluate (time);
                    if (rotations.Add (root, display, value) || SetValue (root, display, value)) applied.Add (display);
                }
            }
            // カーブが無くなった値は元へ戻す
            List<EditorCurveBinding> released = new List<EditorCurveBinding> ();
            foreach (KeyValuePair<EditorCurveBinding, float> pair in originals_) {
                if (applied.Contains (pair.Key)) continue;
                if (!rotations.Add (root, pair.Key, pair.Value)) SetValue (root, pair.Key, pair.Value);
                released.Add (pair.Key);
            }
            foreach (EditorCurveBinding binding in released) originals_.Remove (binding);
            rotations.Flush ();
        }

        /// <summary>
        /// 今入れている値（表示モデルの今の値）を控える。骨を表示モデルへ写し直す処理（編集用の体の同期）が
        /// Transform の値（武器の握りなど）を上書きするので、その前に控えて <see cref="Put"/> で戻す
        /// </summary>
        public List<KeyValuePair<EditorCurveBinding, float>> Capture (GameObject root)
        {
            List<KeyValuePair<EditorCurveBinding, float>> result = new List<KeyValuePair<EditorCurveBinding, float>> ();
            if (root == null) return result;
            foreach (EditorCurveBinding binding in originals_.Keys) {
                float value;
                if (TryGetValue (root, binding, out value)) result.Add (new KeyValuePair<EditorCurveBinding, float> (binding, value));
            }
            return result;
        }

        /// <summary><see cref="Capture"/> で控えた値を入れ直す</summary>
        public static void Put (GameObject root, List<KeyValuePair<EditorCurveBinding, float>> values)
        {
            if (root == null || values == null) return;
            RotationBatch rotations = new RotationBatch ();
            foreach (KeyValuePair<EditorCurveBinding, float> pair in values) {
                if (!rotations.Add (root, pair.Key, pair.Value)) SetValue (root, pair.Key, pair.Value);
            }
            rotations.Flush ();
        }

        /// <summary>
        /// 入れた値を全部元へ戻す（キャラを作り直す前など）
        /// </summary>
        public void Restore (GameObject root)
        {
            if (root != null) {
                RotationBatch rotations = new RotationBatch ();
                foreach (KeyValuePair<EditorCurveBinding, float> pair in originals_) {
                    if (!rotations.Add (root, pair.Key, pair.Value)) SetValue (root, pair.Key, pair.Value);
                }
                rotations.Flush ();
            }
            originals_.Clear ();
        }

        /// <summary>
        /// Transform の回転（m_LocalRotation.x/y/z/w の 4 本）は、成分ごとに入れると 1 本入れるたびに正規化されて別の回転になる
        /// （武器の握りが 10° 以上ずれて見えた。2026-09-30 実測）。成分を集めて、最後に 1 回で入れる
        /// </summary>
        sealed class RotationBatch
        {
            readonly Dictionary<Transform, Vector4> pending_ = new Dictionary<Transform, Vector4> ();

            /// <summary>回転の成分なら貯めて true。回転でなければ false（呼び手が SetValue で入れる）</summary>
            public bool Add (GameObject root, EditorCurveBinding display, float value)
            {
                if (display.type != typeof (Transform) || !display.propertyName.StartsWith (CurveEdit.kRotation + ".")) return false;
                int axis = "xyzw".IndexOf (display.propertyName[display.propertyName.Length - 1]);
                if (axis < 0) return false;
                Transform target = display.path.Length == 0 ? root.transform : root.transform.Find (display.path);
                if (target == null) return true;
                Vector4 q;
                if (!pending_.TryGetValue (target, out q)) {
                    Quaternion current = target.localRotation;
                    q = new Vector4 (current.x, current.y, current.z, current.w);
                }
                q[axis] = value;
                pending_[target] = q;
                return true;
            }

            public void Flush ()
            {
                foreach (KeyValuePair<Transform, Vector4> pair in pending_) {
                    if (pair.Key == null) continue;
                    Vector4 q = pair.Value;
                    float length = q.magnitude;
                    pair.Key.localRotation = length > 1e-6f ? new Quaternion (q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
                }
                pending_.Clear ();
            }
        }

        /// <summary>
        /// 値を 1 つ入れる。入れられなければ false
        /// </summary>
        public static bool SetValue (GameObject root, EditorCurveBinding display, float value)
        {
            Transform target = display.path.Length == 0 ? root.transform : root.transform.Find (display.path);
            if (target == null) return false;

            if (display.type == typeof (GameObject)) {
                if (display.propertyName != "m_IsActive") return false;
                bool active = value > 0.5f;
                if (target.gameObject.activeSelf != active) target.gameObject.SetActive (active);
                return true;
            }

            Component component = target.GetComponent (display.type);
            if (component == null) return false;

            if (component is SkinnedMeshRenderer skin && display.propertyName.StartsWith ("blendShape.")) {
                Mesh mesh = skin.sharedMesh;
                int index = mesh != null ? mesh.GetBlendShapeIndex (display.propertyName.Substring ("blendShape.".Length)) : -1;
                if (index < 0) return false;
                skin.SetBlendShapeWeight (index, value);
                return true;
            }
            if (component is Behaviour behaviour && display.propertyName == "m_Enabled") {
                behaviour.enabled = value > 0.5f;
                return true;
            }
            if (component is Renderer renderer && display.propertyName == "m_Enabled") {
                renderer.enabled = value > 0.5f;
                return true;
            }

            SerializedObject serialized = new SerializedObject (component);
            SerializedProperty property = serialized.FindProperty (display.propertyName);
            if (property == null) return false;
            switch (property.propertyType) {
                case SerializedPropertyType.Float: property.floatValue = value; break;
                case SerializedPropertyType.Integer: property.intValue = Mathf.RoundToInt (value); break;
                case SerializedPropertyType.Boolean: property.boolValue = value > 0.5f; break;
                case SerializedPropertyType.Enum: property.enumValueIndex = Mathf.RoundToInt (value); break;
                default: return false;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo ();
            return true;
        }
    }

}
