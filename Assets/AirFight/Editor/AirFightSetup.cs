using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AirFight.EditorTools
{
    /// <summary>메뉴: Air Fight → 1) Setup Everything  (머티리얼 + 게임 씬 + Android XR 빌드 설정)</summary>
    [InitializeOnLoad]
    public static class AirFightSetup
    {
        const string Root = "Assets/AirFight";
        const string MatDir = Root + "/Resources/AirFight";
        const string ScenePath = Root + "/Scenes/AirFight.unity";

        // Fast Play: skip the ~4-5 s script-domain reload when pressing ▶ (Enter Play Mode Options → no domain reload).
        // The scene is still reloaded, and every static cache is cleared by GameManager.ResetStatics().
        static AirFightSetup() { ConfigurePlayMode(); }

        public static void ConfigurePlayMode()
        {
            var want = EnterPlayModeOptions.DisableDomainReload;
            if (!EditorSettings.enterPlayModeOptionsEnabled || EditorSettings.enterPlayModeOptions != want)
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = want;
                Debug.Log("[AirFight] 빠른 Play: 도메인 리로드 끔 (씬은 매번 새로 로드)");
            }
        }

        [MenuItem("Air Fight/Setup Everything", priority = 0)]
        public static void SetupEverything()
        {
            CreateMaterials();
            ConfigurePlayerSettings();
            CreateScene();
            EditorUtility.DisplayDialog("Air Fight",
                "설정 완료!\n\n남은 수동 단계:\n" +
                "1) Edit > Project Settings > XR Plug-in Management\n" +
                "   - Android 탭: [OpenXR] 체크, [Android XR] 기능 그룹 체크\n" +
                "2) XR Plug-in Management > OpenXR (Android 탭) > OpenXR Feature Groups 목록에서\n" +
                "   'Hand Tracking Subsystem' 체크 (맨손 조종에 필수)\n" +
                "3) XR Plug-in Management > Project Validation 에서 [Fix All]\n" +
                "4) File > Build Profiles > Android 로 전환 후 Galaxy XR 연결하고 Build And Run\n" +
                "   (처음 실행 시 '손 추적' 권한 허용)\n\n" +
                "에디터에서 바로 ▶ Play 하면 키보드/게임패드로도 비행할 수 있습니다.", "OK");
        }

        public static void CreateMaterials()
        {
            EnsureFolder(MatDir);
            // re-import the generated textures so AFTextureImport applies (normal map type, tiling, filtering)
            if (AssetDatabase.IsValidFolder(MatDir + "/Textures"))
                AssetDatabase.ImportAsset(MatDir + "/Textures", ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            MakeMat(MatDir + "/Lit.mat", "Universal Render Pipeline/Lit");
            MakeMat(MatDir + "/Unlit.mat", "Universal Render Pipeline/Unlit");
            // transparent variant (clouds, vapour, flames) — having it as an asset keeps the shader variant in builds
            string tp = MatDir + "/UnlitTransparent.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(tp) == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh != null) { var m = new Material(sh); Fx.ConfigureTransparent(m); AssetDatabase.CreateAsset(m, tp); }
            }
            // lit transparent (canopy glass of the 3D model)
            string lt = MatDir + "/LitTransparent.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(lt) == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh != null) { var m = new Material(sh); Fx.ConfigureTransparent(m); AssetDatabase.CreateAsset(m, lt); }
            }
            AssetDatabase.SaveAssets();
        }

        static void MakeMat(string path, string shaderName)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var sh = Shader.Find(shaderName);
            if (sh == null) { Debug.LogError($"[AirFight] Shader not found: {shaderName} (URP 확인)"); return; }
            var m = new Material(sh);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
            AssetDatabase.CreateAsset(m, path);
        }

        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureFolder(Root + "/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (sky != null) RenderSettings.skybox = sky;
            // Fog must be enabled in a built scene, otherwise URP strips the fog shader variants.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 7000f;
            RenderSettings.fogEndDistance = 42000f;
            RenderSettings.fogColor = new Color(0.68f, 0.77f, 0.88f);
            BuildPreview();
            var go = new GameObject("AirFight Game");
            go.AddComponent<GameManager>();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[AirFight] Scene created: " + ScenePath);
        }

        /// <summary>
        /// Edit-mode title screen: the F-16 on a runway apron + title text, so the Game view shows something before ▶ Play.
        /// Everything lives under "Preview" and GameManager deletes it when the game starts.
        /// </summary>
        static void BuildPreview()
        {
            var root = new GameObject("Preview");
            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.transform.SetParent(root.transform, false);
            cam.transform.SetPositionAndRotation(new Vector3(-11f, 3.2f, 13f), Quaternion.Euler(9f, 140f, 0f));
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.1f;

            var sun = new GameObject("PreviewSun").AddComponent<Light>();
            sun.transform.SetParent(root.transform, false);
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(38f, 120f, 0f);

            // apron
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Apron";
            ground.transform.SetParent(root.transform, false);
            ground.transform.position = new Vector3(0, -2.0f, 0);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            var gm = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/Lit.mat");
            if (gm != null)
            {
                var m = new Material(gm) { name = "PreviewApron" };
                m.SetColor("_BaseColor", new Color(0.32f, 0.33f, 0.34f));
                ground.GetComponent<Renderer>().sharedMaterial = m;
            }
            Object.DestroyImmediate(ground.GetComponent<Collider>());

            // the 3D model (needs glTFast); gear models sit at their tyre centres, so lower them onto the apron
            var f16 = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Resources/AirFight/Models/f16.glb");
            if (f16 != null)
            {
                var jet = (GameObject)PrefabUtility.InstantiatePrefab(f16);
                jet.name = "F-16 (preview)";
                jet.transform.SetParent(root.transform, false);
                foreach (var gname in new[] { "NoseGear", "MainGear_R", "MainGear_L" })
                {
                    var g = Find(jet.transform, gname);
                    if (g == null) continue;
                    Vector3 at = gname == "NoseGear" ? new Vector3(0f, -1.65f, 4.0f) : new Vector3(gname.EndsWith("R") ? 1.2f : -1.2f, -1.65f, -0.7f);
                    g.localPosition = at;
                }
            }

            // title
            var canvasGo = new GameObject("Title", typeof(RectTransform));
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Text(canvasGo.transform, font, "AIR FIGHT", 120, new Vector2(0, 380), new Color(0.95f, 0.98f, 1f));
            Text(canvasGo.transform, font, "F-16 VIPER  ·  GALAXY XR", 40, new Vector2(0, 290), new Color(1f, 0.8f, 0.3f));
            Text(canvasGo.transform, font, "▶ Play 를 누르면 시작 메뉴가 열립니다  (AI 대전 / 자유 비행 / 멀티플레이)", 34, new Vector2(0, -420), Color.white);
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var r = Find(c, name); if (r) return r; }
            return null;
        }

        static void Text(Transform parent, Font font, string s, int size, Vector2 pos, Color c)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(1800, size * 1.6f);
            var t = go.AddComponent<UnityEngine.UI.Text>();
            t.font = font; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = c; t.text = s;
            var sh = go.AddComponent<UnityEngine.UI.Shadow>();
            sh.effectColor = new Color(0, 0, 0, 0.8f); sh.effectDistance = new Vector2(3, -3);
        }

        public static void ConfigurePlayerSettings()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "NC AI";
            PlayerSettings.productName = "Air Fight";
            PlayerSettings.SetApplicationIdentifier(android, "com.ncai.airfight");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.Android.forceInternetPermission = true;
            ConfigurePlayMode();
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            Debug.Log("[AirFight] Android player settings configured (IL2CPP, ARM64, Vulkan, API 32+).");
        }

        static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
