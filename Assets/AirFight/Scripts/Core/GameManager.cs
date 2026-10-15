using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AirFight
{
    /// <summary>
    /// 게임 부트스트랩: 씬에 이 컴포넌트 하나만 있으면 지형·플레이어 기체(조종석/VR 리그/HUD)·적 웨이브를 런타임에 생성한다.
    /// </summary>
    public enum GameMode { Menu, AIBattle, FreeFlight, Multiplayer }

    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }
        public GameMode Mode { get; private set; } = GameMode.Menu;
        /// <summary>AI battle difficulty: 0 EASY, 1 NORMAL, 2 ACE.</summary>
        public int Difficulty { get; private set; } = 1;
        public MainMenu Menu { get; private set; }

        [Header("Start")]
        public float startAltitude = 3000f;
        public float startSpeed = 230f;        // m/s (~450 KCAS at 10,000 ft)
        public int startWave = 1;
        public bool startOnRunway = true;      // false = air start at startAltitude

        public readonly List<Damageable> enemyList = new List<Damageable>();
        /// <summary>Hostile contacts (team-battle allies are left out of the HUD, radar and map).</summary>
        public IEnumerable<Damageable> Enemies { get { foreach (var e in enemyList) if (e && e.team != Team.Blue) yield return e; } }
        /// <summary>Team-battle wingmen (green boxes on the HUD).</summary>
        public IEnumerable<Damageable> Allies { get { foreach (var e in enemyList) if (e && e.team == Team.Blue) yield return e; } }
        public int AliveEnemies { get { int n = 0; foreach (var e in enemyList) if (e && !e.Dead) n++; return n; } }
        public int Wave { get; private set; }
        public int Kills { get; private set; }
        public string WaveBanner { get; private set; }
        public bool OutOfBounds { get; private set; }
        public AudioSynth Audio { get; private set; }

        GameObject player;
        VRCockpitInput playerVr;
        Autoland playerAutoland;
        public MapView Map { get; private set; }
        public ControlsWindow Controls { get; private set; }
        public NetPanel NetPanelRef { get; private set; }
        /// <summary>Airport selected for HUD navigation (null = none).</summary>
        public WorldBuilder.Airport NavTarget { get; set; }
        /// <summary>The local player's jet.</summary>
        public GameObject PlayerObject => player;
        FlightModel playerFm;
        Weapons playerWeapons;
        Damageable playerDmg;
        LandingGear playerGear;
        bool wavesStarted;
        float stoppedTimer;
        bool servicedThisStop = true;   // no "rearmed" banner at the very start
        bool refuelBannerPending;
        float bannerTimer, nextWaveTimer = -1f, deadTimer;

        public static float TerrainHeight(float x, float z) => Mathf.Max(0f, WorldBuilder.GridHeight(x, z));

        /// <summary>
        /// Play-mode start without a domain reload (Enter Play Mode Options): static state from the previous Play must be
        /// cleared by hand. Runs before any scene object wakes up.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            Missile.ResetStatics();
            Weapons.ResetStatics();
            Damageable.ResetStatics();
            Flare.ResetStatics();
            MainMenu.ResetStatics();
            ControlsWindow.ResetStatics();
            KillFeed.ResetStatics();
            NetPanel.ResetStatics();
            ModelKit.ResetStatics();
            Mats.ResetStatics();
            WorldBuilder.ResetStatics();
            TimeOfDay.ResetStatics();
        }

        void Awake()
        {
            Instance = this;
            Time.timeScale = 1f;
            Time.fixedDeltaTime = 1f / 90f;
            // a preview camera may sit in the scene so the Game view isn't black before Play — the pilot camera takes over
            var preview = GameObject.Find("Preview");   // edit-mode title screen (camera, light, model, title)
            if (preview) DestroyImmediate(preview);
            foreach (var c in FindObjectsByType<Camera>()) Destroy(c.gameObject);
            Application.targetFrameRate = 90;
            QualitySettings.vSyncCount = 0;
            WorldBuilder.Create(transform);
            Net = gameObject.AddComponent<NetSession>();
            SpawnPlayer();
            Net.fm = playerFm; Net.weapons = playerWeapons; Net.self = playerDmg; Net.gear = playerGear;
            Net.SessionStarted += OnMultiplayerStarted;
            Net.SessionEnded += OnMultiplayerEnded;
            Net.TeamModeChanged += OnTeamModeChanged;
            Net.MatchStarted += OnMatchStarted;
            Net.MatchEnded += () => Banner("MATCH OVER — " + Net.ResultText, 10f);
            Net.ReturnedToLobby += OnBackToLobby;
            Flare.LocalDropped += (p, v) => { if (Multiplayer) Net.SendFlare(p, v); };
            Menu.Open();   // pick a mode first
        }

        // ------------------------------------------------------------------ Modes
        public void StartAIBattle(int difficulty, bool airStart)
        {
            Difficulty = Mathf.Clamp(difficulty, 0, 2);
            playerDmg.callsign = NetSession.Nick;
            banditCount = 0;
            Mode = GameMode.AIBattle;
            ResetCombat();
            if (airStart)
            {
                AirStart();
                wavesStarted = true;
                StartWave(startWave);
            }
            else Banner($"AI BATTLE ({DifficultyName}) — CLEARED FOR TAKEOFF · BANDITS AFTER 300 M", 10f);
        }

        public void StartFreeFlight()
        {
            Mode = GameMode.FreeFlight;
            ResetCombat();
            Banner("FREE FLIGHT — CLEARED FOR TAKEOFF", 8f);
        }

        public string DifficultyName => Difficulty == 0 ? "EASY" : Difficulty == 1 ? "NORMAL" : "ACE";

        /// <summary>Back to the mode menu: leave multiplayer, clear AI, park on the home runway.</summary>
        public void ReturnToMenu()
        {
            if (Multiplayer) Net.Leave();
            Mode = GameMode.Menu;
            ResetCombat();
            if (playerDmg.Dead) { playerDmg.Revive(); playerFm.Destroyed = false; player.GetComponent<Rigidbody>().isKinematic = false; playerWeapons.enabled = true; deadTimer = 0f; if (slowMo != null) StopCoroutine(slowMo); slowMo = null; Time.timeScale = 1f; HidePlayerJet(false); if (playerVr) playerVr.StopDeathCam(); }
            if (playerAutoland) playerAutoland.Disengage("");
            playerWeapons.Rearm(511, 4, 30);
            playerDmg.Repair();
            playerFm.SetFuel(playerFm.maxFuel);
            TeleportTo(WorldBuilder.Home, false);
            WaveBanner = null;
            Menu.Open();
        }

        void ResetCombat()
        {
            foreach (var e in enemyList.ToArray())
                if (e && e.GetComponent<NetGhost>() == null) Destroy(e.gameObject);
            enemyList.RemoveAll(e => e == null || e.GetComponent<NetGhost>() == null);
            wavesStarted = false;
            nextWaveTimer = -1f;
            Wave = 0; Kills = 0;
        }

        void AirStart()
        {
            var home = WorldBuilder.Home;
            AirStartAt(home.Threshold(1f) + home.Dir * 4000f + Vector3.up * startAltitude, home.Dir);
        }

        void AirStartAt(Vector3 pos, Vector3 dir)
        {
            dir.y = 0f; dir.Normalize();
            var rb = player.GetComponent<Rigidbody>();
            Quaternion rot = Quaternion.LookRotation(dir);
            rb.position = pos; rb.rotation = rot;
            player.transform.SetPositionAndRotation(pos, rot);
            rb.angularVelocity = Vector3.zero;
            playerGear.SetInstant(false);
            playerGear.ResetState();
            playerGear.parkingBrake = false;
            playerFm.SetEngineInstant(0.75f);
            playerVr.Throttle = 0.75f;
            playerFm.Launch(dir * startSpeed);
            if (playerAutoland) playerAutoland.Disengage("");
        }

        // ------------------------------------------------------------------ PvP air start: close to the fight
        const float ArenaRadius = 5000f;      // start ring around the arena centre → opponents ~8-10 km apart (≈ 20 s to the merge)

        /// <summary>
        /// Match start / respawn in the air, on a ring around the arena centre, nose toward the middle.
        /// FFA: everyone at a different angle (by id). Team battle: BLUE south, RED north, team mates spread sideways.
        /// </summary>
        void MultiplayerAirStart(bool respawn)
        {
            float R = respawn ? ArenaRadius * 1.4f : ArenaRadius;
            ulong id = Net.MyId;
            float ang; float side = 0f, up = 0f;
            if (Net.TeamMode)
            {
                ang = Net.MyTeam == 0 ? 180f : 0f;                          // BLUE from the south, RED from the north
                int k = (int)(id / 2);
                side = (k % 2 == 0 ? 1f : -1f) * ((k + 1) / 2) * 900f;    // 0, +900, -900, +1800 …
                up = k * 120f;
            }
            else ang = Mathf.Repeat(id * 137.5f, 360f);                    // golden angle → evenly spread, never the same

            // respawn: if bandits are close to my point, come in from a quieter direction
            if (respawn)
                for (int tries = 0; tries < 4 && EnemyNear(RingPoint(ang, R, side), 4000f); tries++) ang += 90f;

            Vector3 p = RingPoint(ang, R, side);
            p.y = Mathf.Max(TerrainHeight(p.x, p.z) + 1500f, 3000f) + up;
            Vector3 toCenter = -new Vector3(p.x, 0f, p.z);
            AirStartAt(p, toCenter.sqrMagnitude > 1f ? toCenter : Vector3.forward);
            if (playerVr) playerVr.Throttle = 0.75f;
        }

        static Vector3 RingPoint(float angDeg, float r, float side)
        {
            Vector3 dir = Quaternion.Euler(0f, angDeg, 0f) * Vector3.forward;        // centre → me
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            return dir * r + right * side;
        }

        bool EnemyNear(Vector3 p, float dist)
        {
            foreach (var kv in Net.Ghosts)
            {
                var g = kv.Value;
                if (!g || g.Dead || Net.IsAlly(kv.Key)) continue;
                Vector3 q = g.transform.position;
                if (new Vector2(q.x - p.x, q.z - p.z).magnitude < dist) return true;
            }
            return false;
        }

        void Update()
        {
            if (bannerTimer > 0f) { bannerTimer -= Time.deltaTime; if (bannerTimer <= 0f) WaveBanner = null; }

            if (player)
            {
                Vector3 p = player.transform.position;
                OutOfBounds = new Vector2(p.x, p.z).magnitude > WorldBuilder.PlayArea;
            }

            // first wave launches once we are safely airborne
            if (Mode == GameMode.AIBattle && !wavesStarted && !playerDmg.Dead)
            {
                Vector3 p = player.transform.position;
                if (!playerGear.OnGround && p.y - TerrainHeight(p.x, p.z) > 300f)
                {
                    wavesStarted = true;
                    StartWave(startWave);
                }
            }

            if (Mode == GameMode.AIBattle && wavesStarted && !playerDmg.Dead && AliveEnemies == 0 && nextWaveTimer < 0f)
            {
                Banner($"WAVE {Wave} CLEAR", 4f);
                nextWaveTimer = 6f;
            }
            if (nextWaveTimer >= 0f)
            {
                nextWaveTimer -= Time.deltaTime;
                if (nextWaveTimer < 0f)
                {
                    playerWeapons.Rearm(511, 4, 30);
                    playerDmg.Repair();
                    StartWave(Wave + 1);
                }
            }

            // Full stop at any airfield → ground crew: bullets, missiles, flares, repair at once; fuel pumped in (≈10 s)
            var nearAp = WorldBuilder.Nearest(player.transform.position);
            bool atAirfield = (new Vector2(player.transform.position.x, player.transform.position.z) - nearAp.center).magnitude < nearAp.length * 0.5f + 900f;
            if (!playerDmg.Dead && playerGear.OnGround && playerFm.Airspeed < 3f && atAirfield)
            {
                stoppedTimer += Time.deltaTime;
                if (stoppedTimer > 2f)
                {
                    if (!servicedThisStop)
                    {
                        servicedThisStop = true;
                        playerWeapons.Rearm(511, 4, 30);
                        playerDmg.Repair();
                    }
                    if (playerFm.FuelFraction < 0.999f)
                    {
                        playerFm.Refuel(330f * Time.deltaTime);
                        Banner($"{nearAp.name}: REARMED — REFUELING {playerFm.FuelFraction * 100f:0}%", 1f);
                    }
                    else if (stoppedTimer < 3f || refuelBannerPending)
                    {
                        Banner($"{nearAp.name}: GUN 511 · AIM-9 x4 · FUEL 100% — READY", 4f);
                        refuelBannerPending = false;
                    }
                }
            }
            else
            {
                if (stoppedTimer > 2f) refuelBannerPending = true;
                stoppedTimer = 0f;
                if (!playerGear.OnGround) servicedThisStop = false;
            }

            // just joined a room: re-pick my airfield once the others are known (never park on someone else's runway)
            if (reparkAt > 0f && Time.time > reparkAt)
            {
                reparkAt = -1f;
                if (Multiplayer && !playerDmg.Dead && playerGear.OnGround && playerFm.Airspeed < 5f)
                {
                    var mine = MultiplayerBase();
                    var here = WorldBuilder.Nearest(player.transform.position);
                    if (mine != here) TeleportTo(mine, false);
                }
            }

            if (playerDmg.Dead && Mode != GameMode.Menu)
            {
                deadTimer += Time.deltaTime;
                if (deadTimer > 3f) Respawn();                    // 3 s death camera, then back to an airfield
            }
        }

        public void Banner(string s, float t) { WaveBanner = s; bannerTimer = t; }

        // ------------------------------------------------------------------ Multiplayer (PvP)
        public NetSession Net { get; private set; }
        public bool Multiplayer => Net != null && Net.Active;

        public void AddRemoteTarget(Damageable d) { if (d && !enemyList.Contains(d)) enemyList.Add(d); }
        public void RemoveRemoteTarget(Damageable d) { enemyList.Remove(d); }

        void OnMultiplayerStarted()
        {
            // PvP: AI bandits leave, everyone starts on a different airfield
            foreach (var e in enemyList.ToArray())
                if (e && e.GetComponent<NetGhost>() == null) Destroy(e.gameObject);
            enemyList.RemoveAll(e => e == null || e.GetComponent<NetGhost>() == null);
            nextWaveTimer = -1f;
            wavesStarted = false;
            Mode = GameMode.Multiplayer;
            var ap = MultiplayerBase();
            if (playerDmg.Dead) Respawn(); else TeleportTo(ap, false);
            playerWeapons.Rearm(511, 4, 30);
            playerFm.SetFuel(playerFm.maxFuel);
            mpStartTime = Time.time;
            playerDmg.callsign = NetSession.Nick;
            reparkAt = Time.time + 2f;                         // the other pilots' positions arrive a moment after joining → pick again
            Banner(Net.IsHost ? "ROOM CREATED — SET MODE / TIME / LENGTH, THEN START" : "JOINED — WAITING FOR THE HOST TO START", 8f);
        }

        float mpStartTime = -100f;
        float reparkAt = -1f;
        /// <summary>FFA: everyone on a different airfield. Team battle: BLUE from the south base, RED from the north.</summary>
        // team bases: BLUE in the south, RED in the north — several airfields each, so teammates never share a runway
        static readonly int[] BlueBases = { 0, 6, 7, 1, 4 }, RedBases = { 2, 5, 8, 3 };

        /// <summary>
        /// My own airfield: FFA = one airfield per pilot, team battle = one of my team's airfields. The pick follows my
        /// network id, and skips any airfield where another pilot is already on the ground.
        /// </summary>
        WorldBuilder.Airport MultiplayerBase(bool avoidOccupied = false)
        {
            var aps = WorldBuilder.Airports;
            var list = new List<int>();
            if (Net.TeamMode) { foreach (int i in Net.MyTeam == 0 ? BlueBases : RedBases) if (i < aps.Length) list.Add(i); }
            else for (int i = 0; i < aps.Length; i++) list.Add(i);
            // slot from my network id (unique per pilot; team mates' ids step by 2) → known instantly on joining,
            // no need to wait for the others' positions
            int rank = (int)(Net.TeamMode ? Net.MyId / 2 : Net.MyId);
            for (int k = 0; k < list.Count; k++)
            {
                var ap = aps[list[(rank + k) % list.Count]];
                if (!avoidOccupied || !AirfieldTaken(ap)) return ap;     // match start: rank alone gives everyone a different field
            }
            return aps[list[rank % list.Count]];
        }

        /// <summary>Another pilot sits on (or just above) this airfield.</summary>
        bool AirfieldTaken(WorldBuilder.Airport ap)
        {
            foreach (var kv in Net.Ghosts)
            {
                var g = kv.Value;
                if (!g || g.Dead) continue;
                Vector3 p = g.transform.position, c = ap.Center3;
                if (new Vector2(p.x - c.x, p.z - c.z).magnitude < ap.length * 0.5f + 800f && p.y - c.y < 200f) return true;
            }
            return false;
        }

        /// <summary>A client just learnt the host runs a team battle → move to the team's base (only right after joining).</summary>
        void OnTeamModeChanged()
        {
            if (!Multiplayer) return;
            if (Time.time - mpStartTime < 30f && playerGear.OnGround && !playerDmg.Dead) TeleportTo(MultiplayerBase(), false);
            Banner($"{Net.ModeName}{(Net.TeamMode ? " — YOU ARE TEAM " + NetSession.TeamName(Net.MyTeam) : "")}", 6f);
        }

        /// <summary>The host pressed START (or I joined a match in progress): everyone takes off from their base.</summary>
        void OnMatchStarted()
        {
            if (Menu && Menu.IsOpen) Menu.Close();
            if (Map && Map.IsOpen) Map.Close();
            ParkAtBase();                                       // revive / rearm / refuel
            MultiplayerAirStart(false);                         // then straight into the arena
            mpStartTime = Time.time;
            Banner($"MATCH START — {Net.ModeName}{(Net.TeamMode ? " — TEAM " + NetSession.TeamName(Net.MyTeam) : "")} — {Net.RoomMinutes} MIN", 6f);
        }

        /// <summary>Results shown → back to the room's waiting area (menu with the lobby).</summary>
        void OnBackToLobby()
        {
            if (!Multiplayer) return;
            ParkAtBase();
            if (Menu && !Menu.IsOpen) Menu.OpenLobby();
        }

        void ParkAtBase()
        {
            if (playerDmg.Dead) { playerDmg.Revive(); playerFm.Destroyed = false; player.GetComponent<Rigidbody>().isKinematic = false; playerWeapons.enabled = true; deadTimer = 0f; if (slowMo != null) StopCoroutine(slowMo); slowMo = null; Time.timeScale = 1f; HidePlayerJet(false); if (playerVr) playerVr.StopDeathCam(); }
            if (playerAutoland) playerAutoland.Disengage("");
            playerDmg.Repair();
            playerWeapons.Rearm(511, 4, 30);
            playerFm.SetFuel(playerFm.maxFuel);
            TeleportTo(MultiplayerBase(), false);
        }

        void OnMultiplayerEnded()
        {
            enemyList.RemoveAll(e => e == null || e.GetComponent<NetGhost>() != null);
            wavesStarted = false;
            if (Mode == GameMode.Multiplayer)
            {
                Mode = GameMode.FreeFlight;   // keep flying; Esc / MENU returns to the mode menu
                Banner("LEFT MULTIPLAYER — " + Net.Status + " — FREE FLIGHT (Esc = MENU)", 6f);
            }
        }

        /// <summary>Full reload (regenerates the world — slow). Normal play uses Respawn().</summary>
        public void Restart() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

        /// <summary>Instant respawn at the nearest airfield, ready for takeoff (no scene reload).</summary>
        public void Respawn()
        {
            if (!playerDmg.Dead) return;
            if (Map && Map.IsOpen) Map.Close();
            var ap = Multiplayer ? null : WorldBuilder.Nearest(player.transform.position);
            playerDmg.Revive();
            playerFm.Destroyed = false;
            if (slowMo != null) StopCoroutine(slowMo); slowMo = null; Time.timeScale = 1f;
            HidePlayerJet(false);
            if (playerVr) playerVr.StopDeathCam();
            var rb = player.GetComponent<Rigidbody>();
            rb.isKinematic = false;
            playerWeapons.enabled = true;
            playerWeapons.Rearm(511, 4, 30);
            playerFm.SetFuel(playerFm.maxFuel);
            deadTimer = 0f;
            if (playerAutoland) playerAutoland.Disengage("");
            if (ap == null)
            {
                // PvP: back in the air near the fight (during a match); in the lobby, on my own airfield
                if (Net.Phase == NetSession.MatchPhase.Lobby) { ap = MultiplayerBase(true); TeleportTo(ap, false); }
                else { MultiplayerAirStart(true); Banner("RESPAWN — BACK IN THE FIGHT", 4f); }
                return;
            }
            TeleportTo(ap, false);
            Banner($"RESPAWN — {ap.name} RWY {ap.RunwayName(1)}", 4f);
        }

        // ------------------------------------------------------------------ Player
        void SpawnPlayer()
        {
            player = new GameObject("Player F-16");
            var home = WorldBuilder.Home;
            Vector3 spawn = startOnRunway
                ? home.Threshold(1f) + home.Dir * 150f + home.up * (0.15f + 1.85f)   // on the centreline, gear on the runway
                : new Vector3(0f, startAltitude, -15000f);
            player.transform.SetPositionAndRotation(spawn, startOnRunway ? home.Rotation : Quaternion.identity);
            player.AddComponent<Rigidbody>();
            playerFm = player.AddComponent<FlightModel>();
            var col = player.AddComponent<CapsuleCollider>();
            col.direction = 2; col.radius = 1.1f; col.height = 13f; col.center = new Vector3(0, 0.3f, 1.5f);
            var wingCol = player.AddComponent<BoxCollider>();
            wingCol.center = new Vector3(0, 0.25f, -1.5f); wingCol.size = new Vector3(9.4f, 0.4f, 3.5f);

            playerDmg = player.AddComponent<Damageable>();
            playerDmg.team = Team.Blue;
            playerDmg.OnDeath += OnPlayerDeath;
            playerGear = player.AddComponent<LandingGear>();
            playerGear.Init(playerFm);

            var parts = AircraftFactory.BuildExterior(player.transform, new Color(0.55f, 0.58f, 0.62f), true);
            AircraftFactory.BuildCockpit(player.transform, out var stickPivot, out var throttleHandle, out var hudAnchor, out var lMfd, out var rMfd);
            player.AddComponent<NozzleFx>().Init(playerFm, parts.nozzleFlame);
            player.AddComponent<ControlSurfaceFx>().Init(playerFm, parts);
            player.AddComponent<VaporFx>().Init(playerFm, ModelKit.Available ? ModelKit.WingTip : new Vector3(4.75f, 0.3f, -2.2f));
            player.AddComponent<AircraftLights>().Init(AircraftLights.WingTipPos, AircraftLights.TailPos, true, AircraftFactory.EyePoint);

            playerWeapons = player.AddComponent<Weapons>();
            playerWeapons.team = Team.Blue;
            playerWeapons.alwaysSeek = true;
            playerWeapons.hudDesignation = true;    // the boxed target is the missile's target, at any range in view
            playerWeapons.seekerCone = 32f;
            playerWeapons.aimAssistDeg = 2.5f;      // light gun magnetism
            playerWeapons.bulletProximity = 7f;     // rounds within 7 m of a bandit count as hits (forgiving, not automatic)
            var assist = player.AddComponent<CombatAssist>();
            assist.fm = playerFm; assist.weapons = playerWeapons; assist.self = playerDmg; assist.targets = () => enemyList;
            playerWeapons.gunMuzzle = parts.gunMuzzle;
            playerWeapons.boresight = Build.Node("GunBoresight", player.transform, AircraftFactory.EyePoint);   // HUD gun cross line
            playerWeapons.missileRails = parts.rails;
            foreach (var m in parts.railMissiles) playerWeapons.RegisterRailModel(m);
            playerWeapons.targetProvider = () => enemyList;

            // ---- VR rig: tracking space anchored at the design eye point ----
            var tracking = Build.Node("TrackingSpace", player.transform, AircraftFactory.EyePoint);
            var camGo = new GameObject("PilotCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(tracking, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 60000f;
            cam.fieldOfView = 75f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.55f, 0.7f, 0.9f);
            camGo.AddComponent<AudioListener>();
            GraphicsSetup.Apply(cam, Application.isMobilePlatform);
            camGo.AddComponent<SunGlare>().Init(cam);      // sun glare in the pilot's eyes
            // OnAudioFilterRead must not share a GameObject with the AudioListener → separate child object
            var audioGo = new GameObject("CockpitAudio");
            audioGo.transform.SetParent(camGo.transform, false);
            Audio = audioGo.AddComponent<AudioSynth>();

            var vr = player.AddComponent<VRCockpitInput>();
            vr.trackingSpace = tracking;
            vr.head = camGo.transform;
            vr.stickPivot = stickPivot;
            vr.throttleHandle = throttleHandle;
            vr.eyePointLocal = AircraftFactory.EyePoint;
            vr.Throttle = startOnRunway ? 0f : 0.75f;
            vr.gearDeploy = () => playerGear.Deploy;
            vr.currentMode = () => playerWeapons.mode;
            vr.isDead = () => playerDmg.Dead;
            vr.Init(player.transform, cam);

            var hud = player.AddComponent<HUD>();
            hud.fm = playerFm; hud.weapons = playerWeapons; hud.self = playerDmg; hud.game = this; hud.vr = vr; hud.gear = playerGear;
            hud.Build(hudAnchor, lMfd, rMfd, cam);
            var cockpitHudAnchor = hudAnchor;
            var canopyT = parts.canopy;
            vr.OnViewChanged = chase =>
            {
                hud.SetAnchor(chase != null ? chase : cockpitHudAnchor);
                if (canopyT) canopyT.gameObject.SetActive(chase != null);   // no glass streaks in front of the pilot's eyes
            };
            if (canopyT) canopyT.gameObject.SetActive(false);

            playerVr = vr;
            playerAutoland = player.AddComponent<Autoland>();
            playerAutoland.fm = playerFm; playerAutoland.gear = playerGear;
            hud.autoland = playerAutoland;
            vr.autolandActive = () => playerAutoland.Active;
            Map = player.AddComponent<MapView>();
            Map.game = this; Map.fm = playerFm; Map.vr = vr; Map.head = camGo.transform;
            Map.Build(cam);
            var netPanel = player.AddComponent<NetPanel>();
            netPanel.net = Net; netPanel.vr = vr; netPanel.head = camGo.transform;
            netPanel.Build(cam);
            NetPanelRef = netPanel;
            Controls = player.AddComponent<ControlsWindow>();
            Menu = player.AddComponent<MainMenu>();
            Menu.Controls = Controls;
            Menu.game = this; Menu.net = Net; Menu.vr = vr; Menu.head = camGo.transform;
            Menu.Build(cam);
            var pc = player.AddComponent<PlayerController>();
            pc.fm = playerFm; pc.weapons = playerWeapons; pc.self = playerDmg; pc.vr = vr; pc.audioSynth = Audio; pc.cameraT = camGo.transform; pc.gear = playerGear; pc.autoland = playerAutoland;

            if (startOnRunway)
            {
                playerFm.SetEngineInstant(0f);
                playerFm.Launch(Vector3.zero);
            }
            else
            {
                playerGear.SetInstant(false);
                playerGear.parkingBrake = false;
                playerFm.SetEngineInstant(0.75f);
                playerFm.Launch(player.transform.forward * startSpeed);
            }
        }

        /// <summary>Instant relocation: onto the runway (ready for takeoff) or onto a 10 km, 3° final approach.</summary>
        /// <summary>
        /// Lined-up position on the runway. Alone: the centre line. In multiplayer several pilots can share a runway, so
        /// they get staggered slots (left / right of the centre line, 150 m apart) and never a slot someone already sits on.
        /// </summary>
        Vector3 RunwaySpot(WorldBuilder.Airport ap)
        {
            Vector3 thr = ap.Threshold(1f);
            if (!Multiplayer) return thr + ap.Dir * 150f;
            Vector3 right = Vector3.Cross(Vector3.up, ap.Dir).normalized;
            float lane = Mathf.Min(13f, ap.width * 0.5f - 6f);              // wingtips stay on the runway, 26 m between jets
            const int Slots = 10;
            int first = (int)(Net.MyId % Slots);                            // different ids start from different slots
            Vector3 best = thr + ap.Dir * 150f;
            for (int k = 0; k < Slots; k++)
            {
                int s = (first + k) % Slots;
                Vector3 p = thr + ap.Dir * (150f + (s / 2) * 150f) + right * ((s % 2 == 0 ? -1f : 1f) * lane);
                p.y = ap.SurfaceY(p.x, p.z);
                bool taken = false;
                foreach (var kv in Net.Ghosts)
                {
                    var g = kv.Value;
                    if (!g || g.Dead) continue;
                    Vector3 q = g.transform.position;
                    if (new Vector2(q.x - p.x, q.z - p.z).magnitude < 40f && Mathf.Abs(q.y - p.y) < 30f) { taken = true; break; }
                }
                if (!taken) return p;
                if (k == 0) best = p;
            }
            return best;
        }

        public void TeleportTo(WorldBuilder.Airport ap, bool onFinal)
        {
            if (playerDmg.Dead) return;
            var rb = player.GetComponent<Rigidbody>();
            Vector3 pos; Quaternion rot = ap.Rotation;
            if (onFinal)
            {
                Vector3 aim = ap.Threshold(1f) + ap.Dir * 300f;
                pos = aim - ap.Dir * 10000f + Vector3.up * (Mathf.Tan(3f * Mathf.Deg2Rad) * 10000f);
                rot = Quaternion.LookRotation(Quaternion.AngleAxis(-3f, Vector3.Cross(Vector3.up, ap.Dir)) * ap.Dir) * Quaternion.Euler(-10f, 0, 0);
            }
            else pos = RunwaySpot(ap) + ap.up * (0.15f + 1.85f);

            rb.position = pos; rb.rotation = rot;
            player.transform.SetPositionAndRotation(pos, rot);
            rb.angularVelocity = Vector3.zero;
            playerGear.SetInstant(true);
            playerGear.ResetState();
            playerGear.parkingBrake = !onFinal;
            float thr = onFinal ? 0.55f : 0f;
            playerFm.SetEngineInstant(thr);
            playerVr.Throttle = thr;
            playerFm.Launch(onFinal ? ap.Dir * 80f + Vector3.down * (80f * Mathf.Tan(3f * Mathf.Deg2Rad)) : Vector3.zero);
            servicedThisStop = !onFinal ? false : servicedThisStop;
            Banner(onFinal ? $"{ap.name} RWY {ap.RunwayName(1)} — 10 KM FINAL" : $"{ap.name} RWY {ap.RunwayName(1)} — READY FOR TAKEOFF", 6f);
        }

        // ------------------------------------------------------------------ shot down: wreck + death camera
        readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        readonly List<Collider> hiddenColliders = new List<Collider>();

        void HidePlayerJet(bool hide)
        {
            if (hide)
            {
                hiddenRenderers.Clear(); hiddenCanvases.Clear(); hiddenColliders.Clear();
                var rig = playerVr ? playerVr.trackingSpace : null;
                foreach (var r in player.GetComponentsInChildren<Renderer>())
                    if (r.enabled && (rig == null || !r.transform.IsChildOf(rig))) { r.enabled = false; hiddenRenderers.Add(r); }
                foreach (var c in player.GetComponentsInChildren<Canvas>())
                    if (c.enabled && (rig == null || !c.transform.IsChildOf(rig))) { c.enabled = false; hiddenCanvases.Add(c); }
                foreach (var c in player.GetComponentsInChildren<Collider>())
                    if (c.enabled) { c.enabled = false; hiddenColliders.Add(c); }
            }
            else
            {
                foreach (var r in hiddenRenderers) if (r) r.enabled = true;
                foreach (var c in hiddenCanvases) if (c) c.enabled = true;
                foreach (var c in hiddenColliders) if (c) c.enabled = true;
                hiddenRenderers.Clear(); hiddenCanvases.Clear(); hiddenColliders.Clear();
            }
        }

        Coroutine slowMo;
        System.Collections.IEnumerator DeathSlowMo()
        {
            Time.timeScale = 0.35f;                               // the break-up in slow motion
            yield return new WaitForSecondsRealtime(1.3f);
            float t = 0f;
            while (t < 0.6f) { t += Time.unscaledDeltaTime; Time.timeScale = Mathf.Lerp(0.35f, 1f, t / 0.6f); yield return null; }
            Time.timeScale = 1f;
        }

        void OnPlayerDeath(Damageable d)
        {
            if (Multiplayer) Net.ReportDeath();
            else
            {
                bool hit = d.LastAttacker != null && Time.time - d.LastHitTime < 20f;
                KillFeed.Add(hit ? d.LastAttacker.callsign : "", NetSession.Nick, hit ? d.LastWeapon : (byte)0, KillFeed.Hostile);
            }
            playerFm.Destroyed = true;
            var rb = player.GetComponent<Rigidbody>();
            if (playerAutoland) playerAutoland.Disengage("");
            var wreck = Wreck.Create(player.transform, rb.linearVelocity, rb.angularVelocity);
            HidePlayerJet(true);
            if (playerVr) playerVr.StartDeathCam(wreck);
            if (!Multiplayer) slowMo = StartCoroutine(DeathSlowMo());
            rb.linearVelocity *= 0.2f;
            rb.isKinematic = true;
            playerWeapons.triggerHeld = false;
            playerWeapons.enabled = false;
            Banner($"SHOT DOWN — WAVE {Wave}, KILLS {Kills}", 9f);
        }

        // ------------------------------------------------------------------ Enemies
        void StartWave(int n)
        {
            Wave = n;
            nextWaveTimer = -1f;
            enemyList.RemoveAll(e => e == null || e.Dead);
            int count = Mathf.Clamp(1 + (n + 1) / 2 + (Difficulty - 1), 1, 6);
            Banner($"WAVE {n} — {count} BANDIT{(count > 1 ? "S" : "")}", 5f);
            Vector3 pp = player.transform.position;
            Vector3 fwd = player.transform.forward; fwd.y = 0; fwd.Normalize();
            for (int i = 0; i < count; i++)
            {
                float dist = Random.Range(9000f, 14000f);
                float ang = Random.Range(-35f, 35f);
                Vector3 pos = pp + Quaternion.Euler(0, ang, 0) * fwd * dist;
                pos.x = Mathf.Clamp(pos.x, -WorldBuilder.PlayArea, WorldBuilder.PlayArea);
                pos.z = Mathf.Clamp(pos.z, -WorldBuilder.PlayArea, WorldBuilder.PlayArea);
                pos.y = Mathf.Max(TerrainHeight(pos.x, pos.z) + 1500f, Random.Range(2500f, 5000f));
                Vector3 dir = (pp - pos); dir.y = 0;
                SpawnEnemy(pos, Quaternion.LookRotation(dir.normalized), n);
            }
        }

        int banditCount;
        void SpawnEnemy(Vector3 pos, Quaternion rot, int wave)
        {
            var go = new GameObject("Bandit");
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<Rigidbody>();
            var fm = go.AddComponent<FlightModel>();
            fm.maxG = 8f;
            fm.infiniteFuel = true;
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = 2; col.radius = 1.4f; col.height = 15f; col.center = new Vector3(0, 0.3f, 1f);
            var wingCol = go.AddComponent<BoxCollider>();
            wingCol.center = new Vector3(0, 0.25f, -1.5f); wingCol.size = new Vector3(9.4f, 0.6f, 3.5f);

            var dmg = go.AddComponent<Damageable>();
            dmg.team = Team.Red;
            var parts = AircraftFactory.BuildExterior(go.transform, new Color(0.62f, 0.5f, 0.38f), false);
            go.AddComponent<NozzleFx>().Init(fm, parts.nozzleFlame);
            go.AddComponent<ControlSurfaceFx>().Init(fm, parts);
            go.AddComponent<VaporFx>().Init(fm, ModelKit.Available ? ModelKit.WingTip : new Vector3(4.75f, 0.3f, -2.2f));
            go.AddComponent<AircraftLights>().Init(AircraftLights.WingTipPos, AircraftLights.TailPos, false, Vector3.zero);

            var w = go.AddComponent<Weapons>();
            w.team = Team.Red;
            w.gunMuzzle = parts.gunMuzzle;
            w.missileRails = parts.rails;
            foreach (var m in parts.railMissiles) w.RegisterRailModel(m);
            w.gunDamage = 2.5f + Difficulty * 1.0f;
            w.lockTime = 2.6f - Difficulty * 0.5f;
            int msl = Mathf.Clamp(wave - 1 + (Difficulty == 2 ? 1 : 0) - (Difficulty == 0 ? 1 : 0), 0, 2);
            w.Rearm(400, msl, 30);
            for (int i = msl; i < parts.railMissiles.Count; i++) parts.railMissiles[i].SetActive(false); // rails are emptied from the highest index down
            w.targetProvider = () => new[] { playerDmg };

            var ai = go.AddComponent<EnemyAI>();
            ai.fm = fm; ai.weapons = w; ai.self = dmg; ai.target = playerDmg;
            ai.loadGun = 400; ai.loadMissiles = msl; ai.loadFlares = 30;
            ai.skill = Mathf.Clamp(0.25f + Difficulty * 0.15f + wave * 0.07f, 0.25f, 1f);

            dmg.callsign = $"BANDIT {++banditCount}";
            dmg.OnDeath += d =>
            {
                bool hit = d.LastAttacker != null && Time.time - d.LastHitTime < 20f;
                bool mine = hit && d.LastAttacker == playerDmg;
                KillFeed.Add(hit ? d.LastAttacker.callsign : "", d.callsign, hit ? d.LastWeapon : (byte)0, mine ? KillFeed.Mine : KillFeed.Neutral);
                var erb = go.GetComponent<Rigidbody>();
                Wreck.Create(go.transform, erb ? erb.linearVelocity : go.transform.forward * 200f, erb ? erb.angularVelocity : Vector3.zero);
                Kills++;
                fm.Destroyed = true;
                w.enabled = false;
                ai.enabled = false;
                Destroy(go, 0.05f);
            };

            fm.throttle = 0.85f;
            fm.Launch(go.transform.forward * Random.Range(210f, 250f));
            enemyList.Add(dmg);
        }
    }

    /// <summary>
    /// 노즐 화염: 엔진이 돌고 있으면 항상 보인다 (아이들~MIL = 어두운 주황빛 열기, 출력에 따라 길어짐 / 애프터버너 = 길고 밝은 화염).
    /// </summary>
    public class NozzleFx : MonoBehaviour
    {
        FlightModel fm; Transform flame; Material mat; float exitZ;
        public void Init(FlightModel f, Transform t)
        {
            fm = f; flame = t;
            if (t) exitZ = t.localPosition.z + 0.65f;   // the flame was placed 0.65 m behind the nozzle exit
            var r = t ? t.GetComponent<Renderer>() : null;
            if (r) mat = r.material;   // own instance so each jet can glow differently
        }
        void Update()
        {
            if (!fm || !flame) return;
            Apply(flame, mat, fm.Destroyed || fm.Flameout ? 0f : fm.EngineRpm, exitZ);
        }

        /// <summary>Shared by local jets and network ghosts.</summary>
        public static void Apply(Transform flame, Material mat, float rpm, float exitZ)
        {
            float ab = Mathf.InverseLerp(FlightModel.MilDetent, 1f, rpm);          // 0..1 afterburner
            float dry = Mathf.Clamp01(rpm / FlightModel.MilDetent);                 // 0..1 up to MIL
            bool on = rpm > 0.005f;
            if (flame.gameObject.activeSelf != on) flame.gameObject.SetActive(on);
            if (!on) return;
            float flicker = 0.92f + Random.value * 0.12f;
            // dry: short glow, grows with power; afterburner: up to 4x longer
            float len = Mathf.Lerp(0.45f, 1.1f, dry) + ab * 3f;
            float wid = Mathf.Lerp(0.55f, 0.75f, dry) + ab * 0.1f;
            flame.localScale = new Vector3(wid, wid, len) * flicker;
            var lp = flame.localPosition; lp.z = exitZ - len * 0.5f + 0.1f; flame.localPosition = lp;   // glow starts right at the nozzle
            if (mat)
            {
                // dull red-orange heat at idle → hot orange at MIL → white-yellow afterburner (HDR → bloom)
                Color dryCol = Color.Lerp(new Color(1.2f, 0.35f, 0.12f), new Color(2.2f, 0.9f, 0.3f), dry);
                Color c = Color.Lerp(dryCol, new Color(4f, 1.8f, 0.6f), ab);
                c.a = Mathf.Lerp(0.35f, 0.55f, dry) + ab * 0.1f;
                Mats.SetColor(mat, c);
            }
        }
    }
}
