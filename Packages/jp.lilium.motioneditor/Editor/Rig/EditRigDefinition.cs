using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

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
            /// <summary>向きの点（腰・胸・頭の前に出した点と、頭のてっぺんの点）を置くか</summary>
            [HideInInspector]
            public bool directionPoints = true;
            /// <summary>向きの点を、骨からどれだけ前へ離して置くか（体の大きさ humanScale に対する割合）</summary>
            [HideInInspector]
            public float directionLength = 0.25f;
            /// <summary>骨の上の点（足のかかと・つま先の先、手の人差し指側と小指側の 2 点）を置くか</summary>
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
        }

        public const float kDefaultStiffness = 0.2f;
        /// <summary>定義の形の版。古い版のアセットを読んだら、足りない項目を既定値で埋める</summary>
        public const int kVersion = 3;

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
            if (version_ < 3) {
                // 版 3: 点を一覧で持つようにした（S26）。版 2 までの「骨ごとに点を置くか・向きの点・骨の上の点」の設定から一覧を作る
                if (bodyPoints == null) bodyPoints = new List<BodyPoint> ();
                if (bodyPoints.Count == 0) bodyPoints = CreateDefaultBodyPoints (fkControls, fullBody);
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
            }
            if (bodyPoints != null) {
                foreach (BodyPoint point in bodyPoints) {
                    if (point == null || !point.enabled) continue;
                    text.Append ("|p").Append (point.name).Append (',').Append ((int)point.bone).Append (',').Append ((int)point.anchor).Append (',')
                        .Append (point.offset.x.ToString ("R", culture)).Append (',').Append (point.offset.y.ToString ("R", culture)).Append (',')
                        .Append (point.offset.z.ToString ("R", culture)).Append (point.rotation ? 'r' : '-');
                }
            }
            foreach (IkChain chain in ikChains) {
                if (chain == null) continue;
                text.Append ('|').Append ((int)chain.mid).Append (chain.defaultHint.x.ToString ("R", culture)).Append (',')
                    .Append (chain.defaultHint.y.ToString ("R", culture)).Append (',').Append (chain.defaultHint.z.ToString ("R", culture));
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
            control.stiffness = DefaultStiffness (bone);
        }

        /// <summary>全身 IK で解く骨か（指・目・顎は解かない）</summary>
        public static bool IsFullBodyBone (HumanBodyBones bone)
        {
            if (bone == HumanBodyBones.LeftEye || bone == HumanBodyBones.RightEye || bone == HumanBodyBones.Jaw) return false;
            if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.RightLittleDistal) return false;
            return bone >= 0 && bone < HumanBodyBones.LastBone;
        }

        /// <summary>
        /// 既定の点の一覧。名前は版 2 までのコントロールのパスと同じにする（今までの編集用クリップのキーをそのまま読む）:
        /// - 解く骨ごとに、骨の付け根の点（手・足首・頭・腰は向きも持つ）
        /// - 腰・胸・頭の前の点と頭のてっぺんの点（directionPoints）。胸は UpperChest に置き、無いキャラでは親の骨（Chest・Spine）へ乗る
        /// - 左右のかかと・つま先の先・人差し指と小指の付け根（contactPoints）。つま先の先はつま先の骨に置き、無いキャラでは足へ乗る
        /// </summary>
        public static List<BodyPoint> CreateDefaultBodyPoints (IEnumerable<FkControl> fk, FullBodySettings settings)
        {
            settings = settings ?? new FullBodySettings ();
            List<BodyPoint> points = new List<BodyPoint> ();
            foreach (FkControl control in fk) {
                if (control == null || !control.fullBodySolve || !control.fullBodyPoint) continue;
                points.Add (new BodyPoint { name = control.bone.ToString (), bone = control.bone, rotation = PointHasRotation (control.bone) });
            }
            if (settings.directionPoints) {
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.UpperChest, HumanBodyBones.Head }) {
                    points.Add (new BodyPoint { name = bone + "Direction", bone = bone, offset = GetDirectionOffset (bone, settings.directionLength) });
                }
                points.Add (new BodyPoint { name = HumanBodyBones.Head + "Top", bone = HumanBodyBones.Head, offset = new Vector3 (0, settings.directionLength, 0) });
            }
            if (settings.contactPoints) {
                foreach (string side in new[] { "Left", "Right" }) {
                    HumanBodyBones foot = side == "Left" ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
                    HumanBodyBones toes = side == "Left" ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
                    HumanBodyBones hand = side == "Left" ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                    points.Add (new BodyPoint { name = side + "Heel", bone = foot, anchor = BodyPointAnchor.Heel });
                    points.Add (new BodyPoint { name = side + "ToeTip", bone = toes, anchor = BodyPointAnchor.ToeTip });
                    points.Add (new BodyPoint { name = side + "PalmIndex", bone = hand, anchor = BodyPointAnchor.IndexBase });
                    points.Add (new BodyPoint { name = side + "PalmLittle", bone = hand, anchor = BodyPointAnchor.LittleBase });
                }
            }
            return points;
        }

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
        /// 向きの点の、骨からの位置（体の向き x 右・y 上・z 前。humanScale に対する割合）。頭は目の高さあたりへ少し上げる
        /// </summary>
        public static Vector3 GetDirectionOffset (HumanBodyBones bone, float length)
        {
            return bone == HumanBodyBones.Head ? new Vector3 (0, length * 0.3f, length) : new Vector3 (0, 0, length);
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
                if (group.Count () > 1) errors.Add ("FK の骨が重複している: " + group.Key);
            }
            foreach (FkControl control in fkControls) {
                if (control.bone < 0 || control.bone >= HumanBodyBones.LastBone) errors.Add ("FK の骨が範囲外: " + control.bone);
                if (control.stiffness < 0 || control.stiffness > 1) errors.Add ("全身 IK の硬さが 0〜1 の外: " + control.bone);
            }
            if (fullBody != null) {
                if (fullBody.iterations < 1) errors.Add ("全身 IK の解く回数が 1 未満");
                if (fullBody.pull < 0 || fullBody.pull > 1) errors.Add ("全身 IK の引く強さが 0〜1 の外");
                if (fullBody.hipsPin < 0 || fullBody.hipsPin > 1) errors.Add ("全身 IK の腰を留める強さが 0〜1 の外");
            }
            if (bodyPoints != null) {
                foreach (IGrouping<string, BodyPoint> group in bodyPoints.Where (p => p != null).GroupBy (p => p.name)) {
                    if (group.Count () > 1) errors.Add ("全身 IK の点の名前が重複している: " + group.Key);
                }
                foreach (BodyPoint point in bodyPoints) {
                    if (point == null) continue;
                    if (!RigPaths.IsValidName (point.name)) errors.Add ("全身 IK の点の名前がパスに使えない: '" + point.name + "'");
                    if (point.bone < 0 || point.bone >= HumanBodyBones.LastBone) errors.Add ("全身 IK の点の骨が範囲外: " + point.name);
                    bool foot = point.anchor == BodyPointAnchor.Heel || point.anchor == BodyPointAnchor.ToeTip;
                    bool hand = point.anchor == BodyPointAnchor.IndexBase || point.anchor == BodyPointAnchor.LittleBase;
                    if ((foot || hand) && RigPaths.SideOf (point.bone) == "") errors.Add ("全身 IK の点の基準（" + point.anchor + "）には左右のある骨が要る: " + point.name);
                }
            }

            foreach (IGrouping<string, IkChain> group in ikChains.GroupBy (c => c.name)) {
                if (group.Count () > 1) errors.Add ("IK の名前が重複している: " + group.Key);
            }
            foreach (IkChain chain in ikChains) {
                if (!RigPaths.IsValidName (chain.name)) errors.Add ("IK の名前がパスに使えない: '" + chain.name + "'");
                if (chain.root == chain.mid || chain.mid == chain.tip || chain.root == chain.tip) errors.Add ("IK の骨が重複している: " + chain.name);
                if (chain.defaultHint.sqrMagnitude < 1e-6f) errors.Add ("IK の既定のヒントが 0: " + chain.name);
                if (chain.reverseFoot && (chain.toes < 0 || chain.toes >= HumanBodyBones.LastBone || chain.toes == chain.root || chain.toes == chain.mid || chain.toes == chain.tip)) {
                    errors.Add ("足の転がしのつま先の骨が使えない: " + chain.name + "（" + chain.toes + "）");
                }
                if (chain.reverseFoot && chain.toeBreak < 0) errors.Add ("足の転がしの折れ角が負: " + chain.name);
                if (chain.reverseFoot && chain.pivots != null) {
                    foreach (IGrouping<ReverseFoot.Pivot, FootPivot> group in chain.pivots.GroupBy (p => p.pivot)) {
                        if (group.Count () > 1) errors.Add ("足の転がしの支点が重複している: " + chain.name + "（" + group.Key + "）");
                    }
                }
            }

            foreach (IGrouping<string, ExtraControl> group in extraControls.GroupBy (c => c.name)) {
                if (group.Count () > 1) errors.Add ("追加コントロールの名前が重複している: " + group.Key);
            }
            foreach (ExtraControl control in extraControls) {
                if (!RigPaths.IsValidName (control.name)) errors.Add ("追加コントロールの名前がパスに使えない: '" + control.name + "'");
                if (string.IsNullOrEmpty (control.pattern)) {
                    errors.Add ("追加コントロールのパターンが空: " + control.name);
                    continue;
                }
                try {
                    new System.Text.RegularExpressions.Regex (control.pattern);
                }
                catch (System.ArgumentException e) {
                    errors.Add ("追加コントロールのパターンが正規表現として読めない: " + control.name + "（" + e.Message + "）");
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
