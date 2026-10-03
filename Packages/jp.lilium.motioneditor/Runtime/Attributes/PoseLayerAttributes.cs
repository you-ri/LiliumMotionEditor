using System;

namespace Lilium
{

    /// <summary>
    /// 逆方向（下の段で動かしたものを上の段へ書き戻す）に通れるか
    /// </summary>
    public enum InverseKind
    {
        /// <summary>そのまま戻せる</summary>
        Exact,
        /// <summary>戻せるが残差が出る</summary>
        Approximate,
        /// <summary>戻せない。この段をまたぐ組み合わせは選べない</summary>
        None,
    }

    /// <summary>
    /// 自作のアニメーション処理を、モーションエディタの段（レイヤー）として出す。
    /// **キャラに付いている Component だけが段になる**（型があるだけでは出ない）。
    ///
    /// 前進解決は <see cref="IPoseLayerEvaluate"/> を実装するか、public で戻り値の無い Evaluate (float time) を持たせる。
    /// 逆方向は <see cref="LayerInverseAttribute"/> で宣言する。宣言が無ければ「戻せない」扱いになり、その段をまたぐ編集は選べなくなる。
    ///
    /// PlayableBehaviour を段にする（グラフへ挿す）のは未対応。グラフを組む Component 側にこの属性を付けること
    /// </summary>
    [AttributeUsage (AttributeTargets.Class, Inherited = true)]
    public sealed class PoseLayerAttribute : Attribute
    {
        public PoseLayerAttribute (string label)
        {
            this.label = label;
        }

        /// <summary>段の名前</summary>
        public string label { get; private set; }

        /// <summary>
        /// 並ぶ位置。小さいほど上（先に解決する）。同じ値なら組み込みの段が先。
        ///
        /// 組み込みは 編集用リグ 0 / Override 10〜 / Generic Pose 80 / Humanoid Pose 90 / Timeline Clip 100 / Rigging 1000〜 / 表示の骨 最大値。
        /// **90 から下は実行時の PlayableGraph の sorting と同じ体系**にしてあるので、
        /// 実行時にグラフへ挿している処理なら、その sorting をそのまま入れればスタックの並びが実行時と一致する
        /// （例: クリップ 90 → Rig 1000 → 後段の処理 1110）。89 以下は編集専用の段が入る場所。
        /// 90 より上に置くと、取込（Generic Pose・Humanoid Pose の段まで通した姿勢を読む）にもこの段が入る
        /// </summary>
        public int order { get; set; }

        /// <summary>行の下に小さく出す補足</summary>
        public string note { get; set; }
    }

    /// <summary>
    /// 段の行に出すフィールド。
    ///
    /// いまは**表示だけ**（キーを打つ受け口がまだ無いため）。編集できるようになるのは、
    /// 任意プロパティのカーブ（仕様書 S6）が入ってから
    /// </summary>
    [AttributeUsage (AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class LayerParameterAttribute : Attribute
    {
        public LayerParameterAttribute (string label = null)
        {
            this.label = label;
        }

        /// <summary>表示名。省略するとフィールド名</summary>
        public string label { get; private set; }

        /// <summary>範囲付きで出す（min と max が同じなら範囲なし）</summary>
        public float min { get; set; }
        public float max { get; set; }

        /// <summary>
        /// 見え方の設定（キーに打たない値）。true なら行から触れる。
        /// 例: 左右反転のように「今どう見せるか」を決める値
        /// </summary>
        public bool display { get; set; }
    }

    /// <summary>
    /// この段を逆に通れるかの宣言。付けなければ「戻せない」
    /// </summary>
    [AttributeUsage (AttributeTargets.Class, Inherited = true)]
    public sealed class LayerInverseAttribute : Attribute
    {
        public LayerInverseAttribute (InverseKind kind)
        {
            this.kind = kind;
        }

        public InverseKind kind { get; private set; }

        /// <summary>ずれる・戻せない理由（画面に出る）</summary>
        public string reason { get; set; }
    }

    /// <summary>
    /// 段の前進解決。実装しない場合は、public で戻り値の無い Evaluate (float time) があればそれを呼ぶ
    /// </summary>
    public interface IPoseLayerEvaluate
    {
        /// <summary>
        /// その時刻の姿勢を作る。上の段の結果が入った状態で呼ばれる
        /// </summary>
        void Evaluate (float time);
    }

}
