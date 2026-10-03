using UnityEngine;
using UnityEngine.UIElements;

namespace Lilium
{

    /// <summary>
    /// 非線形（指数）のスライダー。UE のカメラの移動速度のスライダーと同じく、つまみの位置 t（0〜1）を
    /// 最小 × (最大 / 最小)^t の値にする。小さい値は細かく、大きい値は大きく動く。
    /// 右の数値欄は実際の値で、直接入れると範囲の外でも入る（つまみは端に止まる）
    /// </summary>
    sealed class LogSlider : VisualElement
    {
        readonly float min_;
        readonly float max_;
        readonly Slider slider_;
        readonly FloatField field_;
        float value_;

        public event System.Action<float> valueChanged;

        public LogSlider (string label, float min, float max, float value)
        {
            min_ = Mathf.Max (1e-6f, min);
            max_ = Mathf.Max (min_ * 1.0001f, max);
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;

            slider_ = new Slider (label, 0, 1);
            slider_.style.flexGrow = 1;
            slider_.style.flexShrink = 1;
            slider_.RegisterValueChangedCallback (e => SetValue (ToValue (e.newValue), false));
            Add (slider_);

            field_ = new FloatField { isDelayed = true };
            field_.style.width = 50;
            field_.RegisterValueChangedCallback (e => SetValue (e.newValue, true));
            Add (field_);

            SetValueWithoutNotify (value);
        }

        public float value
        {
            get { return value_; }
        }

        public void SetValueWithoutNotify (float value)
        {
            value_ = Mathf.Max (1e-6f, value);
            slider_.SetValueWithoutNotify (ToPosition (value_));
            field_.SetValueWithoutNotify (Round (value_));
        }

        void SetValue (float value, bool moveSlider)
        {
            value_ = Mathf.Max (1e-6f, value);
            if (moveSlider) slider_.SetValueWithoutNotify (ToPosition (value_));
            field_.SetValueWithoutNotify (Round (value_));
            if (valueChanged != null) valueChanged (value_);
        }

        float ToValue (float position)
        {
            return min_ * Mathf.Pow (max_ / min_, Mathf.Clamp01 (position));
        }

        float ToPosition (float value)
        {
            return Mathf.Clamp01 (Mathf.Log (value / min_) / Mathf.Log (max_ / min_));
        }

        /// <summary>有効数字 3 桁ほどに丸めて見せる</summary>
        static float Round (float value)
        {
            float step = Mathf.Pow (10, Mathf.Floor (Mathf.Log10 (value)) - 2);
            return Mathf.Round (value / step) * step;
        }
    }

}
