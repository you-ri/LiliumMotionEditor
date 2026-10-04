using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Linq;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Spinner（2D の操作パネル）が使う、選んでいる物を動かしてキーを打つところ。
    /// パネルの見た目と操作は TransformOverlay（Spinner と数値欄を 1 枚にしたもの）
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
        /// <summary>
        /// IK を選んでいるとき、目標（ゴール）ではなく肘・膝の向き（Up）を動かす
        /// </summary>
        [SerializeField] bool spinnerPole_;

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

        public bool spinnerPole
        {
            get { return spinnerPole_; }
            set {
                if (spinnerPole_ == value) return;
                spinnerPole_ = value;
                RaiseStateChanged ();
            }
        }

        /// <summary>
        /// Spinner に出す、いま動かすものの名前（何も選んでいなければ null）
        /// </summary>
        public string spinTargetLabel
        {
            get {
                PoseTarget target = selectedTarget;
                return target != null ? target.GetSpinLabel (spinnerPole_) : null;
            }
        }

        /// <summary>
        /// 選んでいるものが目標と Up（肘・膝の向き）を持つか（Spinner が切り替えられる）
        /// </summary>
        public bool isIKSelected
        {
            get {
                PoseTarget target = selectedTarget;
                return target != null && target.hasPole;
            }
        }

        /// <summary>
        /// そのモードで動かせるか（動かせる値はつかむ対象が決める。編集用リグの値にスケールは無い）
        /// </summary>
        public bool CanSpin (SpinnerMode mode)
        {
            PoseTarget target = selectedTarget;
            if (target == null) return false;
            return (target.GetSpinChannels (spinnerPole_) & ToChannel (mode)) != 0 && CanEditTarget (target);
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
            if (!CanSpin (SpinnerMode.Move)) return;
            selectedTarget.SpinMove (delta, spinnerPole_);
            AfterSpin (TransformChannels.Position);
        }

        /// <param name="euler">ローカル軸まわりの角度（度）</param>
        public void SpinRotate (Vector3 euler)
        {
            if (!CanSpin (SpinnerMode.Rotate)) return;
            selectedTarget.SpinRotateLocal (Quaternion.Euler (euler), spinnerPole_);
            AfterSpin (TransformChannels.Rotation);
        }

        /// <summary>
        /// 見ている向きの軸まわりに回す（横ドラッグで画面の上下軸、縦ドラッグで画面の左右軸。複数軸がまとめて動く）
        /// </summary>
        /// <param name="drag">画面上の動き（度）</param>
        public void SpinRotateView (Vector2 drag)
        {
            if (!CanSpin (SpinnerMode.Rotate)) return;
            Quaternion view = camera_.rotation;
            Quaternion rotation = Quaternion.AngleAxis (drag.x, view * Vector3.up) * Quaternion.AngleAxis (drag.y, view * Vector3.right);
            selectedTarget.SpinRotateWorld (rotation, spinnerPole_);
            AfterSpin (TransformChannels.Rotation);
        }

        /// <param name="delta">軸ごとの倍率の変化（0.1 なら 1.0 → 1.1）</param>
        public void SpinScale (Vector3 delta)
        {
            if (!CanSpin (SpinnerMode.Scale)) return;
            selectedTarget.SpinScale (delta, spinnerPole_);
            AfterSpin (TransformChannels.Scale);
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
            if (CanEditClip () && !(target is BodyPointTarget)) {
                using (CurveWriter writer = BeginCurves ("Spin " + target.label, true)) {
                    target.WriteSpinKeys (writer, spinnerPole_, channels);
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

            // 全身 IK の点を動かした・固定している点があるフレームで骨を動かした: 解いた姿勢を骨のキーとして打つ
            if (HasPendingPointKeys () || hasActivePoints) {
                CommitFullBodyPose ();
                return;
            }
            AddKeyAllCurves (true);
            RepaintView ();
        }

        // ---- 数値欄・Reset・左右反転（S9。Spinner と同じパネル） ----

        /// <summary>
        /// 選んでいる物の数値欄の値（保存している値。位置はメートル）
        /// </summary>
        public bool TryGetSpinValues (out Vector3 position, out Quaternion rotation)
        {
            PoseTarget target = selectedTarget;
            if (target != null) return target.TryGetValues (spinnerPole_, out position, out rotation);
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
            return target != null && (target.GetSpinChannels (spinnerPole_) & channel) != 0 && CanEditTarget (target);
        }

        /// <summary>
        /// 数値欄から値を入れて、今のフレームにキーを打つ（入力 1 回を 1 回の Undo にする）
        /// </summary>
        public void SetSpinValues (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            PoseTarget target = selectedTarget;
            if (target == null || !CanEditValues (channels)) return;
            ApplyValueEdit (target, "Set " + target.label, () => target.SetValues (spinnerPole_, channels, position, rotation));
        }

        /// <summary>
        /// 数値欄を動かしている途中（ラベルのドラッグ・入力中）。姿勢へはすぐ出し、キーは動かした物だけ打つ。
        /// 離したとき・確定したときに EndSpin で姿勢全体のキーを打つ（Spinner のドラッグと同じ流れ）
        /// </summary>
        public void SetSpinValuesLive (TransformChannels channels, Vector3 position, Quaternion rotation)
        {
            PoseTarget target = selectedTarget;
            if (target == null || !CanEditValues (channels)) return;
            target.SetValues (spinnerPole_, channels, position, rotation);
            AfterSpin (channels);
        }

        /// <summary>
        /// そのチャンネルだけ基準姿勢の値へ戻して、今のフレームにキーを打つ
        /// </summary>
        public void ResetSpinValues (TransformChannels channels)
        {
            PoseTarget target = selectedTarget;
            if (target == null || !CanEditValues (channels)) return;
            ApplyValueEdit (target, "Reset " + target.label, () => target.ResetValues (spinnerPole_, channels));
        }

        // ---- 足の転がし（S22b。Transform パネルの roll / bank / twist の行） ----

        /// <summary>選んでいる物が足の転がしを持つ脚の IK なら、その対象。違えば null</summary>
        IkTarget selectedFoot
        {
            get {
                IkTarget target = selectedTarget as IkTarget;
                return target != null && target.hasFootAngles ? target : null;
            }
        }

        /// <summary>選んでいる脚の足の転がしの角度（度。x = roll・y = bank・z = twist）。足の転がしを持つ脚でなければ false</summary>
        public bool TryGetFootAngles (out Vector3 angles)
        {
            IkTarget target = selectedFoot;
            if (target != null) return target.TryGetFootAngles (out angles);
            angles = Vector3.zero;
            return false;
        }

        /// <summary>足の転がしの角度を入れられるか（キーの自動追加が切なら、今のフレームにその脚のキーがあるときだけ）</summary>
        public bool CanEditFootAngles ()
        {
            IkTarget target = selectedFoot;
            return target != null && CanEditTarget (target);
        }

        /// <summary>
        /// 数値欄から足の転がしの角度を入れている途中。姿勢へはすぐ出し、キーはその脚だけ打つ。
        /// 離したとき・確定したときに EndSpin で姿勢全体のキーを打つ（数値欄の位置・回転と同じ流れ）
        /// </summary>
        public void SetFootAnglesLive (Vector3 angles)
        {
            IkTarget target = selectedFoot;
            if (target == null || !CanEditTarget (target) || stage_ == null) return;
            target.SetFootAngles (angles);
            SolveAfterManipulate ();
            if (CanEditClip ()) {
                using (CurveWriter writer = BeginCurves ("Spin " + target.label, true)) {
                    target.WriteKeys (writer);
                }
                keyFrames_ = null;
            }
            RepaintView ();
        }

        /// <summary>
        /// Spinner の扇形を足の転がし（Roll / Twist / Bank）にするか（Reverse Foot）。
        /// 足の転がしを持つ脚の IK の目標を、回転のモードで動かすとき。移動・Pole のときは今までどおり X / Y / Z
        /// </summary>
        public bool spinnerFoot
        {
            get { return spinnerMode_ == SpinnerMode.Rotate && !spinnerPole_ && selectedFoot != null; }
        }

        /// <summary>Spinner の扇形のドラッグで、足の転がしの角度を足す（度。x = roll・y = bank・z = twist）</summary>
        public void SpinFootAngles (Vector3 delta)
        {
            Vector3 angles;
            if (!CanEditFootAngles () || !TryGetFootAngles (out angles)) return;
            SetFootAnglesLive (angles + delta);
        }

        /// <summary>足の転がしの角度を 0 に戻して、今のフレームにキーを打つ</summary>
        public void ResetFootAngles ()
        {
            IkTarget target = selectedFoot;
            if (target == null || !CanEditTarget (target)) return;
            ApplyValueEdit (target, "Reset Foot " + target.label, () => target.SetFootAngles (Vector3.zero));
        }

        /// <summary>
        /// 選んでいる物の左右の相手（体の中心の骨なら自分）。反転できなければ null
        /// </summary>
        public PoseTarget GetMirrorTarget ()
        {
            PoseTarget target = selectedTarget;
            string key = target != null ? target.mirrorKey : null;
            if (key == null) return null;
            string other = MirrorNames.Swap (key) ?? key;
            foreach (PoseTarget candidate in targets_) {
                if (candidate.mirrorKey == other && candidate.GetType () == target.GetType ()) return candidate;
            }
            return null;
        }

        /// <summary>
        /// 選んでいる物の今の見た目を左右反転して、相手へ入れる（体の中心の骨は自分を反転する）。相手のフレームにキーを打つ
        /// </summary>
        public void MirrorSpinValues ()
        {
            PoseTarget source = selectedTarget;
            PoseTarget target = GetMirrorTarget ();
            if (source == null || target == null) return;
            if (!CanEditTarget (target)) {
                NotifyNoKey ();
                return;
            }
            ApplyValueEdit (target, "Mirror " + source.label, () => target.MirrorFrom (source));
        }

        /// <summary>
        /// 値を入れた後の共通の流れ: 解き直す → 動かした対象のキー → 姿勢全体のキー（Spinner を離したときと同じ）。Undo は 1 回にまとめる
        /// </summary>
        void ApplyValueEdit (PoseTarget target, string undoName, System.Action edit)
        {
            if (stage_ == null) return;
            Undo.IncrementCurrentGroup ();
            int group = Undo.GetCurrentGroup ();
            edit ();
            SolveAfterManipulate ();
            if (target is BodyPointTarget || hasActivePoints) {
                CommitFullBodyPose ();
                Undo.SetCurrentGroupName (undoName);
                Undo.CollapseUndoOperations (group);
                return;
            }
            if (CanEditClip ()) {
                using (CurveWriter writer = BeginCurves (undoName, true)) {
                    target.WriteKeys (writer);
                }
                AddKeyAllCurves (true);
            }
            Undo.SetCurrentGroupName (undoName);
            Undo.CollapseUndoOperations (group);
            keyFrames_ = null;
            RepaintView ();
        }

        void RaiseStateChanged ()
        {
            if (stateChanged != null) stateChanged ();
        }

        // Move / Rot / Scale は Unity の Tools/Move・Rotate・Scale と同じ W / E / R。
        // あちらは全体のショートカットなので、窓にフォーカスがある間は窓のものが勝つ（ぶつかりの確認は出ない）
        [Shortcut ("Lilium Motion Editor/Spinner Move", typeof (PreviewWindow), KeyCode.W)]
        static void SpinnerMoveShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.spinnerMode = SpinnerMode.Move;
        }

        [Shortcut ("Lilium Motion Editor/Spinner Rotate", typeof (PreviewWindow), KeyCode.E)]
        static void SpinnerRotateShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.spinnerMode = SpinnerMode.Rotate;
        }

        [Shortcut ("Lilium Motion Editor/Spinner Scale", typeof (PreviewWindow), KeyCode.R)]
        static void SpinnerScaleShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.spinnerMode = SpinnerMode.Scale;
        }

        [Shortcut ("Lilium Motion Editor/Spinner Toggle Pole", typeof (PreviewWindow), KeyCode.U)]
        static void SpinnerPoleShortcut (ShortcutArguments args)
        {
            PreviewWindow window = args.context as PreviewWindow;
            if (window != null) window.spinnerPole = !window.spinnerPole;
        }
    }

}
