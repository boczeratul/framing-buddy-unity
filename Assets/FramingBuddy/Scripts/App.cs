using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CesiumForUnity;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 進入點：建立場景（Cesium 地理參考、Google 3D 圖磚、自建模型、地形）、天空與光線、取景相機與介面，
    /// 串起狀態變更、鍵盤滑鼠操作、換原點、目標追蹤與可見度。
    /// </summary>
    public class App : MonoBehaviour, IAppActions
    {
        public AppAssets assets;
        public PanelSettings panelSettings;

        Store _store;
        WorldController _world;
        Celestial _celestial;
        PhotoRig _rig;
        OrbitView _orbit;
        AppUI _ui;
        UIDocument _doc;

        VisibilityReport _report = new();
        (string id, float until)? _pendingAim;
        float _reportAt, _refreshAt, _saveAt;
        bool _reportDirty = true, _stateDirty;
        int _lastWorldVersion = -1;
        LatLon _appliedOrigin;

        const string StatePref = "framing-buddy.last-state";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- 啟動 ----

        void Awake()
        {
            // macOS 的視窗一律經過合成器（不會撕裂）：關掉垂直同步、以 60 fps 為上限，
            // 避免 GPU 時間略超過一個刷新週期時直接掉到 30 fps
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Application.runInBackground = false;

            var initial = new ShotState();
            // 上次的設定，或命令列 -url 分享連結
            string last = PlayerPrefs.GetString(StatePref, "");
            if (!string.IsNullOrEmpty(last)) ShareLink.TryDecode(last, initial);
            var args = Environment.GetCommandLineArgs();
            int ui = Array.IndexOf(args, "-url");
            if (ui >= 0 && ui + 1 < args.Length) ShareLink.TryDecode(args[ui + 1], initial);
            _store = new Store(initial);
            var s = _store.State;
            Geo.SetOrigin(new LatLon(s.lat0, s.lon0));
            _appliedOrigin = Geo.Origin;

            _world = new GameObject("World").AddComponent<WorldController>();
            _world.Init(assets);
            _celestial = new GameObject("Sky & Light").AddComponent<Celestial>();
            _celestial.Init(assets);
            _rig = new GameObject("Photo Rig").AddComponent<PhotoRig>();
            _rig.Init();
            _orbit = new GameObject("Map 3D").AddComponent<OrbitView>();
            _orbit.Init(assets);

            _doc = gameObject.AddComponent<UIDocument>();
            _doc.panelSettings = panelSettings;
            ApplyUiScale();
            var root = _doc.rootVisualElement;
            if (assets != null && assets.styleSheet != null) root.styleSheets.Add(assets.styleSheet);
            root.style.flexGrow = 1;
            _ui = new AppUI(root, _store, this, _orbit);
            _ui.FrameChanged += OnFrameChanged;
            _rig.TextureChanged += rt => _ui.Viewfinder.SetTexture(rt);
            _ui.Viewfinder.SetTexture(_rig.Texture);

            _store.Changed += OnState;
            OnState(_store.State, AllKeys());
            _ = _world.Rebase(Geo.Origin, s.range * 1000);
            if (ui < 0 && string.IsNullOrEmpty(last)) LookupZone(s.lat0, s.lon0);
            ParseTestArgs(args);
        }

        // ---- 測試用命令列參數：-preset <名稱片段> -time HH:MM -shot <png> [-quit] ----

        string _shotPath;
        bool _quitAfterShot, _shotTaken;
        float _shotReadyAt = -1, _shotDelay = 4;

        void ParseTestArgs(string[] args)
        {
            string Arg(string k)
            {
                int i = Array.IndexOf(args, k);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string preset = Arg("-preset");
            if (preset != null)
            {
                var p = SiteData.Presets.FirstOrDefault(x => x.name.Contains(preset));
                if (p != null) ApplyPreset(p);
                else Debug.LogWarning("[test] 找不到快速位置：" + preset);
            }
            string time = Arg("-time");
            if (time != null && TimeSpan.TryParse(time, Inv, out var ts)) _store.Set(st => st.minutes = (int)ts.TotalMinutes);
            string date = Arg("-date");
            if (date != null) _store.Set(st => st.date = date);
            string q = Arg("-quality");
            if (q != null && Enum.TryParse<Quality>(q, true, out var qq)) _store.Set(st => st.quality = qq);
            _celestial.DebugSky(Array.IndexOf(args, "-gradientsky") >= 0, Array.IndexOf(args, "-nomoon") >= 0,
                Array.IndexOf(args, "-novolume") >= 0, Array.IndexOf(args, "-nostars") >= 0);
            _celestial.DebugSkySpace(Array.IndexOf(args, "-skycamera") >= 0, Array.IndexOf(args, "-noatmo") >= 0);
            string dis = Arg("-disablecomp");
            if (dis != null) _celestial.DisableComponents(dis);
            string fixedEv = Arg("-fixedev");
            if (fixedEv != null)
            {
                _celestial.ForceFixedExposure = float.Parse(fixedEv, Inv);
                Debug.Log("[test] 固定曝光 EV " + fixedEv);
                _celestial.Apply(_store.State, AllKeys(), _world.originEle);
            }
            _profilePath = Arg("-profile");
            if (Array.IndexOf(args, "-vsync") >= 0) { QualitySettings.vSyncCount = 1; Application.targetFrameRate = -1; }
            if (Array.IndexOf(args, "-uncapped") >= 0) Application.targetFrameRate = 300;
            WaterBodies.NoProbe = Array.IndexOf(args, "-noprobe") >= 0;
            WaterBodies.Disabled = Array.IndexOf(args, "-nowater") >= 0;
            if (Array.IndexOf(args, "-notrees") >= 0) _store.Set(st => st.trees = false);
            string delay = Arg("-shotdelay");
            if (delay != null) _shotDelay = float.Parse(delay, Inv);
            _shotPath = Arg("-shot");
            _quitAfterShot = Array.IndexOf(args, "-quit") >= 0;
            // 自動測試時視窗可能不在前景：照常更新
            if (_shotPath != null) Application.runInBackground = true;
        }

        float _fpsAvg = 1f / 60f;
        double _cpuMs, _gpuMs;
        readonly FrameTiming[] _timings = new FrameTiming[1];

        string _profilePath;
        int _profileFrames = -1;

        void TickProfile()
        {
            if (_profilePath == null || _shotReadyAt < 0 || Time.time < _shotReadyAt - 2) return;
            if (_profileFrames < 0)
            {
                UnityEngine.Profiling.Profiler.logFile = _profilePath;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.enabled = true;
                _profileFrames = 0;
            }
            else if (++_profileFrames == 90)
            {
                UnityEngine.Profiling.Profiler.enabled = false;
                UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                Debug.Log("[test] profile saved");
            }
        }

        void TickTestShot()
        {
            TickProfile();
            _fpsAvg = Mathf.Lerp(_fpsAvg, Time.unscaledDeltaTime, 0.05f);
            _cpuMs = _rig.CpuMs;
            _gpuMs = _rig.GpuMs;
            if (_shotPath == null || _shotTaken) return;
            // 場景載入完成後再等 4 秒（曝光、TAA、雲穩定），最多等 150 秒
            if (!Busy && _shotReadyAt < 0) _shotReadyAt = Time.time + _shotDelay;
            if ((_shotReadyAt > 0 && Time.time > _shotReadyAt) || Time.time > 150)
            {
                _shotTaken = true;
                ScreenCapture.CaptureScreenshot(_shotPath);
                Debug.Log($"[test] 截圖 {_shotPath}（{Time.time:0.0}s，{1f / Mathf.Max(1e-4f, _fpsAvg):0} fps（CPU {_cpuMs:0.0} ms、GPU {_gpuMs:0.0} ms），渲染解析度 {_rig.RenderScale:0}%）\n{Status()}\n{_celestial.DebugStack(_rig.Cam)}\nApp.Update {_updMs:0.00} ms\nrenderers {FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length}");
                if (_quitAfterShot) Invoke(nameof(QuitNow), 2f);
            }
        }

        void QuitNow() => Application.Quit();

        static HashSet<string> AllKeys() => new(typeof(ShotState).GetFields().Select(f => f.Name));

        void ApplyUiScale()
        {
            if (panelSettings == null) return;
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            // Retina：以 DPI 推算，對齊 macOS 預設縮放（MacBook Air 約 224 dpi → 1.75 倍）
            float dpi = Screen.dpi > 0 ? Screen.dpi : 110;
            panelSettings.scale = Mathf.Clamp(Mathf.Round(dpi / 128f * 4) / 4, 1f, 3f);
            UiScale.PixelsPerPoint = panelSettings.scale;
        }

        async void LookupZone(double lat, double lon)
        {
            string tz = await TimeUtil.LookupZone(lat, lon);
            if (Math.Abs(_store.State.lat0 - lat) < 1e-9 && Math.Abs(_store.State.lon0 - lon) < 1e-9) _store.Set(st => st.tz = tz);
        }

        void OnApplicationQuit() => SaveState();

        void SaveState()
        {
            PlayerPrefs.SetString(StatePref, ShareLink.Encode(_store.State));
            PlayerPrefs.Save();
        }

        // ---- 狀態 → 場景 ----

        void OnState(ShotState s, HashSet<string> changed)
        {
            if (changed.Contains("lat0") || changed.Contains("lon0"))
            {
                var o = new LatLon(s.lat0, s.lon0);
                if (Geo.Distance(o, _appliedOrigin) > 0.01)
                {
                    _appliedOrigin = o;
                    _ = _world.Rebase(o, s.range * 1000);
                    _surf.valid = false;
                    _ui?.Map.Recenter();
                }
            }
            else if (changed.Contains("range"))
            {
                _world.SetRange(s.range * 1000);
            }
            _world.ApplySettings(s, changed);
            _celestial.Apply(s, changed, _world.originEle);
            if (changed.Contains("quality"))
            {
                _rig.SetQuality(s.quality);
                if (_ui != null) OnFrameChanged();
            }
            _reportDirty = true;
            _stateDirty = true;
            _stateDirtyForIdle = true;
        }

        // ---- 站立面 ----

        struct Surf
        {
            public bool valid, solid;
            public double x, z;
            public float y;
            public int version;
        }

        Surf _surf;

        float SurfaceY(ShotState s)
        {
            ref var c = ref _surf;
            if (c.valid && c.x == s.x && c.z == s.z && c.version == _world.version) return c.y;
            double step = Math.Sqrt((s.x - c.x) * (s.x - c.x) + (s.z - c.z) * (s.z - c.z));
            bool walking = c.valid && c.solid && step < 12;
            var r = _world.SurfaceAt(s.x, s.z, walking ? c.y + 2.5f : float.PositiveInfinity);
            c = new Surf { valid = true, solid = r.solid, x = s.x, z = s.z, y = r.y, version = _world.version };
            return c.y;
        }

        Vector3 EyeOf(ShotState s) => new((float)s.x, (s.snap ? SurfaceY(s) : 0) + s.height, -(float)s.z);

        // ---- 每幀 ----

        readonly System.Diagnostics.Stopwatch _sw = new();
        double _updMs;

        void Update()
        {
            _sw.Restart();
            UpdateInner();
            _updMs = _updMs * 0.95 + _sw.Elapsed.TotalMilliseconds * 0.05;
        }

        float _activeUntil;

        /// <summary>閒置時降低影格率：規劃畫面多半靜止，無風扇的 MacBook Air 可保持低溫，操作時立即回到 60 fps</summary>
        void TickIdle()
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            bool input = (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0 || mouse.scroll.ReadValue().sqrMagnitude > 0 ||
                                            mouse.leftButton.isPressed || mouse.rightButton.isPressed)) ||
                         (kb != null && kb.anyKey.isPressed);
            if (input || _stateDirtyForIdle || _world.version != _idleWorldVersion || _world.rebasing || Busy) _activeUntil = Time.unscaledTime + 1.5f;
            _stateDirtyForIdle = false;
            _idleWorldVersion = _world.version;
            int target = Time.unscaledTime < _activeUntil || _shotPath != null ? 60 : 20;
            if (Application.targetFrameRate != target && Application.targetFrameRate <= 60) Application.targetFrameRate = target;
        }

        bool _stateDirtyForIdle;
        int _idleWorldVersion = -1;

        void UpdateInner()
        {
            TickIdle();
            TickKeys();
            TickTestShot();
            var s = _store.State;
            // 走離原點太遠時換原點，避免浮點誤差
            if (Math.Sqrt(s.x * s.x + s.z * s.z) > 3000 && !_world.rebasing) GoTo(s.CameraLatLon);

            var eye = EyeOf(s);
            _world.Tick(eye, s);
            _rig.Apply(s, eye);
            _rig.AutoFocus(s);
            _celestial.SetEye(eye);
            _orbit.Tick(s, _rig.Cam);

            if (_world.version != _lastWorldVersion)
            {
                _lastWorldVersion = _world.version;
                _celestial.SetOriginElevation(_world.originEle);
                _reportDirty = true;
                _ui.Viewfinder.MarkDirty();
                _ui.Map.Refresh();
            }
            if (_pendingAim.HasValue && !_world.rebasing && _world.terrainReady)
            {
                var (id, until) = _pendingAim.Value;
                if (_world.Targets().Any(t => t.id == id))
                {
                    Aim(id);
                    _pendingAim = null;
                }
                else if (Time.time > until) _pendingAim = null;
            }

            float now = Time.unscaledTime;
            if (_reportDirty && now > _reportAt)
            {
                _reportDirty = false;
                _reportAt = now + 0.16f;
                var target = Occlusion.Pick(_world.Targets(), s.target, eye);
                _report = Occlusion.Compute(_rig.Cam, target);
                _ui.UpdateHud();
                _ui.Map.Refresh();
            }
            _ui.Viewfinder.Update();
            if (now > _refreshAt)
            {
                _refreshAt = now + 0.5f;
                _ui.Refresh();
                _celestial.UpdateNightGlow();
                PlaceCesiumCredits();
                // Retina 螢幕切換時更新 UI 倍率
                if (!Mathf.Approximately(UiScale.PixelsPerPoint, panelSettings != null ? panelSettings.scale : 1)) ApplyUiScale();
            }
            if (_stateDirty && now > _saveAt)
            {
                _stateDirty = false;
                _saveAt = now + 5;
                SaveState();
            }
        }

        void OnFrameChanged()
        {
            var r = _ui.FrameRect;
            float k = UiScale.PixelsPerPoint * _rig.PixelFactor;
            _rig.SetSize(Mathf.RoundToInt(r.width * k), Mathf.RoundToInt(r.height * k));
        }

        // ---- 鍵盤 ----

        void TickKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || _ui == null || _ui.TextFocused) return;
            bool cmd = kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed || kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            if (cmd && kb.vKey.wasPressedThisFrame)
            {
                string clip = GUIUtility.systemCopyBuffer;
                var probe = _store.State.Clone();
                if (ShareLink.TryDecode(clip, probe)) _store.Set(st => ShareLink.TryDecode(clip, st));
                return;
            }
            // Cmd+Ctrl+F：切換全螢幕／視窗
            if (cmd && kb.fKey.wasPressedThisFrame && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
            {
                Screen.fullScreenMode = Screen.fullScreenMode == FullScreenMode.Windowed ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
                return;
            }
            if (cmd && kb.cKey.wasPressedThisFrame)
            {
                GUIUtility.systemCopyBuffer = ShareLink.WebBase + "#" + ShareLink.Encode(_store.State);
                return;
            }
            if (cmd) return;
            if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) _store.Set(st => st.focal *= 1.08f);
            if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) _store.Set(st => st.focal /= 1.08f);

            bool any = kb.wKey.isPressed || kb.sKey.isPressed || kb.aKey.isPressed || kb.dKey.isPressed || kb.rKey.isPressed || kb.fKey.isPressed ||
                       kb.leftArrowKey.isPressed || kb.rightArrowKey.isPressed || kb.upArrowKey.isPressed || kb.downArrowKey.isPressed;
            if (!any) return;
            var s = _store.State;
            float dt = Mathf.Min(0.1f, Time.unscaledDeltaTime);
            float fast = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ? 5 : 1;
            float speed = 6 * fast * dt;
            float turn = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait)).v * 0.6f * fast * dt;
            float a = s.azimuth * Mathf.Deg2Rad;
            double fx = Math.Sin(a), fz = -Math.Cos(a), rx = Math.Cos(a), rz = Math.Sin(a);
            double x = s.x, z = s.z;
            float h = s.height, az = s.azimuth, pitch = s.pitch;
            if (kb.wKey.isPressed) { x += fx * speed; z += fz * speed; }
            if (kb.sKey.isPressed) { x -= fx * speed; z -= fz * speed; }
            if (kb.dKey.isPressed) { x += rx * speed; z += rz * speed; }
            if (kb.aKey.isPressed) { x -= rx * speed; z -= rz * speed; }
            if (kb.rKey.isPressed) h += speed * 0.5f;
            if (kb.fKey.isPressed) h = Mathf.Max(0, h - speed * 0.5f);
            if (kb.leftArrowKey.isPressed) az -= turn;
            if (kb.rightArrowKey.isPressed) az += turn;
            if (kb.upArrowKey.isPressed) pitch += turn;
            if (kb.downArrowKey.isPressed) pitch -= turn;
            _store.Set(st =>
            {
                st.x = x;
                st.z = z;
                st.height = h;
                st.azimuth = az;
                st.pitch = pitch;
            });
        }

        // ---- IAppActions ----

        public IReadOnlyList<SiteData.Preset> Presets => SiteData.Presets;

        public void GoTo(LatLon p, Action<ShotState> patch = null)
        {
            if (Geo.Distance(p, Geo.Origin) > 2500)
            {
                _store.Set(st =>
                {
                    st.lat0 = p.lat;
                    st.lon0 = p.lon;
                    st.x = 0;
                    st.z = 0;
                    st.tz = TimeUtil.GuessZone(p.lon);
                    patch?.Invoke(st);
                });
                LookupZone(p.lat, p.lon);
            }
            else
            {
                var l = Geo.ToLocal(p);
                _store.Set(st =>
                {
                    st.x = l.x;
                    st.z = l.y;
                    patch?.Invoke(st);
                });
            }
        }

        public void ApplyPreset(SiteData.Preset p)
        {
            GoTo(p.at, st =>
            {
                st.height = p.height;
                st.snap = p.snap;
                p.apply?.Invoke(st);
            });
            if (p.aim != null) _pendingAim = (p.aim, Time.time + 45);
            _ui.Map.PanTo(p.at);
        }

        public void Aim(string id)
        {
            var s = _store.State;
            var eye = EyeOf(s);
            Vector3 dir;
            string target = s.target;
            if (id == "sun") dir = _celestial.Info.sunDir;
            else if (id == "moon") dir = _celestial.Info.moonDir;
            else
            {
                var t = _world.Targets().FirstOrDefault(x => x.id == id);
                if (t == null) return;
                dir = Occlusion.Point(t, t.aimAt) - eye;
                target = id;
            }
            float az = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
            _store.Set(st =>
            {
                st.azimuth = az;
                st.pitch = pitch;
                st.roll = 0;
                st.target = target;
            });
        }

        public void AimAt(LatLon p)
        {
            var s = _store.State;
            _store.Set(st => st.azimuth = (float)Geo.BearingLL(s.CameraLatLon, p));
        }

        public List<Target> Targets() => _world.Targets();
        public Vector3 Eye => EyeOf(_store.State);
        public float Surface => _store.State.snap ? SurfaceY(_store.State) : 0;
        public float OriginElevation => _world.originEle;
        public CelestialInfo Celestial => _celestial.Info;
        public VisibilityReport Report => _report;
        public Camera PhotoCamera => _rig.Cam;

        public string MeterText
        {
            get
            {
                var s = _store.State;
                float ev = _celestial.EstimatedEv100 - s.ev;
                // 建議快門：ISO 100、目前光圈
                double t = s.aperture * s.aperture / Math.Pow(2, ev);
                string iso = "100";
                if (t > 1.0 / 30 && ev < 6)
                {
                    // 暗處提高 ISO，快門不慢於 1/30（夜間改用長曝）
                    int isoN = (int)Mathf.Clamp(Mathf.Pow(2, Mathf.Ceil(Mathf.Log((float)(t * 30), 2))) * 100, 100, 6400);
                    t /= isoN / 100.0;
                    iso = isoN.ToString(Inv);
                }
                string evs = s.ev != 0 ? $" {(s.ev > 0 ? "+" : "")}{s.ev:0.0} EV" : "";
                return $"測光：{FramingBuddy.Celestial.MeteringName(s.metering)}{evs} ・ 約 f/{s.aperture:0.#} {Lens.ShutterText(t)} ISO {iso}";
            }
        }

        public void Snapshot(bool highRes)
        {
            _rig.Snapshot(_store.State, highRes, path =>
            {
                if (path != null) Application.OpenURL("file://" + System.IO.Path.GetDirectoryName(path));
            });
        }

        public bool Busy
        {
            get
            {
                if (_world.rebasing || !_world.terrainReady) return true;
                if (!_world.GoogleActive && _world.Osm.Loading > 0) return true;
                return _world.GoogleActive && _world.tileset != null && _world.tileset.ComputeLoadProgress() < 99f;
            }
        }

        public string Status()
        {
            var s = _store.State;
            var parts = new List<string>
            {
                "地形：" + (_world.terrainReady ? $"已載入（原點海拔 {_world.originEle:0} m）" : "載入中"),
            };
            if (!Config.HasGoogle) parts.Add("Google 實景 3D：未設定金鑰（使用 DEM 地形與自建模型）");
            else if (!s.photoreal) parts.Add("Google 實景 3D：關閉");
            else if (_world.googleFailed) parts.Add($"Google 實景 3D 無法使用（{_world.googleError}）");
            else if (_world.tileset != null) parts.Add($"Google 實景 3D：{_world.tileset.ComputeLoadProgress():0}%");
            if (!_world.GoogleActive)
            {
                var n = _world.Osm.NearStats;
                var f = _world.Osm.FarStats;
                parts.Add($"OSM 建物：近景 {n.built}/{n.wanted}、遠景高樓 {f.built}/{f.wanted} 區塊" +
                          (_world.Osm.Loading > 0 ? $"（下載中 {_world.Osm.Loading}）" : "") + (_world.Osm.errors > 0 ? $"，失敗 {_world.Osm.errors} 次" : ""));
            }
            var m = _world.MountainNames.ToList();
            if (m.Count > 0) parts.Add("遠山精細模型：" + string.Join("、", m));
            parts.Add($"渲染解析度 {_rig.RenderScale:0}%（STP 上採樣）");
            return string.Join("\n", parts);
        }

        public string Attribution()
        {
            // Google 圖磚的版權標示由 Cesium 的標示介面顯示（PlaceCesiumCredits 移到取景框內）
            var list = new List<string> { "地形 © Mapzen/AWS Terrain Tiles", "山峰 © OpenStreetMap contributors / OpenFreeMap" };
            return string.Join(" ・ ", list);
        }

        /// <summary>Cesium 的版權標示（含 Google 標誌）預設在整個螢幕下緣：改放到取景框下緣，與介面共用同一個面板</summary>
        void PlaceCesiumCredits()
        {
            if (panelSettings == null) return;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc == _doc) continue;
                if (doc.panelSettings != panelSettings)
                {
                    doc.panelSettings = panelSettings;
                    doc.sortingOrder = 10;
                }
                var root = doc.rootVisualElement;
                if (root == null) continue;
                var r = _ui.FrameRect;
                root.style.position = Position.Absolute;
                root.style.left = r.x;
                root.style.top = r.y;
                root.style.width = r.width;
                root.style.height = r.height;
                root.pickingMode = PickingMode.Ignore;
            }
        }

        public void SaveGoogleKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            Config.GoogleKey = key.Trim();
            _world.ReloadGoogle();
            _ = _world.Rebase(Geo.Origin, _store.State.range * 1000);
        }
    }
}
