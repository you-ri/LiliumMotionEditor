using UnityEngine;
using System.Collections.Generic;
using Lilium;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの解決。コントロールの値（＝編集用クリップに保存する値）から編集用の骨の姿勢を作る（Solve）。
    /// 逆に、今の骨の姿勢からコントロールの値を作る（Capture。キーを打つ・FK と IK を切り替えるときに使う）。
    ///
    /// 値の持ち方（表現の決定 0・2）。コントロールの GameObject のローカル値がそのまま保存値:
    /// - FK: localRotation = 親の骨から見た、基準姿勢（T ポーズ）からの差 L（09-17 ユーザー判断で、ルートから見た差から変更）。
    ///   ルートから見た差を G(骨) = rootRel(骨) * inv(rootRel(基準)) とすると L(骨) = inv(G(親)) * G(骨)、解くときは G(骨) = G(親) * L(骨)。
    ///   G は骨の軸の取り方に依らないので、L もキャラをまたいで同じ意味になる。親は人型の親子（HumanTrait）をたどって
    ///   そのキャラにある最も近い骨（追加コントロールは Transform の親をたどる）。途中の骨が無いキャラでは、その骨の回転が落ちる。
    ///   親を回すと子が付いてくる（普通の FK と同じ。キーも補間も親子で効く）。
    ///   位置も動かす骨は localPosition = ルートから見た位置の基準姿勢からの差 / humanScale
    /// - IK の目標: localPosition = ルートから見た先端の位置 / humanScale、localRotation = ルートから見た先端の回転
    /// - IK のヒント: localPosition = ルートから見た肘・膝の曲がる向き（長さ 1）
    /// - FK/IK の切替: IkControl.ikWeight
    /// - 足の転がし（Reverse Foot。脚だけ）: IkControl.roll / bank / twist（度）。目標（足首）の意味は変えず、
    ///   目標の枠に固定した支点（踵・母趾球・つま先の先・左右の縁）まわりに足を回してから TwoBoneIK に渡す（ReverseFoot）
    /// - ゲームの Rig の値: 代理（RigProxies）。骨の後に、ターゲットの複製と表示モデルの Rig の重みへ入れる
    /// </summary>
    public sealed class EditingRigSolver
    {
        const float kEpsilon = 1e-6f;

        sealed class FkEntry
        {
            public Transform control;
            public Transform bone;
            public bool position;
            public Quaternion restRootRotation;
            public Vector3 restRootPosition;
            /// <summary>値の基準になる親（無ければルート）</summary>
            public FkEntry parent;
            /// <summary>人型の骨なら、その種類（追加コントロールは null）</summary>
            public HumanBodyBones? human;
            /// <summary>解いている途中の、ルートから見た差 G</summary>
            public Quaternion rootDelta;
        }

        sealed class IkEntry
        {
            public string name;
            public Transform target;
            public Transform hint;
            public IkControl weight;
            public Transform root;
            public Transform mid;
            public Transform tip;
            /// <summary>手足がまっすぐなときのヒント（ルートから見た向き）</summary>
            public Vector3 defaultHint;
            /// <summary>足の転がしの支点（無ければ null）</summary>
            public ReverseFoot foot;
            /// <summary>つま先の骨（転がしで床に残す。無ければ null）</summary>
            public Transform toes;
        }

        readonly EditingRig rig_;
        // 親が子より先に来る順（FK は上から当てる）
        readonly List<FkEntry> fk_ = new List<FkEntry> ();
        readonly List<IkEntry> ik_ = new List<IkEntry> ();

        public EditingRigSolver (EditingRig rig)
        {
            rig_ = rig;
            if (rig == null || rig.root == null) return;

            Dictionary<Transform, FkEntry> byBone = new Dictionary<Transform, FkEntry> ();
            foreach (RigBinding.Fk fk in rig.binding.fk) {
                if (fk.enabled) AddFk (byBone, rig.FindControl (fk.path), rig.GetEditingBone (fk.bone), fk.control.position, fk.control.bone);
            }
            foreach (RigBinding.Extra extra in rig.binding.extras) {
                if (extra.enabled) AddFk (byBone, rig.FindControl (extra.path), rig.GetEditingBone (extra.bone), extra.control.position, null);
            }
            foreach (Transform bone in rig.editingBones) {
                FkEntry entry;
                if (byBone.TryGetValue (bone, out entry)) fk_.Add (entry);
            }
            ResolveParents (byBone);

            foreach (RigBinding.Ik ik in rig.binding.ik) {
                if (!ik.enabled) continue;
                Transform chain = rig.FindControl (ik.path);
                IkEntry entry = new IkEntry {
                    name = ik.chain.name,
                    target = rig.FindControl (RigPaths.IkTarget (ik.chain.name)),
                    hint = rig.FindControl (RigPaths.IkHint (ik.chain.name)),
                    weight = chain != null ? chain.GetComponent<IkControl> () : null,
                    root = rig.GetEditingBone (ik.root),
                    mid = rig.GetEditingBone (ik.mid),
                    tip = rig.GetEditingBone (ik.tip),
                    defaultHint = rig.BodyToRoot (ik.chain.defaultHint).normalized,
                };
                if (ik.chain.reverseFoot && entry.root != null && entry.mid != null && entry.tip != null) {
                    entry.toes = rig.GetEditingBone (ik.chain.toes);
                    entry.foot = ReverseFoot.Create (rig, entry.mid, entry.tip, entry.toes, ik.chain);
                }
                if (entry.target != null && entry.hint != null && entry.weight != null && entry.root != null && entry.mid != null && entry.tip != null) {
                    ik_.Add (entry);
                }
            }
        }

        void AddFk (Dictionary<Transform, FkEntry> byBone, Transform control, Transform bone, bool position, HumanBodyBones? human)
        {
            if (control == null || bone == null || byBone.ContainsKey (bone)) return;
            byBone.Add (bone, new FkEntry {
                control = control,
                bone = bone,
                position = position,
                human = human,
                restRootRotation = rig_.GetRestRootRotation (bone),
                restRootPosition = rig_.GetRestRootPosition (bone),
            });
        }

        /// <summary>
        /// 値の基準にする親を決める。人型の骨は人型の親子をたどり（キャラの骨の組み方に依らない）、
        /// 追加コントロールは Transform の親をたどる。どちらも、そのキャラにコントロールがある最も近い骨
        /// </summary>
        void ResolveParents (Dictionary<Transform, FkEntry> byBone)
        {
            Dictionary<HumanBodyBones, FkEntry> byHuman = new Dictionary<HumanBodyBones, FkEntry> ();
            foreach (FkEntry entry in fk_) {
                if (entry.human.HasValue) byHuman[entry.human.Value] = entry;
            }

            foreach (FkEntry entry in fk_) {
                if (entry.human.HasValue) {
                    for (int p = HumanTrait.GetParentBone ((int)entry.human.Value); p >= 0; p = HumanTrait.GetParentBone (p)) {
                        FkEntry parent;
                        if (byHuman.TryGetValue ((HumanBodyBones)p, out parent)) {
                            entry.parent = parent;
                            break;
                        }
                    }
                }
                else {
                    for (Transform t = entry.bone.parent; t != null && t != rig_.root.transform; t = t.parent) {
                        FkEntry parent;
                        if (byBone.TryGetValue (t, out parent)) {
                            entry.parent = parent;
                            break;
                        }
                    }
                }
                // 親は子より先に解く（並びは骨の階層の順なので、親が実際の祖先なら満たされる）
                if (entry.parent != null && !entry.bone.IsChildOf (entry.parent.bone)) {
                    Debug.LogWarning ("MotionEditor: " + Tr ("EDITING_RIG_SOLVER_PARENT_NOT_ANCESTOR", entry.bone.name, entry.parent.bone.name));
                    entry.parent = null;
                }
            }
        }

        /// <summary>
        /// 値の基準にする親の骨（テスト・表示用）。ルート基準なら null
        /// </summary>
        public Transform GetFkParent (Transform editingBone)
        {
            foreach (FkEntry entry in fk_) {
                if (entry.bone == editingBone) return entry.parent != null ? entry.parent.bone : null;
            }
            return null;
        }

        public int fkCount
        {
            get { return fk_.Count; }
        }

        public int ikCount
        {
            get { return ik_.Count; }
        }

        Transform rootTransform
        {
            get { return rig_.root.transform; }
        }

        float humanScale
        {
            get { return rig_.humanScale > kEpsilon ? rig_.humanScale : 1; }
        }

        // ---- 前進（コントロール → 骨） ----

        /// <summary>
        /// コントロールの値から骨の姿勢を作る。コントロールの無い骨は今の値（ふつうは基準姿勢）のまま
        /// </summary>
        public void Solve ()
        {
            if (rig_ == null || rig_.root == null) return;

            Transform root = rootTransform;
            Quaternion rootRotation = root.rotation;
            foreach (FkEntry entry in fk_) {
                Quaternion parentDelta = entry.parent != null ? entry.parent.rootDelta : Quaternion.identity;
                entry.rootDelta = parentDelta * entry.control.localRotation;
                entry.bone.rotation = rootRotation * entry.rootDelta * entry.restRootRotation;
                if (entry.position) {
                    entry.bone.position = root.TransformPoint (entry.restRootPosition + entry.control.localPosition * humanScale);
                }
            }

            foreach (IkEntry entry in ik_) {
                float weight = Mathf.Clamp01 (entry.weight.ikWeight);
                if (weight <= 0) continue;
                SolveIk (entry, weight);
            }

            if (rig_.rigProxies != null) rig_.rigProxies.Apply ();
        }

        void SolveIk (IkEntry entry, float weight)
        {
            Transform root = rootTransform;
            Quaternion fkRoot = entry.root.rotation;
            Quaternion fkMid = entry.mid.rotation;
            Quaternion fkTip = entry.tip.rotation;

            Vector3 targetPosition = entry.target.localPosition * humanScale;
            Quaternion targetRotation = entry.target.localRotation;

            // 足の転がし。目標の枠の中で足を回し、足首の行き先を変える。つま先は母趾球の分だけ戻して床に残す
            bool rolled = false;
            Quaternion toesCorrection = Quaternion.identity;
            if (entry.foot != null) {
                ReverseFoot.Pose pose;
                if (entry.foot.Evaluate (entry.weight.roll, entry.weight.bank, entry.weight.twist, out pose)) {
                    rolled = true;
                    targetPosition += targetRotation * pose.position;
                    Quaternion frame = root.rotation * targetRotation;
                    toesCorrection = frame * (pose.rotation * Quaternion.Inverse (pose.ballRotation) * Quaternion.Inverse (pose.rotation)) * Quaternion.Inverse (frame);
                    targetRotation = targetRotation * pose.rotation;
                }
            }
            Quaternion fkToes = entry.toes != null ? entry.toes.rotation : Quaternion.identity;

            Vector3 target = root.TransformPoint (targetPosition);
            Vector3 hint = root.TransformDirection (entry.hint.localPosition);
            Quaternion tipRotation = root.rotation * targetRotation;
            SolveTwoBone (entry.root, entry.mid, entry.tip, target, hint, root.TransformDirection (entry.defaultHint));
            entry.tip.rotation = tipRotation;
            bool toesMoved = rolled && entry.toes != null;
            if (toesMoved) entry.toes.rotation = toesCorrection * entry.toes.rotation;

            if (weight >= 1) return;
            Quaternion ikRoot = entry.root.rotation;
            Quaternion ikMid = entry.mid.rotation;
            Quaternion ikTip = entry.tip.rotation;
            Quaternion ikToes = toesMoved ? entry.toes.rotation : Quaternion.identity;
            entry.root.rotation = Quaternion.Slerp (fkRoot, ikRoot, weight);
            entry.mid.rotation = Quaternion.Slerp (fkMid, ikMid, weight);
            entry.tip.rotation = Quaternion.Slerp (fkTip, ikTip, weight);
            if (toesMoved) entry.toes.rotation = Quaternion.Slerp (fkToes, ikToes, weight);
        }

        /// <summary>
        /// 2 本の骨を、先端が target に届くように回す（届かなければまっすぐ伸ばして向ける）。
        /// 曲がる向きは hint（root→target の線に垂直な成分）。hint が線と重なるときは fallbackHint、それも重なるなら今の曲がり
        /// </summary>
        public static void SolveTwoBone (Transform root, Transform mid, Transform tip, Vector3 target, Vector3 hint, Vector3 fallbackHint)
        {
            Vector3 a = root.position;
            Vector3 b = mid.position;
            Vector3 c = tip.position;
            float upper = Vector3.Distance (a, b);
            float lower = Vector3.Distance (b, c);
            if (upper < kEpsilon || lower < kEpsilon) return;

            Vector3 toTarget = target - a;
            float distance = toTarget.magnitude;
            if (distance < kEpsilon) return;
            Vector3 axis = toTarget / distance;
            distance = Mathf.Clamp (distance, Mathf.Abs (upper - lower) + kEpsilon, upper + lower);

            Vector3 bend = Perpendicular (hint, axis);
            if (bend == Vector3.zero) bend = Perpendicular (fallbackHint, axis);
            if (bend == Vector3.zero) bend = Perpendicular (b - a, axis);
            if (bend == Vector3.zero) bend = Perpendicular (Vector3.up, axis);
            if (bend == Vector3.zero) bend = Perpendicular (Vector3.forward, axis);

            // 余弦定理で付け根の角度を出して、肘・膝の行き先を決める
            float cos = Mathf.Clamp ((upper * upper + distance * distance - lower * lower) / (2 * upper * distance), -1, 1);
            float sin = Mathf.Sqrt (Mathf.Max (0, 1 - cos * cos));
            Vector3 newMid = a + (axis * cos + bend * sin) * upper;
            Vector3 newTip = a + axis * distance;

            root.rotation = Quaternion.FromToRotation (b - a, newMid - a) * root.rotation;
            // 付け根を回したので先端の位置が変わっている
            Vector3 midPosition = mid.position;
            mid.rotation = Quaternion.FromToRotation (tip.position - midPosition, newTip - midPosition) * mid.rotation;
        }

        /// <summary>
        /// v の axis に垂直な成分（長さ 1）。ほぼ 0 なら zero
        /// </summary>
        static Vector3 Perpendicular (Vector3 v, Vector3 axis)
        {
            Vector3 p = v - axis * Vector3.Dot (v, axis);
            float length = p.magnitude;
            return length > 1e-4f * Mathf.Max (1, v.magnitude) ? p / length : Vector3.zero;
        }

        // ---- 逆（骨 → コントロール） ----

        /// <summary>
        /// 今の骨の姿勢をコントロールの値にする（FK の差分・腰の位置・IK の目標とヒント）。IK の重みは変えない。
        /// Solve と組にすると姿勢が戻る
        /// </summary>
        public void Capture ()
        {
            if (rig_ == null || rig_.root == null) return;

            foreach (FkEntry entry in fk_) {
                CaptureFk (entry);
            }
            foreach (IkEntry entry in ik_) {
                CaptureIk (entry);
            }
        }

        /// <summary>
        /// 1 本の骨のぶんだけ（FK の骨を直接回したあと）
        /// </summary>
        public bool CaptureFk (Transform editingBone)
        {
            foreach (FkEntry entry in fk_) {
                if (entry.bone != editingBone) continue;
                CaptureFk (entry);
                return true;
            }
            return false;
        }

        /// <summary>
        /// IK の組のぶんだけ（名前は定義の組の名前）
        /// </summary>
        public bool CaptureIk (string chainName)
        {
            foreach (IkEntry entry in ik_) {
                if (entry.name != chainName) continue;
                CaptureIk (entry);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 今の骨の姿勢から、ルートから見た差 G を求める
        /// </summary>
        Quaternion CurrentRootDelta (FkEntry entry)
        {
            return Quaternion.Inverse (rootTransform.rotation) * entry.bone.rotation * Quaternion.Inverse (entry.restRootRotation);
        }

        /// <summary>
        /// 親の今の姿勢から見た差にする。IK が効いている親でも、子は今の見た目の親に付いて動く（解くときも IK が親を回すと子が付いてくる）ので、
        /// 今の姿勢を基準にすれば往復で戻る
        /// </summary>
        void CaptureFk (FkEntry entry)
        {
            Transform root = rootTransform;
            Quaternion parentDelta = entry.parent != null ? CurrentRootDelta (entry.parent) : Quaternion.identity;
            entry.control.localRotation = Normalize (Quaternion.Inverse (parentDelta) * CurrentRootDelta (entry));
            if (entry.position) {
                entry.control.localPosition = (root.InverseTransformPoint (entry.bone.position) - entry.restRootPosition) / humanScale;
            }
        }

        /// <summary>
        /// 目標とヒントを今の見た目（足首）にし、足の転がしの角度を 0 にする。転がしでつま先を戻していたなら、その見た目をつま先の FK に写す
        /// </summary>
        void CaptureIk (IkEntry entry)
        {
            ClearFootAngles (entry);
            Transform root = rootTransform;
            Vector3 a = root.InverseTransformPoint (entry.root.position);
            Vector3 b = root.InverseTransformPoint (entry.mid.position);
            Vector3 c = root.InverseTransformPoint (entry.tip.position);

            entry.target.localPosition = c / humanScale;
            entry.target.localRotation = Normalize (Quaternion.Inverse (root.rotation) * entry.tip.rotation);

            entry.hint.localPosition = PoseHint (entry);
        }

        /// <summary>
        /// 今の骨の曲がりから求めたヒント（ルートから見た向き）。まっすぐなときは既定の向き（肘は後ろ・膝は前）を線に垂直にしたもの
        /// </summary>
        Vector3 PoseHint (IkEntry entry)
        {
            Transform root = rootTransform;
            Vector3 a = root.InverseTransformPoint (entry.root.position);
            Vector3 b = root.InverseTransformPoint (entry.mid.position);
            Vector3 c = root.InverseTransformPoint (entry.tip.position);
            return BendHint (a, b, c, entry.defaultHint);
        }

        /// <summary>
        /// 根・中・先の位置（ルートから見た）から求めた曲がる向き
        /// </summary>
        static Vector3 BendHint (Vector3 a, Vector3 b, Vector3 c, Vector3 defaultHint)
        {
            Vector3 axis = c - a;
            if (axis.sqrMagnitude <= kEpsilon * kEpsilon) return defaultHint;
            Vector3 bend = Perpendicular (b - a, axis.normalized);
            if (bend == Vector3.zero) bend = Perpendicular (defaultHint, axis.normalized);
            return bend != Vector3.zero ? bend : defaultHint;
        }

        /// <summary>
        /// 今の骨の曲がりから求めたヒントの点（値は書き換えない。IK が切れている間の表示用）
        /// </summary>
        public Vector3 GetPoseHintWorld (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null) return Vector3.zero;
            return entry.mid.position + rootTransform.TransformDirection (PoseHint (entry)) * HintDistance (entry);
        }

        static Quaternion Normalize (Quaternion q)
        {
            // 同じ回転の 2 通りの表し方のうち w ≥ 0 に揃える（キーの間で符号が飛ぶと補間が遠回りになる）
            q = Quaternion.Normalize (q);
            return q.w < 0 ? new Quaternion (-q.x, -q.y, -q.z, -q.w) : q;
        }

        // ---- 表示・つかむ側が使う値 ----

        /// <summary>
        /// IK の目標のワールド位置と向き（目標のコントロールは正規化した値を持つので、GameObject の位置そのものではない）
        /// </summary>
        public bool TryGetIkTargetWorld (string chainName, out Vector3 position, out Quaternion rotation)
        {
            foreach (IkEntry entry in ik_) {
                if (entry.name != chainName) continue;
                position = rootTransform.TransformPoint (entry.target.localPosition * humanScale);
                rotation = rootTransform.rotation * entry.target.localRotation;
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        public bool SetIkTargetWorld (string chainName, Vector3 position, Quaternion rotation)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null) return false;
            entry.target.localPosition = rootTransform.InverseTransformPoint (position) / humanScale;
            entry.target.localRotation = Normalize (Quaternion.Inverse (rootTransform.rotation) * rotation);
            return true;
        }

        IkEntry FindIk (string chainName)
        {
            foreach (IkEntry entry in ik_) {
                if (entry.name == chainName) return entry;
            }
            return null;
        }

        public float GetIkWeight (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            return entry != null ? Mathf.Clamp01 (entry.weight.ikWeight) : 0;
        }

        public void SetIkWeight (string chainName, float weight)
        {
            IkEntry entry = FindIk (chainName);
            if (entry != null) entry.weight.ikWeight = Mathf.Clamp01 (weight);
        }

        /// <summary>
        /// IK の組の骨（付け根・中間・先端）
        /// </summary>
        public bool TryGetIkBones (string chainName, out Transform root, out Transform mid, out Transform tip)
        {
            IkEntry entry = FindIk (chainName);
            root = entry != null ? entry.root : null;
            mid = entry != null ? entry.mid : null;
            tip = entry != null ? entry.tip : null;
            return entry != null;
        }

        /// <summary>
        /// この骨を受け持つ IK の組の名前（無ければ null）
        /// </summary>
        public string FindIkChainOf (Transform editingBone)
        {
            foreach (IkEntry entry in ik_) {
                if (entry.root == editingBone || entry.mid == editingBone || entry.tip == editingBone) return entry.name;
            }
            return null;
        }

        /// <summary>
        /// ヒントの点（肘・膝から、曲がる向きへ手足の長さの半分だけ離した所）
        /// </summary>
        public Vector3 GetIkHintWorld (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null) return Vector3.zero;
            return entry.mid.position + rootTransform.TransformDirection (entry.hint.localPosition.normalized) * HintDistance (entry);
        }

        /// <summary>
        /// ヒントの点を動かす（肘・膝から見た向きだけを持つ）
        /// </summary>
        public void SetIkHintWorld (string chainName, Vector3 point)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null) return;
            Vector3 direction = rootTransform.InverseTransformDirection (point - entry.mid.position);
            if (direction.sqrMagnitude > kEpsilon * kEpsilon) entry.hint.localPosition = direction.normalized;
        }

        /// <summary>
        /// 足の転がしの角度を 0 にする。0 でなかったなら、今のつま先の見た目を FK の値に写す（転がしの戻しが消えても見た目が変わらない）
        /// </summary>
        void ClearFootAngles (IkEntry entry)
        {
            if (entry.foot == null) return;
            IkControl control = entry.weight;
            if (control.roll == 0 && control.bank == 0 && control.twist == 0) return;
            if (entry.toes != null) {
                FkEntry toes = FindFk (entry.toes);
                if (toes != null) CaptureFk (toes);
            }
            control.roll = 0;
            control.bank = 0;
            control.twist = 0;
        }

        /// <summary>足の転がしを持つ組か</summary>
        public bool HasReverseFoot (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            return entry != null && entry.foot != null;
        }

        /// <summary>足の転がしの角度（度。x = roll・y = bank・z = twist）</summary>
        public bool TryGetFootAngles (string chainName, out Vector3 angles)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null || entry.foot == null) {
                angles = Vector3.zero;
                return false;
            }
            angles = new Vector3 (entry.weight.roll, entry.weight.bank, entry.weight.twist);
            return true;
        }

        public void SetFootAngles (string chainName, Vector3 angles)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null || entry.foot == null) return;
            entry.weight.roll = angles.x;
            entry.weight.bank = angles.y;
            entry.weight.twist = angles.z;
        }

        /// <summary>
        /// 足の転がしの支点のワールド位置（並びは ReverseFoot.Pivot）。目標の枠（転がす前）に付いて動く
        /// </summary>
        public bool TryGetFootPivotsWorld (string chainName, Vector3[] result)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null || entry.foot == null || result == null || result.Length < ReverseFoot.kPivotCount) return false;
            Transform root = rootTransform;
            Vector3 position = entry.target.localPosition * humanScale;
            Quaternion rotation = entry.target.localRotation;
            for (int i = 0; i < ReverseFoot.kPivotCount; i++) {
                result[i] = root.TransformPoint (position + rotation * entry.foot.GetPivot ((ReverseFoot.Pivot)i));
            }
            return true;
        }

        /// <summary>
        /// 足の枠（足首の位置と向き。ワールド）を与えて、足の転がしの支点と足元の面の向きをワールドで求める（足元のコントローラーを描く）
        /// </summary>
        public bool TryGetFootPivotsWorld (string chainName, Vector3 anklePosition, Quaternion ankleRotation, Vector3[] result, out Vector3 up, out Vector3 forward)
        {
            IkEntry entry = FindIk (chainName);
            up = Vector3.up;
            forward = Vector3.forward;
            if (entry == null || entry.foot == null || result == null || result.Length < ReverseFoot.kPivotCount) return false;
            for (int i = 0; i < ReverseFoot.kPivotCount; i++) {
                result[i] = anklePosition + ankleRotation * entry.foot.GetPivot ((ReverseFoot.Pivot)i);
            }
            up = (ankleRotation * entry.foot.up).normalized;
            forward = (ankleRotation * entry.foot.forward).normalized;
            return true;
        }

        static float HintDistance (IkEntry entry)
        {
            return (Vector3.Distance (entry.root.position, entry.mid.position) + Vector3.Distance (entry.mid.position, entry.tip.position)) * 0.5f;
        }

        /// <summary>
        /// FK へ切り替える。今の見た目（IK の結果）を FK の値に写してから IK を切るので、姿勢は飛ばない
        /// </summary>
        public void SwitchToFk (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null || entry.weight.ikWeight <= 0) return;
            foreach (FkEntry fk in fk_) {
                if (fk.bone == entry.root || fk.bone == entry.mid || fk.bone == entry.tip) CaptureFk (fk);
            }
            ClearFootAngles (entry);
            entry.weight.ikWeight = 0;
        }

        /// <summary>
        /// IK へ切り替える。今の見た目（FK の結果）を目標とヒントに写してから IK を入れるので、姿勢は飛ばない
        /// </summary>
        public void SwitchToIk (string chainName)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null || entry.weight.ikWeight >= 1) return;
            CaptureIk (entry);
            entry.weight.ikWeight = 1;
        }

        /// <summary>
        /// 使っていない側の値を、今の見た目に合わせる（FK の間は IK の目標とヒント、IK の間は手足 3 本の FK の差分）。
        /// 切り替えたときにしか合わせないと、使っていない側の値は最後に使ったときのまま（一度も使っていなければ基準姿勢）に
        /// なるので、重みを途中で動かすと姿勢がそこへ引っ張られる。解くたびに合わせておけば、いつ切り替えても飛ばない。
        /// 混ざっている間（0 &lt; 重み &lt; 1）は、どちらの値も今の見た目を作っている元なので触らない。
        /// 足の転がしの角度は IK の側の値なので、FK の間は 0（残すと IK に戻したときに足が飛ぶ）。
        /// IK の間もつま先の FK は写さない（転がしの戻しはつま先の FK に掛け足すので、写すと二重になる）
        /// </summary>
        public void SyncIkFk ()
        {
            foreach (IkEntry entry in ik_) {
                float weight = Mathf.Clamp01 (entry.weight.ikWeight);
                if (weight <= 0) {
                    CaptureIk (entry);
                    continue;
                }
                if (weight < 1) continue;
                foreach (FkEntry fk in fk_) {
                    if (fk.bone == entry.root || fk.bone == entry.mid || fk.bone == entry.tip) CaptureFk (fk);
                }
            }
        }

        // ---- 数値欄・左右反転（S9） ----

        FkEntry FindFk (Transform editingBone)
        {
            foreach (FkEntry entry in fk_) {
                if (entry.bone == editingBone) return entry;
            }
            return null;
        }

        /// <summary>
        /// 骨の今の見た目を FK の値にしたもの（コントロールは書き換えない）。IK が動かしている骨でも、見えている姿勢の値になる
        /// </summary>
        public bool TryGetCurrentFkValue (Transform editingBone, out Quaternion rotation, out Vector3 position)
        {
            FkEntry entry = FindFk (editingBone);
            if (entry == null) {
                rotation = Quaternion.identity;
                position = Vector3.zero;
                return false;
            }
            Quaternion parentDelta = entry.parent != null ? CurrentRootDelta (entry.parent) : Quaternion.identity;
            rotation = Normalize (Quaternion.Inverse (parentDelta) * CurrentRootDelta (entry));
            position = entry.position ? (rootTransform.InverseTransformPoint (entry.bone.position) - entry.restRootPosition) / humanScale : Vector3.zero;
            return true;
        }

        /// <summary>
        /// IK の目標とヒントの基準の値（基準姿勢の手足の先と曲がり）。値の持ち方はコントロールと同じ（位置は humanScale で割った値）
        /// </summary>
        public bool TryGetIkRestValues (string chainName, out Vector3 target, out Quaternion rotation, out Vector3 hint)
        {
            IkEntry entry = FindIk (chainName);
            if (entry == null) {
                target = Vector3.zero;
                rotation = Quaternion.identity;
                hint = Vector3.zero;
                return false;
            }
            Vector3 a = rig_.GetRestRootPosition (entry.root);
            Vector3 b = rig_.GetRestRootPosition (entry.mid);
            Vector3 c = rig_.GetRestRootPosition (entry.tip);
            target = c / humanScale;
            rotation = Normalize (rig_.GetRestRootRotation (entry.tip));
            hint = BendHint (a, b, c, entry.defaultHint);
            return true;
        }

        /// <summary>
        /// 左右反転の面の法線（ルートから見た体の右）と、面の位置（その向きで測った体の中心）
        /// </summary>
        void GetMirrorPlane (out Vector3 normal, out float center)
        {
            rig_.GetMirrorPlane (out normal, out center);
        }

        /// <summary>向き（差分）を左右反転する（面の法線 n で鏡に映した回転）</summary>
        public static Quaternion MirrorRotation (Quaternion q, Vector3 n)
        {
            Vector3 v = new Vector3 (q.x, q.y, q.z);
            v = 2 * Vector3.Dot (v, n) * n - v;
            return Normalize (new Quaternion (v.x, v.y, v.z, q.w));
        }

        /// <summary>向きのベクトル・差分の位置を左右反転する</summary>
        public static Vector3 MirrorVector (Vector3 v, Vector3 n)
        {
            return v - 2 * Vector3.Dot (v, n) * n;
        }

        /// <summary>
        /// source の骨の今の見た目（FK の値）を左右反転して、target の骨の FK の値にする。
        /// source と target が同じ（体の中心の骨）なら、その骨を反転する。
        /// 値は親から見た基準姿勢からの差（ルートの軸で表した回転）なので、反転は親子をまたいで一貫する
        /// </summary>
        public bool MirrorFk (Transform sourceBone, Transform targetBone)
        {
            FkEntry source = FindFk (sourceBone);
            FkEntry target = FindFk (targetBone);
            if (source == null || target == null) return false;

            Quaternion rotation;
            Vector3 position;
            TryGetCurrentFkValue (sourceBone, out rotation, out position);
            string chain = FindIkChainOf (targetBone);
            if (chain != null) SwitchToFk (chain);

            Vector3 normal;
            float center;
            GetMirrorPlane (out normal, out center);
            target.control.localRotation = MirrorRotation (rotation, normal);
            if (target.position && source.position) target.control.localPosition = MirrorVector (position, normal);
            return true;
        }

        /// <summary>
        /// source の手足の IK を左右反転して target へ入れる。source が FK で動いているなら、骨の FK の値を反転して target も FK にする
        /// </summary>
        public bool MirrorIk (string sourceChain, string targetChain)
        {
            IkEntry source = FindIk (sourceChain);
            IkEntry target = FindIk (targetChain);
            if (source == null || target == null) return false;

            float weight = Mathf.Clamp01 (source.weight.ikWeight);
            if (weight <= 0) {
                SwitchToFk (targetChain);
                MirrorFk (source.root, target.root);
                MirrorFk (source.mid, target.mid);
                MirrorFk (source.tip, target.tip);
                return true;
            }

            Vector3 normal;
            float center;
            GetMirrorPlane (out normal, out center);
            Vector3 position = source.target.localPosition * humanScale;
            position -= 2 * (Vector3.Dot (position, normal) - center) * normal;
            // 目標の向きはルートから見た手・足の向き。基準姿勢からの差にして反転し、相手の基準姿勢に掛ける
            Quaternion delta = source.target.localRotation * Quaternion.Inverse (rig_.GetRestRootRotation (source.tip));
            Quaternion rotation = MirrorRotation (delta, normal) * rig_.GetRestRootRotation (target.tip);
            Vector3 hint = MirrorVector (source.hint.localPosition, normal);

            SwitchToIk (targetChain);
            target.target.localPosition = position / humanScale;
            target.target.localRotation = Normalize (rotation);
            target.hint.localPosition = hint;
            target.weight.ikWeight = weight;
            // 転がしの軸は体の軸なので、前後（roll）はそのまま、左右の傾きとひねりは向きが逆になる
            if (source.foot != null && target.foot != null) {
                target.weight.roll = source.weight.roll;
                target.weight.bank = -source.weight.bank;
                target.weight.twist = -source.weight.twist;
            }
            return true;
        }
    }

}
