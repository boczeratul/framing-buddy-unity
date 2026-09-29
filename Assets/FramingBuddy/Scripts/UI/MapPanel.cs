using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>UI 點 → 螢幕像素的倍率（Retina 為 2 左右），由 App 每幀更新。</summary>
    public static class UiScale
    {
        public static float PixelsPerPoint = 1;
    }

    /// <summary>
    /// 左側位置規劃圖：
    /// - 地圖／衛星：Google 2D 圖磚（有金鑰時）或 OpenStreetMap；點一下移動、拖曳相機或方向把手、
    ///   Shift＋點一下對準該處、滾輪縮放、拖曳空白處平移；畫出視角扇形、近景／遠景範圍、太陽／月亮／目標方向線。
    /// - 3D：軌道相機繞著拍攝位置環視，顯示相機視錐；雙擊地面移動位置。
    /// </summary>
    public class MapPanel
    {
        public interface IOrbitView
        {
            Texture Texture { get; }
            bool Active { get; set; }
            void Resize(int pxW, int pxH);
            void Orbit(Vector2 deltaPoints);
            void Pan(Vector2 deltaPoints);
            void Zoom(float wheel);
            bool Pick(Vector2 viewport, out LatLon p);
        }

        enum Mode { Road, Satellite, Orbit }

        readonly Store _store;
        readonly IAppActions _app;
        readonly IOrbitView _orbit;

        public VisualElement Element { get; }
        public VisualElement ModeSelector { get; }
        public string Hint { get; private set; } = "";
        public event Action<string> HintChanged;

        readonly VisualElement _tiles, _overlay, _orbitEl;
        readonly Label _attrib, _scaleLabel, _north;
        readonly Label[] _rayLabels = new Label[3];
        readonly Button _recenter;
        readonly Button _bRoad, _bSat, _b3d;
        Mode _mode = Mode.Road;
        float _w = 1, _h = 1;

        LatLon _center;
        double _zoom = 16;
        bool _centerInit;

        const double Deg = Math.PI / 180;

        public MapPanel(Store store, IAppActions app, IOrbitView orbit)
        {
            _store = store;
            _app = app;
            _orbit = orbit;

            Element = new VisualElement();
            Element.AddToClassList("map");
            _tiles = new VisualElement { pickingMode = PickingMode.Ignore };
            _tiles.style.position = Position.Absolute;
            _tiles.style.left = 0;
            _tiles.style.top = 0;
            _tiles.style.right = 0;
            _tiles.style.bottom = 0;
            _tiles.style.overflow = Overflow.Hidden;
            Element.Add(_tiles);

            _orbitEl = new VisualElement();
            _orbitEl.style.position = Position.Absolute;
            _orbitEl.style.left = 0;
            _orbitEl.style.top = 0;
            _orbitEl.style.right = 0;
            _orbitEl.style.bottom = 0;
            _orbitEl.style.display = DisplayStyle.None;
            Element.Add(_orbitEl);

            _overlay = new VisualElement();
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.top = 0;
            _overlay.style.right = 0;
            _overlay.style.bottom = 0;
            _overlay.generateVisualContent += Draw;
            Element.Add(_overlay);

            for (int i = 0; i < _rayLabels.Length; i++)
            {
                _rayLabels[i] = MakeTag();
                _overlay.Add(_rayLabels[i]);
            }
            _scaleLabel = MakeTag();
            _scaleLabel.style.backgroundColor = new Color(0, 0, 0, 0);
            _overlay.Add(_scaleLabel);
            _north = new Label("N") { pickingMode = PickingMode.Ignore };
            _north.style.position = Position.Absolute;
            _north.style.fontSize = 9;
            _north.style.unityFontStyleAndWeight = FontStyle.Bold;
            _north.style.color = Color.white;
            _overlay.Add(_north);

            _attrib = new Label { pickingMode = PickingMode.Ignore };
            _attrib.AddToClassList("map-attrib");
            Element.Add(_attrib);

            _recenter = new Button(Recenter) { text = "⌖", tooltip = "回到目前位置" };
            _recenter.AddToClassList("map-btn");
            Element.Add(_recenter);

            ModeSelector = new VisualElement();
            ModeSelector.AddToClassList("seg");
            _bRoad = new Button(() => SetMode(Mode.Road)) { text = "地圖" };
            _bSat = new Button(() => SetMode(Mode.Satellite)) { text = "衛星" };
            _b3d = new Button(() => SetMode(Mode.Orbit)) { text = "3D" };
            ModeSelector.Add(_bRoad);
            ModeSelector.Add(_bSat);
            ModeSelector.Add(_b3d);

            Element.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                _w = Mathf.Max(1, Element.contentRect.width);
                _h = Mathf.Max(1, Element.contentRect.height);
                ResizeOrbit();
                Refresh();
            });
            BindPointer();
            _ = InitSources();
            SetMode(Mode.Road);
            Element.schedule.Execute(PumpTiles).Every(100);
        }

        static Label MakeTag()
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.position = Position.Absolute;
            l.style.fontSize = 11;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.paddingLeft = 5;
            l.style.paddingRight = 5;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.borderTopLeftRadius = l.style.borderTopRightRadius = l.style.borderBottomLeftRadius = l.style.borderBottomRightRadius = 4;
            l.style.backgroundColor = new Color(0, 0, 0, 0.5f);
            l.style.display = DisplayStyle.None;
            return l;
        }

        void SetMode(Mode m)
        {
            if (m == Mode.Satellite && !_google) m = Mode.Road;
            _mode = m;
            _bRoad.EnableInClassList("on", m == Mode.Road);
            _bSat.EnableInClassList("on", m == Mode.Satellite);
            _b3d.EnableInClassList("on", m == Mode.Orbit);
            _bSat.style.display = _google ? DisplayStyle.Flex : DisplayStyle.None;
            bool orbit = m == Mode.Orbit;
            _orbitEl.style.display = orbit ? DisplayStyle.Flex : DisplayStyle.None;
            _tiles.style.display = orbit ? DisplayStyle.None : DisplayStyle.Flex;
            if (_orbit != null) _orbit.Active = orbit;
            ResizeOrbit();
            Hint = orbit
                ? "拖曳旋轉 · 右鍵拖曳平移 · 滾輪縮放 · 雙擊地面移動位置"
                : "點一下移動 · 拖曳相機或橘點 · Shift+點一下對準該處 · 滾輪縮放 · 拖曳空白處平移";
            HintChanged?.Invoke(Hint);
            ClearTiles();
            Refresh();
        }

        void ResizeOrbit()
        {
            if (_orbit == null || _mode != Mode.Orbit) return;
            float k = UiScale.PixelsPerPoint;
            _orbit.Resize(Mathf.Max(16, Mathf.RoundToInt(_w * k)), Mathf.Max(16, Mathf.RoundToInt(_h * k)));
            if (_orbit.Texture != null) _orbitEl.style.backgroundImage = Background.FromRenderTexture(_orbit.Texture as RenderTexture);
        }

        public void Recenter()
        {
            _center = _store.State.CameraLatLon;
            Refresh();
        }

        /// <summary>移到某地點（搜尋結果）；zoom 給定時一併縮放</summary>
        public void PanTo(LatLon p, double? zoom = null)
        {
            _center = p;
            if (zoom.HasValue) _zoom = Math.Clamp(zoom.Value, 3, MaxZoom);
            else if (_zoom < 15) _zoom = 16;
            Refresh();
        }

        public void Apply(ShotState s)
        {
            var eye = s.CameraLatLon;
            if (!_centerInit)
            {
                _center = eye;
                _centerInit = true;
            }
            // 相機移出畫面時跟著平移
            var p = ToScreen(eye);
            if (_drag == null && (p.x < 16 || p.y < 16 || p.x > _w - 16 || p.y > _h - 16)) _center = eye;
            Refresh();
        }

        // ---- 投影（Web Mercator，北朝上） ----

        static (double x, double y) World(LatLon p)
        {
            double s = Math.Clamp(Math.Sin(p.lat * Deg), -0.9999, 0.9999);
            return ((p.lon + 180) / 360, 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI));
        }

        static LatLon Unworld(double x, double y)
        {
            double n = Math.PI * (1 - 2 * y);
            return new LatLon(Math.Atan(Math.Sinh(n)) / Deg, x * 360 - 180);
        }

        double WorldPx => 256 * Math.Pow(2, _zoom);

        Vector2 ToScreen(LatLon p)
        {
            var a = World(p);
            var c = World(_center);
            double dx = a.x - c.x;
            if (dx > 0.5) dx -= 1;
            if (dx < -0.5) dx += 1;
            return new Vector2((float)(dx * WorldPx + _w / 2), (float)((a.y - c.y) * WorldPx + _h / 2));
        }

        LatLon ToLatLon(Vector2 sp)
        {
            var c = World(_center);
            return Unworld(c.x + (sp.x - _w / 2) / WorldPx, c.y + (sp.y - _h / 2) / WorldPx);
        }

        float MetersPerPoint(double lat) => (float)(156543.03392 * Math.Cos(lat * Deg) / Math.Pow(2, _zoom));

        Vector2 HandlePos(ShotState s)
        {
            var c = ToScreen(s.CameraLatLon);
            float a = s.azimuth * Mathf.Deg2Rad;
            return c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * 46;
        }

        // ---- 圖磚來源 ----

        class Source
        {
            public string id;
            public int maxZoom;
            /// <summary>圖磚影像為 2 倍解析（Google scaleFactor2x）</summary>
            public bool hiDpi;
            public Func<int, int, int, string> url;
            public bool diskCache;
            public string attribution;
        }

        bool _google;
        Source _osm, _gRoad, _gSat;
        string _gAttribution = "";

        Source Current => _mode == Mode.Satellite ? _gSat : _google ? (_gRoad ?? _osm) : _osm;
        double MaxZoom => Current?.maxZoom ?? 19;

        async Task InitSources()
        {
            _osm = new Source
            {
                id = "osm", maxZoom = 19, hiDpi = false, diskCache = true,
                url = (z, x, y) => $"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                attribution = "© OpenStreetMap contributors",
            };
            if (!Config.HasGoogle) return;
            string key = Config.GoogleKey;
            async Task<Source> Session(string id, string body)
            {
                var json = await Net.PostJson("https://tile.googleapis.com/v1/createSession?key=" + key, body);
                if (json == null) return null;
                var r = JsonUtility.FromJson<SessionResponse>(json);
                if (string.IsNullOrEmpty(r?.session)) return null;
                string session = r.session;
                return new Source
                {
                    id = id, maxZoom = 21, hiDpi = r.tileWidth >= 512, diskCache = false,
                    url = (z, x, y) => $"https://tile.googleapis.com/v1/2dtiles/{z}/{x}/{y}?session={session}&key={key}",
                    attribution = "Google",
                };
            }
            const string common = "\"language\":\"zh-TW\",\"region\":\"TW\",\"scale\":\"scaleFactor2x\",\"highDpi\":true";
            var road = Session("g-road", "{\"mapType\":\"roadmap\"," + common + "}");
            var sat = Session("g-sat", "{\"mapType\":\"satellite\",\"layerTypes\":[\"layerRoadmap\"]," + common + "}");
            await Task.WhenAll(road, sat);
            _gRoad = road.Result;
            _gSat = sat.Result;
            _google = _gRoad != null;
            if (!_google) Debug.LogWarning("[map] Google 2D 圖磚無法使用（Map Tiles API 未啟用或金鑰無效），改用 OpenStreetMap");
            SetMode(_mode);
        }

        [Serializable]
        class SessionResponse
        {
            public string session;
            public int tileWidth;
        }

        [Serializable]
        class ViewportResponse
        {
            public string copyright;
        }

        float _attribAt;
        string _attribKey = "";

        async void UpdateGoogleAttribution()
        {
            var src = Current;
            if (src == null || !src.id.StartsWith("g-") || Time.realtimeSinceStartup < _attribAt) return;
            var nw = ToLatLon(Vector2.zero);
            var se = ToLatLon(new Vector2(_w, _h));
            string k = $"{src.id}/{Math.Round(_zoom)}/{Math.Round(nw.lat, 2)}/{Math.Round(nw.lon, 2)}";
            if (k == _attribKey) return;
            _attribKey = k;
            _attribAt = Time.realtimeSinceStartup + 1.5f;
            string session = src.url(0, 0, 0);
            int i = session.IndexOf("session=", StringComparison.Ordinal);
            session = session.Substring(i + 8, session.IndexOf('&', i) - i - 8);
            var inv = CultureInfo.InvariantCulture;
            string url = "https://tile.googleapis.com/tile/v1/viewport?session=" + session + "&key=" + Config.GoogleKey +
                         "&zoom=" + Math.Round(_zoom).ToString(inv) + "&north=" + nw.lat.ToString(inv) + "&south=" + se.lat.ToString(inv) +
                         "&west=" + nw.lon.ToString(inv) + "&east=" + se.lon.ToString(inv);
            var json = await Net.GetText(url);
            if (json == null) return;
            var r = JsonUtility.FromJson<ViewportResponse>(json);
            if (!string.IsNullOrEmpty(r?.copyright)) _gAttribution = r.copyright;
            Refresh();
        }

        // ---- 圖磚 ----

        class Tile
        {
            public string key, src;
            public int z, x, y;
            public VisualElement el;
            public Texture2D tex;
            public bool loading, loaded;
            public float retryAt;
            public int used;
        }

        readonly Dictionary<string, Tile> _cache = new();
        readonly List<Tile> _queue = new();
        int _inflight;
        int _stamp;
        readonly HashSet<Tile> _visible = new();

        void ClearTiles()
        {
            foreach (var t in _cache.Values)
            {
                t.el.RemoveFromHierarchy();
                if (t.tex) UnityEngine.Object.Destroy(t.tex);
            }
            _cache.Clear();
            _queue.Clear();
            _visible.Clear();
        }

        void LayoutTiles()
        {
            var src = Current;
            if (src == null || _mode == Mode.Orbit) return;
            _stamp++;
            int tz = (int)Math.Round(_zoom) + (src.hiDpi || UiScale.PixelsPerPoint < 1.5f ? 0 : 1);
            tz = Math.Clamp(tz, 1, src.maxZoom);
            int n = 1 << tz;
            double world = WorldPx;
            double tpx = world / n;
            var c = World(_center);
            double left = c.x * world - _w / 2, top = c.y * world - _h / 2;
            int x0 = (int)Math.Floor(left / tpx), x1 = (int)Math.Floor((left + _w) / tpx);
            int y0 = Math.Max(0, (int)Math.Floor(top / tpx)), y1 = Math.Min(n - 1, (int)Math.Floor((top + _h) / tpx));
            var now = new HashSet<Tile>();
            bool allLoaded = true;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    int wx = ((tx % n) + n) % n;
                    string key = $"{src.id}/{tz}/{wx}/{ty}";
                    if (!_cache.TryGetValue(key, out var t))
                    {
                        t = new Tile { key = key, src = src.id, z = tz, x = wx, y = ty, el = new VisualElement { pickingMode = PickingMode.Ignore } };
                        t.el.style.position = Position.Absolute;
                        _cache[key] = t;
                        _queue.Add(t);
                    }
                    t.used = _stamp;
                    Place(t, tx * tpx - left, ty * tpx - top, tpx);
                    if (t.el.parent != _tiles) _tiles.Add(t.el);
                    else t.el.BringToFront();
                    now.Add(t);
                    if (!t.loaded) allLoaded = false;
                }
            // 目前層級還沒載完時保留上一層的圖磚墊底
            foreach (var t in _visible)
            {
                if (now.Contains(t)) continue;
                if (!allLoaded && t.loaded && t.src == src.id)
                {
                    double tp = world / (1 << t.z);
                    double wx = t.x * tp - left;
                    // 經度換日線附近取最近的一份
                    if (wx < -world / 2) wx += world;
                    if (wx > world / 2) wx -= world;
                    Place(t, wx, t.y * tp - top, tp);
                    t.el.SendToBack();
                    now.Add(t);
                }
                else t.el.RemoveFromHierarchy();
            }
            _visible.Clear();
            foreach (var t in now) _visible.Add(t);
            _queue.RemoveAll(t => t.used != _stamp || t.loaded);
            // 快取上限
            if (_cache.Count > 500)
            {
                foreach (var t in _cache.Values.Where(t => !_visible.Contains(t) && !t.loading).OrderBy(t => t.used).Take(_cache.Count - 400).ToList())
                {
                    t.el.RemoveFromHierarchy();
                    if (t.tex) UnityEngine.Object.Destroy(t.tex);
                    _cache.Remove(t.key);
                }
            }
        }

        static void Place(Tile t, double x, double y, double size)
        {
            t.el.style.left = (float)x;
            t.el.style.top = (float)y;
            t.el.style.width = (float)size + 0.6f;
            t.el.style.height = (float)size + 0.6f;
        }

        void PumpTiles()
        {
            var src = Current;
            if (src == null) return;
            float now = Time.realtimeSinceStartup;
            // 由畫面中心往外載入
            var ordered = _queue.Where(t => !t.loading && !t.loaded && now >= t.retryAt && t.src == src.id)
                .OrderBy(t => Mathf.Abs(t.el.resolvedStyle.left + t.el.resolvedStyle.width / 2 - _w / 2) + Mathf.Abs(t.el.resolvedStyle.top + t.el.resolvedStyle.height / 2 - _h / 2))
                .ToList();
            foreach (var t in ordered)
            {
                if (_inflight >= 8) break;
                _ = Load(t, src);
            }
            UpdateGoogleAttribution();
        }

        async Task Load(Tile t, Source src)
        {
            t.loading = true;
            _inflight++;
            byte[] bytes;
            try
            {
                string url = src.url(t.z, t.x, t.y);
                bytes = src.diskCache
                    ? await Net.GetBytesCached(url, $"map-{src.id}-{t.z}-{t.x}-{t.y}.png", TimeSpan.FromDays(14))
                    : await Net.GetBytes(url);
            }
            finally
            {
                _inflight--;
                t.loading = false;
            }
            if (bytes == null)
            {
                t.retryAt = Time.realtimeSinceStartup + 8;
                return;
            }
            if (!_cache.ContainsKey(t.key)) return;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!tex.LoadImage(bytes, true))
            {
                UnityEngine.Object.Destroy(tex);
                t.retryAt = Time.realtimeSinceStartup + 30;
                return;
            }
            t.tex = tex;
            t.loaded = true;
            t.el.style.backgroundImage = new StyleBackground(tex);
            _queue.Remove(t);
            LayoutTiles();
        }

        // ---- 疊加層 ----

        Vector2 _eyeSp, _handleSp;
        float _nearR, _farR, _R;
        readonly List<(float az, Color color, bool dashed)> _rays = new();
        float _scalePx;
        bool _hasWedge;
        float _az0, _az1;

        public void Refresh()
        {
            if (_mode != Mode.Orbit) LayoutTiles();
            var s = _store.State;
            var eye = s.CameraLatLon;
            _eyeSp = ToScreen(eye);
            _handleSp = HandlePos(s);
            float mpp = MetersPerPoint(eye.lat);
            _nearR = s.near / mpp;
            _farR = s.range * 1000 / mpp;
            _R = Mathf.Sqrt(_w * _w + _h * _h) * 1.5f;
            _rays.Clear();
            int li = 0;
            void Ray(double az, Color color, bool dashed, string label)
            {
                _rays.Add(((float)az, color, dashed));
                var l = _rayLabels[li++];
                float a = (float)az * Mathf.Deg2Rad;
                var p = _eyeSp + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * 110;
                l.text = label;
                l.style.color = color;
                l.style.display = _mode == Mode.Orbit ? DisplayStyle.None : DisplayStyle.Flex;
                l.style.left = p.x;
                l.style.top = p.y - 9;
                l.style.translate = new Translate(Length.Percent(-50), 0);
            }
            var info = _app.Celestial;
            if (info != null)
            {
                Ray(info.sunAz, new Color(1f, 0.75f, 0.24f), info.sunAlt <= 0, $"☀ {Math.Round(info.sunAz)}°");
                if (info.moonAlt > -2) Ray(info.moonAz, new Color(0.79f, 0.84f, 1f), true, $"☾ {Math.Round(info.moonAz)}°");
            }
            var tg = _app.Report?.target;
            if (tg != null)
            {
                double b = Geo.BearingLL(eye, tg.at);
                double d = Geo.Distance(eye, tg.at);
                Ray(b, new Color(0.37f, 0.88f, 0.75f), true, $"{tg.label} {(d >= 1000 ? (d / 1000).ToString("F2", CultureInfo.InvariantCulture) + " km" : Math.Round(d) + " m")}");
            }
            for (; li < _rayLabels.Length; li++) _rayLabels[li].style.display = DisplayStyle.None;

            var fov = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait));
            _az0 = s.azimuth - fov.h / 2;
            _az1 = s.azimuth + fov.h / 2;
            _hasWedge = true;

            // 比例尺
            float target = 100 * mpp;
            float[] nice = { 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000 };
            float m = nice.FirstOrDefault(v => v >= target);
            if (m == 0) m = nice[^1];
            _scalePx = m / mpp;
            _scaleLabel.text = m >= 1000 ? $"{m / 1000} km" : $"{m} m";
            _scaleLabel.style.left = 12 + _scalePx + 2;
            _scaleLabel.style.top = _h - 26;
            _scaleLabel.style.display = _mode == Mode.Orbit ? DisplayStyle.None : DisplayStyle.Flex;
            _north.style.left = _w - 25;
            _north.style.top = 26;
            _north.style.display = _mode == Mode.Orbit ? DisplayStyle.None : DisplayStyle.Flex;

            var src = Current;
            _attrib.text = _mode == Mode.Orbit ? "" : src == null ? "" : src.id.StartsWith("g-") ? $"Google　{_gAttribution}" : src.attribution;
            _recenter.style.display = _mode == Mode.Orbit ? DisplayStyle.None : DisplayStyle.Flex;
            _overlay.MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            if (_mode == Mode.Orbit) return;
            var p = ctx.painter2D;
            var c = _eyeSp;

            void Circle(float r, Color col)
            {
                if (r < 2 || r > 20000) return;
                p.strokeColor = col;
                p.lineWidth = 1.5f;
                p.BeginPath();
                p.Arc(c, r, Angle.Degrees(0), Angle.Degrees(360));
                p.ClosePath();
                p.Stroke();
            }
            Circle(_farR, new Color(1, 1, 1, 0.5f));
            Circle(_nearR, new Color(0.37f, 0.88f, 0.75f, 0.9f));

            foreach (var (az, color, dashed) in _rays)
            {
                float a = az * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                p.strokeColor = color;
                p.lineWidth = 1.5f;
                p.BeginPath();
                if (!dashed)
                {
                    p.MoveTo(c);
                    p.LineTo(c + dir * _R);
                }
                else
                {
                    for (float t = 0; t < _R; t += 10)
                    {
                        p.MoveTo(c + dir * t);
                        p.LineTo(c + dir * Mathf.Min(_R, t + 5));
                    }
                }
                p.Stroke();
            }

            if (_hasWedge)
            {
                // 視野扇形
                p.fillColor = new Color(1f, 0.69f, 0.13f, 0.16f);
                p.BeginPath();
                p.MoveTo(c);
                p.Arc(c, _R, Angle.Degrees(_az0 - 90), Angle.Degrees(_az1 - 90));
                p.ClosePath();
                p.Fill();
                p.strokeColor = new Color(1f, 0.69f, 0.13f, 0.9f);
                p.lineWidth = 1;
                p.BeginPath();
                float a0 = _az0 * Mathf.Deg2Rad, a1 = _az1 * Mathf.Deg2Rad;
                p.MoveTo(c + new Vector2(Mathf.Sin(a0), -Mathf.Cos(a0)) * _R);
                p.LineTo(c);
                p.LineTo(c + new Vector2(Mathf.Sin(a1), -Mathf.Cos(a1)) * _R);
                p.Stroke();
            }

            // 方向把手
            var orange = new Color(1f, 0.69f, 0.13f);
            p.strokeColor = orange;
            p.lineWidth = 2;
            p.BeginPath();
            p.MoveTo(c);
            p.LineTo(_handleSp);
            p.Stroke();
            p.fillColor = orange;
            p.BeginPath();
            p.Arc(_handleSp, 6, Angle.Degrees(0), Angle.Degrees(360));
            p.Fill();

            // 相機位置
            p.fillColor = Color.white;
            p.strokeColor = orange;
            p.lineWidth = 3;
            p.BeginPath();
            p.Arc(c, 7, Angle.Degrees(0), Angle.Degrees(360));
            p.Fill();
            p.Stroke();

            // 比例尺
            float x = 12, y = _h - 14;
            p.fillColor = new Color(0, 0, 0, 0.5f);
            p.BeginPath();
            p.MoveTo(new Vector2(x - 6, y - 16));
            p.LineTo(new Vector2(x + _scalePx + 52, y - 16));
            p.LineTo(new Vector2(x + _scalePx + 52, y + 8));
            p.LineTo(new Vector2(x - 6, y + 8));
            p.ClosePath();
            p.Fill();
            p.strokeColor = Color.white;
            p.lineWidth = 2;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y - 4));
            p.LineTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + _scalePx, y));
            p.LineTo(new Vector2(x + _scalePx, y - 4));
            p.Stroke();

            // 指北
            var nc = new Vector2(_w - 22, 26);
            p.fillColor = new Color(0, 0, 0, 0.5f);
            p.BeginPath();
            p.Arc(nc, 15, Angle.Degrees(0), Angle.Degrees(360));
            p.Fill();
            p.fillColor = new Color(1f, 0.36f, 0.31f);
            p.BeginPath();
            p.MoveTo(nc + new Vector2(0, -11));
            p.LineTo(nc + new Vector2(5, 2));
            p.LineTo(nc + new Vector2(-5, 2));
            p.ClosePath();
            p.Fill();
        }

        // ---- 互動 ----

        class Drag
        {
            public enum Kind { Pan, Move, Aim, Orbit, OrbitPan }
            public Kind kind;
            public Vector2 start, last;
            public bool moved;
            public LatLon center0;
        }

        Drag _drag;

        void BindPointer()
        {
            _overlay.RegisterCallback<PointerDownEvent>(e =>
            {
                var pos = (Vector2)e.localPosition;
                var s = _store.State;
                if (_mode == Mode.Orbit)
                {
                    if (e.clickCount == 2 && _orbit != null && _orbit.Pick(new Vector2(pos.x / _w, 1 - pos.y / _h), out var hit))
                    {
                        _app.GoTo(hit);
                        return;
                    }
                    _drag = new Drag { kind = e.button == 0 ? Drag.Kind.Orbit : Drag.Kind.OrbitPan, start = pos, last = pos };
                }
                else
                {
                    var kind = Drag.Kind.Pan;
                    if ((pos - HandlePos(s)).magnitude < 12) kind = Drag.Kind.Aim;
                    else if ((pos - ToScreen(s.CameraLatLon)).magnitude < 12) kind = Drag.Kind.Move;
                    _drag = new Drag { kind = kind, start = pos, last = pos, center0 = _center };
                }
                _overlay.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            _overlay.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (_drag == null) return;
                var pos = (Vector2)e.localPosition;
                if ((pos - _drag.start).magnitude > 3) _drag.moved = true;
                var delta = pos - _drag.last;
                _drag.last = pos;
                if (!_drag.moved) return;
                switch (_drag.kind)
                {
                    case Drag.Kind.Pan:
                    {
                        var c0 = World(_drag.center0);
                        var off = pos - _drag.start;
                        _center = Unworld(c0.x - off.x / WorldPx, c0.y - off.y / WorldPx);
                        Refresh();
                        break;
                    }
                    case Drag.Kind.Move:
                        _app.GoTo(ToLatLon(pos));
                        break;
                    case Drag.Kind.Aim:
                    {
                        var s = _store.State;
                        var target = ToLatLon(pos);
                        _store.Set(st => st.azimuth = (float)Geo.BearingLL(s.CameraLatLon, target));
                        break;
                    }
                    case Drag.Kind.Orbit:
                        _orbit?.Orbit(delta);
                        break;
                    case Drag.Kind.OrbitPan:
                        _orbit?.Pan(delta);
                        break;
                }
            });
            _overlay.RegisterCallback<PointerUpEvent>(e =>
            {
                if (_drag != null && !_drag.moved && _drag.kind == Drag.Kind.Pan)
                {
                    var p = ToLatLon(e.localPosition);
                    if (e.shiftKey) _app.AimAt(p);
                    else _app.GoTo(p);
                }
                _drag = null;
                _overlay.ReleasePointer(e.pointerId);
            });
            _overlay.RegisterCallback<PointerCaptureOutEvent>(_ => _drag = null);
            _overlay.RegisterCallback<WheelEvent>(e =>
            {
                if (_mode == Mode.Orbit)
                {
                    _orbit?.Zoom(e.delta.y);
                }
                else
                {
                    var pos = (Vector2)e.localMousePosition;
                    var before = ToLatLon(pos);
                    _zoom = Math.Clamp(_zoom - e.delta.y * 0.08, 3, MaxZoom);
                    var bw = World(before);
                    // 保持滑鼠下的點不動
                    _center = Unworld(bw.x - (pos.x - _w / 2) / WorldPx, bw.y - (pos.y - _h / 2) / WorldPx);
                    Refresh();
                }
                e.StopPropagation();
            });
        }
    }
}
