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

        /// <summary>今のフレームで、動かした点・固定した点があるか</summary>
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
        /// いくつか選んでいる点の 1 つ（grabbed）を from から delta だけ動かした（grabbed は動かしてある）: ほかの選んでいる点を同じだけ動かす。
        /// Floor Clamp が入っていれば、選んでいる点をひとかたまりとして床に当てる（10-09 ユーザー指示。点ごとに当てると、
        /// つかんだ点だけ床で止まり、ほかの点は床の下へ潜っていた）: 一番低い点が床より下へ行くなら、まとめて動かす量の Y を
        /// その点が床に着く所まで上げる（横の動きはそのまま効き、かたまりは床の上を滑る。1 つの点を床に当てるときと同じ）。
        /// 一緒に動かさない Free の点（親を動かせば体に付いて動く）も、同じだけ動くとみなして低さに入れる。
        /// 選んだ点の骨のうちいちばん根の方の骨が腰なら、腰の FK も同じだけ動かす（10-20 ユーザー指示。点の目標だけ動かすと、全身 IK は動かす前の姿勢から寄せるので、
        /// 体ごと動かす形に届かず背骨が曲がっていた。全部の点を選んで腰を動かすと、固定していない頭などが 35cm ずれていた）。
        /// 点は同じだけそのまま動かす（PlaceWithSelection。点ごとに床に当てたり足の箱で動かしたりすると、かたまりの形が崩れる。10-24）。
        /// もう床の下限より下にある点（下限ができる前に床の高さへ置いた足裏の点など）があっても、持ち上げはしない（それより下へは動かさない）:
        /// 横へ動かしただけで、かたまりが 2cm 浮いていた（10-24）
        /// </summary>
        internal static void MoveSelectedPoints (IList<PoseTarget> targets, IList<BodyPointTarget> selected, BodyPointTarget grabbed, Vector3 from, Vector3 delta)
        {
            if (grabbed.floorClamp) {
                float lowest = float.MaxValue;
                foreach (BodyPointTarget point in selected) {
                    // 床からの余裕（球を持つ点は球の下の端、足の点は足をかたまりのまま下げたときの足裏の球の下の端）
                    lowest = Mathf.Min (lowest, (point == grabbed ? from.y : point.framePosition.y) - point.floorLimit);
                }
                float bottom = Mathf.Min (0, lowest);
                if (lowest + delta.y < bottom) delta.y = bottom - lowest;
            }
            // つかんだ点は、窓のドラッグでは自分で動いてある。かたまりの動かす量で置き直す
            grabbed.PlaceWithSelection (from + delta);
            foreach (BodyPointTarget other in PointsMovedWith (targets, selected, grabbed)) other.PlaceWithSelection (other.framePosition + delta);
            // 点を先に動かす（Free の点は今の骨の上の点から動かすので、骨を先に動かすと二重に動く）。腰より先の骨は位置を持たないので動かない
            foreach (FkTarget fk in TopFkTargets (targets, selected)) fk.MoveWorld (delta);
        }

        /// <summary>
        /// 点を 1 つ選んで回した: その点を中心に、点が乗る骨を回す（S26。点の種類で変えない）。
        /// 骨は FK で回すので、子の骨と、骨に付いて動く Free の点は一緒に回る。その骨と子の骨に乗る固定している点（Pin・Locked。同じ骨の上の点も）も、
        /// 選んだ点を中心に同じだけ回す（10-19 ユーザー指示。S29 では置いた場所のままにしていたが、固定した手先が残って腕だけ回り、
        /// 回したい所が回らなかった）。ほかの枝の点は動かさない。
        /// 付け根から離れた点は、FK だけでは中心が骨の付け根になるので、選んだ点をその場に留め（Pin）、骨の付け根は全身 IK で点に合わせて動かす
        /// （解いた後は選んだ点を中心に回った形になるので、固定している点も選んだ点を中心に回す）。
        /// 骨を FK で回せないとき（FK の対象が無い）は、向きを持つ点の向きだけ回す
        /// </summary>
        internal static void RotatePointFk (IList<PoseTarget> targets, BodyPointTarget point, Quaternion delta)
        {
            Transform bone = point.editingBone;
            FkTarget fk = FindFkTarget (targets, bone);
            if (fk == null) {
                point.RotateAround (point.framePosition, delta, true);
                return;
            }
            // 中心は選んだ点の目標（固定していなければ骨の上の点）
            Vector3 pivot = point.isOnBoneOrigin ? bone.position : point.framePosition;
            // 点を先に回す（Free の点は今の骨の上の点から回すので、骨を先に回すと二重に回る）。
            // 付け根の点は骨と一緒に回るので固定している値だけ、離れた点はその場に留める（Pin）
            point.RotateAround (pivot, delta, !point.isOnBoneOrigin);
            foreach (PoseTarget target in targets) {
                BodyPointTarget other = target as BodyPointTarget;
                // IsChildOf は自分自身でも true（同じ骨の上の点も回す）
                if (other == null || other == point || !other.pinned || other.editingBone == null || !other.editingBone.IsChildOf (bone)) continue;
                other.RotateAround (pivot, delta, false);
            }
            fk.SpinRotateWorld (delta);
        }

        /// <summary>bone を動かす FK の対象（無ければ null）</summary>
        static FkTarget FindFkTarget (IList<PoseTarget> targets, Transform bone)
        {
            if (bone == null) return null;
            foreach (PoseTarget target in targets) {
                FkTarget fk = target as FkTarget;
                if (fk != null && fk.bone == bone) return fk;
            }
            return null;
        }

        /// <summary>
        /// いくつか選んでいるときに中心の点で回した: 選んでいる点だけを、中心の点のまわりで回す（Free の点も、回した場所に置いて Pin にする）。
        /// 選んでいない点は動かさない（解いた体に付いて動く）
        /// </summary>
        void RotateSelectedPoints (BodyPointTarget pivotPoint, Quaternion delta)
        {
            RotatePointsAround (targets_, GetSelectedPoints (), pivotPoint, delta);
        }

        /// <summary>
        /// selected の点を、中心の点 pivotPoint のまわりで delta だけ回す（Free の点も、回した場所に置いて Pin にする）。
        /// 選んだ点の骨のうち、ほかの選んだ点の骨の子でない骨（いちばん根の方の骨）も FK で回す（10-20 ユーザー指示。
        /// 点の目標だけ回すと、全身 IK は回す前の姿勢から目標へ寄せるので、体ごと大きく回す形に届かず、届かない分が背骨や手足のねじれになっていた）。
        /// 腰を選んでいれば、腰は付け根ごと中心のまわりに回るので、全部の点を選んで回すと体がそのまま回る。
        /// 腰より先の骨は付け根を動かせないので、付け根を中心に回す（残りは全身 IK が目標へ寄せる）
        /// </summary>
        internal static void RotatePointsAround (IList<PoseTarget> targets, IList<BodyPointTarget> selected, BodyPointTarget pivotPoint, Quaternion delta)
        {
            // 中心は中心の点の目標（Locked ならその場所）。ほかの点は骨の上の点から回す（目標から回すと、届いていない分の食い違いが残る）
            Vector3 pivot = pivotPoint.framePosition;
            Vector3 pivotOnBone = pivotPoint.bonePosition;
            List<FkTarget> tops = TopFkTargets (targets, selected);
            // 点を先に回す（点は今の骨の上の点から回すので、骨を先に回すと二重に回る）
            foreach (BodyPointTarget point in selected) {
                if (point == pivotPoint) point.RotateAround (pivot, delta, true);
                else point.RotateBodyAround (pivot, pivotOnBone, delta, true);
            }
            // 腰の付け根も点と同じく、中心の点の骨の上の点のまわりに回して中心の点の目標へ重ねる（RotateBodyAround と同じ動かし方）
            foreach (FkTarget fk in tops) fk.RotateAroundWorld (pivotOnBone, pivot, delta);
        }

        /// <summary>
        /// selected の点の骨のうち、ほかの選んだ点の骨の子でない骨（いちばん根の方の骨）の FK。いくつか選んで動かす・回すときに、点と一緒に動かす
        /// </summary>
        static List<FkTarget> TopFkTargets (IList<PoseTarget> targets, IList<BodyPointTarget> selected)
        {
            HashSet<Transform> bones = new HashSet<Transform> ();
            foreach (BodyPointTarget point in selected) {
                if (point.editingBone != null) bones.Add (point.editingBone);
            }
            List<FkTarget> result = new List<FkTarget> ();
            foreach (PoseTarget target in targets) {
                FkTarget fk = target as FkTarget;
                if (fk != null && bones.Contains (fk.bone) && !HasAncestorIn (fk.bone, bones)) result.Add (fk);
            }
            return result;
        }

        /// <summary>
        /// 点に関する骨の FK（10-21 ユーザー指示。点を選ぶと、関する骨も選んだことにする）。
        /// 全身 IK のキーフレームは解いた骨の値と点の両方で姿勢を持つので、点だけ写して貼ると、骨は貼り先の前の姿勢のまま残っていた。
        /// 骨は、自分から根の方へたどって最初に当たる「点の乗る骨」の点に属する（点の無い肩・上胸は胸の点、指は手の点）。
        /// どの骨もどれかの点に属するので、全部の点を選べば全部の骨になる
        /// </summary>
        internal static List<FkTarget> RelatedFkTargets (IList<PoseTarget> targets, IEnumerable<BodyPointTarget> points)
        {
            HashSet<Transform> pointBones = new HashSet<Transform> ();
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null && point.editingBone != null) pointBones.Add (point.editingBone);
            }
            HashSet<Transform> selected = new HashSet<Transform> (points.Select (p => p.editingBone).Where (b => b != null));
            List<FkTarget> result = new List<FkTarget> ();
            foreach (PoseTarget target in targets) {
                FkTarget fk = target as FkTarget;
                if (fk == null || fk.bone == null) continue;
                Transform owner = fk.bone;
                while (owner != null && !pointBones.Contains (owner)) owner = owner.parent;
                if (owner != null && selected.Contains (owner)) result.Add (fk);
            }
            return result;
        }

        /// <summary>
        /// 選んでいる物がキーを打つカーブのパス。全身 IK の点を選んでいれば、点に関する骨（RelatedFkTargets）のパスも入れる
        /// </summary>
        HashSet<string> CollectSelectedPaths ()
        {
            HashSet<string> paths = new HashSet<string> ();
            List<PoseTarget> selected = SelectedTargets ().ToList ();
            foreach (PoseTarget target in selected) target.CollectKeyedPaths (paths);
            foreach (FkTarget fk in RelatedFkTargets (targets_, selected.OfType<BodyPointTarget> ())) fk.CollectKeyedPaths (paths);
            return paths;
        }

        /// <summary>t の親（t 自身は含まない）のどれかが bones にあるか</summary>
        static bool HasAncestorIn (Transform t, HashSet<Transform> bones)
        {
            for (Transform parent = t.parent; parent != null; parent = parent.parent) {
                if (bones.Contains (parent)) return true;
            }
            return false;
        }

        readonly PointTurn pointTurn_ = new PointTurn ();

        /// <summary>
        /// ギズモで点を回す 1 回のドラッグ。ドラッグの間は、毎回ドラッグの始めの操作値へ戻して解き直し、始めからの回転の合計で回し直す（10-19 ユーザー指示）。
        /// 少しずつ回すたびに今の姿勢へ足すと、FK は全身 IK で解いた後の骨を回してその値を焼くので（FkTarget.SpinRotateWorld）、
        /// 前の回で IK が足したねじれ（固定点だけでは決まらない、背骨の軸まわりなど）が次の回の始めの姿勢になって積み上がっていた。
        /// 始めから当て直せば、細かく回しても一度に回したのと同じ姿勢になる
        /// </summary>
        internal sealed class PointTurn
        {
            PreviewStage.ControlState start_;
            BodyPointTarget point_;
            Quaternion total_ = Quaternion.identity;

            /// <summary>
            /// point を delta だけ回した: 始めからの回転の合計を返す。回しはじめなら今の操作値を控え、そうでなければ控えた操作値へ戻して solve で解き直す
            /// （呼ぶ側は、返した合計で回す）
            /// </summary>
            public Quaternion Add (PreviewStage stage, BodyPointTarget point, Quaternion delta, System.Action solve)
            {
                if (start_ == null || point_ != point) {
                    start_ = stage.CaptureControlState ();
                    point_ = point;
                    total_ = Quaternion.identity;
                }
                else {
                    stage.RestoreControlState (start_);
                    solve ();
                }
                total_ = delta * total_;
                return total_;
            }

            /// <summary>ドラッグを終えた（次に回すときは、そのときの姿勢から始める）</summary>
            public void End ()
            {
                start_ = null;
                point_ = null;
                total_ = Quaternion.identity;
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

        /// <summary>
        /// 動かした後に姿勢のキーを打つ（ハンドル・Spinner を離したとき・数値欄に入れたとき）。
        /// 全身 IK の点を動かした・固定している点があるフレームなら、解いた姿勢を骨のキーとして打つ（CommitFullBodyPose）。
        /// そうでなければ全部の対象の今の値を打つ
        /// </summary>
        void CommitPose (bool pointsMoved)
        {
            if (pointsMoved || hasActivePoints) {
                CommitFullBodyPose ();
                return;
            }
            AddKeyAllCurves (true);
            keyFrames_ = null;
            RepaintView ();
        }

        /// <summary>動かした点の、まだ打っていないキーを捨てる</summary>
        void ClearPendingPointKeys ()
        {
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) point.ClearPendingKeys ();
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
                ClearPendingPointKeys ();
                SamplePose ();
                return;
            }
            PoseCommit.InUndoGroup ("Key Pose", () => PoseCommit.WriteFullBodyPose (stage_, targets_, () => BeginCurves ("Key Pose")));
            keyFrames_ = null;
            // 打ったキーから姿勢を作り直す（保存した値で見えている姿勢と同じになる）
            SamplePose ();
        }
    }

}
