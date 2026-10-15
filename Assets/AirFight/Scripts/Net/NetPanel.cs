using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AirFight
{
    /// <summary>
    /// 비행 중 멀티플레이 상태 창 (P 키 / 조종석 MULTI 버튼): 방·경기 시간·점수판, 방장은 START, LEAVE.
    /// 방 만들기/참가는 메인 메뉴의 MULTIPLAYER 화면에서 한다. 키보드: Enter 시작(방장), F3 나가기, Esc 닫기.
    /// </summary>
    public class NetPanel : MonoBehaviour
    {
        public static bool AnyOpen { get; private set; }
        internal static void ResetStatics() { AnyOpen = false; }
        public NetSession net;
        public VRCockpitInput vr;
        public Transform head;
        public bool IsOpen { get; private set; }
        public int ClosedFrame { get; private set; } = -1;

        Transform panel;
        Text status, codeTxt, board;
        readonly List<CockpitButton> buttons = new List<CockpitButton>();
        readonly List<Vector3> tips = new List<Vector3>();
        Font font;
        CockpitButton startBtn;

        public void Build(Camera cam)
        {
            font = MainMenu.KoreanFont();
            panel = new GameObject("NetPanel").transform;
            panel.SetParent(vr.trackingSpace, false);

            var cgo = new GameObject("NetCanvas", typeof(RectTransform));
            cgo.transform.SetParent(panel, false);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            canvas.sortingOrder = 10;
            var crt = (RectTransform)cgo.transform;
            crt.sizeDelta = new Vector2(520, 300);
            crt.localScale = Vector3.one * (0.5f / 520f);
            crt.localPosition = new Vector3(0, 0.0f, 0.004f);
            var bg = Child(crt, Vector2.zero, new Vector2(520, 300)).gameObject.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.07f, 0.08f, 0.96f);
            Label(crt, new Vector2(0, 125), "MULTIPLAYER  PvP  (P)", 22);
            status = Label(crt, new Vector2(0, 90), "", 18);
            codeTxt = Label(crt, new Vector2(0, 50), "", 30);
            board = Label(crt, new Vector2(0, -50), "", 16);
            Label(crt, new Vector2(0, -135), "ENTER = START (host)   F3 LEAVE   Esc CLOSE   ·   rooms: Esc → MENU → MULTIPLAYER", 13);

            string[] act = { "START", "LEAVE", "CLOSE" };
            System.Action[] fn = { () => net.HostStart(), () => net.Leave(), Close };
            for (int i = 0; i < act.Length; i++)
            {
                var b = CockpitButton.Create(panel, new Vector3(-0.12f + i * 0.12f, -0.19f, -0.01f), Quaternion.identity, act[i], i == 2 ? new Color(1f, 0.3f, 0.3f) : new Color(0.3f, 1f, 0.4f), cam);
                var f = fn[i];
                b.Pressed += () => f();
                int idx = i;
                b.isLit = () => idx == 0 ? net.IsHost && net.Phase == NetSession.MatchPhase.Lobby : idx == 1 && net.Active;
                buttons.Add(b);
                if (i == 0) startBtn = b;
            }
            panel.gameObject.SetActive(false);
        }

        RectTransform Child(Transform parent, Vector2 pos, Vector2 size)
        {
            var rt = (RectTransform)new GameObject("E", typeof(RectTransform)).transform;
            rt.SetParent(parent, false); rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        Text Label(Transform parent, Vector2 pos, string s, int size)
        {
            var t = Child(parent, pos, new Vector2(500, size * 6)).gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = new Color(0.85f, 1f, 0.85f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false; t.text = s;
            return t;
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = AnyOpen = true;
            Vector3 hp = vr.trackingSpace.InverseTransformPoint(head.position);
            Vector3 hf = vr.trackingSpace.InverseTransformDirection(head.forward); hf.y = 0f;
            if (hf.sqrMagnitude < 1e-4f) hf = Vector3.forward;
            hf.Normalize();
            panel.localPosition = hp + hf * 0.55f + Vector3.down * 0.05f;
            panel.localRotation = Quaternion.LookRotation(hf, Vector3.up) * Quaternion.Euler(12f, 0, 0);
            panel.gameObject.SetActive(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = AnyOpen = false;
            ClosedFrame = Time.frameCount;
            panel.gameObject.SetActive(false);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (!IsOpen)
            {
                bool mapOpen = (GameManager.Instance && GameManager.Instance.Map && GameManager.Instance.Map.IsOpen) || MainMenu.AnyOpen || ControlsWindow.AnyOpen;
                if (!mapOpen && (KeyBindings.Down(Act.NetPanel) || (vr && vr.MultiToggleDown))) Open();
                return;
            }
            if (vr && vr.MultiToggleDown) { Close(); return; }
            if (kb != null)
            {
                if (kb.escapeKey.wasPressedThisFrame) { Close(); return; }
                if (kb.f3Key.wasPressedThisFrame) net.Leave();
                if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) net.HostStart();
            }
            tips.Clear();
            vr.GetFingertipsWorld(tips);
            foreach (var b in buttons) { if (!IsOpen) break; b.Poke(tips); }
            if (!IsOpen) return;

            startBtn.gameObject.SetActive(net.IsHost && net.Phase == NetSession.MatchPhase.Lobby);
            status.text = net.Active ? $"ROOM {net.RoomName}  ·  {NetSession.TimeName(net.RoomTime)}  ·  {net.RoomMinutes} MIN" : net.Status;
            codeTxt.text = net.Active ? net.MatchClock() : "NOT IN A ROOM";
            board.text = net.Active ? net.Scoreboard() : "Esc → MENU → MULTIPLAYER\nCREATE a room or JOIN one from the list";
        }

        void OnDestroy() { AnyOpen = false; }
    }
}
