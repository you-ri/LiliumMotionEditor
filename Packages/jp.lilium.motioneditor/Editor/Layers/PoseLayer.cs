using System.Collections.Generic;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 姿勢を作る段の種類。上から下へ、この順に解決する。
    /// User は、ゲーム側が [PoseLayer] で足した段
    /// </summary>
    public enum LayerKind
    {
        /// <summary>
        /// 編集用リグ。保存するクリップ（操作値・Rig の値・任意プロパティ）を持ち、値を当てて骨へ解く。
        /// 差分（非破壊編集）はこの段の下に Override の段として重ねる（09-20 ユーザー指示）
        /// </summary>
        EditingRig,
        /// <summary>元のクリップを書き換えずに重ねる差分のクリップ（S14）。1 枚が 1 段</summary>
        Override,
        /// <summary>編集用ボーンの姿勢</summary>
        GenericPose,
        /// <summary>Humanoid への変換</summary>
        HumanoidAnimation,
        /// <summary>キャラに組まれている Animation Rigging</summary>
        RigBuilder,
        /// <summary>ゲーム側が属性で足した段</summary>
        User,
        /// <summary>表示モデルの骨。出力なので値を持たない</summary>
        DisplayBone,
        /// <summary>一緒に見ている演出（Timeline）の中で、このクリップがどう置かれているか（時計の写像・ブレンド。S15b）</summary>
        TimelineClip,
        /// <summary>焼いて書き出す段（S20）。この段より上を通った姿勢を焼く</summary>
        Output,
        /// <summary>全身 IK（S25）。固定の強さが入っている点へ届くように、元の姿勢を曲げる。リグの定義が全身 IK のときだけ並ぶ</summary>
        FullBodyIk,
    }

    /// <summary>
    /// その段で ✋（つかむ）を選んだとき、何をつかむか
    /// </summary>
    public enum GrabTarget
    {
        /// <summary>つかめない</summary>
        None,
        /// <summary>
        /// 段が持つ値（編集用リグの操作値・Rig のターゲット）をつかむ。
        /// **保存データの段でない段の値は、保存データから配られた値**と決めている（Rig の重みとターゲットは Editing Rig の段のクリップにある）。
        /// そのため間の段を通らずに保存データへ直接書く
        /// </summary>
        Values,
        /// <summary>段が出した姿勢（骨）をつかむ。その段から上の姿勢の段を逆に解いて書く</summary>
        Pose,
    }

    /// <summary>
    /// 段が姿勢のどこで働くか。スタックが段の種類を見ずに、グラフを流す時機と焼き方を決めるための宣言
    /// </summary>
    public enum PosePhase
    {
        /// <summary>姿勢を変えない（時計の写像・書き出すだけの段）</summary>
        None,
        /// <summary>編集用の体と、それを写した表示モデルの姿勢を、その場で作る（Editing Rig・Override・Generic Pose・Humanoid Pose）</summary>
        Body,
        /// <summary>
        /// 表示モデルのグラフの後段で解く（Rig・ゲーム側の段）。評価では値を申告するだけで、実際に通すのは終端（Terminal）。
        /// ゲーム側の段は Evaluate (float) をその場で呼ぶものもあるが、グラフの後段に持つものと区別せずここに入れる（余分に流しても申告どおりに通すだけ）
        /// </summary>
        Graph,
        /// <summary>グラフを 1 回流す終端（Display Pose）。Graph の段が申告した値をここでまとめて通す</summary>
        Terminal,
    }

    /// <summary>
    /// 段の行に出して編集できる値。[LayerParameter] を付けたフィールド 1 つぶん
    /// </summary>
    public sealed class PoseParameter
    {
        public string label;
        public System.Type type;
        public float min;
        public float max;
        public System.Func<object> getValue;
        public System.Action<object> setValue;
        /// <summary>見え方の設定（キーに打たない値）。行から触れる</summary>
        public bool display;

        public bool hasRange
        {
            get { return max > min; }
        }
    }

    /// <summary>
    /// スタックの 1 段の基底。**段の性質（書けるか・何をつかむか・逆に解けるか）と前進の処理を、段ごとのサブクラスが宣言する。**
    /// スタック（PoseStack）はインスタンスを並べて、この宣言だけを見て経路と UI を決める。
    ///
    /// 段が出るかどうかは対象から自動で決まる（PoseStack.Build）ので、ここに手で足す設定は無い。
    /// 利用者が変えるのは、段ごとの 👁（有効・既定 ON）と重みだけ
    /// </summary>
    public abstract class PoseLayer
    {
        // ---- 種類と並び ----

        public abstract LayerKind kind { get; }

        /// <summary>並ぶ位置。小さいほど上。ゲーム側の段は属性の order</summary>
        public abstract int order { get; }

        /// <summary>行に出す名前</summary>
        public abstract string label { get; }

        /// <summary>
        /// 同じ種類の段が複数あるとき（Rig の層・同じ型のコンポーネントが 2 つ）の区別。空なら種類がそのまま id
        /// </summary>
        public virtual string idSuffix
        {
            get { return null; }
        }

        /// <summary>
        /// 保存用の名前。組み込みの段は種類がそのまま id になる
        /// </summary>
        public string id
        {
            get { return MakeId (kind, idSuffix); }
        }

        protected static string MakeId (LayerKind kind, string suffix)
        {
            return string.IsNullOrEmpty (suffix) ? kind.ToString () : kind + ":" + suffix;
        }

        // ---- 性質（サブクラスが宣言する） ----

        /// <summary>
        /// 保存データを持つ段（値を持ち、下の段へ配る）。書き出し先にできるのはこの段だけ
        /// </summary>
        public virtual bool isData
        {
            get { return false; }
        }

        /// <summary>
        /// 姿勢を受け取って姿勢を出す段。姿勢をつかんだときに逆に通るのはこの段だけ。
        /// 保存データを持ちながら姿勢も出す段（Editing Rig）は、両方を宣言する
        /// </summary>
        public virtual bool isPoseStage
        {
            get { return !isData; }
        }

        /// <summary>
        /// 姿勢のどこで働く段か。スタックは段の種類ではなくこれを見て、グラフを流す時機と焼き方を決める
        /// </summary>
        public virtual PosePhase phase
        {
            get { return PosePhase.None; }
        }

        /// <summary>段の名前の行の右に出すボタン（S21。Output の段の ↑ ↓ 削除）</summary>
        public readonly List<LayerClipButton> headButtons = new List<LayerClipButton> ();

        /// <summary>行に 👁・✏・✋ を出すか。値も姿勢も持たない段（書き出すだけの段）は出さない</summary>
        public bool hasBadges
        {
            get { return isData || isPoseStage; }
        }

        /// <summary>いま前進を通せるか（処理があり、要るもの（クリップ・結び付け）が揃っている）。通せない段は素通し（恒等）になる</summary>
        public virtual bool canEvaluate
        {
            get { return false; }
        }

        /// <summary>キーの書き出し先（✏）に選べるか。既定は保存データの段</summary>
        public virtual bool canWrite
        {
            get { return isData; }
        }

        /// <summary>✋ を選んだときにつかむもの</summary>
        public virtual GrabTarget grab
        {
            get { return GrabTarget.None; }
        }

        /// <summary>
        /// つかむ対象（✋）に選べるか。つかむものがあっても、まだ操作が入っていない段は false を返す
        /// </summary>
        public virtual bool canManipulate
        {
            get { return grab != GrabTarget.None; }
        }

        /// <summary>✏ や ✋ に選べないときの理由</summary>
        public virtual string unavailableReason
        {
            get { return null; }
        }

        /// <summary>
        /// 段が持つクリップ（編集するクリップ・焼いたクリップ）。持たない段は null
        /// </summary>
        public LayerClip clip { get; protected set; }

        /// <summary>
        /// 段のクリップを、流れてきた情報に合成する重み（Editing Rig / Generic Pose / Humanoid Pose 共通）。
        /// 1（既定）で上書き、0 で合成しない。段の重み（weight。Rig の層など）とは別で、0 にしても段そのものは素通しにならない
        /// </summary>
        public float clipWeight = 1;

        /// <summary>段のクリップを合成する重み（clipWeight）を持つ段か</summary>
        public bool hasClipWeight
        {
            get { return clip != null && clip.hasWeight; }
        }

        /// <summary>重みを持つ段か</summary>
        public virtual bool hasWeight
        {
            get { return false; }
        }

        /// <summary>この段を逆に通れるか（姿勢から、上の段の値を求められるか）</summary>
        public virtual InverseKind inverse
        {
            get { return InverseKind.Exact; }
        }

        /// <summary>逆方向の種別の理由（想定誤差や、逆を持たない拘束の名前）</summary>
        public virtual string inverseReason
        {
            get { return null; }
        }

        /// <summary>
        /// 表示の骨をつかむ（✋ = Display Pose）ときに、つかんだ骨に効く処理だけを一時的に止めて通れるか（S13b）。
        /// 止めている間の表示はその処理を掛けない姿になるので、経路は「近似」として扱う
        /// </summary>
        public virtual bool bypassOnDisplayGrab
        {
            get { return false; }
        }

        // ---- 表示 ----

        /// <summary>行の下に小さく出す補足。止めた段はその理由を優先する</summary>
        public string note
        {
            get { return failure ?? description; }
        }

        /// <summary>何が起きている段か、なぜ通っていないか</summary>
        protected virtual string description
        {
            get { return null; }
        }

        /// <summary>子の行（Rig の層・拘束）。無ければ空</summary>
        public readonly List<string> details = new List<string> ();

        /// <summary>行に出して編集できる値</summary>
        public readonly List<PoseParameter> parameters = new List<PoseParameter> ();

        // ---- 利用者が変える状態 ----

        /// <summary>この段を処理するか（👁）。既定で有効。落とすと素通し（前進解決をスキップし、入力をそのまま次へ渡す）</summary>
        public bool enabled = true;

        public float weight = 1;

        /// <summary>
        /// 前進の処理が例外を出して止めたときの理由。止めた段は素通しになる
        /// </summary>
        public string failure { get; private set; }

        /// <summary>
        /// 実際に評価に入るか（前進を通せて、👁 が入っていて、止まっていない）
        /// </summary>
        public bool active
        {
            get { return canEvaluate && enabled && failure == null; }
        }

        /// <summary>
        /// 逆に通るとき素通しとして扱えるか（評価に入っていない・重み 0）
        /// </summary>
        public bool passThrough
        {
            get { return !active || (hasWeight && weight <= 0); }
        }

        // ---- 処理 ----

        /// <summary>前進解決。評価に入る（active の）段だけが呼ばれる</summary>
        public virtual void Evaluate (float time)
        {
        }

        /// <summary>
        /// 段の値を当て直さずに前進解決する。値を直に動かした後、その段から解き直すときに使う（動かした値をクリップで上書きしないため）。
        /// 値を持たない段は Evaluate と同じ
        /// </summary>
        /// <summary>
        /// 前の段の値を直に動かした後の解き直し（EvaluateFrom で、開始段より後ろの段）。普段は Evaluate と同じ。
        /// クリップを当てただけのときと、操作の途中とで扱いを変える段が上書きする
        /// </summary>
        public virtual void EvaluateAfterEdit (float time)
        {
            Evaluate (time);
        }

        public virtual void EvaluateKeepingValues (float time)
        {
            Evaluate (time);
        }

        /// <summary>
        /// ゲーム側の段が投げても、後ろの段（IK など）まで巻き添えにしないように止める
        /// </summary>
        public void Fail (string reason)
        {
            failure = Tr ("POSE_LAYER_STOPPED_BY_EXCEPTION", reason);
        }
    }

}
