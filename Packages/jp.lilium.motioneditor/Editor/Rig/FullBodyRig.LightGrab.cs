using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    // 軽い点をつかんでいる間の解き方（S88 の決まり 1〜4。動かない点とその向こうを 1 回目の姿勢に留めて、つかんだ点で引く）
    public sealed partial class FullBodyRig
    {
        /// <summary>SnapHeld で、留めた骨を戻すために手前の骨を回す回数</summary>
        const int kSnapHeldIterations = 32;
        // 軽い点をつかんでいる間に、動かない点（Free の重い点・Pin の点）とその向こうを留める姿勢（1 回目に、つかんだ点を除いて解いた姿勢）
        Quaternion[] heavyRotations_;
        Vector3[] heavyPositions_;
        // 軽い点をつかんでいる間に留めた骨: 付け根の場所を留めたか・向きを留めたか（SnapHeld で合わせ直す）。
        // frozen_ は、動かない点の向こう側の骨（つかんだ点から、動かない点を通らずに届かない骨。動かない骨にする）
        bool[] heldOrigin_;
        bool[] heldRotation_;
        bool[] frozen_;
        // 軽い点をつかんでいる間の、つかんだ点の骨（ここから骨の木をたどる。LightRole）
        bool[] grabbedBodies_;
        // AddHeldEffectors の作業用: 動かない点のある骨・向きも動かない骨・付け根から離れた点のある骨・動かない点の数・たどり着いた骨・入ってきた隣の骨
        bool[] walls_;
        bool[] oriented_;
        bool[] offAxis_;
        int[] heldCount_;
        bool[] reached_;
        int[] entry_;
        readonly Stack<int> open_ = new Stack<int> ();
        // LockHeld の作業用: 動けない骨
        bool[] locked_;
        // SnapHeld の作業用の姿勢
        Vector3[] snapPositions_;
        Quaternion[] snapRotations_;

        /// <summary>2 回目: 1 回目の姿勢（hasFirstPass が false なら元の姿勢）から、動かない点（Free の重い点・Pin の点）とその向こうを留めて、つかんだ点で引く</summary>
        void SolveLightGrab (bool hasFirstPass)
        {
            for (int i = 0; i < bodies_.Count; i++) {
                heavyPositions_[i] = hasFirstPass ? (Vector3)data_.positions[i] : solvePositions_[i];
                heavyRotations_[i] = hasFirstPass ? (Quaternion)data_.rotations[i] : solveRotations_[i];
            }
            // 留める動かない点を先に並べる（同じ骨にほかの点があっても、動かない点を先に満たす）
            int count = AddHeldEffectors ();
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                PointRole role = LightRole (point, out positionWeight, out rotationWeight);
                // 留めた点・動かない骨の上の点は、自分の目標を使わない。重さ 0 の Pin の点は、Free の点と同じに目標を使わない（引かれる）
                if (role != PointRole.Grabbed && (role != PointRole.Target || frozen_[point.body])) continue;
                // つかんでいる点は、Pin の点・留めた重い点とぶつかったら譲る（kLightPull）。強さ 1 のままだと、両方を満たそうとして関節が離れたまま反復が終わり、
                // 仕上げで骨をつなぎ直したときに、留めた重い点の骨が親に付いて動いた（10-27 実測で、左肩を動かすと首と頭が 2.3mm 動いた）
                if (role == PointRole.Grabbed) positionWeight = Mathf.Min (positionWeight, kLightPull);
                data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight);
            }
            count = AddFootBoxes (count);
            MarkFixedBones ();
            LockHeld ();
            data_.iterations = Mathf.Max (1, settings_.iterations) * kLightIterations;
            RunSolver (count, heavyPositions_, heavyRotations_);
            SnapHeld ();
        }

        /// <summary>
        /// 軽い点をつかんでいる間、動かせない点を壁にするか（S89）。つかんだ点がどれも、動かせない点の骨か、その先の骨にあるとき（力が先から伝わってくるとき）だけ。
        /// 根の側（胸など）や、ほかの枝（反対の手）もつかんでいれば壁にしない: 胸だけをつかむと、両肩の点が壁になって胸が動けなかった
        /// （10-28 ユーザー報告「passiveとふつうの点とを複数選択して移動するとpassiveの点が動く」）
        /// </summary>
        bool PassiveApplies (Point point)
        {
            bool any = false;
            foreach (Point grabbed in grabbed_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (grabbed, out positionWeight, out rotationWeight) && !IsFootBoxed (grabbed)) continue;
                int body = IsFootBoxed (grabbed) ? grabbed.floorGroup : grabbed.body;
                if (body < 0) continue;
                if (body != point.body && !IsBelow (body, point.body)) return false;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// 軽い点をつかんで動かしている間（S88）、動かない点を今の場所（heavyPositions_ / heavyRotations_。1 回目の姿勢）へ留める目標を、先頭から入れる。入れた数を返す。
        /// 動かない点は、Free の重い点（重さ 1）と、つかんでいない Pin の重い点・Lock の点（位置の強さ 1。Lock は重さに関係なく）と、動かせない点（passive。S89。付け根から離れていても軸まわりに回れる。AxisTurns）。
        /// 強さが 1 より小さい Pin の点と、向きを混ぜる点（膝の Pole）は、動かない点にしない（今までどおり自分の目標で、つかんだ点と引き合う）。
        /// 動かない点は計算の上で動かない物で、その向こうの点は動かない（10-27 ユーザー指示「計算上動かないからその向こう側の制御点は動かない」）:
        /// - つかんだ点の骨から、動かない点のある骨を越えずに届かない骨は、向こう側の骨として動かない骨にする（frozen_）
        /// - 動かない点のある骨は、点のまわりに回っても向こう側が動かないときだけ回れる（Turns）。回れなければ向きも留める
        ///   （首が重く頭が軽いとき、頭の点を動かすと首が回る。腰が重いとき、膝を動かしても腰は回らない。背骨と反対の脚が付いて動くため）
        /// </summary>
        int AddHeldEffectors ()
        {
            int n = bodies_.Count;
            for (int i = 0; i < n; i++) {
                heldOrigin_[i] = heldRotation_[i] = frozen_[i] = grabbedBodies_[i] = false;
                walls_[i] = oriented_[i] = offAxis_[i] = reached_[i] = false;
                heldCount_[i] = 0;
            }
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (grabbed_.Contains (point) && GetWeights (point, out positionWeight, out rotationWeight)) grabbedBodies_[point.body] = true;
            }
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (LightRole (point, out positionWeight, out rotationWeight) != PointRole.Held) continue;
                bool weighted = positionWeight > 0 || rotationWeight > 0;
                heldCount_[point.body]++;
                walls_[point.body] = true;
                if (!point.passive && (weighted ? rotationWeight > 0 : point.hasRotation)) oriented_[point.body] = true;
                if (point.offset.sqrMagnitude > kEpsilon * kEpsilon) offAxis_[point.body] = true;
            }

            // つかんだ点の骨から、動かない点のある骨で止まりながらたどる。entry は動かない点のある骨へ入ってきた隣の骨（2 つ以上の側から入れば -2）
            for (int i = 0; i < n; i++) {
                entry_[i] = -1;
                if (!grabbedBodies_[i]) continue;
                reached_[i] = true;
                open_.Push (i);
            }
            while (open_.Count > 0) {
                int b = open_.Pop ();
                for (int m = 0; m < n; m++) {
                    if (m != bodyParents_[b] && bodyParents_[m] != b) continue;
                    if (walls_[m] && !grabbedBodies_[m]) {
                        reached_[m] = true;
                        entry_[m] = entry_[m] == -1 || entry_[m] == b ? b : -2;
                        continue;
                    }
                    if (reached_[m]) continue;
                    reached_[m] = true;
                    open_.Push (m);
                }
            }
            for (int i = 0; i < n; i++) {
                if (reached_[i]) continue;
                frozen_[i] = heldOrigin_[i] = heldRotation_[i] = true;
            }

            int count = 0;
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (LightRole (point, out positionWeight, out rotationWeight) != PointRole.Held) continue;
                int body = point.body;
                if (frozen_[body]) continue;
                // 付け根から離れた点を 1 つだけ留めた骨は、付け根と点を結ぶ軸まわりに回せる（AxisTurns。骨は留めた点を中心に回る、の基盤どおり）
                bool axisTurn = heldCount_[body] == 1 && !oriented_[body] && AxisTurns (body, entry_[body]);
                bool rotation = !heldRotation_[body] && !axisTurn && !Turns (body, entry_[body], oriented_[body], offAxis_[body]);
                if (rotation) heldRotation_[body] = true;
                // 向きも留めた骨は、付け根の場所も決まる。向きを留めない骨は、付け根の点を留めたときと、軸まわりに回すとき（付け根は動かない親に付いている）
                if (rotation || axisTurn || point.offset.sqrMagnitude <= kEpsilon * kEpsilon) heldOrigin_[body] = true;
                data_.effectors[count++] = new FullBodyIkEffector {
                    bone = body,
                    offset = point.offset,
                    priority = 1,
                    position = heavyPositions_[body] + heavyRotations_[body] * point.offset,
                    rotation = heavyRotations_[body],
                    positionWeight = 1,
                    rotationWeight = rotation ? 1 : 0,
                    blendBone = -1,
                };
            }
            return count;
        }

        /// <summary>
        /// 動かない点のある骨 body が、点のまわりに回れるか（AddHeldEffectors）。side はつかんだ点の側の隣の骨。
        /// 回ると向こう側（side 以外の隣の骨）が動くなら回れない: 子の骨は付いて回り、親の骨は点が付け根から離れていれば付け根ごと動く。
        /// 向きを持つ点（手・足・腰・頭）は、向きも動かない
        /// </summary>
        bool Turns (int body, int side, bool oriented, bool offAxis)
        {
            if (side < 0 || oriented) return false;
            int parent = bodyParents_[body];
            if (parent >= 0 && parent != side && offAxis) return false;
            for (int c = body + 1; c < bodies_.Count; c++) {
                if (bodyParents_[c] == body && c != side) return false;
            }
            return true;
        }

        /// <summary>
        /// 動かない点を 1 つだけ留めた骨 body が、付け根と点を結ぶ軸まわりに回れるか（AddHeldEffectors）。side はつかんだ点の側の隣の骨。
        /// 骨は留めた点を中心に回る（全身 IK の基盤）が、付け根も動かない親に付いていれば、留めた所が 2 つ（付け根と点）になり、2 点を結ぶ軸まわりだけが残る。
        /// 親が向こう側（動かない骨）で、つかんだ点の側が子で、ほかに子が無ければ回れる（ほかの子は軸まわりに回ると動いてしまう）。
        /// 鎖骨の付け根から前後にずらした点なら、軸は体の前後を向き、肩が上下に回る。点が付け根にあれば Turns が付け根まわりに回す
        /// </summary>
        bool AxisTurns (int body, int side)
        {
            int parent = bodyParents_[body];
            if (side < 0 || parent < 0 || !frozen_[parent] || bodyParents_[side] != body) return false;
            for (int c = body + 1; c < bodies_.Count; c++) {
                if (bodyParents_[c] == body && c != side) return false;
            }
            return true;
        }

        /// <summary>
        /// 軽い点をつかんでいる間に、重い点・Pin の点を留めたことで動けなくなる骨を、動かない骨（FullBodyIkData.fixedBones）にする。
        /// 骨の動きを止めるもの（足場）は、付け根（親の骨が場所も向きも決まっていれば動かない）と、子の骨の付け根（留めた骨・動けない骨の付け根）。
        /// 足場が 2 つ以上ある骨は、剛体として動けない（2 点を結ぶ軸まわりのねじりは残るが、ほかの子が付いて回るので止める）。これを広がらなくなるまで繰り返す。
        /// 反復で解くソルバーは、こうした挟まれた骨を少し曲げた所で止まり、仕上げでつなぎ直すと留めた骨がずれた
        /// （10-27。鎖骨の無いキャラで、胸を軽くして肩を動かすと、首・右上腕・肘で挟まれた胸が 9mm 曲がり、重い首が 0.5mm 動いた）。
        /// 動かない骨にするのは、挟まれた軽い点の骨・点の無い骨だけ（重い点の骨は目標で留めてある）
        /// </summary>
        void LockHeld ()
        {
            int n = bodies_.Count;
            bool[] locked = locked_;
            for (int i = 0; i < n; i++) locked[i] = heldOrigin_[i] && heldRotation_[i];
            for (bool grew = true; grew;) {
                grew = false;
                for (int b = 0; b < n; b++) {
                    if (locked[b] || heldOrigin_[b] || heldRotation_[b]) continue;
                    int p = bodyParents_[b];
                    int anchors = p >= 0 && locked[p] ? 1 : 0;
                    for (int c = b + 1; c < n && anchors < 2; c++) {
                        if (bodyParents_[c] == b && (locked[c] || heldOrigin_[c])) anchors++;
                    }
                    if (anchors < 2) continue;
                    locked[b] = true;
                    grew = true;
                }
            }
            // 動かない点の骨は、目標で留めてある（動かない骨にすると、足を留めた膝の点が膝をほとんど動かせなかった）。向こう側の骨は動かない骨にする
            for (int i = 0; i < n; i++) {
                if (frozen_[i] || (locked[i] && !heldOrigin_[i])) data_.fixedBones[i] = 1;
            }
        }

        /// <summary>
        /// 軽い点をつかんでいる間に留めた動かない点の骨とその向こうの骨を、1 回目の場所と向き（heavyPositions_ / heavyRotations_）へぴったり戻す。
        /// ソルバーは最後に骨を根から順につなぎ直すので、反復で詰めきれなかった関節のずれが、留めた骨の側へ寄って残る
        /// （10-27 実測で、胸を軽くして肩を動かすと、重い首が 0.5mm 動いた）。重い点は動かない物として解きたいので、
        /// 留めた骨ごとに、根の側の次に留めた骨までの間の骨（軽い点の骨・点の無い骨）を回して合わせる（CCD）。蝶番の骨は蝶番の軸まわりにだけ回す。
        /// 首と反対の上腕のように、同じ骨（上胸）から留めた骨が何本も出ているときは、1 本ずつ合わせると前に合わせた骨がずれるので、
        /// 全部の留めた骨を何周か回して一緒に詰める（留めた骨の場所は 1 回目の姿勢で全部満たせるので、詰まっていく）。合わせた分のずれは、軽い点の側に出る
        /// </summary>
        void SnapHeld ()
        {
            int n = bodies_.Count;
            Vector3[] positions = snapPositions_;
            Quaternion[] rotations = snapRotations_;
            for (int i = 0; i < n; i++) {
                positions[i] = data_.positions[i];
                rotations[i] = data_.rotations[i];
            }
            for (int iteration = 0; iteration < kSnapHeldIterations; iteration++) {
                bool off = false;
                for (int h = 0; h < n; h++) {
                    if (!heldOrigin_[h]) continue;
                    Vector3 target = heavyPositions_[h];
                    if ((positions[h] - target).sqrMagnitude <= 1e-14f) continue;
                    off = true;
                    for (int c = bodyParents_[h]; c >= 0 && !heldOrigin_[c] && !heldRotation_[c]; c = bodyParents_[c]) {
                        Vector3 from = positions[h] - positions[c];
                        Vector3 to = target - positions[c];
                        Quaternion turn;
                        int p = bodyParents_[c];
                        if ((data_.bones[c].hinge != 0 || data_.bones[c].axisLock != 0) && p >= 0) {
                            // 蝶番と、軸まわりにだけ回る骨（S89。軸は自分の骨の枠で持つ）は、その軸まわりにだけ回す
                            Vector3 axis = data_.bones[c].axisLock != 0 ? rotations[c] * (Vector3)data_.bones[c].lockAxis : rotations[p] * (Vector3)data_.bones[c].hingeAxis;
                            Vector3 a = Vector3.ProjectOnPlane (from, axis), b = Vector3.ProjectOnPlane (to, axis);
                            if (a.sqrMagnitude < 1e-12f || b.sqrMagnitude < 1e-12f) continue;
                            turn = Quaternion.AngleAxis (Vector3.SignedAngle (a, b, axis), axis);
                        }
                        else {
                            if (from.sqrMagnitude < 1e-12f || to.sqrMagnitude < 1e-12f) continue;
                            turn = Quaternion.FromToRotation (from, to);
                        }
                        RotateSubtree (positions, rotations, c, turn);
                    }
                }
                if (!off) break;
            }
            for (int h = 0; h < n; h++) {
                if (heldRotation_[h]) RotateSubtree (positions, rotations, h, heavyRotations_[h] * Quaternion.Inverse (rotations[h]));
            }
            for (int i = 0; i < n; i++) {
                data_.positions[i] = positions[i];
                data_.rotations[i] = rotations[i];
            }
        }

        /// <summary>骨 body とその先を、body の付け根まわりに turn だけ回す（ルート空間の位置と向き）</summary>
        void RotateSubtree (Vector3[] positions, Quaternion[] rotations, int body, Quaternion turn)
        {
            Vector3 pivot = positions[body];
            for (int k = body; k < bodies_.Count; k++) {
                if (k != body && !IsBelow (k, body)) continue;
                positions[k] = pivot + turn * (positions[k] - pivot);
                rotations[k] = Quaternion.Normalize (turn * rotations[k]);
            }
        }
    }

}
