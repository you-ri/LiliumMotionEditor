using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

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
                            // ボーンの向きが表側、裏側どちらに向いているか判断する。		
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
                    if (HandleUtility.nearestControl == controlID || hoverControl == controlID) {
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

        public static Color color = new Color (0, 1, 0, 0.2f);
        public static Color edgeColor = new Color (0, 1, 0, 1);
        /// <summary>マウスが乗ったのと同じに描くハンドル（メッシュでマウスの下にある物。0 なら無し）。呼ぶ側がハンドルごとに入れる</summary>
        public static int hoverControl;

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
                    if (HandleUtility.nearestControl == controlID || hoverControl == controlID) {
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
