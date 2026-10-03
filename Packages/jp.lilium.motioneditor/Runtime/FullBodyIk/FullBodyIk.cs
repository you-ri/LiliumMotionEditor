using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Lilium
{

    /// <summary>
    /// 全身 IK の骨 1 本（剛体 1 つ）の設定。キャラを作るときに 1 回組む
    /// </summary>
    public struct FullBodyIkBone
    {
        /// <summary>親の剛体の番号（-1 = 根。ふつうは腰）。親は必ず自分より前に並べる</summary>
        public int parent;
        /// <summary>硬さ（0〜1）。親から見た向きを、元の姿勢の値に保つ強さ。0 は保たない、1 は曲がらない。根（腰）は、元の姿勢の向きに保つ強さ</summary>
        public float stiffness;
        /// <summary>1 なら、親との関節は 1 軸だけ曲がる蝶番（肘・膝）</summary>
        public int hinge;
        /// <summary>
        /// 蝶番の軸（親の骨の枠で）。基準姿勢で、親の骨の向きと「関節が出る向き（肘は後ろ・膝は前）」から決めておく。
        /// 親の骨と一緒に回るので、太もも・上腕をひねれば曲がる面も付いて回る。
        /// 元の姿勢がはっきり曲がっているときは、その曲がりの面を優先する
        /// </summary>
        public float3 hingeAxis;
    }

    /// <summary>
    /// 全身 IK の目標（固定する点）。骨の上の点（骨の付け根から offset だけ離れた所。骨の枠で。0 なら付け根）を position へ、骨の向きを rotation へ寄せる。
    /// 点の種類による違いは無い（S26: 関節・顔の前・かかと・手のひらも、骨の上のどこにあるかが違うだけ）。
    ///
    /// 強さ 1 なら、その点を position に置いて動かない物にする（骨はその点まわりに回るだけ）。rotationWeight が 1 なら、骨の向きも rotation に留める。
    /// 同じ骨の上の点どうしは剛体として拘束しあう: 2 つ目の固定した点は、1 つ目の点まわりに骨を回して 2 つ目の点の方へ向け、
    /// 3 つ目は、先の 2 点を結ぶ軸まわりに回して向ける（点どうしの距離は変わらないので、届かない分は残る）。
    /// 固定した点が 1 つだけなら、骨の向きは決めない（その点まわりに回れる）
    /// </summary>
    public struct FullBodyIkEffector
    {
        public int bone;
        /// <summary>骨の上の点の、骨の付け根からの位置（骨の枠・メートル）。0 なら骨の付け根そのものを寄せる目標</summary>
        public float3 offset;
        /// <summary>
        /// 同じ骨に強さ 1 の点が 2 つあるとき、0 でない方を先に満たす（その点は目標に置かれ、もう 1 つはそこを支点に届く所まで寄る）。
        /// 動かさないと決めた点（Locked）に入れる。同じなら並びの順
        /// </summary>
        public int priority;
        /// <summary>ルート空間・メートル</summary>
        public float3 position;
        /// <summary>ルート空間</summary>
        public quaternion rotation;
        /// <summary>位置を寄せる強さ（0〜1）。1 で固定、0 で効かない</summary>
        public float positionWeight;
        /// <summary>向きを寄せる強さ（0〜1）</summary>
        public float rotationWeight;
    }

    /// <summary>
    /// 全身 IK が読み書きするデータ。配列は呼ぶ側が持ち、使い終わったら Dispose する。
    /// Transform を見ない（入力も出力もルート空間の値）ので、エディタ以外（アニメーションのジョブなど）からも呼べる
    /// </summary>
    public struct FullBodyIkData
    {
        /// <summary>status の並び</summary>
        public const int kStatusBurst = 0;
        public const int kStatusDefaultBend = 1;
        public const int kStatusCount = 2;

        // ---- 設定（キャラを作るときに 1 回） ----
        public NativeArray<FullBodyIkBone> bones;
        /// <summary>解く回数（固定）</summary>
        public int iterations;
        /// <summary>根（腰）を元の姿勢の位置に留める強さ（0〜1）。向きは根の骨の硬さで留める</summary>
        public float rootPin;

        // ---- 毎回の入力と出力 ----
        /// <summary>骨の付け根の位置（ルート空間・メートル）。入力は元の姿勢、出力は解いた姿勢</summary>
        public NativeArray<float3> positions;
        /// <summary>骨の向き（ルート空間）。入力は元の姿勢、出力は解いた姿勢</summary>
        public NativeArray<quaternion> rotations;
        public NativeArray<FullBodyIkEffector> effectors;
        /// <summary>effectors のうち、先頭から何個を使うか</summary>
        public int effectorCount;
        /// <summary>出力。目標ごとの届かなかった差（メートル）</summary>
        public NativeArray<float> residuals;
        /// <summary>出力。[kStatusBurst] Burst で動いたら 1／[kStatusDefaultBend] 曲がる向きを既定から決めた蝶番の数</summary>
        public NativeArray<int> status;
        /// <summary>
        /// 入力。1 の骨は元の姿勢の場所と向きから動かさない（解く骨の並びにあっても、動かない物として扱う）。
        /// その先の解かない骨は、今までどおり親に付いて動く。キーの間で、固定した手足だけを IK で動かし、腰・背骨は元の姿勢（FK）のままにするときに使う
        /// </summary>
        public NativeArray<int> fixedBones;

        // ---- 作業領域 ----
        internal NativeArray<float3> inputPositions;
        internal NativeArray<quaternion> inputRotations;
        internal NativeArray<float3> centers;
        internal NativeArray<float3> centerLocal;
        internal NativeArray<float3> tipLocal;
        internal NativeArray<float3> anchorLocal;
        internal NativeArray<float3> childSum;
        internal NativeArray<int> childCount;
        internal NativeArray<float> extent;
        internal NativeArray<float> mass;
        internal NativeArray<float> inertia;
        internal NativeArray<float> inverseMass;
        internal NativeArray<float> inverseInertia;
        internal NativeArray<quaternion> restRelative;
        internal NativeArray<float> stiffnessLambda;
        internal NativeArray<int> hingeOn;
        internal NativeArray<float3> hingeAxisParent;
        internal NativeArray<float3> hingeAxisChild;
        internal NativeArray<float3> hingeDirParent;
        internal NativeArray<float3> hingeDirChild;
        internal NativeArray<float2> hingeRange;
        internal NativeArray<float2> rootLambda;
        internal NativeArray<float2> effectorLambda;
        internal NativeArray<int> active;
        internal NativeArray<int> effectorsBelow;
        internal NativeArray<int> pinnedOrigin;
        /// <summary>骨の上の固定した点の数が 2 以上のとき、その数（1 つ目まわりに回して 2 つ目へ向けたら 2、その 2 点の軸まわりに回して 3 つ目へ向けたら 3）</summary>
        internal NativeArray<int> pinCount;
        /// <summary>2 つ目の固定した点の場所（骨の枠で）</summary>
        internal NativeArray<float3> secondPin;
        internal NativeArray<int> effectorBody;
        internal NativeArray<float3> effectorPoint;
        /// <summary>1 なら、強さ 1 の目標だが、骨がもうほかの点で動かない物になっているので、その点まわりに骨を回して寄せる</summary>
        internal NativeArray<int> effectorHard;

        public bool isCreated
        {
            get { return bones.IsCreated; }
        }

        public static FullBodyIkData Create (int boneCount, int maxEffectors, Allocator allocator)
        {
            FullBodyIkData d = new FullBodyIkData ();
            d.iterations = 1;
            d.bones = new NativeArray<FullBodyIkBone> (boneCount, allocator);
            d.positions = new NativeArray<float3> (boneCount, allocator);
            d.rotations = new NativeArray<quaternion> (boneCount, allocator);
            d.effectors = new NativeArray<FullBodyIkEffector> (math.max (1, maxEffectors), allocator);
            d.residuals = new NativeArray<float> (math.max (1, maxEffectors), allocator);
            d.status = new NativeArray<int> (kStatusCount, allocator);
            d.fixedBones = new NativeArray<int> (boneCount, allocator);
            d.inputPositions = new NativeArray<float3> (boneCount, allocator);
            d.inputRotations = new NativeArray<quaternion> (boneCount, allocator);
            d.centers = new NativeArray<float3> (boneCount, allocator);
            d.centerLocal = new NativeArray<float3> (boneCount, allocator);
            d.tipLocal = new NativeArray<float3> (boneCount, allocator);
            d.anchorLocal = new NativeArray<float3> (boneCount, allocator);
            d.childSum = new NativeArray<float3> (boneCount, allocator);
            d.childCount = new NativeArray<int> (boneCount, allocator);
            d.extent = new NativeArray<float> (boneCount, allocator);
            d.mass = new NativeArray<float> (boneCount, allocator);
            d.inertia = new NativeArray<float> (boneCount, allocator);
            d.inverseMass = new NativeArray<float> (boneCount, allocator);
            d.inverseInertia = new NativeArray<float> (boneCount, allocator);
            d.restRelative = new NativeArray<quaternion> (boneCount, allocator);
            d.stiffnessLambda = new NativeArray<float> (boneCount, allocator);
            d.hingeOn = new NativeArray<int> (boneCount, allocator);
            d.hingeAxisParent = new NativeArray<float3> (boneCount, allocator);
            d.hingeAxisChild = new NativeArray<float3> (boneCount, allocator);
            d.hingeDirParent = new NativeArray<float3> (boneCount, allocator);
            d.hingeDirChild = new NativeArray<float3> (boneCount, allocator);
            d.hingeRange = new NativeArray<float2> (boneCount, allocator);
            d.rootLambda = new NativeArray<float2> (boneCount, allocator);
            d.effectorLambda = new NativeArray<float2> (math.max (1, maxEffectors), allocator);
            d.active = new NativeArray<int> (boneCount, allocator);
            d.effectorsBelow = new NativeArray<int> (boneCount, allocator);
            d.pinnedOrigin = new NativeArray<int> (boneCount, allocator);
            d.pinCount = new NativeArray<int> (boneCount, allocator);
            d.secondPin = new NativeArray<float3> (boneCount, allocator);
            d.effectorBody = new NativeArray<int> (math.max (1, maxEffectors), allocator);
            d.effectorPoint = new NativeArray<float3> (math.max (1, maxEffectors), allocator);
            d.effectorHard = new NativeArray<int> (math.max (1, maxEffectors), allocator);
            return d;
        }

        public void Dispose ()
        {
            if (!bones.IsCreated) return;
            bones.Dispose ();
            positions.Dispose ();
            rotations.Dispose ();
            effectors.Dispose ();
            residuals.Dispose ();
            status.Dispose ();
            fixedBones.Dispose ();
            inputPositions.Dispose ();
            inputRotations.Dispose ();
            centers.Dispose ();
            centerLocal.Dispose ();
            tipLocal.Dispose ();
            anchorLocal.Dispose ();
            childSum.Dispose ();
            childCount.Dispose ();
            extent.Dispose ();
            mass.Dispose ();
            inertia.Dispose ();
            inverseMass.Dispose ();
            inverseInertia.Dispose ();
            restRelative.Dispose ();
            stiffnessLambda.Dispose ();
            hingeOn.Dispose ();
            hingeAxisParent.Dispose ();
            hingeAxisChild.Dispose ();
            hingeDirParent.Dispose ();
            hingeDirChild.Dispose ();
            hingeRange.Dispose ();
            rootLambda.Dispose ();
            effectorLambda.Dispose ();
            active.Dispose ();
            effectorsBelow.Dispose ();
            pinnedOrigin.Dispose ();
            pinCount.Dispose ();
            secondPin.Dispose ();
            effectorBody.Dispose ();
            effectorPoint.Dispose ();
            effectorHard.Dispose ();
        }
    }

    /// <summary>
    /// 全身 IK を Burst で解くジョブ。Run() で呼ぶ。計算は FullBodyIk.Solve にあり、ほかのジョブの中からも同じ関数を呼べる。
    ///
    /// Burst のコンパイルは待たない（CompileSynchronously にしない）。エディタではコンパイルが済むまでマネージドで動き、済むと Burst に切り替わる。
    /// 待つと、起動して初めて全身 IK を使うときにコンパイルが終わるまで止まる（Unity 6.0 の Burst 1.8 では 1 分以上）。
    /// Burst とマネージドの結果の差はテスト（Burst_IsUsed_AndMatchesManaged）で見ている
    /// </summary>
    [BurstCompile (FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public struct FullBodyIkJob : IJob
    {
        public FullBodyIkData data;

        public void Execute ()
        {
            data.status[FullBodyIkData.kStatusBurst] = 1;
            FullBodyIk.MarkManaged (ref data);
            FullBodyIk.Solve (ref data);
        }
    }

    /// <summary>
    /// 全身 IK の計算本体。骨を剛体（位置と向き）で持ち、関節・蝶番・硬さ・目標・根の固定を、決まった回数だけ順に満たしていく。
    ///
    /// 毎回、受け取った元の姿勢から解く（前に解いた結果を持ち越さない）ので、同じ入力なら同じ結果になる。
    /// 解くのは、目標のある骨から根までの骨だけ。ほかの骨は親に付いて動く（腰を下げたとき、固定していない腕が遅れて残らない）。
    /// 目標（固定する点）に届く姿勢のうち、元の姿勢から動かす量が小さいものへ寄る。動かしにくさは、骨の重さ（その骨から先の
    /// 骨の長さの合計。根ほど重い）と硬さ（親から見た向きを元の値に保つばね）で決まる。
    ///
    /// 根拠にした公開論文:
    /// - Macklin, Müller, Chentanez, "XPBD: Position-Based Simulation of Compliant Constrained Dynamics", MIG 2016.
    /// - Müller, Macklin, Chentanez, Jeschke, Kim, "Detailed Rigid Body Simulation with Extended Position Based Dynamics", SCA 2020.
    /// </summary>
    public static class FullBodyIk
    {
        const float kEpsilon = 1e-9f;
        /// <summary>位置の補正 1 回で骨を回す角度の上限（ラジアン）。大きく外れた目標で回しすぎて暴れないように</summary>
        const float kMaxStepAngle = 0.5f;
        /// <summary>蝶番が曲がる角度の上限（ラジアン）。0 はまっすぐ</summary>
        const float kHingeMax = 2.97f;
        /// <summary>曲がる面を定義の軸から決めるか、元の姿勢の曲がりから決めるかの境目（曲がりの角度の正弦。約 15°〜35°）</summary>
        const float kBendFrom = 0.26f;
        const float kBendTo = 0.57f;
        /// <summary>出力を丸める単位。計算機ごとの下位の桁の違いを、焼いた値に出さない</summary>
        const float kRound = 1e5f;
        /// <summary>仕上げ（必ず満たす拘束だけを詰める反復）の最少の回数</summary>
        const int kMinPolish = 8;
        /// <summary>仕上げで、固定した点へ合わせ直すときに回す骨の数と、くり返す回数</summary>
        const int kSnapBones = 3;
        const int kSnapPasses = 3;
        /// <summary>位置を固定した点が「届いている」とみなす差（メートル）</summary>
        const float kReachTolerance = 1e-3f;

        [BurstDiscard]
        internal static void MarkManaged (ref FullBodyIkData d)
        {
            d.status[FullBodyIkData.kStatusBurst] = 0;
        }

        /// <summary>
        /// 解く。positions / rotations を元の姿勢から解いた姿勢へ書き換える。
        /// 子の骨の位置は「親の位置＋親の向き × 元の姿勢での付け根の位置」で出すので、骨の長さは変わらない
        /// </summary>
        public static void Solve (ref FullBodyIkData d)
        {
            int n = d.bones.Length;
            d.status[FullBodyIkData.kStatusDefaultBend] = 0;
            if (n == 0) return;
            int effectorCount = math.clamp (d.effectorCount, 0, d.effectors.Length);

            SolveOnce (ref d, n, effectorCount);
        }

        static void SolveOnce (ref FullBodyIkData d, int n, int effectorCount)
        {
            d.status[FullBodyIkData.kStatusDefaultBend] = 0;
            Prepare (ref d, n);
            PrepareEffectors (ref d, effectorCount);

            int iterations = math.max (1, d.iterations);
            for (int iteration = 0; iteration < iterations; iteration++) {
                SolveRoots (ref d, n);
                for (int e = 0; e < effectorCount; e++) SolveReach (ref d, e);
                SolveBends (ref d, n);
                // 関節は、奇数回と偶数回で並びを逆にたどる（根からと先からの両方から寄せる）。並びは固定
                if ((iteration & 1) == 0) {
                    for (int i = 0; i < n; i++) SolveJoint (ref d, i, true);
                }
                else {
                    for (int i = n - 1; i >= 0; i--) SolveJoint (ref d, i, true);
                }
                // 目標は最後に満たす（反復の終わりで、固定した点がいちばん合っている）
                for (int e = 0; e < effectorCount; e++) SolveEffector (ref d, e, true);
            }
            // 仕上げ: 柔らかい拘束（硬さ・根の固定・強さ 1 未満の目標）を外して、必ず満たす物（関節・蝶番・固定した点）だけを詰める。
            // 柔らかい拘束は毎回、関節を少し引き離す側に働くので、混ぜたままだと固定した点に数 mm の差が残る
            int polish = math.max (kMinPolish, iterations / 2);
            for (int iteration = 0; iteration < polish; iteration++) {
                for (int e = 0; e < effectorCount; e++) SolveReach (ref d, e);
                SolveBends (ref d, n);
                if ((iteration & 1) == 0) {
                    for (int i = 0; i < n; i++) SolveJoint (ref d, i, false);
                }
                else {
                    for (int i = n - 1; i >= 0; i--) SolveJoint (ref d, i, false);
                }
                for (int e = 0; e < effectorCount; e++) SolveEffector (ref d, e, false);
            }

            Finish (ref d, n, effectorCount);
        }

        // ---- 準備 ----

        static void Prepare (ref FullBodyIkData d, int n)
        {
            for (int i = 0; i < n; i++) {
                d.rotations[i] = math.normalizesafe (d.rotations[i], quaternion.identity);
                d.inputPositions[i] = d.positions[i];
                d.inputRotations[i] = d.rotations[i];
                d.childSum[i] = float3.zero;
                d.childCount[i] = 0;
                d.extent[i] = 0;
                d.stiffnessLambda[i] = 0;
                d.rootLambda[i] = float2.zero;
                d.hingeOn[i] = 0;
                d.active[i] = 0;
                d.effectorsBelow[i] = 0;
                d.pinnedOrigin[i] = 0;
                d.pinCount[i] = 0;
            }

            // 付け根の位置（親の枠で）と、親から見た向き
            for (int i = 0; i < n; i++) {
                int p = d.bones[i].parent;
                if (p < 0) {
                    d.anchorLocal[i] = float3.zero;
                    d.restRelative[i] = d.rotations[i];
                    continue;
                }
                quaternion parentInverse = math.inverse (d.rotations[p]);
                float3 anchor = math.rotate (parentInverse, d.positions[i] - d.positions[p]);
                d.anchorLocal[i] = anchor;
                d.restRelative[i] = math.mul (parentInverse, d.rotations[i]);
                d.childSum[p] += anchor;
                d.childCount[p] = d.childCount[p] + 1;
                d.extent[p] = math.max (d.extent[p], math.length (anchor));
            }

            // 骨の先（子の付け根の平均。先端の骨は、親から来た向きへ少し延ばす）と大きさ
            float extentSum = 0;
            for (int i = 0; i < n; i++) {
                int p = d.bones[i].parent;
                float3 tip;
                if (d.childCount[i] > 0) {
                    tip = d.childSum[i] / d.childCount[i];
                }
                else if (p >= 0) {
                    tip = math.rotate (math.inverse (d.rotations[i]), d.positions[i] - d.positions[p]) * 0.3f;
                    d.extent[i] = math.length (tip);
                }
                else {
                    tip = new float3 (0, 0.1f, 0);
                    d.extent[i] = 0.1f;
                }
                d.extent[i] = math.max (d.extent[i], 1e-3f);
                d.tipLocal[i] = tip;
                d.centerLocal[i] = tip * 0.5f;
                extentSum += d.extent[i];
            }
            float extentFloor = 0.5f * extentSum / n;

            // 重さ = その骨から先の骨の長さの合計（根ほど重い）
            for (int i = 0; i < n; i++) {
                d.mass[i] = d.extent[i];
                d.inertia[i] = 0;
                d.centers[i] = d.positions[i] + math.rotate (d.rotations[i], d.centerLocal[i]);
            }
            for (int i = n - 1; i >= 0; i--) {
                int p = d.bones[i].parent;
                if (p >= 0) d.mass[p] = d.mass[p] + d.mass[i];
            }
            // 回しにくさ = その骨から先の骨が全部付いたまま回るとしたときの値（骨は長さの棒）。
            // 骨 1 本ぶんだけで出すと、胸や腰が腕 1 本より回しやすくなり、手を引いたときに胴ごと傾く
            for (int k = 0; k < n; k++) {
                float radius = 0.5f * math.max (d.extent[k], extentFloor);
                for (int a = k; a >= 0; a = d.bones[a].parent) {
                    d.inertia[a] = d.inertia[a] + d.extent[k] * (math.distancesq (d.centers[k], d.centers[a]) + radius * radius);
                }
            }
            for (int i = 0; i < n; i++) {
                // 動かさないと指定された骨は、元の姿勢の場所と向きのまま（動かない物）
                bool fixedBone = d.fixedBones[i] != 0;
                d.inverseMass[i] = fixedBone ? 0 : 1 / d.mass[i];
                d.inverseInertia[i] = fixedBone ? 0 : 1 / d.inertia[i];
            }

            for (int i = 0; i < n; i++) PrepareHinge (ref d, i);
        }

        /// <summary>
        /// 蝶番の軸と曲がる範囲を、元の姿勢から決める。軸は親の骨と自分の骨が作る面の法線。
        /// まっすぐで面が決まらないときは、既定の曲がる向き（関節が出る向き）から決める
        /// </summary>
        static void PrepareHinge (ref FullBodyIkData d, int i)
        {
            FullBodyIkBone bone = d.bones[i];
            int p = bone.parent;
            if (bone.hinge == 0 || p < 0 || d.childCount[i] == 0) return;
            float3 upper = math.normalizesafe (d.positions[i] - d.positions[p]);
            float3 lower = math.normalizesafe (math.rotate (d.rotations[i], d.tipLocal[i]));
            if (math.lengthsq (upper) < 0.5f || math.lengthsq (lower) < 0.5f) return;

            // 定義の軸（親の骨と一緒に回る）。骨に直交する成分だけ使う
            float3 fallback = math.rotate (d.rotations[p], bone.hingeAxis);
            fallback = math.normalizesafe (fallback - upper * math.dot (fallback, upper));
            if (math.lengthsq (fallback) < 0.5f) {
                // 軸が無い・骨と平行: 骨に直交する向きを 1 つ選ぶ
                float3 side = math.abs (upper.x) < 0.9f ? new float3 (1, 0, 0) : new float3 (0, 1, 0);
                fallback = math.normalizesafe (math.cross (side, upper));
            }
            // 元の姿勢がはっきり曲がっていれば、その曲がりの面を使う。曲がりが小さい間は定義の軸
            // （立ち姿の膝のわずかな開きを曲がる面にすると、腰を下げたときに膝が横へ出る）。間は滑らかに移る
            float3 bend = math.cross (upper, lower);
            float sine = math.length (bend);
            float blend = math.smoothstep (kBendFrom, kBendTo, sine);
            if (blend < 1) d.status[FullBodyIkData.kStatusDefaultBend] = d.status[FullBodyIkData.kStatusDefaultBend] + 1;
            float3 axis = blend > 0 ? math.normalizesafe (math.lerp (fallback, bend / sine, blend), fallback) : fallback;

            quaternion parentInverse = math.inverse (d.rotations[p]);
            quaternion selfInverse = math.inverse (d.rotations[i]);
            d.hingeAxisParent[i] = math.rotate (parentInverse, axis);
            d.hingeAxisChild[i] = math.rotate (selfInverse, axis);
            d.hingeDirParent[i] = math.rotate (parentInverse, upper);
            d.hingeDirChild[i] = math.rotate (selfInverse, lower);
            // 元の姿勢が範囲の外（逆に曲げてある・曲げすぎ）なら、そこまでは許す（固定の無い姿勢を変えない）
            float angle = SignedAngle (upper, lower, axis);
            d.hingeRange[i] = new float2 (math.min (0, angle), math.max (kHingeMax, angle));
            d.hingeOn[i] = 1;
        }

        /// <summary>
        /// 目標ごとに、どの骨のどこへ掛けるかと、解く骨を決める。
        ///
        /// - 位置だけの目標で、その骨から先にほかの目標が無いとき: その骨は解かずに親に付けて動かし、目標は「親の上の、その骨の付け根」に掛ける
        ///   （手の点を引いたとき、手は前腕に付いたまま動く。手を別の剛体にすると、手の向きだけが元の向きに残ろうとする）
        /// - 強さ 1 の目標は、掛けた場所を目標へ置いて動かない物にする（その場所まわりに回るだけ）。向きの強さ 1 は回らない物にする
        /// </summary>
        static void PrepareEffectors (ref FullBodyIkData d, int effectorCount)
        {
            int n = d.bones.Length;
            for (int e = 0; e < effectorCount; e++) {
                d.effectorLambda[e] = float2.zero;
                d.residuals[e] = 0;
                d.effectorBody[e] = -1;
                d.effectorPoint[e] = float3.zero;
                d.effectorHard[e] = 0;
                if (!IsUsed (ref d, e, n)) continue;
                // 同じ骨の目標（付け根の点・骨の上の点）は 1 つと数える
                bool counted = false;
                for (int k = 0; k < e && !counted; k++) counted = IsUsed (ref d, k, n) && d.effectors[k].bone == d.effectors[e].bone;
                if (counted) continue;
                for (int i = d.effectors[e].bone; i >= 0; i = d.bones[i].parent) d.effectorsBelow[i] = d.effectorsBelow[i] + 1;
            }

            // 先に、骨そのものへ掛ける目標（向きも持つ付け根の点・先にほかの目標がある・同じ骨に骨の上の点がある・根／骨の上の点）。
            // 強さ 1 なら、掛けた点を目標へ置いて動かない物にする。同じ骨の 2 つ目の点は、1 つ目の点まわりに骨を回して寄せる。
            // どちらを 1 つ目にするかは priority（高い方が先。動かさないと決めた点を、つかんで動かしている点より優先する）、同じなら並びの順
            for (int pass = 0; pass < 2; pass++) {
                for (int e = 0; e < effectorCount; e++) {
                    if (!IsUsed (ref d, e, n) || (d.effectors[e].priority != 0) != (pass == 0)) continue;
                    bool offset = IsOffset (ref d, e);
                    if (!offset && IsOnParent (ref d, e, n, effectorCount)) continue;
                    FullBodyIkEffector effector = d.effectors[e];
                    int bone = effector.bone;
                    float3 point = offset ? effector.offset : float3.zero;
                    if (effector.positionWeight < 1 || d.inverseMass[bone] > 0) {
                        PinBody (ref d, e, bone, point);
                        continue;
                    }
                    d.effectorBody[e] = bone;
                    d.effectorPoint[e] = point;
                    d.effectorHard[e] = 1;
                    for (int i = bone; i >= 0 && d.active[i] == 0; i = d.bones[i].parent) d.active[i] = 1;
                    if (d.inverseInertia[bone] <= 0) {
                        // 3 つ目の点（手首＋手の 2 点）: 先の 2 点を結ぶ軸まわりに回して、3 つ目の点の方へ向ける（先の 2 点は動かない）
                        if (d.pinCount[bone] == 2) {
                            float3 axis = math.normalizesafe (math.rotate (d.rotations[bone], d.secondPin[bone] - d.centerLocal[bone]));
                            float3 have = math.rotate (d.rotations[bone], point - d.centerLocal[bone]);
                            float3 want = effector.position - d.centers[bone];
                            have -= axis * math.dot (have, axis);
                            want -= axis * math.dot (want, axis);
                            if (math.lengthsq (axis) > 0.5f && math.lengthsq (have) > 1e-12f && math.lengthsq (want) > 1e-12f) {
                                float turn = SignedAngle (math.normalize (have), math.normalize (want), axis);
                                d.rotations[bone] = math.normalizesafe (math.mul (quaternion.AxisAngle (axis, turn), d.rotations[bone]), d.rotations[bone]);
                            }
                            d.pinCount[bone] = 3;
                        }
                        continue;
                    }
                    if (effector.rotationWeight >= 1) {
                        d.rotations[bone] = math.normalizesafe (effector.rotation, d.rotations[bone]);
                    }
                    else {
                        // 骨の 2 点が決まるので、1 つ目の点まわりに近い側で回して 2 つ目の点の方へ向け、回らない物にする
                        // （2 点を結ぶ軸まわりの回りは、元の姿勢のまま。反復に任せると、この軸まわりに骨が勝手に回る）
                        float3 from = math.rotate (d.rotations[bone], point - d.centerLocal[bone]);
                        d.rotations[bone] = math.normalizesafe (math.mul (FromTo (from, effector.position - d.centers[bone]), d.rotations[bone]), d.rotations[bone]);
                        d.pinCount[bone] = 2;
                        d.secondPin[bone] = point;
                    }
                    d.inverseInertia[bone] = 0;
                }
            }
            // 次に、親へ掛ける目標。親がもう動かない物になっていたら（親の点・兄弟の点）、骨そのものへ掛ける
            for (int e = 0; e < effectorCount; e++) {
                if (!IsUsed (ref d, e, n) || IsOffset (ref d, e) || !IsOnParent (ref d, e, n, effectorCount)) continue;
                FullBodyIkEffector effector = d.effectors[e];
                int b = effector.bone;
                int p = d.bones[b].parent;
                if (effector.positionWeight >= 1 && d.inverseMass[p] <= 0) PinBody (ref d, e, b, float3.zero);
                else PinBody (ref d, e, p, d.anchorLocal[b]);
            }

            // 根を強さ 1 で留めるときは、根の付け根を動かない物にする（点で固定していなければ）
            if (d.rootPin >= 1) {
                for (int i = 0; i < n; i++) {
                    if (d.bones[i].parent >= 0 || d.active[i] == 0 || d.inverseMass[i] <= 0) continue;
                    float3 center = d.centerLocal[i];
                    if (d.inverseInertia[i] > 0) d.inverseInertia[i] = 1 / (1 / d.inverseInertia[i] + d.mass[i] * math.lengthsq (center));
                    d.centerLocal[i] = float3.zero;
                    d.centers[i] = d.inputPositions[i];
                    d.inverseMass[i] = 0;
                }
            }
        }

        static bool IsUsed (ref FullBodyIkData d, int e, int n)
        {
            FullBodyIkEffector effector = d.effectors[e];
            return effector.bone >= 0 && effector.bone < n && (effector.positionWeight > 0 || effector.rotationWeight > 0);
        }

        /// <summary>骨の付け根から離れた「骨の上の点」に掛かる目標か（offset が 0 でない）</summary>
        static bool IsOffset (ref FullBodyIkData d, int e)
        {
            return math.lengthsq (d.effectors[e].offset) > 1e-12f;
        }

        static bool IsOnParent (ref FullBodyIkData d, int e, int n, int effectorCount)
        {
            FullBodyIkEffector effector = d.effectors[e];
            if (effector.rotationWeight > 0 || d.bones[effector.bone].parent < 0 || d.effectorsBelow[effector.bone] != 1) return false;
            // 同じ骨に骨の上の点（かかとなど）があるときは、骨そのものを解く
            for (int k = 0; k < effectorCount; k++) {
                if (k != e && IsUsed (ref d, k, n) && d.effectors[k].bone == effector.bone && IsOffset (ref d, k)) return false;
            }
            return true;
        }

        /// <summary>目標 e を、骨 body の上の点 point（骨の枠で）に掛ける</summary>
        static void PinBody (ref FullBodyIkData d, int e, int body, float3 point)
        {
            FullBodyIkEffector effector = d.effectors[e];
            d.effectorBody[e] = body;
            d.effectorPoint[e] = point;
            // 掛けた骨から根までを解く
            for (int i = body; i >= 0 && d.active[i] == 0; i = d.bones[i].parent) d.active[i] = 1;

            if (effector.rotationWeight >= 1 && body == effector.bone) {
                float3 origin = d.centers[body] - math.rotate (d.rotations[body], d.centerLocal[body]);
                d.rotations[body] = math.normalizesafe (effector.rotation, d.rotations[body]);
                d.centers[body] = origin + math.rotate (d.rotations[body], d.centerLocal[body]);
                d.inverseInertia[body] = 0;
            }
            if (effector.positionWeight >= 1) {
                // 重心を掛けた点へ移す（その点まわりに回るだけの物になる）
                float3 shift = point - d.centerLocal[body];
                if (d.inverseInertia[body] > 0) d.inverseInertia[body] = 1 / (1 / d.inverseInertia[body] + d.mass[body] * math.lengthsq (shift));
                d.centerLocal[body] = point;
                d.centers[body] = effector.position;
                d.inverseMass[body] = 0;
                if (body == effector.bone && math.lengthsq (point) <= 1e-12f) d.pinnedOrigin[body] = 1;
            }
        }

        // ---- 拘束 ----

        /// <summary>
        /// 関節（子の付け根が親の上の決まった位置から離れない）・蝶番・硬さ
        /// </summary>
        static void SolveJoint (ref FullBodyIkData d, int i, bool soft)
        {
            int p = d.bones[i].parent;
            if (p < 0 || d.active[i] == 0) return;

            // 関節
            float3 parentArm = math.rotate (d.rotations[p], d.anchorLocal[i] - d.centerLocal[p]);
            float3 selfArm = math.rotate (d.rotations[i], -d.centerLocal[i]);
            float3 error = (d.centers[p] + parentArm) - (d.centers[i] + selfArm);
            float distance = math.length (error);
            if (distance > kEpsilon) {
                float3 direction = error / distance;
                float selfWeight = PointWeight (ref d, i, selfArm, direction);
                float parentWeight = PointWeight (ref d, p, parentArm, direction);
                float sum = selfWeight + parentWeight;
                if (sum > kEpsilon) {
                    float3 impulse = direction * (distance / sum);
                    ApplyImpulse (ref d, i, selfArm, impulse, distance);
                    ApplyImpulse (ref d, p, parentArm, -impulse, distance);
                }
            }

            if (d.hingeOn[i] != 0) {
                // 蝶番の軸をそろえる
                float3 parentAxis = math.rotate (d.rotations[p], d.hingeAxisParent[i]);
                float3 selfAxis = math.rotate (d.rotations[i], d.hingeAxisChild[i]);
                float3 cross = math.cross (selfAxis, parentAxis);
                float sine = math.length (cross);
                if (sine > kEpsilon) {
                    float angle = math.asin (math.min (sine, 1));
                    if (math.dot (selfAxis, parentAxis) < 0) angle = math.PI - angle;
                    float lambda = 0;
                    ApplyAngular (ref d, i, p, cross / sine * angle, 0, ref lambda);
                }
                // 曲がる範囲（逆に曲がらない）
                parentAxis = math.rotate (d.rotations[p], d.hingeAxisParent[i]);
                float3 upper = math.rotate (d.rotations[p], d.hingeDirParent[i]);
                float3 lower = math.rotate (d.rotations[i], d.hingeDirChild[i]);
                float bend = SignedAngle (upper, lower, parentAxis);
                float2 range = d.hingeRange[i];
                float clamped = math.clamp (bend, range.x, range.y);
                if (clamped != bend) {
                    float lambda = 0;
                    ApplyAngular (ref d, i, p, parentAxis * (clamped - bend), 0, ref lambda);
                }
            }

            // 硬さ: 親から見た向きを、元の姿勢の値へ戻すばね
            float stiffness = soft ? math.saturate (d.bones[i].stiffness) : 0;
            if (stiffness <= 0) return;
            quaternion target = math.mul (d.rotations[p], d.restRelative[i]);
            float3 correction = RotationVector (math.mul (target, math.inverse (d.rotations[i])));
            float spring = d.stiffnessLambda[i];
            ApplyAngular (ref d, i, p, correction, StiffnessCompliance (stiffness) * (d.inverseInertia[i] + d.inverseInertia[p]), ref spring);
            d.stiffnessLambda[i] = spring;
        }

        /// <summary>
        /// 位置を強さ 1 で固定した点から根までの骨を、点に届く距離まで直に引き寄せる。
        /// 骨の長さは変わらないので、点から骨 k 本ぶん離れた関節は、その k 本の長さの合計より遠くには居られない。
        /// 関節を 1 つずつ満たすだけだと、引っ張りが根へ伝わるのに反復を多く使う（軽い手足が重い胴を少しずつしか動かせない）。
        /// 遠すぎる関節を先に寄せておくと、少ない反復で届く（Kim, Chentanez, Müller, "Long Range Attachments", SCA 2012 の考え方）
        /// </summary>
        static void SolveReach (ref FullBodyIkData d, int e)
        {
            FullBodyIkEffector effector = d.effectors[e];
            int body = d.effectorBody[e];
            if (body < 0 || effector.positionWeight < 1 || d.effectorHard[e] != 0) return;
            float reach = math.length (d.effectorPoint[e]);
            for (int i = body; d.bones[i].parent >= 0; i = d.bones[i].parent) {
                int p = d.bones[i].parent;
                // 親の上の、子の付け根が付く場所
                float3 arm = math.rotate (d.rotations[p], d.anchorLocal[i] - d.centerLocal[p]);
                float3 error = effector.position - (d.centers[p] + arm);
                float distance = math.length (error);
                if (distance > reach + 1e-6f) {
                    float3 direction = error / distance;
                    float weight = PointWeight (ref d, p, arm, direction);
                    if (weight > kEpsilon) ApplyImpulse (ref d, p, arm, direction * ((distance - reach) / weight), distance - reach);
                }
                // 位置を固定した骨より先（根の側）は、その骨の点が受け持つ
                if (d.inverseMass[p] <= 0) break;
                reach += math.length (d.anchorLocal[i]);
            }
        }

        /// <summary>
        /// 蝶番（肘・膝）を、届くのに要るだけ曲げる（SolveReach の「近すぎる」側）。
        /// 蝶番の骨の上か、その子の骨の付け根が動かないとき（点で固定した・動かさない骨）、膝はそこから骨の長さだけ離れた所にしか居られない。
        /// 手前の関節（股）からの距離も骨の長さで決まるので、膝の場所は円の上に決まり、蝶番の軸で 1 点に決まる。そこへ直に寄せる。
        /// 関節を 1 つずつ満たすだけだと、まっすぐな脚の足を真上に上げたとき、膝のずれが骨の向きに沿うので回す腕が無く、膝が曲がり始めない。
        /// ずれが股と膝の間を行き来し続け、その取り分で、点まわりに回れる腰が少しずつ回る（腰を位置で固定していても上半身が傾く）
        /// </summary>
        static void SolveBends (ref FullBodyIkData d, int n)
        {
            for (int c = 0; c < n; c++) {
                if (d.inverseMass[c] > 0 || d.active[c] == 0) continue;
                // 骨そのものが蝶番: 膝（付け根）から、動かない点（重心を移した所）までの距離が決まる
                if (d.hingeOn[c] != 0) SolveBend (ref d, c, d.centers[c], math.length (d.centerLocal[c]));
                // 親が蝶番で、この骨の付け根が動かない（付け根で固定した・回らない）: 膝からこの骨の付け根までの距離が決まる
                int p = d.bones[c].parent;
                if (p >= 0 && d.hingeOn[p] != 0 && (d.inverseInertia[c] <= 0 || math.lengthsq (d.centerLocal[c]) <= 1e-12f)) {
                    SolveBend (ref d, p, d.centers[c] - math.rotate (d.rotations[c], d.centerLocal[c]), math.length (d.anchorLocal[c]));
                }
            }
        }

        /// <summary>
        /// 蝶番の骨 i の付け根（膝）を、手前の関節（股）から上の骨の長さ、target から lower だけ離れた円の上の、蝶番が曲がる側の点へ寄せる
        /// </summary>
        static void SolveBend (ref FullBodyIkData d, int i, float3 target, float lower)
        {
            int p = d.bones[i].parent;
            int g = p >= 0 ? d.bones[p].parent : -1;
            if (g < 0) return;
            float upper = math.length (d.anchorLocal[i]);
            if (upper < 1e-5f || lower < 1e-5f) return;
            float3 top = d.centers[g] + math.rotate (d.rotations[g], d.anchorLocal[p] - d.centerLocal[g]);
            float3 toTarget = target - top;
            float distance = math.length (toTarget);
            // 伸ばしきっても届かない（SolveReach が受け持つ）・畳みきっても近すぎるときは寄せない
            if (distance < 1e-6f || distance >= upper + lower || distance <= math.abs (upper - lower)) return;
            float3 u = toTarget / distance;
            float along = (upper * upper - lower * lower + distance * distance) / (2 * distance);
            float height = math.sqrt (math.max (0, upper * upper - along * along));
            // 曲がる側は、蝶番の軸まわりに正の向き（曲がる範囲の側。膝は前・肘は後ろ）
            float3 axis = math.rotate (d.rotations[p], d.hingeAxisParent[i]);
            float3 side = math.normalizesafe (math.cross (u, axis));
            if (math.lengthsq (side) < 0.5f) return;
            float3 wanted = top + u * along + side * height;
            MovePoint (ref d, p, math.rotate (d.rotations[p], d.anchorLocal[i] - d.centerLocal[p]), wanted);
            MovePoint (ref d, i, math.rotate (d.rotations[i], -d.centerLocal[i]), wanted);
        }

        /// <summary>骨 body の上の点（重心から arm）を wanted へ寄せる</summary>
        static void MovePoint (ref FullBodyIkData d, int body, float3 arm, float3 wanted)
        {
            float3 error = wanted - (d.centers[body] + arm);
            float distance = math.length (error);
            if (distance <= kEpsilon) return;
            float3 direction = error / distance;
            float weight = PointWeight (ref d, body, arm, direction);
            if (weight > kEpsilon) ApplyImpulse (ref d, body, arm, direction * (distance / weight), distance);
        }

        /// <summary>
        /// 根（腰）を、元の姿勢の位置と向きに柔らかく留める。位置は rootPin、向きは根の骨の硬さで留める
        /// </summary>
        static void SolveRoots (ref FullBodyIkData d, int n)
        {
            float pin = math.saturate (d.rootPin);
            for (int i = 0; i < n; i++) {
                if (d.bones[i].parent >= 0 || d.active[i] == 0) continue;
                float2 lambda = d.rootLambda[i];

                float3 arm = math.rotate (d.rotations[i], -d.centerLocal[i]);
                float3 error = d.inputPositions[i] - (d.centers[i] + arm);
                float distance = math.length (error);
                if (pin > 0 && distance > kEpsilon && d.inverseMass[i] > 0) {
                    float3 direction = error / distance;
                    float weight = PointWeight (ref d, i, arm, direction);
                    float compliance = StiffnessCompliance (pin) * d.inverseMass[i];
                    if (weight + compliance > kEpsilon) {
                        float delta = (distance - compliance * lambda.x) / (weight + compliance);
                        lambda.x += delta;
                        ApplyImpulse (ref d, i, arm, direction * delta, distance);
                    }
                }

                float stiffness = math.saturate (d.bones[i].stiffness);
                float3 correction = RotationVector (math.mul (d.inputRotations[i], math.inverse (d.rotations[i])));
                float angle = math.length (correction);
                float inertia = d.inverseInertia[i];
                if (stiffness > 0 && angle > kEpsilon && inertia > 0) {
                    float compliance = StiffnessCompliance (stiffness) * inertia;
                    float delta = (angle - compliance * lambda.y) / (inertia + compliance);
                    lambda.y += delta;
                    // 付け根を動かさずに回す
                    float3 origin = d.centers[i] - math.rotate (d.rotations[i], d.centerLocal[i]);
                    d.rotations[i] = Rotate (d.rotations[i], correction / angle * (delta * inertia));
                    if (d.inverseMass[i] > 0) d.centers[i] = origin + math.rotate (d.rotations[i], d.centerLocal[i]);
                }
                d.rootLambda[i] = lambda;
            }
        }

        /// <summary>
        /// 目標。強さ 1 は固定（必ず満たす）、1 未満は柔らかく寄せる（0 に近づくほど効かなくなり、0 で無いのと同じ）。
        /// soft が false のとき（仕上げ）は、必ず満たす物（動かない骨の上の点）だけ
        /// </summary>
        static void SolveEffector (ref FullBodyIkData d, int e, bool soft)
        {
            int n = d.bones.Length;
            FullBodyIkEffector effector = d.effectors[e];
            int b = d.effectorBody[e];
            if (b < 0) return;
            float2 lambda = d.effectorLambda[e];

            if (d.effectorHard[e] != 0) {
                // 骨はもうほかの点で動かない物になっている。その点まわりに骨を回して、骨の上の点を目標へ寄せる
                float3 arm = math.rotate (d.rotations[b], d.effectorPoint[e] - d.centerLocal[b]);
                float3 error = effector.position - (d.centers[b] + arm);
                float distance = math.length (error);
                if (distance <= kEpsilon) return;
                float3 direction = error / distance;
                float weight = PointWeight (ref d, b, arm, direction);
                if (weight > kEpsilon) ApplyImpulse (ref d, b, arm, direction * (distance / weight), distance);
                return;
            }
            if (!soft) return;

            float rotationWeight = math.saturate (effector.rotationWeight);
            if (rotationWeight > 0 && rotationWeight < 1 && b == effector.bone && d.inverseInertia[b] > 0) {
                float3 correction = RotationVector (math.mul (math.normalizesafe (effector.rotation, d.rotations[b]), math.inverse (d.rotations[b])));
                float angle = math.length (correction);
                if (angle > kEpsilon) {
                    float inertia = d.inverseInertia[b];
                    float compliance = (1 - rotationWeight) / rotationWeight * inertia;
                    float delta = (angle - compliance * lambda.y) / (inertia + compliance);
                    lambda.y += delta;
                    // 付け根を動かさずに回す
                    float3 origin = d.centers[b] - math.rotate (d.rotations[b], d.centerLocal[b]);
                    d.rotations[b] = Rotate (d.rotations[b], correction / angle * (delta * inertia));
                    if (d.inverseMass[b] > 0) d.centers[b] = origin + math.rotate (d.rotations[b], d.centerLocal[b]);
                }
            }

            float positionWeight = math.saturate (effector.positionWeight);
            if (positionWeight > 0 && positionWeight < 1) {
                float3 arm = math.rotate (d.rotations[b], d.effectorPoint[e] - d.centerLocal[b]);
                float3 error = effector.position - (d.centers[b] + arm);
                float distance = math.length (error);
                if (distance > kEpsilon) {
                    float3 direction = error / distance;
                    float weight = PointWeight (ref d, b, arm, direction);
                    float compliance = (1 - positionWeight) / positionWeight * d.inverseMass[b];
                    if (weight + compliance > kEpsilon) {
                        float delta = (distance - compliance * lambda.x) / (weight + compliance);
                        lambda.x += delta;
                        ApplyImpulse (ref d, b, arm, direction * delta, distance);
                    }
                }
            }
            d.effectorLambda[e] = lambda;
        }

        // ---- 仕上げ ----

        /// <summary>
        /// 骨の向きと根の位置から、付け根の位置を根から順に作り直す（関節がぴったりつながり、骨の長さが変わらない）。
        /// 作り直すと、詰めきれなかった関節のずれが固定した点の側へ寄るので、固定した点ごとに手前の骨を回して点へ合わせ直す。
        /// 元の姿勢から変わらなかった骨は、元の値をそのまま返す
        /// </summary>
        static void Finish (ref FullBodyIkData d, int n, int effectorCount)
        {
            // 根の位置（解いた根だけ。解かなかった根は元の値のまま）
            for (int i = 0; i < n; i++) {
                if (d.bones[i].parent < 0 && d.active[i] != 0) d.positions[i] = d.centers[i] - math.rotate (d.rotations[i], d.centerLocal[i]);
            }
            Rebuild (ref d, n, false);
            for (int e = 0; e < effectorCount; e++) {
                if (SnapToEffector (ref d, n, e)) Rebuild (ref d, n, false);
            }
            // 向きを強さ 1 で固定した骨は、合わせ直しで回った分を戻す
            for (int e = 0; e < effectorCount; e++) {
                FullBodyIkEffector effector = d.effectors[e];
                if (effector.bone < 0 || effector.bone >= n || effector.rotationWeight < 1) continue;
                // 骨の上の点（向きも留めた物）は、合わせ直しのときに向きを戻してある。ここで回すと、点が目標から外れる
                if (IsOffset (ref d, e)) continue;
                d.rotations[effector.bone] = math.normalizesafe (effector.rotation, d.rotations[effector.bone]);
            }
            Rebuild (ref d, n, true);
            for (int e = 0; e < effectorCount; e++) {
                FullBodyIkEffector effector = d.effectors[e];
                if (effector.bone < 0 || effector.bone >= n) continue;
                d.residuals[e] = math.distance (effector.position, d.positions[effector.bone] + math.rotate (d.rotations[effector.bone], effector.offset));
            }
        }

        /// <summary>
        /// 付け根の位置を、親から順に作り直す。解かなかった骨は親に付いて動く。
        /// round なら出力を丸める（元の姿勢と同じに丸まる骨は、元の値をそのまま返す）
        /// </summary>
        static void Rebuild (ref FullBodyIkData d, int n, bool round)
        {
            for (int i = 0; i < n; i++) {
                int p = d.bones[i].parent;
                if (d.active[i] == 0 && p >= 0) d.rotations[i] = math.mul (d.rotations[p], d.restRelative[i]);
                if (round) d.rotations[i] = RoundRotation (d.rotations[i], d.inputRotations[i]);
                if (p >= 0) d.positions[i] = d.positions[p] + math.rotate (d.rotations[p], d.anchorLocal[i]);
                else if (round) d.positions[i] = RoundPosition (d.positions[i], d.inputPositions[i]);
            }
        }

        /// <summary>
        /// 位置を強さ 1 で固定した点へ、骨の付け根を合わせ直す。
        /// 手前が蝶番（肘・膝）なら、蝶番の曲げ角で距離を合わせてから、その親を回して向きを合わせる（2 本の骨の IK。曲がる面は今の蝶番の軸のまま）。
        /// そうでなければ、親を回して向きだけ合わせる。骨を回したら true
        /// </summary>
        static bool SnapToEffector (ref FullBodyIkData d, int n, int e)
        {
            FullBodyIkEffector effector = d.effectors[e];
            int b = effector.bone;
            if (b < 0 || b >= n || effector.positionWeight < 1) return false;
            // 同じ骨の 2 つ目以降の点は動かさない（1 つ目の点が外れる。骨の向きは、準備のときに 2 つ目以降の点の方へ向けてある）
            if (d.effectorHard[e] != 0) return false;
            quaternion rotation = d.rotations[b];
            if (!IsOffset (ref d, e)) {
                // 付け根の点。骨が回らない物（向きも固定・同じ骨にほかの点もある）なら、手前の骨を合わせたときに一緒に回った分を戻す
                bool keep = d.inverseInertia[b] <= 0;
                if (!SnapOrigin (ref d, n, e, effector.position)) return false;
                if (keep) RotateSubtree (ref d, n, b, math.mul (rotation, math.inverse (d.rotations[b])));
                return true;
            }
            // 骨の上の点（付け根から離れた点）。
            // 骨の向きは今のままで、付け根が「目標 − 骨の上の点」に来るように手前の骨を合わせる。合わせると骨も一緒に回るので、向きを戻す
            float3 onBone = math.rotate (d.rotations[b], effector.offset);
            if (!SnapOrigin (ref d, n, e, effector.position - onBone)) return false;
            RotateSubtree (ref d, n, b, math.mul (rotation, math.inverse (d.rotations[b])));
            return true;
        }

        /// <summary>骨の付け根が target に来るように、手前の骨を合わせ直す。骨を回したら true</summary>
        static bool SnapOrigin (ref FullBodyIkData d, int n, int e, float3 target)
        {
            FullBodyIkEffector effector = d.effectors[e];
            int b = effector.bone;
            int p = d.bones[b].parent;
            if (p < 0) return false;
            float3 end = d.positions[b];

            int g = d.bones[p].parent;
            bool twoBone = g >= 0 && d.hingeOn[p] != 0 && d.pinnedOrigin[p] == 0 && d.inverseInertia[p] > 0 && d.inverseInertia[g] > 0;
            // 手足の先だけに点があるとき（肘・膝やその先にほかの点が無い）は、手足の形をいつも元の姿勢から決め直す
            bool freeLimb = twoBone && d.effectorsBelow[p] == 1;
            if (!freeLimb && math.distancesq (target, end) < 1e-14f) return false;
            if (freeLimb) {
                // 付け根と先の位置が決まっても、肘・膝がどちらを向くか（付け根と先を結ぶ軸まわりの回り）は位置からは決まらない。
                // 反復の結果に任せると、まっすぐに近い手足では決め手が無く、点を少し動かすたびに肘・膝の向きがばらつく（がくがくする）。
                // 上の骨と下の骨を「親から見た元の姿勢の向き」へ戻してから、下の 2 本の骨の IK で合わせる。
                // こうすると手足の形は、親の向き・付け根の位置・点の位置だけで滑らかに決まる
                int gp = d.bones[g].parent;
                quaternion upperRotation = gp >= 0 ? math.mul (d.rotations[gp], d.restRelative[g]) : d.inputRotations[g];
                RotateSubtree (ref d, n, g, math.mul (upperRotation, math.inverse (d.rotations[g])));
                RotateSubtree (ref d, n, p, math.mul (math.mul (upperRotation, d.restRelative[p]), math.inverse (d.rotations[p])));
                Rebuild (ref d, n, false);
                end = d.positions[b];
            }
            if (twoBone) {
                float upperLength = math.distance (d.positions[p], d.positions[g]);
                float lowerLength = math.distance (end, d.positions[p]);
                if (upperLength < 1e-5f || lowerLength < 1e-5f) return false;
                // 手足を伸ばしきっても届かないときは、手前の骨を近い順に回して、手足の付け根を点へ寄せる
                // （その先にほかの点がある骨は回さない。ほかの点がずれる）
                float reach = (upperLength + lowerLength) * 0.9995f;
                for (int a = d.bones[g].parent; a >= 0; a = d.bones[a].parent) {
                    float3 from = d.positions[g];
                    float distanceToTop = math.distance (target, from);
                    if (distanceToTop <= reach) break;
                    if (d.effectorsBelow[a] != 1 || d.inverseInertia[a] <= 0) break;
                    float3 wantedTop = target + (from - target) * (reach / distanceToTop);
                    RotateSubtree (ref d, n, a, FromTo (from - d.positions[a], wantedTop - d.positions[a]));
                    Rebuild (ref d, n, false);
                }
                float3 top = d.positions[g];
                float3 mid = d.positions[p];
                end = d.positions[b];
                float3 lower = end - mid;
                float3 axis = math.normalizesafe (math.rotate (d.rotations[g], d.hingeAxisParent[p]));
                float bend = SignedAngle ((mid - top) / upperLength, lower / lowerLength, axis);
                float2 range = d.hingeRange[p];
                // 曲げ角の見当は 2 本の骨が同じ面にあるとして出し、そのあと実際の距離で詰める
                // （立ち姿の膝のわずかな開きなど、蝶番の面から外れた分があると、見当だけでは数 mm ずれる）
                float wantedDistance = math.distance (target, top);
                float cosine = (upperLength * upperLength + lowerLength * lowerLength - wantedDistance * wantedDistance) / (2 * upperLength * lowerLength);
                float previous = bend;
                float previousError = math.distance (end, top) - wantedDistance;
                float wanted = math.clamp (math.PI - math.acos (math.clamp (cosine, -1, 1)), range.x, range.y);
                for (int step = 0; step < 4; step++) {
                    float error = math.distance (mid + math.rotate (quaternion.AxisAngle (axis, wanted - bend), lower), top) - wantedDistance;
                    if (math.abs (error) < 1e-6f || math.abs (error - previousError) < 1e-9f) break;
                    float next = math.clamp (wanted - error * (wanted - previous) / (error - previousError), range.x, range.y);
                    previous = wanted;
                    previousError = error;
                    wanted = next;
                }
                quaternion lowerTurn = quaternion.AxisAngle (axis, wanted - bend);
                RotateSubtree (ref d, n, p, lowerTurn);
                end = mid + math.rotate (lowerTurn, lower);
                RotateSubtree (ref d, n, g, FromTo (end - top, target - top));
                return true;
            }
            // 蝶番でない所: 手前の骨を近い順に、先が点を向くように回す。数回くり返して距離の差も詰める
            // （その先にほかの点がある骨・向きを固定した骨は回さない）
            bool turned = false;
            for (int pass = 0; pass < kSnapPasses; pass++) {
                int count = 0;
                for (int a = p; a >= 0 && count < kSnapBones; a = d.bones[a].parent, count++) {
                    if (d.effectorsBelow[a] != 1 || d.inverseInertia[a] <= 0) break;
                    end = d.positions[b];
                    if (math.distancesq (target, end) < 1e-14f) return turned;
                    RotateSubtree (ref d, n, a, FromTo (end - d.positions[a], target - d.positions[a]));
                    Rebuild (ref d, n, false);
                    turned = true;
                    // 付け根を点で固定した骨より先（根の側）は回さない
                    if (d.pinnedOrigin[a] != 0) break;
                }
            }
            return turned;
        }

        /// <summary>骨と、その先の解いた骨の向きを turn で回す（位置は Rebuild で作り直す）</summary>
        static void RotateSubtree (ref FullBodyIkData d, int n, int bone, quaternion turn)
        {
            for (int i = bone; i < n; i++) {
                if (d.active[i] == 0) continue;
                bool inside = false;
                for (int k = i; k >= 0 && !inside; k = d.bones[k].parent) inside = k == bone;
                if (inside) d.rotations[i] = math.normalizesafe (math.mul (turn, d.rotations[i]), d.rotations[i]);
            }
        }

        /// <summary>from の向きを to の向きへ、近い側で回す回転（向きが決まらないときは回さない）</summary>
        static quaternion FromTo (float3 from, float3 to)
        {
            float3 a = math.normalizesafe (from);
            float3 b = math.normalizesafe (to);
            float3 axis = math.cross (a, b);
            float sine = math.length (axis);
            if (sine <= kEpsilon) return quaternion.identity;
            return quaternion.AxisAngle (axis / sine, math.atan2 (sine, math.dot (a, b)));
        }

        static quaternion RoundRotation (quaternion value, quaternion input)
        {
            float4 v = value.value;
            // 同じ向きの 2 通りの表し方のうち、元の姿勢に近い側へそろえる
            if (math.dot (v, input.value) < 0) v = -v;
            float4 rounded = math.round (v * kRound) / kRound;
            float4 roundedInput = math.round (input.value * kRound) / kRound;
            if (math.all (rounded == roundedInput)) return input;
            return new quaternion (rounded);
        }

        static float3 RoundPosition (float3 value, float3 input)
        {
            float3 rounded = math.round (value * kRound) / kRound;
            float3 roundedInput = math.round (input * kRound) / kRound;
            if (math.all (rounded == roundedInput)) return input;
            return rounded;
        }

        // ---- 部品 ----

        /// <summary>骨の上の点（重心から arm）を direction へ押したときの動きやすさ</summary>
        static float PointWeight (ref FullBodyIkData d, int i, float3 arm, float3 direction)
        {
            float3 lever = math.cross (arm, direction);
            return d.inverseMass[i] + math.lengthsq (lever) * d.inverseInertia[i];
        }

        /// <summary>
        /// 骨の上の点（重心から arm）を impulse で押す（位置と向きが動く）。error は、この補正で消したい差（メートル）。
        /// 点が error より大きく動くほどは回さない: 押す向きが腕（arm）とほぼ平行だと、回しても差が縮まないので押す量が
        /// 際限なく大きくなり、骨がでたらめに回る（付け根を固定した骨を、届かない点へ引っ張ったとき。脚が伸びきった先へ足を引く、など）
        /// </summary>
        static void ApplyImpulse (ref FullBodyIkData d, int i, float3 arm, float3 impulse, float error)
        {
            d.centers[i] = d.centers[i] + impulse * d.inverseMass[i];
            float3 turn = math.cross (arm, impulse) * d.inverseInertia[i];
            float angle = math.length (turn);
            if (angle <= kEpsilon) return;
            float limit = math.min (kMaxStepAngle, error / math.max (math.length (arm), 1e-4f));
            if (angle > limit) turn *= limit / angle;
            d.rotations[i] = Rotate (d.rotations[i], turn);
        }

        /// <summary>
        /// 2 つの骨の向きの拘束。correction は「self をこれだけ回す（other は逆に回す）と誤差が消える」回転（軸 × 角度）。
        /// 回しにくさに応じて 2 つへ配る。compliance が 0 より大きければ柔らかい（lambda に力をためる）
        /// </summary>
        static void ApplyAngular (ref FullBodyIkData d, int self, int other, float3 correction, float compliance, ref float lambda)
        {
            float angle = math.length (correction);
            if (angle <= kEpsilon) return;
            float selfWeight = d.inverseInertia[self];
            float otherWeight = d.inverseInertia[other];
            float sum = selfWeight + otherWeight + compliance;
            if (sum <= kEpsilon) return;
            float3 axis = correction / angle;
            float delta = (angle - compliance * lambda) / sum;
            lambda += delta;
            // 関節（self の付け根）を動かさずに回す。重心まわりに回すと関節が離れ、つなぎ直すのに反復を使う
            float3 pivot = d.centers[self] - math.rotate (d.rotations[self], d.centerLocal[self]);
            if (selfWeight > 0) {
                d.rotations[self] = Rotate (d.rotations[self], axis * (delta * selfWeight));
                // 位置を固定した骨は動かさない（その場で回す）。動かすと、固定した点が目標から外れる
                if (d.inverseMass[self] > 0) d.centers[self] = pivot + math.rotate (d.rotations[self], d.centerLocal[self]);
            }
            if (otherWeight > 0) {
                // 位置を固定した骨は動かさない（その場で回す）
                bool pinned = d.inverseMass[other] <= 0;
                float3 offset = math.rotate (math.inverse (d.rotations[other]), d.centers[other] - pivot);
                d.rotations[other] = Rotate (d.rotations[other], axis * (-delta * otherWeight));
                if (!pinned) d.centers[other] = pivot + math.rotate (d.rotations[other], offset);
            }
        }

        static quaternion Rotate (quaternion rotation, float3 turn)
        {
            float angle = math.length (turn);
            if (angle <= kEpsilon) return rotation;
            return math.normalizesafe (math.mul (quaternion.AxisAngle (turn / angle, angle), rotation), rotation);
        }

        /// <summary>
        /// 硬さ（0〜1）を、ばねの柔らかさ（骨の回しやすさの何倍か）にする。0.5 で骨の回しやすさと同じ、1 で 0（曲がらない）。
        /// 2 乗にしているのは、硬さの値が 0.5〜0.9 のあたりで効き方がはっきり変わるようにするため（1 乗だと 0.95 以上でしか差が出ない）
        /// </summary>
        static float StiffnessCompliance (float stiffness)
        {
            if (stiffness >= 1) return 0;
            float ratio = (1 - stiffness) / math.max (stiffness, 1e-4f);
            return ratio * ratio;
        }

        /// <summary>回転を「軸 × 角度」にする（近い側の回り方で）</summary>
        static float3 RotationVector (quaternion rotation)
        {
            float4 v = rotation.value;
            if (v.w < 0) v = -v;
            float sine = math.length (v.xyz);
            if (sine <= kEpsilon) return float3.zero;
            return v.xyz / sine * (2 * math.atan2 (sine, v.w));
        }

        /// <summary>axis まわりに、from から to へ回した角度（ラジアン。-π〜π）</summary>
        static float SignedAngle (float3 from, float3 to, float3 axis)
        {
            return math.atan2 (math.dot (math.cross (from, to), axis), math.dot (from, to));
        }
    }

}
