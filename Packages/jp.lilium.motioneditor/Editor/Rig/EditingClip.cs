using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップ（編集用リグの操作値を持つクリップ）の見分け方。
    /// 規約はファイル名の「.rig」（例: Attack.rig.anim）。名前が違っても、編集用リグ（Controls/...）のカーブを持つか空なら編集用として扱う。
    /// 編集用リグのカーブが 1 本も無く、骨の名前などを指すカーブだけのクリップ（旧形式・ゲーム用）は編集できない（取り込みは S7）。
    /// 両方が混ざったクリップ（旧形式で作りかけて新しい形式でキーを打ったもの）は編集でき、残ったカーブは注意として出す（CountStrayCurves）
    /// </summary>
    public static class EditingClip
    {
        /// <summary>
        /// ファイル名の拡張子の前に付ける印（Attack.rig.anim の「.rig」）
        /// </summary>
        public const string kSuffix = ".rig";
        public const string kExtension = ".anim";

        /// <summary>
        /// 規約どおりの名前か（Attack.rig.anim）
        /// </summary>
        public static bool HasEditingName (AnimationClip clip)
        {
            return IsEditingClipPath (clip != null ? AssetDatabase.GetAssetPath (clip) : null);
        }

        /// <summary>
        /// 規約どおりのパスか（〜.rig.anim）
        /// </summary>
        public static bool IsEditingClipPath (string path)
        {
            return !string.IsNullOrEmpty (path) && path.EndsWith (kSuffix + kExtension, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 新しく作るクリップの既定のファイル名（拡張子なし。SaveFilePanel が .anim を付ける）
        /// </summary>
        public static string DefaultFileName (string baseName)
        {
            return (string.IsNullOrEmpty (baseName) ? "New Motion" : baseName) + kSuffix;
        }

        static readonly System.Collections.Generic.Dictionary<AnimationClip, int> ikCurveCounts_ = new System.Collections.Generic.Dictionary<AnimationClip, int> ();
        static bool ikCurveCountsHooked_;

        /// <summary>
        /// IK の組のカーブ（Controls/IK/...）の数。全身 IK の定義では使われない（S25。消さずに残す）。
        /// 画面を描くたびに呼ばれるので、クリップごとに覚えておき、カーブが書き換わったら数え直す
        /// </summary>
        public static int CountIkCurves (AnimationClip clip)
        {
            if (clip == null) return 0;
            if (!ikCurveCountsHooked_) {
                ikCurveCountsHooked_ = true;
                AnimationUtility.onCurveWasModified += (modified, binding, type) => ikCurveCounts_.Remove (modified);
                Undo.undoRedoPerformed += () => ikCurveCounts_.Clear ();
            }
            int count;
            if (ikCurveCounts_.TryGetValue (clip, out count)) return count;
            count = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (binding.path.StartsWith (RigPaths.kIk + "/")) count++;
            }
            ikCurveCounts_[clip] = count;
            return count;
        }

        /// <summary>全身 IK の点 1 つの、状態のキー（フレームの昇順）。値の並びは BodyPoint.kProperties</summary>
        sealed class PointKeys
        {
            public readonly System.Collections.Generic.List<int>[] frames = new System.Collections.Generic.List<int>[Lilium.BodyPoint.kValueCount];
            public readonly System.Collections.Generic.List<float>[] values = new System.Collections.Generic.List<float>[Lilium.BodyPoint.kValueCount];
        }

        static readonly System.Collections.Generic.Dictionary<AnimationClip, System.Collections.Generic.Dictionary<string, PointKeys>> pointKeys_ = new System.Collections.Generic.Dictionary<AnimationClip, System.Collections.Generic.Dictionary<string, PointKeys>> ();
        static bool pointKeysHooked_;

        static System.Collections.Generic.Dictionary<string, PointKeys> GetPointKeys (AnimationClip clip)
        {
            if (!pointKeysHooked_) {
                pointKeysHooked_ = true;
                AnimationUtility.onCurveWasModified += (modified, binding, type) => pointKeys_.Remove (modified);
                Undo.undoRedoPerformed += () => pointKeys_.Clear ();
            }
            System.Collections.Generic.Dictionary<string, PointKeys> points;
            if (pointKeys_.TryGetValue (clip, out points)) return points;
            points = new System.Collections.Generic.Dictionary<string, PointKeys> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (binding.type != typeof (Lilium.BodyPoint)) continue;
                int index = Lilium.BodyPoint.IndexOf (binding.propertyName);
                AnimationCurve curve = index >= 0 ? AnimationUtility.GetEditorCurve (clip, binding) : null;
                if (curve == null) continue;
                PointKeys keys;
                if (!points.TryGetValue (binding.path, out keys)) points.Add (binding.path, keys = new PointKeys ());
                keys.frames[index] = new System.Collections.Generic.List<int> ();
                keys.values[index] = new System.Collections.Generic.List<float> ();
                foreach (Keyframe key in curve.keys) {
                    keys.frames[index].Add (Mathf.RoundToInt (key.time * clip.frameRate));
                    keys.values[index].Add (key.value);
                }
            }
            pointKeys_[clip] = points;
            return points;
        }

        /// <summary>
        /// 全身 IK の点（Controls/Body/...）の、その時刻の状態（S25）。
        /// 状態（Pin / Locked）は、次にその点のキーを打つまで引き継ぐ（IK / FK の切り替えと同じ）。キーの間で補間はしない:
        /// 値は、その時刻以前でいちばん近いキーの値（最初のキーより前は 0 = Free）。
        /// keyed は、その時刻ちょうどに状態のキーがあるか（あれば、点の位置と向きもそのキーの値を使う。無ければ、その時刻の骨の位置に置く）。
        /// 画面を描くたびに呼ばれるので、クリップごとに覚えておき、カーブが書き換わったら作り直す
        /// </summary>
        /// <param name="values">BodyPoint.kProperties の並び（kValueCount 個）</param>
        public static void GetPointState (AnimationClip clip, string path, float time, float[] values, out bool keyed)
        {
            keyed = false;
            for (int v = 0; v < values.Length; v++) values[v] = 0;
            if (clip == null || string.IsNullOrEmpty (path)) return;
            PointKeys keys;
            if (!GetPointKeys (clip).TryGetValue (path, out keys)) return;
            float position = time * clip.frameRate;
            int frame = Mathf.FloorToInt (position + 0.01f);
            bool exact = Mathf.Abs (position - Mathf.RoundToInt (position)) <= 0.01f;
            for (int v = 0; v < values.Length && v < keys.frames.Length; v++) {
                System.Collections.Generic.List<int> frames = keys.frames[v];
                if (frames == null) continue;
                for (int k = frames.Count - 1; k >= 0; k--) {
                    if (frames[k] > frame) continue;
                    values[v] = keys.values[v][k];
                    if (exact && frames[k] == frame) keyed = true;
                    break;
                }
            }
        }

        /// <summary>
        /// キーの間で全身 IK を解く点か（S25d）: その時刻ちょうどの状態のキーは無く、前のキーから Locked が続いていて、
        /// 次の状態のキーでも Locked のまま（次のキーが無ければ、そのまま続く）。
        /// こういう点は、キーの間でも位置のカーブの補間した場所に留める（前後で同じ場所なら、その場から動かない）。
        /// Pin の点や、次のキーで Locked が外れる点は、キーの間では骨に付いて動く（解かない）
        /// </summary>
        public static bool IsLockedSpan (AnimationClip clip, string path, float time)
        {
            if (clip == null || string.IsNullOrEmpty (path)) return false;
            PointKeys keys;
            if (!GetPointKeys (clip).TryGetValue (path, out keys)) return false;
            float position = time * clip.frameRate;
            if (Mathf.Abs (position - Mathf.RoundToInt (position)) <= 0.01f) {
                // ちょうどキーのあるフレームは、キーの姿勢そのもの（解いた姿勢が骨のキーに入っている）
                int at = Mathf.RoundToInt (position);
                foreach (System.Collections.Generic.List<int> frames in keys.frames) {
                    if (frames != null && frames.Contains (at)) return false;
                }
            }
            int frame = Mathf.FloorToInt (position + 0.01f);
            int locked = Lilium.BodyPoint.IndexOf (Lilium.BodyPoint.kLockedProperty);
            int weight = Lilium.BodyPoint.IndexOf (Lilium.BodyPoint.kPositionWeightProperty);
            if (StepValue (keys, locked, frame) < 0.5f || StepValue (keys, weight, frame) <= 0) return false;
            // 次の状態のキー
            int next = int.MaxValue;
            foreach (System.Collections.Generic.List<int> frames in keys.frames) {
                if (frames == null) continue;
                foreach (int f in frames) {
                    if (f > frame && f < next) next = f;
                }
            }
            if (next == int.MaxValue) return true;
            return StepValue (keys, locked, next) >= 0.5f && StepValue (keys, weight, next) > 0;
        }

        /// <summary>
        /// 点の状態のキーのある前後のフレーム。previous はその時刻以前でいちばん近いキー（無ければ -1）、
        /// next はその時刻より後でいちばん近いキー（無ければ -1）
        /// </summary>
        public static void GetPointKeyBounds (AnimationClip clip, string path, float time, out int previous, out int next)
        {
            previous = -1;
            next = -1;
            if (clip == null || string.IsNullOrEmpty (path)) return;
            PointKeys keys;
            if (!GetPointKeys (clip).TryGetValue (path, out keys)) return;
            int frame = Mathf.FloorToInt (time * clip.frameRate + 0.01f);
            foreach (System.Collections.Generic.List<int> frames in keys.frames) {
                if (frames == null) continue;
                foreach (int f in frames) {
                    if (f <= frame) previous = Mathf.Max (previous, f);
                    else if (next < 0 || f < next) next = f;
                }
            }
        }

        /// <summary>
        /// Locked のキーを 1 つでも持つ点の、状態のキーのあるフレーム（昇順・重なりなし。どの点のキーかは問わない）。
        /// キーの間で Locked の点を解くときの、前後のキーはこの中にある
        /// </summary>
        public static System.Collections.Generic.List<int> GetLockedPointKeyFrames (AnimationClip clip)
        {
            System.Collections.Generic.SortedSet<int> result = new System.Collections.Generic.SortedSet<int> ();
            if (clip == null) return new System.Collections.Generic.List<int> ();
            int locked = Lilium.BodyPoint.IndexOf (Lilium.BodyPoint.kLockedProperty);
            foreach (PointKeys keys in GetPointKeys (clip).Values) {
                System.Collections.Generic.List<float> values = locked >= 0 && locked < keys.values.Length ? keys.values[locked] : null;
                if (values == null || !values.Exists (value => value >= 0.5f)) continue;
                foreach (System.Collections.Generic.List<int> frames in keys.frames) {
                    if (frames != null) result.UnionWith (frames);
                }
            }
            return new System.Collections.Generic.List<int> (result);
        }

        static readonly System.Collections.Generic.Dictionary<AnimationClip, int> curveVersions_ = new System.Collections.Generic.Dictionary<AnimationClip, int> ();
        static int undoVersion_;
        static bool curveVersionsHooked_;

        /// <summary>
        /// クリップのカーブが書き換わるたびに変わる数（Undo / Redo でも変わる）。クリップから作った値を覚えておくときの目印
        /// </summary>
        public static int GetCurveVersion (AnimationClip clip)
        {
            if (!curveVersionsHooked_) {
                curveVersionsHooked_ = true;
                AnimationUtility.onCurveWasModified += (modified, binding, type) => {
                    int version;
                    curveVersions_.TryGetValue (modified, out version);
                    curveVersions_[modified] = version + 1;
                };
                Undo.undoRedoPerformed += () => undoVersion_++;
            }
            int count;
            if (clip == null || !curveVersions_.TryGetValue (clip, out count)) count = 0;
            return count * 7919 + undoVersion_;
        }

        /// <summary>状態の値 v の、frame 以前でいちばん近いキーの値（無ければ 0）</summary>
        static float StepValue (PointKeys keys, int v, int frame)
        {
            System.Collections.Generic.List<int> frames = v >= 0 && v < keys.frames.Length ? keys.frames[v] : null;
            if (frames == null) return 0;
            for (int k = frames.Count - 1; k >= 0; k--) {
                if (frames[k] <= frame) return keys.values[v][k];
            }
            return 0;
        }

        /// <summary>その時刻ちょうどに、点の状態のキーがあるか</summary>
        public static bool HasPointKey (AnimationClip clip, string path, float time)
        {
            float[] values = new float[Lilium.BodyPoint.kValueCount];
            bool keyed;
            GetPointState (clip, path, time, values, out keyed);
            return keyed;
        }

        /// <summary>
        /// 編集用リグのカーブか（パスが Controls の下）
        /// </summary>
        public static bool IsRigBinding (EditorCurveBinding binding)
        {
            return binding.path == RigPaths.kRoot || binding.path.StartsWith (RigPaths.kRoot + "/");
        }

        /// <summary>
        /// このクリップを編集用として開けない理由。開けるなら null
        /// </summary>
        public static string GetProblem (AnimationClip clip)
        {
            if (clip == null) return null;
            // Humanoid のクリップは読み取り専用の土台として開く（書き込みは Override へ。取込で編集用クリップにできる。S20）
            if (clip.humanMotion) {
                return "Humanoid のクリップ（読み取り専用）。キーは Override へ打つか、取込で編集用クリップにする";
            }
            if (AssetImporter.GetAtPath (AssetDatabase.GetAssetPath (clip)) is ModelImporter) {
                return "FBX の中のクリップは書き換えられない";
            }

            int rig = 0;
            int other = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (IsRigBinding (binding)) rig++;
                else other++;
            }
            if (rig == 0 && other > 0) {
                return "編集用リグのカーブが無い（骨などを指すカーブが " + other + " 本）。旧形式のクリップは開いても姿勢に出ない。取り込みは今後（S7）";
            }
            return null;
        }

        /// <summary>
        /// 編集用リグ以外を指すカーブの数（編集できるクリップに残っている旧形式のカーブ。姿勢には効かない）
        /// </summary>
        public static int CountStrayCurves (AnimationClip clip)
        {
            if (clip == null) return 0;
            int count = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!IsRigBinding (binding)) count++;
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (clip)) {
                if (!IsRigBinding (binding)) count++;
            }
            return count;
        }

        /// <summary>
        /// 編集用リグ以外を指すカーブを消す（Undo の記録は呼ぶ側）
        /// </summary>
        public static int RemoveStrayCurves (AnimationClip clip)
        {
            if (clip == null) return 0;
            int removed = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (IsRigBinding (binding)) continue;
                AnimationUtility.SetEditorCurve (clip, binding, null);
                removed++;
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings (clip)) {
                if (IsRigBinding (binding)) continue;
                AnimationUtility.SetObjectReferenceCurve (clip, binding, null);
                removed++;
            }
            return removed;
        }
    }

}
