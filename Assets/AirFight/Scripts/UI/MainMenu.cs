using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;

namespace AirFight
{
    /// <summary>
    /// 시작 메뉴 (게임 모드 선택). 활주로 위 기체를 3인칭으로 보여주면서 눈앞에 떠 있는 태블릿.
    ///   AI BATTLE    : 난이도(EASY/NORMAL/ACE) + 출발(활주로/공중) 선택 → AI 웨이브 전투
    ///   FREE FLIGHT  : 적 없이 자유 비행 · 이착륙 연습
    ///   MULTIPLAYER  : 빠른 매칭 / 방 만들기 / 방 코드로 참가 → 대기실(코드·점수판) → 출격
    /// 조작: 검지로 버튼 누르기(VR) · 마우스 클릭 · 키보드(1-3, Enter, Esc). 비행 중 Esc 또는 조종석 MENU 버튼 = 메뉴로.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        public static bool AnyOpen { get; private set; }
        public static int ClosedFrame { get; private set; } = -1;
        internal static void ResetStatics() { AnyOpen = false; ClosedFrame = -1; }

        public GameManager game;
        public NetSession net;
        public VRCockpitInput vr;
        public Transform head;
        public ControlsWindow Controls;
        public bool IsOpen => AnyOpen;

        enum Page { Main, AI, Multi }
        Page page;
        Camera cam;
        Transform panel;
        readonly Dictionary<Page, GameObject> pages = new Dictionary<Page, GameObject>(), pageUi = new Dictionary<Page, GameObject>();
        GameObject gBrowse, gEdit, gSettings, gRooms, gLobby, gStart;
        Transform uBrowse, uSettings;
        const int RoomRows = 4;
        readonly Text[] roomLines = new Text[RoomRows];
        readonly CockpitButton[] roomBtns = new CockpitButton[RoomRows];
        Text nickTxt, roomTxt, settingsTxt, roomsHeader;
        bool nameEdit;
        float roomRefreshTimer;
        readonly Dictionary<Page, List<CockpitButton>> buttons = new Dictionary<Page, List<CockpitButton>>();
        readonly List<Vector3> tips = new List<Vector3>();
        Font font, krFont;
        Text multiStatus, multiBoard, multiHint, aiSummary;
        int difficulty = 1;
        bool airStart;
        bool wasThirdPerson;

        static readonly Color Green = new Color(0.3f, 1f, 0.4f), Amber = new Color(1f, 0.8f, 0.2f), Blue = new Color(0.4f, 0.7f, 1f), Red = new Color(1f, 0.35f, 0.3f);
        const float W = 0.84f, H = 0.66f;          // panel size (m)
        const float CanvasPx = 840f;

        // ------------------------------------------------------------------ build
        public void Build(Camera camera)
        {
            cam = camera;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            krFont = KoreanFont();
            panel = new GameObject("MainMenu").transform;
            panel.SetParent(vr.trackingSpace, false);

            var canvas = MakeCanvas(panel, new Vector2(CanvasPx, CanvasPx * H / W), 0.006f);
            var bg = Rect(canvas, Vector2.zero, ((RectTransform)canvas).sizeDelta).gameObject.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.05f, 0.07f, 0.93f);
            var bar = Rect(canvas, new Vector2(0, 300), new Vector2(CanvasPx, 60)).gameObject.AddComponent<Image>();
            bar.color = new Color(0.1f, 0.25f, 0.35f, 0.95f);
            Label(canvas, new Vector2(0, 300), "AIR FIGHT  ·  F-16 VIPER", 34, new Color(0.9f, 1f, 1f));

            // ---------------- MAIN ----------------
            var main = PageRoot(Page.Main, canvas, out var mainUi);
            Label(mainUi, new Vector2(0, 205), "SELECT MODE", 26, Amber);
            Kr(mainUi, new Vector2(0, 170), "게임 모드를 선택하세요", 22);
            string[] titles = { "AI BATTLE", "FREE FLIGHT", "MULTIPLAYER" };
            string[] kr = { "AI 대전\nAI 적기 웨이브와 공중전", "자유 비행\n적 없이 비행 · 이착륙 연습", "멀티플레이\n방 만들기 / 방 목록에서 참가" };
            Color[] lamp = { Red, Blue, Green };
            System.Action[] act = { () => Show(Page.AI), StartFree, () => Show(Page.Multi) };
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * 0.26f;
                AddButton(Page.Main, main, new Vector3(x, 0.04f, 0f), 3.2f, titles[i], lamp[i], act[i], () => false);
                Kr(mainUi, new Vector2(x * CanvasPx / W, -20), kr[i], 20);
                Label(mainUi, new Vector2(x * CanvasPx / W, -90), $"[{i + 1}]", 20, new Color(0.6f, 0.7f, 0.75f));
            }
            // time of day: day / dusk / night flight
            Label(mainUi, new Vector2(-330, -160), "TIME", 20, new Color(0.7f, 0.85f, 0.9f));
            AddButton(Page.Main, main, new Vector3(-0.17f, -0.16f, 0f), 2.4f, "DAY", Amber, () => TimeOfDay.Set(DayTime.Day), () => TimeOfDay.Current == DayTime.Day);
            AddButton(Page.Main, main, new Vector3(-0.02f, -0.16f, 0f), 2.4f, "DUSK", Red, () => TimeOfDay.Set(DayTime.Dusk), () => TimeOfDay.Current == DayTime.Dusk);
            AddButton(Page.Main, main, new Vector3(0.13f, -0.16f, 0f), 2.4f, "NIGHT", Blue, () => TimeOfDay.Set(DayTime.Night), () => TimeOfDay.Current == DayTime.Night);
            Kr(mainUi, new Vector2(300, -160), "낮 / 해질녘 / 밤 비행", 17);
            if (!XRSettings.isDeviceActive)
            {
                AddButton(Page.Main, main, new Vector3(0.32f, 0.2f, 0f), 2.4f, "CONTROLS", Amber, () => { if (Controls) Controls.Open(); }, () => false);
                Kr(mainUi, new Vector2(0.32f * CanvasPx / W, 160), "조작 설정 [K]", 16);
            }
            Label(mainUi, new Vector2(0, -250), "POKE WITH A FINGER  ·  MOUSE CLICK  ·  KEYS 1 / 2 / 3  ·  N = DAY/DUSK/NIGHT  ·  K = CONTROLS", 18, new Color(0.6f, 0.75f, 0.8f));
            Kr(mainUi, new Vector2(0, -280), "비행 중 Esc 또는 조종석 MENU 버튼 = 이 메뉴로 돌아오기", 18);

            // ---------------- AI BATTLE ----------------
            var ai = PageRoot(Page.AI, canvas, out var aiUi);
            Label(aiUi, new Vector2(0, 240), "AI BATTLE", 28, Amber);
            Kr(aiUi, new Vector2(0, 210), "난이도와 출발 방식을 고르고 출격", 20);
            Label(aiUi, new Vector2(-300, 95), "DIFFICULTY", 20, new Color(0.7f, 0.85f, 0.9f));
            string[] diff = { "EASY", "NORMAL", "ACE" };
            for (int i = 0; i < 3; i++)
            {
                int d = i;
                AddButton(Page.AI, ai, new Vector3(-0.1f + i * 0.17f, 0.11f, 0f), 2.6f, diff[i], i == 0 ? Green : i == 1 ? Amber : Red, () => difficulty = d, () => difficulty == d);
            }
            Label(aiUi, new Vector2(-300, -5), "START", 20, new Color(0.7f, 0.85f, 0.9f));
            AddButton(Page.AI, ai, new Vector3(-0.015f, -0.01f, 0f), 2.6f, "RUNWAY", Blue, () => airStart = false, () => !airStart);
            AddButton(Page.AI, ai, new Vector3(0.155f, -0.01f, 0f), 2.6f, "AIR START", Blue, () => airStart = true, () => airStart);
            aiSummary = Kr(aiUi, new Vector2(0, -82), "", 19);
            AddButton(Page.AI, ai, new Vector3(-0.17f, -0.2f, 0f), 2.8f, "BACK", new Color(0.6f, 0.6f, 0.6f), () => Show(Page.Main), () => false);
            AddButton(Page.AI, ai, new Vector3(0.17f, -0.2f, 0f), 3.2f, "LAUNCH", Red, StartAI, () => true);
            Label(aiUi, new Vector2(0, -290), "KEYS: 1 2 3 = difficulty   R = runway   A = air start   ENTER = launch   ESC = back", 16, new Color(0.6f, 0.75f, 0.8f));

            // ---------------- MULTIPLAYER (room list → lobby → host starts) ----------------
            var mp = PageRoot(Page.Multi, canvas, out var mpUi);
            Label(mpUi, new Vector2(0, 252), "MULTIPLAYER  PvP", 26, Amber);
            multiStatus = Label(mpUi, new Vector2(0, 224), "", 17, new Color(0.8f, 1f, 0.8f), krFont);
            nickTxt = Label(mpUi, new Vector2(-60, 178), "", 22, Color.white, krFont);
            roomTxt = Label(mpUi, new Vector2(0, 188), "", 20, Color.white, krFont);
            gBrowse = Group(mp, "Browse"); gEdit = Group(mp, "NameEdit"); gSettings = Group(mp, "Settings");
            gRooms = Group(mp, "Rooms"); gLobby = Group(mp, "Lobby"); gStart = Group(mp, "Start");
            uBrowse = UiGroup(mpUi, "Browse"); uSettings = UiGroup(mpUi, "Settings");

            AddButton(Page.Multi, gBrowse, new Vector3(0.25f, 0.175f, 0f), 1.3f, "NAME", Amber, BeginNameEdit, () => false);

            // room settings: before creating (my defaults) / in the lobby (host only)
            Label(uSettings, new Vector2(-330, 110), "MODE", 17, new Color(0.7f, 0.85f, 0.9f));
            Label(uSettings, new Vector2(-330, 45), "TIME", 17, new Color(0.7f, 0.85f, 0.9f));
            Label(uSettings, new Vector2(-330, -20), "LENGTH", 17, new Color(0.7f, 0.85f, 0.9f));
            AddButton(Page.Multi, gSettings, new Vector3(-0.2f, 0.11f, 0f), 1.3f, "FFA", Amber, () => net.HostSetTeam(false), () => !(net.Active ? net.TeamMode : net.PreferTeam));
            AddButton(Page.Multi, gSettings, new Vector3(-0.12f, 0.11f, 0f), 1.3f, "TEAM", Blue, () => net.HostSetTeam(true), () => net.Active ? net.TeamMode : net.PreferTeam);
            Kr(uSettings, new Vector2(80, 110), "개인전 / 팀전 (BLUE vs RED)", 16);
            DayTime[] tods = { DayTime.Day, DayTime.Dusk, DayTime.Night };
            for (int i = 0; i < 3; i++)
            {
                var t = tods[i];
                AddButton(Page.Multi, gSettings, new Vector3(-0.2f + i * 0.08f, 0.045f, 0f), 1.3f, NetSession.TimeName(t), i == 0 ? Amber : i == 1 ? Red : Blue,
                          () => net.HostSetTime(t), () => (net.Active ? net.RoomTime : NetSession.PrefTime) == t);
            }
            Kr(uSettings, new Vector2(150, 45), "맵 시간 (낮 / 해질녘 / 밤)", 16);
            for (int i = 0; i < NetSession.MinuteOptions.Length; i++)
            {
                int m = NetSession.MinuteOptions[i];
                AddButton(Page.Multi, gSettings, new Vector3(-0.2f + i * 0.08f, -0.02f, 0f), 1.3f, $"{m} MIN", Green,
                          () => net.HostSetMinutes(m), () => (net.Active ? net.RoomMinutes : NetSession.PrefMinutes) == m);
            }
            Kr(uSettings, new Vector2(250, -20), "경기 시간", 16);
            settingsTxt = Label(mpUi, new Vector2(0, 60), "", 20, new Color(0.85f, 0.95f, 1f));

            // room list
            roomsHeader = Label(uBrowse, new Vector2(0, -62), "", 17, Amber);
            for (int i = 0; i < RoomRows; i++)
            {
                int idx = i;
                float y = -0.095f - i * 0.045f;
                roomLines[i] = Label(uBrowse, new Vector2(-40, y * 1000f), "", 17, Color.white, krFont);
                roomBtns[i] = AddButtonRef(Page.Multi, gRooms, new Vector3(0.33f, y, 0f), 1.0f, $"JOIN {i + 1}", Green, () => JoinRow(idx), () => false);
            }
            multiBoard = Label(mpUi, new Vector2(0, -150), "", 17, new Color(0.85f, 0.95f, 0.85f), krFont);

            // callsign: typed on the keyboard (Korean IME works), no on-screen keypad
            AddButton(Page.Multi, gEdit, new Vector3(0.25f, -0.295f, 0f), 1.6f, "DONE", Green, () => FinishNameEdit(true), () => true);

            // bottom rows
            AddButton(Page.Multi, gBrowse, new Vector3(-0.27f, -0.295f, 0f), 1.6f, "BACK", new Color(0.6f, 0.6f, 0.6f), () => Show(Page.Main), () => false);
            AddButton(Page.Multi, gBrowse, new Vector3(-0.09f, -0.295f, 0f), 1.6f, "REFRESH", Blue, () => net.RefreshRooms(), () => net.Refreshing);
            AddButton(Page.Multi, gBrowse, new Vector3(0.09f, -0.295f, 0f), 1.6f, "QUICK", Green, () => net.QuickMatch(), () => net.Busy);
            AddButton(Page.Multi, gBrowse, new Vector3(0.27f, -0.295f, 0f), 1.6f, "CREATE", Red, () => net.Host(), () => net.Busy);
            AddButton(Page.Multi, gLobby, new Vector3(-0.25f, -0.295f, 0f), 1.6f, "LEAVE", new Color(0.6f, 0.6f, 0.6f), () => net.Leave(), () => false);
            AddButton(Page.Multi, gStart, new Vector3(0.25f, -0.295f, 0f), 1.6f, "START", Red, () => net.HostStart(), () => true);
            multiHint = Kr(mpUi, new Vector2(0, -322), "", 14);

            panel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ open / close
        /// <summary>Back to the room's waiting area after a match.</summary>
        public void OpenLobby() { Open(); Show(Page.Multi); }

        public void Open()
        {
            if (AnyOpen) return;
            AnyOpen = true;
            nameEdit = false;
            Show(net != null && net.Active ? Page.Multi : Page.Main);
            wasThirdPerson = vr.ThirdPerson;
            vr.SetThirdPerson(true);                       // show the jet on the runway behind the menu
            Place();
            panel.gameObject.SetActive(true);
            if (Keyboard.current != null) Keyboard.current.onTextInput += OnText;
        }

        public void Close()
        {
            if (!AnyOpen) return;
            AnyOpen = false;
            if (nameEdit) FinishNameEdit(false);
            ClosedFrame = Time.frameCount;
            panel.gameObject.SetActive(false);
            vr.SetThirdPerson(false);    // fly from the cockpit
            if (Keyboard.current != null) Keyboard.current.onTextInput -= OnText;
        }

        void Place()
        {
            Vector3 hp = vr.trackingSpace.InverseTransformPoint(head.position);
            Vector3 hf = vr.trackingSpace.InverseTransformDirection(head.forward); hf.y = 0f;
            if (hf.sqrMagnitude < 1e-4f) hf = Vector3.forward;
            hf.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, hf);
            bool xr = XRSettings.isDeviceActive;
            // desktop: off to the right so the jet stays visible; VR: straight ahead at arm's reach
            Vector3 p = hp + hf * (xr ? 0.6f : 0.95f) + right * (xr ? 0f : 0.22f) + Vector3.down * (xr ? 0.12f : 0.02f);
            panel.localPosition = p;
            Vector3 look = p - hp; if (xr) look.y = 0f;
            panel.localRotation = Quaternion.LookRotation(look.normalized, Vector3.up) * Quaternion.Euler(xr ? 15f : 0f, 0, 0);
        }

        void Show(Page p)
        {
            page = p;
            if (p == Page.Multi && !net.Active) { nameEdit = false; roomRefreshTimer = 0f; }
            foreach (var kv in pages) kv.Value.SetActive(kv.Key == p);
            foreach (var kv in pageUi) kv.Value.SetActive(kv.Key == p);
        }

        void StartAI() { if (net.Active) net.Leave(); Close(); game.StartAIBattle(difficulty, airStart); }
        void StartFree() { if (net.Active) net.Leave(); Close(); game.StartFreeFlight(); }

        void OnText(char c)
        {
            // Hangul from the IME: the text box may or may not get it (depends on the platform's IME path) —
            // queue it and add it in LateUpdate only if the box didn't
            if (page == Page.Multi && nameEdit && IsHangul(c)) pendingHangul.Add((c, Time.frameCount, nickEdit?.Length ?? 0));
        }
        static bool IsHangul(char c) => (c >= '\uAC00' && c <= '\uD7A3') || (c >= '\u3131' && c <= '\u318E');
        readonly List<(char c, int frame, int lenBefore)> pendingHangul = new List<(char, int, int)>();

        void LateUpdate()
        {
            if (pendingHangul.Count == 0) return;
            if (!nameEdit) { pendingHangul.Clear(); return; }
            for (int i = pendingHangul.Count - 1; i >= 0; i--)
            {
                var p = pendingHangul[i];
                if (Time.frameCount - p.frame < 2) continue;              // give the text box two frames to show it
                bool boxGotIt = nickEdit != null && nickEdit.Length > p.lenBefore && nickEdit.IndexOf(p.c, p.lenBefore > 0 ? p.lenBefore - 1 : 0) >= 0;
                if (!boxGotIt && (nickEdit ?? "").Length < NetSession.NickMax) nickEdit = (nickEdit ?? "") + p.c;
                pendingHangul.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ update
        void Update()
        {
            var kb = Keyboard.current;
            if (!AnyOpen)
            {
                bool other = (game.Map && (game.Map.IsOpen || game.Map.ClosedFrame == Time.frameCount)) || NetPanel.AnyOpen || ControlsWindow.AnyOpen || ControlsWindow.ClosedFrame == Time.frameCount
                             || (game.NetPanelRef && game.NetPanelRef.ClosedFrame == Time.frameCount);
                if (!other && ((kb != null && kb.escapeKey.wasPressedThisFrame) || (vr && vr.MenuDown))) game.ReturnToMenu();
                return;
            }

            if (ControlsWindow.AnyOpen || ControlsWindow.ClosedFrame == Time.frameCount) return;
            if (kb != null)
            {
                if (page == Page.Main && kb.kKey.wasPressedThisFrame && Controls) { Controls.Open(); return; }
                switch (page)
                {
                    case Page.Main:
                        if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) Show(Page.AI);
                        else if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) { StartFree(); return; }
                        else if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame) Show(Page.Multi);
                        else if (kb.nKey.wasPressedThisFrame) TimeOfDay.Set(TimeOfDay.Next(TimeOfDay.Current));
                        break;
                    case Page.AI:
                        if (kb.digit1Key.wasPressedThisFrame) difficulty = 0;
                        if (kb.digit2Key.wasPressedThisFrame) difficulty = 1;
                        if (kb.digit3Key.wasPressedThisFrame) difficulty = 2;
                        if (kb.rKey.wasPressedThisFrame) airStart = false;
                        if (kb.aKey.wasPressedThisFrame) airStart = true;
                        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) { StartAI(); return; }
                        if (kb.escapeKey.wasPressedThisFrame) Show(Page.Main);
                        break;
                    case Page.Multi:
                        if (nameEdit && !net.Active)
                        {
                            // typing goes to the on-screen text box (OnGUI) — Enter / Esc are handled there
                        }
                        else if (!net.Active)
                        {
                            if (kb.f1Key.wasPressedThisFrame) net.QuickMatch();
                            if (kb.f2Key.wasPressedThisFrame) net.Host();
                            if (kb.f5Key.wasPressedThisFrame) net.RefreshRooms();
                            var dk = new[] { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key };
                            for (int r = 0; r < RoomRows; r++) if (dk[r].wasPressedThisFrame) JoinRow(r);
                            if (kb.escapeKey.wasPressedThisFrame) Show(Page.Main);
                        }
                        else
                        {
                            if (net.IsHost && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) net.HostStart();
                            if (kb.f3Key.wasPressedThisFrame) net.Leave();
                        }
                        break;
                }
            }

            // finger pokes (VR) and mouse clicks (desktop)
            var list = buttons[page];
            tips.Clear();
            vr.GetFingertipsWorld(tips);
            Page before = page;
            foreach (var b in list) { if (!b.gameObject.activeInHierarchy) continue; b.Poke(tips); if (!AnyOpen || page != before) return; }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !XRSettings.isDeviceActive)
            {
                Vector2 mp = mouse.position.ReadValue();
                foreach (var b in list)
                    if (b.gameObject.activeInHierarchy && b.ScreenRect(cam).Contains(mp)) { b.Press(); break; }
                if (!AnyOpen) return;
            }

            // live texts
            string[] dn = { "EASY — 적기 1~3대, 느린 반응", "NORMAL — 적기 수·실력 점점 증가", "ACE — 많은 적기, 미사일 사용, 정밀 사격" };
            if (aiSummary) aiSummary.text = dn[difficulty] + "\n" + (airStart ? "공중 출발: 고도 3000 m에서 바로 교전" : "활주로 출발: 이륙 후 고도 300 m 이상에서 적 출현");
            if (page == Page.Multi) UpdateMultiPage();
        }

        // ---- callsign entry: a real text box (IMGUI) so the Windows Korean IME composes Hangul properly ----
        string nickEdit = "";
        bool nickFocus;
        GUIStyle nickBoxStyle, nickFieldStyle;

        void BeginNameEdit()
        {
            if (XRSettings.isDeviceActive) return;          // headset: no keyboard
            nickEdit = NetSession.Nick;
            nickFocus = true;
            nameEdit = true;
            pendingHangul.Clear();
            Ime(true);
        }

        // The Input System keeps the OS IME switched off unless asked — without this, Korean can't be composed at all
        string imeComp = "";
        bool imeOn;
        void Ime(bool on)
        {
            if (imeOn == on) return;
            imeOn = on; imeComp = "";
            var kb = Keyboard.current;
            if (kb == null) return;
            try
            {
                kb.SetIMEEnabled(on);
                if (on) { kb.SetIMECursorPosition(new Vector2(Screen.width * 0.5f, Screen.height * 0.6f)); kb.onIMECompositionChange += OnImeComp; }
                else kb.onIMECompositionChange -= OnImeComp;
            }
            catch (System.Exception ex) { Debug.LogWarning("[AirFight] IME: " + ex.Message); }
        }
        void OnImeComp(UnityEngine.InputSystem.LowLevel.IMECompositionString c) { imeComp = c.ToString(); }

        void FinishNameEdit(bool keep)
        {
            if (keep)
            {
                string n = NetSession.Sanitize(nickEdit);
                if (n.Length > 0) NetSession.Nick = n;
            }
            nameEdit = false;
            Ime(false);
        }

        void OnGUI()
        {
            if (!AnyOpen || page != Page.Multi || !nameEdit || net == null || net.Active) return;
            if (nickFieldStyle == null)
            {
                nickBoxStyle = new GUIStyle(GUI.skin.box) { font = krFont, fontSize = 22, alignment = TextAnchor.UpperCenter };
                nickBoxStyle.normal.textColor = Color.white;
                nickFieldStyle = new GUIStyle(GUI.skin.textField) { font = krFont, fontSize = 30, alignment = TextAnchor.MiddleCenter };
            }
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { FinishNameEdit(true); e.Use(); return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { FinishNameEdit(false); e.Use(); return; }
            float w = 520f, h = 150f;
            var box = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.3f, w, h);
            GUI.Box(box, "콜사인 입력 — 한글·영문·숫자 최대 12자\nENTER = 완료   ESC = 취소", nickBoxStyle);
            GUI.SetNextControlName("AF_Nick");
            nickEdit = GUI.TextField(new Rect(box.x + 30f, box.y + 75f, w - 60f, 56f), nickEdit ?? "", NetSession.NickMax, nickFieldStyle);
            if (nickFocus) { GUI.FocusControl("AF_Nick"); nickFocus = false; }
            if (!string.IsNullOrEmpty(imeComp))                            // Hangul block being composed
                GUI.Label(new Rect(box.x, box.y + h + 4f, w, 40f), "조합 중:  " + imeComp, nickBoxStyle);
        }

        void UpdateMultiPage()
        {
            bool active = net.Active, host = net.IsHost, lobby = active && net.Phase == NetSession.MatchPhase.Lobby;
            bool editing = nameEdit && !active, browse = !active && !editing;
            gBrowse.SetActive(browse);
            gEdit.SetActive(editing);
            bool settings = browse || (host && lobby);
            gSettings.SetActive(settings);
            uSettings.gameObject.SetActive(settings);
            uBrowse.gameObject.SetActive(browse);
            gRooms.SetActive(browse);
            gLobby.SetActive(active);
            gStart.SetActive(host && lobby);

            // room list: refresh on open and every 6 s while browsing
            if (browse) { roomRefreshTimer -= Time.unscaledDeltaTime; if (roomRefreshTimer <= 0f) { roomRefreshTimer = 6f; net.RefreshRooms(); } }
            for (int i = 0; i < RoomRows; i++)
            {
                bool has = browse && i < net.Rooms.Count;
                roomBtns[i].gameObject.SetActive(has);
                roomLines[i].text = has ? $"{i + 1}.  {NetSession.Describe(net.Rooms[i])}" : "";
            }
            roomsHeader.text = net.Refreshing ? "SEARCHING ROOMS..." : $"ROOMS — {net.RoomsStatus}";

            nickTxt.text = active ? "" : editing ? $"CALLSIGN  {NetSession.Sanitize(nickEdit)}{imeComp}_" : $"CALLSIGN  {NetSession.Nick}";
            multiStatus.text = net.Busy ? net.Status : active ? net.MatchClock() : (net.Status == "OFFLINE" ? "" : net.Status);
            roomTxt.text = active ? $"ROOM  {net.RoomName}{(host ? "  (YOU ARE HOST)" : "")}" : "";
            settingsTxt.text = active && !(host && lobby) ? $"{net.ModeName}  ·  {NetSession.TimeName(net.RoomTime)}  ·  {net.RoomMinutes} MIN" : "";
            multiBoard.text = active ? net.Scoreboard() : "";
            multiHint.text = editing ? "화면의 입력창에 콜사인을 쓰세요 (한/영 키로 전환) · ENTER = 완료 · ESC = 취소"
                           : !active ? "QUICK = 빠른 매칭 (F1) · CREATE = 방 만들기 (F2) · JOIN / 1~4 = 참가 · REFRESH (F5) · NAME = 콜사인 · ESC 뒤로"
                           : host && lobby ? "모드·시간·경기 시간을 고르고 START (ENTER) · LEAVE (F3)"
                           : lobby ? "방장이 시작하기를 기다리는 중 · LEAVE (F3)" : "LEAVE (F3)";
        }

        void OnDestroy() { if (AnyOpen && Keyboard.current != null) Keyboard.current.onTextInput -= OnText; AnyOpen = false; }

        // ------------------------------------------------------------------ ui helpers
        GameObject PageRoot(Page p, Transform canvas, out Transform ui)
        {
            var root = new GameObject("Page_" + p);
            root.transform.SetParent(panel, false);
            ui = Rect(canvas, Vector2.zero, ((RectTransform)canvas).sizeDelta);
            ui.name = "PageUI_" + p;
            pages[p] = root;
            pageUi[p] = ui.gameObject;
            buttons[p] = new List<CockpitButton>();
            return root;
        }

        GameObject Group(GameObject page, string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(page.transform, false);
            return g;
        }

        Transform UiGroup(Transform ui, string name)
        {
            var t = Rect(ui, Vector2.zero, ((RectTransform)ui).sizeDelta);
            t.name = name;
            return t;
        }

        CockpitButton AddButtonRef(Page p, GameObject root, Vector3 pos, float scale, string label, Color lamp, System.Action onPress, System.Func<bool> lit)
        {
            AddButton(p, root, pos, scale, label, lamp, onPress, lit);
            return buttons[p][buttons[p].Count - 1];
        }

        void JoinRow(int i) { if (i < net.Rooms.Count && !net.Busy && !net.Active) net.JoinRoom(net.Rooms[i]); }


        void AddButton(Page p, GameObject root, Vector3 pos, float scale, string label, Color lamp, System.Action onPress, System.Func<bool> lit)
        {
            var b = CockpitButton.Create(root.transform, pos, Quaternion.identity, label, lamp, cam);
            b.transform.localScale = Vector3.one * scale;
            b.Pressed += () => onPress();
            b.isLit = lit;
            buttons[p].Add(b);
        }

        Transform MakeCanvas(Transform parent, Vector2 size, float z)
        {
            var cgo = new GameObject("MenuCanvas", typeof(RectTransform));
            cgo.transform.SetParent(parent, false);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            canvas.sortingOrder = 20;
            var rt = (RectTransform)cgo.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * (W / CanvasPx);
            rt.localPosition = new Vector3(0, 0, z);
            return rt;
        }

        static RectTransform Rect(Transform parent, Vector2 pos, Vector2 size)
        {
            var rt = (RectTransform)new GameObject("E", typeof(RectTransform)).transform;
            rt.SetParent(parent, false); rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        Text Label(Transform parent, Vector2 pos, string s, int size, Color c, Font f = null)
        {
            var t = Rect(parent, pos, new Vector2(CanvasPx - 20, size * 4)).gameObject.AddComponent<Text>();
            t.font = f ?? font; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = c;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false; t.text = s;
            return t;
        }

        Text Kr(Transform parent, Vector2 pos, string s, int size) => Label(parent, pos, s, size, new Color(0.85f, 0.92f, 0.95f), krFont);

        /// <summary>A dynamic OS font with Hangul glyphs (Windows: Malgun Gothic, Android: Noto Sans CJK).</summary>
        static Font krCache;
        internal static Font KoreanFont() => krCache != null ? krCache : (krCache = MakeKoreanFont());
        static Font MakeKoreanFont()
        {
            string[] want = { "Malgun Gothic", "맑은 고딕", "Noto Sans CJK KR", "Noto Sans KR", "NotoSansCJK", "Noto Sans CJK", "NanumGothic", "Apple SD Gothic Neo", "Droid Sans Fallback" };
            try
            {
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
                foreach (var w in want)
                    if (installed.Contains(w)) return Font.CreateDynamicFontFromOSFont(w, 24);
                foreach (var name in installed)
                    if (name.Contains("CJK") || name.Contains("Korean") || name.Contains("Gothic")) return Font.CreateDynamicFontFromOSFont(name, 24);
                return Font.CreateDynamicFontFromOSFont(want, 24);
            }
            catch { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        }
    }
}
