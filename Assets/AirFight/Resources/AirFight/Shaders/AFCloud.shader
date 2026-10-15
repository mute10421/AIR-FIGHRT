Shader "AirFight/Cloud"
{
    Properties
    {
        _BaseMap ("Puff (alpha)", 2D) = "white" {}
        _Opacity ("Opacity", Float) = 0.92
        _Shade ("Underside darkness", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Cloud"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "AFSkyCommon.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Opacity, _Shade;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 right : TEXCOORD2;
                float3 up : TEXCOORD3;
                float3 toCam : TEXCOORD4;
                float fogCoord : TEXCOORD5;
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
                o.uv = i.uv;
                o.right = normalize(TransformObjectToWorldDir(float3(1, 0, 0)));
                o.up = normalize(TransformObjectToWorldDir(float3(0, 1, 0)));
                o.toCam = -normalize(TransformObjectToWorldDir(float3(0, 0, 1)));   // billboards face away from the camera (+Z)
                o.fogCoord = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a * _Opacity;
                // treat each puff as a soft sphere: normal from the quad UV
                float2 q = i.uv * 2.0 - 1.0;
                float r2 = saturate(dot(q, q));
                float3 n = normalize(i.right * q.x + i.up * q.y + i.toCam * sqrt(1.0 - r2));
                float3 L = AF_SunDirection();
                float3 sunCol = _AF_SunColor.rgb;
                float lit = saturate(dot(n, L) * 0.6 + 0.45);
                // flat dark bases, bright tops; the per-puff _Shade darkens puffs low in the cloud
                float base = saturate(0.5 + 0.5 * q.y);
                float3 shadowCol = float3(0.50, 0.55, 0.64) * lerp(1.0, 0.03, _AF_Night);
                float3 litCol = float3(1.05, 1.04, 1.02) * sunCol;
                float3 col = lerp(shadowCol, litCol, lit * lerp(0.75, 1.0, base));
                col *= 1.0 - _Shade * 0.35;
                // silver lining when looking toward the sun through thin edges
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float forward = pow(saturate(dot(-V, L)), 8.0);
                col += sunCol * forward * (1.0 - a) * 1.6;
                col += SampleSH(n) * 0.15;
                // fade out when the camera flies into the cloud (no popping)
                float dist = distance(GetCameraPositionWS(), i.positionWS);
                a *= smoothstep(40.0, 350.0, dist);
                col = AF_Aerial(col, i.positionWS, GetCameraPositionWS());
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
