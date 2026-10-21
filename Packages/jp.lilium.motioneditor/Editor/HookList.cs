using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// パッケージの外（ゲーム側）が足す受け口の並び（ContextHooks・HumanoidBakeHooks・PoseGraphHooks など）。
    /// 同じものは 1 回だけ足す。受け口が例外を出してもほかの受け口は呼ぶ（ゲーム側の不具合で窓が止まらないように）
    /// </summary>
    sealed class HookList<T> where T : class
    {
        readonly List<T> items_ = new List<T> ();

        public IReadOnlyList<T> items
        {
            get { return items_; }
        }

        public void Register (T hook)
        {
            if (hook != null && !items_.Contains (hook)) items_.Add (hook);
        }

        public void Unregister (T hook)
        {
            items_.Remove (hook);
        }

        /// <summary>それぞれの受け口で action を呼ぶ（例外はログに出して続ける）</summary>
        public void ForEach (System.Action<T> action)
        {
            foreach (T hook in items_) {
                try {
                    action (hook);
                }
                catch (System.Exception exception) {
                    Debug.LogException (exception);
                }
            }
        }
    }

}
