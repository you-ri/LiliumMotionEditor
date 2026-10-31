using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using System.Linq;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Spinner（2D の操作パネル）が使う、選んでいる物を動かしてキーを打つところ。
    /// パネルの見た目と操作は ControllerOverlay（Spinner と数値欄を 1 枚にしたもの）
    /// </summary>
    public partial class PreviewWindow
    {
        public enum SpinnerMode
        {
            Move,
            Rotate,
            Scale,
        }

        [SerializeField] SpinnerMode spinnerMode_ = SpinnerMode.Rotate;

        public SpinnerMode spinnerMode
        {
            get { return spinnerMode_; }
            set {
                if (spinnerMode_ == value) return;
                spinnerMode_ = value;
                RaiseStateChanged ();
            }
        }

        /// <summary>ビューのギズモ（移動・回転）の軸をワールドにするか（false なら骨の軸 = ローカル）</summary>
        [SerializeField] bool gizmoWorld_;

        public bool gizmoWorld
        {
            get { return gizmoWorld_; }
            set {
                if (gizmoWorld_ == value) return;
                gizmoWorld_ = value;
                RaiseStateChanged ();
                RepaintView ();
            }
        }

        /// <summary>
        /// 全身 IK の点を動かすとき、床（ワールドの Y = 0）より下へ置かない。解いた姿勢の接地の点も床より下へ潜らせない（Controller の Floor Clamp）。
        /// 操作のときだけのフラグなので、窓が持って点と全身 IK へ配る（点ごとにもクリップにも保存しない）
        /// </summary>
        [SerializeField] bool floorClamp_;

        public bool floorClamp
        {
            get { return floorClamp_; }
            set {
                if (floorClamp_ == value) return;
                floorClamp_ = value;
                ApplyFloorClamp ();
                RaiseStateChanged ();
            }
        }

        /// <summary>全身 IK の点があるか（Floor Clamp を出すか）</summary>
        public bool hasBodyPoints
        {
            get { return targets_.Any (t => t is BodyPointTarget); }
        }

        /// <summary>
        /// Floor Clamp を点と全身 IK へ配る（フラグを変えたとき・点の対象を作り直したとき）。
        /// 点は動かした場所を、全身 IK は解いた姿勢の接地の点（足首を Pin にして腰を下げたときのつま先など）を床に当てる
        /// </summary>
        void ApplyFloorClamp ()
        {
            foreach (PoseTarget target in targets_) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) point.floorClamp = floorClamp_;
            }
            if (stage_ != null && stage_.fullBody != null) stage_.fullBody.floorClamp = floorClamp_;
        }

        /// <summary>
        /// Spinner に出す、いま動かすものの名前（何も選んでいなければ null）
        /// </summary>
        public string spinTargetLabel
        {
            get {
                PoseTarget target = selectedTarget;
                return target != null ? target.GetSpinLabel () : null;
            }
        }

        /// <summary>
        /// そのモードで動かせるか（動かせる値はつかむ対象が決める。編集用リグの値にスケールは無い）
        /// </summary>
        public bool CanSpin (SpinnerMode mode)
        {
            return CanEditValues (ToChannel (mode));
        }

        static TransformChannels ToChannel (SpinnerMode mode)
        {
            switch (mode) {
                case SpinnerMode.Move: return TransformChannels.Position;
                case SpinnerMode.Rotate: return TransformChannels.Rotation;
                default: return TransformChannels.Scale;
            }
        }

        /// <param name="delta">親から見た位置の変化（メートル）</param>
        public void SpinMove (Vector3 delta)
        {
            SpinEdit (TransformChannels.Position, target => target.SpinMove (delta));
        }

        /// <param name="euler">ローカル軸まわりの角度（度）</param>
        public void SpinRotate (Vector3 euler)
        {
            SpinEdit (TransformChannels.Rotation, target => target.SpinRotateLocal (Quaternion.Euler (euler)));
        }

        /// <summary>
        /// 見ている向きの軸まわりに回す（横ドラッグで画面の上下軸、縦ドラッグで画面の左右軸。複数軸がまとめて動く）
        /// </summary>
        /// <param name="drag">画面上の動き（度）</param>
        public void SpinRotateView (Vector2 drag)
        {
            SpinEdit (TransformChannels.Rotation, target => {
                Quaternion view = camera_.rotation;
                target.SpinRotateWorld (Quaternion.AngleAxis (drag.x, view * Vector3.up) * Quaternion.AngleAxis (drag.y, view * Vector3.right));
            });
        }

        /// <param name="delta">軸ごとの倍率の変化（0.1 なら 1.0 → 1.1）</param>
        public void SpinScale (Vector3 delta)
        {
            SpinEdit (TransformChannels.Scale, target => target.SpinScale (delta));
        }

        /// <summary>
        /// Spinner・数値欄で選んでいる物を動かす共通の流れ: 動かせるか見る → 動かす（子の Pin の点も連れていく） → 解き直して動かした物のキー
        /// </summary>
        void SpinEdit (TransformChannels channels, System.Action<PoseTarget> edit)
        {
            PoseTarget target = selectedTarget;
            if (target == null || !CanEditValues (channels)) return;
            EditCarryingPinnedPoints (new[] { target }, () => edit (target));
            AfterSpin (channels);
        }

        /// <summary>
        /// targets を edit で動かす。全身 IK の点をつかむ間に FK の骨を動かしたなら、子の Pin の点を一緒に動かす
        /// （動かす前の骨を控えて、動かした骨の差を掛ける。CarryPinnedPoints）
        /// </summary>
        void EditCarryingPinnedPoints (IEnumerable<PoseTarget> targets, System.Action edit)
        {
            List<FkBefore> before = CaptureFkBefore (targets);
            edit ();
            CarryPinnedPoints (before);
        }

        /// <summary>
        /// 動かした後。下の段を解き直して、動かしたものだけキーを打つ。
        /// 姿勢全体のキーはドラッグを離したとき（EndSpin）に打つ。全部のカーブに打つと重く、ドラッグが引っかかる
        /// </summary>
        void AfterSpin (TransformChannels channels)
        {
            PoseTarget target = selectedTarget;
            if (stage_ == null || target == null) return;

            SolveAfterManipulate ();
            // 全身 IK の点は、離したとき（EndSpin）に姿勢ごとキーを打つ
            if (CanEditClip () && !(target is BodyPointTarget) && !(target is HeldObjectTarget)) {
                using (CurveWriter writer = BeginCurves ("Spin " + target.label, true)) {
                    target.WriteSpinKeys (writer, channels);
                }
                keyFrames_ = null;
            }
            RepaintView ();
        }

        /// <summary>
        /// Spinner のドラッグを始めたとき。再生中なら時計を止める
        /// </summary>
        public void BeginSpin ()
        {
            if (!clock_.isPlaying) return;
            spinHold_ = true;
            HoldClock ();
        }

        /// <summary>
        /// Spinner のドラッグを離したとき。そのときの姿勢を全コントローラーぶんキーにする
        /// </summary>
        public void EndSpin ()
        {
            spinHold_ = false;
            ReleaseClock ();
            if (!CanEditClip ()) return;
            CommitPose (HasPendingPointKeys ());
        }

        // ---- 数値欄・Reset・左右反転（S9。Spinner と同じパネル） ----

        /// <summary>
        /// 選んでいる物の数値欄の値（保存している値。位置はメートル）
        /// </summary>
        public bool TryGetSpinValues (out Vector3 position, out Quaternion rotation)
        {
            PoseTarget target = selectedTarget;
            if (target != null) return target.TryGetValues (out position, out rotation);
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// 動かしても表示に出ない理由（後ろの段のクリップが 100% で表示を上書きしているとき）。無ければ null
        /// </summary>
        public string spinOverrideWarning
        {
            get {
                PoseLayer humanoid = poseStack_ != null ? poseStack_.Find (LayerKind.HumanoidAnimation) : null;
                if (humanoidClip_ == null || humanoid == null || !humanoid.enabled || humanoid.clipWeight < 1) return null;
                return Tr ("PREVIEW_WINDOW_SPINNER_HUMANOID_CLIP_OVERRIDES_DISPLAY", humanoidClip_.name);
            }
        }

        /// <summary>
        /// そのチャンネルの値を入れられるか（動かせる値はつかむ対象が決める）
        /// </summary>
        public bool CanEditValues (TransformChannels channel)
        {
            PoseTarget target = selectedTarget;
            return target != null && (target.GetSpinChannels () & channel) != 0 && CanEditTarget (target);
        }

        /// <summary>
        /// 数値欄を動かしている途中（ラベルのドラッグ・入力中）。姿勢へはすぐ出し、キーは動かした物だけ打つ。
        /// 離したとき・確定したときに EndSpin で姿勢全体のキーを打つ（Spinner のドラッグと同じ流れ）
        /// </summary>
        public void SetSpinValuesLive (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            SpinEdit (channels, target => target.SetValues (channels, position, rotation));
        }

        /// <summary>
        /// そのチャンネルだけ基準姿勢の値へ戻して、今のフレームにキーを打つ
        /// </summary>
        public void ResetSpinValues (TransformChannels channels)
        {
            PoseTarget target = selectedTarget;
            if (target == null || !CanEditValues (channels)) return;
            ApplyValueEdit (target, "Reset " + target.label, () => target.ResetValues (channels));
        }

        /// <summary>選んでいる物のうち、今のフレームで値を入れられる物（いくつ選んでいても全部）</summary>
        List<PoseTarget> GetEditableSelectedTargets ()
        {
            return SelectedTargets ().Where (CanEditTarget).ToList ();
        }

        /// <summary>選んでいる物のどれかの値を戻せるか（Controller パネルの Reset）</summary>
        public bool CanResetSelectedValues ()
        {
            // 再生中も毎フレーム聞かれるので、1 つ見つかれば止める
            return SelectedTargets ().Any (CanEditTarget);
        }

        /// <summary>
        /// 選んでいる物すべての値を基準姿勢へ戻して、今のフレームにキーを打つ（Controller パネルの Reset）。
        /// 全身 IK の点は値だけ戻し、固定の強さ（Free / Pin / Lock）は変えない（変えるのは窓の ResetPose）
        /// </summary>
        public void ResetSelectedValues ()
        {
            List<PoseTarget> targets = GetEditableSelectedTargets ();
            if (targets.Count == 0) {
                if (selectedTarget != null) NotifyNoKey ();
                return;
            }
            string undoName = targets.Count == 1 ? "Reset " + targets[0].label : "Reset Selection";
            ApplyValueEdit (targets, undoName, () => ResetValues (targets_, targets));
        }

        /// <summary>
        /// selected の値を基準姿勢へ戻す（Controller パネルの Reset の中身）。
        /// 全身 IK の点は、選んだ点の骨をまとめて戻す（子の点は、戻した親の骨に付いて動いた所へ）。
        /// 値を入れても骨は解き直すまで動かないので、どの点も戻す前の骨の姿勢から求まる。
        /// 点の骨の FK も基準へ戻す（10-22 ユーザー指示）: Free の点（向きを固定していない足首の点・踵・つま先など）は値を入れても解くときに効かず、
        /// 骨は FK の値のまま曲がって残っていた。足首の点は脛から見た基準へ戻しても場所が変わらないので、足を選んで Reset しても何も起きなかった
        /// </summary>
        internal static void ResetValues (IList<PoseTarget> targets, IList<PoseTarget> selected)
        {
            HashSet<Transform> pointBones = new HashSet<Transform> (selected.OfType<BodyPointTarget> ().Select (p => p.editingBone).Where (b => b != null));
            foreach (PoseTarget target in selected) {
                BodyPointTarget point = target as BodyPointTarget;
                if (point != null) {
                    point.ResetValues (TransformChannels.All, pointBones);
                    continue;
                }
                target.ResetValues (TransformChannels.All);
            }
            // 点の値を先に求める（FK は骨をすぐ動かすので、後の点が戻した後の骨から求まってしまう）。選んでいる FK は上で戻してある
            foreach (Transform bone in pointBones) {
                FkTarget fk = FindFkTarget (targets, bone);
                if (fk != null && !selected.Contains (fk)) fk.ResetValues (TransformChannels.All);
            }
        }

        /// <summary>
        /// 値を入れた後の共通の流れ: 解き直す → 動かした対象のキー → 姿勢全体のキー（Spinner を離したときと同じ）。Undo は 1 回にまとめる
        /// </summary>
        void ApplyValueEdit (PoseTarget target, string undoName, System.Action edit)
        {
            ApplyValueEdit (new[] { target }, undoName, edit);
        }

        /// <summary>いくつかの対象の値をまとめて入れたとき（キーは入れた対象すべてに打つ）</summary>
        void ApplyValueEdit (IList<PoseTarget> targets, string undoName, System.Action edit)
        {
            if (stage_ == null) return;
            PoseCommit.InUndoGroup (undoName, () => {
                // 全身 IK の点をつかむ間に FK の値を入れた・戻した: 子の Pin の点を一緒に動かす
                EditCarryingPinnedPoints (targets, edit);
                SolveAfterManipulate ();
                // 入れた対象も含めて、姿勢全体のキーを打つ（Spinner を離したときと同じ）
                CommitPose (targets.Any (t => t is BodyPointTarget || t is HeldObjectTarget));
            });
        }

        void RaiseStateChanged ()
        {
            if (stateChanged != null) stateChanged ();
        }
    }

}
