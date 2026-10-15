using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AirFight
{
    public enum DayTime { Day = 0, Dusk = 1, Night = 2 }

    /// <summary>
    /// 시간대: 낮 / 해질녘 / 밤. 해(또는 달)의 방향·색·세기, 하늘(별·달), 안개, 노출, 지형 그림자, 활주로 등화, 항공기 항법등을 한 번에 바꾼다.
    /// </summary>
    public static class TimeOfDay
    {
        public static DayTime Current
        {
            get { if (sessionOverride.HasValue) return sessionOverride.Value; try { return (DayTime)PlayerPrefs.GetInt("AF_DayTime", 0); } catch { return DayTime.Day; } }
            private set { PlayerPrefs.SetInt("AF_DayTime", (int)value); PlayerPrefs.Save(); }
        }
        /// <summary>0 day … 1 night (lights, halos, cockpit flood).</summary>
        public static float Night { get; private set; }
        public static string Name => Current == DayTime.Day ? "DAY" : Current == DayTime.Dusk ? "DUSK" : "NIGHT";

        internal static ColorAdjustments adjust;
        internal static WhiteBalance white;
        internal static Bloom bloom;
        internal static readonly List<Transform> runwayLights = new List<Transform>();
        static readonly List<Vector3> runwayLightScale = new List<Vector3>();

        public static void ResetStatics() { sessionOverride = null; adjust = null; white = null; bloom = null; runwayLights.Clear(); runwayLightScale.Clear(); }

        internal static void RegisterRunwayLight(Transform t) { runwayLights.Add(t); runwayLightScale.Add(t.localScale); }

        /// <summary>Sun (day/dusk) or moon (night) for the current setting.</summary>
        public static void SetupSun(Light sun)
        {
            if (sun == null) return;
            switch (Current)
            {
                case DayTime.Dusk:
                    sun.transform.rotation = Quaternion.Euler(5.5f, -75f, 0f);      // low in the west-south-west
                    sun.intensity = 1.25f;
                    sun.color = new Color(1f, 0.58f, 0.32f);
                    sun.shadowStrength = 0.75f;
                    Night = 0.15f;
                    break;
                case DayTime.Night:
                    sun.transform.rotation = Quaternion.Euler(38f, 145f, 0f);      // a bright moon
                    sun.intensity = 0.16f;
                    sun.color = new Color(0.62f, 0.72f, 1f);
                    sun.shadowStrength = 0.6f;
                    Night = 1f;
                    break;
                default:
                    sun.transform.rotation = Quaternion.Euler(34f, -35f, 0f);      // mid-morning
                    sun.intensity = 2.0f;
                    sun.color = new Color(1f, 0.94f, 0.86f);
                    sun.shadowStrength = 0.85f;
                    Night = 0f;
                    break;
            }
        }

        /// <summary>Sky, fog, exposure, globals — everything that depends on the light but not on baked textures.</summary>
        public static void ApplyLook()
        {
            var t = Current;
            Shader.SetGlobalFloat("_AF_Night", t == DayTime.Night ? 1f : 0f);
            Realism.PushSun();
            if (adjust != null)
            {
                adjust.postExposure.Override(t == DayTime.Night ? 1.5f : t == DayTime.Dusk ? 0.75f : 0.45f);   // the eye adapts to the dark
                adjust.saturation.Override(t == DayTime.Night ? -30f : t == DayTime.Dusk ? 10f : 6f);         // little colour at night
                adjust.contrast.Override(t == DayTime.Night ? 4f : 8f);
            }
            if (white != null) white.temperature.Override(t == DayTime.Night ? -22f : t == DayTime.Dusk ? 12f : 4f);
            if (bloom != null) bloom.intensity.Override((Application.isMobilePlatform ? 0.35f : 0.6f) * (t == DayTime.Night ? 1.8f : 1f));
            RenderSettings.fogColor = t == DayTime.Night ? new Color(0.03f, 0.04f, 0.06f) : t == DayTime.Dusk ? new Color(0.75f, 0.58f, 0.48f) : new Color(0.62f, 0.72f, 0.86f).gamma;
            RenderSettings.ambientIntensity = 1f;
            DynamicGI.UpdateEnvironment();                     // ambient light from the (now dark / orange) sky
            for (int i = 0; i < runwayLights.Count; i++)
                if (runwayLights[i]) runwayLights[i].localScale = runwayLightScale[i] * (t == DayTime.Night ? 3.5f : t == DayTime.Dusk ? 2f : 1f);
        }

        /// <summary>Switch at runtime (menu): light, look, and re-bake terrain shadows for the new sun/moon.</summary>
        static DayTime? sessionOverride;

        /// <summary>Multiplayer: the room host's time of day (not saved as my own preference). null = back to mine.</summary>
        public static void SetSession(DayTime? t)
        {
            if (sessionOverride == t) return;
            sessionOverride = t;
            SetupSun(RenderSettings.sun);
            ApplyLook();
            WorldBuilder.RefreshLighting();
        }

        public static void Set(DayTime t)
        {
            sessionOverride = null;
            Current = t;
            SetupSun(RenderSettings.sun);
            ApplyLook();
            WorldBuilder.RefreshLighting();
        }

        public static DayTime Next(DayTime t) => (DayTime)(((int)t + 1) % 3);
    }

    /// <summary>
    /// Navigation lights (red left / green right / white tail), a flashing red anti-collision beacon, and for the player a
    /// landing light and a dim cockpit flood light at night. Halos keep a minimum on-screen size, so other jets read as
    /// moving lights in the dark sky.
    /// </summary>
    public class AircraftLights : MonoBehaviour
    {
        class Lamp { public Transform halo; public Renderer r; public float size; public bool beacon; }
        readonly List<Lamp> lamps = new List<Lamp>();
        static readonly Dictionary<Color, Material> mats = new Dictionary<Color, Material>();
        Light landing, flood;
        LandingGear gear;
        float phase;

        public static Vector3 WingTipPos => ModelKit.Available ? ModelKit.WingTip + new Vector3(0.05f, 0f, 0f) : new Vector3(4.8f, 0.3f, -2.2f);
        public static Vector3 TailPos => ModelKit.Available ? ModelKit.NozzleExit + new Vector3(0f, 1.0f, 0.5f) : new Vector3(0f, 1.6f, -6.5f);

        public void Init(Vector3 wingTipRight, Vector3 tail, bool isPlayer, Vector3 eyePoint)
        {
            phase = Random.value * 2f;
            Add(new Vector3(-wingTipRight.x, wingTipRight.y, wingTipRight.z), new Color(4f, 0.25f, 0.2f), 0.9f, false);   // port: red
            Add(wingTipRight, new Color(0.3f, 4f, 0.6f), 0.9f, false);                                                      // starboard: green
            Add(tail, new Color(3.5f, 3.5f, 3.2f), 0.8f, false);                                                            // tail: white
            Add(new Vector3(0f, 1.05f, -1.6f), new Color(5f, 0.3f, 0.2f), 1.2f, true);                                      // anti-collision beacon
            if (!isPlayer) return;
            gear = GetComponent<LandingGear>();
            var lg = new GameObject("LandingLight");
            lg.transform.SetParent(transform, false);
            lg.transform.localPosition = new Vector3(0f, -1.2f, 4.5f);
            lg.transform.localRotation = Quaternion.Euler(4f, 0f, 0f);
            landing = lg.AddComponent<Light>();
            landing.type = LightType.Spot;
            landing.spotAngle = 28f; landing.innerSpotAngle = 12f;
            landing.range = 900f; landing.intensity = 40f;
            landing.color = new Color(1f, 0.95f, 0.85f);
            landing.shadows = LightShadows.None;
            var fl = new GameObject("CockpitFlood");
            fl.transform.SetParent(transform, false);
            fl.transform.localPosition = eyePoint + new Vector3(0f, -0.25f, 0.45f);
            flood = fl.AddComponent<Light>();
            flood.type = LightType.Point;
            flood.range = 1.4f; flood.intensity = 0.35f;
            flood.color = new Color(1f, 0.55f, 0.42f);
            flood.shadows = LightShadows.None;
        }

        void Add(Vector3 local, Color c, float size, bool beacon)
        {
            if (!mats.TryGetValue(c, out var m) || m == null)
            {
                m = Fx.Transparent(c, Fx.SoftDot(), true);
                m.renderQueue = 3150;
                mats[c] = m;
            }
            var q = Build.Prim(PrimitiveType.Quad, transform, local, Vector3.one * size, Color.white, true, null, false, "NavLight");
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            lamps.Add(new Lamp { halo = q.transform, r = r, size = size, beacon = beacon });
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            float night = TimeOfDay.Night;
            foreach (var l in lamps)
            {
                if (l.halo == null) continue;
                bool on = !l.beacon || Mathf.Repeat(Time.time + phase, 1.2f) < 0.12f;    // beacon: short red flash
                l.r.enabled = on;
                if (!on) continue;
                Vector3 to = l.halo.position - cp;
                float d = to.magnitude;
                if (d < 2.5f) { l.r.enabled = false; continue; }                              // not from our own cockpit
                l.halo.rotation = Quaternion.LookRotation(to, cam.transform.up);
                float s = l.size * Mathf.Lerp(0.35f, 1f, night);
                s = Mathf.Max(s, d * Mathf.Lerp(0.0006f, 0.0035f, night));                  // a visible point of light far away at night
                l.halo.localScale = Vector3.one * s / Mathf.Max(transform.lossyScale.x, 1e-3f);
            }
            if (landing) landing.enabled = night > 0.5f && gear != null && gear.Deploy > 0.5f;
            if (flood) flood.enabled = night > 0.5f;
        }
    }
}
