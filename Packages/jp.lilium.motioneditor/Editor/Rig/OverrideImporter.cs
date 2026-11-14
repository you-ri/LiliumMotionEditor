using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 外で作った動き（Humanoid のクリップ・BVH）を、PoseSession の元のクリップに重ねる Override や外部の補間の結果へ書く計算（S40・S74・S81）。
    /// セッションの中の体を動かして値を作り、アセットは書く所でだけ触る。入口は PoseSession.ImportOverride / ImportBetween
    /// </summary>
    sealed class OverrideImporter
    {
        readonly PoseSession session_;

        public OverrideImporter (PoseSession session)
        {
            session_ = session;
        }

        PreviewStage stage
        {
            get { return session_.stage; }
        }

        /// <summary>外の Humanoid のクリップを Override として destination へ書く（PoseSession.ImportOverride）</summary>
        public string Import (AnimationClip source, AnimationClip destination, OverrideMode mode, PoseSession.OverrideImportOptions options, out PoseSession.OverrideImportResult result)
        {
            result = new PoseSession.OverrideImportResult ();
            if (options == null) options = new PoseSession.OverrideImportOptions ();
            string problem = GetOverrideImportProblem (source, options);
            if (problem != null) return problem;
            if (destination == null || destination == session_.clip || destination == source) return Tr ("POSE_SESSION_BAD_IMPORT_DESTINATION");
            ComputedOverride computed = new ComputedOverride { result = result };
            try {
                Stepper.Run (ComputeOverride (source, mode, options, computed));
                WriteOverride (destination, computed);
                return null;
            }
            catch (System.Exception e) {
                return e.Message;
            }
            finally {
                session_.RestoreFrame ();
            }
        }

        /// <summary>BVH を Humanoid のクリップにしてから、Import と同じ取り込みへ流す（PoseSession.ImportOverride）</summary>
        public string Import (Bvh bvh, ExternalSkeleton skeleton, AnimationClip destination, OverrideMode mode, PoseSession.OverrideImportOptions options, out PoseSession.OverrideImportResult result)
        {
            result = new PoseSession.OverrideImportResult ();
            if (options == null) options = new PoseSession.OverrideImportOptions { fingers = false };
            string problem = GetBvhImportProblem (bvh, skeleton);
            if (problem != null) return problem;
            HumanoidFromBvh made = new HumanoidFromBvh ();
            Stepper.Run (MakeHumanoid (bvh, skeleton, options.alignRoot, made));
            if (made.error != null) return made.error;
            try {
                return Import (made.clip, destination, mode, options, out result);
            }
            finally {
                Object.DestroyImmediate (made.clip);
            }
        }

        /// <summary>外部の補間の結果の計算を始める（PoseSession.BeginImportBetween）。始められなければ null と理由</summary>
        public PoseSession.BetweenImport BeginBetween (Bvh bvh, ExternalSkeleton skeleton, IList<BetweenClips.Range> segments, out string problem)
        {
            problem = GetBetweenProblem (segments) ?? GetBvhImportProblem (bvh, skeleton);
            if (problem != null) return null;
            PoseSession.OverrideImportOptions options = new PoseSession.OverrideImportOptions { fingers = false, alignRoot = true };
            ComputedOverride computed = new ComputedOverride { result = new PoseSession.OverrideImportResult () };
            return new PoseSession.BetweenImport (this, ComputeBetween (bvh, skeleton, options, computed), computed);
        }

        /// <summary>接地とみなす足の点の高さと 1 フレームの水平の動き（メートル。体の大きさ 1 のとき）</summary>
        const float kContactHeight = 0.03f;
        const float kContactSpeed = 0.02f;
        /// <summary>接地の区間の中で、足の点が最初の場所からこれ以上動いた（か床に沈んだ）区間だけ留める（メートル。体の大きさ 1 のとき）</summary>
        const float kContactDrift = 0.005f;
        /// <summary>間引くときの許し（四元数の成分で 0.06° 程度・位置で 0.5mm 程度）</summary>
        const float kReduceTolerance = 5e-4f;

        /// <summary>外の Humanoid のクリップを取り込めない理由（書き込み先は見ない）</summary>
        string GetOverrideImportProblem (AnimationClip source, PoseSession.OverrideImportOptions options)
        {
            string character = session_.characterProblem;
            if (character != null) return character;
            if (session_.clip == null) return Tr ("POSE_SESSION_NO_CLIP");
            string problem = HumanoidImport.GetProblem (source);
            if (problem != null) return problem;
            if (options.lockFeet && stage.fullBody == null) return Tr ("POSE_DOCUMENT_NO_POINTS");
            return null;
        }

        /// <summary>取り込みで計算した、書く前の値（フレームごと）。計算（ComputeOverride）と書く所（WriteOverride）を分けるため</summary>
        internal sealed class ComputedOverride
        {
            public PoseSession.OverrideImportResult result;
            public Dictionary<EditorCurveBinding, float[]> stored;
            public HashSet<string> rotationPaths;
            public float rate;
            /// <summary>書くカーブ（間引いた後。PrepareCurves で作ったときだけ）</summary>
            public Dictionary<EditorCurveBinding, AnimationCurve> curves;
            /// <summary>計算できなかった理由</summary>
            public string error;
        }

        /// <summary>
        /// ImportOverride の計算（書く前まで。1〜3 の差分にするところまで）。フレームごとに区切ってあり、Stepper で少しずつ回せる。
        /// このセッションの中の体を動かすだけで、アセットは書かない。終わると computed に書く値が入る
        /// </summary>
        IEnumerator ComputeOverride (AnimationClip source, OverrideMode mode, PoseSession.OverrideImportOptions options, ComputedOverride computed)
        {
            PoseSession.OverrideImportResult result = computed.result;
            EditingRig rig = stage.editingRig;
            Transform root = rig.root.transform;
            // 左右反転して読むキャラは、窓と同じく反転したクリップから取り込む。
            // 反転するのはアセットのクリップ（ゲームが流しているもの）だけ。BVH などから作った一時のクリップは、撮った動きのまま
            if (EditorUtility.IsPersistent (source)) source = stage.ReadHumanoidClip (source);
            float rate = CurveEdit.FrameRateOf (session_.clip);
            // 作業用のクリップ（使い終えたら消す。保存しない印を付けて、Undo にも控えない）
            AnimationClip imported = new AnimationClip { frameRate = rate, hideFlags = HideFlags.HideAndDontSave };
            List<PoseTarget> importTargets = RigTargets.Create (rig, stage.editingRigSolver);
            try {
                // 1. 外のクリップを編集用リグの値にする（表示モデルへ当てて、編集用の体へ写して捉える）。
                //    ルートの移動は HumanoidImport が腰へ移すので、表示モデルの置き場所は動かさない（当てるたびに戻す）
                Transform display = stage.animator.transform;
                display.GetLocalPositionAndRotation (out Vector3 displayPosition, out Quaternion displayRotation);
                using (ClipSampler sampler = new ClipSampler (stage.animator)) {
                    PoseImport.Result imported1 = default;
                    yield return HumanoidImport.ImportSteps (source, imported, rig, stage.editingRigSolver, time => {
                        sampler.Sample (source, time);
                        display.SetLocalPositionAndRotation (displayPosition, displayRotation);
                    }, importTargets, r => imported1 = r);
                    result.frameCount = imported1.frameCount;
                    if (imported1.notes != null) result.notes.AddRange (imported1.notes);
                }
                // 元のクリップのほうが長ければ、その長さまで書く（取り込み元より後ろは下で差 0 にする。書かないとカーブの最後の差が残る）
                result.frameCount = Mathf.Max (result.frameCount, CurveEdit.FrameCount (session_.clip.empty ? 0 : session_.clip.length, rate));
                yield return null;
                Dictionary<EditorCurveBinding, float[]> target = ReadCurves (imported, result.frameCount, rate);
                yield return null;
                if (!options.fingers) {
                    HashSet<string> fingerPaths = new HashSet<string> (rig.binding.boneControls.Where (c => c.human.HasValue && IsFinger (c.human.Value)).Select (c => c.path));
                    foreach (EditorCurveBinding binding in target.Keys.Where (b => fingerPaths.Contains (b.path)).ToList ()) target.Remove (binding);
                }

                // 2. 足を床に付ける
                if (options.lockFeet) result.lockedFrames = LockFeet (imported, target, result.frameCount, rate);

                // 3. 元のクリップとの差分にする。取り込み元より後ろのフレームは元のまま（差 0）にする（最後の姿勢の差を残さない）。
                //    keepKeys なら、元のキーのフレームで差 0、前後でなめらかに差を戻す（重みは差分にする前の狙いの値に掛ける）
                int sourceFrames = Mathf.Clamp (Mathf.RoundToInt ((source.empty ? 0 : source.length) * rate) + 1, 1, result.frameCount);
                int[] baseKeys = ClipCycle.GetFrames (session_.clip).Where (k => k >= 0 && k < sourceFrames).ToArray ();
                result.keyDeviations = MeasureKeyDeviations (imported, baseKeys, rate, options.fingers);
                yield return null;
                float[] weights = new float[result.frameCount];
                for (int f = 0; f < result.frameCount; f++) {
                    weights[f] = f >= sourceFrames ? 0 : options.keepKeys ? KeepKeysWeight (f, baseKeys, options.keepKeysBlend * rate) : 1;
                }
                Dictionary<EditorCurveBinding, float[]> stored = new Dictionary<EditorCurveBinding, float[]> ();
                foreach (EditorCurveBinding binding in target.Keys) stored[binding] = new float[result.frameCount];
                List<IGrouping<string, EditorCurveBinding>> transforms = target.Keys.Where (b => b.type == typeof (Transform)).GroupBy (b => b.path + "|" + b.propertyName.Substring (0, b.propertyName.LastIndexOf ('.'))).ToList ();
                // 置き換えで、どの組も成分がそろっていれば、重み 1 のフレームは元の値を使わない。元のクリップを当てずに済ませる
                // （外部の補間の取り込みは全フレームこれで、10 秒で 0.1 秒ほど縮む）
                bool complete = transforms.All (g => g.Count () == (g.First ().propertyName.StartsWith ("m_LocalRotation.") ? 4 : 3));
                for (int f = 0; f < result.frameCount; f++) {
                    bool useBase = mode != OverrideMode.Override || weights[f] < 1 || !complete;
                    if (useBase) stage.SampleBaseAsControls (session_.clip, session_.useHumanoidBase, f / rate);
                    foreach (IGrouping<string, EditorCurveBinding> group in transforms) StoreTransform (group, root.gameObject, target, stored, f, mode, useBase ? weights[f] : 1, useBase);
                    // 数値（点の固定の強さ・Rig の代理の重み）は足し合わせず、Additive でも置き換える
                    foreach (EditorCurveBinding binding in target.Keys) {
                        if (binding.type == typeof (Transform)) continue;
                        float value = target[binding][f];
                        if (weights[f] < 1 && AnimationUtility.GetFloatValue (root.gameObject, binding, out float baseValue)) value = Mathf.Lerp (baseValue, value, weights[f]);
                        stored[binding][f] = value;
                    }
                    yield return null;
                }
                computed.stored = stored;
                computed.rotationPaths = new HashSet<string> (target.Keys.Where (b => b.propertyName.StartsWith ("m_LocalRotation.")).Select (b => b.path));
                computed.rate = rate;
            }
            finally {
                foreach (PoseTarget t in importTargets) t.Dispose ();
                Object.DestroyImmediate (imported);
            }
        }

        /// <summary>計算した値を destination へ書き、変わらないキーを間引く</summary>
        static void WriteOverride (AnimationClip destination, ComputedOverride computed)
        {
            PoseSession.OverrideImportResult result = computed.result;
            PoseImport.WriteCollected (destination, computed.stored, computed.rotationPaths, result.frameCount, computed.rate);
            result.keyCountBeforeReduce = CountKeys (destination);
            OverrideMerge.ReduceKeys (destination, kReduceTolerance);
            result.keyCount = CountKeys (destination);
            OverrideApplier.Invalidate (destination);
        }

        /// <summary>
        /// 書くカーブを作って間引いておく（1 本ずつ区切ってあり、Stepper で少しずつ回せる）。WriteOverride と同じカーブになるが、
        /// 書いてから間引くと、書く所（分けられない）で 0.1 秒ほど止まるので先に済ませる。書き込み先のほかのカーブは間引かないので、
        /// 書き込み先が編集用リグのカーブだけのとき（外部の補間の結果のファイル）に使う
        /// </summary>
        static IEnumerator PrepareCurves (ComputedOverride computed)
        {
            PoseSession.OverrideImportResult result = computed.result;
            PoseImport.AlignRotationSigns (computed.stored, computed.rotationPaths, result.frameCount);
            computed.curves = new Dictionary<EditorCurveBinding, AnimationCurve> ();
            result.keyCountBeforeReduce = 0;
            result.keyCount = 0;
            foreach (KeyValuePair<EditorCurveBinding, float[]> pair in computed.stored) {
                AnimationCurve curve = PoseImport.BuildCurve (pair.Value, computed.rate);
                result.keyCountBeforeReduce += curve.length;
                if (curve.length >= 3) curve = OverrideMerge.Reduce (curve, kReduceTolerance);
                result.keyCount += curve.length;
                computed.curves.Add (pair.Key, curve);
                yield return null;
            }
        }

        /// <summary>PrepareCurves で作ったカーブを destination へ書く</summary>
        static void WritePrepared (AnimationClip destination, ComputedOverride computed)
        {
            PoseImport.WriteCurves (destination, computed.curves);
            OverrideApplier.Invalidate (destination);
        }

        /// <summary>編集用リグのカーブをフレームごとの値にする</summary>
        static Dictionary<EditorCurveBinding, float[]> ReadCurves (AnimationClip clip, int frameCount, float rate)
        {
            Dictionary<EditorCurveBinding, float[]> values = new Dictionary<EditorCurveBinding, float[]> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (!EditingClip.IsRigBinding (binding)) continue;
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                float[] array = new float[frameCount];
                for (int f = 0; f < frameCount; f++) array[f] = curve.Evaluate (f / rate);
                values[binding] = array;
            }
            return values;
        }

        string GetBetweenProblem (IList<BetweenClips.Range> segments)
        {
            if (session_.clip == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (BetweenClips.PathFor (session_.clip) == null) return Tr ("BETWEEN_CLIPS_ONLY_SAVED_CLIP");
            if (segments == null || segments.Count == 0) return Tr ("BETWEEN_CLIPS_NO_SEGMENTS");
            foreach (BetweenClips.Range range in segments) {
                string problem = BetweenClips.GetSegmentProblem (session_.clip, range);
                if (problem != null) return problem;
            }
            return null;
        }

        IEnumerator ComputeBetween (Bvh bvh, ExternalSkeleton skeleton, PoseSession.OverrideImportOptions options, ComputedOverride computed)
        {
            HumanoidFromBvh made = new HumanoidFromBvh ();
            yield return MakeHumanoid (bvh, skeleton, options.alignRoot, made);
            if (made.error != null) {
                computed.error = made.error;
                yield break;
            }
            try {
                string problem = GetOverrideImportProblem (made.clip, options);
                if (problem != null) {
                    computed.error = problem;
                    yield break;
                }
                yield return ComputeOverride (made.clip, OverrideMode.Override, options, computed);
                yield return PrepareCurves (computed);
            }
            finally {
                Object.DestroyImmediate (made.clip);
            }
        }

        /// <summary>計算した動きを結果のファイルへ書き、区間の印を付ける（ImportBetween の書く所）</summary>
        public string CommitBetween (ComputedOverride computed, IList<BetweenClips.Range> segments, out PoseSession.OverrideImportResult result)
        {
            result = null;
            if (computed.error != null) {
                session_.RestoreFrame ();
                return computed.error;
            }
            // 計算している間にキーが動いて区間でなくなっていれば書かない
            string problem = GetBetweenProblem (segments);
            if (problem != null) {
                session_.RestoreFrame ();
                return problem;
            }
            string path = BetweenClips.PathFor (session_.clip);
            AnimationClip between = AssetDatabase.LoadAssetAtPath<AnimationClip> (path);
            bool created = between == null;
            if (created) {
                between = new AnimationClip { frameRate = session_.clip.frameRate };
                AssetDatabase.CreateAsset (between, path);
            }
            else {
                ClipUndo.Record (between, "Import Between");
            }
            try {
                WritePrepared (between, computed);
            }
            catch (System.Exception e) {
                if (created) AssetDatabase.DeleteAsset (path);
                session_.RestoreFrame ();
                return e.Message;
            }
            AssetDatabase.SaveAssetIfDirty (between);
            // 区間の指紋はキーを足した後で取る（下の補間を曲線に戻すと、端にキーの無いカーブにキーが足される）
            session_.CurveUnderExternal (segments, "Import Between");
            BetweenClips.SaveSegments (between, session_.clip, segments, BetweenClips.MakeStamps (session_.clip));
            session_.RestoreFrame ();
            result = computed.result;
            return null;
        }

        string GetBvhImportProblem (Bvh bvh, ExternalSkeleton skeleton)
        {
            string character = session_.characterProblem;
            if (character != null) return character;
            if (session_.clip == null) return Tr ("POSE_SESSION_NO_CLIP");
            if (bvh == null || bvh.frames.Count == 0) return Tr ("POSE_SESSION_EMPTY_BVH");
            if (skeleton == null) return Tr ("EXTERNAL_SKELETON_NO_JOINTS");
            string mismatch = skeleton.GetMismatch (bvh);
            if (mismatch != null) return mismatch;
            if (!stage.canPreviewHumanoid) return Tr ("POSE_SESSION_NO_CHARACTER");
            return null;
        }

        /// <summary>BVH から作った一時の Humanoid のクリップ（使い終えたら消す）か、作れなかった理由</summary>
        sealed class HumanoidFromBvh
        {
            public AnimationClip clip;
            public string error;
        }

        /// <summary>BVH の各フレームを外部の骨格の Avatar で Humanoid の姿勢にし、一時の Humanoid のクリップにする（フレームごとに区切ってある）</summary>
        IEnumerator MakeHumanoid (Bvh bvh, ExternalSkeleton skeleton, bool alignRoot, HumanoidFromBvh made)
        {
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            if (instance == null) {
                made.error = error;
                yield break;
            }
            int current = session_.frame;
            try {
                // 元の最初のフレームの体の置き場所（焼いた後の姿勢。体の大きさで割った値）
                HumanPose start = new HumanPose ();
                if (alignRoot) {
                    session_.Evaluate (0);
                    if (!stage.ReadHumanPose (ref start)) {
                        made.error = Tr ("POSE_SESSION_NO_CHARACTER");
                        yield break;
                    }
                }
                HumanPose pose = new HumanPose ();
                Vector3 offsetPosition = Vector3.zero;
                Quaternion offsetRotation = Quaternion.identity;
                int muscleCount = HumanTrait.MuscleCount;
                List<Keyframe>[] keys = new List<Keyframe>[7 + muscleCount];
                for (int c = 0; c < keys.Length; c++) keys[c] = new List<Keyframe> ();
                Quaternion previous = Quaternion.identity;
                for (int f = 0; f < bvh.frames.Count; f++) {
                    skeleton.Apply (instance, bvh, f);
                    instance.handler.GetHumanPose (ref pose);
                    if (f == 0 && alignRoot) {
                        Vector3 forward = start.bodyRotation * Quaternion.Inverse (pose.bodyRotation) * Vector3.forward;
                        forward.y = 0;
                        if (forward.sqrMagnitude > 1e-8f) offsetRotation = Quaternion.LookRotation (forward);
                        Vector3 delta = start.bodyPosition - offsetRotation * pose.bodyPosition;
                        offsetPosition = new Vector3 (delta.x, 0, delta.z);
                    }
                    Vector3 position = offsetPosition + offsetRotation * pose.bodyPosition;
                    Quaternion rotation = offsetRotation * pose.bodyRotation;
                    // 隣のフレームと同じ半球にそろえる（成分ごとに補間するので、符号が飛ぶと間で回る）
                    if (f > 0 && Quaternion.Dot (previous, rotation) < 0) rotation = new Quaternion (-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previous = rotation;
                    float time = f * bvh.frameTime;
                    float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
                    for (int c = 0; c < 7; c++) keys[c].Add (new Keyframe (time, values[c]));
                    for (int m = 0; m < muscleCount; m++) keys[7 + m].Add (new Keyframe (time, pose.muscles[m]));
                    yield return null;
                }
                AnimationClip humanoid = new AnimationClip { frameRate = 1 / bvh.frameTime, hideFlags = HideFlags.HideAndDontSave };
                string[] axes = { "x", "y", "z", "w" };
                EditorCurveBinding[] bindings = new EditorCurveBinding[keys.Length];
                AnimationCurve[] curves = new AnimationCurve[keys.Length];
                for (int c = 0; c < keys.Length; c++) {
                    string name = c < 3 ? HumanoidBaker.kRootT + "." + axes[c] : c < 7 ? HumanoidBaker.kRootQ + "." + axes[c - 3] : HumanoidBaker.clipMuscleNames[c - 7];
                    AnimationCurve curve = new AnimationCurve (keys[c].ToArray ());
                    for (int k = 0; k < curve.length; k++) {
                        AnimationUtility.SetKeyLeftTangentMode (curve, k, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode (curve, k, AnimationUtility.TangentMode.Linear);
                    }
                    bindings[c] = EditorCurveBinding.FloatCurve ("", typeof (Animator), name);
                    curves[c] = curve;
                    yield return null;
                }
                // まとめて書く（1 本ずつだと、そのたびにクリップが作り直される）
                AnimationUtility.SetEditorCurves (humanoid, bindings, curves);
                made.clip = humanoid;
            }
            finally {
                instance.Dispose ();
                session_.Evaluate (current);
            }
        }

        /// <summary>
        /// keepKeys の重み: キーのフレームで 0、キーから blendFrames 離れると 1（smoothstep。速さが途切れない）。いちばん近いキーで決める
        /// </summary>
        static float KeepKeysWeight (int f, int[] keys, float blendFrames)
        {
            float weight = 1;
            foreach (int k in keys) {
                float d = Mathf.Abs (f - k);
                float w = blendFrames <= 0 ? (d < 0.5f ? 0 : 1) : Mathf.Clamp01 (d / blendFrames);
                weight = Mathf.Min (weight, w * w * (3 - 2 * w));
            }
            return weight;
        }

        /// <summary>
        /// 元のクリップのキーのフレームで、取り込んだ動き（編集用リグの値）と元の姿勢の、骨の場所の差（S75。寄せる前）。
        /// 指を取り込まないときは、指とその先の骨を測らない（重ねても元の手の形のままなので）
        /// </summary>
        List<PoseSession.KeyDeviation> MeasureKeyDeviations (AnimationClip imported, int[] keys, float rate, bool fingers)
        {
            List<PoseSession.KeyDeviation> deviations = new List<PoseSession.KeyDeviation> ();
            EditingRig rig = stage.editingRig;
            HashSet<Transform> fingerBones = new HashSet<Transform> ();
            if (!fingers) {
                for (HumanBodyBones b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++) {
                    Transform t = rig.GetEditingBone (b);
                    if (t != null) fingerBones.Add (t);
                }
            }
            bool IsFingerBone (Transform t)
            {
                for (; t != null; t = t.parent) {
                    if (fingerBones.Contains (t)) return true;
                }
                return false;
            }
            List<Transform> bones = rig.editingBones.Where (t => !IsFingerBone (t)).ToList ();
            Transform hips = rig.GetEditingBone (HumanBodyBones.Hips);
            Vector3[] before = new Vector3[bones.Count];
            foreach (int k in keys) {
                session_.Evaluate (k);
                for (int b = 0; b < bones.Count; b++) before[b] = bones[b].position;
                Vector3 baseHips = hips != null ? hips.position : Vector3.zero;
                ApplyImported (imported, k / rate);
                PoseSession.KeyDeviation deviation = new PoseSession.KeyDeviation { frame = k, bone = "" };
                for (int b = 0; b < bones.Count; b++) {
                    float d = Vector3.Distance (before[b], bones[b].position);
                    if (d > deviation.distance) {
                        deviation.distance = d;
                        deviation.bone = bones[b].name;
                    }
                }
                deviation.hips = hips != null ? Vector3.Distance (baseHips, hips.position) : 0;
                deviations.Add (deviation);
            }
            return deviations;
        }

        static bool IsFinger (HumanBodyBones bone)
        {
            return bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.RightLittleDistal;
        }

        /// <summary>
        /// 位置・回転・大きさのどれか 1 組（x/y/z[/w]）を、今のコントロールの値（元）との差分にする。
        /// weight は狙いの値の効き（0 なら元の値のまま＝差 0。回転は Slerp、位置と大きさは Lerp で元と混ぜてから差分にする）。
        /// readSource が false なら元の値を読まない（置き換え・重み 1 で、元のクリップを当てていないとき）
        /// </summary>
        static void StoreTransform (IGrouping<string, EditorCurveBinding> group, GameObject root, Dictionary<EditorCurveBinding, float[]> target,
            Dictionary<EditorCurveBinding, float[]> stored, int f, OverrideMode mode, float weight = 1, bool readSource = true)
        {
            string[] axes = { "x", "y", "z", "w" };
            EditorCurveBinding first = group.First ();
            string prefix = first.propertyName.Substring (0, first.propertyName.LastIndexOf ('.') + 1);
            int count = prefix == "m_LocalRotation." ? 4 : 3;
            EditorCurveBinding[] bindings = new EditorCurveBinding[count];
            float[] source = new float[4];
            float[] value = new float[4];
            for (int i = 0; i < count; i++) {
                bindings[i] = EditorCurveBinding.FloatCurve (first.path, typeof (Transform), prefix + axes[i]);
                if (!readSource || !AnimationUtility.GetFloatValue (root, bindings[i], out source[i])) source[i] = 0;
                // 組の一部しかカーブが無いときは、無い成分を元の値のままにする
                float[] array;
                value[i] = weight > 0 && target.TryGetValue (bindings[i], out array) ? array[f] : source[i];
            }
            float[] result = new float[4];
            if (count == 4) {
                Quaternion s = Quaternion.Normalize (new Quaternion (source[0], source[1], source[2], source[3]));
                Quaternion v = Quaternion.Normalize (new Quaternion (value[0], value[1], value[2], value[3]));
                Quaternion q = OverrideMath.StoreRotation (mode, s, weight < 1 ? Quaternion.Slerp (s, v, weight) : v);
                result[0] = q.x; result[1] = q.y; result[2] = q.z; result[3] = q.w;
            }
            else {
                Vector3 s = new Vector3 (source[0], source[1], source[2]);
                Vector3 v = Vector3.Lerp (s, new Vector3 (value[0], value[1], value[2]), weight);
                Vector3 r = prefix == "m_LocalScale." ? OverrideMath.StoreScale (mode, s, v) : OverrideMath.StorePosition (mode, s, v);
                result[0] = r.x; result[1] = r.y; result[2] = r.z;
            }
            for (int i = 0; i < count; i++) {
                float[] array;
                if (!stored.TryGetValue (bindings[i], out array)) stored[bindings[i]] = array = new float[stored.Values.First ().Length];
                array[f] = result[i];
            }
        }

        /// <summary>
        /// 足の点が低くて止まっている区間を接地とみなし、その区間の最初の場所（床より下なら床の上）に点を留めて全身 IK で解き直す。
        /// 点ごとに見るので、かかとが先に上がりつま先が残る、のような足の動きも残る。target（編集用リグの値）の該当フレームを書き換える。
        /// 解き直したフレームの数を返す
        /// </summary>
        int LockFeet (AnimationClip imported, Dictionary<EditorCurveBinding, float[]> target, int frameCount, float rate)
        {
            EditingRig rig = stage.editingRig;
            FullBodyRig body = stage.fullBody;
            float scale = rig.humanScale;
            List<BodyPointId> points = body.pointIds.Where (body.IsContact).ToList ();

            // 足の点の場所（フレームごと）
            Vector3[,] positions = new Vector3[frameCount, points.Count];
            for (int f = 0; f < frameCount; f++) {
                ApplyImported (imported, f / rate);
                for (int p = 0; p < points.Count; p++) {
                    body.TryGetBonePointWorld (points[p], out positions[f, p]);
                    // 接地は、床との当たりの球（定義の radius）の下の端の高さで見る
                    positions[f, p].y -= body.GetRadius (points[p]);
                }
            }
            // 接地の区間と、留める場所
            Vector3?[,] anchors = new Vector3?[frameCount, points.Count];
            for (int p = 0; p < points.Count; p++) {
                int start = -1;
                for (int f = 0; f <= frameCount; f++) {
                    bool contact = false;
                    if (f < frameCount && positions[f, p].y <= kContactHeight * scale) {
                        Vector3 a = positions[f, p];
                        Vector3 b = positions[f > 0 ? f - 1 : System.Math.Min (f + 1, frameCount - 1), p];
                        a.y = b.y = 0;
                        contact = Vector3.Distance (a, b) <= kContactSpeed * scale;
                    }
                    if (contact && start < 0) start = f;
                    if (contact || start < 0) continue;
                    // 2 フレーム以上続き、その間に足が実際に動いている（滑っている・床に沈んでいる）区間だけ留める。
                    // もう留まっている区間を解き直すと、留め始め・離すところで脚が跳ねるだけになる
                    Vector3 anchor = positions[start, p];
                    float drift = 0;
                    for (int g = start; g < f; g++) {
                        Vector3 d = positions[g, p] - anchor;
                        drift = Mathf.Max (drift, new Vector2 (d.x, d.z).magnitude, -positions[g, p].y);
                    }
                    if (f - start >= 2 && drift > kContactDrift * scale) {
                        anchor.y = Mathf.Max (anchor.y, 0);
                        for (int g = start; g < f; g++) anchors[g, p] = anchor;
                    }
                    start = -1;
                }
            }

            int locked = 0;
            List<PoseTarget> writeTargets = RigTargets.Create (rig, stage.editingRigSolver);
            try {
                for (int f = 0; f < frameCount; f++) {
                    bool any = false;
                    for (int p = 0; p < points.Count; p++) any |= anchors[f, p].HasValue;
                    if (!any) continue;
                    locked++;
                    ApplyImported (imported, f / rate);
                    // 留める点のある脚だけを解く（キーの間で Locked の点を留めるときと同じ。全身で解くと腕や背骨まで動く）
                    HashSet<Transform> controls = new HashSet<Transform> ();
                    for (int p = 0; p < points.Count; p++) {
                        if (!anchors[f, p].HasValue) continue;
                        // 留める場所は球の下の端なので、点（球の中心）は半径だけ上
                        body.SetPointPositionWorld (points[p], anchors[f, p].Value + Vector3.up * body.GetRadius (points[p]));
                        body.SetPositionWeight (points[p], 1);
                        controls.Add (body.GetControl (points[p]));
                    }
                    body.CaptureInput ();
                    body.SolveOnly (controls, 1, true);
                    stage.editingRigSolver.Capture ();
                    // 点の固定はキーにしない（全フレームが骨のキーになるので、点は要らない）
                    foreach (BodyPointId id in points) body.SetPositionWeight (id, 0);
                    using (CurveWriter writer = CurveWriter.Begin (null, rig.root.transform, f, null)) {
                        foreach (PoseTarget t in writeTargets) t.WriteKeys (writer);
                        foreach (KeyValuePair<EditorCurveBinding, float> pair in writer.values) {
                            float[] array;
                            if (target.TryGetValue (pair.Key, out array)) array[f] = pair.Value;
                        }
                    }
                }
            }
            finally {
                foreach (PoseTarget t in writeTargets) t.Dispose ();
            }
            return locked;
        }

        /// <summary>取り込んだ値（編集用リグのクリップ）をその時刻の操作値にして解く</summary>
        void ApplyImported (AnimationClip imported, float time)
        {
            stage.ResetControlsToRest ();
            stage.SampleClip (imported, time);
            stage.MaskBodyPointsForBake (imported, time);
            stage.Solve ();
        }

        static int CountKeys (AnimationClip clip)
        {
            int count = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                if (curve != null) count += curve.length;
            }
            return count;
        }
    }

}
