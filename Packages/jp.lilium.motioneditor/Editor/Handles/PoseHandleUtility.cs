using UnityEngine;
using UnityEditor;
using System.Collections;

namespace Lilium
{


    public static class PoseHandleUtility
    {
        static readonly Color SelectBoneColor = new Color (1, 0.5f, 0, 0.4f);
        public static readonly Color StandardColor = new Color (0, 1, 0, 0.1f);
        public static int focusControl = 0;
        /// <summary>
        /// 全身 IK の点を選んでいる間に出すギズモを回転にするか（Controller パネルのモードが Rot。ビューのギズモはモードに従う）。
        /// 窓がハンドルを描く前に入れる
        /// </summary>
        public static bool goalRotate = false;

        /// <summary>
        /// ハンドルの右クリック（点の固定の付け外し）を、押したときでなく「動かさずに離したとき」に出す。
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

        /// <summary>
        /// メッシュでマウスの下にある物（押したら選ぶ物。窓が描く前に入れる）。そのハンドルは、マウスが乗ったのと同じに強調して描く
        /// </summary>
        public static GameObject meshHover;

        /// <summary>メッシュでマウスの下にある物のハンドルなら、そのハンドルの番号（PoseHandles が乗った印で描く）</summary>
        static int MeshHoverControl (int controlHash, Transform t)
        {
            return meshHover != null && t != null && t.gameObject == meshHover && GUIUtility.hotControl == 0 ? controlHash : 0;
        }

        /// <param name="gizmo">押したときに移動のギズモを出すか（出さないなら呼んだ側が出す）</param>
        /// <param name="selectColor">選んでいる間、選択の色で塗るか（false なら color のまま。色で状態を表す丸は、呼んだ側が選択の色を決める）</param>
        /// <param name="scale">丸の大きさ（ハンドルの大きさの何倍か）</param>
        public static Vector3 DoJointHandle (int controlHash, Transform t, Vector3 position, Quaternion rotation, bool gizmo = true, bool selectColor = true, float scale = 1)
        {
            Event ev = Event.current;
            Vector3 outPosition;
            PoseHandles.color = selectColor && EditorHost.current.IsSelected (t.gameObject) ? SelectBoneColor : color;
            PoseHandles.hoverControl = MeshHoverControl (controlHash, t);
            PoseHandles.edgeColor = PoseHandles.color;
            if (PoseHandles.JointHandle (controlHash, position, EditorHost.current.handleSize * scale, out outPosition)) {
                if (ev.type == EventType.MouseDown) {
                    EditorHost.current.RecordUndo (t, "Move " + t.name);
                }
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            if (gizmo && focusControl == controlHash) {
                outPosition = PoseHandles.PositionHandle (outPosition, GizmoAxes (rotation));
            }
            return outPosition;
        }

        /// <summary>
        /// ギズモの軸をワールドにするか（Controller パネルの Local / World。false なら骨の軸）。窓がハンドルを描く前に入れる
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

        public static Quaternion DoBoneHandle (int controlHash, Transform t, Vector3 position, Quaternion rotation, float length)
        {
            Event ev = Event.current;
            Quaternion outRotation;
            PoseHandles.color = EditorHost.current.IsSelected (t.gameObject) ? SelectBoneColor : color;
            PoseHandles.edgeColor = PoseHandles.color;
            PoseHandles.hoverControl = MeshHoverControl (controlHash, t);
            if (PoseHandles.BoneHandle (controlHash, position, rotation, length, EditorHost.current.handleSize, out outRotation)) {
                if (ev.type == EventType.MouseDown) {
                    EditorHost.current.RecordUndo (t, "Rotate " + t.name);
                }
                EditorHost.current.Select (t.gameObject);
                focusControl = controlHash;
            }
            if (focusControl == controlHash) {
                Quaternion delta;
                if (DoRotateGizmo (controlHash, position, outRotation, out delta)) outRotation = delta * outRotation;
            }
            return outRotation;
        }

        /// <summary>
        /// 物（ゲームの Rig のターゲット・持ち替えの物）を動かすハンドル。target を移動の丸で動かし、選んでいるときだけ向きの棒も出す
        /// （胸などの骨のハンドルと重なるため）。動かしたら move・rotate に新しい位置・向きを渡し、GUI.changed を立てる。
        /// owner は選ぶ・Undo に記録する物
        /// </summary>
        public static void DoObjectHandles (int moveHash, int rotateHash, Transform owner, Transform target, float length, Color handleColor, bool selected,
            System.Action<Vector3> move, System.Action<Quaternion> rotate)
        {
            color = handleColor;
            try {
                Vector3 position = target.position;
                Quaternion rotation = target.rotation;
                Vector3 moved = DoJointHandle (moveHash, owner, position, rotation);
                if (moved != position) {
                    move (moved);
                    GUI.changed = true;
                }
                if (!selected) return;
                Quaternion rotated = DoBoneHandle (rotateHash, owner, target.position, rotation, length);
                if (rotated != rotation) {
                    rotate (rotated);
                    GUI.changed = true;
                }
            }
            finally {
                color = StandardColor;
            }
        }

    }

}
