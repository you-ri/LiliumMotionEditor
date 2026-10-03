using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 骨ハンドルとトラックが参照する編集状態（ハンドルの大きさ・今のフレーム・選択）。
    /// プレビュー窓（PreviewWindow）が実装し、
    /// ハンドルやトラックを描く前に EditorHost.current へ入れる
    /// </summary>
    public interface IEditorHost
    {
        float handleSize { get; }
        float currentFrame { get; }
        bool IsSelected (GameObject go);
        void Select (GameObject go);

        /// <summary>
        /// ハンドルで骨を動かす前の Undo の記録。プレビュー窓はプレビューシーンの物を記録しない
        /// （作り直せば消える物で、元に戻すのはクリップの側。記録すると開いているシーンまで変更扱いになることがある）
        /// </summary>
        void RecordUndo (Object obj, string name);
    }

    public static class EditorHost
    {
        static IEditorHost current_;

        /// <summary>
        /// 今ハンドル・トラックを描いている窓。どの窓も入れていなければ Unity の選択で動く既定のもの
        /// </summary>
        public static IEditorHost current
        {
            get { return current_ ?? SelectionHost.instance; }
            set { current_ = value; }
        }

        /// <summary>
        /// 窓を閉じるときに呼ぶ。自分が current のときだけ外す
        /// </summary>
        public static void Release (IEditorHost host)
        {
            if (current_ == host) current_ = null;
        }

        sealed class SelectionHost : IEditorHost
        {
            public static readonly SelectionHost instance = new SelectionHost ();

            public float handleSize { get { return 0.1f; } }
            public float currentFrame { get { return 0; } }

            public bool IsSelected (GameObject go)
            {
                return Selection.Contains (go);
            }

            public void Select (GameObject go)
            {
                Selection.activeGameObject = go;
            }

            public void RecordUndo (Object obj, string name)
            {
                Undo.RecordObject (obj, name);
            }
        }
    }

}
