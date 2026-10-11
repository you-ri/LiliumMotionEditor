using UnityEngine;
using System.Collections.Generic;
using static Lilium.MotionEditorLocalization;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの解決。コントロールの値（＝編集用クリップに保存する値）から編集用の骨の姿勢を作る（Solve）。
    /// 逆に、今の骨の姿勢からコントロールの値を作る（Capture。キーを打つ・読み込んだ姿勢を値にするときに使う）。
    ///
    /// 値の持ち方（表現の決定 0・2）。コントロールの GameObject のローカル値がそのまま保存値:
    /// - FK: localRotation = 親の骨から見た、基準姿勢（T ポーズ）からの差 L（09-17 ユーザー判断で、ルートから見た差から変更）。
    ///   ルートから見た差を G(骨) = rootRel(骨) * inv(rootRel(基準)) とすると L(骨) = inv(G(親)) * G(骨)、解くときは G(骨) = G(親) * L(骨)。
    ///   G は骨の軸の取り方に依らないので、L もキャラをまたいで同じ意味になる。親は人型の親子（HumanTrait）をたどって
    ///   そのキャラにある最も近い骨（追加コントロールは Transform の親をたどる）。途中の骨が無いキャラでは、その骨の回転が落ちる。
    ///   親を回すと子が付いてくる（普通の FK と同じ。キーも補間も親子で効く）。
    ///   位置も動かす骨は localPosition = ルートから見た位置の基準姿勢からの差 / humanScale
    /// - 全身 IK の点: FullBodyRig が持つ（この解決は FK の値だけを骨へ当てる）
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

        readonly EditingRig rig_;
        // 親が子より先に来る順（FK は上から当てる）
        readonly List<FkEntry> fk_ = new List<FkEntry> ();

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

            if (rig_.rigProxies != null) rig_.rigProxies.Apply ();
        }

        // ---- 逆（骨 → コントロール） ----

        /// <summary>
        /// 今の骨の姿勢をコントロールの値にする（FK の差分・腰の位置）。
        /// Solve と組にすると姿勢が戻る
        /// </summary>
        public void Capture ()
        {
            if (rig_ == null || rig_.root == null) return;

            foreach (FkEntry entry in fk_) {
                CaptureFk (entry);
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
        /// 今の骨の姿勢から、ルートから見た差 G を求める
        /// </summary>
        Quaternion CurrentRootDelta (FkEntry entry)
        {
            return Quaternion.Inverse (rootTransform.rotation) * entry.bone.rotation * Quaternion.Inverse (entry.restRootRotation);
        }

        /// <summary>
        /// 親の今の姿勢から見た差にする。全身 IK が親を動かしていても、子は今の見た目の親に付いて動くので、
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

        /// <summary>長さ 1 にして、w ≥ 0 の側にそろえる（全身 IK の FullBodyRig も使う）</summary>
        internal static Quaternion Normalize (Quaternion q)
        {
            // 同じ回転の 2 通りの表し方のうち w ≥ 0 に揃える（キーの間で符号が飛ぶと補間が遠回りになる）
            q = Quaternion.Normalize (q);
            return q.w < 0 ? new Quaternion (-q.x, -q.y, -q.z, -q.w) : q;
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
        /// 骨の今の見た目を FK の値にしたもの（コントロールは書き換えない）。全身 IK が動かしている骨でも、見えている姿勢の値になる
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

            Vector3 normal;
            float center;
            GetMirrorPlane (out normal, out center);
            target.control.localRotation = MirrorRotation (rotation, normal);
            if (target.position && source.position) target.control.localPosition = MirrorVector (position, normal);
            return true;
        }
    }

}
