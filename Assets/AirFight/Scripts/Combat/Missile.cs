using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>AIM-9급 적외선 유도 미사일. 비례항법(PN, N=4), 30G 한계, 근접신관 15m, 플레어 기만.</summary>
    public class Missile : MonoBehaviour
    {
        public static readonly List<Missile> Active = new List<Missile>();
        internal static void ResetStatics() { Active.Clear(); nextNetId = 0; }

        public Team team;
        public Damageable Target { get; private set; }
        public bool Burning => age < burnTime;
        public Vector3 Velocity => vel;
        /// <summary>Guiding on the local player's jet (not decoyed).</summary>
        public bool IsThreatToPlayer => Target != null && decoy == null && GameManager.Instance != null && Target.gameObject == GameManager.Instance.PlayerObject;

        Transform decoy;            // flare we've been seduced by
        Vector3 vel;
        float age;
        const float burnTime = 8f, motorAccel = 160f, maxG = 30f, life = 40f, fuse = 15f, navN = 4f, gimbal = 50f;   // long-burn motor → about twice the reach
        GameObject owner;
        public GameObject Owner => owner;
        static int nextNetId;
        public int NetId { get; private set; }
        TrailRenderer trail;
        Vector3 lastTargetPos;
        bool hasLastTarget;
        readonly HashSet<Flare> evaluated = new HashSet<Flare>();

        public static Missile Launch(Vector3 pos, Quaternion rot, Vector3 launcherVel, Damageable target, Team team, GameObject owner)
        {
            var go = new GameObject("AIM-9");
            go.transform.SetPositionAndRotation(pos, rot);
            BuildModel(go.transform);
            var m = go.AddComponent<Missile>();
            m.vel = launcherVel + rot * Vector3.down * 4f;   // rail/ejection
            m.Target = target;
            m.team = team;
            m.owner = owner;
            m.NetId = ++nextNetId;
            m.trail = MissileFx.Dress(go, () => m != null && m.age < burnTime, () => m != null && m.IsThreatToPlayer);
            Active.Add(m);
            AudioSynth.PlayLaunch(pos);
            return m;
        }

        public static void BuildModel(Transform parent)
        {
            if (ModelKit.SpawnAim9(parent)) return;   // detailed VARCO 3D Sidewinder when available
            var body = Build.Prim(PrimitiveType.Cylinder, parent, Vector3.zero, new Vector3(0.13f, 1.45f, 0.13f), new Color(0.85f, 0.85f, 0.82f), false, Quaternion.Euler(90, 0, 0));
            Build.Prim(PrimitiveType.Sphere, parent, new Vector3(0, 0, 1.45f), new Vector3(0.13f, 0.13f, 0.2f), new Color(0.3f, 0.3f, 0.35f));
            Build.Prim(PrimitiveType.Cube, parent, new Vector3(0, 0, -1.2f), new Vector3(0.6f, 0.02f, 0.3f), new Color(0.8f, 0.8f, 0.8f));
            Build.Prim(PrimitiveType.Cube, parent, new Vector3(0, 0, -1.2f), new Vector3(0.02f, 0.6f, 0.3f), new Color(0.8f, 0.8f, 0.8f));
            body.name = "Body";
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            age += dt;
            Vector3 pos = transform.position;
            float speed = vel.magnitude;
            Vector3 fwd = speed > 1f ? vel / speed : transform.forward;

            float rho = 1.225f * Mathf.Exp(-Mathf.Max(0f, pos.y) / 8500f);
            Vector3 accel = Physics.gravity;
            if (age < burnTime) accel += transform.forward * motorAccel;
            accel += -fwd * (0.000025f * rho * speed * speed);  // drag (≈ Cd·A / m) — low-drag body for range

            // --- Flare check ---
            if (decoy == null && Target != null)
            {
                foreach (var f in Flare.Active)
                {
                    if (f == null || evaluated.Contains(f)) continue;
                    evaluated.Add(f);
                    Vector3 toF = f.transform.position - pos;
                    if (Vector3.Angle(fwd, toF) < 20f && toF.magnitude < 4000f && Random.value < 0.3f) { decoy = f.transform; break; }
                }
            }

            // --- Guidance (Proportional Navigation) ---
            Vector3 tPos; Vector3 tVel; bool guided = false;
            if (decoy != null) { tPos = decoy.position; tVel = decoy.GetComponent<Flare>().Velocity; guided = true; }
            else if (Target != null && !Target.Dead)
            {
                tPos = Target.transform.position;
                tVel = Damageable.VelocityOf(Target);
                guided = true;
            }
            else { tPos = pos + fwd * 1000f; tVel = Vector3.zero; }

            if (guided && age > 0.25f)
            {
                Vector3 R = tPos - pos;
                if (Vector3.Angle(fwd, R) > gimbal) { decoy = null; Target = null; } // seeker lost it
                else
                {
                    Vector3 Vr = tVel - vel;
                    Vector3 omega = Vector3.Cross(R, Vr) / Mathf.Max(R.sqrMagnitude, 1f);
                    Vector3 aCmd = navN * Vector3.Cross(omega, vel);
                    aCmd -= Vector3.ProjectOnPlane(Physics.gravity, fwd);   // gravity compensation
                    aCmd = Vector3.ProjectOnPlane(aCmd, fwd);
                    float maxA = maxG * Units.G * Mathf.Clamp01(rho / 0.4f + 0.3f) * Mathf.Clamp01(speed / 250f);
                    accel += Vector3.ClampMagnitude(aCmd, maxA);
                }
            }

            Vector3 newVel = vel + accel * dt;
            Vector3 newPos = pos + newVel * dt;

            // --- Proximity fuse (closest point on this step's segment) ---
            if (Target != null && !Target.Dead && decoy == null && age > 0.6f)
            {
                Vector3 segment = newPos - pos;
                Vector3 tp = Target.transform.position;
                float k = Mathf.Clamp01(Vector3.Dot(tp - pos, segment) / Mathf.Max(segment.sqrMagnitude, 1e-4f));
                if (Vector3.Distance(pos + segment * k, tp) < fuse)
                {
                    Target.TakeDamage(Random.value < 0.85f ? 200f : 60f, owner ? owner.GetComponent<Damageable>() : null, 2);
                    Detonate(pos + segment * k);
                    return;
                }
            }

            // --- Ground / object impact ---
            if (age > 0.4f && Physics.Raycast(pos, newPos - pos, out RaycastHit hit, (newPos - pos).magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                if (owner == null || !hit.collider.transform.IsChildOf(owner.transform))
                {
                    var d = hit.collider.GetComponentInParent<Damageable>();
                    if (d != null && d.team != team) d.TakeDamage(200f, owner ? owner.GetComponent<Damageable>() : null, 2);
                    Detonate(hit.point);
                    return;
                }
            }

            vel = newVel;
            transform.position = newPos;
            if (vel.sqrMagnitude > 1f) transform.rotation = Quaternion.LookRotation(vel);
            if (trail) trail.emitting = age < burnTime + 1.5f;
            if (age > life) Detonate(newPos);
        }

        void Detonate(Vector3 p)
        {
            Explosion.Spawn(p, 7f);
            Active.Remove(this);
            if (trail) trail.emitting = false;
            foreach (Transform c in transform) Destroy(c.gameObject);
            Destroy(gameObject, 4f);   // let the smoke trail fade
            Destroy(this);
        }

        void OnDestroy() { Active.Remove(this); }
    }

    /// <summary>
    /// What you see of a missile: blinding motor plume (always at least a bright dot, like the real thing at a distance),
    /// a thick white smoke trail that lingers, and — when it is guiding on you — a red threat ring around it.
    /// </summary>
    public class MissileFx : MonoBehaviour
    {
        static Material plumeMat, smokeMat, ringMat;
        static Texture2D smokeTex, ringTex;
        System.Func<bool> burning, threat;
        Transform plume, ring;
        Renderer plumeR, ringR;

        public static TrailRenderer Dress(GameObject go, System.Func<bool> burning, System.Func<bool> threat)
        {
            if (plumeMat == null)
            {
                plumeMat = Fx.Transparent(new Color(6f, 4.2f, 2.2f, 1f), Fx.SoftDot(), true);
                plumeMat.renderQueue = 3100;
                var smokeShader = Realism.Find("AirFight/Smoke");     // vertex colour → the trail thins out as it ages
                if (smokeShader != null)
                {
                    smokeMat = new Material(smokeShader);
                    smokeMat.SetTexture("_BaseMap", SmokeTexture());
                    smokeMat.SetColor("_BaseColor", new Color(0.95f, 0.95f, 0.95f, 0.85f));
                }
                else smokeMat = Fx.Transparent(new Color(0.93f, 0.93f, 0.92f, 0.75f), SmokeTexture());
                ringMat = Fx.Transparent(new Color(3f, 0.25f, 0.15f, 0.9f), RingTexture(), true);
                ringMat.renderQueue = 3200;
            }
            var tr = go.AddComponent<TrailRenderer>();
            tr.sharedMaterial = smokeMat;
            tr.time = 9f;
            tr.widthCurve = new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.15f, 2.5f), new Keyframe(1f, 9f));
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.85f), 0f), new GradientColorKey(new Color(0.9f, 0.9f, 0.9f), 0.1f), new GradientColorKey(new Color(0.85f, 0.86f, 0.88f), 1f) },
                      new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.7f, 0.2f), new GradientAlphaKey(0f, 1f) });
            tr.colorGradient = g;
            tr.textureMode = LineTextureMode.Stretch;
            tr.minVertexDistance = 3f;
            tr.numCapVertices = 2;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows = false;

            var fx = go.AddComponent<MissileFx>();
            fx.burning = burning; fx.threat = threat;
            fx.plume = Quad("Plume", go.transform, plumeMat, out fx.plumeR);
            fx.ring = Quad("ThreatRing", go.transform, ringMat, out fx.ringR);
            return tr;
        }

        static Transform Quad(string name, Transform parent, Material m, out Renderer r)
        {
            var q = Build.Prim(PrimitiveType.Quad, parent, Vector3.zero, Vector3.one, Color.white, true, null, false, name);
            r = q.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return q.transform;
        }

        // soft across the width, so the trail reads as a puffy smoke column instead of a flat ribbon
        static Texture2D SmokeTexture()
        {
            if (smokeTex != null) return smokeTex;
            const int W = 64, H = 32;
            smokeTex = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var rnd = new System.Random(5);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = (y + 0.5f) / H * 2f - 1f;
                    float a = Mathf.Clamp01(1f - v * v);
                    a *= 0.75f + 0.25f * Mathf.PerlinNoise(x * 0.25f, y * 0.3f);
                    smokeTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            smokeTex.Apply(true);
            return smokeTex;
        }

        static Texture2D RingTexture()
        {
            if (ringTex != null) return ringTex;
            const int N = 64;
            ringTex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) / 0.07f);
                    // four gaps → reads as a target marker, not a halo
                    float ang = Mathf.Atan2(v, u) * Mathf.Rad2Deg;
                    if (Mathf.Abs(Mathf.DeltaAngle(ang, 45f)) < 12f || Mathf.Abs(Mathf.DeltaAngle(ang, 135f)) < 12f ||
                        Mathf.Abs(Mathf.DeltaAngle(ang, -45f)) < 12f || Mathf.Abs(Mathf.DeltaAngle(ang, -135f)) < 12f) a = 0f;
                    ringTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            ringTex.Apply(true);
            return ringTex;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || plume == null) return;
            Vector3 cp = cam.transform.position;
            Vector3 toCam = cp - transform.position;
            float dist = toCam.magnitude;
            Quaternion face = Quaternion.LookRotation(-toCam, cam.transform.up);

            bool burn = burning != null && burning();
            plumeR.enabled = burn;
            if (burn)
            {
                // ≥ 0.35° wide however far away: the motor is the brightest thing in the sky
                float s = Mathf.Max(2.2f, dist * 0.006f) * (0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 25f, transform.position.x));
                plume.position = transform.position - transform.forward * 1.8f;
                plume.rotation = face;
                plume.localScale = new Vector3(s, s, 1f);
            }

            bool th = threat != null && threat();
            ringR.enabled = th && dist > 60f;
            if (ringR.enabled)
            {
                float s = Mathf.Clamp(dist * 0.045f, 8f, 900f) * (1f + 0.12f * Mathf.Sin(Time.time * 14f));   // ≈ 2.6° ring, pulsing
                ring.position = transform.position;
                ring.rotation = face;
                ring.localScale = new Vector3(s, s, 1f);
            }
        }
    }
}
