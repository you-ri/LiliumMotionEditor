namespace Lilium
{

#if !UNITY_6000_3_OR_NEWER
    /// <summary>
    /// 古い版（Unity 6.0）に無い API の代わり。新しい版では本物が優先されるので使われない
    /// </summary>
    static class UnityCompat
    {
        /// <summary>
        /// Object.GetEntityId の代わり。ハンドルのコントロール ID の種にしか使わないので、オブジェクトごとに違えばよい
        /// </summary>
        public static int GetEntityId (this UnityEngine.Object obj)
        {
            return obj.GetInstanceID ();
        }
    }
#endif

}
