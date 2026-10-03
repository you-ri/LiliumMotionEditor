using System.Reflection;
using UnityEditor;
using UnityEngine;
using Lilium;

namespace Lilium
{

    /// <summary>
    /// ゲーム側が [PoseLayer] を付けたコンポーネントの段。性質は属性から読む。
    /// 骨はつかめず、パラメータを見る段
    /// </summary>
    public sealed class UserComponentLayer : PoseLayer
    {
        readonly Component component_;
        readonly PoseLayerAttribute attribute_;
        readonly LayerInverseAttribute inverseAttribute_;
        readonly System.Action<float> evaluate_;
        readonly string idSuffix_;
        readonly PreviewStage stage_;
        readonly System.Type type_;

        public UserComponentLayer (Component component, System.Type type, Transform root, PreviewStage stage = null)
        {
            component_ = component;
            stage_ = stage;
            type_ = type;
            attribute_ = (PoseLayerAttribute)System.Attribute.GetCustomAttribute (type, typeof (PoseLayerAttribute));
            inverseAttribute_ = (LayerInverseAttribute)System.Attribute.GetCustomAttribute (type, typeof (LayerInverseAttribute));
            evaluate_ = FindEvaluate (component);
            // 同じ型が 2 つ付いていても分かれるように、置いてある場所まで入れる
            idSuffix_ = type.Name + "@" + AnimationUtility.CalculateTransformPath (component.transform, root);
            CollectParameters (component);
        }

        public override LayerKind kind { get { return LayerKind.User; } }
        public override string idSuffix { get { return idSuffix_; } }
        public override int order { get { return attribute_.order; } }
        public override string label { get { return string.IsNullOrEmpty (attribute_.label) ? component_.GetType ().Name : attribute_.label; } }
        public override PosePhase phase { get { return PosePhase.Graph; } }
        // 前進が Component の Evaluate にある段と、処理をグラフの後段に持つ段（受け口が名乗る）
        public override bool canEvaluate { get { return component_ != null && (evaluate_ != null || hasGraphHook); } }

        /// <summary>この段の処理をグラフの後段に持つ受け口があるか</summary>
        bool hasGraphHook
        {
            get {
                foreach (IPoseGraphHook hook in PoseGraphHooks.hooks) {
                    if (hook.layerType == type_) return true;
                }
                return false;
            }
        }
        public override string unavailableReason { get { return "パラメータを見る段（骨はつかめない）"; } }
        public override InverseKind inverse { get { return inverseAttribute_ != null ? inverseAttribute_.kind : InverseKind.None; } }
        public override string inverseReason { get { return inverseAttribute_ != null ? inverseAttribute_.reason : "逆が宣言されていない"; } }

        protected override string description
        {
            get {
                if (!string.IsNullOrEmpty (attribute_.note)) return attribute_.note;
                if (evaluate_ != null) return component_.gameObject.name;
                return hasGraphHook ? component_.gameObject.name + "（処理はグラフの後段）" : "前進解決が無い（戻り値の無い Evaluate (float) を足すと通る）";
            }
        }

        public override void Evaluate (float time)
        {
            // 処理をグラフの後段に持つ段は、ここでは「評価に入った」ことだけを伝える（実際に通すのは終端）
            if (stage_ != null) stage_.MarkLayerEvaluated (type_);
            if (evaluate_ == null) return;
            evaluate_ (time);
            // 骨を書き換えたかもしれないので、終端では表示から読み直す
            if (stage_ != null) stage_.InvalidateHumanoidPose ();
        }

        /// <summary>
        /// 前進解決の口。インターフェースを実装していれば優先し、無ければ public で戻り値の無い Evaluate (float) を探す
        /// </summary>
        static System.Action<float> FindEvaluate (Component component)
        {
            IPoseLayerEvaluate hook = component as IPoseLayerEvaluate;
            if (hook != null) return hook.Evaluate;

            MethodInfo method = component.GetType ().GetMethod ("Evaluate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof (float) }, null);
            // 戻り値のある Evaluate（カーブの値を返すような別物）は掴まない
            if (method == null || method.ReturnType != typeof (void)) return null;
            return time => method.Invoke (component, new object[] { time });
        }

        void CollectParameters (Component component)
        {
            const BindingFlags kFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo field in component.GetType ().GetFields (kFlags)) {
                LayerParameterAttribute attribute = (LayerParameterAttribute)System.Attribute.GetCustomAttribute (field, typeof (LayerParameterAttribute));
                if (attribute == null) continue;
                FieldInfo captured = field;
                Add (attribute, field.Name, field.FieldType, () => captured.GetValue (component), value => captured.SetValue (component, value));
            }
            // プロパティも拾う。フィールドへ直に書くとセッターの処理（見た目の切り替えなど）が飛ぶため
            foreach (PropertyInfo property in component.GetType ().GetProperties (kFlags)) {
                LayerParameterAttribute attribute = (LayerParameterAttribute)System.Attribute.GetCustomAttribute (property, typeof (LayerParameterAttribute));
                if (attribute == null || !property.CanRead) continue;
                PropertyInfo captured = property;
                Add (attribute, property.Name, property.PropertyType, () => captured.GetValue (component),
                    captured.CanWrite ? (System.Action<object>)(value => captured.SetValue (component, value)) : null);
            }
        }

        void Add (LayerParameterAttribute attribute, string name, System.Type type, System.Func<object> get, System.Action<object> set)
        {
            parameters.Add (new PoseParameter {
                label = string.IsNullOrEmpty (attribute.label) ? name : attribute.label,
                type = type,
                min = attribute.min,
                max = attribute.max,
                display = attribute.display,
                getValue = get,
                setValue = set,
            });
        }

    }

}
