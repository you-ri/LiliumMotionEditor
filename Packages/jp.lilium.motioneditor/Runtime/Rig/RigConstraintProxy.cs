using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// ゲームの拘束（TwoBoneIK など）の重みの代理。拘束が Rig と同じ GameObject にあるときは、層の代理と同じ GameObject に並ぶ
    /// </summary>
    [AddComponentMenu ("")]
    public sealed class RigConstraintProxy : RigWeightProxy
    {
    }

}
