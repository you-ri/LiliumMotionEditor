using UnityEngine;

namespace Lilium
{

    // 解くときの点の扱い。S88 の決まりの表（つかんだ点の重さ × 隣の点の状態と重さ）をここで決め、解く手順はこの答えに従う
    public sealed partial class FullBodyRig
    {
        /// <summary>解くときの点の扱い（HeavyRole / LightRole が決める）</summary>
        enum PointRole
        {
            /// <summary>目標を持たず、骨に付いて動く</summary>
            Follow,
            /// <summary>自分の目標で解く</summary>
            Target,
            /// <summary>つかんだ点。自分の目標で解き、ほかの点とぶつかったら譲ることがある</summary>
            Grabbed,
            /// <summary>重い点をつかんでいる間の Lock の点。動いたら解き直す</summary>
            Lock,
            /// <summary>重い点をつかんでいる間の Pin の点。届かなければ目標を運ぶ</summary>
            CarriedPin,
            /// <summary>軽い点をつかんでいる間の動かない点。1 回目の姿勢の場所へ留める</summary>
            Held,
        }

        /// <summary>設定の pull（点の固定の強さに掛ける値）</summary>
        float pull
        {
            get { return Mathf.Clamp01 (settings_.pull); }
        }

        /// <summary>
        /// 重い点をつかんでいるときの点の扱い。
        /// 重さ 0 の Pin の点は、Free の点と同じに引かれる（目標に使わない。10-28 ユーザー指示「自分（つかんだ点）が重さ 1 のとき
        /// その先の点の重さが0ならpinでも freeと同じように引かれるようにして」）。重さ 1 の Pin の点は強さ 1 のまま効かせ、届かなければ目標を運ぶ
        /// （重さ 0 の Pin の点（腹・胸など）とぶつかって答えが跳ねていたのは、これで起きない。10-28 実測で、強さを 0.99 に抑えて
        /// 仕上げでも効かせる形は、腕のように鎖が何本もつながる所で詰め切れず、腰を 20cm 動かすと Pin の手が 9cm 流れた）
        /// </summary>
        PointRole HeavyRole (Point point, out float positionWeight, out float rotationWeight)
        {
            if (!GetWeights (point, out positionWeight, out rotationWeight)) return PointRole.Follow;
            if (grabbed_.Contains (point)) return PointRole.Grabbed;
            if (PullsPin (point)) return point.weightless ? PointRole.Follow : PointRole.CarriedPin;
            return point.values.locked > 0.5f && positionWeight >= 1 ? PointRole.Lock : PointRole.Target;
        }

        /// <summary>
        /// 軽い点をつかんでいるとき（2 回目）の点の扱い。grabbedBodies_ を入れてから呼ぶ。
        /// 動かない点は、Free の重い点（重さ 1）と、つかんでいない Pin の重い点・Lock の点（位置の強さ 1。Lock は重さに関係なく）と、動かせない点（passive。S89）。
        /// 強さが 1 より小さい Pin の点と、向きを混ぜる点（膝の Pole）と、つかんだ点と同じ骨の点は、動かない点にしない（自分の目標で、つかんだ点と引き合う）
        /// </summary>
        PointRole LightRole (Point point, out float positionWeight, out float rotationWeight)
        {
            bool weighted = GetWeights (point, out positionWeight, out rotationWeight);
            if (!IsUsable (point)) return PointRole.Follow;
            if (grabbed_.Contains (point)) return weighted ? PointRole.Grabbed : PointRole.Follow;
            // 動かせない点（S89）は、重さに関係なく Pin の点と同じに留まる（向きは持たない）。力が先から伝わってこなければ、Free の軽い点と同じに引かれる
            if (point.passive) return !grabbedBodies_[point.body] && PassiveApplies (point) ? PointRole.Held : PointRole.Follow;
            if (weighted) {
                // 重さ 0 の Pin の点は、Free の軽い点と同じに引かれる（10-30 ユーザー指示「Pin でも引く」）
                if (point.weightless && PullsPin (point)) return PointRole.Follow;
                return grabbedBodies_[point.body] || positionWeight < 1 || point.blendBody >= 0 ? PointRole.Target : PointRole.Held;
            }
            // Free の軽い点は引かれる
            return grabbedBodies_[point.body] || point.weightless ? PointRole.Follow : PointRole.Held;
        }

        /// <summary>
        /// 点をつかんで動かしている間に、この Pin の点を引けるようにするか（S88。重さ 0 なら、軽い点・重い点どちらをつかんでも目標に使わない。
        /// 重さ 1 なら、重い点をつかんだとき、届かなければ届かなかった分だけ目標を運ぶ。HeavyEffectors）。Lock の点（動かさないと決めた点）と、
        /// 向きを混ぜる点（膝の Pole。S34。向きを決める点で、引く物ではない）は今のまま。引かれた点は、離したときに骨の今の場所で Pin のまま付け直す（SyncToBone）
        /// </summary>
        bool PullsPin (Point point)
        {
            return !grabbed_.Contains (point) && point.values.locked <= 0.5f && point.blendBody < 0;
        }

        /// <summary>解くときに使える点か（解く骨に乗っていて、足の箱・床に運ばれる点でない）</summary>
        bool IsUsable (Point point)
        {
            // 足の箱で動かしている足の点は使わない（足の骨を箱の位置と向きに留める。AddFootBoxes）
            return point.body >= 0 && !carried_.Contains (point) && !IsFootBoxed (point);
        }

        /// <summary>今効かせる点の、位置と向きの固定の強さ（設定の pull を掛けた値）。どちらも 0 なら false</summary>
        bool GetWeights (Point point, out float positionWeight, out float rotationWeight)
        {
            positionWeight = rotationWeight = 0;
            if (point.passive || !IsUsable (point) || !IsSolved (point)) return false;
            positionWeight = Mathf.Clamp01 (point.values.positionWeight) * pull;
            rotationWeight = point.hasRotation ? Mathf.Clamp01 (point.values.rotationWeight) * pull : 0;
            // Floor Clamp の間は、同じ骨に位置を固定した点がほかにもあれば、向きは点の位置で決める（向きの固定を使わない）
            if (floorClamp && solveOnly_ == null && point.contact && rotationWeight > 0 && HasOtherPositionPin (point)) rotationWeight = 0;
            return positionWeight > 0 || rotationWeight > 0;
        }
    }

}
