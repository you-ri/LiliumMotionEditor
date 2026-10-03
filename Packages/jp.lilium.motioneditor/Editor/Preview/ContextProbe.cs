using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// プレビューに置いた文脈（技の演出など。Timeline で一緒に動く物）の 1 つ。
    /// 中身の読み方は Timeline のアセンブリ側（ContextProbe）が入れる
    /// </summary>
    public sealed class ContextInfo
    {
        /// <summary>プレビューシーンに置いた複製</summary>
        public GameObject instance;
        public string label;
        /// <summary>演出の長さ（秒）。0 なら時計を持たない</summary>
        public double duration;
        /// <summary>演出のフレームの細かさ（Timeline の編集フレームレート）</summary>
        public double frameRate = 60;
        /// <summary>
        /// 開いているシーンの演出を読んだだけのもの（S15d）。複製ではないので、手回しにしない・バインドを変えない・評価しない
        /// （時刻を動かすのは Timeline 窓）
        /// </summary>
        public bool readOnly;
        /// <summary>中身の要約（トラック・バインド・クリップの置き方）。読み直したときに変わったかを見る</summary>
        public string signature;
        /// <summary>一覧の下に出す注意（バインドを外したトラックなど）</summary>
        public readonly List<string> notes = new List<string> ();
        /// <summary>中身のトラック（拡張点が触った後の状態）</summary>
        public readonly List<ContextTrack> tracks = new List<ContextTrack> ();

        /// <summary>演出のカメラ割り（ショット。S15c）。開始順</summary>
        public readonly List<ContextShot> shots = new List<ContextShot> ();

        /// <summary>
        /// その時刻の演出のカメラ（ショットのブレンド込み）。映すショットが無い・読めないショットなら false（name は元のショット名）
        /// </summary>
        public bool TryGetShot (double time, out Pose pose, out float fieldOfView, out string name)
        {
            pose = Pose.identity;
            fieldOfView = 60;
            name = null;
            bool has = false;
            // 後から始まるショットが、自分の入りのブレンドで前のショットに重なる（Cinemachine のトラックの混ぜ方）
            foreach (ContextShot shot in shots) {
                if (!shot.Contains (time)) continue;
                name = shot.name;
                Pose shotPose;
                if (!shot.TryGetPose (out shotPose)) {
                    // 読めないショットが上に来たら、下のショットは見えていない
                    has = false;
                    continue;
                }
                if (!has) {
                    pose = shotPose;
                    fieldOfView = shot.fieldOfView;
                    has = true;
                    continue;
                }
                float weight = shot.WeightAt (time);
                pose = new Pose (Vector3.Lerp (pose.position, shotPose.position, weight), Quaternion.Slerp (pose.rotation, shotPose.rotation, weight));
                fieldOfView = Mathf.Lerp (fieldOfView, shot.fieldOfView, weight);
            }
            return has;
        }

        /// <summary>アニメーションのトラックに置かれたクリップすべて（トラック順・トラックの中は並び順）</summary>
        public IEnumerable<ContextClip> animationClips
        {
            get {
                foreach (ContextTrack track in tracks) {
                    foreach (ContextClip clip in track.clips) yield return clip;
                }
            }
        }
    }

    /// <summary>
    /// 文脈のトラック 1 本。Timeline の型を出さずに拡張点へ渡す
    /// </summary>
    public sealed class ContextTrack
    {
        public string name;
        /// <summary>トラックの型名（例: AnimationTrack・ControlTrack）</summary>
        public string typeName;
        /// <summary>今のバインド先（prefab が持っていたもの）。変えたら rebind を立てる</summary>
        public Object binding;
        /// <summary>binding を入れ直すか</summary>
        public bool rebind;
        /// <summary>一覧に出す注意</summary>
        public string note;
        /// <summary>このトラックのクリップとしては編集しない理由（開いているシーンでキャラにバインドされている など。S15d）。編集できるなら null</summary>
        public string editBlock;
        /// <summary>アニメーションのトラックなら、並んでいるクリップ（S15b）。それ以外は空</summary>
        public readonly List<ContextClip> clips = new List<ContextClip> ();

        public void Bind (Object target)
        {
            binding = target;
            rebind = true;
        }
    }

    /// <summary>
    /// 演出のカメラ割りの 1 ショット（S15c）。カメラの部品はプレビューに置かない（Cinemachine はシーンをまたいで登録されるので、
    /// 開いているシーンのカメラを動かしてしまう）。prefab から読んだ置き場所・注視先・画角だけを持ち、姿は複製の Transform から取る
    /// </summary>
    public sealed class ContextShot
    {
        public string name;
        public double start;
        public double end;
        /// <summary>入りのブレンドの重み（0〜1）。無ければ 1</summary>
        public System.Func<double, float> weightAt;
        /// <summary>カメラの置き場所（複製の中の Transform）。prefab の外のカメラを指していて読めなければ null</summary>
        public Transform camera;
        /// <summary>注視先（複製の中の Transform）。無ければカメラの向きのまま</summary>
        public Transform lookAt;
        public float fieldOfView = 40;

        public bool Contains (double time)
        {
            return time >= start - 1e-6 && time <= end + 1e-6;
        }

        public float WeightAt (double time)
        {
            return weightAt != null ? Mathf.Clamp01 (weightAt (time)) : 1;
        }

        /// <summary>今のカメラの位置と向き（注視先があればそちらへ向ける。Composer の既定の、画面の真ん中に置く見え方）</summary>
        public bool TryGetPose (out Pose pose)
        {
            pose = Pose.identity;
            if (camera == null) return false;
            Quaternion rotation = camera.rotation;
            if (lookAt != null) {
                Vector3 direction = lookAt.position - camera.position;
                if (direction.sqrMagnitude > 1e-8f) rotation = Quaternion.LookRotation (direction, Vector3.up);
            }
            pose = new Pose (camera.position, rotation);
            return true;
        }
    }

    /// <summary>
    /// 外の時計（Unity の Timeline 窓）の今の様子（S15c）
    /// </summary>
    public struct ExternalClock
    {
        /// <summary>同じ演出を開いていて時刻を読めた</summary>
        public bool available;
        /// <summary>演出の時刻（秒）</summary>
        public double time;
        /// <summary>読めない理由・注意（自キャラのトラックがバインドされている など）</summary>
        public string note;
        /// <summary>note が警告か</summary>
        public bool warning;
    }

    /// <summary>
    /// 文脈のアニメーションのトラックに置かれたクリップ 1 つ（S15b）。Timeline の型を出さずに、
    /// 「演出の時刻 ↔ クリップの時刻」の写像と、ブレンド・オフセットなど段の判定に要るものだけを持つ
    /// </summary>
    public sealed class ContextClip
    {
        public ContextTrack track;
        /// <summary>トラックの中の何番目か（選択の保存に使う）</summary>
        public int index;
        public AnimationClip clip;
        /// <summary>演出の中で始まる時刻（秒）</summary>
        public double start;
        public double duration;
        /// <summary>クリップのどこから再生するか（秒）</summary>
        public double clipIn;
        /// <summary>再生の速さ（1 で等速）</summary>
        public double timeScale = 1;
        /// <summary>始まり・終わりで前後のクリップと混ざる長さ（秒。無ければ 0）</summary>
        public double blendIn;
        public double blendOut;
        /// <summary>その演出の時刻での、このクリップの重み（0〜1）。無ければ区間の中を 1 とみなす</summary>
        public System.Func<double, float> weightAt;
        public bool muted;
        /// <summary>上書きのトラック（親がアニメーションのトラック）にある</summary>
        public bool overrideTrack;
        public bool hasAvatarMask;
        /// <summary>位置・回転のオフセットの説明（無ければ null）</summary>
        public string offsets;

        public double end
        {
            get { return start + duration; }
        }

        /// <summary>選択の保存に使う名前（トラック名＋何番目か）</summary>
        public string key
        {
            get { return (track != null ? track.name : "") + "#" + index; }
        }

        public string label
        {
            get { return (track != null ? track.name + " / " : "") + (clip != null ? clip.name : "(空)"); }
        }

        /// <summary>演出の時刻 → クリップの時刻（秒）。区間の外も同じ式で延ばす</summary>
        public double LocalFromMaster (double master)
        {
            return clipIn + (master - start) * timeScale;
        }

        public double MasterFromLocal (double local)
        {
            return start + (local - clipIn) / (timeScale != 0 ? timeScale : 1);
        }

        public bool Contains (double master)
        {
            return master >= start - 1e-6 && master <= end + 1e-6;
        }

        /// <summary>その時刻の重み。区間の外は 0</summary>
        public float WeightAt (double master)
        {
            if (!Contains (master)) return 0;
            return weightAt != null ? Mathf.Clamp01 (weightAt (master)) : 1;
        }
    }

    /// <summary>
    /// 文脈のバインドの決め方を外から足す拡張点。トラック名やゲーム固有の決まりはパッケージに持たないので、
    /// 「相手役に誰を入れるか」などはゲーム側がここで決める（Animation Rigging の RigProbe と同じ構図）
    /// </summary>
    public interface IContextHook
    {
        /// <summary>
        /// 時計を回す前に呼ばれる。トラックの binding を書き換えて Bind を呼ぶと、その先へ入れ直す
        /// </summary>
        void Prepare (ContextInfo context, IList<ContextTrack> tracks);
    }

    public static class ContextHooks
    {
        static readonly List<IContextHook> hooks_ = new List<IContextHook> ();

        /// <summary>
        /// 受け口を足す（[InitializeOnLoad] の静的コンストラクタなどから呼ぶ）。同じものは 1 回だけ
        /// </summary>
        public static void Register (IContextHook hook)
        {
            if (hook != null && !hooks_.Contains (hook)) hooks_.Add (hook);
        }

        public static void Unregister (IContextHook hook)
        {
            hooks_.Remove (hook);
        }

        public static IReadOnlyList<IContextHook> hooks
        {
            get { return hooks_; }
        }

        internal static void Prepare (ContextInfo context)
        {
            foreach (IContextHook hook in hooks_) {
                try {
                    hook.Prepare (context, context.tracks);
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
        }
    }

    /// <summary>
    /// 文脈の時計（Timeline）。Timeline パッケージが入っているときだけ中身が入る（LiliumMotionEditor.Timeline.Editor）。
    /// 入っていなければ文脈は置けるが動かない（見た目だけ）
    /// </summary>
    public static class ContextProbe
    {
        /// <summary>置いた複製を手回しの時計にして、中身（トラックと今のバインド）を読む</summary>
        public static System.Func<GameObject, ContextInfo> prepare;
        /// <summary>拡張点が触った後のバインドを入れて、グラフを作り直す</summary>
        public static System.Action<ContextInfo> apply;
        /// <summary>その時刻（秒）の姿にする</summary>
        public static System.Action<GameObject, double> evaluate;
        /// <summary>演出のカメラ割りを元の prefab から読んで、複製の Transform に結び付ける（S15c）</summary>
        public static System.Action<ContextInfo, GameObject> readShots;
        /// <summary>Unity の Timeline 窓が同じ演出を開いていれば、その時刻を読む（S15c）。2 つ目の引数は自キャラのトラック名（無ければ null）</summary>
        public static System.Func<ContextInfo, string, ExternalClock> readExternalClock;
        /// <summary>Unity の Timeline 窓が開いている、開いているシーンの演出を読むだけで取る（S15d）。開いていなければ null</summary>
        public static System.Func<ContextInfo> readInspectedScene;

        public static bool available
        {
            get { return prepare != null; }
        }

        public static ContextInfo Prepare (GameObject instance)
        {
            return Prepare (instance, null);
        }

        /// <param name="source">複製の元の prefab（カメラ割りを読む。カメラの部品は複製から外しているため）</param>
        public static ContextInfo Prepare (GameObject instance, GameObject source)
        {
            if (instance == null) return null;
            ContextInfo info = prepare != null ? prepare (instance) : null;
            if (info == null) {
                info = new ContextInfo { instance = instance, label = instance.name };
                if (!available) info.notes.Add ("Timeline のパッケージが入っていないので、時計は回せない（見た目だけ置いている）");
            }
            if (source != null && readShots != null) {
                try {
                    readShots (info, source);
                } catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
            // バインドの決め方を外から足せるのは、グラフを作り直す前のここだけ
            ContextHooks.Prepare (info);
            if (apply != null) apply (info);
            return info;
        }

        public static void Evaluate (GameObject instance, double time)
        {
            if (instance == null || evaluate == null) return;
            evaluate (instance, time);
        }

        /// <summary>
        /// Timeline 窓が開いている、開いているシーンの演出（読むだけ。S15d）。開いていない・prefab の編集画面の演出なら null
        /// </summary>
        public static ContextInfo ReadInspectedScene ()
        {
            if (readInspectedScene == null) return null;
            try {
                return readInspectedScene ();
            } catch (System.Exception exception) {
                Debug.LogException (exception);
                return null;
            }
        }

        public static ExternalClock ReadExternalClock (ContextInfo info, string selfTrack)
        {
            if (info == null || readExternalClock == null) return new ExternalClock { note = "Timeline のパッケージが入っていない" };
            return readExternalClock (info, selfTrack);
        }
    }

}
