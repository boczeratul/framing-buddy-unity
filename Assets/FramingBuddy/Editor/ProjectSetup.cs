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
        static readonly string[] LandmarkIds = { "cks", "taipei101", "hallgrimskirkja", "rosenborg", "nyhavn" };

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
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = false;
            PlayerSettings.gpuSkinning = true;
            PlayerSettings.enableFrameTimingStats = true;
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
                QualitySettings.vSyncCount = 0;
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

        // ---- 程序化貼圖（OSM 建物立面） ----

        static Texture2D SaveTexture(string path, int size, Func<int, int, Color> px, bool srgb, bool normal = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, !srgb);
            var data = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    data[y * size + x] = px(x, y);
            tex.SetPixels(data);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = srgb;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.anisoLevel = 8;
            imp.mipmapEnabled = true;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static float Hash(int x, int y, int s)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + s * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177;
                return (h ^ (h >> 16)) / 4294967295f;
            }
        }

        /// <summary>
        /// 立面圖集：8×8 個「柱距 3.5 m × 樓高 3.2 m」格，每格一扇窗（窗框、玻璃、窗台），格與格之間有細微變化；
        /// 遮罩圖（金屬度、遮蔽、光滑度）讓玻璃反光、牆面粗糙；發光圖為夜間隨機亮燈的窗。
        /// </summary>
        static void CreateFacades(AppAssets a)
        {
            const int N = 1024, B = 128;
            string dir = Root + "/Textures";
            // 窗在格內的位置（v 由下往上）
            bool Win(float u, float v) => u > 0.14f && u < 0.86f && v > 0.24f && v < 0.84f;
            bool Frame(float u, float v) => Win(u, v) && (u < 0.17f || u > 0.83f || v < 0.27f || v > 0.81f || Mathf.Abs(u - 0.5f) < 0.015f);
            var albedo = SaveTexture(dir + "/FacadeAlbedo.png", N, (x, y) =>
            {
                int bx = x / B, by = y / B;
                float u = (x % B + 0.5f) / B, v = (y % B + 0.5f) / B;
                float n = Hash(x / 4, y / 4, 1) * 0.05f;
                if (Frame(u, v)) return new Color(0.36f, 0.37f, 0.38f);
                if (Win(u, v))
                {
                    float g = 0.1f + 0.05f * v + Hash(bx, by, 2) * 0.04f;
                    return new Color(g * 0.85f, g, g * 1.1f);
                }
                // 窗台
                if (v > 0.2f && v < 0.24f && u > 0.1f && u < 0.9f) return new Color(0.7f, 0.7f, 0.68f);
                float w = 0.82f + n + (Hash(bx, by, 3) - 0.5f) * 0.03f - (v < 0.04f ? 0.08f : 0);
                return new Color(w, w, w * 0.98f);
            }, true);
            var mask = SaveTexture(dir + "/FacadeMask.png", N, (x, y) =>
            {
                float u = (x % B + 0.5f) / B, v = (y % B + 0.5f) / B;
                // R 金屬度、G 遮蔽、B 細節遮罩、A 光滑度
                if (Frame(u, v)) return new Color(0.6f, 0.9f, 0, 0.55f);
                if (Win(u, v)) return new Color(0.15f, v > 0.78f ? 0.6f : 1f, 0, 0.94f);
                return new Color(0, 1, 0, 0.18f + Hash(x / 8, y / 8, 4) * 0.08f);
            }, false);
            var emissive = SaveTexture(dir + "/FacadeEmissive.png", N, (x, y) =>
            {
                int bx = x / B, by = y / B;
                float u = (x % B + 0.5f) / B, v = (y % B + 0.5f) / B;
                if (!Win(u, v) || Frame(u, v)) return Color.black;
                float r = Hash(bx, by, 5);
                if (r < 0.62f) return Color.black;
                float k = 0.45f + (r - 0.62f) * 1.4f;
                // 暖白（住宅）與冷白（辦公）
                return Hash(bx, by, 6) < 0.6f ? new Color(1f, 0.82f, 0.58f) * k : new Color(0.85f, 0.9f, 1f) * k;
            }, true);
            var roofTex = SaveTexture(dir + "/RoofAlbedo.png", 512, (x, y) =>
            {
                float n = Hash(x / 3, y / 3, 7) * 0.08f + Hash(x / 24, y / 24, 8) * 0.06f;
                float g = 0.52f + n;
                return new Color(g, g * 0.99f, g * 0.96f);
            }, true);

            var tints = new[]
            {
                new Color(0.9f, 0.89f, 0.85f),  // 淺色混凝土
                new Color(0.88f, 0.8f, 0.68f),  // 米色
                new Color(0.66f, 0.7f, 0.74f),  // 冷灰
                new Color(0.42f, 0.43f, 0.45f), // 深灰
            };
            a.facades = new List<Material>();
            for (int i = 0; i < tints.Length; i++)
            {
                var m = NewLit("Facade" + i);
                m.SetTexture("_BaseColorMap", albedo);
                m.SetColor("_BaseColor", tints[i]);
                m.SetTexture("_MaskMap", mask);
                m.EnableKeyword("_MASKMAP");
                m.SetFloat("_MetallicRemapMax", 1);
                m.SetFloat("_SmoothnessRemapMax", 1);
                m.SetTexture("_EmissiveColorMap", emissive);
                HDMaterial.SetUseEmissiveIntensity(m, true);
                m.SetColor("_EmissiveColorLDR", Color.white);
                HDMaterial.SetEmissiveIntensity(m, 0, EmissiveIntensityUnit.Nits);
                HDMaterial.ValidateMaterial(m);
                a.facades.Add(SaveMaterial(m, $"{Materials}/Facade{i}.mat"));
            }
            var roof = NewLit("Roof");
            roof.SetTexture("_BaseColorMap", roofTex);
            roof.SetTexture("_NormalMap", flatNormalCache);
            roof.SetFloat("_Smoothness", 0.22f);
            HDMaterial.ValidateMaterial(roof);
            a.roof = SaveMaterial(roof, Materials + "/Roof.mat");
        }

        static Texture2D flatNormalCache => AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/FlatNormal.png");

        static AppAssets CreateAppAssets()
        {
            string path = Root + "/AppAssets.asset";
            var a = AssetDatabase.LoadAssetAtPath<AppAssets>(path);
            if (a == null)
            {
                a = ScriptableObject.CreateInstance<AppAssets>();
                AssetDatabase.CreateAsset(a, path);
            }

            // 範本材質需先啟用執行時會用到的關鍵字（顏色貼圖＋法線圖），建置時才不會被剔除
            var flatNormal = SaveTexture(Root + "/Textures/FlatNormal.png", 4, (x, y) => new Color(0.5f, 0.5f, 1f), false, true);
            var white = SaveTexture(Root + "/Textures/White.png", 4, (x, y) => Color.white, true);
            var terrain = NewLit("Terrain");
            terrain.SetTexture("_BaseColorMap", white);
            terrain.SetTexture("_NormalMap", flatNormal);
            terrain.EnableKeyword("_NORMALMAP");
            terrain.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            terrain.SetFloat("_Smoothness", 0.1f);
            HDMaterial.ValidateMaterial(terrain);
            a.terrain = SaveMaterial(terrain, Materials + "/Terrain.mat");
            CreateFacades(a);

            // 水面：深色、非常光滑，細微波紋的法線圖讓倒影略為破碎（像真實湖面）
            var ripple = SaveTexture(Root + "/Textures/WaterRipples.png", 512, (x, y) =>
            {
                float H(float px, float py)
                {
                    float h = 0;
                    for (int o = 0; o < 4; o++)
                    {
                        float f = 4 << o;
                        h += Mathf.Sin((px * f + Mathf.Sin(py * f * 0.37f + o) * 1.7f) * Mathf.PI * 2 / 512f * 1f) *
                             Mathf.Cos((py * f * 0.8f + Mathf.Cos(px * f * 0.23f + o * 2) * 1.3f) * Mathf.PI * 2 / 512f) / (o + 1);
                    }
                    return h;
                }
                float dx = H(x + 1, y) - H(x - 1, y), dy = H(x, y + 1) - H(x, y - 1);
                var n = new Vector3(-dx * 1.5f, -dy * 1.5f, 1).normalized;
                return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1);
            }, false, true);
            var water = NewLit("Water");
            water.SetColor("_BaseColor", new Color(0.018f, 0.035f, 0.04f));
            water.SetFloat("_Smoothness", 0.97f);
            water.SetTexture("_NormalMap", ripple);
            water.SetFloat("_NormalScale", 0.12f);
            water.EnableKeyword("_NORMALMAP");
            water.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            HDMaterial.ValidateMaterial(water);
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
            glow.AddRange(a.facades);
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

        public static void SetupAndBuildDev()
        {
            Run();
            BuildDev();
        }

        /// <summary>效能分析用：IL2CPP 開發版（可錄製 Profiler）</summary>
        public static void BuildProfile()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/profile/Framing Buddy.app",
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[build] {report.summary.result}");
            EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

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
