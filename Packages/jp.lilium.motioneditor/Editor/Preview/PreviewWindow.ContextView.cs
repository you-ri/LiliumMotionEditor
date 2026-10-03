using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 演出のカメラ割りで見る・Unity の Timeline 窓の再生位置に付いていく（S15c）。
    ///
    /// 視点の出どころはここ 1 か所（ApplyView）。描画用のカメラとハンドル用のカメラの両方がここを通るので、
    /// 演出のカメラで見ていてもハンドルが骨からずれない
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>演出のカメラ割りで見る</summary>
        [SerializeField] bool useContextCamera_;
        /// <summary>Unity の Timeline 窓の再生位置に付いていく</summary>
        [SerializeField] bool followTimelineWindow_;

        double lastFollowedTime_ = double.NaN;
        string followStatus_;
        bool followWarning_;
        string contextCameraName_;

        /// <summary>演出のカメラ割りで見ているか</summary>
        public bool useContextCamera
        {
            get { return useContextCamera_; }
        }

        /// <summary>演出にカメラ割りがあるか</summary>
        public bool hasContextShots
        {
            get {
                foreach (ContextInfo info in contextInfos) {
                    if (info.shots.Count > 0) return true;
                }
                return false;
            }
        }

        public void SetUseContextCamera (bool value)
        {
            if (value == useContextCamera_) return;
            useContextCamera_ = value;
            RepaintView ();
            RaiseStateChanged ();
        }

        /// <summary>Timeline 窓に付いていっているか</summary>
        public bool followTimelineWindow
        {
            get { return followTimelineWindow_; }
        }

        public void SetFollowTimelineWindow (bool value)
        {
            if (value == followTimelineWindow_) return;
            followTimelineWindow_ = value;
            lastFollowedTime_ = double.NaN;
            followStatus_ = null;
            followWarning_ = false;
            if (value) FollowTimelineWindow ();
            RepaintView ();
            RaiseStateChanged ();
        }

        /// <summary>
        /// 視点をカメラへ当てる。演出のカメラ割りで見ていて、今の時刻にショットがあればそれを、無ければいつもの視点（オービット）を当てる。
        /// 当てた向き（ライトを合わせるため）を返す
        /// </summary>
        Quaternion ApplyView (Camera camera)
        {
            Pose pose;
            float fieldOfView;
            if (TryGetContextShot (out pose, out fieldOfView)) {
                camera.transform.SetPositionAndRotation (pose.position, pose.rotation);
                camera.fieldOfView = fieldOfView;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 1000;
                return pose.rotation;
            }
            camera_.Apply (camera);
            return camera_.rotation;
        }

        /// <summary>
        /// 今の演出の時刻のショット。演出のカメラ割りで見ていないとき・ショットが無い（読めない）ときは false
        /// </summary>
        bool TryGetContextShot (out Pose pose, out float fieldOfView)
        {
            pose = Pose.identity;
            fieldOfView = 0;
            contextCameraName_ = null;
            if (!useContextCamera_ || stage_ == null) return false;
            double master = clock_.MasterFromLocal (CurrentTime ());
            foreach (ContextInfo info in stage_.contextInfos) {
                string name;
                bool found = info.TryGetShot (master, out pose, out fieldOfView, out name);
                if (name != null) contextCameraName_ = found ? name : name + "（読めないのでいつもの視点）";
                if (found) return true;
            }
            if (contextCameraName_ == null) contextCameraName_ = "この時刻はショットが無い（いつもの視点）";
            return false;
        }

        /// <summary>
        /// 演出のカメラで見ているときに視点を動かし始めたら、その位置からいつもの視点（オービット）へ移る
        /// </summary>
        void LeaveContextCamera ()
        {
            if (!useContextCamera_) return;
            Pose pose;
            float fieldOfView;
            if (TryGetContextShot (out pose, out fieldOfView)) {
                // 注視点はキャラの手前（カメラからキャラまでの距離）に置く。回すとキャラのまわりを回る
                Vector3 center = stage_ != null && stage_.animator != null ? stage_.GetBounds ().center : pose.position + pose.rotation * Vector3.forward * 4;
                float distance = Mathf.Max (0.5f, Vector3.Dot (center - pose.position, pose.rotation * Vector3.forward));
                camera_.distance = distance;
                camera_.pivot = pose.position + pose.rotation * Vector3.forward * distance;
                camera_.LookAlong (pose.rotation * Vector3.forward);
                camera_.fieldOfView = fieldOfView;
            }
            SetUseContextCamera (false);
        }

        /// <summary>
        /// Timeline 窓の再生位置を読み、演出の時刻として当てる（窓の Update から毎回）。こちらが再生中は追わない
        /// </summary>
        void FollowTimelineWindow ()
        {
            // Motion Scene（S15d）はいつも付いていく（Timeline でプレビューしながら編集するための窓）
            bool follow = followTimelineWindow_ || sceneTarget_ != null;
            IReadOnlyList<ContextInfo> infos = contextInfos;
            if (!follow || stage_ == null || infos.Count == 0) {
                string idle = null;
                if (sceneTarget_ != null) idle = "Timeline 窓でこのシーンの演出（Director）を開くと、その時刻に付いていく";
                else if (followTimelineWindow_) idle = "演出を置いていないので Timeline 窓に付いていけない";
                SetFollowStatus (idle, false);
                return;
            }
            if (clock_.isPlaying) return;

            ContextClip link = activeTimelineClip;
            ExternalClock external = ContextProbe.ReadExternalClock (infos[0], link != null && link.track != null ? link.track.name : null);
            if (!external.available) {
                SetFollowStatus ("Timeline 窓に付いていけない: " + external.note, false);
                return;
            }
            // 最初の 1 回（NaN との比較は常に偽になるので分けて見る）と、向こうの時刻が動いたときだけ当てる
            if (double.IsNaN (lastFollowedTime_) || System.Math.Abs (external.time - lastFollowedTime_) > 1e-6) {
                lastFollowedTime_ = external.time;
                UpdateClockMapping ();
                clock_.SetMasterTime (external.time);
                SamplePose ();
                RaiseStateChanged ();
            }
            string status = "Timeline 窓に付いていっている（演出 " + masterFrame + "F）";
            if (link == null) {
                status += sceneTarget_ != null
                    ? "。演出の中に編集中のクリップが無いので、演出の時刻 ＝ クリップの時刻 ＋ オフセットで合わせている"
                    : "。演出のクリップと結び付けていないので、自キャラのトラックのバインドは確かめていない";
            }
            if (!string.IsNullOrEmpty (external.note)) status += "。" + external.note;
            SetFollowStatus (status, external.warning);
        }

        /// <summary>Timeline 窓への追従の様子（Motion Scene のパネルに出す。無ければ null）</summary>
        public string followStatus
        {
            get { return followStatus_; }
        }

        public bool followWarning
        {
            get { return followWarning_; }
        }

        void SetFollowStatus (string status, bool warning)
        {
            if (status == followStatus_ && warning == followWarning_) return;
            followStatus_ = status;
            followWarning_ = warning;
            RepaintView ();
        }

        /// <summary>
        /// 表示域の左上に出す、演出のカメラと Timeline 窓への追従の様子
        /// </summary>
        void DrawContextViewInfo (Rect rect)
        {
            float y = rect.y + 6;
            if (useContextCamera_ && contextCameraName_ != null) {
                GUI.Label (new Rect (rect.x + 8, y, rect.width - 16, 18), "カメラ: " + contextCameraName_, EditorStyles.whiteLabel);
                y += 18;
            }
            if (followStatus_ != null) {
                GUIStyle style = new GUIStyle (EditorStyles.whiteLabel);
                if (followWarning_) style.normal.textColor = new Color (1f, 0.8f, 0.3f);
                GUI.Label (new Rect (rect.x + 8, y, rect.width - 16, 18), followStatus_, style);
            }
        }
    }

}
