using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 레이캐스트 서스펜션 랜딩기어 (노즈 1 + 메인 2). 스프링-댐퍼, 타이어 측면 마찰, 구름 저항,
    /// 메인 휠 브레이크, 노즈휠 조향(러더), 기어 올림/내림, 하드 랜딩 판정.
    /// </summary>
    public class LandingGear : MonoBehaviour
    {
        class Wheel
        {
            public Vector3 attach;       // aircraft-local strut top
            public float k, c;           // spring N/m, damper N·s/m
            public bool steer;
            public float comp;           // compression (m)
            public bool contact;
            public Transform strut, tire;
            public Transform model;      // detailed gear leg (origin = tyre centre), swings into its well when retracted
            public float retractSign;    // +1 swings aft (nose gear), -1 swings forward (mains, like the F-16)
        }

        public FlightModel fm;
        public bool Down { get; private set; } = true;
        public bool OnGround { get; private set; }
        public float Deploy { get; private set; } = 1f;     // 0 = up, 1 = down & locked
        [Range(0, 1)] public float brake;
        public bool parkingBrake = true;
        [Range(-1, 1)] public float steerInput;

        const float Rest = 1.1f, Radius = 0.35f;
        readonly List<Wheel> wheels = new List<Wheel>();
        readonly RaycastHit[] hits = new RaycastHit[8];
        Rigidbody rb;
        Damageable dmg;
        float grace = 1.5f;   // ignore spawn settling

        public void Init(FlightModel flight)
        {
            fm = flight;
            rb = GetComponent<Rigidbody>();
            dmg = GetComponent<Damageable>();
            // F-16: nose gear under the intake, mains just aft of the CG
            AddWheel(new Vector3(0f, -0.6f, 4.0f), 70000f, 11000f, true);
            AddWheel(new Vector3(1.2f, -0.6f, -0.7f), 200000f, 45000f, false);
            AddWheel(new Vector3(-1.2f, -0.6f, -0.7f), 200000f, 45000f, false);
        }

        void AddWheel(Vector3 attach, float k, float c, bool steer)
        {
            var w = new Wheel { attach = attach, k = k, c = c, steer = steer };
            Color metal = new Color(0.6f, 0.6f, 0.62f);
            w.strut = Build.Prim(PrimitiveType.Cylinder, transform, attach, new Vector3(0.12f, 0.5f, 0.12f), metal).transform;
            w.tire = Build.Prim(PrimitiveType.Cylinder, transform, attach, new Vector3(Radius * 2f, 0.12f, Radius * 2f), new Color(0.08f, 0.08f, 0.08f), false, Quaternion.Euler(0, 0, 90)).transform;
            wheels.Add(w);
        }

        /// <summary>Use detailed 3D gear legs instead of the primitive strut + tyre.</summary>
        public void AttachModels(Transform nose, Transform mainR, Transform mainL)
        {
            var models = new[] { nose, mainR, mainL };
            for (int i = 0; i < wheels.Count && i < models.Length; i++)
            {
                if (models[i] == null) continue;
                var w = wheels[i];
                w.model = models[i];
                w.model.SetParent(transform, false);
                w.model.localScale = Vector3.one;
                w.retractSign = w.steer ? 1f : -1f;
                w.strut.gameObject.SetActive(false);
                w.tire.gameObject.SetActive(false);
            }
        }

        public void SetInstant(bool down) { Down = down; Deploy = down ? 1f : 0f; }

        /// <summary>After a teleport: forget suspension history and give the gear a moment to settle.</summary>
        public void ResetState()
        {
            foreach (var w in wheels) { w.comp = 0f; w.contact = false; }
            grace = 1.5f;
        }

        public void ToggleGear()
        {
            if (OnGround) return;              // weight-on-wheels interlock
            Down = !Down;
        }

        bool Cast(Vector3 origin, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(origin, dir, hits, dist, ~0, QueryTriggerInteraction.Ignore);
            float min = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].rigidbody == rb) continue;
                if (hits[i].distance < min) { min = hits[i].distance; best = hits[i]; found = true; }
            }
            return found;
        }

        void FixedUpdate()
        {
            if (rb == null || fm == null || fm.Destroyed) return;
            float dt = Time.fixedDeltaTime;
            grace -= dt;
            Deploy = Mathf.MoveTowards(Deploy, Down ? 1f : 0f, dt / 3.5f);
            fm.flaps = Deploy;   // F-16: flaperons + leading-edge flaps droop with the gear handle down
            bool locked = Deploy >= 0.999f;
            bool any = false;
            float speed = rb.linearVelocity.magnitude;
            if (throttleWasAdvanced() ) parkingBrake = false;

            foreach (var w in wheels)
            {
                Vector3 origin = transform.TransformPoint(w.attach);
                Vector3 down = -transform.up;
                float maxDist = Rest + Radius;
                float ext = Rest;
                bool hadContact = w.contact;
                w.contact = false;
                if (locked && Cast(origin, down, maxDist, out RaycastHit hit))
                {
                    float comp = maxDist - hit.distance;
                    float compVel = hadContact ? (comp - w.comp) / dt : 0f;   // no velocity spike on first contact
                    w.comp = comp;
                    w.contact = true;
                    any = true;
                    ext = hit.distance - Radius;

                    // touchdown sink rate (measured along the strut) above ~1300 fpm collapses the gear
                    float sink = -Vector3.Dot(rb.GetPointVelocity(origin), hit.normal);
                    if (!hadContact && grace <= 0f && dmg != null)
                    {
                        if (sink > 14f) { dmg.Kill(); return; }                       // > ~2750 fpm: gear collapses
                        if (sink > 7f)                                                 // hard landing: damage only
                        {
                            dmg.TakeDamage((sink - 7f) * 6f);
                            if (GameManager.Instance) GameManager.Instance.Banner($"HARD LANDING  {sink * 196.85f:0} FPM", 3f);
                        }
                    }

                    Vector3 n = hit.normal;
                    float Fs = Mathf.Max(0f, w.k * comp + w.c * Mathf.Clamp(compVel, -6f, 6f));
                    Vector3 pv = rb.GetPointVelocity(hit.point);
                    Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, n).normalized;
                    if (w.steer)
                    {
                        float maxSteer = Mathf.Lerp(32f, 4f, Mathf.Clamp01(speed / 45f));
                        fwd = Quaternion.AngleAxis(steerInput * maxSteer, n) * fwd;
                    }
                    Vector3 side = Vector3.Cross(n, fwd);
                    float vF = Vector3.Dot(pv, fwd), vS = Vector3.Dot(pv, side);
                    float lateral = -Mathf.Clamp(vS * 4f, -1f, 1f) * 0.85f * Fs;
                    float longi = -Mathf.Clamp(vF * 2f, -1f, 1f) * 0.015f * Fs;                // rolling resistance
                    float b = parkingBrake ? 1f : brake;
                    // wheel brakes (game-tuned): anti-skid μ≈1.3, at least ~0.42 g per main even while the wings still carry lift
                    if (!w.steer) longi += -Mathf.Clamp(vF * 1.5f, -1f, 1f) * b * Mathf.Max(1.3f * Fs, 0.42f * rb.mass * 9.81f);   // strong brakes: ≈0.85 g with both mains
                    rb.AddForceAtPosition(n * Fs + side * lateral + fwd * longi, hit.point);
                }
                else w.comp = 0f;

                // visuals
                bool show = Deploy > 0.05f;
                if (w.model != null)
                {
                    if (w.model.gameObject.activeSelf != show) w.model.gameObject.SetActive(show);
                    if (show)
                    {
                        // the leg swings about its trunnion (the strut attach point) while retracting
                        var swing = Quaternion.Euler(w.retractSign * (1f - Deploy) * 95f, 0f, 0f);
                        w.model.localPosition = w.attach + swing * (Vector3.down * ext);
                        w.model.localRotation = swing * Quaternion.Euler(0f, w.steer ? steerInput * 20f : 0f, 0f);
                    }
                    continue;
                }
                if (w.strut.gameObject.activeSelf != show) { w.strut.gameObject.SetActive(show); w.tire.gameObject.SetActive(show); }
                if (show)
                {
                    float e = ext * Deploy;
                    w.strut.localPosition = w.attach + Vector3.down * (e * 0.5f);
                    w.strut.localScale = new Vector3(0.12f, e * 0.5f, 0.12f);
                    w.tire.localPosition = w.attach + Vector3.down * e;
                    w.tire.localRotation = Quaternion.Euler(0, w.steer ? steerInput * 20f : 0f, 90f);
                }
            }
            OnGround = any;
            fm.onGround = any;
        }

        bool throttleWasAdvanced() => parkingBrake && fm.throttle > 0.15f;
    }
}
