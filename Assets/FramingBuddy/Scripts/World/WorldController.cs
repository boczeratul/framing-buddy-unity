using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace FramingBuddy
{
    /// <summary>
    /// 場景總成：
    /// - Cesium 地理參考（原點＝選定地點，Unity 座標為東－上－北，y＝橢球高 − 原點橢球高）；
    /// - Google Photorealistic 3D Tiles（有桌面用金鑰時；遠景與沒有自建模型的近景）；
    /// - 自建精細模型：地標（近景範圍內接手，挖空 Google 模型）、遠方顯著山峰的 DEM 精細山體；
    /// - 沒有 Google 時：三圈 DEM 地形（到地平線）＋地標；
    /// - 站立面、目標清單、遮擋物（Physics）。
    /// </summary>
    public class WorldController : MonoBehaviour
    {
        public const double Horizon = 90000;
        public const int LayerWorld = 0;

        public AppAssets assets;
        public CesiumGeoreference georef;
        public Cesium3DTileset tileset;
        CesiumPolygonRasterOverlay _clip;

        /// <summary>原點（y＝0）的橢球高</summary>
        public double originH;
        /// <summary>原點地面海拔（DEM）</summary>
        public float originEle;
        /// <summary>大地水準面差：橢球高 − 海拔（由 Google 表面與 DEM 比對估算）</summary>
        public double geoid;
        public bool googleFailed;
        public string googleError;
        public bool terrainReady;
        public bool rebasing;
        /// <summary>內容改變時遞增（重新計算站立面、重新渲染資訊）</summary>
        public int version;

        HeightGrid _originGrid;
        readonly PeakService _peaks = new();
        readonly NamedTargetService _named = new();
        readonly Dictionary<string, PlacedLandmark> _placed = new();
        readonly Dictionary<string, GameObject> _polygonObjs = new();
        readonly List<Mountain> _mountains = new();
        GameObject _fallbackTerrain;
        int _rebaseToken;
        int _month = 9;
        bool _photoreal = true;
        bool _relight = true;
        Quality _quality = Quality.High;
        float _near = 1000, _range = 6000;
        LatLon _eye;
        bool _trees = true;

        class PlacedLandmark
        {
            public SiteData.LandmarkDef def;
            public GameObject root;
            public CesiumGlobeAnchor anchor;
            public GameObject trees;
            public float groundY;
            public bool probing;
            public float probedAt = -999;
        }

        class Mountain
        {
            public PeakService.Peak peak;
            public GameObject root;
            public HeightGrid grid;
            public Material material;
            public Texture2D albedo;
            public float polyR;
            public List<LatLon> mask;
        }

        public bool GoogleActive => tileset != null && _photoreal && !googleFailed;

        // ---------------------------------------------------------------------------------------

        public void Init(AppAssets appAssets)
        {
            assets = appAssets;
            var go = new GameObject("CesiumGeoreference");
            go.transform.SetParent(transform, false);
            georef = go.AddComponent<CesiumGeoreference>();
            georef.originPlacement = CesiumGeoreferenceOriginPlacement.CartographicOrigin;
            Cesium3DTileset.OnCesium3DTilesetLoadFailure += OnTilesetFailure;
            CreateTileset();
        }

        void OnDestroy() => Cesium3DTileset.OnCesium3DTilesetLoadFailure -= OnTilesetFailure;

        void OnTilesetFailure(Cesium3DTilesetLoadFailureDetails d)
        {
            if (d.tileset != tileset) return;
            // 根節點失敗（金鑰無效、未啟用 Map Tiles API、配額）才視為整體失敗
            if (d.type == Cesium3DTilesetLoadType.TilesetJson)
            {
                googleFailed = true;
                googleError = $"{d.httpStatusCode} {d.message}";
                Debug.LogWarning("[google] 3D 圖磚載入失敗：" + googleError);
                ApplyVisibility();
                version++;
            }
        }

        void CreateTileset()
        {
            if (tileset != null) Destroy(tileset.gameObject);
            tileset = null;
            googleFailed = false;
            if (!Config.HasGoogle) return;
            var go = new GameObject("Google Photorealistic 3D Tiles");
            go.transform.SetParent(georef.transform, false);
            tileset = go.AddComponent<Cesium3DTileset>();
            tileset.tilesetSource = CesiumDataSource.FromUrl;
            tileset.url = "https://tile.googleapis.com/v1/3dtiles/root.json?key=" + Config.GoogleKey;
            tileset.showCreditsOnScreen = true;
            tileset.createPhysicsMeshes = true;
            tileset.enableFogCulling = true;
            tileset.maximumCachedBytes = 1536L * 1024 * 1024;
            tileset.maximumSimultaneousTileLoads = 24;
            ApplyTilesetQuality();
            _clip = go.AddComponent<CesiumPolygonRasterOverlay>();
            _clip.invertSelection = false;
            _clip.polygons = new List<CesiumCartographicPolygon>();
        }

        void ApplyTilesetQuality()
        {
            if (tileset == null) return;
            tileset.maximumScreenSpaceError = _quality switch
            {
                Quality.Low => 24,
                Quality.Medium => 16,
                Quality.High => 10,
                _ => 6,
            };
            tileset.ignoreKhrMaterialsUnlit = _relight;
            tileset.generateSmoothNormals = _relight;
        }

        public void ApplySettings(ShotState s, HashSet<string> changed)
        {
            _near = s.near;
            _range = s.range * 1000;
            bool retile = false;
            if (changed.Contains("photoreal")) { _photoreal = s.photoreal; ApplyVisibility(); version++; }
            if (changed.Contains("relight") && _relight != s.relight) { _relight = s.relight; retile = true; }
            if (changed.Contains("quality")) { _quality = s.quality; ApplyTilesetQuality(); }
            if (changed.Contains("trees")) { _trees = s.trees; foreach (var p in _placed.Values) if (p.trees) p.trees.SetActive(_trees); }
            if (changed.Contains("date"))
            {
                int m = int.Parse(s.date.Substring(5, 2));
                if (m != _month) { _month = m; RebakeSnow(); }
            }
            if (retile && tileset != null)
            {
                ApplyTilesetQuality();
                tileset.RecreateTileset();
            }
        }

        /// <summary>金鑰更新後重建 Google 圖磚</summary>
        public void ReloadGoogle()
        {
            CreateTileset();
            ApplyVisibility();
            _ = CalibrateOrigin(_rebaseToken);
        }

        void ApplyVisibility()
        {
            bool g = GoogleActive;
            if (tileset != null) tileset.gameObject.SetActive(_photoreal && !googleFailed);
            if (_fallbackTerrain) _fallbackTerrain.SetActive(!g);
        }

        // ---- 原點 --------------------------------------------------------------------------------

        /// <summary>換原點：地形重抓、Google 圖磚重新定位、地標與山體依新原點重建</summary>
        public async Task Rebase(LatLon origin, float rangeM)
        {
            int token = ++_rebaseToken;
            rebasing = true;
            terrainReady = false;
            Geo.SetOrigin(origin);
            foreach (var p in _placed.Values) Destroy(p.root);
            _placed.Clear();
            ClearMountains();
            try
            {
                _originGrid = await HeightGrid.Fetch(origin, 6000, 50, 12);
                if (token != _rebaseToken) return;
                originEle = _originGrid.Sample(0, 0);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[terrain] DEM 取得失敗：" + e.Message);
                _originGrid = null;
                originEle = 0;
            }
            geoid = 0;
            originH = originEle + geoid;
            georef.SetOriginLongitudeLatitudeHeight(origin.lon, origin.lat, originH);
            terrainReady = true;
            rebasing = false;
            version++;

            await BuildFallbackTerrain(origin, token);
            if (token != _rebaseToken) return;
            ApplyVisibility();
            _ = CalibrateOrigin(token);
            _ = _named.Load(origin, rangeM, Excluded).ContinueWith(_ => version++, TaskScheduler.FromCurrentSynchronizationContext());
            await _peaks.Load(origin, Horizon, originEle);
            if (token != _rebaseToken) return;
            version++;
            await LoadMountains(origin, token);
        }

        public void SetRange(float rangeM) =>
            _ = _named.Load(Geo.Origin, rangeM, Excluded).ContinueWith(_ => version++, TaskScheduler.FromCurrentSynchronizationContext());

        /// <summary>
        /// 以 Google 表面校正原點高度與大地水準面差：原點周圍取樣，與 DEM 比較後取低百分位數
        /// （表面常是屋頂或樹冠，只會偏高）。圖磚由粗到細載入，反覆進行直到穩定。
        /// </summary>
        async Task CalibrateOrigin(int token)
        {
            if (!GoogleActive || _originGrid == null) return;
            double last = double.NaN;
            for (int run = 0; run < 12 && token == _rebaseToken && GoogleActive; run++)
            {
                await Task.Delay(run == 0 ? 3000 : 2500);
                if (token != _rebaseToken || tileset == null) return;
                var pts = new List<double3>();
                var dem = new List<float>();
                for (int i = -3; i <= 3; i++)
                    for (int j = -3; j <= 3; j++)
                    {
                        var p = Geo.Offset(Geo.Origin, i * 100, j * 100);
                        pts.Add(new double3(p.lon, p.lat, 0));
                        dem.Add(_originGrid.Sample(i * 100, j * 100));
                    }
                CesiumSampleHeightResult r;
                try
                {
                    r = await tileset.SampleHeightMostDetailed(pts.ToArray());
                }
                catch (Exception)
                {
                    continue;
                }
                if (token != _rebaseToken) return;
                var diffs = new List<double>();
                for (int k = 0; k < pts.Count; k++)
                    if (r.sampleSuccess[k]) diffs.Add(r.longitudeLatitudeHeightPositions[k].z - dem[k]);
                if (diffs.Count < 10) continue;
                diffs.Sort();
                double g = diffs[(int)(diffs.Count * 0.2)];
                if (!double.IsNaN(last) && Math.Abs(g - last) < 0.3) break;
                last = g;
                geoid = g;
                originH = originEle + geoid;
                georef.SetOriginLongitudeLatitudeHeight(Geo.Origin.lon, Geo.Origin.lat, originH);
                version++;
            }
        }

        /// <summary>海拔 → 場景 y（含曲率下沉）</summary>
        public float EleToY(double ele, double x, double z) => (float)(ele + geoid - originH - Geo.CurvatureDrop(Math.Sqrt(x * x + z * z)));

        public float DemY(double x, double z)
        {
            if (_originGrid == null) return 0;
            if (Math.Abs(x) > 6000 || Math.Abs(z) > 6000) return EleToY(originEle, x, z);
            return EleToY(_originGrid.Sample(x, -z), x, z);
        }

        // ---- 站立面 ------------------------------------------------------------------------------

        /// <summary>
        /// 站立面（場景 y）。fromY 有值：從該高度往下找（走路，可上台階）；否則為瞬間移動：
        /// 站在最上層表面，若該點是樹冠則找周圍 5 m 內樹冠間隙露出的地面。solid＝確實打到模型。
        /// </summary>
        public (float y, bool solid) SurfaceAt(double x, double z, float fromY = float.PositiveInfinity)
        {
            float dem = DemY(x, z);
            bool walking = !float.IsPositiveInfinity(fromY);
            float start = walking ? fromY : dem + 3000;
            float? Top(double px, double pz)
            {
                if (Physics.Raycast(new Vector3((float)px, start, -(float)pz), Vector3.down, out var hit, start - dem + 400, ~0, QueryTriggerInteraction.Ignore))
                    return hit.point.y;
                return null;
            }
            var here = Top(x, z);
            if (here == null) return (dem, false);
            if (walking) return (here.Value, true);
            float low = here.Value;
            for (int k = 0; k < 10; k++)
            {
                double a = k / 10.0 * Math.PI * 2;
                var y = Top(x + Math.Cos(a) * 5, z + Math.Sin(a) * 5);
                if (y != null) low = Math.Min(low, y.Value);
            }
            return (here.Value - low > 6 && low > dem - 25 ? low : here.Value, true);
        }

        // ---- 每幀 --------------------------------------------------------------------------------

        public void Tick(Vector3 eyeUnity, ShotState s)
        {
            _eye = Geo.ToLatLon(eyeUnity.x, -eyeUnity.z);
            if (terrainReady && !rebasing) UpdateLandmarks();
        }

        // ---- 地標（自建精細模型） --------------------------------------------------------------

        public bool Excluded(LatLon p)
        {
            foreach (var d in SiteData.Landmarks)
            {
                if (Geo.Distance(p, d.anchor) > d.radius * 1.6) continue;
                if (SiteData.PointInPolygon(p, d.exclusion)) return true;
            }
            return false;
        }

        void UpdateLandmarks()
        {
            bool google = GoogleActive;
            bool changed = false;
            foreach (var d in SiteData.Landmarks)
            {
                double dist = Geo.Distance(_eye, d.anchor);
                bool want = dist < (google ? _near : _range) + d.radius;
                _placed.TryGetValue(d.id, out var p);
                if (want && p == null)
                {
                    var prefab = assets ? assets.Landmark(d.id) : null;
                    if (prefab == null) continue;
                    p = Place(d, prefab);
                    _placed[d.id] = p;
                    changed = true;
                }
                else if (!want && p != null)
                {
                    Destroy(p.root);
                    _placed.Remove(d.id);
                    changed = true;
                    continue;
                }
                if (p == null) continue;
                // 底座高度：Google 模式對齊周圍 Google 地面；否則用 DEM
                if (google && !p.probing && Time.time - p.probedAt > 4f) _ = ProbeGround(p);
                else if (!google)
                {
                    var l = Geo.ToLocal(d.anchor);
                    float y = DemY(l.x, l.y);
                    if (Math.Abs(y - p.groundY) > 0.2f) SetGround(p, y);
                }
            }
            UpdateClipping(google);
            if (changed) version++;
        }

        PlacedLandmark Place(SiteData.LandmarkDef d, GameObject prefab)
        {
            var root = new GameObject("Landmark:" + d.id);
            root.transform.SetParent(georef.transform, false);
            var anchor = root.AddComponent<CesiumGlobeAnchor>();
            anchor.adjustOrientationForGlobeWhenMoving = true;
            var model = Instantiate(prefab, root.transform, false);
            // glTFast 把 glTF 的 X 軸反轉；網頁版模型 +X 東、+Z 南 → 轉 180° 後對齊 Unity 的東－上－北
            model.transform.localRotation = Quaternion.Euler(0, 180, 0);
            GameObject trees = null;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name.ToLowerInvariant().Contains("tree")) { trees = t.gameObject; break; }
            if (trees) trees.SetActive(_trees);
            var p = new PlacedLandmark { def = d, root = root, anchor = anchor, trees = trees };
            var l = Geo.ToLocal(d.anchor);
            SetGround(p, DemY(l.x, l.y));
            _ = AddColliders(model);
            return p;
        }

        void SetGround(PlacedLandmark p, float y)
        {
            p.groundY = y;
            p.anchor.longitudeLatitudeHeight = new double3(p.def.anchor.lon, p.def.anchor.lat, originH + y + Geo.CurvatureDrop(Geo.Distance(Geo.Origin, p.def.anchor)));
            p.root.transform.localRotation = p.root.transform.localRotation; // 讓錨點重新計算方向
            version++;
        }

        /// <summary>沿挖空範圍外圍一圈取樣 Google 地面，取低百分位數（樹、車、建物只會讓表面偏高）</summary>
        async Task ProbeGround(PlacedLandmark p)
        {
            p.probing = true;
            p.probedAt = Time.time;
            try
            {
                var ring = p.def.exclusion;
                var c = p.def.anchor;
                var pts = new List<double3>();
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    foreach (double t in new[] { 0.0, 0.5 })
                    {
                        var q = new LatLon(a.lat + (b.lat - a.lat) * t, a.lon + (b.lon - a.lon) * t);
                        double dE = (q.lon - c.lon) * 111320 * Math.Cos(c.lat * Geo.Deg), dN = (q.lat - c.lat) * 110574;
                        double len = Math.Max(1, Math.Sqrt(dE * dE + dN * dN));
                        var o = Geo.Offset(q, dE / len * 14, dN / len * 14);
                        pts.Add(new double3(o.lon, o.lat, 0));
                    }
                }
                if (tileset == null) return;
                var r = await tileset.SampleHeightMostDetailed(pts.ToArray());
                var ys = new List<double>();
                for (int k = 0; k < pts.Count; k++) if (r.sampleSuccess[k]) ys.Add(r.longitudeLatitudeHeightPositions[k].z);
                if (ys.Count < 3 || p.root == null) return;
                ys.Sort();
                double h = ys[(int)(ys.Count * 0.3)];
                float y = (float)(h - originH - Geo.CurvatureDrop(Geo.Distance(Geo.Origin, p.def.anchor)));
                if (Math.Abs(y - p.groundY) > 0.25f) SetGround(p, y);
            }
            catch (Exception)
            {
                // 圖磚尚未載入：稍後重試
            }
            finally
            {
                p.probing = false;
            }
        }

        /// <summary>大型網格的碰撞體在背景執行緒烘焙（Physics.BakeMesh 為執行緒安全）</summary>
        static async Task AddColliders(GameObject model)
        {
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            var ids = filters.Where(f => f.sharedMesh).Select(f => f.sharedMesh.GetEntityId()).ToArray();
            await Task.Run(() =>
            {
                foreach (var id in ids) Physics.BakeMesh(id, false);
            });
            foreach (var f in filters)
            {
                if (f == null || f.sharedMesh == null) continue;
                var mc = f.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = f.sharedMesh;
            }
        }

        // ---- Google 挖空 -------------------------------------------------------------------------

        readonly List<string> _clipKeys = new();

        void UpdateClipping(bool google)
        {
            if (_clip == null) return;
            var want = new List<(string key, LatLon anchor, List<LatLon> ring)>();
            if (google)
            {
                foreach (var p in _placed.Values) want.Add(("lm:" + p.def.id, p.def.anchor, p.def.exclusion));
                foreach (var m in _mountains) want.Add(("mt:" + m.peak.name, m.peak.at, m.mask));
            }
            var keys = want.Select(w => w.key).ToList();
            if (keys.SequenceEqual(_clipKeys)) return;
            _clipKeys.Clear();
            _clipKeys.AddRange(keys);
            foreach (var kv in _polygonObjs.Where(kv => !keys.Contains(kv.Key)).ToList())
            {
                Destroy(kv.Value);
                _polygonObjs.Remove(kv.Key);
            }
            var list = new List<CesiumCartographicPolygon>();
            foreach (var (key, anchor, ring) in want)
            {
                if (!_polygonObjs.TryGetValue(key, out var go))
                {
                    go = new GameObject("Clip:" + key);
                    go.transform.SetParent(georef.transform, false);
                    var container = go.AddComponent<SplineContainer>();
                    var ga = go.AddComponent<CesiumGlobeAnchor>();
                    ga.longitudeLatitudeHeight = new double3(anchor.lon, anchor.lat, originH);
                    var spline = new Spline();
                    var knots = new List<BezierKnot>();
                    foreach (var q in ring)
                    {
                        double e = (q.lon - anchor.lon) * 111320 * Math.Cos(anchor.lat * Geo.Deg);
                        double n = (q.lat - anchor.lat) * 110574;
                        knots.Add(new BezierKnot(new float3((float)e, 0, (float)n)));
                    }
                    spline.Knots = knots;
                    spline.Closed = true;
                    spline.SetTangentMode(TangentMode.Linear);
                    container.Spline = spline;
                    go.AddComponent<CesiumCartographicPolygon>();
                    _polygonObjs[key] = go;
                }
                list.Add(go.GetComponent<CesiumCartographicPolygon>());
            }
            _clip.polygons = list;
            _clip.Refresh();
        }

        // ---- 遠方精細山體 ------------------------------------------------------------------------

        void ClearMountains()
        {
            foreach (var m in _mountains)
            {
                if (m.root) Destroy(m.root);
                if (m.albedo) Destroy(m.albedo);
            }
            _mountains.Clear();
        }

        async Task LoadMountains(LatLon origin, int token)
        {
            var cands = _peaks.list
                .Select(p =>
                {
                    double relief = p.ele - originEle;
                    double radius = Math.Min(12000, Math.Max(3000, relief * 3.2));
                    double dist = Geo.Distance(origin, p.at);
                    return (p, relief, radius, dist);
                })
                .Where(c => c.relief > 1400 && c.dist > c.radius + 2500 && c.dist < Horizon)
                .OrderByDescending(c => c.relief / c.dist)
                .ToList();
            var chosen = new List<(PeakService.Peak p, double relief, double radius, double dist)>();
            foreach (var c in cands)
            {
                if (chosen.Any(o => Geo.Distance(o.p.at, c.p.at) < o.radius + c.radius * 0.5 && o.p.ele >= c.p.ele)) continue;
                chosen.Add(c);
                if (chosen.Count >= 2) break;
            }
            foreach (var c in chosen)
            {
                try
                {
                    var m = await BuildMountain(c.p, c.radius);
                    if (token != _rebaseToken) { Destroy(m.root); return; }
                    _mountains.Add(m);
                    version++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[mountains] {c.p.name} 建模失敗：{e.Message}");
                }
            }
        }

        async Task<Mountain> BuildMountain(PeakService.Peak peak, double radius)
        {
            const int sides = 16;
            double polyR = radius / Math.Cos(Math.PI / sides);
            double apothem = polyR * Math.Cos(Math.PI / sides);
            double half = polyR + 400;
            double step = Math.Max(25, Math.Round(half / 500));
            var grid = await HeightGrid.Fetch(peak.at, half, step, 13);
            var mask = new List<LatLon>();
            for (int i = 0; i < sides; i++)
            {
                double a = i / (double)sides * Math.PI * 2;
                mask.Add(Geo.Offset(peak.at, Math.Cos(a) * polyR, Math.Sin(a) * polyR));
            }
            double Outside(double e, double n)
            {
                double a = Math.Atan2(n, e);
                if (a < 0) a += Math.PI * 2;
                int k = (int)(a / (Math.PI * 2) * sides);
                double mid = (k + 0.5) / sides * Math.PI * 2;
                return e * Math.Cos(mid) + n * Math.Sin(mid) - apothem;
            }
            int stride = Math.Max(1, (int)Math.Round(Math.Max(50, polyR / 150) / step));
            var mesh = TerrainBaker.BuildMesh(grid, stride,
                (e, n, ele) =>
                {
                    double d = Outside(e, n);
                    double dip = Math.Clamp(d / 200, 0, 1) * 60;
                    return (float)(ele - Geo.CurvatureDrop(Math.Sqrt(e * e + n * n)) - dip);
                },
                (e, n) => Outside(e, n) < 380, 150);
            var root = new GameObject("Mountain:" + peak.name);
            root.transform.SetParent(georef.transform, false);
            var ga = root.AddComponent<CesiumGlobeAnchor>();
            ga.longitudeLatitudeHeight = new double3(peak.at.lon, peak.at.lat, geoid);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();
            var mat = new Material(assets.terrain);
            var albedo = TerrainBaker.BakeAlbedo(grid, 2048, Palette(peak.at.lat));
            var normal = TerrainBaker.BakeNormal(grid, 2048);
            HdrpMaterials.SetLit(mat, albedo, normal, 0.12f);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            await Task.Run(() => Physics.BakeMesh(mesh.GetEntityId(), false));
            root.AddComponent<MeshCollider>().sharedMesh = mesh;
            return new Mountain { peak = peak, root = root, grid = grid, material = mat, albedo = albedo, polyR = (float)polyR, mask = mask };
        }

        TerrainBaker.Palette Palette(double lat) => new TerrainBaker.Palette
        {
            snowLine = SiteData.SnowLine(_month, lat),
            treeLine = SiteData.TreeLine(lat),
        };

        void RebakeSnow()
        {
            foreach (var m in _mountains)
            {
                TerrainBaker.BakeAlbedo(m.grid, m.albedo.width, Palette(m.peak.at.lat), m.albedo);
            }
            if (_fallbackTerrain) _ = BuildFallbackTerrain(Geo.Origin, _rebaseToken);
            version++;
        }

        public IEnumerable<string> MountainNames => _mountains.Select(m => m.peak.name);

        // ---- 無 Google 時的地形 --------------------------------------------------------------------

        async Task BuildFallbackTerrain(LatLon origin, int token)
        {
            if (_fallbackTerrain) Destroy(_fallbackTerrain);
            _fallbackTerrain = null;
            if (GoogleActive && !googleFailed) return;
            var root = new GameObject("Terrain (DEM)");
            root.transform.SetParent(georef.transform, false);
            var ga = root.AddComponent<CesiumGlobeAnchor>();
            ga.longitudeLatitudeHeight = new double3(origin.lon, origin.lat, geoid);
            _fallbackTerrain = root;
            var rings = new (double half, double step, int stride, int tex, int zoom)[]
            {
                (6000, 25, 4, 2048, 13),
                (25000, 100, 4, 1024, 11),
                (Horizon, 750, 2, 1024, 8),
            };
            double inner = 0;
            foreach (var r in rings)
            {
                HeightGrid g;
                try
                {
                    g = await HeightGrid.Fetch(origin, r.half, r.step, r.zoom);
                }
                catch (Exception)
                {
                    break;
                }
                if (token != _rebaseToken || root == null) return;
                double skip = inner;
                var mesh = TerrainBaker.BuildMesh(g, r.stride,
                    (e, n, ele) =>
                    {
                        bool inside = skip > 0 && Math.Abs(e) < skip + r.step * r.stride && Math.Abs(n) < skip + r.step * r.stride;
                        return (float)(ele - Geo.CurvatureDrop(Math.Sqrt(e * e + n * n)) - (inside ? 6 + r.step * 0.01 : 0) - 0.3);
                    },
                    skip > 0 ? (e, n) => !(Math.Abs(e) < skip - r.step * r.stride && Math.Abs(n) < skip - r.step * r.stride) : null,
                    skip > 0 ? 0 : 60);
                var go = new GameObject($"ring ±{r.half / 1000} km");
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(assets.terrain);
                HdrpMaterials.SetLit(mat, TerrainBaker.BakeAlbedo(g, r.tex, Palette(origin.lat)), TerrainBaker.BakeNormal(g, r.tex), 0.08f);
                mr.sharedMaterial = mat;
                if (inner == 0)
                {
                    await Task.Run(() => Physics.BakeMesh(mesh.GetEntityId(), false));
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
                inner = r.half;
                version++;
            }
        }

        // ---- 目標 --------------------------------------------------------------------------------

        public List<Target> Targets()
        {
            var list = new List<Target>();
            var seen = new HashSet<string>();
            foreach (var d in SiteData.Landmarks)
            {
                if (Geo.Distance(_eye, d.anchor) > _range + d.radius) continue;
                var l = Geo.ToLocal(d.anchor);
                float ground = _placed.TryGetValue(d.id, out var p) ? p.groundY : DemY(l.x, l.y);
                foreach (var t in d.targets)
                {
                    list.Add(new Target
                    {
                        id = t.id, label = t.label, at = t.at, baseY = ground, topY = ground + t.topY,
                        aimAt = t.aimAt, radius = t.radius, kind = t.kind,
                    });
                    seen.Add(t.label);
                }
            }
            foreach (var pk in _peaks.list)
            {
                var l = Geo.ToLocal(pk.at);
                float top = EleToY(pk.ele, l.x, l.y);
                float relief = Mathf.Clamp((float)(pk.ele - originEle), 200, 2500);
                string label = $"{pk.name}（{Math.Round(pk.ele)} m）";
                list.Add(new Target { id = "peak:" + pk.name, label = label, at = pk.at, baseY = top - relief, topY = top, aimAt = 0.7f, radius = 300, kind = TargetKind.Peak });
            }
            foreach (var n in _named.list)
            {
                if (seen.Contains(n.label)) continue;
                var l = Geo.ToLocal(n.at);
                float ground = DemY(l.x, l.y);
                list.Add(new Target { id = n.id, label = n.label, at = n.at, baseY = ground, topY = ground + n.topY, aimAt = 0.5f, radius = n.radius, kind = TargetKind.Building });
            }
            return list;
        }

        public int PeakVersion => _peaks.version + _named.version * 1000;
    }
}
