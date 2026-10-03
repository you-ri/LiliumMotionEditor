using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using Lilium;

namespace Lilium
{

    public sealed class AxisSlider
    {
        public static Color edgeColor = Color.yellow;
        private static Vector3 hangPoint_ = Vector3.zero;
        private static float kDistanceMouseOver = 3.0f;

        public static Vector3 Do (int controlID, Vector3 position, Vector3 direction, float size)
        {
            Event ev = Event.current;

            Vector3 endPosition = position + direction.normalized * size;

            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if ((HandleUtility.nearestControl == controlID && ev.button == 0)) {
                        Ray mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        Vector3 projectPoint = GeometryMath.GetClosestPointToLine (mouseRay.origin, position, endPosition);
                        GUIUtility.hotControl = controlID;

                        hangPoint_ = projectPoint - position;
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
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        Ray mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        Vector3 projectPoint = GeometryMath.GetClosestPointToLine (mouseRay.origin, position, endPosition);
                        position = projectPoint - hangPoint_;
                        GUI.changed = true;
                        ev.Use ();
                    }
                    break;

                case EventType.Repaint:
                    if (HandleUtility.nearestControl == controlID || GUIUtility.hotControl == controlID) {
                        Handles.color = Color.yellow;
                    }
                    Handles.DrawLine (position, endPosition);
                    Handles.ArrowHandleCap (controlID, position, Quaternion.LookRotation (direction), size, ev.GetTypeForControl (controlID));
                    break;

                case EventType.Layout:
                    float dist = HandleUtility.DistanceToLine (position, endPosition);
                    if (dist < kDistanceMouseOver) {
                        HandleUtility.AddControl (controlID, dist);
                    }
                    break;
            }

            return position;
        }

        public static bool RaycastHitToJoint (Ray ray, Vector3 basePoint, float size, out HitInfo hitInfo)
        {
            return GeometryMath.RaycastToShpere (ray, new Sphere { center = basePoint, radius = size }, out hitInfo);
        }

    }

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

    public sealed class PoseHandles
    {
        internal static int xAxisSliderHash = "MktXAxisSliderHash".GetHashCode ();
        internal static int yAxisSliderHash = "MktYAxisSliderHash".GetHashCode ();
        internal static int zAxisSliderHash = "MktZAxisSliderHash".GetHashCode ();
        internal static int xAxisDiscHash = "MktXAxisDiscHash".GetHashCode ();
        internal static int yAxisDiscHash = "MktYAxisDiscHash".GetHashCode ();
        internal static int zAxisDiscHash = "MktZAxisDiscHash".GetHashCode ();
        /// <summary>画面まわりの回転の輪を、軸の弧より一回り大きくする割合</summary>
        const float kCameraDiscScale = 1.1f;
        internal static Color xAxisColor = new Color (0.858823538f, 0.243137255f, 0.113725491f, 1);
        internal static Color yAxisColor = new Color (0.6039216f, 0.9529412f, 0.282352954f, 1);
        internal static Color zAxisColor = new Color (0.227450982f, 0.478431374f, 0.972549f, 1);
        internal static Color centerColor = new Color (0.8f, 0.8f, 0.8f, 1);
        internal static Color selectedColor = new Color (0.9647059f, 0.9490196f, 0.196078435f, 0.89f);
        internal static Color secondaryColor = new Color (0.5f, 0.5f, 0.5f, 0.2f);

        /// <summary>
        /// 今のフレームにキーが無くて動かせない物のハンドルを描いている間（キーの自動追加が切のとき。窓が立てる）。
        /// 薄く描くだけで、マウスは受けない（Layout で近さを出さないので選ばれない）。ギズモも出さない。
        /// 触れるようにしておくと、動かしても戻されるだけで、キーの無いことが見た目で分からない
        /// </summary>
        public static bool locked;

        /// <summary>動かせない物のハンドルの不透明度の倍率</summary>
        const float kLockedAlpha = 0.3f;

        /// <summary>動かせない物（locked）を描いている間は薄くした色、それ以外はそのままの色</summary>
        public static Color Fade (Color color)
        {
            if (locked) color.a *= kLockedAlpha;
            return color;
        }

        public static Quaternion RotateHandle (Quaternion rotation, Vector3 position)
        {
            if (locked) return rotation;
            float handleSize = HandleUtility.GetHandleSize (position);
            Color color = Handles.color;
            Handles.color = xAxisColor;
            float snap = -1;
            // 弧はそれぞれ残りの 2 軸の間の 90°（カメラ側）。axis = u × w の並びで渡す
            rotation = Disc.Do (xAxisDiscHash, rotation, position, rotation * Vector3.right, rotation * Vector3.up, rotation * Vector3.forward, handleSize, snap);
            Handles.color = yAxisColor;
            rotation = Disc.Do (yAxisDiscHash, rotation, position, rotation * Vector3.up, rotation * Vector3.forward, rotation * Vector3.right, handleSize, snap);
            Handles.color = zAxisColor;
            rotation = Disc.Do (zAxisDiscHash, rotation, position, rotation * Vector3.forward, rotation * Vector3.right, rotation * Vector3.up, handleSize, snap);
            Handles.color = centerColor;
            // 画面まわりの回転は丸ごとの輪。3 軸の弧の端と重ならないように一回り大きく出す
            rotation = Handles.Disc (rotation, position, Camera.current.transform.forward, handleSize * kCameraDiscScale, false, 0f);
            Handles.color = color;
            return rotation;
        }

        public static Vector3 PositionHandle (Vector3 position, Quaternion rotation)
        {
            if (locked) return position;
            float handleSize = HandleUtility.GetHandleSize (position);
            Color color = Handles.color;
            //float snap = -1;
            Handles.color = xAxisColor;
            position = AxisSlider.Do (xAxisSliderHash, position, rotation * Vector3.right, handleSize);
            Handles.color = yAxisColor;
            position = AxisSlider.Do (yAxisSliderHash, position, rotation * Vector3.up, handleSize);
            Handles.color = zAxisColor;
            position = AxisSlider.Do (zAxisSliderHash, position, rotation * Vector3.forward, handleSize);
            Handles.color = color;
            return position;
        }

        public static bool JointHandle (int controlID, Vector3 position, float size, out Vector3 outPosition)
        {
            Event ev = Event.current;
            outPosition = position;
            if (locked) {
                if (ev.type == EventType.Repaint) DrawJoint (position, size);
                return false;
            }

            Ray mouseRay;
            HitInfo hitInfo;
            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if (HandleUtility.nearestControl == controlID && ev.button == 0) {
                        mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        if (RaycastHitToJoint (mouseRay, position, size, out hitInfo)) {
                            GUIUtility.hotControl = controlID;

                            _hangLocalPosition = hitInfo.point - position;
                            //hangPoint_.z = 0;
                            // ボーンの向きが表側、裏側どちらに向いているか判断する。		
                            _isBackSphere = Vector3.Dot (mouseRay.direction, hitInfo.point - position) >= 0 ? true : false;
                            _radius = (hitInfo.point - position).magnitude;
                            _isBoneSelected = true;
                            ev.Use ();

                            //Undo.RegisterUndo (t.target, "Move Joint");
                            return true;
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        // 離したらつかみを放す（放さないと他のコントロールにマウスが届かない）
                        GUIUtility.hotControl = 0;
                        _isBoneSelected = false;
                        ev.Use ();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID) {
                        if (ev.button == 0 && _isBoneSelected == true) {
                            mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                            if (RaycastJointForDrag (mouseRay, position + _hangLocalPosition, Camera.current.transform.forward, out hitInfo)) {
                                outPosition = hitInfo.point - _hangLocalPosition;
                                ev.Use ();
                                GUI.changed = true;
                                return true;
                            }
                        }
                    }
                    break;

                case EventType.Repaint:
                    if (HandleUtility.nearestControl == controlID) {
                        edgeColor = Color.yellow;
                    }

                    DrawJoint (position, size);
                    break;

                case EventType.Layout:
                    mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                    if (RaycastHitToJoint (mouseRay, position, size, out hitInfo)) {
                        HandleUtility.AddControl (controlID, HandleUtility.DistanceToCircle (hitInfo.point, size));
                    }
                    break;
            }

            return false;
        }

        /// <summary>リングの芯の線から何ピクセルまでをリングの上とみなすか</summary>
        const float kRingMouseOver = 10f;
        /// <summary>輪の分割数</summary>
        const int kRingSegments = 48;
        /// <summary>断面（管）の分割数</summary>
        const int kRingTubeSegments = 8;

        /// <summary>
        /// 骨の先に出すリング（IK リング）。3D の輪で、rotation の +Z が輪の軸（輪の面は +X と +Y が張る面）。
        /// つかんで動かす物ではなく、左ボタンで押した（Pressed）・右ボタンで押した（ContextClicked）を返すだけ
        /// </summary>
        public static CapsuleResult RingHandle (int controlID, Vector3 center, Quaternion rotation, float radius, float tube, Color ringColor)
        {
            Event ev = Event.current;
            if (locked) {
                if (ev.type == EventType.Repaint) DrawRing (center, rotation, radius, tube, ringColor);
                return CapsuleResult.None;
            }

            if (PoseHandleUtility.IsContextClick (ev, controlID)) {
                ev.Use ();
                return CapsuleResult.ContextClicked;
            }

            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if (HandleUtility.nearestControl != controlID) break;
                    if (ev.button == 0) {
                        // 離すまでつかんでおく（他のハンドルや視点の操作へ流さない）
                        GUIUtility.hotControl = controlID;
                        ev.Use ();
                        return CapsuleResult.Pressed;
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        GUIUtility.hotControl = 0;
                        ev.Use ();
                    }
                    break;

                case EventType.Repaint:
                    DrawRing (center, rotation, radius, tube, ringColor);
                    break;

                case EventType.Layout: {
                    // 拾うのは輪の芯の近くだけ。中は空けておく（そこには目標のジョイントとギズモがある）
                    float distance = HandleUtility.DistanceToPolyLine (GetRingCenterLine (center, rotation, radius));
                    if (distance < kRingMouseOver) HandleUtility.AddControl (controlID, distance);
                    break;
                }
            }

            return CapsuleResult.None;
        }

        /// <summary>輪の芯の線。Layout はイベントごとに来るので使い回す</summary>
        static readonly Vector3[] ringCenterLine_ = new Vector3[kRingSegments + 1];

        /// <summary>
        /// 輪の芯（管の中心を通る円）を線分で。マウスとの距離を測るのに使う
        /// </summary>
        static Vector3[] GetRingCenterLine (Vector3 center, Quaternion rotation, float radius)
        {
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            for (int i = 0; i <= kRingSegments; i++) {
                float angle = i * 2 * Mathf.PI / kRingSegments;
                ringCenterLine_[i] = center + (right * Mathf.Cos (angle) + up * Mathf.Sin (angle)) * radius;
            }
            return ringCenterLine_;
        }

        /// <summary>
        /// 3D の輪（トーラス）。法線で陰を付けて立体に見せ、視点に背を向けた面は描かない
        /// （材質が深度を見ないので、描くと裏側が手前に重なる）
        /// </summary>
        internal static void DrawRing (Vector3 center, Quaternion rotation, float radius, float tube, Color ringColor)
        {
            Camera camera = Camera.current;
            Vector3 view = camera != null ? camera.transform.forward : Vector3.forward;
            // 光は視点の少し左上から当てる（向きを変えても陰の出かたが変わらないように、カメラに付けて持つ）
            Vector3 light = camera != null ? camera.transform.rotation * new Vector3 (-0.4f, 0.6f, -1f).normalized : -view;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 axis = rotation * Vector3.forward;
            ringColor = Fade (ringColor);

            Lilium.GLDraw.lineMaterial.SetPass (0);
            GL.Begin (GL.TRIANGLES);
            for (int i = 0; i < kRingSegments; i++) {
                for (int j = 0; j < kRingTubeSegments; j++) {
                    Vector3 n00 = RingNormal (right, up, axis, i, j);
                    Vector3 n10 = RingNormal (right, up, axis, i + 1, j);
                    Vector3 n11 = RingNormal (right, up, axis, i + 1, j + 1);
                    Vector3 n01 = RingNormal (right, up, axis, i, j + 1);
                    // 裏を向いた面は飛ばす
                    if (Vector3.Dot (n00 + n10 + n11 + n01, view) > 0) continue;

                    Vector3 v00 = RingVertex (center, right, up, radius, tube, n00, i);
                    Vector3 v10 = RingVertex (center, right, up, radius, tube, n10, i + 1);
                    Vector3 v11 = RingVertex (center, right, up, radius, tube, n11, i + 1);
                    Vector3 v01 = RingVertex (center, right, up, radius, tube, n01, i);

                    EmitRingVertex (ringColor, light, n00, v00);
                    EmitRingVertex (ringColor, light, n10, v10);
                    EmitRingVertex (ringColor, light, n11, v11);
                    EmitRingVertex (ringColor, light, n00, v00);
                    EmitRingVertex (ringColor, light, n11, v11);
                    EmitRingVertex (ringColor, light, n01, v01);
                }
            }
            GL.End ();
        }

        /// <summary>輪の i 番目・断面の j 番目の法線</summary>
        static Vector3 RingNormal (Vector3 right, Vector3 up, Vector3 axis, int i, int j)
        {
            float ringAngle = i * 2 * Mathf.PI / kRingSegments;
            float tubeAngle = j * 2 * Mathf.PI / kRingTubeSegments;
            Vector3 outward = right * Mathf.Cos (ringAngle) + up * Mathf.Sin (ringAngle);
            return outward * Mathf.Cos (tubeAngle) + axis * Mathf.Sin (tubeAngle);
        }

        static Vector3 RingVertex (Vector3 center, Vector3 right, Vector3 up, float radius, float tube, Vector3 normal, int i)
        {
            float ringAngle = i * 2 * Mathf.PI / kRingSegments;
            Vector3 outward = right * Mathf.Cos (ringAngle) + up * Mathf.Sin (ringAngle);
            return center + outward * radius + normal * tube;
        }

        /// <summary>法線で明るさを変えて 1 頂点を流す（立体に見せる陰）</summary>
        static void EmitRingVertex (Color ringColor, Vector3 light, Vector3 normal, Vector3 vertex)
        {
            float lambert = 0.45f + 0.55f * Mathf.Max (0, Vector3.Dot (normal, light));
            GL.Color (new Color (ringColor.r * lambert, ringColor.g * lambert, ringColor.b * lambert, ringColor.a));
            GL.Vertex (vertex);
        }

        /// <summary>カプセルのハンドルの操作の結果</summary>
        public enum CapsuleResult
        {
            None,
            /// <summary>左ボタンで押した（選ぶ）</summary>
            Pressed,
            /// <summary>つかんで動かした（delta に動き）</summary>
            Dragged,
            /// <summary>右ボタンで押した（IK / FK の切り替え）</summary>
            ContextClicked,
        }

        /// <summary>カプセルの端の半円の分割数</summary>
        const int kCapsuleArcSegments = 12;
        static readonly Vector3[] capsuleCenterLine_ = new Vector3[kCapsuleArcSegments * 2 + 3];
        static Vector3 _capsuleHang = Vector3.zero;
        static Vector3 _capsulePlaneNormal = Vector3.up;

        /// <summary>
        /// 足元に寝かせたカプセル（足の転がしのコントローラー）。踵から爪先までの長さ・足の幅で、面は足元の面（normal）。
        /// 押す（選ぶ）・つかんで足元の面の上を動かす（真横から見ているときは画面の面）・右ボタンで切り替え、を返す。
        /// 形の中を押しても拾う
        /// </summary>
        /// <param name="heel">踵（カプセルの後ろの端）</param>
        /// <param name="toe">爪先（前の端）</param>
        /// <param name="radius">足の幅の半分</param>
        public static CapsuleResult CapsuleHandle (int controlID, Vector3 heel, Vector3 toe, Vector3 normal, float radius, float tube, Color capsuleColor, out Vector3 delta)
        {
            Event ev = Event.current;
            delta = Vector3.zero;
            Vector3[] line = GetCapsuleCenterLine (heel, toe, normal, radius);
            if (locked) {
                if (ev.type == EventType.Repaint) DrawTube (line, normal, tube, capsuleColor);
                return CapsuleResult.None;
            }

            if (PoseHandleUtility.IsContextClick (ev, controlID)) {
                ev.Use ();
                return CapsuleResult.ContextClicked;
            }

            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if (HandleUtility.nearestControl != controlID) break;
                    if (ev.button == 0) {
                        Ray ray = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        _capsulePlaneNormal = DragPlaneNormal (normal);
                        Vector3 center = (heel + toe) * 0.5f;
                        HitInfo hit;
                        _capsuleHang = RaycastJointForDrag (ray, center, _capsulePlaneNormal, out hit) ? hit.point - center : Vector3.zero;
                        GUIUtility.hotControl = controlID;
                        ev.Use ();
                        return CapsuleResult.Pressed;
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        GUIUtility.hotControl = 0;
                        ev.Use ();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        Ray ray = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        Vector3 center = (heel + toe) * 0.5f;
                        HitInfo hit;
                        if (RaycastJointForDrag (ray, center + _capsuleHang, _capsulePlaneNormal, out hit)) {
                            delta = hit.point - (center + _capsuleHang);
                            ev.Use ();
                            GUI.changed = true;
                            return CapsuleResult.Dragged;
                        }
                    }
                    break;

                case EventType.Repaint:
                    DrawTube (line, normal, tube, capsuleColor);
                    break;

                case EventType.Layout: {
                    float distance = HandleUtility.DistanceToPolyLine (line);
                    // 形の中（足元の面の上でカプセルの内側）も拾う
                    Ray ray = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                    HitInfo hit;
                    if (RaycastJointForDrag (ray, heel, normal, out hit) && DistanceToSegment (hit.point, heel + (toe - heel).normalized * radius, toe - (toe - heel).normalized * radius) <= radius) {
                        distance = 0;
                    }
                    if (distance < kRingMouseOver) HandleUtility.AddControl (controlID, distance);
                    break;
                }
            }
            return CapsuleResult.None;
        }

        /// <summary>つかんで動かす面。足元の面が真横を向いて見えるとき（足元を水平に見ている）は画面の面</summary>
        static Vector3 DragPlaneNormal (Vector3 normal)
        {
            Camera camera = Camera.current;
            if (camera == null) return normal;
            Vector3 view = camera.transform.forward;
            return Mathf.Abs (Vector3.Dot (view, normal.normalized)) < 0.25f ? view : normal;
        }

        static float DistanceToSegment (Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01 (Vector3.Dot (p - a, ab) / ab.sqrMagnitude) : 0;
            return Vector3.Distance (p, a + ab * t);
        }

        /// <summary>
        /// カプセルの縁（管の芯）。踵側の半円 → 横の辺 → 爪先側の半円 → 横の辺 と一周し、始点に戻る
        /// </summary>
        static Vector3[] GetCapsuleCenterLine (Vector3 heel, Vector3 toe, Vector3 normal, float radius)
        {
            Vector3 forward = Vector3.ProjectOnPlane (toe - heel, normal);
            float length = forward.magnitude;
            forward = length > 1e-6f ? forward / length : Vector3.forward;
            radius = Mathf.Min (radius, length * 0.5f);
            Vector3 right = Vector3.Cross (normal.normalized, forward).normalized;
            Vector3 back = heel + forward * radius;
            Vector3 front = heel + forward * (length - radius);
            int n = 0;
            // 爪先側の半円（右 → 前 → 左）
            for (int i = 0; i <= kCapsuleArcSegments; i++) {
                float angle = Mathf.PI * i / kCapsuleArcSegments;
                capsuleCenterLine_[n++] = front + (right * Mathf.Cos (angle) + forward * Mathf.Sin (angle)) * radius;
            }
            // 踵側の半円（左 → 後ろ → 右）
            for (int i = 0; i <= kCapsuleArcSegments; i++) {
                float angle = Mathf.PI + Mathf.PI * i / kCapsuleArcSegments;
                capsuleCenterLine_[n++] = back + (right * Mathf.Cos (angle) + forward * Mathf.Sin (angle)) * radius;
            }
            capsuleCenterLine_[n] = capsuleCenterLine_[0];
            return capsuleCenterLine_;
        }

        /// <summary>
        /// 閉じた線に沿った管（リングと同じ陰の付け方。視点に背を向けた面は描かない）。line の最後は最初と同じ点
        /// </summary>
        static void DrawTube (Vector3[] line, Vector3 normal, float tube, Color tubeColor)
        {
            Camera camera = Camera.current;
            Vector3 view = camera != null ? camera.transform.forward : Vector3.forward;
            Vector3 light = camera != null ? camera.transform.rotation * new Vector3 (-0.4f, 0.6f, -1f).normalized : -view;
            normal = normal.normalized;
            tubeColor = Fade (tubeColor);
            int count = line.Length - 1;

            Lilium.GLDraw.lineMaterial.SetPass (0);
            GL.Begin (GL.TRIANGLES);
            for (int i = 0; i < count; i++) {
                Vector3 outward0 = TubeOutward (line, normal, i, count);
                Vector3 outward1 = TubeOutward (line, normal, i + 1, count);
                for (int j = 0; j < kRingTubeSegments; j++) {
                    float a0 = j * 2 * Mathf.PI / kRingTubeSegments;
                    float a1 = (j + 1) * 2 * Mathf.PI / kRingTubeSegments;
                    Vector3 n00 = outward0 * Mathf.Cos (a0) + normal * Mathf.Sin (a0);
                    Vector3 n10 = outward1 * Mathf.Cos (a0) + normal * Mathf.Sin (a0);
                    Vector3 n11 = outward1 * Mathf.Cos (a1) + normal * Mathf.Sin (a1);
                    Vector3 n01 = outward0 * Mathf.Cos (a1) + normal * Mathf.Sin (a1);
                    if (Vector3.Dot (n00 + n10 + n11 + n01, view) > 0) continue;

                    Vector3 p0 = line[i];
                    Vector3 p1 = line[(i + 1) % count];
                    EmitRingVertex (tubeColor, light, n00, p0 + n00 * tube);
                    EmitRingVertex (tubeColor, light, n10, p1 + n10 * tube);
                    EmitRingVertex (tubeColor, light, n11, p1 + n11 * tube);
                    EmitRingVertex (tubeColor, light, n00, p0 + n00 * tube);
                    EmitRingVertex (tubeColor, light, n11, p1 + n11 * tube);
                    EmitRingVertex (tubeColor, light, n01, p0 + n01 * tube);
                }
            }
            GL.End ();
        }

        /// <summary>線の i 番目の点で、面の中で線から外へ向く向き（前後の点から求める）</summary>
        static Vector3 TubeOutward (Vector3[] line, Vector3 normal, int i, int count)
        {
            Vector3 tangent = line[(i + 1) % count] - line[(i - 1 + count) % count];
            Vector3 outward = Vector3.Cross (normal, tangent);
            return outward.sqrMagnitude > 1e-12f ? outward.normalized : Vector3.zero;
        }

        public static Color color = new Color (0, 1, 0, 0.2f);
        public static Color edgeColor = new Color (0, 1, 0, 1);

        internal static void DrawJoint (Vector3 basePoint, float size)
        {
            Handles.color = Fade (color);
            Handles.DrawSolidDisc (basePoint, Camera.current.transform.forward, size);
            Handles.color = Fade (edgeColor * new Color (1f, 1f, 1f, 0f) + new Color (0f, 0f, 0f, 1f));
            Handles.DrawWireDisc (basePoint, Camera.current.transform.forward, size);
        }

        static Vector3 _hangLocalPosition = Vector3.zero;
        static float _radius = 0;
        static bool _isBackSphere = false;
        static bool _isBoneSelected = false;

        /// <summary>骨の太さ（半径）の上限。長さに対する割合（指のような短い骨が太くなりすぎないように）</summary>
        const float kMaxBoneRadiusPerLength = 0.2f;

        /// <param name="size">骨の太さ（半径）。長さの kMaxBoneRadiusPerLength 倍までに抑える</param>
        public static bool BoneHandle (int controlID, Vector3 position, Quaternion rotation, float length, float size, out Quaternion outRotation)
        {
            Event ev = Event.current;
            outRotation = rotation;
            size = Mathf.Min (size, Mathf.Abs (length) * kMaxBoneRadiusPerLength);
            Vector3 endPosition = rotation * Vector3.forward * length + position;
            Vector3 upVector = rotation * Vector3.right;
            Matrix4x4 boneMatrix = Matrix4x4.TRS (position, rotation, Vector3.one);
            if (locked) {
                if (ev.type == EventType.Repaint) DrawBone (position, endPosition, upVector, size);
                return false;
            }

            Ray mouseRay;
            HitInfo hitInfo;
            switch (ev.GetTypeForControl (controlID)) {

                case EventType.MouseDown:
                    if (HandleUtility.nearestControl == controlID && ev.button == 0) {
                        mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                        if (RaycastHitToBone (mouseRay, endPosition, position, size, upVector, out hitInfo)) {
                            GUIUtility.hotControl = controlID;

                            _hangLocalPosition = boneMatrix.inverse.MultiplyPoint (hitInfo.point);

                            // ボーンの向きが表側、裏側どちらに向いているか判断する
                            _isBackSphere = Vector3.Dot (mouseRay.direction, hitInfo.point - position) >= 0 ? true : false;
                            _radius = (hitInfo.point - position).magnitude;
                            _isBoneSelected = true;
                            ev.Use ();
                            return true;
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && ev.button == 0) {
                        // 離したらつかみを放す（放さないと他のコントロールにマウスが届かない）
                        GUIUtility.hotControl = 0;
                        _isBoneSelected = false;
                        ev.Use ();
                    }
                    break;

                // ドラッグによる回転制御
                case EventType.MouseDrag:

                    if (GUIUtility.hotControl == controlID) {
                        if (ev.button == 0 && _isBoneSelected == true) {
                            mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                            if (RaycastSpherePlaneForDrag (mouseRay, position, _radius, mouseRay.direction, _isBackSphere, out hitInfo)) {
                                Vector3 hangWorldPoint = boneMatrix.MultiplyPoint (_hangLocalPosition);
                                outRotation = Quaternion.FromToRotation (hangWorldPoint - position, hitInfo.point - position) * rotation;
                                ev.Use ();
                                GUI.changed = true;
                                return true;
                            }
                        }
                    }
                    break;

                // ホイールによるねじり制御。つかんでいる間か、選んでいる骨（最後につかんだ骨）の上でホイールを回したとき
                case EventType.ScrollWheel:
                    bool isFocusedBone = GUIUtility.hotControl == 0 && PoseHandleUtility.focusControl == controlID && HandleUtility.nearestControl == controlID;
                    if (GUIUtility.hotControl == controlID || isFocusedBone) {
                        float rollAngle = ev.delta.y;
                        outRotation = Quaternion.AngleAxis (rollAngle, rotation * Vector3.forward) * rotation;
                        ev.Use ();
                        GUI.changed = true;
                        return true;
                    }
                    break;

                case EventType.Repaint:
                    if (HandleUtility.nearestControl == controlID) {
                        edgeColor = Color.yellow;
                    }

                    DrawBone (position, endPosition, upVector, size);
                    break;

                case EventType.Layout:
                    mouseRay = HandleUtility.GUIPointToWorldRay (ev.mousePosition);
                    if (RaycastHitToBone (mouseRay, endPosition, position, size, upVector, out hitInfo)) {
                        HandleUtility.AddControl (controlID, HandleUtility.DistanceToCircle (hitInfo.point, 0));
                    }
                    break;
            }

            return false;
        }

        internal static void DrawBone (Vector3 basePoint, Vector3 endPoint, Vector3 upVector, float size)
        {
            Vector3[] boneVertices = GetBoneVertices (basePoint, endPoint, upVector, size);
            Lilium.GLDraw.lineMaterial.SetPass (0);

            GL.Begin (4);
            GL.Color (Fade (color));
            for (int i = 0; i < 3; i++) {
                GL.Vertex (boneVertices[i * 6 + 0]);
                GL.Vertex (boneVertices[i * 6 + 1]);
                GL.Vertex (boneVertices[i * 6 + 2]);
                GL.Vertex (boneVertices[i * 6 + 3]);
                GL.Vertex (boneVertices[i * 6 + 4]);
                GL.Vertex (boneVertices[i * 6 + 5]);
            }
            GL.End ();
            GL.Begin (1);
            GL.Color (Fade (edgeColor * new Color (1f, 1f, 1f, 0f) + new Color (0f, 0f, 0f, 1f)));
            for (int j = 0; j < 3; j++) {
                GL.Vertex (boneVertices[j * 6 + 0]);
                GL.Vertex (boneVertices[j * 6 + 1]);
                GL.Vertex (boneVertices[j * 6 + 1]);
                GL.Vertex (boneVertices[j * 6 + 2]);
                GL.Vertex (boneVertices[j * 6 + 3]);
                GL.Vertex (boneVertices[j * 6 + 4]);
                GL.Vertex (boneVertices[j * 6 + 4]);
                GL.Vertex (boneVertices[j * 6 + 5]);
            }
            GL.End ();
        }

        /// <summary>
        /// 球体と無限平面に対してレイを飛ばし、接点を取得する。
        /// 主にボーンの回転制御で利用する。
        /// </summary>
        /// <returns></returns>
        static bool RaycastSpherePlaneForDrag (Ray ray, Vector3 center, float radius, Vector3 planeNormal, bool isBackSphere, out HitInfo hitInfo)
        {
            hitInfo = HitInfo.unfixed;
            float planeDistance;
            Plane plane = new Plane (planeNormal, center);

            // 球面との衝突判定
            Sphere sphere = new Sphere { center = center, radius = radius };
            if (GeometryMath.RaycastToShpere (ray, sphere, out hitInfo)) {
                // 裏に向いているなら球面の裏側の衝突位置を返す。
                if (isBackSphere) {
                    if (plane.Raycast (ray, out planeDistance)) {
                        float backSphereDistance = (planeDistance - hitInfo.distance) + planeDistance;
                        hitInfo.point = ray.origin + (ray.direction * backSphereDistance);
                    }
                }
                return true;
            }

            // 平面との衝突判定
            if (plane.Raycast (ray, out planeDistance)) {
                hitInfo.point = ray.origin + (ray.direction * planeDistance);
                hitInfo.point = (hitInfo.point - center).normalized * radius + center;
            }
            return true;
        }

        static bool RaycastJointForDrag (Ray ray, Vector3 center, Vector3 planeNormal, out HitInfo hitInfo)
        {
            float planeDistance;
            Plane plane = new Plane (planeNormal, center);

            hitInfo = HitInfo.unfixed;

            // 平面との衝突判定
            if (plane.Raycast (ray, out planeDistance)) {
                hitInfo.point = ray.origin + (ray.direction * planeDistance);
                return true;
            }

            return false;
        }

        public static bool RaycastHitToJoint (Ray ray, Vector3 basePoint, float size, out HitInfo hitInfo)
        {
            return GeometryMath.RaycastToShpere (ray, new Sphere { center = basePoint, radius = size }, out hitInfo);
        }

        public static bool RaycastHitToBone (Ray ray, Vector3 endPoint, Vector3 basePoint, float size, Vector3 upVector, out HitInfo hitInfo)
        {
            Vector3[] boneVertices = GetBoneVertices (basePoint, endPoint, upVector, size);

            for (int i = 0; i < 6; i++) {
                if (GeometryMath.RaycastToPolygon (ray, boneVertices[i * 3 + 0], boneVertices[i * 3 + 1], boneVertices[i * 3 + 2], out hitInfo)) {
                    return true;
                }
            }

            hitInfo = HitInfo.unfixed;
            return false;
        }

        internal static Vector3[] GetBoneVertices (Vector3 basePoint, Vector3 endPoint, Vector3 upVector, float radius)
        {
            Vector3 centerPoint = basePoint + (endPoint - basePoint).normalized * radius;

            Vector3 lhs = Vector3.Normalize (endPoint - basePoint);
            Vector3 vector = Vector3.Cross (lhs, upVector);

            vector.Normalize ();
            Vector3 a = Vector3.Cross (lhs, vector);
            Vector3[] array = new Vector3[18];
            float num = 0;
            for (int i = 0; i < 3; i++) {
                float num2 = Mathf.Cos (num);
                float num3 = Mathf.Sin (num);
                float num4 = Mathf.Cos (num + 2.09439516f);
                float num5 = Mathf.Sin (num + 2.09439516f);
                Vector3 vector2 = centerPoint + vector * (num2 * radius) + a * (num3 * radius);
                Vector3 vector3 = centerPoint + vector * (num4 * radius) + a * (num5 * radius);
                array[i * 6 + 0] = endPoint;
                array[i * 6 + 1] = vector2;
                array[i * 6 + 2] = vector3;
                array[i * 6 + 3] = basePoint;
                array[i * 6 + 4] = vector3;
                array[i * 6 + 5] = vector2;
                num += 2.09439516f;
            }
            return array;
        }



    }

}
