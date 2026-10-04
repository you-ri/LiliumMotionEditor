using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// ゲームの Animation Rigging の値（Rig と拘束の重み・ターゲットなど）を、編集用の体の代理（Controls/Game/&lt;Rig 名&gt;）に持つ（表現の決定 4）。
    /// 編集用クリップは代理だけを指し、ゲーム prefab のパスへは焼くときに寄せる（対応は channels）。
    ///
    /// 持ち方:
    /// - Rig の重み: Controls/Game/&lt;Rig&gt; の RigLayerProxy.weight
    /// - 拘束の重み: Rig から拘束までと同じ相対パスの GameObject の RigConstraintProxy.weight（拘束が Rig と同じ GameObject なら同じ所）
    /// - ターゲット・ヒントなど（拘束が読む Transform のうち Rig の下にあるもの）: 同じ相対パスの GameObject の
    ///   localPosition = ゲームの localPosition / humanScale、localRotation = ゲームの localRotation
    ///
    /// 有効（active）な層だけを持つ。Rig の外（骨の下など）にあるターゲットは骨と一緒に動くので代理に持たない（notes）
    /// </summary>
    public sealed class RigProxies
    {
        public enum ChannelKind
        {
            Weight,
            Position,
            Rotation,
        }

        /// <summary>
        /// 代理のカーブと、焼くときの行き先（ゲーム prefab の Animator から見たパス）の組
        /// </summary>
        public sealed class Channel
        {
            public ChannelKind kind;
            public string proxyPath;
            public System.Type proxyType;
            /// <summary>重みはプロパティ名、位置・回転は m_LocalPosition / m_LocalRotation（成分は付けない）</summary>
            public string proxyProperty;
            public string gamePath;
            public System.Type gameType;
            public string gameProperty;
            /// <summary>代理の値に掛けるとゲームの値になる倍率（位置は humanScale）</summary>
            public float scale = 1;
            /// <summary>値を持つ代理（重みは weight、位置・回転は proxyTransform）</summary>
            public Weight weight;
            public Transform proxyTransform;
        }

        /// <summary>
        /// 重み 1 つ（Rig の層か拘束）
        /// </summary>
        public sealed class Weight
        {
            public string label;
            public string rigName;
            /// <summary>代理の GameObject（カーブのパス）</summary>
            public Transform owner;
            public System.Type proxyType;
            public float defaultValue;
            internal MonoBehaviour component;
            internal System.Action<float> apply;

            public float value
            {
                get {
                    RigLayerProxy layer = component as RigLayerProxy;
                    if (layer != null) return layer.weight;
                    RigConstraintProxy constraint = component as RigConstraintProxy;
                    return constraint != null ? constraint.weight : 0;
                }
                set {
                    value = Mathf.Clamp01 (value);
                    RigLayerProxy layer = component as RigLayerProxy;
                    if (layer != null) layer.weight = value;
                    RigConstraintProxy constraint = component as RigConstraintProxy;
                    if (constraint != null) constraint.weight = value;
                }
            }

            public void WriteKeys (CurveWriter writer)
            {
                writer.Float (owner, proxyType, RigLayerProxy.kWeightProperty, value);
            }
        }

        /// <summary>
        /// 拘束が読む Transform 1 つ（ターゲット・ヒント・傾きの元など）
        /// </summary>
        public sealed class Source
        {
            public string label;
            public string rigName;
            /// <summary>代理（正規化した値を持つ）</summary>
            public Transform proxy;
            /// <summary>編集用の体の中の複製（表示モデルへはこれが写る）</summary>
            public Transform editing;
            public Transform display;
            public Vector3 defaultPosition;
            public Quaternion defaultRotation;
        }

        public sealed class RigEntry
        {
            public string name;
            public Transform proxy;
            public Weight weight;
            public readonly List<Weight> constraints = new List<Weight> ();
            public readonly List<Source> sources = new List<Source> ();
        }

        readonly List<RigEntry> rigs_ = new List<RigEntry> ();
        readonly List<Weight> weights_ = new List<Weight> ();
        readonly List<Source> sources_ = new List<Source> ();
        readonly List<Channel> channels_ = new List<Channel> ();
        float humanScale_ = 1;

        /// <summary>
        /// 代理に持たなかったものと、その理由
        /// </summary>
        public readonly List<string> notes = new List<string> ();

        public IReadOnlyList<RigEntry> rigs
        {
            get { return rigs_; }
        }

        public IReadOnlyList<Weight> weights
        {
            get { return weights_; }
        }

        public IReadOnlyList<Source> sources
        {
            get { return sources_; }
        }

        public IReadOnlyList<Channel> channels
        {
            get { return channels_; }
        }

        /// <param name="createControl">コントロールのパスから GameObject を作る（途中も作る。既にあればそれを返す）</param>
        public static RigProxies Create (EditingRig rig, IList<RigLayerInfo> layers, System.Func<string, Transform> createControl)
        {
            RigProxies proxies = new RigProxies ();
            if (rig == null || rig.displayAnimator == null || layers == null) return proxies;

            proxies.humanScale_ = rig.humanScale > 0 ? rig.humanScale : 1;
            Transform displayRoot = rig.displayAnimator.transform;
            HashSet<string> names = new HashSet<string> ();
            foreach (RigLayerInfo layer in layers) {
                if (layer == null || !layer.active || layer.transform == null) continue;

                string name = layer.label;
                if (string.IsNullOrEmpty (name) || name.IndexOf ('/') >= 0) {
                    proxies.notes.Add (Tr ("RIG_PROXIES_INVALID_RIG_NAME", name));
                    continue;
                }
                if (!names.Add (name)) {
                    proxies.notes.Add (Tr ("RIG_PROXIES_DUPLICATE_RIG_NAME", name));
                    continue;
                }
                if (!layer.transform.IsChildOf (displayRoot)) {
                    proxies.notes.Add (Tr ("RIG_PROXIES_RIG_OUTSIDE_ANIMATOR", name));
                    continue;
                }
                proxies.AddRig (rig, layer, displayRoot, createControl);
            }
            return proxies;
        }

        void AddRig (EditingRig rig, RigLayerInfo layer, Transform displayRoot, System.Func<string, Transform> createControl)
        {
            RigEntry entry = new RigEntry { name = layer.label };
            entry.proxy = createControl (RigPaths.Game (entry.name));
            entry.weight = AddWeight (entry, entry.name, entry.proxy, entry.proxy.gameObject.AddComponent<RigLayerProxy> (), layer.weight, layer.setWeight);
            AddWeightChannel (entry.weight, RigPaths.Game (entry.name), PathOf (layer.transform, displayRoot), layer.weightType, layer.weightProperty);

            foreach (RigConstraintInfo constraint in layer.constraints) {
                if (constraint.transform == null || !constraint.transform.IsChildOf (layer.transform)) continue;

                string relative = PathOf (constraint.transform, layer.transform);
                Transform owner = createControl (RigPaths.Game (entry.name, relative));
                if (owner.GetComponent<RigConstraintProxy> () != null) continue;

                string label = entry.name + "/" + (relative.Length > 0 ? relative : constraint.label);
                Weight weight = AddWeight (entry, label, owner, owner.gameObject.AddComponent<RigConstraintProxy> (), constraint.weight, constraint.setWeight);
                entry.constraints.Add (weight);
                AddWeightChannel (weight, RigPaths.Game (entry.name, relative), PathOf (constraint.transform, displayRoot), constraint.weightType, constraint.weightProperty);

                foreach (Transform source in constraint.sources) {
                    AddSource (rig, entry, layer.transform, displayRoot, label, source, createControl);
                }
            }
            rigs_.Add (entry);
        }

        Weight AddWeight (RigEntry entry, string label, Transform owner, MonoBehaviour component, float value, System.Action<float> apply)
        {
            Weight weight = new Weight {
                label = label,
                rigName = entry.name,
                owner = owner,
                proxyType = component.GetType (),
                defaultValue = Mathf.Clamp01 (value),
                component = component,
                apply = apply,
            };
            weight.value = weight.defaultValue;
            weights_.Add (weight);
            return weight;
        }

        void AddWeightChannel (Weight weight, string proxyPath, string gamePath, System.Type gameType, string gameProperty)
        {
            channels_.Add (new Channel {
                kind = ChannelKind.Weight,
                proxyPath = proxyPath,
                proxyType = weight.proxyType,
                proxyProperty = RigLayerProxy.kWeightProperty,
                gamePath = gamePath,
                gameType = gameType,
                gameProperty = gameProperty,
                weight = weight,
                proxyTransform = weight.owner,
            });
        }

        void AddSource (EditingRig rig, RigEntry entry, Transform rigTransform, Transform displayRoot, string constraintLabel, Transform source, System.Func<string, Transform> createControl)
        {
            if (source == null || sources_.Exists (s => s.display == source)) return;
            if (!source.IsChildOf (rigTransform)) {
                notes.Add (Tr ("RIG_PROXIES_SOURCE_OUTSIDE_RIG", constraintLabel, source.name));
                return;
            }
            Transform editing = rig.GetEditingBone (source);
            if (editing == null) {
                notes.Add (Tr ("RIG_PROXIES_SOURCE_NOT_IN_EDITING_BODY", constraintLabel, source.name));
                return;
            }

            string relative = PathOf (source, rigTransform);
            Source item = new Source {
                label = entry.name + "/" + (relative.Length > 0 ? relative : source.name),
                rigName = entry.name,
                proxy = createControl (RigPaths.Game (entry.name, relative)),
                editing = editing,
                display = source,
                defaultPosition = source.localPosition / humanScale_,
                defaultRotation = source.localRotation,
            };
            item.proxy.SetLocalPositionAndRotation (item.defaultPosition, item.defaultRotation);
            sources_.Add (item);
            entry.sources.Add (item);

            string proxyPath = RigPaths.Game (entry.name, relative);
            string gamePath = PathOf (source, displayRoot);
            channels_.Add (new Channel {
                kind = ChannelKind.Position, proxyPath = proxyPath, proxyType = typeof (Transform), proxyProperty = "m_LocalPosition",
                gamePath = gamePath, gameType = typeof (Transform), gameProperty = "m_LocalPosition", scale = humanScale_,
                proxyTransform = item.proxy,
            });
            channels_.Add (new Channel {
                kind = ChannelKind.Rotation, proxyPath = proxyPath, proxyType = typeof (Transform), proxyProperty = "m_LocalRotation",
                gamePath = gamePath, gameType = typeof (Transform), gameProperty = "m_LocalRotation",
                proxyTransform = item.proxy,
            });
        }

        static string PathOf (Transform t, Transform root)
        {
            return t == root ? "" : AnimationUtility.CalculateTransformPath (t, root);
        }

        /// <summary>
        /// 代理の値を、編集用の体（ターゲットの複製）と表示モデルの Rig（重み）へ入れる
        /// </summary>
        public void Apply ()
        {
            foreach (Source source in sources_) {
                if (source.proxy == null || source.editing == null) continue;
                source.proxy.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
                source.editing.SetLocalPositionAndRotation (position * humanScale_, rotation);
            }
            foreach (Weight weight in weights_) {
                if (weight.component != null && weight.apply != null) weight.apply (weight.value);
            }
        }

        /// <summary>
        /// ターゲットの複製を動かした後に、その値を代理へ戻す
        /// </summary>
        public void Capture (Source source)
        {
            if (source == null || source.proxy == null || source.editing == null) return;
            source.editing.GetLocalPositionAndRotation (out Vector3 position, out Quaternion rotation);
            source.proxy.SetLocalPositionAndRotation (position / humanScale_, rotation);
        }

        public void ResetSource (Source source)
        {
            if (source == null || source.proxy == null) return;
            source.proxy.SetLocalPositionAndRotation (source.defaultPosition, source.defaultRotation);
        }

        /// <summary>
        /// 重みとターゲットを prefab の値に戻す
        /// </summary>
        public void ResetToDefaults ()
        {
            foreach (Weight weight in weights_) {
                if (weight.component != null) weight.value = weight.defaultValue;
            }
            foreach (Source source in sources_) {
                ResetSource (source);
            }
        }

        public void WriteWeightKeys (CurveWriter writer)
        {
            foreach (Weight weight in weights_) {
                if (weight.component != null) weight.WriteKeys (writer);
            }
        }
    }

}
