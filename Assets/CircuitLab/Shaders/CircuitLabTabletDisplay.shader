Shader "VrcCpuWorld/CircuitLabTabletDisplay"
{
    Properties
    {
        [PerRendererData] _MainTex ("UI Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StateTex ("Current State", 2D) = "black" {}
        _GateTex ("Gate Instructions", 2D) = "black" {}
        _RomTex ("Program ROM", 2D) = "black" {}
        _RamTex ("Current RAM", 2D) = "black" {}
        _TextureWidth ("State Width", Float) = 1
        _NodeCount ("Node Count", Float) = 1
        _PageStart ("Page Start", Float) = 0
        _SelectedSignal ("Selected Signal", Float) = -1
        _DisplayMode ("Display Mode", Float) = 0
        _RawRam ("Raw RAM", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [HideInInspector] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            sampler2D _MainTex;
            sampler2D _StateTex;
            sampler2D _GateTex;
            sampler2D _RomTex;
            sampler2D _RamTex;
            fixed4 _Color;
            float4 _ClipRect;
            float _TextureWidth;
            float _NodeCount;
            float _PageStart;
            float _SelectedSignal;
            float _DisplayMode;
            float _RawRam;
            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 worldPosition : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }
            fixed3 GateColor(float op)
            {
                if (op < 0.5) return fixed3(.08, .16, .23);
                if (op < 2.5) return fixed3(.25, .52, .66);
                if (op < 3.5) return fixed3(.91, .68, .24);
                if (op < 6.5) return fixed3(.20, .65, .61);
                if (op < 10.5) return fixed3(.50, .43, .82);
                if (op < 14.5) return fixed3(.82, .38, .41);
                if (op < 15.5) return fixed3(.92, .75, .33);
                return fixed3(.32, .72, .90);
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                fixed3 color = fixed3(.025, .05, .075);
                float2 grid;
                float2 cell;
                if (_DisplayMode < .5)
                {
                    grid = uv * 8.0;
                    cell = frac(grid);
                    int x = (int)floor(grid.x);
                    int y = (int)floor((1.0 - uv.y) * 8.0);
                    int id = (int)_PageStart + y * 8 + x;
                    float bit = 0.0;
                    if (id >= 0 && id < (int)_NodeCount)
                        bit = tex2D(_StateTex, float2((id + .5) / max(1.0, _TextureWidth), .5)).r;
                    color = bit > .5 ? fixed3(.95, .67, .23) : fixed3(.08, .28, .38);
                    if (id == (int)_SelectedSignal) color = lerp(color, fixed3(.25, .95, .75), .55);
                    if (cell.x < .035 || cell.y < .035 || cell.x > .965 || cell.y > .965) color *= .35;
                    if (id < 0 || id >= (int)_NodeCount) color = fixed3(.035, .055, .07);
                }
                else if (_DisplayMode < 1.5)
                {
                    grid = uv * 16.0;
                    cell = frac(grid);
                    int x = (int)floor(grid.x);
                    int y = (int)floor((1.0 - uv.y) * 16.0);
                    int id = (int)_PageStart + y * 16 + x;
                    float4 gate = tex2D(_GateTex, float2((id + .5) / max(1.0, _TextureWidth), .5));
                    color = id < (int)_NodeCount ? GateColor(gate.r) : fixed3(.035, .055, .07);
                    float stripe = frac((gate.g + gate.b + gate.a) * .03125);
                    color *= .80 + .20 * stripe;
                    if (cell.x < .045 || cell.y < .045 || cell.x > .955 || cell.y > .955) color *= .35;
                }
                else if (_DisplayMode < 2.5)
                {
                    grid = uv * float2(32.0, 16.0);
                    cell = frac(grid);
                    int x = (int)floor(grid.x);
                    int y = (int)floor((1.0 - uv.y) * 16.0);
                    int address = y * 32 + x;
                    float4 word = tex2D(_RomTex, float2((address + .5) / 512.0, .5));
                    float low = word.r;
                    float high = word.g * 17.0;
                    color = fixed3(.08 + low * .72, .10 + high * .70, .22 + (low + high) * .28);
                    if (cell.x < .025 || cell.y < .04 || cell.x > .975 || cell.y > .96) color *= .30;
                }
                else
                {
                    grid = uv * 16.0;
                    cell = frac(grid);
                    int x = (int)floor(grid.x);
                    int y = (int)floor((1.0 - uv.y) * 16.0);
                    int address = y * 16 + x;
                    float4 value = tex2D(_RamTex, float2((x + .5) / 16.0, (y + .5) / 16.0));
                    float v = value.r;
                    color = _RawRam > .5 ? fixed3(v, v, v) : lerp(fixed3(.06, .26, .37), fixed3(.93, .67, .22), v);
                    if (address == (int)_SelectedSignal) color = lerp(color, fixed3(.30, .95, .78), .55);
                    if (cell.x < .045 || cell.y < .045 || cell.x > .955 || cell.y > .955) color *= .28;
                }
                fixed4 output = fixed4(color, 1.0) * i.color;
                #ifdef UNITY_UI_CLIP_RECT
                output.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(output.a - .001);
                #endif
                return output;
            }
            ENDCG
        }
    }
}
