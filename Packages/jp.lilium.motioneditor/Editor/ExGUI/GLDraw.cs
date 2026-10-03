using UnityEngine;

namespace Lilium
{

    /// <summary>
    /// GL で描くときの材質（骨ハンドルの線など）
    /// </summary>
    public class GLDraw
    {
        public static Material lineMaterial = null;
        public static Material sceneGUIMaterial = null;

        public static void Initialize ()
        {
            if (lineMaterial != null)
                return;

            lineMaterial = new Material (Shader.Find ("Lilium Motion Editor/Colored Blended Line"));
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            lineMaterial.shader.hideFlags = HideFlags.HideAndDontSave;

            sceneGUIMaterial = new Material (Shader.Find ("Lilium Motion Editor/Scene GUI"));
            sceneGUIMaterial.hideFlags = HideFlags.HideAndDontSave;
            sceneGUIMaterial.shader.hideFlags = HideFlags.HideAndDontSave;
        }
    }

}
