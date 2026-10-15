using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AirFight
{
    /// <summary>
    /// 지형을 16×16 청크로 나눠 카메라 거리에 따라 해상도를 바꾼다 (가까이 23 m 격자 → 멀리 190 m).
    /// 청크 경계의 틈은 아래로 내려간 "스커트"로 가린다. 충돌체는 47 m 격자(미리 병렬 베이크).
    /// </summary>
    public class TerrainLod : MonoBehaviour
    {
        const int Chunks = 16;
        static readonly int[] Steps = { 1, 2, 4, 8 };

        class Chunk
        {
            public int cx, cz;
            public MeshFilter mf;
            public MeshRenderer mr;
            public Mesh[] lods = new Mesh[4];
            public int lod = -1;
            public Bounds bounds;
            public float lastFine;
        }

        readonly List<Chunk> chunks = new List<Chunk>();
        float[] h;
        int res, gn, cells;
        float size, half, cell;
        float[] lodDist;
        int finePerFrame;

        public void Build(float[] heights, int hres, float worldSize, Material mat)
        {
            h = heights; res = hres; gn = hres + 1; size = worldSize; half = size * 0.5f; cell = size / hres;
            cells = res / Chunks;
            bool mobile = Application.isMobilePlatform;
            // switch distances (m, from the camera to the chunk box)
            lodDist = mobile ? new[] { 1500f, 4000f, 10000f } : new[] { 3000f, 7000f, 16000f };

            int colStep = Mathf.Max(1, Mathf.RoundToInt(47f / cell));
            var colMeshes = new Mesh[Chunks * Chunks];
            for (int cz = 0; cz < Chunks; cz++)
                for (int cx = 0; cx < Chunks; cx++)
                {
                    var go = new GameObject($"T{cx}_{cz}");
                    go.transform.SetParent(transform, false);
                    var c = new Chunk { cx = cx, cz = cz, mf = go.AddComponent<MeshFilter>(), mr = go.AddComponent<MeshRenderer>() };
                    c.mr.sharedMaterial = mat;
                    c.mr.shadowCastingMode = ShadowCastingMode.Off;
                    c.mr.lightProbeUsage = LightProbeUsage.Off;
                    c.mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    // coarse levels are small: build them all now; the finest level is built on demand near the camera
                    for (int l = 1; l < Steps.Length; l++) c.lods[l] = MakeMesh(cx, cz, Steps[l], true);
                    c.bounds = c.lods[Steps.Length - 1].bounds;
                    c.bounds.Expand(new Vector3(0f, 60f, 0f));
                    SetLod(c, Steps.Length - 1);
                    chunks.Add(c);
                    colMeshes[cz * Chunks + cx] = MakeMesh(cx, cz, colStep, false);
                }

            // collision meshes: cook in parallel (PhysX baking is thread-safe), then attach without re-cooking
            var ids = new EntityId[colMeshes.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = colMeshes[i].GetEntityId();
            try { System.Threading.Tasks.Parallel.For(0, ids.Length, i => Physics.BakeMesh(ids[i], false)); }
            catch (System.Exception) { }
            for (int i = 0; i < chunks.Count; i++)
            {
                // own child object: AddComponent<MeshCollider> next to a MeshFilter would first cook the (coarse, skirted)
                // render mesh — slow, and PhysX warns about its big triangles
                var cgo = new GameObject("Collider");
                cgo.transform.SetParent(chunks[i].mf.transform, false);
                var mc = cgo.AddComponent<MeshCollider>();
                mc.sharedMesh = colMeshes[i];
            }
        }

        Mesh MakeMesh(int cx, int cz, int step, bool skirt)
        {
            int k = cells / step, n = k + 1;
            int x0 = cx * cells, z0 = cz * cells;
            int edgeCount = skirt ? 4 * n : 0;
            var v = new Vector3[n * n + edgeCount];
            var nr = new Vector3[v.Length];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int gx = x0 + i * step, gz = z0 + j * step;
                    float y = h[gz * gn + gx];
                    v[j * n + i] = new Vector3(gx * cell - half, y, gz * cell - half);
                    float hl = h[gz * gn + Mathf.Max(0, gx - step)], hr = h[gz * gn + Mathf.Min(res, gx + step)];
                    float hd = h[Mathf.Max(0, gz - step) * gn + gx], hu = h[Mathf.Min(res, gz + step) * gn + gx];
                    nr[j * n + i] = new Vector3(hl - hr, 2f * step * cell, hd - hu).normalized;
                }
            var t = new List<int>(k * k * 6 + edgeCount * 12);
            for (int j = 0; j < k; j++)
                for (int i = 0; i < k; i++)
                {
                    int a = j * n + i;
                    t.Add(a); t.Add(a + n); t.Add(a + 1);
                    t.Add(a + 1); t.Add(a + n); t.Add(a + n + 1);
                }
            if (skirt)
            {
                // hide cracks between neighbours at different detail: a curtain hanging below every edge (both faces)
                float drop = 12f + 18f * step;
                int b = n * n;
                int[][] edges = new int[4][];
                edges[0] = new int[n]; edges[1] = new int[n]; edges[2] = new int[n]; edges[3] = new int[n];
                for (int i = 0; i < n; i++) { edges[0][i] = i; edges[1][i] = (n - 1) * n + i; edges[2][i] = i * n; edges[3][i] = i * n + n - 1; }
                for (int e = 0; e < 4; e++)
                {
                    int s0 = b + e * n;
                    for (int i = 0; i < n; i++)
                    {
                        int src = edges[e][i];
                        v[s0 + i] = v[src] - new Vector3(0f, drop, 0f);
                        nr[s0 + i] = nr[src];
                    }
                    for (int i = 0; i < n - 1; i++)
                    {
                        int a0 = edges[e][i], a1 = edges[e][i + 1], b0 = s0 + i, b1 = s0 + i + 1;
                        t.Add(a0); t.Add(a1); t.Add(b0); t.Add(a1); t.Add(b1); t.Add(b0);
                        t.Add(a0); t.Add(b0); t.Add(a1); t.Add(a1); t.Add(b0); t.Add(b1);
                    }
                }
            }
            var m = new Mesh { name = $"Terrain_{cx}_{cz}_s{step}" };
            if (v.Length > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(v);
            m.SetNormals(nr);
            m.SetTriangles(t, 0, true);
            return m;
        }

        void SetLod(Chunk c, int l)
        {
            if (c.lod == l) return;
            if (c.lods[l] == null)
            {
                if (finePerFrame <= 0) return;     // spread the on-demand builds over frames
                finePerFrame--;
                c.lods[l] = MakeMesh(c.cx, c.cz, Steps[l], true);
            }
            c.lod = l;
            c.mf.sharedMesh = c.lods[l];
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            finePerFrame = 2;
            foreach (var c in chunks)
            {
                float d = Mathf.Sqrt(c.bounds.SqrDistance(p));
                int want = Steps.Length - 1;
                for (int l = 0; l < lodDist.Length; l++)
                    if (d < lodDist[l] * (c.lod > l ? 0.92f : 1.0f)) { want = l; break; }   // small hysteresis
                if (want == 0) c.lastFine = Time.time;
                SetLod(c, want);
                // free far-away fine meshes (≈ 1.3 MB each) after a while
                if (c.lods[0] != null && c.lod != 0 && Time.time - c.lastFine > 20f)
                {
                    Destroy(c.lods[0]);
                    c.lods[0] = null;
                }
            }
        }

        void OnDestroy()
        {
            foreach (var c in chunks)
                foreach (var m in c.lods) if (m) Destroy(m);
        }
    }
}
