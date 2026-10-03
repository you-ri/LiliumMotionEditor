using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// プレビュー窓のカメラ。注視点のまわりを回る。窓に保存して、再読み込み後も視点を保つ
    /// </summary>
    [System.Serializable]
    public class OrbitCamera
    {
        /// <summary>
        /// ドラッグ 1 ピクセルあたりの回転（度）
        /// </summary>
        const float kOrbitSpeed = 0.3f;
        const float kZoomBase = 1.05f;
        const float kMaxPitch = 89.9f;

        public Vector3 pivot = new Vector3 (0, 1, 0);
        public float yaw = 180;
        public float pitch = 8;
        public float distance = 4;
        public float fieldOfView = 30;

        public Quaternion rotation
        {
            get { return Quaternion.Euler (pitch, yaw, 0); }
        }

        public Vector3 position
        {
            get { return pivot - rotation * Vector3.forward * distance; }
        }

        public void Apply (Camera camera)
        {
            camera.transform.SetPositionAndRotation (position, rotation);
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = Mathf.Max (0.01f, distance * 0.02f);
            camera.farClipPlane = distance * 20 + 50;
        }

        public void Orbit (Vector2 delta)
        {
            yaw += delta.x * kOrbitSpeed;
            pitch = Mathf.Clamp (pitch + delta.y * kOrbitSpeed, -kMaxPitch, kMaxPitch);
        }

        /// <param name="viewHeight">表示域の高さ（ピクセル）。注視点をマウスと同じだけ動かすのに使う</param>
        public void Pan (Vector2 delta, float viewHeight)
        {
            float unitsPerPixel = 2 * distance * Mathf.Tan (fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max (1, viewHeight);
            pivot += rotation * new Vector3 (-delta.x, delta.y, 0) * unitsPerPixel;
        }

        /// <param name="amount">正で遠ざかる</param>
        public void Zoom (float amount)
        {
            distance = Mathf.Clamp (distance * Mathf.Pow (kZoomBase, amount), 0.05f, 500);
        }

        /// <summary>
        /// direction の向きに見る。注視点と距離はそのまま
        /// </summary>
        public void LookAlong (Vector3 direction)
        {
            if (direction.sqrMagnitude < 1e-8f) return;
            Vector3 euler = Quaternion.LookRotation (direction).eulerAngles;
            yaw = euler.y;
            pitch = Mathf.Clamp (Mathf.DeltaAngle (0, euler.x), -kMaxPitch, kMaxPitch);
        }

        public void LookDown ()
        {
            pitch = kMaxPitch;
        }

        /// <summary>
        /// 半径 radius の範囲がちょうど画面に収まる距離にする
        /// </summary>
        /// <param name="aspect">表示域の横 / 縦。縦長の窓では横がはみ出さないよう、その分だけ引く</param>
        public void Frame (Vector3 center, float radius, float aspect = 1)
        {
            pivot = center;
            float half = fieldOfView * 0.5f * Mathf.Deg2Rad;
            float vertical = Mathf.Max (radius, 0.01f) / Mathf.Sin (half);
            // 画角は縦が基準。横の画角はアスペクトで決まる
            float horizontalHalf = Mathf.Atan (Mathf.Tan (half) * Mathf.Max (0.01f, aspect));
            float horizontal = Mathf.Max (radius, 0.01f) / Mathf.Sin (horizontalHalf);
            distance = Mathf.Max (vertical, horizontal);
        }

        public void Frame (Bounds bounds, float aspect = 1)
        {
            Frame (bounds.center, bounds.extents.magnitude, aspect);
        }
    }

}
