using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AirFight
{
    /// <summary>
    /// 격추 연출: 기체를 잔해로 바꿔 구겨지고(외판이 찌그러짐) 그을리며, 날개·꼬리 등이 떨어져 나가 빙글빙글 돌고,
    /// 동체는 불길과 검은 연기를 끌며 추락하다 땅에 부딪혀 한 번 더 폭발한다.
    /// </summary>
    public class Wreck : MonoBehaviour
    {
        static readonly string[] Breakable = { "Wing_R", "Wing_L", "VTail", "Stab_R", "Stab_L", "Canopy", "LEF_R", "LEF_L", "Flaperon_R", "Flaperon_L" };
        static Material smokeMat, fireMat;
        static Shader wreckShader;

        readonly List<Material> mats = new List<Material>();
        readonly List<(Transform t, float phase)> flames = new List<(Transform, float)>();
        Rigidbody rb;
        TrailRenderer smoke;
        float age, life, fireSize;
        bool main, impacted;
        float nextPop;
        int pops;

        /// <summary>Turn an aircraft into a falling, burning wreck. Returns the main hull (the death camera follows it).</summary>
        public static Transform Create(Transform aircraft, Vector3 vel, Vector3 angVel)
        {
            var model = aircraft.Find("Model");
            if (model == null) model = aircraft;
            var go = Instantiate(model.gameObject, model.position, model.rotation);
            go.name = "Wreck";
            go.SetActive(true);
            // a clean visual copy: no colliders, scripts, glows or glass
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(b);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterial == null || r.sharedMaterial.renderQueue >= 2900 || r is TrailRenderer || r is LineRenderer) r.enabled = false;

            var hull = go.AddComponent<Wreck>();
            hull.main = true;
            hull.Setup(vel * 0.85f, angVel + Random.onUnitSphere * Random.Range(1.5f, 3.5f), 6000f, 9f, 9f);

            // tear off a few parts (wings, tail, stabilators, canopy …) — they spin away on their own
            var parts = new List<Transform>();
            foreach (var n in Breakable) { var t = FindDeep(go.transform, n); if (t != null && t != go.transform) parts.Add(t); }
            if (parts.Count == 0)
                foreach (Transform c in go.transform) if (c.GetComponentInChildren<Renderer>() != null) parts.Add(c);   // procedural airframe
            int tear = Mathf.Min(parts.Count, Random.Range(2, 5));
            for (int k = 0; k < tear; k++)
            {
                int i = Random.Range(0, parts.Count);
                var p = parts[i]; parts.RemoveAt(i);
                p.SetParent(null, true);
                var piece = p.gameObject.AddComponent<Wreck>();
                Vector3 outward = (p.position - go.transform.position); outward.y = Mathf.Abs(outward.y) + 1f;
                piece.Setup(vel * Random.Range(0.6f, 0.9f) + outward.normalized * Random.Range(15f, 40f),
                            Random.onUnitSphere * Random.Range(4f, 10f), 400f, 6f, 3f);
            }
            // pieces start inside each other: let them separate without shoving
            var all = new List<Collider>();
            foreach (var w in FindObjectsByType<Wreck>(FindObjectsSortMode.None)) if (w.age < 0.01f) { var c = w.GetComponent<Collider>(); if (c) all.Add(c); }
            for (int i = 0; i < all.Count; i++) for (int j = i + 1; j < all.Count; j++) Physics.IgnoreCollision(all[i], all[j]);
            // a few extra blasts while it breaks up
            hull.nextPop = 0.25f;
            return go.transform;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var r = FindDeep(c, name); if (r) return r; }
            return null;
        }

        Vector3 baseScale = Vector3.one;
        void Setup(Vector3 v, Vector3 w, float mass, float lifeTime, float fire)
        {
            life = lifeTime; fireSize = fire; baseScale = transform.localScale;
            rb = gameObject.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.linearDamping = main ? 0.02f : 0.12f;     // torn panels flutter, the hull plummets
            rb.angularDamping = 0.05f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = v;
            rb.angularVelocity = w;
            // a collider from the visual bounds (so it hits the ground and bounces / slides)
            var b = new Bounds(transform.position, Vector3.zero); bool any = false;
            foreach (var r in GetComponentsInChildren<Renderer>()) { if (!r.enabled) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            var bc = gameObject.AddComponent<BoxCollider>();
            bc.center = transform.InverseTransformPoint(b.center);
            Vector3 ls = transform.lossyScale;
            bc.size = new Vector3(b.size.x / Mathf.Max(ls.x, 1e-3f), b.size.y / Mathf.Max(ls.y, 1e-3f), b.size.z / Mathf.Max(ls.z, 1e-3f)) * 0.7f;

            // crumpling, scorching material on every visible part
            if (wreckShader == null) wreckShader = Realism.Find("AirFight/Wreck");
            float seed = Random.Range(0f, 100f);
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || wreckShader == null) continue;
                var src = r.sharedMaterials;
                var dst = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    var m = new Material(wreckShader);
                    var s = src[i];
                    if (s != null)
                    {
                        Texture tex = s.HasProperty("baseColorTexture") ? s.GetTexture("baseColorTexture") : s.HasProperty("_BaseMap") ? s.GetTexture("_BaseMap") : s.HasProperty("_MainTex") ? s.GetTexture("_MainTex") : null;
                        Color c = s.HasProperty("baseColorFactor") ? s.GetColor("baseColorFactor") : s.HasProperty("_BaseColor") ? s.GetColor("_BaseColor") : s.HasProperty("_Color") ? s.GetColor("_Color") : Color.gray;
                        if (tex != null) m.SetTexture("_BaseMap", tex);
                        m.SetColor("_BaseColor", c);
                    }
                    m.SetFloat("_Seed", seed);
                    dst[i] = m; mats.Add(m);
                }
                r.sharedMaterials = dst;
                r.SetPropertyBlock(null);
                r.shadowCastingMode = ShadowCastingMode.On;
            }

            // fire + a trail of thick black smoke
            if (fireMat == null)
            {
                fireMat = Fx.Transparent(new Color(4f, 1.6f, 0.4f, 0.9f), Fx.SoftDot(), true);
                fireMat.renderQueue = 3100;
                var smokeShader = Realism.Find("AirFight/Smoke");
                if (smokeShader != null) { smokeMat = new Material(smokeShader); smokeMat.SetTexture("_BaseMap", Fx.SoftDot()); smokeMat.SetColor("_BaseColor", new Color(0.09f, 0.085f, 0.08f, 0.9f)); }
                else smokeMat = Fx.Transparent(new Color(0.08f, 0.08f, 0.08f, 0.8f), Fx.SoftDot());
            }
            int nFlames = main ? 5 : 2;
            for (int i = 0; i < nFlames; i++)
            {
                var q = Build.Prim(PrimitiveType.Quad, null, Vector3.zero, Vector3.one, Color.white, true, null, false, "WreckFire");
                q.transform.SetParent(transform, false);
                q.transform.position = b.center + Random.insideUnitSphere * b.extents.magnitude * 0.35f;
                var r = q.GetComponent<Renderer>(); r.sharedMaterial = fireMat; r.shadowCastingMode = ShadowCastingMode.Off;
                flames.Add((q.transform, Random.value * 10f));
            }
            var sgo = new GameObject("WreckSmoke");
            sgo.transform.SetParent(transform, false);
            sgo.transform.position = b.center;
            smoke = sgo.AddComponent<TrailRenderer>();
            smoke.sharedMaterial = smokeMat;
            smoke.time = main ? 5f : 3f;
            smoke.widthCurve = new AnimationCurve(new Keyframe(0f, fireSize * 0.4f), new Keyframe(1f, fireSize * 3.5f));
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.5f, 0.2f), 0f), new GradientColorKey(new Color(0.15f, 0.14f, 0.13f), 0.08f), new GradientColorKey(new Color(0.3f, 0.3f, 0.3f), 1f) },
                      new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.75f, 0.3f), new GradientAlphaKey(0f, 1f) });
            smoke.colorGradient = g;
            smoke.minVertexDistance = 4f;
            smoke.shadowCastingMode = ShadowCastingMode.Off;
        }

        void Update()
        {
            age += Time.deltaTime;
            float crumple = Mathf.Clamp01(age / 0.7f);
            crumple = 1f - (1f - crumple) * (1f - crumple);
            float burn = Mathf.Clamp01(age / (life * 0.8f));
            foreach (var m in mats) { m.SetFloat("_Crumple", crumple); m.SetFloat("_Burn", burn); }

            // secondary explosions while the hull breaks up
            if (main && pops < 3 && age >= nextPop)
            {
                pops++;
                Explosion.Spawn(transform.position + Random.insideUnitSphere * 4f, Random.Range(6f, 11f));
                nextPop = age + Random.Range(0.35f, 0.7f);
            }

            var cam = Camera.main;
            float fade = 1f - Mathf.Clamp01((age - life * 0.75f) / (life * 0.25f));
            foreach (var (t, ph) in flames)
            {
                if (t == null) continue;
                float flick = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 6f + ph, ph);
                float s = fireSize * flick * fade * (impacted ? 1.4f : 1f);
                t.localScale = Vector3.one * s / Mathf.Max(transform.lossyScale.x, 1e-3f);
                if (cam) t.rotation = Quaternion.LookRotation(t.position - cam.transform.position, cam.transform.up);
            }
            if (smoke) smoke.emitting = fade > 0.05f;
            // last second: shrink away, then gone (smoke trail is allowed to drift off for a moment)
            float shrink = Mathf.Clamp01(life + 1f - age);
            if (age > life && shrink > 0f) transform.localScale = baseScale * Mathf.Max(shrink, 0.01f);
            if (age > life + 1f) { foreach (var r in GetComponentsInChildren<Renderer>()) if (!(r is TrailRenderer)) r.enabled = false; }
            if (age > life + 1f + (smoke ? smoke.time : 0f)) Destroy(gameObject);
        }

        void OnCollisionEnter(Collision c)
        {
            if (impacted || c.relativeVelocity.magnitude < 12f) return;
            if (c.collider.GetComponentInParent<Wreck>() != null) return;    // pieces touching each other
            impacted = true;
            Explosion.Spawn(c.GetContact(0).point + Vector3.up * 2f, main ? 26f : 9f);
            if (rb) { rb.linearDamping = 1.5f; rb.angularDamping = 2f; }   // dig in, slide, burn
            if (main) life = Mathf.Max(life, age + 4f);                       // burns briefly on the ground
        }
    }
}
