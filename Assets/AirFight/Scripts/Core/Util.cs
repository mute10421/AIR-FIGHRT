using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>Material cache. Base materials live in Resources/AirFight (created by the setup menu) so URP shaders are included in builds.</summary>
    public static class Mats
    {
        static Material litBase, unlitBase;
        static readonly Dictionary<long, Material> cache = new Dictionary<long, Material>();
        internal static void ResetStatics() { cache.Clear(); }

        static Material Base(bool unlit)
        {
            if (unlit)
            {
                if (unlitBase == null)
                {
                    unlitBase = Resources.Load<Material>("AirFight/Unlit");
                    if (unlitBase == null) unlitBase = new Material(FindShader("Universal Render Pipeline/Unlit"));
                }
                return unlitBase;
            }
            if (litBase == null)
            {
                litBase = Resources.Load<Material>("AirFight/Lit");
                if (litBase == null) litBase = new Material(FindShader("Universal Render Pipeline/Lit"));
            }
            return litBase;
        }

        static Shader FindShader(string name)
        {
            var s = Shader.Find(name);
            if (s == null) s = Shader.Find("Sprites/Default");
            return s;
        }

        public static Material Get(Color c, bool unlit = false)
        {
            Color32 c32 = c;
            long key = ((long)c32.r << 24) | ((long)c32.g << 16) | ((long)c32.b << 8) | c32.a | (unlit ? 1L << 40 : 0);
            if (cache.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(Base(unlit));
            SetColor(m, c);
            cache[key] = m;
            return m;
        }

        public static Material Unique(Color c, bool unlit = false)
        {
            var m = new Material(Base(unlit));
            SetColor(m, c);
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }
    }

    public static class Build
    {
        public static GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 localScale, Color color,
            bool unlit = false, Quaternion? localRot = null, bool keepCollider = false, string name = null)
        {
            var go = GameObject.CreatePrimitive(type);
            if (name != null) go.name = name;
            if (!keepCollider)
            {
                var col = go.GetComponent<Collider>();
                if (col)
                {
                    // DestroyImmediate is not allowed inside physics callbacks (explosions spawned on impact)
                    col.enabled = false;
                    if (Application.isPlaying) Object.Destroy(col); else Object.DestroyImmediate(col);
                }
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot ?? Quaternion.identity;
            go.transform.localScale = localScale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = Mats.Get(color, unlit);
            if (unlit) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public static Transform Node(string name, Transform parent, Vector3 localPos, Quaternion? rot = null)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            t.localRotation = rot ?? Quaternion.identity;
            return t;
        }

        public static LineRenderer Line(Transform parent, Color c, float width, int count = 2, bool world = true)
        {
            var go = new GameObject("Line");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = Mats.Get(c, true);
            lr.startWidth = lr.endWidth = width;
            lr.positionCount = count;
            lr.useWorldSpace = world;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }
    }

    public static class Units
    {
        public const float MsToKnots = 1.943844f;
        public const float MToFeet = 3.28084f;
        public const float G = 9.81f;
    }
}
