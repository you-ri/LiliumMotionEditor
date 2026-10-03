using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// Layers の設定の保存（✏・✋ の選択、段ごとの 👁 と重み、置く段の並び）。段そのものは対象から作り直すので、利用者が決めたことだけを持つ。
    /// 段は id で指す（区切り文字で 1 本の文字列にすると、ゲーム側が付けた段の名前と衝突しうるので、素直に配列で持つ）
    /// </summary>
    [System.Serializable]
    public sealed class LayerState
    {
        /// <summary>キーの書き出し先の段の id</summary>
        public string write;
        /// <summary>つかむ対象の段の id</summary>
        public string manipulate;
        /// <summary>
        /// 段ごとの 👁 と重み。重みは今のキャラに無い段の分も残す（キャラを行き来しても消えないように）。👁 は今ある段の分だけ
        /// </summary>
        public List<LayerValues> values = new List<LayerValues> ();
        /// <summary>焼いて書き出す段（Output）の並び（S21）。既定は無し（Layers の上端の「+Output」で足す）</summary>
        public List<OutputEntry> outputs = new List<OutputEntry> ();
        /// <summary>Humanoid Pose の段を置くか（S21。既定は置かない。構造を編集モードの「+Humanoid Pose」で足す）</summary>
        public bool humanoidPose;
        /// <summary>
        /// 元のクリップに重ねる Override（S21。キャラの設定に置く）。元のクリップごとに、上から重ねる順に並べる。
        /// キャラの設定が無いときは使わず、Override のクリップの .meta に置く（窓を閉じても消えないように）
        /// </summary>
        public List<OverrideRecord> overrides = new List<OverrideRecord> ();

        /// <summary>その段の値（無ければ null）</summary>
        public LayerValues Find (string id)
        {
            if (values == null) return null;
            for (int i = 0; i < values.Count; i++) {
                if (values[i] != null && values[i].id == id) return values[i];
            }
            return null;
        }
    }

    /// <summary>
    /// 段 1 つの、利用者が変えた値。持たない値（重みの無い段の weight など）は読むときに使わない
    /// </summary>
    [System.Serializable]
    public sealed class LayerValues
    {
        public string id = "";
        /// <summary>👁（落とすと素通し）</summary>
        public bool enabled = true;
        /// <summary>段の重み（Rig の層・Override など、重みを持つ段だけ）</summary>
        public float weight = 1;
        /// <summary>段のクリップを合成する重み（Editing Rig / Generic Pose / Humanoid Pose だけ）</summary>
        public float clipWeight = 1;
    }

    /// <summary>
    /// 元のクリップに重ねる Override 1 枚（S21）
    /// </summary>
    [System.Serializable]
    public sealed class OverrideRecord
    {
        /// <summary>元のクリップのアセットの GUID</summary>
        public string baseClip = "";
        /// <summary>元が FBX の中のクリップ（サブアセット）なら、そのクリップの名前。ファイルそのものなら空</summary>
        public string baseName = "";
        /// <summary>Override のクリップの GUID</summary>
        public string clip = "";
        /// <summary>合成の仕方（OverrideMode の名前）</summary>
        public string mode = "";
    }

    /// <summary>
    /// 焼いて書き出す段 1 つ（S21）。どの段の後に置くかと、焼いたクリップの名前に足す名前
    /// </summary>
    [System.Serializable]
    public sealed class OutputEntry
    {
        /// <summary>段の名前。空なら既定の段（焼いたクリップは 名前.anim）、それ以外は 名前.&lt;name&gt;.anim</summary>
        public string name = "";
        /// <summary>この段の直前の段の id（その段が無ければ Humanoid Pose の後、それも無ければ最後）</summary>
        public string after = "";
    }

}
