using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace Lilium
{

    /// <summary>
    /// Animation Rigging が入っているときだけ読み込まれるアセンブリ（defineConstraints: MKT_ANIMATION_RIGGING）。
    /// 表示モデルの Rigging を、窓が作るグラフ（姿勢注入）の後ろに足す（S4）。
    ///
    /// Rigging の拘束は AnimationStream に働くので、窓が Transform へ書いた姿勢は解いた瞬間に消える。
    /// そのため「姿勢を入れる Playable → Rig」というグラフを組む。並びはゲームの実行時（クリップ 90 → Rig 1000）と同じ。
    /// 使うのは Animation ウィンドウ・Timeline と同じプレビューの入口（StartPreview / BuildPreviewGraph / UpdatePreviewGraph / StopPreview）
    /// </summary>
    [InitializeOnLoad]
    static class RigPreviewAnimationRigging
    {
        static RigPreviewAnimationRigging ()
        {
            RigProbe.createPreview = Create;
        }

        static IRigPreview Create (GameObject model)
        {
            RigBuilder builder = model != null ? model.GetComponentInChildren<RigBuilder> (true) : null;
            if (builder == null || builder.layers.Count == 0) return null;
            return new Preview (builder);
        }

        sealed class Preview : IRigPreview
        {
            readonly RigBuilder builder_;

            public Preview (RigBuilder builder)
            {
                builder_ = builder;
            }

            public Playable Build (PlayableGraph graph, Playable input)
            {
                if (builder_ == null) return input;
                // プレビューの複製では止めてあるので、ここで動かす（OnEnable は再生中しか Build しないので副作用は無い）
                builder_.enabled = true;
                builder_.StartPreview ();
                return builder_.BuildPreviewGraph (graph, input);
            }

            public void Update (PlayableGraph graph)
            {
                if (builder_ != null) builder_.UpdatePreviewGraph (graph);
            }

            public void Stop ()
            {
                if (builder_ != null) builder_.StopPreview ();
            }
        }
    }

}
