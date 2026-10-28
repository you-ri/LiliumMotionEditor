using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// パッケージの外（サンプルや利用側）が、クリップの .meta に自分の項目を名前を付けて持つ口（Kimodo サンプルの動きの説明とシードなど）。
    /// .meta の userData はパッケージの印（ClipUserData）と同じ JSON なので、直接書くとパッケージが保存するときに消える。ここを通せば残る
    /// </summary>
    public static class ClipExtraData
    {
        /// <summary>name の項目を読む。無ければ（アセットでないクリップも）null</summary>
        public static T Load<T> (AnimationClip clip, string name) where T : class
        {
            ClipUserData.Extra extra = ClipUserData.Load (clip).FindExtra (name);
            if (extra == null || string.IsNullOrEmpty (extra.json)) return null;
            try {
                return JsonUtility.FromJson<T> (extra.json);
            }
            catch (System.ArgumentException) {
                return null;
            }
        }

        /// <summary>name の項目を書く（value が null なら消す）。アセットでないクリップには保存しない（false）</summary>
        public static bool Save<T> (AnimationClip clip, string name, T value) where T : class
        {
            string json = value != null ? JsonUtility.ToJson (value) : null;
            return ClipUserData.Save (clip, data => data.SetExtra (name, json));
        }
    }

}
