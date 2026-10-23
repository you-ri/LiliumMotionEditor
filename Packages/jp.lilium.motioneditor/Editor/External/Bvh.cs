using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// BVH（モーキャプや学習済みモデルの出力でよく使う、骨の階層と各フレームの値のテキスト）の読み書き（S72）。
    /// 値はファイルのまま（単位・座標系を直さない）持つ。Unity の骨へ当てる・骨から読むのは ExternalSkeleton がする
    /// </summary>
    public sealed class Bvh
    {
        /// <summary>関節。CHANNELS はファイルに書いてある順（例: Xposition Yposition Zposition Zrotation Yrotation Xrotation）</summary>
        public sealed class Joint
        {
            public string name;
            /// <summary>親の関節の番号（ルートは -1）</summary>
            public int parent = -1;
            public Vector3 offset;
            public string[] channels = new string[0];
            /// <summary>このフレームの値の並びで、最初のチャンネルが何番目か</summary>
            public int channelStart;
        }

        public readonly List<Joint> joints = new List<Joint> ();
        /// <summary>1 フレームの秒数</summary>
        public float frameTime = 1f / 30f;
        /// <summary>フレームごとの値（全部の関節の CHANNELS を並べたもの）</summary>
        public readonly List<float[]> frames = new List<float[]> ();

        static readonly CultureInfo kInvariant = CultureInfo.InvariantCulture;
        static readonly string[] kChannels = { "Xposition", "Yposition", "Zposition", "Xrotation", "Yrotation", "Zrotation" };

        public int channelCount { get { return joints.Count == 0 ? 0 : joints[joints.Count - 1].channelStart + joints[joints.Count - 1].channels.Length; } }

        public int IndexOf (string name)
        {
            for (int i = 0; i < joints.Count; i++) {
                if (joints[i].name == name) return i;
            }
            return -1;
        }

        /// <summary>関節を足す（channelStart を詰める）</summary>
        public Joint AddJoint (string name, int parent, Vector3 offset, params string[] channels)
        {
            Joint joint = new Joint { name = name, parent = parent, offset = offset, channels = channels, channelStart = channelCount };
            joints.Add (joint);
            return joint;
        }

        /// <summary>
        /// テキストを読む。読めなければ null を返し、error に理由を入れる。
        /// End Site は捨てる（骨の先の長さだけで、値を持たない）
        /// </summary>
        public static Bvh Parse (string text, out string error)
        {
            error = null;
            Bvh bvh = new Bvh ();
            if (string.IsNullOrEmpty (text)) {
                error = Tr ("BVH_EMPTY");
                return null;
            }
            List<string> tokens = Tokenize (text);
            int i = 0;
            try {
                if (tokens.Count == 0 || tokens[i++] != "HIERARCHY") throw new System.FormatException (Tr ("BVH_NO_HIERARCHY"));
                Stack<int> stack = new Stack<int> ();
                int endSiteDepth = -1;
                while (i < tokens.Count && tokens[i] != "MOTION") {
                    string token = tokens[i++];
                    switch (token) {
                        case "ROOT":
                        case "JOINT":
                            if (token == "ROOT" && stack.Count > 0) throw new System.FormatException (Tr ("BVH_BAD_TOKEN", token));
                            bvh.joints.Add (new Joint { name = tokens[i++], parent = stack.Count > 0 ? stack.Peek () : -1 });
                            break;
                        case "End":
                            i++; // Site
                            endSiteDepth = stack.Count;
                            break;
                        case "{":
                            if (endSiteDepth >= 0) stack.Push (-2);
                            else stack.Push (bvh.joints.Count - 1);
                            break;
                        case "}":
                            if (stack.Count == 0) throw new System.FormatException (Tr ("BVH_BAD_TOKEN", token));
                            if (stack.Pop () == -2) endSiteDepth = -1;
                            break;
                        case "OFFSET":
                            Vector3 offset = new Vector3 (Number (tokens[i]), Number (tokens[i + 1]), Number (tokens[i + 2]));
                            i += 3;
                            if (endSiteDepth < 0) Last (bvh).offset = offset;
                            break;
                        case "CHANNELS":
                            int count = int.Parse (tokens[i++], kInvariant);
                            Joint joint = Last (bvh);
                            joint.channels = new string[count];
                            for (int c = 0; c < count; c++) {
                                joint.channels[c] = tokens[i++];
                                if (System.Array.IndexOf (kChannels, joint.channels[c]) < 0) throw new System.FormatException (Tr ("BVH_BAD_CHANNEL", joint.channels[c]));
                            }
                            break;
                        default:
                            throw new System.FormatException (Tr ("BVH_BAD_TOKEN", token));
                    }
                }
                if (bvh.joints.Count == 0) throw new System.FormatException (Tr ("BVH_NO_JOINTS"));
                if (stack.Count != 0) throw new System.FormatException (Tr ("BVH_UNBALANCED"));
                int start = 0;
                foreach (Joint joint in bvh.joints) {
                    joint.channelStart = start;
                    start += joint.channels.Length;
                }

                if (i >= tokens.Count) throw new System.FormatException (Tr ("BVH_NO_MOTION"));
                i++; // MOTION
                Expect (tokens, ref i, "Frames:");
                int frameCount = int.Parse (tokens[i++], kInvariant);
                Expect (tokens, ref i, "Frame");
                Expect (tokens, ref i, "Time:");
                bvh.frameTime = Number (tokens[i++]);
                if (bvh.frameTime <= 0) throw new System.FormatException (Tr ("BVH_BAD_FRAME_TIME"));
                int channels = bvh.channelCount;
                if (tokens.Count - i != frameCount * channels) throw new System.FormatException (Tr ("BVH_VALUE_COUNT", frameCount * channels, tokens.Count - i));
                for (int f = 0; f < frameCount; f++) {
                    float[] values = new float[channels];
                    for (int c = 0; c < channels; c++) values[c] = Number (tokens[i++]);
                    bvh.frames.Add (values);
                }
                return bvh;
            }
            catch (System.Exception e) when (e is System.FormatException || e is System.ArgumentOutOfRangeException || e is System.OverflowException || e is System.NullReferenceException) {
                error = e is System.ArgumentOutOfRangeException || e is System.NullReferenceException ? Tr ("BVH_TRUNCATED") : e.Message;
                return null;
            }
        }

        static Joint Last (Bvh bvh)
        {
            if (bvh.joints.Count == 0) throw new System.FormatException (Tr ("BVH_NO_JOINTS"));
            return bvh.joints[bvh.joints.Count - 1];
        }

        static void Expect (List<string> tokens, ref int i, string word)
        {
            if (i >= tokens.Count || tokens[i] != word) throw new System.FormatException (Tr ("BVH_BAD_TOKEN", i < tokens.Count ? tokens[i] : ""));
            i++;
        }

        static float Number (string token)
        {
            if (!float.TryParse (token, NumberStyles.Float, kInvariant, out float value)) throw new System.FormatException (Tr ("BVH_BAD_NUMBER", token));
            return value;
        }

        static List<string> Tokenize (string text)
        {
            List<string> tokens = new List<string> ();
            int start = -1;
            for (int i = 0; i <= text.Length; i++) {
                bool space = i == text.Length || char.IsWhiteSpace (text[i]);
                if (space) {
                    if (start >= 0) tokens.Add (text.Substring (start, i - start));
                    start = -1;
                }
                else if (start < 0) start = i;
            }
            return tokens;
        }

        /// <summary>テキストにする（タブで字下げ。End Site は書かない）</summary>
        public string ToText ()
        {
            StringBuilder sb = new StringBuilder ("HIERARCHY\n");
            List<int>[] children = new List<int>[joints.Count];
            for (int j = 0; j < joints.Count; j++) children[j] = new List<int> ();
            for (int j = 0; j < joints.Count; j++) {
                if (joints[j].parent >= 0) children[joints[j].parent].Add (j);
            }
            void Write (int j, int depth)
            {
                string indent = new string ('\t', depth);
                Joint joint = joints[j];
                sb.Append (indent).Append (joint.parent < 0 ? "ROOT " : "JOINT ").Append (joint.name).Append ('\n');
                sb.Append (indent).Append ("{\n");
                sb.Append (indent).Append ("\tOFFSET ").Append (Format (joint.offset.x)).Append (' ').Append (Format (joint.offset.y)).Append (' ').Append (Format (joint.offset.z)).Append ('\n');
                sb.Append (indent).Append ("\tCHANNELS ").Append (joint.channels.Length);
                foreach (string channel in joint.channels) sb.Append (' ').Append (channel);
                sb.Append ('\n');
                foreach (int child in children[j]) Write (child, depth + 1);
                sb.Append (indent).Append ("}\n");
            }
            for (int j = 0; j < joints.Count; j++) {
                if (joints[j].parent < 0) Write (j, 0);
            }
            sb.Append ("MOTION\nFrames: ").Append (frames.Count).Append ('\n');
            sb.Append ("Frame Time: ").Append (frameTime.ToString ("R", kInvariant)).Append ('\n');
            foreach (float[] values in frames) {
                for (int c = 0; c < values.Length; c++) {
                    if (c > 0) sb.Append (' ');
                    sb.Append (Format (values[c]));
                }
                sb.Append ('\n');
            }
            return sb.ToString ();
        }

        static string Format (float value)
        {
            return value.ToString ("0.######", kInvariant);
        }

        /// <summary>
        /// 関節の回転のチャンネル（度）を、ファイルの座標系での回転にする。CHANNELS に並んだ順に右へ掛ける
        /// （Zrotation Yrotation Xrotation なら Rz・Ry・Rx。BVH の決まり）
        /// </summary>
        public Quaternion GetRotation (int joint, float[] values)
        {
            Joint j = joints[joint];
            Quaternion rotation = Quaternion.identity;
            for (int c = 0; c < j.channels.Length; c++) {
                int axis = RotationAxis (j.channels[c]);
                if (axis >= 0) rotation *= Quaternion.AngleAxis (values[j.channelStart + c], Axis (axis));
            }
            return rotation;
        }

        /// <summary>位置のチャンネル（無ければ OFFSET）</summary>
        public Vector3 GetPosition (int joint, float[] values)
        {
            Joint j = joints[joint];
            Vector3 position = j.offset;
            for (int c = 0; c < j.channels.Length; c++) {
                int axis = PositionAxis (j.channels[c]);
                if (axis >= 0) position[axis] = values[j.channelStart + c];
            }
            return position;
        }

        /// <summary>関節の回転を、CHANNELS の順の角度（度）にして values へ書く。GetRotation の逆</summary>
        public void SetRotation (int joint, Quaternion rotation, float[] values)
        {
            Joint j = joints[joint];
            List<int> slots = new List<int> ();
            List<int> axes = new List<int> ();
            for (int c = 0; c < j.channels.Length; c++) {
                int axis = RotationAxis (j.channels[c]);
                if (axis < 0) continue;
                slots.Add (j.channelStart + c);
                axes.Add (axis);
            }
            if (axes.Count == 0) return;
            if (axes.Count != 3 || axes[0] == axes[1] || axes[1] == axes[2] || axes[0] == axes[2]) throw new System.NotSupportedException (Tr ("BVH_UNSUPPORTED_ROTATION_ORDER", j.name));
            Vector3 angles = Decompose (rotation, axes[0], axes[1], axes[2]);
            for (int k = 0; k < 3; k++) values[slots[k]] = angles[k];
        }

        /// <summary>位置のチャンネルを values へ書く（位置のチャンネルが無い関節は何もしない）</summary>
        public void SetPosition (int joint, Vector3 position, float[] values)
        {
            Joint j = joints[joint];
            for (int c = 0; c < j.channels.Length; c++) {
                int axis = PositionAxis (j.channels[c]);
                if (axis >= 0) values[j.channelStart + c] = position[axis];
            }
        }

        /// <summary>
        /// 回転を R = Ra(α)·Rb(β)·Rc(γ)（a・b・c は別々の軸）に分けた角度（度）。
        /// 軸の並びが (0,1,2) の偶置換なら s = 1、奇置換なら s = -1 として、sinβ = s·R[a][c]、α = atan2(-s·R[b][c], R[c][c])、γ = atan2(-s·R[a][b], R[a][a])
        /// </summary>
        static Vector3 Decompose (Quaternion rotation, int a, int b, int c)
        {
            Matrix4x4 m = Matrix4x4.Rotate (rotation);
            float s = (b - a + 3) % 3 == 1 ? 1 : -1;
            float beta = Mathf.Asin (Mathf.Clamp (s * m[a, c], -1, 1));
            float alpha = Mathf.Atan2 (-s * m[b, c], m[c, c]);
            float gamma = Mathf.Atan2 (-s * m[a, b], m[a, a]);
            return new Vector3 (alpha, beta, gamma) * Mathf.Rad2Deg;
        }

        static int RotationAxis (string channel)
        {
            switch (channel) {
                case "Xrotation": return 0;
                case "Yrotation": return 1;
                case "Zrotation": return 2;
                default: return -1;
            }
        }

        static int PositionAxis (string channel)
        {
            switch (channel) {
                case "Xposition": return 0;
                case "Yposition": return 1;
                case "Zposition": return 2;
                default: return -1;
            }
        }

        static Vector3 Axis (int axis)
        {
            return axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        }
    }

}
