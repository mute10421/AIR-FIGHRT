using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 적 전투기 AI: 플레이어와 동일한 비행 모델을 "롤 앤 풀(roll-and-pull)" 방식으로 조종.
    /// 지면 회피, 리드 추적, 기관포 사격, AIM-9 발사, 미사일 경보 시 브레이크 턴 + 플레어.
    /// </summary>
    public class EnemyAI : MonoBehaviour
    {
        public FlightModel fm;
        public Weapons weapons;
        public Damageable self;
        public Damageable target;
        [Range(0.3f, 1f)] public float skill = 0.6f;

        float missileTimer;
        float flareTimer;
        float jinkTimer, jinkRoll;

        // ----- resupply: out of weapons (or badly hurt) → leave the fight, fly to the nearest airfield, low pass = rearm + repair
        [HideInInspector] public int loadGun = 400, loadMissiles = 2, loadFlares = 30;
        bool rtb;
        WorldBuilder.Airport home;
        public bool ReturningToBase => rtb;

        void Start()
        {
            missileTimer = Random.Range(12f, 25f);
            weapons.mode = WeaponMode.Gun;
        }

        void Update()
        {
            if (self.Dead || fm == null) return;
            float dt = Time.deltaTime;
            Vector3 pos = transform.position;
            Vector3 vel = fm.Body.linearVelocity;

            // ----- Threat reaction: incoming missile → break turn + flares -----
            bool threatened = false;
            foreach (var m in Missile.Active)
                if (m != null && m.Target == self && Vector3.Distance(m.transform.position, pos) < 3500f) { threatened = true; break; }

            UpdateResupplyDecision(pos);
            Vector3 desiredDir;
            float homeDist = rtb && home != null ? Flat(home.Center3 - pos) : 1e9f;
            float pullLimit = Mathf.Lerp(0.55f, 0.95f, skill);
            float throttle = Mathf.Clamp(0.85f + (230f - fm.Airspeed) * 0.02f, 0.55f, 1f);

            // ----- Ground avoidance has absolute priority -----
            float agl = pos.y - GameManager.TerrainHeight(pos.x, pos.z);
            float sink = -vel.y;
            bool finalPass = homeDist < 6000f;           // allowed down to the airfield for the low pass
            if (finalPass ? (agl < 110f || (agl < 500f && sink > agl * 0.2f)) : (agl < 700f || (agl < 2000f && sink > agl * 0.08f)))
            {
                desiredDir = (Vector3.up * 2f + new Vector3(transform.forward.x, 0, transform.forward.z)).normalized;
                throttle = 1f;
                pullLimit = 1f;
            }
            else if (threatened)
            {
                Vector3 missileDir = Vector3.zero;
                foreach (var m in Missile.Active)
                    if (m != null && m.Target == self) { missileDir = (m.transform.position - pos).normalized; break; }
                // turn perpendicular to the missile (beam it) and dump flares
                desiredDir = Vector3.Cross(missileDir, Vector3.up).normalized;
                if (Vector3.Dot(desiredDir, transform.forward) < 0) desiredDir = -desiredDir;
                desiredDir = (desiredDir - Vector3.up * 0.15f).normalized;
                throttle = 1f; pullLimit = 1f;
                flareTimer -= dt;
                if (flareTimer <= 0f && Random.value < skill) { weapons.DropFlares(); flareTimer = 0.7f; }
            }
            else if (rtb && home != null)
            {
                weapons.triggerHeld = false;
                weapons.mode = WeaponMode.Gun;
                Vector3 c = home.Center3;
                // cruise high, then line up with the runway and come down to ~250 m over it
                Vector3 aim;
                if (homeDist > 6000f) aim = c + Vector3.up * 1500f;
                else
                {
                    Vector3 d = home.Dir;
                    if (Vector3.Dot(d, c - pos) < 0f) d = -d;                         // pass in whichever direction we arrive from
                    aim = c + Vector3.up * 250f + d * Mathf.Min(homeDist * 0.3f, 1500f);
                }
                desiredDir = (aim - pos).normalized;
                desiredDir.y = Mathf.Max(desiredDir.y, -0.25f);                       // no dive-bombing the field
                desiredDir.Normalize();
                pullLimit = 0.8f;
                throttle = Mathf.Clamp(0.85f + ((homeDist > 6000f ? 260f : 190f) - fm.Airspeed) * 0.02f, 0.4f, 1f);
                if (homeDist < 1300f && pos.y - c.y < 550f) Resupply();
            }
            else if (target != null && !target.Dead)
            {
                Vector3 tPos = target.transform.position;
                var trb = target.GetComponent<Rigidbody>();
                Vector3 tVel = trb ? trb.linearVelocity : Vector3.zero;
                float dist = Vector3.Distance(tPos, pos);
                // gun range: lead for the rounds (they keep our own velocity → relative motion);
                // farther out: intercept where the target will be, from its own track — our speed doesn't enter it
                Vector3 aim;
                if (dist < 2500f)
                {
                    float tof = dist / weapons.muzzleVelocity;
                    aim = tPos + (tVel - vel) * tof - Physics.gravity * 0.5f * tof * tof;
                }
                else
                {
                    float tti = Mathf.Min(dist / Mathf.Max(vel.magnitude, 150f), 6f) * 0.5f;
                    aim = tPos + tVel * tti;
                }
                desiredDir = (aim - pos).normalized;
                // own energy management: hold a fighting airspeed whatever the target does (no slowing down to match a slow player)
                float want = Mathf.Lerp(225f, 265f, skill) + (dist > 5000f ? 30f : 0f);
                throttle = Mathf.Clamp(0.85f + (want - fm.Airspeed) * 0.02f, 0.55f, 1f);

                // Weapons employment
                float ang = Vector3.Angle(transform.forward, aim - pos);
                weapons.triggerHeld = dist < 1100f && ang < 2.5f + (1f - skill) * 2f;
                missileTimer -= dt;
                if (weapons.missiles > 0 && missileTimer <= 0f && dist > 1500f && dist < 7000f && ang < 15f)
                {
                    weapons.mode = WeaponMode.AAM;
                    if (weapons.Locked)
                    {
                        weapons.FireMissile();
                        weapons.mode = WeaponMode.Gun;
                        missileTimer = Random.Range(20f, 35f) / skill;
                    }
                }
                else if (weapons.mode == WeaponMode.AAM && (ang > 20f || dist < 1200f)) weapons.mode = WeaponMode.Gun;

                // random jinks when the player sits on our six
                Vector3 fromTarget = pos - tPos;
                if (Vector3.Angle(target.transform.forward, fromTarget) < 20f && fromTarget.magnitude < 2000f)
                {
                    jinkTimer -= dt;
                    if (jinkTimer <= 0f) { jinkTimer = Random.Range(1.5f, 3.5f); jinkRoll = Random.Range(-1f, 1f); }
                    desiredDir = Quaternion.AngleAxis(jinkRoll * 70f, transform.forward) * (transform.forward + transform.up * 0.8f);
                    desiredDir.Normalize();
                    pullLimit = 1f;
                }
            }
            else
            {
                desiredDir = new Vector3(transform.forward.x, 0, transform.forward.z).normalized;
                weapons.triggerHeld = false;
            }

            Steer(desiredDir, pullLimit);
            fm.throttle = Mathf.MoveTowards(fm.throttle, throttle, dt * 0.8f);
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        void UpdateResupplyDecision(Vector3 pos)
        {
            if (rtb) return;
            bool dry = weapons.missiles <= 0 && weapons.gunAmmo < 40;      // nothing left to shoot with
            bool hurt = self.Health < self.maxHealth * 0.3f;               // limping: go home to repair
            if (!dry && !hurt) return;
            var aps = WorldBuilder.Airports;
            if (aps == null || aps.Length == 0) return;
            float best = float.MaxValue;
            foreach (var ap in aps) { float d = Flat(ap.Center3 - pos); if (d < best) { best = d; home = ap; } }
            rtb = true;
            weapons.triggerHeld = false;
            if (GameManager.Instance) GameManager.Instance.Banner(dry ? "BANDIT WINCHESTER — RTB TO REARM" : "BANDIT DAMAGED — RTB", 2.5f);
        }

        void Resupply()
        {
            weapons.Rearm(loadGun, loadMissiles, loadFlares);
            self.Repair();
            rtb = false;
            home = null;
            missileTimer = Random.Range(8f, 15f);
        }

        void Steer(Vector3 desiredDir, float pullLimit)
        {
            Vector3 local = transform.InverseTransformDirection(desiredDir);
            float offNose = Vector3.Angle(Vector3.forward, local);

            // Put the target in the lift vector (roll so it sits "above" the canopy) then pull.
            float rollErr = Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
            if (offNose < 4f) rollErr = Mathf.Atan2(local.x, Mathf.Abs(local.y) + 0.2f) * Mathf.Rad2Deg * 0.3f;
            fm.rollInput = Mathf.Clamp(rollErr / 35f, -1f, 1f) * Mathf.Lerp(0.6f, 1f, skill);

            float rollAligned = Mathf.Clamp01(1f - Mathf.Abs(rollErr) / 100f);
            float pull = Mathf.Clamp01(offNose / 25f) * rollAligned;
            if (local.y < 0f && offNose < 10f) pull = local.y * 3f; // small push for fine tracking
            fm.pitchInput = Mathf.Clamp(pull, -0.3f, pullLimit);
            fm.yawInput = Mathf.Clamp(local.x * 4f, -1f, 1f) * (offNose < 6f ? 1f : 0f);
        }
    }
}
