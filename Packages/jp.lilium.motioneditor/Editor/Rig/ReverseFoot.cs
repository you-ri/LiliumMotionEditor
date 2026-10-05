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
            /// <summary>母趾球の列の足裏の中心（母趾球の真下）。転がしでは使わず、全身 IK の点の置き場所にする</summary>
            SoleBall,
            /// <summary>母趾球の列の小指側の縁（足裏）。転がしでは使わず、全身 IK の点の置き場所にする</summary>
            LittleToeSide,
        }

        public const int kPivotCount = 7;

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
        /// 踵・つま先の先・左右の縁は、足首から母趾球までの長さの比で、足の向き（足首から母趾球へ。つま先の骨が無ければ体の前）に並べる
        /// （足の形からの検出は後で上書きする。回す軸は体の向きのまま）。
        /// 足裏の中心と小指側の縁（全身 IK の点）は、母趾球の真下の足裏と、そこから小指側へ開いた所。
        /// 足の幅がいちばん広い所なので、縁は左右の縁より外に置く
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

            // 支点は足の向き（足首から母趾球へ。地面に沿わせる）に並べる。体の前に並べると、つま先が外を向いたキャラで
            // つま先の先・踵が足の中心線から外れる（足裏の中心を支点に回したときに、足の真ん中で回らない）
            float reach = 0;
            Vector3 ball = Vector3.zero;
            Vector3 along = forward;
            if (toes != null) {
                ball = rig.GetRestRootPosition (toes);
                Vector3 flat = Vector3.ProjectOnPlane (ball - ankle, up);
                if (Vector3.Dot (flat, forward) > 1e-6f) along = flat.normalized;
                reach = Vector3.Dot (ball - ankle, along);
            }
            if (toes == null || reach < shin * 0.05f) {
                along = forward;
                reach = shin * 0.42f;
                ball = sole + forward * reach;
            }
            // 足の右（Unity は左手系なので、上 × 前 が右）
            Vector3 side = Vector3.Cross (up, along).normalized;
            Vector3 middle = sole + along * reach * 0.5f;
            float halfWidth = reach * 0.3f;
            Vector3 ballSole = ball - up * Vector3.Dot (ball - sole, up);
            // 外側（小指側）は体の外の向き。左足なら左
            Vector3 outside = foot == HumanBodyBones.LeftFoot ? -side : side;

            ReverseFoot result = new ReverseFoot ();
            Quaternion inverse = Quaternion.Inverse (rest);
            // 踵は足首の真下から少しだけ後ろ。踵を支点に足を回すこともあるので、足首から離しすぎない（離すと回したときに足首が大きく振れる）
            result.pivots_[(int)Pivot.Heel] = inverse * (sole - along * reach * 0.15f - ankle);
            result.pivots_[(int)Pivot.Ball] = inverse * (ball - ankle);
            result.pivots_[(int)Pivot.ToeTip] = inverse * (sole + along * reach * 1.45f - ankle);
            result.pivots_[(int)Pivot.LeftEdge] = inverse * (middle - side * halfWidth - ankle);
            result.pivots_[(int)Pivot.RightEdge] = inverse * (middle + side * halfWidth - ankle);
            result.pivots_[(int)Pivot.SoleBall] = inverse * (ballSole - ankle);
            result.pivots_[(int)Pivot.LittleToeSide] = inverse * (ballSole + outside * reach * 0.35f - ankle);
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
