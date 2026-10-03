using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// 編集用クリップの見分け方
    /// </summary>
    public class EditingClipTests
    {
        AnimationClip clip_;

        [SetUp]
        public void SetUp ()
        {
            clip_ = new AnimationClip { frameRate = 60 };
        }

        [TearDown]
        public void TearDown ()
        {
            Object.DestroyImmediate (clip_);
        }

        static void AddCurve (AnimationClip clip, string path)
        {
            AnimationUtility.SetEditorCurve (clip, EditorCurveBinding.FloatCurve (path, typeof (Transform), "m_LocalPosition.x"), AnimationCurve.Constant (0, 1, 0));
        }

        [Test]
        public void EmptyAndRigOnlyClipsAreEditable ()
        {
            Assert.IsNull (EditingClip.GetProblem (clip_));
            AddCurve (clip_, "Controls/FK/Hips");
            AddCurve (clip_, "Controls/IK/LeftArm/Target");
            Assert.IsNull (EditingClip.GetProblem (clip_));
            Assert.IsNull (EditingClip.GetProblem (null));
        }

        [Test]
        public void ClipWithOnlyBoneCurvesIsReadOnly ()
        {
            AddCurve (clip_, "root/hip");
            AddCurve (clip_, "ControlsExtra");

            string problem = EditingClip.GetProblem (clip_);
            Assert.IsNotNull (problem);
            StringAssert.Contains ("2 本", problem);
        }

        /// <summary>
        /// 旧形式で作りかけて、新しい形式でキーを打ったクリップ。編集は止めず、残ったカーブを数えて消せる
        /// </summary>
        [Test]
        public void MixedClipIsEditableAndStrayCurvesCanBeRemoved ()
        {
            AddCurve (clip_, "Controls/FK/Hips");
            AddCurve (clip_, "IK Goal hand_L");
            AddCurve (clip_, "root/hip");

            Assert.IsNull (EditingClip.GetProblem (clip_));
            Assert.AreEqual (2, EditingClip.CountStrayCurves (clip_));

            Assert.AreEqual (2, EditingClip.RemoveStrayCurves (clip_));
            Assert.AreEqual (0, EditingClip.CountStrayCurves (clip_));
            Assert.AreEqual (1, AnimationUtility.GetCurveBindings (clip_).Length);
            Assert.AreEqual ("Controls/FK/Hips", AnimationUtility.GetCurveBindings (clip_)[0].path);
        }

        [Test]
        public void RigBindingIsDecidedByPathPrefix ()
        {
            Assert.IsTrue (EditingClip.IsRigBinding (EditorCurveBinding.FloatCurve ("Controls/FK/Hips", typeof (Transform), "m_LocalRotation.x")));
            Assert.IsTrue (EditingClip.IsRigBinding (EditorCurveBinding.FloatCurve ("Controls", typeof (Transform), "m_LocalRotation.x")));
            Assert.IsFalse (EditingClip.IsRigBinding (EditorCurveBinding.FloatCurve ("ControlsX/FK", typeof (Transform), "m_LocalRotation.x")));
            Assert.IsFalse (EditingClip.IsRigBinding (EditorCurveBinding.FloatCurve ("", typeof (Animator), "RootT.x")));
        }

        [Test]
        public void DefaultFileNameHasRigSuffix ()
        {
            Assert.AreEqual ("Attack.rig", EditingClip.DefaultFileName ("Attack"));
            Assert.AreEqual ("New Motion.rig", EditingClip.DefaultFileName (null));
            Assert.IsFalse (EditingClip.HasEditingName (clip_), "アセットでないクリップには名前の規約が無い");
        }
    }

}
