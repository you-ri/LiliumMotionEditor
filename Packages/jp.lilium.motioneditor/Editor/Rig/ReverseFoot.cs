using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 足の転がし（Reverse Foot）。踵・母趾球・つま先の先・左右の縁を支点に、roll / bank / twist の角度で足を回す。
    ///
    /// 支点と軸は「IK の目標の枠」（足首の位置・向き。基準姿勢では足の骨の基準の向き）の中に持つ。位置は実寸（m）で、
    /// humanScale では割らない（足の長さは humanScale に比例しない）。目標の意味（足首）は変えないので、角度が 0 なら今までと同じ姿勢。
    ///
    /// 回し方（支点も軸もすべて回す前の枠に固定し、1 つの剛体の動きにまとめる。T = bank ∘ twist ∘ roll）:
    /// - roll &lt; 0: 踵まわり（つま先が上がる）
    /// - 0 &lt; roll ≤ 折れ角: 母趾球まわり（踵が上がる）。つま先はこの分だけ戻して床に残す
    /// - roll &gt; 折れ角: 折れ角まで母趾球まわり、残りはつま先の先まわり（つま先ごと回る）
    /// - twist: 母趾球まわりに体の上の軸で
    /// - bank: 体の前の軸で、下がる側の縁まわり
    /// </summary>
    public sealed class ReverseFoot
    {
        public const float kDefaultToeBreak = 30;

        public enum Pivot
        {
            Heel,
            Ball,
            ToeTip,
            LeftEdge,
            RightEdge,
        }

        public const int kPivotCount = 5;

        /// <summary>
        /// 転がした足。目標の枠の中で、足首が position へ動き rotation だけ回る。ballRotation はそのうち母趾球まわりの分（つま先を戻す分）
        /// </summary>
        public struct Pose
        {
            public Vector3 position;
            public Quaternion rotation;
            public Quaternion ballRotation;
        }

        // 足首を原点とする剛体の動き（x → r * x + p）
        struct Rigid
        {
            public Quaternion r;
            public Vector3 p;

            public static readonly Rigid identity = new Rigid { r = Quaternion.identity, p = Vector3.zero };

            public static Rigid About (Vector3 pivot, Quaternion q)
            {
                return new Rigid { r = q, p = pivot - q * pivot };
            }

            /// <summary>b の後に this</summary>
            public Rigid After (Rigid b)
            {
                return new Rigid { r = r * b.r, p = r * b.p + p };
            }
        }

        readonly Vector3[] pivots_ = new Vector3[kPivotCount];
        /// <summary>折れ角（度）。roll がこれを越えると、母趾球まわりをやめてつま先の先まわりに回る</summary>
        public float toeBreak = kDefaultToeBreak;
        Vector3 rollAxis_;
        Vector3 bankAxis_;
        Vector3 twistAxis_;

        /// <summary>
        /// 支点（目標の枠の中の位置。m）
        /// </summary>
        public Vector3 GetPivot (Pivot pivot)
        {
            return pivots_[(int)pivot];
        }

        public void SetPivot (Pivot pivot, Vector3 position)
        {
            pivots_[(int)pivot] = position;
        }

        /// <summary>体の上の向き（目標の枠の中。足元の面の法線）</summary>
        public Vector3 up
        {
            get { return twistAxis_; }
        }

        /// <summary>体の前の向き（目標の枠の中）</summary>
        public Vector3 forward
        {
            get { return bankAxis_; }
        }

        /// <summary>
        /// 基準姿勢から支点を見積もる。母趾球はつま先の骨（無ければ脛の長さの比）、足裏の高さは Avatar の値（無ければ足首の高さ）。
        /// 踵・つま先の先・左右の縁は、足首から母趾球までの長さの比で置く（足の形からの検出は後で上書きする）
        /// </summary>
        /// <param name="mid">膝（脛の長さを測る）</param>
        /// <param name="tip">足首（足の骨）</param>
        /// <param name="toes">つま先の骨（無ければ null）</param>
        /// <param name="chain">定義の IK の組（足の骨の種類で足裏の高さを左右で引く。折れ角と支点の上書きもここから）</param>
        public static ReverseFoot Create (EditingRig rig, Transform mid, Transform tip, Transform toes, EditRigDefinition.IkChain chain)
        {
            HumanBodyBones foot = chain.tip;
            Quaternion rest = rig.GetRestRootRotation (tip);
            Vector3 ankle = rig.GetRestRootPosition (tip);
            Vector3 up = rig.BodyToRoot (Vector3.up);
            Vector3 forward = rig.BodyToRoot (Vector3.forward);
            Vector3 right = rig.BodyToRoot (Vector3.right);
            Vector3 sole = ankle - up * FootBottom (rig, foot, ankle, up);
            float shin = Vector3.Distance (rig.GetRestRootPosition (mid), ankle);

            float reach = 0;
            Vector3 ball = Vector3.zero;
            if (toes != null) {
                ball = rig.GetRestRootPosition (toes);
                reach = Vector3.Dot (ball - ankle, forward);
            }
            if (toes == null || reach < shin * 0.05f) {
                reach = shin * 0.42f;
                ball = sole + forward * reach;
            }
            Vector3 middle = sole + forward * reach * 0.5f;
            float halfWidth = reach * 0.3f;

            ReverseFoot result = new ReverseFoot ();
            Quaternion inverse = Quaternion.Inverse (rest);
            result.pivots_[(int)Pivot.Heel] = inverse * (sole - forward * reach * 0.35f - ankle);
            result.pivots_[(int)Pivot.Ball] = inverse * (ball - ankle);
            result.pivots_[(int)Pivot.ToeTip] = inverse * (sole + forward * reach * 1.45f - ankle);
            result.pivots_[(int)Pivot.LeftEdge] = inverse * (middle - right * halfWidth - ankle);
            result.pivots_[(int)Pivot.RightEdge] = inverse * (middle + right * halfWidth - ankle);
            result.rollAxis_ = inverse * right;
            result.bankAxis_ = inverse * forward;
            result.twistAxis_ = inverse * up;

            // 定義の上書き（足首から見た体の向きで書いてある）
            result.toeBreak = chain.toeBreak;
            if (chain.pivots != null) {
                foreach (EditRigDefinition.FootPivot pivot in chain.pivots) {
                    if (pivot == null || pivot.pivot < 0 || (int)pivot.pivot >= kPivotCount) continue;
                    result.pivots_[(int)pivot.pivot] = inverse * rig.BodyToRoot (pivot.position);
                }
            }
            return result;
        }

        /// <summary>足首から足裏までの高さ（m）</summary>
        static float FootBottom (EditingRig rig, HumanBodyBones foot, Vector3 ankle, Vector3 up)
        {
            Animator animator = rig.displayAnimator;
            if (animator != null && animator.isHuman) {
                // Avatar の足裏の高さは実寸（キャラの Animator から見た長さ）。humanScale は掛けない（キャラ A で足首の高さと一致を確認）
                float height = foot == HumanBodyBones.RightFoot ? animator.rightFeetBottomHeight : animator.leftFeetBottomHeight;
                if (height > 1e-4f) return height;
            }
            return Mathf.Max (0, Vector3.Dot (ankle, up));
        }

        /// <summary>
        /// 角度（度）から転がした足を求める。どれも 0 なら false（動かない）
        /// </summary>
        public bool Evaluate (float roll, float bank, float twist, out Pose pose)
        {
            pose = new Pose { position = Vector3.zero, rotation = Quaternion.identity, ballRotation = Quaternion.identity };
            if (roll == 0 && bank == 0 && twist == 0) return false;

            Rigid t = Rigid.identity;
            if (roll < 0) {
                t = Rigid.About (pivots_[(int)Pivot.Heel], Quaternion.AngleAxis (roll, rollAxis_));
            }
            else if (roll > 0) {
                float ball = Mathf.Min (roll, Mathf.Max (0, toeBreak));
                pose.ballRotation = Quaternion.AngleAxis (ball, rollAxis_);
                t = Rigid.About (pivots_[(int)Pivot.Ball], pose.ballRotation);
                if (roll > ball) t = Rigid.About (pivots_[(int)Pivot.ToeTip], Quaternion.AngleAxis (roll - ball, rollAxis_)).After (t);
            }
            if (twist != 0) {
                t = Rigid.About (pivots_[(int)Pivot.Ball], Quaternion.AngleAxis (twist, twistAxis_)).After (t);
            }
            if (bank != 0) {
                Quaternion q = Quaternion.AngleAxis (bank, bankAxis_);
                // 右側が上がる向きなら左の縁、下がる向きなら右の縁が床に残る
                Pivot edge = Vector3.Dot (q * rollAxis_, twistAxis_) > 0 ? Pivot.LeftEdge : Pivot.RightEdge;
                t = Rigid.About (pivots_[(int)edge], q).After (t);
            }
            pose.position = t.p;
            pose.rotation = t.r;
            return true;
        }
    }

}
