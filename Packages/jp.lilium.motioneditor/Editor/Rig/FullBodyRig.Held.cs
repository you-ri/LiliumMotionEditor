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
            /// <summary>
            /// 回る軸（物のローカル。EditRigDefinition.AttachJoint.Hinge の先だけ。0 なら Hinge でない）。物は parent の子として、この軸まわりに ±hingeRange 度だけ回る
            /// </summary>
            public Vector3 hingeAxis;
            /// <summary>
            /// 回る中心（物のローカル。Hinge の先だけ）。全身 IK では、物の骨の付け根をここに置く（木の関節は骨の付け根まわりにしか回らず、
            /// 物の原点は回る中心とは限らない）。物の上の点の場所も、ここから測り直す
            /// </summary>
            public Vector3 origin;
            /// <summary>回る範囲（度。片側）</summary>
            public float hingeRange;
            /// <summary>回りの基準（範囲の真ん中）: 付けた位置での、parent から見た物の向き（ルート空間の向きで、parent⁻¹ × 物）</summary>
            public Quaternion hingeRest;

            public bool Equals (HeldAttachment other)
            {
                return body == other.body && parent == other.parent && axis == other.axis && fixedPose == other.fixedPose && weld == other.weld
                    && hingeAxis == other.hingeAxis && origin == other.origin && hingeRange == other.hingeRange && hingeRest == other.hingeRest;
            }
        }

        /// <summary>
        /// 何か所にも付けた物の、親でない付ける先の骨と物をつなぐ輪の関節 1 つ（S101 の FullBodyIkLoop。S104 で手に限らなくした）。
        /// 付けた位置を柄の軸へ落とした点を離さず、柄の軸まわりにだけ回る
        /// </summary>
        public struct HeldLoop
        {
            /// <summary>付ける先の骨</summary>
            public Transform bone;
            /// <summary>骨の上の枠の場所（骨の付け根から。基準姿勢の骨の枠・メートル。点の offset と同じ）</summary>
            public Vector3 offset;
            /// <summary>骨の基準姿勢の向き（ルート空間）</summary>
            public Quaternion restRotation;
            /// <summary>物の骨</summary>
            public Transform body;
            /// <summary>枠の場所（物のローカル）</summary>
            public Vector3 grip;
            /// <summary>回る軸（物のローカル。柄の軸か、Hinge の先の回る軸）</summary>
            public Vector3 axis;
            /// <summary>回る中心（物のローカル。axis の上）</summary>
            public Vector3 pivot;
            /// <summary>0 より大きければ、axis まわりの回りを ±range 度に収める（Hinge の先）</summary>
            public float range;
            /// <summary>骨の基準姿勢からの回り（ルート空間）から物の向きへの差: 物の向き = 骨の回り × toObject</summary>
            public Quaternion toObject;
            /// <summary>骨が届かなくても物を骨の方へ寄せない（物を決まった位置へ寄せている間。FullBodyIkLoop.holdB）</summary>
            public bool holdObject;
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
        /// Hinge の先に付けた物の、回る軸まわりの硬さ。手首（硬さ 0.2 前後）より硬いばねにして、握りの中の回りは手首で向けきれないときだけ使う
        /// （11-09 ユーザー決定「要るときだけ」）。0 にすると、手首の自由な手で物を 30° 回したとき、握りの中で 30° 回った（11-10 実測）
        /// </summary>
        const float kHingeStiffness = 0.9f;

        /// <summary>
        /// 持った物の大きさ（メートル。体の大きさ 1 のとき）。重さと回しにくさはここから決まる。手と同じくらいにする（S101 の kChainExtent と同じ値）:
        /// 物の本当の大きさ（剣で 0.6m）にすると、物が手の 200 倍ほど回しにくく、輪の関節で向きをそろえる分がほとんど物へ行かない。
        /// 葉の骨のまま（握りから物の原点までの 3 割。数 cm）だと手よりずっと軽く、重い点を引くと物が先に回った
        /// </summary>
        const float kHeldExtent = 0.1f;

        /// <summary>付けた物 1 つあたりの、輪の関節の数の上限（親でない付ける先の数。これを超える先は輪でつながない）</summary>
        const int kHeldLoopsPerObject = 8;

        readonly List<HeldAttachment> heldAttachments_ = new List<HeldAttachment> ();
        readonly List<HeldLoop> heldLoops_ = new List<HeldLoop> ();
        // 解く骨ごとの、持っている物の骨か（骨の階層では手の子でないので、場所も書き戻す）
        bool[] attached_ = new bool[0];
        // 解く骨ごとの、骨の付け根の骨の原点からのずれ（骨の枠・メートル。Hinge の先に付けた物の回る中心。ほかは 0）
        Vector3[] origins_ = new Vector3[0];

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
                boneOffset = Vector3.zero,
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
            origins_ = new Vector3[0];
            if (rig_ == null || rig_.root == null) return;
            CreateBodies ();
        }

        /// <summary>持っている物の骨（解く骨の i 番目。CreateBodies が並びの最後に足した物）の関節を作る</summary>
        void AddHeldBody (int i)
        {
            attached_[i] = true;
            HeldAttachment attachment = heldAttachments_.Find (a => a.body == bodies_[i]);
            int parent = bodies_.IndexOf (attachment.parent);
            if (!attachment.fixedPose && parent >= 0 && attachment.hingeAxis.sqrMagnitude > kEpsilon) {
                AddHingeBody (i, attachment);
                return;
            }
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

        /// <summary>
        /// Hinge の先に付けた物の関節: 付ける骨の子として、回る中心（物の骨の付け根）を通る回る軸まわりにだけ（axisLock）、±hingeRange 度だけ（角度制限。遊びなし）回る。
        /// 硬さは kHingeStiffness（手首で向けきれないときだけ回る）
        /// </summary>
        void AddHingeBody (int i, HeldAttachment attachment)
        {
            int parent = bodies_.IndexOf (attachment.parent);
            bodyParents_.Add (parent);
            childCounts_[parent]++;
            Vector3 axis = attachment.hingeAxis.sqrMagnitude > kEpsilon ? attachment.hingeAxis.normalized : Vector3.up;
            Quaternion rest = Quaternion.Normalize (attachment.hingeRest);
            float range = Mathf.Max (0, attachment.hingeRange) * Mathf.Deg2Rad;
            data_.bones[i] = new FullBodyIkBone {
                parent = parent,
                stiffness = kHingeStiffness,
                axisLock = 1,
                lockAxis = axis,
                limit = 1,
                // 制限の枠の x（ひねりの軸）を、付ける骨の枠で見た回る軸にする。振り（y・z）は axisLock で回らないので、制限では自由にする。
                // 振りを範囲 0 にすると、いつも止めに当たった関節になり、動いていない軸をキーの値に留める決まり（SolveLimit）で、回る軸まわりも動き出せなかった
                limitFrame = Quaternion.FromToRotation (Vector3.right, rest * axis),
                limitRest = rest,
                limitMin = new float3 (-range, -kFreeLimit, -kFreeLimit),
                limitMax = new float3 (range, kFreeLimit, kFreeLimit),
                extent = kHeldExtent * humanScale,
            };
        }

        /// <summary>両手で持つ物の輪の関節を、計算用のデータの first 番目から入れる（解く前。LoadLoops から）。入れ終わった数を返す</summary>
        int LoadHeldLoops (int first)
        {
            int loops = first;
            foreach (HeldLoop loop in heldLoops_) {
                int hand = loop.bone != null ? bodies_.IndexOf (loop.bone) : -1;
                int body = bodies_.IndexOf (loop.body);
                if (hand < 0 || body < 0 || loops >= data_.loops.Length) continue;
                Vector3 axis = loop.axis.sqrMagnitude > 1e-8f ? loop.axis.normalized : Vector3.up;
                // 握ったときの手の骨の向き q は、物の向き R から q = R × toObject⁻¹ × 点の基準の向き。手の骨の枠で見た回る軸と、軸の上の回る中心
                Quaternion handFromObject = Quaternion.Inverse (loop.restRotation) * loop.toObject;
                float range = loop.range * Mathf.Deg2Rad;
                data_.loops[loops++] = new FullBodyIkLoop {
                    boneA = hand,
                    anchorA = loop.offset - handFromObject * (loop.grip - loop.pivot),
                    axisA = handFromObject * axis,
                    boneB = body,
                    // 物の骨の付け根をずらしていれば（Hinge の先を親にした物）、ずらした付け根から測る
                    anchorB = loop.pivot - origins_[body],
                    axisB = axis,
                    twistLimit = loop.range > 0 ? 1 : 0,
                    twistMin = -range,
                    twistMax = range,
                    stiffness = kLoopGripStiffness,
                    // 柄の軸まわりの硬さの基準は握りの向き（手の骨の向き⁻¹ × 物の向き。手を握りに合わせたとき）
                    rest = Quaternion.Normalize (handFromObject),
                    holdB = loop.holdObject ? 1 : 0,
                };
            }
            return loops;
        }
    }

}
