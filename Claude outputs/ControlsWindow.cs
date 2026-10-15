using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;

namespace AirFight
{
    /// <summary>
    /// 조작 설정 창 (PC). 메인 메뉴의 CONTROLS 버튼, 또는 비행 중 F1(바꿀 수 있음)으로 연다.
    /// 칸을 클릭 → 원하는 키나 마우스 버튼을 누르면 지정. 오른쪽 클릭 = 비우기, Esc = 취소/닫기.
    /// 싱글플레이에서는 열려 있는 동안 게임이 멈춘다.
    /// </summary>
    public class ControlsWindow : MonoBehaviour
    {
        public static bool AnyOpen { get; private set; }
        public static int ClosedFrame { get; private set; } = -1;
        internal static void ResetStatics() { AnyOpen = false; ClosedFrame = -1; }

        class Slot { public Act act; public int slot; public RectTransform rt; public Image bg; public Text txt; }
        class Btn { public RectTransform rt; public Image bg; public System.Action onClick; }

        GameObject root;
        Font font;
        readonly List<Slot> slots = new List<Slot>();
        readonly List<Btn> btns = new List<Btn>();
        Slot capturing;
        int captureFrame, openFrame;
        Text msg, sensTxt, invertTxt;
        float msgUntil;
        float savedTimeScale = 1f;
        bool paused;

        static readonly Color Panel = new Color(0.03f, 0.05f, 0.07f, 0.96f), Cell = new Color(0.12f, 0.17f, 0.2f, 1f),
                              CellHover = new Color(0.18f, 0.27f, 0.32f, 1f), CellActive = new Color(0.75f, 0.55f, 0.1f, 1f),
                              Head = new Color(0.1f, 0.25f, 0.35f, 1f), Amber = new Color(1f, 0.8f, 0.2f), Txt = new Color(0.88f, 0.94f, 0.96f);

        public bool IsOpen => AnyOpen;

        void Build()
        {
            font = MainMenu.KoreanFont();
            root = new GameObject("ControlsWindow", typeof(RectTransform));
            DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = 1f;
            var crt = (RectTransform)root.transform;

            // dim the world, then the window
            Img(crt, Vector2.zero, new Vector2(4000, 4000), new Color(0, 0, 0, 0.55f));
            const float PW = 1240, PH = 820;
            Img(crt, Vector2.zero, new Vector2(PW, PH), Panel);
            Img(crt, new Vector2(0, PH / 2 - 30), new Vector2(PW, 60), Head);
            Label(crt, new Vector2(0, PH / 2 - 30), "CONTROLS  ·  조작 설정 (키보드 / 마우스)", 28, Color.white, TextAnchor.MiddleCenter, PW);
            Label(crt, new Vector2(0, PH / 2 - 82), "칸을 클릭하고 원하는 키나 마우스 버튼을 누르세요   ·   오른쪽 클릭 = 비우기   ·   Esc = 취소 / 닫기", 18, new Color(0.7f, 0.82f, 0.88f), TextAnchor.MiddleCenter, PW);

            // two columns of actions: name | key 1 | key 2
            var acts = KeyBindings.All;
            int perCol = (acts.Length + 1) / 2;
            const float rowH = 46, colW = 600, nameW = 270, keyW = 150;
            float top = PH / 2 - 130;
            for (int c = 0; c < 2; c++)
            {
                float x0 = -PW / 2 + 20 + c * (colW + 0);
                Label(crt, new Vector2(x0 + nameW / 2, top + 4), "동작", 17, Amber, TextAnchor.MiddleCenter, nameW);
                Label(crt, new Vector2(x0 + nameW + 8 + keyW / 2, top + 4), "키 1", 17, Amber, TextAnchor.MiddleCenter, keyW);
                Label(crt, new Vector2(x0 + nameW + 16 + keyW * 1.5f, top + 4), "키 2", 17, Amber, TextAnchor.MiddleCenter, keyW);
            }
            for (int i = 0; i < acts.Length; i++)
            {
                int c = i / perCol, r = i % perCol;
                float x0 = -PW / 2 + 20 + c * colW;
                float y = top - 36 - r * rowH;
                Label(crt, new Vector2(x0 + 8, y), KeyBindings.Label(acts[i]), 19, Txt, TextAnchor.MiddleLeft, nameW);
                for (int s = 0; s < KeyBindings.Slots; s++)
                {
                    var bg = Img(crt, new Vector2(x0 + nameW + 8 + s * (keyW + 8) + keyW / 2, y), new Vector2(keyW, rowH - 8), Cell);
                    var t = Label(bg.rectTransform, Vector2.zero, "", 19, Color.white, TextAnchor.MiddleCenter, keyW);
                    slots.Add(new Slot { act = acts[i], slot = s, rt = bg.rectTransform, bg = bg, txt = t });
                }
            }

            // options + buttons along the bottom
            float by = -PH / 2 + 45;
            invertTxt = Button(crt, new Vector2(-470, by), 250, "", () => KeyBindings.InvertPitch = !KeyBindings.InvertPitch);
            Label(crt, new Vector2(-235, by), "둘러보기 감도", 18, Txt, TextAnchor.MiddleCenter, 160);
            Button(crt, new Vector2(-130, by), 46, "−", () => KeyBindings.LookSensitivity -= 0.1f);
            sensTxt = Label(crt, new Vector2(-70, by), "", 20, Color.white, TextAnchor.MiddleCenter, 70);
            Button(crt, new Vector2(-10, by), 46, "+", () => KeyBindings.LookSensitivity += 0.1f);
            Button(crt, new Vector2(200, by), 230, "기본값으로 초기화", () => { KeyBindings.ResetAll(); Flash("모든 키를 기본값으로 되돌렸습니다"); });
            Button(crt, new Vector2(470, by), 200, "닫기 (Esc)", Close);
            msg = Label(crt, new Vector2(0, by + 46), "", 18, Amber, TextAnchor.MiddleCenter, PW);

            root.SetActive(false);
        }

        // ------------------------------------------------------------------ open / close
        public void Open()
        {
            if (AnyOpen || XRSettings.isDeviceActive) return;
            if (root == null) Build();
            AnyOpen = true;
            openFrame = Time.frameCount;
            capturing = null;
            root.SetActive(true);
            var gm = GameManager.Instance;
            paused = gm != null && !gm.Multiplayer && !MainMenu.AnyOpen;
            if (paused) { savedTimeScale = Time.timeScale; Time.timeScale = 0f; }
            Refresh();
        }

        public void Close()
        {
            if (!AnyOpen) return;
            AnyOpen = false;
            ClosedFrame = Time.frameCount;
            capturing = null;
            if (root) root.SetActive(false);
            if (paused) { Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f; paused = false; }
        }

        public void Toggle() { if (AnyOpen) Close(); else Open(); }

        void OnDestroy()
        {
            if (paused) Time.timeScale = 1f;
            AnyOpen = false;
            if (root) Destroy(root);
        }

        // ------------------------------------------------------------------ update
        void Update()
        {
            var kb = Keyboard.current;
            if (!AnyOpen)
            {
                // in flight: the settings key opens the window (not over the map / net panel / main menu, which has its own button)
                bool busy = MainMenu.AnyOpen || NetPanel.AnyOpen || (GameManager.Instance && GameManager.Instance.Map && GameManager.Instance.Map.IsOpen);
                if (!busy && ClosedFrame != Time.frameCount && KeyBindings.Down(Act.Settings)) Open();
                return;
            }

            if (Time.frameCount == openFrame) return;            // the click / key that opened the window
            var mouse = Mouse.current;
            Vector2 mp = mouse != null ? mouse.position.ReadValue() : new Vector2(-1, -1);

            if (capturing != null)
            {
                if (kb != null && kb.escapeKey.wasPressedThisFrame) { capturing = null; Refresh(); return; }
                if (Time.frameCount <= captureFrame) return;            // ignore the click that started capturing
                string b = KeyBindings.PressedThisFrame();
                if (b != null)
                {
                    var stolen = KeyBindings.Set(capturing.act, capturing.slot, b);
                    Flash(stolen.HasValue ? $"{KeyBindings.Pretty(b)} → '{KeyBindings.Label(capturing.act)}' (기존 '{KeyBindings.Label(stolen.Value)}'에서 해제됨)"
                                          : $"{KeyBindings.Pretty(b)} → '{KeyBindings.Label(capturing.act)}'");
                    capturing = null;
                    Refresh();
                }
                return;
            }

            if ((kb != null && kb.escapeKey.wasPressedThisFrame) || KeyBindings.Down(Act.Settings)) { Close(); return; }

            foreach (var s in slots) s.bg.color = Hit(s.rt, mp) ? CellHover : Cell;
            foreach (var b in btns) b.bg.color = Hit(b.rt, mp) ? CellHover : Cell;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                foreach (var s in slots)
                    if (Hit(s.rt, mp)) { capturing = s; captureFrame = Time.frameCount; s.bg.color = CellActive; s.txt.text = "키를 누르세요…"; return; }
                foreach (var b in btns)
                    if (Hit(b.rt, mp)) { b.onClick(); if (AnyOpen) Refresh(); return; }
            }
            if (mouse != null && mouse.rightButton.wasPressedThisFrame)
                foreach (var s in slots)
                    if (Hit(s.rt, mp)) { KeyBindings.Set(s.act, s.slot, ""); Flash($"'{KeyBindings.Label(s.act)}' 키 {s.slot + 1} 비움"); Refresh(); return; }

            if (msg && Time.unscaledTime > msgUntil) msg.text = "";
        }

        void Refresh()
        {
            foreach (var s in slots) { s.txt.text = KeyBindings.Pretty(KeyBindings.Get(s.act, s.slot)); s.bg.color = Cell; }
            if (sensTxt) sensTxt.text = $"{KeyBindings.LookSensitivity:0.0}x";
            if (invertTxt) invertTxt.text = KeyBindings.InvertPitch ? "피치 반전: 켜짐" : "피치 반전: 꺼짐";
        }

        void Flash(string s) { if (msg) { msg.text = s; msgUntil = Time.unscaledTime + 4f; } }

        static bool Hit(RectTransform rt, Vector2 screen) => RectTransformUtility.RectangleContainsScreenPoint(rt, screen, null);

        // ------------------------------------------------------------------ ui helpers
        Image Img(RectTransform parent, Vector2 pos, Vector2 size, Color c)
        {
            var rt = (RectTransform)new GameObject("I", typeof(RectTransform)).transform;
            rt.SetParent(parent, false); rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = rt.gameObject.AddComponent<Image>(); img.color = c; img.raycastTarget = false;
            return img;
        }

        Text Label(RectTransform parent, Vector2 pos, string s, int size, Color c, TextAnchor align, float width)
        {
            var rt = (RectTransform)new GameObject("T", typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(width, size + 12);
            if (align == TextAnchor.MiddleLeft) { rt.pivot = new Vector2(0, 0.5f); }
            rt.anchoredPosition = pos;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = c; t.alignment = align; t.text = s; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        Text Button(RectTransform parent, Vector2 pos, float w, string s, System.Action onClick)
        {
            var bg = Img(parent, pos, new Vector2(w, 42), Cell);
            btns.Add(new Btn { rt = bg.rectTransform, bg = bg, onClick = onClick });
            return Label(bg.rectTransform, Vector2.zero, s, 19, Color.white, TextAnchor.MiddleCenter, w);
        }
    }
}
