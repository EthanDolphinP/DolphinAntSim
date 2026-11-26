// Draws every ant and bug in one instanced call. Per-ant data comes from a StructuredBuffer filled by BuildRenderDataJob:
//   xy = world position, z = heading (radians), w = colony | caste << 4 | carrying << 8 | scout << 10 | drone << 11 | morph << 12
// Colony slot _WildColony is wildlife: caste 0 mite, 1 beetle, 2 spider, 3 caterpillar, 4 cricket, 5 woodlouse,
// 6 antlion, 7 assassin bug, 8 centipede (see WildKind).
// _ColonyShapes picks a body style per colony (species): 0 ellipse, 1 segmented, 2 slender, 3 chunky, 4 termite.
Shader "AntSim/AntInstanced"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _Ants;
            float4 _ColonyColors[16];
            float _ColonyShapes[16];
            float _MorphSizes[32]; // [colony * 4 + morph]: drawn size of each caste variant
            float _WildColony;
            float _AntLength;
            float _MinSize;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                // x: cargo (0 none, 1 food, 2 leaf, 3 queen), y: shape (0-4 ants, 10 + WildKind for bugs)
                float2 info : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                float4 d = _Ants[v.instanceID];
                uint packed = (uint)d.w;
                uint colony = packed & 15u;
                uint caste = (packed >> 4) & 15u;
                uint cargo = (packed >> 8) & 3u;
                bool scout = ((packed >> 10) & 1u) != 0u;
                bool drone = ((packed >> 11) & 1u) != 0u;
                uint morph = (packed >> 12) & 3u;
                bool wild = colony == (uint)_WildColony;

                float scale, aspect, minMul, shape;
                float4 col;
                if (wild)
                {
                    shape = 10.0 + caste;
                    if (caste == 0u)      { scale = 0.75; aspect = 0.8;  minMul = 1.0; col = float4(0.85, 0.40, 0.32, 1); } // mite
                    else if (caste == 1u) { scale = 3.2;  aspect = 0.75; minMul = 2.5; col = float4(0.25, 0.62, 0.45, 1); } // beetle
                    else if (caste == 2u) { scale = 4.2;  aspect = 1.0;  minMul = 3.0; col = float4(0.16, 0.14, 0.14, 1); } // spider
                    else if (caste == 3u) { scale = 3.0;  aspect = 0.34; minMul = 2.4; col = float4(0.48, 0.74, 0.26, 1); } // caterpillar
                    else if (caste == 4u) { scale = 2.0;  aspect = 0.62; minMul = 1.6; col = float4(0.55, 0.43, 0.22, 1); } // cricket
                    else if (caste == 5u) { scale = 1.7;  aspect = 0.68; minMul = 1.5; col = float4(0.46, 0.48, 0.52, 1); } // woodlouse
                    else if (caste == 6u) { scale = 5.5;  aspect = 1.0;  minMul = 3.0; col = float4(0.58, 0.47, 0.30, 1); } // antlion in its pit
                    else if (caste == 7u) { scale = 2.6;  aspect = 0.5;  minMul = 2.0; col = float4(0.30, 0.11, 0.07, 1); } // assassin bug
                    else                  { scale = 6.5;  aspect = 0.22; minMul = 4.0; col = float4(0.62, 0.28, 0.10, 1); } // centipede
                }
                else
                {
                    shape = _ColonyShapes[colony];
                    // Soldiers are bigger. Never shrink below a couple of pixels so zoomed-out swarms stay visible.
                    // 0 worker, 1 soldier, 2 queen, 3 princess (winged), 4 male (winged)
                    scale = caste == 2u ? 3.2 : caste == 3u ? 2.6 : caste == 4u ? 1.3 : caste == 1u ? 1.7 : 1.0;
                    aspect = caste >= 3u ? 1.0 : shape == 2.0 ? 0.36 : shape == 3.0 ? 0.62 : shape == 4.0 ? 0.45 : 0.5;
                    minMul = caste == 2u ? 2.5 : caste == 3u ? 2.4 : caste == 4u ? 1.3 : 1.0;
                    col = _ColonyColors[colony];
                    // Termites are pale and soft: mostly cream, tinted with the colony colour.
                    if (shape == 4.0) col.rgb = lerp(col.rgb, float3(0.95, 0.9, 0.78), 0.55);
                    if (caste == 1u) col.rgb *= 0.6;
                    if (caste == 4u) col.rgb = lerp(col.rgb, float3(0.1, 0.1, 0.1), 0.45);
                    // Scouts: pale, long and slim.
                    if (scout) { col.rgb = lerp(col.rgb, float3(1, 0.97, 0.6), 0.72); scale = 1.25; aspect = 0.3; }
                    // Drones (escort soldiers): soldier-sized, pale blue.
                    if (drone) { col.rgb = lerp(col.rgb, float3(0.7, 0.88, 1.0), 0.6); scale = 1.55; minMul = 1.15; }
                    // Caste variants (majors, minims...) are drawn bigger or smaller.
                    float ms = _MorphSizes[min(colony * 4u + morph, 31u)];
                    scale *= ms;
                    minMul *= lerp(1.0, ms, 0.6);
                }

                float len = max(_AntLength * scale, _MinSize * minMul);
                float2 local = v.positionOS.xy * float2(len, len * aspect);
                float s, c;
                sincos(d.z, s, c);
                float2 rotated = float2(local.x * c - local.y * s, local.x * s + local.y * c);

                Varyings o;
                o.positionCS = TransformWorldToHClip(float3(d.xy + rotated, 0));
                o.uv = v.uv;
                o.color = col;
                o.info = float2(wild ? (float)cargo : caste == 2u ? 3.0 : caste == 3u ? 4.0 : caste == 4u ? 5.0 : (float)cargo, shape);
                return o;
            }

            // Circle test helper: inside a circle at (cx, 0) with radius r, in [-1,1] quad space.
            bool InCircle(float2 p, float cx, float r) { float2 q = p - float2(cx, 0); return dot(q, q) < r * r; }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0; // x along the body (head at +x), y across
                float3 col = i.color.rgb;
                float shape = i.info.y;
                float cargo = i.info.x;
                int sh = (int)(shape + 0.5);

                if (sh == 18) // centipede: a long segmented body with a pair of legs on every segment
                {
                    float seg = frac((p.x + 1.0) * 7.0);
                    bool body = abs(p.y) < 0.42 && abs(p.x) < 0.93;
                    bool leg = !body && abs(p.y) < 0.98 && abs(seg - 0.5) < 0.09 && abs(p.x) < 0.88;
                    bool antenna = p.x > 0.9 && abs(abs(p.y) - (p.x - 0.9) * 6.0) < 0.12;
                    if (!body && !leg && !antenna) discard;
                    if (body && seg < 0.14) col *= 0.55;
                    if (p.x > 0.8) col *= 0.6; // head
                    if (leg || antenna) col = lerp(col, float3(0.85, 0.6, 0.3), 0.4);
                    return half4(col, 1.0);
                }
                if (sh == 17) // assassin bug: slim dark body, narrow head, orange-banded edges
                {
                    float2 b = p * float2(1.15, 1.6);
                    bool body = dot(b, b) < 0.75 && p.x < 0.62;
                    bool head = p.x >= 0.55 && p.x < 0.98 && abs(p.y) < 0.16;
                    bool legs = abs(p.y) < 0.98 && abs(p.y) > 0.5 && abs(frac(p.x * 2.2 + 0.1) - 0.5) < 0.07 && p.x > -0.6 && p.x < 0.5;
                    if (!body && !head && !legs) discard;
                    if (body && abs(p.y) > 0.36 && p.x < 0.3) col = float3(0.95, 0.55, 0.15);
                    if (head) col *= 0.7;
                    return half4(col, 1.0);
                }
                if (sh == 16) // antlion: a fat body with jaws, at the bottom of a sand pit
                {
                    float r = length(p);
                    bool body = InCircle(p, -0.08, 0.26);
                    bool jaws = p.x > 0.12 && p.x < 0.42 && abs(abs(p.y) - 0.2 + (p.x - 0.12) * 0.3) < 0.05;
                    if (body || jaws) return half4(body ? col : col * 0.5, 1.0);
                    if (r > 1.0) discard;
                    // The pit: darker and redder toward the middle, with grains on its slope.
                    float slope = 1.0 - r;
                    float grain = frac(sin(dot(floor(p * 18.0), float2(12.9898, 78.233))) * 43758.5453);
                    float3 pit = lerp(float3(0.55, 0.46, 0.3), float3(0.22, 0.17, 0.1), slope) * (0.9 + 0.2 * grain);
                    return half4(pit, 0.35 + 0.5 * slope);
                }
                if (sh == 15) // woodlouse: grey oval with armour plates across it
                {
                    if (dot(p * float2(1.0, 1.05), p * float2(1.0, 1.05)) > 0.92) discard;
                    if (frac(p.x * 3.2 + 0.5) < 0.16) col *= 0.62;
                    if (p.x > 0.78) col *= 0.75;
                    return half4(col, 1.0);
                }
                if (sh == 14) // cricket: long body, folded wings, big hind legs, long antennae
                {
                    float2 b = (p - float2(0.05, 0.0)) * float2(1.25, 2.2);
                    bool body = dot(b, b) < 0.8;
                    // Hind legs: thick bars from mid-body sweeping back and out.
                    float2 q = float2(p.x, abs(p.y));
                    float t = saturate((0.1 - q.x) / 0.9);
                    float2 onLeg = float2(0.1 - t * 0.9, 0.3 + t * 0.62);
                    bool leg = length(q - onLeg) < 0.1 && q.x < 0.1;
                    bool antenna = p.x > 0.6 && abs(abs(p.y) - (p.x - 0.6) * 1.8) < 0.06;
                    if (!body && !leg && !antenna) discard;
                    if (body && abs(p.y) < 0.08 && p.x < 0.3) col *= 0.7; // wing seam
                    if (leg) col *= 0.8;
                    return half4(col, 1.0);
                }
                if (sh == 13) // caterpillar: a row of soft segments with a dark head
                {
                    float best = 2.0;
                    for (int s2 = 0; s2 < 6; s2++)
                    {
                        float cx = -0.82 + s2 * 0.31;
                        float2 q = float2((p.x - cx) / 0.2, p.y / 0.92);
                        best = min(best, dot(q, q));
                    }
                    if (best > 1.0) discard;
                    if (p.x > 0.66) col = float3(0.2, 0.25, 0.1);
                    else if (frac((p.x + 1.0) / 0.31) < 0.2) col *= 0.75;
                    if (abs(p.y) < 0.15 && p.x < 0.6) col = lerp(col, float3(0.95, 0.9, 0.4), 0.35); // stripe
                    return half4(col, 1.0);
                }
                if (sh == 12) // spider: round body, eight legs
                {
                    float r = length(p);
                    bool body = InCircle(p, -0.15, 0.36) || InCircle(p, 0.25, 0.2);
                    float a = atan2(p.y, p.x);
                    float legPhase = frac(a / 6.2831853 * 8.0 + 0.0625);
                    bool leg = r < 0.98 && r > 0.25 && abs(legPhase - 0.5) < 0.07 && abs(p.y) > 0.12;
                    if (!body && !leg) discard;
                    if (body && InCircle(p, -0.2, 0.12)) col = float3(0.75, 0.15, 0.1); // red mark
                    return half4(col, 1.0);
                }
                if (sh == 11) // beetle: shell with a seam and a head
                {
                    bool shell = dot(p * float2(1.15, 1.0), p * float2(1.15, 1.0)) < 0.78;
                    bool head = InCircle(p, 0.78, 0.2);
                    if (!shell && !head) discard;
                    if (shell && abs(p.y) < 0.05 && p.x < 0.6) col *= 0.45;
                    if (head) col *= 0.5;
                    col += 0.15 * saturate(0.5 - length(p - float2(-0.2, 0.3))); // sheen
                    return half4(col, 1.0);
                }
                if (sh == 10) // mite: round dot
                {
                    if (dot(p, p) > 0.9) discard;
                    return half4(col, 1.0);
                }

                if (cargo > 3.5) // winged: princess (gold) or male
                {
                    float2 b = p * float2(1.0, 2.4);
                    bool body = dot(b, b) <= 0.8;
                    float2 w1 = (p - float2(-0.15, 0.45)) * float2(1.4, 2.0);
                    float2 w2 = (p - float2(-0.15, -0.45)) * float2(1.4, 2.0);
                    bool wing = dot(w1, w1) < 0.5 || dot(w2, w2) < 0.5;
                    if (!body && !wing) discard;
                    if (!body) return half4(lerp(col, float3(0.92, 0.95, 1.0), 0.75), 1.0);
                    if (cargo > 4.5) return half4(col, 1.0);
                    col = lerp(col, float3(1.0, 0.85, 0.3), 0.3);
                    if (dot(b, b) > 0.42) col = float3(1.0, 0.82, 0.25);
                    return half4(col, 1.0);
                }

                // Ants.
                bool inside;
                if (shape > 0.5 && shape < 1.5) // segmented: head, thorax, abdomen
                    inside = InCircle(p, 0.62, 0.34) || InCircle(p, 0.08, 0.3) || InCircle(p, -0.5, 0.48);
                else if (sh == 4) // termite: soft, plump, straight-sided, with a round head
                    inside = InCircle(p, 0.68, 0.3) || (abs(p.y) < 0.82 && p.x > -0.85 && p.x < 0.45 && dot(p * float2(0.9, 1.0), p * float2(0.9, 1.0)) < 1.0);
                else
                    inside = dot(p, p) <= 1.0;
                if (!inside) discard;
                // Termites: a darker head (soldiers' much darker), faint bands on the body.
                if (sh == 4 && cargo < 2.5)
                {
                    if (p.x > 0.45) col *= 0.7;
                    else if (frac((p.x + 1.0) * 3.5) < 0.15) col *= 0.88;
                }

                if (cargo > 2.5)
                {
                    // Queen: bright body with a white rim.
                    col = lerp(col, float3(1, 1, 1), 0.25);
                    if (dot(p, p) > 0.6) col = float3(1, 1, 1);
                }
                else if (cargo > 1.5 && p.x > 0.2) col = float3(0.35, 0.95, 0.3); // leaf
                else if (cargo > 0.5 && p.x > 0.3) col = float3(1.0, 0.78, 0.3);  // food
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
