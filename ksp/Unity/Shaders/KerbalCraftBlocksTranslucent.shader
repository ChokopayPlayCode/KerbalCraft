// Water, stained glass, ice: the same as KerbalCraft/Blocks but alpha-blended and never shadow casting.
Shader "KerbalCraft/BlocksTranslucent"
{
	Properties
	{
		_MainTex ("Atlas", 2D) = "white" {}
		_BlockGlow ("Block light glow", Range(0,2)) = 1.0
		_MinSky ("Darkest sky shade", Range(0,1)) = 0.35
	}
	SubShader
	{
		Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
		LOD 200
		CGPROGRAM
		#pragma surface surf Lambert vertex:vert alpha:fade
		sampler2D _MainTex;
		half _BlockGlow;
		half _MinSky;
		struct Input { float2 uv_MainTex; fixed4 col : COLOR; float2 light; };
		void vert (inout appdata_full v, out Input o)
		{
			UNITY_INITIALIZE_OUTPUT(Input, o);
			o.light = v.texcoord1.xy;
		}
		void surf (Input IN, inout SurfaceOutput o)
		{
			fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * IN.col;
			half sky = lerp(_MinSky, 1.0, IN.light.y);
			o.Albedo = c.rgb * sky;
			o.Emission = c.rgb * (IN.light.x * IN.light.x) * _BlockGlow;
			o.Alpha = c.a;
		}
		ENDCG
	}
	Fallback "Legacy Shaders/Transparent/VertexLit"
}
