Shader "VrcCpuWorld/NetlistDisplay"
{
    Properties
    {
        _StateTex ("GPU signal state", 2D) = "black" {}
        _ZeroColor ("Logic zero", Color) = (0.08, 0.36, 0.65, 1)
        _OneColor ("Logic one", Color) = (1, 0.7, 0.14, 1)
        _TextureWidth ("State texture width", Float) = 128
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _StateTex;
            float4 _ZeroColor, _OneColor;
            float _TextureWidth;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }
            float4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float value = tex2D(_StateTex, float2((input.uv.x + 0.5) / _TextureWidth, 0.5)).r;
                return lerp(_ZeroColor, _OneColor, step(0.5, value)) * input.color;
            }
            ENDCG
        }
    }
}
