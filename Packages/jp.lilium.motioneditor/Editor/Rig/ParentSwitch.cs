using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 物の持ち替え（複数の親の切り替え）の定義。ゲーム側の拘束（MultiParentConstraint など）で、物の上の持ち手ノードを
    /// どの親（手・フリーの置き場）に付けるかを重みで選ぶ。重みと握り（持ち手ノードから見た物のローカル姿勢）は
    /// 任意のプロパティ（Controls/Props/...）として編集用クリップに持つ。パスは表示モデルの Animator から。
    /// prefab の中のパスを指すので、キャラの設定（<see cref="CharacterSettings.parentSwitches"/>）に置く
    /// </summary>
    [System.Serializable]
    public sealed class ParentSwitchDefinition
    {
        [Tooltip ("画面に出す名前（Weapon など）")]
        public string name;
        [Tooltip ("拘束の部品がある GameObject のパス")]
        public string constraintPath;
        [Tooltip ("拘束の部品の型の名前（FullName か Name）")]
        public string constraintType;
        [Tooltip ("親の候補。重みのプロパティで拘束のソースと結び付く")]
        public List<ParentSource> sources = new List<ParentSource> ();
        [Tooltip ("持っている物（切り替えの瞬間にこのワールド姿勢を保つ）。親が持ち手ノード")]
        public string objectPath;
        [Tooltip ("握りのカーブを書く Transform（持ち手ノードからのローカル姿勢。ゲームがこれを物へ写す）")]
        public string gripPath;

        /// <summary>定義の誤り。空なら使える</summary>
        public List<string> Validate ()
        {
            List<string> errors = new List<string> ();
            if (string.IsNullOrEmpty (constraintPath) || string.IsNullOrEmpty (constraintType)) errors.Add ("持ち替えの拘束が指定されていない: " + name);
            if (string.IsNullOrEmpty (objectPath) || string.IsNullOrEmpty (gripPath)) errors.Add ("持ち替えの物・握りのパスが空: " + name);
            if (sources == null || sources.Count < 2) errors.Add ("持ち替えの親が 2 つ未満: " + name);
            else if (sources.Exists (source => source == null || string.IsNullOrEmpty (source.weightProperty) || string.IsNullOrEmpty (source.parentPath))) {
                errors.Add ("持ち替えの親の重み・パスが空: " + name);
            }
            return errors;
        }
    }

    [System.Serializable]
    public sealed class ParentSource
    {
        [Tooltip ("画面に出す名前（Left / Right / Free など）")]
        public string label;
        [Tooltip ("拘束の部品の、このソースの重みのプロパティ（m_Data.m_SourceObjects.m_Item0.weight など）")]
        public string weightProperty;
        [Tooltip ("このソースの親（置き場）の Transform のパス")]
        public string parentPath;
    }

    /// <summary>
    /// 物の持ち替え（<see cref="ParentSwitchDefinition"/>）のキー打ち。
    ///
    /// 持ち手（どの親に付けるか）は拘束の重み、握りは持ち手ノードから見た物のローカル姿勢で、どちらも任意のプロパティ（Controls/Props/...）に持つ。
    /// 切り替えるフレーム f では、物のワールド姿勢を保つように握りを新しい親の基準で入れ直す（跳ばない）。
    /// f の 1 つ前に元の重みと握りのキーを置き、そこから f までは値を保つ（接線 Constant）ので、f で段差として切り替わる。
    /// 焼くと任意のプロパティは接線ごとゲームのパスへ写るので、段差は崩れない。
    ///
    /// 読むのは表示モデルの今の姿勢（f を評価した後）。左右反転などで物と親の見え方がずれているときは
    /// <see cref="blockReason"/> でゲーム側が止める
    /// </summary>
    public static class ParentSwitch
    {
        /// <summary>
        /// 今は持ち替えを打てない理由（表示モデルを渡す。打てるなら null）。左右反転中など、ゲーム側の事情で止めるときに設定する
        /// </summary>
        public static System.Func<GameObject, string> blockReason;

        static readonly string[] kPositionAxes = { "x", "y", "z" };

        /// <summary>定義を表示モデルの物へ結び付けたもの</summary>
        public sealed class Bound
        {
            public ParentSwitchDefinition definition;
            public Component constraint;
            public Transform[] parents;
            public Transform obj;
            public Transform grip;
            /// <summary>各ソースの重み（表示モデルから見たバインディング）</summary>
            public EditorCurveBinding[] weights;
        }

        /// <summary>
        /// 定義を表示モデル（Animator の GameObject）へ結び付ける。見つからない物があれば null と理由
        /// </summary>
        public static Bound Bind (GameObject root, ParentSwitchDefinition definition, out string error)
        {
            error = null;
            if (root == null || definition == null) {
                error = "表示モデルが無い";
                return null;
            }
            Transform constraintTransform = Find (root, definition.constraintPath);
            Component constraint = null;
            if (constraintTransform != null) {
                foreach (Component component in constraintTransform.GetComponents<Component> ()) {
                    if (component == null) continue;
                    System.Type type = component.GetType ();
                    if (type.FullName == definition.constraintType || type.Name == definition.constraintType) {
                        constraint = component;
                        break;
                    }
                }
            }
            if (constraint == null) {
                error = "拘束が見つからない: " + definition.constraintPath + " (" + definition.constraintType + ")";
                return null;
            }
            int count = definition.sources != null ? definition.sources.Count : 0;
            Bound bound = new Bound {
                definition = definition,
                constraint = constraint,
                parents = new Transform[count],
                weights = new EditorCurveBinding[count],
                obj = Find (root, definition.objectPath),
                grip = Find (root, definition.gripPath),
            };
            if (bound.obj == null || bound.grip == null) {
                error = "持っている物・握りが見つからない: " + definition.objectPath + " / " + definition.gripPath;
                return null;
            }
            for (int i = 0; i < count; i++) {
                ParentSource source = definition.sources[i];
                bound.parents[i] = Find (root, source.parentPath);
                if (bound.parents[i] == null) {
                    error = "親が見つからない: " + source.parentPath;
                    return null;
                }
                bound.weights[i] = EditorCurveBinding.FloatCurve (definition.constraintPath, constraint.GetType (), source.weightProperty);
                float ignored;
                if (!PropertyPlayer.TryGetValue (root, bound.weights[i], out ignored)) {
                    error = "重みのプロパティが読めない: " + source.weightProperty;
                    return null;
                }
            }
            return bound;
        }

        /// <summary>今（表示モデルの値で）いちばん重い親の番号。重みが全部 0 なら -1</summary>
        public static int Current (GameObject root, Bound bound)
        {
            int best = -1;
            float bestWeight = 0;
            for (int i = 0; i < bound.weights.Length; i++) {
                float weight;
                if (PropertyPlayer.TryGetValue (root, bound.weights[i], out weight) && weight > bestWeight) {
                    best = i;
                    bestWeight = weight;
                }
            }
            return best;
        }

        /// <summary>
        /// frame で親を target に切り替えるキーを打つ。表示モデルは frame を評価した姿勢になっていること。
        /// 打つのは frame と 1 つ前だけ。後ろのフレームの握りは触らないので、そのまま新しい親の基準になる（物は新しい親に付いて動く）。
        /// 返す文字列は知らせ（無ければ null）
        /// </summary>
        public static string Switch (AnimationClip clip, GameObject root, Bound bound, int frame, int target)
        {
            float rate = clip.frameRate;
            int count = bound.weights.Length;

            // 物のワールド姿勢を保つ握り（新しい親から見た物）
            Transform parent = bound.parents[target];
            Vector3 gripPosition = Quaternion.Inverse (parent.rotation) * (bound.obj.position - parent.position);
            Quaternion gripRotation = Quaternion.Inverse (parent.rotation) * bound.obj.rotation;

            string gripPath = RigPaths.Props (bound.definition.gripPath);
            EditorCurveBinding[] weightBindings = new EditorCurveBinding[count];
            for (int i = 0; i < count; i++) weightBindings[i] = PropertyPlayer.ToClip (bound.weights[i]);

            // 1 つ前のフレームに、今の持ち手と握りを置く（そこから frame までは保つ）
            if (frame > 0) {
                int before = frame - 1;
                float time = before / rate;
                for (int i = 0; i < count; i++) {
                    float value = ValueAt (clip, root, weightBindings[i], bound.weights[i], time);
                    CurveEdit.SetKey (clip, weightBindings[i], before, value);
                }
                Vector3 position = bound.grip.localPosition;
                for (int axis = 0; axis < 3; axis++) {
                    EditorCurveBinding binding = PositionBinding (gripPath, axis);
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                    float value = curve != null && curve.length > 0 ? curve.Evaluate (time) : position[axis];
                    CurveEdit.SetKey (clip, binding, before, value);
                }
                Quaternion rotation = HasRotation (clip, gripPath)
                    ? CurveEdit.EvaluateRotation (clip, gripPath, time)
                    : bound.grip.localRotation;
                CurveEdit.SetRotationKey (clip, gripPath, before, rotation);
            }

            // frame に新しい持ち手と握り
            for (int i = 0; i < count; i++) CurveEdit.SetKey (clip, weightBindings[i], frame, i == target ? 1 : 0);
            for (int axis = 0; axis < 3; axis++) CurveEdit.SetKey (clip, PositionBinding (gripPath, axis), frame, gripPosition[axis]);
            CurveEdit.SetRotationKey (clip, gripPath, frame, gripRotation);

            // 重みはいつも段差。握りは frame の 1 つ前から frame まで保つ
            for (int i = 0; i < count; i++) SetAllConstant (clip, weightBindings[i]);
            if (frame > 0) {
                for (int axis = 0; axis < 3; axis++) SetRightConstant (clip, PositionBinding (gripPath, axis), frame - 1);
                for (int axis = 0; axis < 4; axis++) SetRightConstant (clip, CurveEdit.RotationBinding (gripPath, axis), frame - 1);
            }

            return null;
        }

        static EditorCurveBinding PositionBinding (string path, int axis)
        {
            return EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalPosition." + kPositionAxes[axis]);
        }

        static bool HasRotation (AnimationClip clip, string path)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, CurveEdit.RotationBinding (path, 3));
            return curve != null && curve.length > 0;
        }

        /// <summary>time の値。カーブが無ければ表示モデルの今の値（カーブが無いので時刻によらない）</summary>
        static float ValueAt (AnimationClip clip, GameObject root, EditorCurveBinding clipBinding, EditorCurveBinding display, float time)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, clipBinding);
            if (curve != null && curve.length > 0) return curve.Evaluate (time);
            float value;
            return PropertyPlayer.TryGetValue (root, display, out value) ? value : 0;
        }

        static void SetAllConstant (AnimationClip clip, EditorCurveBinding binding)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            if (curve == null) return;
            for (int i = 0; i < curve.length; i++) {
                AnimationUtility.SetKeyBroken (curve, i, true);
                AnimationUtility.SetKeyLeftTangentMode (curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode (curve, i, AnimationUtility.TangentMode.Constant);
            }
            AnimationUtility.SetEditorCurve (clip, binding, curve);
        }

        static void SetRightConstant (AnimationClip clip, EditorCurveBinding binding, int frame)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            if (curve == null) return;
            int index = ClipKeyUtility.FindKeyAtFrame (curve, clip.frameRate, frame);
            if (index < 0) return;
            AnimationUtility.SetKeyBroken (curve, index, true);
            AnimationUtility.SetKeyRightTangentMode (curve, index, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetEditorCurve (clip, binding, curve);
        }

        static Transform Find (GameObject root, string path)
        {
            if (string.IsNullOrEmpty (path)) return null;
            return root.transform.Find (path);
        }
    }

}
