using System.Collections.Generic;
using UnityEngine;

namespace AirFight
{
    /// <summary>프리미티브와 절차적 메시로 F-16 형태의 기체를 만든다 (외부 에셋 없이 동작).</summary>
    public static class AircraftFactory
    {
        public static readonly Vector3 EyePoint = new Vector3(0f, 1.05f, 4.3f);

        public class Parts
        {
            public Transform root;
            public Transform gunMuzzle;
            public List<Transform> rails = new List<Transform>();
            public List<GameObject> railMissiles = new List<GameObject>();
            public Transform nozzleFlame;
            public Transform lefL, lefR, flapL, flapR, stabL, stabR, sbL, sbR;   // hinge nodes of moving surfaces
            public bool detailedModel;
            public Transform canopy;            // 3D canopy (hidden from the pilot's own eyes in the cockpit view)                                           // VARCO 3D model assembled
        }

        public static Parts BuildExterior(Transform root, Color paint, bool isPlayer)
        {
            var p = new Parts { root = root };
            Color dark = paint * 0.7f; dark.a = 1f;
            Color metal = new Color(0.35f, 0.35f, 0.38f);
            var model = Build.Node("Model", root, Vector3.zero);
            var proc = Build.Node("Proc", model, Vector3.zero);   // procedural airframe (hidden when the 3D model is assembled)

            // Nose & radome
            Build.Prim(PrimitiveType.Sphere, proc, new Vector3(0, 0.42f, 7.4f), new Vector3(1.0f, 0.95f, 4.2f), paint);
            Build.Prim(PrimitiveType.Capsule, proc, new Vector3(0, 0.42f, 9.6f), new Vector3(0.12f, 0.6f, 0.12f), metal, false, Quaternion.Euler(90, 0, 0)); // pitot
            // Forward fuselage under the cockpit (kept below the pilot's eye line)
            if (isPlayer)
            {
                // hollow tub: belly below the cockpit floor + side skins up to the canopy sill
                Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, -0.2f, 4.4f), new Vector3(1.25f, 0.5f, 2.6f), paint);
                Build.Prim(PrimitiveType.Cube, proc, new Vector3(0.6f, 0.17f, 4.4f), new Vector3(0.06f, 1.2f, 2.6f), paint);
                Build.Prim(PrimitiveType.Cube, proc, new Vector3(-0.6f, 0.17f, 4.4f), new Vector3(0.06f, 1.2f, 2.6f), paint);
                Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, 0.3f, 5.66f), new Vector3(1.25f, 0.9f, 0.08f), paint);
            }
            else
            {
                Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, 0.3f, 4.4f), new Vector3(1.25f, 0.9f, 2.6f), paint);
            }
            // Tinted canopy bubble (also on the player's jet for the 3rd-person view; from inside the cockpit its
            // faces are back-facing and culled, so it never blocks the pilot's view). No shadows into the cockpit.
            var canopy = Build.Prim(PrimitiveType.Sphere, proc, new Vector3(0, 1.05f, 4.2f), new Vector3(1.1f, 0.95f, 3.4f), new Color(0.15f, 0.18f, 0.2f), false, null, false, "Canopy");
            canopy.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Main fuselage + spine
            Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, 0.3f, -1.2f), new Vector3(1.45f, 1.2f, 8.4f), paint);
            Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, 0.95f, 0.3f), new Vector3(0.75f, 0.35f, 5.8f), paint);
            // Intake (chin)
            Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, -0.45f, 2.3f), new Vector3(1.1f, 0.65f, 3.6f), dark);
            Build.Prim(PrimitiveType.Cube, proc, new Vector3(0, -0.45f, 4.12f), new Vector3(1.0f, 0.55f, 0.05f), Color.black);
            // Engine nozzle
            Build.Prim(PrimitiveType.Cylinder, proc, new Vector3(0, 0.3f, -5.9f), new Vector3(1.05f, 0.5f, 1.05f), metal, false, Quaternion.Euler(90, 0, 0));
            var flame = Build.Prim(PrimitiveType.Sphere, model, new Vector3(0, 0.3f, -6.8f), new Vector3(0.8f, 0.8f, 2.5f), new Color(1f, 0.55f, 0.2f), true);
            p.nozzleFlame = flame.transform;

            // Wings (cropped delta, 40° leading edge sweep)
            AddPlanform(proc, "WingR", new Vector3(0.7f, 0.25f, 0.6f), 4.3f, 1.2f, 4.0f, 3.6f, 0.14f, paint, false);
            AddPlanform(proc, "WingL", new Vector3(-0.7f, 0.25f, 0.6f), 4.3f, 1.2f, 4.0f, 3.6f, 0.14f, paint, true);
            // LERX strakes
            AddPlanform(proc, "LerxR", new Vector3(0.6f, 0.45f, 3.6f), 3.2f, 0.1f, 0.5f, 3.0f, 0.06f, paint, false);
            AddPlanform(proc, "LerxL", new Vector3(-0.6f, 0.45f, 3.6f), 3.2f, 0.1f, 0.5f, 3.0f, 0.06f, paint, true);
            // Horizontal stabilators (all-moving, pivot near the root quarter chord)
            p.stabR = Build.Node("StabR", model, new Vector3(0.7f, 0.2f, -5.0f));
            p.stabL = Build.Node("StabL", model, new Vector3(-0.7f, 0.2f, -5.0f));
            AddPlanform(p.stabR, "HTailR", new Vector3(0f, 0f, 0.6f), 2.4f, 0.8f, 2.0f, 1.9f, 0.08f, paint, false);
            AddPlanform(p.stabL, "HTailL", new Vector3(0f, 0f, 0.6f), 2.4f, 0.8f, 2.0f, 1.9f, 0.08f, paint, true);

            // Leading-edge flaps (hinge along the 42° swept leading edge) and trailing-edge flaperons
            Color hinge = paint * 0.93f; hinge.a = 1f;
            p.lefR = Build.Node("LEF_R", model, new Vector3(2.7f, 0.25f, -1.2f), Quaternion.Euler(0, 42f, 0));
            p.lefL = Build.Node("LEF_L", model, new Vector3(-2.7f, 0.25f, -1.2f), Quaternion.Euler(0, -42f, 0));
            foreach (var lef in new[] { p.lefR, p.lefL })
            {
                var pivot = Build.Node("Hinge", lef, Vector3.zero);
                Build.Prim(PrimitiveType.Cube, pivot, new Vector3(0, 0, 0.18f), new Vector3(4.6f, 0.07f, 0.36f), hinge);
            }
            p.flapR = Build.Node("Flaperon_R", model, new Vector3(2.05f, 0.25f, -3.85f), Quaternion.Euler(0, 7f, 0));
            p.flapL = Build.Node("Flaperon_L", model, new Vector3(-2.05f, 0.25f, -3.85f), Quaternion.Euler(0, -7f, 0));
            foreach (var fl in new[] { p.flapR, p.flapL })
            {
                var pivot = Build.Node("Hinge", fl, Vector3.zero);
                Build.Prim(PrimitiveType.Cube, pivot, new Vector3(0, 0, -0.32f), new Vector3(2.4f, 0.07f, 0.64f), hinge);
            }
            // Speed-brake petals either side of the nozzle (hinged at their front edge)
            p.sbR = Build.Node("SpeedBrakeR", model, new Vector3(0.78f, 0.3f, -5.2f));
            p.sbL = Build.Node("SpeedBrakeL", model, new Vector3(-0.78f, 0.3f, -5.2f));
            foreach (var sb in new[] { p.sbR, p.sbL })
                Build.Prim(PrimitiveType.Cube, sb, new Vector3(0, 0, -0.6f), new Vector3(0.05f, 0.9f, 1.2f), dark);
            // Vertical tail
            var vt = AddPlanform(proc, "VTail", new Vector3(0, 1.0f, -2.9f), 3.3f, 1.2f, 3.3f, 2.7f, 0.1f, paint, false);
            vt.transform.localRotation = Quaternion.Euler(0, 0, 90);
            // Ventral fins
            var vf = AddPlanform(proc, "VentralR", new Vector3(0.45f, -0.3f, -3.2f), 1.2f, 0.6f, 0.6f, 0.4f, 0.05f, dark, false);
            vf.transform.localRotation = Quaternion.Euler(0, 0, -60);

            // Missile rails (wingtips + outer pylons)
            Vector3[] railPos =
            {
                new Vector3(4.75f, 0.25f, -1.6f), new Vector3(-4.75f, 0.25f, -1.6f),
                new Vector3(3.6f, 0.0f, -0.9f), new Vector3(-3.6f, 0.0f, -0.9f),
            };
            foreach (var rp in railPos)
            {
                var rail = Build.Node("Rail", root, rp);
                Build.Prim(PrimitiveType.Cube, proc, rp + new Vector3(0, 0.08f, 0.2f), new Vector3(0.08f, 0.12f, 2.2f), metal);
                var mslGo = new GameObject("RailMissile");
                mslGo.transform.SetParent(rail, false);
                mslGo.transform.localPosition = new Vector3(0, -0.12f, 0.3f);
                Missile.BuildModel(mslGo.transform);
                p.rails.Add(rail);
                p.railMissiles.Add(mslGo);
            }

            // Gun (left wing root, behind canopy like the real M61A1 installation)
            p.gunMuzzle = Build.Node("GunMuzzle", root, new Vector3(-0.75f, 0.85f, 2.6f));

            // ---- PBR materials: satin two-tone grey paint, metal nozzle, glossy gold-tinted canopy, HDR afterburner ----
            var paintMat = Fx.Pbr(paint, 0.25f, 0.45f);
            var darkMat = Fx.Pbr(dark, 0.25f, 0.4f);
            var metalMat = Fx.Pbr(metal, 0.85f, 0.55f);
            var paintShared = Mats.Get(paint); var darkShared = Mats.Get(dark); var metalShared = Mats.Get(metal);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial == paintShared) r.sharedMaterial = paintMat;
                else if (r.sharedMaterial == darkShared) r.sharedMaterial = darkMat;
                else if (r.sharedMaterial == metalShared) r.sharedMaterial = metalMat;
            }
            canopy.GetComponent<Renderer>().sharedMaterial = Fx.Pbr(new Color(0.12f, 0.11f, 0.06f), 0.8f, 0.96f);
            flame.GetComponent<Renderer>().sharedMaterial = Fx.Transparent(new Color(4f, 1.8f, 0.6f, 0.6f), null, true);
            p.detailedModel = ModelKit.Apply(root, p, paint, isPlayer);
            return p;
        }

        /// <summary>Trapezoid lifting surface. span along +X (or -X if mirror), chords along Z, leading edge swept back by 'sweep'.</summary>
        static GameObject AddPlanform(Transform parent, string name, Vector3 rootLE, float rootChord, float tipChord, float span, float sweep, float thick, Color c, bool mirror)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = rootLE;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.Get(c);
            float s = mirror ? -1f : 1f;
            float h = thick * 0.5f;
            // 4 planform corners (top), then bottom
            Vector3 a = new Vector3(0, 0, 0);
            Vector3 b = new Vector3(s * span, 0, -sweep);
            Vector3 cc = new Vector3(s * span, 0, -sweep - tipChord);
            Vector3 d = new Vector3(0, 0, -rootChord);
            Vector3 up = new Vector3(0, h, 0), dn = new Vector3(0, -h, 0);
            var v = new List<Vector3>
            {
                a+up, b+up, cc+up, d+up,     // 0-3 top
                a+dn, b+dn, cc+dn, d+dn,     // 4-7 bottom
            };
            var tris = new List<int>();
            void Quad(int i0, int i1, int i2, int i3, bool flip)
            {
                if (flip ^ mirror) tris.AddRange(new[] { i0, i2, i1, i0, i3, i2 });
                else tris.AddRange(new[] { i0, i1, i2, i0, i2, i3 });
            }
            Quad(0, 1, 2, 3, false);   // top
            Quad(4, 7, 6, 5, false);   // bottom
            Quad(0, 4, 5, 1, false);   // leading edge
            Quad(1, 5, 6, 2, false);   // tip
            Quad(2, 6, 7, 3, false);   // trailing edge
            // Flat-shade by duplicating vertices per triangle
            var fv = new List<Vector3>(); var ft = new List<int>();
            for (int i = 0; i < tris.Count; i++) { fv.Add(v[tris[i]]); ft.Add(i); }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(fv);
            mesh.SetTriangles(ft, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        /// <summary>F-16 스타일 조종석 (사이드스틱 우측 콘솔, 스로틀 좌측 콘솔).</summary>
        public static Transform BuildCockpit(Transform root, out Transform stickPivot, out Transform throttleHandle, out Transform hudAnchor, out Transform leftMfd, out Transform rightMfd)
        {
            var E = EyePoint;
            var ck = Build.Node("Cockpit", root, Vector3.zero);
            Color tub = new Color(0.22f, 0.24f, 0.26f), panel = new Color(0.12f, 0.13f, 0.14f), black = new Color(0.04f, 0.04f, 0.05f);

            bool detailed = ModelKit.Available;   // the 3D fuselage has its own seat, tub and canopy sills
            if (!detailed) {
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.38f, -0.48f), new Vector3(0.55f, 0.95f, 0.14f), new Color(0.15f, 0.16f, 0.12f), false, Quaternion.Euler(-12, 0, 0)); // seat back (30° reclined seat on real F-16)
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, 0.18f, -0.56f), new Vector3(0.32f, 0.22f, 0.12f), new Color(0.2f, 0.2f, 0.17f)); // headrest
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.82f, -0.15f), new Vector3(0.5f, 0.12f, 0.6f), new Color(0.15f, 0.16f, 0.12f)); // seat pan
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -1.0f, 0.3f), new Vector3(1.05f, 0.05f, 1.6f), tub); // floor
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0.5f, -0.6f, 0.2f), new Vector3(0.05f, 0.75f, 1.7f), tub); // walls
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(-0.5f, -0.6f, 0.2f), new Vector3(0.05f, 0.75f, 1.7f), tub);
            }
            else
            {
                // clean cockpit tub up to the canopy sill (y ≈ 0.6): hides the coarse scanned interior around the pilot
                float sill = 0.6f, floorY = 0.05f;
                Build.Prim(PrimitiveType.Cube, ck, new Vector3(0, floorY, E.z + 0.3f), new Vector3(1.1f, 0.05f, 2.0f), tub);
                foreach (float sx in new[] { -1f, 1f })
                    Build.Prim(PrimitiveType.Cube, ck, new Vector3(sx * 0.54f, (sill + floorY) * 0.5f, E.z + 0.25f), new Vector3(0.05f, sill - floorY, 2.1f), tub);
                Build.Prim(PrimitiveType.Cube, ck, new Vector3(0, (sill + floorY) * 0.5f, E.z + 1.0f), new Vector3(1.1f, sill - floorY, 0.05f), tub); // forward bulkhead under the panel
            }
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0.37f, -0.52f, 0.15f), new Vector3(0.22f, 0.06f, 0.95f), panel); // right console
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(-0.37f, -0.52f, 0.15f), new Vector3(0.22f, 0.06f, 0.95f), panel); // left console
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.45f, 0.74f), new Vector3(0.9f, 0.4f, 0.06f), panel, false, Quaternion.Euler(-18, 0, 0)); // main panel
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.22f, 0.7f), new Vector3(0.6f, 0.05f, 0.28f), black); // glareshield
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.18f, 0.63f), new Vector3(0.22f, 0.04f, 0.12f), black); // HUD body / UFC
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0.1f, -0.1f, 0.62f), new Vector3(0.012f, 0.13f, 0.012f), black); // combiner posts
            Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(-0.1f, -0.1f, 0.62f), new Vector3(0.012f, 0.13f, 0.012f), black);
            var combiner = Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0, -0.07f, 0.625f), new Vector3(0.19f, 0.13f, 0.003f), new Color(0.25f, 0.4f, 0.3f), false, Quaternion.Euler(-10, 0, 0)); // combiner glass (slight tint)
            combiner.GetComponent<Renderer>().sharedMaterial = Fx.Transparent(new Color(0.5f, 0.9f, 0.6f, 0.06f));
            combiner.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // canopy rails & rear frame
            if (!detailed)
            {
                Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(0.55f, -0.27f, 0.1f), new Vector3(0.04f, 0.04f, 1.9f), black);
                Build.Prim(PrimitiveType.Cube, ck, E + new Vector3(-0.55f, -0.27f, 0.1f), new Vector3(0.04f, 0.04f, 1.9f), black);
            }

            // MFDs
            leftMfd = Build.Node("LeftMFD", ck, E + new Vector3(-0.2f, -0.43f, 0.705f), Quaternion.Euler(-18, 0, 0));
            rightMfd = Build.Node("RightMFD", ck, E + new Vector3(0.2f, -0.43f, 0.705f), Quaternion.Euler(-18, 0, 0));
            Build.Prim(PrimitiveType.Cube, leftMfd, new Vector3(0, 0, 0.015f), new Vector3(0.15f, 0.15f, 0.02f), black);
            Build.Prim(PrimitiveType.Cube, rightMfd, new Vector3(0, 0, 0.015f), new Vector3(0.15f, 0.15f, 0.02f), black);

            // Side-stick (right console). Pivot at the console surface; grip extends upward.
            stickPivot = Build.Node("StickPivot", ck, E + new Vector3(0.37f, -0.49f, 0.3f));
            Build.Prim(PrimitiveType.Cylinder, ck, E + new Vector3(0.37f, -0.485f, 0.3f), new Vector3(0.07f, 0.01f, 0.07f), black); // boot
            Build.Prim(PrimitiveType.Cylinder, stickPivot, new Vector3(0, 0.05f, 0), new Vector3(0.025f, 0.05f, 0.025f), new Color(0.1f, 0.1f, 0.1f));
            Build.Prim(PrimitiveType.Capsule, stickPivot, new Vector3(0, 0.14f, 0.005f), new Vector3(0.045f, 0.06f, 0.05f), new Color(0.12f, 0.12f, 0.12f), false, Quaternion.Euler(-10, 0, 0), false, "Grip");
            Build.Prim(PrimitiveType.Cube, stickPivot, new Vector3(0, 0.12f, 0.03f), new Vector3(0.012f, 0.02f, 0.02f), new Color(0.6f, 0.1f, 0.1f)); // trigger
            Build.Prim(PrimitiveType.Cylinder, stickPivot, new Vector3(0, 0.2f, 0.0f), new Vector3(0.012f, 0.004f, 0.012f), new Color(0.6f, 0.1f, 0.1f)); // pickle

            // Throttle quadrant (left console)
            var rail = Build.Node("ThrottleRail", ck, E + new Vector3(-0.36f, -0.47f, 0.15f));
            Build.Prim(PrimitiveType.Cube, rail, new Vector3(0, 0, 0), new Vector3(0.05f, 0.03f, 0.22f), black);
            Build.Prim(PrimitiveType.Cube, rail, new Vector3(0.03f, 0.005f, 0.026f), new Vector3(0.005f, 0.03f, 0.005f), new Color(0.8f, 0.6f, 0.1f)); // MIL detent mark
            throttleHandle = Build.Node("ThrottleHandle", rail, Vector3.zero);
            Build.Prim(PrimitiveType.Cube, throttleHandle, new Vector3(0, 0.05f, 0), new Vector3(0.015f, 0.08f, 0.02f), new Color(0.1f, 0.1f, 0.1f));
            Build.Prim(PrimitiveType.Cube, throttleHandle, new Vector3(0.0f, 0.1f, 0), new Vector3(0.06f, 0.05f, 0.07f), new Color(0.13f, 0.13f, 0.13f), false, null, false, "ThrottleGrip");

            hudAnchor = Build.Node("HUDAnchor", ck, E);
            return ck;
        }
    }
}
