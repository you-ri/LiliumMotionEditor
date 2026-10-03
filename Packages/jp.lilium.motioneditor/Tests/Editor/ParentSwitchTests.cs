using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 物の持ち替え（ParentSwitch）のキー打ち。拘束の代わりに BoxCollider の中心（x / y / z）を 3 つの親の重みに見立てる
    /// （テストのアセンブリに部品を置けないのと、Animation Rigging に依らずに確かめるため）。
    /// 物は持ち手ノードの子で、握り（受け皿）のローカルと同じ値を持つ（ゲームは受け皿から物へ写す）
    /// </summary>
    public class ParentSwitchTests
    {
        const int kFrame = 10;

        GameObject root_;
        Transform parentA_;
        Transform parentB_;
        Transform hold_;
        Transform obj_;
        Transform grip_;
        AnimationClip clip_;
        ParentSwitchDefinition definition_;

        [SetUp]
        public void SetUp ()
        {
            root_ = new GameObject ("Model");
            root_.AddComponent<Animator> ();
            parentA_ = Child ("A", root_.transform, new Vector3 (1, 2, 0), Quaternion.Euler (0, 30, 10));
            parentB_ = Child ("B", root_.transform, new Vector3 (-1, 1.5f, 0.5f), Quaternion.Euler (20, -60, 0));
            Transform free = Child ("Free", root_.transform, Vector3.zero, Quaternion.identity);
            // 持ち手ノードは A に付いている（重み A = 1）
            hold_ = Child ("Hold", root_.transform, parentA_.position, parentA_.rotation);
            Vector3 gripPosition = new Vector3 (0.1f, -0.2f, 0.3f);
            Quaternion gripRotation = Quaternion.Euler (5, 15, 25);
            obj_ = Child ("Object", hold_, Vector3.zero, Quaternion.identity);
            obj_.SetLocalPositionAndRotation (gripPosition, gripRotation);
            grip_ = Child ("Grip", root_.transform, Vector3.zero, Quaternion.identity);
            grip_.SetLocalPositionAndRotation (gripPosition, gripRotation);

            GameObject constraint = new GameObject ("Constraint");
            constraint.transform.SetParent (root_.transform, false);
            constraint.AddComponent<BoxCollider> ().center = new Vector3 (1, 0, 0);

            definition_ = new ParentSwitchDefinition {
                name = "Weapon",
                constraintPath = "Constraint",
                constraintType = "BoxCollider",
                objectPath = "Hold/Object",
                gripPath = "Grip",
            };
            definition_.sources.Add (new ParentSource { label = "A", weightProperty = "m_Center.x", parentPath = "A" });
            definition_.sources.Add (new ParentSource { label = "B", weightProperty = "m_Center.y", parentPath = "B" });
            definition_.sources.Add (new ParentSource { label = "Free", weightProperty = "m_Center.z", parentPath = "Free" });

            clip_ = new AnimationClip { frameRate = 60 };
        }

        [TearDown]
        public void TearDown ()
        {
            Object.DestroyImmediate (root_);
            Object.DestroyImmediate (clip_);
        }

        static Transform Child (string name, Transform parent, Vector3 position, Quaternion rotation)
        {
            Transform t = new GameObject (name).transform;
            t.SetParent (parent, false);
            t.SetPositionAndRotation (position, rotation);
            return t;
        }

        ParentSwitch.Bound Bind ()
        {
            string error;
            ParentSwitch.Bound bound = ParentSwitch.Bind (root_, definition_, out error);
            Assert.IsNotNull (bound, error);
            return bound;
        }

        [Test]
        public void BindsAndReadsTheCurrentParent ()
        {
            ParentSwitch.Bound bound = Bind ();
            Assert.AreEqual (0, ParentSwitch.Current (root_, bound), "重みがいちばん重い親");

            definition_.constraintType = "Missing";
            string error;
            Assert.IsNull (ParentSwitch.Bind (root_, definition_, out error));
            StringAssert.Contains ("拘束", error);
        }

        /// <summary>
        /// 切り替えのフレームで物のワールド姿勢が変わらない（新しい親 × 握り = 元のワールド姿勢）
        /// </summary>
        [Test]
        public void SwitchingKeepsTheObjectInPlace ()
        {
            ParentSwitch.Bound bound = Bind ();
            Vector3 worldPosition = obj_.position;
            Quaternion worldRotation = obj_.rotation;

            Assert.IsNull (ParentSwitch.Switch (clip_, root_, bound, kFrame, 1));

            float time = kFrame / clip_.frameRate;
            Vector3 grip = new Vector3 (Value ("m_LocalPosition.x", time), Value ("m_LocalPosition.y", time), Value ("m_LocalPosition.z", time));
            Quaternion gripRotation = CurveEdit.EvaluateRotation (clip_, RigPaths.Props ("Grip"), time);
            Vector3 position = parentB_.position + parentB_.rotation * grip;
            Quaternion rotation = parentB_.rotation * gripRotation;
            Assert.Less (Vector3.Distance (worldPosition, position), 0.001f, "切り替えで物が跳ぶ");
            Assert.Less (Quaternion.Angle (worldRotation, rotation), 0.05f, "切り替えで物が回る");
        }

        /// <summary>
        /// 1 つ前のフレームは元の持ち手・握りのままで、そこから切り替えのフレームまで値を保つ（段差で切り替わる）
        /// </summary>
        [Test]
        public void SwitchIsAStep ()
        {
            ParentSwitch.Bound bound = Bind ();
            Vector3 oldGrip = grip_.localPosition;
            ParentSwitch.Switch (clip_, root_, bound, kFrame, 1);

            float rate = clip_.frameRate;
            float before = (kFrame - 1) / rate;
            float between = (kFrame - 0.5f) / rate;
            float at = kFrame / rate;

            Assert.AreEqual (1f, Weight ("m_Center.x", before), 1e-5f, "1 つ前は元の親");
            Assert.AreEqual (1f, Weight ("m_Center.x", between), 1e-5f, "間も元の親（段差）");
            Assert.AreEqual (0f, Weight ("m_Center.y", between), 1e-5f);
            Assert.AreEqual (0f, Weight ("m_Center.x", at), 1e-5f, "切り替えのフレームで新しい親");
            Assert.AreEqual (1f, Weight ("m_Center.y", at), 1e-5f);
            Assert.AreEqual (0f, Weight ("m_Center.z", at), 1e-5f);

            Assert.AreEqual (oldGrip.x, Value ("m_LocalPosition.x", before), 1e-5f, "1 つ前は元の握り");
            Assert.AreEqual (oldGrip.x, Value ("m_LocalPosition.x", between), 1e-5f, "間も元の握り（段差）");
        }

        /// <summary>
        /// 打つのは切り替えのフレームと 1 つ前だけ。後ろのフレームの握りのキーは触らない
        /// （値がそのまま新しい親の基準になり、物は新しい親に付いて動く）
        /// </summary>
        [Test]
        public void LaterFramesAreLeftAlone ()
        {
            ParentSwitch.Bound bound = Bind ();
            EditorCurveBinding gripX = EditorCurveBinding.FloatCurve (RigPaths.Props ("Grip"), typeof (Transform), "m_LocalPosition.x");
            CurveEdit.SetKey (clip_, gripX, kFrame + 5, 0.4f);
            ParentSwitch.Switch (clip_, root_, bound, kFrame, 1);

            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, gripX);
            List<int> frames = new List<int> ();
            foreach (Keyframe key in curve.keys) frames.Add (Mathf.RoundToInt (key.time * clip_.frameRate));
            CollectionAssert.AreEqual (new[] { kFrame - 1, kFrame, kFrame + 5 }, frames);
            Assert.AreEqual (0.4f, curve.Evaluate ((kFrame + 5) / clip_.frameRate), 1e-5f, "後ろのキーの値はそのまま");
        }

        float Weight (string property, float time)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, EditorCurveBinding.FloatCurve (RigPaths.Props ("Constraint"), typeof (BoxCollider), property));
            Assert.IsNotNull (curve, property);
            return curve.Evaluate (time);
        }

        float Value (string property, float time)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve (clip_, EditorCurveBinding.FloatCurve (RigPaths.Props ("Grip"), typeof (Transform), property));
            Assert.IsNotNull (curve, property);
            return curve.Evaluate (time);
        }
    }

}
