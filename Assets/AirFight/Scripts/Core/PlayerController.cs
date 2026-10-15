using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.XR;

namespace AirFight
{
    /// <summary>
    /// 입력 통합: VR 가상 HOTAS(기본) + 실물 USB/블루투스 조이스틱(HOTAS) + 게임패드 + 키보드(에디터 테스트용).
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        public FlightModel fm;
        public Weapons weapons;
        public Damageable self;
        public VRCockpitInput vr;
        public AudioSynth audioSynth;
        public Transform cameraT;
        public LandingGear gear;
        public Autoland autoland;

        float kbPitch, kbRoll;
        float mouseYaw, mousePitch;

        void Update()
        {
            if (fm == null) return;
            if (MainMenu.AnyOpen || MainMenu.ClosedFrame == Time.frameCount)
            {
                // parked while the pilot picks a mode
                weapons.triggerHeld = false;
                fm.pitchInput = fm.rollInput = fm.yawInput = 0f;
                if (gear) { gear.brake = 1f; gear.steerInput = 0f; }
                UpdateAudio();
                return;
            }
            var map = GameManager.Instance ? GameManager.Instance.Map : null;
            if (map && (map.IsOpen || map.ClosedFrame == Time.frameCount)) { weapons.triggerHeld = false; return; }
            var np = GameManager.Instance ? GameManager.Instance.NetPanelRef : null;
            if (np && (np.IsOpen || np.ClosedFrame == Time.frameCount)) { weapons.triggerHeld = false; return; }
            if (ControlsWindow.AnyOpen || ControlsWindow.ClosedFrame == Time.frameCount) { weapons.triggerHeld = false; return; }
            float dt = Time.deltaTime;
            var kb = Keyboard.current;
            var gp = Gamepad.current;
            var js = Joystick.current;

            // ---------------- Restart ----------------
            if (self.Dead)
            {
                if ((vr && vr.RestartDown) || KeyBindings.Down(Act.Respawn) || (gp != null && gp.startButton.wasPressedThisFrame))
                    GameManager.Instance.Respawn();
                fm.pitchInput = fm.rollInput = fm.yawInput = 0f;
                weapons.triggerHeld = false;
                UpdateAudio();
                return;
            }

            // ---------------- Stick ----------------
            float pitch = 0f, roll = 0f, yaw = 0f;
            if (vr && (vr.StickGrabbed || Mathf.Abs(vr.Pitch) > 0.001f || Mathf.Abs(vr.Roll) > 0.001f)) { pitch = vr.Pitch; roll = vr.Roll; }

            if (js != null)
            {
                Vector2 s = js.stick.ReadValue();
                if (s.magnitude > 0.05f) { pitch = -s.y; roll = s.x; }
                var twist = js.TryGetChildControl<AxisControl>("twist") ?? js.TryGetChildControl<AxisControl>("rz");
                if (twist != null && Mathf.Abs(twist.ReadValue()) > 0.1f) yaw += twist.ReadValue();
            }
            if (gp != null)
            {
                Vector2 ls = gp.leftStick.ReadValue();
                if (ls.magnitude > 0.1f) { pitch = -ls.y; roll = ls.x; }
                yaw += gp.rightStick.ReadValue().x;
            }
            if (kb != null)
            {
                float tp = KeyBindings.Axis(Act.PitchDown, Act.PitchUp);       // 기본: W = 기수 올림, S = 기수 내림 (조작 설정에서 변경)
                if (KeyBindings.InvertPitch) tp = -tp;
                float tr = KeyBindings.Axis(Act.RollLeft, Act.RollRight);
                kbPitch = Mathf.MoveTowards(kbPitch, tp, dt * 3f);
                kbRoll = Mathf.MoveTowards(kbRoll, tr, dt * 4f);
                if (Mathf.Abs(kbPitch) > 0.01f) pitch = kbPitch;
                if (Mathf.Abs(kbRoll) > 0.01f) roll = kbRoll;
                yaw += KeyBindings.Axis(Act.YawLeft, Act.YawRight);
            }
            if (vr) yaw += vr.Rudder;

            fm.pitchInput = Mathf.Clamp(pitch, -1f, 1f);
            // post-stall / Cobra: hold X (gamepad: left stick click) → AoA limiter off + thrust vectoring below ~800 km/h
            bool ps = KeyBindings.Held(Act.PostStall) || (gp != null && gp.leftStickButton.isPressed);
            if (ps && !fm.postStall && GameManager.Instance)
                GameManager.Instance.Banner(fm.Airspeed < FlightModel.PostStallMaxSpeed ? "LIMITER OFF — POST-STALL (PULL FOR COBRA)" : "POST-STALL: SLOW BELOW 800 KM/H", 2f);
            fm.postStall = ps;
            fm.rollInput = Mathf.Clamp(roll, -1f, 1f);
            fm.yawInput = Mathf.Clamp(yaw, -1f, 1f);

            // ---------------- Throttle ----------------
            float thr = fm.throttle;
            if (vr && vr.ThrottleGrabbed) thr = vr.Throttle;
            else
            {
                if (kb != null)
                {
                    if (KeyBindings.Held(Act.ThrottleUp)) thr += dt * 0.35f;
                    if (KeyBindings.Held(Act.ThrottleDown)) thr -= dt * 0.35f;
                }
                if (gp != null)
                {
                    if (gp.rightShoulder.isPressed) thr += dt * 0.35f;
                    if (gp.leftShoulder.isPressed) thr -= dt * 0.35f;
                }
                if (js != null)
                {
                    var t = js.TryGetChildControl<AxisControl>("throttle") ?? js.TryGetChildControl<AxisControl>("z") ?? js.TryGetChildControl<AxisControl>("slider");
                    if (t != null && t.IsActuated(0.01f)) thr = Mathf.Clamp01(1f - (t.ReadValue() + 1f) * 0.5f);
                }
                thr = Mathf.Clamp01(thr);
                if (vr) vr.Throttle = thr;
            }
            fm.throttle = thr;

            // ---------------- Weapons (HOTAS switchology) ----------------
            bool trigger = (vr && vr.StickGrabbed && vr.Trigger)
                           || (KeyBindings.Held(Act.FireGun) && !XRSettings.isDeviceActive)
                           || (KeyBindings.Held(Act.FireSelected) && weapons.mode == WeaponMode.Gun)   // SPACE = selected weapon
                           || (gp != null && gp.rightTrigger.ReadValue() > 0.5f)
                           || (js != null && js.trigger.isPressed);
            weapons.triggerHeld = trigger;

            bool pickle = (vr && vr.PickleDown) || (gp != null && gp.buttonSouth.wasPressedThisFrame);
            if (pickle && weapons.mode == WeaponMode.AAM) weapons.FireMissile();
            // keyboard: SPACE fires the selected weapon (GUN: hold to fire, AAM: press to launch); F always launches an AIM-9
            if ((KeyBindings.Down(Act.FireSelected) && weapons.mode == WeaponMode.AAM) || KeyBindings.Down(Act.FireMissile)) weapons.FireMissile();

            bool select = KeyBindings.Down(Act.WeaponSelect) || (gp != null && gp.buttonNorth.wasPressedThisFrame);
            if (select) weapons.CycleMode();
            if (vr && vr.SelectGunDown) weapons.mode = WeaponMode.Gun;
            if (vr && vr.SelectMslDown) weapons.mode = WeaponMode.AAM;

            bool flare = (vr && vr.FlaresDown) || KeyBindings.Down(Act.Flares) || (gp != null && gp.buttonWest.wasPressedThisFrame);
            if (flare) weapons.DropFlares();

            bool brakeInput = (vr && vr.SpeedBrake) || KeyBindings.Held(Act.SpeedBrake) || (gp != null && gp.buttonEast.isPressed);
            fm.speedBrake = brakeInput;
            if (gear)
            {
                // on the ground the speed-brake switch also works the wheel brakes; rudder steers the nose wheel
                gear.brake = brakeInput ? 1f : 0f;
                gear.steerInput = fm.yawInput;
                bool gearToggle = (vr && vr.GearToggleDown) || KeyBindings.Down(Act.Gear) || (gp != null && gp.dpad.down.wasPressedThisFrame);
                if (gearToggle) gear.ToggleGear();
            }

            if (KeyBindings.Down(Act.Recenter) && vr) vr.Recenter();
            if (autoland && (KeyBindings.Down(Act.Autoland) || (vr && vr.AutolandDown) || (gp != null && gp.dpad.up.wasPressedThisFrame))) autoland.TryEngage();
            if (vr && (KeyBindings.Down(Act.View) || (gp != null && gp.selectButton.wasPressedThisFrame))) vr.ToggleView();

            // ---------------- Feedback ----------------
            if (vr)
            {
                float buffet = Mathf.Clamp01((fm.GLoad - 6.5f) / 2.5f) + Mathf.Clamp01((fm.AoA - 18f) / 8f);
                vr.BuffetAmount = buffet;
            }
            UpdateAudio();
            DesktopLook();
        }

        void UpdateAudio()
        {
            if (!audioSynth) return;
            audioSynth.engine = self.Dead ? 0f : fm.EngineRpm;
            audioSynth.afterburner = (!self.Dead && fm.Afterburner) ? Mathf.InverseLerp(FlightModel.MilDetent, 1f, fm.EngineRpm) : 0f;
            audioSynth.wind = self.Dead ? 0f : Mathf.Clamp01(fm.Airspeed / 450f);
            audioSynth.gun = weapons.Firing && !self.Dead;
            audioSynth.seekerTone = (weapons.missiles <= 0 || self.Dead) ? 0 : weapons.Locked ? 2 : weapons.mode == WeaponMode.AAM ? 1 : 0;
        }

        // Editor/desktop: hold right mouse to look around the cockpit.
        void DesktopLook()
        {
            if (XRSettings.isDeviceActive || cameraT == null) return;
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (KeyBindings.Held(Act.LookAround))
            {
                Vector2 d = mouse.delta.ReadValue() * 0.15f * KeyBindings.LookSensitivity;
                mouseYaw = Mathf.Clamp(mouseYaw + d.x, -160f, 160f);
                mousePitch = Mathf.Clamp(mousePitch - d.y, -80f, 80f);
            }
            else
            {
                mouseYaw = Mathf.MoveTowards(mouseYaw, 0f, Time.deltaTime * 120f);
                mousePitch = Mathf.MoveTowards(mousePitch, 0f, Time.deltaTime * 120f);
            }
            cameraT.localRotation = Quaternion.Euler(mousePitch, mouseYaw, 0f);
        }
    }
}
