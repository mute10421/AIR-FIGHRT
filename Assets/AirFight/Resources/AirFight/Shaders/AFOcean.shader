Shader "AirFight/Ocean"
{
    Properties
    {
        _DeepColor ("Deep water", Color) = (0.015, 0.07, 0.11, 1)
        _ShallowColor ("Scatter colour", Color) = (0.04, 0.20, 0.24, 1)
        _WaveScale ("Wave scale", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "AFSkyCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _DeepColor, _ShallowColor;
                float _WaveScale;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float fogCoord : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.fogCoord = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // sum of directional sine waves → analytic slope (no textures needed)
            // xy = direction, z = wavelength (m), w = amplitude (m)
            static const float4 W[6] = {
                float4( 0.80,  0.60, 140.0, 0.70),
                float4( 0.30,  0.95,  61.0, 0.25),
                float4(-0.55,  0.83,  27.0, 0.07),
                float4( 0.97, -0.24,  11.0, 0.018),
                float4(-0.20, -0.98,   4.7, 0.005),
                float4( 0.62,  0.78,   1.9, 0.0015) };

            // calm sea: long swell dominates; short ripples are faint and vanish with distance
            float2 WaveSlope(float2 p, float t, float dist)
            {
                float2 s = 0;
                [unroll] for (int k = 0; k < 6; k++)
                {
                    float2 d = normalize(W[k].xy);
                    float kk = 6.2831853 / (W[k].z * _WaveScale);
                    float c = sqrt(9.81 * kk);                 // deep-water dispersion
                    float ph = kk * dot(d, p) - c * t;
                    float fade = 1.0 - smoothstep(W[k].z * 15.0, W[k].z * 90.0, dist);
                    s += d * (W[k].w * kk * cos(ph)) * fade;
                }
                return s;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 wp = i.positionWS;
                float3 cam = GetCameraPositionWS();
                float3 V = normalize(cam - wp);
                float dist = distance(cam, wp);

                // fine waves fade out with distance (avoids shimmering), the big swell stays
                float depth = max(wp.y - AF_GroundHeight(wp), 0.0);
                float2 slope = WaveSlope(wp.xz, _Time.y, dist) * 0.8 * saturate(depth / 8.0 + 0.25);   // calmer in the shallows
                float3 N = normalize(float3(-slope.x, 1.0, -slope.y));

                Light sun = GetMainLight(TransformWorldToShadowCoord(wp));
                float3 R = reflect(-V, N);
                R.y = abs(R.y);                                   // never reflect the ground below the horizon
                float3 sky = AF_SkyColor(R);
                float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(N, V)), 5.0);
                float3 water = lerp(_DeepColor.rgb, _ShallowColor.rgb, saturate(dot(sun.direction, N)) * 0.35) * (SampleSH(N) * 0.6 + 0.4 * sun.color * saturate(sun.direction.y));
                // shallow water: the sandy bottom shows through, red light absorbed first → turquoise near the beaches
                float3 lightIn = sun.color * saturate(sun.direction.y) * 0.9 + SampleSH(float3(0, 1, 0)) * 0.6;
                float3 T = exp(-depth * float3(0.38, 0.075, 0.055));
                float3 bottom = float3(0.32, 0.29, 0.22) * lightIn * T;
                water = lerp(water, bottom + water * (1.0 - T) * 0.6, exp(-depth * 0.09));
                float3 col = lerp(water, sky, saturate(fres));
                // surf: broken white foam where the waves run up the beach
                float surf = saturate(1.0 - depth / 1.6);
                float bands = 0.5 + 0.5 * sin(depth * 3.5 - _Time.y * 1.6 + sin(wp.x * 0.05) * 2.0 + sin(wp.z * 0.043) * 2.0);
                float foam = surf * smoothstep(0.45, 0.85, bands * (0.6 + 0.4 * sin(wp.x * 0.31 + wp.z * 0.27 + _Time.y)));
                foam = max(foam, surf * surf * 0.6) * (1.0 - smoothstep(3000.0, 9000.0, dist));
                col = lerp(col, lightIn * 0.85, saturate(foam));
                // sun glitter
                float spec = pow(saturate(dot(R, sun.direction)), 900.0) * 60.0 + pow(saturate(dot(R, sun.direction)), 90.0) * 0.6;
                float cs = AF_CloudShadow(wp);
                col *= lerp(0.75, 1.0, cs);
                col += sun.color * spec * sun.shadowAttenuation * cs;
                col = AF_Aerial(col, wp, cam);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Lit"
}
