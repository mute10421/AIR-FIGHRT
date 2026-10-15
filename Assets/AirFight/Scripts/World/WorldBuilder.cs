using UnityEngine;
using UnityEngine.Rendering;

namespace AirFight
{
    /// <summary>48km x 48km 절차적 섬 지형 + 바다 + 구름 + 조명/안개.</summary>
    public static class WorldBuilder
    {
        public const float Size = 48000f;

        /// <summary>Play-mode start without a domain reload / map switch: forget the previous world.</summary>
        internal static void ResetStatics() { airports = null; hgrid = null; MapTexture = null; MacroNormal = null; VolumetricClouds.ResetStatics(); }
        public static int Grid => Application.isMobilePlatform ? 384 : 512;
        static int TexRes => Application.isMobilePlatform ? 1024 : 2048;  // height grid = native heightmap resolution (23 m on PC)

        // ---------------------------------------------------------------- cached height field
        // Height() costs ~6 Perlin lookups; the terrain mesh, the 1536² colour texture and the forests used to call it
        // ~8 million times at start-up. Now it is evaluated once per texel (multi-threaded) and everything samples this grid.
        static float[] hgrid;
        static int hres;
        static float hstep;

        static void BuildHeightGrid(int res)
        {
            var _ = Airports;   // initialise statics on the main thread before going wide
            hres = res; hstep = Size / res;
            int n = res + 1;
            var g = new float[n * n];
            float half = Size * 0.5f, step = hstep;
            System.Action<int> row = z => { float wz = z * step - half; for (int x = 0; x < n; x++) g[z * n + x] = Height(x * step - half, wz); };
            try { System.Threading.Tasks.Parallel.For(0, n, row); }
            catch (System.Exception) { for (int z = 0; z < n; z++) row(z); }
            hgrid = g;
        }

        /// <summary>Bilinear sample of the cached height field (falls back to the exact function before it exists).</summary>
        public static float GridHeight(float x, float z)
        {
            if (hgrid == null) return Height(x, z);
            float half = Size * 0.5f;
            float fx = Mathf.Clamp((x + half) / hstep, 0f, hres - 0.001f), fz = Mathf.Clamp((z + half) / hstep, 0f, hres - 0.001f);
            int ix = (int)fx, iz = (int)fz; float tx = fx - ix, tz = fz - iz; int n = hres + 1;
            float a = hgrid[iz * n + ix], b = hgrid[iz * n + ix + 1], c = hgrid[(iz + 1) * n + ix], d = hgrid[(iz + 1) * n + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }
        public const float PlayArea = 21000f;

        // ---------------------------------------------------------------- Airports
        public const float RunwayLength = 3000f, RunwayWidth = 45f;

        public class Airport
        {
            public string name;
            public Vector2 center;      // x, z
            public float heading;       // runway direction (deg, 0 = north)
            public float elevation;
            public float length = RunwayLength, width = RunwayWidth;
            /// <summary>Runway surface normal in Unity space (real map: the local vertical, tilted by the Earth's curvature).</summary>
            public Vector3 up = Vector3.up;
            public string icao = "";
            public Vector3 Dir => Quaternion.Euler(0, heading, 0) * Vector3.forward;
            public Vector3 Center3 => new Vector3(center.x, elevation, center.y);
            /// <summary>Height of the runway plane at (x, z).</summary>
            public float SurfaceY(float x, float z) => elevation - (up.x * (x - center.x) + up.z * (z - center.y)) / up.y;
            /// <summary>Runway orientation (forward = runway heading, up = surface normal).</summary>
            public Quaternion Rotation => Quaternion.LookRotation(Vector3.ProjectOnPlane(Dir, up).normalized, up);
            /// <summary>Threshold you land over when flying along 'dir' (+1 = runway heading, -1 = reciprocal).</summary>
            public Vector3 Threshold(float dir) { var t = Center3 - Dir * (dir * length * 0.5f); t.y = SurfaceY(t.x, t.z); return t; }
            public string RunwayName(float dir)
            {
                float h = dir > 0 ? heading : heading + 180f;
                int n = Mathf.RoundToInt(((h % 360f) + 360f) % 360f / 10f); if (n == 0) n = 36;
                return n.ToString("00");
            }
        }

        static Airport[] airports;
        public static Airport[] Airports
        {
            get
            {
                if (airports == null)
                {
                    LoadHeightmap();
                    airports = new[]
                    {
                        new Airport { name = "SOUTH AB", center = new Vector2(0f, -14000f), heading = 0f },
                        new Airport { name = "EAST AB", center = new Vector2(15500f, -5200f), heading = 75f },
                        new Airport { name = "NORTH AB", center = new Vector2(-2500f, 13500f), heading = 330f },
                        new Airport { name = "WEST AB", center = new Vector2(-13500f, -3000f), heading = 20f },
                        new Airport { name = "VALLEY AB", center = new Vector2(-4600f, -5000f), heading = 90f },
                        new Airport { name = "NE AB", center = new Vector2(9000f, 9500f), heading = 135f },
                        new Airport { name = "SE AB", center = new Vector2(8500f, -11000f), heading = 45f },
                        new Airport { name = "SW AB", center = new Vector2(-9300f, -16100f), heading = 160f },
                        new Airport { name = "NW AB", center = new Vector2(-11000f, 8000f), heading = 70f },
                    };
                    // airfield elevation follows the natural ground there (sea → built-up island, mountains → plateau)
                    foreach (var ap in airports)
                        ap.elevation = Mathf.Round(Mathf.Clamp(RawHeight(ap.center.x, ap.center.y), 15f, 380f));
                }
                return airports;
            }
        }

        public static Airport Home => Airports[0];
        /// <summary>Terrain colour texture, reused by the tactical map.</summary>
        public static Texture2D MapTexture { get; private set; }

        public static Airport Nearest(Vector3 pos)
        {
            Airport best = null; float bd = float.MaxValue;
            foreach (var ap in Airports)
            {
                float d = (new Vector2(pos.x, pos.z) - ap.center).sqrMagnitude;
                if (d < bd) { bd = d; best = ap; }
            }
            return best;
        }

        // ---------------------------------------------------------------- eroded heightmap (Resources/AirFight/Terrain/terrain.bytes)
        // Generated offline: island + mountain belt shaped by river (stream-power) erosion and hillslope diffusion,
        // 2048² over 48 km (23 m), plus a drainage map (rivers / valleys) used for the colours.
        static float[] hmap; static byte[] fmap; static int hmN; static bool hmTried;

        static void LoadHeightmap()
        {
            if (hmTried) return;
            hmTried = true;
            var ta = Resources.Load<TextAsset>("AirFight/Terrain/terrain");
            if (ta == null) return;
            var b = ta.bytes;
            int n = System.BitConverter.ToInt32(b, 0);
            float mn = System.BitConverter.ToSingle(b, 4), mx = System.BitConverter.ToSingle(b, 8);
            if (b.Length < 12 + n * n * 3) return;
            var h = new float[n * n];
            float k = (mx - mn) / 65535f;
            for (int i = 0; i < n * n; i++) h[i] = mn + k * (b[12 + i * 2] | (b[13 + i * 2] << 8));
            var f = new byte[n * n];
            System.Buffer.BlockCopy(b, 12 + n * n * 2, f, 0, n * n);
            hmap = h; fmap = f; hmN = n;
        }

        static float SampleMap(float x, float z, bool flow)
        {
            float half = Size * 0.5f, s = (hmN - 1) / Size;
            float fx = Mathf.Clamp((x + half) * s, 0f, hmN - 1.001f), fz = Mathf.Clamp((z + half) * s, 0f, hmN - 1.001f);
            int ix = (int)fx, iz = (int)fz; float tx = fx - ix, tz = fz - iz;
            int i0 = iz * hmN + ix, i1 = i0 + hmN;
            if (flow)
                return Mathf.Lerp(Mathf.Lerp(fmap[i0], fmap[i0 + 1], tx), Mathf.Lerp(fmap[i1], fmap[i1 + 1], tx), tz) / 255f;
            return Mathf.Lerp(Mathf.Lerp(hmap[i0], hmap[i0 + 1], tx), Mathf.Lerp(hmap[i1], hmap[i1 + 1], tx), tz);
        }

        /// <summary>0..1 drainage (rivers / valley floors ≈ 1).</summary>
        public static float Flow(float x, float z) => fmap != null ? SampleMap(x, z, true) : 0f;

        static float RawHeight(float x, float z)
        {
            if (hmap != null) return SampleMap(x, z, false);
            float n = 0f, amp = 1f, freq = 1f / 7000f, sum = 0f;
            for (int i = 0; i < 5; i++)
            {
                n += amp * Mathf.PerlinNoise(x * freq + 100.3f, z * freq + 57.1f);
                sum += amp; amp *= 0.5f; freq *= 2.1f;
            }
            n /= sum;
            float ridge = 1f - Mathf.Abs(Mathf.PerlinNoise(x / 4200f + 10.7f, z / 4200f + 20.2f) * 2f - 1f);
            ridge = ridge * ridge * ridge;
            float h = (n - 0.42f) * 2600f + ridge * 1100f * n;
            float d = Mathf.Sqrt(x * x + z * z) / (Size * 0.5f);
            h -= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 0.98f, d)) * 2800f;
            return h;
        }

        public static float Height(float x, float z)
        {
            float h = RawHeight(x, z);
            // flatten every air base (rotated rectangle + smooth blend)
            foreach (var ap in Airports)
            {
                float dx = x - ap.center.x, dz = z - ap.center.y;
                float r = ap.heading * Mathf.Deg2Rad;
                float along = dx * Mathf.Sin(r) + dz * Mathf.Cos(r);
                float across = dx * Mathf.Cos(r) - dz * Mathf.Sin(r);
                float fx = Mathf.Max(0f, Mathf.Abs(across) - 500f);
                float fz = Mathf.Max(0f, Mathf.Abs(along) - RunwayLength * 0.5f - 700f);
                float dist = Mathf.Sqrt(fx * fx + fz * fz);
                if (dist <= 1600f)
                {
                    float blend = Mathf.SmoothStep(0f, 1f, dist / 1600f);
                    h = Mathf.Lerp(ap.elevation, h, blend);
                }
                // approach corridors off both runway ends: hills are cut below a 2.2° slope (the 3° glidepath stays clear)
                float beyond = Mathf.Abs(along) - RunwayLength * 0.5f;
                float acrossAbs = Mathf.Abs(across);
                if (beyond > 0f && beyond < 10000f && acrossAbs < 1100f)
                {
                    float cap = ap.elevation + beyond * 0.038f;
                    float w = (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(600f, 1100f, acrossAbs))) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(8500f, 10000f, beyond)));
                    if (h > cap) h = Mathf.Lerp(h, cap, w);
                }
            }
            return h;
        }

        public static void Create(Transform parent)
        {
            BuildHeightGrid(TexRes);
            BuildLighting(parent);
            BuildTerrain(parent);
            BuildSea(parent);
            BuildClouds(parent);
            foreach (var ap in Airports) BuildAirbase(parent, ap);
            var forest = new GameObject("Forests").AddComponent<ForestRenderer>();
            forest.transform.SetParent(parent, false);
            forest.Build(Application.isMobilePlatform ? 25000 : 60000);
        }

        static Material edgeLight, thresholdLight;
        static Material EdgeLightMat => edgeLight != null ? edgeLight : (edgeLight = Fx.Glow(new Color(1f, 0.9f, 0.65f), 3f));
        static Material ThresholdLightMat => thresholdLight != null ? thresholdLight : (thresholdLight = Fx.Glow(new Color(0.3f, 1f, 0.45f), 3f));

        static void BuildAirbase(Transform parent, Airport ap)
        {
            var root = Build.Node(ap.name, parent, ap.Center3, ap.Rotation);
            Color asphalt = new Color(0.2f, 0.2f, 0.21f), paint = new Color(0.95f, 0.95f, 0.95f), yellow = new Color(0.95f, 0.8f, 0.2f);
            float RunwayLength = ap.length, RunwayWidth = ap.width;
            // runway slab with a real collider (top surface 0.15 m above the flattened ground)
            var rwy = Build.Prim(PrimitiveType.Cube, root, new Vector3(0, -0.35f, 0), new Vector3(RunwayWidth, 1f, RunwayLength), asphalt, false, null, true, "Runway");
            var asphaltTex = Realism.Tex("asphalt");
            if (asphaltTex != null)
            {
                // real asphalt grain + tyre marks (texture authored around 0.5 → base colour doubled)
                var m = Fx.Pbr(asphalt * 2f, 0f, 0.25f);
                m.SetTexture("_BaseMap", asphaltTex);
                m.SetTextureScale("_BaseMap", new Vector2(1f, RunwayLength / RunwayWidth));
                rwy.GetComponent<Renderer>().sharedMaterial = m;
            }
            float top = 0.16f, half = RunwayLength * 0.5f;
            // centreline dashes
            for (float z = -half + 120f; z < half - 120f; z += 60f)
                Build.Prim(PrimitiveType.Cube, root, new Vector3(0, top, z), new Vector3(0.9f, 0.02f, 30f), paint);
            // edge lines
            Build.Prim(PrimitiveType.Cube, root, new Vector3(RunwayWidth * 0.5f - 1f, top, 0), new Vector3(0.9f, 0.02f, RunwayLength), paint);
            Build.Prim(PrimitiveType.Cube, root, new Vector3(-RunwayWidth * 0.5f + 1f, top, 0), new Vector3(0.9f, 0.02f, RunwayLength), paint);
            // threshold "piano keys" both ends + aiming points
            foreach (float end in new[] { -1f, 1f })
            {
                for (int i = 0; i < 8; i++)
                {
                    float x = (i - 3.5f) * 4.6f + (i >= 4 ? 3f : -3f);
                    Build.Prim(PrimitiveType.Cube, root, new Vector3(x, top, end * (half - 30f)), new Vector3(1.8f, 0.02f, 30f), paint);
                }
                Build.Prim(PrimitiveType.Cube, root, new Vector3(-9f, top, end * (half - 400f)), new Vector3(9f, 0.02f, 45f), paint);
                Build.Prim(PrimitiveType.Cube, root, new Vector3(9f, top, end * (half - 400f)), new Vector3(9f, 0.02f, 45f), paint);
            }
            // parallel taxiway + apron + hangars + tower
            Build.Prim(PrimitiveType.Cube, root, new Vector3(-180f, -0.4f, 0), new Vector3(23f, 1f, RunwayLength - 200f), asphalt, false, null, true, "Taxiway");
            Build.Prim(PrimitiveType.Cube, root, new Vector3(-180f, 0.11f, 0), new Vector3(0.4f, 0.02f, RunwayLength - 200f), yellow);
            foreach (float z in new[] { -half + 100f, 0f, half - 100f })
                Build.Prim(PrimitiveType.Cube, root, new Vector3(-95f, -0.4f, z), new Vector3(150f, 1f, 23f), asphalt, false, null, true, "Link");
            Build.Prim(PrimitiveType.Cube, root, new Vector3(-300f, -0.4f, -200f), new Vector3(200f, 1f, 500f), new Color(0.45f, 0.45f, 0.43f), false, null, true, "Apron");
            for (int i = 0; i < 4; i++)
            {
                var hg = Build.Node("Hangar", root, new Vector3(-430f, 0f, -400f + i * 110f));
                Build.Prim(PrimitiveType.Cube, hg, new Vector3(0, 7f, 0), new Vector3(50f, 14f, 60f), new Color(0.5f, 0.52f, 0.48f), false, null, true);
                Build.Prim(PrimitiveType.Cylinder, hg, new Vector3(0, 14f, 0), new Vector3(50f, 30f, 12f), new Color(0.45f, 0.47f, 0.43f), false, Quaternion.Euler(90, 0, 0));
            }
            var tower = Build.Node("Tower", root, new Vector3(-300f, 0f, 150f));
            Build.Prim(PrimitiveType.Cube, tower, new Vector3(0, 15f, 0), new Vector3(8f, 30f, 8f), new Color(0.75f, 0.75f, 0.72f), false, null, true);
            Build.Prim(PrimitiveType.Cube, tower, new Vector3(0, 32f, 0), new Vector3(12f, 4f, 12f), new Color(0.2f, 0.3f, 0.35f));
            BuildRunwayLights(root, RunwayLength, RunwayWidth);
        }

        static void BuildRunwayLights(Transform root, float RunwayLength, float RunwayWidth)
        {
            float half = RunwayLength * 0.5f;
            // runway edge lights
            for (float z = -half; z <= half; z += 60f)
                foreach (float sx in new[] { -1f, 1f })
                {
                    var el = Build.Prim(PrimitiveType.Sphere, root, new Vector3(sx * (RunwayWidth * 0.5f + 1.5f), 0.4f, z), Vector3.one * 0.5f, new Color(1f, 0.95f, 0.7f), true);
                    el.GetComponent<Renderer>().sharedMaterial = EdgeLightMat;
                    TimeOfDay.RegisterRunwayLight(el.transform);
                }
            // green threshold bars + PAPI (precision approach path indicator, 3° glide path) for both directions
            foreach (float end in new[] { -1f, 1f })
            {
                for (int i = -5; i <= 5; i++)
                {
                    var tl = Build.Prim(PrimitiveType.Sphere, root, new Vector3(i * 4.5f, 0.5f, end * (half + 3f)), Vector3.one * 1.2f, new Color(0.3f, 1f, 0.4f), true);
                    tl.GetComponent<Renderer>().sharedMaterial = ThresholdLightMat;
                    TimeOfDay.RegisterRunwayLight(tl.transform);
                }
                var papi = Build.Node("PAPI", root, Vector3.zero);
                var pl = papi.gameObject.AddComponent<PapiLights>();
                float side = end < 0 ? -1f : 1f;   // left of the pilot landing toward the far end
                for (int k = 0; k < 4; k++)
                {
                    var l = Build.Prim(PrimitiveType.Sphere, papi, new Vector3(side * (RunwayWidth * 0.5f + 15f + k * 10f), 1.2f, end * (half - 300f)), Vector3.one * 3f, Color.red, true);
                    pl.Add(l.GetComponent<Renderer>());
                }
            }
        }

        static void BuildLighting(Transform parent)
        {
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(parent, false);
            sunGo.transform.rotation = Quaternion.Euler(34f, -35f, 0f);   // mid-morning sun: long, readable mountain shadows
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.35f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            TimeOfDay.SetupSun(sun);                       // day / dusk / night (moon) — chosen in the menu

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.5f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.25f, 0.22f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.68f, 0.77f, 0.88f);
            RenderSettings.fogStartDistance = 7000f;
            RenderSettings.fogEndDistance = 42000f;
        }

        static void BuildTerrain(Transform parent)
        {
            var terrainShader = Realism.Find("AirFight/Terrain");
            var tex = BuildTerrainTexture(hres, terrainShader != null ? 0.2f : 1f);
            Material mat;
            if (terrainShader != null)
            {
                // photo-real terrain: CC0 aerial photographs (Poly Haven) per layer, island colour map, normal map, haze
                mat = new Material(terrainShader);
                string[] layers = { "Grass", "Dry", "Dirt", "Rock", "Sand", "Snow", "Ground" };
                string[] files = { "grass", "dry", "dirt", "rock", "sand", "snow", "ground" };
                for (int i = 0; i < layers.Length; i++)
                {
                    var t = Realism.Tex("Photo/" + files[i]);
                    var n = Realism.Tex("Photo/" + files[i] + "_n");
                    if (t != null) mat.SetTexture("_" + layers[i] + "Tex", t);
                    if (n != null) mat.SetTexture("_" + layers[i] + "N", n);
                }
                // linear-space mean colour of each photo (the colour map supplies the hue at a distance)
                mat.SetVector("_Mean0", new Vector4(0.0837f, 0.0785f, 0.0141f, 1f));
                mat.SetVector("_Mean1", new Vector4(0.1717f, 0.1240f, 0.0296f, 1f));
                mat.SetVector("_Mean2", new Vector4(0.2080f, 0.1187f, 0.0562f, 1f));
                mat.SetVector("_Mean3", new Vector4(0.1747f, 0.1237f, 0.0529f, 1f));
                mat.SetVector("_Mean4", new Vector4(0.2829f, 0.2372f, 0.1959f, 1f));
                mat.SetVector("_Mean5", new Vector4(0.2624f, 0.2632f, 0.3222f, 1f));
                mat.SetVector("_Mean6", new Vector4(0.2948f, 0.2510f, 0.1225f, 1f));
                // real-world size of each photo (m): grass 90, dry meadow 30, fields 40, rock 50 | sand 30, snow 80, close ground 2.5
                mat.SetVector("_Scale0", new Vector4(90f, 30f, 40f, 50f));
                mat.SetVector("_Scale1", new Vector4(30f, 80f, 2.5f, 0f));
                var noise = Realism.Tex("rock");
                if (noise != null) mat.SetTexture("_Noise", noise);
                if (Splat0 != null) { mat.SetTexture("_Splat0", Splat0); mat.SetTexture("_Splat1", Splat1); mat.SetFloat("_UseSplat", 1f); }
                if (MacroNormal != null) { mat.SetTexture("_MacroNormal", MacroNormal); mat.SetFloat("_UseMacroNormal", 1f); }
                float d = Size / hres;
                mat.SetVector("_TerrainST", new Vector4(1f / Size, 1f / Size, 0.5f + 0.5f / hres, 0.5f + 0.5f / hres));
            }
            else mat = Mats.Unique(Color.white);
            MapTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.mainTexture = tex;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);

            // baked mountain shadows + valley occlusion, shared with trees and the sea (global textures)
            RefreshTerrainLight();
            Shader.SetGlobalVector("_AF_TerrainST", new Vector4(1f / Size, 1f / Size, 0.5f, 0.5f));
            Shader.SetGlobalTexture("_AF_TerrainHeight", BuildHeightTexture(Application.isMobilePlatform ? 512 : 1024));

            var go = new GameObject("Terrain");
            go.transform.SetParent(parent, false);
            go.AddComponent<TerrainLod>().Build(hgrid, hres, Size, mat);
        }

        /// <summary>
        /// Light the island like the real sun would: R = direct sunlight (0 where a ridge stands between the ground and the
        /// sun — long mountain shadows), G = sky visibility (deep valleys and gullies are darker). Ray-marched over the
        /// height grid once at start-up, in parallel.
        /// </summary>
        static Texture2D BakeTerrainLight(int res)
        {
            Vector3 sun = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : new Vector3(0.4f, 0.7f, 0.5f).normalized;
            Vector2 sd = new Vector2(sun.x, sun.z);
            float sunTan = sun.y / Mathf.Max(sd.magnitude, 1e-3f);
            sd = sd.sqrMagnitude > 1e-6f ? sd.normalized : Vector2.up;
            var dirs = new Vector2[8];
            for (int k = 0; k < 8; k++) dirs[k] = new Vector2(Mathf.Cos(k * Mathf.PI / 4f + 0.39f), Mathf.Sin(k * Mathf.PI / 4f + 0.39f));
            float[] aoDist = { 35f, 80f, 160f, 320f, 640f };
            var px = new Color32[res * res];
            float half = Size * 0.5f, d = Size / res;
            System.Action<int> row = z =>
            {
                for (int x = 0; x < res; x++)
                {
                    float wx = (x + 0.5f) * d - half, wz = (z + 0.5f) * d - half;
                    float h0 = GridHeight(wx, wz) + 1.5f;
                    // sun: highest horizon angle toward the sun out to 9 km
                    float horizon = -10f;
                    for (float t = 30f; t < 9000f; t *= 1.13f)
                    {
                        float hs = GridHeight(wx + sd.x * t, wz + sd.y * t);
                        horizon = Mathf.Max(horizon, (hs - h0) / t);
                        if (horizon > sunTan + 0.2f) break;
                    }
                    float vis = Mathf.Clamp01((sunTan - horizon) / 0.07f + 0.5f);   // soft penumbra
                    // sky: average horizon elevation in 8 directions
                    float occ = 0f;
                    foreach (var dir in dirs)
                    {
                        float m = 0f;
                        foreach (float t in aoDist) m = Mathf.Max(m, (GridHeight(wx + dir.x * t, wz + dir.y * t) - h0) / t);
                        occ += m / Mathf.Sqrt(1f + m * m);     // sin(horizon angle)
                    }
                    float ao = Mathf.Clamp01(1f - occ / 8f * 1.3f);
                    px[z * res + x] = new Color32((byte)(vis * 255f), (byte)(ao * 255f), 0, 255);
                }
            };
            try { System.Threading.Tasks.Parallel.For(0, res, row); }
            catch (System.Exception) { for (int z = 0; z < res; z++) row(z); }
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true) { name = "TerrainLight", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D terrainLight;
        static void RefreshTerrainLight()
        {
            if (terrainLight != null) Object.Destroy(terrainLight);
            terrainLight = BakeTerrainLight(Application.isMobilePlatform ? 512 : 1024);
            Shader.SetGlobalTexture("_AF_TerrainLight", terrainLight);
        }

        /// <summary>The sun/moon moved (time of day): re-bake mountain shadows and cloud shadows.</summary>
        public static void RefreshLighting()
        {
            if (hgrid == null) return;
            RefreshTerrainLight();
            if (VolumetricClouds.Supported) BuildVolumetricShadowMap();
        }

        /// <summary>Ground height (m, half float) for the sea shader: water depth → turquoise shallows and surf.</summary>
        static Texture2D BuildHeightTexture(int res)
        {
            var tex = new Texture2D(res, res, TextureFormat.RHalf, false, true) { name = "TerrainHeight", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var data = new ushort[res * res];
            float half = Size * 0.5f, d = Size / res;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    data[z * res + x] = Mathf.FloatToHalf(GridHeight((x + 0.5f) * d - half, (z + 0.5f) * d - half));
            tex.SetPixelData(data, 0);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>Terrain normal map (world normal x/z in RG) from the height grid — crisp ridges & gullies on a coarse mesh.</summary>
        public static Texture2D MacroNormal { get; private set; }
        /// <summary>Photo-layer weights for the terrain shader.</summary>
        static Texture2D Splat0, Splat1;

        static Texture2D MakeLinear(string name, int res, Color32[] px)
        {
            var t = new Texture2D(res, res, TextureFormat.RGBA32, true, true) { name = name, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            t.SetPixels32(px);
            t.Apply(true, true);    // no CPU copy needed
            return t;
        }

        /// <param name="shadeAmount">1 = full baked hill-shading (plain lit material); less when the terrain shader lights the relief itself.</param>
        static Texture2D BuildTerrainTexture(int res, float shadeAmount = 1f)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true);
            var px = new Color32[res * res];
            var npx = new Color32[res * res];
            var sp0 = new Color32[res * res];   // photo layer weights: grass, dry meadow, dirt/fields, rock
            var sp1 = new Color32[res * res];   //                       sand, snow
            float half = Size * 0.5f, d = Size / res;
            int gn = hres + 1;
            Vector3 sunDir = new Vector3(-0.45f, 0.75f, -0.5f).normalized;
            // satellite-photo palette (sRGB)
            Color beach = new Color(0.74f, 0.69f, 0.54f), grass = new Color(0.35f, 0.42f, 0.20f), grassDry = new Color(0.47f, 0.47f, 0.27f);
            Color forest = new Color(0.15f, 0.23f, 0.10f), lush = new Color(0.24f, 0.36f, 0.14f), river = new Color(0.17f, 0.22f, 0.20f);
            Color meadow = new Color(0.43f, 0.43f, 0.31f), rock = new Color(0.44f, 0.41f, 0.37f), rockDark = new Color(0.31f, 0.29f, 0.27f);
            Color snow = new Color(0.93f, 0.94f, 0.97f);
            Color[] fields = { new Color(0.53f, 0.50f, 0.28f), new Color(0.37f, 0.46f, 0.20f), new Color(0.46f, 0.38f, 0.25f), new Color(0.30f, 0.40f, 0.17f), new Color(0.58f, 0.55f, 0.33f) };
            // Mathf.PerlinNoise is a thread-safe native function, so rows are coloured in parallel.
            System.Action<int> row = y =>
            {
                for (int x = 0; x < res; x++)
                {
                    float wx = x * d - half, wz = y * d - half;
                    int i = y * gn + x;
                    float h = hgrid[i];
                    float hL = hgrid[y * gn + Mathf.Max(0, x - 1)], hR = hgrid[i + 1];
                    float hD = hgrid[Mathf.Max(0, y - 1) * gn + x], hU = hgrid[i + gn];
                    Vector3 nrm = new Vector3(-(hR - hL) / (2f * d), 1f, -(hU - hD) / (2f * d)).normalized;
                    float slope = 1f - nrm.y;                                  // 0 flat … ~0.3 at 45°
                    float flow = Flow(wx, wz);
                    float n1 = Mathf.PerlinNoise(wx / 260f, wz / 260f), n2 = Mathf.PerlinNoise(wx / 1500f + 11f, wz / 1500f + 3f);
                    float n3 = Mathf.PerlinNoise(wx / 90f + 5f, wz / 90f + 2f);
                    Color c;
                    // layer weights follow every colour blend: wG grass, wD dry, wF dirt/fields, wR rock, wS sand, wN snow
                    float wG = 0f, wD = 0f, wF = 0f, wR = 0f, wS = 0f, wN = 0f;
                    void W(int layer, float t)
                    {
                        t = Mathf.Clamp01(t);
                        wG *= 1f - t; wD *= 1f - t; wF *= 1f - t; wR *= 1f - t; wS *= 1f - t; wN *= 1f - t;
                        switch (layer) { case 0: wG += t; break; case 1: wD += t; break; case 2: wF += t; break; case 3: wR += t; break; case 4: wS += t; break; default: wN += t; break; }
                    }
                    if (h < 0f) { c = Color.Lerp(beach, new Color(0.24f, 0.31f, 0.30f), Mathf.Clamp01(-h / 60f)); wS = 1f; }
                    else if (h < 4f) { c = beach; wS = 1f; }
                    else
                    {
                        // lowland grass with dry / green variation
                        float dryT = Mathf.Clamp01(n2 * 1.4f - 0.35f);
                        c = Color.Lerp(grass, grassDry, dryT);
                        wG = 1f; W(1, dryT * 0.8f);
                        // farmland: rectangular fields on flat, low ground
                        if (h < 260f && slope < 0.012f && Mathf.PerlinNoise(wx / 2200f + 7f, wz / 2200f + 1f) > 0.42f)
                        {
                            int fxI = Mathf.FloorToInt((wx * 0.94f + wz * 0.34f) / 260f), fzI = Mathf.FloorToInt((wz * 0.94f - wx * 0.34f) / 170f);
                            int hsh = (fxI * 73856093) ^ (fzI * 19349663);
                            int fi = (hsh & 0x7fffffff) % fields.Length;
                            var fc = fields[fi];
                            float edge = Mathf.Min(Mathf.Repeat((wx * 0.94f + wz * 0.34f) / 260f, 1f), Mathf.Repeat((wz * 0.94f - wx * 0.34f) / 170f, 1f));
                            float ft = edge > 0.04f ? 0.85f : 0.3f;
                            c = Color.Lerp(c, fc, ft);   // hedgerow lines between fields
                            W(fi == 2 ? 2 : fi == 0 || fi == 4 ? 1 : 0, ft);   // ploughed soil / stubble / green crops
                        }
                        // forest on hillsides and in valleys
                        float forestAmt = Mathf.Clamp01((Mathf.PerlinNoise(wx / 1800f + 3.3f, wz / 1800f + 9.1f) + n1 * 0.35f - 0.62f) * 4f);
                        forestAmt = Mathf.Max(forestAmt, Mathf.Clamp01((flow - 0.55f) * 3f) * 0.8f);
                        forestAmt *= Mathf.Clamp01((h - 30f) / 60f) * (1f - Mathf.Clamp01((h - 1150f) / 200f)) * (1f - Mathf.Clamp01((slope - 0.22f) * 6f));
                        c = Color.Lerp(c, forest * (0.85f + 0.3f * n3), forestAmt);
                        W(0, forestAmt);
                        // green valley floors and dark river channels
                        c = Color.Lerp(c, lush, Mathf.Clamp01((flow - 0.62f) * 4f) * 0.5f);
                        if (h > 2f) c = Color.Lerp(c, river, Mathf.Clamp01((flow - 0.86f) * 12f));
                        // alpine meadow → bare rock with height and steepness (darker strata bands)
                        c = Color.Lerp(c, meadow, Mathf.Clamp01((h - 1050f) / 250f));
                        W(1, (h - 1050f) / 250f);
                        float rockAmt = Mathf.Clamp01((slope - 0.12f) * 6f) * Mathf.Clamp01((h - 20f) / 80f) + Mathf.Clamp01((h - 1350f) / 200f);
                        Color rk = Color.Lerp(rock, rockDark, Mathf.Clamp01(Mathf.Sin(h / 37f + n1 * 3f) * 0.5f + 0.5f) * 0.6f);
                        c = Color.Lerp(c, rk * (0.9f + 0.2f * n3), Mathf.Clamp01(rockAmt));
                        W(3, rockAmt);
                        // snow: lower on north-facing slopes, thin on steep faces
                        float snowline = 1560f - Mathf.Clamp01(nrm.z * 3f) * 160f + (n2 - 0.5f) * 140f;
                        float snowAmt = Mathf.Clamp01((h - snowline) / 90f) * (1f - Mathf.Clamp01((slope - 0.3f) * 3f));
                        c = Color.Lerp(c, snow, snowAmt);
                        W(5, snowAmt);
                    }
                    // subtle baked hill-shading (the shader lights the relief with the normal map)
                    float shade = Mathf.Clamp01(Vector3.Dot(nrm, sunDir));
                    c *= Mathf.Lerp(1f, Mathf.Lerp(0.72f, 1.08f, shade), shadeAmount);
                    c.a = 1f;
                    px[y * res + x] = c;
                    sp0[y * res + x] = new Color32((byte)(wG * 255f), (byte)(wD * 255f), (byte)(wF * 255f), (byte)(wR * 255f));
                    sp1[y * res + x] = new Color32((byte)(wS * 255f), (byte)(wN * 255f), 0, 0);
                    npx[y * res + x] = new Color32((byte)Mathf.RoundToInt((nrm.x * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt((nrm.z * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt(nrm.y * 255f), 255);
                }
            };
            try { System.Threading.Tasks.Parallel.For(0, res, row); }
            catch (System.Exception) { for (int y = 0; y < res; y++) row(y); }
            tex.SetPixels32(px);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 4;
            var ntex = new Texture2D(res, res, TextureFormat.RGBA32, true, true) { name = "TerrainNormal", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            ntex.SetPixels32(npx);
            ntex.Apply(true);
            MacroNormal = ntex;
            Splat0 = MakeLinear("TerrainSplat0", res, sp0);
            Splat1 = MakeLinear("TerrainSplat1", res, sp1);
            return tex;
        }

        static void BuildSea(Transform parent)
        {
            var sea = Build.Prim(PrimitiveType.Plane, parent, new Vector3(0, -2f, 0), new Vector3(30000f, 1f, 30000f), new Color(0.12f, 0.28f, 0.42f), false, null, true, "Sea");
            var oceanShader = Realism.Find("AirFight/Ocean");
            var m = oceanShader != null ? new Material(oceanShader)                   // animated waves, sky reflection, sun glitter
                                        : Fx.Pbr(new Color(0.04f, 0.13f, 0.22f), 0f, 0.93f);
            var r = sea.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        static void BuildClouds(Transform parent)
        {
            if (VolumetricClouds.Supported)
            {
                // PC: real volumetric cumulus (ray-marched), shadows on the ground from the same weather map
                var vc = new GameObject("VolumetricClouds").AddComponent<VolumetricClouds>();
                vc.transform.SetParent(parent, false);
                vc.Build();
                BuildVolumetricShadowMap();
                return;
            }
            // Soft cumulus made of camera-facing, alpha-blended puffs (procedural texture, no assets)
            var root = Build.Node("Clouds", parent, Vector3.zero);
            var rng = new System.Random(1234);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var tex = Fx.PuffTexture();
            Material[] mats;
            var cloudShader = Realism.Find("AirFight/Cloud");
            if (cloudShader != null)
            {
                // sun-lit puffs: bright tops, grey flat bases (3 shades by height inside the cloud)
                var puff = Realism.Tex("cloudpuff");
                mats = new Material[3];
                for (int k = 0; k < 3; k++)
                {
                    mats[k] = new Material(cloudShader);
                    mats[k].SetTexture("_BaseMap", puff != null ? (Texture)puff : tex);
                    mats[k].SetFloat("_Shade", k * 0.5f);
                    mats[k].SetFloat("_Opacity", 0.93f);
                    mats[k].renderQueue = 3000 + k;
                }
            }
            else mats = new[] { Fx.Transparent(new Color(1f, 1f, 1f, 0.95f), tex), Fx.Transparent(new Color(0.93f, 0.94f, 0.97f, 0.9f), tex) };
            int count = Application.isMobilePlatform ? 45 : 70;
            float spread = Size * 0.46f;
            var shadowDiscs = new System.Collections.Generic.List<Vector4>();   // ground-projected centre (x,z), radius, density
            Vector3 sunDir = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : new Vector3(0.4f, 0.7f, 0.5f).normalized;
            for (int i = 0; i < count; i++)
            {
                var c = Build.Node("Cloud", root, new Vector3(R(-spread, spread), R(2600, 5200), R(-spread, spread)));
                int puffs = rng.Next(6, 12);
                float w = R(500, 1300);
                Vector3 ground = c.position - sunDir * (c.position.y / Mathf.Max(0.2f, sunDir.y));     // where its shadow lands
                shadowDiscs.Add(new Vector4(ground.x, ground.z, w * 1.05f, Mathf.Clamp01(0.35f + puffs * 0.03f)));
                for (int p = 0; p < puffs; p++)
                {
                    float s = R(0.45f, 1f) * w;
                    var q = Build.Prim(PrimitiveType.Quad, c, new Vector3(R(-w, w), R(-w * 0.15f, w * 0.35f), R(-w * 0.6f, w * 0.6f)), new Vector3(s * 1.3f, s, 1f), Color.white, true);
                    var r = q.GetComponent<Renderer>();
                    if (mats.Length == 3)
                    {
                        float rel = Mathf.InverseLerp(-w * 0.15f, w * 0.35f, q.transform.localPosition.y);   // 0 = bottom of the cloud
                        r.sharedMaterial = mats[rel > 0.6f ? 0 : rel > 0.25f ? 1 : 2];
                    }
                    else r.sharedMaterial = mats[p % 2];
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }
                c.gameObject.AddComponent<Billboards>();
            }
            BuildCloudShadowMap(shadowDiscs);
        }

        /// <summary>Soft cloud shadows on the ground and sea: a 512² map of the projected cloud field (sampled by the terrain/ocean shaders).</summary>
        /// <summary>Ground shadow of the volumetric cloud field: project each ground point toward the sun into the layer.</summary>
        static void BuildVolumetricShadowMap()
        {
            const int res = 512;
            float cell = Size / res, half = Size * 0.5f;
            Vector3 sun = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : new Vector3(0.4f, 0.7f, 0.5f).normalized;
            float mid = (VolumetricClouds.Bottom + VolumetricClouds.Top) * 0.45f;
            var px = new Color32[res * res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    float wx = x * cell - half, wz = z * cell - half;
                    float g = Mathf.Max(0f, GridHeight(wx, wz));
                    float k = (mid - g) / Mathf.Max(sun.y, 0.15f);
                    float c = VolumetricClouds.CoverageAt(wx + sun.x * k, wz + sun.z * k);
                    float shade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 0.55f, c));
                    byte v = (byte)Mathf.RoundToInt(255f * (1f - 0.62f * shade));
                    px[z * res + x] = new Color32(v, v, v, 255);
                }
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "CloudShadow" };
            tex.SetPixels32(px);
            tex.Apply(true);
            Shader.SetGlobalTexture("_AF_CloudShadow", tex);
            Shader.SetGlobalVector("_AF_CloudShadowST", new Vector4(1f / Size, 1f / Size, 0.5f, 0.5f));
        }

        static void BuildCloudShadowMap(System.Collections.Generic.List<Vector4> discs)
        {
            const int res = 512;
            float cell = Size / res, half = Size * 0.5f;
            var px = new Color32[res * res];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            var dens = new float[res * res];
            foreach (var d in discs)
            {
                int cx = Mathf.RoundToInt((d.x + half) / cell), cz = Mathf.RoundToInt((d.y + half) / cell);
                int r = Mathf.CeilToInt(d.z / cell) + 1;
                for (int z = Mathf.Max(0, cz - r); z <= Mathf.Min(res - 1, cz + r); z++)
                    for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(res - 1, cx + r); x++)
                    {
                        float wx = x * cell - half, wz = z * cell - half;
                        float dist = new Vector2(wx - d.x, (wz - d.y) * 1.25f).magnitude / d.z;
                        float lump = Mathf.PerlinNoise(wx / 700f + 3f, wz / 700f + 7f) * 0.5f;
                        float a = Mathf.Clamp01((1.05f - dist - lump * 0.35f) * 2.2f) * d.w;
                        dens[z * res + x] = Mathf.Max(dens[z * res + x], a);
                    }
            }
            for (int i = 0; i < px.Length; i++) { byte v = (byte)Mathf.RoundToInt(255f * (1f - 0.55f * dens[i])); px[i] = new Color32(v, v, v, 255); }
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "CloudShadow" };
            tex.SetPixels32(px);
            tex.Apply(true);
            Shader.SetGlobalTexture("_AF_CloudShadow", tex);
            Shader.SetGlobalVector("_AF_CloudShadowST", new Vector4(1f / Size, 1f / Size, 0.5f, 0.5f));
        }
    }

    /// <summary>PAPI: 4 lights, white above / red below their angle. On the 3° glide path → 2 white + 2 red.</summary>
    public class PapiLights : MonoBehaviour
    {
        readonly System.Collections.Generic.List<Renderer> lights = new System.Collections.Generic.List<Renderer>();
        static readonly float[] Angles = { 3.5f, 3.17f, 2.83f, 2.5f };   // nearest the runway → farthest
        Material red, white;

        public void Add(Renderer r) { lights.Add(r); }

        void Start()
        {
            red = Fx.Glow(new Color(1f, 0.15f, 0.1f), 4f);
            white = Fx.Glow(new Color(1f, 1f, 0.95f), 4f);
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null || lights.Count == 0) return;
            Vector3 c = cam.transform.position;
            for (int i = 0; i < lights.Count; i++)
            {
                Vector3 d = c - lights[i].transform.position;
                float dist = d.magnitude;
                float ang = dist > 1f ? Mathf.Asin(Mathf.Clamp(d.y / dist, -1f, 1f)) * Mathf.Rad2Deg : 0f;
                lights[i].sharedMaterial = ang > Angles[Mathf.Min(i, 3)] ? white : red;
                // keep them visible from a long final (grow with distance)
                lights[i].transform.localScale = Vector3.one * Mathf.Clamp(dist * 0.0015f, 3f, 9f);
            }
        }
    }
}
