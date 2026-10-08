using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点（S25）の操作。選んでいる点・固定の付け外し・FK で回したときに先の固定点を連れていく・解いた姿勢を骨のキーに打つ
    /// </summary>
    public partial class PreviewWindow
    {
        BodyPointTarget contextPoint_;

        /// <summary>今のフレームで、動かした点・固定した点があるか（全身 IK の定義のとき）</summary>
        bool hasActivePoints
        {
            get { return stage_ != null && stage_.fullBody != null && stage_.fullBody.hasActivePoint; }
        }

        /// <summary>選んでいる全身 IK の点（✋ が全身 IK の段のとき）</summary>
        List<BodyPointTarget> GetSelectedPoints ()
        {
            List<BodyPointTarget> points = new List<BodyPointTarget> ();
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null && IsSelected (point.gameObject)) points.Add (point);
            }
            return points;
        }

        /// <summary>FK の骨の、動かす前の値（ローカルの値で動かしたかを見て、ワールドの値で点を一緒に動かす）</summary>
        struct FkBefore
        {
            public Transform bone;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Pose world;
        }

        /// <summary>targets のうち FK の骨の、動かす前の値。全身 IK の点をつかむ間でなければ null（点を一緒に動かさない）</summary>
        List<FkBefore> CaptureFkBefore (IEnumerable<PoseTarget> targets)
        {
            if (!isPointGrab) return null;
            List<FkBefore> result = new List<FkBefore> ();
            foreach (PoseTarget target in targets) {
                FkTarget fk = target as FkTarget;
                if (fk == null || fk.bone == null) continue;
                FkBefore before = new FkBefore { bone = fk.bone, world = new Pose (fk.bone.position, fk.bone.rotation) };
                fk.bone.GetLocalPositionAndRotation (out before.localPosition, out before.localRotation);
                result.Add (before);
            }
            return result;
        }

        /// <summary>CaptureFkBefore から後に動かした FK の骨に付けて、子の Pin の点を動かす。動かした点があれば true</summary>
        bool CarryPinnedPoints (List<FkBefore> before)
        {
            if (before == null || before.Count == 0) return false;
            Dictionary<Transform, Pose> moved = new Dictionary<Transform, Pose> ();
            foreach (FkBefore entry in before) {
                Vector3 position;
                Quaternion rotation;
                entry.bone.GetLocalPositionAndRotation (out position, out rotation);
                // 子の骨はワールドの値が親に付いて変わるので、ローカルの値で動かした骨を見分ける（Equals は誤差を許さない比べ方）
                if (!position.Equals (entry.localPosition) || !rotation.Equals (entry.localRotation)) moved[entry.bone] = entry.world;
            }
            return moved.Count > 0 && CarryPinnedPoints (targets_, moved);
        }

        /// <summary>
        /// 全身 IK の点をつかむ間に、FK で骨を動かした（moved は動かした骨と、動かす前のワールドの位置・向き）:
        /// その骨と子の骨に乗る Pin の点を、骨と同じだけ回して・ずらして置き直す（10-08。点をつなぐ線から骨を選んで FK で動かす）。
        /// Locked の点は置いた場所に残す。Free の点は体に付いて動く。
        /// 点ごとに、自分の骨から根の方へたどって最初に当たる動かした骨の変化を掛ける（その骨より先は付いて動くだけなので、
        /// いくつかの骨をまとめて動かしても二重に掛からない）。動かした点があれば true
        /// </summary>
        internal static bool CarryPinnedPoints (IList<PoseTarget> targets, IDictionary<Transform, Pose> moved)
        {
            bool carried = false;
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point == null || !point.pinned || point.locked) continue;
                for (Transform t = point.editingBone; t != null; t = t.parent) {
                    Pose before;
                    if (!moved.TryGetValue (t, out before)) continue;
                    point.CarryWith (before.position, t.rotation * Quaternion.Inverse (before.rotation), t.position - before.position);
                    carried = true;
                    break;
                }
            }
            return carried;
        }

        /// <summary>FK の対象を、編集用の骨で引く（点をつなぐ線から、線に当たる骨の FK を探す）</summary>
        Dictionary<Transform, FkTarget> FkTargetsByBone ()
        {
            Dictionary<Transform, FkTarget> result = new Dictionary<Transform, FkTarget> ();
            foreach (PoseTarget target in targets_) {
                FkTarget fk = target as FkTarget;
                if (fk != null && fk.bone != null) result[fk.bone] = fk;
            }
            return result;
        }

        /// <summary>
        /// いくつか選んでいる点の 1 つ（grabbed）をつかんで動かしたとき、一緒に同じだけ動かす点（grabbed は含まない）。
        /// 選んだ中に親の点（ChildTargets。Ctrl で子階層を選ぶときと同じ関係）がある Free の点は動かさない:
        /// 親の点を動かせば体に付いて動くので、Pin にしない（親と子をまとめて選んで動かすと、子まで全部 Pin になっていた。10-07）。
        /// Pin・Locked の点は体に付いて動かないので、今までどおり同じだけ動かす。
        /// つかんだ点が子で、選んだ親が Free のときは、親を動かして Pin にする（まとめて動かすと、選んだ物は全部動く。親をつかんだときと同じ見た目になる）
        /// </summary>
        internal static List<BodyPointTarget> PointsMovedWith (IList<PoseTarget> targets, IList<BodyPointTarget> selected, BodyPointTarget grabbed)
        {
            HashSet<PoseTarget> carried = new HashSet<PoseTarget> ();
            foreach (BodyPointTarget parent in selected) {
                foreach (PoseTarget child in ChildTargets (targets, parent, null)) carried.Add (child);
            }
            List<BodyPointTarget> moved = new List<BodyPointTarget> ();
            foreach (BodyPointTarget point in selected) {
                if (point == grabbed) continue;
                if (point.state == BodyPointState.Free && carried.Contains (point)) continue;
                moved.Add (point);
            }
            return moved;
        }

        /// <summary>
        /// 点を 1 つ選んで回した: その点を中心に、点が乗る骨を回す（S26。点の種類で変えない）。
        /// 骨は FK で回すので、子の骨と、骨に付いて動く Free の点は一緒に回る。固定している点（Pin・Locked。同じ骨の上の点も）は回さず、
        /// 置いた場所と向きのまま（S29。移動と同じく、ほかの点の操作に連動しない。全身 IK が手足を曲げて届かせる）。
        /// 付け根から離れた点は、FK だけでは中心が骨の付け根になるので、選んだ点をその場に留め（Pin）、骨の付け根は全身 IK で点に合わせて動かす。
        /// 骨を FK で回せないとき（FK の対象が無い）は、向きを持つ点の向きだけ回す
        /// </summary>
        internal static void RotatePointFk (IList<PoseTarget> targets, BodyPointTarget point, Quaternion delta)
        {
            Transform bone = point.editingBone;
            FkTarget fk = null;
            foreach (PoseTarget target in targets) {
                FkTarget candidate = target as FkTarget;
                if (candidate != null && candidate.bone == bone) {
                    fk = candidate;
                    break;
                }
            }
            if (bone == null || fk == null) {
                point.RotateAround (point.framePosition, delta, true);
                return;
            }
            // 中心は選んだ点の目標（固定していなければ骨の上の点）
            Vector3 pivot = point.isOnBoneOrigin ? bone.position : point.framePosition;
            fk.SpinRotateWorld (delta, false);
            if (point.isOnBoneOrigin) point.RotateAround (pivot, delta, false);
            else point.RotateAround (pivot, delta, true);
        }

        /// <summary>
        /// いくつか選んでいるときに中心の点で回した: 選んでいる点だけを、中心の点のまわりで回す（Free の点も、回した場所に置いて Pin にする）。
        /// 選んでいない点は動かさない（解いた体に付いて動く）
        /// </summary>
        void RotateSelectedPoints (BodyPointTarget pivotPoint, Quaternion delta)
        {
            // 中心は中心の点の目標（Locked ならその場所）。ほかの点は骨の上の点から回す（目標から回すと、届いていない分の食い違いが残る）
            Vector3 pivot = pivotPoint.framePosition;
            Vector3 pivotOnBone = pivotPoint.bonePosition;
            foreach (BodyPointTarget point in GetSelectedPoints ()) {
                if (point == pivotPoint) point.RotateAround (pivot, delta, true);
                else point.RotateBodyAround (pivot, pivotOnBone, delta, true);
            }
        }

        /// <summary>
        /// 点の固定を付け外しして、今のフレームにキーを打つ。clicked が選んでいる点の 1 つなら、選んでいる点をまとめて変える。
        /// clicked が null なら、選んでいる点すべて
        /// </summary>
        void SetPointsLocked (BodyPointTarget clicked, bool on)
        {
            if (stage_ == null || stage_.fullBody == null) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (clicked != null && !points.Contains (clicked)) {
                points.Clear ();
                points.Add (clicked);
                Select (clicked.gameObject);
            }
            if (points.Count == 0) return;
            foreach (BodyPointTarget point in points) point.SetLocked (on);
            SolveAfterManipulate ();
            CommitFullBodyPose ();
        }

        /// <summary>選んでいる全身 IK の点の数（Controller のパネルが、状態のボタンを出すかどうかに使う）</summary>
        public int selectedPointCount
        {
            get { return stage_ != null && stage_.fullBody != null ? GetSelectedPoints ().Count : 0; }
        }

        /// <summary>選んでいる点のうち、その状態の点の数</summary>
        public int CountSelectedPoints (BodyPointState state)
        {
            if (stage_ == null || stage_.fullBody == null) return 0;
            int count = 0;
            foreach (BodyPointTarget point in GetSelectedPoints ()) {
                if (point.state == state) count++;
            }
            return count;
        }

        /// <summary>
        /// 選んでいる点をまとめてその状態にして、今のフレームにキーを打つ（Controller のパネルの Free / Pin / Lock）
        /// </summary>
        public void SetSelectedPointsState (BodyPointState state)
        {
            if (stage_ == null || stage_.fullBody == null) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (points.Count == 0) return;
            foreach (BodyPointTarget point in points) point.SetState (state);
            SolveAfterManipulate ();
            CommitFullBodyPose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 選んでいる点の固定を付け外しする（ショートカット）。1 つでも固定していない点があれば全部固定し、全部固定していれば外す
        /// </summary>
        public void ToggleSelectedPointsLocked ()
        {
            if (!isPointGrab) return;
            List<BodyPointTarget> points = GetSelectedPoints ();
            if (points.Count == 0) return;
            SetPointsLocked (null, points.Exists (p => !p.locked));
        }

        /// <summary>
        /// ✋ が全身 IK の段のとき（S25）。関節の点をつかんで固定する
        /// </summary>
        public bool isPointGrab
        {
            get {
                PoseLayer manipulate = poseStack_ != null ? poseStack_.manipulateLayer : null;
                return manipulate != null && manipulate.kind == LayerKind.FullBodyIk && stage_ != null && stage_.fullBody != null;
            }
        }

        bool HasPendingPointKeys ()
        {
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null && point.hasPendingKeys) return true;
            }
            return false;
        }

        /// <summary>
        /// 全身 IK で作った今の姿勢を、今のフレームのキーにする（キーフレームは全身の姿勢を持つ）。
        /// 点を離したとき・点の固定を付け外ししたとき・固定している点があるフレームで骨を動かしたときに呼ぶ。
        /// 1. 動かした点（固定していないもの）を、実際の骨の位置へ合わせ直す
        /// 2. 解いた姿勢を骨の値にして、全部の骨のキーを打つ（キーの自動追加が切でも全部の骨に打つ。切のときにハンドル・Spinner でここへ来るのは、
        ///    今のフレームに姿勢のキーがあるときだけ。キーの間は骨の値の補間で動く）
        /// 3. 動かした点・固定した点のキーを打つ（このキーフレームへ戻ったときに、同じ点から直せる。ほかのフレームでは効かない）
        /// クリップに書けないとき（クリップが無い・合成中など）は打たずに、姿勢をクリップから当て直す
        /// </summary>
        void CommitFullBodyPose ()
        {
            if (stage_ == null || stage_.fullBody == null) return;
            if (!CanEditClip ()) {
                foreach (PoseTarget target in targets_) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null) point.ClearPendingKeys ();
                }
                SamplePose ();
                return;
            }
            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) point.SyncToBone ();
            }
            // 解いた姿勢を骨の値（FK の差分・腰の位置）にする。ここからは、この姿勢が元の姿勢
            if (stage_.editingRigSolver != null) stage_.editingRigSolver.Capture ();
            stage_.fullBody.CaptureInput ();
            using (CurveWriter writer = BeginCurves ("Key Pose")) {
                foreach (PoseTarget target in targets_) {
                    BodyPointTarget point = target as BodyPointTarget;
                    if (point != null && point.hasPendingKeys) point.WritePendingKeys (writer);
                }
                foreach (PoseTarget target in targets_) target.WriteKeys (writer);
                if (rigProxies != null) rigProxies.WriteWeightKeys (writer);
            }
            Undo.SetCurrentGroupName ("Key Pose");
            Undo.CollapseUndoOperations (group);
            keyFrames_ = null;
            // 打ったキーから姿勢を作り直す（保存した値で見えている姿勢と同じになる）
            SamplePose ();
        }
    }

}
