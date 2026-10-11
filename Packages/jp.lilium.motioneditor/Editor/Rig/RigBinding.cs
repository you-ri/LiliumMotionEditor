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

        public sealed class Extra : Control
        {
            public EditRigDefinition.ExtraControl control;
            public Transform bone;
        }

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
            /// <summary>
            /// ずれの向きを混ぜる相手の骨（S34。定義の blendBone がこのキャラに無い・解かない骨なら、親をたどって最初に当たる解く骨）。
            /// 混ぜない点・相手が乗る骨と同じになった点は null
            /// </summary>
            public Transform blendBone;
        }

        public readonly List<Fk> fk = new List<Fk> ();
        public readonly List<Extra> extras = new List<Extra> ();
        /// <summary>全身 IK の点（S25）</summary>
        public readonly List<Point> points = new List<Point> ();
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
            get { return fk.Cast<Control> ().Concat (extras.Cast<Control> ()).Concat (points.Cast<Control> ()); }
        }

        public Fk FindFk (HumanBodyBones bone)
        {
            return fk.FirstOrDefault (c => c.control.bone == bone);
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

            if (definition.bodyPoints != null) {
                foreach (EditRigDefinition.BodyPoint entry in definition.bodyPoints) {
                    if (entry == null || !entry.enabled || !RigPaths.IsValidName (entry.name)) continue;
                    // 乗る骨: 定義の骨がこのキャラに無い・解かない骨なら、親の骨をたどって最初に当たる解く骨（つま先の骨が無ければ足）。
                    // ただし骨の付け根そのものの点は親へ乗せない（親の付け根の点と重なるだけなので、作らない）
                    bool onOrigin = entry.onBoneOrigin;
                    if (isHuman && !HasAnchorBones (entry, animator)) continue;
                    Fk host = null;
                    for (int b = (int)entry.bone; b >= 0 && b < (int)HumanBodyBones.LastBone && host == null; b = HumanTrait.GetParentBone (b)) {
                        Fk fk = binding.FindFk ((HumanBodyBones)b);
                        if (fk != null && fk.enabled && fk.control.fullBodySolve) host = fk;
                        else if (onOrigin) break;
                    }
                    if (host == null) continue;
                    Fk blend = null;
                    if (entry.blends) {
                        for (int b = (int)entry.blendBone; b >= 0 && b < (int)HumanBodyBones.LastBone && blend == null; b = HumanTrait.GetParentBone (b)) {
                            Fk fk = binding.FindFk ((HumanBodyBones)b);
                            if (fk != null && fk.enabled && fk.control.fullBodySolve) blend = fk;
                        }
                    }
                    binding.points.Add (new Point {
                        definition = entry,
                        name = entry.name,
                        path = RigPaths.BodyPoint (entry.name),
                        bone = host.bone,
                        humanBone = host.control.bone,
                        hasRotation = entry.rotation,
                        blendBone = blend != null && blend.bone != host.bone ? blend.bone : null,
                    });
                }
            }

            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                binding.extras.Add (BindExtra (binding, control, animator));
            }
            return binding;
        }

        /// <summary>
        /// 点の置き場所の基準の骨があるか（無い点は作らない）。先の骨が無いときは、代わりの向き（fallback）があれば作る
        /// </summary>
        static bool HasAnchorBones (EditRigDefinition.BodyPoint entry, Animator animator)
        {
            if (entry.origin != HumanBodyBones.LastBone && animator.GetBoneTransform (entry.origin) == null) return false;
            return entry.toward == HumanBodyBones.LastBone || animator.GetBoneTransform (entry.toward) != null || entry.fallback != Vector3.zero;
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
