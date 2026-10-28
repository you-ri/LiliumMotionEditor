using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// 姿勢を少ない値で読み書きする（S36）。体の向き基準の点の場所・向き・状態だけで、窓と同じく全身 IK で解いてキーを打つ
    /// </summary>
    public class PoseDocumentTests
    {
        const float kPositionTolerance = 1e-3f;
        const float kAngleTolerance = 0.1f;

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

        /// <param name="yaw">キャラを置く向き（体の向き基準が置き方によらないことを見る）</param>
        PoseSession Open (float size = 1, float yaw = 0)
        {
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true, size);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            display.transform.localRotation = Quaternion.Euler (0, yaw, 0);
            PoseSession session = new PoseSession (prefab, definition_);
            disposables_.Add (session);
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            session.clip = clip;
            return session;
        }

        static PoseDocument Read (PoseSession session, int frame)
        {
            string problem;
            PoseDocument document = PoseDocument.Read (session, frame, out problem);
            Assert.IsNull (problem);
            Assert.IsNotNull (document);
            return document;
        }

        /// <summary>そのフレームの姿勢から、点を 1 つだけ並べた書き込みを作る</summary>
        static PoseDocument One (PoseDocument source, string name, string state, Vector3 offset)
        {
            PosePoint point = source.Find (name);
            Assert.IsNotNull (point, name);
            return new PoseDocument {
                frame = source.frame,
                humanScale = source.humanScale,
                points = { new PosePoint { name = name, state = state, position = point.position + offset } },
            };
        }

        static void AssertSamePoints (PoseDocument expected, PoseDocument actual, string message, float scale = 1, float positionTolerance = kPositionTolerance, float angleTolerance = kAngleTolerance)
        {
            Assert.AreEqual (expected.points.Count, actual.points.Count, message + ": 点の数");
            foreach (PosePoint e in expected.points) {
                PosePoint a = actual.Find (e.name);
                Assert.IsNotNull (a, message + ": " + e.name);
                Assert.AreEqual (e.state, a.state, message + ": " + e.name + " の状態");
                Assert.LessOrEqual (Vector3.Distance (e.position * scale, a.position), positionTolerance * Mathf.Max (1, scale), message + ": " + e.name + " の場所");
                if (e.hasRotation) {
                    Assert.LessOrEqual (Quaternion.Angle (Quaternion.Euler (e.rotation), Quaternion.Euler (a.rotation)), angleTolerance, message + ": " + e.name + " の向き");
                }
            }
        }

        [Test]
        public void Read_UsesTheBodyFrame_WithoutQuaternions ()
        {
            foreach (float yaw in new[] { 0f, 270f }) {
                PoseSession session = Open (1, yaw);
                PoseDocument document = Read (session, 0);
                Assert.AreEqual (session.stage.fullBody.pointCount, document.points.Count, yaw + "°: 点を全部並べる");
                Assert.IsTrue (document.points.All (p => p.state == "Free"), "基準姿勢では全部 Free");
                Vector3 left = document.Find ("LeftHand").position;
                Vector3 right = document.Find ("RightHand").position;
                Vector3 hips = document.Find ("Hips").position;
                Assert.Less (left.x, -0.3f, yaw + "°: 左手は体の左（x が負）");
                Assert.Greater (right.x, 0.3f, yaw + "°: 右手は体の右");
                Assert.LessOrEqual (Mathf.Abs (left.z), 0.05f, yaw + "°: T ポーズの手は体の横");
                Assert.Greater (hips.y, 0.5f, yaw + "°: 腰は足元より上");
                Assert.IsTrue (document.points.Where (p => p.hasRotation).All (p => p.rotation.magnitude < kAngleTolerance), "基準姿勢からの向きの差は 0");

                string json = document.ToJson ();
                StringAssert.DoesNotContain ("\"w\"", json, "四元数を出さない");
                AssertSamePoints (document, PoseDocument.FromJson (json), "JSON の往復");
            }
        }

        [Test]
        public void Write_PlacesThePoint_AndRoundTrips ()
        {
            PoseSession session = Open ();
            PoseDocument rest = Read (session, 10);
            Vector3 offset = new Vector3 (0.1f, 0.3f, 0.2f);
            Assert.IsNull (PoseDocument.Write (session, One (rest, "LeftHand", "Pin", offset)));

            CollectionAssert.AreEqual (new[] { 10 }, ClipCycle.GetFrames (session.clip), "書いたフレームのキー");
            PoseDocument posed = Read (session, 10);
            PosePoint hand = posed.Find ("LeftHand");
            Assert.AreEqual ("Pin", hand.state);
            Assert.LessOrEqual (Vector3.Distance (rest.Find ("LeftHand").position + offset, hand.position), kPositionTolerance, "置いた場所に手が届く");

            // 読んだものをそのまま書いて読み直しても、ほとんど変わらない。書くたびに全身 IK を解き直すので、
            // 1 回目の解きで収束しきらなかった分だけ動くが、繰り返すと縮んで止まる（2026-10-10 実測: 1.5 → 0.6 → 0.25mm、合計 2.6mm。固定していない手の向きは 0.34°）
            PoseDocument previous = posed;
            float lastStep = float.MaxValue;
            for (int i = 0; i < 4; i++) {
                Assert.IsNull (PoseDocument.Write (session, previous));
                PoseDocument again = Read (session, 10);
                float step = again.points.Max (p => Vector3.Distance (p.position, previous.Find (p.name).position));
                Assert.LessOrEqual (step, i == 0 ? 2e-3f : lastStep, i + " 回目の書き直しで動いた量");
                AssertSamePoints (posed, again, "読む → 書く → 読む", 1, 3e-3f, 1);
                lastStep = step;
                previous = again;
            }
        }

        [Test]
        public void Rescale_SharesThePoseBetweenBodySizes ()
        {
            PoseSession small = Open (1);
            PoseSession large = Open (1.3f);
            Assert.Greater (large.stage.editingRig.humanScale, small.stage.editingRig.humanScale * 1.2f);
            Assert.IsNull (PoseDocument.Write (small, One (Read (small, 0), "LeftHand", "Pin", new Vector3 (0.05f, 0.25f, 0.2f))));
            PoseDocument source = Read (small, 0);

            PoseDocument copy = PoseDocument.FromJson (source.ToJson ());
            copy.Rescale (large.stage.editingRig.humanScale);
            Assert.IsNull (PoseDocument.Write (large, copy));
            PoseDocument result = Read (large, 0);

            // 体の大きさで割った場所が、体型によらず同じ（≤1cm）
            foreach (PosePoint expected in source.points) {
                PosePoint actual = result.Find (expected.name);
                Vector3 a = expected.position / source.humanScale;
                Vector3 b = actual.position / result.humanScale;
                Assert.LessOrEqual (Vector3.Distance (a, b), 0.01f, expected.name + " の体の大きさで割った場所");
            }
        }

        [Test]
        public void LockedAtBothKeys_KeepsTheFootBetweenKeys ()
        {
            // 0F と 20F で両足を Locked にし、20F で腰を下げる。キーの間も足は動かない
            PoseSession session = Open ();
            PoseDocument rest = Read (session, 0);
            PoseDocument planted = new PoseDocument { frame = 0, humanScale = rest.humanScale };
            foreach (string foot in new[] { "LeftFoot", "RightFoot" }) {
                planted.points.Add (new PosePoint { name = foot, state = "Locked", position = rest.Find (foot).position });
            }
            Assert.IsNull (PoseDocument.Write (session, planted));
            planted.frame = 20;
            planted.points.Add (new PosePoint { name = "Hips", state = "Pin", position = rest.Find ("Hips").position + new Vector3 (0, -0.12f, 0.03f) });
            Assert.IsNull (PoseDocument.Write (session, planted));

            Assert.Less (Read (session, 20).Find ("Hips").position.y, rest.Find ("Hips").position.y - 0.1f, "20F で腰が下がっている");
            foreach (int frame in new[] { 5, 10, 15 }) {
                PoseDocument between = Read (session, frame);
                foreach (string foot in new[] { "LeftFoot", "RightFoot" }) {
                    Assert.LessOrEqual (Vector3.Distance (rest.Find (foot).position, between.Find (foot).position), kPositionTolerance, frame + "F の " + foot);
                    Assert.AreEqual ("Locked", between.Find (foot).state);
                }
            }
        }

        [Test]
        public void Write_RejectsUnknownPointsAndStates_WithoutWriting ()
        {
            PoseSession session = Open ();
            PoseDocument rest = Read (session, 0);
            PoseDocument document = One (rest, "LeftHand", "Pin", Vector3.up * 0.1f);
            document.points.Add (new PosePoint { name = "Tail", state = "Pin" });
            StringAssert.Contains ("Tail", PoseDocument.Write (session, document));

            document = One (rest, "LeftHand", "Hold", Vector3.up * 0.1f);
            StringAssert.Contains ("Hold", PoseDocument.Write (session, document));
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (session.clip), "何も書かない");

            Assert.IsNotNull (PoseDocument.Write (session, null));
        }

        [Test]
        public void PassivePoint_IsNotRead_AndCannotBeWritten ()
        {
            // 動かせない点（S89）は値を持たないので読まず、書こうとすると断る（読んだ物はそのまま書き戻せる）
            definition_.bodyPoints.Add (new EditRigDefinition.BodyPoint { name = "Pivot", bone = HumanBodyBones.Chest, weight = 0, passive = true });
            PoseSession session = Open ();
            Assert.IsTrue (session.stage.fullBody.HasPoint (new BodyPointId ("Pivot")));
            PoseDocument rest = Read (session, 0);
            Assert.IsNull (rest.Find ("Pivot"), "動かせない点は読まない");

            PoseDocument document = One (rest, "LeftHand", "Pin", Vector3.up * 0.1f);
            document.points.Add (new PosePoint { name = "Pivot", state = "Pin" });
            StringAssert.Contains ("Pivot", PoseDocument.Write (session, document));
            Assert.IsEmpty (AnimationUtility.GetCurveBindings (session.clip), "何も書かない");
        }

        [Test]
        public void NoBodyPoints_ExplainsThatItCannotReadOrWrite ()
        {
            // 点が 1 つも無ければ全身 IK が作られず、点で姿勢を表せない
            definition_.bodyPoints.Clear ();
            PoseSession session = Open ();
            Assert.IsNull (session.stage.fullBody);
            string problem;
            Assert.IsNull (PoseDocument.Read (session, 0, out problem));
            Assert.IsNotNull (problem);
            Assert.IsNotNull (PoseDocument.Write (session, new PoseDocument ()));
        }
    }

}
