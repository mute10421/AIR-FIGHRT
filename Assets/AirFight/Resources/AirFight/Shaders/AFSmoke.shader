Shader "AirFight/Smoke"
{
    // Missile / exhaust smoke for TrailRenderers: vertex colour + alpha (fades along the trail), sun-lit, hazed with distance.
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Name "Smoke"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "AFSkyCommon.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
            CBUFFER_END
            struct A { float4 p : POSITION; float2 uv : TEXCOORD0; float4 c : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; float2 uv : TEXCOORD0; float4 c : COLOR; float3 wp : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            V vert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.wp = TransformObjectToWorld(i.p.xyz);
                o.p = TransformWorldToHClip(o.wp);
                o.uv = i.uv; o.c = i.c;
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 t = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor * i.c;
                Light sun = GetMainLight();
                float3 col = t.rgb * (sun.color * 0.55 + SampleSH(float3(0, 1, 0)) * 0.8);
                float3 cam = GetCameraPositionWS();
                col = AF_Aerial(col, i.wp, cam);
                float a = t.a * smoothstep(3.0, 25.0, distance(cam, i.wp));     // don't fill the canopy when flying through it
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
