using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace AirFight
{
    /// <summary>
    /// 맨손(핸드 트래킹) 조종 시스템 — 컨트롤러 없이 Galaxy XR 손 추적만으로 조종.
    ///
    ///  오른손 (사이드스틱)
    ///   - 스틱 위에서 주먹 쥐기(중지·약지·새끼) = 스틱 잡기 / 손 펴기 = 놓기 (스프링으로 중립 복귀)
    ///   - 잡은 채 손을 앞/뒤/좌/우로 기울이기 = 피치/롤
    ///   - 잡은 채 손목 비틀기 = 러더 (트위스트 그립)
    ///   - 검지 당기기 = 기관포 트리거
    ///   - 엄지를 세웠다가 검지 옆으로 꾹 누르기 = 피클 버튼(미사일 발사)
    ///  왼손 (스로틀)
    ///   - 스로틀 위에서 주먹 쥐기 = 잡기, 앞뒤로 밀고 당기기 = 추력 (MIL 디텐트 → 더 밀면 애프터버너)
    ///   - 잡은 채 엄지 누르기(유지) = 스피드 브레이크
    ///  조종석 버튼 (검지 끝으로 누르기): A-A GUN / A-A MSL / MASTER ARM / RECENTER / FLARE / RESTART
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class VRCockpitInput : MonoBehaviour
    {
        [Header("Rig")]
        public Transform trackingSpace;
        public Transform head;
        public Transform stickPivot, throttleHandle;
        public Vector3 eyePointLocal;

        [Header("Stick")]
        public float stickMaxAngle = 16f;        // virtual stick angle for full deflection
        public float stickVirtualLength = 0.2f;  // m (≈ 5.5 cm hand travel for full deflection)
        public float stickDeadzone = 0.06f;
        [Range(0, 1)] public float stickExpo = 0.45f;
        public float twistForFullRudder = 25f;   // deg of wrist twist
        public float grabRadius = 0.11f;

        [Header("Throttle")]
        public float throttleTravel = 0.16f;
        public float milDetentWidth = 0.015f;

        // ----- Outputs read by PlayerController -----
        public float Pitch { get; private set; }
        public float Roll { get; private set; }
        public float Rudder { get; private set; }
        public float Throttle { get; set; } = 0.75f;
        public bool StickGrabbed { get; private set; }
        public bool ThrottleGrabbed { get; private set; }
        public bool Trigger { get; private set; }
        public bool PickleDown { get; private set; }
        public bool SelectGunDown { get; private set; }
        public bool SelectMslDown { get; private set; }
        public bool FlaresDown { get; private set; }
        public bool RestartDown { get; private set; }
        public bool SpeedBrake { get; private set; }
        public bool MasterArm { get; private set; } = true;
        public bool HeadTracked { get; private set; }
        public bool HandsTracked => leftHand.Tracked || rightHand.Tracked;
        public float BuffetAmount { get; set; }   // visual stick shake

        // state for button lamps
        public System.Func<WeaponMode> currentMode;
        public System.Func<bool> isDead;
        public System.Func<float> gearDeploy;
        public bool GearToggleDown { get; private set; }

        readonly TrackedHand leftHand = new TrackedHand(Handedness.Left);
        readonly TrackedHand rightHand = new TrackedHand(Handedness.Right);
        readonly List<CockpitButton> buttons = new List<CockpitButton>();
        readonly List<Vector3> pokeTips = new List<Vector3>();
        XRHandSubsystem handSub;
        InputDevice hmd;
        Transform cockpit;
        Vector3 stickGrabPalm, throttleGrabPalm;
        Vector3 stickGrabFwd;
        float throttleGrabValue;
        float stickGrabTime;
        float recenterTimer = 1.0f;
        bool recentered;

        // ----- View (1st person cockpit / 3rd person chase) -----
        [Header("Third-person chase view")]
        public float chaseDistance = 24f;
        public float chaseHeight = 5f;
        public bool ThirdPerson { get; private set; }
        /// <summary>Called with the chase anchor when switching to 3rd person, or null when back in the cockpit.</summary>
        public System.Action<Transform> OnViewChanged;
        Quaternion recRot = Quaternion.identity;   // recenter yaw correction
        Vector3 recPos;                            // recenter head offset
        Transform chaseAnchor;
        Quaternion chaseRot = Quaternion.identity;
        bool pendingView, pendingMap;
        public bool MapToggleDown { get; private set; }
        public bool AutolandDown { get; private set; }
        public bool MultiToggleDown { get; private set; }
        public bool MenuDown { get; private set; }
        bool pendingMulti, pendingMenu;
        public System.Func<bool> autolandActive;
        bool pendingAutoland;

        /// <summary>Real (not cockpit-mapped) index fingertip positions in world space — for floating panels like the map.</summary>
        public void GetFingertipsWorld(List<Vector3> list)
        {
            if (leftHand.Tracked) list.Add(trackingSpace.TransformPoint(leftHand.IndexTip));
            if (rightHand.Tracked) list.Add(trackingSpace.TransformPoint(rightHand.IndexTip));
        }
        bool pendingGun, pendingMsl, pendingFlare, pendingRestart, pendingGear;

        public void Init(Transform cockpitRoot, Camera cam)
        {
            cockpit = cockpitRoot;
            leftHand.BuildVisual(trackingSpace);
            rightHand.BuildVisual(trackingSpace);
            BuildButtons(cam);
            RequestHandPermission();
        }

        void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs) s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
        }

        void OnDisable() { Application.onBeforeRender -= OnBeforeRender; }

        static void RequestHandPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string perm = "android.permission.HAND_TRACKING";
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm))
                UnityEngine.Android.Permission.RequestUserPermission(perm);
#endif
        }

        // ------------------------------------------------------------ buttons
        void BuildButtons(Camera cam)
        {
            Vector3 E = eyePointLocal;
            // Up-front control panel (ICP area) just under the glareshield
            Build.Prim(PrimitiveType.Cube, cockpit, E + new Vector3(0, -0.3f, 0.555f), new Vector3(0.3f, 0.075f, 0.02f), new Color(0.1f, 0.1f, 0.11f));
            var gun = CockpitButton.Create(cockpit, E + new Vector3(-0.105f, -0.3f, 0.543f), Quaternion.identity, "A-A GUN", new Color(0.3f, 1f, 0.4f), cam);
            var msl = CockpitButton.Create(cockpit, E + new Vector3(-0.05f, -0.3f, 0.543f), Quaternion.identity, "A-A MSL", new Color(0.3f, 1f, 0.4f), cam);
            var arm = CockpitButton.Create(cockpit, E + new Vector3(0.015f, -0.3f, 0.543f), Quaternion.identity, "MASTER ARM", new Color(1f, 0.75f, 0.2f), cam);
            var ctr = CockpitButton.Create(cockpit, E + new Vector3(0.075f, -0.3f, 0.543f), Quaternion.identity, "RECENTER", new Color(0.4f, 0.7f, 1f), cam);
            var rst = CockpitButton.Create(cockpit, E + new Vector3(0.125f, -0.3f, 0.543f), Quaternion.identity, "RESTART", new Color(1f, 0.3f, 0.3f), cam);
            // Flare dispense button on the left cockpit wall, next to the throttle
            var flr = CockpitButton.Create(cockpit, E + new Vector3(-0.47f, -0.36f, 0.3f), Quaternion.Euler(0, -90, 0), "FLARE", new Color(1f, 0.55f, 0.2f), cam);

            // Landing gear handle on the left wall, forward of the flare button (lamp: green = down & locked, red = in transit)
            var gear = CockpitButton.Create(cockpit, E + new Vector3(-0.47f, -0.34f, 0.46f), Quaternion.Euler(0, -90, 0), "GEAR", new Color(0.3f, 1f, 0.4f), cam);
            gear.Pressed += () => pendingGear = true;
            gear.isLit = () => gearDeploy != null && gearDeploy() > 0.999f;
            buttons.Add(gear);
            gun.Pressed += () => pendingGun = true;
            msl.Pressed += () => pendingMsl = true;
            arm.Pressed += () => MasterArm = !MasterArm;
            ctr.Pressed += Recenter;
            rst.Pressed += () => pendingRestart = true;
            flr.Pressed += () => pendingFlare = true;
            gun.isLit = () => currentMode != null && currentMode() == WeaponMode.Gun;
            msl.isLit = () => currentMode != null && currentMode() == WeaponMode.AAM;
            arm.isLit = () => MasterArm;
            rst.isLit = () => isDead != null && isDead();
            flr.isLit = () => false;
            ctr.isLit = () => false;
            buttons.AddRange(new[] { gun, msl, arm, ctr, rst, flr });
            // View switch on the right cockpit wall (1st ↔ 3rd person)
            var view = CockpitButton.Create(cockpit, E + new Vector3(0.47f, -0.36f, 0.38f), Quaternion.Euler(0, 90, 0), "VIEW", new Color(0.4f, 0.7f, 1f), cam);
            var al = CockpitButton.Create(cockpit, E + new Vector3(-0.47f, -0.36f, 0.14f), Quaternion.Euler(0, -90, 0), "AUTOLAND", new Color(0.3f, 1f, 0.4f), cam);
            al.Pressed += () => pendingAutoland = true;
            al.isLit = () => autolandActive != null && autolandActive();
            buttons.Add(al);
            var multi = CockpitButton.Create(cockpit, E + new Vector3(0.47f, -0.36f, 0.16f), Quaternion.Euler(0, 90, 0), "MULTI", new Color(0.4f, 0.7f, 1f), cam);
            multi.Pressed += () => pendingMulti = true;
            multi.isLit = () => NetSession.I != null && NetSession.I.Active;
            buttons.Add(multi);
            var map = CockpitButton.Create(cockpit, E + new Vector3(0.47f, -0.36f, 0.27f), Quaternion.Euler(0, 90, 0), "MAP", new Color(1f, 0.85f, 0.2f), cam);
            map.Pressed += () => pendingMap = true;
            map.isLit = () => false;
            buttons.Add(map);
            var menu = CockpitButton.Create(cockpit, E + new Vector3(0.47f, -0.36f, 0.05f), Quaternion.Euler(0, 90, 0), "MENU", new Color(1f, 0.85f, 0.2f), cam);
            menu.Pressed += () => pendingMenu = true;
            menu.isLit = () => false;
            buttons.Add(menu);
            view.Pressed += () => pendingView = true;
            view.isLit = () => ThirdPerson;
            buttons.Add(view);
        }

        // ------------------------------------------------------------ tracking
        void OnBeforeRender() { UpdateHead(); }

        void UpdateHead()
        {
            if (head == null) return;
            if (!hmd.isValid) hmd = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!hmd.isValid) hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.devicePosition, out var hp) && hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out var hr))
            {
                head.localPosition = hp; head.localRotation = hr; HeadTracked = true;
            }
        }

        public void Recenter()
        {
            if (!hmd.isValid) return;
            if (!hmd.TryGetFeatureValue(CommonUsages.devicePosition, out var hp) || !hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out var hr)) return;
            recRot = Quaternion.Euler(0f, -hr.eulerAngles.y, 0f);
            recPos = -(recRot * hp);
            if (!ThirdPerson) { trackingSpace.localRotation = recRot; trackingSpace.localPosition = eyePointLocal + recPos; }
            recentered = true;
        }

        public void ToggleView() => SetThirdPerson(!ThirdPerson);

        public void SetThirdPerson(bool on)
        {
            if (on == ThirdPerson) return;
            ThirdPerson = on;
            if (chaseAnchor == null) chaseAnchor = new GameObject("ChaseAnchor").transform;
            if (on)
            {
                trackingSpace.SetParent(null, true);
                chaseRot = TargetChaseRotation();
                UpdateChase(1f);
            }
            else
            {
                trackingSpace.SetParent(cockpit, false);
                trackingSpace.localRotation = recRot;
                trackingSpace.localPosition = eyePointLocal + recPos;
            }
            OnViewChanged?.Invoke(on ? chaseAnchor : null);
        }

        Quaternion TargetChaseRotation()
        {
            // In VR keep the horizon level (yaw only) — rolling the world around the player causes motion sickness.
            if (XRSettings.isDeviceActive)
            {
                Vector3 f = cockpit.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) { f = cockpit.up * -Mathf.Sign(cockpit.forward.y); f.y = 0f; }
                return f.sqrMagnitude < 1e-4f ? chaseRot : Quaternion.LookRotation(f.normalized, Vector3.up);
            }
            return cockpit.rotation;
        }

        void UpdateChase(float k)
        {
            chaseRot = Quaternion.Slerp(chaseRot, TargetChaseRotation(), k);
            Vector3 pos = cockpit.position + chaseRot * new Vector3(0f, chaseHeight, -chaseDistance);
            chaseAnchor.SetPositionAndRotation(pos, chaseRot);
            trackingSpace.SetPositionAndRotation(pos + chaseRot * recPos, chaseRot * recRot);
        }

        // ------------------------------------------------------------ death camera
        Transform deathFocus;
        Vector3 deathLastPos;
        float deathYaw, deathT;
        public bool DeathCam => deathFocus != null || deathT > 0f;

        /// <summary>Shot down: watch your jet crumple, burn and fall from outside (orbiting on desktop, level and steady in VR).</summary>
        public void StartDeathCam(Transform focus)
        {
            deathFocus = focus;
            deathT = 0.001f;
            deathLastPos = focus.position;
            Vector3 f = cockpit.forward; f.y = 0f;
            deathYaw = f.sqrMagnitude > 1e-3f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + 150f : 0f;
            trackingSpace.SetParent(null, true);
            UpdateDeathCam(1f);
        }

        public void StopDeathCam()
        {
            if (!DeathCam) return;
            deathFocus = null; deathT = 0f;
            if (ThirdPerson) { trackingSpace.SetParent(null, true); UpdateChase(1f); }
            else
            {
                trackingSpace.SetParent(cockpit, false);
                trackingSpace.localRotation = recRot;
                trackingSpace.localPosition = eyePointLocal + recPos;
            }
        }

        void UpdateDeathCam(float k)
        {
            if (deathFocus != null) deathLastPos = deathFocus.position;
            Vector3 target = deathLastPos;
            bool xr = XRSettings.isDeviceActive;
            if (!xr) deathYaw += 18f * Time.unscaledDeltaTime;                 // slow orbit
            float dist = Mathf.Lerp(28f, 60f, Mathf.Clamp01(deathT / 4f));
            Quaternion yaw = Quaternion.Euler(0f, deathYaw, 0f);
            Vector3 want = target + yaw * new Vector3(0f, 0f, -dist) + Vector3.up * Mathf.Lerp(8f, 22f, Mathf.Clamp01(deathT / 4f));
            float ground = GameManager.TerrainHeight(want.x, want.z) + 6f;
            if (want.y < ground) want.y = ground;
            Vector3 pos = Vector3.Lerp(trackingSpace.position - (trackingSpace.rotation * recPos), want, k);
            Vector3 look = target - pos;
            Quaternion rot = xr ? Quaternion.LookRotation(new Vector3(look.x, 0f, look.z).sqrMagnitude > 1e-3f ? new Vector3(look.x, 0f, look.z) : Vector3.forward, Vector3.up)
                                : Quaternion.LookRotation(look.sqrMagnitude > 1e-3f ? look : Vector3.forward, Vector3.up);
            trackingSpace.SetPositionAndRotation(pos + rot * recPos, rot * recRot);
        }

        void LateUpdate()
        {
            if (DeathCam) { deathT += Time.unscaledDeltaTime; UpdateDeathCam(1f - Mathf.Exp(-Time.unscaledDeltaTime * 2.5f)); return; }
            if (ThirdPerson && cockpit != null) UpdateChase(1f - Mathf.Exp(-Time.deltaTime * 3f));
        }

        void Update()
        {
            if (cockpit == null || head == null) return;
            float dt = Time.deltaTime;
            UpdateHead();
            if (!recentered && HeadTracked)
            {
                recenterTimer -= dt;
                if (recenterTimer <= 0f) Recenter();
            }

            if (handSub == null || !handSub.running) handSub = HandSubsystemLocator.Find();
            leftHand.Update(handSub, dt);
            rightHand.Update(handSub, dt);

            UpdateStick(dt);
            UpdateThrottle();
            UpdateButtons();

            leftHand.Tint(ThrottleGrabbed ? new Color(0.55f, 0.9f, 0.6f) : new Color(0.85f, 0.7f, 0.6f));
            rightHand.Tint(StickGrabbed ? (Trigger ? new Color(1f, 0.6f, 0.3f) : new Color(0.55f, 0.9f, 0.6f)) : new Color(0.85f, 0.7f, 0.6f));
        }

        // Hands are always mapped into the *virtual seated pose*, so the stick, throttle and switches stay
        // exactly where your hands remember them even while you watch from the 3rd-person chase camera.
        Vector3 ToCockpit(Vector3 trackingLocal) => eyePointLocal + recPos + recRot * trackingLocal;
        Vector3 DirToCockpit(Vector3 trackingDir) => recRot * trackingDir;

        // ------------------------------------------------------------ stick (right hand)
        void UpdateStick(float dt)
        {
            var h = rightHand;
            Vector3 gripCentre = cockpit.InverseTransformPoint(stickPivot.parent.TransformPoint(stickPivot.localPosition + Vector3.up * 0.13f));

            if (!StickGrabbed && h.Tracked && h.Fist && !h.Busy)
            {
                Vector3 palm = ToCockpit(h.Palm);
                if (Vector3.Distance(palm, gripCentre) < grabRadius)
                {
                    StickGrabbed = true; h.Busy = true;
                    stickGrabPalm = palm;
                    stickGrabFwd = Flat(DirToCockpit(h.HandForward));
                    stickGrabTime = Time.time;
                }
            }
            // release: open hand, or tracking lost for a while
            if (StickGrabbed && ((h.Tracked && !h.Fist) || h.LostTime > 0.35f)) { StickGrabbed = false; h.Busy = false; }

            Trigger = false; PickleDown = false; Rudder = 0f;
            if (StickGrabbed)
            {
                if (h.Tracked)
                {
                    Vector3 d = ToCockpit(h.Palm) - stickGrabPalm;
                    Pitch = Shape(Mathf.Atan2(-d.z, stickVirtualLength) * Mathf.Rad2Deg / stickMaxAngle);   // pull back = nose up
                    Roll = Shape(Mathf.Atan2(d.x, stickVirtualLength) * Mathf.Rad2Deg / stickMaxAngle);     // right = roll right
                    float twist = Vector3.SignedAngle(stickGrabFwd, Flat(DirToCockpit(h.HandForward)), Vector3.up);
                    Rudder = Shape(twist / twistForFullRudder);
                }
                Trigger = h.IndexPulled && MasterArm;
                PickleDown = h.ThumbPressedDown && MasterArm && Time.time - stickGrabTime > 0.4f;
            }
            else
            {
                Pitch = Mathf.MoveTowards(Pitch, 0f, dt * 4f);
                Roll = Mathf.MoveTowards(Roll, 0f, dt * 4f);
            }
            float shake = BuffetAmount > 0.05f ? (Mathf.PerlinNoise(Time.time * 25f, 0f) - 0.5f) * BuffetAmount * 4f : 0f;
            stickPivot.localRotation = Quaternion.Euler(-Pitch * 14f + shake, Rudder * 8f, -Roll * 14f + shake);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        float Shape(float x)
        {
            x = Mathf.Clamp(x, -1f, 1f);
            float a = Mathf.Abs(x);
            if (a < stickDeadzone) return 0f;
            a = (a - stickDeadzone) / (1f - stickDeadzone);
            a = (1f - stickExpo) * a + stickExpo * a * a * a;
            return Mathf.Sign(x) * a;
        }

        // ------------------------------------------------------------ throttle (left hand)
        float gateLo => FlightModel.MilDetent;
        float gateHi => FlightModel.MilDetent + milDetentWidth / throttleTravel;

        float FromRaw(float raw)
        {
            raw *= 1f + (gateHi - gateLo);
            if (raw <= gateLo) return raw;
            if (raw <= gateHi) return gateLo;          // resting in the MIL detent
            return Mathf.Min(1f, gateLo + (raw - gateHi));
        }

        float ToRaw(float thr)
        {
            float raw = thr <= gateLo ? thr : thr + (gateHi - gateLo);
            return raw / (1f + (gateHi - gateLo));
        }

        void UpdateThrottle()
        {
            var h = leftHand;
            Vector3 handle = cockpit.InverseTransformPoint(throttleHandle.TransformPoint(new Vector3(0, 0.1f, 0)));
            if (!ThrottleGrabbed && h.Tracked && h.Fist && !h.Busy)
            {
                Vector3 palm = ToCockpit(h.Palm);
                if (Vector3.Distance(palm, handle) < grabRadius)
                {
                    ThrottleGrabbed = true; h.Busy = true;
                    throttleGrabPalm = palm;
                    throttleGrabValue = ToRaw(Throttle);
                }
            }
            if (ThrottleGrabbed && ((h.Tracked && !h.Fist) || h.LostTime > 0.35f)) { ThrottleGrabbed = false; h.Busy = false; }

            SpeedBrake = false;
            if (ThrottleGrabbed && h.Tracked)
            {
                float dz = ToCockpit(h.Palm).z - throttleGrabPalm.z;
                Throttle = FromRaw(Mathf.Clamp01(throttleGrabValue + dz / throttleTravel));
                SpeedBrake = h.ThumbPressed;
            }
            throttleHandle.localPosition = new Vector3(0f, 0f, Mathf.Lerp(-0.08f, 0.08f, Throttle));
        }

        // ------------------------------------------------------------ poke buttons
        void UpdateButtons()
        {
            pokeTips.Clear();
            if (leftHand.Tracked && !leftHand.Busy) pokeTips.Add(cockpit.TransformPoint(ToCockpit(leftHand.IndexTip)));
            if (rightHand.Tracked && !rightHand.Busy) pokeTips.Add(cockpit.TransformPoint(ToCockpit(rightHand.IndexTip)));
            pendingGun = pendingMsl = pendingFlare = pendingRestart = pendingGear = false;
            if (MainMenu.AnyOpen) pokeTips.Clear();   // the hands are working the main menu, not the cockpit
            foreach (var b in buttons) b.Poke(pokeTips);
            SelectGunDown = pendingGun;
            SelectMslDown = pendingMsl;
            FlaresDown = pendingFlare;
            RestartDown = pendingRestart;
            GearToggleDown = pendingGear;
            if (pendingView) { pendingView = false; ToggleView(); }
            MapToggleDown = pendingMap; pendingMap = false;
            AutolandDown = pendingAutoland; pendingAutoland = false;
            MultiToggleDown = pendingMulti; pendingMulti = false;
            MenuDown = pendingMenu; pendingMenu = false;
        }
    }
}
