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
            /// <summary>
            /// 置き場所の基準の骨（LastBone なら乗る骨）。基準の場所は、この骨の付け根 + along × d + across × (体の上 × d)。
            /// d は、この骨の付け根から toward の骨の付け根への向き（axes で成分を選ぶ）。キャラにこの骨が無ければ、点を作らない
            /// </summary>
            public HumanBodyBones origin = HumanBodyBones.LastBone;
            /// <summary>d の先の骨（LastBone なら d は 0 で、基準の場所は origin の付け根）</summary>
            public HumanBodyBones toward = HumanBodyBones.LastBone;
            /// <summary>d に掛ける割合（足の点なら、足首からつま先の骨までの長さの何倍の所か）</summary>
            public float along;
            /// <summary>d を体の上の軸まわりに回した向き（d の右）に掛ける割合。足の小指側の縁を、足の外へ出す</summary>
            public float across;
            /// <summary>d のうち残す成分（体の向き x 右・y 上・z 前ごとに、1 で残す・0 で落とす）。足の点は水平だけ (1, 0, 1)、目の高さは上だけ (0, 1, 0)</summary>
            public Vector3 axes = Vector3.one;
            /// <summary>基準の場所を地面（基準姿勢のルートの高さ）へ落とすか（足の点）</summary>
            public bool ground;
            /// <summary>toward の骨がキャラに無いときの d（体の向き。humanScale に対する割合）。0 なら点を作らない（つま先の骨が無いキャラの足の点など）</summary>
            public Vector3 fallback;
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

            /// <summary>乗る骨の付け根そのものの点か（基準の骨も d も無く、ずれも 0）</summary>
            public bool onBoneOrigin
            {
                get { return (origin == HumanBodyBones.LastBone || origin == bone) && toward == HumanBodyBones.LastBone && offset == Vector3.zero; }
            }

            /// <summary>ずれの向きを、ほかの骨と混ぜるか</summary>
            public bool blends
            {
                get { return blend > 0 && blendBone != bone; }
            }
        }

        public const float kDefaultStiffness = 0.2f;

        /// <summary>手足の位置を保つ仕組み（2 本骨 IK＋足の転がし か 全身 IK）</summary>
        public RigSolver solver = RigSolver.FullBodyIk;
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
            bodyPoints = CreateDefaultBodyPoints (fkControls);

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
            NotifyChanged (this);
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
                    text.Append ("|p").Append (point.name).Append (',').Append ((int)point.bone).Append (',').Append ((int)point.origin).Append (',')
                        .Append ((int)point.toward).Append (',').Append (point.along.ToString ("R", culture)).Append (',').Append (point.across.ToString ("R", culture));
                    AppendVector (text, point.axes, culture);
                    AppendVector (text, point.fallback, culture);
                    AppendVector (text, point.offset, culture);
                    text.Append (point.ground ? 'g' : '-').Append (point.rotation ? 'r' : '-');
                    // 引く強さ 0 の点（S28）だけ足す（1 の点だけの定義は、項目を足す前と同じハッシュのまま。焼き直しが要る扱いにしない）
                    if (point.pullStrength <= 0) text.Append ('a');
                    // 向きを混ぜる点（S34）だけ足す（混ぜない定義は、項目を足す前と同じハッシュのまま）
                    if (point.blends) text.Append ('b').Append ((int)point.blendBone).Append (',').Append (point.blend.ToString ("R", culture));
                }
            }
            return Hash128.Compute (text.ToString ()).ToString ();
        }

        static void AppendVector (System.Text.StringBuilder text, Vector3 value, System.Globalization.CultureInfo culture)
        {
            text.Append (',').Append (value.x.ToString ("R", culture)).Append (',').Append (value.y.ToString ("R", culture)).Append (',').Append (value.z.ToString ("R", culture));
        }

        /// <summary>
        /// 全身 IK の骨ごとの既定値。硬さは体の中心ほど強くする（全部同じだと、手を引いたときにてこの長い腰・背骨ほど大きく回る）
        /// </summary>
        public static void SetFullBodyDefaults (FkControl control)
        {
            HumanBodyBones bone = control.bone;
            bool solve = IsFullBodyBone (bone);
            control.fullBodySolve = solve;
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
        /// - 解く骨ごとに、骨の付け根の点（手・足首・頭・腰は向きも持つ）。つま先の骨には置かない（足の点で足りる）
        /// - 腰・胸・頭の前の点と頭のてっぺんの点、膝と肘の Pole。顔の前の点は目の高さ。胸は UpperChest に置き、無いキャラでは親の骨（Chest・Spine）へ乗る。
        ///   引く強さは 0（骨を回して向けるだけ。S28）。膝の Pole は太ももに乗せて膝の前、肘の Pole は上腕に乗せて肘の後ろに置き、
        ///   ずれの向きは脛・前腕の向きと半分ずつ混ぜる（S34・S43。曲げても関節が出る向きに置かれる）
        /// - 足の踵・足裏の中心・小指側・つま先の先と、手の人差し指と小指の付け根。踵と足裏の中心は足に乗り、足首とつなぐ。
        ///   小指側は足に乗る向けるだけの点（引く強さ 0）で、足を回して傾ける・ひねるのに使い、足裏の中心とつなぐ。
        ///   つま先の先はつま先の骨に乗り、足裏の中心とつなぐ。つま先・指の骨が無いキャラには置かない
        /// </summary>
        public static List<BodyPoint> CreateDefaultBodyPoints (IEnumerable<FkControl> fk)
        {
            List<BodyPoint> points = new List<BodyPoint> ();
            foreach (FkControl control in fk) {
                if (control == null || !control.fullBodySolve) continue;
                if (control.bone == HumanBodyBones.LeftToes || control.bone == HumanBodyBones.RightToes) continue;
                points.Add (new BodyPoint { name = control.bone.ToString (), bone = control.bone, rotation = PointHasRotation (control.bone) });
            }
            // 向きの点は、骨を回して向けるだけ（引く強さ 0。S28）。顔の向きを変えても首・胴を引かない
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.UpperChest, HumanBodyBones.Head }) {
                BodyPoint direction = new BodyPoint { name = bone + "Direction", bone = bone, offset = new Vector3 (0, 0, kDirectionLength), pullStrength = 0 };
                if (bone == HumanBodyBones.Head) {
                    // 顔の前の点は目の高さ（頭を目線で向ける）。目の骨の高さだけを借りる。目の骨が無いキャラは決まった高さ
                    direction.toward = HumanBodyBones.LeftEye;
                    direction.along = 1;
                    direction.axes = Vector3.up;
                    direction.fallback = new Vector3 (0, kEyeHeight, 0);
                }
                points.Add (direction);
            }
            points.Add (new BodyPoint { name = HumanBodyBones.Head + "Top", bone = HumanBodyBones.Head, offset = new Vector3 (0, kDirectionLength, 0), pullStrength = 0 });
            foreach (string side in new[] { "Left", "Right" }) {
                bool left = side == "Left";
                HumanBodyBones shin = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                points.Add (new BodyPoint {
                    name = side + "KneePole", bone = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, origin = shin,
                    offset = new Vector3 (0, 0, kDirectionLength), pullStrength = 0, link = shin.ToString (), blendBone = shin, blend = kPoleBlend,
                });
            }
            foreach (string side in new[] { "Left", "Right" }) {
                bool left = side == "Left";
                HumanBodyBones foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                HumanBodyBones toes = left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
                HumanBodyBones hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                // 足の点は、足首からつま先の骨への向き（水平だけ）に並べて地面に置く
                points.Add (SolePoint (side + "Heel", foot, foot, toes, -0.15f, 0, null));
                points.Add (SolePoint (side + "SoleBall", foot, foot, toes, 1, 0, null));
                // 小指側の縁は、母趾球の列から体の外へ（左足なら左）
                points.Add (SolePoint (side + "LittleToeSide", foot, foot, toes, 1, left ? -0.35f : 0.35f, side + "SoleBall"));
                points.Add (SolePoint (side + "ToeTip", toes, foot, toes, 1.45f, 0, side + "SoleBall"));
                // 手のひらの 2 点は指の付け根
                points.Add (new BodyPoint { name = side + "PalmIndex", bone = hand, origin = left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal });
                points.Add (new BodyPoint { name = side + "PalmLittle", bone = hand, origin = left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal });
            }
            foreach (string side in new[] { "Left", "Right" }) {
                bool left = side == "Left";
                HumanBodyBones forearm = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
                points.Add (new BodyPoint {
                    name = side + "ElbowPole", bone = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, origin = forearm,
                    offset = new Vector3 (0, 0, -kDirectionLength), pullStrength = 0, link = forearm.ToString (), blendBone = forearm, blend = kPoleBlend,
                });
            }
            return points;
        }

        /// <summary>
        /// 足裏の点。足首から、つま先の骨の付け根への向き（水平だけ）の along 倍の所（across だけ外へ）を地面に置く
        /// </summary>
        static BodyPoint SolePoint (string name, HumanBodyBones bone, HumanBodyBones foot, HumanBodyBones toes, float along, float across, string link)
        {
            return new BodyPoint {
                name = name, bone = bone, origin = foot, toward = toes, along = along, across = across, axes = new Vector3 (1, 0, 1), ground = true, link = link,
            };
        }

        /// <summary>目の骨が無いキャラの、頭の付け根から目までの高さ（humanScale に対する割合。デモのキャラで 0.095）</summary>
        public const float kEyeHeight = 0.095f;
        /// <summary>向きの点・Pole を、骨からどれだけ離して置くか（humanScale に対する割合）</summary>
        public const float kDirectionLength = 0.25f;
        /// <summary>膝・肘の Pole の、脛・前腕の向きを混ぜる割合（S34・S43）</summary>
        public const float kPoleBlend = 0.5f;

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
                    if (point.origin < 0 || point.origin > HumanBodyBones.LastBone || point.toward < 0 || point.toward > HumanBodyBones.LastBone) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BONE_OUT_OF_RANGE", point.name));
                    }
                    if (point.pullStrength <= 0 && point.onBoneOrigin) {
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
