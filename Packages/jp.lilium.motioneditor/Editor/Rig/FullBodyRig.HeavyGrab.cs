using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Lilium
{

    // 重い点をつかんでいる間の解き直し（S88 の決まり 9。届かない・Lock の点が動いたとき、届かなかった分だけ Pin の点の目標を運ぶ）
    public sealed partial class FullBodyRig
    {
        /// <summary>重い点をつかんで動かしているとき、つかんだ点が届いたとみなす残り（m）。これより離れていれば、Pin の点の目標を届かなかった分だけ運んで解き直す（HeavyEffectors）</summary>
        const float kReachTolerance = 1e-3f;
        /// <summary>Pin の点の目標を運ぶと Lock の点が動くときに、運ぶ割合を半分ずつ絞って探す回数（1/2^回数 の細かさ）</summary>
        const int kShiftSearches = 4;
        /// <summary>StraightenPath で、骨の間の曲がりを何倍にするかを半分ずつ絞って探す回数</summary>
        const int kStraightenSearches = 12;
        /// <summary>運んだ目標で解いてもつかんだ点が届かなかったとき、届かなかった分を道の上の Pin の点（腰）へ運び足して解き直す回数の上限</summary>
        const int kCarryRefines = 4;
        /// <summary>保持解きでつかんだ点が届かなかった分がこの長さ（× humanScale）に達するまでに、根（腰）の向きを留める硬さを 0 まで緩める（ResolveHeavyGrab）</summary>
        const float kRootRelease = 0.2f;
        // 重い点をつかんでいる間の、1 回目に解いたつかんだ点の目標の並び（届いたかを見る）
        readonly List<int> grabbedEffectors_ = new List<int> ();
        // 元の姿勢での、Lock の点ごとのずれ（m。lockedEffectors_ と同じ並び。LocksHeld で、Lock の点が動いたかを見る基準）
        readonly List<float> lockBases_ = new List<float> ();
        // 解き直しで解き始める姿勢（元の姿勢 solvePositions_ / solveRotations_ から、StraightenPath・運び足しで変わる）
        Vector3[] startPositions_;
        Quaternion[] startRotations_;
        // つかんだ点の骨から根への道の上の骨か（PathToRoot）
        bool[] onPath_;
        // StraightenPath: 骨ごとの回し・回す骨の並び・骨のベクトル・骨ごとの曲がりの回し
        Vector3[] pathSegments_;
        Quaternion[] segmentTurns_;
        Quaternion[] pathTurns_;
        readonly List<int> pathChain_ = new List<int> ();
        // BranchShifts で決めた、道の外の Pin の点を運ぶ量（points_ と同じ並び。ルート空間）
        Vector3[] branchShifts_;
        // 重い点をつかんでいる間の、Lock の点の目標の並び（Lock の点が動いたかを見る）と、つかんだ点を譲らせて解いた姿勢（保持解き）
        readonly List<int> lockedEffectors_ = new List<int> ();
        Vector3[] yieldedPositions_;
        Quaternion[] yieldedRotations_;

        // 重い点をつかんで解いた答えが届かないか Lock の点が動いたときの解き直し
        // 届かないか Lock の点が動いたら、届かなかった分だけ Pin の点の目標を運んで解き直す（10-28。Fable の見立てを受けて、段に分けて外す形から換えた）:
        // 1. 保持解き: Pin の点・Lock の点を強さ 1、つかんだ点を譲らせて（kLightPull）解き、つかんだ点が届かなかった分 shift を測る。
        // 2. つかんだ点の骨から根（腰）へたどる道の上の Pin の点（首なら腰）の目標を、間の骨（背骨）が伸びきるまでは運ばず、超えた分だけ運ぶ（PathStretch）。
        //    Lock の点（足）が届く所まで（LockLimit）。届かないほど、根（腰）の向きを元の向きに留める硬さを緩める。
        // 3. 道の外の Pin の点は、その姿勢で、手足の付け根（肩・股）から目標までが手足の長さ（伸びきった長さ）を超えた分だけ、付け根の方へ運ぶ（BranchShifts）。
        //    腕が伸びきるまでは動かず、伸びきったらその先だけ付いてくる（10-28 ユーザー報告「首を持ち上げると、手首がのびきっていないのに、手首が動いてしまう」、
        //    10-29「首をどんどんひっぱったとき腕がのびきらない」。首が届かなかった分を両手に運ぶと、体が傾くと肩の動きと合わず、運びすぎて腕が曲がった）。
        // 4. Lock の点が動いたら、運ぶ割合を絞る（CarryPins）。Lock の点が無ければ、つかんだ点を強さ 1 にして、運んだ目標のまま解く。
        //    それでもつかんだ点が届かなければ、届かなかった分を道の上の Pin の点（腰）へ運び足して解き直す（kCarryRefines 回まで）。
        // どの量も前の答えから連続に決まり、届く所では 0 なので、境目で姿勢が跳ねず、元の姿勢（キーの形）へ戻すこともない
        // （10-28 ユーザー指示「ひっぱる距離に応じて、強制的に姿勢をもとに戻す制御をしているが、そういったことはしないようにして」）
        void ResolveHeavyGrab ()
        {
            bool[] path = PathToRoot ();
            System.Array.Copy (solvePositions_, startPositions_, bodies_.Count);
            System.Array.Copy (solveRotations_, startRotations_, bodies_.Count);
            SolveHeavyTargets (path, Vector3.zero, 0, true);
            Vector3 shift = GrabbedShift ();
            if (shift.sqrMagnitude <= kReachTolerance * kReachTolerance) return;
            KeepYielded ();
            // 運んだあと Lock の点が動いたかは、保持解き（運ぶ前）のずれと比べる。同じ足に Lock の点が 3 つ以上あると、首に引かれて足が少し回るだけで
            // どれかの点のずれが元の姿勢より増え、どれだけ少なく運んでも動いたとみなした（10-29 実測で、両足が Lock のキーで腰が上がらなかった）
            for (int k = 0; k < lockedEffectors_.Count; k++) lockBases_[k] = Mathf.Max (lockBases_[k], data_.residuals[lockedEffectors_[k]]);
            // 根（腰）の向きを元の姿勢に留める硬さを、届かなかった分に応じて緩める。留めたままだと、腰の点は向きを留めていなくても
            // 腰の骨が元の向きのまま残り、手を横へ引くと背骨の付け根だけが 50° 曲がった（10-29 ユーザー報告「左手を+Xに引いたが体、腰あたりが伸びきらない」）。
            // 届く所では 0 なので、境目で姿勢は跳ねない
            int root = RootOnPath (path);
            float stiffness = root >= 0 ? data_.bones[root].stiffness : 0;
            SetStiffness (root, stiffness * (1 - Mathf.Clamp01 (shift.magnitude / (kRootRelease * humanScale))));
            try {
                CarryPinsToGrabbed (path);
            }
            finally {
                SetStiffness (root, stiffness);
            }
        }

        /// <summary>解き直しの本体: 届かなかった分だけ、道の上・道の外の Pin の点の目標を運んで解く</summary>
        void CarryPinsToGrabbed (bool[] path)
        {
            bool locks = lockedEffectors_.Count > 0;
            // 道の上の Pin の点（腰）を運ぶ量は、長さから決める（PathStretch）: つかんだ点の一番近くの道の上の Pin の点から、つかんだ点の目標までが、
            // 間の骨を伸ばしきった長さを超えた分だけ。体が伸びきるまで腰は動かず、伸びきったらその先だけ付いてくる。
            // 譲らせて解いた答えの届かなかった分で決めると、譲らせた首が関節の硬さのばねに負けて背骨を伸ばしきれず、背骨が曲がったまま腰が運ばれた
            // （10-29 ユーザー報告「首をひっぱっても体が伸び切らない」。腰から首までが、伸びきった長さの 90% で止まっていた）
            Vector3 shift = PathStretch ();
            // Lock の点（足）は、手足の付け根（股）から目標までが手足の長さを超えない所までしか、道の上の Pin の点（腰）を運ばない（LockLimit）。
            // 運んでから Lock の点が動いたかで割合を絞ると、遠くへ引いたとき絞る細かさが足りず、運べる所でも運ばなかった（10-29 実測で、腰が 5.8cm 上がってから 0 に戻った）
            shift *= LockLimit (path, shift);
            // 背骨を伸ばした姿勢から解き始める（StraightenPath）。ソルバーは伸びきる手前を詰めるのが苦手で、反復を 4 倍にしても腰から首までが 98.6% で止まり、
            // 胸・上胸に 20〜25° ずつ曲がりが残った（10-30 ユーザー報告「首を持ち上げても体がまっすぐにならない」）
            StraightenPath (shift);
            // 道の外の Pin の点を運ぶ量は、腰を運んで道の外の Pin の点を外して解いた姿勢で決める（BranchShifts）
            SolveHeavyTargets (path, shift, 0, locks, false);
            BranchShifts (path);
            RestoreYielded ();
            float fraction = CarryPins (path, shift);
            if (locks) return;
            SolveHeavyTargets (path, shift * fraction, fraction, false);
            // 背骨を伸ばしきる手前は、反復で解くソルバーが詰めきれず、つかんだ点（首）が届かない（10-29 実測で 1cm）。届かなかった分を腰へ運び足す。
            // 10-29 までは、運ぶ量を伸ばしきった長さの 95% から数えて届かせていたが、元の背骨が丸い姿勢では 95% でも胸・上胸に 35° ずつ曲がりが残った
            // （10-30 ユーザー報告「首を持ち上げても体がまっすぐにならない」）。届く所では足す量は 0 なので、境目で姿勢は跳ねない
            for (int k = 0; k < kCarryRefines; k++) {
                Vector3 more = GrabbedShift ();
                if (more.sqrMagnitude <= kReachTolerance * kReachTolerance) break;
                shift = shift * fraction + more;
                fraction = 1;
                // 前の答えから続けて解く（元の姿勢から解き直すと、詰まった分がまた初めからになる）
                for (int i = 0; i < bodies_.Count; i++) {
                    startPositions_[i] = data_.positions[i];
                    startRotations_[i] = data_.rotations[i];
                }
                SolveHeavyTargets (path, shift, 1, false);
            }
        }

        /// <summary>HeavyEffectors の目標で、解き始める姿勢（startPositions_ / startRotations_）から解く</summary>
        void SolveHeavyTargets (bool[] path, Vector3 pathShift, float branchScale, bool yield, bool branches = true)
        {
            RunSolver (HeavyEffectors (path, pathShift, branchScale, yield, branches), startPositions_, startRotations_);
        }

        /// <summary>つかんだ点を譲らせて解いた姿勢（保持解き）を控える（Pin の点を運んで解き直したら Lock の点が動いたときに戻す）</summary>
        void KeepYielded ()
        {
            for (int i = 0; i < bodies_.Count; i++) {
                yieldedPositions_[i] = data_.positions[i];
                yieldedRotations_[i] = data_.rotations[i];
            }
        }

        /// <summary>控えた、つかんだ点を譲らせて解いた姿勢（保持解き）へ戻す</summary>
        void RestoreYielded ()
        {
            for (int i = 0; i < bodies_.Count; i++) {
                data_.positions[i] = yieldedPositions_[i];
                data_.rotations[i] = yieldedRotations_[i];
            }
        }

        /// <summary>
        /// 重い点をつかんで解いた答えでよいか: つかんだ点が届き、Lock の点も動いていない。つかんだ点が届いても、強さ 1 の Lock の点とぶつかると、
        /// 仕上げで骨をつなぎ直したときに Lock の点が動いた（10-28 実測で、Lock の足のキーで腰を前へ 20cm 動かすと 5.3cm）
        /// </summary>
        bool Solved ()
        {
            return Reached (grabbedEffectors_) && LocksHeld ();
        }

        /// <summary>Lock の点がどれも、元の姿勢でのずれ（lockBases_）から kReachTolerance より大きくは動いていないか</summary>
        bool LocksHeld ()
        {
            return Held (lockedEffectors_, lockBases_);
        }

        /// <summary>目標（effectors）がどれも、元の姿勢でのずれ（bases。同じ並び）から kReachTolerance より大きくは外れていないか</summary>
        bool Held (List<int> effectors, List<float> bases)
        {
            for (int k = 0; k < effectors.Count; k++) {
                if (data_.residuals[effectors[k]] > bases[k] + kReachTolerance) return false;
            }
            return true;
        }

        /// <summary>目標 e の、元の姿勢（解く前）での点と目標の離れ（m）</summary>
        float InputResidual (int e)
        {
            FullBodyIkEffector effector = data_.effectors[e];
            Vector3 point = solvePositions_[effector.bone] + solveRotations_[effector.bone] * (Vector3)effector.offset;
            return Vector3.Distance (effector.position, point);
        }

        /// <summary>解いた目標（data_.effectors の並び）が、どれも tolerance（m。既定 kReachTolerance）より近くへ届いたか</summary>
        bool Reached (List<int> effectors, float tolerance = kReachTolerance)
        {
            foreach (int e in effectors) {
                if (data_.residuals[e] > tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// 重い点をつかんで、届かないか Lock の点が動いたときに解き直す目標を、先頭から入れる（S88）。並びは 1 回目と同じで、grabbedEffectors_・lockedEffectors_ も作り直す。
        /// 重さ 0 の Pin の点は入れない（Free の点と同じ）。Lock でない重さ 1 の Pin の点（PullsPin）は、目標を、つかんだ点から根への道の上（path）の骨の点なら pathShift、
        /// 道の外なら branchShifts_（points_ と同じ並び）× branchScale（ルート空間）だけずらす。path が null なら道の外と同じに扱う。branches が false なら、道の外の Pin の点は入れない。
        /// yield なら、つかんだ点（足の箱を含む）を譲らせる（kLightPull）。入れた数を返す
        /// </summary>
        int HeavyEffectors (bool[] path, Vector3 pathShift, float branchScale, bool yield, bool branches = true)
        {
            int count = 0;
            grabbedEffectors_.Clear ();
            lockedEffectors_.Clear ();
            for (int i = 0; i < points_.Count; i++) {
                Point point = points_[i];
                float positionWeight, rotationWeight;
                PointRole role = HeavyRole (point, out positionWeight, out rotationWeight);
                if (role == PointRole.Follow) continue;
                if (role == PointRole.CarriedPin && !branches && !path[point.body]) continue;
                if (role == PointRole.Grabbed) grabbedEffectors_.Add (count);
                else if (role == PointRole.Lock) lockedEffectors_.Add (count);
                if (role == PointRole.Grabbed && yield) positionWeight = Mathf.Min (positionWeight, kLightPull);
                FullBodyIkEffector effector = PointEffector (point, positionWeight, rotationWeight);
                if (role == PointRole.CarriedPin) effector.position += (float3)(path != null && path[point.body] ? pathShift : branchShifts_[i] * branchScale);
                data_.effectors[count++] = effector;
            }
            int boxes = count;
            count = AddFootBoxes (count);
            for (int e = boxes; e < count; e++) {
                grabbedEffectors_.Add (e);
                if (!yield) continue;
                FullBodyIkEffector effector = data_.effectors[e];
                effector.positionWeight = kLightPull;
                data_.effectors[e] = effector;
            }
            return count;
        }

        /// <summary>重い点をつかんでいる間に、運ぶ Pin の点があるか</summary>
        bool HasCarriedPin ()
        {
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (HeavyRole (point, out positionWeight, out rotationWeight) == PointRole.CarriedPin) return true;
            }
            return false;
        }

        /// <summary>骨 body の上の、運ぶ強さ 1 の Pin の点（無ければ null）</summary>
        Point FullPinOn (int body, out float rotationWeight)
        {
            rotationWeight = 0;
            foreach (Point point in points_) {
                float positionWeight;
                if (point.body == body && HeavyRole (point, out positionWeight, out rotationWeight) == PointRole.CarriedPin && positionWeight >= 1) return point;
            }
            rotationWeight = 0;
            return null;
        }

        /// <summary>
        /// つかんだ点を譲らせて、Pin の点の目標を、道の上は pathShift、道の外は branchShifts_ だけ運んで解く（HeavyEffectors）。運べた割合を返し、その答えを data_ に残す。
        /// Lock の点が動いたら、Lock の点が動かない範囲で一番遠くまで運ぶ（運ぶ割合を半分ずつ kShiftSearches 回絞る。全部運ぶか運ばないかで切り替えると、
        /// Lock の足から届かなくなった所で姿勢が跳ねた。10-28 実測で頭が 1 回 9.6cm）。どこまでも運べなければ 0 で、呼ぶ前に KeepYielded で控えた答え
        /// </summary>
        float CarryPins (bool[] path, Vector3 pathShift)
        {
            SolveHeavyTargets (path, pathShift, 1, true);
            if (LocksHeld ()) {
                KeepYielded ();
                return 1;
            }
            float valid = 0, invalid = 1;
            for (int k = 0; k < kShiftSearches; k++) {
                float fraction = (valid + invalid) * 0.5f;
                SolveHeavyTargets (path, pathShift * fraction, fraction, true);
                if (LocksHeld ()) {
                    valid = fraction;
                    KeepYielded ();
                }
                else {
                    invalid = fraction;
                }
            }
            RestoreYielded ();
            return valid;
        }

        /// <summary>
        /// 今の姿勢（data_。道の外の Pin の点を外して解いた姿勢）で、道の外の Lock でない重さ 1 の Pin の点（両手・両足）を運ぶ量を branchShifts_ に入れる（points_ と同じ並び）。
        /// 手足の付け根（つかんだ点から根への道から分かれた最初の骨の付け根。肩・股）から目標までが、手足の長さ（付け根から点まで、骨を伸ばしきった長さ）を超えた分だけ、
        /// 付け根の方へ運ぶ。超えなければ 0（腕が伸びきるまでは手は動かない）
        /// </summary>
        void BranchShifts (bool[] path)
        {
            for (int i = 0; i < points_.Count; i++) {
                branchShifts_[i] = Vector3.zero;
                Point point = points_[i];
                float positionWeight, rotationWeight;
                if (HeavyRole (point, out positionWeight, out rotationWeight) != PointRole.CarriedPin || path[point.body]) continue;
                float length;
                int root = LimbRoot (point, path, out length);
                Vector3 target = TargetPosition (point);
                Vector3 toRoot = (Vector3)data_.positions[root] - target;
                float over = toRoot.magnitude - length;
                if (over > 0) branchShifts_[i] = toRoot.normalized * over;
            }
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
            // length は、つかんだ点から骨 b の付け根までの、骨を伸ばしきった長さ
            float length = ((Vector3)grabbed.offset).magnitude;
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
        /// 解き始める姿勢（startPositions_ / startRotations_）で、つかんだ点の骨から道の上の Pin の点（腰）までの骨を、運んだ Pin の点からつかんだ点の目標までの距離に合うように伸ばす。
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
            // 根の骨は、運んだ距離に応じて回す。曲がりと一緒に回すと、伸びきる手前の短い間に回りきって、足が 1 回に 8cm 振れた（10-30 実測）。
            // 回さないと、腰と背骨の間に 21〜28° の曲がりが残った
            Vector3 rootSegment = solvePositions_[pathChain_[0]] - pinPoint;
            Vector3 pinTarget = pinPoint + pathShift;
            Quaternion rootTurn = turnsRoot
                ? Quaternion.Slerp (Quaternion.identity, Quaternion.FromToRotation (rootSegment, (Vector3)grabbed.position - pinTarget), Mathf.Clamp01 (pathShift.magnitude / (kRootRelease * humanScale)))
                : Quaternion.identity;
            Vector3 start = pinTarget + rootTurn * rootSegment;
            // 骨ごとの、付け根から次の骨の付け根（最後はつかんだ点）までのベクトル
            int count = pathChain_.Count;
            Vector3 end = solvePositions_[grabbed.bone] + solveRotations_[grabbed.bone] * (Vector3)grabbed.offset;
            for (int k = 0; k < count; k++) {
                pathSegments_[k] = rootTurn * ((k + 1 < count ? solvePositions_[pathChain_[k + 1]] : end) - solvePositions_[pathChain_[k]]);
            }
            Vector3 toTarget = (Vector3)grabbed.position - start;
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
            // 骨の木を根からたどって置き直す（道の外の骨は親と一緒に動く）
            for (int b = 0; b < bodies_.Count; b++) {
                int parent = bodyParents_[b];
                if (parent >= 0 && !pathChain_.Contains (b)) pathTurns_[b] = pathTurns_[parent];
                if (b == pinBody) startPositions_[b] = pinTarget + rootTurn * (solvePositions_[b] - pinPoint);
                else if (parent >= 0) startPositions_[b] = startPositions_[parent] + pathTurns_[parent] * (solvePositions_[b] - solvePositions_[parent]);
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

        /// <summary>解いた姿勢で、つかんだ点が届かなかった分（目標 − 届いた所の平均。ルート空間）</summary>
        Vector3 GrabbedShift ()
        {
            Vector3 shift = Vector3.zero;
            foreach (int e in grabbedEffectors_) {
                FullBodyIkEffector effector = data_.effectors[e];
                Vector3 reached = (Vector3)data_.positions[effector.bone] + (Quaternion)data_.rotations[effector.bone] * (Vector3)effector.offset;
                shift += (Vector3)effector.position - reached;
            }
            return grabbedEffectors_.Count > 0 ? shift / grabbedEffectors_.Count : shift;
        }
    }

}
