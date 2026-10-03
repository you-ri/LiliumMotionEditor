using System.Collections.Generic;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 段が持つクリップの状態。行の状態の色に使う
    /// </summary>
    public enum LayerClipState
    {
        /// <summary>まだ何も無い（クリップが選ばれていない・置き場所が決まっていない）</summary>
        None,
        /// <summary>そのまま使える</summary>
        Ready,
        /// <summary>使えるが古い・気をつけること（焼いた後に元が変わった・余分なカーブ）</summary>
        Stale,
        /// <summary>使えない（編集できない・書き出せない）</summary>
        Blocked,
    }

    public struct LayerClipStatus
    {
        public LayerClipState state;
        public string text;

        public LayerClipStatus (LayerClipState state, string text)
        {
            this.state = state;
            this.text = text;
        }
    }

    /// <summary>
    /// クリップの行に並べるボタン。problem が理由を返すときは押せない
    /// </summary>
    public sealed class LayerClipButton
    {
        public string label;
        public string tooltip;
        public System.Action run;
        /// <summary>押せない理由（押せるなら null）。null の関数は常に押せる</summary>
        public System.Func<string> problem;
        /// <summary>出すかどうか。null の関数は常に出す</summary>
        public System.Func<bool> visible;
    }

    public sealed class LayerClipToggle
    {
        public string label;
        public string tooltip;
        public System.Func<bool> get;
        public System.Action<bool> set;
    }

    /// <summary>
    /// 段が持つクリップ。**どの段にどのクリップが付いているかを、段の行で見せる**ためのもの。
    /// 元（編集するクリップ）は選び直せる。出力（焼いたクリップ）は置き場所が決まっているので見せるだけ。
    /// 重みを持つクリップは、その段を流れる姿勢に合成する（既定の 100% は上書き）
    /// </summary>
    public sealed class LayerClip
    {
        public string label;
        public string tooltip;
        public System.Func<AnimationClip> getClip;
        /// <summary>選び直したとき。null なら選び直せない（出力）</summary>
        public System.Action<AnimationClip> assign;
        public System.Func<LayerClipStatus> getStatus;
        public readonly List<LayerClipButton> buttons = new List<LayerClipButton> ();
        public readonly List<LayerClipToggle> toggles = new List<LayerClipToggle> ();
        /// <summary>合成の重み。null なら重みを持たない（合成しないクリップ）</summary>
        public System.Func<float> getWeight;
        public System.Action<float> setWeight;

        public bool hasWeight
        {
            get { return getWeight != null && setWeight != null; }
        }

        public AnimationClip clip
        {
            get { return getClip != null ? getClip () : null; }
        }

        public bool canAssign
        {
            get { return assign != null; }
        }

        public LayerClipStatus status
        {
            get { return getStatus != null ? getStatus () : new LayerClipStatus (LayerClipState.None, null); }
        }
    }

    static class LayerClipText
    {
        public static string Blending (float weight)
        {
            return weight >= 1
                ? "このクリップで上書き中（カーブのある骨。上の段の編集はそこに出ない）"
                : "このクリップを " + Mathf.RoundToInt (weight * 100) + "% で合成中";
        }
    }

}
