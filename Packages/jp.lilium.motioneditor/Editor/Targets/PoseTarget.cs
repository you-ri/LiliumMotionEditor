using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// Transform のどの値を動かす・キーにするか
    /// </summary>
    [System.Flags]
    public enum TransformChannels
    {
        None = 0,
        Position = 1,
        Rotation = 2,
        Scale = 4,
        All = Position | Rotation | Scale,
    }

    /// <summary>
    /// 表示の骨をつかんだとき（✋ = Display Pose）の狙い。書いて前進し直した後に、ここへ届いたかで残差を測る
    /// </summary>
    public struct DisplayGoal
    {
        /// <summary>つかんだ表示モデルの骨</summary>
        public Transform bone;
        public Quaternion rotation;
        public Vector3 position;
        public bool hasRotation;
        public bool hasPosition;
        /// <summary>骨の回転に後ろから掛けると +Z が子へ向く回転（狙いの残像を骨の形で描く）</summary>
        public Quaternion shapeAdjust;
        /// <summary>骨の見た目の長さ。0 なら点だけ描く</summary>
        public float length;
    }

    /// <summary>
    /// つかむ対象（骨の FK・手足の IK など）。窓・Spinner・Picker・タイムラインはこれだけを見て、
    /// 中身（どのコントローラーで、クリップに何を書くか）は知らない。
    /// 選択は gameObject で持つ（ハンドルが選ぶのも、リロード後に名前で選び直すのもこれ）
    /// </summary>
    public abstract class PoseTarget : System.IDisposable
    {
        /// <summary>
        /// 選択の単位。ハンドルをつかむとこれが選ばれる
        /// </summary>
        public abstract GameObject gameObject { get; }

        public string name
        {
            get { return gameObject != null ? gameObject.name : ""; }
        }

        /// <summary>
        /// 一覧（Picker）とトラックに出す名前
        /// </summary>
        public virtual string label
        {
            get { return name; }
        }

        /// <summary>
        /// 一覧で並べる位置（この骨の深さに置く）
        /// </summary>
        public abstract Transform anchor { get; }

        /// <summary>
        /// 選んだときにカメラを寄せる先（ワールド）
        /// </summary>
        public virtual Vector3 framePosition
        {
            get { return anchor != null ? anchor.position : Vector3.zero; }
        }

        /// <summary>
        /// 目標のほかに、肘・膝の向き（Up）を持つか（Spinner が切り替えられる）
        /// </summary>
        public virtual bool hasPole
        {
            get { return false; }
        }

        /// <summary>
        /// Spinner で動かせる値
        /// </summary>
        public abstract TransformChannels GetSpinChannels (bool pole);

        /// <summary>
        /// Spinner に出す、いま動かすものの名前
        /// </summary>
        public virtual string GetSpinLabel (bool pole)
        {
            return label;
        }

        /// <summary>
        /// Spinner の移動。delta は動かすものの親から見た位置の変化（メートル）。
        /// コントロールは保存値（正規化した値）を持つので、Transform を外から直接動かさず、対象に任せる
        /// </summary>
        public virtual void SpinMove (Vector3 delta, bool pole)
        {
        }

        /// <summary>
        /// Spinner の回転。動かすもののローカル軸まわり（後ろから掛ける）
        /// </summary>
        public virtual void SpinRotateLocal (Quaternion delta, bool pole)
        {
        }

        /// <summary>
        /// Spinner の回転。ワールドの軸まわり（前から掛ける）
        /// </summary>
        public virtual void SpinRotateWorld (Quaternion delta, bool pole)
        {
        }

        /// <summary>
        /// Spinner のスケール。delta は軸ごとの倍率の変化
        /// </summary>
        public virtual void SpinScale (Vector3 delta, bool pole)
        {
        }

        /// <summary>
        /// 数値欄（Transform のパネル）に出す値。保存している値そのもので、位置はメートル・回転は値の軸での向き。
        /// FK は親から見た基準姿勢からの差、IK の目標はルートから見た手足の先、ヒントは曲がる向き（長さ 1）。
        /// 出せる値が無ければ false
        /// </summary>
        public virtual bool TryGetValues (bool pole, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// 数値欄から値を入れる（channels の分だけ）。動かせない値は無視する。キーは打たない
        /// </summary>
        public virtual void SetValues (bool pole, TransformChannels channels, Vector3 position, Quaternion rotation)
        {
        }

        /// <summary>
        /// channels の分だけ基準姿勢の値へ戻す。キーは打たない
        /// </summary>
        public virtual void ResetValues (bool pole, TransformChannels channels)
        {
        }

        /// <summary>
        /// 左右の相手を探す名前（左右の書き方を入れ替えた名前の対象が相手。入れ替えられなければ体の中心で、相手は自分）。
        /// 反転できない対象は null
        /// </summary>
        public virtual string mirrorKey
        {
            get { return null; }
        }

        /// <summary>
        /// source（左右の相手、または自分）の今の見た目を左右反転して、この対象の値にする。キーは打たない
        /// </summary>
        public virtual bool MirrorFrom (PoseTarget source)
        {
            return false;
        }

        /// <summary>
        /// ハンドルを描いて操作を受ける。動かしたら GUI.changed を立てる
        /// </summary>
        /// <param name="selected">この対象が選ばれているか（選ばれているときだけ出すハンドルがある）</param>
        public abstract void OnHandleGUI (bool selected);

        /// <summary>
        /// 表示モデルの骨（段の並びを最後まで通した姿勢）の上にハンドルを出せるか（✋ = Display Pose）
        /// </summary>
        public virtual bool supportsDisplayHandles
        {
            get { return false; }
        }

        /// <summary>
        /// 表示モデルの骨の上にハンドルを出して操作を受ける（✋ = Display Pose）。動かしたら、その差を編集用の値へ写して
        /// 狙った表示の姿勢を goal に入れ、true を返す
        /// </summary>
        public virtual bool OnDisplayHandleGUI (bool selected, out DisplayGoal goal)
        {
            goal = default;
            return false;
        }

        /// <summary>
        /// 表示の骨をつかむとき、この対象が動かす表示モデルの骨（その骨か親を動かす Rig の拘束を、選んでいる間だけ切る）
        /// </summary>
        public virtual Transform displayGrabBone
        {
            get { return null; }
        }

        /// <summary>
        /// つかんでいるハンドルが替わったとき
        /// </summary>
        /// <param name="focused">選ばれている対象（無ければ null）</param>
        public virtual void OnFocusChange (PoseTarget focused)
        {
        }

        /// <summary>
        /// 今の値を、この対象のぶんだけキーとして打つ（打つ先とフレームは writer が持つ）
        /// </summary>
        public abstract void WriteKeys (CurveWriter writer);

        /// <summary>
        /// Spinner で動かしたぶんだけキーを打つ（ドラッグ中は軽くしたいので、姿勢全体は離したときに WriteKeys で打つ）。
        /// 既定はこの対象のぶん全部
        /// </summary>
        public virtual void WriteSpinKeys (CurveWriter writer, bool pole, TransformChannels channels)
        {
            WriteKeys (writer);
        }

        /// <summary>
        /// この対象がキーを打つカーブのパス（クリップのルートから）。タイムラインのトラックに出すキーを選ぶのに使う
        /// </summary>
        public abstract void CollectKeyedPaths (ICollection<string> result);

        /// <summary>
        /// この対象が受け持つ骨。メッシュをクリックした場所の骨から、選ぶ対象を引くのに使う
        /// </summary>
        public abstract void CollectPickTransforms (List<Transform> result);

        /// <summary>
        /// 基準の姿勢へ戻す（Reset）。キーは打たない
        /// </summary>
        public virtual void ResetPose ()
        {
        }

        /// <summary>
        /// キャラを作り直すときに呼ぶ。一時的に作った物（エディタなど）を捨てる
        /// </summary>
        public virtual void Dispose ()
        {
        }
    }

}
