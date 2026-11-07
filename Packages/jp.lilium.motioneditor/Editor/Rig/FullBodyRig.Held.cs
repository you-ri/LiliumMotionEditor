using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Lilium
{

    // 持っている物を骨の組みに入れる（S102。持つ・離す・差すが変わったら組み直し、あとはいつもの全身 IK で解く）
    public sealed partial class FullBodyRig
    {
        /// <summary>
        /// 骨の組みに入れる、持っている物 1 つ。物の骨を、持つ手の骨の子にする。手との関節は、物の原点を通る柄の軸のまわりにだけ回る
        /// （S89 の axisLock と同じ。物の原点は柄の軸の上にあるので、握りの点も軸の上で手に留まる）。
        /// 手から見た物の場所と向きは、解く前に置いた物の姿勢で決まる（HeldObjectRig.Prepare が、手の今の姿勢 × 握りへ置く）。
        /// 差している・差す場所へ運んでいる物（fixedPose）は、差す骨（parent）の子として固定し（weld の向きのまま回らない）、持っている手は全部輪の関節でつなぐ。
        /// 物は差す骨に付いて動き、手が物へ伸びる。差す骨を解かなければ、動かない剛体（根に置く）にする
        /// </summary>
        public struct HeldAttachment
        {
            /// <summary>物の骨（編集用の体の中の物）</summary>
            public Transform body;
            /// <summary>物を付ける骨（持つ手の骨・差す骨。解く骨でなければ組みに入らない）。fixedPose で null なら根に置く</summary>
            public Transform parent;
            /// <summary>柄の軸（物のローカル）。0 なら手に固定する</summary>
            public Vector3 axis;
            /// <summary>動かさない（差している・差す場所へ運んでいる）。差す骨（parent）に固定する。parent が null なら解く前に置いた所に留める</summary>
            public bool fixedPose;
            /// <summary>fixedPose で parent があるときの、parent から見た物の向き（ルート空間の向きで、parent⁻¹ × 物）</summary>
            public Quaternion weld;

            public bool Equals (HeldAttachment other)
            {
                return body == other.body && parent == other.parent && axis == other.axis && fixedPose == other.fixedPose && weld == other.weld;
            }
        }

        /// <summary>
        /// 両手で持つ物の、もう片方の手と物をつなぐ輪の関節 1 つ（S101 の FullBodyIkLoop）。握りを柄の軸へ落とした点を離さず、柄の軸まわりにだけ回る
        /// </summary>
        public struct HeldLoop
        {
            /// <summary>手首の点（手の骨とその上の点の場所を引く）</summary>
            public BodyPointId hand;
            /// <summary>物の骨</summary>
            public Transform body;
            /// <summary>握りの場所（物のローカル。柄に沿ってずらした後）</summary>
            public Vector3 grip;
            /// <summary>柄の軸（物のローカル）</summary>
            public Vector3 axis;
            /// <summary>手首の点の向き（コントロールの向き）から物の向きへの差: 物の向き = 手首の点の向き × toObject</summary>
            public Quaternion toObject;
        }

        /// <summary>
        /// 握りの柄の軸まわりの硬さ（骨の硬さと同じ意味。手の子にした物の関節）。1 なら反復の間は回らず、
        /// 必ず満たす物だけを詰める仕上げでだけ回る: 握りは柄の軸まわりに自由だが、要らなければ解く前の手首の回りのまま（S100「軸まわりは今の手首に一番近く」）。
        /// 0 にすると、腕が楽な所まで手が柄のまわりを回り込んだ（11-07 実測で、テストの人型で両手の物を持つと手が柄のまわりを 110° 回り、握りから 7〜8cm 外れた所を握った）。
        /// 0.9 にして仕上げにも掛けると、かえって 3.5mm 回った（11-07 実測）
        /// </summary>
        const float kGripStiffness = 1;
        /// <summary>
        /// 輪の関節でつないだ手（両手で持つ物の左手）の、柄の軸まわりの硬さ。輪の関節のばねは仕上げにも掛かる（掛けないと仕上げで手が柄のまわりを回り、3mm 外れた）。
        /// 必ず満たす物どうしで取り合わないよう 1 未満にする
        /// </summary>
        const float kLoopGripStiffness = 0.9f;

        /// <summary>
        /// 持った物の大きさ（メートル。体の大きさ 1 のとき）。重さと回しにくさはここから決まる。手と同じくらいにする（S101 の kChainExtent と同じ値）:
        /// 物の本当の大きさ（剣で 0.6m）にすると、物が手の 200 倍ほど回しにくく、輪の関節で向きをそろえる分がほとんど物へ行かない。
        /// 葉の骨のまま（握りから物の原点までの 3 割。数 cm）だと手よりずっと軽く、重い点を引くと物が先に回った
        /// </summary>
        const float kHeldExtent = 0.1f;

        readonly List<HeldAttachment> heldAttachments_ = new List<HeldAttachment> ();
        readonly List<HeldLoop> heldLoops_ = new List<HeldLoop> ();
        // 解く骨ごとの、持っている物の骨か（骨の階層では手の子でないので、場所も書き戻す）
        bool[] attached_ = new bool[0];

        /// <summary>
        /// 持ち物の姿勢の点（見えない点。S102）を足す。物の骨の原点に乗り、位置と向きを持つ重い点。値と強さは持ち物の仕組みが解く前に入れる。
        /// 点の一覧（pointIds）には出ない。同じ名前の点があれば何もしない
        /// </summary>
        /// <param name="control">点のコントロール（BodyPoint を持つ。localPosition はルート空間 / humanScale）</param>
        /// <param name="bone">物の骨（編集用の体の中の物）</param>
        public void AddHeldPosePoint (BodyPointId id, Transform control, Transform bone)
        {
            if (Find (id) != null || control == null || bone == null) return;
            BodyPoint values = control.GetComponent<BodyPoint> ();
            if (values == null) values = control.gameObject.AddComponent<BodyPoint> ();
            Quaternion restRotation = rig_.GetRestRootRotation (bone);
            points_.Add (new Point {
                id = id,
                offset = Vector3.zero,
                control = control,
                values = values,
                bone = bone,
                hasRotation = true,
                restRootRotation = restRotation,
                restRootPosition = rig_.root.transform.InverseTransformPoint (bone.position),
                depth = Depth (bone, rig_.root.transform),
                hidden = true,
            });
            RebuildBodies ();
        }

        /// <summary>
        /// 骨の組みに入れる持っている物を決める。前と同じなら何もしない。変われば、骨の並び・計算用の配列を作り直す（つかんでいる点は引き継ぐ）。組み直したら true
        /// </summary>
        public bool SetHeldAttachments (IReadOnlyList<HeldAttachment> attachments)
        {
            bool same = attachments.Count == heldAttachments_.Count;
            for (int i = 0; same && i < attachments.Count; i++) same = attachments[i].Equals (heldAttachments_[i]);
            if (same) return false;
            heldAttachments_.Clear ();
            heldAttachments_.AddRange (attachments);
            RebuildBodies ();
            return true;
        }

        /// <summary>両手で持つ物の、もう片方の手の輪の関節を決める（解くたびに。握りはキーで変わるので、組み直さずに入れ替える）</summary>
        public void SetHeldLoops (IReadOnlyList<HeldLoop> loops)
        {
            heldLoops_.Clear ();
            heldLoops_.AddRange (loops);
        }

        /// <summary>骨が今の組みで解く骨か（持っている物の骨も含む）</summary>
        public bool IsSolvedBody (Transform bone)
        {
            return bone != null && bodies_.Contains (bone);
        }

        /// <summary>持っている物の骨を、骨の組みへ入れたか（解く骨に、持つ手の骨が無ければ入らない）</summary>
        public bool IsHeldAttached (Transform body)
        {
            int index = body != null ? bodies_.IndexOf (body) : -1;
            return index >= 0 && attached_[index];
        }

        /// <summary>直前に解いた、輪の関節 l の残り（点の離れ m・軸の差 度）</summary>
        internal Vector2 GetHeldLoopResidual (int l)
        {
            if (!data_.isCreated || l < 0 || l >= data_.loopCount) return Vector2.zero;
            float2 residual = data_.loopResiduals[l];
            return new Vector2 (residual.x, residual.y * Mathf.Rad2Deg);
        }

        /// <summary>輪が閉じなかったので、両手で持った物のつかんだ点を輪より弱くして解き直している間か</summary>
        bool yieldToLoops_;

        /// <summary>直前に解いた輪の関節が、どれも閉じたか（点 1mm・軸 0.5° 以内）</summary>
        bool HeldLoopsClosed ()
        {
            for (int l = 0; l < data_.loopCount; l++) {
                float2 residual = data_.loopResiduals[l];
                if (residual.x > kReachTolerance * humanScale || residual.y > 0.5f * Mathf.Deg2Rad) return false;
            }
            return true;
        }

        /// <summary>解く骨 body が、今の解き方で輪の関節の物の側か（両手で持った物）</summary>
        bool OnHeldLoop (int body)
        {
            if (body < 0 || !data_.isCreated) return false;
            for (int l = 0; l < data_.loopCount; l++) {
                if (data_.loops[l].boneB == body) return true;
            }
            return false;
        }

        /// <summary>動かさない物（差している物）の骨を、動かさない骨にする（MarkFixedBones の後）</summary>
        void MarkFixedHeldBodies ()
        {
            for (int i = 0; i < bodies_.Count; i++) {
                if (attached_[i] && data_.bones[i].detached != 0) data_.fixedBones[i] = 1;
            }
        }

        /// <summary>骨の並び・計算用の配列を作り直す（点の乗る骨の番号も引き直す）</summary>
        void RebuildBodies ()
        {
            if (data_.isCreated) data_.Dispose ();
            data_ = default (FullBodyIkData);
            bodies_.Clear ();
            bodyParents_.Clear ();
            attached_ = new bool[0];
            if (rig_ == null || rig_.root == null) return;
            CreateBodies ();
        }

        /// <summary>持っている物の骨（解く骨の i 番目。CreateBodies が並びの最後に足した物）の関節を作る</summary>
        void AddHeldBody (int i)
        {
            HeldAttachment attachment = heldAttachments_.Find (a => a.body == bodies_[i]);
            attached_[i] = true;
            int parent = bodies_.IndexOf (attachment.parent);
            if (attachment.fixedPose && parent < 0) {
                // 動かない剛体（骨格に付いていない根。MarkFixedBones が動かさない骨にする）
                bodyParents_.Add (-1);
                data_.bones[i] = new FullBodyIkBone { parent = -1, detached = 1, extent = kHeldExtent * humanScale };
                return;
            }
            if (attachment.fixedPose) {
                // 差す骨に固定する: 角度制限を 0（遊びも 0）にする。制限の範囲 + 遊びは仕上げでも必ず守るので、物は差す骨から回らない
                bodyParents_.Add (parent);
                childCounts_[parent]++;
                data_.bones[i] = new FullBodyIkBone {
                    parent = parent,
                    stiffness = 1,
                    limit = 1,
                    limitFrame = quaternion.identity,
                    limitRest = Quaternion.Normalize (attachment.weld),
                    extent = kHeldExtent * humanScale,
                };
                return;
            }
            bodyParents_.Add (parent);
            childCounts_[parent]++;
            bool axis = attachment.axis.sqrMagnitude > kEpsilon;
            // 柄の軸まわりだけ回る（角度制限なし。硬さは kGripStiffness）。軸が無ければ手に固定する
            data_.bones[i] = new FullBodyIkBone {
                parent = parent,
                stiffness = axis ? kGripStiffness : 1,
                axisLock = axis ? 1 : 0,
                lockAxis = axis ? attachment.axis.normalized : Vector3.zero,
                extent = kHeldExtent * humanScale,
            };
        }

        /// <summary>両手で持つ物の輪の関節を、計算用のデータへ入れる（解く前）</summary>
        void LoadHeldLoops ()
        {
            int loops = 0;
            foreach (HeldLoop loop in heldLoops_) {
                Point point = Find (loop.hand);
                int hand = point != null ? point.body : -1;
                int body = bodies_.IndexOf (loop.body);
                if (hand < 0 || body < 0 || loops >= data_.loops.Length) continue;
                Vector3 axis = loop.axis.sqrMagnitude > 1e-8f ? loop.axis.normalized : Vector3.up;
                Vector3 onAxis = axis * Vector3.Dot (loop.grip, axis);
                // 握ったときの手の骨の向き q は、物の向き R から q = R × toObject⁻¹ × 点の基準の向き。手の骨の枠で見た柄の軸と、軸の上の握りの点
                Quaternion handFromObject = Quaternion.Inverse (point.restRootRotation) * loop.toObject;
                data_.loops[loops++] = new FullBodyIkLoop {
                    boneA = hand,
                    anchorA = point.offset - handFromObject * (loop.grip - onAxis),
                    axisA = handFromObject * axis,
                    boneB = body,
                    anchorB = onAxis,
                    axisB = axis,
                    stiffness = kLoopGripStiffness,
                    // 柄の軸まわりの硬さの基準は握りの向き（手の骨の向き⁻¹ × 物の向き。手を握りに合わせたとき）
                    rest = Quaternion.Normalize (handFromObject),
                };
            }
            data_.loopCount = loops;
        }
    }

}
