using UnityEngine;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 足を、接地の点（足首・踵・足裏の中心・小指側・つま先の先）を頂点にした硬い箱とみなし、つかんだ頂点を手で動かして床（ワールドの Y = 0）に
    /// 押し当てたときに、現実の箱がどう動くかをまねる（Floor Clamp。10-22 ユーザー指示「制御点を面の頂点としたボックスを、制御点をつかんで面に押し当てたとき、
    /// 現実ではどういった挙動になるかをシミュレーションした動きにしてほしい」）。頂点は床との当たりの球（定義の radius）の中心で、床と比べるのは球の下の端。
    ///
    /// 手の動きを少しずつ（kStep）に分けて、次のように動かす:
    /// - どこも床に着いていない: 向きを変えずにそのまま動く。床に潜った頂点があれば床まで戻し、その頂点を当たりにする
    /// - 床に着いている: 当たった頂点は滑らない（摩擦）。当たりの頂点を上から見た外形（凸包）を作り、つかんだ頂点の真下が外形の中なら倒れず
    ///   （押し下げても下がらず、横へは床の上を滑る）、外なら（当たりが 1 つならその頂点、2 つ以上なら外形のいちばん近い辺を軸に。辺の延長の上ならその端を支点に）倒れて、つかんだ頂点が手の方へ向く
    ///   （つま先だけ着いていれば、つま先を支点に踵が下りる。つま先と足裏の中心が着いて足首がその後ろなら、足裏の中心を支点に踵が下りる）
    /// - 持ち上げる: 足の形と向きのまま床を離れる。動かさない頂点（Locked の点）があれば、それを支点に起き上がる（足裏の中心を Locked にして足首を上げるとつま先立ち）
    /// 転がした後に潜った頂点は、今の当たりのまわりに回して床まで戻し、当たりに加える。
    /// 動かさないと決めた頂点（Locked の点）は、置いた場所から動かない当たり（支点）にする（持ち上げても外れず、横へも滑らない）。
    /// つま先の骨の上の頂点（つま先の先）は、つま先の付け根の関節で曲がる別の箱に乗る: 床に潜りそうになったら、まずつま先を曲げて床に沿わせ
    /// （足裏の中心を支点に踵を上げても、つま先は床に着いたまま。つま先立ち）、曲げる必要がなくなれば元の曲がりへ戻す。足裏が面で着いていれば、浮いたつま先も床まで下ろす。
    ///
    /// 毎回元の姿勢から解き直すと、滑らない当たりは作れない（どこに着いていたかを持ち越せない）ので、ドラッグの間は箱の姿勢と当たりを持ち越す（FullBodyRig.TryMoveFoot）
    /// </summary>
    sealed class FootBox
    {
        /// <summary>手の動きを分ける幅（メートル）。転がる・当たりが増える所を飛ばさない</summary>
        const float kStep = 0.002f;
        const int kMaxSteps = 1000;
        /// <summary>床に着いているとみなす高さ（メートル）</summary>
        const float kTouch = 2e-3f;
        /// <summary>床より下とみなす深さ（メートル）</summary>
        const float kTolerance = 1e-4f;
        /// <summary>潜った頂点を床まで戻すのをくり返す回数</summary>
        const int kSettlePasses = 8;
        /// <summary>つま先を曲げられる範囲（ラジアン。反らす向き・曲げる向き）と、元の曲がりへ戻す速さ（1 回あたり）</summary>
        const float kToeUp = 70 * Mathf.Deg2Rad;
        const float kToeDown = 30 * Mathf.Deg2Rad;
        const float kToeRelax = 2 * Mathf.Deg2Rad;

        readonly Vector3[] local_;
        readonly float[] radius_;
        Vector3 position_;
        Quaternion rotation_;
        readonly List<int> contacts_ = new List<int> ();
        readonly bool[] fixed_;
        /// <summary>つま先の骨の上の頂点か</summary>
        readonly bool[] toe_;
        readonly bool hasToes_;
        /// <summary>つま先の付け根の関節と曲がる軸（足の骨の枠）、反らす向きの符号、今の曲げ（ラジアン。0 が始めの曲がり）</summary>
        readonly Vector3 toeJoint_;
        readonly Vector3 toeAxis_;
        readonly float toeUpSign_;
        float toeAngle_;

        /// <param name="position">箱の基準（足の骨の付け根。ワールド）</param>
        /// <param name="rotation">箱の向き（足の骨の向き。ワールド）</param>
        /// <param name="vertices">頂点（球の中心。ワールド）</param>
        /// <param name="radius">頂点ごとの球の半径（メートル）</param>
        /// <param name="fixedVertices">動かさない頂点（Locked の点。置いた場所から動かない支点）。null なら無し</param>
        /// <param name="toeVertices">つま先の骨の上の頂点。null なら無し（全部が足の箱に乗る）</param>
        /// <param name="toeJoint">つま先の付け根の関節（ワールド）</param>
        /// <param name="toeAxis">つま先が曲がる軸（ワールド。足の横向き）</param>
        public FootBox (Vector3 position, Quaternion rotation, IList<Vector3> vertices, IList<float> radius, IList<bool> fixedVertices = null,
            IList<bool> toeVertices = null, Vector3 toeJoint = default (Vector3), Vector3 toeAxis = default (Vector3))
        {
            // 足裏の点が片側（小指側）にしか無いときは、足の中心線で折り返した内側（親指側）の頂点を足す（つかめる頂点は渡した点だけ。足した頂点は後ろに並ぶ）。
            // 内側に縁が無いと、箱が内側へ倒れ込んで足首の点が床に着くまで転がった（10-22）
            List<Vector3> allVertices = new List<Vector3> (vertices);
            List<float> allRadius = new List<float> (radius);
            List<bool> allFixed = new List<bool> ();
            List<bool> allToes = new List<bool> ();
            for (int i = 0; i < vertices.Count; i++) {
                allFixed.Add (fixedVertices != null && fixedVertices[i]);
                allToes.Add (toeVertices != null && toeVertices[i]);
            }
            AddInnerEdge (vertices.Count, allVertices, allRadius, allFixed, allToes);
            vertices = allVertices;
            radius = allRadius;
            fixedVertices = allFixed;
            if (toeVertices != null) toeVertices = allToes;
            position_ = position;
            rotation_ = rotation;
            local_ = new Vector3[vertices.Count];
            radius_ = new float[vertices.Count];
            fixed_ = new bool[vertices.Count];
            toe_ = new bool[vertices.Count];
            Quaternion inverse = Quaternion.Inverse (rotation);
            hasToes_ = toeVertices != null && toeAxis.sqrMagnitude > 1e-10f;
            toeJoint_ = inverse * (toeJoint - position);
            toeAxis_ = hasToes_ ? (inverse * toeAxis).normalized : Vector3.right;
            for (int i = 0; i < local_.Length; i++) {
                local_[i] = inverse * (vertices[i] - position);
                radius_[i] = Mathf.Max (0, radius[i]);
                toe_[i] = hasToes_ && toeVertices[i];
                // つま先の頂点はつま先を曲げて床に沿わせるので、動かない支点にはしない
                fixed_[i] = fixedVertices != null && fixedVertices[i] && !toe_[i];
            }
            // 反らす向き: 曲げを増やすとつま先の頂点が上がる向き
            toeUpSign_ = 1;
            for (int i = 0; i < local_.Length; i++) {
                if (!toe_[i]) continue;
                toeUpSign_ = Vector3.Cross (rotation * toeAxis_, rotation * (local_[i] - toeJoint_)).y >= 0 ? 1 : -1;
                break;
            }
            // 動かさない頂点と、今床に着いている頂点が、最初の当たり
            for (int i = 0; i < local_.Length; i++) {
                if (fixed_[i] || Bottom (i) <= kTouch) contacts_.Add (i);
            }
            Settle ();
        }

        /// <summary>箱の基準（足の骨の付け根。ワールド）</summary>
        public Vector3 position
        {
            get { return position_; }
        }

        /// <summary>箱の向き（足の骨の向き。ワールド）</summary>
        public Quaternion rotation
        {
            get { return rotation_; }
        }

        /// <summary>つま先の付け根の関節（ワールド）</summary>
        public Vector3 toeJoint
        {
            get { return position_ + rotation_ * toeJoint_; }
        }

        /// <summary>つま先の、始めの曲がりからの曲げ（足の骨の枠での回転）</summary>
        public Quaternion toeTurn
        {
            get { return Quaternion.AngleAxis (toeAngle_ * Mathf.Rad2Deg, toeAxis_); }
        }

        /// <summary>床に着いている頂点（テスト用）</summary>
        internal IReadOnlyList<int> contacts
        {
            get { return contacts_; }
        }

        public Vector3 Vertex (int i)
        {
            if (toe_[i]) return position_ + rotation_ * (toeJoint_ + toeTurn * (local_[i] - toeJoint_));
            return position_ + rotation_ * local_[i];
        }

        /// <summary>頂点の球の下の端の、床からの高さ</summary>
        public float Bottom (int i)
        {
            return Vertex (i).y - radius_[i];
        }

        /// <summary>頂点 grabbed を hand の方へ動かす。動いた先（届かなければ手前）を返す</summary>
        public Vector3 Move (int grabbed, Vector3 hand)
        {
            int steps = Mathf.Clamp (Mathf.CeilToInt ((hand - Vertex (grabbed)).magnitude / kStep), 0, kMaxSteps);
            for (int s = 0; s < steps; s++) {
                Step (grabbed, (hand - Vertex (grabbed)) / (steps - s));
            }
            return Vertex (grabbed);
        }

        void Step (int grabbed, Vector3 delta)
        {
            if (delta.sqrMagnitude < 1e-12f) return;
            // つかんだ頂点は手に付いて動く（支点にしない。床の上を滑り、潜れば床まで戻す）
            bool onFloor = Bottom (grabbed) <= kTouch;
            if (!fixed_[grabbed]) contacts_.Remove (grabbed);
            // 持ち上げるときは、動かさない頂点だけを支点に残す（ほかの当たりは離れる。無ければ足の形と向きのまま床を離れる）
            if (delta.y > 0) contacts_.RemoveAll (i => !fixed_[i]);
            if (contacts_.Count == 0) {
                position_ += delta;
            }
            else {
                Push (grabbed, delta, onFloor && delta.y <= 0);
            }
            Settle ();
        }

        /// <summary>
        /// 床に着いた箱を押す: 当たりの外形（上から見た凸包）の中につかんだ頂点の真下があれば倒れず、横へだけ滑る（動かさない頂点があれば滑らない）。
        /// 外にあれば、当たりが 1 つならその頂点を支点に、2 つ以上なら外形のいちばん近い辺を軸に倒す（辺の延長の上にあれば、その端を支点に）
        /// </summary>
        /// <param name="grabbedOnFloor">つかんだ頂点も床に着いていて、押し下げている（支えに入れる。踵をつかんで横へ動かしても倒れずに滑る）</param>
        void Push (int grabbed, Vector3 delta, bool grabbedOnFloor)
        {
            List<int> supports = new List<int> (contacts_);
            if (grabbedOnFloor) supports.Add (grabbed);
            List<int> hull = Hull (supports);
            Vector2 point = Flat (Vertex (grabbed));
            int a, b;
            if (IsSupported (hull, point, out a, out b)) {
                // 支えられている: 床の上を滑る。動かさない頂点があれば、それを支点に床の上で回す
                Vector3 across = new Vector3 (delta.x, 0, delta.z);
                int pin = contacts_.FindIndex (i => fixed_[i]);
                if (pin < 0) {
                    // 押し下げていれば、床に触れているとみなした当たりのわずかなすき間（kTouch まで）を詰めて床にぴったり着ける
                    float gap = float.MaxValue;
                    foreach (int contact in contacts_) gap = Mathf.Min (gap, Bottom (contact));
                    if (delta.y < 0 && gap > 0 && gap < float.MaxValue) across.y = -Mathf.Min (gap, -delta.y);
                    position_ += across;
                }
                else {
                    Pivot (grabbed, across, contacts_[pin]);
                }
            }
            else {
                // 倒れるときに支えるのは、支点の角・辺だけ（ほかの当たりは浮くか、潜れば床まで戻して当たりに戻す）
                contacts_.RemoveAll (i => i != a && i != b && !fixed_[i]);
                if (b < 0) Pivot (grabbed, delta, a);
                else Hinge (grabbed, delta, a, b);
            }
        }

        static Vector2 Flat (Vector3 point)
        {
            return new Vector2 (point.x, point.z);
        }

        /// <summary>頂点 indices を上から見た凸包（反時計回り。1 つ・2 つならそのまま）</summary>
        List<int> Hull (List<int> indices)
        {
            List<int> sorted = new List<int> (indices);
            sorted.Sort ((p, q) => {
                Vector2 u = Flat (Vertex (p)), v = Flat (Vertex (q));
                return u.x != v.x ? u.x.CompareTo (v.x) : u.y.CompareTo (v.y);
            });
            if (sorted.Count < 3) return sorted;
            List<int> hull = new List<int> ();
            for (int pass = 0; pass < 2; pass++) {
                int start = hull.Count;
                for (int k = 0; k < sorted.Count; k++) {
                    int i = pass == 0 ? sorted[k] : sorted[sorted.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross (hull[hull.Count - 2], hull[hull.Count - 1], i) <= 1e-9f) hull.RemoveAt (hull.Count - 1);
                    hull.Add (i);
                }
                hull.RemoveAt (hull.Count - 1);
            }
            return hull;
        }

        float Cross (int o, int p, int q)
        {
            Vector2 a = Flat (Vertex (p)) - Flat (Vertex (o)), b = Flat (Vertex (q)) - Flat (Vertex (o));
            return a.x * b.y - a.y * b.x;
        }

        /// <summary>
        /// 点 point（上から見た）が凸包 hull で支えられているか（中・辺の上）。支えられていなければ、当たりが 1 つならその頂点（a。b は -1）、2 つ以上ならいちばん近い辺（a と b）を返す
        /// </summary>
        bool IsSupported (List<int> hull, Vector2 point, out int a, out int b)
        {
            a = hull.Count > 0 ? hull[0] : -1;
            b = -1;
            if (hull.Count == 1) return (Flat (Vertex (hull[0])) - point).magnitude <= kTouch;
            bool inside = hull.Count >= 3;
            float best = float.MaxValue;
            int edges = hull.Count == 2 ? 1 : hull.Count;
            for (int e = 0; e < edges; e++) {
                int i = hull[e], j = hull[(e + 1) % hull.Count];
                Vector2 p = Flat (Vertex (i)), q = Flat (Vertex (j));
                Vector2 edge = q - p;
                if (edge.x * (point.y - p.y) - edge.y * (point.x - p.x) < -1e-7f) inside = false;
                float along = edge.sqrMagnitude > 1e-12f ? Vector2.Dot (point - p, edge) / edge.sqrMagnitude : 0;
                float t = Mathf.Clamp01 (along);
                float distance = (p + edge * t - point).magnitude;
                if (distance >= best) continue;
                best = distance;
                // 点が辺の横にあれば、その辺を軸に転がす（足は点ではなく縁で転がる。角を支点にすると、つま先と小指側で支えた足が
                // 小指側の 1 点まわりに回ってつま先が浮き、外の縁に乗って横に傾いたまま床に密着しなかった。10-22）。
                // 辺の延長の上（端の先）にあれば、その端を支点に回す（辺を軸にすると、軸が点を通って回る向きが決まらず、箱がひっくり返った）
                float beyond = (along < 0 ? -along : along - 1) * edge.magnitude;
                float side = Mathf.Abs (edge.x * (point.y - p.y) - edge.y * (point.x - p.x)) / Mathf.Max (edge.magnitude, 1e-6f);
                if (beyond > 0 && side < beyond) {
                    a = along < 0 ? i : j;
                    b = -1;
                }
                else {
                    a = i;
                    b = j;
                }
            }
            return inside || best <= kTouch;
        }

        bool HasFixed ()
        {
            foreach (int contact in contacts_) {
                if (fixed_[contact]) return true;
            }
            return false;
        }

        /// <summary>当たり 1 つ: 当たった頂点を支点に、つかんだ頂点が手の方を向くように転がす（支点は動かない）</summary>
        void Pivot (int grabbed, Vector3 delta, int contact)
        {
            Vector3 center = Vertex (contact);
            Vector3 from = Vertex (grabbed) - center;
            Vector3 to = from + delta;
            if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return;
            Turn (center, Quaternion.FromToRotation (from, to));
        }

        /// <summary>当たり 2 つ: 2 つを結ぶ辺を軸に、つかんだ頂点が手の方へ回るように転がす</summary>
        void Hinge (int grabbed, Vector3 delta, int a, int b)
        {
            Vector3 center = Vertex (a);
            Vector3 axis = Vertex (b) - center;
            if (axis.sqrMagnitude < 1e-10f) {
                Pivot (grabbed, delta, a);
                return;
            }
            axis.Normalize ();
            Vector3 from = Vector3.ProjectOnPlane (Vertex (grabbed) - center, axis);
            Vector3 to = Vector3.ProjectOnPlane (Vertex (grabbed) + delta - center, axis);
            if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return;
            Turn (center, Quaternion.AngleAxis (Vector3.SignedAngle (from, to, axis), axis));
        }

        /// <summary>箱を center まわりに turn だけ回す</summary>
        void Turn (Vector3 center, Quaternion turn)
        {
            rotation_ = EditingRigSolver.Normalize (turn * rotation_);
            position_ = center + turn * (position_ - center);
        }

        /// <summary>
        /// 潜った頂点を床まで戻して当たりに加える。当たりが無ければ真上へ、1 つならその頂点のまわりに、2 つならその辺のまわりに回して戻す。
        /// 3 つ以上（面で着いている）なら真上へ戻す
        /// </summary>
        void Settle ()
        {
            for (int pass = 0; pass < kSettlePasses; pass++) {
                int deepest = -1;
                float depth = kTolerance;
                for (int i = 0; i < local_.Length; i++) {
                    // つま先の頂点は、当たりでも足が回れば潜るので見る
                    if ((contacts_.Contains (i) && !toe_[i]) || -Bottom (i) <= depth) continue;
                    depth = -Bottom (i);
                    deepest = i;
                }
                if (deepest < 0) break;
                // つま先の頂点は、まずつま先を曲げて床に沿わせる（曲げきっても潜る分は、足ごと戻す）
                if (toe_[deepest] && BendToe (deepest)) {
                    if (!contacts_.Contains (deepest)) contacts_.Add (deepest);
                    continue;
                }
                // 動かさない頂点があれば、それを中心に回して戻す（真上へ持ち上げると、動かさない頂点も動く）
                List<int> centers = HasFixed () ? contacts_.FindAll (i => fixed_[i]) : contacts_;
                if (centers.Count == 0 || centers.Count >= 3) {
                    position_ += Vector3.up * depth;
                }
                else {
                    Vector3 center = Vertex (centers[0]);
                    Vector3 axis = centers.Count == 2
                        ? (Vertex (centers[1]) - center).normalized
                        : Vector3.Cross (Vertex (deepest) - center, Vector3.up).normalized;
                    // 回す角度は、頂点の軸からの距離と、上がる向きの成分から近似し、数回くり返して詰める
                    for (int k = 0; k < kSettlePasses && -Bottom (deepest) > kTolerance; k++) {
                        Vector3 arm = Vector3.ProjectOnPlane (Vertex (deepest) - center, axis);
                        Vector3 rise = Vector3.Cross (axis, arm);
                        float rate = rise.y;
                        if (Mathf.Abs (rate) < 1e-5f) break;
                        Turn (center, Quaternion.AngleAxis (-Bottom (deepest) / rate * Mathf.Rad2Deg, axis));
                    }
                }
                // 床からわずかに浮いたり潜ったりした分は、床の高さにそろえて当たりにする
                if (!contacts_.Contains (deepest)) contacts_.Add (deepest);
            }
            RelaxToe ();
            RestToe ();
            // 当たりのうち床から離れた頂点は外す（持ち上げた・転がって浮いた）。動かさない頂点は外さない
            contacts_.RemoveAll (i => !fixed_[i] && Bottom (i) > kTouch);
            // 床に触れた頂点（潜ってはいない）も当たりにする（平らに着いた足を、面で着いたとみなす。2 点だけと数えると、その辺を軸に横へ倒れた）
            for (int i = 0; i < local_.Length; i++) {
                if (!contacts_.Contains (i) && Bottom (i) <= kTouch) contacts_.Add (i);
            }
        }

        /// <summary>つま先の頂点 i が床に着くまでつま先を曲げる（範囲の中で）。床に着いたら true</summary>
        bool BendToe (int i)
        {
            Vector3 axis = rotation_ * toeAxis_;
            for (int k = 0; k < kSettlePasses && -Bottom (i) > kTolerance; k++) {
                float rate = Vector3.Cross (axis, Vertex (i) - toeJoint).y;
                if (Mathf.Abs (rate) < 1e-5f) break;
                float up = Mathf.Clamp ((toeAngle_ - Bottom (i) / rate) * toeUpSign_, -kToeDown, kToeUp);
                if (Mathf.Approximately (up * toeUpSign_, toeAngle_)) break;
                toeAngle_ = up * toeUpSign_;
            }
            return -Bottom (i) <= kTolerance;
        }

        /// <summary>
        /// 足裏が面で着いている（つま先の骨の上でない頂点が 3 つ以上床に着いている）ときは、浮いたつま先を床まで下ろす（曲げる範囲の中で）。
        /// 平らに着いた足でも、姿勢のつま先が反っていると、つま先の先が浮いたまま床に密着しなかった（10-22 ユーザー指示）
        /// </summary>
        void RestToe ()
        {
            if (!hasToes_) return;
            int resting = 0;
            for (int i = 0; i < local_.Length; i++) {
                if (!toe_[i] && radius_[i] > 0 && Bottom (i) <= kTouch) resting++;
            }
            if (resting < 3) return;
            Vector3 axis = rotation_ * toeAxis_;
            for (int i = 0; i < local_.Length; i++) {
                if (!toe_[i]) continue;
                for (int k = 0; k < kSettlePasses && Bottom (i) > kTolerance; k++) {
                    float rate = Vector3.Cross (axis, Vertex (i) - toeJoint).y;
                    if (Mathf.Abs (rate) < 1e-5f) break;
                    float up = Mathf.Clamp ((toeAngle_ - Bottom (i) / rate) * toeUpSign_, -kToeDown, kToeUp);
                    if (Mathf.Approximately (up * toeUpSign_, toeAngle_)) break;
                    toeAngle_ = up * toeUpSign_;
                }
                if (Bottom (i) <= kTouch && !contacts_.Contains (i)) contacts_.Add (i);
            }
        }

        /// <summary>曲げたつま先を、床に潜らない所まで始めの曲がりへ戻す</summary>
        void RelaxToe ()
        {
            if (!hasToes_) return;
            for (int k = 0; k < kSettlePasses && toeAngle_ != 0; k++) {
                float before = toeAngle_;
                toeAngle_ = Mathf.MoveTowards (toeAngle_, 0, kToeRelax);
                bool above = true;
                for (int i = 0; i < local_.Length && above; i++) above = !toe_[i] || Bottom (i) >= -kTolerance;
                if (above) continue;
                toeAngle_ = before;
                break;
            }
        }

        /// <summary>
        /// 足の箱の頂点（count 個は足の点）に、内側の縁の頂点を足す。足の骨の上の足裏の頂点（半径のあるもの。つま先の骨の上の点は使わない）の、
        /// 踵から足裏の前への線から横へのずれを見て、片側にだけ 1cm 以上離れた頂点があれば、いちばん離れた頂点を線で折り返した所に足す。
        /// つま先の先を線に使うと、つま先が反った姿勢では線が傾き、内側の頂点が足裏より浮いた
        /// </summary>
        static void AddInnerEdge (int count, List<Vector3> vertices, List<float> radius, List<bool> fixedVertices, List<bool> toeVertices)
        {
            int back = -1, front = -1;
            Vector3 middle = Vector3.zero;
            int soles = 0;
            for (int i = 0; i < count; i++) {
                if (radius[i] <= 0 || toeVertices[i]) continue;
                middle += vertices[i];
                soles++;
            }
            if (soles < 3) return;
            middle /= soles;
            // 踵と足裏の前: 足裏の頂点の真ん中からいちばん遠い頂点と、それからいちばん遠い頂点
            float far = -1;
            for (int i = 0; i < count; i++) {
                if (radius[i] <= 0 || toeVertices[i] || (vertices[i] - middle).sqrMagnitude <= far) continue;
                far = (vertices[i] - middle).sqrMagnitude;
                front = i;
            }
            far = -1;
            for (int i = 0; i < count; i++) {
                if (radius[i] <= 0 || toeVertices[i] || i == front || (vertices[i] - vertices[front]).sqrMagnitude <= far) continue;
                far = (vertices[i] - vertices[front]).sqrMagnitude;
                back = i;
            }
            if (back < 0) return;
            // 足裏の面の中で、踵とつま先の線に対して折り返す（真上を向いた面で折り返すと、横に傾いた足では内側の頂点が足裏より下に来て、
            // 小指側と内側の 2 点で先に着き、その線を軸に転がって足裏の中心が 1.4cm 浮いたまま止まった）
            Vector3 line = (vertices[front] - vertices[back]).normalized;
            if (line.sqrMagnitude < 0.5f) return;
            int outer = -1;
            Vector3 across = Vector3.zero;
            for (int i = 0; i < count; i++) {
                if (radius[i] <= 0 || toeVertices[i]) continue;
                Vector3 offset = vertices[i] - vertices[back];
                offset -= line * Vector3.Dot (offset, line);
                if (offset.sqrMagnitude <= across.sqrMagnitude) continue;
                across = offset;
                outer = i;
            }
            if (outer < 0 || across.magnitude < 0.01f) return;
            // 反対側にもう縁の頂点があれば足さない
            Vector3 side = across.normalized;
            for (int i = 0; i < count; i++) {
                if (radius[i] <= 0 || toeVertices[i]) continue;
                Vector3 offset = vertices[i] - vertices[back];
                if (Vector3.Dot (offset - line * Vector3.Dot (offset, line), side) < -0.003f) return;
            }
            vertices.Add (vertices[outer] - across * 2);
            radius.Add (radius[outer]);
            fixedVertices.Add (false);
            toeVertices.Add (false);
        }
    }

}
