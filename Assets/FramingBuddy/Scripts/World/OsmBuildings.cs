using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace FramingBuddy
{
    /// <summary>
    /// 沒有 Google 實景圖磚時的建物備援（與網頁版相同的資料來源）：
    /// - 遠景 z14（約 2 km 見方）：OpenFreeMap 向量圖磚的 building 圖層，高於 25 m 的建物（含 render_height）；
    /// - 近景 z15（約 1 km 見方）：Overpass 取全部建物與 building:part（樓層、高度、屋頂形狀）。
    /// 建物擠出成窗格立面＋屋頂（小型凸多邊形用四坡頂），每個圖磚合併成一個網格並產生碰撞體（遮擋判斷、站上屋頂）。
    /// </summary>
    public class OsmBuildings
    {
        const int FarZ = 14, NearZ = 15;
        const float FarMinHeight = 25;
        const float BayWidth = 3.5f, FloorHeight = 3.2f, AtlasBays = 8;

        readonly WorldController _world;
        readonly Transform _parent;
        readonly Dictionary<string, Tile> _tiles = new();
        int _inflightFar, _inflightNear, _epoch;
        public int errors;

        class Building
        {
            public List<Vector2> ring; // 局部（東、北）公尺，逆時針
            public LatLon center;
            public float minH, eave, top;
            public string roof;
            public int material;
        }

        class Tile
        {
            public string key;
            public bool near;
            public int x, y;
            public string status = "idle"; // idle / loading / data / built / error
            public List<Building> data;
            public GameObject go;
            public float lastWanted;
            public int builtEpoch = -1;
            public string exclusionKey = "";
        }

        public OsmBuildings(WorldController world, Transform parent)
        {
            _world = world;
            _parent = parent;
        }

        public (int built, int wanted) NearStats => Stats(true);
        public (int built, int wanted) FarStats => Stats(false);
        public int Loading => _inflightFar + _inflightNear;

        (int, int) Stats(bool near)
        {
            int built = 0, wanted = 0;
            foreach (var t in _tiles.Values)
            {
                if (t.near != near || Time.time - t.lastWanted > 1.5f) continue;
                wanted++;
                if (t.status == "built") built++;
            }
            return (built, wanted);
        }

        /// <summary>換原點：所有網格作廢（已下載的資料保留，重新建模）</summary>
        public void Rebase()
        {
            _epoch++;
            foreach (var t in _tiles.Values)
            {
                if (t.go) UnityEngine.Object.Destroy(t.go);
                t.go = null;
                if (t.status == "built") t.status = "data";
            }
        }

        public void SetVisible(bool v)
        {
            foreach (var t in _tiles.Values) if (t.go) t.go.SetActive(v);
        }

        // ---- 排程 ----

        public void Update(LatLon eye, double near, double range, bool active)
        {
            if (!active || !_world.terrainReady || _world.rebasing) return;
            float now = Time.time;
            var nearKeys = new HashSet<string>();
            foreach (var (x, y) in TilesAround(eye, near, NearZ))
            {
                var t = Want($"n/{x}/{y}", true, x, y, now);
                nearKeys.Add(t.key);
            }
            foreach (var (x, y) in TilesAround(eye, Math.Max(range, near), FarZ).OrderBy(t => TileDistance(eye, t.x, t.y, FarZ)).Take(64))
                Want($"f/{x}/{y}", false, x, y, now);

            // 遠景圖磚跳過已由近景圖磚涵蓋的建物：近景範圍改變時重建重疊的遠景圖磚
            string exKey = string.Join(",", nearKeys.Where(k => _tiles[k].status == "built").OrderBy(k => k));

            foreach (var t in _tiles.Values.OrderBy(t => TileDistance(eye, t.x, t.y, t.near ? NearZ : FarZ)).ToList())
            {
                bool wanted = now - t.lastWanted < 1.5f;
                if (!wanted)
                {
                    // 離開範圍 45 秒後釋放
                    if (t.go && now - t.lastWanted > 45)
                    {
                        UnityEngine.Object.Destroy(t.go);
                        t.go = null;
                        if (t.status == "built") t.status = "data";
                    }
                    if (now - t.lastWanted > 600) _tiles.Remove(t.key);
                    continue;
                }
                if (t.status == "idle")
                {
                    if (t.near && _inflightNear < 1) _ = LoadNear(t);
                    else if (!t.near && _inflightFar < 4) _ = LoadFar(t);
                }
                else if (t.status == "data" || (t.status == "built" && (t.builtEpoch != _epoch || (!t.near && t.exclusionKey != exKey))))
                {
                    _ = Build(t, t.near ? "" : exKey);
                }
            }
        }

        Tile Want(string key, bool near, int x, int y, float now)
        {
            if (!_tiles.TryGetValue(key, out var t))
            {
                t = new Tile { key = key, near = near, x = x, y = y };
                _tiles[key] = t;
            }
            t.lastWanted = now;
            return t;
        }

        static double TileDistance(LatLon p, int x, int y, int z)
        {
            var (s, w, n, e) = TileBounds(x, y, z);
            var q = new LatLon(Math.Clamp(p.lat, s, n), Math.Clamp(p.lon, w, e));
            return Geo.Distance(p, q);
        }

        static (double s, double w, double n, double e) TileBounds(int x, int y, int z)
        {
            double n = Math.Pow(2, z);
            double Lon(double xx) => xx / n * 360 - 180;
            double Lat(double yy) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * yy / n))) / Geo.Deg;
            return (Lat(y + 1), Lon(x), Lat(y), Lon(x + 1));
        }

        static IEnumerable<(int x, int y)> TilesAround(LatLon p, double radius, int z)
        {
            double n = Math.Pow(2, z);
            double dLat = radius / 111000.0, dLon = radius / (111000.0 * Math.Max(0.05, Math.Cos(p.lat * Geo.Deg)));
            int X(double lon) => (int)Math.Floor((lon + 180) / 360 * n);
            int Y(double lat)
            {
                double r = lat * Geo.Deg;
                return (int)Math.Floor((1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
            }
            for (int x = X(p.lon - dLon); x <= X(p.lon + dLon); x++)
                for (int y = Y(p.lat + dLat); y <= Y(p.lat - dLat); y++)
                    if (TileDistance(p, x, y, z) <= radius) yield return (x, y);
        }

        // ---- 遠景：OpenFreeMap 向量圖磚 ----

        async Task LoadFar(Tile t)
        {
            t.status = "loading";
            _inflightFar++;
            try
            {
                string tpl = await PeakService.TileTemplate();
                if (tpl == null) throw new Exception("tilejson");
                string url = tpl.Replace("{z}", FarZ.ToString()).Replace("{x}", t.x.ToString()).Replace("{y}", t.y.ToString());
                var bytes = await Net.GetBytesCached(url, $"mvt-{FarZ}-{t.x}-{t.y}.pbf", TimeSpan.FromDays(14));
                if (bytes == null) throw new Exception("download");
                t.data = await Task.Run(() => ParseFar(bytes, t.x, t.y));
                t.status = "data";
            }
            catch (Exception e)
            {
                errors++;
                t.status = "idle";
                Debug.LogWarning($"[osm] 遠景圖磚 {t.key}：{e.Message}");
                await Task.Delay(5000);
            }
            finally
            {
                _inflightFar--;
            }
        }

        static List<Building> ParseFar(byte[] bytes, int tx, int ty)
        {
            var feats = Mvt.DecodePolygons(bytes, "building", out int extent);
            var list = new List<Building>();
            double n = Math.Pow(2, FarZ);
            foreach (var f in feats)
            {
                float h = Num(f.props, "render_height");
                if (h < FarMinHeight || f.props.TryGetValue("hide_3d", out var hide) && hide is true) continue;
                float minH = Num(f.props, "render_min_height");
                foreach (var poly in f.polygons)
                {
                    var outer = poly[0];
                    // 裁掉跨出圖磚的部分（避免與鄰接圖磚重複）
                    var ll = new List<LatLon>();
                    foreach (var (px, py) in outer)
                    {
                        double fx = Math.Clamp(px / (double)extent, 0, 1), fy = Math.Clamp(py / (double)extent, 0, 1);
                        double lon = (tx + fx) / n * 360 - 180;
                        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (ty + fy) / n))) / Geo.Deg;
                        ll.Add(new LatLon(lat, lon));
                    }
                    var b = MakeBuilding(ll, minH, h, h, "flat", Hash(tx * 7919 + ty, list.Count));
                    if (b != null) list.Add(b);
                }
            }
            return list;
        }

        static float Num(Dictionary<string, object> p, string k) => p.TryGetValue(k, out var v) && v is double d ? (float)d : 0;

        // ---- 近景：Overpass ----

        static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        };
        static int _endpoint;

        [Serializable] class OGeom { public double lat, lon; }
        [Serializable] class OMember { public string type, role; public OGeom[] geometry; }
        [Serializable]
        class OTags
        {
            public string building, building_part, height, min_height, building_levels, building_min_level, roof_shape, roof_height, roof_levels, man_made;
        }
        [Serializable] class OElement { public string type; public long id; public OTags tags; public OGeom[] geometry; public OMember[] members; }
        [Serializable] class OResponse { public OElement[] elements; }

        async Task LoadNear(Tile t)
        {
            t.status = "loading";
            _inflightNear++;
            try
            {
                var (s, w, n, e) = TileBounds(t.x, t.y, NearZ);
                var inv = CultureInfo.InvariantCulture;
                string bbox = $"{s.ToString("F6", inv)},{w.ToString("F6", inv)},{n.ToString("F6", inv)},{e.ToString("F6", inv)}";
                string q = "[out:json][timeout:60][bbox:" + bbox + "];(way[\"building\"];relation[\"building\"][\"type\"=\"multipolygon\"];way[\"building:part\"];way[\"man_made\"~\"^(tower|mast|chimney)$\"];);out geom qt;";
                string cacheFile = System.IO.Path.Combine(Application.temporaryCachePath, "fb-cache", $"overpass-{NearZ}-{t.x}-{t.y}.json");
                string json = null;
                if (System.IO.File.Exists(cacheFile) && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromDays(14))
                    json = System.IO.File.ReadAllText(cacheFile);
                for (int attempt = 0; json == null && attempt < Endpoints.Length * 2; attempt++)
                {
                    json = await Net.PostForm(Endpoints[_endpoint % Endpoints.Length], "data=" + Uri.EscapeDataString(q), 90);
                    if (json == null || !json.TrimStart().StartsWith("{"))
                    {
                        json = null;
                        _endpoint++;
                        await Task.Delay(1500 * (attempt + 1));
                    }
                }
                if (json == null) throw new Exception("Overpass 無回應");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cacheFile));
                System.IO.File.WriteAllText(cacheFile, json);
                t.data = await Task.Run(() => ParseNear(json, t.x * 31 + t.y));
                t.status = "data";
            }
            catch (Exception ex)
            {
                errors++;
                t.status = "idle";
                Debug.LogWarning($"[osm] 近景圖磚 {t.key}：{ex.Message}");
                await Task.Delay(8000);
            }
            finally
            {
                _inflightNear--;
            }
        }

        static List<Building> ParseNear(string json, int seed)
        {
            // JsonUtility 不支援含冒號的鍵
            json = json.Replace("\"building:part\"", "\"building_part\"").Replace("\"building:levels\"", "\"building_levels\"")
                .Replace("\"building:min_level\"", "\"building_min_level\"").Replace("\"roof:shape\"", "\"roof_shape\"")
                .Replace("\"roof:height\"", "\"roof_height\"").Replace("\"roof:levels\"", "\"roof_levels\"");
            var r = JsonUtility.FromJson<OResponse>(json);
            var parts = new List<(List<LatLon> ring, OTags tags)>();
            var buildings = new List<(List<LatLon> ring, OTags tags)>();
            foreach (var el in r.elements ?? Array.Empty<OElement>())
            {
                if (el.tags == null) continue;
                var rings = new List<List<LatLon>>();
                if (el.type == "way" && el.geometry != null && el.geometry.Length >= 4) rings.Add(el.geometry.Select(g => new LatLon(g.lat, g.lon)).ToList());
                else if (el.type == "relation" && el.members != null)
                    rings.AddRange(Assemble(el.members.Where(m => m.role != "inner" && m.geometry != null).Select(m => m.geometry.Select(g => new LatLon(g.lat, g.lon)).ToList()).ToList()));
                foreach (var ring in rings)
                {
                    if (ring.Count < 4) continue;
                    if (Same(ring[0], ring[^1])) ring.RemoveAt(ring.Count - 1);
                    if (!string.IsNullOrEmpty(el.tags.building_part)) parts.Add((ring, el.tags));
                    else buildings.Add((ring, el.tags));
                }
            }
            var list = new List<Building>();
            // 有 building:part 的建物只畫各部分
            var partCenters = parts.Select(p => Centroid(p.ring)).ToList();
            int k = 0;
            foreach (var (ring, tags) in buildings.Concat(parts))
            {
                bool isPart = !string.IsNullOrEmpty(tags.building_part);
                if (!isPart && partCenters.Any(c => SiteData.PointInPolygon(c, ring))) continue;
                var (minH, eave, top, roof) = Shape(tags);
                var b = MakeBuilding(ring, minH, eave, top, roof, Hash(seed, k++));
                if (b != null) list.Add(b);
            }
            return list;
        }

        static bool Same(LatLon a, LatLon b) => Math.Abs(a.lat - b.lat) < 1e-9 && Math.Abs(a.lon - b.lon) < 1e-9;

        static List<List<LatLon>> Assemble(List<List<LatLon>> ways)
        {
            var pool = ways.Where(w => w.Count >= 2).ToList();
            var rings = new List<List<LatLon>>();
            while (pool.Count > 0)
            {
                var ring = new List<LatLon>(pool[0]);
                pool.RemoveAt(0);
                for (int guard = 0; guard < 2000 && !Same(ring[0], ring[^1]); guard++)
                {
                    var end = ring[^1];
                    int i = pool.FindIndex(w => Same(w[0], end) || Same(w[^1], end));
                    if (i < 0) break;
                    var w = pool[i];
                    pool.RemoveAt(i);
                    if (Same(w[0], end)) ring.AddRange(w.Skip(1));
                    else ring.AddRange(Enumerable.Reverse(w).Skip(1));
                }
                if (ring.Count >= 4 && Same(ring[0], ring[^1])) rings.Add(ring);
            }
            return rings;
        }

        static float? Len(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var m = Regex.Match(v.Trim(), @"^(-?\d+(?:[.,]\d+)?)\s*(m|meters?|ft|feet|')?", RegexOptions.IgnoreCase);
            if (!m.Success || !float.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return null;
            return m.Groups[2].Success && Regex.IsMatch(m.Groups[2].Value, "^(ft|feet|')$", RegexOptions.IgnoreCase) ? n * 0.3048f : n;
        }

        static readonly Dictionary<string, int> DefaultLevels = new()
        {
            ["house"] = 2, ["detached"] = 2, ["residential"] = 4, ["apartments"] = 6, ["terrace"] = 3, ["commercial"] = 4, ["retail"] = 2,
            ["office"] = 8, ["industrial"] = 2, ["warehouse"] = 2, ["garage"] = 1, ["garages"] = 1, ["shed"] = 1, ["hut"] = 1, ["roof"] = 1,
            ["kiosk"] = 1, ["carport"] = 1, ["school"] = 4, ["university"] = 5, ["hospital"] = 6, ["hotel"] = 10, ["church"] = 3, ["temple"] = 2,
            ["train_station"] = 2,
        };

        static (float minH, float eave, float top, string roof) Shape(OTags t)
        {
            float.TryParse(t.building_levels, NumberStyles.Float, CultureInfo.InvariantCulture, out float levels);
            float.TryParse(t.roof_levels, NumberStyles.Float, CultureInfo.InvariantCulture, out float roofLevels);
            float.TryParse(t.building_min_level, NumberStyles.Float, CultureInfo.InvariantCulture, out float minLevel);
            string roof = (t.roof_shape ?? "flat").ToLowerInvariant();
            float? height = Len(t.height);
            float minH = Len(t.min_height) ?? minLevel * FloorHeight;
            float lv = levels > 0 ? levels : DefaultLevels.TryGetValue(t.building ?? "", out int d) ? d : 3;
            if (t.man_made != null && height == null) height = 20;
            float? roofH = Len(t.roof_height) ?? (roofLevels > 0 ? roofLevels * 3 : null);
            float top = height ?? lv * FloorHeight + (roofH ?? 0) + minH;
            float rh = roofH ?? (roof == "flat" ? 0 : Mathf.Clamp((top - minH) * 0.2f, 2, 8));
            rh = Mathf.Min(rh, Mathf.Max(0, top - minH - 0.5f));
            return (minH, top - rh, top, roof);
        }

        static LatLon Centroid(List<LatLon> ring)
        {
            double lat = 0, lon = 0;
            foreach (var p in ring)
            {
                lat += p.lat;
                lon += p.lon;
            }
            return new LatLon(lat / ring.Count, lon / ring.Count);
        }

        static int Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393 + b * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                return (int)((h ^ (h >> 16)) & 0x7fffffff);
            }
        }

        /// <summary>環 → 建物（以中心點為原點的局部公尺座標，逆時針）</summary>
        static Building MakeBuilding(List<LatLon> ring, float minH, float eave, float top, string roof, int hash)
        {
            if (ring.Count < 3 || top <= minH) return null;
            var c = Centroid(ring);
            double mLat = 110574, mLon = 111320 * Math.Cos(c.lat * Geo.Deg);
            var pts = new List<Vector2>(ring.Count);
            foreach (var p in ring)
            {
                var v = new Vector2((float)((p.lon - c.lon) * mLon), (float)((p.lat - c.lat) * mLat));
                if (pts.Count == 0 || (pts[^1] - v).sqrMagnitude > 0.01f) pts.Add(v);
            }
            if (pts.Count >= 2 && (pts[0] - pts[^1]).sqrMagnitude < 0.01f) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return null;
            float area = SignedArea(pts);
            if (Mathf.Abs(area) < 4) return null;
            if (area < 0) pts.Reverse();
            return new Building { ring = pts, center = c, minH = minH, eave = eave, top = top, roof = roof, material = hash % 4 };
        }

        static float SignedArea(List<Vector2> p)
        {
            float a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].x * p[i].y - p[i].x * p[j].y;
            return a * 0.5f;
        }

        // ---- 建模 ----

        class MeshData
        {
            public List<Vector3> v = new();
            public List<Vector3> n = new();
            public List<Vector2> uv = new();
            public List<int>[] tris = { new(), new(), new(), new(), new() }; // 立面 0–3、屋頂 4
        }

        async Task Build(Tile t, string exclusionKey)
        {
            var data = t.data;
            if (data == null) return;
            int epoch = _epoch;
            t.status = "building";
            try
            {
                // 地面高度：原點 ±6 km 內用原點格網，更遠的圖磚另取 DEM
                var local = data.Select(b => Geo.ToLocal(b.center)).ToList();
                var ground = new float[data.Count];
                var farIdx = new List<int>();
                for (int i = 0; i < data.Count; i++)
                {
                    if (Math.Abs(local[i].x) < 5900 && Math.Abs(local[i].y) < 5900) ground[i] = _world.DemY(local[i].x, local[i].y);
                    else farIdx.Add(i);
                }
                if (farIdx.Count > 0)
                {
                    try
                    {
                        var h = await Dem.Sample(farIdx.Select(i => data[i].center).ToList(), 12);
                        for (int k = 0; k < farIdx.Count; k++)
                        {
                            int i = farIdx[k];
                            ground[i] = _world.EleToY(h[k], local[i].x, local[i].y);
                        }
                    }
                    catch (Exception)
                    {
                        foreach (int i in farIdx) ground[i] = _world.EleToY(_world.originEle, local[i].x, local[i].y);
                    }
                }
                // 遠景跳過近景圖磚已涵蓋的建物、以及自建地標範圍
                var nearBounds = new List<(double s, double w, double n, double e)>();
                if (!t.near && exclusionKey.Length > 0)
                    foreach (var k in exclusionKey.Split(','))
                    {
                        var p = k.Split('/');
                        nearBounds.Add(TileBounds(int.Parse(p[1]), int.Parse(p[2]), NearZ));
                    }
                var skip = new bool[data.Count];
                for (int i = 0; i < data.Count; i++)
                {
                    var c = data[i].center;
                    if (_world.Excluded(c)) skip[i] = true;
                    foreach (var (s, w, n, e) in nearBounds)
                        if (c.lat >= s && c.lat <= n && c.lon >= w && c.lon <= e) skip[i] = true;
                }
                var md = await Task.Run(() =>
                {
                    var m = new MeshData();
                    for (int i = 0; i < data.Count; i++)
                    {
                        if (skip[i]) continue;
                        var b = data[i];
                        var o = new Vector3((float)local[i].x, ground[i], -(float)local[i].y);
                        Extrude(m, b, o);
                    }
                    return m;
                });
                if (epoch != _epoch || !_tiles.ContainsKey(t.key)) return;
                if (t.go) UnityEngine.Object.Destroy(t.go);
                t.go = null;
                if (md.v.Count > 0)
                {
                    var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "osm " + t.key };
                    mesh.SetVertices(md.v);
                    mesh.SetNormals(md.n);
                    mesh.SetUVs(0, md.uv);
                    mesh.subMeshCount = 5;
                    for (int s = 0; s < 5; s++) mesh.SetTriangles(md.tris[s], s, false);
                    mesh.RecalculateBounds();
                    mesh.RecalculateTangents();
                    var go = new GameObject("OSM " + t.key);
                    go.transform.SetParent(_parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var a = _world.assets;
                    var mats = new Material[5];
                    for (int s = 0; s < 4; s++) mats[s] = a.facades.Count > 0 ? a.facades[s % a.facades.Count] : a.terrain;
                    mats[4] = a.roof ? a.roof : a.terrain;
                    mr.sharedMaterials = mats;
                    t.go = go;
                    var id = mesh.GetEntityId();
                    await Task.Run(() => Physics.BakeMesh(id, false));
                    if (go) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
                t.status = "built";
                t.builtEpoch = epoch;
                t.exclusionKey = exclusionKey;
                _world.version++;
            }
            catch (Exception e)
            {
                errors++;
                t.status = "data";
                Debug.LogWarning($"[osm] 建模 {t.key}：{e.Message}");
            }
        }

        static void Extrude(MeshData m, Building b, Vector3 o)
        {
            var ring = b.ring;
            int cnt = ring.Count;
            float y0 = o.y + b.minH - (b.minH <= 0 ? 2f : 0f); // 牆往地下多延伸，避免坡地浮空
            float yE = o.y + b.eave, yT = o.y + b.top;
            var wall = m.tris[b.material];
            float along = 0;
            for (int i = 0; i < cnt; i++)
            {
                var a = ring[i];
                var c = ring[(i + 1) % cnt];
                var d = c - a;
                float len = d.magnitude;
                if (len < 0.05f) continue;
                var nrm = new Vector3(d.y, 0, -d.x) / len;
                var A0 = o + new Vector3(a.x, 0, a.y);
                var C0 = o + new Vector3(c.x, 0, c.y);
                int k = m.v.Count;
                float u0 = along / BayWidth / AtlasBays, u1 = (along + len) / BayWidth / AtlasBays;
                float v0 = (y0 - o.y) / FloorHeight / AtlasBays, v1 = (yE - o.y) / FloorHeight / AtlasBays;
                m.v.Add(new Vector3(A0.x, y0, A0.z));
                m.v.Add(new Vector3(A0.x, yE, A0.z));
                m.v.Add(new Vector3(C0.x, yE, C0.z));
                m.v.Add(new Vector3(C0.x, y0, C0.z));
                for (int q = 0; q < 4; q++) m.n.Add(nrm);
                m.uv.Add(new Vector2(u0, v0));
                m.uv.Add(new Vector2(u0, v1));
                m.uv.Add(new Vector2(u1, v1));
                m.uv.Add(new Vector2(u1, v0));
                wall.Add(k); wall.Add(k + 1); wall.Add(k + 2);
                wall.Add(k); wall.Add(k + 2); wall.Add(k + 3);
                along += len;
            }
            var roofTris = m.tris[4];
            bool pitched = b.roof != "flat" && yT - yE > 0.3f && cnt <= 8 && IsConvex(ring);
            if (pitched)
            {
                // 四坡頂：每條邊連到中心頂點
                var apex = o + new Vector3(0, yT - o.y, 0);
                for (int i = 0; i < cnt; i++)
                {
                    var a = o + new Vector3(ring[i].x, yE - o.y, ring[i].y);
                    var c = o + new Vector3(ring[(i + 1) % cnt].x, yE - o.y, ring[(i + 1) % cnt].y);
                    var nrm = Vector3.Cross(apex - a, c - a).normalized;
                    if (nrm.y < 0) nrm = -nrm;
                    int k = m.v.Count;
                    m.v.Add(a);
                    m.v.Add(apex);
                    m.v.Add(c);
                    for (int q = 0; q < 3; q++) m.n.Add(nrm);
                    m.uv.Add(new Vector2(a.x, a.z) / 6f);
                    m.uv.Add(new Vector2(apex.x, apex.z) / 6f);
                    m.uv.Add(new Vector2(c.x, c.z) / 6f);
                    roofTris.Add(k); roofTris.Add(k + 1); roofTris.Add(k + 2);
                }
            }
            else
            {
                float yr = b.roof == "flat" ? yT : yE;
                var idx = EarClip(ring);
                int k = m.v.Count;
                foreach (var p in ring)
                {
                    m.v.Add(o + new Vector3(p.x, yr - o.y, p.y));
                    m.n.Add(Vector3.up);
                    m.uv.Add(new Vector2(o.x + p.x, o.z + p.y) / 6f);
                }
                // 逆時針環的三角形從上方看是逆時針：反轉成 Unity 的正面
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    roofTris.Add(k + idx[i]);
                    roofTris.Add(k + idx[i + 2]);
                    roofTris.Add(k + idx[i + 1]);
                }
            }
        }

        static bool IsConvex(List<Vector2> p)
        {
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                var c = p[(i + 2) % p.Count];
                if ((b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x) < -1e-3f) return false;
            }
            return true;
        }

        /// <summary>耳切法三角化（逆時針簡單多邊形）</summary>
        static List<int> EarClip(List<Vector2> p)
        {
            var result = new List<int>();
            var idx = Enumerable.Range(0, p.Count).ToList();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 5000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    var a = p[ia];
                    var b = p[ib];
                    var c = p[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-6f) continue; // 凹角
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (InTri(p[j], a, b, c))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside) continue;
                    result.Add(ia);
                    result.Add(ib);
                    result.Add(ic);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // 自相交等異常：放棄剩下的部分
            }
            if (idx.Count == 3)
            {
                result.Add(idx[0]);
                result.Add(idx[1]);
                result.Add(idx[2]);
            }
            return result;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
