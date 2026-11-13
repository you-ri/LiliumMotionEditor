using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

namespace Lilium
{

    /// <summary>
    /// プレビュー窓の照明の出どころ
    /// </summary>
    public enum PreviewLighting
    {
        /// <summary>窓の決まった照明（カメラについて回る 2 灯と灰色の環境光）。開いているシーンに左右されない</summary>
        Default,
        /// <summary>開いているシーンの照明（主光源・環境光・反射。ゲーム側の受け口が足す物も）</summary>
        Scene,
    }

    /// <summary>
    /// プレビューのキャラを描く直前の受け口（任意）。[InitializeOnLoad] の静的コンストラクタなどから PreviewLightingHooks に登録する。
    /// シェーダーのグローバルの値でキャラの照明を変える仕組み（ゲーム側のキャラ用の照明など）は、そのままだと開いているシーンの値が
    /// 窓にも効く。既定の照明（PreviewLighting.Default）のときにそれを止める値を block に入れる
    /// </summary>
    public interface IPreviewLightingHook
    {
        /// <param name="block">renderer に付ける値（窓が環境光・反射を入れた後）。足すだけで、Clear しない</param>
        void Apply (Renderer renderer, MaterialPropertyBlock block, PreviewLighting lighting);
    }

    public static class PreviewLightingHooks
    {
        static readonly HookList<IPreviewLightingHook> hooks_ = new HookList<IPreviewLightingHook> ();

        public static IReadOnlyList<IPreviewLightingHook> hooks
        {
            get { return hooks_.items; }
        }

        /// <summary>受け口を足す。同じものは 1 回だけ</summary>
        public static void Register (IPreviewLightingHook hook)
        {
            hooks_.Register (hook);
        }

        public static void Unregister (IPreviewLightingHook hook)
        {
            hooks_.Unregister (hook);
        }

        internal static void Apply (Renderer renderer, MaterialPropertyBlock block, PreviewLighting lighting)
        {
            hooks_.ForEach (hook => hook.Apply (renderer, block, lighting));
        }
    }

    public sealed partial class PreviewStage
    {
        static readonly int kEnableProbeVolumes = Shader.PropertyToID ("_EnableProbeVolumes");
        static readonly int kGlossyEnvironmentCubeMap = Shader.PropertyToID ("_GlossyEnvironmentCubeMap");
        static readonly int kGlossyEnvironmentCubeMapHdr = Shader.PropertyToID ("_GlossyEnvironmentCubeMap_HDR");
        static readonly int kSpecCube0 = Shader.PropertyToID ("unity_SpecCube0");
        static readonly int kSpecCube0Hdr = Shader.PropertyToID ("unity_SpecCube0_HDR");
        static readonly Color kFillLightColor = new Color (0.75f, 0.8f, 1f);
        const float kKeyLightIntensity = 1.1f;
        const float kFillLightIntensity = 0.5f;
        const int kReflectionSize = 8;
        /// <summary>ライトの反転（縦軸まわりに半周。手前から当たる光が奥から当たり、こちらを向いた面が暗くなる）</summary>
        static readonly Quaternion kFlip = Quaternion.Euler (0, 180, 0);

        MaterialPropertyBlock lightingBlock_;
        Cubemap defaultReflection_;
        readonly SphericalHarmonicsL2[] ambientProbe_ = new SphericalHarmonicsL2[1];
        readonly List<GameObject> lightingRoots_ = new List<GameObject> ();
        readonly List<Renderer> lightingRenderers_ = new List<Renderer> ();
        readonly List<Renderer> lightingFound_ = new List<Renderer> ();

        /// <summary>
        /// 描く直前に、ライトと、置いた物（キャラ・文脈）の環境光・反射を決める。
        /// 既定の照明はカメラについて回る（どこから見ても手前の左上から当たる）。シーンの照明は開いているシーンの主光源の向きのまま
        /// </summary>
        /// <param name="flip">ライトを縦軸まわりに 180° 回す（こちらを向いた面が暗くなり、ハンドルが見やすくなる）</param>
        public void UpdateLights (Quaternion viewRotation, PreviewLighting lighting = PreviewLighting.Default, bool flip = false)
        {
            if (renderUtility == null) return;
            Light[] lights = renderUtility.lights;
            Quaternion flipRotation = flip ? kFlip : Quaternion.identity;
            if (lighting == PreviewLighting.Scene) {
                Light sun = FindSceneSun ();
                lights[0].intensity = sun != null ? sun.intensity : 0;
                lights[0].color = sun != null ? LightColor (sun) : Color.white;
                // シーンの光はワールドの向き。反転もワールドの縦軸まわり
                lights[0].transform.rotation = flipRotation * (sun != null ? sun.transform.rotation : Quaternion.identity);
                lights[1].intensity = 0;
                ambientProbe_[0] = RenderSettings.ambientProbe;
            }
            else {
                lights[0].intensity = kKeyLightIntensity;
                lights[0].color = Color.white;
                lights[1].intensity = kFillLightIntensity;
                lights[1].color = kFillLightColor;
                lights[0].transform.rotation = viewRotation * flipRotation * Quaternion.Euler (35, 35, 0);
                lights[1].transform.rotation = viewRotation * flipRotation * Quaternion.Euler (10, 200, 0);
                // PreviewRenderUtility の環境光（Flat の 1 色）と同じ値を自分で渡す（シーンの Light Probe を拾わないように）
                ambientProbe_[0] = new SphericalHarmonicsL2 ();
                ambientProbe_[0].AddAmbientLight (kAmbientColor.linear);
            }
            ApplyRendererLighting (lighting);
        }

        /// <summary>
        /// 置いた物のレンダラーに環境光・反射を渡す。プレビューの別シーンでも、URP は開いているシーンの APV・空の反射を使わせるので、
        /// 既定の照明ではそれを止めて窓の値に差し替える
        /// </summary>
        void ApplyRendererLighting (PreviewLighting lighting)
        {
            if (lightingBlock_ == null) lightingBlock_ = new MaterialPropertyBlock ();
            Texture reflection;
            Vector4 reflectionHdr;
            if (lighting == PreviewLighting.Scene) {
                reflection = ReflectionProbe.defaultTexture;
                reflectionHdr = ReflectionProbe.defaultTextureHDRDecodeValues;
            }
            else {
                reflection = DefaultReflection ();
                reflectionHdr = new Vector4 (1, 1, 0, 0);
            }

            // 置いた物は窓の操作（文脈の出し入れ・読み直し）で変わるので、描くたびに集める
            lightingRenderers_.Clear ();
            scene_.GetRootGameObjects (lightingRoots_);
            foreach (GameObject root in lightingRoots_) {
                if (root == floor_) continue;
                root.GetComponentsInChildren (true, lightingFound_);
                lightingRenderers_.AddRange (lightingFound_);
            }

            foreach (Renderer renderer in lightingRenderers_) {
                if (renderer == null) continue;
                // 環境光は窓から渡す（PreviewRenderUtility.Render は環境光を 1 色に決め直すので、シーンの環境光を出せない）
                renderer.lightProbeUsage = LightProbeUsage.CustomProvided;
                // 値は窓がすべて持つ（置いた物は見た目以外のスクリプトを外してあるので、ほかに値を入れる物は無い）
                lightingBlock_.Clear ();
                lightingBlock_.CopySHCoefficientArraysFrom (ambientProbe_);
                // APV を焼いたシーンを開いていると、URP がプレビューにも APV を使わせ、上の環境光が読まれない（シーンの位置の光になる）
                if (lighting == PreviewLighting.Default) lightingBlock_.SetInteger (kEnableProbeVolumes, 0);
                // 反射（Forward+ は _GlossyEnvironmentCubeMap、それ以外は unity_SpecCube0 を読む）
                if (reflection != null) {
                    lightingBlock_.SetTexture (kGlossyEnvironmentCubeMap, reflection);
                    lightingBlock_.SetVector (kGlossyEnvironmentCubeMapHdr, reflectionHdr);
                    lightingBlock_.SetTexture (kSpecCube0, reflection);
                    lightingBlock_.SetVector (kSpecCube0Hdr, reflectionHdr);
                }
                PreviewLightingHooks.Apply (renderer, lightingBlock_, lighting);
                renderer.SetPropertyBlock (lightingBlock_);
            }
        }

        /// <summary>既定の照明の反射。環境光と同じ 1 色の cubemap（空の代わり）</summary>
        Cubemap DefaultReflection ()
        {
            if (defaultReflection_ != null) return defaultReflection_;
            defaultReflection_ = new Cubemap (kReflectionSize, TextureFormat.RGBAHalf, true) { hideFlags = HideFlags.HideAndDontSave };
            Color[] pixels = new Color[kReflectionSize * kReflectionSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = kAmbientColor.linear;
            for (int face = 0; face < 6; face++) defaultReflection_.SetPixels (pixels, (CubemapFace)face);
            defaultReflection_.Apply (true);
            assets_.Add (defaultReflection_);
            return defaultReflection_;
        }

        /// <summary>
        /// 開いているシーンの主光源。Lighting 窓の Sun Source があればそれ、無ければ有効なディレクショナルライトで一番強い物
        /// </summary>
        static Light FindSceneSun ()
        {
            Light sun = RenderSettings.sun;
            if (sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional) return sun;
            sun = null;
            foreach (Light light in Object.FindObjectsByType<Light> (FindObjectsSortMode.None)) {
                if (light == null || !light.isActiveAndEnabled || light.type != LightType.Directional) continue;
                if (!light.gameObject.scene.IsValid () || EditorSceneManager.IsPreviewScene (light.gameObject.scene)) continue;
                if (sun == null || light.intensity > sun.intensity) sun = light;
            }
            return sun;
        }

        /// <summary>ライトの色（色温度を使うライトは、その色を掛ける）</summary>
        static Color LightColor (Light light)
        {
            Color color = light.color;
            if (light.useColorTemperature && GraphicsSettings.lightsUseColorTemperature) {
                color *= Mathf.CorrelatedColorTemperatureToRGB (light.colorTemperature);
            }
            return color;
        }
    }

}
