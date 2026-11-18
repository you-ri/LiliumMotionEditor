using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    public sealed class AxisSlider
    {
        private static float kDistanceMouseOver = 3.0f;
        // つかんだときの軸（ワールド）。動かしている間は、渡された位置と向きでなく、この線の上でマウスを測る。
        // 動かすと軸の向き（点の形・物の向き）や点の場所が解き直しで変わる物がある。今の位置と向きの線で測ると、
        // 前の軸で控えたつかんだ所とずれて、ずれた分がドラッグのたびに足されて点が暴れた
        // （11-18 ユーザー報告「持ち物の Center をギズモの軸で動かすと暴れる。丸をつかむと問題ない」）
        private static Vector3 start_;
        private static Vector3 direction_;
        // つかんだ所の、軸の根元からの距離
        private static float hang_;
        // 前のドラッグまでに動かした距離（軸の上）
        private static float moved_;

        /// <summary>
        /// 軸の矢印。動かしたら、前の描画からマウスが軸の上を動いた分だけ position を動かして返す
        /// （呼んだ側は返した位置と position の差を動かした量として使う。解き直しで点が置いた所からずれても、差はマウスの分だけ）
        /// </summary>
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

                        start_ = position;
                        direction_ = direction.normalized;
                        hang_ = Vector3.Dot (projectPoint - position, direction_);
                        moved_ = 0;
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
                        // マウスの光線にいちばん近い、つかんだときの軸の上の点へ（光線と軸がほぼ平行なら動かさない）
                        Vector3 projectPoint;
                        if (GeometryMath.GetClosestPointToRay (mouseRay, start_, start_ + direction_ * size, out projectPoint)) {
                            float moved = Vector3.Dot (projectPoint - start_, direction_) - hang_;
                            position += direction_ * (moved - moved_);
                            moved_ = moved;
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
