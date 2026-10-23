using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// キーから次のキーまで（区間）の補間を曲線・リニア・一定から選ぶ（S80）
    /// </summary>
    public class KeyInterpolationTests
    {
        const float kRate = 60;

        readonly List<Object> created_ = new List<Object> ();
        readonly List<System.IDisposable> disposables_ = new List<System.IDisposable> ();

        [TearDown]
        public void TearDown ()
        {
            for (int i = disposables_.Count - 1; i >= 0; i--) disposables_[i].Dispose ();
            disposables_.Clear ();
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
            TimelineClipboard.Clear ();
        }

        AnimationClip NewClip ()
        {
            AnimationClip clip = new AnimationClip { frameRate = kRate };
            created_.Add (clip);
            return clip;
        }

        static readonly EditorCurveBinding kHipsX = CurveEdit.TransformBinding (RigPaths.kRoot + "/Body/Hips", CurveEdit.kPosition, 0);
        static readonly EditorCurveBinding kHipsY = CurveEdit.TransformBinding (RigPaths.kRoot + "/Body/Hips", CurveEdit.kPosition, 1);

        static float Value (AnimationClip clip, EditorCurveBinding binding, float frame)
        {
            return AnimationUtility.GetEditorCurve (clip, binding).Evaluate (frame / kRate);
        }

        static Keyframe KeyAt (AnimationClip clip, EditorCurveBinding binding, int frame)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            return curve[ClipKeyUtility.FindKeyAtFrame (curve, kRate, frame)];
        }

        /// <summary>同じキーの並びで全部 ClampedAuto のカーブの、frame のキーの傾き（曲線の側の傾きの基準）</summary>
        static float AutoSlopeOf (AnimationClip clip, EditorCurveBinding binding, int frame)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
            AnimationCurve auto = new AnimationCurve ();
            foreach (Keyframe key in curve.keys) auto.AddKey (new Keyframe (key.time, key.value));
            // 傾きは両隣で決まるので、キーを全部足してから自動にする
            for (int i = 0; i < auto.length; i++) {
                AnimationUtility.SetKeyLeftTangentMode (auto, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode (auto, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            return auto[ClipKeyUtility.FindKeyAtFrame (auto, kRate, frame)].outTangent;
        }

        /// <summary>0・30・60・90F に 0・1・3・4 のキー（単調に増えるので、ClampedAuto の傾きが 0 にならない）</summary>
        AnimationClip FourKeys ()
        {
            AnimationClip clip = NewClip ();
            float[] values = { 0, 1, 3, 4 };
            for (int i = 0; i < values.Length; i++) {
                CurveEdit.SetKey (clip, kHipsX, i * 30, values[i]);
                CurveEdit.SetKey (clip, kHipsY, i * 30, values[i] * 0.5f);
            }
            return clip;
        }

        [Test]
        public void LinearSegment_IsStraight_AndTheCurveSideStaysSmooth ()
        {
            AnimationClip clip = FourKeys ();
            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 30), "既定は曲線");
            Assert.AreEqual (2, KeyInterpolations.SetSegment (clip, 30, KeyInterpolation.Linear));

            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 30));
            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 0), "隣の区間は曲線のまま");
            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 60));
            Assert.AreEqual (2, Value (clip, kHipsX, 45), 1e-4f, "区間の中は直線");
            Assert.AreEqual (1.5f, Value (clip, kHipsX, 37.5f), 1e-4f);
            Assert.AreEqual (1, Value (clip, kHipsY, 45), 1e-4f, "区間の全部のカーブが直線");
            Assert.AreEqual (AutoSlopeOf (clip, kHipsX, 30), KeyAt (clip, kHipsX, 30).inTangent, 1e-4f, "境目のキーの曲線の側は ClampedAuto と同じ傾き");
            Assert.AreEqual (AutoSlopeOf (clip, kHipsX, 60), KeyAt (clip, kHipsX, 60).outTangent, 1e-4f);

            // 境目のキーを打ち直しても、曲線の側が自動に戻ってリニアが消えることはない（傾きは付け直す）
            CurveEdit.SetKey (clip, kHipsX, 30, 1.4f);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 30), "打ち直した後もリニア");
            Assert.AreEqual ((1.4f + 3) / 2, Value (clip, kHipsX, 45), 1e-4f);
            Assert.AreEqual (AutoSlopeOf (clip, kHipsX, 30), KeyAt (clip, kHipsX, 30).inTangent, 1e-4f, "曲線の側の傾きを新しい値で付け直す");
            // ほかのキーを動かしても
            ClipKeyUtility.MoveKeysAtFrame (clip, 90, 100, false);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 30));
            Assert.AreEqual (AutoSlopeOf (clip, kHipsX, 60), KeyAt (clip, kHipsX, 60).outTangent, 1e-4f, "次の区間が延びたら傾きも付け直す");
        }

        [Test]
        public void KeysAddedMovedRemovedOrPasted_InsideALinearSegment_KeepItLinear ()
        {
            AnimationClip clip = NewClip ();
            CurveEdit.SetKey (clip, kHipsX, 0, 0);
            CurveEdit.SetKey (clip, kHipsX, 60, 6);
            KeyInterpolations.SetSegment (clip, 0, KeyInterpolation.Linear);

            CurveEdit.SetKey (clip, kHipsX, 30, 5);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 0), "足したキーは区間の補間を受け継ぐ");
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 30));
            Assert.AreEqual (2.5f, Value (clip, kHipsX, 15), 1e-4f);
            Assert.AreEqual (5.5f, Value (clip, kHipsX, 45), 1e-4f);

            ClipKeyUtility.MoveKeysAtFrame (clip, 30, 20, false);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 0), "動かした後も");
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 20));
            Assert.AreEqual (2.5f, Value (clip, kHipsX, 10), 1e-4f);

            TimelineClipboard.Copy (clip, new List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> { new KeyValuePair<int, System.Predicate<EditorCurveBinding>> (20, null) });
            TimelineClipboard.Paste (clip, 40);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 20), "貼った後も");
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 40));
            Assert.AreEqual (5.5f, Value (clip, kHipsX, 50), 1e-4f);
            // 境目のキーに貼っても（曲線の区間から写したキーでも）、行き先の補間のまま
            CurveEdit.SetKey (clip, kHipsX, 90, 0);
            TimelineClipboard.Copy (clip, new List<KeyValuePair<int, System.Predicate<EditorCurveBinding>>> { new KeyValuePair<int, System.Predicate<EditorCurveBinding>> (90, null) });
            TimelineClipboard.Paste (clip, 40);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 20), "境目に貼った後も");
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 40));

            ClipKeyUtility.RemoveKeysAtFrame (clip, 20);
            ClipKeyUtility.RemoveKeysAtFrame (clip, 40);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 0), "消した後も");
            Assert.AreEqual (3, Value (clip, kHipsX, 30), 1e-4f);
        }

        [Test]
        public void ConstantSegment_HoldsUntilTheNextKey ()
        {
            AnimationClip clip = FourKeys ();
            KeyInterpolations.SetSegment (clip, 30, KeyInterpolation.Constant);
            Assert.AreEqual (KeyInterpolation.Constant, KeyInterpolations.GetSegment (clip, 30));
            Assert.AreEqual (1, Value (clip, kHipsX, 31), 1e-4f);
            Assert.AreEqual (1, Value (clip, kHipsX, 59), 1e-4f, "次のキーの直前まで前のキーの値");
            Assert.AreEqual (3, Value (clip, kHipsX, 60), 1e-4f);
            KeyInterpolations.SetSegment (clip, 30, KeyInterpolation.Curve);
            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 30), "曲線に戻せる");
            Assert.Greater (Value (clip, kHipsX, 45), 1.5f);
        }

        [Test]
        public void LinearRotation_TurnsAtAnEvenSpeed ()
        {
            AnimationClip clip = NewClip ();
            string spine = RigPaths.kRoot + "/Body/Spine";
            Quaternion end = Quaternion.Euler (10, 60, 0);
            CurveEdit.SetRotationKey (clip, spine, 0, Quaternion.identity);
            CurveEdit.SetRotationKey (clip, spine, 30, end);
            CurveEdit.SetRotationKey (clip, spine, 60, Quaternion.Euler (0, 90, 30));
            Assert.AreEqual (4, KeyInterpolations.SetSegment (clip, 0, KeyInterpolation.Linear), "四元数の 4 本");
            float total = Quaternion.Angle (Quaternion.identity, end);
            foreach (int frame in new[] { 6, 15, 24 }) {
                float angle = Quaternion.Angle (Quaternion.identity, CurveEdit.EvaluateRotation (clip, spine, frame / kRate));
                Assert.AreEqual (total * frame / 30, angle, 0.5f, frame + "F の回りが等速");
            }
        }

        [Test]
        public void SegmentEnds_WithoutKeys_GetKeysAtTheirValues ()
        {
            // 手は 0・60F、体は 0・30・60F。30F から 60F をリニアにすると、手にも 30F のキーが入り、区間が本当にリニアになる
            AnimationClip clip = NewClip ();
            EditorCurveBinding hand = CurveEdit.TransformBinding (RigPaths.kRoot + "/Body/LeftHand", CurveEdit.kPosition, 0);
            CurveEdit.SetKey (clip, hand, 0, 0);
            CurveEdit.SetKey (clip, hand, 60, 2);
            foreach (int frame in new[] { 0, 30, 60 }) CurveEdit.SetKey (clip, kHipsX, frame, frame / 30f);
            float before = Value (clip, hand, 30);
            KeyInterpolations.SetSegment (clip, 30, KeyInterpolation.Linear);
            Assert.GreaterOrEqual (ClipKeyUtility.FindKeyAtFrame (AnimationUtility.GetEditorCurve (clip, hand), kRate, 30), 0, "区間の端にキーを足す");
            Assert.AreEqual (before, KeyAt (clip, hand, 30).value, 1e-4f, "足したキーはその時刻の値");
            Assert.AreEqual ((before + 2) / 2, Value (clip, hand, 45), 1e-4f);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 30));
        }

        [Test]
        public void PointStatesAndExcludedCurves_AreNotChanged ()
        {
            AnimationClip clip = FourKeys ();
            // 点の状態（段差）
            EditorCurveBinding locked = EditorCurveBinding.FloatCurve (RigPaths.kRoot + "/Points/LeftHeel", typeof (BodyPoint), BodyPoint.kLockedProperty);
            AnimationCurve state = new AnimationCurve (new Keyframe (0, 1), new Keyframe (30 / kRate, 1), new Keyframe (60 / kRate, 0));
            for (int i = 0; i < state.length; i++) {
                AnimationUtility.SetKeyLeftTangentMode (state, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode (state, i, AnimationUtility.TangentMode.Constant);
            }
            AnimationUtility.SetEditorCurve (clip, locked, state);
            // 持ち替えの重み（呼ぶ側が外す）
            EditorCurveBinding weight = EditorCurveBinding.FloatCurve (RigPaths.Props ("Hand"), typeof (Transform), "m_Weight");
            AnimationUtility.SetEditorCurve (clip, weight, state);
            System.Predicate<EditorCurveBinding> exclude = binding => binding.path.StartsWith (RigPaths.kProps);

            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 30, exclude), "段差のカーブは区間の補間に数えない（混在にならない）");
            foreach (KeyInterpolation mode in new[] { KeyInterpolation.Linear, KeyInterpolation.Curve }) {
                KeyInterpolations.SetSegment (clip, 30, mode, exclude);
                Assert.AreEqual (mode, KeyInterpolations.GetSegment (clip, 30, exclude));
                foreach (EditorCurveBinding binding in new[] { locked, weight }) {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, binding);
                    for (int i = 0; i < curve.length; i++) {
                        Assert.AreEqual (AnimationUtility.TangentMode.Constant, AnimationUtility.GetKeyRightTangentMode (curve, i), binding.propertyName + " は段差のまま");
                    }
                    Assert.AreEqual (1, curve.Evaluate (59 / kRate), 1e-4f);
                }
            }
        }

        [Test]
        public void CycleClip_LastSegmentLinear_KeepsTheEndsMatching ()
        {
            AnimationClip clip = NewClip ();
            CurveEdit.SetKey (clip, kHipsX, 0, 0);
            CurveEdit.SetKey (clip, kHipsX, 20, 1);
            CurveEdit.SetKey (clip, kHipsX, 40, 3);
            CurveEdit.SetKey (clip, kHipsX, 60, 5);
            ClipCycle.Sync (clip);
            KeyInterpolations.SetSegment (clip, 40, KeyInterpolation.Linear);
            ClipCycle.Sync (clip);

            Keyframe first = KeyAt (clip, kHipsX, 0);
            Keyframe last = KeyAt (clip, kHipsX, 60);
            Assert.AreEqual (first.value, last.value, 1e-5f, "最後のキーは先頭の値");
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.GetSegment (clip, 40), "最後の区間はリニアのまま");
            Assert.AreEqual (KeyInterpolation.Curve, KeyInterpolations.GetSegment (clip, 0), "最初の区間は曲線のまま");
            // つなぎ目は「最後のキーの左（最後の区間）→ 先頭のキーの右（最初の区間）」。先頭のキーの左の傾きは前のキーが無いので比べない
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip, kHipsX);
            Assert.AreEqual (KeyInterpolation.Linear, KeyInterpolations.Side (curve, 0, false), "先頭のキーの左も最後の区間の補間");
            Assert.AreEqual (KeyInterpolations.Side (curve, 0, true), KeyInterpolations.Side (curve, curve.length - 1, true), "先頭と最後の右の補間が合う");
            Assert.AreEqual (first.outTangent, last.outTangent, 1e-4f, "先頭と最後の右の傾きが合う");
            Assert.IsFalse (ClipCycle.Sync (clip), "合わせ直しても変わらない");
        }

        [Test]
        public void PoseSession_SetInterpolation_IsKeptByNewKeys_AndUndone ()
        {
            EditRigDefinition definition = EditRigDefinition.CreateDefault ();
            created_.Add (definition);
            GameObject prefab = new GameObject ("Prefab");
            created_.Add (prefab);
            Animator display = TestSkeleton.CreateHumanoid (created_, true);
            display.transform.SetParent (prefab.transform, false);
            PoseSession session = new PoseSession (prefab, definition);
            disposables_.Add (session);
            session.clip = NewClip ();
            foreach (int frame in new[] { 0, 30 }) {
                session.Evaluate (frame);
                Assert.IsNull (session.CommitPose ());
            }
            Assert.IsNotNull (session.SetInterpolation (30, KeyInterpolation.Linear), "最後のキーには区間が無い");

            Undo.IncrementCurrentGroup ();
            Assert.IsNull (session.SetInterpolation (0, KeyInterpolation.Linear));
            Assert.AreEqual (KeyInterpolation.Linear, session.GetInterpolation (0));
            session.Evaluate (15);
            Assert.IsNull (session.CommitPose ());
            Assert.AreEqual (KeyInterpolation.Linear, session.GetInterpolation (0), "間に打ったキーは区間の補間を受け継ぐ");
            Assert.AreEqual (KeyInterpolation.Linear, session.GetInterpolation (15));
            Assert.AreEqual (2, session.GetInterpolations ().Count);

            Undo.PerformUndo ();
            Undo.PerformUndo ();
            Assert.AreEqual (KeyInterpolation.Curve, session.GetInterpolation (0), "Undo で曲線に戻る");
        }
    }

}
