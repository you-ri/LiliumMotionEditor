using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// キーの間で Locked の点の行き先を、同じ手足の先の点ごと剛体として動かす（10-24）。
    /// 点ごとに直線で補間すると、足を母指球のまわりに回したキーの間で、つま先の先が弦の上を通って母指球へ近づき
    /// （0F→22F で 100° 回した足で 6.2cm → 4.2cm）、届かない分をつま先の骨を曲げて合わせようとして、上と下へ交互に曲がっていた。
    /// 前後のキーの点の組から、いちばん合う回転と移動（ねじ運動）を求め、その回転を時刻で進める。動かない点（回す軸の上の点）は動かず、
    /// ほかの点は弧を描く。剛体から外れる分（つま先を曲げたキーなど）は、時刻で混ぜて足す（キーのフレームではキーの値そのもの）
    /// </summary>
    static class RigidSpan
    {
        /// <summary>回したと見なす最小の角度（度）。これより小さければ移動だけ</summary>
        const float kMinAngle = 0.01f;

        /// <summary>
        /// from（前のキーの点）を to（次のキーの点）へ運ぶ剛体の動きを s（0..1）だけ進めた点に、剛体から外れる分を s で混ぜて足した所を result に入れる。
        /// 点が 3 つ未満か一直線に並んでいて回し方が決まらなければ false（直線で補間したままにする）
        /// </summary>
        public static bool Interpolate (IList<Vector3> from, IList<Vector3> to, float s, IList<Vector3> result)
        {
            return Interpolate (from, to, s, result, out Quaternion partial, out Quaternion rotation);
        }

        /// <summary>
        /// 上と同じ。partial に s だけ進めた回り、rotation に from から to への回り全体を返す（向きを持つ点の向きを、点の組と同じだけ回すため）
        /// </summary>
        public static bool Interpolate (IList<Vector3> from, IList<Vector3> to, float s, IList<Vector3> result, out Quaternion partial, out Quaternion rotation)
        {
            partial = Quaternion.identity;
            int count = from.Count;
            if (count < 3 || to.Count != count || result.Count != count) {
                rotation = Quaternion.identity;
                return false;
            }
            Vector3 fromCenter = Center (from);
            Vector3 toCenter = Center (to);
            if (!FitRotation (from, to, fromCenter, toCenter, out rotation)) return false;

            // 回す軸の上の点 c を求め、c のまわりに回しながら軸に沿って進む（ねじ運動）。軸に直交する移動 d⊥ は (I - R) c = d⊥ を満たす
            if (rotation.w < 0) rotation = new Quaternion (-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            float angle;
            Vector3 axis;
            rotation.ToAngleAxis (out angle, out axis);
            Vector3 move = toCenter - rotation * fromCenter;
            bool turns = angle >= kMinAngle && axis.sqrMagnitude > 0.5f;
            Vector3 along = turns ? Vector3.Dot (move, axis) * axis : move;
            Vector3 across = move - along;
            Vector3 pivot = turns ? 0.5f * (across + Vector3.Cross (axis, across) / Mathf.Tan (angle * 0.5f * Mathf.Deg2Rad)) : Vector3.zero;
            partial = turns ? Quaternion.AngleAxis (angle * s, axis) : Quaternion.identity;

            for (int i = 0; i < count; i++) {
                Vector3 moved = turns ? pivot + partial * (from[i] - pivot) + along * s : from[i] + move * s;
                Vector3 whole = rotation * from[i] + move;
                result[i] = moved + (to[i] - whole) * s;
            }
            return true;
        }

        /// <summary>
        /// 一緒に剛体として動かす組の基準の骨（点の乗る骨ごと）。基本は乗る骨そのもの。手足のいちばん先の骨（つま先など。下にほかの点の骨が無い）は、
        /// 親の骨にも点があれば親の組に入れる（つま先の先だけでは回し方が決まらず、足と別に直線で動くと母指球との間が縮む）。
        /// 一番先でない骨（脛など）は親とまとめない（膝と足首は別に曲がる）
        /// </summary>
        public static Transform[] Anchors (IList<Transform> bones)
        {
            HashSet<Transform> set = new HashSet<Transform> ();
            foreach (Transform bone in bones) {
                if (bone != null) set.Add (bone);
            }
            Transform[] anchors = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++) {
                Transform bone = bones[i];
                anchors[i] = bone;
                if (bone == null || bone.parent == null || !set.Contains (bone.parent)) continue;
                bool leaf = true;
                foreach (Transform other in set) {
                    if (other != bone && other.IsChildOf (bone)) leaf = false;
                }
                if (leaf) anchors[i] = bone.parent;
            }
            return anchors;
        }

        static Vector3 Center (IList<Vector3> points)
        {
            Vector3 sum = Vector3.zero;
            foreach (Vector3 p in points) sum += p;
            return sum / points.Count;
        }

        /// <summary>
        /// from を to へいちばん近く重ねる回転（重心まわり。Horn の四元数の方法: 4x4 の対称行列のいちばん大きい固有値の固有ベクトル）。
        /// いちばん大きい固有値が 2 つ並ぶ（点が一直線に並ぶなど、回し方が 1 つに決まらない）ときは false
        /// </summary>
        internal static bool FitRotation (IList<Vector3> from, IList<Vector3> to, Vector3 fromCenter, Vector3 toCenter, out Quaternion rotation)
        {
            double xx = 0, xy = 0, xz = 0, yx = 0, yy = 0, yz = 0, zx = 0, zy = 0, zz = 0;
            for (int i = 0; i < from.Count; i++) {
                Vector3 p = from[i] - fromCenter;
                Vector3 q = to[i] - toCenter;
                xx += p.x * q.x; xy += p.x * q.y; xz += p.x * q.z;
                yx += p.y * q.x; yy += p.y * q.y; yz += p.y * q.z;
                zx += p.z * q.x; zy += p.z * q.y; zz += p.z * q.z;
            }
            // 並びは (w, x, y, z)
            double[,] n = {
                { xx + yy + zz, yz - zy, zx - xz, xy - yx },
                { yz - zy, xx - yy - zz, xy + yx, zx + xz },
                { zx - xz, xy + yx, -xx + yy - zz, yz + zy },
                { xy - yx, zx + xz, yz + zy, -xx - yy + zz },
            };
            double[,] vectors;
            double[] values = SymmetricEigen (n, out vectors);
            int best = 0;
            for (int k = 1; k < 4; k++) {
                if (values[k] > values[best]) best = k;
            }
            double second = double.MinValue;
            for (int k = 0; k < 4; k++) {
                if (k != best && values[k] > second) second = values[k];
            }
            rotation = new Quaternion ((float)vectors[1, best], (float)vectors[2, best], (float)vectors[3, best], (float)vectors[0, best]);
            double scale = System.Math.Abs (values[best]);
            if (scale < 1e-12 || values[best] - second < 1e-4 * scale) {
                rotation = Quaternion.identity;
                return false;
            }
            rotation = CurveEdit.ToRotation (rotation.x, rotation.y, rotation.z, rotation.w);
            return true;
        }

        /// <summary>4x4 の対称行列の固有値と固有ベクトル（列）。Jacobi 法</summary>
        static double[] SymmetricEigen (double[,] source, out double[,] vectors)
        {
            const int size = 4;
            double[,] a = (double[,])source.Clone ();
            vectors = new double[size, size];
            for (int i = 0; i < size; i++) vectors[i, i] = 1;
            for (int sweep = 0; sweep < 50; sweep++) {
                double off = 0;
                for (int p = 0; p < size; p++) {
                    for (int q = p + 1; q < size; q++) off += System.Math.Abs (a[p, q]);
                }
                if (off < 1e-15) break;
                for (int p = 0; p < size; p++) {
                    for (int q = p + 1; q < size; q++) {
                        if (System.Math.Abs (a[p, q]) < 1e-18) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = (theta >= 0 ? 1 : -1) / (System.Math.Abs (theta) + System.Math.Sqrt (theta * theta + 1));
                        double c = 1 / System.Math.Sqrt (t * t + 1);
                        double s = t * c;
                        for (int k = 0; k < size; k++) {
                            double kp = a[k, p], kq = a[k, q];
                            a[k, p] = c * kp - s * kq;
                            a[k, q] = s * kp + c * kq;
                        }
                        for (int k = 0; k < size; k++) {
                            double pk = a[p, k], qk = a[q, k];
                            a[p, k] = c * pk - s * qk;
                            a[q, k] = s * pk + c * qk;
                        }
                        for (int k = 0; k < size; k++) {
                            double kp = vectors[k, p], kq = vectors[k, q];
                            vectors[k, p] = c * kp - s * kq;
                            vectors[k, q] = s * kp + c * kq;
                        }
                    }
                }
            }
            double[] values = new double[size];
            for (int i = 0; i < size; i++) values[i] = a[i, i];
            return values;
        }
    }

}
