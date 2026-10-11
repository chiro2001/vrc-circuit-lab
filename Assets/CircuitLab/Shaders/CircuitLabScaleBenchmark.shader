// Independent scale experiment; not used by the production simulator.
Shader "VrcCpuWorld/CircuitLabScaleBenchmark"
{
    Properties { _MainTex ("State", 2D) = "black" {} _Gates ("Instructions", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _MainTex, _Gates;
            int _NodeCount, _TextureWidth, _TextureHeight, _Mode, _InputMask;
            float2 Coordinate(uint id)
            {
                return (float2(id % (uint)_TextureWidth, id / (uint)_TextureWidth) + .5) / float2(_TextureWidth, _TextureHeight);
            }
            uint Bit(uint id) { return tex2Dlod(_MainTex, float4(Coordinate(id), 0, 0)).r > .5 ? 1u : 0u; }
            float4 frag(v2f_img input) : SV_Target
            {
                uint id = (uint)(input.uv.x * _TextureWidth) + (uint)(input.uv.y * _TextureHeight) * (uint)_TextureWidth;
                if (id >= (uint)_NodeCount) return float4(0,0,0,1);
                float4 gate = tex2Dlod(_Gates, float4(Coordinate(id),0,0));
                uint op = (uint)round(gate.x), value = 0;
                if (op == 1u) value = 1;
                else if (op == 2u) value = ((uint)_InputMask >> (uint)round(gate.y)) & 1u;
                else if (_Mode == 0) value = 0;
                else if (op == 3u) value = _Mode == 2 ? Bit((uint)round(gate.y)) : Bit(id);
                else if (_Mode == 2) value = Bit(id);
                else
                {
                    uint a = Bit((uint)round(gate.y)), b = Bit((uint)round(gate.z));
                    if (op == 6u) value = a & b;
                    else if (op == 7u) value = a | b;
                    else if (op == 8u) value = a ^ b;
                }
                return float4(value,0,0,1);
            }
            ENDCG
        }
    }
}
