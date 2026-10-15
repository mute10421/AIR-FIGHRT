Shader "AirFight/VolumeCloud"
{
    // Ray-marched cumulus layer. Drawn on an inside-out box around the eye; stops at the scene depth so mountains and
    // aircraft in front hide the clouds and clouds in front hide them.
    Properties
    {
        _Weather ("Weather (R coverage, G type)", 2D) = "black" {}
        _Shape ("Shape noise", 3D) = "" {}
        _Detail ("Detail noise", 3D) = "" {}
        _Layer ("bottom, top, weather period", Vector) = (1800, 4300, 64000, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Cull Front
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha
        Pass
        {
            Name "Clouds"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "AFSkyCommon.hlsl"

            TEXTURE2D(_Weather); SAMPLER(sampler_Weather);
            TEXTURE3D(_Shape); SAMPLER(sampler_Shape);
            TEXTURE3D(_Detail); SAMPLER(sampler_Detail);
            CBUFFER_START(UnityPerMaterial)
                float4 _Weather_ST;
                float4 _Layer;
            CBUFFER_END

            struct A { float4 p : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; float4 sp : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            V vert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.wp = TransformObjectToWorld(i.p.xyz);
                o.pos = TransformWorldToHClip(o.wp);
                o.sp = ComputeScreenPos(o.pos);
                return o;
            }

            float CloudRemap(float v, float a, float b, float c, float d) { return c + (v - a) / max(b - a, 1e-4) * (d - c); }

            // extinction coefficient (1/m)
            float Density(float3 p, bool detail)
            {
                float h = (p.y - _Layer.x) / (_Layer.y - _Layer.x);
                if (h <= 0.0 || h >= 1.0) return 0.0;
                float4 w = SAMPLE_TEXTURE2D_LOD(_Weather, sampler_Weather, p.xz / _Layer.z + 0.5, 0);
                float cov = w.r;
                if (cov < 0.01) return 0.0;
                float topH = lerp(0.45, 1.0, w.g);                                   // flat fair-weather cu … towering cu
                float grad = smoothstep(0.0, 0.07, h) * (1.0 - smoothstep(topH * 0.6, topH, h));
                float shape = SAMPLE_TEXTURE3D_LOD(_Shape, sampler_Shape, p / 4800.0, 0).r;
                float d = saturate(CloudRemap(shape * grad, 1.0 - cov, 1.0, 0.0, 1.0)) * cov;
                if (d <= 0.0) return 0.0;
                if (detail)
                {
                    float det = SAMPLE_TEXTURE3D_LOD(_Detail, sampler_Detail, p / 650.0, 0).r;
                    float erode = lerp(det, 1.0 - det, saturate(h * 4.0)) * 0.3;   // wispy bottoms, billowy tops
                    d = saturate(CloudRemap(d, erode, 1.0, 0.0, 1.0));
                }
                return d * 0.035;
            }

            float HG(float c, float g) { float g2 = g * g; return (1.0 - g2) / (4.0 * PI * pow(abs(1.0 + g2 - 2.0 * g * c), 1.5)); }

            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 cam = GetCameraPositionWS();
                float3 rd = normalize(i.wp - cam);

                // distance to whatever is already drawn (terrain, aircraft); sky = far plane
                float2 suv = i.sp.xy / i.sp.w;
                float raw = SampleSceneDepth(suv);
                #if UNITY_REVERSED_Z
                    bool sky = raw <= 1e-6;
                #else
                    bool sky = raw >= 1.0 - 1e-6;
                #endif
                float3 fwd = -UNITY_MATRIX_V[2].xyz;
                float sceneT = sky ? 1e8 : LinearEyeDepth(raw, _ZBufferParams) / max(dot(rd, fwd), 1e-3);

                // intersect the cloud slab
                float B = _Layer.x, T = _Layer.y;
                float t0, t1;
                if (abs(rd.y) < 1e-4)
                {
                    if (cam.y < B || cam.y > T) return 0;
                    t0 = 0; t1 = 60000;
                }
                else
                {
                    float tb = (B - cam.y) / rd.y, tt = (T - cam.y) / rd.y;
                    t0 = max(min(tb, tt), 0.0); t1 = max(tb, tt);
                }
                t1 = min(min(t1, sceneT), t0 + 40000.0);
                if (t1 <= t0) return 0;

                Light sun = GetMainLight();
                float3 L = sun.direction;
                float cosT = dot(rd, L);
                float phase = lerp(HG(cosT, -0.2), HG(cosT, 0.65), 0.7) * 4.0 * PI * 0.55 + 0.45;
                float3 skyAmb = AF_SkyColor(float3(0, 1, 0)) * 1.15;
                float3 gndAmb = float3(0.30, 0.33, 0.36) * lerp(1.0, 0.03, _AF_Night);

                const int STEPS = 72;
                float ds = max((t1 - t0) / STEPS, 25.0);
                float jitter = frac(sin(dot(suv * _ScreenParams.xy, float2(12.9898, 78.233))) * 43758.5453);
                float t = t0 + ds * jitter;
                float trans = 1.0;
                float3 col = 0;
                float tSum = 0, wSum = 0;
                [loop] for (int s = 0; s < STEPS; s++)
                {
                    if (t > t1 || trans < 0.01) break;
                    float3 p = cam + rd * t;
                    float sigma = Density(p, true);
                    if (sigma > 1e-5)
                    {
                        // light: optical depth toward the sun (6 growing steps)
                        float od = 0, lt = 0;
                        [unroll] for (int k = 0; k < 6; k++)
                        {
                            float stepL = 40.0 * pow(1.9, k);
                            lt += stepL;
                            od += Density(p + L * (lt - stepL * 0.5), k < 2) * stepL;
                        }
                        float beer = exp(-od) * 0.7 + exp(-od * 0.25) * 0.3;                // multiple-scattering approximation
                        float powder = 1.0 - exp(-sigma * 2.0 * 60.0);
                        float h = saturate((p.y - B) / (T - B));
                        float3 amb = lerp(gndAmb, skyAmb, h);
                        float3 S = sun.color * beer * phase * lerp(0.6, 1.0, powder) + amb * 0.9;
                        float stepT = exp(-sigma * ds);
                        col += trans * S * (1.0 - stepT);
                        tSum += t * trans * (1.0 - stepT); wSum += trans * (1.0 - stepT);
                        trans *= stepT;
                    }
                    t += ds;
                }
                float alpha = 1.0 - trans;
                if (alpha < 0.002) return 0;
                // haze between the eye and the cloud
                float3 hit = cam + rd * (tSum / max(wSum, 1e-4));
                float3 c = AF_Aerial(col / alpha, hit, cam);
                return half4(c * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
