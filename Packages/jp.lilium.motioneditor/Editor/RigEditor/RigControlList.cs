using UnityEngine;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>リグエディタの一覧の種類（並びもこの順）</summary>
    enum RigControlKind
    {
        Fk,
        Ik,
        Extra,
        Point,
        Pivot,
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
        /// <summary>線を引く先（IK の組は根 → 中 → 先、FK は親の骨）。無ければ空</summary>
        public Vector3[] line = System.Array.Empty<Vector3> ();
        /// <summary>定義のどの項目か（SerializedObject のパス）。定義を渡さずに作ったときは null</summary>
        public string propertyPath;
        /// <summary>使えない・使われない理由。null なら使える</summary>
        public string disabledReason;
        /// <summary>
        /// 定義の誤りやキャラに骨が無いのではなく、今の定義の解き方では使わないもの（全身 IK の定義の IK の組など）。灰色で出す
        /// </summary>
        public bool unused;

        public bool enabled
        {
            get { return disabledReason == null; }
        }
    }

    /// <summary>
    /// 定義の全コントロールを、リグエディタに出す一覧にする（S45a）。
    /// IK の組は全身 IK の定義だと GameObject が作られないので、割り当ての骨から場所を出す
    /// </summary>
    static class RigControlList
    {
        /// <summary>足の転がしの支点のうち、一覧に出すもの（足裏の 2 つは全身 IK の点の置き場所にだけ使う）</summary>
        static readonly ReverseFoot.Pivot[] kPivots = {
            ReverseFoot.Pivot.Heel,
            ReverseFoot.Pivot.Ball,
            ReverseFoot.Pivot.ToeTip,
            ReverseFoot.Pivot.LeftEdge,
            ReverseFoot.Pivot.RightEdge,
            ReverseFoot.Pivot.SoleBall,
            ReverseFoot.Pivot.LittleToeSide,
        };

        /// <param name="definition">リグを作った定義。渡すと、各項目に定義のどの項目か（propertyPath）を入れる</param>
        public static List<RigControlEntry> Build (EditingRig rig, EditRigDefinition definition = null)
        {
            List<RigControlEntry> entries = new List<RigControlEntry> ();
            if (rig == null || rig.binding == null) return entries;
            RigBinding binding = rig.binding;
            bool fullBody = binding.solver == RigSolver.FullBodyIk;

            foreach (RigBinding.Fk fk in binding.fk) {
                Transform bone = rig.GetEditingBone (fk.bone);
                RigControlEntry entry = new RigControlEntry {
                    id = "Fk/" + fk.control.bone,
                    kind = RigControlKind.Fk,
                    label = fk.control.bone.ToString (),
                    position = Position (rig, bone),
                    propertyPath = ElementPath (definition, "fkControls", definition != null ? definition.fkControls.IndexOf (fk.control) : -1),
                    disabledReason = fk.disabledReason,
                };
                // 骨のつながりが見えるよう、親の FK の骨へ線を引く
                Transform parent = ParentFkBone (rig, fk.control.bone);
                if (bone != null && parent != null) entry.line = new[] { Position (rig, parent).Value, entry.position.Value };
                entries.Add (entry);
            }

            foreach (RigBinding.Ik ik in binding.ik) {
                Transform root = rig.GetEditingBone (ik.root);
                Transform mid = rig.GetEditingBone (ik.mid);
                Transform tip = rig.GetEditingBone (ik.tip);
                RigControlEntry entry = new RigControlEntry {
                    id = "Ik/" + ik.chain.name,
                    kind = RigControlKind.Ik,
                    label = ik.chain.name,
                    position = Position (rig, tip),
                    propertyPath = ChainPath (definition, ik),
                    disabledReason = ik.disabledReason,
                    unused = ik.disabledReason == RigBinding.kFullBodyIkReason,
                };
                if (root != null && mid != null && tip != null) entry.line = new[] { Position (rig, root).Value, Position (rig, mid).Value, Position (rig, tip).Value };
                entries.Add (entry);
            }

            foreach (RigBinding.Extra extra in binding.extras) {
                entries.Add (new RigControlEntry {
                    id = "Extra/" + extra.control.name,
                    kind = RigControlKind.Extra,
                    label = extra.control.name,
                    position = Position (rig, rig.GetEditingBone (extra.bone)),
                    propertyPath = ElementPath (definition, "extraControls", definition != null ? definition.extraControls.IndexOf (extra.control) : -1),
                    disabledReason = extra.disabledReason,
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

            foreach (RigBinding.Ik ik in binding.ik) {
                if (ik.chain.tip != HumanBodyBones.LeftFoot && ik.chain.tip != HumanBodyBones.RightFoot) continue;
                AddPivots (entries, rig, ik, fullBody, ChainPath (definition, ik));
            }
            return entries;
        }

        /// <summary>
        /// 脚の組の足の支点。全身 IK の定義では足の点（かかと・足裏・つま先の先）の置き場所に使い、
        /// 2 本の骨の IK の定義では足の転がしを使う組だけで効く
        /// </summary>
        static void AddPivots (List<RigControlEntry> entries, EditingRig rig, RigBinding.Ik ik, bool fullBody, string chainPath)
        {
            Transform shin = rig.GetEditingBone (ik.mid);
            Transform foot = rig.GetEditingBone (ik.tip);
            string reason = null;
            bool unused = false;
            if (shin == null || foot == null) reason = Tr ("RIG_EDITOR_PIVOT_NO_LEG");
            else if (!fullBody && !ik.chain.reverseFoot) {
                reason = Tr ("RIG_EDITOR_PIVOT_NO_REVERSE_FOOT");
                unused = true;
            }
            ReverseFoot shape = shin != null && foot != null ? ReverseFoot.Create (rig, shin, foot, rig.GetEditingBone (ik.chain.toes), ik.chain) : null;
            foreach (ReverseFoot.Pivot pivot in kPivots) {
                Vector3? position = null;
                if (shape != null) position = rig.root.transform.TransformPoint (rig.GetRestRootPosition (foot) + rig.GetRestRootRotation (foot) * shape.GetPivot (pivot));
                entries.Add (new RigControlEntry {
                    id = "Pivot/" + ik.chain.name + "/" + pivot,
                    kind = RigControlKind.Pivot,
                    label = ik.chain.name + " " + pivot,
                    position = position,
                    // 支点の上書き・折れ角は脚の組の項目に持つ（S48 で窓から直す）
                    propertyPath = chainPath,
                    disabledReason = reason,
                    unused = unused,
                });
            }
        }

        static string ChainPath (EditRigDefinition definition, RigBinding.Ik ik)
        {
            return ElementPath (definition, "ikChains", definition != null ? definition.ikChains.IndexOf (ik.chain) : -1);
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

        /// <summary>人型の親をたどって、最初に当たる FK の骨（編集用）。無ければ null</summary>
        static Transform ParentFkBone (EditingRig rig, HumanBodyBones bone)
        {
            for (int b = HumanTrait.GetParentBone ((int)bone); b >= 0 && b < (int)HumanBodyBones.LastBone; b = HumanTrait.GetParentBone (b)) {
                Transform parent = rig.GetEditingBone ((HumanBodyBones)b);
                if (parent != null) return parent;
            }
            return null;
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
                case RigControlKind.Ik: return Tr ("RIG_EDITOR_KIND_IK");
                case RigControlKind.Extra: return Tr ("RIG_EDITOR_KIND_EXTRA");
                case RigControlKind.Point: return Tr ("RIG_EDITOR_KIND_POINT");
                default: return Tr ("RIG_EDITOR_KIND_PIVOT");
            }
        }
    }

}
