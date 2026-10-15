using System;
using UnityEngine;

namespace AirFight
{
    public enum Team { Blue, Red }

    public class Damageable : MonoBehaviour
    {
        public Team team;
        public float maxHealth = 100f;
        public float Health { get; private set; }
        public bool Dead { get; private set; }
        public event Action<Damageable> OnDeath;
        public event Action<float> OnDamaged;
        /// <summary>Name in the kill feed.</summary>
        public string callsign = "";
        /// <summary>Who hit us last, with what (0 other, 1 gun, 2 AIM-9), and when — for the kill feed.</summary>
        public Damageable LastAttacker { get; private set; }
        public byte LastWeapon { get; private set; }
        public float LastHitTime { get; private set; } = -99f;
        public static string WeaponName(byte w) => w == 1 ? "GUN" : w == 2 ? "AIM-9" : "";

        void Awake() { Health = maxHealth; }

        public static readonly System.Collections.Generic.List<Damageable> All = new System.Collections.Generic.List<Damageable>();
        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); }
        internal static void ResetStatics() { All.Clear(); }

        /// <summary>Remote player proxy: hits are forwarded to the owner instead of applied here.</summary>
        public Action<float, byte> forwardDamage;

        public void SetRemoteState(float health, bool dead) { Health = health; Dead = dead; }

        /// <summary>World velocity of a target (rigidbody or networked ghost).</summary>
        public static Vector3 VelocityOf(Damageable d)
        {
            if (d == null) return Vector3.zero;
            var g = d.GetComponent<NetGhost>();
            if (g) return g.Velocity;
            var rb = d.GetComponent<Rigidbody>();
            return rb && !rb.isKinematic ? rb.linearVelocity : Vector3.zero;
        }

        public void TakeDamage(float amount, Damageable source = null, byte weapon = 0)
        {
            if (Dead) return;
            if (forwardDamage != null) { if (amount > 0f) forwardDamage(amount, weapon); return; }
            if (source != null && source != this) { LastAttacker = source; LastWeapon = weapon; LastHitTime = Time.time; }
            Health -= amount;
            OnDamaged?.Invoke(amount);
            if (Health <= 0f) Kill();
        }

        public void Repair() { if (!Dead) Health = maxHealth; }
        public void Revive() { Dead = false; Health = maxHealth; }

        public void Kill()
        {
            if (Dead) return;
            Dead = true;
            Health = 0f;
            Explosion.Spawn(transform.position, 14f);
            OnDeath?.Invoke(this);
        }

        void OnCollisionEnter(Collision c)
        {
            if (forwardDamage != null) return;   // remote proxies never crash locally
            // Destroyed when the impact speed *into* the surface is high (a tail scrape on rotation is survivable).
            float vn = 0f;
            for (int i = 0; i < c.contactCount; i++)
                vn = Mathf.Max(vn, Mathf.Abs(Vector3.Dot(c.relativeVelocity, c.GetContact(i).normal)));
            if (vn > 20f) Kill();
        }
    }

    public class Explosion : MonoBehaviour
    {
        float t, life = 1.4f, size;
        Transform ball; Light flash;

        public static void Spawn(Vector3 pos, float size)
        {
            var go = new GameObject("Explosion");
            go.transform.position = pos;
            var e = go.AddComponent<Explosion>();
            e.size = size;
            e.ball = Build.Prim(PrimitiveType.Sphere, go.transform, Vector3.zero, Vector3.one, new Color(1f, 0.55f, 0.15f), true).transform;
            e.ball.GetComponent<Renderer>().sharedMaterial = Fx.Transparent(new Color(3f, 1.2f, 0.3f, 0.7f), null, true);
            var core = Build.Prim(PrimitiveType.Sphere, e.ball, Vector3.zero, Vector3.one * 0.6f, new Color(1f, 0.9f, 0.5f), true);
            core.GetComponent<Renderer>().sharedMaterial = Fx.Glow(new Color(1f, 0.8f, 0.4f), 5f);
            e.flash = go.AddComponent<Light>();
            e.flash.type = LightType.Point;
            e.flash.color = new Color(1f, 0.6f, 0.3f);
            e.flash.range = size * 12f;
            e.flash.intensity = 8f;
            AudioSynth.PlayBoom(pos);
        }

        void Update()
        {
            t += Time.deltaTime;
            float k = t / life;
            float s = size * Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI) * (0.6f + k);
            ball.localScale = Vector3.one * Mathf.Max(0.01f, s);
            flash.intensity = Mathf.Lerp(8f, 0f, k * 2f);
            if (t > life) Destroy(gameObject);
        }
    }
}
