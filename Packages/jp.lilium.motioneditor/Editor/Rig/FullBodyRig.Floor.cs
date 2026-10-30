using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    // 床（Floor Clamp。点の止める高さ・足の箱・ソルバーへ渡す床との当たり）
    public sealed partial class FullBodyRig
    {
        /// <summary>足が平らとみなす、踵とつま先の高さの差（メートル）。傾きを調べる刻み（0.25°）で足の長さ 25cm なら 1.1mm ずつ変わる</summary>
        const float kFlatSlope = 2e-3f;
        /// <summary>足の止める高さを探すとき、傾けて調べる刻み（度）と回数（左右合わせて。±90° まで）</summary>
        const float kFloorRollStep = 0.25f;
        const int kFloorRollSteps = 720;

        /// <summary>
        /// 点を動かすとき、床（ワールドの Y = 0）より下へ行かない点の高さの下限（Floor Clamp）。
        /// 接地の点（足首・足裏の点）は、今の足の形のまま前後に傾けて、足裏の球がどれも床の上に乗るいちばん低い高さ（踵とつま先が両方着く所。
        /// まっすぐなつま先なら足が平らに着く高さ）。そこまでは下ろせ、下ろす間に潜る足裏は、
        /// 解くときに床との当たりで足が転がって床に沿う（FullBodyIk.SolveContact。つま先が着いたまま踵が下りて、足が平らになっていく）。
        /// 足首だけを床まで下げられると、足裏を床の上に置くには足をつま先が上を向くまで回すしかなく、足が下がらずに起きた（10-22 ユーザー指示）。
        /// 今の足の形のまま止めると、つま先の下がった足は、つま先が床に着いた所からまったく下ろせなかった。
        /// 足の向きを固定している足も、床との当たりが向きの固定に勝って回る。
        /// ほかの点は自分の球の半径
        /// </summary>
        public float GetFloorLimit (BodyPointId id)
        {
            Point point = Find (id);
            if (point == null) return 0;
            if (point.floorGroup < 0) return point.radius;
            // 足の点の、今の場所（つま先の骨の今の曲がりも入る。向きを固定していれば、その向きの値の向き）を、今の向きから前後に傾けて、
            // 足裏の球がどれも床の上に乗るいちばん低い高さを探す。基準姿勢の形で決めると、つま先の骨が曲がっていたり足が横に傾いていたりする足は、
            // その高さまで下ろしたときに前後に回すだけでは床の上に乗らず、足が踵側へ倒れてつま先が 8〜12cm 浮いた（10-22）
            Vector3 self = BonePointWorld (point);
            Quaternion toTarget = point.hasRotation && point.values.rotationWeight >= 1 ? PointWorldRotation (point) * Quaternion.Inverse (point.bone.rotation) : Quaternion.identity;
            List<KeyValuePair<Vector3, float>> arms = new List<KeyValuePair<Vector3, float>> ();
            Vector3 forward = Vector3.zero;
            foreach (Point other in points_) {
                if (other == point || other.floorGroup != point.floorGroup) continue;
                Vector3 arm = toTarget * (BonePointWorld (other) - self);
                arms.Add (new KeyValuePair<Vector3, float> (arm, other.radius));
                forward += arm;
            }
            forward = Vector3.ProjectOnPlane (forward, Vector3.up);
            Vector3 axis = forward.sqrMagnitude > 1e-8f ? Vector3.Cross (forward.normalized, Vector3.up).normalized : Vector3.right;
            float slope = FloorSlope (arms, forward, Quaternion.identity);
            float best = float.MaxValue;
            for (int step = -kFloorRollSteps / 2; step <= kFloorRollSteps / 2; step++) {
                Quaternion tilt = Quaternion.AngleAxis (step * kFloorRollStep, axis);
                // 足が平ら（踵とつま先が同じ高さ）になる所を越えては傾けない（越えると、踵やつま先だけで立って足首がいくらでも下がる）
                if (!IsTiltAllowed (slope, FloorSlope (arms, forward, tilt))) continue;
                float limit = point.radius;
                foreach (KeyValuePair<Vector3, float> arm in arms) limit = Mathf.Max (limit, arm.Value - (tilt * arm.Key).y);
                best = Mathf.Min (best, limit);
            }
            return best;
        }

        /// <summary>
        /// 今の前後の傾き now から、傾き tilted へ傾けてよいか。平らに近づく向きだけで、平らになる所（傾き 0）を越えない。今が平らなら、平らなままの向きだけ
        /// </summary>
        static bool IsTiltAllowed (float now, float tilted)
        {
            if (Mathf.Abs (tilted) <= kFlatSlope) return true;
            // 平らに近づく向きだけ（踵だけで立った足をさらに反らせれば足首はいくらでも下がるが、下げても足は平らにならない。10-22）
            return now * tilted > 0 && Mathf.Abs (tilted) <= Mathf.Abs (now) + kFlatSlope;
        }

        /// <summary>
        /// 足の前後の傾き: tilt だけ傾けたときの、いちばん前の点といちばん後ろの点（forward の向きで）の球の下の端の高さの差（前が高ければ正）。
        /// 足が平らなら 0
        /// </summary>
        static float FloorSlope (List<KeyValuePair<Vector3, float>> arms, Vector3 forward, Quaternion tilt)
        {
            if (arms.Count == 0) return 0;
            int front = 0, back = 0;
            for (int i = 1; i < arms.Count; i++) {
                if (Vector3.Dot (arms[i].Key, forward) > Vector3.Dot (arms[front].Key, forward)) front = i;
                if (Vector3.Dot (arms[i].Key, forward) < Vector3.Dot (arms[back].Key, forward)) back = i;
            }
            return ((tilt * arms[front].Key).y - arms[front].Value) - ((tilt * arms[back].Key).y - arms[back].Value);
        }

        /// <summary>
        /// Floor Clamp で足の点（grabbed）をつかんで動かしている間、同じ足のほかの Pin の点（Locked でないもの）を固定に使わず、足と一緒に動かす（10-22 ユーザー指示）。
        /// 足裏の点が Pin のままだと、足首をつかんで下げても足裏の点がその場に留まり、足がひとかたまりで下がらずに、つま先の骨だけが曲がった。
        /// 一緒に動かした点は、離したときに骨の今の場所で Pin のまま付け直す（SyncToBone。Pin のいつもの決まり）。Locked の点は留めたまま。
        /// キーを打ったとき・クリップから姿勢を当て直したときに止める（EndGrab）
        /// </summary>
        public void CarryFootPins (BodyPointId grabbed)
        {
            Point point = Find (grabbed);
            if (point == null || point.floorGroup < 0) return;
            foreach (Point other in points_) {
                if (other == point || other.passive || other.floorGroup != point.floorGroup || other.values.locked > 0.5f) continue;
                if (other.values.positionWeight > 0 || (other.hasRotation && other.values.rotationWeight > 0)) carried_.Add (other);
            }
        }

        /// <summary>ドラッグの間に持ち越す足の箱（足の根の骨の番号ごと）と、箱の頂点にした点</summary>
        readonly Dictionary<int, FootBox> footBoxes_ = new Dictionary<int, FootBox> ();
        readonly Dictionary<int, List<Point>> footBoxPoints_ = new Dictionary<int, List<Point>> ();
        /// <summary>足の箱のつま先の骨の番号（無ければ -1）と、始めの足から見たつま先の向き</summary>
        readonly Dictionary<int, KeyValuePair<int, Quaternion>> footBoxToes_ = new Dictionary<int, KeyValuePair<int, Quaternion>> ();

        /// <summary>
        /// Floor Clamp で足の点（grabbed）をつかんで hand（ワールド）へ動かす: 足を、接地の点を頂点にした硬い箱として床に押し当てたときのように動かし（FootBox）、
        /// 点の置き場所（届かなければ手前）を placed に返す（10-22 ユーザー指示）。箱はドラッグの間持ち越し、キーを打つとき・クリップから当て直すときに捨てる（EndGrab）。
        /// 解くときは、足の骨を箱の位置と向きに強さ 1 で留め、脚は全身 IK で届かせる（足のほかの点は使わない）。
        /// 向きを持つ点（足首の点）の向きの値は、向きを固定していれば箱の向きに合わせる（キーと姿勢が食い違わない）。足の点でなければ false
        /// </summary>
        public bool TryMoveFoot (BodyPointId grabbed, Vector3 hand, out Vector3 placed)
        {
            placed = hand;
            Point point = Find (grabbed);
            if (point == null || point.floorGroup < 0 || point.floorGroup >= bodies_.Count) return false;
            int group = point.floorGroup;
            FootBox box;
            List<Point> members;
            if (!footBoxes_.TryGetValue (group, out box)) {
                members = new List<Point> ();
                List<Vector3> vertices = new List<Vector3> ();
                List<float> radius = new List<float> ();
                List<bool> fixedVertices = new List<bool> ();
                List<bool> toeVertices = new List<bool> ();
                Transform root = bodies_[group];
                int toes = -1;
                Vector3 tip = Vector3.zero;
                foreach (Point other in points_) {
                    if (other.floorGroup != group) continue;
                    members.Add (other);
                    vertices.Add (BonePointWorld (other));
                    radius.Add (other.radius);
                    // 動かさないと決めた点（Locked）は、置いた場所から動かない支点。つかんだ点は手で動かすので除く
                    fixedVertices.Add (other != point && other.values.locked > 0.5f && other.values.positionWeight >= 1);
                    // つま先の骨（足の骨の子）の上の点は、つま先の付け根で曲がる
                    bool onToes = other.body != group && other.body >= 0;
                    toeVertices.Add (onToes);
                    if (onToes && toes < 0) {
                        toes = other.body;
                        tip = BonePointWorld (other);
                    }
                }
                Vector3 joint = toes >= 0 ? bodies_[toes].position : Vector3.zero;
                // つま先が曲がる軸は、足の横向き（上向きとつま先の向きに直交する）
                Vector3 axis = toes >= 0 ? Vector3.Cross (Vector3.up, tip - joint) : Vector3.zero;
                if (toes >= 0 && axis.sqrMagnitude < 1e-10f) axis = root.rotation * Vector3.right;
                box = new FootBox (root.position, root.rotation, vertices, radius, fixedVertices, toes >= 0 ? toeVertices : null, joint, axis.normalized);
                footBoxes_[group] = box;
                footBoxPoints_[group] = members;
                footBoxToes_[group] = new KeyValuePair<int, Quaternion> (toes, toes >= 0 ? Quaternion.Inverse (root.rotation) * bodies_[toes].rotation : Quaternion.identity);
            }
            members = footBoxPoints_[group];
            int index = members.IndexOf (point);
            if (index < 0) return false;
            placed = box.Move (index, hand);
            foreach (Point other in members) {
                if (other.bone == bodies_[group] && other.hasRotation && other.values.rotationWeight > 0) {
                    other.control.localRotation = EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * box.rotation * Quaternion.Inverse (other.restRootRotation));
                }
            }
            return true;
        }

        /// <summary>点が、ドラッグの間に持ち越している足の箱の頂点か（Floor Clamp の止める高さは箱が受け持つ）</summary>
        public bool IsFootBoxed (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && IsFootBoxed (point);
        }

        /// <summary>Floor Clamp の足の箱で動かしている足の点か（点としては解かない。AddFootBoxes）</summary>
        bool IsFootBoxed (Point point)
        {
            return point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup);
        }

        /// <summary>足の箱（Floor Clamp で動かしている足）の目標を足す。足す前の数を受け取り、足した後の数を返す</summary>
        int AddFootBoxes (int count)
        {
            if (solveOnly_ != null) return count;
            Quaternion rootInverse = Quaternion.Inverse (rootTransform.rotation);
            Vector3 rootPosition = rootTransform.position;
            foreach (KeyValuePair<int, FootBox> pair in footBoxes_) {
                if (count >= data_.effectors.Length) break;
                FootBox box = pair.Value;
                data_.effectors[count++] = new FullBodyIkEffector {
                    bone = pair.Key,
                    priority = 1,
                    position = rootInverse * (box.position - rootPosition),
                    rotation = rootInverse * box.rotation,
                    positionWeight = 1,
                    rotationWeight = 1,
                    blendBone = -1,
                };
                // つま先の骨も、箱で曲げた向きに留める
                KeyValuePair<int, Quaternion> toes = footBoxToes_[pair.Key];
                if (toes.Key < 0 || count >= data_.effectors.Length) continue;
                data_.effectors[count++] = new FullBodyIkEffector {
                    bone = toes.Key,
                    priority = 1,
                    position = rootInverse * (box.toeJoint - rootPosition),
                    rotation = rootInverse * (box.rotation * box.toeTurn * toes.Value),
                    positionWeight = 1,
                    rotationWeight = 1,
                    blendBone = -1,
                };
            }
            return count;
        }

        /// <summary>
        /// 解いた姿勢で、接地の点（定義の contact）の球を床（ワールドの Y = 0）より下へ置かない（Controller の Floor Clamp）。
        /// 全身 IK の中で床との当たりとして解く（FullBodyIkContact。足は床に当たった所を支点に転がる。点の目標には触らない）。
        /// 操作のときだけのフラグで、窓が配る（クリップには保存しない）
        /// </summary>
        public bool floorClamp { get; set; }

        /// <summary>
        /// 床との当たりをソルバーへ渡す（Floor Clamp。姿勢を作るときだけ。前後のキーで Locked の点だけを留めるとき（SolveOnly）は渡さない）。
        /// 10-22 まではエディタ側で、解いた後に足を持ち上げる・回す・点の向きの値を回すなどを重ねていて、床に当たると足の転がり方が不自然だった（ユーザー報告）
        /// </summary>
        void SetContacts (float floorHeight, Vector3 up)
        {
            int count = 0;
            if (floorClamp && solveOnly_ == null) {
                foreach (Point point in points_) {
                    if (!point.contact || point.body < 0 || count >= data_.contacts.Length) continue;
                    // 足の箱で動かしている足は、箱が床との当たりを受け持つ（全身 IK の当たりを重ねると、箱がちょうど床に置いた足裏の点を潜ったとみなして足を回し直した）
                    if (point.floorGroup >= 0 && footBoxes_.ContainsKey (point.floorGroup)) continue;
                    // つま先の点などは、足（接地の点のまとまりの根の骨）の上の点として渡す（解く前の姿勢での、足から見た場所）。
                    // つま先の骨に当てると、足が転がる前につま先の骨だけが反った。足の形（つま先の曲がり）は保つ（10-22 ユーザー指示）
                    int body = point.floorGroup >= 0 ? point.floorGroup : point.body;
                    Vector3 offset = point.offset;
                    if (body != point.body) {
                        Vector3 position = solvePositions_[point.body] + solveRotations_[point.body] * point.offset;
                        offset = Quaternion.Inverse (solveRotations_[body]) * (position - solvePositions_[body]);
                    }
                    data_.contacts[count++] = new FullBodyIkContact { bone = body, offset = offset, radius = point.radius };
                }
            }
            data_.contactCount = count;
            data_.floorNormal = up;
            data_.floorHeight = floorHeight;
        }

        /// <summary>
        /// 向きを固定した点の骨に、ほかにも位置を強さ 1 で固定した点があるか（Floor Clamp の間は、向きを点の位置で決める）。
        /// 足首の向きを固定し、足裏の中心を Locked にして足首を持ち上げると、足が回れずに踵が付いてこなかった（10-22 ユーザー指示）
        /// </summary>
        bool HasOtherPositionPin (Point point)
        {
            foreach (Point other in points_) {
                if (other != point && !other.passive && other.body == point.body && !carried_.Contains (other) && Mathf.Clamp01 (other.values.positionWeight) * pull >= 1) return true;
            }
            return false;
        }
    }

}
