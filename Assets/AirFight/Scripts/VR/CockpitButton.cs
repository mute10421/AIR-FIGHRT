using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AirFight
{
    /// <summary>손가락 끝으로 눌러서 작동하는 조종석 버튼 (맨손 포크 입력).</summary>
    public class CockpitButton : MonoBehaviour
    {
        public string label;
        public float radius = 0.022f;
        public Func<bool> isLit;            // lamp state
        public event Action Pressed;

        Transform cap;
        Renderer capR;
        bool down;
        float cooldown;
        Material litMat, offMat;

        static Font font;

        public static CockpitButton Create(Transform parent, Vector3 localPos, Quaternion rot, string label, Color lamp, Camera cam)
        {
            var root = Build.Node("Btn_" + label, parent, localPos, rot);
            Build.Prim(PrimitiveType.Cube, root, new Vector3(0, 0, 0.006f), new Vector3(0.036f, 0.026f, 0.01f), new Color(0.08f, 0.08f, 0.09f));
            var b = root.gameObject.AddComponent<CockpitButton>();
            b.label = label;
            b.cap = Build.Prim(PrimitiveType.Cube, root, new Vector3(0, 0, -0.002f), new Vector3(0.03f, 0.02f, 0.008f), new Color(0.2f, 0.2f, 0.2f)).transform;
            b.capR = b.cap.GetComponent<Renderer>();
            b.litMat = Mats.Get(lamp, true);
            b.offMat = Mats.Get(new Color(0.22f, 0.22f, 0.24f));

            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var cgo = new GameObject("Label", typeof(RectTransform));
            cgo.transform.SetParent(root, false);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            var rt = (RectTransform)cgo.transform;
            rt.sizeDelta = new Vector2(120, 30);
            rt.localPosition = new Vector3(0, 0.022f, -0.003f);
            rt.localScale = Vector3.one * 0.0004f;
            var t = new GameObject("T", typeof(RectTransform)).AddComponent<Text>();
            t.transform.SetParent(rt, false);
            ((RectTransform)t.transform).sizeDelta = new Vector2(120, 30);
            t.font = font; t.fontSize = 22; t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.9f, 0.9f, 0.85f); t.text = label;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return b;
        }

        /// <summary>Activate from code (mouse click on a menu, keyboard shortcut).</summary>
        public void Press() { cooldown = 0.35f; Pressed?.Invoke(); }

        /// <summary>Screen-space rectangle of the cap (for mouse clicks on world-space menus).</summary>
        public Rect ScreenRect(Camera cam)
        {
            float xmin = float.MaxValue, ymin = float.MaxValue, xmax = float.MinValue, ymax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? -0.018f : 0.018f, (i & 2) == 0 ? -0.013f : 0.013f, 0f);
                Vector3 sp = cam.WorldToScreenPoint(transform.TransformPoint(c));
                if (sp.z <= 0f) return Rect.zero;
                xmin = Mathf.Min(xmin, sp.x); xmax = Mathf.Max(xmax, sp.x); ymin = Mathf.Min(ymin, sp.y); ymax = Mathf.Max(ymax, sp.y);
            }
            return Rect.MinMaxRect(xmin, ymin, xmax, ymax);
        }

        /// <summary>Feed fingertip positions (world space) of hands that are free to poke.</summary>
        public void Poke(List<Vector3> fingertips)
        {
            cooldown -= Time.unscaledDeltaTime;   // works while the game is paused (map)
            float best = float.MaxValue;
            foreach (var p in fingertips)
            {
                Vector3 l = transform.InverseTransformPoint(p);
                // press zone: in front of the face (negative z = pilot side), within the cap footprint
                if (Mathf.Abs(l.x) < radius && Mathf.Abs(l.y) < radius * 0.8f && l.z > -0.03f && l.z < 0.012f)
                    best = Mathf.Min(best, l.z);
            }
            bool inside = best < float.MaxValue && best > -0.006f;   // finger pushed past the cap surface
            if (!down && inside && cooldown <= 0f)
            {
                down = true;
                cooldown = 0.35f;
                Pressed?.Invoke();
            }
            else if (down && (best == float.MaxValue || best < -0.015f)) down = false;

            cap.localPosition = new Vector3(0, 0, down ? 0.003f : -0.002f);
            bool lit = isLit != null && isLit();
            capR.sharedMaterial = lit ? litMat : offMat;
        }
    }
}
