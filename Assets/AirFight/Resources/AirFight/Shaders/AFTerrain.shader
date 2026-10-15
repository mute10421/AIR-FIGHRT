Shader "AirFight/Terrain"
{
    // Photo-textured terrain (CC0 aerial photographs from Poly Haven) blended by a baked splat map, tinted by the
    // whole-island colour map, lit with a full-resolution normal map, triplanar rock on cliffs and aerial perspective.
    Properties
    {
        _BaseMap ("Macro colour (whole island)", 2D) = "white" {}
        _MacroNormal ("Macro world normal (xz in RG)", 2D) = "grey" {}
        _UseMacroNormal ("Use macro normal", Float) = 0
        _Splat0 ("Splat grass/dry/dirt/rock", 2D) = "red" {}
        _Splat1 ("Splat sand/snow", 2D) = "black" {}
        _UseSplat ("Use splat", Float) = 0
        _Noise ("Variation noise", 2D) = "grey" {}
        _GrassTex ("Grass", 2D) = "grey" {}
        _GrassN ("Grass N", 2D) = "bump" {}
        _DryTex ("Dry meadow", 2D) = "grey" {}
        _DryN ("Dry N", 2D) = "bump" {}
        _DirtTex ("Dirt / fields", 2D) = "grey" {}
        _DirtN ("Dirt N", 2D) = "bump" {}
        _RockTex ("Rock", 2D) = "grey" {}
        _RockN ("Rock N", 2D) = "bump" {}
        _SandTex ("Sand", 2D) = "grey" {}
        _SandN ("Sand N", 2D) = "bump" {}
        _SnowTex ("Snow", 2D) = "grey" {}
        _SnowN ("Snow N", 2D) = "bump" {}
        _GroundTex ("Close ground", 2D) = "grey" {}
        _GroundN ("Close ground N", 2D) = "bump" {}
        _Mean0 ("Mean grass", Vector) = (0.08,0.08,0.014,1)
        _Mean1 ("Mean dry", Vector) = (0.17,0.12,0.03,1)
        _Mean2 ("Mean dirt", Vector) = (0.21,0.12,0.056,1)
        _Mean3 ("Mean rock", Vector) = (0.17,0.12,0.053,1)
        _Mean4 ("Mean sand", Vector) = (0.28,0.24,0.20,1)
        _Mean5 ("Mean snow", Vector) = (0.26,0.26,0.32,1)
        _Mean6 ("Mean ground", Vector) = (0.29,0.25,0.12,1)
        _Scale0 ("Layer scales (m) grass,dry,dirt,rock", Vector) = (90,30,40,50)
        _Scale1 ("Layer scales (m) sand,snow,ground", Vector) = (30,80,2.5,0)
        _NormalStrength ("Detail normal strength", Float) = 1.0
        _TerrainST ("World xz → map uv", Vector) = (0.0000208333, 0.0000208333, 0.5, 0.5)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "AFSkyCommon.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MacroNormal);
            TEXTURE2D(_Splat0); TEXTURE2D(_Splat1);
            TEXTURE2D(_Noise); SAMPLER(sampler_Noise);
            TEXTURE2D(_GrassTex); SAMPLER(sampler_GrassTex); TEXTURE2D(_GrassN);
            TEXTURE2D(_DryTex); TEXTURE2D(_DryN);
            TEXTURE2D(_DirtTex); TEXTURE2D(_DirtN);
            TEXTURE2D(_RockTex); TEXTURE2D(_RockN);
            TEXTURE2D(_SandTex); TEXTURE2D(_SandN);
            TEXTURE2D(_SnowTex); TEXTURE2D(_SnowN);
            TEXTURE2D(_GroundTex); TEXTURE2D(_GroundN);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST, _MacroNormal_ST, _Splat0_ST, _Splat1_ST, _Noise_ST;
                float4 _GrassTex_ST, _GrassN_ST, _DryTex_ST, _DryN_ST, _DirtTex_ST, _DirtN_ST, _RockTex_ST, _RockN_ST;
                float4 _SandTex_ST, _SandN_ST, _SnowTex_ST, _SnowN_ST, _GroundTex_ST, _GroundN_ST;
                float4 _Mean0, _Mean1, _Mean2, _Mean3, _Mean4, _Mean5, _Mean6;
                float4 _Scale0, _Scale1, _TerrainST;
                float _UseMacroNormal, _UseSplat, _NormalStrength;
            CBUFFER_END

            #define REP sampler_GrassTex
            #define CLAMPS sampler_BaseMap

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = p.positionWS.xz * _TerrainST.xy + _TerrainST.zw;   // chunk meshes carry no UVs
                return o;
            }

            // Explicit-gradient sampling: safe inside dynamic branches (layers with no weight are skipped entirely).
            // Two differently scaled + rotated samples blended by low-frequency noise: no visible repeat grid from the air.
            float2 Rot(float2 p) { return float2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8); }
            float3 SampleAT(TEXTURE2D_PARAM(tex, smp), float2 xz, float2 dx, float2 dy, float size, float mixN)
            {
                float3 a = SAMPLE_TEXTURE2D_GRAD(tex, smp, xz / size, dx / size, dy / size).rgb;
                float k = 1.0 / (size * 3.7);
                float3 b = SAMPLE_TEXTURE2D_GRAD(tex, smp, Rot(xz) * k + 0.37, Rot(dx) * k, Rot(dy) * k).rgb;
                return lerp(a, b, mixN);
            }
            float2 SampleN(TEXTURE2D_PARAM(tex, smp), float2 xz, float2 dx, float2 dy, float size)
            {
                return UnpackNormal(SAMPLE_TEXTURE2D_GRAD(tex, smp, xz / size, dx / size, dy / size)).xy;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 wp = i.positionWS;
                float3 cam = GetCameraPositionWS();
                float dist = distance(cam, wp);
                float2 xz = wp.xz;
                float2 dx = ddx(xz), dy = ddy(xz);
                float2 dxZY = ddx(wp.zy), dyZY = ddy(wp.zy), dxXY = ddx(wp.xy), dyXY = ddy(wp.xy);

                float3 n = normalize(i.normalWS);
                if (_UseMacroNormal > 0.5)
                {
                    float2 mn = SAMPLE_TEXTURE2D(_MacroNormal, CLAMPS, i.uv).rg * 2.0 - 1.0;
                    n = normalize(float3(mn.x, sqrt(saturate(1.0 - dot(mn, mn))), mn.y));
                }
                float3 macro = SAMPLE_TEXTURE2D(_BaseMap, CLAMPS, i.uv).rgb;

                // ---- layer weights (baked splat: grass, dry meadow, dirt/fields, rock | sand, snow) ----
                float4 s0 = float4(1, 0, 0, 0), s1 = 0;
                if (_UseSplat > 0.5)
                {
                    s0 = SAMPLE_TEXTURE2D(_Splat0, CLAMPS, i.uv);
                    s1 = SAMPLE_TEXTURE2D(_Splat1, CLAMPS, i.uv);
                }
                s1.zw = 0;
                // break up the splat borders with noise so layer edges look natural, not blurry
                float nz = SAMPLE_TEXTURE2D(_Noise, sampler_Noise, xz / 700.0).r;
                float nz2 = SAMPLE_TEXTURE2D(_Noise, sampler_Noise, xz / 5300.0 + 0.3).g;
                s0 = pow(saturate(s0 * (0.75 + nz * 0.5)), 1.6);
                s1 = pow(saturate(s1 * (0.75 + nz * 0.5)), 1.6);
                float wsum = dot(s0, float4(1, 1, 1, 1)) + s1.x + s1.y + 1e-4;
                s0 /= wsum; s1 /= wsum;

                float mixN = smoothstep(0.35, 0.65, nz2);
                float3 photo = 0, mean = 0; float2 dn = 0;
                // fine detail normals only where they can be seen
                float nearN = 1.0 - smoothstep(1500.0, 6000.0, dist);
                [branch] if (s0.x > 0.01) { photo += s0.x * SampleAT(TEXTURE2D_ARGS(_GrassTex, REP), xz, dx, dy, _Scale0.x, mixN); mean += s0.x * _Mean0.rgb; if (nearN > 0) dn += s0.x * SampleN(TEXTURE2D_ARGS(_GrassN, REP), xz, dx, dy, _Scale0.x); }
                [branch] if (s0.y > 0.01) { photo += s0.y * SampleAT(TEXTURE2D_ARGS(_DryTex, REP), xz, dx, dy, _Scale0.y, mixN); mean += s0.y * _Mean1.rgb; if (nearN > 0) dn += s0.y * SampleN(TEXTURE2D_ARGS(_DryN, REP), xz, dx, dy, _Scale0.y); }
                [branch] if (s0.z > 0.01) { photo += s0.z * SampleAT(TEXTURE2D_ARGS(_DirtTex, REP), xz, dx, dy, _Scale0.z, mixN); mean += s0.z * _Mean2.rgb; if (nearN > 0) dn += s0.z * SampleN(TEXTURE2D_ARGS(_DirtN, REP), xz, dx, dy, _Scale0.z); }
                [branch] if (s0.w > 0.01)
                {
                    // rock: triplanar so cliffs show rock faces instead of stretched streaks
                    float3 bw = pow(abs(n), 4.0); bw /= dot(bw, float3(1, 1, 1));
                    float3 r = SampleAT(TEXTURE2D_ARGS(_RockTex, REP), xz, dx, dy, _Scale0.w, mixN) * bw.y;
                    float rs = 1.0 / _Scale0.w;
                    [branch] if (bw.x > 0.02) r += SAMPLE_TEXTURE2D_GRAD(_RockTex, REP, wp.zy * rs, dxZY * rs, dyZY * rs).rgb * bw.x;
                    [branch] if (bw.z > 0.02) r += SAMPLE_TEXTURE2D_GRAD(_RockTex, REP, wp.xy * rs, dxXY * rs, dyXY * rs).rgb * bw.z;
                    photo += s0.w * r; mean += s0.w * _Mean3.rgb;
                    if (nearN > 0) dn += s0.w * SampleN(TEXTURE2D_ARGS(_RockN, REP), xz, dx, dy, _Scale0.w) * bw.y;
                }
                [branch] if (s1.x > 0.01) { photo += s1.x * SampleAT(TEXTURE2D_ARGS(_SandTex, REP), xz, dx, dy, _Scale1.x, mixN); mean += s1.x * _Mean4.rgb; if (nearN > 0) dn += s1.x * SampleN(TEXTURE2D_ARGS(_SandN, REP), xz, dx, dy, _Scale1.x); }
                [branch] if (s1.y > 0.01) { photo += s1.y * SampleAT(TEXTURE2D_ARGS(_SnowTex, REP), xz, dx, dy, _Scale1.y, mixN); mean += s1.y * _Mean5.rgb; if (nearN > 0) dn += s1.y * SampleN(TEXTURE2D_ARGS(_SnowN, REP), xz, dx, dy, _Scale1.y); }

                // ---- colour: photo detail, hue from the whole-island colour map (fields, forests, river valleys) ----
                float3 ratio = clamp(macro / max(mean, 0.004), 0.05, 12.0);
                float far = smoothstep(800.0, 9000.0, dist);
                float3 albedo = photo * lerp(sqrt(ratio), ratio, 0.55 + 0.45 * far);
                // fine ground (grass blades, twigs, soil) under ~400 m: taxiing / very low flight
                float groundW = (s0.x + s0.y + s0.z) * (1.0 - smoothstep(150.0, 450.0, dist));
                [branch] if (groundW > 0.01)
                {
                    float gs = 1.0 / _Scale1.z;
                    float3 g = SAMPLE_TEXTURE2D_GRAD(_GroundTex, REP, xz * gs, dx * gs, dy * gs).rgb / _Mean6.rgb;
                    albedo *= lerp(1.0, clamp(g, 0.3, 2.2), groundW * 0.6);
                    dn += SampleN(TEXTURE2D_ARGS(_GroundN, REP), xz, dx, dy, _Scale1.z) * groundW * 0.5;
                }
                // far away: average down to the map colour (mip of photos ≈ mean)
                albedo = lerp(albedo, macro, smoothstep(12000.0, 30000.0, dist));
                albedo = saturate(albedo);

                // ---- normal: macro relief + photo micro relief (terrain tangent frame ≈ world X / Z) ----
                n = normalize(n + float3(dn.x, 0.0, dn.y) * _NormalStrength * nearN);

                // ---- lighting: sun (shadows, cloud shadows) + sky ambient; wet-ish sheen on snow ----
                Light sun = GetMainLight(TransformWorldToShadowCoord(wp));
                float ndl = saturate(dot(n, sun.direction));
                float2 tl = AF_TerrainLight(wp);                         // baked mountain shadows (x) and valley occlusion (y)
                float3 col = albedo * (sun.color * ndl * min(sun.shadowAttenuation, tl.x) * AF_CloudShadow(wp) + SampleSH(n) * tl.y);
                float3 V = normalize(cam - wp);
                float3 Hh = normalize(V + sun.direction);
                col += sun.color * sun.shadowAttenuation * s1.y * 0.08 * pow(saturate(dot(n, Hh)), 60.0);

                col = AF_Aerial(col, wp, cam);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // depth + shadow casting so the mountains shadow the valleys
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma vertex sv
            #pragma fragment sf
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct A { float4 p : POSITION; float3 n : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; };
            V sv(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i);
                float3 w = TransformObjectToWorld(i.p.xyz);
                float3 nn = TransformObjectToWorldNormal(i.n);
                o.p = TransformWorldToHClip(ApplyShadowBias(w, nn, _LightDirection));
                #if UNITY_REVERSED_Z
                    o.p.z = min(o.p.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.p.z = max(o.p.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return o;
            }
            half4 sf(V i) : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex dv
            #pragma fragment df
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 p : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            V dv(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.p = TransformObjectToHClip(i.p.xyz); return o; }
            half4 df(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Lit"
}
