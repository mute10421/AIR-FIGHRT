using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AirFight
{
    /// <summary>
    /// F-16 스타일 HUD. 30m 앞에 배치해 실제 HUD처럼 "무한원 시준(collimated)" 효과를 흉내낸다
    /// (머리를 움직여도 심볼이 바깥 세계에 붙어 있음). 피치 래더·FPM(속도벡터)·속도/고도·방위·G·마하·AoA,
    /// 표적 지시 박스, 기관포 리드 서클, AIM-9 시커 서클, 경고 표시.
    /// </summary>
    public class HUD : MonoBehaviour
    {
        public static readonly Color HudGreen = new Color(0.35f, 1f, 0.45f, 0.95f);
        public static readonly Color HostileRed = new Color(1f, 0.22f, 0.18f, 0.95f);
        public static readonly Color FriendGreen = new Color(0.25f, 1f, 0.35f, 0.95f);
        const float Distance = 30f;
        const float PxPerDeg = 18f;
        float scale;

        public FlightModel fm;
        public Weapons weapons;
        public Damageable self;
        public GameManager game;
        public VRCockpitInput vr;
        public LandingGear gear;
        public Autoland autoland;
        Transform anchor;

        Font font;
        Image[] tdOuter, seekerImgs, leadImgs;
        readonly List<(RectTransform rt, Image[] imgs, Text txt)> contactBoxes = new List<(RectTransform, Image[], Text)>();
        readonly List<(RectTransform rt, Text txt)> rimArrows = new List<(RectTransform, Text)>();
        readonly List<(Damageable d, float dist)> arrowCands = new List<(Damageable, float)>();
        Text banditTxt, matchTxt;
        readonly List<Text> feedRows = new List<Text>();
        RectTransform canvasRT, ladderRot, ladderContent, fpm, targetBox, leadCircle, seekerCircle, headingTape;
        Text speedTxt, altTxt, headingTxt, machTxt, gTxt, aoaTxt, thrTxt, weaponTxt, warnTxt, rangeTxt, infoTxt, ralt;
        readonly List<Text> headingLabels = new List<Text>();
        float maxG = 1f;

        // MFDs
        RectTransform radarRoot;
        readonly List<RectTransform> radarBlips = new List<RectTransform>();
        Text radarRangeTxt, storesTxt;
        const float RadarRange = 40000f;

        public void Build(Transform hudAnchor, Transform leftMfd, Transform rightMfd, Camera cam)
        {
            anchor = hudAnchor;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            scale = Distance * Mathf.Tan(Mathf.Deg2Rad) / PxPerDeg;

            var canvas = MakeCanvas("HUDCanvas", hudAnchor, new Vector3(0, 0, Distance), new Vector2(560, 560), scale, cam);
            canvasRT = (RectTransform)canvas.transform;

            // --- Pitch ladder (clipped to the HUD field of view) ---
            var mask = Rect("LadderMask", canvasRT, Vector2.zero, new Vector2(300, 330));
            mask.gameObject.AddComponent<RectMask2D>();
            ladderRot = Rect("LadderRot", mask, Vector2.zero, Vector2.zero);
            ladderContent = Rect("LadderContent", ladderRot, Vector2.zero, Vector2.zero);
            for (int p = -85; p <= 85; p += 5)
            {
                float y = p * PxPerDeg;
                if (p == 0)
                {
                    Img(ladderContent, new Vector2(-150, y), new Vector2(200, 2));
                    Img(ladderContent, new Vector2(150, y), new Vector2(200, 2));
                    continue;
                }
                bool pos = p > 0;
                for (int side = -1; side <= 1; side += 2)
                {
                    if (pos) Img(ladderContent, new Vector2(side * 55, y), new Vector2(70, 2));
                    else
                    {
                        Img(ladderContent, new Vector2(side * 35, y), new Vector2(26, 2));
                        Img(ladderContent, new Vector2(side * 76, y), new Vector2(26, 2));
                    }
                    Img(ladderContent, new Vector2(side * 89, y + (pos ? -6 : 6)), new Vector2(2, 12));
                    Txt(ladderContent, new Vector2(side * 112, y), Mathf.Abs(p).ToString(), 14, TextAnchor.MiddleCenter);
                }
            }

            // --- Boresight gun cross ---
            Img(canvasRT, new Vector2(0, 0), new Vector2(18, 2));
            Img(canvasRT, new Vector2(0, 0), new Vector2(2, 18));

            // --- Flight path marker ---
            fpm = Rect("FPM", canvasRT, Vector2.zero, new Vector2(30, 30));
            Circle(fpm, 8f, 12);
            Img(fpm, new Vector2(-15, 0), new Vector2(14, 2));
            Img(fpm, new Vector2(15, 0), new Vector2(14, 2));
            Img(fpm, new Vector2(0, 13), new Vector2(2, 9));

            // --- Speed / altitude boxes ---
            Box(canvasRT, new Vector2(-200, 40), new Vector2(76, 26));
            speedTxt = Txt(canvasRT, new Vector2(-200, 40), "000", 20, TextAnchor.MiddleCenter);
            Box(canvasRT, new Vector2(200, 40), new Vector2(90, 26));
            altTxt = Txt(canvasRT, new Vector2(200, 40), "00000", 20, TextAnchor.MiddleCenter);
            ralt = Txt(canvasRT, new Vector2(200, 10), "", 15, TextAnchor.MiddleCenter);
            machTxt = Txt(canvasRT, new Vector2(-200, -70), "", 15, TextAnchor.MiddleLeft);
            gTxt = Txt(canvasRT, new Vector2(-200, 85), "", 16, TextAnchor.MiddleLeft);
            aoaTxt = Txt(canvasRT, new Vector2(-200, -95), "", 15, TextAnchor.MiddleLeft);
            thrTxt = Txt(canvasRT, new Vector2(-200, -120), "", 15, TextAnchor.MiddleLeft);
            infoTxt = Txt(canvasRT, new Vector2(200, -95), "", 15, TextAnchor.MiddleRight);

            // --- Heading tape ---
            headingTape = Rect("HeadingTape", canvasRT, new Vector2(0, 225), new Vector2(240, 30));
            headingTape.gameObject.AddComponent<RectMask2D>();
            for (int i = 0; i < 9; i++) headingLabels.Add(Txt(headingTape, Vector2.zero, "", 14, TextAnchor.MiddleCenter));
            Img(canvasRT, new Vector2(0, 205), new Vector2(2, 10));
            headingTxt = Txt(canvasRT, new Vector2(0, 192), "", 14, TextAnchor.MiddleCenter);

            // --- Weapon / warnings ---
            weaponTxt = Txt(canvasRT, new Vector2(-200, -150), "", 16, TextAnchor.MiddleLeft);
            rangeTxt = Txt(canvasRT, new Vector2(200, -150), "", 15, TextAnchor.MiddleRight);
            warnTxt = Txt(canvasRT, new Vector2(0, -110), "", 26, TextAnchor.MiddleCenter);
            banditTxt = Txt(canvasRT, new Vector2(0, -140), "", 17, TextAnchor.MiddleCenter, HostileRed);
            for (int i = 0; i < KillFeed.Rows; i++) { var ft = Txt(canvasRT, new Vector2(212, 252 - i * 19), "", 14, TextAnchor.MiddleRight); ft.font = MainMenu.KoreanFont(); feedRows.Add(ft); }
            matchTxt = Txt(canvasRT, new Vector2(0, 252), "", 16, TextAnchor.MiddleCenter);

            // --- Target designator box & lead / seeker circles ---
            targetBox = Rect("TD", canvasRT, Vector2.zero, new Vector2(26, 26));
            Box(targetBox, Vector2.zero, new Vector2(26, 26), HostileRed);
            Box(targetBox, Vector2.zero, new Vector2(34, 34), HostileRed);          // outer box: shown only when locked
            tdOuter = new Image[4]; var tdImgs = targetBox.GetComponentsInChildren<Image>(true);
            for (int k = 0; k < 4; k++) tdOuter[k] = tdImgs[4 + k];
            leadCircle = Rect("Lead", canvasRT, Vector2.zero, new Vector2(20, 20));
            Circle(leadCircle, 10f, 12);
            Img(leadCircle, Vector2.zero, new Vector2(3, 3));
            seekerCircle = Rect("Seeker", canvasRT, Vector2.zero, new Vector2(60, 60));
            Circle(seekerCircle, 30f, 20);

            BuildLandingGuidance();
            BuildRadar(leftMfd, cam);
            BuildStores(rightMfd, cam);
        }

        Canvas MakeCanvas(string name, Transform parent, Vector3 localPos, Vector2 size, float s, Camera cam)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            c.worldCamera = cam;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localPosition = localPos;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one * s;
            return c;
        }

        RectTransform Rect(string name, Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        Image Img(Transform parent, Vector2 pos, Vector2 size, float rot = 0f, Color? c = null)
        {
            var rt = Rect("I", parent, pos, size);
            rt.localRotation = Quaternion.Euler(0, 0, rot);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c ?? HudGreen;
            img.raycastTarget = false;
            return img;
        }

        Text Txt(Transform parent, Vector2 pos, string s, int size, TextAnchor align, Color? c = null)
        {
            var rt = Rect("T", parent, pos, new Vector2(160, size + 8));
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.color = c ?? HudGreen;
            t.text = s;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            if (align == TextAnchor.MiddleLeft) { rt.pivot = new Vector2(0, 0.5f); rt.anchoredPosition = pos - new Vector2(40, 0); }
            if (align == TextAnchor.MiddleRight) { rt.pivot = new Vector2(1, 0.5f); rt.anchoredPosition = pos + new Vector2(45, 0); }
            return t;
        }

        void Box(Transform parent, Vector2 c, Vector2 size, Color? col = null)
        {
            Img(parent, c + new Vector2(0, size.y / 2), new Vector2(size.x, 2), 0, col);
            Img(parent, c - new Vector2(0, size.y / 2), new Vector2(size.x, 2), 0, col);
            Img(parent, c + new Vector2(size.x / 2, 0), new Vector2(2, size.y), 0, col);
            Img(parent, c - new Vector2(size.x / 2, 0), new Vector2(2, size.y), 0, col);
        }

        /// <summary>Outline triangle (apex up) of side length a, centred on the parent.</summary>
        void Triangle(Transform parent, float a, Color? col = null)
        {
            float h = a * 0.866f, yb = -h / 3f, yt = h * 2f / 3f;
            float ang = Mathf.Atan2(h, a * 0.5f) * Mathf.Rad2Deg;
            Img(parent, new Vector2(0, yb), new Vector2(a + 1f, 2), 0f, col);
            Img(parent, new Vector2(-a * 0.25f, (yb + yt) * 0.5f), new Vector2(a + 1f, 2), ang, col);
            Img(parent, new Vector2(a * 0.25f, (yb + yt) * 0.5f), new Vector2(a + 1f, 2), -ang, col);
        }

        void Circle(Transform parent, float r, int segs, Color? col = null)
        {
            float seg = 2f * Mathf.PI * r / segs * 1.05f;
            for (int i = 0; i < segs; i++)
            {
                float a = i * Mathf.PI * 2f / segs;
                Img(parent, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, new Vector2(seg, 2), a * Mathf.Rad2Deg + 90f, col);
            }
        }

        /// <summary>Re-home the HUD (cockpit HUD anchor, or the 3rd-person chase anchor).</summary>
        public void SetAnchor(Transform a)
        {
            anchor = a;
            canvasRT.SetParent(a, false);
            canvasRT.localPosition = new Vector3(0, 0, Distance);
            canvasRT.localRotation = Quaternion.identity;
        }

        bool Project(Vector3 world, out Vector2 hud)
        {
            Vector3 l = anchor.InverseTransformPoint(world);
            hud = Vector2.zero;
            if (l.z <= 1f) return false;
            hud = new Vector2(l.x / l.z, l.y / l.z) * (Distance / scale);
            return true;
        }

        bool ProjectDir(Vector3 dir, out Vector2 hud)
        {
            return Project(anchor.position + dir.normalized * 1000f, out hud);
        }

        static bool InField(Vector2 p, float r = 260f) => Mathf.Abs(p.x) < r && Mathf.Abs(p.y) < r;

        void LateUpdate()
        {
            if (fm == null || anchor == null) return;

            // Pitch ladder & horizon
            ladderRot.localRotation = Quaternion.Euler(0, 0, fm.Bank);
            ladderContent.anchoredPosition = new Vector2(0, -fm.Pitch * PxPerDeg);

            // FPM
            Vector3 v = fm.Body.linearVelocity;
            if (v.sqrMagnitude > 25f && ProjectDir(v, out var fp))
            {
                fpm.gameObject.SetActive(true);
                fpm.anchoredPosition = new Vector2(Mathf.Clamp(fp.x, -240, 240), Mathf.Clamp(fp.y, -240, 240));
            }
            else fpm.gameObject.SetActive(false);

            float kts = fm.Airspeed * Units.MsToKnots * Mathf.Sqrt(Mathf.Exp(-Mathf.Max(0, fm.Altitude) / 8500f)); // ≈ CAS
            speedTxt.text = Mathf.RoundToInt(kts).ToString();
            altTxt.text = (Mathf.RoundToInt(fm.Altitude * Units.MToFeet / 10f) * 10).ToString("N0");
            machTxt.text = fm.Mach.ToString("0.00");
            maxG = Mathf.Max(maxG, fm.GLoad);
            gTxt.text = $"{fm.GLoad:0.0}G   MAX {maxG:0.0}";
            aoaTxt.text = $"AOA {fm.AoA:0}";
            thrTxt.text = fm.Afterburner ? $"AB {Mathf.RoundToInt((fm.throttle - FlightModel.MilDetent) / (1 - FlightModel.MilDetent) * 100)}%" :
                                          $"THR {Mathf.RoundToInt(fm.throttle / FlightModel.MilDetent * 100)}%{(fm.speedBrake ? "  SPD BRK" : "")}";
            // radar altimeter below 5000 ft
            if (Physics.Raycast(fm.transform.position, Vector3.down, out var rh, 1524f, ~0, QueryTriggerInteraction.Ignore))
                ralt.text = $"AL {Mathf.RoundToInt(rh.distance * Units.MToFeet / 10f) * 10}";
            else ralt.text = "";

            // Heading tape (5° ticks labelled every 10°)
            float hdg = fm.Heading;
            headingTxt.text = Mathf.RoundToInt(hdg).ToString("000");
            float baseH = Mathf.Floor(hdg / 10f) * 10f;
            for (int i = 0; i < headingLabels.Count; i++)
            {
                float h = baseH + (i - 4) * 10f;
                float x = Mathf.DeltaAngle(hdg, h) * 6f;
                var t = headingLabels[i];
                ((RectTransform)t.transform).anchoredPosition = new Vector2(x, 0);
                int hv = Mathf.RoundToInt((h % 360f + 360f) % 360f) / 10;
                t.text = hv.ToString("00");
            }

            UpdateWeapons();
            UpdateKillFeed();
            UpdateWarnings();
            UpdateLandingGuidance();
            UpdateNav();
            UpdateMfds();
        }

        Damageable CurrentTarget()
        {
            if (weapons.SeekerTarget != null && !weapons.SeekerTarget.Dead) return weapons.SeekerTarget;   // the box = the missile's target
            // nearest enemy in front hemisphere
            Damageable best = null; float bestScore = float.MaxValue;
            foreach (var e in game.Enemies)
            {
                if (e == null || e.Dead) continue;
                Vector3 to = e.transform.position - fm.transform.position;
                float ang = Vector3.Angle(fm.transform.forward, to);
                if (ang > 60f) continue;
                float score = to.magnitude * (1f + ang / 15f);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            return best;
        }

        /// <summary>
        /// Contact cues: every jet on the HUD gets a box (hostile red, wingman green) with its range;
        /// hostiles off the HUD get a red arrow on the HUD rim pointing toward them (nearest 6), and the nearest
        /// hidden bandit is called out ("BANDIT 4 O'CLOCK HIGH 8.2 KM").
        /// </summary>
        void UpdateContacts(Damageable tgt)
        {
            int n = 0, na = 0;
            Damageable nearestHidden = null; float nearestHiddenD = float.MaxValue;
            arrowCands.Clear();
            void Mark(Damageable d, Color c, bool hostile)
            {
                if (d == null || d.Dead) return;
                float dist = Vector3.Distance(d.transform.position, fm.transform.position);
                if (dist > 60000f) return;
                bool onHud = Project(d.transform.position, out var p) && InField(p);
                if (!onHud)
                {
                    if (hostile)
                    {
                        arrowCands.Add((d, dist));
                        if (dist < nearestHiddenD) { nearestHiddenD = dist; nearestHidden = d; }
                    }
                    return;
                }
                if (d == tgt) return;                       // the target designator box already marks it
                if (dist > 20000f) return;
                if (n >= contactBoxes.Count)
                {
                    var rt = Rect("Contact", canvasRT, Vector2.zero, new Vector2(22, 22));
                    Triangle(rt, 22f);                      // triangle = contact; the square is kept for the designated / locked target
                    var imgs = rt.GetComponentsInChildren<Image>(true);
                    var t = Txt(rt, new Vector2(0, -22), "", 13, TextAnchor.MiddleCenter);
                    contactBoxes.Add((rt, imgs, t));
                }
                var cb = contactBoxes[n++];
                cb.rt.gameObject.SetActive(true);
                cb.rt.anchoredPosition = p;
                foreach (var im in cb.imgs) im.color = c;
                cb.txt.color = c;
                cb.txt.text = $"{dist / 1000f:0.0}";
            }
            foreach (var e in game.Enemies) Mark(e, HostileRed, true);
            foreach (var a in game.Allies) Mark(a, FriendGreen, false);
            for (int i = n; i < contactBoxes.Count; i++) contactBoxes[i].rt.gameObject.SetActive(false);

            // rim arrows toward off-HUD bandits (nearest first)
            arrowCands.Sort((x, y) => x.dist.CompareTo(y.dist));
            foreach (var (d, dist) in arrowCands)
            {
                if (na >= 6) break;
                Vector3 l = anchor.InverseTransformDirection(d.transform.position - anchor.position);
                Vector2 dir2 = new Vector2(l.x, l.y);
                if (dir2.sqrMagnitude < 1e-4f) dir2 = Vector2.down;      // dead astern
                dir2.Normalize();
                if (na >= rimArrows.Count)
                {
                    var rt = Rect("BanditArrow", canvasRT, Vector2.zero, new Vector2(24, 24));
                    Img(rt, new Vector2(-3, 5), new Vector2(16, 3), -38f, HostileRed);
                    Img(rt, new Vector2(-3, -5), new Vector2(16, 3), 38f, HostileRed);
                    Img(rt, new Vector2(-12, 0), new Vector2(10, 2), 0f, HostileRed);
                    var t = Txt(canvasRT, Vector2.zero, "", 14, TextAnchor.MiddleCenter, HostileRed);
                    rimArrows.Add((rt, t));
                }
                var ar = rimArrows[na++];
                ar.rt.gameObject.SetActive(true);
                ar.txt.gameObject.SetActive(true);
                // closer bandits sit on the rim brighter and bigger
                float r = 235f;
                ar.rt.anchoredPosition = dir2 * r;
                ar.rt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir2.y, dir2.x) * Mathf.Rad2Deg);
                ar.rt.localScale = Vector3.one * Mathf.Lerp(1.3f, 0.8f, Mathf.InverseLerp(2000f, 25000f, dist));
                ((RectTransform)ar.txt.transform).anchoredPosition = dir2 * (r - 30f);
                ar.txt.text = $"{dist / 1000f:0.0}";
            }
            for (int i = na; i < rimArrows.Count; i++) { rimArrows[i].rt.gameObject.SetActive(false); rimArrows[i].txt.gameObject.SetActive(false); }

            // spoken-style call for the nearest bandit we can't see on the HUD
            if (banditTxt)
            {
                if (nearestHidden != null)
                {
                    Vector3 rel = nearestHidden.transform.position - fm.transform.position;
                    Vector3 fwd = fm.transform.forward; fwd.y = 0f;
                    Vector3 flat = rel; flat.y = 0f;
                    float ang = Vector3.SignedAngle(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward, flat, Vector3.up);
                    int clock = Mathf.RoundToInt(Mathf.Repeat(ang, 360f) / 30f); if (clock == 0) clock = 12;
                    float elev = Mathf.Atan2(rel.y, flat.magnitude) * Mathf.Rad2Deg;
                    string hl = elev > 10f ? " HIGH" : elev < -10f ? " LOW" : " LEVEL";
                    banditTxt.text = $"BANDIT {clock} O'CLOCK{hl}  {nearestHiddenD / 1000f:0.0} KM";
                }
                else banditTxt.text = "";
            }
        }

        /// <summary>Kill log (top right): newest at the top, fades after a few seconds. Plus the PvP match clock.</summary>
        void UpdateKillFeed()
        {
            var list = KillFeed.Entries;
            float now = Time.unscaledTime;
            list.RemoveAll(e => now - e.time > KillFeed.Life);
            for (int i = 0; i < feedRows.Count; i++)
            {
                int k = list.Count - 1 - i;
                if (k < 0) { feedRows[i].text = ""; continue; }
                var e = list[k];
                var c = e.color; c.a *= Mathf.Clamp01((KillFeed.Life - (now - e.time)) / 1.5f);
                feedRows[i].color = c;
                feedRows[i].text = e.text;
            }
            var net = NetSession.I;
            matchTxt.text = net != null && net.Active ? net.MatchClock() : "";
        }

        void UpdateWeapons()
        {
            var tgt = CurrentTarget();
            UpdateContacts(tgt);
            targetBox.gameObject.SetActive(false);
            leadCircle.gameObject.SetActive(false);
            rangeTxt.text = "";
            if (tgt != null && Project(tgt.transform.position, out var tp) && InField(tp))
            {
                targetBox.gameObject.SetActive(true);
                targetBox.anchoredPosition = tp;
                bool locked = weapons.missiles > 0 && weapons.Locked && weapons.SeekerTarget == tgt;   // F fires at it in any mode
                bool blink = !locked || (int)(Time.time * 6f) % 2 == 0;
                foreach (var im in tdOuter) im.enabled = locked && blink;
                float range = Vector3.Distance(tgt.transform.position, fm.transform.position);
                Vector3 relV = Vector3.zero;
                relV = Damageable.VelocityOf(tgt) - fm.Body.linearVelocity;
                float closure = -Vector3.Dot(relV, (tgt.transform.position - fm.transform.position).normalized);
                rangeTxt.text = $"{range / 1852f:0.0} NM\n{Mathf.RoundToInt(closure * Units.MsToKnots)} Vc";

                if (weapons.mode == WeaponMode.Gun && range < 2500f)
                {
                    // Lead (same ballistics as the gun): put the gun cross in this circle and fire.
                    Vector3 to = tgt.transform.position - fm.transform.position;
                    float tof = weapons.TimeOfFlight(range, to / Mathf.Max(range, 1f));
                    Vector3 aim = tgt.transform.position + relV * tof - Physics.gravity * 0.5f * tof * tof;
                    if (Project(aim, out var lp) && InField(lp))
                    {
                        leadCircle.gameObject.SetActive(true);
                        leadCircle.anchoredPosition = lp;
                        // circle = the real hit window at this range (jet size + proximity); it turns red with "SHOOT" when the cross is inside
                        float px = (weapons.bulletProximity + 4f) / Mathf.Max(range, 1f) * (Distance / scale);
                        float r = Mathf.Clamp(px, 6f, 45f);
                        leadCircle.localScale = Vector3.one * (r / 10f);
                        bool onTarget = lp.magnitude < r && range < 1500f;
                        var lc = onTarget ? HostileRed : HudGreen;
                        foreach (var im in leadImgs ??= leadCircle.GetComponentsInChildren<Image>(true)) im.color = lc;
                        if (onTarget && (int)(Time.time * 5f) % 2 == 0) rangeTxt.text += "\nSHOOT";
                    }
                }
            }

            // AIM-9 seeker circle: boresight while searching, slaved to target when locked
            if (weapons.mode == WeaponMode.AAM)
            {
                seekerCircle.gameObject.SetActive(true);
                Vector2 sp = Vector2.zero;
                if (weapons.SeekerTarget != null && Project(weapons.SeekerTarget.transform.position, out var stp))
                    sp = Vector2.Lerp(Vector2.zero, stp, weapons.LockProgress);
                seekerCircle.anchoredPosition = sp;
                seekerCircle.localScale = Vector3.one * (weapons.Locked ? 0.6f : 1f);
                var scol = weapons.Locked ? HostileRed : HudGreen;
                foreach (var im in seekerImgs ??= seekerCircle.GetComponentsInChildren<Image>(true)) im.color = scol;
                weaponTxt.text = $"SRM {weapons.missiles}  {(weapons.Locked ? "LOCK" : weapons.SeekerTarget ? "TRK" : "SRCH")}";
            }
            else
            {
                seekerCircle.gameObject.SetActive(false);
                weaponTxt.text = $"GUN {weapons.gunAmmo}";
            }
            if (vr && !vr.MasterArm) weaponTxt.text = "SAFE  " + weaponTxt.text;
            string gearTxt = gear == null ? "" : gear.Deploy >= 0.999f ? "GEAR DN" : gear.Deploy <= 0.001f ? "" : "GEAR ...";
            string brk = gear != null && gear.OnGround && (gear.parkingBrake || gear.brake > 0f) ? (gear.parkingBrake ? "  PARK BRK" : "  BRAKE") : "";
            infoTxt.text = $"FUEL {fm.Fuel:0} KG\nFLR {weapons.flares}\nDMG {Mathf.RoundToInt(100 - self.Health)}%\n{gearTxt}{brk}";
        }

        /// <summary>"MISSILE 7 O'CLOCK LOW 2.4 KM" — where to look for it.</summary>
        string MissileCall(Missile m, float dist)
        {
            Vector3 rel = m.transform.position - fm.transform.position;
            Vector3 fwd = fm.transform.forward; fwd.y = 0f;
            Vector3 flat = rel; flat.y = 0f;
            float ang = Vector3.SignedAngle(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward, flat, Vector3.up);
            int clock = Mathf.RoundToInt(Mathf.Repeat(ang, 360f) / 30f); if (clock == 0) clock = 12;
            float elev = Mathf.Atan2(rel.y, flat.magnitude) * Mathf.Rad2Deg;
            string hl = elev > 15f ? " HIGH" : elev < -15f ? " LOW" : "";
            return $"MISSILE {clock} O'CLOCK{hl}  {dist / 1000f:0.0} KM — FLARES / BREAK";
        }

        void UpdateWarnings()
        {
            string w = "";
            bool missile = false;
            Missile threat = null; float threatDist = float.MaxValue;
            foreach (var m in Missile.Active)
                if (m != null && m.Target == self)
                {
                    missile = true;
                    float d = Vector3.Distance(m.transform.position, fm.transform.position);
                    if (d < threatDist) { threatDist = d; threat = m; }
                }
            if (NetSession.I && NetSession.I.IncomingMissile) missile = true;
            int lockOn = Weapons.ThreatLevelOn(self);
            if (NetSession.I) lockOn = Mathf.Max(lockOn, NetSession.I.LockOnMe);
            Vector3 v = fm.Body.linearVelocity;
            bool pullUp = fm.VerticalSpeed < -5f && Physics.Raycast(fm.transform.position, v, v.magnitude * 6f, ~0, QueryTriggerInteraction.Ignore);
            if (self.Dead) w = "EJECT   ( Y / R : RESTART )";
            else if (pullUp) w = "PULL UP";
            else if (missile) w = threat != null ? MissileCall(threat, threatDist) : "MISSILE";
            else if (lockOn == 2) w = ((int)(Time.time * 4f) % 2 == 0) ? "LOCK" : "";
            else if (lockOn == 1 && game.WaveBanner == null) w = "SPIKE — TRACKED";
            else if (fm.Flameout) w = "FUEL 0 — FLAMEOUT — GLIDE TO A RUNWAY";
            else if (fm.Airspeed < 60f && !fm.onGround) w = "STALL";
            else if (fm.Bingo && !fm.onGround && ((int)(Time.time * 2f) % 2 == 0)) w = "BINGO FUEL — RTB";
            else if (game.OutOfBounds) w = "RTB - LEAVING AREA";
            else if (game.WaveBanner != null) w = game.WaveBanner;
            else if (autoland && autoland.Active) w = autoland.Status;
            warnTxt.text = w;
            if (game.Audio)
            {
                game.Audio.missileWarning = missile && !self.Dead;
                game.Audio.pullUpWarning = pullUp && !self.Dead;
                game.Audio.rwrLock = self.Dead || missile ? 0 : lockOn;
            }
        }

        // ---------------- Landing guidance (ILS-style needles + conformal runway + AoA indexer) ----------------
        RectTransform ilsRoot, locNeedle, gsNeedle;
        readonly RectTransform[] rwyLines = new RectTransform[4];
        Text ilsTxt, aoaIdxTxt;
        readonly List<RectTransform> airportBlips = new List<RectTransform>();

        void BuildLandingGuidance()
        {
            ilsRoot = Rect("ILS", canvasRT, Vector2.zero, new Vector2(10, 10));
            for (int i = 0; i < 4; i++) rwyLines[i] = Img(ilsRoot, Vector2.zero, new Vector2(10, 2)).rectTransform;
            // needle box (±1 dot = full scale)
            Color dim = new Color(0.35f, 1f, 0.45f, 0.45f);
            for (int i = -2; i <= 2; i++)
            {
                if (i == 0) continue;
                Img(ilsRoot, new Vector2(i * 40, -150), new Vector2(4, 4), 0, dim);
                Img(ilsRoot, new Vector2(-110, -60 + i * 40 - 90), new Vector2(4, 4), 0, dim);
            }
            locNeedle = Img(ilsRoot, new Vector2(0, -150), new Vector2(3, 150)).rectTransform;
            gsNeedle = Img(ilsRoot, new Vector2(0, -150), new Vector2(150, 3)).rectTransform;
            ilsTxt = Txt(ilsRoot, new Vector2(0, -250), "", 15, TextAnchor.MiddleCenter);
            aoaIdxTxt = Txt(ilsRoot, new Vector2(-200, 115), "", 16, TextAnchor.MiddleLeft);
            ilsRoot.gameObject.SetActive(false);
        }

        void SetLine(RectTransform r, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            r.anchoredPosition = (a + b) * 0.5f;
            r.sizeDelta = new Vector2(d.magnitude, 2f);
            r.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }

        void UpdateLandingGuidance()
        {
            bool show = gear != null && gear.Deploy > 0.5f && !fm.onGround && !self.Dead;
            if (ilsRoot.gameObject.activeSelf != show) ilsRoot.gameObject.SetActive(show);
            if (!show) return;

            Vector3 pos = fm.transform.position;
            var ap = WorldBuilder.Nearest(pos);
            Vector3 f = fm.Body.linearVelocity.sqrMagnitude > 25f ? fm.Body.linearVelocity : fm.transform.forward;
            float dirSign = Vector3.Dot(new Vector3(f.x, 0, f.z), ap.Dir) >= 0f ? 1f : -1f;
            Vector3 L = ap.Dir * dirSign;
            Vector3 right = Vector3.Cross(Vector3.up, L);
            Vector3 thr = ap.Threshold(dirSign);
            Vector3 aim = thr + L * 300f;                                  // touchdown zone (PAPI)
            Vector3 rel = pos - aim;
            float along = -Vector3.Dot(rel, L);                            // distance to go
            float lateral = Vector3.Dot(rel, right);
            float height = rel.y;
            float locDev = Mathf.Atan2(lateral, Mathf.Max(along, 50f)) * Mathf.Rad2Deg;
            float gsDev = Mathf.Atan2(height, Mathf.Max(along, 50f)) * Mathf.Rad2Deg - 3f;

            locNeedle.anchoredPosition = new Vector2(Mathf.Clamp(-locDev / 2.5f, -1f, 1f) * 80f, -150f);
            gsNeedle.anchoredPosition = new Vector2(0f, -150f + Mathf.Clamp(-gsDev / 0.7f, -1f, 1f) * 80f);

            // conformal runway outline
            float hw = ap.width * 0.5f;
            Vector3 far = ap.Threshold(-dirSign);
            Vector3[] c = { thr - right * hw, thr + right * hw, far + right * hw, far - right * hw };
            var pts = new Vector2[4]; bool ok = true;
            for (int i = 0; i < 4; i++) ok &= Project(c[i] + Vector3.up * 0.2f, out pts[i]);
            for (int i = 0; i < 4; i++)
            {
                rwyLines[i].gameObject.SetActive(ok);
                if (ok) SetLine(rwyLines[i], pts[i], pts[(i + 1) % 4]);
            }

            string idx = fm.AoA > 15f ? "SLOW (^)" : fm.AoA < 11f ? "FAST (v)" : "ON SPEED (o)";
            aoaIdxTxt.text = $"AOA {fm.AoA:0}  {idx}";
            ilsTxt.text = $"{ap.name}  RWY {ap.RunwayName(dirSign)}   {Mathf.Max(0f, along) / 1852f:0.0} NM   TGT AOA 13";
        }

        // ---------------- Navigation to a selected airport ----------------
        RectTransform navDiamond, navTapeMark;
        Text navTxt;

        void UpdateNav()
        {
            if (navTxt == null)
            {
                navDiamond = Rect("NavDiamond", canvasRT, Vector2.zero, new Vector2(16, 16));
                Box(navDiamond, Vector2.zero, new Vector2(16, 16), new Color(0.4f, 0.75f, 1f));
                navDiamond.localRotation = Quaternion.Euler(0, 0, 45);
                navTapeMark = Img(canvasRT, Vector2.zero, new Vector2(8, 8), 45f, new Color(0.4f, 0.75f, 1f)).rectTransform;
                navTxt = Txt(canvasRT, new Vector2(0, 170), "", 14, TextAnchor.MiddleCenter, new Color(0.5f, 0.8f, 1f));
            }
            var ap = game.NavTarget;
            bool on = ap != null && !self.Dead;
            navDiamond.gameObject.SetActive(false);
            navTapeMark.gameObject.SetActive(on);
            navTxt.gameObject.SetActive(on);
            if (!on) return;
            Vector3 to = ap.Center3 - fm.transform.position;
            float brg = (Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + 360f) % 360f;
            float dist = new Vector2(to.x, to.z).magnitude;
            navTapeMark.anchoredPosition = new Vector2(Mathf.Clamp(Mathf.DeltaAngle(fm.Heading, brg) * 6f, -118f, 118f), 207f);
            navTxt.text = $"NAV {ap.name}  BRG {brg:000}  {dist / 1852f:0.0} NM";
            if (Project(ap.Center3, out var p) && InField(p, 250f)) { navDiamond.gameObject.SetActive(true); navDiamond.anchoredPosition = p; }
        }

        // ---------------- MFDs ----------------
        void BuildRadar(Transform mfd, Camera cam)
        {
            var c = MakeCanvas("RadarMFD", mfd, new Vector3(0, 0, -0.006f), new Vector2(256, 256), 0.13f / 256f, cam);
            radarRoot = (RectTransform)c.transform;
            Color dim = new Color(0.3f, 1f, 0.4f, 0.4f);
            Box(radarRoot, Vector2.zero, new Vector2(240, 240), dim);
            Img(radarRoot, new Vector2(0, -60), new Vector2(1, 120), 0, dim);
            Img(radarRoot, new Vector2(0, 0), new Vector2(240, 1), 0, dim);
            Img(radarRoot, new Vector2(0, -118), new Vector2(12, 12), 45f); // own ship
            radarRangeTxt = Txt(radarRoot, new Vector2(-75, 105), $"{RadarRange / 1852f:0} NM", 14, TextAnchor.MiddleLeft);
            Txt(radarRoot, new Vector2(70, 105), "CRM", 14, TextAnchor.MiddleRight);
        }

        void BuildStores(Transform mfd, Camera cam)
        {
            var c = MakeCanvas("StoresMFD", mfd, new Vector3(0, 0, -0.006f), new Vector2(256, 256), 0.13f / 256f, cam);
            var rt = (RectTransform)c.transform;
            Box(rt, Vector2.zero, new Vector2(240, 240), new Color(0.3f, 1f, 0.4f, 0.4f));
            storesTxt = Txt(rt, new Vector2(-70, 0), "", 15, TextAnchor.MiddleLeft);
            storesTxt.font = MainMenu.KoreanFont();
        }

        void UpdateMfds()
        {
            int i = 0;
            foreach (var e in game.Enemies)
            {
                if (e == null || e.Dead) continue;
                Vector3 l = fm.transform.InverseTransformPoint(e.transform.position);
                Vector2 flat = new Vector2(l.x, l.z);
                if (flat.y < 0 || flat.magnitude > RadarRange) continue;
                if (i >= radarBlips.Count)
                {
                    var b = Rect("Blip", radarRoot, Vector2.zero, new Vector2(10, 10));
                    var img = b.gameObject.AddComponent<Image>(); img.color = new Color(1f, 0.35f, 0.3f); img.raycastTarget = false;
                    radarBlips.Add(b);
                }
                var blip = radarBlips[i++];
                blip.gameObject.SetActive(true);
                blip.anchoredPosition = new Vector2(flat.x / RadarRange * 120f * 2f, flat.y / RadarRange * 236f - 118f);
            }
            for (; i < radarBlips.Count; i++) radarBlips[i].gameObject.SetActive(false);

            // airfields on the radar page (green runway symbols)
            var aps = WorldBuilder.Airports;
            for (int k = 0; k < aps.Length; k++)
            {
                if (k >= airportBlips.Count)
                {
                    var b = Rect("Airfield", radarRoot, Vector2.zero, new Vector2(3, 14));
                    var img = b.gameObject.AddComponent<Image>(); img.color = new Color(0.4f, 1f, 0.5f); img.raycastTarget = false;
                    airportBlips.Add(b);
                }
                Vector3 l = fm.transform.InverseTransformPoint(aps[k].Center3);
                Vector2 flat = new Vector2(l.x, l.z);
                var ab = airportBlips[k];
                bool vis = flat.y > -RadarRange * 0.05f && flat.magnitude < RadarRange;
                ab.gameObject.SetActive(vis);
                if (!vis) continue;
                ab.anchoredPosition = new Vector2(flat.x / RadarRange * 240f, Mathf.Max(flat.y, 0f) / RadarRange * 236f - 118f);
                Vector3 rdir = fm.transform.InverseTransformDirection(aps[k].Dir);
                ab.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(rdir.x, rdir.z) * Mathf.Rad2Deg);
            }

            storesTxt.text =
                (game.Multiplayer ? $"ROOM {game.Net.RoomName}\n{game.Net.Scoreboard()}\n" : $"WAVE   {game.Wave}\nKILLS  {game.Kills}\nENEMY  {game.AliveEnemies}\n\n") +
                $"M61    {weapons.gunAmmo}\nAIM-9  {weapons.missiles}\nFLARE  {weapons.flares}\nFUEL   {fm.Fuel:0} KG ({fm.FuelFraction * 100f:0}%)\nFLOW   {fm.FuelFlow * 3600f:0} KG/H\n\n" +
                $"{(weapons.mode == WeaponMode.Gun ? ">GUN" : ">SRM")}";
        }
    }
}

namespace AirFight
{
    /// <summary>"MAVERICK  [AIM-9]  GOOSE" lines for the HUD kill log.</summary>
    public static class KillFeed
    {
        public const int Rows = 5;
        public const float Life = 7f;
        public struct Entry { public string text; public Color color; public float time; }
        public static readonly List<Entry> Entries = new List<Entry>();
        internal static void ResetStatics() => Entries.Clear();

        public static readonly Color Mine = new Color(1f, 0.85f, 0.25f, 1f), Friendly = new Color(0.35f, 1f, 0.45f, 1f),
                                     Hostile = new Color(1f, 0.4f, 0.32f, 1f), Neutral = new Color(0.9f, 0.95f, 0.95f, 1f);

        public static void Add(string killer, string victim, byte weapon, Color c)
        {
            string w = Damageable.WeaponName(weapon);
            string t = string.IsNullOrEmpty(killer) ? $"{victim}  CRASHED"
                     : w.Length > 0 ? $"{killer}  [{w}]  {victim}" : $"{killer}  >  {victim}";
            Entries.Add(new Entry { text = t, color = c, time = Time.unscaledTime });
            if (Entries.Count > 12) Entries.RemoveAt(0);
        }
    }
}
