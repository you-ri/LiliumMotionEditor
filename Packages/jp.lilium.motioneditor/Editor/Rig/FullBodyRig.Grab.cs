using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    // つかんでいる間の状態（つかんだ点・足と一緒に運ぶ点。キーを打つ・クリップから当て直すと消える）
    public sealed partial class FullBodyRig
    {
        /// <summary>つかんだ点と一緒に動かしている（固定に使わない）Pin の点。CarryFootPins</summary>
        readonly HashSet<Point> carried_ = new HashSet<Point> ();
        /// <summary>今つかんで動かしている点（MarkGrabbed。軽い点をつかんでいる間は、ほかの点とぶつかったら譲る）</summary>
        readonly HashSet<Point> grabbed_ = new HashSet<Point> ();

        /// <summary>
        /// 点をつかんで動かした（値を直に置いた）。つかんでいる点は、軽い点をつかんでいる間、Pin の点・重い点とぶつかったら譲る（kLightPull）。
        /// 引く点と、引かれる点の決まり（10-27 ユーザー指示「肘をうごかしているなら、手首、肩両方をひっぱる挙動になる。
        /// 自身の weight が 0 で手首は 1 で pin になっているから手首は動かない。肩は 0 のため、pin の場合は動かない。free の場合は動く」）:
        /// 軽い点は Free でも Pin でも引かれ、重い点は Free でも Pin でも留まる（10-30 ユーザー指示で、軽い Pin の点も引くようにした）。Lock の点は留まる。
        /// 重い点をつかんだときは、Free の点と重さ 0 の Pin の点は引かれ、重さ 1 の Pin の点は届く間はその場に留まり、届かなければ外れて付いて動く（PullsPin）。EndGrab で消える
        /// </summary>
        public void MarkGrabbed (BodyPointId id)
        {
            Point point = Find (id);
            if (point != null) MarkGrabbed (point);
        }

        void MarkGrabbed (Point point)
        {
            grabbed_.Add (point);
            carried_.Remove (point);
        }

        /// <summary>つかんだ点と一緒に動かすのをやめる（キーを打ったとき・クリップから姿勢を当て直したとき）。足の箱（TryMoveFoot）も捨てる</summary>
        public void EndGrab ()
        {
            carried_.Clear ();
            grabbed_.Clear ();
            footBoxes_.Clear ();
            footBoxPoints_.Clear ();
            footBoxToes_.Clear ();
        }

        /// <summary>足と一緒に動かしている Pin の点か（固定に使わない。点は骨の今の場所に出す）</summary>
        public bool IsCarried (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && carried_.Contains (point);
        }

        /// <summary>
        /// 点を、置いた場所でなく骨の今の場所に出すか（位置・向きそれぞれ）。足と一緒に動かしている Pin の点（CarryFootPins）と、
        /// ほかの点をつかんで動かしている間の、離したときに骨へ付け直す点（SyncPointToBone と同じ条件。強さ 1 で Locked でない）。
        /// 置いた場所に出すと、動かしている間は動かす前の場所に残り、離したときに骨の場所へ飛んで見えた（10-27 ユーザー指示）
        /// </summary>
        public void GetShownOnBone (BodyPointId id, out bool position, out bool rotation)
        {
            position = rotation = false;
            Point point = Find (id);
            if (point == null || point.passive) return;
            if (carried_.Contains (point)) {
                position = rotation = true;
                return;
            }
            if (grabbed_.Count == 0 || grabbed_.Contains (point) || point.values.locked > 0.5f) return;
            position = point.values.positionWeight >= 1;
            rotation = point.hasRotation && point.values.rotationWeight >= 1;
        }
    }

}
