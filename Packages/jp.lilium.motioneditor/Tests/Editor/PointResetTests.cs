using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Controller パネルの Reset（PreviewWindow.ResetValues）。全身 IK の点を選んで戻すと、点の骨の FK も基準へ戻り、
    /// Free の点（向きを固定していない足首の点・踵・つま先など）の骨も、親から見た基準姿勢になる
    /// </summary>
    public class PointResetTests
    {
        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        const float kAngleTolerance = 0.5f;
        const float kPositionTolerance = 1e-3f;

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
            Animator display = TestSkeleton.CreateHumanoidWithToes (created_);
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
        public void ResetFootPoints_StraightensTheFoot ()
        {
            // 足を FK で曲げ、足首の点を位置だけ Pin にする（向きと、踵・つま先などの点は Free）。足の点をまとめて Reset すると、
            // 足とつま先の骨が脛から見た基準の向きに戻り、足首は動かない（点の値だけ戻すと、Free の点は解くときに効かず何も変わらなかった）
            PoseSession session = Open ();
            List<PoseTarget> targets = session.targets.ToList ();
            List<BodyPointTarget> points = targets.OfType<BodyPointTarget> ().ToList ();
            BodyPointTarget ankle = points.Single (p => p.id.name == "LeftFoot");
            Transform footBone = ankle.editingBone;
            List<FkTarget> footFks = targets.OfType<FkTarget> ().Where (f => f.bone != null && f.bone.IsChildOf (footBone)).ToList ();
            Assert.Greater (footFks.Count, 1, "足とつま先の FK がある");
            foreach (FkTarget fk in footFks) fk.SetValues (TransformChannels.Rotation, Vector3.zero, Quaternion.Euler (40, 10, 15));
            session.SolveAfterEdit ();
            ankle.MoveBy (Vector3.zero);
            session.SolveAfterEdit ();
            Assert.IsTrue (ankle.positionPinned);
            Assert.IsFalse (ankle.rotationPinned, "足首の点の向きは Free");
            Vector3 ankleBefore = footBone.position;

            List<PoseTarget> selected = points.Where (p => p.editingBone != null && p.editingBone.IsChildOf (footBone)).Cast<PoseTarget> ().ToList ();
            Assert.Greater (selected.Count, 1, "足首のほかに足の点がある");
            Assert.IsTrue (selected.OfType<BodyPointTarget> ().Any (p => p.state == BodyPointState.Free), "Free の点がある");
            PreviewWindow.ResetValues (targets, selected);
            session.SolveAfterEdit ();

            foreach (FkTarget fk in footFks) {
                Quaternion rest;
                Assert.IsTrue (session.stage.editingRig.TryGetRestRotation (fk.bone, out rest));
                Assert.Less (Quaternion.Angle (rest, fk.bone.localRotation), kAngleTolerance, fk.bone.name + " は親から見た基準の向き");
            }
            Assert.LessOrEqual (Vector3.Distance (ankleBefore, footBone.position), kPositionTolerance, "足首は動かない");
            Assert.IsTrue (ankle.positionPinned, "固定の強さは変えない");
            Assert.IsFalse (ankle.rotationPinned, "固定の強さは変えない");
        }
    }

}
