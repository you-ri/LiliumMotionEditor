using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.UIElements;

namespace Lilium
{

    /// <summary>
    /// 角度制限の 1 軸（S95）を 1 行で出す: 決め方・最小・最大。最小と最大は「範囲」のときだけ直せる
    /// </summary>
    [CustomPropertyDrawer (typeof (EditRigDefinition.AxisLimit))]
    sealed class AxisLimitDrawer : PropertyDrawer
    {
        const float kNumberWidth = 52;

        public override VisualElement CreatePropertyGUI (SerializedProperty property)
        {
            SerializedProperty mode = property.FindPropertyRelative ("mode");
            EnumField modeField = new EnumField (property.displayName, EditRigDefinition.LimitMode.Free) { tooltip = property.tooltip };
            modeField.AddToClassList (BaseField<System.Enum>.alignedFieldUssClassName);
            modeField.style.flexGrow = 1;
            modeField.BindProperty (mode);

            FloatField min = Number (property.FindPropertyRelative ("min"));
            FloatField max = Number (property.FindPropertyRelative ("max"));
            VisualElement row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            row.Add (modeField);
            row.Add (min);
            row.Add (max);

            // 値はバインドで入るので、入ったとき（と変えたとき）に合わせる
            modeField.RegisterValueChangedCallback (e => {
                bool range = e.newValue is EditRigDefinition.LimitMode limitMode && limitMode == EditRigDefinition.LimitMode.Range;
                min.SetEnabled (range);
                max.SetEnabled (range);
            });
            return row;
        }

        static FloatField Number (SerializedProperty property)
        {
            FloatField field = new FloatField { tooltip = property.displayName };
            field.style.width = kNumberWidth;
            field.BindProperty (property);
            return field;
        }
    }

}
