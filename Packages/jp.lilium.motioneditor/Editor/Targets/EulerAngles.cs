using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 回転をオイラー角で見せるときの、表し方の選び方。同じ回転には 2 通りの角度の組と ±360° の違いがあるので、
    /// 前に見せていた角度に一番近いものを選ぶ（±180° をまたいでも数字が飛ばない）
    /// </summary>
    public static class EulerAngles
    {
        public static Vector3 Closest (Quaternion rotation, Vector3 hint)
        {
            Vector3 a = rotation.eulerAngles;
            // Unity の順（Z → X → Y）で同じ回転になる、もう 1 つの組
            Vector3 b = new Vector3 (180 - a.x, a.y + 180, a.z + 180);
            a = Near (a, hint);
            b = Near (b, hint);
            return (a - hint).sqrMagnitude <= (b - hint).sqrMagnitude ? a : b;
        }

        static Vector3 Near (Vector3 euler, Vector3 hint)
        {
            for (int i = 0; i < 3; i++) euler[i] = hint[i] + Mathf.DeltaAngle (hint[i], euler[i]);
            return euler;
        }
    }

}
