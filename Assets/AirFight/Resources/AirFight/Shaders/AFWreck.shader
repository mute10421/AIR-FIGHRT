Shader "AirFight/Wreck"
{
    // Shot-down airframe: panels crumple and dent (vertex displacement grows with _Crumple), paint scorches black with
    // glowing embers in the cracks while it burns (_Burn).
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1,1,1,1)
        _Crumple ("Crumple 0..1", Float) = 0
        _Burn ("Burn 0..1", Float) = 0
        _Seed ("Seed", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "AFSkyCommon.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Crumple, _Burn, _Seed;
            CBUFFER_END

            struct A { float4 p : POSITION; float3 n : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 wp : TEXCOORD1; float3 nw : TEXCOORD2; float3 mp : TEXCOORD3; UNITY_VERTEX_OUTPUT_STEREO };

            float H3(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }
            float N3(float3 p)
            {
                float3 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(H3(i), H3(i + float3(1,0,0)), f.x), lerp(H3(i + float3(0,1,0)), H3(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(H3(i + float3(0,0,1)), H3(i + float3(1,0,1)), f.x), lerp(H3(i + float3(0,1,1)), H3(i + float3(1,1,1)), f.x), f.y), f.z);
            }

            V vert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // metric object-space position (primitives are scaled cubes / cylinders)
                float3 sc = float3(length(UNITY_MATRIX_M._m00_m10_m20), length(UNITY_MATRIX_M._m01_m11_m21), length(UNITY_MATRIX_M._m02_m12_m22));
                float3 mp = i.p.xyz * sc + _Seed;
                float3 wp = TransformObjectToWorld(i.p.xyz);
                float3 nw = TransformObjectToWorldNormal(i.n);
                // large buckles + small dents, mostly inward (crushed), plus a twist of the whole panel
                float big = N3(mp * 0.55) - 0.45, small = N3(mp * 2.3 + 7.0) - 0.5;
                float3 rnd = float3(N3(mp * 0.8 + 3.1), N3(mp * 0.8 + 5.7), N3(mp * 0.8 + 9.3)) - 0.5;
                wp += (nw * (big * 0.9 + small * 0.25) + rnd * 0.5) * _Crumple;
                o.wp = wp; o.nw = nw; o.mp = mp;
                o.pos = TransformWorldToHClip(wp);
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                float soot = N3(i.mp * 1.3) * 0.7 + N3(i.mp * 5.0) * 0.3;
                float scorch = saturate((_Burn * 1.4 - soot) * 3.0);
                albedo = lerp(albedo, float3(0.025, 0.022, 0.02), scorch);
                // facet normals from the displaced surface → every dent catches the light
                float3 n = normalize(cross(ddy(i.wp), ddx(i.wp)));
                if (dot(n, i.nw) < 0) n = -n;
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.wp));
                float3 col = albedo * (sun.color * saturate(dot(n, sun.direction)) * sun.shadowAttenuation + SampleSH(n));
                // embers glowing in the burnt cracks, flickering
                float crack = saturate(1.0 - abs(N3(i.mp * 3.0) - 0.5) * 14.0) * scorch;
                float flick = 0.6 + 0.4 * sin(_Time.y * 9.0 + soot * 20.0);
                col += float3(3.0, 0.9, 0.2) * crack * flick * saturate(_Burn * 2.0) * (1.0 - saturate((_Burn - 0.85) * 6.0));
                col = AF_Aerial(col, i.wp, GetCameraPositionWS());
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
