using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// F-16급 경량 전투기 비행 모델.
    /// - 양력/항력/추력/측력: 받음각(AoA), 동압, 고도별 공기밀도, 마하수(천음속 조파항력) 반영
    /// - 플라이 바이 와이어(FBW): 피치 = G 명령(-3~+9G), 롤 = 롤 레이트 명령, AoA 리미터 25°
    ///   → 실제 F-16처럼 스틱을 놓으면 1G 유지, 저속에선 AoA 한계 때문에 G가 안 나옴
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class FlightModel : MonoBehaviour
    {
        [Header("Airframe (F-16C 근사치)")]
        public float dryMass = 7300f;        // kg (기체 + 조종사 + 공대공 무장 + 탄약)
        public float maxFuel = 3200f;        // kg 내부 연료 (F-16C ≈ 3,200 kg)
        public bool infiniteFuel;            // AI aircraft
        [System.NonSerialized] public float mass = 10500f;   // dryMass + fuel (updated every physics step)
        public float Fuel { get; private set; } = 3200f;
        public float FuelFlow { get; private set; }          // kg/s
        public float FuelFraction => Fuel / maxFuel;
        public bool Bingo => !infiniteFuel && Fuel < 600f;
        public bool Flameout => !infiniteFuel && Fuel <= 0f;
        public void Refuel(float kg) { Fuel = Mathf.Min(maxFuel, Fuel + kg); }
        public void SetFuel(float kg) { Fuel = Mathf.Clamp(kg, 0f, maxFuel); }
        public float wingArea = 27.87f;      // m²
        public float milThrust = 92000f;     // N  (F110-GE-129 MIL, game-boosted)
        public float abThrust = 170000f;     // N  (Afterburner, game-boosted for a higher top speed)
        public float clAlpha = 4.0f;         // 1/rad (LERX 와류양력 포함)
        public float clMax = 1.85f;
        public float cd0 = 0.021f;
        public float inducedK = 0.12f;
        public float speedBrakeCd = 0.26f;

        [Header("FBW limits")]
        public float maxG = 11.5f;
        public float minG = -8f;            // push-over authority (nose-down as quick as nose-up)
        public float maxAoA = 30f;           // deg
        public float maxRollRate = 340f;     // deg/s
        public float maxPitchRate = 62f;     // deg/s (low/medium speed pitch-rate command)
        /// <summary>Limiter off + thrust vectoring (held by the pilot): post-stall manoeuvres such as the Cobra below ~800 km/h.</summary>
        [System.NonSerialized] public bool postStall;
        public const float PostStallMaxSpeed = 230f;
        public bool PostStallActive => postStall && !onGround && Airspeed < PostStallMaxSpeed;
        public float fullEffectivenessQ = 2800f; // Pa — dynamic pressure where control surfaces reach full bite (~210 kt at sea level)
        public float maxYawRate = 10f;       // deg/s
        public float pitchTau = 0.11f, rollTau = 0.09f, yawTau = 0.25f;

        [Header("Pilot inputs (-1..1, throttle 0..1)")]
        public float pitchInput;    // + = 스틱 당김(기수 상승)
        public float rollInput;     // + = 우측 롤
        public float yawInput;      // + = 우측 러더
        [Range(0, 1)] public float throttle = 0.75f; // 0~0.8 = IDLE~MIL, 0.8~1 = A/B
        public bool speedBrake;
        [System.NonSerialized] public bool onGround;   // set by LandingGear (weight on wheels)
        [System.NonSerialized] public float flaps;     // 0..1 — F-16 flaperons/LEF droop automatically with the gear handle down
        /// <summary>0..1 control-surface effectiveness (dynamic pressure). Low = mushy, high = crisp.</summary>
        public float ControlEffectiveness { get; private set; }

        /// <summary>When true the autoland moves the jet itself (exact path); aero forces and FBW are suspended,
        /// telemetry (speed, AoA, attitude, fuel) keeps updating.</summary>
        [System.NonSerialized] public bool ExternalControl;

        // ---- autopilot: direct load-factor command (bypasses the stick shaping so the autoland can fly precise paths) ----
        float apG = float.NaN, apGTime = -1f;
        /// <summary>Command a load factor (G along the lift axis) for this frame. Expires after 0.2 s if not refreshed.</summary>
        public void SetAutopilotG(float g) { apG = g; apGTime = Time.time; }
        public void ClearAutopilot() { apG = float.NaN; }
        bool AutopilotPitch => !float.IsNaN(apG) && Time.time - apGTime < 0.2f;

        public const float MilDetent = 0.8f;

        // Telemetry
        public float Airspeed { get; private set; }
        public float Mach { get; private set; }
        public float AoA { get; private set; }
        public float Sideslip { get; private set; }
        public float GLoad { get; private set; } = 1f;
        public float Altitude => transform.position.y;
        public float VerticalSpeed { get; private set; }
        public float Heading { get; private set; }
        public float Pitch { get; private set; }
        public float Bank { get; private set; }
        public float ThrustNewtons { get; private set; }
        public bool Afterburner => throttle > MilDetent + 0.01f;
        public float EngineRpm { get; private set; } = 0.8f;   // spool 0..1
        public bool Destroyed { get; set; }
        public Rigidbody Body { get; private set; }

        Vector3 lastVel;
        float smoothedG = 1f;

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            mass = dryMass + Fuel;
            Body.mass = mass;
            Body.useGravity = true;
            Body.linearDamping = 0f;
            Body.angularDamping = 0f;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Body.maxAngularVelocity = 20f;
            Body.inertiaTensor = new Vector3(60000f, 80000f, 15000f);
            Body.inertiaTensorRotation = Quaternion.identity;
        }

        public void SetEngineInstant(float t) { throttle = t; EngineRpm = t; }

        public void Launch(Vector3 velocity)
        {
            Body.linearVelocity = velocity;
            lastVel = velocity;
        }

        void FixedUpdate()
        {
            if (Destroyed) return;
            float dt = Time.fixedDeltaTime;
            Vector3 vel = Body.linearVelocity;
            float V = vel.magnitude;
            Vector3 lv = transform.InverseTransformDirection(vel);

            // --- Atmosphere ---
            float alt = Mathf.Max(0f, transform.position.y);
            float rho = 1.225f * Mathf.Exp(-alt / 8500f);
            float sos = Mathf.Lerp(340.3f, 295.1f, Mathf.Clamp01(alt / 11000f));
            Mach = V / sos;
            float qbar = 0.5f * rho * V * V;

            // --- Angles ---
            float alpha = V > 1f ? Mathf.Atan2(-lv.y, lv.z) : 0f;
            float beta = V > 1f ? Mathf.Atan2(lv.x, lv.z) : 0f;
            AoA = alpha * Mathf.Rad2Deg;
            Sideslip = beta * Mathf.Rad2Deg;

            // --- Aerodynamic coefficients ---
            float cl = LiftCoefficient(alpha);
            float groundEffect = 0f;
            if (!onGround && Physics.Raycast(transform.position, Vector3.down, out RaycastHit geHit, 12f, ~0, QueryTriggerInteraction.Ignore))
                groundEffect = 1f - geHit.distance / 12f;   // 0 at one wingspan, 1 at the surface
            if (Mathf.Abs(alpha) < clMax / clAlpha) cl += 0.28f * flaps;     // flaps: more lift at the same AoA
            if (onGround && speedBrake) cl -= 0.6f;                          // lift dump on the rollout → weight onto the wheels
            float cdWave = 0f;
            if (Mach > 0.85f) cdWave = 0.024f * Mathf.Clamp01((Mach - 0.85f) / 0.2f) - 0.008f * Mathf.Clamp01((Mach - 1.2f) / 0.6f);
            cl *= 1f + 0.15f * groundEffect;
            float cd = cd0 + cdWave + inducedK * (1f - 0.4f * groundEffect) * cl * cl + (speedBrake ? speedBrakeCd : 0f) + 0.025f * flaps + 0.6f * Mathf.Abs(Mathf.Sin(beta));
            float stallA = clMax / clAlpha;
            if (Mathf.Abs(alpha) > stallA) { float sa = Mathf.Sin(Mathf.Abs(alpha)); cd += 1.25f * sa * sa; }   // belly into the wind: huge drag (Cobra brakes the jet)

            Vector3 force = Vector3.zero;
            if (V > 0.5f)
            {
                Vector3 vDir = vel / V;
                Vector3 liftDir = Vector3.Cross(vDir, transform.right).normalized;
                force += liftDir * (cl * qbar * wingArea);
                force += -vDir * (cd * qbar * wingArea);
                force += transform.right * (-1.0f * beta * qbar * wingArea * 0.5f);
            }

            // --- Engine (spool lag + thrust lapse with density) ---
            float spoolRate = throttle > EngineRpm ? 0.35f : 0.6f;
            EngineRpm = Mathf.MoveTowards(EngineRpm, Flameout ? 0f : throttle, (Flameout ? 0.25f : spoolRate) * dt);
            // fuel burn: idle 0.12 kg/s, MIL ≈1.4 kg/s, full afterburner ≈7 kg/s (real F110 ≈ 1.2 / 7.5)
            FuelFlow = Flameout ? 0f : (EngineRpm <= MilDetent ? Mathf.Lerp(0.12f, 1.4f, EngineRpm / MilDetent) : Mathf.Lerp(1.4f, 7f, (EngineRpm - MilDetent) / (1f - MilDetent)));
            if (!infiniteFuel) Fuel = Mathf.Max(0f, Fuel - FuelFlow * dt);
            mass = dryMass + (infiniteFuel ? maxFuel * 0.7f : Fuel);
            Body.mass = mass;
            float t = EngineRpm;
            float thrust = t <= MilDetent
                ? Mathf.Lerp(0.06f * milThrust, milThrust, t / MilDetent)
                : Mathf.Lerp(milThrust, abThrust, (t - MilDetent) / (1f - MilDetent));
            thrust *= Mathf.Pow(rho / 1.225f, 0.75f) * (1f + 0.2f * Mathf.Clamp(Mach, 0f, 1.6f));
            if (Flameout) thrust = 0f;
            ThrustNewtons = thrust;
            force += transform.forward * thrust;

            // --- Forces + fly-by-wire (skipped while the autoland flies the jet along its computed path) ---
            if (!ExternalControl)
            {
                Body.AddForce(force);
                FlyByWire(dt, V, qbar, alpha);
            }

            // --- Telemetry ---
            Vector3 accel = (vel - lastVel) / dt;
            lastVel = vel;
            Vector3 specific = accel - Physics.gravity;
            float nz = Vector3.Dot(specific, transform.up) / Units.G;
            smoothedG = Mathf.Lerp(smoothedG, nz, 1f - Mathf.Exp(-dt / 0.08f));
            GLoad = smoothedG;
            Airspeed = V;
            VerticalSpeed = vel.y;
            Vector3 f = transform.forward;
            Heading = (Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + 360f) % 360f;
            Pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 flatRight = Vector3.Cross(Vector3.up, f);
            if (flatRight.sqrMagnitude < 1e-4f) flatRight = transform.right;
            flatRight.Normalize();
            Vector3 flatUp = Vector3.Cross(f, flatRight);
            Bank = Mathf.Atan2(Vector3.Dot(transform.right, -flatUp), Vector3.Dot(transform.right, flatRight)) * Mathf.Rad2Deg;
        }

        float LiftCoefficient(float alpha)
        {
            float a = Mathf.Abs(alpha);
            float stall = clMax / clAlpha;  // ~26°
            float c;
            if (a <= stall) c = clAlpha * a;
            else c = Mathf.Lerp(clMax, 0.9f, Mathf.Clamp01((a - stall) / 0.6f)) * Mathf.Cos(Mathf.Clamp(a - stall, 0f, 1.2f));
            return Mathf.Sign(alpha) * c;
        }

        float CurrentLiftG(float alpha, float qbar) =>
            (LiftCoefficient(alpha) + (Mathf.Abs(alpha) < clMax / clAlpha ? 0.28f * flaps : 0f)) * qbar * wingArea / (mass * Units.G);

        /// <summary>Body pitch rate that drives the aircraft toward load factor gCmd (respecting the AoA limit).</summary>
        float PitchRateForG(float gCmd, float qbar, float Veff, float alpha, float gravityOnLiftAxis)
        {
            float alphaMax = maxAoA * Mathf.Deg2Rad;
            float clReq = gCmd * mass * Units.G / Mathf.Max(qbar * wingArea, 1f) - 0.28f * flaps;
            float alphaReq = Mathf.Clamp(clReq / clAlpha, -alphaMax * 0.8f, alphaMax);
            float gAch = CurrentLiftG(alphaReq, qbar);
            float gUsed = gCmd >= 0f ? Mathf.Min(gCmd, gAch) : Mathf.Max(gCmd, gAch);
            return Units.G * (gUsed - gravityOnLiftAxis) / Veff + 3.5f * (alphaReq - alpha);
        }

        void FlyByWire(float dt, float V, float qbar, float alpha)
        {
            float eff = Mathf.Clamp01(qbar / fullEffectivenessQ);      // control effectiveness ∝ ½ρV²
            ControlEffectiveness = eff;
            float p = Mathf.Clamp(pitchInput, -1f, 1f);

            if (onGround)
            {
                // Weight-on-wheels: direct mode. The stabilator moment grows with dynamic pressure, so the
                // nose wheel only lifts once there is enough airspeed (~110 kt starts, ~140-150 kt rotates easily).
                Vector3 wl = transform.InverseTransformDirection(Body.angularVelocity);
                float torque = -p * qbar * 95f - wl.x * 80000f;   // nose wheel lifts from ~85 kt, rotates easily at 130 kt
                Body.AddRelativeTorque(new Vector3(torque, 0f, -wl.z * 60000f));
                return;
            }
            float Veff = Mathf.Max(V, 40f);
            float lowSpeed = Mathf.Clamp01(qbar / 2500f);
            float alphaMax = maxAoA * Mathf.Deg2Rad;
            float gravityOnLiftAxis = Vector3.Dot(Vector3.up, transform.up);

            // ---- Post-stall (limiter off + thrust vectoring): direct pitch-rate command up to ~120°/s, AoA past 90° ----
            if (PostStallActive)
            {
                float vecAuth = Mathf.Lerp(0.6f, 1f, eff) * Mathf.Clamp01(0.4f + EngineRpm);   // nozzle vectoring needs thrust
                float pathNowPS = Units.G * (CurrentLiftG(alpha, qbar) - gravityOnLiftAxis) / Veff;
                float qPS = Mathf.Abs(p) > 0.12f
                    ? p * 125f * Mathf.Deg2Rad * vecAuth
                    : pathNowPS + 3.5f * (0.17f - alpha);                     // stick released: nose drops back onto the flight path
                if (alpha > 100f * Mathf.Deg2Rad) qPS = Mathf.Min(qPS, 0f);  // don't flip over backwards
                float rPS = Mathf.Clamp(yawInput, -1f, 1f) * 45f * Mathf.Deg2Rad * vecAuth;
                float pRatePS = Mathf.Clamp(rollInput, -1f, 1f) * maxRollRate * 0.6f * Mathf.Deg2Rad * Mathf.Lerp(0.3f, 1f, eff);
                Vector3 tgt = new Vector3(-qPS, rPS, -pRatePS);
                Vector3 wps = transform.InverseTransformDirection(Body.angularVelocity);
                wps.x = Mathf.Lerp(wps.x, tgt.x, 1f - Mathf.Exp(-dt / 0.12f));
                wps.y = Mathf.Lerp(wps.y, tgt.y, 1f - Mathf.Exp(-dt / 0.2f));
                wps.z = Mathf.Lerp(wps.z, tgt.z, 1f - Mathf.Exp(-dt / rollTau));
                Body.angularVelocity = transform.TransformDirection(wps);
                ControlEffectiveness = Mathf.Max(eff, vecAuth);
                return;
            }

            // ---- Pitch: F-16 style blend ----
            //  • low / medium speed → pitch-RATE command, scaled by control effectiveness (nose points where you pull,
            //    but slowly when you're slow)
            //  • high speed → G command (the same stick gives up to 9 G; pitch rate then falls as speed rises)
            //  • AoA limiter 25° and G limiter always on top
            float q;
            if (AutopilotPitch) q = PitchRateForG(Mathf.Clamp(apG, minG, maxG), qbar, Veff, alpha, gravityOnLiftAxis);
            else
            {
                float qG = PitchRateForG(p >= 0f ? 1f + p * (maxG - 1f) : 1f + p * (1f - minG), qbar, Veff, alpha, gravityOnLiftAxis);
                float qRate = p * maxPitchRate * Mathf.Deg2Rad * Mathf.Lerp(0.7f, 1f, eff);
                q = p > 0f ? Mathf.Max(qG, qRate) : p < 0f ? Mathf.Min(qG, qRate) : qG;   // neutral stick = hold 1 G
            }
            // G limiter: never exceed what full stick would command
            float qLimUp = PitchRateForG(maxG, qbar, Veff, alpha, gravityOnLiftAxis);
            float qLimDn = PitchRateForG(minG, qbar, Veff, alpha, gravityOnLiftAxis);
            q = Mathf.Clamp(q, qLimDn, Mathf.Max(qLimUp, 0f));
            // AoA limiter: as alpha approaches 25° the nose may only follow the flight path
            float pathNow = Units.G * (CurrentLiftG(alpha, qbar) - gravityOnLiftAxis) / Veff;
            if (alpha > alphaMax * 0.85f) q = Mathf.Min(q, pathNow + 4f * (alphaMax - alpha));
            if (alpha < -alphaMax * 0.8f) q = Mathf.Max(q, pathNow + 4f * (-alphaMax * 0.8f - alpha));
            float maxQ = maxPitchRate * Mathf.Deg2Rad * Mathf.Lerp(0.8f, 1.15f, eff);
            q = Mathf.Clamp(q, -maxQ, maxQ);
            // very low energy: the nose falls through toward the velocity vector
            q = Mathf.Lerp(-alpha * 1.5f + pathNow, q, lowSpeed);
            float authority = eff;

            // Roll: rate command, reduced at low speed and very high AoA
            float aoaFactor = Mathf.Lerp(1f, 0.45f, Mathf.Clamp01((Mathf.Abs(alpha) * Mathf.Rad2Deg - 15f) / 10f));
            float pRate = Mathf.Clamp(rollInput, -1f, 1f) * maxRollRate * Mathf.Deg2Rad * Mathf.Lerp(0.15f, 1f, authority) * aoaFactor;

            // Yaw: rudder rate command (+ turn coordination handled by side force)
            float r = Mathf.Clamp(yawInput, -1f, 1f) * maxYawRate * Mathf.Deg2Rad * Mathf.Lerp(0.3f, 1f, authority);

            // Unity left-handed axes: +x rotation = nose down, +z rotation = left roll
            Vector3 target = new Vector3(-q, r, -pRate);
            Vector3 w = transform.InverseTransformDirection(Body.angularVelocity);
            float tauP = pitchTau / Mathf.Lerp(0.35f, 1f, eff);   // slow → sluggish response, fast → crisp
            w.x = Mathf.Lerp(w.x, target.x, 1f - Mathf.Exp(-dt / tauP));
            w.y = Mathf.Lerp(w.y, target.y, 1f - Mathf.Exp(-dt / yawTau));
            w.z = Mathf.Lerp(w.z, target.z, 1f - Mathf.Exp(-dt / rollTau));
            Body.angularVelocity = transform.TransformDirection(w);
        }
    }
}
