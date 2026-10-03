using System.Linq;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 開いているシーンに置いたキャラを、その場で編集する（S15d）。
    /// 姿勢はいつもどおりプレビューの世界（裏）で作り、シーンのキャラの骨へ写す（SceneMirror）。写した値は編集をやめると元に戻り、
    /// シーンや prefab は書き換えない（保存されるのはクリップだけ）。裏の複製はシーンのキャラから作り、同じ場所に置くので、骨のハンドルは本物の骨の上に出る
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>編集している、開いているシーンのキャラ（prefab のインスタンスのルート）。無ければ prefab を編集している</summary>
        [SerializeField] GameObject sceneTarget_;
        /// <summary>
        /// 編集しているキャラの、シーンを開き直しても引ける名札（GlobalObjectId）と名前。シーンを閉じる・開き直す（テストの実行も読み直す）と
        /// 参照は切れるので、見失ったら写すのをやめ、同じシーンが開かれたらこの名札で引き直して続ける
        /// </summary>
        [SerializeField] string sceneTargetId_;
        [SerializeField] string sceneTargetName_;
        bool sceneTargetLost_;
        double nextSceneTargetLookup_;

        readonly SceneMirror sceneMirror_ = new SceneMirror ();
        /// <summary>Timeline 窓が開いている、開いているシーンの演出（読むだけ）。開いていなければ null</summary>
        ContextInfo sceneContext_;

        public GameObject sceneTarget
        {
            get { return sceneTarget_; }
        }

        /// <summary>シーンのキャラへ写している様子（写せなかった骨の数など）。編集していなければ null</summary>
        public string sceneTargetStatus
        {
            get {
                if (sceneTarget_ == null) {
                    return sceneTargetLost_ ? sceneTargetName_ + " が見つからない（そのシーンを開くと続きから編集する）" : null;
                }
                if (sceneMirror_.target == null) return sceneTarget_.name + ": Humanoid の Animator が無いので写せない";
                string text = sceneTarget_.name + " を編集中" + (sceneMirror_.ownsMode ? "" : "（Timeline のプレビューに相乗り）");
                if (sceneMirror_.missing > 0) text += "。対応する骨が無いものが " + sceneMirror_.missing + " 本";
                return text;
            }
        }

        /// <summary>
        /// 開いているシーンのキャラを編集対象にする。子を選んでいても、prefab のインスタンスのルートを探す
        /// </summary>
        public bool SetSceneTarget (GameObject go)
        {
            GameObject root = FindSceneTargetRoot (go);
            if (root == null || root == sceneTarget_) return root != null;
            sceneMirror_.Unbind ();
            sceneTarget_ = root;
            sceneTargetId_ = GlobalObjectId.GetGlobalObjectIdSlow (root).ToString ();
            sceneTargetName_ = root.name;
            sceneTargetLost_ = false;
            sceneContext_ = ContextProbe.ReadInspectedScene ();
            lastFollowedTime_ = double.NaN;
            // AnimBank の置き場所・焼いた版の対応などは元の prefab から引く
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource (root);
            if (source != null) prefab_ = source;
            RebuildStage ();
            return true;
        }

        /// <summary>シーンのキャラの編集をやめる（写した姿勢は元に戻る）</summary>
        public void ClearSceneTarget ()
        {
            if (sceneTarget_ == null && string.IsNullOrEmpty (sceneTargetId_)) return;
            sceneMirror_.Unbind ();
            sceneTarget_ = null;
            sceneTargetId_ = null;
            sceneTargetName_ = null;
            sceneTargetLost_ = false;
            sceneContext_ = null;
            lastFollowedTime_ = double.NaN;
            RebuildStage ();
        }

        /// <summary>
        /// 開いているシーンの物から、編集するキャラのルートを探す（prefab のインスタンスならその一番外側、そうでなければ Humanoid の Animator を持つ物）
        /// </summary>
        internal static GameObject FindSceneTargetRoot (GameObject go)
        {
            if (go == null || EditorUtility.IsPersistent (go) || !go.scene.IsValid ()) return null;
            GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot (go);
            if (root == null) {
                Animator animator = go.GetComponentInParent<Animator> (true);
                root = animator != null ? animator.gameObject : null;
            }
            if (root == null) return null;
            bool humanoid = root.GetComponentsInChildren<Animator> (true).Any (a => a.avatar != null && a.avatar.isHuman);
            return humanoid ? root : null;
        }

        /// <summary>世界を作り直した後、裏の表示モデルとシーンのキャラを結ぶ</summary>
        void BindSceneMirror ()
        {
            if (sceneTarget_ == null || stage_ == null || stage_.animator == null) {
                sceneMirror_.Unbind ();
                return;
            }
            Animator target = sceneTarget_.GetComponentsInChildren<Animator> (true).FirstOrDefault (a => a.avatar != null && a.avatar.isHuman);
            sceneMirror_.Bind (stage_.animator, target);
            ApplySceneMirror ();
        }

        /// <summary>今の姿勢をシーンのキャラへ写す（姿勢が変わるたび）</summary>
        void ApplySceneMirror ()
        {
            if (sceneTarget_ == null) return;
            sceneMirror_.Apply ();
            SceneView.RepaintAll ();
        }

        /// <summary>
        /// 毎フレーム: 編集しているキャラを見失っていないか。シーンを閉じる・開き直すと参照が切れる。見失ったらすぐ写すのをやめ
        /// （AnimationMode を掛けたままにすると Timeline がプレビューを始められない）、prefab の編集に戻す。
        /// 名札で引けるようになったら（同じシーンが開かれた）続きから編集する
        /// </summary>
        /// <returns>世界を作り直したか</returns>
        bool KeepSceneTarget ()
        {
            if (string.IsNullOrEmpty (sceneTargetId_) || EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (sceneTarget_ != null) return false;
            if (!sceneTargetLost_) {
                sceneTargetLost_ = true;
                sceneTarget_ = null;
                sceneContext_ = null;
                sceneMirror_.Unbind ();
                RebuildStage ();
                return true;
            }
            // 名札で引くのは重いので間を空ける
            double now = EditorApplication.timeSinceStartup;
            if (now < nextSceneTargetLookup_) return false;
            nextSceneTargetLookup_ = now + 1;
            GlobalObjectId id;
            if (!GlobalObjectId.TryParse (sceneTargetId_, out id)) return false;
            GameObject found = GlobalObjectId.GlobalObjectIdentifierToObjectSlow (id) as GameObject;
            if (found == null) return false;
            sceneTargetLost_ = false;
            sceneTarget_ = found;
            sceneContext_ = ContextProbe.ReadInspectedScene ();
            lastFollowedTime_ = double.NaN;
            RebuildStage ();
            return true;
        }

        /// <summary>
        /// 毎フレーム: Timeline 窓が開いている演出を読み直す（読むだけ）。演出を開き直した・クリップを動かした・バインドを変えたなら、
        /// Timeline Clip の段と時計の写像を作り直す
        /// </summary>
        void RefreshSceneContext ()
        {
            if (sceneTarget_ == null) {
                sceneContext_ = null;
                return;
            }
            ContextInfo next = ContextProbe.ReadInspectedScene ();
            bool same = next == null ? sceneContext_ == null
                : sceneContext_ != null && next.instance == sceneContext_.instance && next.signature == sceneContext_.signature;
            if (same) return;
            sceneContext_ = next;
            lastFollowedTime_ = double.NaN;
            if (stage_ == null) return;
            // 演出のクリップの有無で Timeline Clip の段が出入りする
            BuildStack ();
            SamplePose ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 毎フレーム: 持ち主（Timeline のプレビュー）が AnimationMode を立て直すと登録が外れるので、外れていたら写し直す
        /// </summary>
        void KeepSceneMirror ()
        {
            if (sceneTarget_ == null || sceneMirror_.target == null) return;
            if (!sceneMirror_.registered) ApplySceneMirror ();
        }
    }

}
