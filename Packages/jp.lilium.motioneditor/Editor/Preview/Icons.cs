using UnityEngine;
using UnityEditor;

namespace Lilium
{

    /// <summary>
    /// パネル・窓のアイコン（Editor/Icons）。絵は Material Icons（Apache License 2.0。Editor/Icons/LICENSE-MaterialIcons.txt）を
    /// エディタの色に塗り直したもの。暗いテーマ用は「d_」、高解像度用は「@2x」を付けたファイル
    /// </summary>
    static class Icons
    {
        public const string kFolder = "Packages/jp.lilium.motioneditor/Editor/Icons/";

        /// <summary>
        /// 今のテーマ・画面の倍率に合うアイコン（窓のタブなど、[Icon] 属性で付けられない所に使う）
        /// </summary>
        public static Texture2D Load (string name)
        {
            string prefix = EditorGUIUtility.isProSkin ? "d_" : "";
            Texture2D icon = EditorGUIUtility.pixelsPerPoint > 1 ? AssetDatabase.LoadAssetAtPath<Texture2D> (kFolder + prefix + name + "@2x.png") : null;
            return icon != null ? icon : AssetDatabase.LoadAssetAtPath<Texture2D> (kFolder + prefix + name + ".png");
        }
    }

}
