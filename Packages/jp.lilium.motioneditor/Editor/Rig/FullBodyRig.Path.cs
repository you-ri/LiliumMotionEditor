using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    // 重い点をつかんでいる間の、つかんだ点から根への道（PathToRoot）の上の骨の扱い。道の上の Pin の点（腰）を運ぶ量と、運ぶ前に道を伸ばす（S88）
    public sealed partial class FullBodyRig
    {
        /// <summary>StraightenPath で、骨の間の曲がりを何倍にするかを半分ずつ絞って探す回数</summary>
        const int kStraightenSearches = 12;
        // つかんだ点の骨から根への道の上の骨か（PathToRoot）
        bool[] onPath_;
        // StraightenPath: 骨ごとの回し・回す骨の並び・骨のベクトル・骨ごとの曲がりの回し
        Vector3[] pathSegments_;
        Quaternion[] segmentTurns_;
        Quaternion[] pathTurns_;
        readonly List<int> pathChain_ = new List<int> ();

        /// <summary>骨 body の上の、運ぶ強さ 1 の Pin の点（無ければ null）</summary>
        Point FullPinOn (int body, out float rotationWeight)
        {
            rotationWeight = 0;
            foreach (Point point in points_) {
                float positionWeight;
                // つかんだ点と離れの変わらない Pin の点は、道の長さでなく、つかんだ点からの離れで運ぶ（RigidShift）
                if (point.body == body && HeavyRole (point, out positionWeight, out rotationWeight) == PointRole.CarriedPin && positionWeight >= 1
                    && !RigidToGrabbed (point, out _, out _)) return point;
            }
            rotationWeight = 0;
            return null;
        }

        /// <summary>
        /// 道の上の Lock でない重さ 1 の Pin の点（腰）を運ぶ量（ルート空間）。つかんだ点の骨から根へたどって最初に当たる、そうした Pin の点から、
        /// つかんだ点の目標までが、間の骨を伸ばしきった長さを超えた分だけ、つかんだ点の方へ。超えなければ 0。道の上にそうした Pin の点が無ければ 0。
        /// </summary>
        Vector3 PathStretch ()
        {
            if (grabbedEffectors_.Count == 0) return Vector3.zero;
            FullBodyIkEffector grabbed = data_.effectors[grabbedEffectors_[0]];
            Vector3 target = Vector3.zero;
            foreach (int e in grabbedEffectors_) target += (Vector3)data_.effectors[e].position;
            target /= grabbedEffectors_.Count;
            // つかんだ骨は向きを変えずに動き、その付け根から根の側の骨だけが伸びるとして数える（StraightenPath と同じ）。目標は、つかんだ骨の付け根が行く所。
            // つかんだ骨まで回して点を鎖の向きへそろえる長さに数えると、骨の付け根から離れた点（胸の点は背骨の付け根から前下 5cm）では、
            // 骨ごと回して点を振り上げれば届くとみなし、腰を運ばずに胸を回した（11-03 実測で、胸の点を上へ 5cm 引くと胸が 60° 回った）
            target -= solveRotations_[grabbed.bone] * (Vector3)grabbed.offset;
            // length は、つかんだ骨の付け根から骨 b の付け根までの、骨を伸ばしきった長さ
            float length = 0;
            int child = -1;
            for (int b = grabbed.bone; bodyParents_[b] >= 0; child = b, b = bodyParents_[b]) {
                int parent = bodyParents_[b];
                float rotationWeight;
                Point point = FullPinOn (parent, out rotationWeight);
                if (point != null) {
                    Vector3 pin = TargetPosition (point);
                    Vector3 pinPoint = solvePositions_[parent] + solveRotations_[parent] * point.offset;
                    // 向きも留めた Pin の点なら、その骨は回れないので、鎖は骨 b の付け根から始まる（Pin の点と一緒に動く）。
                    // 向きを留めていなければ、骨はその点まわりに回れるので、鎖は Pin の点から始まり、点から骨 b の付け根までは骨の中の決まった距離
                    // （腰の向きまで Pin のとき、腰の点から数えると届く距離を長めに見て、背骨が伸びきらないまま首が 1cm 届かなかった）
                    Vector3 anchor = rotationWeight > 0 ? solvePositions_[b] + (pin - pinPoint) : pin;
                    // 伸ばしきる手前でソルバーが詰めきれず届かなかった分は、解いてから運び足す（SolveBodies の kCarryRefines）
                    float reach = rotationWeight > 0 ? length : length + Vector3.Distance (pinPoint, solvePositions_[b]);
                    Vector3 toGrabbed = target - anchor;
                    float over = toGrabbed.magnitude - reach;
                    return over > 0 ? toGrabbed.normalized * over : Vector3.zero;
                }
                // 動かせない点で軸まわりにしか回れない骨（S89 の axisLock。鎖骨など）は、親と一緒の 1 つの塊として、子の付け根から親の付け根までを数える
                // （骨ごとに足すと、曲がったまま回れない鎖骨の所を伸ばしきれる長さに数え、届かない所まで背骨を伸ばそうとして背骨が曲がり、手も 2.5cm 届かなかった）
                // 軸まわりには回れるので、子の付け根は軸まわりの円の上を動く。親の付け根から一番遠い所までを数える
                if (child >= 0 && data_.bones[b].axisLock != 0) length += AxisLockedReach (b, child, parent) - Vector3.Distance (solvePositions_[child], solvePositions_[b]);
                else length += Vector3.Distance (solvePositions_[b], solvePositions_[parent]);
            }
            return Vector3.zero;
        }

        /// <summary>
        /// 解き始める姿勢（startPositions_ / startRotations_）で、つかんだ点の骨の付け根から道の上の Pin の点（腰）までの骨を、運んだ Pin の点から、
        /// つかんだ骨を向きを変えずに点を目標へ置いたときの付け根までの距離に合うように伸ばす。
        /// 骨の間の曲がりを同じ割合で小さくし（目標まで伸びるなら 0 倍でまっすぐ）、伸ばした分だけ目標の方へ向ける。距離が元の姿勢より近ければ何もしないので、境目で姿勢は跳ねない。
        /// Pin の点が向きを留めていない根（腰）なら、根の骨も、運んだ距離に応じて（kRootRelease まで）点まわりに目標の方へ回す。
        /// 蝶番（伸ばす手助けがソルバーにある）・軸まわりにだけ回る骨を含むとき（手を引くときの腕）と、つかんだ点が 2 つ以上のときは何もしない
        /// </summary>
        void StraightenPath (Vector3 pathShift)
        {
            if (grabbedEffectors_.Count != 1) return;
            FullBodyIkEffector grabbed = data_.effectors[grabbedEffectors_[0]];
            // 道の上で一番近い Pin の点を探す
            int pinBody = -1;
            bool turnsRoot = false;
            Vector3 pinPoint = Vector3.zero;
            for (int b = bodyParents_[grabbed.bone]; b >= 0 && pinBody < 0; b = bodyParents_[b]) {
                float rotationWeight;
                Point point = FullPinOn (b, out rotationWeight);
                if (point == null) continue;
                pinBody = b;
                turnsRoot = bodyParents_[b] < 0 && rotationWeight <= 0;
                pinPoint = solvePositions_[b] + solveRotations_[b] * point.offset;
            }
            if (pinBody < 0) return;
            pathChain_.Clear ();
            for (int b = grabbed.bone; b != pinBody; b = bodyParents_[b]) pathChain_.Insert (0, b);
            foreach (int b in pathChain_) {
                if (data_.bones[b].hinge != 0 || data_.bones[b].axisLock != 0) return;
            }
            // つかんだ骨は向きを変えずに動かし、その付け根を、点を目標へ置く所（goal）へ伸ばす（PathStretch と同じ数え方）。
            // つかんだ骨まで伸ばす向きへ回すと、骨の付け根から離れた点（胸の点）では、伸ばすたびに胸が回り、胸に付いた首・腕が大きく振れた（11-03 実測）
            int count = pathChain_.Count - 1;
            if (count <= 0) return;
            Vector3 goal = (Vector3)grabbed.position - solveRotations_[grabbed.bone] * (Vector3)grabbed.offset;
            // 根の骨は、運んだ距離に応じて回す。曲がりと一緒に回すと、伸びきる手前の短い間に回りきって、足が 1 回に 8cm 振れた（10-30 実測）。
            // 回さないと、腰と背骨の間に 21〜28° の曲がりが残った
            Vector3 rootSegment = solvePositions_[pathChain_[0]] - pinPoint;
            Vector3 pinTarget = pinPoint + pathShift;
            Quaternion rootTurn = turnsRoot
                ? Quaternion.Slerp (Quaternion.identity, Quaternion.FromToRotation (rootSegment, goal - pinTarget), Mathf.Clamp01 (pathShift.magnitude / (kRootRelease * humanScale)))
                : Quaternion.identity;
            Vector3 start = pinTarget + rootTurn * rootSegment;
            // 骨ごとの、付け根から次の骨の付け根（最後はつかんだ骨の付け根）までのベクトル
            for (int k = 0; k < count; k++) pathSegments_[k] = rootTurn * (solvePositions_[pathChain_[k + 1]] - solvePositions_[pathChain_[k]]);
            Vector3 toTarget = goal - start;
            float demand = toTarget.magnitude;
            if (demand <= StraightenedEnd (pathSegments_, count, 1, null).magnitude) return;
            // 曲がりを何倍にすれば、先端までの距離が demand になるか（倍率が小さいほど遠い）
            float bend = 0;
            if (demand < StraightenedEnd (pathSegments_, count, 0, null).magnitude) {
                float low = 0, high = 1;
                for (int k = 0; k < kStraightenSearches; k++) {
                    float middle = (low + high) * 0.5f;
                    if (StraightenedEnd (pathSegments_, count, middle, null).magnitude < demand) high = middle;
                    else low = middle;
                }
                bend = high;
            }
            Vector3 reached = StraightenedEnd (pathSegments_, count, bend, segmentTurns_);
            Quaternion aim = Quaternion.Slerp (Quaternion.identity, Quaternion.FromToRotation (reached, toTarget), 1 - bend);
            for (int b = 0; b < bodies_.Count; b++) pathTurns_[b] = Quaternion.identity;
            pathTurns_[pinBody] = rootTurn;
            for (int k = 0; k < count; k++) pathTurns_[pathChain_[k]] = aim * segmentTurns_[k] * rootTurn;
            // Pin の点の骨が根でなければ（Pin の点が根と道の途中の骨に乗る）、その骨の根の方の骨も Pin の点と一緒に運ぶ。
            // Pin の点の骨だけを運ぶと、親との間の骨が伸びた姿勢から解き始め、ソルバーは解き始めの姿勢で骨の長さを測るので、伸びたまま解いた。
            // 骨へは向きだけを書き戻すので、Pin の点は元の場所に残り、つかんだ点が届かなかった（11-08 の単純な鎖のテストで 23cm）
            int top = pinBody;
            while (bodyParents_[top] >= 0) top = bodyParents_[top];
            // 骨の木を根からたどって置き直す（道の外の骨は親と一緒に動く。つかんだ骨は回しが無いままで、向きを変えない）
            for (int b = 0; b < bodies_.Count; b++) {
                int parent = bodyParents_[b];
                if (parent >= 0 && !pathChain_.Contains (b)) pathTurns_[b] = pathTurns_[parent];
                if (b == pinBody) startPositions_[b] = pinTarget + rootTurn * (solvePositions_[b] - pinPoint);
                else if (parent >= 0) startPositions_[b] = startPositions_[parent] + pathTurns_[parent] * (solvePositions_[b] - solvePositions_[parent]);
                else if (b == top) startPositions_[b] = solvePositions_[b] + pathShift;
                startRotations_[b] = pathTurns_[b] * solveRotations_[b];
            }
        }

        /// <summary>骨のベクトル segments（先頭から count 本）の間の曲がりを bend 倍にしたときの、先端までのベクトル。turns があれば、骨ごとの回しを入れる</summary>
        static Vector3 StraightenedEnd (Vector3[] segments, int count, float bend, Quaternion[] turns)
        {
            Vector3 sum = segments[0];
            Quaternion turn = Quaternion.identity;
            if (turns != null) turns[0] = turn;
            for (int k = 1; k < count; k++) {
                Quaternion between = Quaternion.FromToRotation (segments[k - 1], segments[k]);
                turn = turn * Quaternion.Slerp (Quaternion.identity, between, bend) * Quaternion.Inverse (between);
                if (turns != null) turns[k] = turn;
                sum += turn * segments[k];
            }
            return sum;
        }

        /// <summary>
        /// 軸まわりにだけ回れる骨 b（axisLock）の子 child の付け根が、親 parent の付け根から一番離れられる距離（今の姿勢 solvePositions_ で）。
        /// 子の付け根は、b の付け根を通る軸まわりの円の上を動く
        /// </summary>
        float AxisLockedReach (int b, int child, int parent)
        {
            Vector3 origin = solvePositions_[b];
            Vector3 axis = (solveRotations_[b] * (Vector3)data_.bones[b].lockAxis).normalized;
            Vector3 toChild = solvePositions_[child] - origin;
            float along = Vector3.Dot (toChild, axis);
            float radius = (toChild - axis * along).magnitude;
            Vector3 center = origin + axis * along - solvePositions_[parent];
            Vector3 across = center - axis * Vector3.Dot (center, axis);
            return Mathf.Sqrt (center.sqrMagnitude + radius * radius + 2 * radius * across.magnitude);
        }

        /// <summary>道の上の根（腰）の骨（無ければ -1）</summary>
        int RootOnPath (bool[] path)
        {
            for (int b = 0; b < bodies_.Count; b++) {
                if (path[b] && bodyParents_[b] < 0) return b;
            }
            return -1;
        }

        /// <summary>骨 body（-1 なら何もしない）の、元の向きに留める硬さを入れる</summary>
        void SetStiffness (int body, float stiffness)
        {
            if (body < 0) return;
            FullBodyIkBone bone = data_.bones[body];
            bone.stiffness = stiffness;
            data_.bones[body] = bone;
        }

        /// <summary>
        /// 道の上の Pin の点を pathShift の何割まで運べるか（0〜1）。道の外の Lock の点（足）ごとに、手足の付け根（股）を pathShift と一緒に動かしたとき、
        /// 付け根から Lock の点の目標までが手足の長さを超えない一番大きい割合（2 次方程式）。付け根の場所は、控えた保持解きの姿勢（yieldedPositions_）で見る
        /// </summary>
        float LockLimit (bool[] path, Vector3 pathShift)
        {
            float limit = 1;
            float ss = pathShift.sqrMagnitude;
            if (ss <= 0) return limit;
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (HeavyRole (point, out positionWeight, out rotationWeight) != PointRole.Lock || path[point.body]) continue;
                float length;
                int root = LimbRoot (point, path, out length);
                Vector3 a = yieldedPositions_[root] - TargetPosition (point);
                // |a + t s| = length を満たす t のうち大きい方。今でも届かなければ 0
                float c = a.sqrMagnitude - length * length;
                if (c > 0) return 0;
                float b = Vector3.Dot (a, pathShift);
                float t = (-b + Mathf.Sqrt (Mathf.Max (0, b * b - ss * c))) / ss;
                limit = Mathf.Min (limit, Mathf.Max (0, t));
            }
            return limit;
        }

        /// <summary>
        /// 点の手足の付け根の骨（点の骨から根へたどり、つかんだ点から根への道 path に出る手前の骨。肩・股）と、付け根から点までの手足の長さ（骨を伸ばしきった長さ）
        /// </summary>
        int LimbRoot (Point point, bool[] path, out float length)
        {
            int root = point.body;
            length = point.offset.magnitude;
            // 軸まわりにだけ回る骨（S89。鎖骨）は手足の方へ向けないので、その子の付け根から数える。鎖骨も伸ばせる長さに数えると、手の目標が届かない所に置かれ、
            // 手に引かれて上胸が 21° 曲がったまま残った（10-30 実測）
            while (bodyParents_[root] >= 0 && !path[bodyParents_[root]] && data_.bones[bodyParents_[root]].axisLock == 0) {
                length += Vector3.Distance (startPositions_[root], startPositions_[bodyParents_[root]]);
                root = bodyParents_[root];
            }
            return root;
        }

        /// <summary>つかんだ点の骨から根（腰）へたどる道の上の骨（足の箱で動かす足は、足の根の骨から）</summary>
        bool[] PathToRoot ()
        {
            bool[] path = onPath_;
            System.Array.Clear (path, 0, path.Length);
            foreach (Point point in grabbed_) {
                int body = IsFootBoxed (point) ? point.floorGroup : point.body;
                for (int b = body; b >= 0 && !path[b]; b = bodyParents_[b]) path[b] = true;
            }
            return path;
        }
    }

}
