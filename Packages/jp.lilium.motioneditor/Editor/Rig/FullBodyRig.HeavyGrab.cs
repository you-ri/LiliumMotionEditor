using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Lilium
{

    // 重い点をつかんでいる間の解き直し（S88 の決まり 9。届かない・Lock の点が動いたとき、届かなかった分だけ Pin の点の目標を運ぶ）。
    // 道の上の骨の扱いは FullBodyRig.Path.cs、つかんだ点と離れの変わらない点の扱いは FullBodyRig.Rigid.cs
    public sealed partial class FullBodyRig
    {
        /// <summary>重い点をつかんで動かしているとき、つかんだ点が届いたとみなす残り（m）。これより離れていれば、Pin の点の目標を届かなかった分だけ運んで解き直す（HeavyEffectors）</summary>
        const float kReachTolerance = 1e-3f;
        /// <summary>Pin の点の目標を運ぶと Lock の点が動くときに、運ぶ割合を半分ずつ絞って探す回数（1/2^回数 の細かさ）</summary>
        const int kShiftSearches = 4;
        /// <summary>LeastBranchScale で、道の外の Pin の点を運ぶ割合を半分ずつ絞って探す回数（1/2^回数 の細かさ）</summary>
        const int kBranchSearches = 6;
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
        // BranchShifts で決めた、道の外の Pin の点を運ぶ量（points_ と同じ並び。ルート空間）
        Vector3[] branchShifts_;
        // 重い点をつかんでいる間の、Lock の点の目標の並び（Lock の点が動いたかを見る）と、つかんだ点を譲らせて解いた姿勢（保持解き）
        readonly List<int> lockedEffectors_ = new List<int> ();
        // 重い点をつかんでいる間の、つかんだ点と離れの変わらない Pin の点の目標の並びと、元の姿勢でのずれ（RigidToGrabbed。Solved で留まったかを見る）
        readonly List<int> rigidEffectors_ = new List<int> ();
        readonly List<float> rigidBases_ = new List<float> ();
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
        //    つかんだ点と離れの変わらない Pin の点は、つかんだ点からの離れで運ぶ（RigidShift）。
        // 4. Lock の点が動いたら、運ぶ割合を絞る（CarryPins）。Lock の点が無ければ、つかんだ点を強さ 1 にして、届く一番少ない量だけ道の外の Pin の点を運んで解く（LeastBranchScale）。
        //    それでもつかんだ点が届かなければ、届かなかった分を道の上の Pin の点（腰）へ運び足して解き直す（kCarryRefines 回まで）。
        // どの量も前の答えから連続に決まり、届く所では 0 なので、境目で姿勢が跳ねず、元の姿勢（キーの形）へ戻すこともない
        // （10-28 ユーザー指示「ひっぱる距離に応じて、強制的に姿勢をもとに戻す制御をしているが、そういったことはしないようにして」）
        void ResolveHeavyGrab ()
        {
            if (data_.rootPin <= 0) {
                ResolveUnpinnedHeavyGrab ();
                return;
            }
            bool[] path = PathToRoot ();
            StartFrom (solvePositions_, solveRotations_);
            SolveHeavyTargets (path, Vector3.zero, 0, true);
            Vector3 shift = GrabbedShift ();
            if (IsReached (shift)) return;
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

        /// <summary>
        /// 根を留めない定義（rootPin が 0。S98）の解き直し。骨の木のどちら向きにも同じ扱いにする（根の側の骨をつかんでも、先の側の骨をつかんでも同じ）。
        /// 1. Pin の点・Lock の点を留めて、つかんだ点を譲らせずに解く。届かなければ、ソルバーが留めた骨からつかんだ点まで一直線に並べる（StraightenUnreachable）。
        /// 2. Lock の点があれば、ここまで（Lock の点は動かない）。無ければ、届かなかった分だけ Lock でない Pin の点を全部運び、
        ///    1 の姿勢を同じだけずらした所から解き直す（一直線のまま付いてくる）。
        /// 腰を留める定義の道（根への向き）で運ぶ量を決める作りでは、先の骨をつかむと Pin の根が一直線のまま付いてきたのに、
        /// 根をつかむと先の Pin の点が道の外の扱いになり、間の関節が 20° ほど曲がった（11-03 ユーザー指示「point end と point root どちらを引っ張っても、間が一直線にならない」）。
        /// ずらした姿勢から解くのは、伸びきった形（反復で解くソルバーが詰めきれない）を探させないため。
        /// つかんだ点と離れの変わらない Pin の点は、初めから、つかんだ点から決まった離れの所へ最小だけ運ぶ（RigidShift。根を留める定義と同じ）
        /// </summary>
        void ResolveUnpinnedHeavyGrab ()
        {
            StartFrom (solvePositions_, solveRotations_);
            for (int i = 0; i < points_.Count; i++) branchShifts_[i] = RigidShift (points_[i]);
            SolveHeavyTargets (null, Vector3.zero, 1, false);
            Vector3 shift = GrabbedShift ();
            if (lockedEffectors_.Count > 0) return;
            if (!HeldLoopsClosed ()) {
                CarryLoopPins ();
                return;
            }
            if (IsReached (shift)) return;
            // Pin の点ごとに、届かなかった分を運ぶ（間の関節を角度制限で縮めきれないときは、目標から離れる向きへ必要な分だけ。SpanShift）。
            // 解き始める姿勢は、運んだ量の平均だけずらす
            Vector3 start = Vector3.zero;
            int carried = 0;
            for (int i = 0; i < points_.Count; i++) {
                if (HeavyRole (points_[i], out _, out _) != PointRole.CarriedPin || CarriedRigid (points_[i])) continue;
                branchShifts_[i] = SpanShift (points_[i], TargetPosition (points_[i]), shift);
                carried++;
            }
            ShareLoopShifts ();
            for (int i = 0; i < points_.Count; i++) {
                if (HeavyRole (points_[i], out _, out _) == PointRole.CarriedPin && !CarriedRigid (points_[i])) start += branchShifts_[i];
            }
            StartFromSolved (carried > 0 ? start / carried : shift);
            SolveHeavyTargets (null, Vector3.zero, 1, false);
        }

        /// <summary>
        /// 根を留めない定義で、Pin の点を留めて解くと輪の関節（S105）が開いたとき（つかんだ点が、輪を開いて届いた）。運ぶ量は、根を留める定義の道の外の Pin の点と同じく長さで決める:
        /// 道の外の Pin の点を外して解いた姿勢で、手足の付け根から目標までが手足の長さを超えた分（BranchShifts）。輪でつながった Pin の点どうしはそろえ（ShareLoopShifts）、
        /// 届かない分は運び足す（kCarryRefines）。伸びきるまでは 0 なので、境目で跳ばない。
        /// つかんだ点を譲らせて届かなかった分で測ると、譲らせた点では鎖を伸ばしきれず、伸びきる前から Pin の点を運んだ。Pin の点を譲らせて引かれた量で測ると、
        /// 伸びきった所で、輪を体のまわりに傾けた方が引かれる量の少ない答えへ移って跳んだ（11-09 の輪のテストで、両手を Pin にして体を引き上げると肩が 1 回 350mm 前後）
        /// </summary>
        void CarryLoopPins ()
        {
            bool[] path = PathToRoot ();
            StartFrom (solvePositions_, solveRotations_);
            SolveHeavyTargets (path, Vector3.zero, 0, false, branches: false);
            BranchShifts (path);
            ShareLoopShifts ();
            for (int k = 0; k <= kCarryRefines; k++) {
                // 解き始める姿勢は、元の姿勢を運ぶ量の平均だけずらした所
                Vector3 start = Vector3.zero;
                int carried = 0;
                for (int i = 0; i < points_.Count; i++) {
                    if (!SharesShift (points_[i])) continue;
                    start += branchShifts_[i];
                    carried++;
                }
                StartFrom (solvePositions_, solveRotations_);
                if (carried > 0) {
                    for (int i = 0; i < bodies_.Count; i++) startPositions_[i] += start / carried;
                }
                SolveHeavyTargets (null, Vector3.zero, 1, false);
                if (k == kCarryRefines || (IsReached (GrabbedShift ()) && HeldLoopsClosed ())) break;
                Vector3 more = LoopShortfall (null, Vector3.zero);
                for (int i = 0; i < points_.Count; i++) {
                    if (SharesShift (points_[i])) branchShifts_[i] += more;
                }
            }
        }

        /// <summary>
        /// 今の目標で解いた答え（data_）でつかんだ点が届かなかった分。輪の関節（S105）が開いていれば、つかんだ点を譲らせて同じ目標で解き直し、その届かなかった分にする。
        /// 輪が開くと、つかんだ点は届いてしまうので、運び足す量が 0 のまま輪が 2mm ほど開いて残った（11-09 の輪のテストで、両手を Pin にして体を引き上げたとき）
        /// </summary>
        Vector3 LoopShortfall (bool[] path, Vector3 pathShift)
        {
            if (HeldLoopsClosed ()) return GrabbedShift ();
            SolveHeavyTargets (path, pathShift, 1, true);
            return GrabbedShift ();
        }

        /// <summary>
        /// 伸ばしきる所の手前で Pin の点が付いてき始める幅（Pin の点からつかんだ点・手足の付け根までの、骨を伸ばしきった長さに対する割合。SoftenStretch）
        /// </summary>
        internal const float kSoftStretch = 0.03f;
        /// <summary>SoftenStretch で、運び足して解き直す回数の上限（Pin の点を運ぶと手足の付け根も動くので、測り直す）</summary>
        const int kSoftPasses = 3;
        // 重い点をつかんでいる間の、目標（data_.effectors の並び）ごとの点（points_ の並び。足の箱は -1。HeavyEffectors が入れる）
        readonly List<int> effectorPoints_ = new List<int> ();
        // SoftenStretch で、目標ごとに運び足す量（data_.effectors と同じ並び。ルート空間）
        Vector3[] softShifts_ = new Vector3[0];
        Vector3[] softShared_ = new Vector3[0];

        /// <summary>
        /// 伸ばしきった長さ length の鎖で、distance 離れた 2 点を結ぶときに、鎖が実際に伸びる長さ。付いてき始める所 start（length の kSoftStretch 手前と、
        /// 元の姿勢での離れ rest の遠い方）までは distance のまま、そこから幅の 2 倍先（length + 幅）でちょうど length になる 2 次の曲線で伸び、その先は length。
        /// distance との差だけ Pin の点が付いてくる。残り（length − 伸びる長さ）が (1 − x)² で減るので、関節の一直線からのずれ（√(残り × 骨の長さ)）は
        /// 一定の速さで詰まり、有限の所で一直線になる（11-10 の Fable のレビュー。指数で近づけると、いつまでも伸びきらず、脚が曲がったまま付いてきた）。
        /// 元の姿勢から数えるのは、キーの姿勢ですでに伸びきりに近い手足（T ポーズの脚）で、何も引いていないのに Pin の点が付いてこないため
        /// （11-10 実測で、全部の点を回すと腰が 4.8mm ずれた）。元から伸びきっていれば、関節は初めから一直線の上にあり、跳ばない
        /// </summary>
        static float SoftReach (float distance, float length, float rest)
        {
            float start = Mathf.Max (length * (1 - kSoftStretch), rest);
            float soft = length - start;
            if (distance <= start || soft <= kEpsilon) return Mathf.Min (distance, length);
            float x = (distance - start) / (2 * soft);
            if (x >= 1) return length;
            return length - soft * (1 - x) * (1 - x);
        }

        /// <summary>
        /// 重い点をつかんで解いた後、伸ばしきる手前の鎖の Pin の点を、伸びきるまでの残りに応じて少しずつ付いてこさせる（11-10 ユーザー決定「伸びきる手前から付いてくる」）。
        /// 伸びきるまで Pin の点を留めると、伸びきる直前に鎖の途中の関節（膝・肘）が、つかんだ点の動きよりずっと速く一直線へ詰まった
        /// （伸びきるまでの残りが δ のとき、関節は鎖の両端を結ぶ線から √(δ × 骨の長さ) くらい離れている。11-10 ユーザー報告「腰を持ち上げると、
        /// 一定以上上に動かすと、足が変に動く」。デモのキャラで、腰を 10mm 上げる 1 回で膝が 56mm）。
        /// 鎖が伸びる長さを SoftReach にして、両端の離れとの差を Pin の点の目標へ運び足す（今までの運ぶ量より少なければ何もしない）。
        /// 道の外の Pin の点（手足の先）は、手足の付け根から目標まで（BranchShifts と同じ数え方）、道の上の Pin の点（腰）は、一番近い点からつかんだ点の目標まで
        /// （PathStretch と同じ数え方）で、道の上の Pin の点全部に同じだけ足す（HeavyEffectors と同じ）。輪でつながった Pin の点どうしは運び足す量をそろえる（ShareLoopShifts と同じ）。
        /// 運び足して解くとつかんだ点が届かなくなるときは、届く一番多い割合まで絞る。Lock の点があるときは何もしない（Lock の点を守る絞り込みの答えを崩さない）
        /// </summary>
        void SoftenStretch ()
        {
            if (lockedEffectors_.Count > 0 || effectorPoints_.Count != data_.effectorCount) return;
            bool[] path = PathToRoot ();
            int count = data_.effectorCount;
            if (softShifts_.Length < count) softShifts_ = new Vector3[count];
            PathPinStretch (out Point pathPin, out Vector3 toGrabbed, out float reach, out float pathRest);
            for (int pass = 0; pass < kSoftPasses; pass++) {
                if (!SoftShifts (path, pathPin, toGrabbed, reach, pathRest, count)) break;
                bool reachedBefore = IsReached (GrabbedShift ()) && HeldLoopsClosed ();
                KeepSoftened ();
                if (SolveSoftened (count, 1) || !reachedBefore) continue;
                // 一直線に近い鎖を縮めるのは、反復で解くソルバーが苦手で、つかんだ点が届かなくなることがある（背骨のように骨の多い道）。
                // そのときは、届く一番多い割合まで運び足す量を絞る（全部か 0 かで切り替えると、境目で Pin の点が跳ぶ。11-10 の Fable のレビュー）
                float low = 0, high = 1;
                for (int k = 0; k < kSoftSearches; k++) {
                    float middle = (low + high) * 0.5f;
                    if (SolveSoftened (count, middle)) low = middle;
                    else high = middle;
                }
                if (low > 0) SolveSoftened (count, low);
                else RestoreSoftened ();
                break;
            }
        }

        /// <summary>SoftenStretch で、運び足す量を半分ずつ絞って探す回数（1/2^回数 の細かさ）</summary>
        const int kSoftSearches = 4;

        /// <summary>
        /// 今の答えで、目標ごとに運び足す量を softShifts_ に入れる（SoftenStretch）。運び足す目標があれば true
        /// </summary>
        bool SoftShifts (bool[] path, Point pathPin, Vector3 toGrabbed, float reach, float pathRest, int count)
        {
            // 道の上の Pin の点に足す量は、一番近い点（pathPin）で決める
            Vector3 pathMore = Vector3.zero;
            for (int e = 0; e < count; e++) {
                if (pathPin != null && effectorPoints_[e] >= 0 && points_[effectorPoints_[e]] == pathPin) pathMore = SoftMore (e, toGrabbed, reach, pathRest);
            }
            for (int e = 0; e < count; e++) {
                softShifts_[e] = Vector3.zero;
                int i = effectorPoints_[e];
                if (i < 0) continue;
                Point point = points_[i];
                if (HeavyRole (point, out _, out _) != PointRole.CarriedPin || CarriedRigid (point)) continue;
                if (path[point.body]) softShifts_[e] = pathMore;
                else {
                    int root = LimbRoot (point, path, out float length);
                    Vector3 pointAtRest = solvePositions_[point.body] + solveRotations_[point.body] * point.offset;
                    softShifts_[e] = SoftMore (e, (Vector3)data_.positions[root] - TargetPosition (point), length, Vector3.Distance (solvePositions_[root], pointAtRest));
                }
            }
            // 輪でつながった Pin の点どうしは、運び足す量の平均にそろえる（点ごとに運ぶと、必ず満たす輪の関節と取り合って輪が開く）
            if (loopJoints_.Count > 0) {
                if (softShared_.Length < count) softShared_ = new Vector3[count];
                for (int e = 0; e < count; e++) {
                    softShared_[e] = softShifts_[e];
                    int i = effectorPoints_[e];
                    if (i < 0 || !SharesShift (points_[i])) continue;
                    Vector3 sum = softShifts_[e];
                    int pairs = 1;
                    for (int f = 0; f < count; f++) {
                        int j = effectorPoints_[f];
                        if (f == e || j < 0 || !SharesShift (points_[j]) || !LoopPair (points_[i], points_[j])) continue;
                        sum += softShifts_[f];
                        pairs++;
                    }
                    softShared_[e] = sum / pairs;
                }
                for (int e = 0; e < count; e++) softShifts_[e] = softShared_[e];
            }
            for (int e = 0; e < count; e++) {
                if (softShifts_[e].sqrMagnitude > kEpsilon * kEpsilon) return true;
            }
            return false;
        }

        /// <summary>
        /// 控えた答え（KeepSoftened）の目標へ softShifts_ × scale を運び足し、控えた答えから続けて解く。つかんだ点が届き、輪も閉じたら true
        /// </summary>
        bool SolveSoftened (int count, float scale)
        {
            RestoreSoftened ();
            for (int e = 0; e < count; e++) {
                if (softShifts_[e].sqrMagnitude <= kEpsilon * kEpsilon) continue;
                FullBodyIkEffector effector = data_.effectors[e];
                effector.position += (float3)(softShifts_[e] * scale);
                data_.effectors[e] = effector;
            }
            StartFromSolved (Vector3.zero);
            RunSolver (count, startPositions_, startRotations_);
            return IsReached (GrabbedShift ()) && HeldLoopsClosed ();
        }

        // SoftenStretch で、運び足す前の答え（data_ の骨の姿勢と目標）
        Vector3[] softPositions_ = new Vector3[0];
        Quaternion[] softRotations_ = new Quaternion[0];
        FullBodyIkEffector[] softEffectors_ = new FullBodyIkEffector[0];
        float[] softResiduals_ = new float[0];
        float2[] softLoopResiduals_ = new float2[0];

        /// <summary>今の答え（骨の姿勢・目標・残り）を控える（SoftenStretch）</summary>
        void KeepSoftened ()
        {
            if (softPositions_.Length < bodies_.Count) {
                softPositions_ = new Vector3[bodies_.Count];
                softRotations_ = new Quaternion[bodies_.Count];
            }
            if (softEffectors_.Length < data_.effectorCount) {
                softEffectors_ = new FullBodyIkEffector[data_.effectorCount];
                softResiduals_ = new float[data_.effectorCount];
            }
            if (softLoopResiduals_.Length < data_.loopCount) softLoopResiduals_ = new float2[data_.loopCount];
            for (int i = 0; i < bodies_.Count; i++) {
                softPositions_[i] = data_.positions[i];
                softRotations_[i] = data_.rotations[i];
            }
            for (int e = 0; e < data_.effectorCount; e++) {
                softEffectors_[e] = data_.effectors[e];
                softResiduals_[e] = data_.residuals[e];
            }
            for (int l = 0; l < data_.loopCount; l++) softLoopResiduals_[l] = data_.loopResiduals[l];
        }

        /// <summary>控えた答えへ戻す</summary>
        void RestoreSoftened ()
        {
            for (int i = 0; i < bodies_.Count; i++) {
                data_.positions[i] = softPositions_[i];
                data_.rotations[i] = softRotations_[i];
            }
            for (int e = 0; e < data_.effectorCount; e++) {
                data_.effectors[e] = softEffectors_[e];
                data_.residuals[e] = softResiduals_[e];
            }
            for (int l = 0; l < data_.loopCount; l++) data_.loopResiduals[l] = softLoopResiduals_[l];
        }

        /// <summary>
        /// 目標 e（運ぶ Pin の点）を、伸ばしきった長さ length の鎖が SoftReach で伸びる所まで運ぶのに、今の目標では足りない量（足りていれば 0）。
        /// span は、点の元の目標から鎖のもう片方の端（手足の付け根・つかんだ点の目標）へのベクトルで、運ぶ向きもその向き
        /// </summary>
        Vector3 SoftMore (int e, Vector3 span, float length, float rest)
        {
            float distance = span.magnitude;
            if (distance <= kEpsilon) return Vector3.zero;
            float need = distance - SoftReach (distance, length, rest);
            if (need <= kEpsilon) return Vector3.zero;
            Vector3 direction = span / distance;
            float along = Vector3.Dot ((Vector3)data_.effectors[e].position - TargetPosition (points_[effectorPoints_[e]]), direction);
            return need > along + kEpsilon ? direction * (need - along) : Vector3.zero;
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
            SolveHeavyTargets (path, shift, 0, locks, branches: false);
            BranchShifts (path);
            ShareLoopShifts ();
            RestoreYielded ();
            float fraction = CarryPins (path, shift);
            if (locks) return;
            shift *= fraction;
            SolveHeavyTargets (path, shift, fraction, false, LeastBranchScale (path, shift, fraction));
            // 背骨を伸ばしきる手前は、反復で解くソルバーが詰めきれず、つかんだ点（首）が届かない（10-29 実測で 1cm）。届かなかった分を腰へ運び足す。
            // 10-29 までは、運ぶ量を伸ばしきった長さの 95% から数えて届かせていたが、元の背骨が丸い姿勢では 95% でも胸・上胸に 35° ずつ曲がりが残った
            // （10-30 ユーザー報告「首を持ち上げても体がまっすぐにならない」）。届く所では足す量は 0 なので、境目で姿勢は跳ねない
            for (int k = 0; k < kCarryRefines; k++) {
                if (IsReached (GrabbedShift ()) && HeldLoopsClosed ()) break;
                Vector3 more = LoopShortfall (path, shift);
                shift += PathRefine (shift, more);
                // 引かれている道の外の Pin の点（手足の先）にも、届かなかった分を運び足す。手足の付け根から目標までの長さで決めた量（BranchShifts）は、
                // 関節の角度制限を数えないので、制限で手足を目標の方へ向けきれないと足りず、体ごと運べなかった
                // （11-08 の枝分かれのテストの狭い制限で、もう片方の枝の Pin の点が付いてこず、つかんだ点が 26mm 届かなかった）
                for (int i = 0; i < points_.Count; i++) {
                    if (branchShifts_[i].sqrMagnitude > kEpsilon * kEpsilon && !CarriedRigid (points_[i])) branchShifts_[i] += more;
                }
                // 前の答えから続けて解く（元の姿勢から解き直すと、詰まった分がまた初めからになる）
                StartFromSolved (Vector3.zero);
                SolveHeavyTargets (path, shift, 1, false);
            }
        }

        /// <summary>
        /// Pin の点の目標を、道の上は pathShift、道の外は branchShifts_ だけ運んで、つかんだ点を譲らせて解く（HeavyEffectors）。運べた割合を返し、その答えを data_ に残す。
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
        /// 道の外の Pin の点（手足の先）を BranchShifts の何割だけ運べば、つかんだ点が届くか（0〜fraction）。届く一番小さい割合を、半分ずつ絞って探す。
        /// BranchShifts は、道の外の Pin の点を外して解いた姿勢で手足の付け根の場所を見るので、多すぎることがある: Pin の点を効かせている間は、
        /// 体が Pin の点のまわりに回って留まっていたのに、外すと体がキーの形へ戻って起き上がり、付け根が Pin の点から遠くなる。
        /// そのまま運ぶと、Pin の点が引かれ始める 1 回で、マウスの動きの 4 倍跳んだ（11-08 の枝分かれのテストで、マウス 4cm に Pin の点が 15cm）。
        /// 届く一番小さい割合なら、引かれ始めは 0 から増えるので跳ばない（Pin の決まり「留まれる分は留まり、届かない分だけ引かれる」）。
        /// fraction でも届かなければ fraction（届かなかった分は、呼ぶ側が道の上の Pin の点へ運び足す）。
        /// 絞るのは手足の先を運ぶ量だけで、つかんだ点と離れの変わらない Pin の点（RigidShift）は fraction のまま運ぶ
        /// </summary>
        float LeastBranchScale (bool[] path, Vector3 pathShift, float fraction)
        {
            if (fraction <= 0 || !AnyBranchShift ()) return fraction;
            float low = 0, high = fraction;
            // 輪の関節（S105）を開いて届いた答えは数えない（開けば手足の先を運ばずに届くので、運ぶ割合が 0 になった。11-09 の輪のテストで 85mm 開いた）
            SolveHeavyTargets (path, pathShift, fraction, false, 0);
            if (IsReached (GrabbedShift ()) && HeldLoopsClosed ()) return 0;
            for (int k = 0; k < kBranchSearches; k++) {
                float middle = (low + high) * 0.5f;
                SolveHeavyTargets (path, pathShift, fraction, false, middle);
                if (IsReached (GrabbedShift ()) && HeldLoopsClosed ()) high = middle;
                else low = middle;
            }
            return high;
        }

        /// <summary>BranchShifts で、運ぶ量が 0 でない手足の先の Pin の点（つかんだ点と離れの変わらない点を除く）があるか</summary>
        bool AnyBranchShift ()
        {
            for (int i = 0; i < points_.Count; i++) {
                if (branchShifts_[i].sqrMagnitude > kEpsilon * kEpsilon && !CarriedRigid (points_[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// 今の姿勢（data_。道の外の Pin の点を外して解いた姿勢）で、道の外の Lock でない重さ 1 の Pin の点（両手・両足）を運ぶ量を branchShifts_ に入れる（points_ と同じ並び）。
        /// 手足の付け根（つかんだ点から根への道から分かれた最初の骨の付け根。肩・股）から目標までが、手足の長さ（付け根から点まで、骨を伸ばしきった長さ）を超えた分だけ、
        /// 付け根の方へ運ぶ。超えなければ 0（腕が伸びきるまでは手は動かない）。つかんだ点と離れの変わらない Pin の点は、道の上でも RigidShift の量
        /// </summary>
        void BranchShifts (bool[] path)
        {
            for (int i = 0; i < points_.Count; i++) {
                branchShifts_[i] = Vector3.zero;
                Point point = points_[i];
                if (HeavyRole (point, out _, out _) != PointRole.CarriedPin) continue;
                if (CarriedRigid (point)) {
                    branchShifts_[i] = RigidShift (point);
                    continue;
                }
                if (path[point.body]) continue;
                float length;
                int root = LimbRoot (point, path, out length);
                Vector3 target = TargetPosition (point);
                Vector3 toRoot = (Vector3)data_.positions[root] - target;
                float over = toRoot.magnitude - length;
                if (over > 0) branchShifts_[i] = toRoot.normalized * over;
            }
        }

        /// <summary>
        /// 輪の関節（S105）でつながって離れの変わらない、運ぶ Pin の点どうし（つかんだ点とは離れの変わる点）は、同じ量だけ運ぶ（運ぶ量の平均。branchShifts_）。
        /// 点ごとに手足の付け根の方へ運ぶと、運んだ目標どうしの離れが変わり、必ず満たす輪の関節と取り合って輪が開いた
        /// （11-09 の輪のテストで、両手を Pin にして体を引き上げると、腕が伸びきった所で 85mm）。届かない分は運び足し（kCarryRefines）でそろえて足す
        /// </summary>
        void ShareLoopShifts ()
        {
            if (loopJoints_.Count == 0) return;
            if (sharedShifts_.Length != points_.Count) sharedShifts_ = new Vector3[points_.Count];
            for (int i = 0; i < points_.Count; i++) {
                sharedShifts_[i] = branchShifts_[i];
                if (!SharesShift (points_[i])) continue;
                Vector3 sum = branchShifts_[i];
                int count = 1;
                for (int j = 0; j < points_.Count; j++) {
                    if (j == i || !SharesShift (points_[j]) || !LoopPair (points_[i], points_[j])) continue;
                    sum += branchShifts_[j];
                    count++;
                }
                sharedShifts_[i] = sum / count;
            }
            for (int i = 0; i < points_.Count; i++) branchShifts_[i] = sharedShifts_[i];
        }

        Vector3[] sharedShifts_ = new Vector3[0];

        /// <summary>運ぶ量をそろえる Pin の点か（運ぶ Pin の点で、つかんだ点とは離れの変わる点）</summary>
        bool SharesShift (Point point)
        {
            return HeavyRole (point, out _, out _) == PointRole.CarriedPin && !CarriedRigid (point);
        }

        /// <summary>HeavyEffectors の目標で、解き始める姿勢（startPositions_ / startRotations_）から解く</summary>
        void SolveHeavyTargets (bool[] path, Vector3 pathShift, float fraction, bool yield, float branchScale = -1, bool branches = true)
        {
            RunSolver (HeavyEffectors (path, pathShift, fraction, yield, branchScale, branches), startPositions_, startRotations_);
        }

        /// <summary>
        /// 重い点をつかんで、届かないか Lock の点が動いたときに解き直す目標を、先頭から入れる（S88）。並びは 1 回目と同じで、grabbedEffectors_・lockedEffectors_ も作り直す。
        /// 重さ 0 の Pin の点は入れない（Free の点と同じ）。Lock でない重さ 1 の Pin の点（PullsPin）は、目標を運ぶ（ルート空間）:
        /// つかんだ点から根への道の上（path）の骨の点は pathShift、道の外の点は branchShifts_（points_ と同じ並び）× branchScale（負なら fraction）、
        /// つかんだ点と離れの変わらない点（RigidShift）は branchShifts_ × fraction。path が null なら道の外と同じに扱う。branches が false なら、道の上の点だけ入れる。
        /// yield なら、つかんだ点（足の箱を含む）を譲らせる（kLightPull）。入れた数を返す
        /// </summary>
        int HeavyEffectors (bool[] path, Vector3 pathShift, float fraction, bool yield, float branchScale = -1, bool branches = true)
        {
            if (branchScale < 0) branchScale = fraction;
            int count = 0;
            grabbedEffectors_.Clear ();
            lockedEffectors_.Clear ();
            rigidEffectors_.Clear ();
            rigidBases_.Clear ();
            effectorPoints_.Clear ();
            for (int i = 0; i < points_.Count; i++) {
                Point point = points_[i];
                float positionWeight, rotationWeight;
                PointRole role = HeavyRole (point, out positionWeight, out rotationWeight);
                if (role == PointRole.Follow) continue;
                bool rigid = role == PointRole.CarriedPin && RigidToGrabbed (point, out _, out _);
                bool onPath = path != null && path[point.body] && !rigid;
                if (role == PointRole.CarriedPin && !branches && !onPath) continue;
                if (role == PointRole.Grabbed) grabbedEffectors_.Add (count);
                else if (role == PointRole.Lock) lockedEffectors_.Add (count);
                if (role == PointRole.Grabbed && yield) positionWeight = Mathf.Min (positionWeight, kLightPull);
                // 両手で持った物（輪の関節の物の側。S102）をつかんで輪が閉じなかったときは、つかんだ点を輪より少し弱くする（SolveBodies）。
                // 手が物から外れず、つかんだ点が届かない分を残す
                if (role == PointRole.Grabbed && yieldToLoops_ && OnHeldLoop (point.body)) {
                    positionWeight = Mathf.Min (positionWeight, kLightPull);
                    rotationWeight = Mathf.Min (rotationWeight, kLightPull);
                }
                FullBodyIkEffector effector = PointEffector (point, positionWeight, rotationWeight);
                if (role == PointRole.Grabbed) effector.position = (float3)ClampToLock (point, effector.position);
                if (role == PointRole.CarriedPin) effector.position += (float3)(onPath ? pathShift : branchShifts_[i] * (rigid ? fraction : branchScale));
                data_.effectors[count] = effector;
                effectorPoints_.Add (i);
                if (rigid) {
                    rigidEffectors_.Add (count);
                    rigidBases_.Add (InputResidual (count));
                }
                count++;
            }
            int boxes = count;
            count = AddFootBoxes (count);
            for (int e = boxes; e < count; e++) {
                effectorPoints_.Add (-1);
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
                if (HeavyRole (point, out _, out _) == PointRole.CarriedPin) return true;
            }
            return false;
        }

        /// <summary>
        /// 重い点をつかんで解いた答えでよいか: つかんだ点が届き、Lock の点も動いていない。つかんだ点が届いても、強さ 1 の Lock の点とぶつかると、
        /// 仕上げで骨をつなぎ直したときに Lock の点が動いた（10-28 実測で、Lock の足のキーで腰を前へ 20cm 動かすと 5.3cm）
        /// </summary>
        bool Solved ()
        {
            // つかんだ点と離れの変わらない Pin の点（RigidToGrabbed）も、留まっていなければ解き直す。つかんだ点が届いても、その点を外して届いた答えは、
            // 届かなかったときに運ぶ量（BranchShifts）と食い違い、引くたびに 2 つの姿勢の間を跳んだ。
            // 輪の関節（S105）が開いた答えも解き直す。Pin の点と必ず満たす輪の関節が取り合うと輪の方が開き、つかんだ点が届いて Pin の点が留まったまま
            // 運ぶ処理へ進まなかった（11-09 の輪のテストで、両手を Pin にして体を引き上げると 85mm 開いた）
            return Reached (grabbedEffectors_) && LocksHeld () && Held (rigidEffectors_, rigidBases_) && HeldLoopsClosed ();
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

        /// <summary>つかんだ点が届かなかった分（GrabbedShift）が、届いたとみなせるほど小さいか</summary>
        static bool IsReached (Vector3 shift)
        {
            return shift.sqrMagnitude <= kReachTolerance * kReachTolerance;
        }

        /// <summary>解き始める姿勢を、positions / rotations（ルート空間）にする</summary>
        void StartFrom (Vector3[] positions, Quaternion[] rotations)
        {
            System.Array.Copy (positions, startPositions_, bodies_.Count);
            System.Array.Copy (rotations, startRotations_, bodies_.Count);
        }

        /// <summary>解き始める姿勢を、今の答え（data_）を shift だけずらした姿勢にする</summary>
        void StartFromSolved (Vector3 shift)
        {
            for (int i = 0; i < bodies_.Count; i++) {
                startPositions_[i] = (Vector3)data_.positions[i] + shift;
                startRotations_[i] = data_.rotations[i];
            }
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
    }

}
