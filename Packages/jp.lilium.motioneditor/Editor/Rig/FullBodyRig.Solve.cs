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
            LoadInput ();
            GrabKind grab = DetectGrab ();

            // 1 回目: つかんでいる軽い点を除いて、ほかの点を全部いつもどおり解く（軽い点をつかんでいなければ、これだけ）。
            // 解き直すたびに元の姿勢（キーの姿勢）から解くので、動かない点の「今の場所」は、つかんだ点が無いときに点が居る場所
            grabbedEffectors_.Clear ();
            lockedEffectors_.Clear ();
            int count = grab == GrabKind.Heavy ? HeavyEffectors (null, Vector3.zero, null, 0, false) : KeyedEffectors (grab == GrabKind.Light);
            if (count == 0 && grab != GrabKind.Light) return false;
            MarkFixedBones ();
            // 重い点をつかんで Pin の点を効かせている間は、反復を増やす（kLightIterations 倍）。Pin の点に挟まれた背骨が詰め切れず、
            // 届く所でも届かないとみなして腰の Pin を外していた（10-28 実測で、腹・胸を硬さ 0.6〜0.8 にした定義で首を前へ 5mm 動かすと、1 倍では 1.9mm 残った）
            bool pins = grab == GrabKind.Heavy && (lockedEffectors_.Count > 0 || HasCarriedPin ());
            // Lock の点が動いたかは、点ごとに元の姿勢でのずれと比べる（同じ足に Lock の点が 3 つ以上あると、元の姿勢でも数 mm ずれている。
            // 一番大きいずれを全部の点に使うと、ずれの小さい足首の点がその差だけ動けた。10-28 実測で 1cm）
            lockBases_.Clear ();
            foreach (int e in lockedEffectors_) lockBases_.Add (InputResidual (e));
            data_.iterations = Mathf.Max (1, settings_.iterations) * (pins ? kLightIterations : 1);
            data_.rootPin = Mathf.Clamp01 (settings_.hipsPin);
            SetContacts ();
            if (count > 0) RunSolver (count, solvePositions_, solveRotations_);

            if (pins && !Solved ()) ResolveHeavyGrab ();
            if (grab == GrabKind.Light) SolveLightGrab (count > 0);
            return WriteBack ();
        }

        /// <summary>元の姿勢（今の編集用の骨）をルート空間の値にして控え、ソルバーへ渡す</summary>
        void LoadInput ()
        {
            Quaternion rootInverse = Quaternion.Inverse (rootTransform.rotation);
            Vector3 rootPosition = rootTransform.position;
            for (int i = 0; i < bodies_.Count; i++) {
                solvePositions_[i] = rootInverse * (bodies_[i].position - rootPosition);
                solveRotations_[i] = rootInverse * bodies_[i].rotation;
                data_.positions[i] = solvePositions_[i];
                data_.rotations[i] = solveRotations_[i];
            }
        }

        /// <summary>解いた姿勢を骨へ書き戻す。何も変わらなければ骨に触らず false</summary>
        bool WriteBack ()
        {
            Quaternion rootRotation = rootTransform.rotation;
            Vector3 rootPosition = rootTransform.position;
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
        /// 点の目標。どの点も「骨の上の点を目標へ寄せる」だけ（S26。種類で解き方を変えない。同じ骨の点どうしは剛体として拘束しあう）。
        /// 軽い点はふつうの点として渡し、向けるだけになるのは向きを混ぜる点だけ
        /// </summary>
        FullBodyIkEffector PointEffector (Point point, float positionWeight, float rotationWeight)
        {
            return new FullBodyIkEffector {
                bone = point.body,
                offset = point.offset,
                priority = point.values.locked > 0.5f ? 1 : 0,
                position = TargetPosition (point),
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

        /// <summary>点の目標の位置（ルート空間）</summary>
        Vector3 TargetPosition (Point point)
        {
            return point.control.localPosition * humanScale;
        }

        /// <summary>つかんでいないとき・軽い点をつかんでいるときの 1 回目の目標を、先頭から入れる。点はどれも自分の目標で解く。skipGrabbed ならつかんだ点を除く。入れた数を返す</summary>
        int KeyedEffectors (bool skipGrabbed)
        {
            int count = 0;
            foreach (Point point in points_) {
                float positionWeight, rotationWeight;
                if (!GetWeights (point, out positionWeight, out rotationWeight)) continue;
                if (skipGrabbed && grabbed_.Contains (point)) continue;
                data_.effectors[count++] = PointEffector (point, positionWeight, rotationWeight);
            }
            return AddFootBoxes (count);
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
