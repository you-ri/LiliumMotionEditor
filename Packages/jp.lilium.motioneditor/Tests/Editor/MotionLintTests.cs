using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// モーションの検査（S37）。わざと壊したモーションで、壊した項目だけが出る。正常なモーションでは何も出ない
    /// </summary>
    public class MotionLintTests
    {
        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();
        EditRigDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
        }

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

        PoseSession Open ()
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            // 足を踵・つま先の先・小指側で置くので、つま先の骨のあるキャラにする（足の点はつま先の骨のあるキャラにだけできる）
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition_);
            disposables_.Add (session);
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            session.clip = clip;
            return session;
        }

        static PoseDocument Rest (PoseSession session)
        {
            string problem;
            PoseDocument rest = PoseDocument.Read (session, 0, out problem);
            Assert.IsNull (problem);
            return rest;
        }

        static void Write (PoseSession session, int frame, PoseDocument rest, params (string name, string state, Vector3 offset)[] points)
        {
            PoseDocument document = new PoseDocument { frame = frame, humanScale = rest.humanScale };
            foreach ((string name, string state, Vector3 offset) in points) {
                document.points.Add (new PosePoint { name = name, state = state, position = rest.Find (name).position + offset });
            }
            Assert.IsNull (PoseDocument.Write (session, document), frame + "F に書ける");
        }

        /// <summary>
        /// 足を置く点（かかと・つま先の先・小指側）。足首の点 1 つだけを固定すると、足は足首まわりに回れるので、キーの間でつま先が床に沈む
        /// </summary>
        static (string, string, Vector3)[] Foot (string side, string state, Vector3 offset)
        {
            return new[] { (side + "Heel", state, offset), (side + "ToeTip", state, offset), (side + "LittleToeSide", state, offset) };
        }

        static (string, string, Vector3)[] Join (params (string, string, Vector3)[][] groups)
        {
            return groups.SelectMany (g => g).ToArray ();
        }

        /// <summary>しゃがんで戻る（両足を Locked のまま腰を下げる）。正しいモーション</summary>
        static void Squat (PoseSession session)
        {
            PoseDocument rest = Rest (session);
            (string, string, Vector3)[] feet = Join (Foot ("Left", "Locked", Vector3.zero), Foot ("Right", "Locked", Vector3.zero));
            Write (session, 0, rest, feet);
            Write (session, 30, rest, Join (feet, new[] { ("Hips", "Pin", new Vector3 (0, -0.12f, 0.03f)) }));
            Write (session, 60, rest, Join (feet, new[] { ("Hips", "Pin", Vector3.zero) }));
        }

        static string Describe (MotionLintReport report)
        {
            return string.Join ("\n", report.issues.Select (i => i.kind + " " + i.target + " " + i.frame + "-" + i.endFrame + " " + i.value + " / " + i.limit));
        }

        [Test]
        public void CleanMotion_HasNoIssues_AndIsFast ()
        {
            PoseSession session = Open ();
            Squat (session);
            MotionLint.Run (session, 0, 5);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();
            MotionLintReport report = MotionLint.Run (session, 0, 60);
            watch.Stop ();
            Assert.IsNull (report.error);
            Assert.IsEmpty (report.issues, Describe (report));
            Assert.Less (report.maxAsymmetry, 0.05f, "左右対称の動き");
            // 60 フレーム ≤0.5s（Burst）。短いバッチ実行では Burst が間に合わずマネージドで解くので、2s まで許す
            Assert.Less (watch.Elapsed.TotalSeconds, session.stage.fullBody.lastSolveUsedBurst ? 0.5 : 2.0, watch.Elapsed.TotalSeconds + "s");
            Assert.AreEqual (60, report.endFrame);
            StringAssert.Contains ("\"issues\"", report.ToJson ());
        }

        [Test]
        public void SlidingFoot_IsFound ()
        {
            // 左足を床の上で前へ 20cm 滑らせる（Pin なのでキーの間は留めない）
            PoseSession session = Open ();
            PoseDocument rest = Rest (session);
            Write (session, 0, rest, Join (Foot ("Left", "Pin", Vector3.zero), Foot ("Right", "Locked", Vector3.zero)));
            Write (session, 20, rest, Join (Foot ("Left", "Pin", new Vector3 (0, 0, 0.2f)), Foot ("Right", "Locked", Vector3.zero)));

            MotionLintReport report = MotionLint.Run (session, 0, 20);
            Assert.Greater (report.Count (MotionLint.kFootSlide), 0, Describe (report));
            Assert.IsTrue (report.issues.Where (i => i.kind == MotionLint.kFootSlide).All (i => i.target.StartsWith ("Left")), "滑らせた左足だけ: " + Describe (report));
            // 床への沈み込みは、固定した右足には出ない（Pin の左足はキーの間で留めないので、少し沈むことがある）
            Assert.IsFalse (report.issues.Any (i => i.kind == MotionLint.kFloorPenetration && i.target.StartsWith ("Right")), Describe (report));
            Assert.AreEqual (0, report.Count (MotionLint.kPop), Describe (report));
        }

        [Test]
        public void FootSwungLowAndFast_IsNotASlide ()
        {
            // 左足を床すれすれ（1cm 上）で 4 フレームに 30cm 前へ振る（秒速 4.5m）。床から 3cm 以内でも、振り抜ける足は接地ではない
            PoseSession session = Open ();
            PoseDocument rest = Rest (session);
            Vector3 lifted = new Vector3 (0, 0.01f, 0);
            Write (session, 0, rest, Join (Foot ("Left", "Pin", lifted), Foot ("Right", "Locked", Vector3.zero)));
            Write (session, 4, rest, Join (Foot ("Left", "Pin", lifted + new Vector3 (0, 0, 0.3f)), Foot ("Right", "Locked", Vector3.zero)));

            MotionLintReport report = MotionLint.Run (session, 0, 4);
            Assert.AreEqual (0, report.Count (MotionLint.kFootSlide), Describe (report));

            // 同じ動きでも、速さの上限を外すと（高さだけで接地とみなすと）滑りとして数えていた
            MotionLintReport heightOnly = MotionLint.Run (session, 0, 4, new MotionLint.Settings { contactSpeed = float.PositiveInfinity });
            Assert.Greater (heightOnly.Count (MotionLint.kFootSlide), 0, "確かめたい動きになっている: " + Describe (heightOnly));
        }

        [Test]
        public void SunkenFoot_IsFound ()
        {
            PoseSession session = Open ();
            PoseDocument rest = Rest (session);
            Write (session, 10, rest, Join (Foot ("Left", "Locked", new Vector3 (0, -0.06f, 0)), Foot ("Right", "Locked", Vector3.zero)));

            MotionLintReport report = MotionLint.Run (session, 10, 10);
            Assert.Greater (report.Count (MotionLint.kFloorPenetration), 0, Describe (report));
            Assert.IsTrue (report.issues.Where (i => i.kind == MotionLint.kFloorPenetration).All (i => i.target.StartsWith ("Left")), Describe (report));
        }

        [Test]
        public void HipsMovingTooFast_IsFound ()
        {
            // 1 フレームで腰を 30cm 動かす（60fps で 18m/s）
            PoseSession session = Open ();
            PoseDocument rest = Rest (session);
            Write (session, 0, rest, ("Hips", "Pin", Vector3.zero));
            Write (session, 1, rest, ("Hips", "Pin", new Vector3 (0, 0, 0.3f)));
            Write (session, 20, rest, ("Hips", "Pin", new Vector3 (0, 0, 0.3f)));

            MotionLintReport report = MotionLint.Run (session, 0, 20);
            MotionLintIssue[] fast = report.issues.Where (i => i.kind == MotionLint.kHipSpeed).ToArray ();
            Assert.AreEqual (1, fast.Length, Describe (report));
            Assert.AreEqual (1, fast[0].frame, "動いた 1 フレームだけ");
            Assert.Greater (fast[0].value, 15, "m/s");
        }

        [Test]
        public void ArmBeyondItsRange_IsFound ()
        {
            PoseSession session = Open ();
            session.Evaluate (0);
            Transform arm = session.stage.editingRig.FindControl (RigPaths.Fk (HumanBodyBones.LeftUpperArm));
            arm.localRotation *= Quaternion.Euler (0, 0, 170);
            session.SolveAfterEdit ();
            Assert.IsNull (session.CommitPose ());

            MotionLintReport report = MotionLint.Run (session, 0, 0);
            Assert.Greater (report.Count (MotionLint.kMuscleRange), 0, Describe (report));
            Assert.IsTrue (report.issues.Where (i => i.kind == MotionLint.kMuscleRange).All (i => i.target.StartsWith ("Left")), Describe (report));
            Assert.Greater (report.maxAsymmetry, 0.1f, "左腕だけ回したので左右が違う");
        }

        [Test]
        public void OneFramePop_IsFound ()
        {
            // 背骨を 0〜30F で 30° 回す。15F だけ 25° 跳ねる
            PoseSession session = Open ();
            session.Evaluate (0);
            string path = RigPaths.Fk (HumanBodyBones.Spine);
            Quaternion rest = session.stage.editingRig.FindControl (path).localRotation;
            AnimationCurve[] curves = { new AnimationCurve (), new AnimationCurve (), new AnimationCurve (), new AnimationCurve () };
            for (int f = 0; f <= 30; f++) {
                Quaternion q = rest * Quaternion.Euler (f + (f == 15 ? 25 : 0), 0, 0);
                for (int i = 0; i < 4; i++) curves[i].AddKey (new Keyframe (f / 60f, q[i]));
            }
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                for (int k = 0; k < curves[i].length; k++) {
                    AnimationUtility.SetKeyLeftTangentMode (curves[i], k, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode (curves[i], k, AnimationUtility.TangentMode.Linear);
                }
                AnimationUtility.SetEditorCurve (session.clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), curves[i]);
            }
            session.stage.InvalidateClipCurves ();

            MotionLintReport report = MotionLint.Run (session, 0, 30);
            MotionLintIssue[] pops = report.issues.Where (i => i.kind == MotionLint.kPop).ToArray ();
            Assert.IsNotEmpty (pops, Describe (report));
            Assert.IsTrue (pops.All (i => i.frame >= 14 && i.endFrame <= 17), "15F の前後だけ: " + Describe (report));
            Assert.IsTrue (pops.Any (i => i.target.Contains ("Spine")), Describe (report));
        }

        [Test]
        public void LargeRotationBetweenKeys_IsFound ()
        {
            PoseSession session = Open ();
            session.Evaluate (0);
            string path = RigPaths.Fk (HumanBodyBones.Spine);
            Quaternion rest = session.stage.editingRig.FindControl (path).localRotation;
            Quaternion end = rest * Quaternion.Euler (0, 120, 0);
            string[] axes = { "x", "y", "z", "w" };
            for (int i = 0; i < 4; i++) {
                AnimationUtility.SetEditorCurve (session.clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalRotation." + axes[i]), AnimationCurve.Linear (0, rest[i], 0.5f, end[i]));
            }
            session.stage.InvalidateClipCurves ();

            MotionLintReport report = MotionLint.Run (session, 0, 30);
            MotionLintIssue issue = report.issues.FirstOrDefault (i => i.kind == MotionLint.kLargeKeyRotation);
            Assert.IsNotNull (issue, Describe (report));
            Assert.AreEqual (path, issue.target);
            Assert.AreEqual (0, issue.frame);
            Assert.AreEqual (30, issue.endFrame);
            Assert.AreEqual (120, issue.value, 0.1f);
        }

        [Test]
        public void BadRange_AndMissingCharacter_ReportErrors ()
        {
            PoseSession session = Open ();
            Assert.IsNotNull (MotionLint.Run (session, 10, 5).error);

            GameObject prefab = new GameObject ("NotHumanoid");
            created_.Add (prefab);
            PoseSession empty = new PoseSession (prefab, definition_);
            disposables_.Add (empty);
            Assert.IsNotNull (MotionLint.Run (empty, 0, 10).error);
        }
    }

}
