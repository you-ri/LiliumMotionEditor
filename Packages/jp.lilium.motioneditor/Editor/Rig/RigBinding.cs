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

        /// <summary>
        /// 骨を 1 本 FK で動かすコントロール（FK と追加コントロール）。扱いは同じで、骨の引き方（人型の骨か、名前のパターンか）だけが違う
        /// </summary>
        public abstract class BoneControl : Control
        {
            public Transform bone;
            /// <summary>表示の名前</summary>
            public abstract string label { get; }
            /// <summary>位置も動かすか</summary>
            public abstract bool position { get; }
            /// <summary>全身 IK の点をつかむ間もハンドルを出すか</summary>
            public abstract bool fullBodyHandle { get; }
            /// <summary>人型の骨（追加コントロールは null）</summary>
            public abstract HumanBodyBones? human { get; }
            /// <summary>全身 IK で解く骨か</summary>
            public abstract bool fullBodySolve { get; }
            /// <summary>全身 IK の硬さ</summary>
            public abstract float stiffness { get; }
            /// <summary>一方向にだけ曲がる関節か</summary>
            public abstract bool hinge { get; }
            /// <summary>蝶番の曲がる向き</summary>
            public abstract Vector3 bendDirection { get; }
            /// <summary>全身 IK の角度制限（S95。null なら制限しない）</summary>
            public abstract EditRigDefinition.JointLimit limit { get; }
            /// <summary>全身 IK の重さを、この骨の分だけにするか（S96。先の骨を足さない）</summary>
            public abstract bool ownWeightOnly { get; }
        }

        public sealed class Fk : BoneControl
        {
            public EditRigDefinition.FkControl control;

            public override string label => control.bone.ToString ();
            public override bool position => control.position;
            public override bool fullBodyHandle => control.fullBodyHandle;
            public override HumanBodyBones? human => control.bone;
            public override bool fullBodySolve => control.fullBodySolve;
            public override float stiffness => control.stiffness;
            public override bool hinge => control.hinge;
            public override Vector3 bendDirection => control.bendDirection;
            public override EditRigDefinition.JointLimit limit => control.limit;
            public override bool ownWeightOnly => control.ownWeightOnly;
        }

        public sealed class Extra : BoneControl
        {
            public EditRigDefinition.ExtraControl control;

            public override string label => control.name;
            public override bool position => control.position;
            public override bool fullBodyHandle => control.fullBodyHandle;
            public override HumanBodyBones? human => null;
            public override bool fullBodySolve => control.fullBodySolve;
            public override float stiffness => control.stiffness;
            public override bool hinge => control.hinge;
            public override Vector3 bendDirection => control.bendDirection;
            public override EditRigDefinition.JointLimit limit => control.limit;
            public override bool ownWeightOnly => control.ownWeightOnly;
        }

        /// <summary>全身 IK の点（リグ定義の点の一覧の 1 つを、このキャラの骨に割り当てたもの）</summary>
        public sealed class Point : Control
        {
            public EditRigDefinition.BodyPoint definition;
            /// <summary>点の名前（定義の名前）</summary>
            public string name;
            /// <summary>点が乗る骨（定義の骨がこのキャラに無い・解かない骨なら、親をたどって最初に当たる解く骨）</summary>
            public Transform bone;
            /// <summary>点が乗る骨の人型の名前（人型でない骨・持ち物の上の点は LastBone）</summary>
            public HumanBodyBones humanBone;
            /// <summary>向きも持つ点か</summary>
            public bool hasRotation;
            /// <summary>
            /// ずれの向きを混ぜる相手の骨（S34。定義の blendBone がこのキャラに無い・解かない骨なら、親をたどって最初に当たる解く骨）。
            /// 混ぜない点・相手が乗る骨と同じになった点は null
            /// </summary>
            public Transform blendBone;
            /// <summary>
            /// 持ち物の上の点か（S91e）。bone は表示モデルの物で、humanBone は LastBone。扱いはほかの点と同じで、
            /// 解き方だけが違う（剛体 1 つなので全身 IK のソルバーへ渡さず、HeldObjectRig が物の姿勢を決める）
            /// </summary>
            public bool onObject;
            /// <summary>持ち物の上の点の、物のローカルの場所（メートル）</summary>
            public Vector3 objectOffset;
            /// <summary>持ち物の上の点の基準の場所（物を定義の基準の場所に置いたときの、ルートから見た位置・メートル）</summary>
            public Vector3 objectRest;
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

        /// <summary>FK と追加コントロール（FK が先）</summary>
        public IEnumerable<BoneControl> boneControls
        {
            get { return fk.Cast<BoneControl> ().Concat (extras); }
        }

        internal IEnumerable<Control> controls
        {
            get { return fk.Cast<Control> ().Concat (extras.Cast<Control> ()).Concat (points.Cast<Control> ()); }
        }

        public Fk FindFk (HumanBodyBones bone)
        {
            return fk.FirstOrDefault (c => c.control.bone == bone);
        }

        /// <summary>骨の付け根の点（既定の一覧での名前は骨の名前）</summary>
        internal Point FindPoint (HumanBodyBones bone)
        {
            return FindPoint (bone.ToString ());
        }

        internal Point FindPoint (string name)
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

            // 名前で乗る点が引くので、点より先に割り当てる
            foreach (EditRigDefinition.ExtraControl control in definition.extraControls) {
                binding.extras.Add (BindExtra (binding, control, animator));
            }
            // Humanoid でないキャラは、追加コントロールが 1 つでも動かせれば使える（S93。人型の FK が無効なだけ）
            if (humanReason != null && (animator == null || !binding.extras.Any (e => e.enabled))) binding.errors.Add (humanReason);

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
                    if (entry.onControl) {
                        BindControlPoint (binding, entry);
                        continue;
                    }
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
            NoteRoots (binding);
            return binding;
        }

        /// <summary>
        /// 追加コントロールの名前で乗る点（S93）。解かないコントロールなら、Transform の親をたどって最初に当たる解くコントロールへ乗る
        /// （骨の付け根そのものの点は親へ乗せない。親の付け根の点と重なるだけなので作らない）
        /// </summary>
        static void BindControlPoint (RigBinding binding, EditRigDefinition.BodyPoint entry)
        {
            Extra extra = binding.extras.FirstOrDefault (e => e.control.name == entry.control);
            if (extra == null || !extra.enabled) return;
            BoneControl host = extra.fullBodySolve ? extra : entry.onBoneOrigin ? null : binding.FindSolved (extra.bone.parent);
            if (host == null) return;
            Extra blendExtra = entry.blends && !string.IsNullOrEmpty (entry.blendControl) ? binding.extras.FirstOrDefault (e => e.control.name == entry.blendControl) : null;
            BoneControl blend = blendExtra != null && blendExtra.enabled ? blendExtra.fullBodySolve ? blendExtra : binding.FindSolved (blendExtra.bone.parent) : null;
            binding.points.Add (new Point {
                definition = entry,
                name = entry.name,
                path = RigPaths.BodyPoint (entry.name),
                bone = host.bone,
                humanBone = HumanBodyBones.LastBone,
                hasRotation = entry.rotation,
                blendBone = blend != null && blend.bone != host.bone ? blend.bone : null,
            });
        }

        /// <summary>from から上へたどって最初に当たる、解くコントロール（無ければ null）</summary>
        BoneControl FindSolved (Transform from)
        {
            for (Transform t = from; t != null; t = t.parent) {
                BoneControl found = boneControls.FirstOrDefault (c => c.enabled && c.fullBodySolve && c.bone == t);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 解く骨の根を見る（S93）。根は位置まで解かれるので、位置を持たない根はキーに残らず、解いた姿勢とずれる。根が 2 本あると別々に固定される
        /// </summary>
        static void NoteRoots (RigBinding binding)
        {
            if (binding.points.Count == 0) return;
            List<BoneControl> solved = binding.boneControls.Where (c => c.enabled && c.fullBodySolve).ToList ();
            // 人型の骨だけの定義は今までどおり（根は腰）
            if (solved.All (c => c.human != null)) return;
            List<BoneControl> roots = solved.Where (c => binding.FindSolved (c.bone.parent) == null).ToList ();
            if (roots.Count > 1) binding.notes.Add (Tr ("RIG_BINDING_MULTIPLE_SOLVED_ROOTS", string.Join (", ", roots.Select (r => r.label))));
            foreach (BoneControl root in roots) {
                if (!root.position) binding.notes.Add (Tr ("RIG_BINDING_SOLVED_ROOT_WITHOUT_POSITION", root.label));
            }
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
