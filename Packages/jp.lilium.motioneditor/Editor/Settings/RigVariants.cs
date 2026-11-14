using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// キャラ＋バリアントで使うリグの組み（S92 の決めたこと 12）。編集用リグの定義と持つ物の一覧を、窓・PoseSession・焼きが同じ物で受け取る。
    /// 一覧は元の項目（キャラの設定・バリアントの中の物）の参照を返す（複製すると、リグエディタの一覧が元の項目を IndexOf で引けなくなる）
    /// </summary>
    public sealed class RigSetup
    {
        static readonly IReadOnlyList<HeldObjectDefinition> kNone = System.Array.Empty<HeldObjectDefinition> ();

        /// <param name="definition">編集用リグの定義。null なら PreviewStage が既定の定義を一時の物として作る</param>
        /// <param name="heldObjects">持つ物（null なら持たない）</param>
        public RigSetup (EditRigDefinition definition, IReadOnlyList<HeldObjectDefinition> heldObjects = null, string variantName = null,
            bool missing = false, CharacterSettings settings = null, RigVariant variant = null)
        {
            this.definition = definition;
            this.heldObjects = heldObjects ?? kNone;
            this.variantName = variantName ?? "";
            this.missing = missing;
            this.settings = settings;
            this.variant = variant;
            heldObjectsKey = MakeHeldObjectsKey (this.heldObjects);
        }

        /// <summary>編集用リグの定義</summary>
        public EditRigDefinition definition { get; private set; }

        /// <summary>持つ物（ベースの物 + バリアントの物）</summary>
        public IReadOnlyList<HeldObjectDefinition> heldObjects { get; private set; }

        /// <summary>バリアントの名前（空はベース）</summary>
        public string variantName { get; private set; }

        /// <summary>名前のバリアントがキャラに無い（ベースの物で開く。キーを打つ・焼くは断る）</summary>
        public bool missing { get; private set; }

        /// <summary>ベースのキャラの設定（無ければ null）</summary>
        public CharacterSettings settings { get; private set; }

        /// <summary>バリアントのアセット（ベース・無いときは null）</summary>
        public RigVariant variant { get; private set; }

        /// <summary>持つ物の中身を表す文字列（焼いた版の判定・窓の作り直しの判定）</summary>
        public string heldObjectsKey { get; private set; }

        /// <summary>
        /// 世界を作り直すかの判定のキー。定義と持つ物が同じなら同じ世界でよい（名前だけ違うバリアント・無いバリアントとベースも同じ世界）
        /// </summary>
        public string key
        {
            get { return (definition != null ? definition.GetInstanceID () : 0) + "|" + heldObjectsKey; }
        }

        /// <summary>持つ物の中身を表す文字列。CharacterSettings.GetHeldObjectsKey と同じ形（持ち物の無いキャラとベースのクリップの焼いた版を古い扱いにしない）</summary>
        public static string MakeHeldObjectsKey (IEnumerable<HeldObjectDefinition> heldObjects)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder ();
            if (heldObjects != null) {
                foreach (HeldObjectDefinition definition in heldObjects) text.Append (JsonUtility.ToJson (definition)).Append ('|');
            }
            return text.ToString ();
        }
    }

    /// <summary>
    /// リグのバリアントの引き当てとクリップの印（S92）。バリアントはキャラ（prefab の GUID）と名前で引く。
    /// 見つけた結果は覚えておき、プロジェクトのファイルが変わったら捨てる（SettingsLookup と同じ）
    /// </summary>
    public static class RigVariants
    {
        static Dictionary<string, List<RigVariant>> byPrefab_;
        static readonly HashSet<string> warned_ = new HashSet<string> ();

        [InitializeOnLoadMethod]
        static void Register ()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.projectChanged += Invalidate;
        }

        /// <summary>バリアントを探し直す（プロジェクトのファイルが変わったとき・バリアントを直したとき）</summary>
        public static void Invalidate ()
        {
            byPrefab_ = null;
        }

        /// <summary>このキャラのバリアント（パスの順。同じ名前が 2 つあれば先のものだけ）</summary>
        public static IReadOnlyList<RigVariant> FindAll (GameObject prefab)
        {
            string guid = SettingsLookup.PrefabGuid (prefab);
            if (guid == null) return System.Array.Empty<RigVariant> ();
            if (byPrefab_ == null) Collect ();
            List<RigVariant> variants;
            return byPrefab_.TryGetValue (guid, out variants) ? variants : (IReadOnlyList<RigVariant>)System.Array.Empty<RigVariant> ();
        }

        /// <summary>このキャラの、その名前のバリアント（空の名前・無ければ null）</summary>
        public static RigVariant Find (GameObject prefab, string variantName)
        {
            if (string.IsNullOrEmpty (variantName)) return null;
            foreach (RigVariant variant in FindAll (prefab)) {
                if (variant != null && variant.variantName == variantName) return variant;
            }
            return null;
        }

        static void Collect ()
        {
            // 作り終えてから置く。初めて読み込むバリアントは OnValidate で Invalidate を呼ぶので、作りかけを置くと途中で null に戻される
            Dictionary<string, List<RigVariant>> byPrefab = new Dictionary<string, List<RigVariant>> ();
            List<string> paths = new List<string> ();
            foreach (string guid in AssetDatabase.FindAssets ("t:" + typeof (RigVariant).Name)) paths.Add (AssetDatabase.GUIDToAssetPath (guid));
            paths.Sort (System.StringComparer.Ordinal);
            foreach (string path in paths) {
                RigVariant variant = AssetDatabase.LoadAssetAtPath<RigVariant> (path);
                string prefab = variant != null ? SettingsLookup.PrefabGuid (variant.prefab) : null;
                // 名前の無いバリアントはベースと見分けられないので使わない
                if (prefab == null || string.IsNullOrEmpty (variant.variantName)) continue;
                List<RigVariant> list;
                if (!byPrefab.TryGetValue (prefab, out list)) byPrefab.Add (prefab, list = new List<RigVariant> ());
                RigVariant first = list.Find (v => v.variantName == variant.variantName);
                if (first != null) {
                    if (warned_.Add (path)) {
                        Debug.LogWarning (Tr ("RIG_VARIANTS_DUPLICATE", variant.prefab.name, variant.variantName, AssetDatabase.GetAssetPath (first), path), variant);
                    }
                    continue;
                }
                list.Add (variant);
            }
            byPrefab_ = byPrefab;
        }

        /// <summary>
        /// キャラとバリアントの名前から、使うリグの組みを作る。定義はキャラの設定（無ければプロジェクト設定）、持つ物はキャラの設定の物 + バリアントの物。
        /// 名前のバリアントが無ければ、ベースの物で開き missing にする
        /// </summary>
        public static RigSetup Resolve (GameObject prefab, string variantName)
        {
            CharacterSettings settings = prefab != null ? SettingsLookup.Find (prefab) : null;
            return Resolve (prefab, variantName, RigDefinitionAssets.Resolve (settings));
        }

        /// <summary>定義を決めて作る（PoseSession・PreviewStage を定義つきで作るとき。持つ物は prefab から引く）</summary>
        public static RigSetup Resolve (GameObject prefab, string variantName, EditRigDefinition definition)
        {
            CharacterSettings settings = prefab != null ? SettingsLookup.Find (prefab) : null;
            List<HeldObjectDefinition> held = new List<HeldObjectDefinition> ();
            // ベースの物はそのまま並べる（空の項目も。ベースのクリップの焼いた版の判定の値を、バリアントを入れる前と同じにする）
            if (settings != null && settings.heldObjects != null) held.AddRange (settings.heldObjects);
            string name = variantName ?? "";
            RigVariant variant = Find (prefab, name);
            if (variant != null && variant.heldObjects != null) {
                foreach (HeldObjectDefinition item in variant.heldObjects) {
                    if (item == null) continue;
                    // 同じ名前はバリアントの物を丸ごと使う。並びはベースの位置のまま（物の順でクリップの意味は変わらないが、一覧の順を保つ）
                    int index = held.FindIndex (h => h != null && h.name == item.name);
                    if (index >= 0) held[index] = item;
                    else held.Add (item);
                }
            }
            bool missing = name.Length > 0 && variant == null;
            return new RigSetup (definition, held, name, missing, settings, variant);
        }

        /// <summary>このキャラで clip を開くときのリグの組み（clip の印のバリアント。印が無い・Humanoid のクリップはベース）</summary>
        public static RigSetup Resolve (GameObject prefab, AnimationClip clip)
        {
            return Resolve (prefab, GetClipVariant (clip));
        }

        /// <summary>
        /// clip の印のバリアントの名前（空はベース）。印を持つのは編集用クリップだけで、Humanoid の土台（FBX・焼いた版）はいつもベースで開く（決めたこと 10）
        /// </summary>
        public static string GetClipVariant (AnimationClip clip)
        {
            if (!CanHoldMark (clip)) return "";
            return ClipUserData.Load (clip).rigVariant ?? "";
        }

        /// <summary>
        /// clip に印を書く（空はベース。印を消す）。Humanoid の土台（FBX・焼いた版）とアセットでないクリップには書かない（false）
        /// </summary>
        public static bool SetClipVariant (AnimationClip clip, string variantName)
        {
            if (!CanHoldMark (clip)) return false;
            string name = variantName ?? "";
            return ClipUserData.Save (clip, data => data.rigVariant = name.Length > 0 ? name : null);
        }

        /// <summary>印を持てるクリップか（アセットの .anim で、Humanoid でなく、焼いた版でない）</summary>
        static bool CanHoldMark (AnimationClip clip)
        {
            if (clip == null || clip.humanMotion) return false;
            string path = AssetDatabase.GetAssetPath (clip);
            if (string.IsNullOrEmpty (path) || AssetDatabase.IsSubAsset (clip)) return false;
            if (!path.EndsWith (EditingClip.kExtension, System.StringComparison.OrdinalIgnoreCase)) return false;
            return HumanoidOutput.ReadLink (path) == null;
        }

        /// <summary>
        /// clip を setup のリグで書けない理由（書けるなら null。見ることはできる。決めたこと 11）。
        /// 印のバリアントがキャラに無い・印と setup のバリアントが違う・setup に無い物のカーブ（Controls/Object/物の名前）がある
        /// </summary>
        public static string GetClipProblem (AnimationClip clip, RigSetup setup)
        {
            if (clip == null || setup == null || clip.humanMotion) return null;
            string mark = GetClipVariant (clip);
            if (setup.missing && setup.variantName == mark) {
                return Tr ("RIG_VARIANTS_MISSING", mark);
            }
            if (mark != setup.variantName) {
                return Tr ("RIG_VARIANTS_OTHER_VARIANT", DisplayName (mark), DisplayName (setup.variantName));
            }
            string unknown = FindUnknownObject (clip, setup.heldObjects);
            return unknown != null ? Tr ("RIG_VARIANTS_UNKNOWN_OBJECT", unknown) : null;
        }

        /// <summary>画面に出すバリアントの名前（空はベース）</summary>
        public static string DisplayName (string variantName)
        {
            return string.IsNullOrEmpty (variantName) ? Tr ("RIG_VARIANTS_BASE") : variantName;
        }

        /// <summary>
        /// clip にある、持つ物に無い物のキーの物の名前（無ければ null）。キーの無いカーブ（物を外したときの名残）は姿勢に効かないので数えない
        /// </summary>
        static string FindUnknownObject (AnimationClip clip, IReadOnlyList<HeldObjectDefinition> heldObjects)
        {
            string prefix = RigPaths.kObject + "/";
            HashSet<string> names = HeldNames (heldObjects);
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                string name = ObjectName (binding.path, prefix);
                if (string.IsNullOrEmpty (name) || names.Contains (name)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve != null && curve.length > 0) return name;
            }
            return null;
        }

        /// <summary>
        /// clip にある、setup のリグが持たない物のカーブ（Controls/Object/物の名前・Controls/ObjectPose/物の名前。キーの無いカーブも。S92b）。
        /// 印のバリアントが setup と合わないクリップは空（物のカーブを消しても直らない。バリアントを選び直す）
        /// </summary>
        public static List<EditorCurveBinding> FindUnknownObjectCurves (AnimationClip clip, RigSetup setup)
        {
            List<EditorCurveBinding> result = new List<EditorCurveBinding> ();
            if (clip == null || setup == null || clip.humanMotion) return result;
            if (setup.missing || GetClipVariant (clip) != setup.variantName) return result;
            HashSet<string> names = HeldNames (setup.heldObjects);
            string objectPrefix = RigPaths.kObject + "/";
            string posePrefix = RigPaths.ObjectPose ("");
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (IsUnknown (binding.path, names, objectPrefix, posePrefix)) result.Add (binding);
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (clip)) {
                if (IsUnknown (binding.path, names, objectPrefix, posePrefix)) result.Add (binding);
            }
            return result;
        }

        /// <summary>
        /// setup のリグが持たない物のカーブを消す（S92b の物のカーブを消すボタン。Undo の記録は呼ぶ側）。消した数を返す
        /// </summary>
        public static int RemoveUnknownObjectCurves (AnimationClip clip, RigSetup setup)
        {
            List<EditorCurveBinding> bindings = FindUnknownObjectCurves (clip, setup);
            foreach (EditorCurveBinding binding in bindings) {
                if (binding.isPPtrCurve) AnimationUtility.SetObjectReferenceCurve (clip, binding, null);
                else AnimationUtility.SetEditorCurve (clip, binding, null);
            }
            return bindings.Count;
        }

        /// <summary>カーブの物の名前（並びは見つけた順・重複なし）。確かめの文に出す</summary>
        public static List<string> ObjectNames (IEnumerable<EditorCurveBinding> bindings)
        {
            List<string> result = new List<string> ();
            string objectPrefix = RigPaths.kObject + "/";
            string posePrefix = RigPaths.ObjectPose ("");
            foreach (EditorCurveBinding binding in bindings) {
                string name = ObjectName (binding.path, objectPrefix) ?? ObjectName (binding.path, posePrefix);
                if (!string.IsNullOrEmpty (name) && !result.Contains (name)) result.Add (name);
            }
            return result;
        }

        static HashSet<string> HeldNames (IReadOnlyList<HeldObjectDefinition> heldObjects)
        {
            HashSet<string> names = new HashSet<string> ();
            foreach (HeldObjectDefinition held in heldObjects) {
                if (held != null && !string.IsNullOrEmpty (held.name)) names.Add (held.name);
            }
            return names;
        }

        static bool IsUnknown (string path, HashSet<string> names, string objectPrefix, string posePrefix)
        {
            string name = ObjectName (path, objectPrefix) ?? ObjectName (path, posePrefix);
            return !string.IsNullOrEmpty (name) && !names.Contains (name);
        }

        /// <summary>prefix（Controls/Object/ など）の下のパスなら、その次の名前（物の名前）。違えば null</summary>
        static string ObjectName (string path, string prefix)
        {
            if (path == null || !path.StartsWith (prefix, System.StringComparison.Ordinal)) return null;
            string rest = path.Substring (prefix.Length);
            int slash = rest.IndexOf ('/');
            return slash >= 0 ? rest.Substring (0, slash) : rest;
        }
    }

}
