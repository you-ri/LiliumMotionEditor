using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// クリップを書き換える前の Undo の記録。クリップを書き換えるところはすべてこれを通す。
    /// 差分（Undo.RecordObject）ではなく、クリップを丸ごと控える（Undo.RegisterCompleteObjectUndo）。
    /// 差分で控えると、キーを差し込んだカーブは後ろのキーがみなずれた扱いになり、戻すときに Unity がプロパティを 1 つずつ書き戻す。
    /// 全カーブにキーを打った 1 回を戻すのに 1.8 秒（丸ごとなら 50ms）かかり、Ctrl+Z を連打すると数十秒止まった（10-26）
    /// </summary>
    public static class ClipUndo
    {
        static int group_ = -1;
        static readonly HashSet<AnimationClip> recorded_ = new HashSet<AnimationClip> ();
        static bool hooked_;

        /// <summary>
        /// clip を書き換える前に呼ぶ。同じ Undo のまとまりの中で 2 度目からは控えない
        /// （戻すのはまとまりの最初の姿なので要らず、毎回丸ごと写すと、フレームごとに書く流れが重くなる）
        /// </summary>
        public static void Record (AnimationClip clip, string name)
        {
            if (clip == null) return;
            if (!hooked_) {
                hooked_ = true;
                // Undo / Redo の後は、同じ番号のまとまりでも控え直す
                Undo.undoRedoPerformed += () => group_ = -1;
            }
            int group = Undo.GetCurrentGroup ();
            if (group != group_) {
                group_ = group;
                recorded_.Clear ();
            }
            if (!recorded_.Add (clip)) return;
            Undo.RegisterCompleteObjectUndo (clip, name);
        }
    }

}
