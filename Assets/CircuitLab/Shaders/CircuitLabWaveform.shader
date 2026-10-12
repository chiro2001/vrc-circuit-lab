Shader "VrcCpuWorld/CircuitLabWaveform"
{
    Properties
    {
        _HistoryTex ("Recorded GPU samples", 2D) = "black" {}
        _Background ("Background", Color) = (0.016, 0.032, 0.06, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _HistoryTex;
            float4 _Background, _DisplayWidths, _DisplaySegments, _DisplayBindSamples;
            float _HistoryCapacity, _DisplaySampleCount, _ViewStart, _WindowSamples, _CursorA, _CursorB;
            int _BitViewChannel, _NumberFormat;
            float _AnalyticAA, _WaveAspect, _EdgeWidth;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }
            float3 ChannelColor(int channel)
            {
                if (channel == 0) return float3(0.32, 0.92, 1);
                if (channel == 1) return float3(1, 0.74, 0.24);
                if (channel == 2) return float3(0.64, 1, 0.42);
                return float3(1, 0.43, 0.76);
            }
            float4 Sample(float index, int channel)
            {
                if (index < 0 || index >= _DisplaySampleCount || index < _DisplaySampleCount - _HistoryCapacity) return 0;
                float slot = index - floor(index / _HistoryCapacity) * _HistoryCapacity;
                float4 record = tex2Dlod(_HistoryTex, float4((slot + 0.5) / _HistoryCapacity, (channel + 0.5) / 4, 0, 0));
                if (abs(record.a - (index + 1)) > 0.25 || abs(record.b - _DisplaySegments[channel]) > 0.25 || index < _DisplayBindSamples[channel]) return 0;
                return record;
            }
            float Line(float position, float center, float thickness)
            {
                float aa = _AnalyticAA > 0.5 ? max(fwidth(position), thickness * 0.1) : thickness * 0.7;
                return 1 - smoothstep(max(0, thickness - aa * 0.5), thickness + aa * 0.5, abs(position - center));
            }
            float HexGlyph(uint digit, float2 p)
            {
                uint segments = 63u;
                if (digit == 1u) segments = 6u;
                else if (digit == 2u) segments = 91u;
                else if (digit == 3u) segments = 79u;
                else if (digit == 4u) segments = 102u;
                else if (digit == 5u) segments = 109u;
                else if (digit == 6u) segments = 125u;
                else if (digit == 7u) segments = 7u;
                else if (digit == 8u) segments = 127u;
                else if (digit == 9u) segments = 111u;
                else if (digit == 10u) segments = 119u;
                else if (digit == 11u) segments = 124u;
                else if (digit == 12u) segments = 57u;
                else if (digit == 13u) segments = 94u;
                else if (digit == 14u) segments = 121u;
                else if (digit == 15u) segments = 113u;
                float ax = _AnalyticAA > .5 ? max(fwidth(p.x), .002) : .001;
                float ay = _AnalyticAA > .5 ? max(fwidth(p.y), .002) : .001;
                float h = smoothstep(.16 - ax, .16 + ax, p.x) * (1 - smoothstep(.84 - ax, .84 + ax, p.x));
                float top = h * Line(p.y, 0.9, 0.055);
                float middle = h * Line(p.y, 0.5, 0.055);
                float bottom = h * Line(p.y, 0.1, 0.055);
                float upper = smoothstep(.54 - ay, .54 + ay, p.y) * (1 - smoothstep(.86 - ay, .86 + ay, p.y));
                float lower = smoothstep(.14 - ay, .14 + ay, p.y) * (1 - smoothstep(.46 - ay, .46 + ay, p.y));
                float right = Line(p.x, 0.86, 0.055), left = Line(p.x, 0.14, 0.055);
                return saturate(top * (segments & 1u) + right * upper * ((segments >> 1) & 1u) + right * lower * ((segments >> 2) & 1u)
                    + bottom * ((segments >> 3) & 1u) + left * lower * ((segments >> 4) & 1u) + left * upper * ((segments >> 5) & 1u) + middle * ((segments >> 6) & 1u));
            }
            float4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // Both overview and bit expansion read the same recorded bus; no CPU-created waveforms.
                bool bitView = _BitViewChannel >= 0;
                int selectedChannel = clamp(_BitViewChannel, 0, 3);
                int rows = bitView ? max(1, (int)_DisplayWidths[selectedChannel]) : 4;
                float rowPosition = (1 - input.uv.y) * rows;
                int row = min(rows - 1, (int)rowPosition);
                int channel = bitView ? selectedChannel : row;
                int bitIndex = max(0, rows - 1 - row);
                float y = 1 - frac(rowPosition);
                float3 tint = ChannelColor(channel);
                float3 color = _Background.rgb;
                float width = bitView ? min(1, _DisplayWidths[channel]) : _DisplayWidths[channel];
                float waveX = (input.uv.x - 0.045) / 0.755;
                if (input.uv.x >= 0.045 && input.uv.x < 0.8)
                {
                    float position = waveX * _WindowSamples;
                    float index = _ViewStart + floor(position);
                    float4 current = Sample(index, channel);
                    float4 previous = Sample(index - 1, channel);
                    if (bitView)
                    {
                        current.r = ((uint)round(current.r) >> bitIndex) & 1u;
                        previous.r = ((uint)round(previous.r) >> bitIndex) & 1u;
                    }
                    float grid = Line(frac(position / 8), 0, 0.006) + Line(y, 0.05, 0.01);
                    color += grid * float3(0.06, 0.09, 0.13);
                    if (current.g > 0.5 && width > 0)
                    {
                        float trace = 0;
                        float edge = frac(position);
                        float edgeWidth = min(0.4, _WindowSamples / 700 * max(0.5, _EdgeWidth));
                        if (width == 1)
                        {
                            float level = current.r > 0.5 ? 0.72 : 0.28;
                            trace = Line(y, level, 0.017);
                            if (previous.g > 0.5 && previous.r != current.r)
                                trace = max(trace, (1 - smoothstep(edgeWidth, edgeWidth * 2, edge)) * step(0.28, y) * step(y, 0.72));
                        }
                        else
                        {
                            trace = max(Line(y, 0.3, 0.014), Line(y, 0.7, 0.014));
                            if (previous.g > 0.5 && previous.r != current.r)
                            {
                                float crossing = saturate(edge / max(edgeWidth * 3, 0.05));
                                float diamond = max(Line(y, lerp(0.7, 0.3, crossing), 0.018), Line(y, lerp(0.3, 0.7, crossing), 0.018));
                                if (edge < edgeWidth * 3) trace = diamond;
                            }
                        }
                        color = lerp(color, tint, saturate(trace));
                    }
                    else color += Line(y, 0.5, 0.006) * float3(0.08, 0.09, 0.1);
                    float absolutePosition = _ViewStart + position;
                    color = lerp(color, float3(1, 0.94, 0.5), Line(absolutePosition, _CursorA + 0.5, _WindowSamples / 700));
                    color = lerp(color, float3(0.9, 0.65, 1), Line(absolutePosition, _CursorB + 0.5, _WindowSamples / 700));
                }
                else if (input.uv.x >= 0.8)
                {
                    bool atA = y >= 0.5;
                    float selected = atA ? _CursorA : _CursorB;
                    float4 record = Sample(selected, channel);
                    int digits = bitView ? 1 : _NumberFormat == 1 ? max(1, (int)width) : _NumberFormat == 2 ? 5 : 4;
                    float glyphHeight = min(0.26, 0.172 * max(0.1, _WaveAspect) * rows / (0.6 * digits));
                    float glyphWidth = glyphHeight / rows * 0.6 / max(0.1, _WaveAspect) * digits;
                    float2 p = float2((input.uv.x - (0.9 - glyphWidth * 0.5)) / glyphWidth, (y - (atA ? 0.7 : 0.3) + glyphHeight * 0.5) / glyphHeight);
                    if (p.x >= 0 && p.x < 1 && p.y >= 0 && p.y <= 1 && record.g > 0.5)
                    {
                        uint value = (uint)round(record.r);
                        if (bitView) value = (value >> bitIndex) & 1u;
                        int digitIndex = min(digits - 1, (int)(p.x * digits));
                        uint digit;
                        if (bitView) digit = value;
                        else if (_NumberFormat == 1) digit = (value >> (digits - 1 - digitIndex)) & 1u;
                        else if (_NumberFormat == 2)
                        {
                            uint divisor = (uint)round(pow(10.0, digits - 1 - digitIndex));
                            digit = (value / divisor) % 10u;
                        }
                        else digit = (value >> ((digits - 1 - digitIndex) * 4)) & 15u;
                        color = lerp(color, tint, HexGlyph(digit, float2(frac(p.x * digits), p.y)));
                    }
                    float marker = Line(input.uv.x, 0.808, 0.002) * Line(y, atA ? 0.7 : 0.3, 0.05);
                    color = lerp(color, atA ? float3(1, 0.94, 0.5) : float3(0.9, 0.65, 1), marker);
                    color += Line(input.uv.x, 0.801, 0.0015) * float3(0.1, 0.15, 0.2);
                }
                else
                {
                    uint label = bitView ? (uint)bitIndex : (uint)(channel + 10);
                    int digits = bitView ? 2 : 1;
                    float glyphWidth = min(0.04, 0.36 / rows * 0.6 / max(0.1, _WaveAspect) * digits);
                    float2 p = float2((input.uv.x - (0.022 - glyphWidth * 0.5)) / glyphWidth, (y - 0.32) / 0.36);
                    if (p.x >= 0 && p.x < 1 && p.y >= 0 && p.y <= 1)
                    {
                        int digitIndex = min(digits - 1, (int)(p.x * digits));
                        uint digit = bitView ? (digitIndex == 0 ? label / 10u : label % 10u) : label;
                        color = lerp(color, tint * 0.55, HexGlyph(digit, float2(frac(p.x * digits), p.y)));
                    }
                }
                color += Line(y, 0.02, 0.005) * float3(0.08, 0.12, 0.17);
                return float4(color, 1);
            }
            ENDCG
        }
    }
}
