using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 자동 착륙 — 어디서 켜든 공항까지 자동으로 날아가서 활주로 중심선에 정확히 내린다.
    /// 오토파일럿이 켜져 있는 동안은 기체를 계산된 경로 위로 직접 움직인다 (비행 모델 튜닝·조종 반응과 무관하게 항상 같은 결과).
    ///   1) NAV   : 목표 공항(지도 NAV로 고른 공항, 없으면 가장 가까운 공항)의 최종 진입점(FAF, 활주로 연장선 5 km)으로 직행
    ///              고도 = 활주로 + 300 m 이상 (앞쪽 지형 + 450 m), 너무 높으면 바깥으로 돌며 강하
    ///   2) FINAL : 기어/플랩 내림, 활주로 중심선 + 3° 글라이드패스를 정확히 추종, 220→160 kt 감속
    ///   3) FLARE : 12 m부터 침하율을 줄이며 접지
    ///   4) ROLLOUT : 접지 순간 물리로 복귀 → 스피드브레이크 + 최대 제동 + 중심선 유지 → 정지 후 주차 브레이크
    /// L 키 / 조종석 AUTOLAND 버튼으로 켜고 끔. 스틱을 크게 움직이면 즉시 해제(그 자리에서 수동 비행).
    /// </summary>
    [DefaultExecutionOrder(100)]   // after PlayerController so it can override the pilot's inputs
    public class Autoland : MonoBehaviour
    {
        public FlightModel fm;
        public LandingGear gear;
        public bool Active { get; private set; }
        public string Status { get; private set; } = "";

        enum Phase { Nav, Express, Final, Rollout, Taxi }
        Phase phase;
        WorldBuilder.Airport ap;
        float dirSign;
        bool holding;
        float bankNow;
        float lastTrack;
        bool flying;   // we are moving the jet (ExternalControl)
        float autoThrottle;
        int taxiStage;   // 0 = to the hold point before the threshold, 1 = onto the runway centreline, 2 = align & stop

        const float FafDist = 5000f;         // final approach fix: 5 km before touchdown on the extended centreline
        const float NavAltAgl = 300f;        // ≈ glidepath height at the FAF
        const float NavSpeed = 300f;         // m/s (~580 kt) — get there as fast as possible
        const float AppSpeed = 100f;         // m/s (~195 kt) — fast approach

        // ------------------------------------------------------------------ engage / disengage
        public bool TryEngage()
        {
            if (Active) { Disengage("AUTOLAND OFF"); return false; }
            var gm = GameManager.Instance;
            if (fm.onGround || gear.OnGround) return StartTaxi(gm);
            PickNearestRunway();                          // always the quickest runway to reach from here
            if (gm != null) gm.NavTarget = ap;            // HUD steering cue points at the same field
            phase = BuildExpressPath() ? Phase.Express : Phase.Nav;   // fly a planned curve straight onto short final
            holding = false;
            bankNow = fm.Bank;
            lastTrack = TrackOf(fm.Body.linearVelocity);
            Active = true;
            TakeControl();
            Flash($"AUTOLAND → {ap.name} RWY {ap.RunwayName(dirSign)}");
            return true;
        }

        // ------------------------------------------------------------------ ground: auto-taxi onto the runway
        /// <summary>On the ground: drive (steering, thrust, brakes) to the nearest runway end and line up on the centreline.</summary>
        bool StartTaxi(GameManager gm)
        {
            ap = WorldBuilder.Nearest(transform.position);
            if (gm != null) gm.NavTarget = ap;
            Vector3 pos = transform.position;
            float d1 = Vector3.Distance(pos, ap.Threshold(1f)), d2 = Vector3.Distance(pos, ap.Threshold(-1f));
            dirSign = d1 <= d2 ? 1f : -1f;                                   // nearest end, take off away from it
            // already on the runway and roughly lined up? → just line up where we are
            Track(pos, out float along, out float lateral);
            Vector3 v = fm.Body.linearVelocity; v.y = 0f;
            taxiStage = Mathf.Abs(lateral) < 20f && Vector3.Angle(transform.forward, L) < 60f && along < 300f && along > -2600f ? 2 : 0;
            phase = Phase.Taxi;
            gear.parkingBrake = false;
            Active = true;
            Flash($"AUTO TAXI → {ap.name} RWY {ap.RunwayName(dirSign)}");
            return true;
        }

        Vector3 HoldPoint => ap.Threshold(dirSign) - L * 120f;              // on the extended centreline, short of the threshold
        Vector3 LineUpPoint => ap.Threshold(dirSign) + L * 80f;             // on the runway centreline

        void TaxiStep(float dt)
        {
            var rb = fm.Body;
            Vector3 pos = rb.position;
            Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 target = taxiStage == 0 ? HoldPoint : LineUpPoint;
            Vector3 to = target - pos; to.y = 0f;
            float dist = to.magnitude;
            float wantHdg = taxiStage == 2 || dist < 3f ? Course : TrackOf(to);
            float hdg = TrackOf(fwd);
            float err = Mathf.DeltaAngle(hdg, wantHdg);

            float speed;
            if (taxiStage == 2)
            {
                speed = 0f;
                if (Mathf.Abs(err) < 1.5f && rb.linearVelocity.magnitude < 0.5f)
                {
                    gear.parkingBrake = true;
                    autoThrottle = 0f;
                    Active = false; Status = "";
                    Flash($"RWY {ap.RunwayName(dirSign)} — LINED UP, READY FOR TAKEOFF (throttle up to go)");
                    return;
                }
            }
            else
            {
                speed = Mathf.Min(22f, dist * 0.3f + 2f);
                if (Mathf.Abs(err) > 40f) speed = Mathf.Min(speed, 5f);       // slow down for sharp turns
                if (dist < 12f) { taxiStage++; }
            }

            // drive: yaw toward the wanted heading, velocity along the nose (gear springs keep us on the ground)
            float yawRate = Mathf.Clamp(err * 1.2f, -25f, 25f) * (taxiStage == 2 ? 0.6f : 1f);
            if (taxiStage == 2) yawRate = Mathf.Clamp(err * 1.5f, -15f, 15f);   // pivot in place onto the runway heading
            Vector3 v = rb.linearVelocity;
            float cur = Vector3.Dot(v, fwd);
            float newSpeed = Mathf.MoveTowards(cur, speed, (speed > cur ? 4f : 7f) * dt);   // thrust / brakes
            rb.linearVelocity = fwd * newSpeed + Vector3.up * v.y;
            rb.angularVelocity = new Vector3(rb.angularVelocity.x, yawRate * Mathf.Deg2Rad, rb.angularVelocity.z);

            autoThrottle = newSpeed < speed - 0.5f ? 0.3f : 0.12f;
            gear.brake = newSpeed > speed + 0.5f || speed < 0.1f ? 1f : 0f;
            gear.steerInput = Mathf.Clamp(err / 30f, -1f, 1f);
            Status = taxiStage == 2 ? $"AUTO TAXI — LINING UP RWY {ap.RunwayName(dirSign)}" : $"AUTO TAXI → RWY {ap.RunwayName(dirSign)}  {dist:0} M";
        }

        public void Disengage(string why)
        {
            if (!Active) return;
            Active = false;
            Release();
            Status = "";
            fm.speedBrake = false;
            Flash(why);
        }

        void TakeControl()
        {
            flying = true;
            fm.ExternalControl = true;
            fm.Body.useGravity = false;
        }

        void Release()
        {
            if (!flying) return;
            flying = false;
            fm.ExternalControl = false;
            fm.Body.useGravity = true;
            fm.Body.angularVelocity = Vector3.zero;
        }

        void OnDisable() { if (Active) { Active = false; Release(); } }

        void Flash(string s) { if (GameManager.Instance && !string.IsNullOrEmpty(s)) GameManager.Instance.Banner(s, 3f); }

        // ------------------------------------------------------------------ geometry
        Vector3 L => ap.Dir * dirSign;                                         // landing direction (horizontal)
        Vector3 Right => Vector3.Cross(Vector3.up, L);
        Vector3 Aim => ap.Threshold(dirSign) + L * 300f;                       // touchdown point (runway surface)
        Vector3 Faf => Aim - L * FafDist;
        float Course => Mathf.Atan2(L.x, L.z) * Mathf.Rad2Deg;
        float RunwayY => ap.SurfaceY(Aim.x, Aim.z) + 2.0f;                                  // aircraft origin height with the wheels on the runway (surface +0.15)
        static float Tan3 => Mathf.Tan(3f * Mathf.Deg2Rad);
        static float TrackOf(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

        /// <summary>Of every runway end on the island, the one that is quickest to reach (distance + turning).</summary>
        void PickNearestRunway()
        {
            Vector3 pos = transform.position;
            Vector3 v = fm.Body.linearVelocity; v.y = 0f;
            Vector3 fwd = v.sqrMagnitude > 1f ? v.normalized : Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float best = float.MaxValue;
            WorldBuilder.Airport bestAp = null; float bestSign = 1f;
            foreach (var a in WorldBuilder.Airports)
            {
                ap = a;
                for (float sgn = -1f; sgn <= 1f; sgn += 2f)
                {
                    float c = Cost(sgn, pos, fwd);
                    if (c < best) { best = c; bestAp = a; bestSign = sgn; }
                }
            }
            ap = bestAp; dirSign = bestSign;
        }

        void PickRunwayEnd()
        {
            Vector3 pos = transform.position;
            Vector3 v = fm.Body.linearVelocity; v.y = 0f;
            Vector3 fwd = v.sqrMagnitude > 1f ? v.normalized : Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            dirSign = Cost(1f, pos, fwd) <= Cost(-1f, pos, fwd) ? 1f : -1f;
        }

        float Cost(float s, Vector3 pos, Vector3 fwd)
        {
            Vector3 l = ap.Dir * s;
            Vector3 faf = ap.Threshold(s) + l * 300f - l * FafDist;
            Vector3 to = faf - pos; to.y = 0f;
            float turnToFaf = to.sqrMagnitude > 1f ? Vector3.Angle(fwd, to) : 0f;
            float finalTurn = Vector3.Angle(to.sqrMagnitude > 1f ? to.normalized : fwd, l);
            return to.magnitude + (turnToFaf + finalTurn) * 35f;
        }

        void Track(Vector3 pos, out float along, out float lateral)
        {
            Vector3 rel = pos - Aim; rel.y = 0f;
            along = -Vector3.Dot(rel, L);                                      // metres before the touchdown point
            lateral = Vector3.Dot(rel, Right);                                 // + = right of the centreline
        }

        // ------------------------------------------------------------------ express approach: one smooth curve to short final
        // A cubic Bézier from where we are (leaving along our current flight path) to a gate 450 m before touchdown on the
        // 3° glidepath (arriving exactly on the centreline). Flown kinematically at up to Mach 2 with hard braking at the
        // end, lifted over any terrain on the way → most landings take 20–30 s from anywhere near an airfield.
        const float GateDist = 450f, GateSpeed = 150f, ExpressMax = 700f, ExpressAcc = 70f, ExpressLatG = 250f;
        Vector3 b0, b1, b2, b3;
        float[] arcLen; float[] arcCurv;
        const int ArcN = 256;
        float pathS, pathV;

        Vector3 Bez(float u)
        {
            float a = 1f - u;
            return a * a * a * b0 + 3f * a * a * u * b1 + 3f * a * u * u * b2 + u * u * u * b3;
        }
        Vector3 BezD(float u)
        {
            float a = 1f - u;
            return 3f * a * a * (b1 - b0) + 6f * a * u * (b2 - b1) + 3f * u * u * (b3 - b2);
        }

        bool BuildExpressPath()
        {
            Vector3 pos = transform.position;
            Vector3 v = fm.Body.linearVelocity;
            Vector3 vd = v.sqrMagnitude > 25f ? v.normalized : transform.forward;
            Vector3 gd = (L * Mathf.Cos(3f * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(3f * Mathf.Deg2Rad)).normalized;
            Vector3 gate = Aim - L * GateDist; gate.y = RunwayY + GateDist * Tan3;
            float D = Vector3.Distance(gate, pos);
            if (D < 900f) return false;                                        // already close: the normal approach logic
            b0 = pos; b3 = gate;
            b1 = pos + vd * Mathf.Clamp(D * 0.2f, 300f, 4000f);
            b2 = gate - gd * Mathf.Clamp(D * 0.3f, 600f, 5000f);
            // keep 180 m above the ground the whole way (lift the middle of the curve where needed)
            for (int it = 0; it < 6; it++)
            {
                float need = 0f;
                for (int k = 2; k < 40; k++)
                {
                    float u = k / 40f;
                    Vector3 p = Bez(u);
                    if (u > 0.93f) continue;                                    // short final: corridor is cleared
                    need = Mathf.Max(need, GameManager.TerrainHeight(p.x, p.z) + 180f - p.y);
                }
                if (need <= 0f) break;
                b1.y += need * 1.5f; b2.y += need * 1.5f;
            }
            // arc length + curvature tables
            arcLen = new float[ArcN + 1]; arcCurv = new float[ArcN + 1];
            Vector3 prev = b0;
            for (int i = 0; i <= ArcN; i++)
            {
                float u = i / (float)ArcN;
                Vector3 p = Bez(u);
                arcLen[i] = i == 0 ? 0f : arcLen[i - 1] + Vector3.Distance(prev, p);
                prev = p;
                float a = 1f - u;
                Vector3 d1 = BezD(u), d2 = 6f * a * (b2 - 2f * b1 + b0) + 6f * u * (b3 - 2f * b2 + b1);
                float m = Mathf.Max(d1.magnitude, 1e-3f);
                arcCurv[i] = Vector3.Cross(d1, d2).magnitude / (m * m * m);
            }
            pathS = 0f;
            pathV = Mathf.Max(v.magnitude, 90f);
            return true;
        }

        float ArcParam(float s, out int idx)
        {
            int lo = 0, hi = ArcN;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (arcLen[mid] < s) lo = mid; else hi = mid; }
            idx = lo;
            float seg = Mathf.Max(arcLen[hi] - arcLen[lo], 1e-3f);
            return (lo + Mathf.Clamp01((s - arcLen[lo]) / seg)) / ArcN;
        }

        void ExpressStep(float dt)
        {
            var rb = fm.Body;
            float total = arcLen[ArcN];
            float u = ArcParam(pathS, out int idx);
            float remain = total - pathS;
            float allow = Mathf.Min(ExpressMax, Mathf.Sqrt(GateSpeed * GateSpeed + 2f * ExpressAcc * remain), Mathf.Sqrt(ExpressLatG / Mathf.Max(arcCurv[idx], 1e-6f)));
            pathV = Mathf.Min(pathV + ExpressAcc * dt, allow);
            Vector3 tan = BezD(u).normalized;
            rb.position = Bez(u);
            Vector3 nv = tan * pathV;
            rb.linearVelocity = nv;
            Attitude(nv, 2f, 85f, dt, 140f);
            pathS += pathV * dt;

            float secs = remain / Mathf.Max(pathV, 1f);
            if (remain < 6000f && !gear.Down) gear.ToggleGear();
            autoThrottle = pathV < allow - 5f ? 1f : remain < 2500f ? 0.3f : 0.85f;
            fm.speedBrake = remain < 3000f;
            Status = $"AUTOLAND → {ap.name} RWY {ap.RunwayName(dirSign)}  {Mathf.Ceil(secs + GateDist / 120f + 2f):0} S";
            if (pathS >= total)
            {
                rb.position = b3;
                rb.linearVelocity = (L * Mathf.Cos(3f * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(3f * Mathf.Deg2Rad)).normalized * pathV;
                phase = Phase.Final;
                Flash($"AUTOLAND: SHORT FINAL RWY {ap.RunwayName(dirSign)}");
            }
        }

        void Attitude(Vector3 nv, float aoa, float maxBank, float dt, float bankRate)
        {
            var rb = fm.Body;
            float track = TrackOf(nv);
            float turnRate = Mathf.DeltaAngle(lastTrack, track) / dt * Mathf.Deg2Rad;
            lastTrack = track;
            float Vh = new Vector2(nv.x, nv.z).magnitude;
            float wantBank = Mathf.Clamp(Mathf.Atan2(Vh * turnRate, Units.G) * Mathf.Rad2Deg, -maxBank, maxBank);
            bankNow = Mathf.MoveTowards(bankNow, wantBank, bankRate * dt);
            Quaternion rot = Quaternion.LookRotation(nv.normalized, Vector3.up) * Quaternion.Euler(-aoa, 0f, -bankNow);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, rot, 1f - Mathf.Exp(-dt / 0.15f)));
            rb.angularVelocity = Vector3.zero;
        }

        // ------------------------------------------------------------------ path flying (physics step)
        void FixedUpdate()
        {
            if (!Active || fm == null) return;
            if (fm.Destroyed) { Active = false; Release(); Status = ""; return; }
            if (phase == Phase.Taxi) { TaxiStep(Time.fixedDeltaTime); return; }
            if (phase == Phase.Express) { ExpressStep(Time.fixedDeltaTime); return; }
            if (phase == Phase.Rollout) return;

            var rb = fm.Body;
            float dt = Time.fixedDeltaTime;
            Vector3 pos = rb.position;
            Vector3 v = rb.linearVelocity;
            float V = Mathf.Max(v.magnitude, 50f);
            Track(pos, out float along, out float lateral);
            float height = pos.y - RunwayY;

            // ---- touchdown: hand the jet back to the physics (wheels, brakes) ----
            if (phase == Phase.Final && (gear.OnGround || height < 0.25f))
            {
                Release();
                rb.linearVelocity = L * Vector3.Dot(v, L) + Vector3.down * 0.6f;   // straight down the centreline
                phase = Phase.Rollout;
                return;
            }

            Vector3 wantDir; float wantSpeed, maxTurnDeg, aoa, maxBank;
            if (phase == Phase.Nav) NavPath(pos, along, lateral, out wantDir, out wantSpeed, out maxTurnDeg, out aoa, out maxBank);
            else FinalPath(pos, along, lateral, height, V, out wantDir, out wantSpeed, out maxTurnDeg, out aoa, out maxBank);

            // steer heading and flight-path angle separately (rate-limited like a real turn / pull), change speed smoothly
            Vector3 cur = v.sqrMagnitude > 1f ? v.normalized : transform.forward;
            float psi = Mathf.Atan2(cur.x, cur.z), gam = Mathf.Asin(Mathf.Clamp(cur.y, -1f, 1f));
            float psiD = Mathf.Atan2(wantDir.x, wantDir.z), gamD = Mathf.Asin(Mathf.Clamp(wantDir.y, -1f, 1f));
            psi += Mathf.Clamp(Mathf.DeltaAngle(psi * Mathf.Rad2Deg, psiD * Mathf.Rad2Deg), -maxTurnDeg * dt, maxTurnDeg * dt) * Mathf.Deg2Rad;
            gam += Mathf.Clamp(gamD - gam, -6f * Mathf.Deg2Rad * dt, 6f * Mathf.Deg2Rad * dt);
            float newSpeed = Mathf.MoveTowards(v.magnitude, wantSpeed, (wantSpeed > v.magnitude ? 12f : 24f) * dt);   // full power / speed brakes
            Vector3 nv = new Vector3(Mathf.Cos(gam) * Mathf.Sin(psi), Mathf.Sin(gam), Mathf.Cos(gam) * Mathf.Cos(psi)) * newSpeed;
            rb.linearVelocity = nv;

            // attitude: nose along the flight path + angle of attack, bank matching the turn rate
            float track = TrackOf(nv);
            float turnRate = Mathf.DeltaAngle(lastTrack, track) / dt * Mathf.Deg2Rad;   // rad/s, + = right
            lastTrack = track;
            float Vh = new Vector2(nv.x, nv.z).magnitude;
            float wantBank = Mathf.Clamp(Mathf.Atan2(Vh * turnRate, Units.G) * Mathf.Rad2Deg, -maxBank, maxBank);
            bankNow = Mathf.MoveTowards(bankNow, wantBank, 25f * dt);
            Quaternion rot = Quaternion.LookRotation(nv.normalized, Vector3.up) * Quaternion.Euler(-aoa, 0f, -bankNow);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, rot, 1f - Mathf.Exp(-dt / 0.25f)));
            rb.angularVelocity = Vector3.zero;
        }

        void NavPath(Vector3 pos, float along, float lateral, out Vector3 wantDir, out float wantSpeed, out float maxTurn, out float aoa, out float maxBank)
        {
            Vector3 to = Faf - pos; to.y = 0f;
            float dist = to.magnitude;
            float aboveRwy = pos.y - RunwayY;
            bool tooHigh = aboveRwy > NavAltAgl + 450f;
            if (dist < 3500f && tooHigh) holding = true;                       // over the FAF but high: go outbound, descend, come back
            if (holding && aboveRwy < NavAltAgl + 150f) holding = false;

            // horizontal: to a point on the extended centreline (further out when we're off to the side → we arrive lined up)
            Vector3 lead = Faf - L * Mathf.Clamp(Mathf.Abs(lateral) * 0.5f, 0f, 4000f);
            Vector3 h = holding ? -L : (lead - pos);
            h.y = 0f; h = h.sqrMagnitude > 1f ? h.normalized : L;

            // vertical: approach altitude, never closer than 450 m to the terrain ahead
            Vector3 vh = fm.Body.linearVelocity; vh.y = 0f;
            float terrain = 0f;
            for (int i = 0; i <= 6; i++)
            {
                Vector3 p = pos + vh * (i * 5f);
                terrain = Mathf.Max(terrain, GameManager.TerrainHeight(p.x, p.z));
            }
            float wantAlt = Mathf.Max(RunwayY + NavAltAgl, terrain + 450f);
            float gamma = Mathf.Clamp((wantAlt - pos.y) * 0.005f, -0.36f, 0.30f);      // ≤ 20° dive / 17° climb
            wantDir = (h * Mathf.Cos(gamma) + Vector3.up * Mathf.Sin(gamma)).normalized;
            // full speed until the last moment, then speed brakes so the jet reaches the FAF at approach speed
            wantSpeed = holding ? 150f : Mathf.Min(NavSpeed, Mathf.Sqrt(150f * 150f + 2f * 13f * Mathf.Max(0f, dist - 600f)));
            maxTurn = 14f; aoa = 3f; maxBank = 70f;

            if (gear.Down && fm.Airspeed > 110f) gear.ToggleGear();
            float Vnow = fm.Body.linearVelocity.magnitude;
            autoThrottle = Vnow < NavSpeed - 5f || gamma > 0.05f ? 1f : gamma < -0.1f ? 0.45f : 0.8f;   // afterburner to accelerate / climb
            fm.speedBrake = Vnow > wantSpeed + 10f || gamma < -0.18f;
            Status = holding ? $"AUTOLAND  DESCENDING — {aboveRwy:0} M" : $"AUTOLAND → {ap.name} RWY {ap.RunwayName(dirSign)}  {dist / 1000f:0.0} KM TO FINAL";

            // capture the final approach once at the FAF, or earlier if already lined up in front of the runway
            Vector3 vel = fm.Body.linearVelocity; vel.y = 0f;
            float hdgOff = Vector3.Angle(vel, L);
            float gsH = Mathf.Max(0f, along) * Tan3;
            if (!holding && along > 3000f && aboveRwy - gsH < 350f && ((dist < 1500f && hdgOff < 90f) || (Mathf.Abs(lateral) < along * 0.25f && hdgOff < 60f)))
            {
                phase = Phase.Final;
                if (!gear.Down) gear.ToggleGear();
                Flash($"AUTOLAND: FINAL APPROACH RWY {ap.RunwayName(dirSign)}");
            }
        }

        void FinalPath(Vector3 pos, float along, float lateral, float height, float V, out Vector3 wantDir, out float wantSpeed, out float maxTurn, out float aoa, out float maxBank)
        {
            if (!gear.Down) gear.ToggleGear();
            wantSpeed = along > 1500f ? Mathf.Lerp(AppSpeed, 150f, Mathf.InverseLerp(1500f, 4000f, along)) : AppSpeed;
            if (along > 4000f) wantSpeed = Mathf.Min(NavSpeed, Mathf.Sqrt(150f * 150f + 2f * 13f * (along - 4000f)));   // long final: fast until the last 4 km
            if (along < 1000f) wantSpeed = Mathf.Min(V, Mathf.Max(AppSpeed, Mathf.Sqrt(AppSpeed * AppSpeed + 2f * 20f * Mathf.Max(0f, along - 150f))));   // short final from the express gate: brake hard, never speed up
            float gsH = Mathf.Max(0f, along) * Tan3;

            // lost the runway (should not happen) → go around
            if (along < -1500f || (along < 1500f && height - gsH > 80f))
            {
                phase = Phase.Nav; holding = true; PickRunwayEnd();
                Flash("AUTOLAND: GO AROUND");
                wantDir = (fm.Body.linearVelocity.normalized + Vector3.up * 0.2f).normalized;
                maxTurn = 5f; aoa = 6f; maxBank = 30f;
                return;
            }

            if (height > 12f)
            {
                // pure pursuit of a carrot point on the centreline / 3° glidepath ahead of us → converges exactly onto the path
                float look = Mathf.Max(500f, V * 5f);
                float cAlong = along - look;
                Vector3 carrot = Aim - L * cAlong;
                carrot.y = RunwayY + Mathf.Max(0f, cAlong) * Tan3;
                // never below the terrain (+60 m) on the way in
                if (cAlong > 1200f) carrot.y = Mathf.Max(carrot.y, GameManager.TerrainHeight(carrot.x, carrot.z) + 60f);
                wantDir = (carrot - pos).normalized;
                // don't dive more than 8° to rejoin the glidepath
                float minSin = -Mathf.Sin(8f * Mathf.Deg2Rad);
                if (wantDir.y < minSin) { wantDir.y = minSin; wantDir = wantDir.normalized; }
                maxTurn = 7f;
                aoa = Mathf.Lerp(3f, 6f, Mathf.InverseLerp(150f, AppSpeed, V));
                maxBank = height < 60f ? 10f : 30f;
                Status = $"AUTOLAND {ap.name} RWY {ap.RunwayName(dirSign)}  {Mathf.Max(0f, along) / 1852f:0.0} NM";
            }
            else
            {
                // flare: straight down the centreline, sink rate easing from ~4 m/s to 0.6 m/s
                float sink = Mathf.Lerp(0.6f, 3.8f, height / 12f);
                Vector3 h = (L - Right * Mathf.Clamp(lateral * 0.02f, -0.05f, 0.05f)).normalized;
                wantDir = (h * V - Vector3.up * sink).normalized;
                maxTurn = 8f; aoa = Mathf.Lerp(6f, 5f, height / 12f); maxBank = 3f;   // modest pitch: no tail strike
                wantSpeed = AppSpeed - 4f;   // ~150 kt over the threshold
                Status = "AUTOLAND  FLARE";
            }

            autoThrottle = height < 12f ? 0f : (V > wantSpeed + 3f ? 0.3f : V < wantSpeed - 3f ? (wantSpeed > 150f ? 1f : 0.75f) : wantSpeed > 150f ? 0.85f : 0.55f);
            fm.speedBrake = V > wantSpeed + 6f;
        }

        // ------------------------------------------------------------------ frame update: pilot override, rollout
        void LateUpdate()
        {
            if (!Active || fm == null) return;

            // the pilot takes over by moving the stick firmly (PlayerController wrote this frame's inputs)
            if (phase != Phase.Rollout && (Mathf.Abs(fm.pitchInput) > 0.6f || Mathf.Abs(fm.rollInput) > 0.6f)) { Disengage("AUTOLAND DISCONNECT"); return; }

            if (phase == Phase.Taxi)
            {
                fm.pitchInput = 0f; fm.rollInput = 0f; fm.yawInput = gear.steerInput;
                fm.throttle = autoThrottle; fm.speedBrake = false;
                return;
            }
            if (phase != Phase.Rollout)
            {
                fm.throttle = autoThrottle;                                    // auto-throttle (shown on the HUD / engine sound)
                // neutral controls while we fly the path (control surfaces animate from these)
                fm.pitchInput = 0f; fm.rollInput = Mathf.Clamp(bankNow / 45f, -1f, 1f) * 0.2f; fm.yawInput = 0f;
                gear.brake = 0f;
                return;
            }

            // ---- rollout (physics): centreline, idle, speed brake / lift dump, max wheel brakes ----
            Track(transform.position, out _, out float lateral);
            float latRate = Vector3.Dot(fm.Body.linearVelocity, Right);
            fm.Body.AddForce(Right * Mathf.Clamp(-0.1f * lateral - 1.0f * latRate, -3f, 3f), ForceMode.Acceleration);
            fm.pitchInput = -0.2f;
            fm.rollInput = 0f;
            float hdgErr = Mathf.DeltaAngle(fm.Heading, Course);
            fm.yawInput = Mathf.Clamp(hdgErr * 0.08f - lateral * 0.04f, -1f, 1f);
            gear.steerInput = fm.yawInput;
            fm.throttle = 0f;
            fm.speedBrake = true;
            gear.brake = 1f;
            Status = "AUTOLAND  ROLLOUT";
            if (fm.Body.linearVelocity.magnitude < 3f) { gear.parkingBrake = true; Disengage("LANDED — AUTOLAND OFF"); }
        }
    }
}
