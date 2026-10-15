Shader "AirFight/Tree"
{
    // Instanced tree impostors (3 crossed cards + a top card), alpha-tested, round crown lighting, aerial perspective.
    Properties
    {
        _BaseMap ("Atlas", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Float) = 0.5
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull Off
        Pass
        {
            Name "ForwardLit"
            AlphaToMask On
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
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
                float _Cutoff;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 tint : TEXCOORD3;   // rgb colour variation, a = height fraction (ambient occlusion)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = i.uv;
                float3 origin = TransformObjectToWorld(float3(0, 0, 0));
                float r = Hash(origin.xz), g = Hash(origin.zx + 7.1);
                // per-tree colour: some yellower, some bluer / darker
                o.tint = float4(lerp(float3(0.85, 0.9, 0.85), float3(1.15, 1.08, 0.85), r) * (0.8 + 0.35 * g), saturate(i.positionOS.y));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                clip(tex.a - _Cutoff);
                float3 wp = i.positionWS;
                float3 cam = GetCameraPositionWS();
                float3 n = normalize(i.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(wp));
                float3 L = sun.direction;
                float3 V = normalize(cam - wp);
                float wrap = saturate(dot(n, L) * 0.55 + 0.45);
                float trans = pow(saturate(dot(-V, L)), 4.0) * 0.35;          // sun through the leaves when back-lit
                float ao = lerp(0.55, 1.0, i.tint.a);
                float3 albedo = tex.rgb * i.tint.rgb;
                float2 tl = AF_TerrainLight(wp);
                float3 col = albedo * (sun.color * (wrap + trans) * min(sun.shadowAttenuation, tl.x) * AF_CloudShadow(wp) + SampleSH(n) * 0.9 * tl.y) * ao;
                col = AF_Aerial(col, wp, cam);
                return half4(col, 1);
            }
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
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Cutoff;
            CBUFFER_END
            struct A { float4 p : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            V dv(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.p = TransformObjectToHClip(i.p.xyz); o.uv = i.uv; return o; }
            half4 df(V i) : SV_Target { clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a - _Cutoff); return 0; }
            ENDHLSL
        }
    }
}
