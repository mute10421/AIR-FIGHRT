using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// 인터넷 PvP 멀티플레이 (Unity Multiplayer Services 세션 = Relay + Lobby). 방 목록에서 골라 들어가거나 방을 만든다.
    /// 방장이 모드(개인전/팀전)·시간대(낮/해질녘/밤)·경기 시간을 정하고 START → 모두 출격 → 시간 종료 시 결과 → 대기실.
    /// 넷코드는 NGO의 Custom Named Message만 사용(네트워크 프리팹 없음):
    ///  - 각자 자기 기체를 시뮬레이션하고 20Hz로 상태를 보냄 (호스트가 중계)
    ///  - 명중 판정은 쏜 사람이 하고, 데미지를 피격자에게 보냄 → 피격자가 체력/격추를 결정
    ///  - 미사일 위치·플레어도 공유 → 상대 화면에서 미사일 경보, 플레어 기만이 동작
    /// </summary>
    public class NetSession : MonoBehaviour
    {
        public static NetSession I { get; private set; }

        public const int MaxPlayers = 8;
        const string Msg = "AF";
        const byte TState = 1, TDamage = 2, TKill = 3, TFlare = 4, TName = 5, TMatch = 6;

        // ---- my callsign (kill feed, scoreboard, room names) ----
        public const int NickMax = 12;
        public static string Nick
        {
            get
            {
                string n = null;
                try { n = PlayerPrefs.GetString("AF_Nick", ""); } catch { }
                if (string.IsNullOrEmpty(n)) { n = "PILOT" + UnityEngine.Random.Range(100, 1000); try { PlayerPrefs.SetString("AF_Nick", n); } catch { } }
                return n;
            }
            set
            {
                var c = Sanitize(value);
                try { PlayerPrefs.SetString("AF_Nick", c.Length > 0 ? c : "PILOT"); PlayerPrefs.Save(); } catch { }
            }
        }
        /// <summary>Allowed in a callsign: A-Z, 0-9, _ -, and Hangul (syllables and jamo).</summary>
        public static bool NickChar(char ch) =>
            (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' ||
            (ch >= '\uAC00' && ch <= '\uD7A3') || (ch >= '\u3131' && ch <= '\u318E');
        public static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (s ?? "").ToUpperInvariant())
                if (NickChar(ch) && sb.Length < NickMax) sb.Append(ch);
            return sb.ToString();
        }

        // ---- match: the host sets mode / time of day / length, then starts it ----
        public enum MatchPhase : byte { Lobby = 0, Playing = 1, Results = 2 }
        public static readonly int[] MinuteOptions = { 3, 5, 10, 15, 20 };
        public static DayTime PrefTime
        {
            get { try { return (DayTime)PlayerPrefs.GetInt("AF_RoomTime", 0); } catch { return DayTime.Day; } }
            set { PlayerPrefs.SetInt("AF_RoomTime", (int)value); PlayerPrefs.Save(); }
        }
        public static int PrefMinutes
        {
            get { try { return PlayerPrefs.GetInt("AF_RoomMin", 10); } catch { return 10; } }
            set { PlayerPrefs.SetInt("AF_RoomMin", value); PlayerPrefs.Save(); }
        }
        public MatchPhase Phase { get; private set; }
        public DayTime RoomTime { get; private set; }
        public int RoomMinutes { get; private set; } = 10;
        public float Remaining { get; private set; }
        public string RoomName { get; private set; } = "";
        public string ResultText { get; private set; } = "";
        public bool IsHost => Active && nm.IsServer;
        public event Action MatchStarted, MatchEnded, ReturnedToLobby;
        float matchSendTimer, nameSendTimer, resultsTimer;

        // ---- room browser ----
        public class RoomInfo { public string id, host; public bool team; public DayTime time; public int minutes, players, max; }
        public readonly List<RoomInfo> Rooms = new List<RoomInfo>();
        public bool Refreshing { get; private set; }
        public string RoomsStatus { get; private set; } = "";

        public bool Active => session != null && nm != null && nm.IsListening && (nm.IsServer || nm.IsConnectedClient);
        public bool Busy { get; private set; }
        public string Status { get; private set; } = "OFFLINE";
        public ulong MyId => nm != null ? nm.LocalClientId : 0;
        public int MyKills { get; private set; }
        // ---- game type: free-for-all or team battle (BLUE = even player ids, RED = odd; the host's choice wins) ----
        public bool PreferTeam
        {
            get { try { return PlayerPrefs.GetInt("AF_TeamMode", 0) == 1; } catch { return false; } }
            set { PlayerPrefs.SetInt("AF_TeamMode", value ? 1 : 0); PlayerPrefs.Save(); }
        }
        public bool TeamMode { get; private set; }
        public event Action TeamModeChanged;
        public static int TeamOf(ulong id) => (int)(id % 2);
        public int MyTeam => TeamOf(MyId);
        public static string TeamName(int t) => t == 0 ? "BLUE" : "RED";
        public string ModeName => TeamMode ? "TEAM BATTLE" : "FREE-FOR-ALL";
        public bool IsAlly(ulong id) => TeamMode && TeamOf(id) == MyTeam;
        public int MyDeaths { get; private set; }
        public IReadOnlyDictionary<ulong, NetGhost> Ghosts => ghosts;
        /// <summary>A remote player's missile is tracking me (for the RWR / HUD warning).</summary>
        public bool IncomingMissile { get; private set; }
        /// <summary>Highest remote seeker state aimed at me: 0 none, 1 tracking, 2 locked.</summary>
        public int LockOnMe { get; private set; }

        public event Action SessionStarted, SessionEnded;

        ISession session;
        NetworkManager nm;
        bool handlerRegistered;
        float sendTimer;
        ulong lastAttacker = ulong.MaxValue;
        byte lastWeapon;
        float lastAttackTime = -99f;
        readonly Dictionary<ulong, NetGhost> ghosts = new Dictionary<ulong, NetGhost>();
        readonly List<ulong> targets = new List<ulong>();

        // local player refs (set by GameManager)
        public FlightModel fm; public Weapons weapons; public Damageable self; public LandingGear gear;

        void Awake() { I = this; }

        // ------------------------------------------------------------------ setup
        void EnsureNetworkManager()
        {
            nm = NetworkManager.Singleton;
            if (nm == null)
            {
                var go = new GameObject("NetworkManager");
                var tr = go.AddComponent<UnityTransport>();
                nm = go.AddComponent<NetworkManager>();
                nm.NetworkConfig = new NetworkConfig { NetworkTransport = tr, EnableSceneManagement = false, TickRate = 30 };
                nm.SetSingleton();
            }
            if (nm.NetworkConfig != null) nm.NetworkConfig.EnableSceneManagement = false;
            nm.OnClientDisconnectCallback -= OnClientDisconnect;
            nm.OnClientDisconnectCallback += OnClientDisconnect;
        }

        static async Task SignIn()
        {
            // a separate sign-in profile per running copy of the game: the editor, Multiplayer Play Mode virtual players and
            // standalone builds on the same PC then count as different pilots (otherwise they share one player id)
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile("pilot" + System.Diagnostics.Process.GetCurrentProcess().Id);
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        // room name carries the host's settings for the room list: "CALLSIGN|T|2|10" (T/F mode, time of day, minutes)
        static string EncodeName(string host, bool team, DayTime t, int min) => $"{host}|{(team ? "T" : "F")}|{(int)t}|{min}";
        static RoomInfo DecodeName(string name)
        {
            var r = new RoomInfo { host = name ?? "?", minutes = 10 };
            var p = (name ?? "").Split('|');
            if (p.Length >= 4)
            {
                r.host = p[0]; r.team = p[1] == "T";
                if (int.TryParse(p[2], out int t)) r.time = (DayTime)Mathf.Clamp(t, 0, 2);
                if (int.TryParse(p[3], out int m)) r.minutes = m;
            }
            return r;
        }
        public static string TimeName(DayTime t) => t == DayTime.Day ? "DAY" : t == DayTime.Dusk ? "DUSK" : "NIGHT";
        public static string Describe(RoomInfo r) => $"{r.host}  ·  {(r.team ? "TEAM" : "FFA")}  ·  {TimeName(r.time)}  ·  {r.minutes} MIN  ·  {r.players}/{r.max}";

        SessionOptions Options() => new SessionOptions { MaxPlayers = MaxPlayers, IsPrivate = false, Name = EncodeName(Nick, PreferTeam, PrefTime, PrefMinutes) }.WithRelayNetwork();

        /// <summary>Create a room with my settings (PreferTeam / PrefTime / PrefMinutes); I become its host.</summary>
        public async void Host() => await Run("CREATING ROOM...", async () => session = await MultiplayerService.Instance.CreateSessionAsync(Options()));

        /// <summary>Quick match: jump into any open room, or create one with my settings if none is free.</summary>
        public async void QuickMatch() => await Run("QUICK MATCH...", async () =>
            session = await MultiplayerService.Instance.MatchmakeSessionAsync(new QuickJoinOptions { CreateSession = true, Timeout = TimeSpan.FromSeconds(5) }, Options()));

        public async void JoinRoom(RoomInfo r)
        {
            if (r == null) return;
            await Run($"JOINING {r.host}...", async () => session = await MultiplayerService.Instance.JoinSessionByIdAsync(r.id));
        }

        /// <summary>Fetch the list of open rooms.</summary>
        public async void RefreshRooms()
        {
            if (Refreshing || Busy || Active) return;
            Refreshing = true; RoomsStatus = "SEARCHING ROOMS...";
            try
            {
                await SignIn();
                var res = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions { Count = 20 });
                Rooms.Clear();
                foreach (var si in res.Sessions)
                {
                    if (si == null || si.AvailableSlots <= 0 || si.IsLocked) continue;
                    var r = DecodeName(si.Name);
                    r.id = si.Id; r.max = si.MaxPlayers; r.players = si.MaxPlayers - si.AvailableSlots;
                    Rooms.Add(r);
                }
                RoomsStatus = Rooms.Count == 0 ? "NO ROOMS — CREATE ONE" : $"{Rooms.Count} ROOM{(Rooms.Count > 1 ? "S" : "")}";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AirFight] Room list: " + e);
                RoomsStatus = "ROOM LIST FAILED: " + Short(e.Message);
            }
            finally { Refreshing = false; }
        }

        async Task Run(string status, Func<Task> action)
        {
            if (Busy || Active) return;
            Busy = true; Status = status;
            try
            {
                EnsureNetworkManager();
                await SignIn();
                await action();
                // wait for the transport to come up
                float t = 0f;
                while (!Active && t < 15f) { await Task.Delay(100); t += 0.1f; }
                if (!Active) throw new Exception("connection timed out");
                RegisterHandler();
                MyKills = 0; MyDeaths = 0;
                Phase = MatchPhase.Lobby; Remaining = 0f; ResultText = "";
                if (nm.IsServer)
                {
                    TeamMode = PreferTeam; RoomTime = PrefTime; RoomMinutes = PrefMinutes;
                    RoomName = Nick;
                    TimeOfDay.SetSession(RoomTime);
                }
                else { TeamMode = false; RoomName = "..."; }                   // clients learn the host's settings from its match message
                nameSendTimer = 0f; matchSendTimer = 0f;
                Status = nm.IsServer ? "ROOM CREATED — YOU ARE THE HOST" : "JOINED ROOM";
                session.Deleted += OnSessionGone;
                SessionStarted?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AirFight] Multiplayer: " + e);
                Status = "FAILED: " + Short(e.Message);
                session = null;
            }
            finally { Busy = false; }
        }

        static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 60 ? s.Substring(0, 60) : s);

        public async void Leave()
        {
            if (session == null) return;
            var s = session; session = null;
            try { await s.LeaveAsync(); } catch (Exception e) { Debug.LogWarning(e.Message); }
            if (nm != null && nm.IsListening) nm.Shutdown();
            EndCleanup("OFFLINE");
        }

        void OnSessionGone() { session = null; if (nm != null && nm.IsListening) nm.Shutdown(); EndCleanup("ROOM CLOSED"); }

        void EndCleanup(string status)
        {
            foreach (var g in ghosts.Values) if (g) Destroy(g.gameObject);
            ghosts.Clear();
            handlerRegistered = false;
            IncomingMissile = false;
            Phase = MatchPhase.Lobby;
            TimeOfDay.SetSession(null);                                     // back to my own time of day
            Status = status;
            SessionEnded?.Invoke();
        }

        void OnClientDisconnect(ulong id)
        {
            if (nm != null && !nm.IsServer && id == nm.LocalClientId) { session = null; EndCleanup("DISCONNECTED"); return; }
            RemoveGhost(id);
        }

        void RegisterHandler()
        {
            if (handlerRegistered) return;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Msg, OnMessage);
            handlerRegistered = true;
        }

        void OnDestroy() { if (session != null) Leave(); }

        // ------------------------------------------------------------------ sending
        void Send(FastBufferWriter w, NetworkDelivery delivery)
        {
            if (!Active) return;
            var cm = nm.CustomMessagingManager;
            if (nm.IsServer)
            {
                targets.Clear();
                foreach (var id in nm.ConnectedClientsIds) if (id != nm.LocalClientId) targets.Add(id);
                if (targets.Count > 0) cm.SendNamedMessage(Msg, targets, w, delivery);
            }
            else cm.SendNamedMessage(Msg, NetworkManager.ServerClientId, w, delivery);
        }

        void Update()
        {
            if (!Active || fm == null) { IncomingMissile = false; LockOnMe = 0; return; }
            float udt = Time.unscaledDeltaTime;
            sendTimer -= udt;
            if (sendTimer <= 0f) { sendTimer = 0.05f; SendState(); }
            nameSendTimer -= udt;
            if (nameSendTimer <= 0f) { nameSendTimer = 3f; SendName(); }

            // match clock: the host runs it and tells everybody once a second; clients just count down in between
            if (Phase == MatchPhase.Playing) Remaining = Mathf.Max(0f, Remaining - udt);
            if (nm.IsServer)
            {
                if (Phase == MatchPhase.Playing && Remaining <= 0f) { SetPhase(MatchPhase.Results); resultsTimer = 10f; SendMatch(); }
                else if (Phase == MatchPhase.Results) { resultsTimer -= udt; if (resultsTimer <= 0f) { SetPhase(MatchPhase.Lobby); SendMatch(); } }
                matchSendTimer -= udt;
                if (matchSendTimer <= 0f) { matchSendTimer = 1f; SendMatch(); }
            }

            bool incoming = false; int lockOn = 0;
            foreach (var g in ghosts.Values)
            {
                if (!g || g.Dead) continue;
                if (g.MissileOnMe) incoming = true;
                lockOn = Mathf.Max(lockOn, g.LockOnMe);
            }
            IncomingMissile = incoming;
            LockOnMe = lockOn;
        }

        void SendState()
        {
            using var w = new FastBufferWriter(512, Allocator.Temp);
            w.WriteValueSafe(TState);
            w.WriteValueSafe(MyId);
            w.WriteValueSafe(fm.transform.position);
            w.WriteValueSafe(fm.transform.rotation);
            w.WriteValueSafe(fm.Body.linearVelocity);
            w.WriteValueSafe(fm.EngineRpm);
            byte flags = 0;
            if (self.Dead) flags |= 1;
            if (weapons.Firing) flags |= 2;
            if (gear != null && gear.Deploy > 0.5f) flags |= 4;
            if (fm.speedBrake) flags |= 8;
            if (nm.IsServer && TeamMode) flags |= 16;                       // the host announces the game type
            w.WriteValueSafe(flags);
            // my seeker: who am I tracking / locked on (lets the victim's RWR sound the lock tone)
            ulong lockTgt = ulong.MaxValue; byte lockState = 0;
            if (weapons.SeekerTarget != null)
                foreach (var kv in ghosts)
                    if (kv.Value && kv.Value.Damage == weapons.SeekerTarget) { lockTgt = kv.Key; lockState = (byte)(weapons.Locked ? 2 : 1); break; }
            w.WriteValueSafe(lockState);
            w.WriteValueSafe(lockTgt);
            w.WriteValueSafe((byte)Mathf.Clamp(Mathf.RoundToInt(self.Health), 0, 255));
            w.WriteValueSafe((short)MyKills);
            w.WriteValueSafe((short)MyDeaths);
            // my missiles in flight
            byte n = 0;
            foreach (var m in Missile.Active) if (m && m.Owner == fm.gameObject) n++;
            n = (byte)Mathf.Min(n, 6);
            w.WriteValueSafe(n);
            byte k = 0;
            foreach (var m in Missile.Active)
            {
                if (!m || m.Owner != fm.gameObject || k >= n) continue;
                k++;
                w.WriteValueSafe(m.NetId);
                w.WriteValueSafe(m.transform.position);
                w.WriteValueSafe(m.Velocity);
                var tg = m.Target ? m.Target.GetComponent<NetGhost>() : null;
                w.WriteValueSafe(tg ? tg.ClientId : ulong.MaxValue);
            }
            Send(w, NetworkDelivery.UnreliableSequenced);
        }

        /// <summary>My bullet/missile hit a remote player's ghost → tell its owner.</summary>
        public void SendDamage(ulong target, float amount, byte weapon)
        {
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(TDamage); w.WriteValueSafe(MyId); w.WriteValueSafe(target); w.WriteValueSafe(amount); w.WriteValueSafe(weapon);
            Send(w, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>I was shot down — credit the last player who hit me.</summary>
        public void ReportDeath()
        {
            MyDeaths++;
            bool credited = Time.time - lastAttackTime < 20f;
            ulong killer = credited ? lastAttacker : ulong.MaxValue;
            byte weapon = credited ? lastWeapon : (byte)0;
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(TKill); w.WriteValueSafe(MyId); w.WriteValueSafe(killer); w.WriteValueSafe(weapon);
            Send(w, NetworkDelivery.ReliableSequenced);
            ShowKill(MyId, killer, weapon);
        }

        void SendName()
        {
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(TName); w.WriteValueSafe(MyId);
            WriteAscii(w, Nick);
            Send(w, NetworkDelivery.ReliableSequenced);
        }

        void SendMatch()
        {
            if (!IsHost) return;
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(TMatch); w.WriteValueSafe(MyId);
            w.WriteValueSafe((byte)Phase); w.WriteValueSafe((byte)(TeamMode ? 1 : 0)); w.WriteValueSafe((byte)RoomTime);
            w.WriteValueSafe((byte)RoomMinutes); w.WriteValueSafe(Remaining);
            WriteAscii(w, RoomName);
            Send(w, NetworkDelivery.ReliableSequenced);
        }

        // short UTF-16 strings (callsigns may be Korean)
        static void WriteAscii(FastBufferWriter w, string s)
        {
            s = s ?? "";
            int n = Mathf.Min(s.Length, 24);
            w.WriteValueSafe((byte)n);
            for (int i = 0; i < n; i++) w.WriteValueSafe((ushort)s[i]);
        }

        static string ReadAscii(FastBufferReader r)
        {
            r.ReadValueSafe(out byte n);
            var c = new char[n];
            for (int i = 0; i < n; i++) { r.ReadValueSafe(out ushort u); c[i] = (char)u; }
            return new string(c);
        }

        // ------------------------------------------------------------------ host controls
        public void HostSetTeam(bool team) { PreferTeam = team; if (!IsHost || Phase != MatchPhase.Lobby) return; if (TeamMode != team) { TeamMode = team; TeamModeChanged?.Invoke(); } SendMatch(); }
        public void HostSetTime(DayTime t) { PrefTime = t; if (!IsHost || Phase != MatchPhase.Lobby) return; RoomTime = t; TimeOfDay.SetSession(t); SendMatch(); }
        public void HostSetMinutes(int m) { PrefMinutes = m; if (!IsHost || Phase != MatchPhase.Lobby) return; RoomMinutes = m; SendMatch(); }
        public void HostStart()
        {
            if (!IsHost || Phase != MatchPhase.Lobby) return;
            Remaining = RoomMinutes * 60f;
            SetPhase(MatchPhase.Playing);
            SendMatch();
        }

        void SetPhase(MatchPhase p)
        {
            if (p == Phase) return;
            var old = Phase;
            Phase = p;
            switch (p)
            {
                case MatchPhase.Playing:
                    MyKills = 0; MyDeaths = 0;
                    TimeOfDay.SetSession(RoomTime);
                    MatchStarted?.Invoke();
                    break;
                case MatchPhase.Results:
                    ResultText = Result();
                    MatchEnded?.Invoke();
                    break;
                case MatchPhase.Lobby:
                    if (old != MatchPhase.Lobby) ReturnedToLobby?.Invoke();
                    break;
            }
        }

        string Result()
        {
            if (TeamMode)
            {
                int[] score = new int[2];
                score[MyTeam] += MyKills;
                foreach (var kv in ghosts) if (kv.Value) score[TeamOf(kv.Key)] += kv.Value.Kills;
                return score[0] == score[1] ? $"DRAW  {score[0]} : {score[1]}" : $"{TeamName(score[0] > score[1] ? 0 : 1)} TEAM WINS  {score[0]} : {score[1]}";
            }
            string best = Nick; int bk = MyKills; bool tie = false;
            foreach (var kv in ghosts)
            {
                if (!kv.Value) continue;
                if (kv.Value.Kills > bk) { bk = kv.Value.Kills; best = kv.Value.Nick; tie = false; }
                else if (kv.Value.Kills == bk) tie = true;
            }
            return bk == 0 ? "NO KILLS — DRAW" : tie ? $"DRAW  ({bk} KILLS)" : $"WINNER  {best}  ({bk} KILLS)";
        }

        /// <summary>Top-of-HUD text: lobby / time left / result.</summary>
        public string MatchClock()
        {
            if (!Active) return "";
            switch (Phase)
            {
                case MatchPhase.Playing: int s = Mathf.CeilToInt(Remaining); return $"{ModeName}   {s / 60:00}:{s % 60:00}";
                case MatchPhase.Results: return "MATCH OVER — " + ResultText;
                default: return IsHost ? "LOBBY — START FROM THE MENU" : "LOBBY — WAITING FOR HOST";
            }
        }

        public string NameOf(ulong id) => id == MyId ? Nick : ghosts.TryGetValue(id, out var g) && g ? g.Nick : $"P{id}";

        public void SendFlare(Vector3 pos, Vector3 vel)
        {
            if (!Active) return;
            using var w = new FastBufferWriter(64, Allocator.Temp);
            w.WriteValueSafe(TFlare); w.WriteValueSafe(MyId); w.WriteValueSafe(pos); w.WriteValueSafe(vel);
            Send(w, NetworkDelivery.ReliableSequenced);
        }

        // ------------------------------------------------------------------ receiving
        void OnMessage(ulong sender, FastBufferReader reader)
        {
            // the host relays every client's message to everybody else
            if (nm.IsServer && sender != nm.LocalClientId)
            {
                int start = reader.Position;                 // payload begins after NGO's message header
                int count = reader.Length - start;
                var bytes = new byte[count];
                reader.ReadBytesSafe(ref bytes, count);
                using var w = new FastBufferWriter(bytes.Length + 8, Allocator.Temp);
                w.WriteBytesSafe(bytes);
                byte type0 = bytes.Length > 0 ? bytes[0] : (byte)0;
                targets.Clear();
                foreach (var id in nm.ConnectedClientsIds) if (id != nm.LocalClientId && id != sender) targets.Add(id);
                if (targets.Count > 0) nm.CustomMessagingManager.SendNamedMessage(Msg, targets, w, type0 == TState ? NetworkDelivery.UnreliableSequenced : NetworkDelivery.ReliableSequenced);
                reader.Seek(start);
            }

            reader.ReadValueSafe(out byte type);
            reader.ReadValueSafe(out ulong origin);
            if (origin == MyId) return;
            switch (type)
            {
                case TState: ReadState(origin, reader); break;
                case TDamage:
                {
                    reader.ReadValueSafe(out ulong target);
                    reader.ReadValueSafe(out float amount);
                    reader.ReadValueSafe(out byte weapon);
                    if (target == MyId && self != null && !self.Dead)
                    {
                        lastAttacker = origin; lastAttackTime = Time.time; lastWeapon = weapon;
                        self.TakeDamage(amount);
                    }
                    break;
                }
                case TKill:
                {
                    reader.ReadValueSafe(out ulong killer);
                    reader.ReadValueSafe(out byte weapon);
                    if (killer == MyId)
                    {
                        MyKills++;
                        // PvP reward: every kill reloads gun, AIM-9s and flares on the spot
                        if (weapons != null && self != null && !self.Dead) weapons.Rearm(511, 4, 30);
                    }
                    ShowKill(origin, killer, weapon);
                    break;
                }
                case TName:
                {
                    string n = Sanitize(ReadAscii(reader));
                    if (n.Length > 0) { var g = GetGhost(origin); if (g.Nick != n) { bool first = g.Nick.StartsWith("P") && g.Nick == $"P{origin}"; g.Nick = n; g.Damage.callsign = n; if (first && GameManager.Instance) GameManager.Instance.Banner($"{n} JOINED", 3f); } }
                    break;
                }
                case TMatch:
                {
                    reader.ReadValueSafe(out byte ph);
                    reader.ReadValueSafe(out byte tm);
                    reader.ReadValueSafe(out byte tod);
                    reader.ReadValueSafe(out byte min);
                    reader.ReadValueSafe(out float rem);
                    string rn = ReadAscii(reader);
                    if (origin != NetworkManager.ServerClientId || nm.IsServer) break;
                    bool team = tm != 0;
                    if (team != TeamMode) { TeamMode = team; TeamModeChanged?.Invoke(); }
                    RoomTime = (DayTime)Mathf.Clamp(tod, 0, 2);
                    RoomMinutes = min;
                    RoomName = rn;
                    TimeOfDay.SetSession(RoomTime);
                    Remaining = rem;
                    SetPhase((MatchPhase)ph);
                    break;
                }
                case TFlare:
                {
                    reader.ReadValueSafe(out Vector3 p);
                    reader.ReadValueSafe(out Vector3 v);
                    Flare.Spawn(p, v, true);
                    break;
                }
            }
        }

        void ReadState(ulong origin, FastBufferReader r)
        {
            r.ReadValueSafe(out Vector3 pos);
            r.ReadValueSafe(out Quaternion rot);
            r.ReadValueSafe(out Vector3 vel);
            r.ReadValueSafe(out float rpm);
            r.ReadValueSafe(out byte flags);
            r.ReadValueSafe(out byte lockState);
            r.ReadValueSafe(out ulong lockTgt);
            r.ReadValueSafe(out byte health);
            r.ReadValueSafe(out short kills);
            r.ReadValueSafe(out short deaths);
            r.ReadValueSafe(out byte n);
            var g = GetGhost(origin);
            g.SetSide(TeamMode, IsAlly(origin));
            g.LockOnMe = lockTgt == MyId ? lockState : 0;
            g.missileBuffer.Clear();
            for (int i = 0; i < n; i++)
            {
                r.ReadValueSafe(out int id);
                r.ReadValueSafe(out Vector3 mp);
                r.ReadValueSafe(out Vector3 mv);
                r.ReadValueSafe(out ulong tgt);
                g.missileBuffer.Add(new NetGhost.MissileState { id = id, pos = mp, vel = mv, targetIsMe = tgt == MyId });
            }
            g.Receive(pos, rot, vel, rpm, flags, health, kills, deaths);
        }

        NetGhost GetGhost(ulong id)
        {
            if (ghosts.TryGetValue(id, out var g) && g) return g;
            g = NetGhost.Create(id);
            ghosts[id] = g;
            GameManager.Instance?.AddRemoteTarget(g.Damage);
            return g;
        }

        void RemoveGhost(ulong id)
        {
            if (!ghosts.TryGetValue(id, out var g)) return;
            ghosts.Remove(id);
            if (GameManager.Instance) GameManager.Instance.Banner($"{(g ? g.Nick : "P" + id)} LEFT", 3f);
            if (g) Destroy(g.gameObject);
        }

        void ShowKill(ulong victim, ulong killer, byte weapon)
        {
            bool crash = killer == ulong.MaxValue;
            Color c = !crash && killer == MyId ? KillFeed.Mine
                    : victim == MyId ? KillFeed.Hostile
                    : TeamMode && !crash && IsAlly(killer) ? KillFeed.Friendly
                    : TeamMode && IsAlly(victim) ? KillFeed.Hostile
                    : KillFeed.Neutral;
            KillFeed.Add(crash ? "" : NameOf(killer), NameOf(victim), weapon, c);
            if (!crash && killer == MyId && GameManager.Instance) GameManager.Instance.Banner($"SPLASH — {NameOf(victim)}  ·  REARMED (GUN · AIM-9 x4 · FLARES)", 3.5f);
        }

        public string Scoreboard()
        {
            var sb = new System.Text.StringBuilder();
            if (TeamMode)
            {
                int[] score = new int[2];
                score[MyTeam] += MyKills;
                foreach (var kv in ghosts) if (kv.Value) score[TeamOf(kv.Key)] += kv.Value.Kills;
                sb.Append($"TEAM BATTLE  BLUE {score[0]} : {score[1]} RED\n");
                sb.Append($"{Nick} (YOU) {TeamName(MyTeam)}  K{MyKills} D{MyDeaths}\n");
                foreach (var kv in ghosts) if (kv.Value) sb.Append($"{kv.Value.Nick} {TeamName(TeamOf(kv.Key))}{(IsAlly(kv.Key) ? " ALLY" : "")}  K{kv.Value.Kills} D{kv.Value.Deaths}\n");
            }
            else
            {
                sb.Append("FREE-FOR-ALL\n");
                sb.Append($"{Nick} (YOU)  K{MyKills} D{MyDeaths}\n");
                foreach (var kv in ghosts) if (kv.Value) sb.Append($"{kv.Value.Nick}  K{kv.Value.Kills} D{kv.Value.Deaths}\n");
            }
            return sb.ToString();
        }
    }
}
