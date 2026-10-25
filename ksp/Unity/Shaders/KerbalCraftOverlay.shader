// Minecraft's hand, hotbar, hearts and screens: premultiplied alpha straight from Minecraft,
// drawn over everything. Minecraft's crosshair (an "invert" blend) arrives as white with no
// alpha, which premultiplied blending adds on top, so it stays visible.
Shader "KerbalCraft/Overlay"
{
	Properties { _MainTex ("Overlay", 2D) = "black" {} }
	SubShader
	{
		Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
		Cull Off ZWrite Off ZTest Always
		Blend One OneMinusSrcAlpha
		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
			v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; return o; }
			fixed4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
			ENDCG
		}
	}
}
