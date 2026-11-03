using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// Humanoid の可動域から、全身 IK の角度制限を作る（S95）。
    ///
    /// 骨ごとに「muscle を全部 0」と「その骨の muscle を 1 本ずつ ±1（ほかは 0）」の姿勢を表示モデルへ当て、編集用の骨へ写して、
    /// ソルバーと同じ量（親の解く骨から見た T ポーズからの回りを、体の向きの軸で。FullBodyIk.LimitAngles）を測る。軸ごとの最小・最大を範囲にする。
    /// muscle ±1 は Avatar の範囲の端なので、測った値は Avatar の可動域そのもの。ひねりの配り方などで体の軸ときれいに合わない骨もあるので、目安として入れ、手で詰める。
    /// その骨の muscle が動かさない軸（肩のひねりなど）は、測った値のまわりの狭い範囲になる（遊びの分だけ動く）。遊びは定義の今の値を残す
    /// </summary>
    public static class HumanoidJointLimits
    {
        /// <summary>
        /// display（prefab の初期姿勢＝T ポーズのままの、Humanoid のキャラ）の可動域から、definition の全身 IK で解く骨の角度制限を作る。
        /// 表示モデルの姿勢は元に戻す。作った骨の数を返す。作れなければ problem に理由
        /// </summary>
        public static int Apply (EditRigDefinition definition, Animator display, out string problem)
        {
            problem = null;
            if (definition == null) {
                problem = Tr ("JOINT_LIMITS_NO_DEFINITION");
                return 0;
            }
            if (display == null || display.avatar == null || !display.avatar.isHuman || !display.avatar.isValid) {
                problem = Tr ("JOINT_LIMITS_NOT_HUMANOID");
                return 0;
            }

            Transform[] transforms = display.GetComponentsInChildren<Transform> (true);
            Vector3[] positions = new Vector3[transforms.Length];
            Quaternion[] rotations = new Quaternion[transforms.Length];
            for (int i = 0; i < transforms.Length; i++) transforms[i].GetLocalPositionAndRotation (out positions[i], out rotations[i]);

            List<GameObject> created = new List<GameObject> ();
            EditingRig rig = null;
            try {
                // 編集用の骨は、T ポーズの今の姿勢から作る（基準姿勢と体の向きが、モーションエディタで解くときと同じになる）
                rig = EditingRig.Create (definition, display, name => {
                    GameObject go = new GameObject (name) { hideFlags = HideFlags.HideAndDontSave };
                    // 表示モデルと同じシーンに置く（リグエディタはプレビューのシーンに複製を置いて呼ぶ。開いているシーンを汚さない）
                    if (display.gameObject.scene.IsValid ()) SceneManager.MoveGameObjectToScene (go, display.gameObject.scene);
                    created.Add (go);
                    return go;
                });
                if (rig.root == null) {
                    problem = Tr ("JOINT_LIMITS_NOT_HUMANOID");
                    return 0;
                }
                return Measure (definition, rig, display);
            }
            finally {
                rig?.Dispose ();
                foreach (GameObject go in created) {
                    if (go != null) Object.DestroyImmediate (go);
                }
                for (int i = 0; i < transforms.Length; i++) {
                    if (transforms[i] != null) transforms[i].SetLocalPositionAndRotation (positions[i], rotations[i]);
                }
            }
        }

        static int Measure (EditRigDefinition definition, EditingRig rig, Animator display)
        {
            // 親は、全身 IK と同じく、骨の階層を上へたどって最初に当たる解く骨
            HashSet<Transform> solved = new HashSet<Transform> ();
            foreach (RigBinding.BoneControl control in rig.binding.boneControls) {
                Transform bone = control.enabled && control.fullBodySolve ? rig.GetEditingBone (control.bone) : null;
                if (bone != null) solved.Add (bone);
            }

            HumanPoseHandler handler = new HumanPoseHandler (display.avatar, display.transform);
            HumanPose pose = new HumanPose ();
            handler.GetHumanPose (ref pose);
            Quaternion rootInverse = Quaternion.Inverse (rig.root.transform.rotation);
            int count = 0;
            try {
                foreach (EditRigDefinition.FkControl control in definition.fkControls) {
                    if (control == null || !control.fullBodySolve) continue;
                    RigBinding.Fk fk = rig.binding.FindFk (control.bone);
                    Transform bone = fk != null && fk.enabled ? rig.GetEditingBone (fk.bone) : null;
                    if (bone == null) continue;
                    Transform parent = bone.parent;
                    while (parent != null && !solved.Contains (parent)) parent = parent.parent;
                    if (parent == null) continue;
                    List<int> muscles = new List<int> ();
                    for (int dof = 0; dof < 3; dof++) {
                        int muscle = HumanTrait.MuscleFromBone ((int)control.bone, dof);
                        if (muscle >= 0) muscles.Add (muscle);
                    }
                    if (muscles.Count == 0) continue;
                    if (!FullBodyRig.TryGetLimitFrame (rig, bone, parent, out Quaternion frame, out Quaternion rest, out int twist)) continue;

                    Vector3 low = Vector3.positiveInfinity, high = Vector3.negativeInfinity;
                    // muscle を全部 0 にした姿勢と、この骨の muscle を 1 本ずつ ±1 にした姿勢
                    for (int sample = -1; sample < muscles.Count * 2; sample++) {
                        System.Array.Clear (pose.muscles, 0, pose.muscles.Length);
                        if (sample >= 0) pose.muscles[muscles[sample / 2]] = sample % 2 == 0 ? -1 : 1;
                        handler.SetHumanPose (ref pose);
                        rig.SyncFromDisplay ();
                        Quaternion relative = Quaternion.Inverse (rootInverse * parent.rotation) * (rootInverse * bone.rotation);
                        Vector3 angles = FullBodyRig.LimitToBodyAxes ((Vector3)FullBodyIk.LimitAngles (frame, rest, relative), twist) * Mathf.Rad2Deg;
                        low = Vector3.Min (low, angles);
                        high = Vector3.Max (high, angles);
                    }

                    if (control.limit == null) control.limit = new EditRigDefinition.JointLimit ();
                    for (int axis = 0; axis < 3; axis++) {
                        EditRigDefinition.AxisLimit value = control.limit[axis];
                        value.mode = EditRigDefinition.LimitMode.Range;
                        // 外側へ 1° 単位で丸める（測った端を範囲に入れる）
                        value.min = Mathf.Floor (low[axis] + 1e-3f);
                        value.max = Mathf.Ceil (high[axis] - 1e-3f);
                    }
                    count++;
                }
            }
            finally {
                handler.Dispose ();
            }
            return count;
        }
    }

}
