using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// キーの書き出し口。クリップへキーを打つところはすべてこれを通す。
    /// 始めるときに Undo を記録し（アセットのクリップだけ）、値を貯めて、終わるとき（Dispose）にまとめて書く。
    /// Transform のほか、コンポーネントの数値（float）も受ける。
    /// 回転（四元数）は、同じカーブの隣のキーと符号をそろえてから書く（符号が飛ぶと補間が遠回りになる）
    /// </summary>
    public sealed class CurveWriter : System.IDisposable
    {
        const string kPosition = "m_LocalPosition";
        const string kRotation = "m_LocalRotation";
        const string kScale = "m_LocalScale";
        static readonly string[] kAxes = { "x", "y", "z", "w" };

        readonly AnimationClip clip_;
        readonly Transform root_;
        readonly float frame_;
        readonly Dictionary<EditorCurveBinding, float> values_ = new Dictionary<EditorCurveBinding, float> ();
        readonly List<string> rotationPaths_ = new List<string> ();
        /// <summary>貼り付けなどでクリップの値をそのまま入れたカーブ（Override の変換を掛けない）</summary>
        readonly HashSet<EditorCurveBinding> raw_ = new HashSet<EditorCurveBinding> ();
        bool committed_;

        /// <summary>
        /// 書く前に、Transform の値（狙いの値。パスはルートから）をクリップに入れる値へ直す（Override の差分。S14）。null なら直さない。
        /// 数値のカーブは storeFloat で直す（無ければそのまま書く）
        /// </summary>
        public System.Func<string, Vector3, Vector3> storePosition;
        public System.Func<string, Quaternion, Quaternion> storeRotation;
        public System.Func<string, Vector3, Vector3> storeScale;
        /// <summary>数値の狙いの値をクリップに入れる値へ直す（足の転がしの角度の差分など）。貼り付けた値（Set）には掛けない</summary>
        public System.Func<EditorCurveBinding, float, float> storeFloat;

        /// <summary>
        /// 今のフレームにキーがある物（パス = 骨・コントローラー 1 つ）のカーブだけ書き、キーの無い物には打たない（キーの自動追加が切のときの操作）。
        /// 物の単位で見るので、キーのある物ならカーブの無いチャンネル（腰の位置など）も書く
        /// </summary>
        public bool onlyExistingKeys;

        /// <summary>onlyExistingKeys で打たなかったカーブの数（書き終えた後に読む）</summary>
        public int skippedCount { get; private set; }

        /// <summary>書いたカーブの数（書き終えた後に読む）</summary>
        public int writtenCount { get; private set; }

        CurveWriter (AnimationClip clip, Transform root, float frame)
        {
            clip_ = clip;
            root_ = root;
            frame_ = frame;
        }

        /// <param name="root">カーブのパスの基準（編集用の体のルート）</param>
        /// <param name="undoName">Undo に出す名前</param>
        public static CurveWriter Begin (AnimationClip clip, Transform root, float frame, string undoName)
        {
            // アセットでないクリップ（メモリ上だけの物）を記録すると、どのシーンにも属さない物として開いているシーンが変更扱いになる。
            // 名前が無いときは記録しない（統合のように、外で 1 つの Undo にまとめて何フレームも書くとき）
            if (clip != null && !string.IsNullOrEmpty (undoName) && EditorUtility.IsPersistent (clip)) Undo.RecordObject (clip, undoName);
            return new CurveWriter (clip, root, frame);
        }

        public AnimationClip clip
        {
            get { return clip_; }
        }

        public float frame
        {
            get { return frame_; }
        }

        /// <summary>
        /// 貯めている値の数
        /// </summary>
        public int count
        {
            get { return values_.Count; }
        }

        /// <summary>
        /// 貯めている値（クリップを渡さずに始めて、値だけを集めるとき）
        /// </summary>
        internal IReadOnlyDictionary<EditorCurveBinding, float> values
        {
            get { return values_; }
        }

        /// <summary>貯めている値のうち回転（四元数）のパス</summary>
        internal IReadOnlyList<string> rotationPaths
        {
            get { return rotationPaths_; }
        }

        public string PathOf (Transform t)
        {
            return AnimationUtility.CalculateTransformPath (t, root_);
        }

        /// <summary>
        /// Transform の今のローカル値を打つ
        /// </summary>
        public void Transform (Transform t, TransformChannels channels)
        {
            if (t == null) return;
            string path = PathOf (t);
            if ((channels & TransformChannels.Position) != 0) {
                Vector3 p = t.localPosition;
                SetTransform (path, kPosition + ".x", p.x);
                SetTransform (path, kPosition + ".y", p.y);
                SetTransform (path, kPosition + ".z", p.z);
            }
            if ((channels & TransformChannels.Rotation) != 0) {
                Quaternion q = t.localRotation;
                SetTransform (path, kRotation + ".x", q.x);
                SetTransform (path, kRotation + ".y", q.y);
                SetTransform (path, kRotation + ".z", q.z);
                SetTransform (path, kRotation + ".w", q.w);
                if (!rotationPaths_.Contains (path)) rotationPaths_.Add (path);
            }
            if ((channels & TransformChannels.Scale) != 0) {
                Vector3 s = t.localScale;
                SetTransform (path, kScale + ".x", s.x);
                SetTransform (path, kScale + ".y", s.y);
                SetTransform (path, kScale + ".z", s.z);
            }
        }

        void SetTransform (string path, string property, float value)
        {
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve (path, typeof (Transform), property);
            values_[binding] = value;
            raw_.Remove (binding);
        }

        /// <summary>
        /// コンポーネントの数値を打つ（float の受け口）
        /// </summary>
        public void Float (Transform owner, System.Type componentType, string property, float value)
        {
            if (owner == null) return;
            values_[EditorCurveBinding.FloatCurve (PathOf (owner), componentType, property)] = value;
        }

        /// <summary>
        /// カーブを直接指定して打つ（貼り付けなど）。回転の成分なら、符号をそろえる対象に入れる
        /// </summary>
        public void Set (EditorCurveBinding binding, float value)
        {
            values_[binding] = value;
            raw_.Add (binding);
            if (binding.type == typeof (Transform) && binding.propertyName.StartsWith (kRotation + ".") && !rotationPaths_.Contains (binding.path)) {
                rotationPaths_.Add (binding.path);
            }
        }

        /// <summary>
        /// 画面に出したい値（狙いの値）をカーブを指定して打つ（PoseBank など）。Set と違い、Transform は Transform () と同じく
        /// Override の変換を掛ける。数値のカーブはそのまま
        /// </summary>
        public void SetTarget (EditorCurveBinding binding, float value)
        {
            if (binding.type != typeof (Transform)) {
                values_[binding] = value;
                return;
            }
            SetTransform (binding.path, binding.propertyName, value);
            if (binding.propertyName.StartsWith (kRotation + ".") && !rotationPaths_.Contains (binding.path)) {
                rotationPaths_.Add (binding.path);
            }
        }

        public void Dispose ()
        {
            if (committed_) return;
            committed_ = true;
            if (clip_ == null || values_.Count == 0) return;

            ConvertTransforms ();
            AlignRotationSigns ();

            int frame = Mathf.RoundToInt (frame_);
            float time = frame / clip_.frameRate;
            HashSet<string> keyedPaths = onlyExistingKeys ? KeyedPathsAt (frame) : null;
            List<EditorCurveBinding> bindings = new List<EditorCurveBinding> (values_.Count);
            List<AnimationCurve> curves = new List<AnimationCurve> (values_.Count);
            foreach (KeyValuePair<EditorCurveBinding, float> pair in values_) {
                if (keyedPaths != null && !keyedPaths.Contains (pair.Key.path)) {
                    skippedCount++;
                    continue;
                }
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, pair.Key) ?? new AnimationCurve ();
                int existing = ClipKeyUtility.FindKeyAtFrame (curve, clip_.frameRate, frame);
                if (existing >= 0) {
                    // 接線の種類は保つ（Constant など）
                    Keyframe key = curve[existing];
                    key.value = pair.Value;
                    curve.MoveKey (existing, key);
                }
                else {
                    curve.AddKey (new Keyframe (time, pair.Value));
                }
                // 打ったキーと両隣の接線を付け直す（打ったときの両隣で固定すると、後でキーの間が空いたときに大きく行き過ぎる）
                CurveEdit.AutoTangentsNear (curve, time);
                bindings.Add (pair.Key);
                curves.Add (curve);
            }
            writtenCount = bindings.Count;
            if (bindings.Count > 0) AnimationUtility.SetEditorCurves (clip_, bindings.ToArray (), curves.ToArray ());
        }

        /// <summary>
        /// 書こうとしている物（パス）のうち、クリップのどれかのカーブに frame のキーがあるもの
        /// </summary>
        HashSet<string> KeyedPathsAt (int frame)
        {
            HashSet<string> paths = new HashSet<string> ();
            foreach (EditorCurveBinding binding in values_.Keys) paths.Add (binding.path);
            HashSet<string> keyed = new HashSet<string> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip_)) {
                if (!paths.Contains (binding.path) || keyed.Contains (binding.path)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, binding);
                if (curve != null && ClipKeyUtility.FindKeyAtFrame (curve, clip_.frameRate, frame) >= 0) keyed.Add (binding.path);
            }
            return keyed;
        }

        /// <summary>
        /// 狙いの値をクリップに入れる値へ直す（storePosition などがあるとき）。成分が一部しか無いときは、残りを今の値で補ってから直す
        /// </summary>
        void ConvertTransforms ()
        {
            if (storeFloat != null) {
                List<EditorCurveBinding> floats = new List<EditorCurveBinding> ();
                foreach (EditorCurveBinding binding in values_.Keys) {
                    if (binding.type != typeof (Transform) && !raw_.Contains (binding)) floats.Add (binding);
                }
                foreach (EditorCurveBinding binding in floats) values_[binding] = storeFloat (binding, values_[binding]);
            }
            if (storePosition == null && storeRotation == null && storeScale == null) return;
            HashSet<string> paths = new HashSet<string> ();
            foreach (EditorCurveBinding binding in values_.Keys) {
                if (binding.type == typeof (Transform) && !raw_.Contains (binding)) paths.Add (binding.path);
            }
            foreach (string path in paths) {
                Transform t = root_ == null ? null : path.Length == 0 ? root_ : root_.Find (path);
                if (storePosition != null) {
                    Vector3 current = t != null ? t.localPosition : Vector3.zero;
                    ConvertVector (path, kPosition, current, storePosition);
                }
                if (storeRotation != null) ConvertRotation (path, t != null ? t.localRotation : Quaternion.identity);
                if (storeScale != null) {
                    Vector3 current = t != null ? t.localScale : Vector3.one;
                    ConvertVector (path, kScale, current, storeScale);
                }
            }
        }

        void ConvertVector (string path, string property, Vector3 current, System.Func<string, Vector3, Vector3> store)
        {
            EditorCurveBinding[] parts = new EditorCurveBinding[3];
            Vector3 value = current;
            bool any = false;
            for (int a = 0; a < 3; a++) {
                parts[a] = EditorCurveBinding.FloatCurve (path, typeof (Transform), property + "." + kAxes[a]);
                float v;
                if (values_.TryGetValue (parts[a], out v) && !raw_.Contains (parts[a])) {
                    value[a] = v;
                    any = true;
                }
            }
            if (!any) return;
            Vector3 stored = store (path, value);
            for (int a = 0; a < 3; a++) values_[parts[a]] = stored[a];
        }

        void ConvertRotation (string path, Quaternion current)
        {
            EditorCurveBinding[] parts = new EditorCurveBinding[4];
            Quaternion value = current;
            bool any = false;
            for (int a = 0; a < 4; a++) {
                parts[a] = EditorCurveBinding.FloatCurve (path, typeof (Transform), kRotation + "." + kAxes[a]);
                float v;
                if (values_.TryGetValue (parts[a], out v) && !raw_.Contains (parts[a])) {
                    value[a] = v;
                    any = true;
                }
            }
            if (!any) return;
            Quaternion stored = storeRotation (path, value);
            for (int a = 0; a < 4; a++) values_[parts[a]] = stored[a];
            if (!rotationPaths_.Contains (path)) rotationPaths_.Add (path);
        }

        /// <summary>
        /// 打つ四元数を、隣のキー（前のキー、無ければ後ろのキー）と同じ側（内積が正）へそろえる
        /// </summary>
        void AlignRotationSigns ()
        {
            int frame = Mathf.RoundToInt (frame_);
            EditorCurveBinding[] parts = new EditorCurveBinding[4];
            AnimationCurve[] existing = new AnimationCurve[4];
            foreach (string path in rotationPaths_) {
                bool complete = true;
                for (int a = 0; a < 4 && complete; a++) {
                    parts[a] = EditorCurveBinding.FloatCurve (path, typeof (Transform), kRotation + "." + kAxes[a]);
                    complete = values_.ContainsKey (parts[a]);
                }
                for (int a = 0; a < 4 && complete; a++) {
                    existing[a] = AnimationUtility.GetEditorCurve (clip_, parts[a]);
                    complete = existing[a] != null;
                }
                float neighbor;
                if (!complete || !TryFindNeighborTime (existing[3], frame, out neighbor)) continue;

                float dot = 0;
                for (int a = 0; a < 4; a++) {
                    dot += existing[a].Evaluate (neighbor) * values_[parts[a]];
                }
                if (dot >= 0) continue;
                for (int a = 0; a < 4; a++) values_[parts[a]] = -values_[parts[a]];
            }
        }

        bool TryFindNeighborTime (AnimationCurve curve, int frame, out float time)
        {
            time = 0;
            float before = float.MinValue;
            float after = float.MaxValue;
            foreach (Keyframe key in curve.keys) {
                int keyFrame = Mathf.RoundToInt (key.time * clip_.frameRate);
                if (keyFrame < frame && key.time > before) before = key.time;
                if (keyFrame > frame && key.time < after) after = key.time;
            }
            if (before > float.MinValue) {
                time = before;
                return true;
            }
            if (after < float.MaxValue) {
                time = after;
                return true;
            }
            return false;
        }
    }

}
