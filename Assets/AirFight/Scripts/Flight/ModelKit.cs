using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>
    /// VARCO 3D로 만든 부위별 F-16 모델(Resources/AirFight/Models/f16.glb, aim9.glb)을 절차적 기체에 조립한다.
    /// GLB 안의 각 노드는 이미 기체 좌표(또는 힌지 로컬 좌표)로 구워져 있어서, 해당 힌지 노드 밑에 그대로 붙이면 된다.
    ///   Fuselage / Canopy / VTail / Nozzle / Wing_R,L         → Model
    ///   LEF_R,L / Flaperon_R,L                               → 앞전플랩·플래퍼론 힌지 (ControlSurfaceFx가 회전)
    ///   Stab_R,L                                             → 스태빌레이터 피벗
    ///   NoseGear / MainGear_R,L                              → LandingGear (내림/올림 애니메이션)
    /// glTFast 패키지가 없거나 모델이 없으면 아무것도 하지 않고 기존 절차적 모델을 그대로 쓴다.
    /// </summary>
    public static class ModelKit
    {
        const string F16Path = "AirFight/Models/f16", Aim9Path = "AirFight/Models/aim9";
        static GameObject f16, aim9;
        static bool loaded;
        internal static void ResetStatics() { loaded = false; f16 = null; aim9 = null; }
        static Material canopyGlass;
        static readonly Color BasePaint = new Color(0.55f, 0.58f, 0.62f);

        // ---- assembly data: generated with f16.glb by the Python assembler (ModelKitData.cs) ----
        static Vector3 LefPos => ModelKitData.LefPos;
        static float LefYaw => ModelKitData.LefYaw;
        static Vector3 FlapPos => ModelKitData.FlapPos;
        static float FlapYaw => ModelKitData.FlapYaw;
        static Vector3 StabPos => ModelKitData.StabPos;
        static Vector3 TipMissile => ModelKitData.TipMissile;       // missile centre on the wingtip rail
        static Vector3 PylonMissile => ModelKitData.PylonMissile;   // missile centre under the outer pylon
        static float WingBottomAtPylon => ModelKitData.WingBottomAtPylon;
        public static Vector3 NozzleExit => ModelKitData.NozzleExit;
        public static Vector3 WingTip => ModelKitData.WingTip;

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            f16 = Resources.Load<GameObject>(F16Path);
            aim9 = Resources.Load<GameObject>(Aim9Path);
            if (f16 == null) Debug.Log("[AirFight] f16.glb 모델 없음 (glTFast 미설치?) → 절차적 기체 사용");
        }

        public static bool Available { get { Load(); return f16 != null; } }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var r = FindDeep(c, name); if (r) return r; }
            return null;
        }

        static void Attach(Transform part, Transform parent)
        {
            if (part == null || parent == null) return;
            part.SetParent(parent, false);
            part.localPosition = Vector3.zero;
            part.localRotation = Quaternion.identity;
            part.localScale = Vector3.one;
        }

        static void HideRenderers(Transform t)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }

        static Material CanopyGlass()
        {
            if (canopyGlass != null) return canopyGlass;
            var baseMat = Resources.Load<Material>("AirFight/LitTransparent");
            canopyGlass = baseMat != null ? new Material(baseMat) : Fx.Pbr(Color.white, 0f, 0.95f);
            if (baseMat == null) Fx.ConfigureTransparent(canopyGlass);
            Mats.SetColor(canopyGlass, new Color(0.35f, 0.3f, 0.15f, 0.35f));    // smoked gold tint (seen from outside only)
            if (canopyGlass.HasProperty("_Smoothness")) canopyGlass.SetFloat("_Smoothness", 0.8f);
            if (canopyGlass.HasProperty("_Metallic")) canopyGlass.SetFloat("_Metallic", 0.1f);
            return canopyGlass;
        }

        /// <summary>Replace the procedural airframe with the scanned-quality model. Returns false if no model is available.</summary>
        public static bool Apply(Transform root, AircraftFactory.Parts p, Color paint, bool isPlayer)
        {
            Load();
            if (f16 == null) return false;
            var model = root.Find("Model");
            var proc = model ? model.Find("Proc") : null;
            if (model == null || proc == null) return false;

            var inst = Object.Instantiate(f16);
            inst.name = "F16";
            Transform T(string n) => FindDeep(inst.transform, n);

            // fixed structure
            foreach (var n in new[] { "Fuselage", "VTail", "Nozzle", "Wing_R", "Wing_L", "Canopy" }) Attach(T(n), model);
            proc.gameObject.SetActive(false);

            // leading-edge flaps & flaperons: move the procedural hinge nodes onto the model's hinge lines
            p.lefR.localPosition = LefPos; p.lefR.localRotation = Quaternion.Euler(0, LefYaw, 0);
            p.lefL.localPosition = Mirror(LefPos); p.lefL.localRotation = Quaternion.Euler(0, -LefYaw, 0);
            p.flapR.localPosition = FlapPos; p.flapR.localRotation = Quaternion.Euler(0, FlapYaw, 0);
            p.flapL.localPosition = Mirror(FlapPos); p.flapL.localRotation = Quaternion.Euler(0, -FlapYaw, 0);
            foreach (var (node, name) in new[] { (p.lefR, "LEF_R"), (p.lefL, "LEF_L"), (p.flapR, "Flaperon_R"), (p.flapL, "Flaperon_L") })
            {
                var hinge = node.GetChild(0);
                HideRenderers(hinge);
                Attach(T(name), hinge);
            }
            // all-moving stabilators
            p.stabR.localPosition = StabPos; p.stabL.localPosition = Mirror(StabPos);
            HideRenderers(p.stabR); HideRenderers(p.stabL);
            Attach(T("Stab_R"), p.stabR); Attach(T("Stab_L"), p.stabL);

            // speed-brake petals beside the nozzle, afterburner flame at the nozzle exit
            p.sbR.localPosition = new Vector3(ModelKitData.NozzleRadius + 0.05f, NozzleExit.y, -4.95f);
            p.sbL.localPosition = new Vector3(-ModelKitData.NozzleRadius - 0.05f, NozzleExit.y, -4.95f);
            if (p.nozzleFlame) p.nozzleFlame.localPosition = NozzleExit + new Vector3(0, 0, -0.65f);

            // missile stations: wingtip rails + under-wing pylons (rail node + (0,-0.12,0.3) = missile centre)
            var railOffset = new Vector3(0f, -0.12f, 0.3f);
            if (p.rails.Count >= 4)
            {
                p.rails[0].localPosition = TipMissile - railOffset;
                p.rails[1].localPosition = Mirror(TipMissile) - railOffset;
                p.rails[2].localPosition = PylonMissile - railOffset;
                p.rails[3].localPosition = Mirror(PylonMissile) - railOffset;
            }
            Color pyl = paint * 0.8f; pyl.a = 1f;
            var pylMat = Fx.Pbr(pyl, 0.2f, 0.4f);
            for (int s = -1; s <= 1; s += 2)
            {
                float top = WingBottomAtPylon + 0.03f, bot = PylonMissile.y + 0.11f;
                var py = Build.Prim(PrimitiveType.Cube, model, new Vector3(s * PylonMissile.x, (top + bot) * 0.5f, PylonMissile.z + 0.1f),
                                    new Vector3(0.09f, top - bot, 1.7f), pyl, false, null, false, "Pylon");
                py.GetComponent<Renderer>().sharedMaterial = pylMat;
            }

            // canopy: tinted glass (the pilot looks through it), no shadows into the cockpit
            var canopy = model.Find("Canopy");
            p.canopy = canopy;
            if (canopy)
                foreach (var r in canopy.GetComponentsInChildren<Renderer>())
                {
                    r.sharedMaterial = CanopyGlass();
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

            // landing gear
            var gear = root.GetComponent<LandingGear>();
            Transform ng = T("NoseGear"), mr = T("MainGear_R"), ml = T("MainGear_L");
            if (gear != null) gear.AttachModels(ng, mr, ml);
            else { foreach (var g in new[] { ng, mr, ml }) if (g) Object.Destroy(g.gameObject); }

            // paint: the scanned textures read as bare metal under URP → force a satin, non-metallic paint finish,
            // and tint enemy / remote jets with their team colour (player keeps the original grey)
            Vector4 tint = new Vector4(paint.r / BasePaint.r, paint.g / BasePaint.g, paint.b / BasePaint.b, 1f);
            if (isPlayer) tint = Vector4.one;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("baseColorFactor", (Color)tint);
            mpb.SetColor("_BaseColor", (Color)tint);
            mpb.SetFloat("metallicFactor", 0.05f);
            mpb.SetFloat("_Metallic", 0.05f);
            mpb.SetFloat("roughnessFactor", 1f);
            foreach (var n in new[] { "Fuselage", "VTail", "Wing_R", "Wing_L", "LEF_R", "LEF_L", "Flaperon_R", "Flaperon_L", "Stab_R", "Stab_L" })
                TintAll(FindDeep(model, n), mpb);

            Object.Destroy(inst);   // only the (now re-parented) parts survive
            return true;
        }

        static void TintAll(Transform t, MaterialPropertyBlock mpb)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(mpb);
        }

        static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        /// <summary>AIM-9 visual (2.9 m, nose +Z, centred). Returns false when the model is unavailable.</summary>
        public static bool SpawnAim9(Transform parent)
        {
            Load();
            if (aim9 == null) return false;
            var go = Object.Instantiate(aim9, parent, false);
            go.name = "AIM9Model";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return true;
        }
    }
}
