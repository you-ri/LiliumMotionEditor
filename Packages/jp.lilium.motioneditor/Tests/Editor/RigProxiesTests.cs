using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// ゲームの Rig の値の代理（Controls/Game/...）。Animation Rigging に頼らないよう、Rig の中身の情報（RigLayerInfo）はテストで組む
    /// </summary>
    public class RigProxiesTests
    {
        readonly List<Object> created_ = new List<Object> ();
        EditRigDefinition definition_;
        EditingRig rig_;
        Animator display_;
        Transform rigTransform_;
        Transform source_;
        Transform handIk_;
        Transform handTarget_;
        Transform weaponTarget_;
        readonly Dictionary<string, float> applied_ = new Dictionary<string, float> ();

        [SetUp]
        public void SetUp ()
        {
            definition_ = EditRigDefinition.CreateDefault ();
            created_.Add (definition_);
            display_ = TestSkeleton.CreateHumanoid (created_, true, 1.5f);

            Transform rigs = Child (display_.transform, "Rigs", Vector3.zero);
            rigTransform_ = Child (rigs, "Upper Rig", new Vector3 (0, 0.1f, 0));
            source_ = Child (rigTransform_, "Source", new Vector3 (0.1f, 1.2f, 0.3f));
            source_.localRotation = Quaternion.Euler (10, 20, 30);
            handIk_ = Child (rigTransform_, "Hand IK", Vector3.zero);
            handTarget_ = Child (handIk_, "Target", new Vector3 (-0.4f, 1.0f, 0.2f));
            weaponTarget_ = Child (display_.GetBoneTransform (HumanBodyBones.LeftHand), "weapon_target", new Vector3 (0, 0, 0.1f));
            applied_.Clear ();
        }

        [TearDown]
        public void TearDown ()
        {
            if (rig_ != null) rig_.Dispose ();
            rig_ = null;
            foreach (Object o in created_) {
                if (o != null) Object.DestroyImmediate (o);
            }
            created_.Clear ();
        }

        static Transform Child (Transform parent, string name, Vector3 position)
        {
            Transform t = new GameObject (name).transform;
            t.SetParent (parent, false);
            t.localPosition = position;
            return t;
        }

        RigConstraintInfo Constraint (string label, Transform transform, float weight, params Transform[] sources)
        {
            RigConstraintInfo info = new RigConstraintInfo {
                label = label,
                transform = transform,
                weight = weight,
                weightType = typeof (MonoBehaviour),
                weightProperty = "m_Weight",
                setWeight = value => applied_[label] = value,
            };
            info.sources.AddRange (sources);
            return info;
        }

        RigLayerInfo Layer (string label, Transform transform, bool active, float weight, params RigConstraintInfo[] constraints)
        {
            RigLayerInfo info = new RigLayerInfo {
                label = label,
                active = active,
                weight = weight,
                transform = transform,
                weightType = typeof (Behaviour),
                weightProperty = "m_Weight",
                setWeight = value => applied_[label] = value,
            };
            info.constraints.AddRange (constraints);
            return info;
        }

        /// <summary>
        /// ゲームの組み方に似せる: Rig と同じ GameObject の拘束（上半身の傾き）と、子の拘束（手の IK。ターゲットの 1 つは武器＝骨の下）。
        /// 無効の層と、同じ名前の層も混ぜる
        /// </summary>
        RigProxies CreateProxies ()
        {
            Transform foot = Child (display_.transform, "Foot Rig", Vector3.zero);
            List<RigLayerInfo> layers = new List<RigLayerInfo> {
                Layer ("Upper Rig", rigTransform_, true, 0.8f,
                    Constraint ("Override", rigTransform_, 1, source_),
                    Constraint ("TwoBoneIK", handIk_, 0.5f, handTarget_, weaponTarget_, source_)),
                Layer ("Foot Rig", foot, false, 1, Constraint ("FootIK", foot, 1)),
                Layer ("Upper Rig", rigTransform_, true, 1),
            };
            rig_ = EditingRig.Create (definition_, display_, name => new GameObject (name), layers);
            return rig_.rigProxies;
        }

        [Test]
        public void CreatesProxiesAtRigRelativePaths ()
        {
            RigProxies proxies = CreateProxies ();

            Assert.AreEqual (1, proxies.rigs.Count, "有効で名前の重ならない層だけ");
            Transform rigProxy = rig_.FindControl ("Controls/Game/Upper Rig");
            Assert.IsNotNull (rigProxy);
            Assert.AreEqual (0.8f, rigProxy.GetComponent<RigLayerProxy> ().weight, 1e-6f);
            Assert.AreEqual (1, rigProxy.GetComponent<RigConstraintProxy> ().weight, 1e-6f, "Rig と同じ GameObject の拘束は同じ所に並ぶ");
            Assert.AreEqual (0.5f, rig_.FindControl ("Controls/Game/Upper Rig/Hand IK").GetComponent<RigConstraintProxy> ().weight, 1e-6f);
            Assert.IsNull (rig_.FindControl ("Controls/Game/Foot Rig"), "無効の層は持たない");

            CollectionAssert.AreEqual (new[] { "Upper Rig/Source", "Upper Rig/Hand IK/Target" }, proxies.sources.Select (s => s.label).ToArray (), "骨の下のターゲットと重複は持たない");
            Assert.That (proxies.notes.Any (n => n.Contains ("weapon_target")), "骨の下のターゲットは理由を残す");
            Assert.That (proxies.notes.Any (n => n.Contains ("同じ名前")));

            Transform sourceProxy = rig_.FindControl ("Controls/Game/Upper Rig/Source");
            float scale = rig_.humanScale;
            Assert.That (Mathf.Abs (scale - 1), Is.GreaterThan (1e-3f), "テストの体は humanScale が 1 でない");
            Assert.That (Vector3.Distance (source_.localPosition / scale, sourceProxy.localPosition), Is.LessThan (1e-5f));
            Assert.That (Quaternion.Angle (source_.localRotation, sourceProxy.localRotation), Is.LessThan (1e-3f));
        }

        [Test]
        public void ChannelsMapProxiesToGamePaths ()
        {
            RigProxies proxies = CreateProxies ();

            RigProxies.Channel layerWeight = proxies.channels.Single (c => c.proxyType == typeof (RigLayerProxy));
            Assert.AreEqual ("Controls/Game/Upper Rig", layerWeight.proxyPath);
            Assert.AreEqual ("Rigs/Upper Rig", layerWeight.gamePath);
            Assert.AreEqual (typeof (Behaviour), layerWeight.gameType);
            Assert.AreEqual ("m_Weight", layerWeight.gameProperty);

            RigProxies.Channel ikWeight = proxies.channels.Single (c => c.gamePath == "Rigs/Upper Rig/Hand IK" && c.kind == RigProxies.ChannelKind.Weight);
            Assert.AreEqual ("Controls/Game/Upper Rig/Hand IK", ikWeight.proxyPath);
            Assert.AreEqual (typeof (RigConstraintProxy), ikWeight.proxyType);

            RigProxies.Channel position = proxies.channels.Single (c => c.gamePath == "Rigs/Upper Rig/Source" && c.kind == RigProxies.ChannelKind.Position);
            Assert.AreEqual ("Controls/Game/Upper Rig/Source", position.proxyPath);
            Assert.AreEqual (rig_.humanScale, position.scale, 1e-6f);
            RigProxies.Channel rotation = proxies.channels.Single (c => c.gamePath == "Rigs/Upper Rig/Source" && c.kind == RigProxies.ChannelKind.Rotation);
            Assert.AreEqual (1, rotation.scale);
        }

        [Test]
        public void ApplyWritesTargetsToEditingBodyAndWeightsToDisplay ()
        {
            RigProxies proxies = CreateProxies ();
            RigProxies.Source source = proxies.sources[0];
            source.proxy.localPosition = new Vector3 (0.2f, 0.5f, -0.1f);
            source.proxy.localRotation = Quaternion.Euler (0, 45, 0);
            proxies.rigs[0].weight.value = 0.25f;
            proxies.rigs[0].constraints[1].value = 1.5f;

            proxies.Apply ();

            Assert.That (Vector3.Distance (source.editing.localPosition, new Vector3 (0.2f, 0.5f, -0.1f) * rig_.humanScale), Is.LessThan (1e-5f));
            Assert.That (Quaternion.Angle (source.editing.localRotation, Quaternion.Euler (0, 45, 0)), Is.LessThan (1e-3f));
            Assert.AreEqual ("Rigs/Upper Rig/Source", AnimationUtility.CalculateTransformPath (source.editing, rig_.root.transform), "複製はゲームと同じパス");
            Assert.AreEqual (0.25f, applied_["Upper Rig"], 1e-6f);
            Assert.AreEqual (1, applied_["TwoBoneIK"], 1e-6f, "重みは 0〜1 に丸める");
            Assert.AreEqual (1, applied_["Override"], 1e-6f);
        }

        [Test]
        public void MovingTheCloneAndCapturingKeepsTheWorldPose ()
        {
            RigProxies proxies = CreateProxies ();
            RigProxies.Source source = proxies.sources[1];
            Vector3 world = rig_.root.transform.TransformPoint (new Vector3 (0.3f, 0.9f, 0.4f));
            Quaternion rotation = Quaternion.Euler (30, -60, 15);

            source.editing.SetPositionAndRotation (world, rotation);
            proxies.Capture (source);
            rig_.ResetBonesToRest ();
            proxies.Apply ();

            Assert.That (Vector3.Distance (source.editing.position, world), Is.LessThan (1e-4f));
            Assert.That (Quaternion.Angle (source.editing.rotation, rotation), Is.LessThan (1e-2f));
        }

        [Test]
        public void ResetReturnsToPrefabValues ()
        {
            RigProxies proxies = CreateProxies ();
            RigProxies.Source source = proxies.sources[0];
            Vector3 position = source.proxy.localPosition;
            source.proxy.localPosition += Vector3.one;
            proxies.rigs[0].weight.value = 0;
            proxies.rigs[0].constraints[1].value = 0;

            proxies.ResetToDefaults ();

            Assert.That (Vector3.Distance (source.proxy.localPosition, position), Is.LessThan (1e-6f));
            Assert.AreEqual (0.8f, proxies.rigs[0].weight.value, 1e-6f);
            Assert.AreEqual (0.5f, proxies.rigs[0].constraints[1].value, 1e-6f);
        }

        /// <summary>
        /// 重み（float）とターゲット（Transform）をキーに打ち、クリップを当て直すと戻る
        /// </summary>
        [Test]
        public void KeysRoundTripThroughClip ()
        {
            RigProxies proxies = CreateProxies ();
            AnimationClip clip = new AnimationClip { frameRate = 60 };
            created_.Add (clip);

            RigProxies.Source source = proxies.sources[0];
            source.proxy.localPosition = new Vector3 (0.1f, 0.7f, 0.2f);
            source.proxy.localRotation = Quaternion.Euler (5, 15, 25);
            proxies.rigs[0].weight.value = 0.3f;
            proxies.rigs[0].constraints[1].value = 0.6f;
            using (CurveWriter writer = CurveWriter.Begin (clip, rig_.root.transform, 0, "Test")) {
                proxies.WriteWeightKeys (writer);
                writer.Transform (source.proxy, TransformChannels.Position | TransformChannels.Rotation);
            }

            string[] paths = AnimationUtility.GetCurveBindings (clip).Select (b => b.path + ":" + b.type.Name + "." + b.propertyName).Distinct ().ToArray ();
            CollectionAssert.Contains (paths, "Controls/Game/Upper Rig:RigLayerProxy.weight");
            CollectionAssert.Contains (paths, "Controls/Game/Upper Rig:RigConstraintProxy.weight");
            CollectionAssert.Contains (paths, "Controls/Game/Upper Rig/Hand IK:RigConstraintProxy.weight");
            Assert.That (paths.All (p => p.StartsWith ("Controls/Game/")), "代理だけを指す");

            proxies.ResetToDefaults ();
            using (ClipSampler sampler = new ClipSampler (rig_.animator)) {
                sampler.Sample (clip, 0);
            }

            Assert.AreEqual (0.3f, proxies.rigs[0].weight.value, 1e-5f);
            Assert.AreEqual (0.6f, proxies.rigs[0].constraints[1].value, 1e-5f);
            Assert.AreEqual (1, proxies.rigs[0].constraints[0].value, 1e-5f);
            Assert.That (Vector3.Distance (source.proxy.localPosition, new Vector3 (0.1f, 0.7f, 0.2f)), Is.LessThan (1e-5f));
            Assert.That (Quaternion.Angle (source.proxy.localRotation, Quaternion.Euler (5, 15, 25)), Is.LessThan (1e-2f));
        }

        [Test]
        public void NoLayersGivesEmptyProxies ()
        {
            rig_ = EditingRig.Create (definition_, display_, name => new GameObject (name));
            Assert.IsNotNull (rig_.rigProxies);
            Assert.AreEqual (0, rig_.rigProxies.rigs.Count);
            Assert.IsNull (rig_.FindControl (RigPaths.kGame));
            rig_.rigProxies.Apply ();
        }
    }

}
