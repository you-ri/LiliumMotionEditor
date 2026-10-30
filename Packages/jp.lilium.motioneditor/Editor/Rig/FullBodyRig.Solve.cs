using UnityEngine;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;

namespace Lilium
{

    // 解く手順の共通部分（元の姿勢とルート空間の値を渡し、つかみ方ごとに解いて、結果を骨へ書き戻す）
    public sealed partial class FullBodyRig
    {
        /// <summary>
        /// controls にあるコントロールの点だけを効かせて解く（ほかの点は、固定の強さが入っていても使わない）。
        /// キーの間で、前後のキーで Locked の点だけを留めるときに使う（S25d）。
        /// limbsOnly なら、点のある手足（首から先）だけを動かし、腰・背骨などそのほかの骨は元の姿勢（FK）のままにする:
        /// 点のある骨から根の方へ、枝分かれする骨（腰・上胸など）の手前までを動かす。点が腰そのものにあれば腰も動かす
        /// </summary>
        public void SolveOnly (ICollection<Transform> controls, float weight = 1, bool limbsOnly = false)
        {
            if (controls == null || controls.Count == 0) return;
            solveOnly_ = controls;
            limbsOnly_ = limbsOnly;
            try {
                Solve (weight);
            }
            finally {
                solveOnly_ = null;
                limbsOnly_ = false;
            }
        }

        ICollection<Transform> solveOnly_;
        bool limbsOnly_;

        /// <summary>
        /// 動かさない骨の印を入れる（limbsOnly のとき、点のある手足以外は 1）
        /// </summary>
        void MarkFixedBones ()
        {
            int n = bodies_.Count;
            for (int i = 0; i < n; i++) data_.fixedBones[i] = limbsOnly_ ? 1 : 0;
            if (!limbsOnly_) return;
            int[] children = new int[n];
            for (int i = 0; i < n; i++) {
                if (bodyParents_[i] >= 0) children[bodyParents_[i]]++;
            }
            foreach (Point point in points_) {
                if (point.body < 0 || !IsActive (point)) continue;
                int body = point.body;
                data_.fixedBones[body] = 0;
                for (int p = bodyParents_[body]; p >= 0 && children[p] <= 1; p = bodyParents_[p]) data_.fixedBones[p] = 0;
            }
        }

        /// <summary>
        /// 今の編集用の骨の姿勢（元の姿勢）から、固定の強さが入っている点へ寄せる。
        /// weight は段の重み（元の姿勢と解いた姿勢を、骨のローカルの値で混ぜる）。0 なら何もしない
        /// </summary>
        public void Solve (float weight = 1)
        {
            if (rig_ == null || rig_.root == null || weight <= 0 || !hasActivePoint) return;

            Vector3[] beforePositions = null;
            Quaternion[] beforeRotations = null;
            if (weight < 1) {
                beforePositions = new Vector3[bones_.Length];
                beforeRotations = new Quaternion[bones_.Length];
                for (int i = 0; i < bones_.Length; i++) {
                    if (bones_[i] != null) bones_[i].GetLocalPositionAndRotation (out beforePositions[i], out beforeRotations[i]);
                }
            }

            if (!SolveBodies ()) return;
            solved_ = true;

            if (weight >= 1) return;
            for (int i = 0; i < bones_.Length; i++) {
                if (bones_[i] == null) continue;
                bones_[i].GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                bones_[i].SetLocalPositionAndRotation (Vector3.Lerp (beforePositions[i], position, weight), Quaternion.Slerp (beforeRotations[i], rotation, weight));
            }
        }

        bool IsActive (Point point)
        {
            return !point.passive && IsSolved (point) && (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0));
        }

        /// <summary>今解く点か（SolveOnly で絞っているときは、その点だけ）</summary>
        bool IsSolved (Point point)
        {
            return solveOnly_ == null || solveOnly_.Contains (point.control);
        }

        /// <summary>
        /// 今の編集用の骨（元の姿勢）と点の値をルート空間の値にして解き、結果を骨へ書き戻す。
        /// 書き戻すのは骨の向きと、根（腰）の位置だけ（骨の長さは変わらない）。何も変わらなければ骨に触らず false
        /// </summary>
        bool SolveBodies ()
        {
            if (!data_.isCreated) return false;
            Transform root = rootTransform;
            Quaternion rootRotation = root.rotation;
            Quaternion rootInverse = Quaternion.Inverse (rootRotation);
            Vector3 rootPosition = root.position;
            float scale = humanScale;
            float pull = Mathf.Clamp01 (settings_.pull);

            for (int i = 0; i < bodies_.Count; i++) {
                solvePositions_[i] = rootInverse * (bodies_[i].position - rootPosition);
                solveRotations_[i] = rootInverse * bodies_[i].rotation;
                data_.positions[i] = solvePositions_[i];
                data_.rotations[i] = solveRotations_[i];
            }
            // 軽い点（重さ 0。S88）をつかんで動かしているか。つかんでいれば、つかんだ点が引き、引かれる点は状態と重さで決まる:
            // 軽い点は Free でも Pin でも引かれ、重い点は Free でも Pin でも今の場所に留まる。Lock の点は重さに関係なく留まる（10-27 ユーザー指示。
            // 軽い Pin の点は、10-27 には留めていたが、10-30 ユーザー指示「weight0を動かすと同じweight0 の肘は動くはず」「Pin でも引く」で引くようにした）。
            // まとめてつかんだ点に重い点が混ざれば、一番重い点で決める（重い点をつかんだときと同じ。10-27 ユーザー決定）。
            // 重い点をつかんでいれば、重さ 0 の Pin の点は Free の点と同じに引かれ、重さ 1 の Pin の点は、届く間はその場に留まり、つかんだ点が届かなければ外れて付いて動く
            // （PullsPin。10-27 ユーザー指示「ウェイト１がウェイト１をひっぱる場合、pin でも動くようにする」「pinの場合はその場を維持するように。ただし引っ張ることはできる」）。
            // つかんでいなければ（キーから解き直す）、今までどおり Pin の点を全部効かせ、Free の点は重さに関係なく引かれる
            bool lightGrab = false, heavyGrab = false;
            foreach (Point point in points_) {
                if (!grabbed_.Contains (point)) continue;
                // Floor Clamp の足の箱で動かしている足の点は、点としては解かない（箱の目標で動かす）が、つかんだ点として数える
                // （数えないと、重い足を引いて脚が伸びきっても、Pin の腰を外して解き直さず、体が付いてこなかった。10-28 ユーザー報告）
                float positionWeight, rotationWeight;
                if (!GetWeights (point, pull, out positionWeight, out rotationWeight) && !IsFootBoxed (point)) continue;
                if (point.light) lightGrab = true;
                else if (!point.weightless) heavyGrab = true;
            }
            if (heavyGrab) lightGrab = false;

            // 1 回目: つかんでいる軽い点を除いて、ほかの点を全部いつもどおり解く（軽い点をつかんでいなければ、これだけ）。
            // 解き直すたびに元の姿勢（キーの姿勢）から解くので、動かない点の「今の場所」は、つかんだ点が無いときに点が居る場所
            int count = 0;
            bool pins = false;
            grabbedEffectors_.Clear ();
            lockedEffectors_.Clear ();
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (point, pull, out positionWeight, out rotationWeight)) continue;
                if (lightGrab && grabbed_.Contains (point)) continue;
                if (heavyGrab && grabbed_.Contains (point)) grabbedEffectors_.Add (count);
                else if (heavyGrab && point.values.locked > 0.5f && positionWeight >= 1) lockedEffectors_.Add (count);
                // 重さ 0 の Pin の点は、Free の点と同じに引かれる（目標に使わない。10-28 ユーザー指示「自分（つかんだ点）が重さ 1 のとき
                // その先の点の重さが0ならpinでも freeと同じように引かれるようにして」）。重さ 1 の Pin の点は強さ 1 のまま効かせる
                // （重さ 0 の Pin の点（腹・胸など）とぶつかって答えが跳ねていたのは、これで起きない。10-28 実測で、強さを 0.99 に抑えて
                // 仕上げでも効かせる形は、腕のように鎖が何本もつながる所で詰め切れず、腰を 20cm 動かすと Pin の手が 9cm 流れた）
                if (heavyGrab && point.weightless && PullsPin (point)) continue;
                pins |= heavyGrab && PullsPin (point);
                data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight, scale);
            }
            int boxes = count;
            count = AddFootBoxes (count, rootInverse, rootPosition);
            // 足の箱はつかんで動かしている足なので、届いたかを見る目標に入れる
            if (heavyGrab) {
                for (int e = boxes; e < count; e++) grabbedEffectors_.Add (e);
            }
            if (count == 0 && !lightGrab) return false;
            MarkFixedBones ();
            // 重い点をつかんで Pin の点を効かせている間は、反復を増やす（kLightIterations 倍）。Pin の点に挟まれた背骨が詰め切れず、
            // 届く所でも届かないとみなして腰の Pin を外していた（10-28 実測で、腹・胸を硬さ 0.6〜0.8 にした定義で首を前へ 5mm 動かすと、1 倍では 1.9mm 残った）
            pins |= lockedEffectors_.Count > 0;
            // Lock の点が動いたかは、点ごとに元の姿勢でのずれと比べる（同じ足に Lock の点が 3 つ以上あると、元の姿勢でも数 mm ずれている。
            // 一番大きいずれを全部の点に使うと、ずれの小さい足首の点がその差だけ動けた。10-28 実測で 1cm）
            lockBases_.Clear ();
            foreach (int e in lockedEffectors_) lockBases_.Add (InputResidual (e));
            data_.iterations = Mathf.Max (1, settings_.iterations) * (pins ? kLightIterations : 1);
            data_.rootPin = Mathf.Clamp01 (settings_.hipsPin);
            SetContacts (rootPosition.y, rootInverse * Vector3.up);
            if (count > 0) RunSolver (count, solvePositions_, solveRotations_);
            // 届かないか Lock の点が動いたら、届かなかった分だけ Pin の点の目標を運んで解き直す（10-28。Fable の見立てを受けて、段に分けて外す形から換えた）:
            // 1. 保持解き: Pin の点・Lock の点を強さ 1、つかんだ点を譲らせて（kLightPull）解き、つかんだ点が届かなかった分 shift を測る。
            // 2. つかんだ点の骨から根（腰）へたどる道の上の Pin の点（首なら腰）の目標を、間の骨（背骨）が伸びきるまでは運ばず、超えた分だけ運ぶ（PathStretch）。
            //    Lock の点（足）が届く所まで（LockLimit）。届かないほど、根（腰）の向きを元の向きに留める硬さを緩める（ReleaseRoot）。
            // 3. 道の外の Pin の点は、その姿勢で、手足の付け根（肩・股）から目標までが手足の長さ（伸びきった長さ）を超えた分だけ、付け根の方へ運ぶ（BranchShifts）。
            //    腕が伸びきるまでは動かず、伸びきったらその先だけ付いてくる（10-28 ユーザー報告「首を持ち上げると、手首がのびきっていないのに、手首が動いてしまう」、
            //    10-29「首をどんどんひっぱったとき腕がのびきらない」。首が届かなかった分を両手に運ぶと、体が傾くと肩の動きと合わず、運びすぎて腕が曲がった）。
            // 4. Lock の点が動いたら、運ぶ割合を絞る（CarryPins）。Lock の点が無ければ、つかんだ点を強さ 1 にして、運んだ目標のまま解く。
            //    それでもつかんだ点が届かなければ、届かなかった分を道の上の Pin の点（腰）へ運び足して解き直す（kCarryRefines 回まで）。
            // どの量も前の答えから連続に決まり、届く所では 0 なので、境目で姿勢が跳ねず、元の姿勢（キーの形）へ戻すこともない
            // （10-28 ユーザー指示「ひっぱる距離に応じて、強制的に姿勢をもとに戻す制御をしているが、そういったことはしないようにして」）
            if (pins && !Solved ()) {
                bool locks = lockedEffectors_.Count > 0;
                bool[] path = PathToRoot ();
                count = HeavyEffectors (path, Vector3.zero, null, 0, true, pull, scale, rootInverse, rootPosition);
                RunSolver (count, solvePositions_, solveRotations_);
                Vector3 shift = GrabbedShift ();
                if (shift.sqrMagnitude > kReachTolerance * kReachTolerance) {
                    KeepYielded ();
                    // 運んだあと Lock の点が動いたかは、保持解き（運ぶ前）のずれと比べる。同じ足に Lock の点が 3 つ以上あると、首に引かれて足が少し回るだけで
                    // どれかの点のずれが元の姿勢より増え、どれだけ少なく運んでも動いたとみなした（10-29 実測で、両足が Lock のキーで腰が上がらなかった）
                    for (int k = 0; k < lockedEffectors_.Count; k++) lockBases_[k] = Mathf.Max (lockBases_[k], data_.residuals[lockedEffectors_[k]]);
                    // 道の上の Pin の点（腰）を運ぶ量は、長さから決める（PathStretch）: つかんだ点の一番近くの道の上の Pin の点から、つかんだ点の目標までが、
                    // 間の骨を伸ばしきった長さを超えた分だけ。体が伸びきるまで腰は動かず、伸びきったらその先だけ付いてくる。
                    // 譲らせて解いた答えの届かなかった分で決めると、譲らせた首が関節の硬さのばねに負けて背骨を伸ばしきれず、背骨が曲がったまま腰が運ばれた
                    // （10-29 ユーザー報告「首をひっぱっても体が伸び切らない」。腰から首までが、伸びきった長さの 90% で止まっていた）
                    // 根（腰）の向きを元の姿勢に留める硬さを、届かなかった分に応じて緩める（ReleaseRoot）。留めたままだと、腰の点は向きを留めていなくても
                    // 腰の骨が元の向きのまま残り、手を横へ引くと背骨の付け根だけが 50° 曲がった（10-29 ユーザー報告「左手を+Xに引いたが体、腰あたりが伸びきらない」）。
                    // 届く所では 0 なので、境目で姿勢は跳ねない
                    int releasedRoot;
                    float rootStiffness = ReleaseRoot (path, Mathf.Clamp01 (shift.magnitude / (kRootRelease * scale)), out releasedRoot);
                    shift = PathStretch (path, scale);
                    // Lock の点（足）は、手足の付け根（股）から目標までが手足の長さを超えない所までしか、道の上の Pin の点（腰）を運ばない（LockLimit）。
                    // 運んでから Lock の点が動いたかで割合を絞ると、遠くへ引いたとき絞る細かさが足りず、運べる所でも運ばなかった（10-29 実測で、腰が 5.8cm 上がってから 0 に戻った）
                    shift *= LockLimit (path, shift, pull, scale);
                    // 背骨を伸ばした姿勢から解き始める（StraightenPath）。ソルバーは伸びきる手前を詰めるのが苦手で、反復を 4 倍にしても腰から首までが 98.6% で止まり、
                    // 胸・上胸に 20〜25° ずつ曲がりが残った（10-30 ユーザー報告「首を持ち上げても体がまっすぐにならない」）。解き終えたら元の姿勢へ戻す
                    System.Array.Copy (solvePositions_, keptPositions_, bodies_.Count);
                    System.Array.Copy (solveRotations_, keptRotations_, bodies_.Count);
                    StraightenPath (shift, scale);
                    // 道の外の Pin の点を運ぶ量は、腰を運んで道の外の Pin の点を外して解いた姿勢で決める（BranchShifts）
                    count = HeavyEffectors (path, shift, null, 0, locks, pull, scale, rootInverse, rootPosition, false);
                    RunSolver (count, solvePositions_, solveRotations_);
                    BranchShifts (path, pull, scale);
                    RestoreYielded ();
                    float fraction = CarryPins (path, shift, pull, scale, rootInverse, rootPosition);
                    if (!locks) {
                        count = HeavyEffectors (path, shift * fraction, branchShifts_, fraction, false, pull, scale, rootInverse, rootPosition);
                        RunSolver (count, solvePositions_, solveRotations_);
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
                                solvePositions_[i] = data_.positions[i];
                                solveRotations_[i] = data_.rotations[i];
                            }
                            count = HeavyEffectors (path, shift, branchShifts_, 1, false, pull, scale, rootInverse, rootPosition);
                            RunSolver (count, solvePositions_, solveRotations_);
                        }
                    }
                    if (releasedRoot >= 0) {
                        FullBodyIkBone bone = data_.bones[releasedRoot];
                        bone.stiffness = rootStiffness;
                        data_.bones[releasedRoot] = bone;
                    }
                    System.Array.Copy (keptPositions_, solvePositions_, bodies_.Count);
                    System.Array.Copy (keptRotations_, solveRotations_, bodies_.Count);
                }
            }

            // 2 回目: 1 回目の姿勢から、動かない点（Free の重い点・Pin の点）とその向こうを留めて、つかんだ点で引く
            if (lightGrab) {
                for (int i = 0; i < bodies_.Count; i++) {
                    heavyPositions_[i] = count > 0 ? (Vector3)data_.positions[i] : solvePositions_[i];
                    heavyRotations_[i] = count > 0 ? (Quaternion)data_.rotations[i] : solveRotations_[i];
                }
                // 留める動かない点を先に並べる（同じ骨にほかの点があっても、動かない点を先に満たす）
                count = AddHeldEffectors (pull);
                foreach (Point point in points_) {
                    float positionWeight, rotationWeight;
                    if (!GetWeights (point, pull, out positionWeight, out rotationWeight)) continue;
                    // 留めた点・動かない骨の上の点は、自分の目標を使わない。重さ 0 の Pin の点は、Free の点と同じに目標を使わない（引かれる）
                    if (held_.Contains (point) || frozen_[point.body]) continue;
                    if (point.weightless && PullsPin (point)) continue;
                    // つかんでいる点は、Pin の点・留めた重い点とぶつかったら譲る（kLightPull）。強さ 1 のままだと、両方を満たそうとして関節が離れたまま反復が終わり、
                    // 仕上げで骨をつなぎ直したときに、留めた重い点の骨が親に付いて動いた（10-27 実測で、左肩を動かすと首と頭が 2.3mm 動いた）
                    if (grabbed_.Contains (point)) positionWeight = Mathf.Min (positionWeight, kLightPull);
                    data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight, scale);
                }
                count = AddFootBoxes (count, rootInverse, rootPosition);
                MarkFixedBones ();
                LockHeld ();
                data_.iterations = Mathf.Max (1, settings_.iterations) * kLightIterations;
                RunSolver (count, heavyPositions_, heavyRotations_);
                SnapHeld ();
            }

            // 変わらなかった骨は元の値がそのまま返る。全部そうなら触らない（固定した点が今の姿勢の上にあるとき、姿勢をまったく変えない）
            bool changed = false;
            for (int i = 0; i < bodies_.Count && !changed; i++) {
                quaternion rotation = data_.rotations[i];
                changed = !rotation.Equals ((quaternion)solveRotations_[i])
                    || (bodyParents_[i] < 0 && !data_.positions[i].Equals ((float3)solvePositions_[i]));
            }
            if (!changed) return false;

            // 親から順に書く（向きはワールドの値で入れるので、親を先に決める）
            for (int i = 0; i < bodies_.Count; i++) {
                if (bodyParents_[i] < 0) bodies_[i].position = rootPosition + rootRotation * (Vector3)data_.positions[i];
                bodies_[i].rotation = rootRotation * Quaternion.Normalize ((Quaternion)data_.rotations[i]);
            }
            return true;
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
        bool GetWeights (Point point, float pull, out float positionWeight, out float rotationWeight)
        {
            positionWeight = rotationWeight = 0;
            if (point.passive || !IsUsable (point) || !IsSolved (point)) return false;
            positionWeight = Mathf.Clamp01 (point.values.positionWeight) * pull;
            rotationWeight = point.hasRotation ? Mathf.Clamp01 (point.values.rotationWeight) * pull : 0;
            // Floor Clamp の間は、同じ骨に位置を固定した点がほかにもあれば、向きは点の位置で決める（向きの固定を使わない）
            if (floorClamp && solveOnly_ == null && point.contact && rotationWeight > 0 && HasOtherPositionPin (point, pull)) rotationWeight = 0;
            return positionWeight > 0 || rotationWeight > 0;
        }

        /// <summary>
        /// 点の目標。どの点も「骨の上の点を目標へ寄せる」だけ（S26。種類で解き方を変えない。同じ骨の点どうしは剛体として拘束しあう）。
        /// 軽い点はふつうの点として渡し、向けるだけになるのは向きを混ぜる点だけ
        /// </summary>
        FullBodyIkEffector PointEffector (Point point, float positionWeight, float rotationWeight, float scale)
        {
            return new FullBodyIkEffector {
                bone = point.body,
                offset = point.offset,
                priority = point.values.locked > 0.5f ? 1 : 0,
                position = point.control.localPosition * scale,
                rotation = point.control.localRotation * point.restRootRotation,
                positionWeight = positionWeight,
                rotationWeight = rotationWeight,
                aim = point.weightless && !point.light ? 1 : 0,
                blendBone = point.blendBody,
                blend = point.blendBody >= 0 ? point.blend : 0,
                blendOffset = point.blendOffset,
                blendRest = point.blendRest,
            };
        }

        /// <summary>解く骨 i が、解く骨 bone の先（子・孫）にあるか</summary>
        bool IsBelow (int i, int bone)
        {
            for (int k = bodyParents_[i]; k >= 0; k = bodyParents_[k]) {
                if (k == bone) return true;
            }
            return false;
        }

        /// <summary>先頭から count 個の目標で、元の姿勢（positions / rotations。ルート空間）から解く</summary>
        void RunSolver (int count, Vector3[] positions, Quaternion[] rotations)
        {
            for (int i = 0; i < bodies_.Count; i++) {
                data_.positions[i] = positions[i];
                data_.rotations[i] = rotations[i];
            }
            data_.effectorCount = count;
            if (useBurst) {
                new FullBodyIkJob { data = data_ }.Run ();
            }
            else {
                data_.status[FullBodyIkData.kStatusBurst] = 0;
                FullBodyIk.Solve (ref data_);
            }
            lastSolveUsedBurst = data_.status[FullBodyIkData.kStatusBurst] != 0;
            lastDefaultBendCount = data_.status[FullBodyIkData.kStatusDefaultBend];
        }
    }

}
