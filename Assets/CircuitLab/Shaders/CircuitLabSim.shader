Shader "VrcCpuWorld/CircuitLabSim"
{
    Properties
    {
        _MainTex ("Previous state", 2D) = "black" {}
        _Gates ("Gate instructions", 2D) = "black" {}
        _ProgramTex ("Program ROM", 2D) = "black" {}
        _RamTex ("Current RAM", 2D) = "black" {}
        _RestoreState ("DFF restore state", 2D) = "black" {}
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
            #define RAM_TEXTURE_WIDTH 32u
            sampler2D _MainTex, _Gates, _ProgramTex, _RamTex;
            sampler2D _RestoreState;
            int _NodeCount, _TextureWidth, _Mode, _InputMask;
            // Measurement probes, both disabled (0) in every shipped configuration.
            // _ProbeMode 1 skips the whole node evaluation for blit-cost baselines.
            // _TrackChanges makes .g carry how many passes a node has held its value
            // (its quiet streak) and .b carry the window minimum of that streak. The
            // solver reads them back to learn how deep a settle really had to run, so
            // waves that converge early do not pay for the full combinational depth.
            int _TrackChanges, _PassIndex, _ProbeMode;
            float4 _RomAddressA, _RomAddressB, _RomAddressC;
            float4 _RamReadAddressA, _RamReadAddressB, _RamReadAddressC;

            uint Bit(float id)
            {
                if (id < 0 || id >= _NodeCount) return 0u;
                return tex2Dlod(_MainTex, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u;
            }

            float AddressSignal(float4 a, float4 b, float4 c, int bit)
            {
                if (bit < 4) return a[bit & 3];
                if (bit < 8) return b[bit & 3];
                return c[bit & 3];
            }

            uint BusBit(float4 a, float4 b, float4 c, int bit)
            {
                return Bit(AddressSignal(a, b, c, bit));
            }

            uint RomAddress()
            {
                uint address = 0u;
                [unroll] for (int bit = 0; bit < 9; bit++) address |= BusBit(_RomAddressA, _RomAddressB, _RomAddressC, bit) << bit;
                return address;
            }

            uint RamReadAddress()
            {
                uint address = 0u;
                [unroll] for (int bit = 0; bit < 10; bit++) address |= BusBit(_RamReadAddressA, _RamReadAddressB, _RamReadAddressC, bit) << bit;
                return address;
            }

            uint RomBit(uint bit)
            {
                uint address = RomAddress();
                float4 encoded = tex2Dlod(_ProgramTex, float4((address + 0.5) / 512.0, 0.5, 0, 0));
                uint word = (uint)round(encoded.r * 255.0) | ((uint)round(encoded.g * 255.0) << 8);
                return (word >> bit) & 1u;
            }

            uint RamBit(uint bit)
            {
                uint address = RamReadAddress();
                float2 uv = float2((address % RAM_TEXTURE_WIDTH + 0.5) / RAM_TEXTURE_WIDTH,
                    (address / RAM_TEXTURE_WIDTH + 0.5) / RAM_TEXTURE_WIDTH);
                uint value = (uint)round(tex2Dlod(_RamTex, float4(uv, 0, 0)).r * 255.0);
                return (value >> bit) & 1u;
            }

            float4 frag(v2f_img input) : SV_Target
            {
                if (_ProbeMode == 1) return float4(0, 0, 0, 1);
                uint id = (uint)(input.uv.x * _TextureWidth);
                if (id >= (uint)_NodeCount) return float4(0, 0, 0, 1);
                float4 instruction = tex2Dlod(_Gates, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0));
                uint op = (uint)round(instruction.x);
                uint value = 0u;
                if (op == 1u) value = 1u;
                else if (op == 2u) value = ((uint)_InputMask >> (uint)round(instruction.y)) & 1u;
                else if (_Mode == 3)
                {
                    if (op == 3u) value = tex2Dlod(_RestoreState, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u;
                    else value = Bit(id);
                }
                else if (_Mode == 0) value = 0u;
                // Mode 5 rewrites each node with its own value: a self copy used to
                // restart the probe window without disturbing the logic state.
                else if (_Mode == 5) value = Bit(id);
                // Mode 2 reads every D from the same old state, so all DFFs commit atomically.
                else if (op == 3u) value = _Mode == 2 ? Bit(instruction.y) : Bit(id);
                else if (_Mode == 2) value = Bit(id);
                else
                {
                    uint a = Bit(instruction.y), b = Bit(instruction.z);
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
                    else if (op == 15u) value = RomBit((uint)round(instruction.y));
                    else if (op == 16u) value = RamBit((uint)round(instruction.y));
                }
                float4 result = float4(value, 0, 0, 1);
                // The probe window restart must work even while change tracking is
                // off, so a later window never inherits a stale minimum.
                if (_Mode == 5) { result.g = 0.0; result.b = 1.0; }
                else if (_TrackChanges != 0)
                {
                    float4 previousSelf = tex2Dlod(_MainTex, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0));
                    uint previousValue = previousSelf.r > 0.5 ? 1u : 0u;
                    // The state texture is ARGB32 (8 bits per channel), so the pass
                    // counter is kept in 1/255 steps and saturates at 1.0.
                    float streak = previousValue == value ? min(previousSelf.g + (1.0 / 255.0), 1.0) : 0.0;
                    result.g = streak;
                    result.b = previousSelf.b;
                    if (_Mode == 2) result.g = 0.0;                       // register commit restarts the streak
                    else if (_Mode == 6) result.b = min(previousSelf.b, streak);  // fold the window minimum
                }
                return result;
            }
            ENDCG
        }
    }
}
