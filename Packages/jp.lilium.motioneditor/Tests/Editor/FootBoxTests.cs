using NUnit.Framework;
using UnityEngine;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// 足を、接地の点を頂点にした硬い箱として床に押し当てたときの動き（FootBox。10-22）
    /// </summary>
    public class FootBoxTests
    {
        const int kAnkle = 0, kHeel = 1, kBall = 2, kSide = 3, kToe = 4;
        const float kRadius = 0.02f;
        /// <summary>足が平らに床に着いたときの足首の高さ</summary>
        const float kAnkleHeight = 0.09f;

        /// <summary>足首を基準にした、平らな足の頂点（足首・踵・足裏の中心・小指側・つま先の先）</summary>
        static readonly Vector3[] kFlat = {
            new Vector3 (0, 0, 0),
            new Vector3 (0, kRadius - kAnkleHeight, -0.05f),
            new Vector3 (0, kRadius - kAnkleHeight, 0.12f),
            new Vector3 (0.04f, kRadius - kAnkleHeight, 0.11f),
            new Vector3 (0, kRadius - kAnkleHeight, 0.18f),
        };

        /// <summary>つま先の付け根の関節（足首を基準に。足裏の中心の少し前）</summary>
        static readonly Vector3 kToeJoint = new Vector3 (0, 0.03f - kAnkleHeight, 0.13f);

        /// <summary>つま先の先はつま先の付け根で曲がる（足の横向きの軸）</summary>
        static FootBox Create (Vector3 ankle, Quaternion rotation, bool[] fixedVertices = null)
        {
            Vector3[] vertices = kFlat.Select (v => ankle + rotation * v).ToArray ();
            float[] radius = { 0, kRadius, kRadius, kRadius, kRadius };
            bool[] toes = { false, false, false, false, true };
            return new FootBox (ankle, rotation, vertices, radius, fixedVertices, toes, ankle + rotation * kToeJoint, rotation * Vector3.right);
        }

        static void AssertAboveFloor (FootBox box, string message)
        {
            for (int i = 0; i < kFlat.Length; i++) Assert.GreaterOrEqual (box.Bottom (i), -1e-3f, message + ": " + i);
        }

        [Test]
        public void PushingDown_TouchesWithToeFirst_ThenLandsFlat ()
        {
            // つま先を下げた足を、足首を持って真下へ押し下げる: つま先が最初に着き、着いたつま先は滑らずにそこを支点に踵が下り、面で着いた所で止まる
            FootBox box = Create (new Vector3 (0, 0.3f, 0), Quaternion.AngleAxis (30, Vector3.right));
            Assert.AreEqual (0, box.contacts.Count, "初めは床から離れている");
            Vector3 hand = box.Vertex (kAnkle);
            Vector3 toeFirst = Vector3.zero;
            bool toeTouched = false;
            for (int i = 0; i < 40; i++) {
                hand += new Vector3 (0, -0.01f, 0);
                box.Move (kAnkle, hand);
                AssertAboveFloor (box, "床に潜らない step " + i);
                if (!toeTouched && box.contacts.Count > 0) {
                    CollectionAssert.AreEqual (new[] { kToe }, box.contacts, "最初に着くのはつま先");
                    toeTouched = true;
                    toeFirst = box.Vertex (kToe);
                }
                else if (toeTouched && box.contacts.Count == 1 && box.contacts[0] == kToe) {
                    // つま先だけで支えている間（足裏の中心が着いて支点が移るまで）
                    Assert.LessOrEqual (Vector3.Distance (toeFirst, box.Vertex (kToe)), 1e-3f, "着いたつま先は滑らない step " + i);
                }
            }
            Assert.IsTrue (toeTouched);
            Assert.GreaterOrEqual (box.contacts.Count, 3, "面で着く");
            Assert.AreEqual (kAnkleHeight, box.Vertex (kAnkle).y, 2e-3f, "足首は平らに着いた高さで止まる");
            for (int i = kHeel; i <= kToe; i++) Assert.LessOrEqual (box.Bottom (i), 2e-3f, "足裏は平らに床に着く: " + i);
        }

        [Test]
        public void LandingFlat_RestsRaisedToeOnFloor ()
        {
            // つま先が反った足（つま先の先が 1cm 浮く形）を下ろす: 足裏が面で着いたら、つま先の付け根を曲げてつま先の先も床まで下ろす（足裏全体が床に密着する）
            Vector3[] vertices = kFlat.Select (v => new Vector3 (0, 0.2f, 0) + v).ToArray ();
            vertices[kToe] += new Vector3 (0, 0.01f, 0);
            float[] radius = { 0, kRadius, kRadius, kRadius, kRadius };
            bool[] toes = { false, false, false, false, true };
            FootBox box = new FootBox (new Vector3 (0, 0.2f, 0), Quaternion.identity, vertices, radius, null, toes, new Vector3 (0, 0.2f, 0) + kToeJoint, Vector3.right);
            Vector3 hand = box.Vertex (kAnkle);
            for (int i = 0; i < 20; i++) {
                hand += new Vector3 (0, -0.01f, 0);
                box.Move (kAnkle, hand);
            }
            for (int i = kHeel; i <= kToe; i++) Assert.AreEqual (0, box.Bottom (i), 2e-3f, "足裏全体が床に着く: " + i);
        }

        [Test]
        public void LiftingAnkle_LiftsTheWholeFoot ()
        {
            // 平らに着いた足の足首を持ち上げる: 足の形と向きのまま床を離れ、足首は手に付いてくる（踵も一緒に上がる）
            FootBox box = Create (new Vector3 (0, kAnkleHeight, 0), Quaternion.identity);
            Assert.GreaterOrEqual (box.contacts.Count, 3, "初めは面で着いている");
            Quaternion rotation = box.rotation;
            Vector3 hand = box.Vertex (kAnkle);
            for (int i = 0; i < 5; i++) {
                hand += new Vector3 (0.002f, 0.01f, 0);
                box.Move (kAnkle, hand);
            }
            Assert.AreEqual (0, box.contacts.Count, "床を離れる");
            Assert.LessOrEqual (Vector3.Distance (hand, box.Vertex (kAnkle)), 1e-3f, "足首は手に付いてくる");
            Assert.LessOrEqual (Quaternion.Angle (rotation, box.rotation), 0.1f, "足の向きは変わらない");
            for (int i = kHeel; i <= kToe; i++) Assert.AreEqual (0.05f, box.Bottom (i), 1e-3f, "足裏が一緒に上がる: " + i);
        }

        [Test]
        public void LiftingAnkle_TurnsAboutLockedBall ()
        {
            // 足裏の中心を動かさない（Locked）と決めて足首を持ち上げる: 足裏の中心を支点に踵が上がり、つま先はつま先の付け根で曲がって床に着いたまま（つま先立ち）。
            // 足裏の中心は動かず、床を離れない
            bool[] locked = { false, false, true, false, false };
            FootBox box = Create (new Vector3 (0, kAnkleHeight, 0), Quaternion.identity, locked);
            Vector3 ball = box.Vertex (kBall);
            Vector3 hand = box.Vertex (kAnkle);
            for (int i = 0; i < 30; i++) {
                hand += new Vector3 (0, 0.01f, 0);
                box.Move (kAnkle, hand);
            }
            Assert.LessOrEqual (Vector3.Distance (ball, box.Vertex (kBall)), 1e-3f, "足裏の中心は動かない");
            Assert.Greater (box.Bottom (kHeel), 0.05f, "踵が上がる");
            Assert.Contains (kBall, box.contacts.ToList ());
            Assert.AreEqual (0, box.Bottom (kToe), 1e-3f, "つま先は曲がって床に着いたまま");
        }

        [Test]
        public void MovingFlatFootSideways_SlidesOnFloor ()
        {
            // 面で着いた足を横へ動かす: 床の上を滑る（押し下げても下がらない）
            FootBox box = Create (new Vector3 (0, kAnkleHeight, 0), Quaternion.identity);
            Vector3 start = box.Vertex (kAnkle);
            box.Move (kAnkle, start + new Vector3 (0.1f, -0.05f, 0.05f));
            Assert.LessOrEqual (Vector3.Distance (start + new Vector3 (0.1f, 0, 0.05f), box.Vertex (kAnkle)), 1e-3f, "横へだけ動く");
            AssertAboveFloor (box, "床に潜らない");
            Assert.GreaterOrEqual (box.contacts.Count, 3, "面で着いたまま");
        }
    }

}
