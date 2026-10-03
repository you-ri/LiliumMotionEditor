using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.UIElements;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// 編集用リグの定義のインスペクタ（S23）。一覧は既定の描き方で、上に定義の誤りと「既定に戻す」を出す。
    /// 値を直すと、その定義を使っている窓が編集用リグを作り直す（EditRigDefinition.changed）
    /// </summary>
    [CustomEditor (typeof (EditRigDefinition))]
    sealed class EditRigDefinitionEditor : Editor
    {
        public override VisualElement CreateInspectorGUI ()
        {
            VisualElement root = new VisualElement ();
            root.Add (new HelpBox (
                "骨は HumanBodyBones で書き、キャラへは Avatar で割り当てる。キャラに無い骨のコントロールは無効になるだけ。" +
                "直すと、この定義を使っているモーションエディタの編集用リグがすぐ作り直される。",
                HelpBoxMessageType.Info));

            // パッケージの既定は、パッケージを更新すると戻る（git から入れたときは書き換えもできない）
            if (target == EditRigDefinition.packageDefault) {
                root.Add (new HelpBox (
                    "パッケージの既定の定義。直すときは Create > Lilium Motion Editor > Rig Definition で作り、" +
                    "Project Settings > Lilium Motion Editor の Rig Definition（全キャラ）かキャラの設定に入れる。",
                    HelpBoxMessageType.Warning));
            }

            HelpBox errors = new HelpBox ("", HelpBoxMessageType.Error);
            root.Add (errors);

            Button reset = new Button (ResetToDefault) { text = "既定に戻す", tooltip = "人型の骨すべての FK と、両腕・両脚の IK（脚は足の転がし付き）に戻す" };
            reset.style.alignSelf = Align.FlexStart;
            root.Add (reset);

            InspectorElement.FillDefaultInspector (root, serializedObject, this);

            UpdateErrors (errors);
            root.TrackSerializedObjectValue (serializedObject, so => UpdateErrors (errors));
            return root;
        }

        void UpdateErrors (HelpBox box)
        {
            EditRigDefinition definition = target as EditRigDefinition;
            List<string> errors = definition != null ? definition.Validate () : new List<string> ();
            box.text = string.Join ("\n", errors);
            box.style.display = errors.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ResetToDefault ()
        {
            EditRigDefinition definition = target as EditRigDefinition;
            if (definition == null) return;
            Undo.RecordObject (definition, "Reset Rig Definition");
            definition.SetDefault ();
            EditorUtility.SetDirty (definition);
            serializedObject.Update ();
            EditRigDefinition.NotifyChanged (definition);
        }
    }

    /// <summary>
    /// IK の組の 1 行。足の転がしの項目（つま先の骨・折れ角・支点の上書き）は、転がしを付けたときだけ出す
    /// </summary>
    [CustomPropertyDrawer (typeof (EditRigDefinition.IkChain))]
    sealed class IkChainDrawer : PropertyDrawer
    {
        static readonly string[] kChainFields = { "name", "root", "mid", "tip", "defaultHint", "reverseFoot" };
        static readonly string[] kFootFields = { "toes", "toeBreak", "pivots" };

        public override VisualElement CreatePropertyGUI (SerializedProperty property)
        {
            SerializedProperty name = property.FindPropertyRelative ("name");
            SerializedProperty reverseFoot = property.FindPropertyRelative ("reverseFoot");

            Foldout foldout = new Foldout { value = false };
            foreach (string field in kChainFields) {
                foldout.Add (new PropertyField (property.FindPropertyRelative (field)));
            }

            VisualElement foot = new VisualElement ();
            foot.style.paddingLeft = 12;
            foot.Add (new Label ("足の転がし（支点は足首から見た 右・上・前、メートル。無い支点は骨と Avatar から見積もる）") {
                style = { fontSize = 10, whiteSpace = WhiteSpace.Normal, color = new Color (0.7f, 0.7f, 0.7f) },
            });
            foreach (string field in kFootFields) {
                foot.Add (new PropertyField (property.FindPropertyRelative (field)));
            }
            foldout.Add (foot);

            System.Action update = () => {
                foldout.text = string.IsNullOrEmpty (name.stringValue) ? "(名前なし)" : name.stringValue;
                foot.style.display = reverseFoot.boolValue ? DisplayStyle.Flex : DisplayStyle.None;
            };
            update ();
            foldout.TrackPropertyValue (property, p => update ());
            return foldout;
        }
    }

}
