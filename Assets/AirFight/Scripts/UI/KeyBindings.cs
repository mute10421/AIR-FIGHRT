using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace AirFight
{
    /// <summary>Everything the pilot can do with keyboard / mouse (each action has two key slots).</summary>
    public enum Act
    {
        PitchUp, PitchDown, RollLeft, RollRight, YawLeft, YawRight,
        ThrottleUp, ThrottleDown, PostStall, SpeedBrake, Gear, Autoland,
        FireGun, FireSelected, FireMissile, WeaponSelect, Flares,
        LookAround, View, Recenter, Map, NetPanel, Respawn, Settings,
    }

    /// <summary>
    /// 컴퓨터 조작 키 설정. 동작마다 키 2개(키보드 키 또는 마우스 버튼)를 지정할 수 있고 PlayerPrefs에 저장된다.
    /// 바인딩 문자열: "K:W" (키보드 Key 이름), "M:Left" / "M:Right" / "M:Middle" / "M:Back" / "M:Forward", "" (없음).
    /// </summary>
    public static class KeyBindings
    {
        public const int Slots = 2;
        static string[,] map;
        static bool loaded;
        static float lookSens = 1f;
        static bool invertPitch;
        public static int Version { get; private set; }

        public static readonly Act[] All = (Act[])System.Enum.GetValues(typeof(Act));

        public static string Label(Act a)
        {
            switch (a)
            {
                case Act.PitchUp: return "기수 올림 (당기기)";
                case Act.PitchDown: return "기수 내림 (밀기)";
                case Act.RollLeft: return "왼쪽 롤";
                case Act.RollRight: return "오른쪽 롤";
                case Act.YawLeft: return "왼쪽 러더";
                case Act.YawRight: return "오른쪽 러더";
                case Act.ThrottleUp: return "추력 증가";
                case Act.ThrottleDown: return "추력 감소";
                case Act.PostStall: return "포스트스톨 (코브라)";
                case Act.SpeedBrake: return "에어브레이크 / 휠 브레이크";
                case Act.Gear: return "랜딩기어";
                case Act.Autoland: return "자동 착륙";
                case Act.FireGun: return "기관포 발사";
                case Act.FireSelected: return "선택 무장 발사";
                case Act.FireMissile: return "미사일 발사";
                case Act.WeaponSelect: return "무장 전환 (기총/미사일)";
                case Act.Flares: return "플레어";
                case Act.LookAround: return "둘러보기 (누른 채 마우스)";
                case Act.View: return "시점 전환 (조종석/3인칭)";
                case Act.Recenter: return "시점 정렬 (VR)";
                case Act.Map: return "지도";
                case Act.NetPanel: return "멀티 패널";
                case Act.Respawn: return "재출격 (격추 후)";
                case Act.Settings: return "조작 설정 창";
            }
            return a.ToString();
        }

        static string[] Default(Act a)
        {
            switch (a)
            {
                case Act.PitchUp: return new[] { "K:W", "K:UpArrow" };
                case Act.PitchDown: return new[] { "K:S", "K:DownArrow" };
                case Act.RollLeft: return new[] { "K:A", "K:LeftArrow" };
                case Act.RollRight: return new[] { "K:D", "K:RightArrow" };
                case Act.YawLeft: return new[] { "K:Q", "" };
                case Act.YawRight: return new[] { "K:E", "" };
                case Act.ThrottleUp: return new[] { "K:LeftShift", "K:Equals" };
                case Act.ThrottleDown: return new[] { "K:LeftCtrl", "K:Minus" };
                case Act.PostStall: return new[] { "K:X", "" };
                case Act.SpeedBrake: return new[] { "K:B", "" };
                case Act.Gear: return new[] { "K:G", "" };
                case Act.Autoland: return new[] { "K:L", "" };
                case Act.FireGun: return new[] { "K:J", "M:Left" };
                case Act.FireSelected: return new[] { "K:Space", "" };
                case Act.FireMissile: return new[] { "K:F", "" };
                case Act.WeaponSelect: return new[] { "K:Tab", "" };
                case Act.Flares: return new[] { "K:C", "" };
                case Act.LookAround: return new[] { "M:Right", "" };
                case Act.View: return new[] { "K:V", "" };
                case Act.Recenter: return new[] { "K:Home", "" };
                case Act.Map: return new[] { "K:M", "" };
                case Act.NetPanel: return new[] { "K:P", "" };
                case Act.Respawn: return new[] { "K:R", "" };
                case Act.Settings: return new[] { "K:F1", "" };
            }
            return new[] { "", "" };
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            map = new string[All.Length, Slots];
            foreach (var a in All)
            {
                var d = Default(a);
                for (int s = 0; s < Slots; s++)
                {
                    string v = d[s];
                    try { v = PlayerPrefs.GetString($"AF_Key_{a}_{s}", d[s]); } catch { }
                    map[(int)a, s] = v;
                }
            }
            try { lookSens = PlayerPrefs.GetFloat("AF_LookSens", 1f); invertPitch = PlayerPrefs.GetInt("AF_InvertPitch", 0) == 1; } catch { }
        }

        static void Save(Act a, int s) { try { PlayerPrefs.SetString($"AF_Key_{a}_{s}", map[(int)a, s]); PlayerPrefs.Save(); } catch { } }

        public static string Get(Act a, int slot) { Load(); return map[(int)a, slot]; }

        /// <summary>Assign a binding; the same key is taken away from any other action. Returns the action it was taken from (or null).</summary>
        public static Act? Set(Act a, int slot, string binding)
        {
            Load();
            Act? stolen = null;
            if (!string.IsNullOrEmpty(binding))
                foreach (var o in All)
                    for (int s = 0; s < Slots; s++)
                        if ((o != a || s != slot) && map[(int)o, s] == binding) { map[(int)o, s] = ""; Save(o, s); stolen = o; }
            map[(int)a, slot] = binding;
            Save(a, slot);
            Version++;
            return stolen;
        }

        public static void ResetAll()
        {
            Load();
            foreach (var a in All) { var d = Default(a); for (int s = 0; s < Slots; s++) { map[(int)a, s] = d[s]; Save(a, s); } }
            LookSensitivity = 1f; InvertPitch = false;
            Version++;
        }

        public static float LookSensitivity
        {
            get { Load(); return lookSens; }
            set { Load(); lookSens = Mathf.Clamp(value, 0.2f, 3f); try { PlayerPrefs.SetFloat("AF_LookSens", lookSens); PlayerPrefs.Save(); } catch { } }
        }

        public static bool InvertPitch
        {
            get { Load(); return invertPitch; }
            set { Load(); invertPitch = value; try { PlayerPrefs.SetInt("AF_InvertPitch", value ? 1 : 0); PlayerPrefs.Save(); } catch { } }
        }

        // ------------------------------------------------------------------ queries
        static ButtonControl Control(string b)
        {
            if (string.IsNullOrEmpty(b) || b.Length < 3) return null;
            string n = b.Substring(2);
            if (b[0] == 'K')
            {
                var kb = Keyboard.current;
                if (kb == null || !System.Enum.TryParse(n, out Key k) || k == Key.None) return null;
                try { return kb[k]; } catch { return null; }
            }
            if (b[0] == 'M')
            {
                var m = Mouse.current;
                if (m == null) return null;
                switch (n)
                {
                    case "Left": return m.leftButton;
                    case "Right": return m.rightButton;
                    case "Middle": return m.middleButton;
                    case "Back": return m.backButton;
                    case "Forward": return m.forwardButton;
                }
            }
            return null;
        }

        public static bool Held(Act a)
        {
            Load();
            for (int s = 0; s < Slots; s++) { var c = Control(map[(int)a, s]); if (c != null && c.isPressed) return true; }
            return false;
        }

        public static bool Down(Act a)
        {
            Load();
            for (int s = 0; s < Slots; s++) { var c = Control(map[(int)a, s]); if (c != null && c.wasPressedThisFrame) return true; }
            return false;
        }

        public static float Axis(Act neg, Act pos) => (Held(pos) ? 1f : 0f) - (Held(neg) ? 1f : 0f);

        /// <summary>The key / mouse button pressed this frame, as a binding string (null if none). Used while rebinding.</summary>
        public static string PressedThisFrame()
        {
            var kb = Keyboard.current;
            if (kb != null)
                foreach (var k in kb.allKeys)
                    if (k != null && k.wasPressedThisFrame && k.keyCode != Key.Escape) return "K:" + k.keyCode;
            var m = Mouse.current;
            if (m != null)
            {
                if (m.leftButton.wasPressedThisFrame) return "M:Left";
                if (m.rightButton.wasPressedThisFrame) return "M:Right";
                if (m.middleButton.wasPressedThisFrame) return "M:Middle";
                if (m.backButton.wasPressedThisFrame) return "M:Back";
                if (m.forwardButton.wasPressedThisFrame) return "M:Forward";
            }
            return null;
        }

        /// <summary>"W", "Space", "Mouse L" … for menus and banners.</summary>
        public static string Pretty(string b)
        {
            if (string.IsNullOrEmpty(b)) return "—";
            string n = b.Length > 2 ? b.Substring(2) : b;
            if (b[0] == 'M')
                return n == "Left" ? "마우스 왼쪽" : n == "Right" ? "마우스 오른쪽" : n == "Middle" ? "마우스 휠클릭" : n == "Back" ? "마우스 뒤로" : n == "Forward" ? "마우스 앞으로" : n;
            switch (n)
            {
                case "UpArrow": return "↑"; case "DownArrow": return "↓"; case "LeftArrow": return "←"; case "RightArrow": return "→";
                case "LeftShift": return "L-Shift"; case "RightShift": return "R-Shift";
                case "LeftCtrl": return "L-Ctrl"; case "RightCtrl": return "R-Ctrl";
                case "LeftAlt": return "L-Alt"; case "RightAlt": return "R-Alt";
                case "Equals": return "="; case "Minus": return "-";
                case "Comma": return ","; case "Period": return "."; case "Slash": return "/"; case "Backslash": return "\\";
                case "Semicolon": return ";"; case "Quote": return "'"; case "LeftBracket": return "["; case "RightBracket": return "]";
                case "Backquote": return "`";
            }
            if (n.StartsWith("Digit")) return n.Substring(5);
            if (n.StartsWith("Numpad")) return "Num " + n.Substring(6);
            return n;
        }

        public static string Names(Act a)
        {
            string a0 = Get(a, 0), a1 = Get(a, 1);
            if (string.IsNullOrEmpty(a0)) return Pretty(a1);
            if (string.IsNullOrEmpty(a1)) return Pretty(a0);
            return Pretty(a0) + " / " + Pretty(a1);
        }
    }
}
