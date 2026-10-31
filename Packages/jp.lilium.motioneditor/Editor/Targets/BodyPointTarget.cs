using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の点（S25）。状態は丸の色で表す（選び方は丸の外の輪の色で表す）。関節の上の丸をつかんで動かすと、全身が付いてくる。
    ///
    /// 点の状態は 3 つ。状態は、次にその点のキーを打つまで先のフレームへ引き継ぐ。
    /// 引き継ぐのは状態だけで、場所は引き継がない（キーの無いフレームでは、そのフレームの骨の位置に置く）:
    /// - Free（青）: 体に付いて動く
    /// - Pin（オレンジ）: 動かした点。動かした場所に留まる。手を離すと、実際の骨の位置へ合わせ直す（体が届かない所へ引いても、点が体の形から離れない）
    /// - Locked（赤）: 置いた場所から動かさない。丸を右ボタンで付け外しする。いくつか選んでいるときは、選んでいる点をまとめて付け外しする
    ///
    /// 点はリグ定義の点の一覧（骨の上の好きな場所）から作る。点の種類（関節・顔の前・かかとなど）で扱いは変わらない（S26）:
    /// 動かすと、その点が届くように骨ごと引かれる。同じ骨の上の点どうしは剛体として拘束しあう（2 点で骨が置かれ、3 点で向きまで決まる）。
    /// 点を 1 つ選んでギズモで回すと、その点を中心に骨を FK で回す（子の点は骨に付いて回る。固定している子の点は、置いた場所も同じだけ回す。
    /// 骨の付け根から離れた点は、その場に留めて骨の付け根を全身 IK で動かす）。
    /// いくつか選んでいるときは、その後に押した 1 つ（中心の点）にギズモを出し、選んでいる点だけを中心の点のまわりで回す・動かす。
    /// 向きを持つ点は、回すと向きも回る。
    /// 点どうしは線でつながる（丸との間に隙間を空ける）。
    /// 手を離すと、解いた姿勢が骨のキーとして今のフレームに打たれる（キーの間は骨の値の補間で動く）。
    /// 点のキーは、動かした点・固定した点にだけ打つ（キーフレームへ戻ったときに、同じ点から直せる）
    /// </summary>
    public enum BodyPointState
    {
        /// <summary>体に付いて動く（青）</summary>
        Free,
        /// <summary>動かした場所に留まる。手を離すと、実際の骨の位置へ合わせ直す（オレンジ）</summary>
        Pin,
        /// <summary>置いた場所から動かさない（赤）</summary>
        Locked,
    }

    public sealed class BodyPointTarget : PoseTarget
    {
        /// <summary>
        /// 点どうしをつなぐ線の太さ（ピクセル）と、丸との間に空ける隙間（丸の中心から。ハンドルの大きさの何倍か）。
        /// リグエディタの骨の線も同じ見た目で描く（RigEditorWindow）
        /// </summary>
        internal const float kLinkWidth = 4.5f;
        internal const float kLinkGap = 1.7f;
        internal static readonly Color kLinkColor = new Color (0.8f, 0.85f, 0.9f, 0.55f);
        /// <summary>線を押す当たりで、丸より後に当たるよう足す距離（ピクセル）。線の端と丸が近いとき、丸を優先する</summary>
        internal const float kLinkPickBias = 2;
        /// <summary>マウスが乗っている線の太さ（線の太さの何倍か）</summary>
        internal const float kLinkHighlightWidth = 1.8f;
        /// <summary>点をつかまない間に出す、固定した点の印の大きさ（ハンドルの大きさの何倍か）</summary>
        const float kMarkScale = 0.6f;
        /// <summary>
        /// 重さ 0 の点（向けるだけの点）の丸の大きさ（引く点の何倍か）。ほかの骨を引かない点だと見て分かるよう小さくする。
        /// リグエディタの点の丸も同じ割合で描く
        /// </summary>
        internal const float kAimScale = 0.5f;
        static readonly Color kPinnedColor = new Color (1f, 0.55f, 0.1f, 0.8f);
        static readonly Color kFreeColor = new Color (0.2f, 0.8f, 1f, 0.35f);
        static readonly Color kLockedColor = new Color (1f, 0.2f, 0.15f, 0.85f);
        /// <summary>動かせない点（定義の passive。S89）の丸の色。状態を持たないので、状態の色と見分けられる灰色。リグエディタも同じ色で描く</summary>
        internal static readonly Color kPassiveColor = new Color (0.6f, 0.6f, 0.6f, 0.6f);
        /// <summary>持ち物を持っている手の丸の色（S91。状態を持たない）</summary>
        static readonly Color kHeldColor = new Color (1f, 0.75f, 0.2f, 0.85f);
        /// <summary>
        /// 丸の外に出す輪。丸との隙間（ハンドルの大きさの何倍か）と線の太さ（ピクセル）。
        /// 色で選び方を表す（丸の色は状態のためにとってある）: 選ぼうとしている（マウスが乗っている・枠に入っている）/ 選んで動かせる
        /// </summary>
        const float kRingGap = 0.4f;
        const float kRingWidth = 2.5f;
        internal static readonly Color kRingHoverColor = new Color (1f, 1f, 1f, 0.7f);
        internal static readonly Color kRingSelectedColor = new Color (0.3f, 1f, 0.45f, 1f);
        /// <summary>床との当たりの球（定義の radius）の輪の色。リグエディタも同じ色で描く</summary>
        internal static readonly Color kCollisionColor = new Color (0.55f, 0.9f, 1f, 0.45f);

        readonly EditingRig rig_;
        readonly FullBodyRig body_;
        readonly BoneShapes shapes_;
        readonly BodyPointId human_;
        /// <summary>線でつなぐ相手の点（無ければ null。腰など）</summary>
        BodyPointTarget link_;
        /// <summary>定義で指した、線でつなぐ相手の点の名前（空なら骨の階層から決める）</summary>
        readonly string linkName_;
        /// <summary>関節の点でないか（骨の付け根から離れた、骨の上の点。骨から線でつないで描く）</summary>
        readonly bool offsetPoint_;
        /// <summary>引く点か（重さ 1。向けるだけの点でない）</summary>
        readonly bool pulls_;
        /// <summary>動かせない点か（定義の passive。S89）。選べるが、値を変える操作はどれも何もしない</summary>
        readonly bool passive_;
        readonly HumanBodyBones humanBone_;
        readonly Transform control_;
        readonly Transform bone_;
        readonly Transform displayBone_;
        readonly bool hasRotation_;
        readonly int moveHash_;
        readonly int linkHash_;
        bool linkClicked_;
        /// <summary>動かしたがまだキーを打っていない値（位置と位置の固定の強さ）</summary>
        bool positionDirty_;
        /// <summary>動かしたがまだキーを打っていない値（向きと向きの固定の強さ）</summary>
        bool rotationDirty_;
        /// <summary>完全固定を切り替えたがまだキーを打っていない</summary>
        bool lockedDirty_;
        bool contextClicked_;

        /// <summary>持ち物を持つ手か（S91。手首の点だけ。持っていないフレームではふつうの点）</summary>
        public HeldObjectRig.Hand holder;

        /// <summary>
        /// 今のフレームで持ち物を持っている手か。値は物の姿勢と握りから作るので、キーを打たず読まず、固定の付け外しもできない。
        /// つかむと物を動かす
        /// </summary>
        public bool held
        {
            get { return holder != null && holder.held; }
        }

        /// <summary>乗っている持ち物（S91e。物の上の点だけ）。扱いはほかの点と同じで、乗る骨の代わりに物へ乗る</summary>
        public HeldObjectRig.Entry heldObject;

        /// <summary>持っている手の点・物の上の点を回すと、その点を中心に物を回す</summary>
        public void TurnHeldObject (Quaternion delta)
        {
            if (held) holder.owner.TurnAboutHand (holder, delta);
            else if (heldObject != null) heldObject.TurnAbout (framePosition, delta);
        }

        /// <summary>選んだときに移動 / 回転のギズモを出すか（いくつか選んでいる間は、中心の点にだけ出す。窓が描く前に入れる）</summary>
        public bool showGizmo = true;

        /// <summary>引いている選択の枠に入っているか（窓が描く前に入れる。離すと選ばれる点に、選ぼうとしている輪を出す）</summary>
        public bool inBox;

        Vector3 dragDelta_;
        /// <summary>動かした量をためはじめる前の点の位置（ワールド）</summary>
        Vector3 dragFrom_;

        /// <summary>
        /// 丸をつかんで動かした量（ワールド。読むと消える）と、動かす前の点の位置。いくつか選んでいるとき、窓がほかの選んでいる点を同じだけ動かす。
        /// 動かす前の位置は、Floor Clamp を選んだ点全体で掛け直すときに使う（この点は自分だけで床に当てて置いてある）
        /// </summary>
        public Vector3 ConsumeDragDelta (out Vector3 from)
        {
            Vector3 delta = dragDelta_;
            from = dragFrom_;
            dragDelta_ = Vector3.zero;
            return delta;
        }

        /// <summary>点を delta（ワールド）だけ動かす。Free の点は、今の骨の位置から動かして Pin にする</summary>
        public void MoveBy (Vector3 delta)
        {
            ReleaseSameBoneRotations ();
            PinPosition (MoveFoot (framePosition + delta));
        }

        /// <summary>点を position（ワールド）へ置いて Pin にする</summary>
        public void MoveTo (Vector3 position)
        {
            ReleaseSameBoneRotations ();
            PinPosition (MoveFoot (position));
        }

        /// <summary>
        /// この点だけを直接動かすとき（丸・ギズモでつかむ・Spinner）、同じ骨の上の別の場所にある点の向きの固定を外す（外した値は次のキーで強さ 0 を打つ）。
        /// 向きも固定した点があると、骨は位置も向きも動かない物になり、同じ骨のほかの点を動かしても骨が回らない
        /// （頭の点が向きを固定したキーで、顔の前の点を動かしても頭が一切向かなかった。10-25）。
        /// 動かした点と向きを固定した点の 2 点で、骨の向きを決め直す。Locked の点は置いた場所と向きから動かさないので外さない。
        /// まとめて動かす・回す・骨と一緒に動かすときは外さない（選んだ点どうしの向きの固定が、並びの順で外れてしまう）
        /// </summary>
        void ReleaseSameBoneRotations ()
        {
            if (sameBone_ == null) return;
            Vector3 self = bonePoint;
            foreach (BodyPointTarget other in sameBone_) {
                if (!other.rotationPinned || other.locked) continue;
                // 同じ場所の点なら、動かしても向きと食い違わない
                if ((other.bonePoint - self).sqrMagnitude <= 1e-10f) continue;
                other.ReleaseRotation ();
            }
        }

        /// <summary>同じ骨の上のほかの点（無ければ null）</summary>
        List<BodyPointTarget> sameBone_;

        /// <summary>
        /// いくつか選んだ点をまとめて動かすとき（PreviewWindow.MoveSelectedPoints）に、点を position（ワールド）へそのまま置いて Pin にする。
        /// 床に当てるのは窓が選んだ点のかたまりで済ませてあるので、点ごとには当てず、足の箱（FullBodyRig.TryMoveFoot）も使わない（10-24 不具合
        /// 「全部選んで平行移動すると姿勢が崩れる」。足の箱は同じ足のほかの Locked の点を支点にするので、Locked の足の点を 1 つずつ通すと足が回り、
        /// 床の下限より下にある足裏の点は 1 つずつ持ち上がって足がゆがんだ）
        /// </summary>
        public void PlaceWithSelection (Vector3 position)
        {
            PinPosition (position, false);
        }

        /// <summary>
        /// いくつか選んでいて、つかむとまとめて動かす間か（窓が描く前に入れる）。丸・ギズモでつかんだときも点ごとには床に当てず、足の箱も使わない（PlaceWithSelection）
        /// </summary>
        public bool movesWithSelection;

        /// <summary>
        /// Floor Clamp で足の点を動かすとき、同じ足のほかの Pin の点を足と一緒に動かす（FullBodyRig.CarryFootPins）。
        /// 動かすとき（つかむ・ギズモ・Spinner・まとめて動かす）だけ呼ぶ。固定の付け外しでは呼ばない（ほかの Pin の点が外れて足が動く）
        /// </summary>
        void CarryFoot ()
        {
            if (floorClamp) body_.CarryFootPins (human_);
        }

        /// <summary>
        /// 動かすとき（つかむ・ギズモ・Spinner・まとめて動かす）の置き場所。Floor Clamp で足の点を動かすときは、足を硬い箱として床に押し当てたように動かし
        /// （FullBodyRig.TryMoveFoot。当たった所は滑らず、そこを支点に転がる）、点の置き場所を返す。同じ足のほかの Pin の点は足と一緒に動かす
        /// </summary>
        Vector3 MoveFoot (Vector3 position)
        {
            CarryFoot ();
            Vector3 placed;
            return floorClamp && body_.TryMoveFoot (human_, position, out placed) ? placed : position;
        }

        /// <summary>丸・ギズモで動かした量をためる。ためはじめなら、動かす前の位置を控える</summary>
        void AddDragDelta (Vector3 from, Vector3 to)
        {
            if (dragDelta_ == Vector3.zero) dragFrom_ = from;
            dragDelta_ += to - from;
        }

        Quaternion rotateDelta_ = Quaternion.identity;

        /// <summary>
        /// 回転のギズモで回した量（ワールド。前から掛ける差。読むと消える）。点は自分では回さず、窓が回し方を決める
        /// （1 つだけ選んでいれば骨を FK で回す。その骨より先の固定している点も一緒に回る。いくつか選んでいれば選んだ点だけをこの点を中心に）
        /// </summary>
        public Quaternion ConsumeRotateDelta ()
        {
            Quaternion delta = rotateDelta_;
            rotateDelta_ = Quaternion.identity;
            return delta;
        }

        /// <summary>
        /// 点を pivot を中心に delta（ワールド）だけ回す。向きを持つ点は向きも回す。
        /// pinFree なら Free の点も、回した場所に置いて Pin にする。そうでなければ、固定している値だけ回す（Free の点は骨に付いて動く）。
        /// pinFreeRotation が false なら、pinFree でも向きは固定していた点だけ回す（Free の向きは骨に付いて動く）
        /// </summary>
        public void RotateAround (Vector3 pivot, Quaternion delta, bool pinFree, bool pinFreeRotation = true)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PlaceRotated (position, rotation, pivot, pivot, delta, pinFree, pinFree && pinFreeRotation);
        }

        /// <summary>
        /// 点の回し方の芯（回す・体ごと回す・骨に付けて動かすは、どこから回すかが違うだけ）:
        /// position・rotation を from を中心に delta だけ回し、from を to へ重ねた所に置く。
        /// pinFree なら Free の位置も Pin にし、pinFreeRotation なら固定していない向きも固定する。そうでなければ、固定している値だけ置き直す
        /// </summary>
        void PlaceRotated (Vector3 position, Quaternion rotation, Vector3 from, Vector3 to, Quaternion delta, bool pinFree, bool pinFreeRotation)
        {
            if (pinFree || positionPinned) PinPosition (to + delta * (position - from));
            if (hasRotation_ && (pinFreeRotation || rotationPinned)) PinRotation (delta * rotation);
        }

        /// <summary>乗る骨の付け根にある点か（付け根から離れた所に置いた点でない）</summary>
        public bool isOnBoneOrigin
        {
            get { return !offsetPoint_; }
        }

        /// <summary>
        /// 体ごと回す: 今の骨の上の点（目標ではない）を、pivotOnBone を pivot に重ねて、pivot を中心に delta だけ回した場所に置く。向きを持つ点は向きも今の骨から回す。
        /// 目標から回すと、届いていない点（同じ足の Pin と Locked が食い違っているなど）の食い違いが回した後も残り、Locked の点が中心からずれていく。
        /// 骨の上の点から回すと、回した点どうしは体の上で矛盾しない（剛体として回る）。
        /// pinFree なら Free の点も Pin にする。そうでなければ、固定している値だけ置き直す。
        /// 向きは、固定していた点だけ回す（Free の向きは固定しない）。体ごと回すときは骨も FK で回すので、向きを固定しなくても点の向きは骨と一緒に回る。
        /// 固定すると、向きを持つ点が全部その向きで留まり、後で同じ骨のほかの点を動かしても骨が回らなかった（10-25）
        /// </summary>
        public void RotateBodyAround (Vector3 pivot, Vector3 pivotOnBone, Quaternion delta, bool pinFree)
        {
            PlaceRotated (bonePoint, bone_.rotation, pivotOnBone, pivot, delta, pinFree, false);
        }

        /// <summary>点が付いている骨の上の点の今の位置（目標ではない）</summary>
        public Vector3 bonePosition
        {
            get { return bonePoint; }
        }

        /// <summary>点を動かす編集用の骨</summary>
        public Transform editingBone
        {
            get { return bone_; }
        }

        /// <summary>
        /// 動かした点を、床（ワールドの Y = 0）より下へ置かない（Controller の Floor Clamp）。
        /// 操作のときだけのフラグで、窓が配る（点ごとには持たず、クリップにも保存しない）
        /// </summary>
        public bool floorClamp { get; set; }

        /// <summary>
        /// 床より下へ動かすとき、Y だけ床の高さにする（横の動きはそのまま効き、点は床の上を滑る。10-08 ユーザー指示で、床に当たった所で止めるのをやめた）。
        /// 床との当たりの球（定義の radius）を持つ点は、球の下の端が床に着く高さ（半径）で止める（10-22）。
        /// 足の点は、足をかたまりのまま下げて、どれかの足裏の点の球が床に着く高さで止める（FullBodyRig.GetFloorLimit）
        /// </summary>
        Vector3 ClampToFloor (Vector3 position)
        {
            // 足の箱で動かしている点は、箱が床との当たりを受け持つ（FullBodyRig.TryMoveFoot）
            if (!floorClamp || body_.IsFootBoxed (human_)) return position;
            float limit = body_.GetFloorLimit (human_);
            if (position.y < limit) position.y = limit;
            return position;
        }

        /// <summary>床との当たりの球の半径（メートル。定義の radius。無ければ 0）</summary>
        public float floorRadius
        {
            get { return body_.GetRadius (human_); }
        }

        /// <summary>Floor Clamp で止める、点の高さの下限（足の点は、足をかたまりのまま下げて足裏の球が床に着く高さ。FullBodyRig.GetFloorLimit）</summary>
        public float floorLimit
        {
            get { return body_.GetFloorLimit (human_); }
        }

        /// <summary>
        /// 線（つなぐ相手の点までの線）に当たる骨。線を押すと、窓がこの骨の FK を選ぶ。
        /// 骨の上の点（踵・膝の Pole など）はその点の骨、関節の点は相手の点の骨（肘の点 → 肩の点の線は上腕）
        /// </summary>
        public Transform linkBone
        {
            get { return link_ == null ? null : offsetPoint_ ? bone_ : link_.bone_; }
        }

        /// <summary>線を押せるか（線に当たる骨の FK を今動かせるか。窓が描く前に入れる）</summary>
        public bool linkPickable;

        /// <summary>線に当たる骨がメッシュでマウスの下にあるか（マウスが乗ったのと同じに描く。窓が描く前に入れる）</summary>
        public bool linkHovered;

        /// <summary>線を左ボタンで押されたか（読むと消える）。窓が、線に当たる骨の FK を選ぶ</summary>
        public bool ConsumeLinkClick ()
        {
            bool clicked = linkClicked_;
            linkClicked_ = false;
            return clicked;
        }

        /// <summary>
        /// 骨を FK で動かした（pivot を中心に delta だけ回し、offset だけずらした）とき、固定している値を骨と一緒に動かす。
        /// Free の値は骨に付いて動くので触らない。Locked の点も動かすかは呼ぶ側が決める
        /// （FK で骨を動かしたときは置いた場所に残し、点を回したときは一緒に回す）
        /// </summary>
        public void CarryWith (Vector3 pivot, Quaternion delta, Vector3 offset)
        {
            // 持っている手は物に付いているので、骨と一緒には動かさない。物の上の点も、物は階層の親の骨に付いて動く物ではない
            if (held || heldObject != null) return;
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PlaceRotated (position, rotation, pivot, pivot + offset, delta, false, false);
        }

        /// <summary>右ボタンで押されたか（読むと消える）。窓が、選んでいる点をまとめて固定 / 解除する</summary>
        public bool ConsumeContextClick ()
        {
            bool clicked = contextClicked_;
            contextClicked_ = false;
            return clicked;
        }

        /// <summary>
        /// 全身 IK の点の対象を作る（全身 IK が無ければ空）
        /// </summary>
        public static List<PoseTarget> Create (EditingRig rig, FullBodyRig body)
        {
            List<PoseTarget> targets = new List<PoseTarget> ();
            if (rig == null || rig.root == null || body == null) return targets;
            BoneShapes shapes = new BoneShapes (rig);
            foreach (RigBinding.Point point in rig.binding.points) {
                if (!point.enabled || !body.HasPoint (new BodyPointId (point.name))) continue;
                targets.Add (new BodyPointTarget (rig, body, shapes, point));
            }
            // 線でつなぐ相手。定義で名前を指していればその点（つま先の先 → 母趾球など）。
            // 無ければ、関節の点は骨の階層を上へたどって最初に当たる関節の点、骨の上の点はその骨の関節の点。
            // 関節の点の無い骨（背骨の胸・お腹の点の骨）は、その骨の上の点を関節の代わりにする（首・上腕の線が腰まで飛ばない）。
            // 重い点を軽い点より先に選び、同じ重さの点が複数あれば、先に並んだ点
            Dictionary<Transform, BodyPointTarget> joints = new Dictionary<Transform, BodyPointTarget> ();
            Dictionary<string, BodyPointTarget> byName = new Dictionary<string, BodyPointTarget> ();
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = (BodyPointTarget)target;
                if (!point.offsetPoint_) joints[point.bone_] = point;
                byName[point.human_.name] = point;
            }
            // 胸の点を重さ 0 にした定義でも、胸の骨の代わりの点が要る（重い点だけにすると、首・上腕の線が腰まで飛ぶ）
            foreach (bool heavy in new[] { true, false }) {
                foreach (PoseTarget target in targets) {
                    BodyPointTarget point = (BodyPointTarget)target;
                    if (point.offsetPoint_ && point.pulls_ == heavy && !joints.ContainsKey (point.bone_)) joints[point.bone_] = point;
                }
            }
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = (BodyPointTarget)target;
                foreach (PoseTarget otherTarget in targets) {
                    BodyPointTarget other = (BodyPointTarget)otherTarget;
                    if (other == point || other.bone_ != point.bone_) continue;
                    if (point.sameBone_ == null) point.sameBone_ = new List<BodyPointTarget> ();
                    point.sameBone_.Add (other);
                }
            }
            foreach (PoseTarget target in targets) {
                BodyPointTarget point = (BodyPointTarget)target;
                if (!string.IsNullOrEmpty (point.linkName_)) byName.TryGetValue (point.linkName_, out point.link_);
                // 持ち物の上の点は、物の中だけをつなぐ（階層の親は持つ手で、手を離しても線が残る）
                if (point.humanBone_ == HumanBodyBones.LastBone) continue;
                Transform root = rig.root.transform;
                for (Transform t = point.offsetPoint_ ? point.bone_ : point.bone_.parent; t != null && t != root && point.link_ == null; t = t.parent) {
                    BodyPointTarget joint;
                    if (joints.TryGetValue (t, out joint) && joint != point) point.link_ = joint;
                }
            }
            return targets;
        }

        BodyPointTarget (EditingRig rig, FullBodyRig body, BoneShapes shapes, RigBinding.Point point)
        {
            rig_ = rig;
            body_ = body;
            shapes_ = shapes;
            human_ = new BodyPointId (point.name);
            humanBone_ = point.humanBone;
            linkName_ = point.definition != null ? point.definition.link : null;
            offsetPoint_ = rig.GetPointRestOffset (point).sqrMagnitude > 1e-10f;
            pulls_ = point.definition == null || point.definition.weight > 0;
            passive_ = body.IsPassive (human_);
            control_ = body.GetControl (human_);
            bone_ = body.GetBone (human_);
            displayBone_ = point.bone;
            hasRotation_ = point.hasRotation;
            moveHash_ = ("MktBodyPointMove" + control_.GetEntityId ()).GetHashCode ();
            linkHash_ = ("MktBodyPointLink" + control_.GetEntityId ()).GetHashCode ();
        }

        /// <summary>丸の大きさ（ハンドルの大きさの何倍か）。重さで変える</summary>
        float handleScale
        {
            get { return pulls_ ? 1 : kAimScale; }
        }

        /// <summary>点が乗る骨</summary>
        public HumanBodyBones bone
        {
            get { return humanBone_; }
        }

        /// <summary>動かせない点か（定義の passive。S89）</summary>
        public bool passive
        {
            get { return passive_; }
        }

        /// <summary>点の名前（リグ定義の点の一覧の名前）</summary>
        public BodyPointId id
        {
            get { return human_; }
        }

        /// <summary>線でつなぐ相手の点（無ければ null。テスト用）</summary>
        internal BodyPointTarget link
        {
            get { return link_; }
        }

        /// <summary>点が付いている「骨の上の点」の今の位置（付け根の点は骨の位置、離れた点は定義の場所）</summary>
        Vector3 bonePoint
        {
            get {
                Vector3 position;
                return body_.TryGetBonePointWorld (human_, out position) ? position : bone_.position;
            }
        }

        /// <summary>ギズモの軸の向き（骨の見た目の軸。骨の上の点は、骨から点への向き）</summary>
        Quaternion GetShape (Vector3 position, Quaternion rotation)
        {
            if (offsetPoint_) {
                Vector3 toward = position - bone_.position;
                return toward.sqrMagnitude > 1e-10f ? Quaternion.LookRotation (toward) : Quaternion.identity;
            }
            return rotation * Quaternion.Inverse (bone_.rotation) * shapes_.GetRotation (bone_);
        }

        /// <summary>位置か向きを固定しているか</summary>
        public bool pinned
        {
            get { return positionPinned || rotationPinned; }
        }

        public bool positionPinned
        {
            get { return body_.GetPositionWeight (human_) > 0; }
        }

        public bool rotationPinned
        {
            get { return hasRotation_ && body_.GetRotationWeight (human_) > 0; }
        }

        /// <summary>完全固定か（置いた場所から動かさない。手を離しても骨の位置へ合わせ直さない）</summary>
        public bool locked
        {
            get { return pinned && body_.GetLocked (human_); }
        }

        /// <summary>動かしてまだキーを打っていない値があるか</summary>
        public bool hasPendingKeys
        {
            get { return positionDirty_ || rotationDirty_ || lockedDirty_; }
        }

        /// <summary>
        /// 点が骨を引く強さ（0〜1。位置と向きの強いほう）。ふつうは固定していれば 1 だが、強さ 1 未満のキーでは弱い
        /// </summary>
        public float pull
        {
            get {
                float rotation = hasRotation_ ? body_.GetRotationWeight (human_) : 0;
                return Mathf.Clamp01 (Mathf.Max (body_.GetPositionWeight (human_), rotation));
            }
        }

        /// <summary>引く強さが弱まっているか（強さ 1 未満のキー）</summary>
        bool weakened
        {
            get { return pinned && pull < 1; }
        }

        /// <summary>丸の色。弱く引いている点は、引く強さのぶんだけ Free の色へ寄せる（ほとんど引いていない点を Locked の色で出さない）</summary>
        Color fillColor
        {
            get {
                if (passive_) return kPassiveColor;
                if (held) return kHeldColor;
                Color color = locked ? kLockedColor : pinned ? kPinnedColor : kFreeColor;
                return weakened ? Color.Lerp (kFreeColor, color, pull) : color;
            }
        }

        static readonly float[] stateValues_ = new float[BodyPoint.kValueCount];

        /// <summary>
        /// タイムラインのキーの色。クリップのそのフレームの状態の丸の色（不透明にする。丸は骨が透けるよう薄くしてあるが、トラックの上では見分けにくい）。
        /// 今の点ではなくクリップから読むのは、キーごとに状態が違うため
        /// </summary>
        public Color GetKeyColor (AnimationClip clip, int frame)
        {
            bool keyed;
            string path = AnimationUtility.CalculateTransformPath (control_, rig_.root.transform);
            EditingClip.GetPointState (clip, path, frame / clip.frameRate, stateValues_, out keyed);
            bool pinned = stateValues_[BodyPoint.IndexOf (BodyPoint.kPositionWeightProperty)] > 0
                || (hasRotation_ && stateValues_[BodyPoint.IndexOf (BodyPoint.kRotationWeightProperty)] > 0);
            bool locked = pinned && stateValues_[BodyPoint.IndexOf (BodyPoint.kLockedProperty)] >= 0.5f;
            Color color = locked ? kLockedColor : pinned ? kPinnedColor : kFreeColor;
            color.a = 1;
            return color;
        }

        public override GameObject gameObject
        {
            get { return control_ != null ? control_.gameObject : null; }
        }

        public override string label
        {
            get { return "Point " + human_; }
        }

        public override Transform anchor
        {
            get { return displayBone_; }
        }

        public override Vector3 framePosition
        {
            get {
                Vector3 position;
                Quaternion rotation;
                GetPoint (out position, out rotation);
                return position;
            }
        }

        /// <summary>
        /// 点の位置と向き。固定していない間は、骨の今の位置と向きに出す（つかむとそこから固定が始まる）。
        /// 弱く引いている点（強さ 1 未満のキー）は、骨から置いた場所へ引く強さだけ寄せた所に出す。置いた場所に出すと、
        /// ほとんど引いていない点が、キーの間をまっすぐ結んだ場所（AI の足から離れた所）に描かれていた（10-26）
        /// </summary>
        void GetPoint (out Vector3 position, out Quaternion rotation)
        {
            Vector3 pointPosition;
            Quaternion pointRotation;
            body_.TryGetPointWorld (human_, out pointPosition, out pointRotation);
            // 足と一緒に動かしている Pin の点と、ほかの点を動かしている間の Pin の点は、骨の今の場所に出す（置いた場所は離したときに付け直す）
            bool positionOnBone, rotationOnBone;
            body_.GetShownOnBone (human_, out positionOnBone, out rotationOnBone);
            position = positionPinned && !positionOnBone ? Vector3.Lerp (bonePoint, pointPosition, body_.GetPositionWeight (human_)) : bonePoint;
            rotation = rotationPinned && !rotationOnBone ? Quaternion.Slerp (bone_.rotation, pointRotation, body_.GetRotationWeight (human_)) : bone_.rotation;
        }

        /// <summary>
        /// 位置を固定して、点を position へ置く。固定していなかった点は、向きの値も今の骨に合わせておく（後で向きを固定したときに飛ばない）
        /// </summary>
        /// <param name="clamp">床に当てるか（まとめて動かすときは窓がかたまりで当てるので false）</param>
        void PinPosition (Vector3 position, bool clamp = true)
        {
            // つかむ・ギズモ・Spinner・数値欄・まとめて動かす・回す、どれもここを通る。動かせない点は値を持たないので何もしない
            if (passive_) return;
            // 持っている手は物に付いている。手を動かす操作は、物を動かす操作にする
            if (held) {
                holder.owner.DragHandTo (holder, position);
                return;
            }
            if (clamp) position = ClampToFloor (position);
            if (!pinned) body_.CapturePoint (human_);
            body_.MarkGrabbed (human_);
            body_.SetPointPositionWorld (human_, position);
            body_.SetPositionWeight (human_, 1);
            positionDirty_ = true;
        }

        void PinRotation (Quaternion rotation)
        {
            if (!hasRotation_ || passive_) return;
            if (held) {
                Vector3 position;
                Quaternion current;
                GetPoint (out position, out current);
                TurnHeldObject (rotation * Quaternion.Inverse (current));
                return;
            }
            if (!pinned) body_.CapturePoint (human_);
            body_.SetPointRotationWorld (human_, rotation);
            body_.SetRotationWeight (human_, 1);
            rotationDirty_ = true;
        }

        /// <summary>
        /// 向きの固定だけを外す（位置の固定は残す。S36 で姿勢を書くとき、向きを決めない点にする）。外したら次のキーで強さ 0 を打つ
        /// </summary>
        public void ReleaseRotation ()
        {
            if (!rotationPinned || held) return;
            body_.SetRotationWeight (human_, 0);
            rotationDirty_ = true;
        }

        /// <summary>
        /// 固定を付け外しする。外すときは位置も向きも外す。付けるときは、今の骨の位置で位置だけ固定する
        /// </summary>
        public void TogglePin ()
        {
            if (pinned) {
                positionDirty_ = true;
                Unpin ();
            }
            else {
                PinPosition (bonePoint);
            }
        }

        /// <summary>位置も向きも固定を外し、完全固定も外す（外した値は次のキーで強さ 0 を打つ。位置のキーは呼ぶ側が決める）</summary>
        void Unpin ()
        {
            if (passive_ || held) return;
            rotationDirty_ = rotationPinned;
            lockedDirty_ = body_.GetLocked (human_);
            body_.SetPositionWeight (human_, 0);
            body_.SetRotationWeight (human_, 0);
            body_.SetLocked (human_, false);
        }

        /// <summary>完全固定を付け外しする（変わったら次のキーで打つ）</summary>
        void SetLockedFlag (bool on)
        {
            if (passive_ || held || body_.GetLocked (human_) == on) return;
            body_.SetLocked (human_, on);
            lockedDirty_ = true;
        }

        /// <summary>
        /// 完全固定を付け外しする。固定していない点は、今の骨の位置で固定してから完全固定にする。外しても固定は残る
        /// </summary>
        public void ToggleLock ()
        {
            if (!pinned) PinPosition (bonePoint);
            SetLockedFlag (!body_.GetLocked (human_));
        }

        /// <summary>今のフレームでの点の状態</summary>
        public BodyPointState state
        {
            get { return locked ? BodyPointState.Locked : pinned ? BodyPointState.Pin : BodyPointState.Free; }
        }

        /// <summary>
        /// 窓に出す状態（Controller のパネルのボタン）。引く強さが半分に満たない点は Free と出す（ほとんど引いていない点）
        /// </summary>
        public BodyPointState shownState
        {
            get { return pull < 0.5f ? BodyPointState.Free : state; }
        }

        /// <summary>
        /// 点の状態を変える。Free の点を Pin / Locked にするときは、今の骨の位置に置く。Locked を Pin にすると、置いた場所のまま Pin になる
        /// （次に手を離したときに、骨の位置へ合わせ直される）。
        /// 弱く引いている点を Pin / Locked にするときは、今出している所に強さ 1 で置き直す（同じ状態を選んでも）
        /// </summary>
        public void SetState (BodyPointState value)
        {
            if (value == state && !weakened) return;
            if (weakened && value != BodyPointState.Free) {
                Vector3 position;
                Quaternion rotation;
                GetPoint (out position, out rotation);
                PinPosition (position);
                if (rotationPinned) PinRotation (rotation);
            }
            if (value == state) return;
            switch (value) {
                case BodyPointState.Free:
                    SetLocked (false);
                    break;
                case BodyPointState.Locked:
                    SetLocked (true);
                    break;
                default:
                    if (!pinned) PinPosition (bonePoint);
                    SetLockedFlag (false);
                    break;
            }
        }

        /// <summary>
        /// 固定する（置いた場所から動かさない点にする）/ 固定を外す（何もしていない点に戻す）。
        /// 固定するとき、まだ動かしていない点は今の骨の位置に置く
        /// </summary>
        public void SetLocked (bool on)
        {
            if (on) {
                if (!pinned) PinPosition (bonePoint);
                SetLockedFlag (true);
            }
            else if (pinned) {
                TogglePin ();
            }
        }

        /// <summary>
        /// 固定した点（完全固定でないもの）を、実際の骨の位置へ合わせ直す。手を離したときに呼ぶ。合わせ直したら true（キーを打つ値が増える）
        /// </summary>
        public bool SyncToBone ()
        {
            if (held) return false;
            bool position, rotation;
            body_.SyncPointToBone (human_, out position, out rotation);
            positionDirty_ |= position;
            rotationDirty_ |= rotation;
            return position || rotation;
        }

        /// <summary>左右の相手を探す名前（Point LeftHand ⇔ Point RightHand。体の中心の点は自分）</summary>
        public override string mirrorKey
        {
            get { return label; }
        }

        /// <summary>
        /// 左右の相手（体の中心の点なら自分）の今の状態と場所を、左右反転してこの点に入れる。
        /// Free の点の反転は Free（体に付いて動く）。Pin / Locked は、反転した場所に置いて同じ状態にする。向きを留めていれば向きも反転する
        /// </summary>
        public override bool MirrorFrom (PoseTarget source)
        {
            BodyPointTarget other = source as BodyPointTarget;
            // 相手は名前の左右を入れ替えた点（体の中心の点は自分）。持ち物は左右反転しないので、物の上の点もしない
            if (other == null || held || heldObject != null) return false;
            string mirrored = MirrorNames.Swap (human_.name) ?? human_.name;
            if (other.human_.name != mirrored) return false;
            BodyPointState from = other.state;
            bool rotation = other.rotationPinned && hasRotation_;
            Vector3 position;
            Quaternion delta;
            other.TryGetValues (out position, out delta);
            if (from == BodyPointState.Free) {
                SetState (BodyPointState.Free);
                return true;
            }
            Vector3 normal;
            float center;
            rig_.GetMirrorPlane (out normal, out center);
            position -= 2 * (Vector3.Dot (position, normal) - center) * normal;
            SetValues (TransformChannels.Position, position, Quaternion.identity);
            if (rotation) {
                SetValues (TransformChannels.Rotation, position, EditingRigSolver.MirrorRotation (delta, normal));
            }
            else {
                ReleaseRotation ();
            }
            SetLockedFlag (from == BodyPointState.Locked);
            return true;
        }

        public override TransformChannels GetSpinChannels ()
        {
            if (passive_) return TransformChannels.None;
            return hasRotation_ ? TransformChannels.Position | TransformChannels.Rotation : TransformChannels.Position;
        }

        public override void SpinMove (Vector3 delta)
        {
            MoveBy (rig_.root.transform.TransformVector (delta));
        }

        public override void SpinRotateLocal (Quaternion delta)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinRotation (rotation * delta);
        }

        public override void SpinRotateWorld (Quaternion delta)
        {
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            PinRotation (delta * rotation);
        }

        public override void OnHandleGUI (bool selected)
        {
            if (bone_ == null || control_ == null) return;

            // 右ボタン: 丸の上で、固定を付け外しする（プレビュー窓では動かさずに離したとき。動かすと視点の回転）。動かせない点は固定を持たない
            Event ev = Event.current;
            if (!passive_ && !ev.alt && PoseHandleUtility.IsContextClick (ev, moveHash_)) {
                contextClicked_ = true;
                ev.Use ();
                GUI.changed = true;
                return;
            }

            // 状態は丸の色で出す。選び方は丸の外の輪で出す（選択の色に塗り替えると状態が分からなくなる）
            PoseHandleUtility.color = fillColor;
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            Quaternion shape = GetShape (position, rotation);

            DoLink (position);
            DrawRing (position, selected);
            DrawCollision (position);

            // 関節の丸（つかんで動かすと位置を固定する）。動かせない点は押して選べるだけで、つかんで動かしても何もしない。ギズモも出さない
            Vector3 moved = PoseHandleUtility.DoJointHandle (moveHash_, control_, position, rotation, false, false, handleScale);
            if (passive_) {
                PoseHandleUtility.color = PoseHandleUtility.StandardColor;
                return;
            }
            if (moved != position) {
                if (!movesWithSelection) {
                    ReleaseSameBoneRotations ();
                    moved = MoveFoot (moved);
                }
                AddDragDelta (position, moved);
                PinPosition (moved, !movesWithSelection);
                position = moved;
                GUI.changed = true;
            }

            // 選んでいる間はギズモ（Controller パネルのモードで移動 / 回転）。
            // 動かした量は丸をつかんだときと同じく窓へ渡す（いくつか選んでいれば、ほかの選んでいる点も同じだけ動かす）。
            // 回した量は窓へ渡すだけで、ここでは回さない（位置だけの点も、骨を FK で回して回せる）
            if (selected && showGizmo && PoseHandleUtility.goalRotate) {
                Quaternion turned;
                if (PoseHandleUtility.DoRotateGizmo (moveHash_, position, shape, out turned)) {
                    rotateDelta_ = turned * rotateDelta_;
                    GUI.changed = true;
                }
            }
            else if (selected && showGizmo) {
                Vector3 gizmoPosition = PoseHandles.PositionHandle (position, PoseHandleUtility.GizmoAxes (shape));
                if (gizmoPosition != position) {
                    if (!movesWithSelection) {
                        ReleaseSameBoneRotations ();
                        gizmoPosition = MoveFoot (gizmoPosition);
                    }
                    AddDragDelta (position, gizmoPosition);
                    PinPosition (gizmoPosition, !movesWithSelection);
                    GUI.changed = true;
                }
            }

            // 固定した点が届いていないときは、骨までの線を出す（弱く引いている点は、出している所まで）
            if (Event.current.type == EventType.Repaint && positionPinned) {
                Vector3 pointPosition;
                Quaternion pointRotation;
                GetPoint (out pointPosition, out pointRotation);
                Vector3 onBone = bonePoint;
                if ((pointPosition - onBone).sqrMagnitude > 1e-6f) {
                    Handles.color = PoseHandles.Fade (new Color (1, 0.85f, 0.2f, 0.8f));
                    Handles.DrawDottedLine (onBone, pointPosition, 3);
                }
            }
            PoseHandleUtility.color = PoseHandleUtility.StandardColor;
        }

        /// <summary>床との当たりの球（定義の radius）を、見る向きの輪で描く（半径の無い点は描かない）</summary>
        void DrawCollision (Vector3 position)
        {
            float radius = floorRadius;
            if (Event.current.type != EventType.Repaint || radius <= 0) return;
            Color saved = Handles.color;
            Handles.color = PoseHandles.Fade (kCollisionColor);
            Vector3 normal = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
            Handles.DrawWireDisc (position, normal, radius);
            Handles.color = saved;
        }

        /// <summary>
        /// 丸の外に少し隙間を空けて輪を描く。選んでいる点は選んだ色、選ぼうとしている点（マウスが乗っている・枠に入っている）は乗った色。
        /// どちらでもなければ描かない。マウスが乗った黄色の縁取りは丸の側（PoseHandles.JointHandle）が描く
        /// </summary>
        void DrawRing (Vector3 position, bool selected)
        {
            if (Event.current.type != EventType.Repaint) return;
            int hot = GUIUtility.hotControl;
            bool hover = HandleUtility.nearestControl == moveHash_ && (hot == 0 || hot == moveHash_);
            if (!selected && !hover && !inBox) return;
            Color saved = Handles.color;
            Handles.color = PoseHandles.Fade (selected ? kRingSelectedColor : kRingHoverColor);
            Vector3 normal = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
            Handles.DrawWireDisc (position, normal, EditorHost.current.handleSize * (handleScale + kRingGap), kRingWidth);
            Handles.color = saved;
        }

        /// <summary>
        /// つなぐ相手の点まで、少し太い線を引く（体の形が点と線で見える）。丸に重ならないよう、両端に隙間を空ける
        /// </summary>
        void DrawLink (Vector3 position)
        {
            Vector3 start, end;
            if (Event.current.type != EventType.Repaint || !TryGetLink (position, out start, out end)) return;
            DrawLinkLine (start, end, false);
        }

        /// <summary>
        /// 点をつかむ間の線。押すと、線に当たる骨の FK を選ぶ（窓が ConsumeLinkClick で読む）。
        /// マウスが乗っている間は、太く乗った色で描く。その骨の FK を選んでいても強調しない
        /// </summary>
        void DoLink (Vector3 position)
        {
            Vector3 start, end;
            if (!TryGetLink (position, out start, out end)) return;
            Event ev = Event.current;
            if (linkPickable) {
                if (ev.type == EventType.Layout) {
                    HandleUtility.AddControl (linkHash_, HandleUtility.DistanceToLine (start, end) + kLinkPickBias);
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && !ev.alt && GUIUtility.hotControl == 0 && HandleUtility.nearestControl == linkHash_) {
                    linkClicked_ = true;
                    ev.Use ();
                }
            }
            if (ev.type != EventType.Repaint) return;
            bool hover = linkPickable && GUIUtility.hotControl == 0 && (HandleUtility.nearestControl == linkHash_ || linkHovered);
            DrawLinkLine (start, end, hover);
        }

        /// <summary>線の両端（丸に重ならないよう、両端に隙間を空ける）。短すぎて隙間しか残らなければ false</summary>
        bool TryGetLink (Vector3 position, out Vector3 start, out Vector3 end)
        {
            start = end = position;
            if (link_ == null) return false;
            Vector3 delta = link_.framePosition - position;
            float length = delta.magnitude;
            // 隙間は両端それぞれの丸の大きさに合わせる
            float gap = EditorHost.current.handleSize * kLinkGap * handleScale;
            float linkGap = EditorHost.current.handleSize * kLinkGap * link_.handleScale;
            if (length <= gap + linkGap + 1e-5f) return false;
            Vector3 direction = delta / length;
            start = position + direction * gap;
            end = position + direction * (length - linkGap);
            return true;
        }

        void DrawLinkLine (Vector3 start, Vector3 end, bool hover)
        {
            Color saved = Handles.color;
            Handles.color = PoseHandles.Fade (hover ? kRingHoverColor : kLinkColor);
            Handles.DrawAAPolyLine (hover ? kLinkWidth * kLinkHighlightWidth : kLinkWidth, start, end);
            Handles.color = saved;
        }

        /// <summary>
        /// 点をつかまない間（✋ が全身 IK の段でない）の表示。固定している点だけ、状態の色の小さい印を描く（操作は受けない）
        /// </summary>
        public void DrawPinned ()
        {
            if (Event.current.type != EventType.Repaint || bone_ == null || shownState == BodyPointState.Free) return;
            Vector3 position;
            Quaternion rotation;
            GetPoint (out position, out rotation);
            Color saved = Handles.color;
            Handles.color = fillColor;
            if (offsetPoint_) DrawLink (position);
            Vector3 normal = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
            Handles.DrawSolidDisc (position, normal, EditorHost.current.handleSize * kMarkScale * handleScale);
            Handles.color = saved;
        }

        /// <summary>
        /// 姿勢全体にキーを打つとき（Key All・骨を動かした後）。固定している点の値だけ打つ
        /// </summary>
        public override void WriteKeys (CurveWriter writer)
        {
            // 持っている手の値は物から作る（物のキーと手のキーの二重管理にしない）
            if (held) return;
            if (positionPinned) WritePosition (writer);
            if (rotationPinned) WriteRotation (writer);
            if (locked) WriteLocked (writer);
        }

        /// <summary>
        /// 動かした点のキーを打つ（位置を動かしたら位置と位置の固定の強さ、回したら向きと向きの固定の強さ。固定を外したときは強さ 0 も打つ）
        /// </summary>
        public void WritePendingKeys (CurveWriter writer)
        {
            if (held) {
                ClearPendingKeys ();
                return;
            }
            if (positionDirty_) WritePosition (writer);
            if (rotationDirty_) WriteRotation (writer);
            if (lockedDirty_) WriteLocked (writer);
            ClearPendingKeys ();
        }

        /// <summary>動かした値を打たずに捨てる（姿勢をクリップから当て直したとき）</summary>
        public void ClearPendingKeys ()
        {
            positionDirty_ = false;
            rotationDirty_ = false;
            lockedDirty_ = false;
        }

        void WriteLocked (CurveWriter writer)
        {
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kLockedProperty, body_.GetLocked (human_) ? 1 : 0);
        }

        void WritePosition (CurveWriter writer)
        {
            writer.Transform (control_, TransformChannels.Position);
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kPositionWeightProperty, body_.GetPositionWeight (human_));
        }

        void WriteRotation (CurveWriter writer)
        {
            if (!hasRotation_) return;
            writer.Transform (control_, TransformChannels.Rotation);
            writer.Float (control_, typeof (BodyPoint), BodyPoint.kRotationWeightProperty, body_.GetRotationWeight (human_));
        }

        public override void WriteSpinKeys (CurveWriter writer, TransformChannels channels)
        {
            WritePendingKeys (writer);
        }

        public override void CollectKeyedPaths (ICollection<string> result)
        {
            // 動かせない点はキーを持たない（トラックも出さない）
            if (passive_) return;
            result.Add (AnimationUtility.CalculateTransformPath (control_, rig_.root.transform));
        }

        /// <summary>
        /// メッシュのクリックでは選ばない（骨は FK の対象が受け持つ）
        /// </summary>
        public override void CollectPickTransforms (List<Transform> result)
        {
        }

        float humanScale
        {
            get { return rig_.humanScale; }
        }

        /// <summary>
        /// 数値欄に出す値。位置はルートから見た点の位置（メートル）、向きはルートから見た基準姿勢からの差。
        /// 固定していない間は、今の骨の位置と向き
        /// </summary>
        public override bool TryGetValues (out Vector3 position, out Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            Vector3 world;
            Quaternion worldRotation;
            GetPoint (out world, out worldRotation);
            position = root.InverseTransformPoint (world);
            rotation = Quaternion.Normalize (Quaternion.Inverse (root.rotation) * worldRotation * Quaternion.Inverse (rig_.GetRestRootRotation (bone_)));
            return true;
        }

        public override void SetValues (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            Transform root = rig_.root.transform;
            if ((channels & TransformChannels.Position) != 0) PinPosition (root.TransformPoint (position));
            if ((channels & TransformChannels.Rotation) != 0) PinRotation (root.rotation * Quaternion.Normalize (rotation) * rig_.GetRestRootRotation (bone_));
        }

        /// <summary>
        /// 点の骨を親から見た基準姿勢に戻した所・向きへ点を置く（ローカルで戻す）。固定の強さは変えない。
        /// 点の値はルートから見た値なので、基準の値そのものへ戻すと、しゃがんだ姿勢などで体から離れた所へ飛ぶ。
        /// 姿勢全体を基準へ戻すのは ResetPose
        /// </summary>
        public override void ResetValues (TransformChannels channels)
        {
            ResetValues (channels, null);
        }

        /// <summary>
        /// いくつかの点をまとめて戻すとき。resetBones は一緒に戻す点の骨（editingBone）すべて。
        /// 祖先の骨も戻すなら、戻した祖先に付いて動いた所へ置く（FK で根から順に戻したのと同じ）
        /// </summary>
        public void ResetValues (TransformChannels channels, ICollection<Transform> resetBones)
        {
            Vector3 position;
            Quaternion rotation;
            // 物の上の点は親から見た基準姿勢を持たない（物の基準の場所へ戻すのは物の FK）
            if (passive_ || held || heldObject != null || !body_.TryGetPointLocalRestWorld (human_, resetBones, out position, out rotation)) return;
            if ((channels & TransformChannels.Position) != 0) {
                body_.SetPointPositionWorld (human_, ClampToFloor (position));
                positionDirty_ = true;
            }
            if (hasRotation_ && (channels & TransformChannels.Rotation) != 0) {
                body_.SetPointRotationWorld (human_, rotation);
                rotationDirty_ = true;
            }
        }

        /// <summary>
        /// 基準へ戻す（Reset）。点を基準姿勢の骨の上へ戻し、固定を外す
        /// </summary>
        public override void ResetPose ()
        {
            Vector3 position;
            Quaternion rotation;
            if (passive_ || held || !body_.TryGetPointRestValues (human_, out position, out rotation)) return;
            control_.localPosition = position;
            control_.localRotation = rotation;
            positionDirty_ = positionPinned;
            Unpin ();
        }
    }

}
