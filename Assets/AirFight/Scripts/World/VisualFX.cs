using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AirFight
{
    /// <summary>
    /// 사실적 그래픽 세팅: HDR + ACES 톤매핑 + 블룸 + 색보정, MSAA 4x, 그림자 거리, 대기 산란 하늘,
    /// 하늘 기반 환경광, PBR 재질, 반투명 구름, 숲(GPU 인스턴싱), 고G 베이퍼, HDR 발광 조명.
    /// </summary>
    public static class Fx
    {
        static Material transparentBase;
        static Texture2D puffTex, softDotTex;

        /// <summary>Alpha-blended unlit material (Resources/AirFight/UnlitTransparent when present → survives builds).</summary>
        public static Material Transparent(Color c, Texture tex = null, bool additive = false)
        {
            if (transparentBase == null)
            {
                transparentBase = Resources.Load<Material>("AirFight/UnlitTransparent");
                if (transparentBase == null)
                {
                    transparentBase = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    ConfigureTransparent(transparentBase);
                }
            }
            var m = new Material(transparentBase);
            if (additive)
            {
                m.SetFloat("_Blend", 2f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.One);
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One); m.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            }
            Mats.SetColor(m, c);
            if (tex != null) { m.SetTexture("_BaseMap", tex); m.mainTexture = tex; }
            return m;
        }

        public static void ConfigureTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetOverrideTag("RenderType", "Transparent");
        }

        /// <summary>HDR unlit colour (values above 1 bloom).</summary>
        public static Material Glow(Color c, float intensity)
        {
            var m = Mats.Unique(new Color(c.r * intensity, c.g * intensity, c.b * intensity, 1f), true);
            return m;
        }

        /// <summary>PBR paint: metallic / smoothness.</summary>
        public static Material Pbr(Color c, float metallic, float smoothness)
        {
            var m = Mats.Unique(c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        /// <summary>Soft, lumpy cumulus puff (white, alpha) with a slightly darker base.</summary>
        public static Texture2D PuffTexture()
        {
            if (puffTex != null) return puffTex;
            const int N = 128;
            puffTex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float n = 0f, amp = 0.5f, f = 3f;
                    for (int o = 0; o < 4; o++) { n += amp * Mathf.PerlinNoise(u * f + 13.1f, v * f + 7.7f); amp *= 0.5f; f *= 2f; }
                    float a = Mathf.Clamp01((1f - r) * 1.6f + (n - 0.5f) * 1.2f);
                    a = a * a * (3f - 2f * a);
                    float shade = Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(v * 0.6f + 0.6f + (n - 0.5f) * 0.4f));
                    px[y * N + x] = new Color(shade, shade, shade * 1.02f, a * 0.9f);
                }
            puffTex.SetPixels(px); puffTex.Apply(true);
            return puffTex;
        }

        public static Texture2D SoftDot()
        {
            if (softDotTex != null) return softDotTex;
            const int N = 32;
            softDotTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)); a *= a;
                    softDotTex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            softDotTex.Apply();
            return softDotTex;
        }
    }

    /// <summary>Custom realistic shaders (Resources/AirFight/Shaders). Each returns null if the shader is missing / unsupported.</summary>
    public static class Realism
    {
        public static Shader Find(string name)
        {
            var s = Shader.Find(name);
            return s != null && s.isSupported ? s : null;
        }
        public static Texture2D Tex(string name) => Resources.Load<Texture2D>("AirFight/Textures/" + name);

        /// <summary>Sun direction/colour for the sky, ocean and cloud shaders (call after the sun is set up).</summary>
        public static void PushSun()
        {
            var sun = RenderSettings.sun;
            Vector3 dir = sun != null ? -sun.transform.forward : new Vector3(0.4f, 0.7f, 0.5f).normalized;
            Color c = sun != null ? sun.color * (sun.intensity * 0.5f) : Color.white;   // ≈ 1 at noon, dim at night
            Shader.SetGlobalVector("_AF_SunDir", new Vector4(dir.x, dir.y, dir.z, 0f));
            Shader.SetGlobalVector("_AF_SunColor", new Vector4(c.r, c.g, c.b, 1f));
        }
    }

    /// <summary>Camera / pipeline / lighting setup for a realistic look.</summary>
    public static class GraphicsSetup
    {
        public static void Apply(Camera cam, bool mobile)
        {
            // ---- URP pipeline asset ----
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.msaaSampleCount = 4;
                if (!mobile) urp.supportsCameraDepthTexture = true;   // volumetric clouds stop at the terrain / aircraft
                urp.supportsHDR = true;
                urp.shadowDistance = mobile ? 250f : 600f;
                urp.shadowCascadeCount = mobile ? 2 : 4;
            }
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;

            // ---- camera ----
            cam.allowHDR = true;
            cam.allowMSAA = true;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.None;   // MSAA handles it (cheaper in VR)
            data.renderShadows = true;
            if (!mobile) data.requiresDepthTexture = true;

            // ---- post-processing volume ----
            var vgo = new GameObject("PostProcessing");
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            vol.sharedProfile = prof;
            var tm = prof.Add<Tonemapping>(true);
            tm.mode.Override(TonemappingMode.ACES);
            var ca = prof.Add<ColorAdjustments>(true);
            TimeOfDay.adjust = ca;
            ca.postExposure.Override(0.45f);
            ca.contrast.Override(8f);
            ca.saturation.Override(6f);
            var bloom = prof.Add<Bloom>(true);
            TimeOfDay.bloom = bloom;
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(mobile ? 0.35f : 0.6f);
            bloom.scatter.Override(0.65f);
            bloom.highQualityFiltering.Override(!mobile);
            var vig = prof.Add<Vignette>(true);           // the eye's natural fall-off toward the edges
            vig.intensity.Override(0.16f);
            vig.smoothness.Override(0.45f);
            var wb = prof.Add<WhiteBalance>(true);
            TimeOfDay.white = wb;
            wb.temperature.Override(4f);

            // ---- analytic sky (AirFight/Sky): deep blue zenith, hazy horizon, sun glow; fog matches its horizon ----
            Realism.PushSun();
            var skyShader = Realism.Find("AirFight/Sky");
            if (skyShader != null)
            {
                RenderSettings.skybox = new Material(skyShader);
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1.0f;
                RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.86f).gamma;    // = sky horizon (linear) in gamma space
                RenderSettings.fogStartDistance = 4000f;
                RenderSettings.fogEndDistance = 38000f;
                DynamicGI.UpdateEnvironment();
            }
            else if (RenderSettings.skybox != null && RenderSettings.skybox.shader != null && RenderSettings.skybox.shader.name.Contains("Procedural"))
            {
                var sky = new Material(RenderSettings.skybox);
                sky.SetFloat("_SunSize", 0.03f);
                sky.SetFloat("_SunSizeConvergence", 6f);
                sky.SetFloat("_AtmosphereThickness", 1.05f);
                sky.SetColor("_SkyTint", new Color(0.5f, 0.55f, 0.62f));
                sky.SetColor("_GroundColor", new Color(0.32f, 0.35f, 0.38f));
                sky.SetFloat("_Exposure", 1.25f);
                RenderSettings.skybox = sky;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1.1f;
                DynamicGI.UpdateEnvironment();
            }
            TimeOfDay.SetupSun(RenderSettings.sun);
            TimeOfDay.ApplyLook();
        }
    }

    /// <summary>Rotates every child quad to face the main camera (cloud puffs).</summary>
    public class Billboards : MonoBehaviour
    {
        Transform[] quads;
        void Start()
        {
            quads = new Transform[transform.childCount];
            for (int i = 0; i < quads.Length; i++) quads[i] = transform.GetChild(i);
        }
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || quads == null) return;
            Vector3 cp = cam.transform.position;
            foreach (var q in quads)
            {
                Vector3 d = q.position - cp;
                if (d.sqrMagnitude > 1f) q.rotation = Quaternion.LookRotation(d, Vector3.up);
            }
        }
    }

    /// <summary>
    /// Forests: instanced tree impostors (conifers up the mountains, broadleaf trees in the lowlands) placed exactly where
    /// the terrain colour map shows forest. Generated lazily in 1 km cells around the camera.
    /// </summary>
    public class ForestRenderer : MonoBehaviour
    {
        const float Cell = 1000f;
        float drawDistance, spacing, maxAgl;
        class CellData { public List<Matrix4x4[]> con = new List<Matrix4x4[]>(), broad = new List<Matrix4x4[]>(); public List<int> conN = new List<int>(), broadN = new List<int>(); }
        readonly Dictionary<Vector2Int, CellData> cells = new Dictionary<Vector2Int, CellData>();
        Mesh conifer, broadleaf, cone;
        RenderParams rp;
        bool impostors;
        readonly List<Vector2Int> drop = new List<Vector2Int>();

        public void Build(int maxTrees)
        {
            bool mobile = Application.isMobilePlatform;
            drawDistance = mobile ? 3500f : 6500f;
            spacing = mobile ? 34f : 22f;
            maxAgl = mobile ? 2500f : 4000f;
            var shader = Realism.Find("AirFight/Tree");
            var atlas = Realism.Tex("trees");
            impostors = shader != null && atlas != null;
            Material mat;
            if (impostors)
            {
                mat = new Material(shader);
                mat.SetTexture("_BaseMap", atlas);
                conifer = CardTree(0f, 0.45f, 0.5f, 0.94f, true);
                broadleaf = CardTree(1f / 3f, 0.58f, 0.62f, 0.9f, false);
            }
            else
            {
                mat = Fx.Pbr(new Color(0.14f, 0.24f, 0.11f), 0f, 0.05f);
                cone = BuildConeMesh();
            }
            mat.enableInstancing = true;
            rp = new RenderParams(mat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true };
        }

        /// <summary>3 vertical cards at 60° + a horizontal card seen from above. Height 1, width = aspect.</summary>
        static Mesh CardTree(float u0, float topY, float aspect, float topSize, bool coniferTop)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float w = aspect * 0.5f, crownY = coniferTop ? 0.45f : 0.6f;
            for (int k = 0; k < 3; k++)
            {
                var rot = Quaternion.Euler(0, k * 60f, 0);
                int b = v.Count;
                Vector3[] p = { new Vector3(-w, 0, 0), new Vector3(w, 0, 0), new Vector3(w, 1, 0), new Vector3(-w, 1, 0) };
                Vector2[] q = { new Vector2(u0, 0), new Vector2(u0 + 1f / 3f, 0), new Vector2(u0 + 1f / 3f, 1), new Vector2(u0, 1) };
                for (int i = 0; i < 4; i++)
                {
                    var pp = rot * p[i];
                    v.Add(pp); uv.Add(q[i]);
                    n.Add((pp - new Vector3(0, crownY, 0) + Vector3.up * 0.3f).normalized);   // round crown shading
                }
                t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            {
                int b = v.Count; float s = aspect * topSize * 0.5f;
                float v0 = coniferTop ? 0.5f : 0f;
                Vector3[] p = { new Vector3(-s, topY, -s), new Vector3(s, topY, -s), new Vector3(s, topY, s), new Vector3(-s, topY, s) };
                Vector2[] q = { new Vector2(2f / 3f, v0), new Vector2(1f, v0), new Vector2(1f, v0 + 0.5f), new Vector2(2f / 3f, v0 + 0.5f) };
                for (int i = 0; i < 4; i++) { v.Add(p[i]); uv.Add(q[i]); n.Add((p[i] * 0.6f + Vector3.up).normalized); }
                t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            var m = new Mesh { name = coniferTop ? "ConiferCards" : "BroadleafCards" };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.bounds = new Bounds(new Vector3(0, 0.5f, 0), new Vector3(1.2f, 1.2f, 1.2f));
            return m;
        }

        static Mesh BuildConeMesh()
        {
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i < 7; i++)
            {
                float a0 = i * Mathf.PI * 2f / 7, a1 = (i + 1) * Mathf.PI * 2f / 7;
                int k = v.Count;
                v.Add(new Vector3(Mathf.Cos(a0) * 0.25f, 0.08f, Mathf.Sin(a0) * 0.25f)); v.Add(new Vector3(0, 1f, 0)); v.Add(new Vector3(Mathf.Cos(a1) * 0.25f, 0.08f, Mathf.Sin(a1) * 0.25f));
                t.Add(k); t.Add(k + 1); t.Add(k + 2);
            }
            var m = new Mesh { name = "Conifer" };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Same forest coverage as the terrain colour map (WorldBuilder.BuildTerrainTexture).</summary>
        static float ForestAmount(float x, float z, out float h)
        {
            h = WorldBuilder.GridHeight(x, z);
            if (h < 30f || h > 1300f) return 0f;
            const float d = 23f;
            float hl = WorldBuilder.GridHeight(x - d, z), hr = WorldBuilder.GridHeight(x + d, z);
            float hd = WorldBuilder.GridHeight(x, z - d), hu = WorldBuilder.GridHeight(x, z + d);
            float slope = 1f - new Vector3(-(hr - hl) / (2f * d), 1f, -(hu - hd) / (2f * d)).normalized.y;
            float n1 = Mathf.PerlinNoise(x / 260f, z / 260f);
            float f = Mathf.Clamp01((Mathf.PerlinNoise(x / 1800f + 3.3f, z / 1800f + 9.1f) + n1 * 0.35f - 0.62f) * 4f);
            f = Mathf.Max(f, Mathf.Clamp01((WorldBuilder.Flow(x, z) - 0.55f) * 3f) * 0.8f);
            f *= Mathf.Clamp01((h - 30f) / 60f) * (1f - Mathf.Clamp01((h - 1150f) / 200f)) * (1f - Mathf.Clamp01((slope - 0.22f) * 6f));
            float rock = Mathf.Clamp01((slope - 0.12f) * 6f) + Mathf.Clamp01((h - 1350f) / 200f);
            return f * (1f - Mathf.Clamp01(rock));
        }

        static float Hash(int x, int z, int s)
        {
            uint hsh = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(s * 83492791);
            hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
            return (hsh & 0xffffff) / 16777216f;
        }

        CellData Generate(Vector2Int key)
        {
            var cd = new CellData();
            var con = new List<Matrix4x4>(); var br = new List<Matrix4x4>();
            int n = Mathf.CeilToInt(Cell / spacing);
            float x0 = key.x * Cell, z0 = key.y * Cell;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int gx = key.x * n + i, gz = key.y * n + j;
                    float px = x0 + (i + Hash(gx, gz, 1)) * spacing, pz = z0 + (j + Hash(gx, gz, 2)) * spacing;
                    float f = ForestAmount(px, pz, out float h);
                    float keep = f > 0.05f ? Mathf.Clamp01(f * 1.3f) : 0.012f;      // forests + the odd lone tree in the meadows
                    if (h < 8f || Hash(gx, gz, 3) > keep) continue;
                    if (NearAirport(px, pz)) continue;
                    bool isCon = Hash(gx, gz, 4) < Mathf.Clamp01((h - 250f) / 500f) * 0.9f + 0.1f;
                    float height = isCon ? 14f + 12f * Hash(gx, gz, 5) : 10f + 9f * Hash(gx, gz, 5);
                    if (!impostors) isCon = true;
                    float wscale = impostors ? height : height * 0.9f;
                    var m = Matrix4x4.TRS(new Vector3(px, h - 0.6f, pz), Quaternion.Euler(0, Hash(gx, gz, 6) * 360f, 0), new Vector3(wscale, height, wscale));
                    (isCon ? con : br).Add(m);
                }
            Split(con, cd.con, cd.conN);
            Split(br, cd.broad, cd.broadN);
            return cd;
        }

        static void Split(List<Matrix4x4> src, List<Matrix4x4[]> dst, List<int> counts)
        {
            for (int i = 0; i < src.Count; i += 1023)
            {
                int k = Mathf.Min(1023, src.Count - i);
                dst.Add(src.GetRange(i, k).ToArray()); counts.Add(k);
            }
        }

        static bool NearAirport(float x, float z)
        {
            foreach (var ap in WorldBuilder.Airports)
                if ((new Vector2(x, z) - ap.center).magnitude < ap.length * 0.5f + 1200f) return true;
            return false;
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null || rp.material == null) return;
            Vector3 c = cam.transform.position;
            if (c.y - WorldBuilder.GridHeight(c.x, c.z) > maxAgl) return;    // trees are sub-pixel from high up
            int r = Mathf.CeilToInt(drawDistance / Cell);
            var cc = new Vector2Int(Mathf.FloorToInt(c.x / Cell), Mathf.FloorToInt(c.z / Cell));
            int budget = 3;
            float half = WorldBuilder.Size * 0.5f;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var key = new Vector2Int(cc.x + dx, cc.y + dz);
                    Vector2 centre = new Vector2((key.x + 0.5f) * Cell, (key.y + 0.5f) * Cell);
                    if ((centre - new Vector2(c.x, c.z)).magnitude > drawDistance + Cell * 0.7f) continue;
                    if (Mathf.Abs(centre.x) > half || Mathf.Abs(centre.y) > half) continue;
                    if (!cells.TryGetValue(key, out var cd))
                    {
                        if (budget-- <= 0) continue;
                        cells[key] = cd = Generate(key);
                    }
                    var mc = impostors ? conifer : cone;
                    for (int b = 0; b < cd.con.Count; b++) Graphics.RenderMeshInstanced(rp, mc, 0, cd.con[b], cd.conN[b]);
                    if (impostors) for (int b = 0; b < cd.broad.Count; b++) Graphics.RenderMeshInstanced(rp, broadleaf, 0, cd.broad[b], cd.broadN[b]);
                }
            // forget cells far behind
            if (Time.frameCount % 60 == 0)
            {
                drop.Clear();
                foreach (var k in cells.Keys)
                    if ((new Vector2((k.x + 0.5f) * Cell, (k.y + 0.5f) * Cell) - new Vector2(c.x, c.z)).magnitude > drawDistance + 3000f) drop.Add(k);
                foreach (var k in drop) cells.Remove(k);
            }
        }
    }

    /// <summary>Wing-tip vapour trails at high G / high AoA (like real jets pulling hard in humid air).</summary>
    public class VaporFx : MonoBehaviour
    {
        FlightModel fm;
        TrailRenderer left, right;

        public void Init(FlightModel flight, Vector3 tipRight)
        {
            fm = flight;
            right = Make(tipRight);
            left = Make(new Vector3(-tipRight.x, tipRight.y, tipRight.z));
        }

        TrailRenderer Make(Vector3 local)
        {
            var go = new GameObject("Vapor");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            var tr = go.AddComponent<TrailRenderer>();
            tr.sharedMaterial = Fx.Transparent(new Color(1f, 1f, 1f, 0.55f), Fx.SoftDot());
            tr.time = 0.7f;
            tr.startWidth = 0.5f; tr.endWidth = 2.5f;
            tr.minVertexDistance = 3f;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0.7f, 0f), new GradientAlphaKey(0f, 1f) });
            tr.colorGradient = g;
            tr.shadowCastingMode = ShadowCastingMode.Off;
            tr.emitting = false;
            return tr;
        }

        void Update()
        {
            if (fm == null) return;
            bool on = !fm.Destroyed && !fm.onGround && fm.Airspeed > 110f && (fm.GLoad > 5.5f || fm.AoA > 17f);
            left.emitting = on; right.emitting = on;
        }
    }
}
