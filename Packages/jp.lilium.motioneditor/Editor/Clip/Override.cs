using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
        /// <summary>効く範囲（S17）。null なら全部に効く。マスクの外のカーブは重ねず、書き込み先のときは打たない</summary>
        public ChannelMask mask;
    }

    /// <summary>
    /// Override の合成の式。値はコントロール（操作値）ごとに、その値の空間の中で合成する。
    /// 同じコントロールの値同士しか混ぜないので、空間の違う値（全身 IK の点の位置と骨の回転など）が混ざることは無い。
    /// - Additive: 回転は元の後ろに掛ける（元 × 差分）、位置は足す、大きさは掛ける。数値は、足し合わせる値（任意のプロパティの数値）だけ足し、
    ///   ほかの数値（点の固定の強さ・Rig の代理の重みなど）は置き換える。
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

        /// <param name="additive">足し合わせで重ねる値か（任意のプロパティの数値。PropertyPlayer.IsAdditive）。そうでない値（点の固定の強さなど）は Additive でも置き換える</param>
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
            return CurveEdit.ToRotation (q.x, q.y, q.z, q.w);
        }
    }

    /// <summary>
    /// Override のクリップを編集用の体のコントロールへ重ねる。クリップのカーブはクリップごとにまとめて覚え、
    /// カーブが書き換わったら（onCurveWasModified・Undo）捨てる。
    /// 重ねるのはコントロールの Transform（位置・回転・大きさ）と、全身 IK の点の固定の強さ・Rig の代理の重み。それ以外のプロパティは v1 では扱わない
    /// </summary>
    public static class OverrideApplier
    {
        const string kPosition = CurveEdit.kPosition + ".";
        const string kRotation = CurveEdit.kRotation + ".";
        const string kScale = CurveEdit.kScale + ".";

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
            /// <summary>任意のプロパティ（Controls/Props/...。S17）。値は編集用の体ではなく表示モデルへ入るので別に持つ</summary>
            public readonly List<PropertyCurve> properties = new List<PropertyCurve> ();
        }

        /// <summary>任意のプロパティのカーブ 1 本（clip は編集用クリップのバインディング、display は表示モデルから見たもの）</summary>
        public struct PropertyCurve
        {
            public EditorCurveBinding clip;
            public EditorCurveBinding display;
            public AnimationCurve curve;
        }

        /// <summary>Override のクリップの任意のプロパティのカーブ（覚えておいた物。書き換えたら捨て直す）</summary>
        public static IReadOnlyList<PropertyCurve> GetPropertyCurves (AnimationClip clip)
        {
            return clip != null ? GetCurves (clip).properties : (IReadOnlyList<PropertyCurve>)new PropertyCurve[0];
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
        internal static void Apply (AnimationClip clip, OverrideMode mode, float weight, float time, Transform root)
        {
            Apply (clip, mode, weight, time, root, null, null);
        }

        /// <summary>
        /// 層を重ねる。mask があれば、その外のカーブは重ねない（S17）。parts はパスの部位の引き当て（無ければ部位は見ない）
        /// </summary>
        public static void Apply (AnimationClip clip, OverrideMode mode, float weight, float time, Transform root, ChannelMask mask, ChannelMaskParts parts)
        {
            if (clip == null || root == null || weight <= 0) return;
            Curves curves = GetCurves (clip);
            weight = Mathf.Clamp01 (weight);
            foreach (TransformCurves channel in curves.transforms) {
                // Transform の 3 つの値は同じ物の値なので、位置・回転・大きさをまとめて絞る（決まりはパスと部位で決まる）
                if (mask != null && !mask.Contains (EditorCurveBinding.FloatCurve (channel.path, typeof (Transform), kRotation + "x"), PartOf (parts, channel.path))) continue;
                Transform t = CurveEdit.FindPath (root, channel.path);
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
                if (mask != null && !mask.Contains (EditorCurveBinding.FloatCurve (channel.path, channel.type, channel.property), PartOf (parts, channel.path))) continue;
                Transform t = CurveEdit.FindPath (root, channel.path);
                Component component = t != null ? t.GetComponent (channel.type) : null;
                if (component == null) continue;
                float current;
                if (!TryGetFloat (component, channel.property, out current)) continue;
                SetFloat (component, channel.property, OverrideMath.ComposeFloat (current, channel.curve.Evaluate (time), weight));
            }
        }

        /// <summary>
        /// 層の値と今の値の差（外部の補間。BetweenClips）。カーブごとに、位置・大きさ・数値は今の値 − 層の値、回転は 層の値⁻¹ × 今の値（骨の向きから見た回り）
        /// </summary>
        public sealed class Offsets
        {
            internal object curves;
            internal Vector3[] positions;
            internal Quaternion[] rotations;
            internal Vector3[] scales;
            internal float[] floats;
        }

        /// <summary>今の値（キーの姿勢を当てた所）と、time の層の値の差を測る</summary>
        public static Offsets MeasureOffsets (AnimationClip clip, float time, Transform root)
        {
            if (clip == null || root == null) return null;
            Curves curves = GetCurves (clip);
            Offsets offsets = new Offsets {
                curves = curves,
                positions = new Vector3[curves.transforms.Count],
                rotations = new Quaternion[curves.transforms.Count],
                scales = new Vector3[curves.transforms.Count],
                floats = new float[curves.floats.Count],
            };
            for (int i = 0; i < curves.transforms.Count; i++) {
                TransformCurves channel = curves.transforms[i];
                offsets.rotations[i] = Quaternion.identity;
                Transform t = CurveEdit.FindPath (root, channel.path);
                if (t == null) continue;
                if (channel.position != null) offsets.positions[i] = t.localPosition - Evaluate (channel.position, time, t.localPosition);
                if (channel.rotation != null) offsets.rotations[i] = Quaternion.Inverse (EvaluateRotation (channel, time)) * t.localRotation;
                if (channel.scale != null) offsets.scales[i] = t.localScale - Evaluate (channel.scale, time, t.localScale);
            }
            for (int i = 0; i < curves.floats.Count; i++) {
                FloatCurve channel = curves.floats[i];
                Transform t = CurveEdit.FindPath (root, channel.path);
                Component component = t != null ? t.GetComponent (channel.type) : null;
                float current;
                if (component != null && TryGetFloat (component, channel.property, out current)) offsets.floats[i] = current - channel.curve.Evaluate (time);
            }
            return offsets;
        }

        /// <summary>
        /// 層の値に、from から to へ blend（0〜1）だけ移した差を足して、そのまま置く。差を測ったときと同じカーブのクリップでなければ何もせず false
        /// </summary>
        public static bool ApplyWithOffsets (AnimationClip clip, float time, Transform root, Offsets from, Offsets to, float blend)
        {
            if (clip == null || root == null || from == null || to == null) return false;
            Curves curves = GetCurves (clip);
            if (from.curves != curves || to.curves != curves) return false;
            for (int i = 0; i < curves.transforms.Count; i++) {
                TransformCurves channel = curves.transforms[i];
                Transform t = CurveEdit.FindPath (root, channel.path);
                if (t == null) continue;
                if (channel.position != null) {
                    t.localPosition = Evaluate (channel.position, time, t.localPosition) + Vector3.Lerp (from.positions[i], to.positions[i], blend);
                }
                if (channel.rotation != null) {
                    t.localRotation = EvaluateRotation (channel, time) * Quaternion.Slerp (from.rotations[i], to.rotations[i], blend);
                }
                if (channel.scale != null) {
                    t.localScale = Evaluate (channel.scale, time, t.localScale) + Vector3.Lerp (from.scales[i], to.scales[i], blend);
                }
            }
            for (int i = 0; i < curves.floats.Count; i++) {
                FloatCurve channel = curves.floats[i];
                Transform t = CurveEdit.FindPath (root, channel.path);
                Component component = t != null ? t.GetComponent (channel.type) : null;
                if (component != null) SetFloat (component, channel.property, channel.curve.Evaluate (time) + Mathf.Lerp (from.floats[i], to.floats[i], blend));
            }
            return true;
        }

        static Quaternion EvaluateRotation (TransformCurves channel, float time)
        {
            Quaternion q = new Quaternion (channel.rotation[0].Evaluate (time), channel.rotation[1].Evaluate (time), channel.rotation[2].Evaluate (time), channel.rotation[3].Evaluate (time));
            float length = Mathf.Sqrt (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > 1e-6f ? new Quaternion (q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }

        static AvatarMaskBodyPart PartOf (ChannelMaskParts parts, string path)
        {
            return parts != null ? parts.PartOf (path) : AvatarMaskBodyPart.LastBodyPart;
        }

        /// <summary>重ねられる数値のプロパティか（全身 IK の点の固定の強さ・Rig の代理の重み）</summary>
        public static bool IsSupportedFloat (EditorCurveBinding binding)
        {
            return (binding.type == typeof (BodyPoint) && BodyPoint.IndexOf (binding.propertyName) >= 0)
                || (typeof (RigWeightProxy).IsAssignableFrom (binding.type) && binding.propertyName == RigWeightProxy.kWeightProperty);
        }

        static bool TryGetFloat (Component component, string property, out float value)
        {
            value = 0;
            BodyPoint point = component as BodyPoint;
            if (point != null) {
                int index = BodyPoint.IndexOf (property);
                if (index < 0) return false;
                value = point.GetValue (index);
                return true;
            }
            RigWeightProxy proxy = component as RigWeightProxy;
            if (proxy == null) return false;
            value = proxy.weight;
            return true;
        }

        static void SetFloat (Component component, string property, float value)
        {
            BodyPoint point = component as BodyPoint;
            if (point != null) {
                point.SetValue (BodyPoint.IndexOf (property), value);
                return;
            }
            RigWeightProxy proxy = component as RigWeightProxy;
            if (proxy != null) proxy.weight = value;
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
                EditorCurveBinding display;
                if (PropertyPlayer.TryToDisplay (binding, out display)) {
                    curves.properties.Add (new PropertyCurve { clip = binding, display = display, curve = AnimationUtility.GetEditorCurve (clip, binding) });
                    continue;
                }
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
                    axis = CurveEdit.AxisOf (binding.propertyName);
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

        /// <summary>.meta に書く段の印（ClipUserData.overrideLayer）</summary>
        [System.Serializable]
        internal sealed class Settings
        {
            public string baseClip;
            /// <summary>元が FBX の中のクリップ（サブアセット）なら、そのクリップの名前（同じ FBX の別のテイクと分ける。S20）。空なら元はファイルそのもの</summary>
            public string baseName;
            public string mode;
            /// <summary>重ねる順（S21）</summary>
            public int order;
            /// <summary>効く範囲（ChannelMask の GUID。S17）。空なら全部に効く</summary>
            public string mask;
        }

        /// <summary>
        /// 元のクリップの名前の元になるもの。FBX の中のクリップ（サブアセット）はクリップの名前、ファイルそのものならファイルの名前
        /// </summary>
        static string BaseName (AnimationClip baseClip, string path)
        {
            return AssetDatabase.IsSubAsset (baseClip) ? baseClip.name : Path.GetFileNameWithoutExtension (path);
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
            string basePath = baseClip != null ? AssetDatabase.GetAssetPath (baseClip) : null;
            if (string.IsNullOrEmpty (path) || string.IsNullOrEmpty (basePath)) return false;
            Settings settings = new Settings {
                baseClip = AssetDatabase.AssetPathToGUID (basePath),
                baseName = AssetDatabase.IsSubAsset (baseClip) ? baseClip.name : null,
                mode = entry.mode.ToString (),
                order = entry.order,
                mask = ChannelMaskFiles.ToGuid (entry.mask),
            };
            return ClipUserData.Save (path, data => data.overrideLayer = settings);
        }

        /// <summary>元から外す（ファイルは消さない。印だけ消す）</summary>
        public static void Unlink (AnimationClip clip)
        {
            string path = clip != null ? AssetDatabase.GetAssetPath (clip) : null;
            if (string.IsNullOrEmpty (path) || Read (path) == null) return;
            // 印だけ消す（Stacker の設定・サイクルキーの印は残す）
            ClipUserData.Save (path, data => data.overrideLayer = null);
        }

        static OverrideClip ToEntry (AnimationClip clip, Settings settings)
        {
            OverrideMode mode;
            if (!System.Enum.TryParse (settings.mode, out mode)) mode = OverrideMode.Additive;
            return new OverrideClip { clip = clip, mode = mode, order = settings.order, mask = ChannelMaskFiles.FromGuid (settings.mask) };
        }

        static Settings Read (string path)
        {
            Settings settings = ClipUserData.Load (path).overrideLayer;
            return settings != null && !string.IsNullOrEmpty (settings.baseClip) ? settings : null;
        }
    }

    /// <summary>
    /// 元のクリップの任意のプロパティ（Controls/Props/...）に Override の段を重ねたカーブ（S17）。
    /// 任意のプロパティは焼くときに解かずにカーブをそのまま写すので、段を重ねた値は焼き・統合の前にフレームごとに作る
    /// </summary>
    public static class OverrideProperties
    {
        /// <summary>重ねる段 1 つ（重みは段の重み）</summary>
        public struct Layer
        {
            public AnimationClip clip;
            public OverrideMode mode;
            public float weight;
            public ChannelMask mask;
        }

        /// <summary>段のどれかが、効く任意のプロパティのカーブを持つか</summary>
        public static bool Any (IList<Layer> layers)
        {
            if (layers == null) return false;
            foreach (Layer layer in layers) {
                if (layer.clip != null && layer.weight > 0 && OverrideApplier.GetPropertyCurves (layer.clip).Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 段が重ねるプロパティごとに、0 から frameCount - 1 フレームまで合成したカーブ（編集用クリップのバインディング）。
        /// 元にカーブが無い値は、表示モデルの元の値から重ねる。段が重ねないプロパティは返さない
        /// </summary>
        public static Dictionary<EditorCurveBinding, AnimationCurve> Compose (AnimationClip baseClip, IList<Layer> layers, PreviewStage stage, int frameCount, float rate)
        {
            Dictionary<EditorCurveBinding, AnimationCurve> result = new Dictionary<EditorCurveBinding, AnimationCurve> ();
            if (layers == null || stage == null || stage.animator == null || frameCount <= 0) return result;
            GameObject root = stage.animator.gameObject;
            // 重ねる値（段の順は問わず、最初に見つけた順）
            List<OverrideApplier.PropertyCurve> touched = new List<OverrideApplier.PropertyCurve> ();
            HashSet<EditorCurveBinding> seen = new HashSet<EditorCurveBinding> ();
            foreach (Layer layer in layers) {
                if (layer.clip == null || layer.weight <= 0) continue;
                foreach (OverrideApplier.PropertyCurve channel in OverrideApplier.GetPropertyCurves (layer.clip)) {
                    if (layer.mask != null && !stage.InMask (layer.mask, channel.clip)) continue;
                    if (seen.Add (channel.clip)) touched.Add (channel);
                }
            }
            foreach (OverrideApplier.PropertyCurve channel in touched) {
                // 土台が Humanoid のクリップなら、プロパティのカーブはゲーム prefab から見たパスにある（焼くとそのまま写る）
                AnimationCurve baseCurve = baseClip != null ? AnimationUtility.GetEditorCurve (baseClip, baseClip.humanMotion ? channel.display : channel.clip) : null;
                if (baseCurve != null && baseCurve.length == 0) baseCurve = null;
                float original = 0;
                if (baseCurve == null && !stage.TryGetPropertyOriginal (channel.display, out original)) continue;
                bool additive = PropertyPlayer.IsAdditive (root, channel.display);
                float[] values = new float[frameCount];
                for (int f = 0; f < frameCount; f++) {
                    float time = f / rate;
                    float value = baseCurve != null ? baseCurve.Evaluate (time) : original;
                    foreach (Layer layer in layers) {
                        if (layer.clip == null || layer.weight <= 0) continue;
                        if (layer.mask != null && !stage.InMask (layer.mask, channel.clip)) continue;
                        AnimationCurve curve = FindCurve (layer.clip, channel.clip);
                        if (curve == null || curve.length == 0) continue;
                        value = OverrideMath.ComposeFloat (layer.mode, additive, value, curve.Evaluate (time), Mathf.Clamp01 (layer.weight));
                    }
                    values[f] = value;
                }
                result[channel.clip] = Linear (values, rate);
            }
            return result;
        }

        /// <summary>
        /// 焼くときに任意のプロパティを読むクリップ（元のクリップの任意のプロパティのカーブに、段を重ねたカーブを上書きしたもの）。
        /// 段が任意のプロパティを持たなければ null（元のクリップをそのまま読めばよい）。使い終えたら呼ぶ側で消す
        /// </summary>
        public static AnimationClip CreateBakeSource (AnimationClip baseClip, IList<Layer> layers, PreviewStage stage)
        {
            if (baseClip == null || !Any (layers)) return null;
            float rate = CurveEdit.FrameRateOf (baseClip);
            // 焼く長さ（HumanoidBaker と同じ決め方）
            int frameCount = baseClip.empty ? 1 : CurveEdit.FrameCount (baseClip.length, rate);
            AnimationClip result = new AnimationClip { frameRate = rate, name = baseClip.name, hideFlags = HideFlags.HideAndDontSave };
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (baseClip)) {
                if (RigPaths.FromProps (binding.path) != null) AnimationUtility.SetEditorCurve (result, binding, AnimationUtility.GetEditorCurve (baseClip, binding));
            }
            foreach (KeyValuePair<EditorCurveBinding, AnimationCurve> pair in Compose (baseClip, layers, stage, frameCount, rate)) {
                AnimationUtility.SetEditorCurve (result, pair.Key, pair.Value);
            }
            return result;
        }

        static AnimationCurve FindCurve (AnimationClip clip, EditorCurveBinding binding)
        {
            foreach (OverrideApplier.PropertyCurve channel in OverrideApplier.GetPropertyCurves (clip)) {
                if (channel.clip == binding) return channel.curve;
            }
            return null;
        }

        /// <summary>フレームごとの値を、間を直線でつなぐカーブにする（統合の後の間引きで、直線の区間のキーが消える）</summary>
        static AnimationCurve Linear (float[] values, float rate)
        {
            Keyframe[] keys = new Keyframe[values.Length];
            for (int f = 0; f < values.Length; f++) {
                float left = f > 0 ? (values[f] - values[f - 1]) * rate : 0;
                float right = f + 1 < values.Length ? (values[f + 1] - values[f]) * rate : 0;
                if (f == 0) left = right;
                if (f + 1 == values.Length) right = left;
                keys[f] = new Keyframe (f / rate, values[f], left, right);
            }
            return new AnimationCurve (keys);
        }
    }

}
