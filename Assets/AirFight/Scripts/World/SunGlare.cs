using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 눈부신 태양: 태양 쪽을 보면 빛 번짐(글로우) + 방사형 빛줄기 + 시야 전체가 뿌옇게 밝아지는 베일 글레어.
    /// 지형·기체·구름에 가리면 사라진다. 사람 눈 기준(카메라 렌즈 고스트 없음)이라 VR에서도 자연스럽다.
    /// </summary>
    public class SunGlare : MonoBehaviour
    {
        Camera cam;
        Transform glow, star, veil;
        Material glowMat, starMat, veilMat;
        float vis;                       // smoothed visibility 0..1
        static Texture2D starTex;

        public void Init(Camera c)
        {
            cam = c;
            glowMat = Fx.Transparent(new Color(4f, 3.6f, 3.0f, 0.9f), Fx.SoftDot(), true);
            starMat = Fx.Transparent(new Color(3f, 2.8f, 2.4f, 0.8f), StarTexture(), true);
            veilMat = Fx.Transparent(new Color(1f, 0.92f, 0.78f, 0f), null, true);
            foreach (var m in new[] { glowMat, starMat, veilMat }) m.renderQueue = 3500;   // over clouds
            glow = Quad("SunGlow", glowMat);
            star = Quad("SunStar", starMat);
            veil = Quad("SunVeil", veilMat);
            veil.SetParent(cam.transform, false);
            veil.localPosition = new Vector3(0, 0, 0.6f);
            veil.localRotation = Quaternion.identity;
            veil.localScale = new Vector3(4f, 4f, 1f);
        }

        Transform Quad(string name, Material m)
        {
            var q = Build.Prim(PrimitiveType.Quad, null, Vector3.zero, Vector3.one, Color.white, true, null, false, name);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return q.transform;
        }

        static Texture2D StarTexture()
        {
            if (starTex != null) return starTex;
            const int N = 128;
            starTex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v), a = Mathf.Atan2(v, u);
                    float rays = 0f;
                    for (int k = 0; k < 6; k++)            // 6 thin streaks (12 spikes), slightly uneven like a real eye
                    {
                        float ang = k * Mathf.PI / 6f + 0.13f * Mathf.Sin(k * 2.3f);
                        float d = Mathf.Abs(Mathf.Sin(a - ang));
                        rays += Mathf.Exp(-d * d / 0.0009f) * (0.6f + 0.4f * Mathf.Cos(k * 1.7f));
                    }
                    float fall = Mathf.Clamp01(1f - r);
                    float val = rays * fall * fall * 0.8f + Mathf.Exp(-r * r * 40f);
                    px[y * N + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(val));
                }
            starTex.SetPixels(px); starTex.Apply(true);
            return starTex;
        }

        void LateUpdate()
        {
            if (cam == null || RenderSettings.sun == null) return;
            Vector3 sunDir = -RenderSettings.sun.transform.forward;
            Vector3 cp = cam.transform.position;

            // occlusion: terrain / aircraft (ray), clouds (inside a cloud puff cluster along the line of sight)
            bool blocked = sunDir.y < -0.02f || Physics.Raycast(cp + sunDir * 2f, sunDir, 25000f, ~0, QueryTriggerInteraction.Ignore);
            float target = blocked ? 0f : Mathf.Clamp01((RenderSettings.sun.intensity - 0.3f) / 1.7f);   // the moon doesn't dazzle
            vis = Mathf.MoveTowards(vis, target, Time.deltaTime * 4f);

            // the glow sits far away in the sun direction and is always the same angular size (≈ 7°)
            const float dist = 9000f;
            float size = dist * Mathf.Tan(3.5f * Mathf.Deg2Rad) * 2f;
            glow.position = cp + sunDir * dist;
            glow.rotation = Quaternion.LookRotation(sunDir);
            glow.localScale = new Vector3(size, size, 1f);
            star.position = cp + sunDir * (dist * 0.99f);
            float look = Mathf.Max(0f, Vector3.Dot(cam.transform.forward, sunDir));
            star.rotation = Quaternion.LookRotation(sunDir, cam.transform.up);
            star.localScale = Vector3.one * size * Mathf.Lerp(1.2f, 3.2f, Mathf.Pow(look, 8f));

            Mats.SetColor(glowMat, new Color(4f, 3.6f, 3.0f, 0.85f * vis));
            Mats.SetColor(starMat, new Color(3f, 2.8f, 2.4f, 0.75f * vis * Mathf.Lerp(0.3f, 1f, Mathf.Pow(look, 4f))));
            // veiling glare: the whole view washes out when looking (nearly) into the sun
            float veilA = vis * (Mathf.Pow(look, 12f) * 0.42f + Mathf.Pow(look, 3f) * 0.06f);
            Mats.SetColor(veilMat, new Color(1f, 0.92f, 0.78f, veilA));
            glow.gameObject.SetActive(vis > 0.01f);
            star.gameObject.SetActive(vis > 0.01f);
            veil.gameObject.SetActive(veilA > 0.003f);
        }
    }
}
