using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 定義（EditRigDefinition）を 1 体のキャラに当てた結果。人型の骨は Avatar の対応表（Animator.GetBoneTransform）で、
    /// 追加コントロールは骨の名前のパターンで引く。見つからないコントロールは無効にして理由を付ける（消さない）
    /// </summary>
    public sealed class RigBinding
    {
        public abstract class Control
        {
            /// <summary>編集用階層でのパス（＝クリップのカーブのパス）</summary>
            public string path;
            /// <summary>無効の理由。null なら有効</summary>
            public string disabledReason;

            public bool enabled
            {
                get { return disabledReason == null; }
            }
        }

        public sealed class Fk : Control
        {
            public EditRigDefinition.FkControl control;
            public Transform bone;
        }

        public sealed class Ik : Control
        {
            public EditRigDefinition.IkChain chain;
            public Transform root;
            public Transform mid;
            public Transform tip;
        }

        public sealed class Extra : Control
        {
            public EditRigDefinition.ExtraControl control;
            public Transform bone;
        }

        /// <summary>全身 IK の点（S25）。全身 IK の定義のときだけ並ぶ</summary>
        /// <summary>全身 IK の点（リグ定義の点の一覧の 1 つを、このキャラの骨に割り当てたもの）</summary>
        public sealed class Point : Control
        {
            public EditRigDefinition.BodyPoint definition;
            /// <summary>点の名前（定義の名前）</summary>
            public string name;
            /// <summary>点が乗る骨（定義の骨がこのキャラに無い・解かない骨なら、親をたどって最初に当たる解く骨）</summary>
            public Transform bone;
            /// <summary>点が乗る骨の人型の名前</summary>
            public HumanBodyBones humanBone;
            /// <summary>向きも持つ点か</summary>
            public bool hasRotation;
        }

        public static string kFullBodyIkReason => Tr ("RIG_BINDING_FULL_BODY_IK_UNUSED");

        public readonly List<Fk> fk = new List<Fk> ();
        public readonly List<Ik> ik = new List<Ik> ();
        public readonly List<Extra> extras = new List<Extra> ();
        public readonly List<Point> points = new List<Point> ();
        /// <summary>定義の、手足の位置を保つ仕組み</summary>
        public RigSolver solver { get; private set; }
        /// <summary>
        /// キャラ全体の問題（Avatar が人型でない・定義の誤りなど）
        /// </summary>
        public readonly List<string> errors = new List<string> ();
        /// <summary>
        /// 動くが気にしておくこと（パターンに複数の骨が当たった、など）
        /// </summary>
        public readonly List<string> notes = new List<string> ();

        public Animator animator { get; private set; }

        public IEnumerable<Control> controls
        {
            get { return fk.Cast<Control> ().Concat (ik.Cast<Control> ()).Concat (extras.Cast<Control> ()).Concat (points.Cast<Control> ()); }
        }

        public Fk FindFk (HumanBodyBones bone)
        {
            return fk.FirstOrDefault (c => c.control.bone == bone);
        }

        public Ik FindIk (string name)
        {
            return ik.FirstOrDefault (c => c.chain.name == name);
        }

        /// <summary>骨の付け根の点（既定の一覧での名前は骨の名前）</summary>
        public Point FindPoint (HumanBodyBones bone)
        {
            return FindPoint (bone.ToString ());
        }

        public Point FindPoint (string name)
        {
            return points.FirstOrDefault (c => c.name == name);
        }

        public static RigBinding Bind (EditRigDefinition definition, Animator animator)
        {
            RigBinding binding = new RigBinding { animator = animator };
            if (definition == null) {
                binding.errors.Add (Tr ("RIG_BINDING_NO_DEFINITION"));
                return binding;
            }
            binding.errors.AddRange (definition.Validate ());
            binding.solver = definition.solver;
            bool fullBody = definition.solver == RigSolver.FullBodyIk;

            bool isHuman = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;
            string humanReason = animator == null ? Tr ("RIG_BINDING_NO_ANIMATOR")
                : isHuman ? null : Tr ("RIG_BINDING_NO_HUMANOID_AVATAR");
            if (humanReason != null) binding.errors.Add (humanReason);

            foreach (EditRigDefinition.FkControl control in definition.fkControls) {
                Transform bone = isHuman ? animator.GetBoneTransform (control.bone) : null;
                binding.fk.Add (new Fk {
                    control = control,
                    path = RigPaths.Fk (control.bone),
                    bone = bone,
                    disabledReason = humanReason ?? (bone == null ? Tr ("RIG_BINDING_MISSING_BONE", control.bone) : null),
                });
            }

            foreach (EditRigDefinition.IkChain chain in definition.ikChains) {
                Ik entry = new Ik { chain = chain, path = RigPaths.IkChain (chain.name) };
                if (isHuman) {
                    entry.root = animator.GetBoneTransform (chain.root);
                    entry.mid = animator.GetBoneTransform (chain.mid);
                    entry.tip = animator.GetBoneTransform (chain.tip);
                }
                // 全身 IK の定義では IK の組を使わない（消さずに無効にする。コントロールも、解く処理も、ハンドルも、この印で外れる）
                entry.disabledReason = humanReason ?? (fullBody ? kFullBodyIkReason : ChainProblem (entry));
                binding.ik.Add (entry);
            }

            if (fullBody && definition.bodyPoints != null) {
                foreach (EditRigDefinition.BodyPoint entry in definition.bodyPoints) {
                    if (entry == null || !entry.enabled || !RigPaths.IsValidName (entry.name)) continue;
                    // 乗る骨: 定義の骨がこのキャラに無い・解かない骨なら、親の骨をたどって最初に当たる解く骨（つま先の骨が無ければ足）。
                    // ただし骨の付け根そのものの点は親へ乗せない（親の付け根の点と重なるだけなので、作らない）
                    bool onOrigin = entry.anchor == BodyPointAnchor.BoneOrigin && entry.offset == Vector3.zero;
                    Fk host = null;
                    for (int b = (int)entry.bone; b >= 0 && b < (int)HumanBodyBones.LastBone && host == null; b = HumanTrait.GetParentBone (b)) {
                        Fk fk = binding.FindFk ((HumanBodyBones)b);
                        if (fk != null && fk.enabled && fk.control.fullBodySolve) host = fk;
                        else if (onOrigin) break;
                    }
                    if (host == null) continue;
                    binding.points.Add (new Point {
                        definition = entry,
                        name = entry.name,
                        path = RigPaths.BodyPoint (entry.name),
                        bone = host.bone,
                        humanBone = host.control.bone,
                        hasRotation = entry.rotation,
                    });
                }
            }

            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                binding.extras.Add (BindExtra (binding, control, animator));
            }
            return binding;
        }

        /// <summary>
        /// IK の 3 本の骨がそろい、親子の順（root → mid → tip）になっているか
        /// </summary>
        static string ChainProblem (Ik entry)
        {
            EditRigDefinition.IkChain chain = entry.chain;
            List<string> missing = new List<string> ();
            if (entry.root == null) missing.Add (chain.root.ToString ());
            if (entry.mid == null) missing.Add (chain.mid.ToString ());
            if (entry.tip == null) missing.Add (chain.tip.ToString ());
            if (missing.Count > 0) return Tr ("RIG_BINDING_MISSING_CHAIN_BONES", string.Join (Tr ("RIG_BINDING_BONE_SEPARATOR"), missing));

            if (!entry.mid.IsChildOf (entry.root) || entry.mid == entry.root) return Tr ("RIG_BINDING_NOT_CHILD_OF", chain.mid, chain.root);
            if (!entry.tip.IsChildOf (entry.mid) || entry.tip == entry.mid) return Tr ("RIG_BINDING_NOT_CHILD_OF", chain.tip, chain.mid);
            return null;
        }

        static Extra BindExtra (RigBinding binding, EditRigDefinition.ExtraControl control, Animator animator)
        {
            Extra entry = new Extra { control = control, path = RigPaths.Extra (control.name) };
            if (animator == null) {
                entry.disabledReason = Tr ("RIG_BINDING_NO_ANIMATOR");
                return entry;
            }

            Regex regex;
            try {
                regex = new Regex (control.pattern ?? "");
            }
            catch (System.ArgumentException) {
                entry.disabledReason = Tr ("RIG_BINDING_INVALID_PATTERN");
                return entry;
            }

            // 深さ優先の順（GetComponentsInChildren の順）で、最初に当たった骨を使う
            List<Transform> matches = animator.GetComponentsInChildren<Transform> (true)
                .Where (t => t != animator.transform && !string.IsNullOrEmpty (control.pattern) && regex.IsMatch (t.name))
                .ToList ();
            if (matches.Count == 0) {
                entry.disabledReason = Tr ("RIG_BINDING_NO_MATCHING_BONE", control.pattern);
                return entry;
            }
            entry.bone = matches[0];
            if (matches.Count > 1) {
                binding.notes.Add (Tr ("RIG_BINDING_MULTIPLE_MATCHES", control.name, control.pattern, matches.Count, matches[0].name));
            }
            return entry;
        }
    }

}
