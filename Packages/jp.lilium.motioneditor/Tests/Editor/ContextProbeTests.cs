using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// 文脈（一緒に見る演出）の受け口。時計の中身は Timeline のアセンブリが持つので、
    /// ここでは「拡張点がバインドを決める順番」だけを見る（読む → 拡張点 → 入れて作り直す）
    /// </summary>
    public class ContextProbeTests
    {
        sealed class Hook : IContextHook
        {
            public ContextInfo seen;

            public void Prepare (ContextInfo context, IList<ContextTrack> tracks)
            {
                seen = context;
                foreach (ContextTrack track in tracks) {
                    if (track.typeName == "AnimationTrack" && track.name == "Target Animation") track.Bind (context.instance.transform);
                }
            }
        }

        [Test]
        public void HooksDecideBindingsBeforeTheyAreApplied ()
        {
            System.Func<GameObject, ContextInfo> prepare = ContextProbe.prepare;
            System.Action<ContextInfo> apply = ContextProbe.apply;
            Hook hook = new Hook ();
            GameObject instance = new GameObject ("Cutscene");
            List<string> order = new List<string> ();
            try {
                ContextProbe.prepare = go => {
                    order.Add ("prepare");
                    ContextInfo info = new ContextInfo { instance = go, label = go.name, duration = 2 };
                    info.tracks.Add (new ContextTrack { name = "Self Animation", typeName = "AnimationTrack" });
                    info.tracks.Add (new ContextTrack { name = "Target Animation", typeName = "AnimationTrack" });
                    return info;
                };
                ContextProbe.apply = info => order.Add ("apply");
                ContextHooks.Register (hook);

                ContextInfo result = ContextProbe.Prepare (instance);

                CollectionAssert.AreEqual (new[] { "prepare", "apply" }, order, "読んでから拡張点、そのあと入れて作り直す");
                Assert.AreSame (result, hook.seen, "拡張点は同じ文脈を受け取る");
                Assert.AreEqual (2, result.duration);
                Assert.IsFalse (result.tracks[0].rebind, "触らなかったトラックはそのまま");
                Assert.IsTrue (result.tracks[1].rebind);
                Assert.AreSame (instance.transform, result.tracks[1].binding);
            }
            finally {
                ContextHooks.Unregister (hook);
                ContextProbe.prepare = prepare;
                ContextProbe.apply = apply;
                Object.DestroyImmediate (instance);
            }
        }

        /// <summary>
        /// Timeline が入っていない環境（時計の中身が無い）でも、置くだけはできて理由が出る
        /// </summary>
        [Test]
        public void WithoutAClockTheContextStillReportsWhy ()
        {
            System.Func<GameObject, ContextInfo> prepare = ContextProbe.prepare;
            System.Action<ContextInfo> apply = ContextProbe.apply;
            GameObject instance = new GameObject ("Cutscene");
            try {
                ContextProbe.prepare = null;
                ContextProbe.apply = null;
                ContextInfo info = ContextProbe.Prepare (instance);
                Assert.IsFalse (ContextProbe.available);
                Assert.AreSame (instance, info.instance);
                Assert.AreEqual (0, info.duration);
                StringAssert.Contains ("Timeline", info.notes[0]);
            }
            finally {
                ContextProbe.prepare = prepare;
                ContextProbe.apply = apply;
                Object.DestroyImmediate (instance);
            }
        }
    }

}
