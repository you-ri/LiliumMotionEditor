using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// ギズモで点を回すドラッグ（PreviewWindow.PointTurn）。毎回ドラッグの始めの姿勢から合計の回転で回し直すので、
    /// 細かく回しても、IK が足したねじれが積み上がらず、一度に回したのと同じ姿勢になる。
    /// いくつか選んだ点をまとめて回す・動かすと、いちばん根の方の骨（腰）も一緒に動き、体がそのまま回る・動く
    /// </summary>
    public class PointTurnTests
    {
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

        PoseSession Open ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, false);
            display.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition);
            disposables_.Add (session);
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);
            session.clip = clip;
            return session;
        }

        [Test]
        public void TurningInSmallSteps_MatchesOneTurn ()
        {
            // 腰・足・左肩を Pin にして、背骨の付け根から離れた胸の点を回す（背骨が Spine だけのキャラ。腰を留めているので点を中心には回りきれず、
            // IK が背骨をねじって合わせる。左肩の 1 点だけでは胸の向きが決まらないので、ねじれが積み上がりやすい形）
            PoseSession session = Open ();
            List<PoseTarget> targets = session.targets.ToList ();
            System.Func<string, BodyPointTarget> named = name => targets.OfType<BodyPointTarget> ().Single (p => p.id.name == name);
            BodyPointTarget chest = named ("Chest");
            Assert.IsFalse (chest.isOnBoneOrigin, "胸の点は背骨の付け根から離れている");
            foreach (string name in new[] { "Hips", "LeftFoot", "RightFoot", "LeftUpperArm" }) named (name).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            PreviewStage.ControlState start = session.stage.CaptureControlState ();
            // 背骨の軸から外れた斜めの軸で回す（背骨の軸まわりだけなら、付け根と点のずれが効かない）
            Vector3 axis = new Vector3 (0.6f, 1, 0.4f).normalized;
            Quaternion step = Quaternion.AngleAxis (2, axis);

            // 始めの姿勢から一度に 80° 回す
            PreviewWindow.RotatePointFk (targets, chest, Quaternion.AngleAxis (80, axis));
            session.SolveAfterEdit ();
            Dictionary<BodyPointTarget, Vector3> once = targets.OfType<BodyPointTarget> ().ToDictionary (p => p, p => p.bonePosition);

            // 窓のドラッグと同じ流れで 2° ずつ 40 回（回すたびに解き直す）。合計で回し直さないと、ねじれが積み上がって一度に回したのとずれる
            System.Func<bool, float> drag = useTurn => {
                session.stage.RestoreControlState (start);
                session.SolveAfterEdit ();
                PreviewWindow.PointTurn turn = new PreviewWindow.PointTurn ();
                for (int i = 0; i < 40; i++) {
                    PreviewWindow.RotatePointFk (targets, chest, useTurn ? turn.Add (session.stage, chest, step, session.SolveAfterEdit) : step);
                    session.SolveAfterEdit ();
                }
                turn.End ();
                return once.Max (entry => Vector3.Distance (entry.Value, entry.Key.bonePosition));
            };
            Assert.Greater (drag (false), 0.01f, "今の姿勢へ足していくと、一度に回したのと 1cm より大きくずれる（ねじれが積み上がる形になっている）");
            Assert.LessOrEqual (drag (true), 1e-3f, "始めから合計で回し直すと、一度に回したのと同じ所");
        }

        [Test]
        public void TurningAllPoints_TurnsTheBodyAsItIs ([Values (1, 30)] int steps)
        {
            // 全部の点を選んで足の点を中心に回すと、腰の FK も中心のまわりに回るので、体がそのまま回る（目標だけ回すと、全身 IK が届かずねじれていた）。
            // ドラッグで細かく回しても同じ
            PoseSession session = Open ();
            List<PoseTarget> targets = session.targets.ToList ();
            List<BodyPointTarget> points = targets.OfType<BodyPointTarget> ().ToList ();
            BodyPointTarget foot = points.Single (p => p.id.name == "LeftFoot");
            foreach (string name in new[] { "Hips", "LeftFoot", "RightFoot" }) points.Single (p => p.id.name == name).SetState (BodyPointState.Pin);
            session.SolveAfterEdit ();
            Dictionary<BodyPointTarget, Vector3> start = points.ToDictionary (p => p, p => p.bonePosition);
            Vector3 pivot = foot.framePosition;
            Vector3 axis = new Vector3 (0.3f, 1, 0.2f).normalized;
            Quaternion total = Quaternion.AngleAxis (90, axis);

            PreviewWindow.PointTurn turn = new PreviewWindow.PointTurn ();
            for (int i = 0; i < steps; i++) {
                Quaternion delta = turn.Add (session.stage, foot, Quaternion.AngleAxis (90f / steps, axis), session.SolveAfterEdit);
                PreviewWindow.RotatePointsAround (targets, points, foot, delta);
                session.SolveAfterEdit ();
            }
            turn.End ();

            foreach (BodyPointTarget point in points) {
                Vector3 expected = pivot + total * (start[point] - pivot);
                Assert.LessOrEqual (Vector3.Distance (expected, point.bonePosition), 1e-3f, point.id.name + " は体ごと回った所");
                Assert.AreNotEqual (BodyPointState.Free, point.state, "回した点は Pin になる");
            }
        }

        [Test]
        public void MovingAllPoints_MovesTheBodyAsItIs ([Values (1, 30)] int steps)
        {
            // 足と手だけを Pin にして全部の点を選び、腰をつかんで動かす: 腰の FK も同じだけ動くので、固定していない頭なども体ごと動く
            // （点の目標だけ動かすと、全身 IK が届かせようとして背骨が曲がり、頭が 35cm ずれていた）。窓のドラッグと同じく少しずつ動かしても同じ
            PoseSession session = Open ();
            List<PoseTarget> targets = session.targets.ToList ();
            List<BodyPointTarget> points = targets.OfType<BodyPointTarget> ().ToList ();
            BodyPointTarget hips = points.Single (p => p.id.name == "Hips");
            foreach (string name in new[] { "Hips", "LeftFoot", "RightFoot", "LeftHand", "RightHand" }) points.Single (p => p.id.name == name).SetState (BodyPointState.Pin);
            foreach (BodyPointTarget point in points) point.floorClamp = false;
            session.SolveAfterEdit ();
            Dictionary<BodyPointTarget, Vector3> start = points.ToDictionary (p => p, p => p.bonePosition);
            Vector3 total = new Vector3 (0.4f, 0.1f, 0.3f);

            for (int i = 0; i < steps; i++) {
                Vector3 delta = total / steps;
                Vector3 from = hips.framePosition;
                hips.MoveBy (delta);
                PreviewWindow.MoveSelectedPoints (targets, points, hips, from, delta);
                session.SolveAfterEdit ();
            }

            foreach (BodyPointTarget point in points) {
                Assert.LessOrEqual (Vector3.Distance (start[point] + total, point.bonePosition), 1e-3f, point.id.name + " は体ごと動いた所");
            }
        }

        [Test]
        public void TurningAnotherPoint_StartsFromCurrentPose ()
        {
            // 回す点が変わったら（ドラッグを終えずに別の点を回した）、そのときの姿勢から始める（前の点の始めへ戻さない）
            PoseSession session = Open ();
            List<PoseTarget> targets = session.targets.ToList ();
            System.Func<string, BodyPointTarget> named = name => targets.OfType<BodyPointTarget> ().Single (p => p.id.name == name);
            PreviewWindow.PointTurn turn = new PreviewWindow.PointTurn ();
            Quaternion delta = Quaternion.AngleAxis (10, Vector3.up);
            Assert.AreEqual (delta, turn.Add (session.stage, named ("Stomach"), delta, session.SolveAfterEdit));
            Assert.AreEqual (delta * delta, turn.Add (session.stage, named ("Stomach"), delta, session.SolveAfterEdit), "同じ点なら合計");
            Assert.AreEqual (delta, turn.Add (session.stage, named ("Chest"), delta, session.SolveAfterEdit), "別の点なら始めから");
            turn.End ();
            Assert.AreEqual (delta, turn.Add (session.stage, named ("Chest"), delta, session.SolveAfterEdit), "ドラッグを終えたら始めから");
        }
    }

}
