using UnityEngine;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>リグエディタの一覧の種類（並びもこの順）</summary>
    enum RigControlKind
    {
        Fk,
        Extra,
        Point,
    }

    /// <summary>
    /// リグエディタに出すコントロール 1 つ。定義の項目を 1 体のキャラに当てた結果（RigBinding）から作る。
    /// 場所は基準姿勢のワールドの位置（窓は基準姿勢のまま描くので、作った後に骨が動くことはない）
    /// </summary>
    sealed class RigControlEntry
    {
        /// <summary>選択を覚えておくための名前（種類と名前。作り直しても同じ物を指す）</summary>
        public string id;
        public RigControlKind kind;
        public string label;
        /// <summary>描く場所（基準姿勢のワールド）。骨が無いコントロールは null</summary>
        public Vector3? position;
        /// <summary>
        /// 骨の線（FK と追加のコントロール）。[0] が骨の根元、[1] 以降が骨の先で、根元から先それぞれへ 1 本ずつ引く。
        /// 先は子の骨の根元すべて（腰 → 背骨・両脚。子が複数でも全部へ引く）。子が無ければ親から続く向きに 1 つ（モーションエディタの骨の形と同じ）。
        /// 窓はこの線で骨を描いて選ばせる（点の丸と同じ場所に出すと、点と骨を押し分けられない）。無ければ空
        /// </summary>
        public Vector3[] line = System.Array.Empty<Vector3> ();
        /// <summary>定義のどの項目か（SerializedObject のパス）。定義を渡さずに作ったときは null</summary>
        public string propertyPath;
        /// <summary>使えない理由。null なら使える</summary>
        public string disabledReason;

        public bool enabled
        {
            get { return disabledReason == null; }
        }
    }

    /// <summary>
    /// 定義の全コントロールを、リグエディタに出す一覧にする（S45a）
    /// </summary>
    static class RigControlList
    {
        /// <param name="definition">リグを作った定義。渡すと、各項目に定義のどの項目か（propertyPath）を入れる</param>
        public static List<RigControlEntry> Build (EditingRig rig, EditRigDefinition definition = null)
        {
            List<RigControlEntry> entries = new List<RigControlEntry> ();
            if (rig == null || rig.binding == null) return entries;
            RigBinding binding = rig.binding;
            BoneShapes shapes = new BoneShapes (rig);

            foreach (RigBinding.Fk fk in binding.fk) {
                Transform bone = rig.GetEditingBone (fk.bone);
                RigControlEntry entry = new RigControlEntry {
                    id = "Fk/" + fk.control.bone,
                    kind = RigControlKind.Fk,
                    label = fk.control.bone.ToString (),
                    position = Position (rig, bone),
                    propertyPath = ElementPath (definition, "fkControls", definition != null ? definition.fkControls.IndexOf (fk.control) : -1),
                    disabledReason = fk.disabledReason,
                    line = BoneLine (rig, shapes, bone),
                };
                entries.Add (entry);
            }

            foreach (RigBinding.Extra extra in binding.extras) {
                Transform bone = rig.GetEditingBone (extra.bone);
                entries.Add (new RigControlEntry {
                    id = "Extra/" + extra.control.name,
                    kind = RigControlKind.Extra,
                    label = extra.control.name,
                    position = Position (rig, bone),
                    propertyPath = ElementPath (definition, "extraControls", definition != null ? definition.extraControls.IndexOf (extra.control) : -1),
                    disabledReason = extra.disabledReason,
                    line = BoneLine (rig, shapes, bone),
                });
            }

            // 全身 IK の点は、割り当てに残ったものだけが並ぶ（乗る骨が見つからない点は割り当てに入らない）
            foreach (RigBinding.Point point in binding.points) {
                Transform host = rig.GetEditingBone (point.bone);
                entries.Add (new RigControlEntry {
                    id = "Point/" + point.name,
                    kind = RigControlKind.Point,
                    label = point.name,
                    position = host != null ? rig.root.transform.TransformPoint (rig.GetRestRootPosition (host) + rig.GetPointRestOffset (point)) : (Vector3?)null,
                    propertyPath = ElementPath (definition, "bodyPoints", definition != null ? definition.bodyPoints.IndexOf (point.definition) : -1),
                    disabledReason = point.disabledReason,
                });
            }
            return entries;
        }

        static string ElementPath (EditRigDefinition definition, string list, int index)
        {
            return definition != null && index >= 0 ? list + ".Array.data[" + index + "]" : null;
        }

        static Vector3? Position (EditingRig rig, Transform editingBone)
        {
            if (editingBone == null) return null;
            return rig.root.transform.TransformPoint (rig.GetRestRootPosition (editingBone));
        }

        /// <summary>骨の根元と、骨の先すべて（BoneShapes.GetRestEnds。基準姿勢のワールド）。骨が無ければ空</summary>
        static Vector3[] BoneLine (EditingRig rig, BoneShapes shapes, Transform editingBone)
        {
            if (editingBone == null) return System.Array.Empty<Vector3> ();
            List<Vector3> line = new List<Vector3> { Position (rig, editingBone).Value };
            line.AddRange (shapes.GetRestEnds (editingBone));
            return line.ToArray ();
        }

        /// <summary>
        /// 一覧の上に出す問題（定義の誤り・キャラ全体の問題・気にしておくこと）。誤りが先
        /// </summary>
        public static List<string> CollectProblems (EditingRig rig, out int errorCount)
        {
            List<string> problems = new List<string> ();
            errorCount = 0;
            if (rig == null) return problems;
            // 割り当ての errors には定義の Validate の誤りも入っている（RigBinding.Bind）
            foreach (string error in rig.errors) {
                if (!problems.Contains (error)) problems.Add (error);
            }
            errorCount = problems.Count;
            if (rig.binding != null) {
                foreach (string note in rig.binding.notes) {
                    if (!problems.Contains (note)) problems.Add (note);
                }
            }
            return problems;
        }

        public static string KindLabel (RigControlKind kind)
        {
            switch (kind) {
                case RigControlKind.Fk: return Tr ("RIG_EDITOR_KIND_FK");
                case RigControlKind.Extra: return Tr ("RIG_EDITOR_KIND_EXTRA");
                default: return Tr ("RIG_EDITOR_KIND_POINT");
            }
        }
    }

}
