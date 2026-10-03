using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// Humanoid の姿勢・クリップの左右反転（体の左右の面で映す）。Unity の AnimationHumanStream.MirrorPose と同じ結果になるようにしてある（テストで比べる）。
    /// - 左右の muscle を入れ替える（手足・肩・目・指）
    /// - 体の中心の muscle のうち、左右へ曲げる・ねじるもの（Left-Right / Twist / Turn / Tilt）の符号を反転する
    /// - 体の位置は x を反転、向きは (x, -y, -z, w)
    /// 反転は 2 回で元に戻る
    /// </summary>
    public static class HumanoidMirror
    {
        static int[] partner_;
        static bool[] negate_;

        static void Build ()
        {
            if (partner_ != null) return;

            string[] names = HumanTrait.MuscleName;
            Dictionary<string, int> index = new Dictionary<string, int> ();
            for (int i = 0; i < names.Length; i++) index[names[i]] = i;

            partner_ = new int[names.Length];
            negate_ = new bool[names.Length];
            for (int i = 0; i < names.Length; i++) {
                string name = names[i];
                string other = null;
                if (name.StartsWith ("Left ")) other = "Right " + name.Substring (5);
                else if (name.StartsWith ("Right ")) other = "Left " + name.Substring (6);

                int j;
                if (other != null && index.TryGetValue (other, out j)) {
                    partner_[i] = j;
                    continue;
                }
                partner_[i] = i;
                // 中心の骨（背骨・胸・首・頭・顎）で、左右に曲げる・ねじる向きのもの
                negate_[i] = name.Contains ("Left-Right");
            }
        }

        /// <summary>
        /// muscle の i 番目の反転先（左右の相手。中心の骨は自分）
        /// </summary>
        public static int PartnerOf (int muscle)
        {
            Build ();
            return partner_[muscle];
        }

        public static bool IsNegated (int muscle)
        {
            Build ();
            return negate_[muscle];
        }

        public static void Mirror (ref HumanPose pose)
        {
            Build ();
            float[] source = (float[])pose.muscles.Clone ();
            for (int i = 0; i < source.Length; i++) {
                float value = source[partner_[i]];
                pose.muscles[i] = negate_[i] ? -value : value;
            }
            pose.bodyPosition = MirrorPosition (pose.bodyPosition);
            pose.bodyRotation = MirrorRotation (pose.bodyRotation);
        }

        public static Vector3 MirrorPosition (Vector3 position)
        {
            return new Vector3 (-position.x, position.y, position.z);
        }

        public static Quaternion MirrorRotation (Quaternion rotation)
        {
            return new Quaternion (rotation.x, -rotation.y, -rotation.z, rotation.w);
        }

        /// <summary>
        /// Humanoid のクリップの体のカーブ（RootT / RootQ / muscle）と手足の IK ゴールを左右反転する。ほかのカーブ（骨・Rig など）はそのまま
        /// </summary>
        public static void MirrorClip (AnimationClip clip)
        {
            Build ();
            IReadOnlyList<string> muscleNames = HumanoidBaker.clipMuscleNames;
            Dictionary<string, int> muscleIndex = new Dictionary<string, int> ();
            for (int i = 0; i < muscleNames.Count; i++) muscleIndex[muscleNames[i]] = i;

            Dictionary<string, AnimationCurve> curves = new Dictionary<string, AnimationCurve> ();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings (clip)) {
                if (binding.type == typeof (Animator) && binding.path == "") {
                    curves[binding.propertyName] = AnimationUtility.GetEditorCurve (clip, binding);
                }
            }

            List<EditorCurveBinding> bindings = new List<EditorCurveBinding> ();
            List<AnimationCurve> values = new List<AnimationCurve> ();
            foreach (KeyValuePair<string, AnimationCurve> pair in curves) {
                string property = pair.Key;
                AnimationCurve result = null;
                int muscle;
                if (muscleIndex.TryGetValue (property, out muscle)) {
                    AnimationCurve from;
                    // 相手のカーブが無ければ 0 のまま（反転後にこのカーブは消す）
                    result = curves.TryGetValue (muscleNames[partner_[muscle]], out from) ? Scale (from, negate_[muscle] ? -1 : 1) : null;
                }
                else if (property == HumanoidBaker.kRootT + ".x" || property == HumanoidBaker.kRootQ + ".y" || property == HumanoidBaker.kRootQ + ".z") {
                    result = Scale (pair.Value, -1);
                }
                else {
                    continue;
                }
                bindings.Add (EditorCurveBinding.FloatCurve ("", typeof (Animator), property));
                values.Add (result);
            }
            // 相手だけにあったカーブ（左だけ動かしていた等）も作る
            foreach (KeyValuePair<string, AnimationCurve> pair in curves) {
                int muscle;
                if (!muscleIndex.TryGetValue (pair.Key, out muscle)) continue;
                string target = muscleNames[partner_[muscle]];
                if (curves.ContainsKey (target)) continue;
                bindings.Add (EditorCurveBinding.FloatCurve ("", typeof (Animator), target));
                values.Add (Scale (pair.Value, negate_[muscle] ? -1 : 1));
            }
            MirrorGoals (curves, bindings, values);
            AnimationUtility.SetEditorCurves (clip, bindings.ToArray (), values.ToArray ());
        }

        /// <summary>
        /// 手足の IK ゴール（LeftFootT / LeftFootQ など）を左右反転する。左右の相手の値から作る。
        /// 位置は x の符号を返す。向きはゴールごとの一定の回転（HumanoidGoals.kClipRotations）を外してから反転し、自分の分を掛け直す
        /// （2026-09-28 に Unity の Mirror 再生と一致を確認）。向きは掛け算なので、相手のキーの時刻ごとに値を作り直す
        /// </summary>
        static void MirrorGoals (Dictionary<string, AnimationCurve> curves, List<EditorCurveBinding> bindings, List<AnimationCurve> values)
        {
            string[] names = HumanoidGoals.kCurveNames;
            for (int i = 0; i < names.Length; i++) {
                // 左右の相手（LeftFoot と RightFoot、LeftHand と RightHand）
                int partner = i ^ 1;
                AnimationCurve[] from = new AnimationCurve[7];
                bool complete = true;
                for (int c = 0; c < 7; c++) {
                    string property = GoalProperty (names[partner], c);
                    if (!curves.TryGetValue (property, out from[c])) complete = false;
                }
                if (!complete) continue;

                SortedSet<float> times = new SortedSet<float> ();
                foreach (AnimationCurve curve in from) {
                    foreach (Keyframe key in curve.keys) times.Add (key.time);
                }
                Quaternion toPartner = Quaternion.Inverse (HumanoidGoals.kClipRotations[partner]);
                Quaternion own = HumanoidGoals.kClipRotations[i];
                List<float>[] mirrored = new List<float>[7];
                for (int c = 0; c < 7; c++) mirrored[c] = new List<float> ();
                Quaternion previous = Quaternion.identity;
                bool first = true;
                foreach (float time in times) {
                    Vector3 t = new Vector3 (from[0].Evaluate (time), from[1].Evaluate (time), from[2].Evaluate (time));
                    Quaternion q = Quaternion.Normalize (new Quaternion (from[3].Evaluate (time), from[4].Evaluate (time), from[5].Evaluate (time), from[6].Evaluate (time)));
                    Quaternion r = MirrorRotation (q * toPartner) * own;
                    if (!first && Quaternion.Dot (previous, r) < 0) r = new Quaternion (-r.x, -r.y, -r.z, -r.w);
                    previous = r;
                    first = false;
                    mirrored[0].Add (-t.x);
                    mirrored[1].Add (t.y);
                    mirrored[2].Add (t.z);
                    mirrored[3].Add (r.x);
                    mirrored[4].Add (r.y);
                    mirrored[5].Add (r.z);
                    mirrored[6].Add (r.w);
                }
                float[] keyTimes = new float[times.Count];
                times.CopyTo (keyTimes);
                for (int c = 0; c < 7; c++) {
                    bindings.Add (EditorCurveBinding.FloatCurve ("", typeof (Animator), GoalProperty (names[i], c)));
                    values.Add (Linear (keyTimes, mirrored[c], from[c]));
                }
            }
        }

        static string GoalProperty (string name, int channel)
        {
            string[] axes = { "x", "y", "z", "w" };
            return channel < 3 ? name + "T." + axes[channel] : name + "Q." + axes[channel - 3];
        }

        /// <summary>キーの間を直線でつなぐカーブ（折り返しの設定は元のカーブから）</summary>
        static AnimationCurve Linear (float[] times, List<float> values, AnimationCurve like)
        {
            Keyframe[] keys = new Keyframe[times.Length];
            for (int k = 0; k < times.Length; k++) {
                float inSlope = k > 0 ? (values[k] - values[k - 1]) / (times[k] - times[k - 1]) : 0;
                float outSlope = k < times.Length - 1 ? (values[k + 1] - values[k]) / (times[k + 1] - times[k]) : 0;
                keys[k] = new Keyframe (times[k], values[k], k > 0 ? inSlope : outSlope, k < times.Length - 1 ? outSlope : inSlope);
            }
            AnimationCurve result = new AnimationCurve (keys);
            result.preWrapMode = like.preWrapMode;
            result.postWrapMode = like.postWrapMode;
            return result;
        }

        static AnimationCurve Scale (AnimationCurve curve, float factor)
        {
            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++) {
                keys[i].value *= factor;
                keys[i].inTangent *= factor;
                keys[i].outTangent *= factor;
            }
            AnimationCurve result = new AnimationCurve (keys);
            result.preWrapMode = curve.preWrapMode;
            result.postWrapMode = curve.postWrapMode;
            return result;
        }
    }

}
