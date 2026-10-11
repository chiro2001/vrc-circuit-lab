Shader "VrcCpuWorld/CircuitLabDisplay"
{
    Properties
    {
        _StateTex ("Signal state", 2D) = "black" {}
        _TextureWidth ("State texture width", Float) = 1024
        _SignalOverride ("Fixed signal (-1 uses mesh UV)", Float) = -1
        _ZeroColor ("Logic zero", Color) = (0.10,0.46,0.76,1)
        _OneColor ("Logic one", Color) = (1.0,0.72,0.17,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _StateTex;
            float _TextureWidth, _SignalOverride;
            float4 _ZeroColor, _OneColor;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex:SV_POSITION; float signal:TEXCOORD0; float4 color:COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.signal = _SignalOverride >= 0 ? _SignalOverride : v.uv.x;
                o.color = v.color;
                return o;
            }
            float4 frag(v2f i):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Interpolation can place an integer ID just below its value.
                // Round before point sampling to avoid reading the adjacent net.
                float value = tex2D(_StateTex, float2((floor(i.signal+.5)+.5)/_TextureWidth,.5)).r;
                float4 tint = lerp(_ZeroColor,_OneColor,step(.5,value));
                return float4(tint.rgb * i.color.rgb,1);
            }
            ENDCG
        }
    }
}
