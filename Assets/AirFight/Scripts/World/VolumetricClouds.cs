using UnityEngine;
using UnityEngine.Rendering;

namespace AirFight
{
    /// <summary>
    /// 입체(볼류메트릭) 적운: 화면 픽셀마다 구름층(1.8~4.3 km)을 광선 행진해서 밀도·햇빛 투과·산란을 계산한다.
    /// 구름 속을 실제로 뚫고 지나갈 수 있고, 햇빛 쪽은 은빛 테두리, 아래쪽은 회색 그늘, 멀리는 대기에 묻힌다.
    /// 3D 노이즈(Perlin-Worley)와 날씨 맵은 시작할 때 만든다. PC 전용 (헤드셋은 기존 빌보드 구름).
    /// </summary>
    public class VolumetricClouds : MonoBehaviour
    {
        public const float Bottom = 1800f, Top = 4300f, WeatherPeriod = 64000f;
        const int WRes = 512, ShapeRes = 64, DetailRes = 32;
        static float[] coverage;     // CPU copy of the weather map (cloud shadows on the ground, start altitude checks)
        Transform shell;
        Material mat;

        public static bool Supported =>
            !Application.isMobilePlatform && SystemInfo.supports3DTextures && Realism.Find("AirFight/VolumeCloud") != null;

        public static void ResetStatics() { coverage = null; }

        /// <summary>Cloud coverage 0..1 at a world position (wraps).</summary>
        public static float CoverageAt(float x, float z)
        {
            if (coverage == null) return 0f;
            float u = Mathf.Repeat(x / WeatherPeriod + 0.5f, 1f) * WRes, v = Mathf.Repeat(z / WeatherPeriod + 0.5f, 1f) * WRes;
            int x0 = (int)u % WRes, z0 = (int)v % WRes, x1 = (x0 + 1) % WRes, z1 = (z0 + 1) % WRes;
            float tx = u - Mathf.Floor(u), tz = v - Mathf.Floor(v);
            return Mathf.Lerp(Mathf.Lerp(coverage[z0 * WRes + x0], coverage[z0 * WRes + x1], tx), Mathf.Lerp(coverage[z1 * WRes + x0], coverage[z1 * WRes + x1], tx), tz);
        }

        public void Build()
        {
            mat = new Material(Realism.Find("AirFight/VolumeCloud"));
            mat.SetTexture("_Weather", MakeWeather());
            mat.SetTexture("_Shape", MakeShape());
            mat.SetTexture("_Detail", MakeDetail());
            mat.SetVector("_Layer", new Vector4(Bottom, Top, WeatherPeriod, 0f));
            mat.renderQueue = 2950;                                   // after the opaque world and sky, before other transparents

            // a small inside-out box around the eye: every pixel runs the cloud ray-march
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "VolumetricCloudShell";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * 40f;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            shell = go.transform;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null && shell != null) shell.position = cam.transform.position;
        }

        // ---------------------------------------------------------------- tileable noise
        static uint H(int x, int y, int z, uint s)
        {
            uint h = ((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ ((uint)z * 83492791u) ^ (s * 2654435761u);
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return h;
        }
        static float R01(uint h) => (h & 0xffffff) / 16777216f;
        static int Wrap(int a, int p) => ((a % p) + p) % p;

        static float Perlin3(float x, float y, float z, int p, uint seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            float Grad(int ix, int iy, int iz, float dx, float dy, float dz)
            {
                uint h = H(Wrap(ix, p), Wrap(iy, p), Wrap(iz, p), seed);
                // 12 edge gradients
                switch (h % 12u)
                {
                    case 0: return dx + dy; case 1: return -dx + dy; case 2: return dx - dy; case 3: return -dx - dy;
                    case 4: return dx + dz; case 5: return -dx + dz; case 6: return dx - dz; case 7: return -dx - dz;
                    case 8: return dy + dz; case 9: return -dy + dz; case 10: return dy - dz; default: return -dy - dz;
                }
            }
            float u = fx * fx * fx * (fx * (fx * 6 - 15) + 10), v = fy * fy * fy * (fy * (fy * 6 - 15) + 10), w = fz * fz * fz * (fz * (fz * 6 - 15) + 10);
            float a = Mathf.Lerp(Grad(x0, y0, z0, fx, fy, fz), Grad(x0 + 1, y0, z0, fx - 1, fy, fz), u);
            float b = Mathf.Lerp(Grad(x0, y0 + 1, z0, fx, fy - 1, fz), Grad(x0 + 1, y0 + 1, z0, fx - 1, fy - 1, fz), u);
            float c = Mathf.Lerp(Grad(x0, y0, z0 + 1, fx, fy, fz - 1), Grad(x0 + 1, y0, z0 + 1, fx - 1, fy, fz - 1), u);
            float d = Mathf.Lerp(Grad(x0, y0 + 1, z0 + 1, fx, fy - 1, fz - 1), Grad(x0 + 1, y0 + 1, z0 + 1, fx - 1, fy - 1, fz - 1), u);
            return Mathf.Lerp(Mathf.Lerp(a, b, v), Mathf.Lerp(c, d, v), w);   // ≈ -1..1
        }

        /// <summary>Inverted Worley (1 at feature points) on a p³ periodic grid; x,y,z in cell units.</summary>
        static float Worley3(float x, float y, float z, int p, uint seed)
        {
            int cx = Mathf.FloorToInt(x), cy = Mathf.FloorToInt(y), cz = Mathf.FloorToInt(z);
            float best = 9f;
            for (int k = -1; k <= 1; k++)
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        int gx = cx + i, gy = cy + j, gz = cz + k;
                        uint h = H(Wrap(gx, p), Wrap(gy, p), Wrap(gz, p), seed);
                        float px = gx + R01(h), py = gy + R01(h * 747796405u + 1u), pz = gz + R01(h * 2891336453u + 7u);
                        float dx = px - x, dy = py - y, dz = pz - z;
                        best = Mathf.Min(best, dx * dx + dy * dy + dz * dz);
                    }
            return 1f - Mathf.Clamp01(Mathf.Sqrt(best));
        }

        static float Remap(float v, float a, float b, float c, float d) => c + (v - a) / Mathf.Max(b - a, 1e-4f) * (d - c);

        static Texture3D MakeShape()
        {
            int n = ShapeRes;
            var px = new Color32[n * n * n];
            System.Action<int> slice = z =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float fx = (float)x / n, fy = (float)y / n, fz = (float)z / n;
                        float perlin = 0f, amp = 1f, tot = 0f;
                        for (int o = 0; o < 3; o++)
                        {
                            int f = 4 << o;
                            perlin += amp * Perlin3(fx * f, fy * f, fz * f, f, 11u + (uint)o);
                            tot += amp; amp *= 0.5f;
                        }
                        perlin = perlin / tot * 0.5f + 0.5f;
                        float w1 = Worley3(fx * 4, fy * 4, fz * 4, 4, 21u), w2 = Worley3(fx * 8, fy * 8, fz * 8, 8, 22u), w3 = Worley3(fx * 16, fy * 16, fz * 16, 16, 23u);
                        float wf = w1 * 0.625f + w2 * 0.25f + w3 * 0.125f;
                        float pw = Mathf.Clamp01(Remap(perlin, wf - 1f, 1f, 0f, 1f));      // billowy Perlin-Worley
                        px[(z * n + y) * n + x] = new Color32((byte)(pw * 255f), (byte)(wf * 255f), 0, 255);
                    }
            };
            try { System.Threading.Tasks.Parallel.For(0, n, slice); } catch (System.Exception) { for (int z = 0; z < n; z++) slice(z); }
            var t = new Texture3D(n, n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "CloudShape" };
            t.SetPixels32(px); t.Apply(true, true);
            return t;
        }

        static Texture3D MakeDetail()
        {
            int n = DetailRes;
            var px = new Color32[n * n * n];
            System.Action<int> slice = z =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float fx = (float)x / n, fy = (float)y / n, fz = (float)z / n;
                        float w = Worley3(fx * 4, fy * 4, fz * 4, 4, 31u) * 0.625f + Worley3(fx * 8, fy * 8, fz * 8, 8, 32u) * 0.25f + Worley3(fx * 16, fy * 16, fz * 16, 16, 33u) * 0.125f;
                        px[(z * n + y) * n + x] = new Color32((byte)(w * 255f), 0, 0, 255);
                    }
            };
            try { System.Threading.Tasks.Parallel.For(0, n, slice); } catch (System.Exception) { for (int z = 0; z < n; z++) slice(z); }
            var t = new Texture3D(n, n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "CloudDetail" };
            t.SetPixels32(px); t.Apply(true, true);
            return t;
        }

        /// <summary>R = coverage (scattered cumulus fields, clear gaps), G = cloud type (towering vs flat).</summary>
        static Texture2D MakeWeather()
        {
            int n = WRes;
            var px = new Color32[n * n];
            var cov = new float[n * n];
            System.Action<int> row = y =>
            {
                for (int x = 0; x < n; x++)
                {
                    float fx = (float)x / n, fy = (float)y / n;
                    float f = 0f, amp = 1f, tot = 0f;
                    for (int o = 0; o < 5; o++)
                    {
                        int fr = 6 << o;
                        f += amp * Perlin3(fx * fr, fy * fr, 0.5f, fr, 41u + (uint)o);
                        tot += amp; amp *= 0.55f;
                    }
                    f = f / tot * 0.5f + 0.5f;
                    float c = Mathf.Clamp01((f - 0.47f) * 3.2f);
                    float typ = Mathf.Clamp01(Perlin3(fx * 3, fy * 3, 0.25f, 3, 51u) * 0.8f + 0.5f);
                    cov[y * n + x] = c;
                    px[y * n + x] = new Color32((byte)(c * 255f), (byte)(typ * 255f), 0, 255);
                }
            };
            try { System.Threading.Tasks.Parallel.For(0, n, row); } catch (System.Exception) { for (int y = 0; y < n; y++) row(y); }
            coverage = cov;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "CloudWeather" };
            t.SetPixels32(px); t.Apply(true, true);
            return t;
        }
    }
}
