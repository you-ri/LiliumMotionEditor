using UnityEngine;

namespace Lilium
{

    // 重い点をつかんでいる間の、つかんだ点と離れの変わらない点（間に回れる関節が無い点）の扱い（11-08）
    public sealed partial class FullBodyRig
    {
        /// <summary>骨の付け根からの離れがこれ以下（m）の点は、付け根の点とみなす（JointParent）</summary>
        const float kJointOffset = 1e-4f;

        /// <summary>
        /// 2 つの点の離れが、骨の形だけで決まるか（間に回れる関節が無い）。点が固く付く骨（乗る骨・付け根の点なら親の骨・輪の関節の付け根の点なら
        /// つないだ先の骨）に、同じ骨があるか。付け根の点は、骨の長さが変わらないので親の骨に付いて動く
        /// （手首の点と前腕に乗る肘の点・手首の点と手のひらの点・持った物の原点の点と手首の点）。輪の関節（S105）の付け根の点も、つないだ先の骨に付いて動く
        /// </summary>
        bool RigidPair (Point a, Point b)
        {
            // 解く骨に乗っていない点（足の箱で運ぶ点など）は数えない
            if (a.body < 0 || b.body < 0) return false;
            if (a.body == b.body) return true;
            int parentA = JointParent (a), parentB = JointParent (b);
            int loopA = LoopParent (a), loopB = LoopParent (b);
            return parentA == b.body || parentB == a.body || (parentA >= 0 && parentA == parentB)
                || (loopA >= 0 && (loopA == b.body || loopA == parentB || loopA == loopB))
                || (loopB >= 0 && (loopB == a.body || loopB == parentA));
        }

        /// <summary>離れの変わらない 2 点で、どちらかが輪の関節（S105）でつないだ骨の付け根の点か</summary>
        bool LoopPair (Point a, Point b)
        {
            return a.body >= 0 && b.body >= 0 && (LoopParent (a) >= 0 || LoopParent (b) >= 0) && RigidPair (a, b);
        }

        /// <summary>リグ定義の輪の関節（S105）でつないだ骨の付け根の点なら、つないだ先の骨（そうでなければ -1）</summary>
        int LoopParent (Point point)
        {
            if (point.offset.sqrMagnitude > kJointOffset * kJointOffset) return -1;
            foreach (LoopJoint joint in loopJoints_) {
                if (bodies_[point.body] == joint.bone) return bodies_.IndexOf (joint.to);
            }
            return -1;
        }

        /// <summary>骨の付け根の点なら、その骨の親の骨（付け根の点でないか、根の骨なら -1）</summary>
        int JointParent (Point point)
        {
            return point.offset.sqrMagnitude <= kJointOffset * kJointOffset ? bodyParents_[point.body] : -1;
        }

        /// <summary>元の姿勢（解く前）での、2 つの点の離れ（m）</summary>
        float InputDistance (Point a, Point b)
        {
            Vector3 from = solvePositions_[a.body] + solveRotations_[a.body] * a.offset;
            Vector3 to = solvePositions_[b.body] + solveRotations_[b.body] * b.offset;
            return Vector3.Distance (from, to);
        }

        /// <summary>
        /// 点が、つかんだ点との離れの変わらない点か（RigidPair）。そうなら、つかんだ点の目標と、元の姿勢での 2 つの点の離れを返す。
        /// 重さ 1 の Pin の点なら、つかんだ骨を点のまわりに回せば留まり、届かなければ（離れが遠すぎても近すぎても）、つかんだ点から決まった離れのまま、
        /// 必要な分だけ引かれる（体の点の Pin の決まり「その場を維持する。ただし引っ張ることはできる」。11-08 ユーザー決定「つかんだ点を届かせ、Pin の点は必要な分だけ引く」）。
        /// 手足の長さで運ぶ量を決める形（BranchShifts の手足・PathStretch）は、つかんだ骨を回せることを数えず、近すぎる所へ引いたときも運ばなかった。
        /// そのまま解くと、ソルバーがつかんだ点と Pin の点を取り合い、Pin の点が最小の所から 7cm ずれた所へ押されて、引くたびに 2 つの姿勢の間を跳んだ
        /// （11-07 ユーザー報告「手首を動かしていると、たまに棒の姿勢がワープする」。11-08 の単純な鎖のテストで 1 回 18cm）。
        /// 根を留めない定義（S98）でも同じに扱う
        /// </summary>
        bool RigidToGrabbed (Point point, out Vector3 grabbedTarget, out float distance)
        {
            grabbedTarget = Vector3.zero;
            distance = 0;
            foreach (Point grabbed in grabbed_) {
                if (grabbed == point || !RigidPair (point, grabbed) || !GetWeights (grabbed, out _, out _)) continue;
                grabbedTarget = TargetPosition (grabbed);
                distance = InputDistance (grabbed, point);
                return true;
            }
            return false;
        }

        /// <summary>運ぶ Pin の点（CarriedPin）で、つかんだ点と離れの変わらない点か。こうした点は、道の上の骨に乗っていても道の長さでなく、つかんだ点からの離れで運ぶ（RigidShift）</summary>
        bool CarriedRigid (Point point)
        {
            return HeavyRole (point, out _, out _) == PointRole.CarriedPin && RigidToGrabbed (point, out _, out _);
        }

        /// <summary>
        /// つかんだ点と離れの変わらない Pin の点（CarriedRigid）の目標を、つかんだ点の目標から決まった離れの球の上で一番近い所へ運ぶ量（ルート空間）。
        /// 離れが元のままなら 0（留まる）。そうした点でなければ 0
        /// </summary>
        Vector3 RigidShift (Point point)
        {
            if (!CarriedRigid (point)) return Vector3.zero;
            RigidToGrabbed (point, out Vector3 grabbedTarget, out float distance);
            Vector3 toGrabbed = grabbedTarget - TargetPosition (point);
            float gap = toGrabbed.magnitude;
            return gap > kEpsilon ? toGrabbed / gap * (gap - distance) : Vector3.zero;
        }

        /// <summary>
        /// つかんだ点の目標 target を、つかんだ点と離れの変わらない Lock の点（RigidPair）から、元の姿勢での離れの球の上で一番近い所にする（ルート空間）。
        /// Lock の点は動かないので、つかんだ点はそこまでしか行けない。目標のまま解くと、ソルバーが Lock の点と取り合って骨を回しきれず、
        /// つかんだ点が元の場所に残ったまま、引くたびに別の答えへ跳んだ（11-08 の単純な鎖のテストで 1 回 20cm）。そうした Lock の点が無ければ target のまま
        /// </summary>
        Vector3 ClampToLock (Point grabbed, Vector3 target)
        {
            foreach (Point point in points_) {
                if (point == grabbed || !RigidPair (point, grabbed) || HeavyRole (point, out _, out _) != PointRole.Lock) continue;
                Vector3 center = TargetPosition (point);
                Vector3 away = target - center;
                if (away.sqrMagnitude <= kEpsilon * kEpsilon) {
                    away = solvePositions_[grabbed.body] + solveRotations_[grabbed.body] * grabbed.offset - (solvePositions_[point.body] + solveRotations_[point.body] * point.offset);
                }
                return center + away.normalized * InputDistance (grabbed, point);
            }
            return target;
        }
    }

}
