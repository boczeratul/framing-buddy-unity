using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FramingBuddy
{
    /// <summary>
    /// 天空與光線（HDRP，全部為物理單位）：
    /// - Physically Based Sky（大氣散射、日落色偏、地影）＋真實星表與銀河（依恆星時旋轉）；
    /// - 太陽、月亮兩盞平行光（照度 lux；月亮依月相反射太陽光，並以 HDRP 天體算繪月相）；
    /// - 體積雲（雲量）、高度霧＋氣溶膠（能見度，Koschmieder：平均自由徑 = 能見度 / 3.912）；
    /// - 自動曝光（Sony 多重測光近似：偏重中央的權重遮罩＋直方圖排除高光）與曝光補償；
    /// - 陰影串接到約 30 km（讓遠山自我遮蔽）、接觸陰影、SSAO、SSR、泛光、鏡頭眩光、ACES 色調對映；
    /// - 景深（實體相機：焦段、光圈、對焦距離）。
    /// </summary>
    public class Celestial : MonoBehaviour
    {
        public CelestialInfo Info { get; } = new();
        /// <summary>依照度模型估計的場景 EV100（HUD 建議快門用）</summary>
        public float EstimatedEv100 { get; private set; } = 12;

        Volume _volume;
        VolumeProfile _profile;
        VisualEnvironment _env;
        PhysicallyBasedSky _sky;
        VolumetricClouds _clouds;
        Fog _fog;
        Exposure _exposure;
        HDShadowSettings _shadows;
        ContactShadows _contact;
        ScreenSpaceAmbientOcclusion _ssao;
        ScreenSpaceReflection _ssr;
        GlobalIllumination _ssgi;
        Bloom _bloom;
        Tonemapping _tone;
        ScreenSpaceLensFlare _flare;
        DepthOfField _dof;
        MicroShadowing _micro;
        Vignette _vignette;

        Light _sun, _moon;
        HDAdditionalLightData _sunHd, _moonHd;
        Texture2D[] _masks;
        Cubemap _stars;
        readonly List<Material> _glow = new();
        float _originEle;
        float _lastFogBase = float.NaN;
        string _timesKey;

        const float SunLux = 120000f;
        const float FullMoonLux = 0.25f;

        public Light Sun => _sun;

        public void Init(AppAssets assets)
        {
            var go = new GameObject("Global Volume");
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10;
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "FramingBuddy";
            _volume.sharedProfile = _profile;

            _env = _profile.Add<VisualEnvironment>(true);
            _env.skyType.Override((int)SkyType.PhysicallyBased);
            _env.cloudType.Override(0);
            _env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            _env.renderingSpace.Override(RenderingSpace.World);
            _env.centerMode.Override(VisualEnvironment.PlanetMode.Manual);

            _sky = _profile.Add<PhysicallyBasedSky>(true);
            _sky.type.Override(PhysicallyBasedSkyModel.EarthAdvanced);
            _sky.atmosphericScattering.Override(true);
            _sky.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
            _sky.multiplier.Override(1f);
            _sky.updateMode.Override(EnvironmentUpdateMode.Realtime);
            _sky.spaceEmissionMultiplier.Override(1f / StarField.StoredScale);
            _sky.groundTint.Override(new Color(0.14f, 0.13f, 0.11f));

            _clouds = _profile.Add<VolumetricClouds>(true);
            _clouds.enable.Override(false);
            _clouds.cloudControl.Override(VolumetricClouds.CloudControl.Simple);
            _clouds.cloudSimpleMode.Override(VolumetricClouds.CloudSimpleMode.Quality);
            _clouds.cloudPreset = VolumetricClouds.CloudPresets.Custom;
            _clouds.shadows.Override(true);
            _clouds.shadowDistance.Override(20000f);
            _clouds.shadowResolution.Override(VolumetricClouds.CloudShadowResolution.High512);
            _clouds.fadeInMode.Override(VolumetricClouds.CloudFadeInMode.Automatic);

            _fog = _profile.Add<Fog>(true);
            _fog.enabled.Override(true);
            _fog.colorMode.Override(FogColorMode.SkyColor);
            _fog.maxFogDistance.Override(200000f);
            _fog.mipFogFar.Override(20000f);
            _fog.albedo.Override(new Color(0.92f, 0.94f, 0.97f));
            _fog.anisotropy.Override(0.65f);
            _fog.depthExtent.Override(400f);
            _fog.denoisingMode.Override(FogDenoisingMode.Reprojection);

            _exposure = _profile.Add<Exposure>(true);
            _exposure.mode.Override(ExposureMode.AutomaticHistogram);
            _exposure.limitMin.Override(-7f);
            _exposure.limitMax.Override(17f);
            // 規劃工具：測光立即反應（像相機的測光表），不做人眼式的漸進適應
            _exposure.adaptationMode.Override(AdaptationMode.Fixed);
            _exposure.targetMidGray.Override(TargetMidGray.Grey125);
            _masks = BuildMeterMasks();

            _shadows = _profile.Add<HDShadowSettings>(true);
            _contact = _profile.Add<ContactShadows>(true);
            _contact.enable.Override(true);
            _contact.length.Override(0.35f);
            _contact.distanceScaleFactor.Override(0.6f);
            _contact.maxDistance.Override(120f);
            _ssao = _profile.Add<ScreenSpaceAmbientOcclusion>(true);
            _ssao.intensity.Override(0.9f);
            _ssao.radius.Override(1.6f);
            _ssr = _profile.Add<ScreenSpaceReflection>(true);
            _ssr.enabled.Override(true);
            _ssgi = _profile.Add<GlobalIllumination>(true);
            _micro = _profile.Add<MicroShadowing>(true);
            _micro.enable.Override(true);

            _bloom = _profile.Add<Bloom>(true);
            _bloom.intensity.Override(0.12f);
            _bloom.scatter.Override(0.65f);
            _bloom.threshold.Override(1.2f);
            _tone = _profile.Add<Tonemapping>(true);
            _tone.mode.Override(TonemappingMode.ACES);
            _flare = _profile.Add<ScreenSpaceLensFlare>(true);
            // 鏡頭眩光保持含蓄：不要大圓環
            _flare.intensity.Override(0.12f);
            _flare.warpedFlareIntensity.Override(0f);
            _flare.secondaryFlareIntensity.Override(0.6f);
            _vignette = _profile.Add<Vignette>(true);
            _vignette.intensity.Override(0.12f);
            _vignette.smoothness.Override(0.6f);

            _dof = _profile.Add<DepthOfField>(true);
            _dof.focusMode.Override(DepthOfFieldMode.UsePhysicalCamera);
            _dof.focusDistanceMode.Override(FocusDistanceMode.Camera);

            // 太陽
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(transform, false);
            _sunHd = sunGo.AddHDLight(LightType.Directional);
            _sun = sunGo.GetComponent<Light>();
            _sun.lightUnit = LightUnit.Lux;
            _sun.intensity = SunLux;
            _sun.color = Color.white;
            _sunHd.angularDiameter = 0.53f;
            _sunHd.interactsWithSky = true;
            _sunHd.EnableShadows(true);
            _sunHd.SetShadowResolutionOverride(true);
            _sunHd.SetShadowResolution(4096);
            _sunHd.flareSize = 2f;
            _sunHd.flareMultiplier = 1f;
            _sunHd.normalBias = 0.75f;
            RenderSettings.sun = _sun;
            // AddHDLight 先以預設的點光源註冊、之後才改成平行光：重新啟用讓 HDRP 登錄為平行光（天空才會被照亮）
            sunGo.SetActive(false);
            sunGo.SetActive(true);

            // 月亮：反射太陽光（HDRP 依太陽方向算繪月相）
            var moonGo = new GameObject("Moon");
            moonGo.transform.SetParent(transform, false);
            _moonHd = moonGo.AddHDLight(LightType.Directional);
            _moon = moonGo.GetComponent<Light>();
            _moon.lightUnit = LightUnit.Lux;
            _moon.color = new Color(0.86f, 0.9f, 1f);
            _moonHd.angularDiameter = 0.52f;
            _moonHd.interactsWithSky = true;
            _moonHd.celestialBodyShadingSource = HDAdditionalLightData.CelestialBodyShadingSource.ReflectSunLight;
            _moonHd.sunLightOverride = _sun;
            _moonHd.earthshine = 1f;
            _moonHd.flareSize = 0.6f;
            _moonHd.flareMultiplier = 0.3f;
            _moonHd.surfaceTint = new Color(0.95f, 0.95f, 0.95f);
            _moonHd.EnableShadows(false);
            _moonHd.SetShadowResolutionOverride(true);
            _moonHd.SetShadowResolution(2048);
            moonGo.SetActive(false);
            moonGo.SetActive(true);

            if (assets != null) _glow.AddRange(assets.nightGlow);
            _ = LoadStars();
        }

        async System.Threading.Tasks.Task LoadStars()
        {
            _stars = await StarField.Build(1024);
            if (_sky != null && !noStars) _sky.spaceEmissionTexture.Override(_stars);
        }

        // ---- 測光遮罩 ----

        /// <summary>
        /// 多重測光遮罩（Sony 多重測光的近似）：中央權重最高、往外遞減，
        /// 畫面上方（多為天空）略降權，避免逆光時整體過暗。
        /// </summary>
        static Texture2D[] BuildMeterMasks()
        {
            const int n = 64;
            var multi = new Texture2D(n, n, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, name = "Meter Multi" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.5f;
                    float r = Mathf.Sqrt(u * u * 1.2f + v * v * 1.6f);
                    float w = r < 0.12f ? 1f : r < 0.28f ? 0.7f : r < 0.45f ? 0.45f : 0.3f;
                    // 貼圖 y＝0 在下緣：上方三分之一降權
                    if (v > 0.18f) w *= 0.75f;
                    byte b = (byte)Mathf.RoundToInt(w * 255);
                    px[y * n + x] = new Color32(b, b, b, 255);
                }
            multi.SetPixels32(px);
            multi.Apply(false, true);
            return new[] { multi };
        }

        // ---- 畫質 ----

        public void ApplyQuality(Quality q)
        {
            int lvl = q switch { Quality.Low => 0, Quality.Medium => 1, _ => 2 };
            _shadows.maxShadowDistance.Override(q switch { Quality.Low => 8000f, Quality.Medium => 20000f, _ => 32000f });
            _shadows.cascadeShadowSplitCount.Override(4);
            // 近處 150 m 內極細，遠處涵蓋整座山
            float max = _shadows.maxShadowDistance.value;
            _shadows.cascadeShadowSplit0.Override(150f / max);
            _shadows.cascadeShadowSplit1.Override(900f / max);
            _shadows.cascadeShadowSplit2.Override(5000f / max);
            _sunHd.SetShadowResolution(q switch { Quality.Low => 2048, Quality.Medium => 4096, _ => 4096 });
            _contact.quality.Override(lvl);
            _ssao.quality.Override(lvl);
            _ssr.quality.Override(lvl);
            // 螢幕空間全域光照約 5 ms：只在極致畫質（M5 Pro／Max）開啟
            _ssgi.enable.Override(q >= Quality.Ultra);
            _ssgi.quality.Override(Mathf.Min(lvl, 1));
            // 體積霧（光束）：高畫質用中等品質，極致才用高品質
            _fog.enableVolumetricFog.Override(q >= Quality.High);
            _fog.quality.Override(q >= Quality.Ultra ? 2 : q >= Quality.High ? 1 : 0);
            _clouds.numPrimarySteps.Override(q switch { Quality.Low => 32, Quality.Medium => 40, Quality.High => 48, _ => 80 });
            _clouds.numLightSteps.Override(q switch { Quality.Low => 4, Quality.Medium => 5, Quality.High => 6, _ => 8 });
            _clouds.cloudSimpleMode.Override(q >= Quality.Ultra ? VolumetricClouds.CloudSimpleMode.Quality : VolumetricClouds.CloudSimpleMode.Performance);
            _clouds.microErosion.Override(q >= Quality.Ultra);
            // 景深的高品質（物理式、大量取樣）在 M 系列 GPU 上要 80 ms 以上：最高用中等品質
            _dof.quality.Override(q >= Quality.Medium ? 1 : 0);
            _bloom.quality.Override(lvl);
        }

        // ---- 狀態 ----

        static readonly HashSet<string> EnvKeys = new() { "date", "minutes", "clouds", "visibility", "tz", "lat0", "lon0", "metering", "ev", "quality" };

        public void Apply(ShotState s, HashSet<string> changed, float originEle)
        {
            _originEle = originEle;
            if (changed.Contains("quality")) ApplyQuality(s.quality);
            SetOriginElevation(originEle);

            var utc = TimeUtil.ZonedToUtc(s.date, s.minutes, s.tz);
            var loc = s.CameraLatLon;
            var sun = SunCalc.SunPosition(utc, loc.lat, loc.lon);
            var moon = SunCalc.MoonPosition(utc, loc.lat, loc.lon);
            var illum = SunCalc.MoonIllumination(utc);
            Info.sunAz = sun.azimuth;
            Info.sunAlt = sun.altitude;
            Info.moonAz = moon.azimuth;
            Info.moonAlt = moon.altitude;
            Info.moonFraction = illum.fraction;
            Info.moonPhase = illum.phase;
            Info.sunDir = Dir(sun.azimuth, sun.altitude);
            Info.moonDir = Dir(moon.azimuth, moon.altitude);
            Info.night = Mathf.Clamp01((float)(-sun.altitude - 0) / 12f);
            string tk = $"{s.date}|{s.tz}|{Math.Round(loc.lat, 2)}|{Math.Round(loc.lon, 2)}";
            if (tk != _timesKey)
            {
                _timesKey = tk;
                // 以當地正午所在的太陽日計算各時刻
                Info.times = SunCalc.GetTimes(TimeUtil.ZonedToUtc(s.date, 12 * 60, s.tz), loc.lat, loc.lon);
            }

            _sun.transform.rotation = Quaternion.LookRotation(-Info.sunDir);
            _moon.transform.rotation = Quaternion.LookRotation(-Info.moonDir);
            // 月光：滿月約 0.25 lux，月相變暗比面積快（衝效應）
            _moon.intensity = FullMoonLux * Mathf.Pow((float)illum.fraction, 1.6f);
            _moon.enabled = moon.altitude > -3;
            // HDRP 只有一盞平行光能投射陰影：白天給太陽，夜晚給月亮
            bool moonShadows = sun.altitude < -4 && moon.altitude > 3 && illum.fraction > 0.25;
            _sunHd.EnableShadows(!moonShadows);
            _moonHd.EnableShadows(moonShadows);

            // 星空隨恆星時旋轉
            double lst = SunCalc.LocalSiderealDeg(utc, loc.lon) * Math.PI / 180;
            double phi = loc.lat * Math.PI / 180;
            var P = new Vector3(0, (float)Math.Sin(phi), (float)Math.Cos(phi));
            var M = new Vector3(0, (float)Math.Cos(phi), (float)-Math.Sin(phi));
            var W = new Vector3(-1, 0, 0);
            var A = M * (float)Math.Cos(lst) + W * (float)Math.Sin(lst);
            _sky.spaceRotation.Override(Quaternion.LookRotation(A, P).eulerAngles);

            // 雲
            float c = s.clouds;
            _clouds.enable.Override(c > 0.02f);
            // 覆蓋率：HDRP 的 shapeFactor 越低雲越多（稀疏 0.95、多雲 0.9、陰天 0.5）
            float shape = c <= 0.5f ? Mathf.Lerp(0.985f, 0.9f, c / 0.5f) : Mathf.Lerp(0.9f, 0.45f, (c - 0.5f) / 0.5f);
            _clouds.shapeFactor.Override(shape);
            _clouds.densityMultiplier.Override(Mathf.Lerp(0.32f, 0.45f, c));
            _clouds.shapeScale.Override(5f);
            _clouds.erosionFactor.Override(Mathf.Lerp(0.85f, 0.6f, c));
            _clouds.erosionScale.Override(107f);
            float bottom = Mathf.Max(1300f, originEle + 900f) - c * 400f;
            _clouds.bottomAltitude.Override(bottom);
            _clouds.altitudeRange.Override(Mathf.Lerp(1200f, 2600f, c));
            _clouds.densityCurve.Override(new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.12f, 1f), new Keyframe(1f, 0.1f)));

            // 能見度：一半由大氣氣溶膠（遠景藍灰、太陽周圍光暈），一半由近地高度霧
            float vis = Mathf.Max(1000f, s.visibility * 1000f);
            float beta = 3.912f / vis;
            const float aerosolH = 1200f;
            _sky.aerosolDensity.Override(Mathf.Clamp01(1f - Mathf.Exp(-0.5f * beta * aerosolH)));
            _sky.aerosolAnisotropy.Override(0.78f);
            _sky.aerosolTint.Override(new Color(0.9f, 0.9f, 0.88f));
            _fog.meanFreePath.Override(vis / (0.5f * 3.912f));
            _fog.enabled.Override(true);

            // 曝光
            ApplyMetering(s);
            _exposure.compensation.Override(s.ev);

            EstimatedEv100 = EstimateEv100(sun.altitude, moon.altitude, illum.fraction, c);
        }

        /// <summary>除錯：強制固定曝光（EV100）</summary>
        public float? ForceFixedExposure;

        /// <summary>除錯：改用漸層天空／關閉月光</summary>
        public bool noStars;

        public void DebugSkySpace(bool camera, bool noAtmo)
        {
            if (camera) _env.renderingSpace.Override(RenderingSpace.Camera);
            if (noAtmo) _sky.atmosphericScattering.Override(false);
        }

        public void DisableComponents(string csv)
        {
            foreach (var name in csv.Split(','))
                foreach (var c in _profile.components)
                    if (c.GetType().Name == name.Trim()) c.active = false;
        }

        public void DebugSky(bool gradient, bool noMoon, bool noVolume, bool noStarsFlag)
        {
            noStars = noStarsFlag;
            if (noStars) _sky.spaceEmissionTexture.Override(null);
            if (noVolume) _volume.gameObject.SetActive(false);
            if (gradient)
            {
                var g = _profile.Add<GradientSky>(true);
                g.top.Override(new Color(0.2f, 0.4f, 1f) * 3000);
                g.middle.Override(new Color(0.6f, 0.7f, 1f) * 3000);
                g.bottom.Override(new Color(0.3f, 0.3f, 0.3f) * 3000);
                g.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
                _env.skyType.Override((int)SkyType.Gradient);
            }
            if (noMoon) _moon.gameObject.SetActive(false);
        }

        public string DebugStack(Camera cam)
        {
            var hd = HDCamera.GetOrCreate(cam);
            var st = hd.volumeStack;
            var e = st.GetComponent<Exposure>();
            var f = st.GetComponent<Fog>();
            var sky = st.GetComponent<VisualEnvironment>();
            return $"profile exposure {_exposure.mode.value}/{_exposure.mode.overrideState} fixed {_exposure.fixedExposure.value}; volume active {_volume.isActiveAndEnabled} prio {_volume.priority}; stack exposure {e.mode.value}/{e.meteringMode.value} limit {e.limitMin.value}..{e.limitMax.value} comp {e.compensation.value} fixed {e.fixedExposure.value}; " +
                   $"planet r {sky.planetRadius.value} c {sky.planetCenter.value} space {sky.renderingSpace.value} mode {sky.centerMode.value} (profile r {_env.planetRadius.value} c {_env.planetCenter.value}); cam {cam.transform.position}; " +
                   $"fog {f.enabled.value} mfp {f.meanFreePath.value:0} base {f.baseHeight.value:0} max {f.maximumHeight.value:0}; sky {sky.skyType.value}; " +
                   $"sun {_sun.intensity} lux {_sun.lightUnit}; ev100 est {EstimatedEv100:0.0}; profile comps {_profile.components.Count}";
        }

        void ApplyMetering(ShotState s)
        {
            if (ForceFixedExposure.HasValue)
            {
                _exposure.mode.Override(ExposureMode.Fixed);
                _exposure.fixedExposure.Override(ForceFixedExposure.Value);
                return;
            }
            switch (s.metering)
            {
                case MeteringMode.Multi:
                    _exposure.mode.Override(ExposureMode.AutomaticHistogram);
                    _exposure.meteringMode.Override(UnityEngine.Rendering.HighDefinition.MeteringMode.MaskWeighted);
                    _exposure.weightTextureMask.Override(_masks[0]);
                    // 排除最暗 45% 與最亮 2%（太陽、鏡面反光）：偏重亮部，夜景燈光不會整片過曝
                    _exposure.histogramPercentages.Override(new Vector2(45f, 98f));
                    break;
                case MeteringMode.CenterWeighted:
                    _exposure.mode.Override(ExposureMode.Automatic);
                    _exposure.meteringMode.Override(UnityEngine.Rendering.HighDefinition.MeteringMode.CenterWeighted);
                    break;
                case MeteringMode.Spot:
                    _exposure.mode.Override(ExposureMode.Automatic);
                    _exposure.meteringMode.Override(UnityEngine.Rendering.HighDefinition.MeteringMode.Spot);
                    break;
                default:
                    _exposure.mode.Override(ExposureMode.Automatic);
                    _exposure.meteringMode.Override(UnityEngine.Rendering.HighDefinition.MeteringMode.Average);
                    break;
            }
        }

        public static string MeteringName(MeteringMode m) => m switch
        {
            MeteringMode.Multi => "多重（偏重中央）",
            MeteringMode.CenterWeighted => "中央重點",
            MeteringMode.Spot => "點測光",
            _ => "平均",
        };

        /// <summary>地球半徑與中心（HDRP 17 以公里為單位）：海平面在 y = −原點海拔，大氣密度隨真實海拔變化</summary>
        public void SetOriginElevation(float originEle)
        {
            _originEle = originEle;
            _env.planetRadius.Override(6378.1f);
            _env.planetCenter.Override(new Vector3(0, -(6378.1f + originEle / 1000f), 0));
        }

        /// <summary>高度霧以相機高度為基準（能見度定義在相機所在高度）</summary>
        public void SetEye(Vector3 eye)
        {
            if (!float.IsNaN(_lastFogBase) && Mathf.Abs(eye.y - _lastFogBase) < 5f) return;
            _lastFogBase = eye.y;
            _fog.baseHeight.Override(eye.y - 50f);
            _fog.maximumHeight.Override(Mathf.Max(eye.y + 400f, -_originEle + 1600f));
        }

        public void SetFocus(Camera cam)
        {
            // 景深由相機的實體參數（焦段、光圈、對焦距離）決定
            _dof.focusMode.Override(DepthOfFieldMode.UsePhysicalCamera);
        }

        /// <summary>夜間燈光：地標材質的發光強度隨天色調整</summary>
        float _glowLevel = -1;

        public void UpdateNightGlow()
        {
            float n = Mathf.SmoothStep(0, 1, Mathf.Clamp01(((float)-Info.sunAlt + 2f) / 8f));
            if (Mathf.Abs(n - _glowLevel) < 0.02f) return;
            _glowLevel = n;
            foreach (var m in _glow)
            {
                if (m == null) continue;
                var baseColor = m.HasProperty("_EmissiveColorLDR") ? m.GetColor("_EmissiveColorLDR") : new Color(1f, 0.82f, 0.55f);
                // 室內亮燈的窗約 20–60 nits；地標泛光照明的牆面約 3–8 nits
                float nits = m.name.StartsWith("Facade") ? 30f : m.name.ToLowerInvariant().Contains("glass") ? 12f : 1.5f;
                HdrpMaterials.SetEmissive(m, baseColor, nits * n);
            }
        }

        static Vector3 Dir(double azDeg, double altDeg)
        {
            double a = azDeg * Math.PI / 180, h = altDeg * Math.PI / 180;
            return new Vector3((float)(Math.Sin(a) * Math.Cos(h)), (float)Math.Sin(h), (float)(Math.Cos(a) * Math.Cos(h)));
        }

        /// <summary>
        /// 場景照度估算 → EV100（入射式，C＝250）：直射日光依大氣質量衰減、天空散射光、月光與夜天光。
        /// </summary>
        static float EstimateEv100(double sunAlt, double moonAlt, double moonFrac, float clouds)
        {
            double E = 0;
            if (sunAlt > -0.8)
            {
                double h = Math.Max(0.1, sunAlt) * Math.PI / 180;
                double airmass = 1 / (Math.Sin(h) + 0.50572 * Math.Pow(Math.Max(0.1, sunAlt) + 6.07995, -1.6364));
                double direct = 128000 * Math.Pow(0.7, Math.Pow(airmass, 0.678)) * Math.Sin(h);
                double diffuse = 15000 * Math.Pow(Math.Sin(h), 0.5);
                E += direct * (1 - 0.75 * clouds) + diffuse * (1 + 0.3 * clouds);
            }
            // 民用曙暮光：地平線下每度約降 0.45 EV
            if (sunAlt < 5 && sunAlt > -18) E += 700 * Math.Pow(10, sunAlt / 2.2);
            if (moonAlt > 0) E += 0.25 * Math.Pow(moonFrac, 1.6) * Math.Sin(moonAlt * Math.PI / 180);
            E += 0.001; // 星光與夜天光
            return (float)Math.Log(E / 2.5, 2);
        }
    }
}
