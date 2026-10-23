using System.Collections.Generic;
using UnityEngine;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 外部の骨格（学習済みのモーション生成モデルやモーキャプの骨）の定義（S72）。T ポーズの階層とオフセット・各関節のチャンネル・
    /// 関節と Humanoid の骨の対応・単位・座標系を持つ。ここから Avatar を作り、Humanoid を通して体型の違うキャラと姿勢をやりとりする。
    /// 包みのルート（Kimodo の Root など人型でない上の関節）も、人型でない関節としてそのまま持つ（書き出すときに同じ形に戻せるように）
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/External Skeleton", fileName = "New External Skeleton", order = 1103)]
    public sealed class ExternalSkeleton : ScriptableObject
    {
        [System.Serializable]
        public sealed class Joint
        {
            public string name = "";
            /// <summary>親の関節の番号（ルートは -1。親は自分より前に並ぶ）</summary>
            public int parent = -1;
            /// <summary>T ポーズでの親からのずれ（ファイルの単位・座標系）</summary>
            public Vector3 offset;
            /// <summary>BVH の CHANNELS（書き出すときの並び）</summary>
            public string[] channels = new string[0];
            /// <summary>対応する Humanoid の骨（無ければ LastBone）</summary>
            public HumanBodyBones human = HumanBodyBones.LastBone;
        }

        public List<Joint> joints = new List<Joint> ();
        /// <summary>ファイルの 1 単位が何メートルか（cm なら 0.01）</summary>
        public float unitScale = 0.01f;
        /// <summary>ファイルが右手系（Y 上）か。右手系なら X を反転して Unity（左手系）へ写す</summary>
        public bool rightHanded = true;

        /// <summary>BVH の階層とオフセットから作る（Humanoid の骨の対応は空。呼ぶ側か名前の表で埋める）</summary>
        public static ExternalSkeleton FromBvh (Bvh bvh)
        {
            ExternalSkeleton skeleton = CreateInstance<ExternalSkeleton> ();
            foreach (Bvh.Joint j in bvh.joints) {
                skeleton.joints.Add (new Joint { name = j.name, parent = j.parent, offset = j.offset, channels = (string[])j.channels.Clone () });
            }
            return skeleton;
        }

        /// <summary>名前と Humanoid の骨の表で対応を埋める（表に無い関節は変えない）</summary>
        public void MapHumanBones (IDictionary<string, HumanBodyBones> table)
        {
            foreach (Joint joint in joints) {
                if (table.TryGetValue (joint.name, out HumanBodyBones bone)) joint.human = bone;
            }
        }

        /// <summary>この定義の形の BVH（フレームは空）</summary>
        public Bvh CreateBvh (float frameTime)
        {
            Bvh bvh = new Bvh { frameTime = frameTime };
            foreach (Joint joint in joints) bvh.AddJoint (joint.name, joint.parent, joint.offset, (string[])joint.channels.Clone ());
            return bvh;
        }

        public Vector3 ToUnity (Vector3 position)
        {
            return (rightHanded ? new Vector3 (-position.x, position.y, position.z) : position) * unitScale;
        }

        public Vector3 FromUnity (Vector3 position)
        {
            position /= unitScale;
            return rightHanded ? new Vector3 (-position.x, position.y, position.z) : position;
        }

        /// <summary>右手系の回転を Unity の回転にする（X を反転した鏡写し。Y・Z まわりの向きが逆になる）</summary>
        public Quaternion ToUnity (Quaternion rotation)
        {
            return rightHanded ? new Quaternion (rotation.x, -rotation.y, -rotation.z, rotation.w) : rotation;
        }

        public Quaternion FromUnity (Quaternion rotation)
        {
            return ToUnity (rotation);
        }

        /// <summary>Avatar を作れない理由（作れるなら null）。必須の Humanoid の骨・名前の重なり・親の並び</summary>
        public string GetProblem ()
        {
            if (joints.Count == 0) return Tr ("EXTERNAL_SKELETON_NO_JOINTS");
            if (unitScale <= 0) return Tr ("EXTERNAL_SKELETON_BAD_UNIT");
            HashSet<string> names = new HashSet<string> ();
            HashSet<HumanBodyBones> mapped = new HashSet<HumanBodyBones> ();
            for (int i = 0; i < joints.Count; i++) {
                Joint joint = joints[i];
                if (string.IsNullOrEmpty (joint.name) || !names.Add (joint.name)) return Tr ("EXTERNAL_SKELETON_DUPLICATE_NAME", joint.name);
                if (joint.parent >= i || (joint.parent < 0 && i > 0)) return Tr ("EXTERNAL_SKELETON_BAD_PARENT", joint.name);
                if (joint.human != HumanBodyBones.LastBone && !mapped.Add (joint.human)) return Tr ("EXTERNAL_SKELETON_DUPLICATE_BONE", joint.human);
            }
            List<string> missing = new List<string> ();
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++) {
                if (HumanTrait.RequiredBone (b) && !mapped.Contains ((HumanBodyBones)b)) missing.Add (((HumanBodyBones)b).ToString ());
            }
            if (missing.Count > 0) return Tr ("EXTERNAL_SKELETON_MISSING_BONES", string.Join (", ", missing));
            return null;
        }

        /// <summary>
        /// T ポーズの骨（隠した GameObject）と Avatar。使い終わったら Dispose で捨てる。
        /// transforms[i] は joints[i] の骨
        /// </summary>
        public sealed class Instance : System.IDisposable
        {
            public GameObject root;
            public Transform[] transforms;
            public Avatar avatar;
            HumanPoseHandler handler_;
            Animator animator_;

            public HumanPoseHandler handler {
                get {
                    if (handler_ == null) handler_ = new HumanPoseHandler (avatar, root.transform);
                    return handler_;
                }
            }

            /// <summary>体の大きさ（Avatar の humanScale。Animator からしか読めないので、要るときに付ける）</summary>
            public float humanScale {
                get {
                    if (animator_ == null) {
                        animator_ = root.AddComponent<Animator> ();
                        animator_.avatar = avatar;
                    }
                    return animator_.humanScale;
                }
            }

            public void Dispose ()
            {
                handler_?.Dispose ();
                handler_ = null;
                if (root != null) Object.DestroyImmediate (root);
                if (avatar != null) Object.DestroyImmediate (avatar);
            }
        }

        /// <summary>T ポーズの骨と Avatar を作る。作れなければ null を返し、error に理由を入れる（何も残さない）</summary>
        public Instance Instantiate (out string error)
        {
            error = GetProblem ();
            if (error != null) return null;

            Instance instance = new Instance ();
            instance.root = new GameObject (name + " (External Skeleton)") { hideFlags = HideFlags.HideAndDontSave };
            instance.transforms = new Transform[joints.Count];
            for (int i = 0; i < joints.Count; i++) {
                Transform t = new GameObject (joints[i].name) { hideFlags = HideFlags.HideAndDontSave }.transform;
                t.SetParent (joints[i].parent < 0 ? instance.root.transform : instance.transforms[joints[i].parent], false);
                t.localPosition = ToUnity (joints[i].offset);
                instance.transforms[i] = t;
            }

            List<HumanBone> human = new List<HumanBone> ();
            foreach (Joint joint in joints) {
                if (joint.human == HumanBodyBones.LastBone) continue;
                human.Add (new HumanBone { boneName = joint.name, humanName = HumanTrait.BoneName[(int)joint.human], limit = new HumanLimit { useDefaultValues = true } });
            }
            List<SkeletonBone> skeleton = new List<SkeletonBone> ();
            foreach (Transform t in instance.root.GetComponentsInChildren<Transform> ()) {
                skeleton.Add (new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            }
            HumanDescription description = new HumanDescription {
                human = human.ToArray (),
                skeleton = skeleton.ToArray (),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0,
                hasTranslationDoF = false,
            };
            instance.avatar = AvatarBuilder.BuildHumanAvatar (instance.root, description);
            instance.avatar.hideFlags = HideFlags.HideAndDontSave;
            if (!instance.avatar.isValid || !instance.avatar.isHuman) {
                instance.Dispose ();
                error = Tr ("EXTERNAL_SKELETON_AVATAR_INVALID");
                return null;
            }
            return instance;
        }

        /// <summary>
        /// BVH の 1 フレームを骨へ当てる。関節は名前で引く（BVH に無い関節は T ポーズのまま）。位置のチャンネルのある関節は位置も当てる
        /// </summary>
        public void Apply (Instance instance, Bvh bvh, int frame)
        {
            float[] values = bvh.frames[frame];
            for (int i = 0; i < joints.Count; i++) {
                Transform t = instance.transforms[i];
                int j = bvh.IndexOf (joints[i].name);
                if (j < 0) {
                    t.localPosition = ToUnity (joints[i].offset);
                    t.localRotation = Quaternion.identity;
                    continue;
                }
                t.localPosition = ToUnity (bvh.GetPosition (j, values));
                t.localRotation = ToUnity (bvh.GetRotation (j, values));
            }
        }

        /// <summary>骨の今の姿勢を BVH の 1 フレームにして足す（bvh は CreateBvh で作ったもの）</summary>
        public void Capture (Instance instance, Bvh bvh)
        {
            float[] values = new float[bvh.channelCount];
            for (int i = 0; i < joints.Count; i++) {
                int j = bvh.IndexOf (joints[i].name);
                if (j < 0) continue;
                Transform t = instance.transforms[i];
                bvh.SetPosition (j, FromUnity (t.localPosition), values);
                bvh.SetRotation (j, FromUnity (t.localRotation), values);
            }
            bvh.frames.Add (values);
        }

        /// <summary>BVH の階層がこの定義と合わない理由（合えば null）。Humanoid の骨に対応する関節が BVH に揃っているか</summary>
        public string GetMismatch (Bvh bvh)
        {
            List<string> missing = new List<string> ();
            foreach (Joint joint in joints) {
                if (joint.human != HumanBodyBones.LastBone && bvh.IndexOf (joint.name) < 0) missing.Add (joint.name);
            }
            return missing.Count > 0 ? Tr ("EXTERNAL_SKELETON_BVH_MISSING_JOINTS", string.Join (", ", missing)) : null;
        }
    }

}
