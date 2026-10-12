Shader "VrcCpuWorld/CircuitLabOutputView"
{
    Properties
    {
        [PerRendererData] _MainTex ("UI Texture", 2D) = "white" {}
        _StateTex ("Gate state", 2D) = "black" {}
        _RamTex ("RAM", 2D) = "black" {}
        _Mode ("0 LED / 1 Life", Float) = 0
        _StateWidth ("State width", Float) = 1
        _Enabled ("Enabled", Float) = 1
        _BufferA ("RAM buffer A", Float) = 256
        _BufferB ("RAM buffer B", Float) = 320
        _Selector ("RAM selector", Float) = 224
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _StateTex, _RamTex;
            float _Mode, _StateWidth, _Enabled, _BufferA, _BufferB, _Selector;
            float4 _Out0, _Out1;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v) { v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color; return o; }
            float ram(int address) { return floor(tex2D(_RamTex, float2(((address % 32)+.5)/32., ((address/32)+.5)/32.)).r*255.+.5); }
            fixed4 frag(v2f i):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float bit = 0, shape;
                if (_Mode < .5)
                {
                    int slot = min(7,(int)(i.uv.x*8.)); int b=7-slot;
                    int id=b<4 ? (int)_Out0[b & 3] : (int)_Out1[b & 3];
                    bit=tex2D(_StateTex,float2((id+.5)/_StateWidth,.5)).r;
                    float2 p=float2(frac(i.uv.x*8.)-.5,(i.uv.y-.5)*1.25);
                    float d=length(p)-.30;
                    shape=1.-smoothstep(-fwidth(d),fwidth(d),d);
                }
                else
                {
                    int x=min(7,(int)(i.uv.x*8.)); int y=min(7,(int)((1.-i.uv.y)*8.));
                    int baseAddress=ram((int)_Selector)>.5 ? (int)_BufferB : (int)_BufferA;
                    bit=ram(baseAddress+y*8+x)>.5 ? 1. : 0.; bit *= _Enabled;
                    float2 p=abs(frac(i.uv*8.)-.5)-.44;
                    float d=max(p.x,p.y); shape=1.-smoothstep(-fwidth(d),fwidth(d),d);
                }
                fixed3 color=lerp(fixed3(.025,.055,.075),lerp(fixed3(.08,.21,.27),fixed3(.22,1.,.65),bit),shape);
                return fixed4(color,1)*i.color;
            }
            ENDCG
        }
    }
}
