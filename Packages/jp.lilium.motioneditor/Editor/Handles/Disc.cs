using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 軸まわりに回すハンドル。丸ごとの輪ではなく、90° の弧だけを描いて拾う。
    /// 軸まわりの一周をローカルの 2 本の軸（u・w）で 4 つに分け（0〜90°・90〜180°・180〜270°・270〜360°）、
    /// カメラに向いている 1 つを描く。3 軸ともそうするので、3 本の弧は端でつながってカメラ側の球面の三角形になる。
    /// 3 軸を輪で出すと画面の上で何度も交わって狙った軸を選びにくいため。
    /// 弧は骨に付いて回り、視点を変えるとその場で切り替わる（つかんでいる間は切り替えない）
    /// </summary>
    public sealed class Disc
    {
        /// <summary>弧の角度</summary>
        const float kArcAngle = 90f;
        /// <summary>
        /// 範囲を切り替えるのは、カメラへの向きが境目をこれだけ越えてから（向きの内積。約 3°）。
        /// 境目の近くで視点を少し動かしただけで行ったり来たりしないように
        /// </summary>
        const float kSwitchMargin = 0.05f;
        /// <summary>弧の太さ（マウスが乗っていない間）</summary>
        const float kArcThickness = 4f;
        /// <summary>弧の太さ（マウスが乗っている間・回している間）</summary>
        const float kArcThicknessFocus = 6f;

        static Quaternion _startRotation = Quaternion.identity;
        static Vector3 _startAxis = Vector3.right;
        static Vector3 _grabPoint = Vector3.zero;
        static Vector2 _startMousePosition = Vector2.zero;
        static float _angle = 0f;
        /// <summary>ハンドルごとの今の範囲（u・w の向きの符号。+1 / -1）</summary>
        static readonly System.Collections.Generic.Dictionary<int, Vector2Int> _quadrants = new System.Collections.Generic.Dictionary<int, Vector2Int> ();

        /// <param name="axis">回す軸（ローカルの X・Y・Z をワールドで）</param>
        /// <param name="u">軸と直角なローカルの軸（ワールドで）。axis = u × w の並びで渡す</param>
        /// <param name="w">軸と直角なもう 1 本のローカルの軸（ワールドで）</param>
        public static Quaternion Do (int controlID, Quaternion rotation, Vector3 position, Vector3 axis, Vector3 u, Vector3 w, float size, float snap)
        {
            Event ev = Event.current;
            axis = axis.normalized;
            Vector3 from;
            Quadrant (controlID, position, u.normalized, w.normalized, out from);

            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if (HandleUtility.nearestControl == controlID && ev.button == 0) {
                        GUIUtility.hotControl = controlID;
                        _startRotation = rotation;
                        _startAxis = axis;
                        _grabPoint = HandleUtility.ClosestPointToArc (position, axis, from, kArcAngle, size);
                        _startMousePosition = ev.mousePosition;
                        _angle = 0;
                        ev.Use ();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        GUIUtility.hotControl = 0;
                        ev.Use ();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID) {
                        // つかんだ点の接線にそったマウスの移動量を、そのまま弧の上をなぞった長さとして角度にする
                        Vector3 tangent = Vector3.Cross (_startAxis, _grabPoint - position);
                        if (tangent.sqrMagnitude > 0 && size > 0) {
                            float moved = HandleUtility.CalcLineTranslation (_startMousePosition, ev.mousePosition, _grabPoint, tangent.normalized);
                            _angle = moved / size * Mathf.Rad2Deg;
                            if (snap > 0) _angle = Handles.SnapValue (_angle, snap);
                            rotation = Quaternion.AngleAxis (_angle, _startAxis) * _startRotation;
                            GUI.changed = true;
                        }
                        ev.Use ();
                    }
                    break;

                case EventType.Repaint:
                    Draw (controlID, position, axis, from, size);
                    break;

                case EventType.Layout:
                    float distance = HandleUtility.DistanceToArc (position, axis, from, kArcAngle, size);
                    // Unity の Handles.Disc と同じく、他のハンドルより輪を選びやすくしておく
                    HandleUtility.AddControl (controlID, distance * 0.5f);
                    break;
            }

            return rotation;
        }

        /// <summary>
        /// カメラに向いている範囲を選び、弧の始まりの向きを返す。弧は始まりから軸まわりに +90°。
        /// つかんでいる間は範囲を変えない（骨と一緒に回るので、回しているうちにカメラとの向きが変わっても弧が飛ばない）
        /// </summary>
        static void Quadrant (int controlID, Vector3 position, Vector3 u, Vector3 w, out Vector3 from)
        {
            Vector2Int signs;
            if (!_quadrants.TryGetValue (controlID, out signs)) signs = new Vector2Int (1, 1);
            Camera camera = Camera.current;
            if (camera != null && GUIUtility.hotControl != controlID) {
                Vector3 view = camera.orthographic ? -camera.transform.forward : camera.transform.position - position;
                if (view.sqrMagnitude > 0) {
                    view.Normalize ();
                    signs.x = Pick (signs.x, Vector3.Dot (view, u));
                    signs.y = Pick (signs.y, Vector3.Dot (view, w));
                }
            }
            _quadrants[controlID] = signs;
            // u から w へは軸まわりに +90°。片方だけ裏返したときは、w 側から始めると +90° で u 側へ届く
            from = signs.x == signs.y ? u * signs.x : w * signs.y;
        }

        /// <summary>カメラへの向き（内積）の符号。境目の近くでは今の符号のまま</summary>
        static int Pick (int current, float dot)
        {
            if (current > 0 ? dot < -kSwitchMargin : dot > kSwitchMargin) return -current;
            return current;
        }

        static void Draw (int controlID, Vector3 position, Vector3 axis, Vector3 from, float size)
        {
            bool isHot = GUIUtility.hotControl == controlID;
            bool isFocus = isHot || (GUIUtility.hotControl == 0 && HandleUtility.nearestControl == controlID);
            Color color = Handles.color;
            if (isFocus) Handles.color = PoseHandles.selectedColor;
            // 回している間は、つかんだ点からどれだけ回したかを扇で見せる
            if (isHot) {
                Vector3 grabDirection = _grabPoint - position;
                if (grabDirection.sqrMagnitude > 0) {
                    Color arcColor = Handles.color;
                    Handles.color = new Color (arcColor.r, arcColor.g, arcColor.b, 0.15f);
                    Handles.DrawSolidArc (position, _startAxis, grabDirection.normalized, _angle, size);
                    Handles.color = arcColor;
                }
            }
            float thickness = isFocus ? kArcThicknessFocus : kArcThickness;
            // 両端に矢は付けない。陰の付いた円錐が弧の端で浮いて見え、平らな弧だけの方が他のハンドルとなじむ
            Handles.DrawWireArc (position, axis, from, kArcAngle, size, thickness);
            Handles.color = color;
        }
    }

}
