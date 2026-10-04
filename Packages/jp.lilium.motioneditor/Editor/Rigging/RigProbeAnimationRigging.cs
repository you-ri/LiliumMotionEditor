using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEditor.Animations.Rigging;
using UnityEngine.Animations.Rigging;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Animation Rigging が入っているときだけ読み込まれるアセンブリ（defineConstraints: MKT_ANIMATION_RIGGING）。
    /// キャラに組まれている Rig の層と拘束をスタックへ知らせる。
    ///
    /// 逆方向に解けるかは、拘束の型に [InverseRigConstraint] が付いた型があるかで自動判定する。
    /// 手で一覧を持つと、パッケージが増えたときに古くなるため
    /// </summary>
    [InitializeOnLoad]
    static class RigProbeAnimationRigging
    {
        /// <summary>
        /// Rig と拘束の重みのシリアライズ名（どちらも m_Weight。カーブはこの名前を指す）
        /// </summary>
        const string kWeightProperty = "m_Weight";

        static Dictionary<System.Type, bool> inverseByType_;

        static RigProbeAnimationRigging ()
        {
            RigProbe.describe = Describe;
            RigProbe.isRigComponent = IsRigComponent;
        }

        /// <summary>
        /// プレビューの複製で残すもの。これを外すと Rig の段が出ない
        /// </summary>
        static bool IsRigComponent (Component component)
        {
            return component is RigBuilder || component is Rig || component is IRigConstraint;
        }

        static List<RigLayerInfo> Describe (GameObject model)
        {
            List<RigLayerInfo> result = new List<RigLayerInfo> ();
            if (model == null) return result;

            RigBuilder builder = model.GetComponentInChildren<RigBuilder> (true);
            if (builder == null) return result;

            foreach (RigLayer layer in builder.layers) {
                if (layer == null || layer.rig == null) continue;

                Rig rig = layer.rig;
                RigLayerInfo info = new RigLayerInfo {
                    label = rig.name,
                    active = layer.active,
                    weight = rig.weight,
                    transform = rig.transform,
                    weightType = typeof (Rig),
                    weightProperty = kWeightProperty,
                    setWeight = value => { if (rig != null) rig.weight = value; },
                    getWeight = () => rig != null ? rig.weight : 0,
                };
                foreach (IRigConstraint constraint in layer.rig.GetComponentsInChildren<IRigConstraint> (true)) {
                    Component component = constraint as Component;
                    if (component == null) continue;

                    info.constraints.Add (DescribeConstraint (component));
                }
                result.Add (info);
            }
            return result;
        }

        /// <summary>
        /// 拘束を逆に通れるか。逆に通るとは「欲しい骨の姿勢から、拘束の値（ターゲットなど）を求める」こと。
        /// TwoBoneIK はヒントがあれば曲げる向きが決まるので 1 つに定まる。
        /// ヒントが無いと、肘・膝の向きが拘束に入ってくる姿勢しだいになる
        /// </summary>
        static RigConstraintInfo DescribeConstraint (Component component)
        {
            IRigConstraint constraint = (IRigConstraint)component;
            RigConstraintInfo info = new RigConstraintInfo {
                label = component.GetType ().Name,
                transform = component.transform,
                weight = constraint.weight,
                weightType = component.GetType (),
                weightProperty = kWeightProperty,
                setWeight = value => { if (component != null) constraint.weight = value; },
                getWeight = () => component != null ? constraint.weight : 0,
            };
            CollectSources (component, info.sources, info.constrained);

            TwoBoneIKConstraint twoBone = component as TwoBoneIKConstraint;
            if (twoBone != null) {
                bool hasHint = twoBone.data.hint != null;
                if (!hasHint) info.label = Tr ("RIG_PROBE_ANIMATION_RIGGING_NO_HINT_LABEL", info.label);
                info.inverse = hasHint ? InverseKind.Exact : InverseKind.Approximate;
                info.reason = hasHint ? Tr ("RIG_PROBE_ANIMATION_RIGGING_HINT_INVERSE")
                    : Tr ("RIG_PROBE_ANIMATION_RIGGING_NO_HINT_INVERSE");
                return info;
            }

            bool hasInverse = HasInverse (component.GetType ());
            info.inverse = hasInverse ? InverseKind.Approximate : InverseKind.None;
            info.reason = hasInverse ? Tr ("RIG_PROBE_ANIMATION_RIGGING_HAS_INVERSE") : Tr ("RIG_PROBE_ANIMATION_RIGGING_NO_INVERSE");
            return info;
        }

        /// <summary>
        /// 拘束が読む Transform。拘束のデータ（m_Data）にある参照のうち、[SyncSceneToStream] が付いたもの
        /// （AR が毎評価シーンから読み直す値＝ターゲット・ヒント・ソース）。拘束される骨には付いていない。
        /// 型ごとの一覧を持たないので、ゲーム側の自作の拘束（WorldPivotOverrideTransform など）もそのまま読める
        /// </summary>
        static void CollectSources (Component component, List<Transform> result, List<Transform> constrained)
        {
            SerializedObject serialized = new SerializedObject (component);
            SerializedProperty data = serialized.FindProperty ("m_Data");
            if (data == null) return;

            System.Type dataType = FindDataType (component.GetType ());
            SerializedProperty end = data.GetEndProperty ();
            SerializedProperty it = data.Copy ();
            bool enter = true;
            while (it.Next (enter) && !SerializedProperty.EqualContents (it, end)) {
                enter = true;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                Transform t = it.objectReferenceValue as Transform;
                if (t == null || result.Contains (t)) continue;
                if (!IsSyncedField (dataType, it.propertyPath)) {
                    // 読み直さない参照は、拘束が動かす骨（constrainedObject・root/mid/tip など）
                    if (!constrained.Contains (t)) constrained.Add (t);
                    continue;
                }
                result.Add (t);
            }
        }

        /// <summary>
        /// RigConstraint&lt;TJob, TData, TBinder&gt; の TData
        /// </summary>
        static System.Type FindDataType (System.Type type)
        {
            for (System.Type t = type; t != null; t = t.BaseType) {
                if (t.IsGenericType && t.GetGenericTypeDefinition () == typeof (RigConstraint<,,>)) return t.GetGenericArguments ()[1];
            }
            return null;
        }

        /// <summary>
        /// m_Data の直下のフィールド（m_Data.m_Target や m_Data.m_SourceObjects.m_Item0.transform の m_Target / m_SourceObjects）に
        /// [SyncSceneToStream] が付いているか。データの型が分からなければ、拘束される骨らしい名前以外を採る
        /// </summary>
        static bool IsSyncedField (System.Type dataType, string propertyPath)
        {
            string[] parts = propertyPath.Split ('.');
            if (parts.Length < 2) return false;
            string field = parts[1];
            if (dataType == null) return !field.StartsWith ("m_Constrained") && field != "m_Root" && field != "m_Mid" && field != "m_Tip";

            FieldInfo info = dataType.GetField (field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return info != null && info.GetCustomAttribute<SyncSceneToStreamAttribute> () != null;
        }

        /// <summary>
        /// その拘束を逆に解く拘束があるか。[InverseRigConstraint] が指す先の型で引く
        /// </summary>
        static bool HasInverse (System.Type constraintType)
        {
            if (inverseByType_ == null) {
                inverseByType_ = new Dictionary<System.Type, bool> ();
                foreach (System.Type type in TypeCache.GetTypesWithAttribute<InverseRigConstraintAttribute> ()) {
                    InverseRigConstraintAttribute attribute = type.GetCustomAttribute<InverseRigConstraintAttribute> ();
                    if (attribute == null || attribute.baseConstraint == null) continue;
                    inverseByType_[attribute.baseConstraint] = true;
                }
            }
            return inverseByType_.ContainsKey (constraintType);
        }
    }

}
