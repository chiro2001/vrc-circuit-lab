Shader "VrcCpuWorld/CircuitLabRam"
{
    Properties
    {
        _MainTex ("Previous RAM state", 2D) = "black" {}
        _StateTex ("Stable pre-edge node state", 2D) = "black" {}
        _InitialRAM ("RAM initialization bytes", 2D) = "black" {}
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
            sampler2D _MainTex, _StateTex, _InitialRAM;
            int _Mode, _Reset, _TextureWidth, _NodeCount, _RamWriteEnableSignal;
            float4 _RamWriteAddressA, _RamWriteAddressB, _RamWriteDataA, _RamWriteDataB;
            float4 _MainTex_TexelSize;

            uint SignalBit(float id)
            {
                if (id < 0 || id >= _NodeCount) return 0u;
                return tex2Dlod(_StateTex, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u;
            }

            float AddressSignal(float4 a, float4 b, int bit)
            {
                return bit < 4 ? a[bit & 3] : b[bit & 3];
            }

            uint ReadBus(float4 a, float4 b, int bits)
            {
                uint value = 0u;
                [unroll] for (int bit = 0; bit < 8; bit++)
                    if (bit < bits) value |= SignalBit(AddressSignal(a, b, bit)) << bit;
                return value;
            }

            float4 frag(v2f_img input) : SV_Target
            {
                float2 outputUv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) outputUv.y = 1 - outputUv.y;
                #endif
                float4 previous = tex2Dlod(_MainTex, float4(input.uv, 0, 0));
                if (_Mode == 1)
                {
                    float initialByte = tex2Dlod(_InitialRAM, float4(outputUv, 0, 0)).r;
                    return float4(initialByte, 0, 0, 1);
                }
                if (_Reset != 0 || SignalBit(_RamWriteEnableSignal) == 0u) return previous;

                uint address = ReadBus(_RamWriteAddressA, _RamWriteAddressB, 8);
                uint data = ReadBus(_RamWriteDataA, _RamWriteDataB, 8);
                uint pixelX = (uint)min(15, (int)(outputUv.x * 16.0));
                uint pixelY = (uint)min(15, (int)(outputUv.y * 16.0));
                uint pixelAddress = pixelY * 16u + pixelX;
                if (pixelAddress != address) return previous;
                return float4(data / 255.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
