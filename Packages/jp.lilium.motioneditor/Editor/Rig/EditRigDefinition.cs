using UnityEngine;
using UnityEditor;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの定義。キャラの骨名ではなく HumanBodyBones で書くので、1 つの定義を全キャラで使える
    /// （キャラへの割り当ては RigBinding が Avatar から引く）。
    /// 定義は骨の上位集合にする。キャラに無い骨のコントロールは、そのキャラでは無効になるだけで、クリップのカーブは消さない。
    /// キャラごとの調整（全身 IK の硬さ・点の場所など）も持てるので、キャラごとにアセットを作って使い分けてよい（S23）
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
            /// <summary>全身 IK の角度制限（S95）。FK のハンドルには掛けない</summary>
            public JointLimit limit = new JointLimit ();
            /// <summary>
            /// 全身 IK の重さ（回しにくさ）を、この骨の分だけにするか（S96）。既定は、この骨から先の骨を全部足す（根ほど重い。人の胴が腕より回しにくくなる）。
            /// 鎖・尻尾のような一列の骨を両端で留めて曲げると、足した重さで根元が回りにくく、曲がりが先の軽い骨に集まる。足さなければ全体に配られる
            /// </summary>
            [Tooltip (kOwnWeightOnlyTooltip)]
            public bool ownWeightOnly;

            /// <summary>Humanoid で必須の骨か（無ければ Avatar が作れないので、どのキャラにもある）</summary>
            public bool required
            {
                get { return bone >= 0 && bone < HumanBodyBones.LastBone && HumanTrait.RequiredBone ((int)bone); }
            }
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
            /// <summary>全身 IK の点をつかむ間も、ハンドルを出すか（解かない追加のコントロールには点が無いので、既定は出す）</summary>
            public bool fullBodyHandle = true;
            /// <summary>全身 IK で解く骨か（S93。解く骨には点（BodyPoint.control）を乗せられる。Humanoid でないキャラの体はこれで作る）</summary>
            public bool fullBodySolve;
            /// <summary>全身 IK の硬さ（0〜1）。FK の同じ項目と同じ</summary>
            [Range (0, 1)]
            public float stiffness = kDefaultStiffness;
            /// <summary>一方向にだけ曲がる関節か。FK の同じ項目と同じ</summary>
            public bool hinge;
            /// <summary>蝶番の曲がる向き。関節が出る向きを、体の向き（x 右・y 上・z 前）で書く</summary>
            public Vector3 bendDirection;
            /// <summary>全身 IK の角度制限（S95）。FK の同じ項目と同じ</summary>
            public JointLimit limit = new JointLimit ();
            /// <summary>全身 IK の重さを、この骨の分だけにするか（S96）。FK の同じ項目と同じ</summary>
            [Tooltip (kOwnWeightOnlyTooltip)]
            public bool ownWeightOnly;
        }

        const string kOwnWeightOnlyTooltip = "Full-body IK weighs this bone by itself instead of with every bone beyond it. Turn on for a row of bones held at both ends (a chain, a tail) so the bend spreads along it instead of gathering at the light end.";

        /// <summary>角度制限の軸ごとの決め方</summary>
        public enum LimitMode
        {
            /// <summary>制限しない</summary>
            Free,
            /// <summary>最小〜最大の範囲。越えると遊びの分だけ徐々に回しにくくなる</summary>
            Range,
            /// <summary>回さない（遊びも使わない）</summary>
            Locked,
        }

        /// <summary>角度制限の 1 軸（体の向きの軸まわりの回り。度）</summary>
        [System.Serializable]
        public sealed class AxisLimit
        {
            public LimitMode mode;
            public float min = -45;
            public float max = 45;

            public AxisLimit Clone ()
            {
                return new AxisLimit { mode = mode, min = min, max = max };
            }
        }

        /// <summary>
        /// 全身 IK の角度制限（S95）。親の解く骨から見た、T ポーズからの回りを、T ポーズの体の向きの軸（x 右・y 上・z 前）で書く
        /// （骨の軸の取り方に依らず、全キャラで同じ意味）。T ポーズの骨の向きにいちばん近い軸がひねり（腕は x、脚・背骨は y、足は z）、
        /// 残り 2 軸が振り。範囲を越えると、越えた量が遊び（slack）に近づくほど回しにくくなり、範囲 + 遊びで止まる。
        /// 元の姿勢（キーの姿勢）が範囲の外なら、そこまでは許す
        /// </summary>
        [System.Serializable]
        public sealed class JointLimit
        {
            [Tooltip ("Rotation about the body's right axis, relative to the T-pose as seen from the parent bone (degrees).")]
            public AxisLimit x = new AxisLimit ();
            [Tooltip ("Rotation about the body's up axis, relative to the T-pose as seen from the parent bone (degrees).")]
            public AxisLimit y = new AxisLimit ();
            [Tooltip ("Rotation about the body's forward axis, relative to the T-pose as seen from the parent bone (degrees).")]
            public AxisLimit z = new AxisLimit ();
            /// <summary>遊び（度）。範囲を越えてから止まるまでの角度。この間は越えるほど回しにくくなる</summary>
            [Min (0)]
            [Tooltip ("Play beyond the range (degrees). Past the range the joint gets gradually harder to turn and stops at range + slack.")]
            public float slack = kDefaultLimitSlack;

            /// <summary>どれかの軸を制限しているか</summary>
            public bool any
            {
                get { return x.mode != LimitMode.Free || y.mode != LimitMode.Free || z.mode != LimitMode.Free; }
            }

            public AxisLimit this[int axis]
            {
                get { return axis == 0 ? x : axis == 1 ? y : z; }
            }

            /// <summary>
            /// 左右の反対側の骨の値（体の左右の面で鏡写し）。回転ベクトルは面に垂直な x がそのまま、y・z は符号が変わるので、
            /// y・z の範囲は最小と最大を入れ替えて符号を変える
            /// </summary>
            public JointLimit Mirrored ()
            {
                return new JointLimit {
                    x = x.Clone (),
                    y = new AxisLimit { mode = y.mode, min = -y.max, max = -y.min },
                    z = new AxisLimit { mode = z.mode, min = -z.max, max = -z.min },
                    slack = slack,
                };
            }

            public JointLimit Clone ()
            {
                return new JointLimit { x = x.Clone (), y = y.Clone (), z = z.Clone (), slack = slack };
            }
        }

        /// <summary>角度制限の遊びの既定（度。11-03 ユーザー指示「遊びのディフォルトは 15° ぐらい」）</summary>
        public const float kDefaultLimitSlack = 15;

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
            /// 乗る骨を追加コントロールの名前で指す（S93。人型でない骨）。空でなければ bone より優先する。
            /// 解かないコントロールなら、Transform の親をたどって最初に当たる解くコントロールへ乗る。場所は乗る骨の付け根 + offset（と ground・radius）で、origin・toward は使わない
            /// </summary>
            public string control;
            /// <summary>ずれの向きを混ぜる相手を追加コントロールの名前で指す（S93）。空でなければ blendBone より優先する</summary>
            public string blendControl;
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
            /// 重さ（0 か 1。S28・S88。10-27 までは「引く強さ」pullStrength）。1（重い点）は点を引くと骨ごと動き、ほかの骨も付いてくる。
            /// 0（軽い点。向きの点の既定）は重い点を動かさない: 重い点を動かない物として解くので、その向こうの点も動かず、間の骨だけが引かれる。
            /// 重い点のある骨の上の軽い点と、向きを混ぜる点（blend）は、乗る骨を回して点を目標の方へ向けるだけ。項目の無い古い定義は 1 で読まれる
            /// </summary>
            [Range (0, 1)]
            [FormerlySerializedAs ("pullStrength")]
            [Tooltip ("1: a heavy point; dragging it pulls its bone and the rest of the body along. 0: a light point (default for direction points); dragging it never moves heavy points, so nothing beyond them moves either.")]
            public int weight = 1;
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
            /// 重さ 0 の点だけで使える
            /// </summary>
            [Range (0, 1)]
            [Tooltip ("How much the offset turns with Blend Bone instead of the host bone (0: host bone only, 1: Blend Bone only). 0.5 keeps a knee pole between the thigh and the shin when the knee bends. Only for points with weight 0.")]
            public float blend;

            /// <summary>
            /// 接地の点か（足首・足裏の点）。読み込んだ動きの足を床に留める（PoseSession）・モーションの点検で床と滑りを見る（MotionLint）のは、この点だけ。
            /// 解き方は変わらない
            /// </summary>
            public bool contact;

            /// <summary>
            /// 床との当たりに使う球の半径（体の大きさ humanScale に対する割合。0 なら点そのもの）。Floor Clamp と検査（MotionLint）は、
            /// 点の高さから半径を引いた所（球の下の端）を床の高さと比べる。地面へ落とす点（ground）は、基準姿勢で球の下の端が地面に着くよう、半径だけ上に置く
            /// </summary>
            [Min (0)]
            [Tooltip ("Radius of the sphere used for contact with the floor, as a fraction of the character's human scale (0: the point itself). Floor Clamp and Motion Lint compare the bottom of the sphere with the floor. Ground points are raised by the radius so the sphere rests on the ground in the reference pose.")]
            public float radius;

            /// <summary>
            /// 動かせない点か（S89）。選べるが、つかむ・ギズモ・数値欄・状態の切り替えでは動かず、キーもトラックも持たない（灰色で描く）。
            /// 乗る骨の上でも、親の骨から見ても動かない。付け根から離して置くと、骨は付け根と点を結ぶ軸まわりにだけ回る（鎖骨の付け根から前後にずらすと、肩が上下に回る）。
            /// どの点を引くときも、キーから解き直すときも同じ。軸から外れる向きに引くと、親の骨が回って受ける
            /// </summary>
            [Tooltip ("A point that can be selected but not moved, and gets no keys or tracks (drawn gray). It stays fixed on its bone and relative to the parent bone, so away from the bone's root the bone only turns about the axis from the root to the point. Pulls across that axis turn the parent bone.")]
            public bool passive;

            /// <summary>乗る骨の付け根そのものの点か（基準の骨も d も無く、ずれも 0）</summary>
            public bool onBoneOrigin
            {
                get { return (origin == HumanBodyBones.LastBone || origin == bone) && toward == HumanBodyBones.LastBone && offset == Vector3.zero; }
            }

            /// <summary>ずれの向きを、ほかの骨と混ぜるか</summary>
            public bool blends
            {
                get {
                    if (blend <= 0) return false;
                    if (!string.IsNullOrEmpty (blendControl)) return blendControl != control;
                    return blendBone != bone;
                }
            }

            /// <summary>乗る骨を追加コントロールの名前で指す点か（S93）</summary>
            public bool onControl
            {
                get { return !string.IsNullOrEmpty (control); }
            }
        }

        public const float kDefaultStiffness = 0.2f;

        public FullBodySettings fullBody = new FullBodySettings ();
        /// <summary>
        /// Humanoid でないキャラの体の大きさ（メートル。S94。0 なら 1）。Humanoid の humanScale の代わりに、腰の位置・全身 IK の点の値をこれで割って持つので、
        /// 大きさの違うキャラで同じ編集用クリップを開くと、場所の動きが大きさの比で伸び縮みする。四足なら腰の高さなど、両方のキャラで同じ所を測る。Humanoid のキャラでは使わない
        /// </summary>
        [Min (0)]
        [Tooltip ("Body size in meters for a character without a Humanoid avatar (0: 1). Hip positions and full-body IK point values are stored divided by it, like humanScale for Humanoid characters, so a clip opened on a character of another size moves in proportion. Measure the same place on both characters, such as the hip height of a quadruped. Not used for Humanoid characters.")]
        public float bodyScale;
        public List<FkControl> fkControls = new List<FkControl> ();
        public List<ExtraControl> extraControls = new List<ExtraControl> ();
        /// <summary>全身 IK の点</summary>
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
        /// 既定の定義を一時の物として作る（保存しない）。Humanoid の骨を全部 FK にし（腰だけ位置も）、全身 IK の既定の点を置く
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
            fullBody = new FullBodySettings ();
            bodyPoints = CreateDefaultBodyPoints (fkControls);
        }

        /// <summary>
        /// 中身を、キャラの骨から作った定義にする（S93。前の中身は捨てる。Humanoid でないキャラ向け）。
        /// 骨ごとに解く追加コントロールと付け根の点を置く。一番上の骨は位置も持つ（全身 IK の根が 1 本になり、解いた位置がキーに残る）
        /// </summary>
        public void SetFromBones (Animator animator)
        {
            fkControls.Clear ();
            extraControls.Clear ();
            fullBody = new FullBodySettings ();
            bodyPoints = new List<BodyPoint> ();
            if (animator == null) return;
            foreach (Transform bone in FindBones (animator)) {
                bool top = !IsBelowBone (bone, animator);
                extraControls.Add (new ExtraControl {
                    name = bone.name,
                    pattern = "^" + System.Text.RegularExpressions.Regex.Escape (bone.name) + "$",
                    position = top,
                    fullBodySolve = true,
                    fullBodyHandle = false,
                });
                bodyPoints.Add (new BodyPoint { name = bone.name, control = bone.name, origin = HumanBodyBones.LastBone, toward = HumanBodyBones.LastBone });
            }
        }

        /// <summary>メッシュを動かす骨とその親（Animator の下）を、親が先の順で返す。メッシュが無ければ、Animator の下の Renderer の無い Transform 全部</summary>
        static List<Transform> FindBones (Animator animator)
        {
            HashSet<Transform> used = new HashSet<Transform> ();
            foreach (SkinnedMeshRenderer skin in animator.GetComponentsInChildren<SkinnedMeshRenderer> (true)) {
                foreach (Transform bone in skin.bones) {
                    for (Transform t = bone; t != null && t != animator.transform && used.Add (t); t = t.parent) { }
                }
            }
            List<Transform> bones = new List<Transform> ();
            foreach (Transform t in animator.GetComponentsInChildren<Transform> (true)) {
                if (t == animator.transform) continue;
                if (used.Count > 0 ? used.Contains (t) : t.GetComponent<Renderer> () == null) bones.Add (t);
            }
            return bones;
        }

        /// <summary>親にも骨があるか（Animator の直下の中間の物は骨に数えない）</summary>
        static bool IsBelowBone (Transform bone, Animator animator)
        {
            HashSet<Transform> bones = new HashSet<Transform> (FindBones (animator));
            for (Transform t = bone.parent; t != null && t != animator.transform; t = t.parent) {
                if (bones.Contains (t)) return true;
            }
            return false;
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
        /// 定義のうち、全身 IK の姿勢を変える設定のハッシュ（焼いた版が今の定義で焼いたものかを見分ける）
        /// </summary>
        public string GetFullBodyHash ()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder ();
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
            FullBodySettings settings = fullBody ?? new FullBodySettings ();
            text.Append (settings.iterations).Append ('/').Append (settings.pull.ToString ("R", culture)).Append ('/').Append (settings.hipsPin.ToString ("R", culture));
            // 体の大きさ（S94）は入れたときだけ足す（無い定義は、項目を足す前と同じハッシュのまま）
            if (bodyScale > 0) text.Append ("/s").Append (bodyScale.ToString ("R", culture));
            foreach (FkControl control in fkControls) {
                if (control == null) continue;
                text.Append ('|').Append ((int)control.bone).Append (control.fullBodySolve ? 's' : '-').Append (control.stiffness.ToString ("R", culture));
                if (control.hinge) {
                    text.Append ('h').Append (control.bendDirection.x.ToString ("R", culture)).Append (',')
                        .Append (control.bendDirection.y.ToString ("R", culture)).Append (',').Append (control.bendDirection.z.ToString ("R", culture));
                }
                AppendLimit (text, control.limit, culture);
                // 重さを自分の骨の分だけにする骨（S96）だけ足す（無い定義は、項目を足す前と同じハッシュのまま）
                if (control.ownWeightOnly) text.Append ('o');
            }
            // 解く追加コントロール（S93）だけ足す（無い定義は、項目を足す前と同じハッシュのまま）
            foreach (ExtraControl control in extraControls) {
                if (control == null || !control.fullBodySolve) continue;
                text.Append ("|e").Append (control.name).Append (',').Append (control.pattern).Append (',').Append (control.stiffness.ToString ("R", culture));
                if (control.hinge) AppendVector (text, control.bendDirection, culture);
                AppendLimit (text, control.limit, culture);
                if (control.ownWeightOnly) text.Append ('o');
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
                    // 重さ 0 の点（S28）だけ足す（1 の点だけの定義は、項目を足す前と同じハッシュのまま。焼き直しが要る扱いにしない）
                    if (point.weight <= 0) text.Append ('a');
                    // 向きを混ぜる点（S34）だけ足す（混ぜない定義は、項目を足す前と同じハッシュのまま）
                    if (point.blends) text.Append ('b').Append ((int)point.blendBone).Append (',').Append (point.blend.ToString ("R", culture));
                    // 半径のある点だけ足す（地面へ落とす点の場所が変わる。半径の無い定義は、項目を足す前と同じハッシュのまま）
                    if (point.radius > 0) text.Append ('c').Append (point.radius.ToString ("R", culture));
                    // 動かせない点（passive。S89）だけ足す（骨を軸まわりにだけ回すので、キーから解いた姿勢も変わる。無い定義は、項目を足す前と同じハッシュのまま）
                    if (point.passive) text.Append ('v');
                    // 名前で乗る点（S93）だけ足す（無い定義は、項目を足す前と同じハッシュのまま）
                    if (point.onControl) text.Append ('n').Append (point.control);
                    if (point.blends && !string.IsNullOrEmpty (point.blendControl)) text.Append ('m').Append (point.blendControl);
                }
            }
            return Hash128.Compute (text.ToString ()).ToString ();
        }

        /// <summary>角度制限（S95）は、制限している骨だけ足す（全部 Free の定義は、項目を足す前と同じハッシュのまま）</summary>
        static void AppendLimit (System.Text.StringBuilder text, JointLimit limit, System.Globalization.CultureInfo culture)
        {
            if (limit == null || !limit.any) return;
            text.Append ('l').Append (limit.slack.ToString ("R", culture));
            for (int axis = 0; axis < 3; axis++) {
                AxisLimit value = limit[axis];
                text.Append (',').Append ((int)value.mode);
                if (value.mode == LimitMode.Range) text.Append (':').Append (value.min.ToString ("R", culture)).Append (':').Append (value.max.ToString ("R", culture));
            }
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
        /// - 解く骨ごとに、骨の付け根の点（手・足首・頭・腰は向きも持つ）。つま先の骨（足の点で足りる）と肩（上腕の点で足りる）には置かない
        /// - 背骨（Spine・Chest・UpperChest）は骨ごとに置かず、胸の点 1 つにまとめる。キャラによって背骨の数が違っても、同じ名前の点が同じ場所（腰から頭の付け根への kChestHeight 倍）に
        ///   できるので、点の値をキャラの間で使い回せる。点は UpperChest に乗り、無いキャラでは親の骨（Chest・Spine）へ乗る。間の背骨は硬さの分だけ付いてくる（肩と同じ）
        /// - 胸の点の下に、お腹の点。腰と胸の点の真ん中（kStomachHeight）に置き、Spine に乗る。お腹と胸を別々に曲げられる
        /// - 腰・胸・頭の前の点と頭のてっぺんの点。顔の前の点は目の高さ。胸の前の点も胸の点と同じ骨に乗る。
        ///   重さは 0（骨を回して向けるだけ。S28）。膝・肘の Pole は置かない（膝・肘の点で足りる。S56）
        /// - 足の踵・足裏の中心・小指側・つま先の先と、手の人差し指と小指の付け根。踵と足裏の中心は足に乗り、足首とつなぐ。
        ///   小指側は足に乗る向けるだけの点（重さ 0）で、足を回して傾ける・ひねるのに使い、足裏の中心とつなぐ。
        ///   つま先の先はつま先の骨に乗り、足裏の中心とつなぐ。つま先・指の骨が無いキャラには置かない
        /// </summary>
        public static List<BodyPoint> CreateDefaultBodyPoints (IEnumerable<FkControl> fk)
        {
            List<BodyPoint> points = new List<BodyPoint> ();
            foreach (FkControl control in fk) {
                if (control == null || !control.fullBodySolve) continue;
                if (control.bone == HumanBodyBones.LeftToes || control.bone == HumanBodyBones.RightToes) continue;
                // 肩は点を持たず、上腕の点に引かれて硬さの分だけ付いてくる（解く骨のまま）
                if (control.bone == HumanBodyBones.LeftShoulder || control.bone == HumanBodyBones.RightShoulder) continue;
                // 背骨は下の胸の点 1 つにまとめる
                if (IsSpineBone (control.bone)) continue;
                bool foot = control.bone == HumanBodyBones.LeftFoot || control.bone == HumanBodyBones.RightFoot;
                points.Add (new BodyPoint { name = control.bone.ToString (), bone = control.bone, rotation = PointHasRotation (control.bone), contact = foot });
            }
            // 胸とお腹の点は、腰と頭の付け根（どちらも Humanoid で必須の骨）を結ぶ線の上に置く（首の骨は無いキャラがある）。
            // 線は腰 → お腹 → 胸とつなぐ。背骨が Spine だけのキャラでは両方が Spine に乗るので、骨の階層からは決められない。
            // 胸を先に並べるのは、同じ骨に乗ったとき首・上腕の線が胸へつながるように（骨を引く点は先に並んだ方。BodyPointTarget.Create）
            points.Add (new BodyPoint { name = kChestPoint, bone = HumanBodyBones.UpperChest, origin = HumanBodyBones.Hips, toward = HumanBodyBones.Head, along = kChestHeight, link = kStomachPoint });
            points.Add (new BodyPoint { name = kStomachPoint, bone = HumanBodyBones.Spine, origin = HumanBodyBones.Hips, toward = HumanBodyBones.Head, along = kStomachHeight, link = HumanBodyBones.Hips.ToString () });
            // 向きの点は、骨を回して向けるだけ（重さ 0。S28）。顔の向きを変えても首・胴を引かない
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.UpperChest, HumanBodyBones.Head }) {
                string owner = bone == HumanBodyBones.UpperChest ? kChestPoint : bone.ToString ();
                BodyPoint direction = new BodyPoint { name = owner + "Direction", bone = bone, offset = new Vector3 (0, 0, kDirectionLength), weight = 0 };
                if (bone == HumanBodyBones.Head) {
                    // 顔の前の点は目の高さ（頭を目線で向ける）。目の骨の高さだけを借りる。目の骨が無いキャラは決まった高さ
                    direction.toward = HumanBodyBones.LeftEye;
                    direction.along = 1;
                    direction.axes = Vector3.up;
                    direction.fallback = new Vector3 (0, kEyeHeight, 0);
                }
                points.Add (direction);
            }
            points.Add (new BodyPoint { name = HumanBodyBones.Head + "Top", bone = HumanBodyBones.Head, offset = new Vector3 (0, kDirectionLength, 0), weight = 0 });
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
            return points;
        }

        /// <summary>
        /// 足裏の点。足首から、つま先の骨の付け根への向き（水平だけ）の along 倍の所（across だけ外へ）を地面に置く。
        /// 床との当たりの球（kSoleRadius）を持ち、基準姿勢で球の下の端が地面に着く高さに置く
        /// </summary>
        static BodyPoint SolePoint (string name, HumanBodyBones bone, HumanBodyBones foot, HumanBodyBones toes, float along, float across, string link)
        {
            return new BodyPoint {
                name = name, bone = bone, origin = foot, toward = toes, along = along, across = across, axes = new Vector3 (1, 0, 1), ground = true, link = link, contact = true,
                radius = kSoleRadius,
            };
        }

        /// <summary>
        /// 足裏の点の、床との当たりの球の半径（humanScale に対する割合。10-22 ユーザー指示）。
        /// 足裏の点は靴底の中にあるとみなし、底からの厚みを球にする（体の大きさ 1 で 2cm）
        /// </summary>
        public const float kSoleRadius = 0.02f;

        /// <summary>背骨をまとめた胸の点の名前</summary>
        public const string kChestPoint = "Chest";
        /// <summary>
        /// 胸の点の高さ（腰から頭の付け根までの何倍の所か）。胴の真ん中に置く（10-11 ユーザー指示）。
        /// デモのキャラで Chest の付け根が 0.36〜0.38、UpperChest の付け根が 0.56〜0.58。乗る骨（UpperChest）の付け根より少し下になるが、骨に付いた点として解ける
        /// </summary>
        public const float kChestHeight = 0.5f;

        /// <summary>背骨の下側（お腹）の点の名前（10-19 ユーザー指示）</summary>
        public const string kStomachPoint = "Stomach";
        /// <summary>
        /// お腹の点の高さ（腰から頭の付け根までの何倍の所か）。腰と胸の点の真ん中。
        /// デモのキャラで Spine の付け根が 0.19〜0.20、Chest の付け根が 0.35〜0.38 なので、Spine の骨の上に来る
        /// </summary>
        public const float kStomachHeight = 0.25f;

        /// <summary>背骨（胸の点 1 つにまとめる骨）か</summary>
        static bool IsSpineBone (HumanBodyBones bone)
        {
            return bone == HumanBodyBones.Spine || bone == HumanBodyBones.Chest || bone == HumanBodyBones.UpperChest;
        }

        /// <summary>目の骨が無いキャラの、頭の付け根から目までの高さ（humanScale に対する割合。デモのキャラで 0.095）</summary>
        public const float kEyeHeight = 0.095f;
        /// <summary>向きの点を、骨からどれだけ離して置くか（humanScale に対する割合）</summary>
        public const float kDirectionLength = 0.25f;

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

        /// <summary>
        /// 定義の誤り（名前の重複・パスに使えない名前・範囲外の値など）。空なら使える
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
                ValidateLimit (control.limit, control.bone.ToString (), errors);
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
                    if (point.onControl) {
                        // 名前で乗る点は bone を使わない（S93）
                        if (!extraControls.Any (c => c.name == point.control)) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_UNKNOWN_CONTROL", point.name, point.control));
                    }
                    else if (point.bone < 0 || point.bone >= HumanBodyBones.LastBone) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BONE_OUT_OF_RANGE", point.name));
                    if (!string.IsNullOrEmpty (point.blendControl) && !extraControls.Any (c => c.name == point.blendControl)) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_UNKNOWN_CONTROL", point.name, point.blendControl));
                    }
                    if (point.origin < 0 || point.origin > HumanBodyBones.LastBone || point.toward < 0 || point.toward > HumanBodyBones.LastBone) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BONE_OUT_OF_RANGE", point.name));
                    }
                    if (point.blend < 0 || point.blend > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_OUT_OF_RANGE", point.name));
                    if (point.blend > 0 && string.IsNullOrEmpty (point.blendControl) && (point.blendBone < 0 || point.blendBone >= HumanBodyBones.LastBone)) {
                        errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_BONE_OUT_OF_RANGE", point.name));
                    }
                    // 重さ 1 の点は、骨を剛体として引き合う解き方なので、2 本の骨の向きで決まる場所は扱えない
                    if (point.blends && point.weight > 0) errors.Add (Tr ("EDIT_RIG_DEFINITION_POINT_BLEND_NEEDS_AIM", point.name));
                }
            }

            foreach (IGrouping<string, ExtraControl> group in extraControls.GroupBy (c => c.name)) {
                if (group.Count () > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_DUPLICATE_EXTRA_NAME", group.Key));
            }
            foreach (ExtraControl control in extraControls) {
                if (!RigPaths.IsValidName (control.name)) errors.Add (Tr ("EDIT_RIG_DEFINITION_INVALID_EXTRA_NAME", control.name));
                if (control.stiffness < 0 || control.stiffness > 1) errors.Add (Tr ("EDIT_RIG_DEFINITION_STIFFNESS_OUT_OF_RANGE", control.name));
                if (control.hinge && control.bendDirection.sqrMagnitude < 1e-6f) errors.Add (Tr ("EDIT_RIG_DEFINITION_ZERO_BEND_DIRECTION", control.name));
                ValidateLimit (control.limit, control.name, errors);
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

        static void ValidateLimit (JointLimit limit, string label, List<string> errors)
        {
            if (limit == null) return;
            if (limit.slack < 0) errors.Add (Tr ("EDIT_RIG_DEFINITION_LIMIT_SLACK_NEGATIVE", label));
            for (int axis = 0; axis < 3; axis++) {
                AxisLimit value = limit[axis];
                if (value.mode == LimitMode.Range && value.min > value.max) errors.Add (Tr ("EDIT_RIG_DEFINITION_LIMIT_MIN_ABOVE_MAX", label, "XYZ"[axis]));
            }
        }

        /// <summary>左右のある骨の、反対側の骨（中心の骨・左右の無い骨は null）</summary>
        public static HumanBodyBones? OppositeBone (HumanBodyBones bone)
        {
            string name = bone.ToString ();
            string other = name.StartsWith ("Left") ? "Right" + name.Substring (4) : name.StartsWith ("Right") ? "Left" + name.Substring (5) : null;
            HumanBodyBones result;
            return other != null && System.Enum.TryParse (other, out result) ? result : (HumanBodyBones?)null;
        }

        /// <summary>FK の骨（無ければ null）</summary>
        public FkControl FindFkControl (HumanBodyBones bone)
        {
            return fkControls.FirstOrDefault (c => c != null && c.bone == bone);
        }
    }

    /// <summary>
    /// 編集用リグの階層パス（＝編集用クリップのカーブのパス）。定義から決まる固定名で、キャラの骨名は使わない
    /// </summary>
    public static class RigPaths
    {
        public const string kRoot = "Controls";
        public const string kFk = kRoot + "/FK";
        public const string kExtra = kRoot + "/Extra";
        /// <summary>全身 IK の点（S25）。骨の名前は HumanBodyBones</summary>
        public const string kBody = kRoot + "/Body";

        public static string Fk (HumanBodyBones bone)
        {
            return kFk + "/" + bone;
        }

        public static string Body (HumanBodyBones bone)
        {
            return kBody + "/" + bone;
        }

        /// <summary>全身 IK の向きの点（骨の前に出した点）</summary>
        internal static string BodyDirection (HumanBodyBones bone)
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

        public static string Extra (string name)
        {
            return kExtra + "/" + name;
        }

        /// <summary>持ち物（S91）</summary>
        public const string kObject = kRoot + "/Object";

        public static string Object (string name)
        {
            return kObject + "/" + name;
        }

        /// <summary>持ち物の差す場所（Controls/Object/物の名前/場所の名前）。場所ごとに差しているかのカーブを持つ</summary>
        public static string ObjectSocket (string name, string socket)
        {
            return Object (name) + "/" + socket;
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
