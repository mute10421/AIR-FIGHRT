using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 절차적 사운드 (에셋 불필요): 엔진/애프터버너/바람, 기관포, AIM-9 시커 톤(그롤/락), RWR 미사일 경보, 경고음.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AudioSynth : MonoBehaviour
    {
        // Parameters set from main thread
        public volatile float engine;      // 0..1 rpm
        public volatile float afterburner; // 0..1
        public volatile float wind;        // 0..1
        public volatile bool gun;
        public volatile int seekerTone;    // 0 off, 1 searching growl, 2 locked
        public volatile bool missileWarning;
        public volatile bool pullUpWarning;
        public volatile int rwrLock;        // 0 none, 1 enemy radar/seeker tracking me, 2 enemy missile LOCK on me
        double phRwr;
        public float masterVolume = 0.8f;

        int sampleRate;
        double phEngine, phTone, phGun, phWarn, time;
        float brown, lpWind, lpAb;
        System.Random rng = new System.Random();

        static AudioClip boomClip, launchClip;

        void Awake()
        {
            sampleRate = AudioSettings.outputSampleRate;
            var src = GetComponent<AudioSource>();
            src.playOnAwake = true;
            src.loop = true;
            src.spatialBlend = 0f;
            src.clip = AudioClip.Create("silence", 256, 1, sampleRate, false);
            src.Play();
        }

        float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

        void OnAudioFilterRead(float[] data, int channels)
        {
            double dt = 1.0 / sampleRate;
            float eng = engine, ab = afterburner, wnd = wind;
            bool g = gun, mw = missileWarning, pu = pullUpWarning;
            int tone = seekerTone;
            int rwr = rwrLock;
            for (int i = 0; i < data.Length; i += channels)
            {
                time += dt;
                float n = Noise();
                brown = Mathf.Clamp(brown + n * 0.02f, -1f, 1f) * 0.998f;
                lpWind += (n - lpWind) * 0.05f;
                lpAb += (n - lpAb) * 0.25f;

                // engine whine + rumble
                phEngine += dt * (180.0 + 520.0 * eng);
                float s = Mathf.Sin((float)(phEngine * 2 * Mathf.PI)) * 0.05f * eng;
                s += brown * (0.25f + 0.35f * eng);
                s += lpAb * 0.35f * ab;
                s += lpWind * 0.5f * wnd;

                // gun: 100 Hz buzz-saw
                if (g)
                {
                    phGun += dt * 100.0;
                    float saw = (float)(phGun - System.Math.Floor(phGun)) * 2f - 1f;
                    s += (saw * 0.5f + n * 0.3f) * 0.6f;
                }

                // AIM-9 tone
                if (tone == 1)
                {
                    phTone += dt * (420.0 + 40.0 * System.Math.Sin(time * 2 * System.Math.PI * 7.0));
                    s += Mathf.Sin((float)(phTone * 2 * Mathf.PI)) * 0.12f;
                }
                else if (tone == 2)
                {
                    phTone += dt * 1250.0;
                    s += Mathf.Sin((float)(phTone * 2 * Mathf.PI)) * 0.14f;
                }

                // RWR missile launch warning: fast alternating
                if (mw)
                {
                    double f = ((int)(time * 8.0) % 2 == 0) ? 1000.0 : 1400.0;
                    phWarn += dt * f;
                    s += (Mathf.Sin((float)(phWarn * 2 * Mathf.PI)) > 0 ? 0.08f : -0.08f);
                }
                else if (pu && ((int)(time * 3.0) % 2 == 0))
                {
                    phWarn += dt * 800.0;
                    s += Mathf.Sin((float)(phWarn * 2 * Mathf.PI)) * 0.1f;
                }

                // RWR lock warning (independent of the launch warning so you hear the lock before the missile comes)
                if (rwr == 1 && (time * 2.0) % 1.0 < 0.12)                 // tracked: slow "deedle" chirp twice a second
                {
                    phRwr += dt * 1100.0;
                    s += Mathf.Sin((float)(phRwr * 2 * Mathf.PI)) * 0.09f;
                }
                else if (rwr == 2 && (time * 5.0) % 1.0 < 0.55)             // LOCKED: fast, high, insistent beeping
                {
                    phRwr += dt * 1700.0;
                    s += (Mathf.Sin((float)(phRwr * 2 * Mathf.PI)) > 0 ? 0.075f : -0.075f);
                }

                s *= masterVolume;
                for (int c = 0; c < channels; c++) data[i + c] = s;
            }
        }

        public static void PlayBoom(Vector3 pos)
        {
            if (boomClip == null) boomClip = MakeNoiseClip("boom", 1.8f, 0.985f, 3.5f);
            AudioSource.PlayClipAtPoint(boomClip, pos, 1f);
        }

        public static void PlayLaunch(Vector3 pos)
        {
            if (launchClip == null) launchClip = MakeNoiseClip("launch", 1.4f, 0.8f, 1.5f);
            AudioSource.PlayClipAtPoint(launchClip, pos, 0.9f);
        }

        static AudioClip MakeNoiseClip(string name, float seconds, float lowpass, float decay)
        {
            int sr = AudioSettings.outputSampleRate;
            int len = (int)(sr * seconds);
            var data = new float[len];
            var r = new System.Random(name.GetHashCode());
            float lp = 0f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)sr;
                float n = (float)(r.NextDouble() * 2 - 1);
                lp = lp * lowpass + n * (1f - lowpass);
                data[i] = lp * Mathf.Exp(-t * decay) * (lowpass > 0.9f ? 6f : 1f);
            }
            var clip = AudioClip.Create(name, len, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
