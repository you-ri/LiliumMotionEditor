using System.Collections;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 長い処理（IEnumerator）を、エディタの更新ごとに決めた時間ずつ回す。入れ子の IEnumerator を yield すると、その中を回し切ってから戻る。
    /// 取り込みのように数百ミリ秒かかる処理を一度に回すと、その間エディタが止まるので、分けて回すために使う。
    /// 一度に回し切る（Run）こともでき、結果は分けて回したときと同じ
    /// </summary>
    public sealed class Stepper : System.IDisposable
    {
        readonly Stack<IEnumerator> stack_ = new Stack<IEnumerator> ();

        public Stepper (IEnumerator routine)
        {
            if (routine != null) stack_.Push (routine);
        }

        public bool done
        {
            get { return stack_.Count == 0; }
        }

        /// <summary>
        /// seconds 秒を過ぎるまで回す（少なくとも 1 区切りは進む）。終わったら true。
        /// 中で投げた例外はそのまま出る（出たら途中の finally を回して終わる）
        /// </summary>
        public bool Step (double seconds)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();
            try {
                while (stack_.Count > 0) {
                    IEnumerator top = stack_.Peek ();
                    if (!top.MoveNext ()) {
                        stack_.Pop ();
                        (top as System.IDisposable)?.Dispose ();
                        continue;
                    }
                    if (top.Current is IEnumerator inner) {
                        stack_.Push (inner);
                        continue;
                    }
                    if (watch.Elapsed.TotalSeconds >= seconds) return false;
                }
                return true;
            }
            catch {
                Dispose ();
                throw;
            }
        }

        /// <summary>途中でやめる（中の finally を内側から回す）</summary>
        public void Dispose ()
        {
            while (stack_.Count > 0) (stack_.Pop () as System.IDisposable)?.Dispose ();
        }

        /// <summary>一度に回し切る</summary>
        public static void Run (IEnumerator routine)
        {
            using (Stepper stepper = new Stepper (routine)) stepper.Step (double.PositiveInfinity);
        }
    }

}
