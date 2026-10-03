using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// Override の層の合成の仕方（S14。語彙は Unity のアニメーションツールで通じる言い方に合わせる）
    /// </summary>
    public enum OverrideMode
    {
        /// <summary>元（下の層）に差分を足す。元を直せば追従する</summary>
        Additive,
        /// <summary>キーのある値を置き換える（重みで元と混ぜる）</summary>
        Override,
    }

    /// <summary>
    /// 元のクリップ（Base Layer）の上に重ねる Override のクリップ 1 枚（S14）。1 枚が 1 段（OverrideLayer）になる。
    /// クリップは元とは別のアセット（`<元>.override.anim`）。どの元に重ねるか・合成の仕方は、そのクリップの .meta に持つ（OverrideFiles）。
    /// ブレンド重みとミュート（👁）は段の仕組みが持つ（段ごとの状態として窓に保存される）
    /// </summary>
    [System.Serializable]
    public sealed class OverrideClip
    {
        public AnimationClip clip;
        public OverrideMode mode = OverrideMode.Additive;
        /// <summary>重ねる順（小さいほど上。S21。同じなら名前順）</summary>
        public int order;
    }

    /// <summary>
    /// Override の合成の式。値はコントロール（操作値）ごとに、その値の空間の中で合成する。
    /// 同じコントロールの値同士しか混ぜないので、空間の違う値（IK の目標と骨の回転など）が混ざることは無い。
    /// - Additive: 回転は元の後ろに掛ける（元 × 差分）、位置は足す、大きさは掛ける。足の転がしの角度は足す。ほかの数値（IK の重みなど）は置き換える。
    /// - Override: キーのある値を置き換え、重みで元と混ぜる。
    /// 書くときは逆に、狙いの値と元の値から、クリップに入れる値（差分か、そのままの値）を求める
    /// </summary>
    public static class OverrideMath
    {
        public static Vector3 ComposePosition (OverrideMode mode, Vector3 source, Vector3 stored, float weight)
        {
            if (mode == OverrideMode.Override) return Vector3.LerpUnclamped (source, stored, weight);
            return source + stored * weight;
        }

        public static Quaternion ComposeRotation (OverrideMode mode, Quaternion source, Quaternion stored, float weight)
        {
            if (mode == OverrideMode.Override) return Quaternion.Slerp (source, Normalize (stored), weight);
            return source * Quaternion.Slerp (Quaternion.identity, Normalize (stored), weight);
        }

        public static Vector3 ComposeScale (OverrideMode mode, Vector3 source, Vector3 stored, float weight)
        {
            if (mode == OverrideMode.Override) return Vector3.LerpUnclamped (source, stored, weight);
            return Vector3.Scale (source, Vector3.LerpUnclamped (Vector3.one, stored, weight));
        }

        public static float ComposeFloat (float source, float stored, float weight)
        {
            return Mathf.LerpUnclamped (source, stored, weight);
        }

        /// <param name="additive">足し合わせで重ねる値か（足の転がしの角度）。そうでない値（IK の重みなど）は Additive でも置き換える</param>
        public static float ComposeFloat (OverrideMode mode, bool additive, float source, float stored, float weight)
        {
            if (mode == OverrideMode.Additive && additive) return source + stored * weight;
            return ComposeFloat (source, stored, weight);
        }

        public static float StoreFloat (OverrideMode mode, bool additive, float source, float value)
        {
            return mode == OverrideMode.Additive && additive ? value - source : value;
        }

        public static Vector3 StorePosition (OverrideMode mode, Vector3 source, Vector3 value)
        {
            return mode == OverrideMode.Override ? value : value - source;
        }

        public static Quaternion StoreRotation (OverrideMode mode, Quaternion source, Quaternion value)
        {
            return mode == OverrideMode.Override ? value : Normalize (Quaternion.Inverse (source) * value);
        }

        public static Vector3 StoreScale (OverrideMode mode, Vector3 source, Vector3 value)
        {
            if (mode == OverrideMode.Override) return value;
            return new Vector3 (Divide (value.x, source.x), Divide (value.y, source.y), Divide (value.z, source.z));
        }

        static float Divide (float value, float source)
        {
            return Mathf.Abs (source) > 1e-6f ? value / source : 1;
        }

        static Quaternion Normalize (Quaternion q)
        {
            float length = Mathf.Sqrt (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (length < 1e-6f) return Quaternion.identity;
            return new Quaternion (q.x / length, q.y / length, q.z / length, q.w / length);
        }
    }

    /// <summary>
    /// Override のクリップを編集用の体のコントロールへ重ねる。クリップのカーブはクリップごとにまとめて覚え、
    /// カーブが書き換わったら（onCurveWasModified・Undo）捨てる。
    /// 重ねるのはコントロールの Transform（位置・回転・大きさ）と、IK の切替・Rig の代理の重み。それ以外のプロパティは v1 では扱わない
    /// </summary>
    public static class OverrideApplier
    {
        const string kPosition = "m_LocalPosition.";
        const string kRotation = "m_LocalRotation.";
        const string kScale = "m_LocalScale.";

        sealed class TransformCurves
        {
            public string path;
            public AnimationCurve[] position;
            public AnimationCurve[] rotation;
            public AnimationCurve[] scale;
        }

        sealed class FloatCurve
        {
            public string path;
            public System.Type type;
            public string property;
            public AnimationCurve curve;
        }

        sealed class Curves
        {
            public readonly List<TransformCurves> transforms = new List<TransformCurves> ();
            public readonly List<FloatCurve> floats = new List<FloatCurve> ();
        }

        static readonly Dictionary<AnimationClip, Curves> cache_ = new Dictionary<AnimationClip, Curves> ();

        static OverrideApplier ()
        {
            AnimationUtility.onCurveWasModified += (clip, binding, type) => cache_.Remove (clip);
            Undo.undoRedoPerformed += () => cache_.Clear ();
        }

        /// <summary>覚えているカーブを捨てる（クリップを外から書き換えたとき）</summary>
        public static void Invalidate (AnimationClip clip = null)
        {
            if (clip == null) cache_.Clear ();
            else cache_.Remove (clip);
        }

        /// <summary>
        /// 層を重ねる。root は編集用の体のルート（カーブのパスの基準）
        /// </summary>
        public static void Apply (AnimationClip clip, OverrideMode mode, float weight, float time, Transform root)
        {
            if (clip == null || root == null || weight <= 0) return;
            Curves curves = GetCurves (clip);
            weight = Mathf.Clamp01 (weight);
            foreach (TransformCurves channel in curves.transforms) {
                Transform t = channel.path.Length == 0 ? root : root.Find (channel.path);
                if (t == null) continue;
                if (channel.position != null) {
                    t.localPosition = OverrideMath.ComposePosition (mode, t.localPosition, Evaluate (channel.position, time, mode == OverrideMode.Override ? t.localPosition : Vector3.zero), weight);
                }
                if (channel.rotation != null) {
                    Quaternion stored = new Quaternion (channel.rotation[0].Evaluate (time), channel.rotation[1].Evaluate (time), channel.rotation[2].Evaluate (time), channel.rotation[3].Evaluate (time));
                    t.localRotation = OverrideMath.ComposeRotation (mode, t.localRotation, stored, weight);
                }
                if (channel.scale != null) {
                    t.localScale = OverrideMath.ComposeScale (mode, t.localScale, Evaluate (channel.scale, time, mode == OverrideMode.Override ? t.localScale : Vector3.one), weight);
                }
            }
            foreach (FloatCurve channel in curves.floats) {
                Transform t = channel.path.Length == 0 ? root : root.Find (channel.path);
                Component component = t != null ? t.GetComponent (channel.type) : null;
                if (component == null) continue;
                float current;
                if (!TryGetFloat (component, channel.property, out current)) continue;
                SetFloat (component, channel.property, OverrideMath.ComposeFloat (mode, IkControl.IsAdditive (channel.property) && component is IkControl, current, channel.curve.Evaluate (time), weight));
            }
        }

        /// <summary>重ねられる数値のプロパティか（IK の切替と足の転がしの角度・全身 IK の点の固定の強さ・Rig の代理の重み）</summary>
        public static bool IsSupportedFloat (EditorCurveBinding binding)
        {
            return (binding.type == typeof (IkControl) && IkControl.IndexOf (binding.propertyName) >= 0)
                || (binding.type == typeof (BodyPoint) && BodyPoint.IndexOf (binding.propertyName) >= 0)
                || (binding.type == typeof (RigLayerProxy) && binding.propertyName == RigLayerProxy.kWeightProperty)
                || (binding.type == typeof (RigConstraintProxy) && binding.propertyName == RigConstraintProxy.kWeightProperty);
        }

        static bool TryGetFloat (Component component, string property, out float value)
        {
            value = 0;
            IkControl ik = component as IkControl;
            if (ik != null) {
                int index = IkControl.IndexOf (property);
                if (index < 0) return false;
                value = ik.GetValue (index);
                return true;
            }
            BodyPoint point = component as BodyPoint;
            if (point != null) {
                int index = BodyPoint.IndexOf (property);
                if (index < 0) return false;
                value = point.GetValue (index);
                return true;
            }
            RigLayerProxy layer = component as RigLayerProxy;
            if (layer != null) { value = layer.weight; return true; }
            RigConstraintProxy constraint = component as RigConstraintProxy;
            if (constraint != null) { value = constraint.weight; return true; }
            return false;
        }

        static void SetFloat (Component component, string property, float value)
        {
            IkControl ik = component as IkControl;
            if (ik != null) {
                ik.SetValue (IkControl.IndexOf (property), value);
                return;
            }
            BodyPoint point = component as BodyPoint;
            if (point != null) {
                point.SetValue (BodyPoint.IndexOf (property), value);
                return;
            }
            RigLayerProxy layer = component as RigLayerProxy;
            if (layer != null) { layer.weight = value; return; }
            RigConstraintProxy constraint = component as RigConstraintProxy;
            if (constraint != null) constraint.weight = value;
        }

        /// <summary>欠けた成分は fallback（Additive なら 0 / 1、Override なら元の値）</summary>
        static Vector3 Evaluate (AnimationCurve[] curves, float time, Vector3 fallback)
        {
            return new Vector3 (
                curves[0] != null ? curves[0].Evaluate (time) : fallback.x,
                curves[1] != null ? curves[1].Evaluate (time) : fallback.y,
                curves[2] != null ? curves[2].Evaluate (time) : fallback.z);
        }

        static Curves GetCurves (AnimationClip clip)
        {
            Curves curves;
            if (cache_.TryGetValue (clip, out curves)) return curves;
            curves = new Curves ();
            Dictionary<string, TransformCurves> byPath = new Dictionary<string, TransformCurves> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (binding.type == typeof (Transform)) {
                    int axis;
                    AnimationCurve[] slot = null;
                    TransformCurves channel;
                    if (!byPath.TryGetValue (binding.path, out channel)) {
                        channel = new TransformCurves { path = binding.path };
                        byPath.Add (binding.path, channel);
                        curves.transforms.Add (channel);
                    }
                    if (binding.propertyName.StartsWith (kPosition)) slot = channel.position ?? (channel.position = new AnimationCurve[3]);
                    else if (binding.propertyName.StartsWith (kRotation)) slot = channel.rotation ?? (channel.rotation = new AnimationCurve[4]);
                    else if (binding.propertyName.StartsWith (kScale)) slot = channel.scale ?? (channel.scale = new AnimationCurve[3]);
                    if (slot == null) continue;
                    axis = "xyzw".IndexOf (binding.propertyName[binding.propertyName.Length - 1]);
                    if (axis < 0 || axis >= slot.Length) continue;
                    slot[axis] = AnimationUtility.GetEditorCurve (clip, binding);
                    continue;
                }
                if (IsSupportedFloat (binding)) {
                    curves.floats.Add (new FloatCurve { path = binding.path, type = binding.type, property = binding.propertyName, curve = AnimationUtility.GetEditorCurve (clip, binding) });
                }
            }
            // 回転は 4 成分がそろっていなければ使わない
            foreach (TransformCurves channel in curves.transforms) {
                if (channel.rotation != null && channel.rotation.Any (c => c == null)) channel.rotation = null;
            }
            cache_[clip] = curves;
            return curves;
        }
    }

    /// <summary>
    /// Override のクリップのファイル。元のクリップと同じフォルダに `<元の名前>.override.anim` で作り、
    /// どの元に重ねるか・合成の仕方・重み・ミュートを .meta の userData に持つ（AnimationClip は利用者のデータを持てないため）
    /// </summary>
    public static class OverrideFiles
    {
        public const string kSuffix = ".override";

        [System.Serializable]
        sealed class Settings
        {
            public string baseClip;
            /// <summary>元が FBX の中のクリップ（サブアセット）なら、そのクリップの名前（同じ FBX の別のテイクと分ける。S20）。空なら元はファイルそのもの</summary>
            public string baseName;
            public string mode;
            /// <summary>重ねる順（S21）</summary>
            public int order;
        }

        /// <summary>
        /// 元のクリップの名前の元になるもの。FBX の中のクリップ（サブアセット）はクリップの名前、ファイルそのものならファイルの名前
        /// </summary>
        static string BaseName (AnimationClip baseClip, string path)
        {
            return AssetDatabase.IsSubAsset (baseClip) ? baseClip.name : Path.GetFileNameWithoutExtension (path);
        }

        [System.Serializable]
        sealed class Envelope
        {
            public Settings overrideLayer;
        }

        /// <summary>新しく作るときのパス（重ならない名前）</summary>
        public static string DefaultPath (AnimationClip baseClip)
        {
            string path = AssetDatabase.GetAssetPath (baseClip);
            if (string.IsNullOrEmpty (path)) return null;
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            return AssetDatabase.GenerateUniqueAssetPath (folder + "/" + BaseName (baseClip, path) + kSuffix + ".anim");
        }

        /// <summary>
        /// 元に重ねる Override のクリップを新しく作る（元と同じフレームレート）。作れなければ null
        /// </summary>
        public static OverrideClip Create (AnimationClip baseClip)
        {
            return Create (baseClip, true);
        }

        /// <param name="mark">.meta に「どの元に重ねるか」の印を書くか（キャラの設定に置くときは書かない。S21）</param>
        public static OverrideClip Create (AnimationClip baseClip, bool mark)
        {
            string path = DefaultPath (baseClip);
            if (path == null) return null;
            AnimationClip clip = new AnimationClip { frameRate = baseClip.frameRate };
            AssetDatabase.CreateAsset (clip, path);
            OverrideClip entry = new OverrideClip { clip = clip };
            if (mark) Save (entry, baseClip);
            return entry;
        }

        /// <summary>
        /// 元に重ねている Override のクリップ（元と同じフォルダから探す。名前順）
        /// </summary>
        public static List<OverrideClip> FindFor (AnimationClip baseClip)
        {
            List<OverrideClip> result = new List<OverrideClip> ();
            string path = baseClip != null ? AssetDatabase.GetAssetPath (baseClip) : null;
            if (string.IsNullOrEmpty (path)) return result;
            string baseGuid = AssetDatabase.AssetPathToGUID (path);
            string baseName = AssetDatabase.IsSubAsset (baseClip) ? baseClip.name : null;
            string folder = Path.GetDirectoryName (path).Replace ('\\', '/');
            foreach (string guid in AssetDatabase.FindAssets ("t:AnimationClip", new[] { folder })) {
                string candidate = AssetDatabase.GUIDToAssetPath (guid);
                // 下のフォルダは見ない
                if (Path.GetDirectoryName (candidate).Replace ('\\', '/') != folder) continue;
                Settings settings = Read (candidate);
                if (settings == null || settings.baseClip != baseGuid) continue;
                // 同じ FBX の別のテイクに重ねたものは除く（名前の無い古い印は、どのテイクにも出す）
                if (!string.IsNullOrEmpty (settings.baseName) && settings.baseName != baseName) continue;
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip> (candidate);
                if (clip == null) continue;
                result.Add (ToEntry (clip, settings));
            }
            return result.OrderBy (l => l.order).ThenBy (l => AssetDatabase.GetAssetPath (l.clip)).ToList ();
        }

        /// <summary>Override のクリップか（どこかの元に重ねる印がある）</summary>
        public static bool IsOverride (AnimationClip clip)
        {
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            return !string.IsNullOrEmpty (path) && Read (path) != null;
        }

        /// <summary>
        /// 層の設定（どの元に重ねるか・合成の仕方）を .meta に書く
        /// </summary>
        public static bool Save (OverrideClip entry, AnimationClip baseClip)
        {
            string path = entry != null && entry.clip != null ? AssetDatabase.GetAssetPath (entry.clip) : null;
            AssetImporter importer = string.IsNullOrEmpty (path) ? null : AssetImporter.GetAtPath (path);
            string basePath = baseClip != null ? AssetDatabase.GetAssetPath (baseClip) : null;
            if (importer == null || string.IsNullOrEmpty (basePath)) return false;
            Settings settings = new Settings {
                baseClip = AssetDatabase.AssetPathToGUID (basePath),
                baseName = AssetDatabase.IsSubAsset (baseClip) ? baseClip.name : null,
                mode = entry.mode.ToString (),
                order = entry.order,
            };
            string json = JsonUtility.ToJson (new Envelope { overrideLayer = settings });
            if (importer.userData == json) return true;
            importer.userData = json;
            AssetDatabase.WriteImportSettingsIfDirty (path);
            return true;
        }

        /// <summary>元から外す（ファイルは消さない。印だけ消す）</summary>
        public static void Unlink (AnimationClip clip)
        {
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            AssetImporter importer = string.IsNullOrEmpty (path) ? null : AssetImporter.GetAtPath (path);
            if (importer == null || Read (path) == null) return;
            importer.userData = "";
            AssetDatabase.WriteImportSettingsIfDirty (path);
        }

        static OverrideClip ToEntry (AnimationClip clip, Settings settings)
        {
            OverrideMode mode;
            if (!System.Enum.TryParse (settings.mode, out mode)) mode = OverrideMode.Additive;
            return new OverrideClip { clip = clip, mode = mode, order = settings.order };
        }

        static Settings Read (string path)
        {
            AssetImporter importer = AssetImporter.GetAtPath (path);
            if (importer == null || string.IsNullOrEmpty (importer.userData) || !importer.userData.Contains ("overrideLayer")) return null;
            try {
                Envelope envelope = JsonUtility.FromJson<Envelope> (importer.userData);
                return envelope != null && envelope.overrideLayer != null && !string.IsNullOrEmpty (envelope.overrideLayer.baseClip) ? envelope.overrideLayer : null;
            }
            catch (System.ArgumentException) {
                return null;
            }
        }
    }

}
