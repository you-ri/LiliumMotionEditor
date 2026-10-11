using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 手足の位置を保つ仕組みの選び方（S25）。両方を同時には効かせない
    /// </summary>
    public enum RigSolver
    {
        /// <summary>2 本の骨の IK（腕・脚）と足の転がし。solver の項目が無い古い定義はこれで読む</summary>
        TwoBoneIk,
        /// <summary>全身 IK。関節の点（Controls/Body/...）にキーを打って固定し、全身を解く。IK の組は使わない。新しく作る定義の既定（SetDefault）</summary>
        FullBodyIk,
    }

    /// <summary>
    /// 全身 IK の点を置く場所の基準（S26）。基準の場所はキャラの骨から決め、そこから定義のずれだけ動かした所に点を置く。
    /// 基準は置き場所を決めるだけで、解き方はどれも同じ（骨の上の点を目標へ寄せる）
    /// </summary>
    public enum BodyPointAnchor
    {
        /// <summary>乗る骨の付け根</summary>
        BoneOrigin,
        /// <summary>かかと（同じ側の脚の IK の組の、足の転がしの支点。足の転がしの上書きも効く）</summary>
        Heel,
        /// <summary>つま先の先（同じ側の脚の IK の組の、足の転がしの支点）</summary>
        ToeTip,
        /// <summary>同じ側の人差し指の付け根（指の骨が無ければ、前腕の向きから見積もる）</summary>
        IndexBase,
        /// <summary>同じ側の小指の付け根（指の骨が無ければ、前腕の向きから見積もる）</summary>
        LittleBase,
        /// <summary>母趾球の列の足裏の中心（同じ側の脚の IK の組の、足の転がしの支点。足の転がしの上書きも効く）</summary>
        SoleBall,
        /// <summary>母趾球の列の小指側の縁（同じ側の脚の IK の組の、足の転がしの支点。足の転がしの上書きも効く）</summary>
        LittleToeSide,
        /// <summary>同じ側の膝（下腿の骨の付け根）</summary>
        Knee,
        /// <summary>
        /// 目の高さ（頭の骨の付け根の真上）。目の骨があればその高さ、無ければ頭に付いたメッシュから見積もる（EditingRig.GetEyeHeight）
        /// </summary>
        Eyes,
        /// <summary>同じ側の肘（前腕の骨の付け根）</summary>
        Elbow,
    }

    /// <summary>
    /// 編集用リグの定義。キャラの骨名ではなく HumanBodyBones で書くので、1 つの定義を全キャラで使える
    /// （キャラへの割り当ては RigBinding が Avatar から引く）。
    /// 定義は骨の上位集合にする。キャラに無い骨のコントロールは、そのキャラでは無効になるだけで、クリップのカーブは消さない。
    /// キャラごとの調整（足の転がしの折れ角・支点など）も持てるので、キャラごとにアセットを作って使い分けてよい（S23）
    /// </summary>
    [CreateAssetMenu (menuName = "Lilium Motion Editor/Rig Definition", fileName = "RigDefinition")]
    public sealed class EditRigDefinition : ScriptableObject
    {
        /// <summary>
        /// 骨を直接回す（FK）コントロール。値は基準姿勢（T ポーズ）からの差で持つ
        /// </summary>
        [System.Serializable]
        public sealed class FkControl
        {
            public HumanBodyBones bone;
            /// <summary>位置も動かすか（腰など）</summary>
            public bool position;
            /// <summary>全身 IK で解く骨か（解かない骨は親に付いて動く。指・目・顎は既定で解かない）</summary>
            public bool fullBodySolve = true;
            /// <summary>全身 IK の点（つかんで固定する所）を置くか。版 2 までの設定（版 3 で点の一覧 bodyPoints へ移した。古い定義から一覧を作るときだけ読む）</summary>
            [HideInInspector]
            public bool fullBodyPoint = true;
            /// <summary>全身 IK の硬さ（0〜1）。元の姿勢の角度に戻ろうとする強さ。体の中心ほど強くする</summary>
            [Range (0, 1)]
            public float stiffness = kDefaultStiffness;
            /// <summary>
            /// 全身 IK の点をつかむ間（✋ が全身 IK の段）も、この骨の FK のハンドルを出すか。点をつかむ間は関節の上で点と重なるので FK のハンドルを隠すが、
            /// 点の無い骨（指・目・顎）は隠すと見えず動かせない。既定は全身 IK で解かない骨だけ出す
            /// </summary>
            public bool fullBodyHandle;
            /// <summary>
            /// 一方向にだけ曲がる関節か（肘・膝）。全身 IK では、親の骨と bendDirection の両方に直交する軸のまわりだけで回す
            /// （位置だけでは曲がる向きが決まらず、反復の途中経過しだいでばらつくため）
            /// </summary>
            public bool hinge;
            /// <summary>蝶番（hinge）の曲がる向き。関節が出る向きを、体の向き（x 右・y 上・z 前）で書く（肘は後ろ・膝は前）</summary>
            public Vector3 bendDirection;

            /// <summary>Humanoid で必須の骨か（無ければ Avatar が作れないので、どのキャラにもある）</summary>
            public bool required
            {
                get { return bone >= 0 && bone < HumanBodyBones.LastBone && HumanTrait.RequiredBone ((int)bone); }
            }
        }

        /// <summary>
        /// 2 本の骨の IK（腕・脚）。必須の骨だけで組む（肩は含めない）
        /// </summary>
        [System.Serializable]
        public sealed class IkChain
        {
            /// <summary>パスに使う名前（LeftArm など）</summary>
            public string name;
            public HumanBodyBones root;
            public HumanBodyBones mid;
            public HumanBodyBones tip;
            /// <summary>
            /// 手足がまっすぐでヒント（肘・膝の向き）が決まらないときの向き。体の向き（x 右・y 上・z 前）で書く
            /// </summary>
            public Vector3 defaultHint;
            /// <summary>
            /// 足の転がし（Reverse Foot。踵・母趾球・つま先を支点に roll / bank / twist で回す）を付けるか。脚だけ
            /// </summary>
            public bool reverseFoot;
            /// <summary>つま先の骨（転がしでつま先を床に残す。キャラに無ければ戻しだけ無い）</summary>
            public HumanBodyBones toes;
            /// <summary>折れ角（度）。roll がこれを越えると、母趾球まわりをやめてつま先の先まわりに回る</summary>
            public float toeBreak = ReverseFoot.kDefaultToeBreak;
            /// <summary>支点の上書き（無い支点は骨と Avatar から見積もる）</summary>
            public List<FootPivot> pivots = new List<FootPivot> ();
        }

        /// <summary>
        /// 足の転がしの支点の位置。足首から見た体の向き（x 右・y 上・z 前）で、メートル
        /// </summary>
        [System.Serializable]
        public sealed class FootPivot
        {
            public ReverseFoot.Pivot pivot;
            public Vector3 position;
        }

        /// <summary>
        /// 人型の骨でないもの（武器・尻尾など）。骨の名前のパターン（正規表現）で引く
        /// </summary>
        [System.Serializable]
        public sealed class ExtraControl
        {
            public string name;
            public string pattern;
            public bool position;
            /// <summary>全身 IK の点をつかむ間も、ハンドルを出すか（追加のコントロールは全身 IK で解かず点も無いので、既定は出す）</summary>
            public bool fullBodyHandle = true;
        }

        /// <summary>
        /// 全身 IK の全体の設定（S25）
        /// </summary>
        [System.Serializable]
        public sealed class FullBodySettings
        {
            /// <summary>解く回数（固定。同じ入力なら同じ結果になる）</summary>
            [Min (1)]
            public int iterations = 48;
            /// <summary>点を目標へ寄せる強さ（0〜1）。点の固定の強さに掛かる</summary>
            [Range (0, 1)]
            public float pull = 1;
            /// <summary>腰を元の姿勢の位置と向きに留める強さ（0〜1）。0 なら留めない</summary>
            [Range (0, 1)]
            public float hipsPin = 0.6f;
            // 以下の 3 つは版 2 までの設定（版 3 で点の一覧 bodyPoints へ移した。古い定義から一覧を作るときと、既定の一覧を作るときだけ読む）
            /// <summary>向きの点（腰・胸・頭の前に出した点と、頭のてっぺんの点、膝の Pole）を置くか</summary>
            [HideInInspector]
            public bool directionPoints = true;
            /// <summary>向きの点を、骨からどれだけ前へ離して置くか（体の大きさ humanScale に対する割合）</summary>
            [HideInInspector]
            public float directionLength = 0.25f;
            /// <summary>骨の上の点（足の踵・足裏の中心・小指側・つま先の先、手の人差し指側と小指側の 2 点）を置くか</summary>
            [HideInInspector]
            public bool contactPoints = true;
        }

        /// <summary>
        /// 全身 IK の点（S26）。骨の上の好きな場所に置ける。種類で解き方は変わらず、同じ骨の上の点どうしは剛体として拘束しあう
        /// </summary>
        [System.Serializable]
        public sealed class BodyPoint
        {
            public bool enabled = true;
            /// <summary>名前。コントロールのパス（Controls/Body/名前）になり、編集用クリップのキーはこの名前で残る</summary>
            public string name;
            /// <summary>乗る骨。キャラに無い・全身 IK で解かない骨なら、親の骨をたどって最初に当たる解く骨へ乗る（つま先の骨が無ければ足）</summary>
            public HumanBodyBones bone;
            /// <summary>置く場所の基準（キャラの骨から決める）。左右は bone の左右</summary>
            public BodyPointAnchor anchor;
            /// <summary>基準からのずれ（体の向き x 右・y 上・z 前。体の大きさ humanScale に対する割合）</summary>
            public Vector3 offset;
            /// <summary>向きも持つか（向きを固定すると、骨の向きも留まる）</summary>
            public bool rotation;
            /// <summary>
            /// 引く強さ（0 か 1。S28）。1 は点を引くと骨ごと動き、ほかの骨も付いてくる。0 は乗る骨を回して点を目標の方へ向けるだけで、
            /// ほかの骨を引かない（向きの点の既定）。骨の付け根そのものの点は 0 にできない。項目の無い古い定義は 1 で読まれる
            /// </summary>
            [Range (0, 1)]
            [Tooltip ("1: dragging the point pulls its bone and the rest of the body along. 0: the point only turns its bone to face the target and never pulls other bones (default for direction points).")]
            public int pullStrength = 1;
            /// <summary>
            /// 線でつなぐ相手の点の名前（表示だけ。解き方は変わらない）。空なら骨の階層から決める
            /// （関節の点は上の関節の点、骨の上の点はその骨の関節の点）
            /// </summary>
            [Tooltip ("Name of the point to draw a line to (display only). Empty: the joint point above it in the bone hierarchy.")]
            public string link;
            /// <summary>
            /// 基準からのずれ（offset）の向きを、乗る骨と混ぜる相手の骨（S34）。blend が 0 なら使わない。
            /// キャラに無い・全身 IK で解かない骨なら、親の骨をたどって最初に当たる解く骨（乗る骨と同じになれば混ぜない）
            /// </summary>
            [Tooltip ("Bone whose rotation is blended with the host bone's to turn the offset (e.g. the shin for a knee pole on the thigh). Used only when Blend is above 0.")]
            public HumanBodyBones blendBone;
            /// <summary>
            /// 混ぜる割合（0〜1。S34）。ずれを、乗る骨の向きと blendBone の向きを基準姿勢からの回りで混ぜた向きで回す（0 は乗る骨だけ、1 は blendBone だけ）。
            /// 基準の場所は乗る骨に付いたまま。膝の Pole（太ももに乗る）を 0.5 にすると、膝を曲げても Pole は太ももと脛の間の向き（膝の外側）に出る。
            /// 引く強さ 0 の点だけで使える
            /// </summary>
            [Range (0, 1)]
            [Tooltip ("How much the offset turns with Blend Bone instead of the host bone (0: host bone only, 1: Blend Bone only). 0.5 keeps a knee pole between the thigh and the shin when the knee bends. Only for points with pull strength 0.")]
            public float blend;

            /// <summary>ずれの向きを、ほかの骨と混ぜるか</summary>
            public bool blends
            {
                get { return blend > 0 && blendBone != bone; }
            }
        }

        public const float kDefaultStiffness = 0.2f;
        /// <summary>定義の形の版。古い版のアセットを読んだら、足りない項目を既定値で埋める</summary>
        public const int kVersion = 8;

        [SerializeField, HideInInspector]
        int version_;

        /// <summary>
        /// 手足の位置を保つ仕組み（2 本骨 IK＋足の転がし か 全身 IK）。初期値が 2 本骨 IK なのは、この項目が無い古い定義アセット（S25 より前）を
        /// 今までどおりに読むため。新しく作る定義は SetDefault で全身 IK になる
        /// </summary>
        public RigSolver solver = RigSolver.TwoBoneIk;
        public FullBodySettings fullBody = new FullBodySettings ();
        public List<FkControl> fkControls = new List<FkControl> ();
        public List<IkChain> ikChains = new List<IkChain> ();
        public List<ExtraControl> extraControls = new List<ExtraControl> ();
        /// <summary>全身 IK の点（全身 IK の定義のときだけ使う）</summary>
        public List<BodyPoint> bodyPoints = new List<BodyPoint> ();

        /// <summary>パッケージに入れた既定の定義のアセット。中身は SetDefault と同じ</summary>
        public const string kPackageDefaultPath = "Packages/jp.lilium.motioneditor/Editor/Rig/Default Rig Definition.asset";

        /// <summary>
        /// パッケージの既定の定義（kPackageDefaultPath）。プロジェクト設定の Rig Definition が空のときに使う。
        /// git から入れたパッケージは書き換えられないので、直すときは Create メニューで作ってプロジェクト設定に入れる。
        /// 読めなければ null（使う側は CreateDefault で代わりを作る）
        /// </summary>
        public static EditRigDefinition packageDefault
        {
            get { return AssetDatabase.LoadAssetAtPath<EditRigDefinition> (kPackageDefaultPath); }
        }

        /// <summary>
        /// 既定の定義を一時の物として作る（保存しない）。Humanoid の骨を全部 FK にし（腰だけ位置も）、両腕・両脚に IK を置く
        /// </summary>
        public static EditRigDefinition CreateDefault ()
        {
            EditRigDefinition definition = CreateInstance<EditRigDefinition> ();
            definition.name = "Default Rig Definition";
            definition.hideFlags = HideFlags.DontSave;
            definition.SetDefault ();
            return definition;
        }

        /// <summary>
        /// 中身を既定の定義にする（前の中身は捨てる）
        /// </summary>
        public void SetDefault ()
        {
            fkControls.Clear ();
            ikChains.Clear ();
            extraControls.Clear ();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++) {
                HumanBodyBones bone = (HumanBodyBones)i;
                FkControl control = new FkControl {
                    bone = bone,
                    position = bone == HumanBodyBones.Hips,
                };
                SetFullBodyDefaults (control);
                fkControls.Add (control);
            }
            solver = RigSolver.FullBodyIk;
            fullBody = new FullBodySettings ();
            bodyPoints = CreateDefaultBodyPoints (fkControls, fullBody);
            version_ = kVersion;

            // 肘は後ろ、膝は前へ曲がる
            ikChains.Add (Chain ("LeftArm", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, Vector3.back));
            ikChains.Add (Chain ("RightArm", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, Vector3.back));
            ikChains.Add (Leg ("LeftLeg", HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes));
            ikChains.Add (Leg ("RightLeg", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes));
        }

        /// <summary>
        /// source の中身を写す（名前とアセットの情報は写さない）
        /// </summary>
        public void CopyFrom (EditRigDefinition source)
        {
            if (source == null || source == this) return;
            JsonUtility.FromJsonOverwrite (JsonUtility.ToJson (source), this);
        }

        /// <summary>
        /// 定義の値が変わった（インスペクタで直した・Undo）。使っている窓が編集用リグを作り直す
        /// </summary>
        public static event System.Action<EditRigDefinition> changed;

        void OnValidate ()
        {
            Upgrade ();
            NotifyChanged (this);
        }

        void OnEnable ()
        {
            Upgrade ();
        }

        /// <summary>
        /// 古い版のアセットの足りない項目を既定値で埋める（全身 IK の「解くか・点・硬さ」は骨ごとに既定値が違うので、
        /// 項目の初期値だけでは足りない）。新しく作った定義は SetDefault が版を入れる
        /// </summary>
        void Upgrade ()
        {
            if (version_ >= kVersion) return;
            if (fullBody == null) fullBody = new FullBodySettings ();
            if (version_ < 1) {
                foreach (FkControl control in fkControls) {
                    if (control != null) SetFullBodyDefaults (control);
                }
            }
            else if (version_ < 2) {
                // 版 2: 全身 IK の解き方を本体へ差し替えたときに、既定値を見直した。版 1 の既定値のままの項目だけ、新しい既定値にする
                if (fullBody.iterations == 20) fullBody.iterations = 48;
                if (Mathf.Approximately (fullBody.hipsPin, 0.3f)) fullBody.hipsPin = 0.6f;
                foreach (FkControl control in fkControls) {
                    if (control == null) continue;
                    bool shoulder = control.bone == HumanBodyBones.LeftShoulder || control.bone == HumanBodyBones.RightShoulder;
                    if (shoulder && Mathf.Approximately (control.stiffness, 0.3f)) control.stiffness = DefaultStiffness (control.bone);
                }
            }
            if (version_ < 6) {
                // 版 5: 足の点を踵・足裏の中心・小指側・つま先の先にし、膝の Pole を足した（10-05）。版 6: 顔の前の点を目の高さに置いた（10-05）。
                // 後方互換は保たない方針なので、点の一覧は今の既定で作り直す（一覧を直していた定義も既定に戻る。今までのクリップの Toes の点のキーは当たらなくなる）
                bodyPoints = CreateDefaultBodyPoints (fkControls, fullBody);
            }
            else {
                if (version_ < 7) {
                    // 版 7: 膝の Pole のずれを、太ももと脛の向きを半分ずつ混ぜた向きで回すようにした（10-08。S34）。
                    // 既定の一覧の膝の Pole で、混ぜる設定をまだ入れていない物だけ直す（版 6 で作り直した一覧なので、ほかの点はそのまま）
                    foreach (BodyPoint point in bodyPoints) {
                        if (point != null) SetKneePoleBlend (point);
                    }
                }
                if (version_ < 8 && fullBody.directionPoints) {
                    // 版 8: 肘の Pole を足した（10-10。S43）。足すだけで、ほかの点は変えない。同じ名前の点があれば足さない（自分で足した肘の Pole を残す）。
                    // Free（キーの無い今までのクリップ）の Pole は解き方に効かないので、今までのクリップの姿勢は変わらない
                    AddElbowPoles (bodyPoints, fullBody);
                }
            }
            if (version_ >= 1 && version_ < 4) {
                // 版 4: 全身 IK の点をつかむ間も出す骨を選べるようにした。今の定義で解かない骨（点が無い骨）を出す（版 0 からは SetFullBodyDefaults が入れる）
                foreach (FkControl control in fkControls) {
                    if (control != null) control.fullBodyHandle = !control.fullBodySolve;
                }
            }
            version_ = kVersion;
        }

        /// <summary>版の番号（テスト用）</summary>
        internal int version
        {
            get { return version_; }
            set { version_ = value; }
        }

        /// <summary>古い版なら足りない項目を埋める（テスト用。普段は読み込み時に自動で呼ばれる）</summary>
        internal void UpgradeIfNeeded ()
        {
            Upgrade ();
        }

        /// <summary>
        /// 定義のうち、全身 IK の姿勢を変える設定のハッシュ（焼いた版が今の定義で焼いたものかを見分ける）。
        /// 2 本骨 IK の定義では空（全身 IK の設定は姿勢に効かない）
        /// </summary>
        public string GetFullBodyHash ()
        {
            if (solver != RigSolver.FullBodyIk) return "";
            System.Text.StringBuilder text = new System.Text.StringBuilder ();
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
            FullBodySettings settings = fullBody ?? new FullBodySettings ();
            text.Append (settings.iterations).Append ('/').Append (settings.pull.ToString ("R", culture)).Append ('/').Append (settings.hipsPin.ToString ("R", culture));
            foreach (FkControl control in fkControls) {
                if (control == null) continue;
                text.Append ('|').Append ((int)control.bone).Append (control.fullBodySolve ? 's' : '-').Append (control.stiffness.ToString ("R", culture));
                if (control.hinge) {
                    text.Append ('h').Append (control.bendDirection.x.ToString ("R", culture)).Append (',')
                        .Append (control.bendDirection.y.ToString ("R", culture)).Append (',').Append (control.bendDirection.z.ToString ("R", culture));
                }
            }
            if (bodyPoints != null) {
                foreach (BodyPoint point in bodyPoints) {
                    if (point == null || !point.enabled) continue;
                    text.Append ("|p").Append (point.name).Append (',').Append ((int)point.bone).Append (',').Append ((int)point.anchor).Append (',')
                        .Append (point.offset.x.ToString ("R", culture)).Append (',').Append (point.offset.y.ToString ("R", culture)).Append (',')
                        .Append (point.offset.z.ToString ("R", culture)).Append (point.rotation ? 'r' : '-');
                    // 引く強さ 0 の点（S28）だけ足す（1 の点だけの定義は、項目を足す前と同じハッシュのまま。焼き直しが要る扱いにしない）
                    if (point.pullStrength <= 0) text.Append ('a');
                    // 向きを混ぜる点（S34）だけ足す（混ぜない定義は、項目を足す前と同じハッシュのまま）
                    if (point.blends) text.Append ('b').Append ((int)point.blendBone).Append (',').Append (point.blend.ToString ("R", culture));
                }
            }
            return Hash128.Compute (text.ToString ()).ToString ();
        }

        /// <summary>
        /// 全身 IK の骨ごとの既定値。硬さは体の中心ほど強くする（全部同じだと、手を引いたときにてこの長い腰・背骨ほど大きく回る）
        /// </summary>
        public static void SetFullBodyDefaults (FkControl control)
        {
            HumanBodyBones bone = control.bone;
            bool solve = IsFullBodyBone (bone);
            control.fullBodySolve = solve;
            control.fullBodyPoint = solve;
            control.fullBodyHandle = !solve;
            control.stiffness = DefaultStiffness (bone);
            // 肘は後ろ、膝は前へ曲がる
            bool elbow = bone == HumanBodyBones.LeftLowerArm || bone == HumanBodyBones.RightLowerArm;
            bool knee = bone == HumanBodyBones.LeftLowerLeg || bone == HumanBodyBones.RightLowerLeg;
            control.hinge = elbow || knee;
            control.bendDirection = elbow ? Vector3.back : knee ? Vector3.forward : Vector3.zero;
        }

        /// <summary>全身 IK で解く骨か（指・目・顎は解かない）</summary>
        public static bool IsFullBodyBone (HumanBodyBones bone)
        {
            if (bone == HumanBodyBones.LeftEye || bone == HumanBodyBones.RightEye || bone == HumanBodyBones.Jaw) return false;
            if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.RightLittleDistal) return false;
            return bone >= 0 && bone < HumanBodyBones.LastBone;
        }

        /// <summary>
        /// 既定の点の一覧:
        /// - 解く骨ごとに、骨の付け根の点（手・足首・頭・腰は向きも持つ）。つま先の骨には置かない（足の点で足りる。contactPoints のとき）
        /// - 腰・胸・頭の前の点と頭のてっぺんの点、膝の Pole（directionPoints）。顔の前の点は目の高さ（Eyes）。胸は UpperChest に置き、無いキャラでは親の骨（Chest・Spine）へ乗る。
        ///   引く強さは 0（骨を回して向けるだけ。S28）。膝の Pole は太ももに乗せて膝の前に置き、太ももを回して膝の向きを決める。
        ///   膝の Pole のずれの向きは、太ももと脛の向きを半分ずつ混ぜる（S34。膝を曲げても膝が出る向きに置かれる）。
        ///   肘の Pole（S43）は上腕に乗せて肘の後ろに置き、同じく上腕と前腕の向きを半分ずつ混ぜる。版 8 で足したので一覧の最後に置く
        /// - 足の踵・足裏の中心・小指側・つま先の先と、手の人差し指と小指の付け根（contactPoints）。踵と足裏の中心は足に乗り、足首とつなぐ。
        ///   小指側は足に乗る向けるだけの点（引く強さ 0）で、足を回して傾ける・ひねるのに使い、足裏の中心とつなぐ。
        ///   つま先の先はつま先の骨に置き（無いキャラでは足へ乗る）、足裏の中心とつなぐ
        /// </summary>
        public static List<BodyPoint> CreateDefaultBodyPoints (IEnumerable<FkControl> fk, FullBodySettings settings)
        {
            settings = settings ?? new FullBodySettings ();
            List<BodyPoint> points = new List<BodyPoint> ();
            foreach (FkControl control in fk) {
                if (control == null || !control.fullBodySolve || !control.fullBodyPoint) continue;
                bool toes = control.bone == HumanBodyBones.LeftToes || control.bone == HumanBodyBones.RightToes;
                if (toes && settings.contactPoints) continue;
                points.Add (new BodyPoint { name = control.bone.ToString (), bone = control.bone, rotation = PointHasRotation (control.bone) });
            }
            if (settings.directionPoints) {
                // 向きの点は、骨を回して向けるだけ（引く強さ 0。S28）。顔の向きを変えても首・胴を引かない
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.UpperChest, HumanBodyBones.Head }) {
                    // 顔の前の点は目の高さ（頭を目線で向ける）
                    points.Add (new BodyPoint {
                        name = bone + "Direction", bone = bone, anchor = bone == HumanBodyBones.Head ? BodyPointAnchor.Eyes : BodyPointAnchor.BoneOrigin,
                        offset = GetDirectionOffset (bone, settings.directionLength), pullStrength = 0,
                    });
                }
                points.Add (new BodyPoint { name = HumanBodyBones.Head + "Top", bone = HumanBodyBones.Head, offset = new Vector3 (0, settings.directionLength, 0), pullStrength = 0 });
                foreach (string side in new[] { "Left", "Right" }) {
                    HumanBodyBones thigh = side == "Left" ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
                    HumanBodyBones shin = side == "Left" ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                    BodyPoint pole = new BodyPoint {
                        name = side + "KneePole", bone = thigh, anchor = BodyPointAnchor.Knee, offset = new Vector3 (0, 0, settings.directionLength),
                        pullStrength = 0, link = shin.ToString (),
                    };
                    SetKneePoleBlend (pole);
                    points.Add (pole);
                }
            }
            if (settings.contactPoints) {
                foreach (string side in new[] { "Left", "Right" }) {
                    HumanBodyBones foot = side == "Left" ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                    HumanBodyBones toes = side == "Left" ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
                    HumanBodyBones hand = side == "Left" ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                    points.Add (new BodyPoint { name = side + "Heel", bone = foot, anchor = BodyPointAnchor.Heel });
                    points.Add (new BodyPoint { name = side + "SoleBall", bone = foot, anchor = BodyPointAnchor.SoleBall });
                    points.Add (new BodyPoint {
                        name = side + "LittleToeSide", bone = foot, anchor = BodyPointAnchor.LittleToeSide, link = side + "SoleBall",
                    });
                    points.Add (new BodyPoint { name = side + "ToeTip", bone = toes, anchor = BodyPointAnchor.ToeTip, link = side + "SoleBall" });
                    points.Add (new BodyPoint { name = side + "PalmIndex", bone = hand, anchor = BodyPointAnchor.IndexBase });
                    points.Add (new BodyPoint { name = side + "PalmLittle", bone = hand, anchor = BodyPointAnchor.LittleBase });
                }
            }
            // 肘の Pole は一覧の最後に置く（版 7 までの定義に足すときと同じ並びにする）
            if (settings.directionPoints) AddElbowPoles (points, settings);
            return points;
        }

        /// <summary>
        /// 肘の Pole（S43）を足す。上腕に乗せて肘の後ろ（基準姿勢で肘が向く方）に置き、引く強さ 0 の向けるだけの点にする。
        /// 膝の Pole と同じく、ずれは上腕と前腕の向きを半分ずつ混ぜた向きで回す（肘を曲げても、肘の曲がりの外側に出る）。同じ名前の点があれば足さない
        /// </summary>
        static void AddElbowPoles (List<BodyPoint> points, FullBodySettings settings)
        {
            foreach (string side in new[] { "Left", "Right" }) {
                string name = side + "ElbowPole";
                if (points.Exists (p => p != null && p.name == name)) continue;
                points.Add (new BodyPoint {
                    name = name, bone = side == "Left" ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, anchor = BodyPointAnchor.Elbow,
                    offset = new Vector3 (0, 0, -settings.directionLength), pullStrength = 0, link = side + "LowerArm",
                    blendBone = side == "Left" ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, blend = kElbowPoleBlend,
                });
            }
        }

        /// <summary>肘の Pole の、前腕の向きを混ぜる割合（S43。膝の Pole と同じ）</summary>
        public const float kElbowPoleBlend = 0.5f;

        /// <summary>
        /// 膝の Pole（既定の一覧の名前・太ももに乗る・膝が基準）なら、ずれを太ももと脛の向きを半分ずつ混ぜた向きで回すようにする（S34）。
        /// 太ももだけの向きだと、膝を曲げたとき Pole が太ももの前（腿を上げると膝の真上）へ回り、膝の外側から大きく外れる。
        /// 半分ずつなら、膝の曲がりの二等分の向き（膝が出る向き）に出る。混ぜる設定が入っている点は変えない
        /// </summary>
        static void SetKneePoleBlend (BodyPoint point)
        {
            bool left = point.name == "LeftKneePole" && point.bone == HumanBodyBones.LeftUpperLeg;
            bool right = point.name == "RightKneePole" && point.bone == HumanBodyBones.RightUpperLeg;
            if ((!left && !right) || point.anchor != BodyPointAnchor.Knee || point.blend > 0) return;
            point.blendBone = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            point.blend = kKneePoleBlend;
        }

        /// <summary>膝の Pole の、脛の向きを混ぜる割合（S34）</summary>
        public const float kKneePoleBlend = 0.5f;

        /// <summary>全身 IK の点が向きも持つ骨か（手・足首・頭・腰。ほかは位置だけ）</summary>
        public static bool PointHasRotation (HumanBodyBones bone)
        {
            switch (bone) {
                case HumanBodyBones.Hips:
                case HumanBodyBones.Head:
                case HumanBodyBones.LeftHand:
                case HumanBodyBones.RightHand:
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.RightFoot:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 向きの点の、骨からの位置（体の向き x 右・y 上・z 前。humanScale に対する割合）。顔の前の点の高さは置き場所の基準（目の高さ）で決める
        /// </summary>
        public static Vector3 GetDirectionOffset (HumanBodyBones bone, float length)
        {
            return new Vector3 (0, 0, length);
        }

        static float DefaultStiffness (HumanBodyBones bone)
        {
            switch (bone) {
                case HumanBodyBones.Hips: return 0.95f;
                case HumanBodyBones.Spine: return 0.8f;
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest: return 0.6f;
                case HumanBodyBones.Neck: return 0.5f;
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.RightShoulder: return 0.5f;
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.RightLowerLeg: return 0.1f;
            }
            return kDefaultStiffness;
        }

        /// <summary>定義を直したことを知らせる（スクリプトから書き換えたとき。インスペクタでの変更は OnValidate が知らせる）</summary>
        public static void NotifyChanged (EditRigDefinition definition)
        {
            if (changed != null && definition != null) changed (definition);
        }

        /// <summary>
        /// メニューから作ったとき・インスペクタの Reset で、空ではなく既定の中身で始める
        /// </summary>
        void Reset ()
        {
            SetDefault ();
        }

        static IkChain Leg (string name, HumanBodyBones root, HumanBodyBones mid, HumanBodyBones tip, HumanBodyBones toes)
        {
            IkChain chain = Chain (name, root, mid, tip, Vector3.forward);
            chain.reverseFoot = true;
            chain.toes = toes;
            return chain;
        }

        static IkChain Chain (string name, HumanBodyBones root, HumanBodyBones mid, HumanBodyBones tip, Vector3 defaultHint)
        {
            return new IkChain { name = name, root = root, mid = mid, tip = tip, defaultHint = defaultHint };
        }

        /// <summary>
        /// 定義の誤り（名前の重複・パスに使えない名前・IK の骨の重複など）。空なら使える
        /// </summary>
        public List<string> Validate ()
        {
            List<string> errors = new List<string> ();

            foreach (IGrouping<HumanBodyBones, FkControl> group in fkControls.GroupBy (c => c.bone)) {
                if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_FK_BONE", group.Key));
            }
            foreach (FkControl control in fkControls) {
                if (control.bone < 0 || control.bone >= HumanBodyBones.LastBone) errors.Add (Tr ("EDIT_RIG_DEFINITION_FK_BONE_OUT_OF_RANGE", control.bone));
                if (control.stiffness < 0 || control.stiffness > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_STIFFNESS_OUT_OF_RANGE", control.bone));
                if (control.hinge && control.bendDirection.sqrMagnitude < 1e-6f) errors.Add (Tr ("EDIT_RIG_DEFINITION_ZERO_BEND_DIRECTION", control.bone));
            }
            if (fullBody != null) {
                if (fullBody.iterations < 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_ITERATIONS_TOO_LOW"));
                if (fullBody.pull < 0 || fullBody.pull > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_PULL_OUT_OF_RANGE"));
                if (fullBody.hipsPin < 0 || fullBody.hipsPin > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_HIPS_PIN_OUT_OF_RANGE"));
            }
            if (bodyPoints != null) {
                foreach (IGrouping<string, BodyPoint> group in bodyPoints.Where (p => p != null).GroupBy (p => p.name)) {
                    if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_POINT_NAME", group.Key));
                }
                foreach (BodyPoint point in bodyPoints) {
                    if (point == null) continue;
                    if (!RigPaths.IsValidName (point.name)) errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_POINT_NAME", point.name));
                    if (point.bone < 0 || point.bone >= HumanBodyBones.LastBone) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BONE_OUT_OF_RANGE", point.name));
                    if (point.anchor != BodyPointAnchor.BoneOrigin && point.anchor != BodyPointAnchor.Eyes && RigPaths.SideOf (point.bone) == "") {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_ANCHOR_NEEDS_SIDE", point.anchor, point.name));
                    }
                    if (point.pullStrength <= 0 && point.anchor == BodyPointAnchor.BoneOrigin && point.offset == Vector3.zero) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_ORIGIN_POINT_NEEDS_PULL", point.name));
                    }
                    if (point.blend < 0 || point.blend > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_OUT_OF_RANGE", point.name));
                    if (point.blend > 0 && (point.blendBone < 0 || point.blendBone >= HumanBodyBones.LastBone)) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_BONE_OUT_OF_RANGE", point.name));
                    }
                    // 引く強さ 1 の点は、骨を剛体として引き合う解き方なので、2 本の骨の向きで決まる場所は扱えない
                    if (point.blends && point.pullStrength > 0) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_NEEDS_AIM", point.name));
                }
            }

            foreach (IGrouping<string, IkChain> group in ikChains.GroupBy (c => c.name)) {
                if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_IK_NAME", group.Key));
            }
            foreach (IkChain chain in ikChains) {
                if (!RigPaths.IsValidName (chain.name)) errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_IK_NAME", chain.name));
                if (chain.root == chain.mid || chain.mid == chain.tip || chain.root == chain.tip) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_IK_BONE", chain.name));
                if (chain.defaultHint.sqrMagnitude < 1e-6f) errors.Add (Tr ("EDIT_RIG_DEFINITION_ZERO_DEFAULT_HINT", chain.name));
                if (chain.reverseFoot && (chain.toes < 0 || chain.toes >= HumanBodyBones.LastBone || chain.toes == chain.root || chain.toes == chain.mid || chain.toes == chain.tip)) {
                    errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_TOE_BONE", chain.name, chain.toes));
                }
                if (chain.reverseFoot && chain.toeBreak < 0) errors.Add (Tr ("EDIT_RIG_DEFINITION_NEGATIVE_TOE_BREAK", chain.name));
                if (chain.reverseFoot && chain.pivots != null) {
                    foreach (IGrouping<ReverseFoot.Pivot, FootPivot> group in chain.pivots.GroupBy (p => p.pivot)) {
                        if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_PIVOT", chain.name, group.Key));
                    }
                }
            }

            foreach (IGrouping<string, ExtraControl> group in extraControls.GroupBy (c => c.name)) {
                if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_EXTRA_NAME", group.Key));
            }
            foreach (ExtraControl control in extraControls) {
                if (!RigPaths.IsValidName (control.name)) errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_EXTRA_NAME", control.name));
                if (string.IsNullOrEmpty (control.pattern)) {
                    errors.Add (Tr ("EDIT_RIG_DEFINITION_EMPTY_EXTRA_PATTERN", control.name));
                    continue;
                }
                try {
                    new System.Text.RegularExpressions.Regex (control.pattern);
                }
                catch (System.ArgumentException e) {
                    errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_EXTRA_PATTERN", control.name, e.Message));
                }
            }
            return errors;
        }
    }

    /// <summary>
    /// 編集用リグの階層パス（＝編集用クリップのカーブのパス）。定義から決まる固定名で、キャラの骨名は使わない
    /// </summary>
    public static class RigPaths
    {
        public const string kRoot = "Controls";
        public const string kFk = kRoot + "/FK";
        public const string kIk = kRoot + "/IK";
        public const string kExtra = kRoot + "/Extra";
        /// <summary>全身 IK の点（S25）。骨の名前は HumanBodyBones</summary>
        public const string kBody = kRoot + "/Body";
        public const string kTarget = "Target";
        public const string kHint = "Hint";

        public static string Fk (HumanBodyBones bone)
        {
            return kFk + "/" + bone;
        }

        public static string Body (HumanBodyBones bone)
        {
            return kBody + "/" + bone;
        }

        /// <summary>全身 IK の向きの点（骨の前に出した点）</summary>
        public static string BodyDirection (HumanBodyBones bone)
        {
            return kBody + "/" + bone + "Direction";
        }

        /// <summary>全身 IK の点（名前はリグ定義の点の一覧の名前）</summary>
        public static string BodyPoint (string name)
        {
            return kBody + "/" + name;
        }

        /// <summary>骨の名前の左右（Left / Right。中心の骨は空）</summary>
        public static string SideOf (HumanBodyBones bone)
        {
            string name = bone.ToString ();
            return name.StartsWith ("Left") ? "Left" : name.StartsWith ("Right") ? "Right" : "";
        }

        public static string IkChain (string name)
        {
            return kIk + "/" + name;
        }

        public static string IkTarget (string name)
        {
            return IkChain (name) + "/" + kTarget;
        }

        public static string IkHint (string name)
        {
            return IkChain (name) + "/" + kHint;
        }

        public static string Extra (string name)
        {
            return kExtra + "/" + name;
        }

        /// <summary>
        /// ゲームの Rig の値の代理（Controls/Game/&lt;Rig 名&gt;）。Rig の下の物は、ゲームと同じ相対パスで並べる
        /// </summary>
        public static string Game (string rigName, string relativePath = null)
        {
            string path = kGame + "/" + rigName;
            return string.IsNullOrEmpty (relativePath) ? path : path + "/" + relativePath;
        }

        public const string kGame = kRoot + "/Game";

        /// <summary>
        /// 任意のプロパティ（S6）の置き場所。表示モデル（ゲーム prefab の Animator）から見たパスを後ろに付ける（Controls/Props/Body/Face）
        /// </summary>
        public const string kProps = kRoot + "/Props";

        public static string Props (string displayPath)
        {
            return string.IsNullOrEmpty (displayPath) ? kProps : kProps + "/" + displayPath;
        }

        /// <summary>
        /// 任意のプロパティのパスなら、表示モデルから見たパスを返す（違えば null）
        /// </summary>
        public static string FromProps (string path)
        {
            if (path == kProps) return "";
            return path != null && path.StartsWith (kProps + "/") ? path.Substring (kProps.Length + 1) : null;
        }

        /// <summary>
        /// パスの 1 段に使える名前か（空・スラッシュ・前後の空白は不可）
        /// </summary>
        public static bool IsValidName (string name)
        {
            return !string.IsNullOrEmpty (name) && name.IndexOf ('/') < 0 && name.Trim () == name;
        }
    }

}
