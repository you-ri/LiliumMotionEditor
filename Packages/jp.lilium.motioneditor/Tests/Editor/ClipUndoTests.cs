using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// クリップの Undo の記録（ClipUndo）。丸ごと控えて、キーを差し込んだ後も Undo / Redo で元どおりに戻る
    /// </summary>
    public class ClipUndoTests : RigTestFixture
    {
        const int kCurves = 40;

        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = 60 };
            created_.Add (clip_);
            for (int i = 0; i < kCurves; i++) {
                AnimationCurve curve = new AnimationCurve ();
                for (int k = 0; k < 5; k++) curve.AddKey (k * 10 / 60f, i + k);
                AnimationUtility.SetEditorCurve (clip_, Binding (i), curve);
            }
        }

        [TearDown]
        public void TearDown ()
        {
            Undo.ClearUndo (clip_);
        }

        static EditorCurveBinding Binding (int i)
        {
            return EditorCurveBinding.FloatCurve ("Bone" + i, typeof (Transform), "m_LocalPosition.x");
        }

        /// <summary>全カーブの先頭近くにキーを差し込む（差分で控えると、後ろのキーがみなずれた扱いになる書き方）</summary>
        void InsertKeys (float value, int frame = 1)
        {
            for (int i = 0; i < kCurves; i++) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, Binding (i));
                curve.AddKey (frame / 60f, value);
                AnimationUtility.SetEditorCurve (clip_, Binding (i), curve);
            }
        }

        [Test]
        public void Undo_RestoresAllCurves_AndRedoBringsTheKeysBack ()
        {
            Undo.IncrementCurrentGroup ();
            ClipUndo.Record (clip_, "Insert Keys");
            InsertKeys (100);
            Assert.AreEqual (6, AnimationUtility.GetEditorCurve (clip_, Binding (0)).length);

            Undo.PerformUndo ();
            for (int i = 0; i < kCurves; i++) {
                AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, Binding (i));
                Assert.AreEqual (5, curve.length, "差し込んだキーが消える");
                Assert.AreEqual (i + 1, curve.keys[1].value, 1e-6f, "後ろのキーは元の値");
            }

            Undo.PerformRedo ();
            Assert.AreEqual (6, AnimationUtility.GetEditorCurve (clip_, Binding (kCurves - 1)).length, "Redo で差し込んだキーが戻る");
            Assert.AreEqual (100, AnimationUtility.GetEditorCurve (clip_, Binding (kCurves - 1)).keys[1].value, 1e-6f);
        }

        /// <summary>同じまとまりの中で何度記録しても、Undo 1 回でまとまりの前の姿へ戻る（2 度目からは控えない）</summary>
        [Test]
        public void RecordTwiceInOneGroup_UndoesToTheStateBeforeTheGroup ()
        {
            Undo.IncrementCurrentGroup ();
            ClipUndo.Record (clip_, "Insert Keys");
            InsertKeys (100);
            ClipUndo.Record (clip_, "Insert Keys");
            InsertKeys (200, 2);
            Assert.AreEqual (7, AnimationUtility.GetEditorCurve (clip_, Binding (0)).length);

            Undo.PerformUndo ();
            Assert.AreEqual (5, AnimationUtility.GetEditorCurve (clip_, Binding (0)).length, "まとまりの前に戻る");
        }

        /// <summary>Undo の後は、同じ番号のまとまりでも記録し直す（記録を飛ばすと、その書き換えが Undo で戻らない）</summary>
        [Test]
        public void RecordAfterUndo_IsRecordedAgain ()
        {
            Undo.IncrementCurrentGroup ();
            ClipUndo.Record (clip_, "Insert Keys");
            InsertKeys (100);
            Undo.PerformUndo ();

            ClipUndo.Record (clip_, "Insert Keys");
            InsertKeys (300);
            Undo.PerformUndo ();
            Assert.AreEqual (5, AnimationUtility.GetEditorCurve (clip_, Binding (0)).length, "Undo の後の書き換えも戻る");
        }
    }

}
