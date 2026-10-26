using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// キーの間で Locked の足（S25d）。足を回しながら動かしたキーの間でも、足の点の組は形を保って剛体として動き、
    /// 向きを留めた足首も同じだけ回る（10-26 不具合「左のつま先が 17・23・27F で 20〜30° 曲がり方を変える」。
    /// 点ごとのカーブの膨らみを残していたので足の形が縮み、足首の向きは別に補間していたので、足裏の点に 1〜3cm 届かず、
    /// 届かない分をつま先の曲げで合わせようとして、フレームごとに曲がり方が飛んだ）
    /// </summary>
    public class LockedSpanFootTests
    {
        const string kFolder = "Assets/__MktLockedSpanFootTests";
        static readonly string[] kFoot = { "LeftFoot", "LeftHeel", "LeftSoleBall", "LeftLittleToeSide", "LeftToeTip" };

        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;
        GameObject prefab_;
        PoseSession session_;

        [SetUp]
        public void SetUp ()
        {
            if (AssetDatabase.IsValidFolder (kFolder)) AssetDatabase.DeleteAsset (kFolder);
            AssetDatabase.CreateFolder ("Assets", "__MktLockedSpanFootTests");
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            prefab_ = new GameObject ("Prefab");
            created_.Add (prefab_);
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab_.transform, false);
        }

        [TearDown]
        public void TearDown ()
        {
            session_?.Dispose ();
            session_ = null;
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            AssetDatabase.DeleteAsset (kFolder);
        }

        BodyPointTarget Target (string name)
        {
            GameObject control = session_.stage.fullBody.GetControl (new BodyPointId (name)).gameObject;
            return session_.targets.OfType<BodyPointTarget> ().First (t => t.gameObject == control);
        }

        [Test]
        public void TurningLockedFoot_KeepsItsShape_ReachesThePoints_AndTheToeDoesNotJump ()
        {
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            AssetDatabase.CreateAsset (clip, kFolder + "/Turn.rig.anim");
            session_ = new PoseSession (prefab_, definition_);
            session_.clip = clip;
            FullBodyRig body = session_.stage.fullBody;
            string[] names = kFoot.Where (n => body.HasPoint (new BodyPointId (n))).ToArray ();
            Assert.GreaterOrEqual (names.Length, 4, "足の点がある");

            // 0・30・60F に、左足の点を母指球のまわりに 0・40・80° 回して少しずつ動かした所で Locked（足首は向きも留める）
            session_.Evaluate (0);
            Dictionary<string, Vector3> start = new Dictionary<string, Vector3> ();
            Quaternion ankle = Quaternion.identity;
            foreach (string name in names) {
                Assert.IsTrue (Target (name).TryGetValues (out Vector3 position, out Quaternion rotation));
                start[name] = position;
                if (name == "LeftFoot") ankle = rotation;
            }
            Vector3 pivot = start["LeftSoleBall"];
            int[] frames = { 0, 30, 60 };
            float[] yaw = { 0, 40, 80 };
            Vector3[] move = { Vector3.zero, new Vector3 (0.02f, 0, 0.04f), new Vector3 (0.06f, 0, 0.06f) };
            for (int k = 0; k < frames.Length; k++) {
                session_.Evaluate (frames[k]);
                Quaternion turn = Quaternion.AngleAxis (yaw[k], Vector3.up);
                foreach (string name in names) Target (name).SetValues (TransformChannels.Position, pivot + turn * (start[name] - pivot) + move[k], Quaternion.identity);
                Target ("LeftFoot").SetValues (TransformChannels.Rotation, Vector3.zero, turn * ankle);
                foreach (string name in names) Target (name).SetLocked (true);
                session_.SolveAfterEdit ();
                Assert.IsNull (session_.CommitPose ());
            }

            // 点ごとのカーブの膨らみ（直線から外れる分）が点ごとに違う（直す前は、これを点ごとに残して足の形が崩れた）
            float Bulge (string name, string axis) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, EditorCurveBinding.FloatCurve (RigPaths.BodyPoint (name), typeof (Transform), CurveEdit.kPosition + "." + axis));
                return curve.Evaluate (15 / 60f) - Mathf.Lerp (curve.Evaluate (0), curve.Evaluate (0.5f), 0.5f);
            }
            float spread = new Vector2 (Bulge ("LeftHeel", "x") - Bulge ("LeftToeTip", "x"), Bulge ("LeftHeel", "z") - Bulge ("LeftToeTip", "z")).magnitude;
            Assert.Greater (spread, 0.005f, "踵とつま先の先でカーブの膨らみが違う（確かめたい入力になっている）");

            Transform toes = session_.stage.editingRig.GetEditingBone (HumanBodyBones.LeftToes);
            float shape = 0, miss = 0, toeStep = 0;
            Quaternion? previous = null;
            for (int f = 1; f < 60; f++) {
                session_.Evaluate (f);
                Quaternion toe = toes.localRotation;
                if (previous.HasValue) toeStep = Mathf.Max (toeStep, Quaternion.Angle (previous.Value, toe));
                previous = toe;
                if (f == 30) continue;
                Dictionary<string, Vector3> placed = new Dictionary<string, Vector3> ();
                Dictionary<string, Vector3> onBone = new Dictionary<string, Vector3> ();
                foreach (string name in names) {
                    Assert.IsTrue (body.TryGetPointWorld (new BodyPointId (name), out Vector3 point, out Quaternion rotation));
                    Assert.IsTrue (body.TryGetBonePointWorld (new BodyPointId (name), out Vector3 bone));
                    placed[name] = point;
                    onBone[name] = bone;
                    miss = Mathf.Max (miss, Vector3.Distance (point, bone));
                }
                foreach (string other in names.Where (n => n != "LeftHeel")) {
                    shape = Mathf.Max (shape, Mathf.Abs (Vector3.Distance (placed["LeftHeel"], placed[other]) - Vector3.Distance (onBone["LeftHeel"], onBone[other])));
                }
            }
            Assert.LessOrEqual (shape, 0.003f, "キーの間でも、足の点の組は足の形のまま（踵からの距離）");
            Assert.LessOrEqual (miss, 0.005f, "キーの間でも、足の点に届く");
            Assert.LessOrEqual (toeStep, 3f, "つま先の曲がり方が 1 フレームで飛ばない");
        }
    }

}
