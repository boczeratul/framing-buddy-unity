using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UIElements;

namespace FramingBuddy.EditorTools
{
    /// <summary>
    /// 一鍵建立專案內容（可在 batchmode 以 -executeMethod FramingBuddy.EditorTools.ProjectSetup.Run 執行）：
    /// HDRP 管線資產與設定、Player 設定（Metal、Apple Silicon、線性色彩）、材質範本、
    /// 地標預製物（GLB 的 glTFast 材質換成調整過的 HDRP/Lit）、介面資產、主場景與建置設定。
    /// </summary>
    public static class ProjectSetup
    {
        const string Root = "Assets/FramingBuddy";
        const string Settings = Root + "/Settings";
        const string Materials = Root + "/Materials";
        const string Prefabs = Root + "/Prefabs";
        const string ScenePath = Root + "/Scenes/Main.unity";
        static readonly string[] LandmarkIds = { "cks", "taipei101", "hallgrimskirkja" };

        [MenuItem("Framing Buddy/Setup Project")]
        public static void Run()
        {
            foreach (var d in new[] { Settings, Materials, Prefabs, Root + "/Scenes", Root + "/UI" })
                Directory.CreateDirectory(d);
            AssetDatabase.Refresh();

            ConfigurePlayer();
            var hdrp = ConfigureHdrp();
            ConfigureLayers();
            var assets = CreateAppAssets();
            var panel = CreatePanelSettings(assets);
            CreateScene(assets, panel);
            AssetDatabase.SaveAssets();
            Debug.Log("[setup] 完成：" + AssetDatabase.GetAssetPath(hdrp));
        }

        // ---- Player ----

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Framing Buddy";
            PlayerSettings.productName = "Framing Buddy";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "tw.framingbuddy.unity");
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
            PlayerSettings.macRetinaSupport = true;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 1000;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = false;
            PlayerSettings.gpuSkinning = true;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Minimal);
            // Apple Silicon 原生
            UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.ARM64;

            // 輸入：Input System 與舊版並用（UI Toolkit 與 Cesium 的點擊都能運作）
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (ps != null)
            {
                var so = new SerializedObject(ps);
                var p = so.FindProperty("activeInputHandler");
                if (p != null)
                {
                    p.intValue = 2;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        // ---- HDRP ----

        static HDRenderPipelineAsset ConfigureHdrp()
        {
            string path = Settings + "/HDRP.asset";
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, path);
            }
            var rp = asset.currentPlatformRenderPipelineSettings;
            rp.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.DeferredOnly;
            rp.colorBufferFormat = RenderPipelineSettings.ColorBufferFormat.R16G16B16A16;
            rp.supportVolumetrics = true;
            rp.supportVolumetricClouds = true;
            rp.supportSSAO = true;
            rp.supportSSR = true;
            rp.supportSSRTransparent = false;
            rp.supportSSGI = true;
            rp.supportMotionVectors = true;
            rp.supportScreenSpaceLensFlare = true;
            rp.supportDataDrivenLensFlare = false;
            rp.supportDecals = true;
            rp.supportWater = false;
            rp.supportRayTracing = false;
            rp.supportTransparentDepthPrepass = true;
            rp.supportSubsurfaceScattering = true;
            rp.supportDistortion = false;
            rp.supportShadowMask = false;
            rp.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.LegacyLightProbes;

            var sh = rp.hdShadowInitParams;
            sh.maxDirectionalShadowMapResolution = 4096;
            sh.directionalShadowsDepthBits = DepthBits.Depth32;
            sh.directionalShadowFilteringQuality = HDShadowFilteringQuality.High;
            sh.punctualShadowFilteringQuality = HDShadowFilteringQuality.Medium;
            sh.supportScreenSpaceShadows = false;
            rp.hdShadowInitParams = sh;

            var dr = rp.dynamicResolutionSettings;
            dr.enabled = true;
            dr.dynResType = DynamicResolutionType.Software;
            dr.upsampleFilter = DynamicResUpscaleFilter.TAAU;
            dr.advancedUpscalerNames = new List<string> { AdvancedUpscalers.STP.ToString() };
            dr.maxPercentage = 100;
            dr.minPercentage = 50;
            dr.forceResolution = false;
            rp.dynamicResolutionSettings = dr;

            asset.currentPlatformRenderPipelineSettings = rp;
            EditorUtility.SetDirty(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
                QualitySettings.vSyncCount = 1;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            }
            QualitySettings.SetQualityLevel(Math.Max(current, QualitySettings.names.Length - 1), false);

            // HDRP 全域設定（內部類別，以反射呼叫 Ensure）
            var gs = Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineGlobalSettings, Unity.RenderPipelines.HighDefinition.Runtime");
            var ensure = gs?.GetMethod("Ensure", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            try
            {
                ensure?.Invoke(null, ensure.GetParameters().Length == 1 ? new object[] { false } : null);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[setup] HDRP Global Settings：" + e.Message);
            }
            AssetDatabase.SaveAssets();
            return asset;
        }

        static void ConfigureLayers()
        {
            var tm = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (tm == null) return;
            var so = new SerializedObject(tm);
            var layers = so.FindProperty("layers");
            if (layers != null && layers.arraySize > 31)
            {
                layers.GetArrayElementAtIndex(31).stringValue = "MapOnly";
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ---- 材質 ----

        static Material SaveMaterial(Material m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = m.shader;
                existing.CopyPropertiesFromMaterial(m);
                existing.shaderKeywords = m.shaderKeywords;
                HDMaterial.ValidateMaterial(existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material NewLit(string name)
        {
            var m = new Material(Shader.Find("HDRP/Lit")) { name = name };
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        [Serializable] class GTex { public int index = -1; }
        [Serializable] class GPbr { public float[] baseColorFactor; public float metallicFactor = 1; public float roughnessFactor = 1; public GTex baseColorTexture; }
        [Serializable] class GMat { public string name; public GPbr pbrMetallicRoughness; public float[] emissiveFactor; public string alphaMode; public bool doubleSided; }
        [Serializable] class GRoot { public List<GMat> materials; }

        static List<GMat> ReadGltfMaterials(string glbPath)
        {
            var b = File.ReadAllBytes(glbPath);
            int len = BitConverter.ToInt32(b, 12);
            var json = Encoding.UTF8.GetString(b, 20, len);
            return JsonUtility.FromJson<GRoot>(json)?.materials ?? new List<GMat>();
        }

        static Color Lin(float[] f, Color fallback) =>
            f != null && f.Length >= 3 ? new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1) : fallback;

        /// <summary>glTFast 材質 → 調整過的 HDRP/Lit（依網頁版材質名稱微調光滑度、清漆等）</summary>
        static Material ConvertMaterial(Material src, GMat g, string path, List<Material> glow)
        {
            var m = NewLit(src.name);
            var pbr = g?.pbrMetallicRoughness;
            var baseLin = Lin(pbr?.baseColorFactor, Color.white);
            m.SetColor("_BaseColor", baseLin.gamma);
            var tex = src.HasProperty("baseColorTexture") ? src.GetTexture("baseColorTexture") : null;
            if (tex != null)
            {
                m.SetTexture("_BaseColorMap", tex);
                if (src.HasProperty("baseColorTexture_ST"))
                {
                    var st = src.GetVector("baseColorTexture_ST");
                    m.SetTextureScale("_BaseColorMap", new Vector2(st.x, st.y));
                    m.SetTextureOffset("_BaseColorMap", new Vector2(st.z, st.w));
                }
            }
            var nrm = src.HasProperty("normalTexture") ? src.GetTexture("normalTexture") : null;
            if (nrm != null)
            {
                m.SetTexture("_NormalMap", nrm);
                m.EnableKeyword("_NORMALMAP");
            }
            float metal = pbr != null ? pbr.metallicFactor : 0;
            float rough = pbr != null ? pbr.roughnessFactor : 0.8f;
            string n = src.name.ToLowerInvariant();
            float coat = 0;
            if (n.Contains("glass")) { rough = Mathf.Min(rough, 0.06f); coat = 0.6f; }
            else if (n == "water") { rough = 0.04f; metal = 0; }
            else if (n.StartsWith("marble")) { rough = Mathf.Min(rough, 0.42f); coat = 0.25f; }
            else if (n.Contains("tile")) { coat = 0.5f; }
            else if (n is "gold" or "finial" or "bronze" or "steel") { rough = Mathf.Min(rough, 0.3f); }
            else if (n is "grass" or "foliage") { rough = Mathf.Max(rough, 0.9f); }
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Smoothness", 1 - rough);
            if (coat > 0)
            {
                m.SetFloat("_CoatMask", coat);
                m.EnableKeyword("_MATERIAL_FEATURE_CLEAR_COAT");
            }
            if (g != null && g.doubleSided || n is "foliage" or "grass")
                m.SetFloat("_DoubleSidedEnable", 1);
            m.enableInstancing = true;
            bool emissive = g?.emissiveFactor != null && g.emissiveFactor.Length >= 3 && g.emissiveFactor.Max() > 0.001f;
            if (emissive)
            {
                var e = Lin(g.emissiveFactor, Color.black);
                HDMaterial.SetUseEmissiveIntensity(m, true);
                m.SetColor("_EmissiveColorLDR", new Color(e.r, e.g, e.b, 1).gamma);
                HDMaterial.SetEmissiveIntensity(m, 0, EmissiveIntensityUnit.Nits);
            }
            HDMaterial.ValidateMaterial(m);
            var saved = SaveMaterial(m, path);
            if (emissive && !glow.Contains(saved)) glow.Add(saved);
            return saved;
        }

        static GameObject ConvertLandmark(string id, List<Material> glow)
        {
            string glb = $"{Root}/Landmarks/{id}.glb";
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(glb);
            if (src == null)
            {
                Debug.LogWarning("[setup] 找不到 " + glb);
                return null;
            }
            var gmats = ReadGltfMaterials(glb);
            Directory.CreateDirectory($"{Materials}/{id}");
            var inst = UnityEngine.Object.Instantiate(src);
            inst.name = id;
            var converted = new Dictionary<Material, Material>();
            var used = new Dictionary<string, int>();
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var sm = mats[i];
                    if (sm == null) continue;
                    if (!converted.TryGetValue(sm, out var dm))
                    {
                        int k = used.TryGetValue(sm.name, out int c) ? c + 1 : 0;
                        used[sm.name] = k;
                        var g = gmats.Where(x => x.name == sm.name).Skip(k).FirstOrDefault() ?? gmats.FirstOrDefault(x => x.name == sm.name);
                        string file = k == 0 ? sm.name : $"{sm.name}_{k}";
                        dm = ConvertMaterial(sm, g, $"{Materials}/{id}/{file}.mat", glow);
                        converted[sm] = dm;
                    }
                    mats[i] = dm;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
                if (r is MeshRenderer mr) mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(inst, $"{Prefabs}/{id}.prefab");
            UnityEngine.Object.DestroyImmediate(inst);
            return prefab;
        }

        static AppAssets CreateAppAssets()
        {
            string path = Root + "/AppAssets.asset";
            var a = AssetDatabase.LoadAssetAtPath<AppAssets>(path);
            if (a == null)
            {
                a = ScriptableObject.CreateInstance<AppAssets>();
                AssetDatabase.CreateAsset(a, path);
            }

            var terrain = NewLit("Terrain");
            terrain.SetFloat("_Smoothness", 0.1f);
            a.terrain = SaveMaterial(terrain, Materials + "/Terrain.mat");

            var water = NewLit("Water");
            water.SetColor("_BaseColor", new Color(0.03f, 0.08f, 0.1f));
            water.SetFloat("_Smoothness", 0.95f);
            a.water = SaveMaterial(water, Materials + "/Water.mat");

            var gizmo = new Material(Shader.Find("HDRP/Unlit")) { name = "Gizmo" };
            HDMaterial.SetSurfaceType(gizmo, true);
            gizmo.SetColor("_UnlitColor", new Color(1f, 0.69f, 0.13f, 0.5f));
            gizmo.SetColor("_EmissiveColor", new Color(1f, 0.69f, 0.13f));
            gizmo.SetFloat("_EmissiveExposureWeight", 0f);
            gizmo.SetFloat("_DoubleSidedEnable", 1);
            HDMaterial.ValidateMaterial(gizmo);
            a.gizmo = SaveMaterial(gizmo, Materials + "/Gizmo.mat");

            var glow = new List<Material>();
            a.landmarks = new List<AppAssets.LandmarkPrefab>();
            foreach (var id in LandmarkIds)
            {
                var p = ConvertLandmark(id, glow);
                if (p != null) a.landmarks.Add(new AppAssets.LandmarkPrefab { id = id, prefab = p });
            }
            a.nightGlow = glow;
            a.styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(Root + "/Resources/FramingBuddy.uss");
            a.theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(Root + "/Resources/FramingBuddyTheme.tss");
            EditorUtility.SetDirty(a);
            return a;
        }

        static PanelSettings CreatePanelSettings(AppAssets a)
        {
            string path = Root + "/UI/PanelSettings.asset";
            var p = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(p, path);
            }
            p.themeStyleSheet = a.theme;
            p.scaleMode = PanelScaleMode.ConstantPixelSize;
            p.scale = 2;
            p.sortingOrder = 0;
            p.clearColor = true;
            p.colorClearValue = Color.black;
            EditorUtility.SetDirty(p);
            return p;
        }

        static void CreateScene(AppAssets a, PanelSettings panel)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("App");
            var app = go.AddComponent<App>();
            app.assets = a;
            app.panelSettings = panel;
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        // ---- 建置 ----

        /// <summary>開發用：Mono 後端，建置較快</summary>
        public static void BuildDev()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            BuildTo("Builds/dev/Framing Buddy.app");
        }

        [MenuItem("Framing Buddy/Build macOS App")]
        public static void Build()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            BuildTo("Builds/Framing Buddy.app");
        }

        static void BuildTo(string location)
        {
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = location,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[build] {report.summary.result} {report.summary.totalSize / 1048576} MB, {report.summary.totalTime}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
