using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 外部の骨格の定義と BVH の読み書き（S72）
    /// </summary>
    public class BvhTests
    {
        const float kPositionTolerance = 0.01f;
        const float kAngleTolerance = 1f;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        /// <summary>
        /// Kimodo の出す形の BVH（包みの Root・腰も位置を持つ・回転は ZYX・単位は cm・End Site 無し・右手系で体は +Z を向く）。
        /// 骨の長さは TestSkeleton と同じ。人型でない関節（HeadEnd・LeftHandMiddle1）も混ぜる
        /// </summary>
        const string kKimodoLike =
            "HIERARCHY\n" +
            "ROOT Root\n{\n  OFFSET 0.0 0.0 0.0\n  CHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation\n" +
            "  JOINT Hips\n  {\n    OFFSET 0.0 100.0 0.0\n    CHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation\n" +
            "    JOINT Spine1\n    {\n      OFFSET 0.0 15.0 0.0\n      CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "      JOINT Chest\n      {\n        OFFSET 0.0 15.0 0.0\n        CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "        JOINT Neck1\n        {\n          OFFSET 0.0 20.0 0.0\n          CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "          JOINT Head\n          {\n            OFFSET 0.0 10.0 0.0\n            CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "            JOINT HeadEnd\n            {\n              OFFSET 0.0 15.0 0.0\n              CHANNELS 3 Zrotation Yrotation Xrotation\n            }\n" +
            "          }\n        }\n" +
            "        JOINT LeftArm\n        {\n          OFFSET 20.0 15.0 0.0\n          CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "          JOINT LeftForeArm\n          {\n            OFFSET 30.0 0.0 0.0\n            CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "            JOINT LeftHand\n            {\n              OFFSET 25.0 0.0 0.0\n              CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "              JOINT LeftHandMiddle1\n              {\n                OFFSET 8.0 0.0 0.0\n                CHANNELS 3 Zrotation Yrotation Xrotation\n              }\n" +
            "            }\n          }\n        }\n" +
            "        JOINT RightArm\n        {\n          OFFSET -20.0 15.0 0.0\n          CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "          JOINT RightForeArm\n          {\n            OFFSET -30.0 0.0 0.0\n            CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "            JOINT RightHand\n            {\n              OFFSET -25.0 0.0 0.0\n              CHANNELS 3 Zrotation Yrotation Xrotation\n            }\n" +
            "          }\n        }\n" +
            "      }\n    }\n" +
            "    JOINT LeftLeg\n    {\n      OFFSET 10.0 -5.0 0.0\n      CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "      JOINT LeftShin\n      {\n        OFFSET 0.0 -45.0 0.0\n        CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "        JOINT LeftFoot\n        {\n          OFFSET 0.0 -45.0 0.0\n          CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "          JOINT LeftToeBase\n          {\n            OFFSET 0.0 -3.0 12.0\n            CHANNELS 3 Zrotation Yrotation Xrotation\n          }\n" +
            "        }\n      }\n    }\n" +
            "    JOINT RightLeg\n    {\n      OFFSET -10.0 -5.0 0.0\n      CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "      JOINT RightShin\n      {\n        OFFSET 0.0 -45.0 0.0\n        CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "        JOINT RightFoot\n        {\n          OFFSET 0.0 -45.0 0.0\n          CHANNELS 3 Zrotation Yrotation Xrotation\n" +
            "          JOINT RightToeBase\n          {\n            OFFSET 0.0 -3.0 12.0\n            CHANNELS 3 Zrotation Yrotation Xrotation\n          }\n" +
            "        }\n      }\n    }\n" +
            "  }\n}\n";

        static readonly Dictionary<string, HumanBodyBones> kKimodoLikeBones = new Dictionary<string, HumanBodyBones> {
            { "Hips", HumanBodyBones.Hips }, { "Spine1", HumanBodyBones.Spine }, { "Chest", HumanBodyBones.Chest },
            { "Neck1", HumanBodyBones.Neck }, { "Head", HumanBodyBones.Head },
            { "LeftArm", HumanBodyBones.LeftUpperArm }, { "LeftForeArm", HumanBodyBones.LeftLowerArm }, { "LeftHand", HumanBodyBones.LeftHand },
            { "RightArm", HumanBodyBones.RightUpperArm }, { "RightForeArm", HumanBodyBones.RightLowerArm }, { "RightHand", HumanBodyBones.RightHand },
            { "LeftLeg", HumanBodyBones.LeftUpperLeg }, { "LeftShin", HumanBodyBones.LeftLowerLeg }, { "LeftFoot", HumanBodyBones.LeftFoot }, { "LeftToeBase", HumanBodyBones.LeftToes },
            { "RightLeg", HumanBodyBones.RightUpperLeg }, { "RightShin", HumanBodyBones.RightLowerLeg }, { "RightFoot", HumanBodyBones.RightFoot }, { "RightToeBase", HumanBodyBones.RightToes },
        };

        /// <summary>Kimodo の形の BVH（MOTION を足したもの）を読み、骨格の定義を作る</summary>
        ExternalSkeleton KimodoLikeSkeleton (out Bvh bvh, string motion)
        {
            bvh = Bvh.Parse (kKimodoLike + motion, out string error);
            Assert.IsNull (error);
            ExternalSkeleton skeleton = ExternalSkeleton.FromBvh (bvh);
            created_.Add (skeleton);
            skeleton.MapHumanBones (kKimodoLikeBones);
            return skeleton;
        }

        /// <summary>Kimodo の形の骨格の定義（ほかのテスト用。骨の長さを scale 倍にする）</summary>
        internal static ExternalSkeleton CreateKimodoLike (List<Object> created, float scale = 1)
        {
            Bvh bvh = Bvh.Parse (kKimodoLike + ZeroFrames (0, 0), out string error);
            Assert.IsNull (error);
            ExternalSkeleton skeleton = ExternalSkeleton.FromBvh (bvh);
            created.Add (skeleton);
            skeleton.MapHumanBones (kKimodoLikeBones);
            foreach (ExternalSkeleton.Joint joint in skeleton.joints) joint.offset *= scale;
            return skeleton;
        }

        ExternalSkeleton.Instance Instantiate (ExternalSkeleton skeleton)
        {
            ExternalSkeleton.Instance instance = skeleton.Instantiate (out string error);
            Assert.IsNull (error);
            disposables_.Add (instance);
            return instance;
        }

        static string ZeroFrames (int frames, int channels)
        {
            string line = string.Join (" ", Enumerable.Repeat ("0", channels));
            return "MOTION\nFrames: " + frames + "\nFrame Time: 0.0333333\n" + string.Concat (Enumerable.Repeat (line + "\n", frames));
        }

        [Test]
        public void KimodoLikeBvh_IsRead_AndRotationsFollowTheChannelOrder ()
        {
            // 2 フレーム目: 腰を 1m 横へ、左肘を Z まわり 90°・Y まわり 30°（ZYX の順に掛ける）
            Bvh probe = Bvh.Parse (kKimodoLike + ZeroFrames (1, 1), out string probeError);
            Assert.IsNotNull (probeError, "値の数が合わない BVH は読めない");
            int channels = 6 + 6 + 3 * 20;
            string zero = string.Join (" ", Enumerable.Repeat ("0", channels));
            float[] second = new float[channels];
            second[6] = 100; // Hips Xposition（cm）
            second[7] = 100;
            int foreArm = 6 + 6 + 3 * 6; // Root・Hips の後、Spine1 Chest Neck1 Head HeadEnd LeftArm の次
            second[foreArm] = 90;
            second[foreArm + 1] = 30;
            string motion = "MOTION\nFrames: 2\nFrame Time: 0.0333333\n" + zero + "\n" + string.Join (" ", second.Select (v => v.ToString (System.Globalization.CultureInfo.InvariantCulture))) + "\n";
            ExternalSkeleton skeleton = KimodoLikeSkeleton (out Bvh bvh, motion);
            Assert.AreEqual (22, bvh.joints.Count, "End Site は関節にしない");
            Assert.AreEqual (2, bvh.frames.Count);
            Assert.AreEqual (1f / 30f, bvh.frameTime, 1e-5f);

            ExternalSkeleton.Instance instance = Instantiate (skeleton);
            skeleton.Apply (instance, bvh, 1);
            Transform hips = instance.transforms[bvh.IndexOf ("Hips")];
            Assert.LessOrEqual (Vector3.Distance (new Vector3 (-1, 1, 0), hips.localPosition), 1e-4f, "cm をメートルへ、右手系の X を反転");
            Quaternion expected = skeleton.ToUnity (Quaternion.AngleAxis (90, Vector3.forward) * Quaternion.AngleAxis (30, Vector3.up));
            Assert.LessOrEqual (Quaternion.Angle (expected, instance.transforms[bvh.IndexOf ("LeftForeArm")].localRotation), 0.01f, "Rz・Ry の順に掛ける");

            // 書いて読み直すと同じ値
            Bvh again = Bvh.Parse (bvh.ToText (), out string error);
            Assert.IsNull (error);
            for (int f = 0; f < 2; f++) {
                for (int c = 0; c < channels; c++) Assert.AreEqual (bvh.frames[f][c], again.frames[f][c], 1e-4f);
            }
        }

        [Test]
        public void EveryRotationOrder_RoundTrips ()
        {
            string[][] orders = {
                new[] { "Xrotation", "Yrotation", "Zrotation" }, new[] { "Xrotation", "Zrotation", "Yrotation" },
                new[] { "Yrotation", "Xrotation", "Zrotation" }, new[] { "Yrotation", "Zrotation", "Xrotation" },
                new[] { "Zrotation", "Xrotation", "Yrotation" }, new[] { "Zrotation", "Yrotation", "Xrotation" },
            };
            Random.State state = Random.state;
            Random.InitState (72);
            try {
                foreach (string[] order in orders) {
                    Bvh bvh = new Bvh ();
                    bvh.AddJoint ("Joint", -1, Vector3.zero, order);
                    float[] values = new float[3];
                    for (int i = 0; i < 20; i++) {
                        Quaternion rotation = Random.rotationUniform;
                        bvh.SetRotation (0, rotation, values);
                        Assert.LessOrEqual (Quaternion.Angle (rotation, bvh.GetRotation (0, values)), 0.01f, string.Join (" ", order));
                    }
                }
            }
            finally {
                Random.state = state;
            }
        }

        [Test]
        public void HumanoidMotion_ThroughBvh_ComesBackTheSame ()
        {
            // テストの骨の動きを、外部の骨格へ Humanoid で写して BVH に書き、読み戻して元の骨へ写し直す
            Animator character = TestSkeleton.CreateHumanoidWithToes (created_);
            ExternalSkeleton skeleton = KimodoLikeSkeleton (out Bvh unused, ZeroFrames (0, 0));
            ExternalSkeleton.Instance writer = Instantiate (skeleton);
            ExternalSkeleton.Instance reader = Instantiate (skeleton);
            HumanPoseHandler characterPose = new HumanPoseHandler (character.avatar, character.transform);
            disposables_.Add (characterPose);

            string[] muscles = { "Spine Front-Back", "Left Upper Leg Front-Back", "Right Lower Leg Stretch", "Left Arm Down-Up", "Right Forearm Stretch", "Head Turn Left-Right", "Left Foot Up-Down" };
            int[] indices = muscles.Select (m => System.Array.IndexOf (HumanTrait.MuscleName, m)).ToArray ();
            Assert.IsFalse (indices.Contains (-1));

            HumanPose pose = new HumanPose ();
            characterPose.GetHumanPose (ref pose);
            List<HumanPose> poses = new List<HumanPose> ();
            Bvh bvh = skeleton.CreateBvh (1f / 30f);
            for (int f = 0; f < 5; f++) {
                HumanPose p = pose;
                p.muscles = (float[])pose.muscles.Clone ();
                for (int m = 0; m < indices.Length; m++) p.muscles[indices[m]] = 0.6f * Mathf.Sin (f * 0.7f + m);
                p.bodyPosition = pose.bodyPosition + new Vector3 (0.1f * f, 0, 0.2f * f);
                p.bodyRotation = Quaternion.Euler (0, 20 * f, 5);
                poses.Add (p);
                writer.handler.SetHumanPose (ref p);
                skeleton.Capture (writer, bvh);
            }

            Bvh read = Bvh.Parse (bvh.ToText (), out string error);
            Assert.IsNull (error);
            Assert.IsNull (skeleton.GetMismatch (read));
            Transform[] bones = Enumerable.Range (0, (int)HumanBodyBones.LastBone).Select (b => character.GetBoneTransform ((HumanBodyBones)b)).Where (t => t != null).ToArray ();
            for (int f = 0; f < poses.Count; f++) {
                HumanPose expected = poses[f];
                characterPose.SetHumanPose (ref expected);
                List<Pose> before = bones.Select (t => new Pose (t.position, t.rotation)).ToList ();

                skeleton.Apply (reader, read, f);
                HumanPose back = new HumanPose ();
                reader.handler.GetHumanPose (ref back);
                characterPose.SetHumanPose (ref back);
                for (int b = 0; b < bones.Length; b++) {
                    Assert.LessOrEqual (Vector3.Distance (before[b].position, bones[b].position), kPositionTolerance, f + " フレーム目の " + bones[b].name + " の位置");
                    Assert.LessOrEqual (Quaternion.Angle (before[b].rotation, bones[b].rotation), kAngleTolerance, f + " フレーム目の " + bones[b].name + " の向き");
                }
            }
        }

        [Test]
        public void Avatar_IsBuiltWithoutWarnings ()
        {
            List<string> logs = new List<string> ();
            void OnLog (string message, string stackTrace, LogType type)
            {
                if (type != LogType.Log) logs.Add (type + ": " + message);
            }
            Application.logMessageReceived += OnLog;
            try {
                ExternalSkeleton skeleton = KimodoLikeSkeleton (out Bvh bvh, ZeroFrames (1, 6 + 6 + 3 * 20));
                ExternalSkeleton.Instance instance = Instantiate (skeleton);
                Assert.IsTrue (instance.avatar.isHuman);
            }
            finally {
                Application.logMessageReceived -= OnLog;
            }
            Assert.AreEqual (0, logs.Count, string.Join ("\n", logs));
        }

        static int CountSkeletonObjects ()
        {
            return Resources.FindObjectsOfTypeAll<GameObject> ().Count (g => g.name.EndsWith ("(External Skeleton)"));
        }

        [Test]
        public void BadInputs_ReturnReasons_AndLeaveNothing ()
        {
            string error;
            Assert.IsNull (Bvh.Parse ("not a bvh", out error));
            Assert.IsNotNull (error);
            Assert.IsNull (Bvh.Parse ("", out error));
            Assert.IsNotNull (error);
            Assert.IsNull (Bvh.Parse (kKimodoLike, out error), "MOTION が無い");
            Assert.IsNotNull (error);
            Assert.IsNull (Bvh.Parse (kKimodoLike + "MOTION\nFrames: 1\nFrame Time: 0.0333\n0 0 0\n", out error), "値の数が合わない");
            Assert.IsNotNull (error);
            Assert.IsNull (Bvh.Parse (kKimodoLike.Replace ("Zrotation Yrotation Xrotation\n            }", "Zrotation Wrotation Xrotation\n            }") + ZeroFrames (0, 0), out error), "知らないチャンネル");
            Assert.IsNotNull (error);

            int before = CountSkeletonObjects ();
            ExternalSkeleton skeleton = KimodoLikeSkeleton (out Bvh bvh, ZeroFrames (0, 0));
            skeleton.joints.First (j => j.name == "LeftShin").human = HumanBodyBones.LastBone;
            Assert.IsNull (skeleton.Instantiate (out error), "必須の骨（左のすね）が無い");
            StringAssert.Contains ("LeftLowerLeg", error);
            Assert.AreEqual (before, CountSkeletonObjects (), "何も作らない");

            skeleton.MapHumanBones (kKimodoLikeBones);
            skeleton.joints.First (j => j.name == "HeadEnd").human = HumanBodyBones.Head;
            Assert.IsNull (skeleton.Instantiate (out error), "同じ骨に 2 つの関節");
            Assert.IsNotNull (error);
            Assert.AreEqual (before, CountSkeletonObjects ());

            // 対応する関節が BVH に無い
            skeleton.MapHumanBones (kKimodoLikeBones);
            skeleton.joints.First (j => j.name == "HeadEnd").human = HumanBodyBones.LastBone;
            Bvh partial = new Bvh ();
            partial.AddJoint ("Hips", -1, Vector3.zero, "Xposition", "Yposition", "Zposition", "Zrotation", "Yrotation", "Xrotation");
            StringAssert.Contains ("LeftShin", skeleton.GetMismatch (partial));
        }
    }

}
