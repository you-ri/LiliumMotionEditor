using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// [PoseLayer] が付いた Component の型。段を組み立てるときと、
    /// プレビューの複製から要らないコンポーネントを外すときの「残す判定」で使う（外すと段が出なくなるため）
    /// </summary>
    public static class LayerTypes
    {
        static List<System.Type> poseLayerTypes_;

        /// <summary>
        /// 段になりうる型。Component でないもの（PlayableBehaviour など）は今は対象外
        /// </summary>
        public static IReadOnlyList<System.Type> poseLayerTypes
        {
            get {
                if (poseLayerTypes_ == null) {
                    poseLayerTypes_ = TypeCache.GetTypesWithAttribute<PoseLayerAttribute> ()
                        .Where (t => typeof (Component).IsAssignableFrom (t))
                        .ToList ();
                }
                return poseLayerTypes_;
            }
        }

        public static bool IsPoseLayer (Component component)
        {
            if (component == null) return false;

            System.Type type = component.GetType ();
            for (int i = 0; i < poseLayerTypes.Count; i++) {
                if (poseLayerTypes[i].IsAssignableFrom (type)) return true;
            }
            return false;
        }
    }

}
