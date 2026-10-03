using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 裏（プレビューの世界）で作った姿勢を、開いているシーンのキャラの骨へ写す（S15d）。
    ///
    /// 写した値は AnimationMode（Animation 窓や Timeline のプレビューが使う仕組み）に登録するので、止めると元に戻り、
    /// シーンは変更扱いにならない（保存されるのはクリップだけ）。AnimationMode は同時に 1 つの持ち主しか持てない（2026-09-19 に確認:
    /// Timeline のプレビュー中に自分のドライバで始めても失敗する）ので、
    /// - 誰かが持っていれば（Timeline のプレビュー中）その中に骨を登録して相乗りする。Timeline がプレビューを切ると一緒に戻る。
    /// - 誰も持っていなければ自分のドライバで始める。
    /// - 持ち主が立て直すと登録が外れる（Timeline は再評価のときに立て直す）。毎回登録が生きているかを見て、外れていたら登録し直す。
    ///
    /// 写すのは表示モデルの Animator より下の骨のローカルの位置・回転・大きさ。Animator の GameObject とそれより上（ルート・体）は写さない
    /// </summary>
    public sealed class SceneMirror
    {
        const string kDriverName = "SceneMirror";

        /// <summary>
        /// 取り残されたドライバを止めて捨てる。ドライバ（Unity の物）はスクリプトの再読み込みを越えて残るが、それを持っていた
        /// この C# の物は作り直されるので、止める人がいなくなる。残すと AnimationMode が掛かったままになり、Timeline がプレビューを始められない
        /// （同時に 1 つしか持てない）。エディタの起動・再読み込みの後と、自分のドライバを始める前に呼ぶ
        /// </summary>
        [InitializeOnLoadMethod]
        public static void StopOrphanDrivers ()
        {
            StopOrphanDrivers (null);
        }

        static void StopOrphanDrivers (AnimationModeDriver keep)
        {
            foreach (AnimationModeDriver driver in Resources.FindObjectsOfTypeAll<AnimationModeDriver> ().Where (d => d != null && d.name == kDriverName && d != keep).ToList ()) {
                if (AnimationMode.InAnimationMode (driver)) AnimationMode.StopAnimationMode (driver);
                Object.DestroyImmediate (driver);
            }
        }
        static readonly string[] kProperties = {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w",
            "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z",
        };

        struct Pair
        {
            public Transform source;
            public Transform target;
            public string path;
        }

        readonly List<Pair> pairs_ = new List<Pair> ();
        Animator target_;
        AnimationModeDriver driver_;

        /// <summary>写している先（シーンのキャラの Animator）。無ければ null</summary>
        public Animator target
        {
            get { return target_; }
        }

        /// <summary>写せなかった骨の数（シーンのキャラに同じパスの骨が無い）</summary>
        public int missing { get; private set; }

        /// <summary>
        /// 写す元（プレビューの世界の表示モデル）と先（シーンのキャラ）を結ぶ。骨の対応は Animator からのパス
        /// </summary>
        public void Bind (Animator source, Animator target)
        {
            Release ();
            pairs_.Clear ();
            missing = 0;
            target_ = target;
            if (source == null || target == null) return;
            foreach (Transform bone in source.GetComponentsInChildren<Transform> (true)) {
                if (bone == source.transform) continue;
                string path = AnimationUtility.CalculateTransformPath (bone, source.transform);
                Transform found = target.transform.Find (path);
                if (found == null) {
                    missing++;
                    continue;
                }
                pairs_.Add (new Pair { source = bone, target = found, path = path });
            }
        }

        /// <summary>
        /// 今の姿勢を写す。AnimationMode に入っていなければ始め、登録が外れていれば登録し直す
        /// </summary>
        public void Apply ()
        {
            if (target_ == null || pairs_.Count == 0) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnsureRegistered ();
            foreach (Pair pair in pairs_) {
                if (pair.source == null || pair.target == null) continue;
                pair.target.localPosition = pair.source.localPosition;
                pair.target.localRotation = pair.source.localRotation;
                pair.target.localScale = pair.source.localScale;
            }
        }

        /// <summary>
        /// 登録が生きているか（持ち主が立て直すと外れる）。外れていれば、次の Apply で登録し直す
        /// </summary>
        public bool registered
        {
            get {
                if (target_ == null || pairs_.Count == 0 || !AnimationMode.InAnimationMode ()) return false;
                Pair first = pairs_[0];
                return first.target != null && AnimationMode.IsPropertyAnimated (first.target, "m_LocalRotation.x");
            }
        }

        /// <summary>自分のドライバで AnimationMode を持っているか（相乗りでなく）</summary>
        public bool ownsMode
        {
            get { return driver_ != null && AnimationMode.InAnimationMode (driver_); }
        }

        void EnsureRegistered ()
        {
            if (registered) return;
            if (!AnimationMode.InAnimationMode ()) {
                StopOrphanDrivers (driver_);
                if (driver_ == null) {
                    driver_ = ScriptableObject.CreateInstance<AnimationModeDriver> ();
                    driver_.name = kDriverName;
                    driver_.hideFlags = HideFlags.HideAndDontSave;
                }
                AnimationMode.StartAnimationMode (driver_);
            }
            GameObject root = target_.gameObject;
            foreach (Pair pair in pairs_) {
                foreach (string property in kProperties) {
                    AnimationMode.AddEditorCurveBinding (root, EditorCurveBinding.FloatCurve (pair.path, typeof (Transform), property));
                }
            }
        }

        /// <summary>
        /// 写すのをやめる。自分のドライバで持っていれば止めて元に戻す。相乗りしているときは、持ち主（Timeline）がプレビューを切ったときに戻る
        /// </summary>
        public void Release ()
        {
            if (driver_ != null) {
                if (AnimationMode.InAnimationMode (driver_)) AnimationMode.StopAnimationMode (driver_);
                Object.DestroyImmediate (driver_);
                driver_ = null;
            }
        }

        /// <summary>結びを解いて、写すのをやめる</summary>
        public void Unbind ()
        {
            Release ();
            pairs_.Clear ();
            target_ = null;
            missing = 0;
        }
    }

}
