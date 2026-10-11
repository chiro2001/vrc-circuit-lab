Shader "VrcCpuWorld/CircuitLabHistory"
{
    Properties
    {
        _MainTex ("Previous history", 2D) = "black" {}
        _StateTex ("Stable GPU state", 2D) = "black" {}
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
            sampler2D _MainTex, _StateTex;
            int _TextureWidth, _NodeCount, _HistoryCapacity, _WriteSlot, _SampleIndex, _ClearMode, _CopyMode;
            int _RestoreMode, _EventMode, _EventCapacity, _EventCycle, _WriteSignal;
            float4 _RestoreValues, _RestoreValidity, _OutputSignalsA, _OutputSignalsB, _SequenceSignalsA, _SequenceSignalsB, _EventCycleBytes;
            float4 _ProbeWidths, _ProbeSegments, _BindSamples;
            float4 _Probe0A, _Probe0B, _Probe0C, _Probe0D;
            float4 _Probe1A, _Probe1B, _Probe1C, _Probe1D;
            float4 _Probe2A, _Probe2B, _Probe2C, _Probe2D;
            float4 _Probe3A, _Probe3B, _Probe3C, _Probe3D;

            float Id(int channel, int bit)
            {
                float4 ids;
                if (channel == 0) ids = bit < 4 ? _Probe0A : bit < 8 ? _Probe0B : bit < 12 ? _Probe0C : _Probe0D;
                else if (channel == 1) ids = bit < 4 ? _Probe1A : bit < 8 ? _Probe1B : bit < 12 ? _Probe1C : _Probe1D;
                else if (channel == 2) ids = bit < 4 ? _Probe2A : bit < 8 ? _Probe2B : bit < 12 ? _Probe2C : _Probe2D;
                else ids = bit < 4 ? _Probe3A : bit < 8 ? _Probe3B : bit < 12 ? _Probe3C : _Probe3D;
                return ids[bit & 3];
            }

            float4 frag(v2f_img input) : SV_Target
            {
                if (_ClearMode != 0) return 0;
                if (_CopyMode != 0) return tex2Dlod(_MainTex, float4(input.uv, 0, 0));
                if (_EventMode != 0)
                {
                    int eventSlot = (int)(input.uv.x * _EventCapacity);
                    if (eventSlot != _WriteSlot) return tex2Dlod(_MainTex, float4(input.uv, 0, 0));
                    if (input.uv.y >= 0.5) return _EventCycleBytes / 255;
                    uint value = 0u, sequence = 0u;
                    [unroll] for (int bit = 0; bit < 8; bit++)
                    {
                        float outputId = bit < 4 ? _OutputSignalsA[bit & 3] : _OutputSignalsB[bit & 3];
                        float sequenceId = bit < 4 ? _SequenceSignalsA[bit & 3] : _SequenceSignalsB[bit & 3];
                        value |= (tex2Dlod(_StateTex, float4((outputId + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u) << bit;
                        sequence |= (tex2Dlod(_StateTex, float4((sequenceId + 0.5) / _TextureWidth, 0.5, 0, 0)).r > 0.5 ? 1u : 0u) << bit;
                    }
                    float write = tex2Dlod(_StateTex, float4((_WriteSignal + 0.5) / _TextureWidth, 0.5, 0, 0)).r;
                    return float4(value / 255.0, write > 0.5 ? 1 : 0, sequence / 255.0, 1);
                }
                int slot = (int)(input.uv.x * _HistoryCapacity);
                int channel = min(3, (int)(input.uv.y * 4));
                if (slot != _WriteSlot) return tex2Dlod(_MainTex, float4(input.uv, 0, 0));
                if (_RestoreMode != 0) return float4(_RestoreValues[channel], _RestoreValidity[channel], _ProbeSegments[channel], _SampleIndex + 1);
                int width = (int)_ProbeWidths[channel];
                float value = 0;
                float valid = width > 0 && _SampleIndex >= _BindSamples[channel] ? 1 : 0;
                [unroll] for (int bit = 0; bit < 16; bit++)
                {
                    if (bit < width)
                    {
                        float id = Id(channel, bit);
                        if (id < 0 || id >= _NodeCount) valid = 0;
                        else
                        {
                            float state = tex2Dlod(_StateTex, float4((id + 0.5) / _TextureWidth, 0.5, 0, 0)).r;
                            value += (state > 0.5 ? 1 : 0) * (1u << bit);
                        }
                    }
                }
                // ARGBFloat retains all 16 bus bits, validity, segment and absolute sample index.
                return float4(value, valid, _ProbeSegments[channel], _SampleIndex + 1);
            }
            ENDCG
        }
    }
}
