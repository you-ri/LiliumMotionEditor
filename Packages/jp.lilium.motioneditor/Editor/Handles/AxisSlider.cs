using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;

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
                        // 軸をまっすぐ見ていて決まらないときは、つかんだ所を軸の根元とする
                        Vector3 projectPoint;
                        GeometryMath.GetClosestPointToRay (mouseRay, position, endPosition, out projectPoint);
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
                        // マウスの光線にいちばん近い軸の上の点へ（光線と軸がほぼ平行なら動かさない）
                        Vector3 projectPoint;
                        if (GeometryMath.GetClosestPointToRay (mouseRay, position, endPosition, out projectPoint)) {
                            position = projectPoint - hangPoint_;
                            GUI.changed = true;
                        }
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
    }

}
