using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace AirFight
{
    /// <summary>
    /// 전술 지도 (M 키 / 조종석 MAP 버튼). 열려 있는 동안 게임 일시정지.
    /// 공항을 골라서 → RUNWAY(활주로로 순간이동) / FINAL(10km 최종 접근으로 순간이동) / NAV(HUD에 길 안내).
    /// VR에서는 눈앞에 태블릿처럼 뜨고 손가락으로 누른다.
    /// </summary>
    public class MapView : MonoBehaviour
    {
        public GameManager game;
        public FlightModel fm;
        public VRCockpitInput vr;
        public Transform head;
        public bool IsOpen { get; private set; }
        public int Selected { get; private set; }
        public int ClosedFrame { get; private set; } = -1;

        const float MapUnits = 380f;        // canvas units for the 48 km island
        const float PanelMeters = 0.42f;    // physical width of the tablet
        Transform panel;
        RectTransform mapRT, playerArrow;
        readonly List<RectTransform> apMarkers = new List<RectTransform>();
        readonly List<Image> apMarkerImgs = new List<Image>();
        readonly List<RectTransform> enemyDots = new List<RectTransform>();
        readonly List<CockpitButton> buttons = new List<CockpitButton>();
        readonly List<Vector3> tips = new List<Vector3>();
        Text info;
        Font font;

        public void Build(Camera cam)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            panel = new GameObject("MapPanel").transform;
            panel.SetParent(vr.trackingSpace, false);

            // canvas
            var cgo = new GameObject("MapCanvas", typeof(RectTransform));
            cgo.transform.SetParent(panel, false);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            canvas.sortingOrder = 10;
            var crt = (RectTransform)cgo.transform;
            crt.sizeDelta = new Vector2(420, 470);
            crt.localScale = Vector3.one * (PanelMeters / 420f);
            crt.localPosition = new Vector3(0, 0.03f, 0.004f);

            var bg = Child(crt, "BG", Vector2.zero, new Vector2(420, 470)).gameObject.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.07f, 0.08f, 0.96f);

            mapRT = Child(crt, "Map", new Vector2(0, 30), new Vector2(MapUnits, MapUnits));
            var raw = mapRT.gameObject.AddComponent<RawImage>();
            raw.texture = WorldBuilder.MapTexture;
            raw.color = WorldBuilder.MapTexture ? Color.white : new Color(0.2f, 0.35f, 0.25f);

            var aps = WorldBuilder.Airports;
            for (int i = 0; i < aps.Length; i++)
            {
                var m = Child(mapRT, "AP", ToMap(aps[i].Center3), new Vector2(5, 26));
                m.localRotation = Quaternion.Euler(0, 0, -aps[i].heading);
                var img = m.gameObject.AddComponent<Image>();
                apMarkers.Add(m); apMarkerImgs.Add(img);
                Label(mapRT, ToMap(aps[i].Center3) + new Vector2(0, 22), $"{i + 1}. {aps[i].name}", 15);
            }
            playerArrow = Child(mapRT, "Player", Vector2.zero, new Vector2(10, 18));
            playerArrow.gameObject.AddComponent<Image>().color = new Color(0.4f, 0.8f, 1f);
            Label(crt, new Vector2(0, 222), "TACTICAL MAP   (M)", 18);
            info = Label(crt, new Vector2(0, -175), "", 15);

            // poke buttons (VR) — keyboard: 1-9 / arrows select, T runway, F final, N nav, M/Esc close
            // airport selection: previous / next (any number of airfields)
            AddAction(new Vector3(-0.1f, -0.215f, -0.01f), "< PREV", new Color(1f, 0.85f, 0.2f), () => Selected = (Selected + aps.Length - 1) % aps.Length, cam);
            AddAction(new Vector3(0.1f, -0.215f, -0.01f), "NEXT >", new Color(1f, 0.85f, 0.2f), () => Selected = (Selected + 1) % aps.Length, cam);
            AddAction(new Vector3(-0.15f, -0.27f, -0.01f), "RUNWAY", new Color(0.3f, 1f, 0.4f), () => Go(false), cam);
            AddAction(new Vector3(-0.05f, -0.27f, -0.01f), "FINAL", new Color(0.3f, 1f, 0.4f), () => Go(true), cam);
            var nav = AddAction(new Vector3(0.05f, -0.27f, -0.01f), "NAV", new Color(0.4f, 0.7f, 1f), SetNav, cam);
            nav.isLit = () => game.NavTarget != null && game.NavTarget == WorldBuilder.Airports[Selected];
            AddAction(new Vector3(0.15f, -0.27f, -0.01f), "CLOSE", new Color(1f, 0.3f, 0.3f), Close, cam);

            panel.gameObject.SetActive(false);
        }

        CockpitButton AddAction(Vector3 pos, string label, Color c, System.Action a, Camera cam)
        {
            var b = CockpitButton.Create(panel, pos, Quaternion.identity, label, c, cam);
            b.Pressed += a;
            b.isLit = () => false;
            buttons.Add(b);
            return b;
        }

        RectTransform Child(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        Text Label(Transform parent, Vector2 pos, string s, int size)
        {
            var t = Child(parent, "L", pos, new Vector2(380, size + 10)).gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = new Color(0.85f, 1f, 0.85f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false; t.text = s;
            return t;
        }

        static Vector2 ToMap(Vector3 world) => new Vector2(world.x, world.z) / WorldBuilder.Size * MapUnits;

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            if (game.NavTarget != null) Selected = System.Array.IndexOf(WorldBuilder.Airports, game.NavTarget);
            else Selected = System.Array.IndexOf(WorldBuilder.Airports, WorldBuilder.Nearest(fm.transform.position));
            if (Selected < 0) Selected = 0;
            // float the tablet ~55 cm in front of the eyes, slightly low, facing the pilot
            Vector3 hp = vr.trackingSpace.InverseTransformPoint(head.position);
            Vector3 hf = vr.trackingSpace.InverseTransformDirection(head.forward); hf.y = 0f;
            if (hf.sqrMagnitude < 1e-4f) hf = Vector3.forward;
            hf.Normalize();
            panel.localPosition = hp + hf * 0.55f + Vector3.down * 0.12f;
            panel.localRotation = Quaternion.LookRotation(hf, Vector3.up) * Quaternion.Euler(12f, 0, 0);
            panel.gameObject.SetActive(true);
            Time.timeScale = 0f;     // pause while planning
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            ClosedFrame = Time.frameCount;
            panel.gameObject.SetActive(false);
            Time.timeScale = 1f;
        }

        void Go(bool final)
        {
            var ap = WorldBuilder.Airports[Selected];
            Close();
            game.TeleportTo(ap, final);
        }

        void SetNav()
        {
            var ap = WorldBuilder.Airports[Selected];
            game.NavTarget = game.NavTarget == ap ? null : ap;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (NetPanel.AnyOpen || MainMenu.AnyOpen || ControlsWindow.AnyOpen) return;   // typing a room code / main menu
            if (KeyBindings.Down(Act.Map)) Toggle();
            if (vr != null && vr.MapToggleDown) Toggle();
            if (!IsOpen) return;

            var aps = WorldBuilder.Airports;
            if (kb != null)
            {
                if (kb.escapeKey.wasPressedThisFrame) { Close(); return; }
                var digits = new[] { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key, kb.digit5Key, kb.digit6Key, kb.digit7Key, kb.digit8Key, kb.digit9Key };
                for (int d = 0; d < digits.Length && d < aps.Length; d++) if (digits[d].wasPressedThisFrame) Selected = d;
                if (kb.leftArrowKey.wasPressedThisFrame) Selected = (Selected + aps.Length - 1) % aps.Length;
                if (kb.rightArrowKey.wasPressedThisFrame) Selected = (Selected + 1) % aps.Length;
                if (kb.tKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) { Go(false); return; }
                if (kb.fKey.wasPressedThisFrame) { Go(true); return; }
                if (kb.nKey.wasPressedThisFrame) SetNav();
            }

            // VR fingertips (real hand positions — the tablet floats in tracking space)
            tips.Clear();
            vr.GetFingertipsWorld(tips);
            for (int i = 0; i < buttons.Count; i++) { if (!IsOpen) break; buttons[i].Poke(tips); }
            if (!IsOpen) return;

            // markers
            for (int i = 0; i < apMarkerImgs.Count; i++)
            {
                bool nav = game.NavTarget == aps[i];
                apMarkerImgs[i].color = i == Selected ? new Color(1f, 0.85f, 0.2f) : nav ? new Color(0.4f, 0.7f, 1f) : new Color(0.9f, 0.9f, 0.9f);
                apMarkers[i].sizeDelta = i == Selected ? new Vector2(8, 34) : new Vector2(5, 26);
            }
            playerArrow.anchoredPosition = ToMap(fm.transform.position);
            playerArrow.localRotation = Quaternion.Euler(0, 0, -fm.Heading);
            int e = 0;
            foreach (var en in game.Enemies)
            {
                if (en == null || en.Dead) continue;
                if (e >= enemyDots.Count)
                {
                    var d = Child(mapRT, "Bandit", Vector2.zero, new Vector2(8, 8));
                    d.gameObject.AddComponent<Image>().color = new Color(1f, 0.3f, 0.25f);
                    enemyDots.Add(d);
                }
                enemyDots[e].gameObject.SetActive(true);
                enemyDots[e++].anchoredPosition = ToMap(en.transform.position);
            }
            for (; e < enemyDots.Count; e++) enemyDots[e].gameObject.SetActive(false);

            var sel = aps[Selected];
            float dist = Vector2.Distance(new Vector2(fm.transform.position.x, fm.transform.position.z), sel.center);
            Vector3 to = sel.Center3 - fm.transform.position;
            float brg = (Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + 360f) % 360f;
            info.text = $"{sel.name}   RWY {sel.RunwayName(1)}/{sel.RunwayName(-1)}   ELEV {sel.elevation * Units.MToFeet:0} FT\n" +
                        $"BRG {brg:000}   {dist / 1852f:0.0} NM\n" +
                        "1-4 select   T/Enter RUNWAY   F FINAL   N NAV   M close";
        }

        void OnDestroy() { if (IsOpen) Time.timeScale = 1f; }
    }
}
