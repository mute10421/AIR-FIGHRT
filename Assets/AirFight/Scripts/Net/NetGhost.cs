using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 상대 플레이어 기체(원격). 수신 상태를 100ms 지연 보간 + 속도 외삽으로 부드럽게 표시.
    /// 맞으면 데미지를 주인에게 전송(이 쪽에서는 체력을 깎지 않음), 기관포 예광탄·미사일·애프터버너를 재현.
    /// </summary>
    public class NetGhost : MonoBehaviour
    {
        public struct MissileState { public int id; public Vector3 pos, vel; public bool targetIsMe; }
        struct Snap { public float t; public Vector3 pos, vel; public Quaternion rot; }

        public ulong ClientId { get; private set; }
        /// <summary>Callsign chosen by that player (arrives in a name message; P&lt;id&gt; until then).</summary>
        public string Nick { get; set; }
        public Damageable Damage { get; private set; }
        public Vector3 Velocity { get; private set; }
        public int Kills { get; private set; }
        public int Deaths { get; private set; }
        public bool Dead { get; private set; }
        public bool MissileOnMe { get; private set; }
        /// <summary>This remote player's seeker on me: 0 none, 1 tracking, 2 locked.</summary>
        public int LockOnMe { get; set; }
        public readonly List<MissileState> missileBuffer = new List<MissileState>();

        readonly List<Snap> snaps = new List<Snap>();
        readonly Dictionary<int, Transform> missiles = new Dictionary<int, Transform>();
        readonly HashSet<int> threatIds = new HashSet<int>();
        readonly HashSet<int> seen = new HashSet<int>();
        GameObject model;
        Transform flame, muzzle;
        Material flameMat; float flameExitZ = float.NaN;
        bool firing;
        float rpm, gunAccum, lastPacket;
        const float Delay = 0.1f;

        static readonly Color[] Paints =
        {
            new Color(0.62f, 0.5f, 0.38f), new Color(0.45f, 0.5f, 0.42f), new Color(0.38f, 0.42f, 0.55f),
            new Color(0.6f, 0.45f, 0.45f), new Color(0.5f, 0.5f, 0.5f), new Color(0.55f, 0.55f, 0.35f),
        };

        public static NetGhost Create(ulong id)
        {
            var go = new GameObject($"Pilot {id}");
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = 2; col.radius = 1.4f; col.height = 15f; col.center = new Vector3(0, 0.3f, 1f);
            var wing = go.AddComponent<BoxCollider>();
            wing.center = new Vector3(0, 0.25f, -1.5f); wing.size = new Vector3(9.4f, 0.6f, 3.5f);

            var g = go.AddComponent<NetGhost>();
            g.ClientId = id;
            g.Nick = $"P{id}";
            var dmg = go.AddComponent<Damageable>();
            dmg.team = Team.Red;
            dmg.forwardDamage = (amount, weapon) => NetSession.I?.SendDamage(id, amount, weapon);
            g.Damage = dmg;

            var parts = AircraftFactory.BuildExterior(go.transform, Paints[(int)(id % (ulong)Paints.Length)], false);
            g.model = go.transform.Find("Model").gameObject;
            g.flame = parts.nozzleFlame;
            g.muzzle = parts.gunMuzzle;
            foreach (var m in parts.railMissiles) m.SetActive(false);
            g.nameTag = go.AddComponent<NameTag>();
            g.nameTag.Init($"P{id}");
            g.nameTag.Hide();                         // no floating labels over other players
            go.AddComponent<AircraftLights>().Init(AircraftLights.WingTipPos, AircraftLights.TailPos, false, Vector3.zero);
            return g;
        }

        NameTag nameTag;
        int side = -1;
        /// <summary>Team battle: allies are not targets (no lock, no hits) and get a green name tag; FFA: everyone is a bandit.</summary>
        public void SetSide(bool teamMode, bool ally)
        {
            int s = !teamMode ? 0 : ally ? 1 : 2;
            if (s == side) return;
            side = s;
            Damage.team = ally ? Team.Blue : Team.Red;
            Damage.callsign = Nick;
            if (nameTag) nameTag.Set(teamMode ? $"{Nick} {(ally ? "ALLY" : NetSession.TeamName(NetSession.TeamOf(ClientId)))}" : Nick,
                             ally ? new Color(0.35f, 1f, 0.45f) : new Color(1f, 0.3f, 0.25f));
        }

        public void Receive(Vector3 pos, Quaternion rot, Vector3 vel, float engineRpm, byte flags, byte health, short kills, short deaths)
        {
            float now = Time.time;
            lastPacket = now;
            snaps.Add(new Snap { t = now, pos = pos, rot = rot, vel = vel });
            if (snaps.Count > 20) snaps.RemoveAt(0);
            rpm = engineRpm;
            firing = (flags & 2) != 0;
            Kills = kills; Deaths = deaths;
            bool dead = (flags & 1) != 0;
            if (dead && !Dead) { Explosion.Spawn(pos, 14f); Wreck.Create(transform, vel, Vector3.zero); }   // crumpled, burning wreck
            if (dead != Dead)
            {
                Dead = dead;
                model.SetActive(!dead);
                foreach (var c in GetComponents<Collider>()) c.enabled = !dead;
                if (!dead) snaps.Clear();
                if (!dead) snaps.Add(new Snap { t = now, pos = pos, rot = rot, vel = vel });
            }
            Damage.SetRemoteState(health, dead);
            SyncMissiles();
        }

        void SyncMissiles()
        {
            seen.Clear();
            bool onMe = false;
            foreach (var m in missileBuffer)
            {
                seen.Add(m.id);
                if (!missiles.TryGetValue(m.id, out var t) || t == null)
                {
                    var go = new GameObject("RemoteMissile");
                    Missile.BuildModel(go.transform);
                    float born = Time.time;
                    int mid = m.id;
                    MissileFx.Dress(go, () => Time.time - born < 8.3f, () => threatIds.Contains(mid));
                    t = go.transform;
                    t.position = m.pos;
                    missiles[m.id] = t;
                }
                var rm = t.GetComponent<RemoteMissileMotion>();
                if (rm == null) rm = t.gameObject.AddComponent<RemoteMissileMotion>();
                rm.Set(m.pos, m.vel);
                if (m.targetIsMe) { onMe = true; threatIds.Add(m.id); } else threatIds.Remove(m.id);
            }
            var gone = new List<int>();
            foreach (var kv in missiles) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                var t = missiles[id];
                missiles.Remove(id);
                if (t) { Explosion.Spawn(t.position, 7f); t.GetComponent<RemoteMissileMotion>().Finish(); }
            }
            MissileOnMe = onMe;
        }

        void Update()
        {
            if (snaps.Count == 0) return;
            float rt = Time.time - Delay;
            // interpolate between the two snapshots around render time, else extrapolate from the newest
            Snap a = snaps[snaps.Count - 1], b = a;
            for (int i = snaps.Count - 1; i > 0; i--)
                if (snaps[i - 1].t <= rt) { a = snaps[i - 1]; b = snaps[i]; break; }
            Vector3 pos; Quaternion rot;
            if (b.t > a.t && rt <= b.t)
            {
                float k = Mathf.InverseLerp(a.t, b.t, rt);
                pos = Vector3.Lerp(a.pos, b.pos, k);
                rot = Quaternion.Slerp(a.rot, b.rot, k);
                Velocity = Vector3.Lerp(a.vel, b.vel, k);
            }
            else
            {
                var last = snaps[snaps.Count - 1];
                float dt = Mathf.Clamp(rt - last.t, 0f, 0.5f);
                pos = last.pos + last.vel * dt;
                rot = last.rot;
                Velocity = last.vel;
            }
            transform.SetPositionAndRotation(pos, rot);

            // visual-only effects (real damage is decided by the shooter)
            if (flame)
            {
                if (flameMat == null) { var fr = flame.GetComponent<Renderer>(); if (fr) flameMat = fr.material; }
                if (float.IsNaN(flameExitZ)) flameExitZ = flame.localPosition.z + 0.65f;
                NozzleFx.Apply(flame, flameMat, Dead ? 0f : rpm, flameExitZ);
            }
            if (firing && !Dead && muzzle)
            {
                gunAccum += Time.deltaTime * 25f;   // a few visual tracers
                while (gunAccum >= 1f)
                {
                    gunAccum -= 1f;
                    Bullets.Fire(muzzle.position, Velocity + muzzle.forward * 1030f, Team.Red, 0f, true);
                }
            }
            if (Time.time - lastPacket > 6f) { Destroy(gameObject); }
        }

        void OnDestroy()
        {
            foreach (var t in missiles.Values) if (t) Destroy(t.gameObject);
            GameManager.Instance?.RemoveRemoteTarget(Damage);
        }
    }

    /// <summary>Smooth motion for a remote missile between 20 Hz updates.</summary>
    public class RemoteMissileMotion : MonoBehaviour
    {
        Vector3 target, vel;
        bool done;
        public void Set(Vector3 p, Vector3 v) { target = p; vel = v; if (Vector3.Distance(transform.position, p) > 200f) transform.position = p; }
        public void Finish() { done = true; foreach (Transform c in transform) Destroy(c.gameObject); var tr = GetComponent<TrailRenderer>(); if (tr) tr.emitting = false; Destroy(gameObject, 4f); }
        void Update()
        {
            if (done) return;
            target += vel * Time.deltaTime;
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-Time.deltaTime * 12f));
            if (vel.sqrMagnitude > 1f) transform.rotation = Quaternion.LookRotation(vel);
        }
    }

    /// <summary>Floating player label (always faces the camera, scales with distance).</summary>
    public class NameTag : MonoBehaviour
    {
        TextMesh tm;
        public void Init(string text)
        {
            var go = new GameObject("NameTag");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0, 6f, 0);
            tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.characterSize = 0.5f;
            tm.fontSize = 48;
            tm.color = new Color(1f, 0.45f, 0.35f);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
        public void Set(string text, Color c) { if (tm) { tm.text = text; tm.color = c; } }
        public void Hide() { if (tm) tm.gameObject.SetActive(false); }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || tm == null) return;
            var t = tm.transform;
            t.rotation = Quaternion.LookRotation(t.position - cam.transform.position);
            float d = Vector3.Distance(cam.transform.position, t.position);
            t.localScale = Vector3.one * Mathf.Clamp(d / 300f, 0.3f, 40f);
        }
    }
}
