using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    // 全身 IK の点の値（定義から読んだ点の情報・世界座標との行き来・固定の強さ）
    public sealed partial class FullBodyRig
    {
        /// <summary>点を骨へ合わせ直す差の下限（これ以下は届いているとみなす）</summary>
        const float kSyncDistance = 1e-4f;
        const float kSyncAngle = 0.01f;

        public int pointCount
        {
            get { return points_.Count; }
        }

        /// <summary>点の名前（親が子より先に来る順）</summary>
        public IEnumerable<BodyPointId> pointIds
        {
            get {
                foreach (Point point in points_) yield return point.id;
            }
        }

        /// <summary>
        /// 固定の強さが入っている点があるか。無ければ解かない（素通し）
        /// </summary>
        public bool hasActivePoint
        {
            get {
                foreach (Point point in points_) {
                    if (!point.passive && (point.values.positionWeight > 0 || (point.hasRotation && point.values.rotationWeight > 0))) return true;
                }
                return false;
            }
        }

        Transform rootTransform
        {
            get { return rig_.root.transform; }
        }

        float humanScale
        {
            get { return rig_.humanScale; }
        }

        Point Find (BodyPointId id)
        {
            foreach (Point point in points_) {
                if (point.id.Equals (id)) return point;
            }
            return null;
        }

        Point Find (Transform control)
        {
            foreach (Point point in points_) {
                if (point.control == control) return point;
            }
            return null;
        }

        public bool HasPoint (BodyPointId id)
        {
            return Find (id) != null;
        }

        /// <summary>点のコントロール（無ければ null）</summary>
        public Transform GetControl (BodyPointId id)
        {
            Point point = Find (id);
            return point != null ? point.control : null;
        }

        /// <summary>
        /// 点が付いている「骨の上の点」の今のワールドの位置。付け根の点は骨の位置、離れた点は定義の場所
        /// </summary>
        public bool TryGetBonePointWorld (BodyPointId id, out Vector3 position)
        {
            Point point = Find (id);
            position = point != null ? BonePointWorld (point) : Vector3.zero;
            return point != null;
        }

        static Vector3 BonePointWorld (Point point)
        {
            return point.bone.position + point.bone.rotation * PointOffset (point, point.bone.rotation, point.blendBone != null ? point.blendBone.rotation : point.bone.rotation);
        }

        /// <summary>
        /// 骨の上の点の、骨の付け根からの位置（今の骨の枠・メートル）。向きを混ぜる点（S34）は、ずれ（blendOffset）を
        /// 骨から見た相手の骨の、基準姿勢からの回りを blend だけ掛けて回す（解く側の FullBodyIk.PointOffset と同じ計算）。
        /// boneRotation / blendRotation は骨と相手の骨の向き（同じ空間なら、ワールドでもルート空間でもよい）
        /// </summary>
        static Vector3 PointOffset (Point point, Quaternion boneRotation, Quaternion blendRotation)
        {
            if (point.blendBone == null || point.blend <= 0) return point.offset;
            Quaternion relative = Quaternion.Inverse (boneRotation) * blendRotation * point.blendRest;
            return point.offset - point.blendOffset + Quaternion.Slerp (Quaternion.identity, EditingRigSolver.Normalize (relative), point.blend) * point.blendOffset;
        }

        /// <summary>接地の点か（定義の contact。床に留める・床と滑りを見る点）</summary>
        public bool IsContact (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && point.contact;
        }

        /// <summary>動かせない点か（定義の passive。S89。選べるが動かせず、キーを持たない）</summary>
        public bool IsPassive (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && point.passive;
        }

        /// <summary>床との当たりの球の半径（メートル。定義の radius。無ければ 0）</summary>
        public float GetRadius (BodyPointId id)
        {
            Point point = Find (id);
            return point != null ? point.radius : 0;
        }

        public Transform GetBone (BodyPointId id)
        {
            Point point = Find (id);
            return point != null ? point.bone : null;
        }

        public bool PointHasRotation (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && point.hasRotation;
        }

        // ---- 点の値 ----

        /// <summary>点のワールドの位置と向き（コントロールは正規化した値を持つので、GameObject の位置そのものではない）</summary>
        public bool TryGetPointWorld (BodyPointId id, out Vector3 position, out Quaternion rotation)
        {
            Point point = Find (id);
            if (point == null) {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = PointWorldPosition (point);
            rotation = PointWorldRotation (point);
            return true;
        }

        Vector3 PointWorldPosition (Point point)
        {
            return rootTransform.TransformPoint (point.control.localPosition * humanScale);
        }

        Quaternion PointWorldRotation (Point point)
        {
            return rootTransform.rotation * point.control.localRotation * point.restRootRotation;
        }

        public bool SetPointPositionWorld (BodyPointId id, Vector3 position)
        {
            Point point = Find (id);
            if (point == null) return false;
            point.control.localPosition = rootTransform.InverseTransformPoint (position) / humanScale;
            return true;
        }

        public bool SetPointRotationWorld (BodyPointId id, Quaternion rotation)
        {
            Point point = Find (id);
            if (point == null || !point.hasRotation) return false;
            point.control.localRotation = EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * rotation * Quaternion.Inverse (point.restRootRotation));
            return true;
        }

        /// <summary>
        /// 点の位置と向きを、今の骨の見た目に合わせる（固定の強さは変えない）。固定していない点をつかむ前に呼ぶと、今の場所から動かせる
        /// </summary>
        public bool CapturePoint (BodyPointId id)
        {
            Point point = Find (id);
            if (point == null) return false;
            CapturePoint (point);
            return true;
        }

        /// <summary>controls にあるコントロールの点を、今の骨の位置と向きに置く</summary>
        public void CapturePoints (ICollection<Transform> controls)
        {
            foreach (Point point in points_) {
                if (controls.Contains (point.control)) CapturePoint (point);
            }
        }

        /// <summary>
        /// control の点を今の骨の位置と向きに置いたときの、コントロールのローカルの値（コントロールは動かさない）。
        /// 向きを持たない点の向きは今のコントロールの値。点のコントロールでなければ false
        /// </summary>
        public bool TryGetCapturedValues (Transform control, out Vector3 localPosition, out Quaternion localRotation)
        {
            Point point = Find (control);
            if (point == null) {
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                return false;
            }
            localPosition = BoneLocalPosition (point);
            localRotation = point.hasRotation
                ? BoneLocalRotation (point)
                : point.control.localRotation;
            return true;
        }

        /// <summary>点を今の骨の上の点に置いたときの、コントロールのローカルの位置</summary>
        Vector3 BoneLocalPosition (Point point)
        {
            return rootTransform.InverseTransformPoint (BonePointWorld (point)) / humanScale;
        }

        /// <summary>点を今の骨の向きにしたときの、コントロールのローカルの向き（基準姿勢からの差）</summary>
        Quaternion BoneLocalRotation (Point point)
        {
            return EditingRigSolver.Normalize (Quaternion.Inverse (rootTransform.rotation) * point.bone.rotation * Quaternion.Inverse (point.restRootRotation));
        }

        void CapturePoint (Point point)
        {
            point.control.localPosition = BoneLocalPosition (point);
            if (point.hasRotation) {
                point.control.localRotation = BoneLocalRotation (point);
            }
        }

        public float GetPositionWeight (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && !point.passive ? point.values.positionWeight : 0;
        }

        public float GetRotationWeight (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && !point.passive && point.hasRotation ? point.values.rotationWeight : 0;
        }

        public void SetPositionWeight (BodyPointId id, float weight)
        {
            Point point = Find (id);
            if (point != null) point.values.positionWeight = Mathf.Clamp01 (weight);
        }

        public void SetRotationWeight (BodyPointId id, float weight)
        {
            Point point = Find (id);
            if (point != null && point.hasRotation) point.values.rotationWeight = Mathf.Clamp01 (weight);
        }

        /// <summary>完全固定か（置いた場所から動かさない点。そうでない点は、編集のたびに骨の位置へ合わせ直す）</summary>
        public bool GetLocked (BodyPointId id)
        {
            Point point = Find (id);
            return point != null && !point.passive && point.values.locked > 0.5f;
        }

        public void SetLocked (BodyPointId id, bool locked)
        {
            Point point = Find (id);
            if (point != null) point.values.locked = locked ? 1 : 0;
        }

        /// <summary>
        /// 強さ 1 で固定していて完全固定でない点を、実際の骨の位置（向きも固定していれば向きも）へ合わせ直す。
        /// 体が届かない所へ置いた点・ほかの点を動かして届かなくなった点が、体の形から離れたまま残らないようにする。
        /// 位置を直したら position、向きを直したら rotation が true
        /// </summary>
        public void SyncPointToBone (BodyPointId id, out bool position, out bool rotation)
        {
            position = false;
            rotation = false;
            Point point = Find (id);
            if (point == null || point.passive || point.values.locked > 0.5f) return;
            if (point.values.positionWeight >= 1 && Vector3.Distance (BonePointWorld (point), PointWorldPosition (point)) > kSyncDistance) {
                point.control.localPosition = BoneLocalPosition (point);
                position = true;
            }
            if (point.hasRotation && point.values.rotationWeight >= 1 && Quaternion.Angle (point.bone.rotation, PointWorldRotation (point)) > kSyncAngle) {
                point.control.localRotation = BoneLocalRotation (point);
                rotation = true;
            }
        }

        /// <summary>
        /// 点の基準の値（基準姿勢の骨の位置・向きの差なし）。ルートから見た値（＝保存する値）
        /// </summary>
        public bool TryGetPointRestValues (BodyPointId id, out Vector3 position, out Quaternion rotation)
        {
            Point point = Find (id);
            position = point != null ? point.restRootPosition / humanScale : Vector3.zero;
            rotation = Quaternion.identity;
            return point != null;
        }

        /// <summary>
        /// 点の骨を、親の骨から見た基準姿勢（ローカル）に戻したときの点のワールドの位置と向き（骨は動かさない）。
        /// 点の値はルートから見た値なので、基準の値（TryGetPointRestValues）へ戻すと、今の姿勢の体から離れた所へ飛ぶ。
        /// こちらは親の骨の今の姿勢に乗せるので、点は体に付いたまま、その骨の曲げだけが無くなる（FK の Reset と同じ）。
        /// 骨の付け根の点は位置が変わらず（腰は親から見た基準の位置へ戻る）、骨から離れた点は骨の向きと一緒に回る。
        /// resetBones は一緒に戻す骨（いくつかの点をまとめて戻すとき、その点の骨すべて）。祖先に戻す骨があれば、
        /// FK で根から順に戻したのと同じく、戻した祖先に付いて動いた所で求める（無ければ点の骨だけ戻す）。
        /// 祖先を戻さずに点ごとに求めると、脚の点をまとめて戻したとき、足首は曲がったままの脛に、膝の Pole はまっすぐにした太ももに乗り、食い違う
        /// </summary>
        public bool TryGetPointLocalRestWorld (BodyPointId id, ICollection<Transform> resetBones, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            Point point = Find (id);
            if (point == null || point.bone.parent == null) return false;
            if (resetBones == null) resetBones = new[] { point.bone };
            else if (!resetBones.Contains (point.bone)) resetBones = new List<Transform> (resetBones) { point.bone };
            Pose pose = LocalRestPose (point.bone, resetBones);
            // 点の向きは骨の向きそのもの（CapturePoint と同じ）
            rotation = pose.rotation;
            // 向きを混ぜる点は、相手の骨も戻した（戻した祖先に付いて動いた）向きで混ぜる
            Quaternion blendRotation = point.blendBone != null ? LocalRestPose (point.blendBone, resetBones).rotation : pose.rotation;
            position = pose.position + pose.rotation * PointOffset (point, pose.rotation, blendRotation);
            return true;
        }

        /// <summary>
        /// resetBones の骨を親から見た基準姿勢に戻したときの、bone のワールドの位置と向き（骨は動かさない）。
        /// 戻さない骨は親から見た今の値のまま、戻した祖先に付いて動く
        /// </summary>
        Pose LocalRestPose (Transform bone, ICollection<Transform> resetBones)
        {
            Transform parent = bone.parent;
            bool moved = false;
            foreach (Transform reset in resetBones) {
                // IsChildOf は自分自身でも true
                if (reset != null && bone.IsChildOf (reset)) {
                    moved = true;
                    break;
                }
            }
            if (!moved || parent == null) return new Pose (bone.position, bone.rotation);

            Pose parentPose = LocalRestPose (parent, resetBones);
            Vector3 localPosition;
            Quaternion localRotation;
            if (!resetBones.Contains (bone) || !rig_.TryGetRestPosition (bone, out localPosition) || !rig_.TryGetRestRotation (bone, out localRotation)) {
                bone.GetLocalPositionAndRotation (out localPosition, out localRotation);
            }
            return new Pose (parentPose.position + parentPose.rotation * Vector3.Scale (parent.lossyScale, localPosition), parentPose.rotation * localRotation);
        }

        /// <summary>
        /// 点が乗る骨の、基準姿勢からの今の向きの差（ルートから見た値）。向きを持たない点（胸など）でも出す（S60。点を座標の基準にするとき）
        /// </summary>
        internal bool TryGetBoneRotation (BodyPointId id, out Quaternion rotation)
        {
            Point point = Find (id);
            rotation = point != null ? BoneLocalRotation (point) : Quaternion.identity;
            return point != null;
        }

        /// <summary>届かなかった差（メートル）。固定の強さが 1 の点が、解いた後にどれだけ離れているか</summary>
        internal float GetResidual (BodyPointId id)
        {
            Point point = Find (id);
            return point != null ? Vector3.Distance (BonePointWorld (point), PointWorldPosition (point)) : 0;
        }
    }

}
