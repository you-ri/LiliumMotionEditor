Shader "Lilium Motion Editor/Scene GUI" {
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