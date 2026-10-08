using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Lilium
{

    /// <summary>
    /// Picker（骨の一覧とメッシュクリックでの選択）が使うところ。一覧のパネルは PickerOverlay
    /// </summary>
    public partial class PreviewWindow
    {
        /// <summary>
        /// 一覧の 1 行
        /// </summary>
        public sealed class PickerEntry
        {
            public PoseTarget target;
            public string label;
            /// <summary>骨のつながりの深さ（一覧の字下げ）</summary>
            public int depth;
        }

        List<PickerEntry> pickerEntries_ = new List<PickerEntry> ();
        /// <summary>
        /// 骨の Transform → それを受け持つ対象。クリックした場所の骨から親をたどって引く
        /// </summary>
        readonly Dictionary<Transform, PoseTarget> pickTransforms_ = new Dictionary<Transform, PoseTarget> ();

        /// <summary>
        /// 一覧の中身。キャラを読み込み直すと別の一覧になる（オーバーレイはこれで作り直しを判断する）
        /// </summary>
        public IList<PickerEntry> pickerEntries
        {
            get { return pickerEntries_; }
        }

        public int pickerSelectedIndex
        {
            get {
                PoseTarget target = selectedTarget;
                if (target == null) return -1;
                return pickerEntries_.FindIndex (e => e.target == target);
            }
        }

        /// <summary>
        /// 骨のつながりの順（親から子へ）に一覧を作る。各対象は anchor の骨の位置に並ぶ。キャラを読み込んだ後に呼ぶ
        /// </summary>
        void BuildPicker ()
        {
            pickerEntries_ = new List<PickerEntry> ();
            pickTransforms_.Clear ();
            if (stage_ == null || stage_.animator == null) return;

            List<Transform> transforms = new List<Transform> ();
            Dictionary<Transform, List<PoseTarget>> byAnchor = new Dictionary<Transform, List<PoseTarget>> ();
            foreach (PoseTarget target in targets_) {
                transforms.Clear ();
                target.CollectPickTransforms (transforms);
                foreach (Transform t in transforms) {
                    if (t != null && !pickTransforms_.ContainsKey (t)) pickTransforms_.Add (t, target);
                }

                Transform anchor = target.anchor;
                if (anchor == null) continue;
                List<PoseTarget> list;
                if (!byAnchor.TryGetValue (anchor, out list)) byAnchor.Add (anchor, list = new List<PoseTarget> ());
                list.Add (target);
            }

            HashSet<PoseTarget> added = new HashSet<PoseTarget> ();
            AddPickerEntries (stage_.animator.transform, 0, byAnchor, added);
            // 骨の下に無い対象（anchor が無い・キャラの外）は末尾に並べる
            foreach (PoseTarget target in targets_) {
                if (added.Add (target)) pickerEntries_.Add (new PickerEntry { target = target, label = target.label, depth = 0 });
            }
        }

        void AddPickerEntries (Transform t, int depth, Dictionary<Transform, List<PoseTarget>> byAnchor, HashSet<PoseTarget> added)
        {
            int childDepth = depth;
            List<PoseTarget> targets;
            if (byAnchor.TryGetValue (t, out targets)) {
                foreach (PoseTarget target in targets) {
                    if (!added.Add (target)) continue;
                    pickerEntries_.Add (new PickerEntry { target = target, label = target.label, depth = depth });
                    childDepth = depth + 1;
                }
            }
            foreach (Transform child in t) {
                AddPickerEntries (child, childDepth, byAnchor, added);
            }
        }

        public void SelectPickerIndex (int index)
        {
            if (index < 0 || index >= pickerEntries_.Count) return;

            PoseTarget target = pickerEntries_[index].target;
            Select (target != null ? target.gameObject : null);
            RepaintView ();
        }

        /// <summary>
        /// 一覧の中で選択を動かす（↑↓）。何も選んでいなければ先頭から
        /// </summary>
        public void MovePickerSelection (int offset)
        {
            if (pickerEntries_.Count == 0) return;

            int index = pickerSelectedIndex;
            SelectPickerIndex (index < 0 ? 0 : Mathf.Clamp (index + offset, 0, pickerEntries_.Count - 1));
        }

        /// <summary>
        /// 一覧の先頭・末尾へ（Home / End）
        /// </summary>
        public void SelectPickerEdge (bool first)
        {
            if (pickerEntries_.Count == 0) return;

            SelectPickerIndex (first ? 0 : pickerEntries_.Count - 1);
        }

        /// <summary>一覧の複数行をまとめて選ぶ（Ctrl / Shift クリック）</summary>
        public void SelectPickerIndices (IEnumerable<int> indices)
        {
            if (indices == null) return;

            SelectObjects (indices
                .Where (i => i >= 0 && i < pickerEntries_.Count)
                .Select (i => pickerEntries_[i].target)
                .Where (t => t != null)
                .Select (t => t.gameObject));
        }

        /// <summary>一覧に出ている物（骨・IK・その他のコントロール）を全部選ぶ</summary>
        public void SelectPickerAll ()
        {
            SelectObjects (pickerEntries_.Select (e => e.target).Where (t => t != null).Select (t => t.gameObject));
        }

        /// <summary>
        /// 右半身（right = true）か左半身をまとめて選ぶ。
        /// 見分けるのはコントロールの名前の頭で、Humanoid の骨（LeftUpperArm など）と IK の組（LeftArm など）が当たる。
        /// 背骨・頭・腰のような中心の骨と、名前で左右の付かないコントロール（武器・尻尾）はどちらにも入れない
        /// </summary>
        public void SelectPickerSide (bool right)
        {
            string side = right ? "Right" : "Left";
            SelectObjects (pickerEntries_
                .Select (e => e.target)
                .Where (t => t != null && t.name.StartsWith (side, System.StringComparison.Ordinal))
                .Select (t => t.gameObject));
        }

        /// <summary>Humanoid の指の骨の名前（LeftThumbProximal などの真ん中）</summary>
        static readonly string[] kFingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };

        /// <summary>
        /// 片手の指をまとめて選ぶ（親指〜小指の 3 本ずつ、そのキャラにある分だけ）。
        /// 見分けるのは SelectPickerSide と同じくコントロールの名前で、Humanoid の指は「左右 ＋ 指の名前 ＋ 位置」で付く
        /// </summary>
        public void SelectPickerFingers (bool right)
        {
            string side = right ? "Right" : "Left";
            SelectObjects (pickerEntries_
                .Select (e => e.target)
                .Where (t => t != null && IsFingerName (t.name, side))
                .Select (t => t.gameObject));
        }

        static bool IsFingerName (string name, string side)
        {
            if (!name.StartsWith (side, System.StringComparison.Ordinal)) return false;
            foreach (string finger in kFingerNames) {
                if (name.IndexOf (finger, side.Length, System.StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>腰と脚の骨。腰（Hips）は体の付け根なので下半身に入れる</summary>
        static readonly HashSet<HumanBodyBones> kLowerBodyBones = new HashSet<HumanBodyBones> {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
        };

        /// <summary>
        /// 上半身（背骨から上と腕・指、腕の IK）か下半身（腰と脚、脚の IK）をまとめて選ぶ。
        /// FK のコントロールは名前が `HumanBodyBones` そのものなので、名前を enum に戻して見分ける。
        /// IK の組（LeftArm / LeftLeg など）は enum に無いので名前の終わりで見る。
        /// 左右の付かない追加のコントロール（武器・尻尾）はどちらにも入れない
        /// </summary>
        public void SelectPickerBody (bool upper)
        {
            SelectObjects (pickerEntries_
                .Select (e => e.target)
                .Where (t => t != null && IsBodyName (t.name, upper))
                .Select (t => t.gameObject));
        }

        static bool IsBodyName (string name, bool upper)
        {
            HumanBodyBones bone;
            if (System.Enum.TryParse (name, out bone)) return kLowerBodyBones.Contains (bone) != upper;
            return name.EndsWith (upper ? "Arm" : "Leg", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 表示域のクリック。メッシュに当たればその場所を一番動かしている骨、当たらなければ選択を外す（Shift なら外さない）。
        /// Shift・Ctrl はハンドルを押したときと同じ（SelectWithModifiers）
        /// </summary>
        /// <param name="point">表示域の中の位置</param>
        void SelectByMeshClick (Vector2 point, Rect rect, EventModifiers modifiers)
        {
            if (stage_ == null || rect.width < 1 || rect.height < 1) {
                SelectPicked (null, modifiers);
                return;
            }

            SelectPicked (PickTargetAt (point, rect, false), modifiers);
        }

        /// <summary>
        /// 表示域の point のメッシュに当たる物（押したら選ぶ物）。cached なら前に焼いたメッシュを使い回す（マウスを動かすたびに探すとき）
        /// </summary>
        PoseTarget PickTargetAt (Vector2 point, Rect rect, bool cached)
        {
            if (stage_ == null || rect.width < 1 || rect.height < 1) return null;
            Camera camera = stage_.camera;
            camera_.Apply (camera);
            camera.aspect = rect.width / rect.height;
            // ビューポート座標は左下が原点
            Ray ray = camera.ViewportPointToRay (new Vector3 (point.x / rect.width, 1 - point.y / rect.height, 0));

            Transform bone = cached ? stage_.PickBoneCached (ray) : stage_.PickBone (ray);
            PoseTarget target = FindPickTarget (bone);
            // キーの自動追加が切で、今のフレームにキーの無い物は選ばない（ハンドルも薄く出してつかめなくしている）
            if (target != null && !CanEditTarget (target)) target = null;
            return target;
        }

        /// <summary>メッシュでマウスの下にある物（押したら選ぶ物）。ハンドルを、マウスが乗ったのと同じに強調する</summary>
        GameObject meshHover_;

        /// <summary>
        /// マウスを動かしたとき、メッシュでマウスの下にある物を探し直す。active でなければ（表示域の外・ハンドルの上・つかんでいる間）外す。
        /// 変わったら true
        /// </summary>
        bool UpdateMeshHover (bool active, Vector2 point, Rect rect)
        {
            PoseTarget target = active ? PickTargetAt (point, rect, true) : null;
            GameObject hover = target != null ? target.gameObject : null;
            if (hover == meshHover_) return false;
            meshHover_ = hover;
            return true;
        }

        void InvalidatePickMeshes ()
        {
            if (stage_ != null) stage_.InvalidatePickMeshes ();
        }

        /// <summary>メッシュのクリックで当たった物を選ぶ。修飾キーが無ければ 1 つだけ選ぶ（null なら外す）</summary>
        void SelectPicked (PoseTarget target, EventModifiers modifiers)
        {
            bool shift = (modifiers & EventModifiers.Shift) != 0;
            bool children = (modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            if (target == null) {
                if (!shift) Select (null);
                return;
            }
            if (shift || children) SelectWithModifiers (target.gameObject, modifiers);
            else Select (target.gameObject);
            // 点をつかむ間は FK のハンドルを押さずに選ぶので、回転のギズモを出す
            FkTarget fk = target as FkTarget;
            if (fk != null && isPointGrab) fk.FocusHandle ();
        }

        /// <summary>
        /// 一覧のパネル（オーバーレイが渡す）。Home / End をタイムラインと一覧のどちらに効かせるか決めるのに使う
        /// </summary>
        VisualElement pickerList_;

        public void SetPickerList (VisualElement list)
        {
            pickerList_ = list;
        }

        /// <summary>
        /// 一覧にキーボードのフォーカスがあるか
        /// </summary>
        public bool isPickerFocused
        {
            get {
                if (pickerList_ == null || pickerList_.panel == null) return false;

                VisualElement focused = pickerList_.panel.focusController.focusedElement as VisualElement;
                for (VisualElement e = focused; e != null; e = e.parent) {
                    if (e == pickerList_) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 骨から、それを受け持つ対象を探す。指など受け持ちの無い骨は親をたどる
        /// </summary>
        PoseTarget FindPickTarget (Transform bone)
        {
            // ✋ が全身 IK の段のときは、その骨（無ければ親へたどって最初に当たる骨）の FK を選ぶ（点をつなぐ線を押したときと同じ。10-08）。
            // 点はメッシュの上に丸で出ているので、丸を押して選ぶ。FK で骨を回すと、子の Pin の点が付いてくる（CarryPinnedPoints）
            if (isPointGrab) {
                for (Transform t = bone; t != null; t = t.parent) {
                    PoseTarget target;
                    FkTarget fk = pickTransforms_.TryGetValue (t, out target) ? target as FkTarget : null;
                    if (fk != null) return fk;
                }
                return null;
            }
            for (Transform t = bone; t != null; t = t.parent) {
                PoseTarget target;
                if (pickTransforms_.TryGetValue (t, out target) && target != null) return target;
            }
            return null;
        }
    }

}
