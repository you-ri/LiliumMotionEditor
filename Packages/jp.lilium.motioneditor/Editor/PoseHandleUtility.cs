using UnityEngine;
using UnityEditor;
using System.Collections;
using Lilium;

namespace Lilium
{


    public static class PoseHandleUtility
    {
        static readonly Color SelectBoneColor = new Color (1, 0.5f, 0, 0.4f);
        public static readonly Color StandardColor = new Color (0, 1, 0, 0.1f);
        public static int focusControl = 0;
        /// <summary>
        /// IK の目標を選んでいる間に出すギズモを回転にするか（Transform パネルのモードが Rot。ビューのギズモはモードに従う）。
        /// 窓がハンドルを描く前に入れる
        /// </summary>
        public static bool goalRotate = false;
        public static bool isMouseOver = false;

        /// <summary>
        /// ハンドルの右クリック（固定の付け外し・IK / FK の切り替え）を、押したときでなく「動かさずに離したとき」に出す。
        /// プレビュー窓が立てる: 右ボタンは視点の回転にも使うので、押した時点でハンドルが取ると、ハンドルの上から回し始められない
        /// </summary>
        public static bool contextClickOnRelease;
        /// <summary>動かさずに離した右ボタンを、今のイベントでハンドルへ渡している（contextClickOnRelease の間だけ意味を持つ）</summary>
        public static bool contextClickReleased;

        /// <summary>
        /// 今のイベントが、このハンドルの右クリックか
        /// </summary>
        public static bool IsContextClick (Event ev, int controlID)
        {
            if (ev.button != 1 || HandleUtility.nearestControl != controlID) return false;
            if (contextClickOnRelease) return contextClickReleased && ev.type == EventType.MouseUp;
            return ev.type == EventType.MouseDown;
        }
        public static Color color = StandardColor;

        public static bool IsSelectedController (GameObject go)
        {
            return EditorHost.current.IsSelected (go);
        }

        public static bool IsFocusedController (GameObject go)
        {
            return EditorHost.current.IsSelected (go);
        }

        public static Vector3 JointHandle (Transform t, Vector3 position, Quaternion rotation)
        {
            int controlHash = ("Joint" + t.GetEntityId ()).GetHashCode ();
            return DoJointHandle (controlHash, t, position, rotation);
        }

        /// <param name="gizmo">押したときに移動のギズモを出すか（出さないなら呼んだ側が DoGoalGizmo で出す）</param>
        /// <param name="selectColor">選んでいる間、選択の色で塗るか（false なら color のまま。色で状態を表す丸は、呼んだ側が選択の色を決める）</param>
        public static Vector3 DoJointHandle (int controlHash, Transform t, Vector3 position, Quaternion rotation, bool gizmo = true, bool selectColor = true)
        {
            Event ev = Event.current;
            Vector3 outPosition;
            PoseHandles.color = selectColor && EditorHost.current.IsSelected (t.gameObject) ? SelectBoneColor : color;
            PoseHandles.edgeColor = PoseHandles.color;
            if (PoseHandles.JointHandle (controlHash, position, EditorHost.current.handleSize, out outPosition)) {
                if (ev.type == EventType.MouseDown) {
                    EditorHost.current.RecordUndo (t, "Move " + t.name);
                }
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            isMouseOver = HandleUtility.nearestControl == controlHash ? true : false;
            if (gizmo && focusControl == controlHash) {
                outPosition = PoseHandles.PositionHandle (outPosition, GizmoAxes (rotation));
            }
            return outPosition;
        }

        /// <summary>
        /// IK の目標のギズモ。Transform パネルのモードが Rot なら回転（軸は axes）、それ以外なら移動。
        /// 回したら、回した差を rotation に前から掛けて返す（axes と rotation の向きが違っても同じだけ回る）
        /// </summary>
        /// <param name="axes">ギズモの軸の向き（骨の見た目の軸。FK の骨の回転ギズモとそろえる）</param>
        /// <param name="key">ギズモの持ち主を見分ける値（ハンドルの hash など。回している間の向きを持ち主ごとに覚える）</param>
        public static bool DoGoalGizmo (int key, ref Vector3 position, ref Quaternion rotation, Quaternion axes)
        {
            if (goalRotate) {
                Quaternion delta;
                if (!DoRotateGizmo (key, position, axes, out delta)) return false;
                rotation = delta * rotation;
                return true;
            }
            Vector3 moved = PoseHandles.PositionHandle (position, GizmoAxes (axes));
            if (moved == position) return false;
            position = moved;
            return true;
        }

        /// <summary>
        /// ギズモの軸をワールドにするか（Transform パネルの Local / World。false なら骨の軸）。窓がハンドルを描く前に入れる
        /// </summary>
        public static bool gizmoWorld = false;

        /// <summary>ギズモに渡す軸（ワールドなら回転なし、ローカルなら localAxes）</summary>
        public static Quaternion GizmoAxes (Quaternion localAxes)
        {
            return gizmoWorld ? Quaternion.identity : localAxes;
        }

        static int turningKey_;
        static Quaternion turningAxes_ = Quaternion.identity;

        /// <summary>
        /// 回転のギズモ。回したら、前の描画から回した差（ワールドで前から掛ける）を delta に返す。
        /// ギズモ（Disc）は、つかんだときの軸に回した角度を掛けた向きを返す（前の描画からの差ではない）。
        /// そのため回している間は前に返った向きを軸に渡し、差はその向きから取る。今の骨から軸を作り直して差を取ると、
        /// 軸が回した量どおりに付いてこないとき（骨の上の点のねじり・ワールドの軸）に差がずれて積み重なる
        /// </summary>
        /// <param name="key">ギズモの持ち主を見分ける値。回している持ち主のギズモだけ、前に返った向きを軸にする</param>
        /// <param name="localAxes">ローカルのときの軸（骨の見た目の軸）</param>
        public static bool DoRotateGizmo (int key, Vector3 position, Quaternion localAxes, out Quaternion delta)
        {
            if (GUIUtility.hotControl == 0) turningKey_ = 0;
            Quaternion axes = turningKey_ != 0 && turningKey_ == key ? turningAxes_ : GizmoAxes (localAxes);
            Quaternion turned = PoseHandles.RotateHandle (axes, position);
            if (turned == axes) {
                delta = Quaternion.identity;
                return false;
            }
            delta = turned * Quaternion.Inverse (axes);
            turningKey_ = key;
            turningAxes_ = turned;
            return true;
        }

        /// <summary>IK が入っている手足のリングの色（赤）</summary>
        public static readonly Color IkRingOnColor = new Color (0.95f, 0.2f, 0.15f, 0.9f);
        /// <summary>IK が切れている手足のリングの色（灰）</summary>
        public static readonly Color IkRingOffColor = new Color (0.65f, 0.65f, 0.65f, 0.7f);

        /// <summary>輪の太さ（半径に対する管の太さ）</summary>
        const float kRingTubeRatio = 0.12f;

        /// <summary>
        /// 骨の先に出す 3D のリング。左ボタンで押すとその対象を選んでギズモを出し（値は動かさない）、右ボタンは呼んだ側で IK / FK を切り替える。
        /// 色は状態（IK の入/切）のためにとってあるので、選んでいる間とマウスが乗っている間は色相を変えず、太く明るくする
        /// </summary>
        public static PoseHandles.CapsuleResult DoRingHandle (int controlHash, Transform t, Vector3 position, Quaternion rotation, float radius, Color ringColor)
        {
            bool highlight = EditorHost.current.IsSelected (t.gameObject) || HandleUtility.nearestControl == controlHash;
            Color drawColor = ringColor;
            float tube = radius * kRingTubeRatio;
            if (highlight) {
                drawColor = Color.Lerp (ringColor, Color.white, 0.35f);
                drawColor.a = 1;
                tube *= 1.6f;
            }
            PoseHandles.CapsuleResult result = PoseHandles.RingHandle (controlHash, position, rotation, radius, tube, drawColor);
            if (result == PoseHandles.CapsuleResult.Pressed) {
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            else if (result == PoseHandles.CapsuleResult.ContextClicked) {
                EditorHost.current.Select (t.gameObject);
            }
            return result;
        }

        /// <summary>
        /// 足元のカプセル（Reverse Foot のコントローラー）。押すとその対象を選び、つかんで動かすと delta を返す。
        /// 色は IK の入/切を表すので、選んでいる間とマウスが乗っている間は色相を変えずに太く明るくする（リングと同じ）
        /// </summary>
        public static PoseHandles.CapsuleResult DoCapsuleHandle (int controlHash, Transform t, Vector3 heel, Vector3 toe, Vector3 normal, float radius, Color capsuleColor, out Vector3 delta)
        {
            bool highlight = EditorHost.current.IsSelected (t.gameObject) || HandleUtility.nearestControl == controlHash;
            Color drawColor = capsuleColor;
            float tube = EditorHost.current.handleSize * 0.35f;
            if (highlight) {
                drawColor = Color.Lerp (capsuleColor, Color.white, 0.35f);
                drawColor.a = 1;
                tube *= 1.6f;
            }
            PoseHandles.CapsuleResult result = PoseHandles.CapsuleHandle (controlHash, heel, toe, normal, radius, tube, drawColor, out delta);
            if (result == PoseHandles.CapsuleResult.Pressed) {
                if (Event.current.type == EventType.Used) EditorHost.current.RecordUndo (t, "Move " + t.name);
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            else if (result == PoseHandles.CapsuleResult.ContextClicked) {
                EditorHost.current.Select (t.gameObject);
            }
            return result;
        }

        public static Quaternion BoneHandle (Transform t, Vector3 position, Quaternion rotation, float length)
        {
            int controlHash = ("Bone" + t.GetEntityId ()).GetHashCode ();
            return DoBoneHandle (controlHash, t, position, rotation, length);
        }

        public static Quaternion DoBoneHandle (int controlHash, Transform t, Vector3 position, Quaternion rotation, float length)
        {
            Event ev = Event.current;
            Quaternion outRotation;
            PoseHandles.color = EditorHost.current.IsSelected (t.gameObject) ? SelectBoneColor : color;
            PoseHandles.edgeColor = PoseHandles.color;
            if (PoseHandles.BoneHandle (controlHash, position, rotation, length, EditorHost.current.handleSize, out outRotation)) {
                if (ev.type == EventType.MouseDown) {
                    EditorHost.current.RecordUndo (t, "Rotate " + t.name);
                }
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            isMouseOver = HandleUtility.nearestControl == controlHash ? true : false;
            if (focusControl == controlHash) {
                Quaternion delta;
                if (DoRotateGizmo (controlHash, position, outRotation, out delta)) outRotation = delta * outRotation;
            }
            return outRotation;
        }

    }

}
