using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    public enum WeaponMode { Gun, AAM }

    /// <summary>M61A1 20mm 기관포 + AIM-9 적외선 유도 미사일 + 플레어.</summary>
    public class Weapons : MonoBehaviour
    {
        public Team team;
        public Transform gunMuzzle;
        public List<Transform> missileRails = new List<Transform>();
        public int gunAmmo = 511;
        public int missiles = 4;
        public int flares = 30;
        public float gunRate = 100f;          // rounds/sec (6000 rpm)
        public float muzzleVelocity = 1030f;
        public float gunDamage = 9f;
        public float seekerCone = 27f;        // deg
        public float seekerRange = 9000f;     // m
        public float lockTime = 0.9f;
        /// <summary>Player: the seeker takes the same target the HUD designates (nearest, weighted by angle off the nose), at any range.</summary>
        public bool hudDesignation;
        public WeaponMode mode = WeaponMode.Gun;
        public Func<IEnumerable<Damageable>> targetProvider;

        [NonSerialized] public bool triggerHeld;
        /// <summary>Keep the AIM-9 seeker searching even in gun mode (player: SPACE fires a missile at any time).</summary>
        public bool alwaysSeek;
        /// <summary>Gun aim assist: rounds are bent up to this many degrees toward the lead point of a target near the pipper.</summary>
        public float aimAssistDeg;
        /// <summary>Rounds passing within this distance of an enemy count as hits (0 = exact collider hits only).</summary>
        public float bulletProximity;
        /// <summary>Pilot's eye / HUD gun cross. When set, the gun is harmonized: rounds converge on the gun cross line at
        /// <see cref="harmonizeRange"/> instead of flying parallel from the muzzle off to the side.</summary>
        public Transform boresight;
        public float harmonizeRange = 700f;
        public Damageable SeekerTarget { get; private set; }
        public float LockProgress { get; private set; }
        public bool Locked => SeekerTarget != null && LockProgress >= 1f;
        public bool Firing { get; private set; }
        public event Action OnGunShot;
        public event Action OnMissileLaunched;

        Rigidbody body;
        Damageable selfDmg;
        float gunAccum;
        int roundCounter;
        float missileCooldown;
        readonly List<GameObject> railModels = new List<GameObject>();

        void Awake() { body = GetComponentInParent<Rigidbody>(); }

        // ---- RWR support: every active weapon system, so the victim can ask "is anyone tracking / locking me?" ----
        public static readonly List<Weapons> All = new List<Weapons>();
        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); }
        internal static void ResetStatics() { All.Clear(); }

        /// <summary>0 = nobody, 1 = an enemy seeker is tracking this target, 2 = an enemy has a missile lock on it.</summary>
        public static int ThreatLevelOn(Damageable target)
        {
            int level = 0;
            foreach (var w in All)
            {
                if (w == null || w.SeekerTarget != target || w.team == target.team) continue;
                var d = w.GetComponent<Damageable>();
                if (d != null && d.Dead) continue;
                level = Mathf.Max(level, w.Locked ? 2 : 1);
            }
            return level;
        }

        public void RegisterRailModel(GameObject model) { railModels.Add(model); }

        void Update()
        {
            float dt = Time.deltaTime;
            missileCooldown -= dt;

            // --- Gun ---
            Firing = triggerHeld && mode == WeaponMode.Gun && gunAmmo > 0;
            if (Firing && gunMuzzle != null)
            {
                Vector3 baseDir = AssistedGunDirection();
                gunAccum += dt * gunRate;
                while (gunAccum >= 1f && gunAmmo > 0)
                {
                    gunAccum -= 1f;
                    gunAmmo--;
                    roundCounter++;
                    Vector3 dir = baseDir;
                    dir = Quaternion.Euler(UnityEngine.Random.Range(-0.12f, 0.12f), UnityEngine.Random.Range(-0.12f, 0.12f), 0f) * dir;
                    Vector3 v = (body ? body.linearVelocity : Vector3.zero) + dir * muzzleVelocity;
                    Bullets.Fire(gunMuzzle.position + dir * UnityEngine.Random.Range(6f, 18f), v, team, gunDamage, roundCounter % 4 == 0, bulletProximity, selfDmg ? selfDmg : (selfDmg = GetComponentInParent<Damageable>()));
                }
                OnGunShot?.Invoke();
            }
            else gunAccum = 0.99f; // first round fires immediately

            // --- AIM-9 seeker ---
            if ((mode == WeaponMode.AAM || alwaysSeek) && missiles > 0 && targetProvider != null) UpdateSeeker(dt);
            else { SeekerTarget = null; LockProgress = 0f; }
        }

        /// <summary>Time of flight of a round to a point at distance <paramref name="dist"/> along <paramref name="dir"/> (own speed + light drag).</summary>
        public float TimeOfFlight(float dist, Vector3 dir)
        {
            float v0 = muzzleVelocity + Vector3.Dot(body ? body.linearVelocity : Vector3.zero, dir);
            float t = dist / Mathf.Max(v0, 300f);
            return t * (1f + 0.06f * t);                     // the rounds slow ~12 %/s
        }

        /// <summary>Gun direction with a light "magnetism" toward the firing solution of the closest target near the nose.</summary>
        Vector3 AssistedGunDirection()
        {
            Vector3 fwd = boresight ? (boresight.position + boresight.forward * harmonizeRange - gunMuzzle.position).normalized : gunMuzzle.forward;
            if (aimAssistDeg <= 0f || targetProvider == null) return fwd;
            Vector3 pos = gunMuzzle.position;
            Vector3 myVel = body ? body.linearVelocity : Vector3.zero;
            Vector3 best = fwd; float bestAng = aimAssistDeg * 3f;   // search a bit wider than the bend we allow
            foreach (var d in targetProvider())
            {
                if (d == null || d.Dead || d.team == team) continue;
                Vector3 to = d.transform.position - pos;
                float dist = to.magnitude;
                if (dist > 2000f || dist < 50f) continue;
                // lead: where the target will be when the rounds (muzzle velocity + our speed) get there
                Vector3 tv = Damageable.VelocityOf(d);
                float t = TimeOfFlight(dist, to / dist);
                Vector3 aim = d.transform.position + (tv - myVel) * t - 0.5f * Physics.gravity * t * t;
                Vector3 dir = (aim - pos).normalized;
                float ang = Vector3.Angle(fwd, dir);
                if (ang < bestAng) { bestAng = ang; best = dir; }
            }
            return Vector3.RotateTowards(fwd, best, aimAssistDeg * Mathf.Deg2Rad, 0f);
        }

        void UpdateSeeker(float dt)
        {
            Damageable best = null;
            float bestAngle = seekerCone, bestScore = float.MaxValue;
            Vector3 pos = transform.position;
            foreach (var d in targetProvider())
            {
                if (d == null || d.Dead || d.team == team) continue;
                Vector3 to = d.transform.position - pos;
                float dist = to.magnitude;
                if (dist < 150f) continue;
                if (!hudDesignation && dist > seekerRange) continue;
                float ang = Vector3.Angle(transform.forward, to);
                if (ang >= seekerCone) continue;
                if (hudDesignation)
                {
                    float score = dist * (1f + ang / 15f);                      // same pick as the HUD target box
                    if (d == SeekerTarget) score *= 0.8f;                         // stick with the current lock
                    if (score < bestScore) { bestScore = score; best = d; }
                }
                else if (ang < bestAngle) { bestAngle = ang; best = d; }
            }
            if (best != SeekerTarget) { SeekerTarget = best; LockProgress = 0f; }
            if (SeekerTarget != null) LockProgress = Mathf.Min(1f, LockProgress + dt / lockTime);
        }

        public void CycleMode()
        {
            mode = mode == WeaponMode.Gun ? WeaponMode.AAM : WeaponMode.Gun;
            LockProgress = 0f;
        }

        public bool FireMissile()
        {
            if (missiles <= 0 || missileCooldown > 0f) return false;
            missileCooldown = 0.6f;
            int idx = missileRails.Count > 0 ? (missiles - 1) % missileRails.Count : -1;
            Transform rail = idx >= 0 ? missileRails[idx] : transform;
            missiles--;
            if (idx >= 0 && idx < railModels.Count && railModels[idx] != null) railModels[idx].SetActive(false);
            Damageable tgt = Locked ? SeekerTarget : null;
            Missile.Launch(rail.position, transform.rotation, body ? body.linearVelocity : Vector3.zero, tgt, team, gameObject);
            OnMissileLaunched?.Invoke();
            return true;
        }

        public void DropFlares()
        {
            if (flares <= 0) return;
            flares -= 2;
            Vector3 v = body ? body.linearVelocity : Vector3.zero;
            Flare.Spawn(transform.position - transform.forward * 5f, v * 0.8f + (-transform.up + transform.right) * 25f);
            Flare.Spawn(transform.position - transform.forward * 5f, v * 0.8f + (-transform.up - transform.right) * 25f);
        }

        public void Rearm(int ammo, int msl, int flr)
        {
            gunAmmo = ammo; missiles = msl; flares = flr;
            for (int i = 0; i < railModels.Count; i++) if (railModels[i]) railModels[i].SetActive(i < msl);   // only the rounds actually loaded
        }
    }

    /// <summary>Pooled ballistic bullets with segment raycasts and tracer lines.</summary>
    public class Bullets : MonoBehaviour
    {
        struct Round { public Vector3 pos, vel; public float life; public Team team; public float dmg; public int tracer; public float prox; public Damageable src; }

        static Bullets inst;
        readonly List<Round> rounds = new List<Round>(512);
        readonly List<LineRenderer> tracerPool = new List<LineRenderer>();
        readonly List<bool> tracerUsed = new List<bool>();

        static Bullets I
        {
            get
            {
                if (inst == null) inst = new GameObject("Bullets").AddComponent<Bullets>();
                return inst;
            }
        }

        public static void Fire(Vector3 pos, Vector3 vel, Team team, float dmg, bool tracer, float proximity = 0f, Damageable src = null)
        {
            var b = I;
            int tr = -1;
            if (tracer) tr = b.GetTracer(team);
            b.rounds.Add(new Round { pos = pos, vel = vel, life = 3f, team = team, dmg = dmg, tracer = tr, prox = proximity, src = src });
        }

        int GetTracer(Team team)
        {
            for (int i = 0; i < tracerPool.Count; i++)
                if (!tracerUsed[i]) { tracerUsed[i] = true; Color(tracerPool[i], team); tracerPool[i].enabled = true; return i; }
            if (tracerPool.Count >= 120) return -1;
            var lr = Build.Line(transform, UnityEngine.Color.white, 0.25f);
            tracerPool.Add(lr); tracerUsed.Add(true);
            Color(lr, team);
            return tracerPool.Count - 1;
        }

        static void Color(LineRenderer lr, Team t)
        {
            lr.sharedMaterial = Mats.Get(t == Team.Blue ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.35f, 0.2f), true);
            lr.startWidth = 0.35f; lr.endWidth = 0.1f;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            for (int i = rounds.Count - 1; i >= 0; i--)
            {
                var r = rounds[i];
                Vector3 next = r.pos + r.vel * dt;
                r.vel += Physics.gravity * dt;
                r.vel *= 1f - 0.12f * dt; // drag
                r.life -= dt;
                Vector3 seg = next - r.pos;
                bool remove = r.life <= 0f;
                if (!remove && Physics.Raycast(r.pos, seg, out RaycastHit hit, seg.magnitude, ~0, QueryTriggerInteraction.Ignore))
                {
                    var d = hit.collider.GetComponentInParent<Damageable>();
                    if (d != null)
                    {
                        if (d.team != r.team)
                        {
                            d.TakeDamage(r.dmg, r.src, 1);
                            Spark.Spawn(hit.point);
                            remove = true;
                        }
                    }
                    else { Spark.Spawn(hit.point); remove = true; }
                }
                // near-miss counts as a hit (forgiving gunnery for the player)
                if (!remove && r.prox > 0f)
                {
                    foreach (var d in Damageable.All)
                    {
                        if (d == null || d.Dead || d.team == r.team) continue;
                        Vector3 c = d.transform.position;
                        float k = Mathf.Clamp01(Vector3.Dot(c - r.pos, seg) / Mathf.Max(seg.sqrMagnitude, 1e-4f));
                        Vector3 closest = r.pos + seg * k;
                        if ((closest - c).sqrMagnitude < r.prox * r.prox)
                        {
                            d.TakeDamage(r.dmg, r.src, 1);
                            Spark.Spawn(closest);
                            remove = true;
                            break;
                        }
                    }
                }
                if (r.tracer >= 0)
                {
                    var lr = tracerPool[r.tracer];
                    lr.SetPosition(0, next);
                    lr.SetPosition(1, next - r.vel.normalized * 18f);
                }
                if (remove)
                {
                    if (r.tracer >= 0) { tracerUsed[r.tracer] = false; tracerPool[r.tracer].enabled = false; }
                    rounds.RemoveAt(i);
                    continue;
                }
                r.pos = next;
                rounds[i] = r;
            }
        }
    }

    public class Spark : MonoBehaviour
    {
        float t;
        public static void Spawn(Vector3 p)
        {
            var go = Build.Prim(PrimitiveType.Sphere, null, p, Vector3.one * 1.5f, new Color(1f, 0.8f, 0.4f), true);
            go.AddComponent<Spark>();
        }
        void Update()
        {
            t += Time.deltaTime;
            transform.localScale = Vector3.one * Mathf.Lerp(2.5f, 0.1f, t / 0.25f);
            if (t > 0.25f) Destroy(gameObject);
        }
    }

    public class Flare : MonoBehaviour
    {
        public static readonly List<Flare> Active = new List<Flare>();
        public Vector3 Velocity;
        float life = 5f;

        /// <summary>Raised for locally dropped flares (multiplayer shares them so remote missiles can be decoyed).</summary>
        public static event System.Action<Vector3, Vector3> LocalDropped;
        internal static void ResetStatics() { Active.Clear(); LocalDropped = null; }

        public static void Spawn(Vector3 pos, Vector3 vel) => Spawn(pos, vel, false);

        public static void Spawn(Vector3 pos, Vector3 vel, bool remote)
        {
            if (!remote) LocalDropped?.Invoke(pos, vel);
            var go = Build.Prim(PrimitiveType.Sphere, null, pos, Vector3.one * 1.2f, new Color(1f, 0.95f, 0.7f), true, name: "Flare");
            var f = go.AddComponent<Flare>();
            f.Velocity = vel;
            var tr = go.AddComponent<TrailRenderer>();
            tr.sharedMaterial = Mats.Get(new Color(0.85f, 0.85f, 0.85f), true);
            tr.time = 1.2f; tr.startWidth = 1.5f; tr.endWidth = 0.2f;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Active.Add(f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            Velocity += Physics.gravity * 0.5f * dt;
            Velocity *= 1f - 1.5f * dt;
            transform.position += Velocity * dt;
            life -= dt;
            if (life <= 0f) Destroy(gameObject);
        }

        void OnDestroy() { Active.Remove(this); }
    }
}
