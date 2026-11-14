using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{
    /// <summary>操作口への要求（MotionEditorCommands）。使わない項目は省いてよい</summary>
    [System.Serializable]
    public sealed class MotionEditorRequest
    {
        public string command;
        public string prefab;
        public string clip;
        public string definition;
        public bool create;
        public List<MotionEditorOverride> overrides = new List<MotionEditorOverride> ();
        public int overrideWriteIndex = -1;
        public int frame;
        /// <summary>writePose でキーを打たずに解いた姿勢だけを返す（S57）</summary>
        public bool dryRun;
        /// <summary>readPose・writePose の応答を、丸めて項目を省いた形（PoseDocument.ToCompactJson）にする（S58）</summary>
        public bool compact;
        /// <summary>writePose の姿勢を、pose の代わりにこのファイル（PoseDocument の JSON）から読む（S58。相対パスはプロジェクトのフォルダから）</summary>
        public string poseFile;
        /// <summary>writeMotion で書くキー（S59）</summary>
        public List<MotionEditorKey> keys = new List<MotionEditorKey> ();
        /// <summary>writeMotion で、書く前に書き先のクリップのキーを全部消す</summary>
        public bool replace;
        /// <summary>writeMotion の後に全体を検査して返す</summary>
        public bool lint = true;
        /// <summary>deleteKeys で消すキーのフレーム（S61）</summary>
        public List<int> frames = new List<int> ();
        /// <summary>deleteKeys で全部のキーを消す</summary>
        public bool all;
        /// <summary>sample のフレームの間隔（省くと 1）</summary>
        public int step = 1;
        /// <summary>sample で返す点の名前（省くと全部）</summary>
        public List<string> pointNames = new List<string> ();
        /// <summary>sample で返す muscle の名前（HumanTrait.MuscleName。省くと返さない）</summary>
        public List<string> muscleNames = new List<string> ();
        public int start;
        public int end = -1;
        public string outputName;
        public PoseDocument pose;
        /// <summary>capture の書き出し先（省くとプロジェクトの Temp/MotionEditorCaptures）</summary>
        public string folder;
        /// <summary>capture の画像の一辺（ピクセル）</summary>
        public int size = 512;
        /// <summary>importOverride の取り込む Humanoid のクリップのパス</summary>
        public string source;
        /// <summary>importGeneric で、source の中のクリップの名前（FBX の中のクリップを選ぶとき）</summary>
        public string sourceName;
        /// <summary>importOverride の書き先の Override のクリップのパス（create なら作る）</summary>
        public string overrideClip;
        /// <summary>importOverride の重ね方（Additive / Override）</summary>
        public string mode;
        /// <summary>importOverride で足を床に付けるか</summary>
        public bool lockFeet;
        /// <summary>importOverride（.bvh）で、最初のフレームの体の向きと地面の上の場所を元に合わせる（S74）</summary>
        public bool alignRoot = true;
        /// <summary>importOverride（.bvh）で指も取り込む（既定は取り込まず、打った手の形を残す。.anim は今までどおり指も取り込む）</summary>
        public bool fingers;
        /// <summary>importOverride で、元のクリップのキーのフレームでは元のキーの姿勢に戻し、前後 keepKeysBlend 秒でなめらかにつなぐ（S75）</summary>
        public bool keepKeys;
        public float keepKeysBlend = 0.25f;
        /// <summary>writeHand のプリセット（PoseBank の姿勢の名前かパス。Hand_Fist など）</summary>
        public string preset;
        /// <summary>writeHand で当てる手（Left / Right / Both。省くと Both）</summary>
        public string hand;
        /// <summary>writeHand で個別に当てる指の muscle（プリセットの後に当てる）</summary>
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
        /// <summary>exportKeys で写す外部の骨格（ExternalSkeleton のアセットのパス。S73）</summary>
        public string skeleton;
        /// <summary>exportKeys で書く BVH のパス（相対パスはプロジェクトのフォルダから）</summary>
        public string path;
    }

    /// <summary>importGeneric の応答（S93）</summary>
    [System.Serializable]
    public sealed class MotionEditorGenericImport
    {
        public int frameCount;
        public int curveCount;
        /// <summary>取り込んだ値で解き直したときの骨の位置のずれの最大（m）</summary>
        public float maxError;
        /// <summary>取り込まなかったカーブなどの注意</summary>
        public List<string> notes = new List<string> ();
    }

    /// <summary>exportKeys の応答（S73）</summary>
    [System.Serializable]
    public sealed class MotionEditorExport
    {
        /// <summary>書いた BVH のパス</summary>
        public string path;
        public float frameRate;
        public int lastFrame;
        /// <summary>書いたフレーム（BVH の 1 フレームがこの 1 つ）</summary>
        public List<int> frames = new List<int> ();
        public int jointCount;
    }

    /// <summary>muscle の名前と値（writeHand・readHand）</summary>
    [System.Serializable]
    public sealed class MotionEditorMuscle
    {
        /// <summary>"Left Index 1 Stretched" / "LeftHand.Index.1 Stretched"、writeHand では左右を省いた "Index 1 Stretched" も</summary>
        public string name;
        public float value;
    }

    /// <summary>手の形（readHand・writeHand の応答）</summary>
    [System.Serializable]
    public sealed class MotionEditorHand
    {
        public int frame;
        /// <summary>両手の指の muscle の値（HumanTrait.MuscleName の名前）</summary>
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
        /// <summary>このキャラで使えるプリセットの名前（writeHand の preset）</summary>
        public List<string> presets = new List<string> ();
    }

    [System.Serializable]
    public sealed class MotionEditorOverride
    {
        public string clip;
        /// <summary>Additive / Override</summary>
        public string mode;
        /// <summary>効く範囲（ChannelMask のアセットのパス。S17）。空なら全部に効く</summary>
        public string mask;
    }

    /// <summary>writeMotion・readMotion のキー 1 つ（S59）。体の点と手の形のどちらか、または両方</summary>
    [System.Serializable]
    public sealed class MotionEditorKey
    {
        public int frame;
        /// <summary>全身 IK の点（PoseDocument の points と同じ）</summary>
        public List<PosePoint> points = new List<PosePoint> ();
        /// <summary>接地をまとめて書く（PoseDocument の contacts と同じ。S60）</summary>
        public List<PoseContact> contacts = new List<PoseContact> ();
        /// <summary>持ち物（PoseDocument の objects と同じ。S91）</summary>
        public List<PoseObject> objects = new List<PoseObject> ();
        /// <summary>手の形（writeHand と同じ指定。並べた順に当て、後のものが上書きする）</summary>
        public List<MotionEditorHandShape> hands = new List<MotionEditorHandShape> ();
        /// <summary>
        /// このキーから次のキーまでの補間（S80。curve / linear / constant、S82 の external（外部の動き。結果のファイルがあるときだけ））。
        /// 省くと変えない。readMotion では mixed もある（最後のキーには無い）
        /// </summary>
        public string interpolation;
        /// <summary>readMotion: 外部の動きの区間で、キーの値が変わる前に作った動き（S82。作り直すと合う）</summary>
        public bool stale;
    }

    /// <summary>手の形の指定（writeMotion。writeHand の preset・hand・muscles と同じ）</summary>
    [System.Serializable]
    public sealed class MotionEditorHandShape
    {
        public string preset;
        /// <summary>Left / Right / Both（省くと Both）</summary>
        public string hand;
        public List<MotionEditorMuscle> muscles = new List<MotionEditorMuscle> ();
    }

    /// <summary>要求を並べたもの（-executeMethod で渡すファイル）</summary>
    [System.Serializable]
    public sealed class MotionEditorBatch
    {
        public List<MotionEditorRequest> requests = new List<MotionEditorRequest> ();
        /// <summary>最後に閉じない（S59。Unity CLI から並べて送り、続けて読み書きするとき）。-executeMethod の口では使わず、必ず閉じる</summary>
        public bool keepOpen;
    }

    /// <summary>開いているキャラとクリップの説明（describe・open の応答）</summary>
    [System.Serializable]
    public sealed class MotionEditorDescription
    {
        public string character;
        public string clip;
        /// <summary>キーを打つクリップ（書き込み先が Override ならそのクリップ）</summary>
        public string targetClip;
        public float frameRate;
        public int lastFrame;
        public List<int> keyFrames = new List<int> ();
        public bool cycle;
        /// <summary>このフレームより後ろには打てない（サイクルキー。-1 は限りなし）</summary>
        public int writableUntil;
        /// <summary>全身 IK のキャラか（姿勢の読み書きは全身 IK のキャラだけ）</summary>
        public bool fullBody;
        public float humanScale;
        public int overrideWriteIndex;
        /// <summary>今キーを打てない理由（打てるなら空）</summary>
        public string writeProblem;
        public List<MotionEditorPointInfo> points = new List<MotionEditorPointInfo> ();
        /// <summary>持ち物（S91。無ければ空）</summary>
        public List<MotionEditorObjectInfo> objects = new List<MotionEditorObjectInfo> ();
    }

    /// <summary>持ち物 1 つの説明（S91）</summary>
    [System.Serializable]
    public sealed class MotionEditorObjectInfo
    {
        public string name;
        /// <summary>物の階層の親の骨（HumanBodyBones の名前。空は Animator の GameObject）</summary>
        public string parent;
        /// <summary>キーの値がどの骨から見た値か（Root / Hips / Chest）。その骨が動くと物も付いて動く</summary>
        public string frame;
        /// <summary>キーの無いときの T ポーズでの場所（体の向き基準・メートル）</summary>
        public Vector3 rest;
        /// <summary>キーの無いときの向き（体の向き基準のオイラー角。度）</summary>
        public Vector3 restRotation;
        /// <summary>付ける先（リグ定義の一覧。S104。姿勢の objects の attach・snap・grip に名前を書く）</summary>
        public List<MotionEditorAttachInfo> attachments;
    }

    /// <summary>物を付ける先 1 つの説明（S104）</summary>
    [System.Serializable]
    public sealed class MotionEditorAttachInfo
    {
        public string name;
        /// <summary>付ける骨（HumanBodyBones の名前か、追加コントロールの名前）</summary>
        public string bone;
        /// <summary>Twist（柄の軸まわりに回る）/ Fixed（回らない）</summary>
        public string joint;
        /// <summary>物の設定に決まった位置があるか（snap で寄せられる）</summary>
        public bool hasSettings;
        /// <summary>キーの無いクリップで付けているか</summary>
        public bool attachedAtRest;
        /// <summary>決まった位置の、付ける先の枠の場所（物のローカル・メートル。手なら手首が来る場所）</summary>
        public Vector3 position;
    }

    [System.Serializable]
    public sealed class MotionEditorPointInfo
    {
        public string name;
        public bool hasRotation;
        /// <summary>点が乗る骨（HumanBodyBones の名前）</summary>
        public string bone;
        /// <summary>基準姿勢（T ポーズ）での場所（体の向き基準・メートル。キーが入った後でも基準姿勢のまま）</summary>
        public Vector3 rest;
        /// <summary>点をつなぐ線の相手の点（定義の link。空なら骨の階層の 1 つ上の関節の点。無ければ空）</summary>
        public string link;
        /// <summary>基準姿勢での link の点からの長さ（メートル。上腕・前腕などの骨の長さ。link が無ければ 0）</summary>
        public float length;
        /// <summary>接地の点か（定義の contact。床と足の滑りを見る点）</summary>
        public bool contact;
        /// <summary>基準の場所を地面に落とす点か（定義の ground。足裏の点）</summary>
        public bool ground;
        /// <summary>動かせない点か（定義の passive。S89）。姿勢の JSON には書けない</summary>
        public bool passive;
    }
}
