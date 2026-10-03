Shader "Lilium Motion Editor/Colored Blended Line" {
	SubShader{
		Pass {
			Blend SrcAlpha OneMinusSrcAlpha
			Ztest Off ZWrite Off Cull Off Fog { Mode Off }
			BindChannels {
				Bind "vertex", vertex Bind "color", color
			}
		}
	}
}