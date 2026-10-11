Shader "VrcCpuWorld/NetlistSim"
{
    Properties
    {
        _MainTex ("Previous state", 2D) = "black" {}
        _Gates ("Gate instructions", 2D) = "black" {}
    }
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
            sampler2D _MainTex;
            sampler2D _Gates;
            int _NodeCount, _TextureWidth, _Mode, _InputMask;

            uint Bit(float id)
            {
                return tex2Dlod(_MainTex, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u;
            }

            float4 frag(v2f_img input) : SV_Target
            {
                uint id = (uint)(input.uv.x * _TextureWidth);
                if (id >= (uint)_NodeCount) return float4(0, 0, 0, 1);
                float4 instruction = tex2Dlod(_Gates, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0));
                uint op = (uint)round(instruction.x);
                uint value = 0u;
                if (op == 1u) value = 1u;
                else if (op == 2u) value = ((uint)_InputMask >> (uint)round(instruction.y)) & 1u;
                else if (_Mode == 0) value = 0u;
                else if (op == 3u) value = _Mode == 2 ? Bit(instruction.y) : Bit(id);
                else if (_Mode == 2) value = Bit(id);
                else
                {
                    uint a = Bit(instruction.y);
                    uint b = Bit(instruction.z);
                    if (op == 4u) value = a;
                    else if (op == 5u) value = 1u - a;
                    else if (op == 6u) value = a & b;
                    else if (op == 7u) value = a | b;
                    else if (op == 8u) value = a ^ b;
                    else if (op == 9u) value = 1u - (a ^ b);
                    else if (op == 10u) value = Bit(instruction.w) != 0u ? b : a;
                    else if (op == 11u) value = 1u - (a & b);
                    else if (op == 12u) value = 1u - (a | b);
                    else if (op == 13u) value = a & (1u - b);
                    else if (op == 14u) value = a | (1u - b);
                }
                return float4(value, 0, 0, 1);
            }
            ENDCG
        }
    }
}
