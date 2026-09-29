using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FramingBuddy
{
    /// <summary>
    /// 湖泊、水庫、池塘（OSM natural=water）：在相機附近建立平整水面（光滑、帶細微波紋的 HDRP 材質），
    /// 以螢幕空間反射與平面反射探針呈現倒影（河口湖、山中湖的逆富士等構圖）。
    /// 有 Google 圖磚時把水域從圖磚挖空，避免攝影測量的湖面與水面重疊閃爍。
    /// </summary>
    public class WaterBodies
    {
        const double Radius = 4000;

        readonly WorldController _world;
        readonly Transform _parent;
        readonly List<Body> _bodies = new();
        LatLon _loadedAt;
        bool _loading, _loaded;
        int _token;
        PlanarReflectionProbe _probe;
        Quality _quality = Quality.High;

        public class Body
        {
            public long id;
            public List<LatLon> ring;
            public LatLon center;
            public float y;
            public GameObject go;
            public Bounds bounds;
        }

        public IReadOnlyList<Body> Bodies => _bodies;

        public WaterBodies(WorldController world, Transform parent)
        {
            _world = world;
            _parent = parent;
        }

        public void Clear()
        {
            _token++;
            foreach (var b in _bodies) if (b.go) UnityEngine.Object.Destroy(b.go);
            _bodies.Clear();
            _loaded = false;
            _loading = false;
            if (_probe) _probe.gameObject.SetActive(false);
        }

        public void SetQuality(Quality q) => _quality = q;
        public static bool Disabled, NoProbe;

        public void Update(LatLon eye, Vector3 eyeUnity)
        {
            if (!_world.terrainReady || _world.rebasing || Disabled) return;
            if (!_loading && (!_loaded || Geo.Distance(eye, _loadedAt) > Radius * 0.5)) _ = Load(eye);
            UpdateProbe(eyeUnity);
        }

        // ---- 下載 ----

        [Serializable] class OGeom { public double lat, lon; }
        [Serializable] class OMember { public string type, role; public OGeom[] geometry; }
        [Serializable] class OElement { public string type; public long id; public OGeom[] geometry; public OMember[] members; }
        [Serializable] class OResponse { public OElement[] elements; }

        static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        };

        async Task Load(LatLon eye)
        {
            _loading = true;
            int token = ++_token;
            try
            {
                var sw = Geo.Offset(eye, -Radius, -Radius);
                var ne = Geo.Offset(eye, Radius, Radius);
                var inv = CultureInfo.InvariantCulture;
                string bbox = $"{sw.lat.ToString("F4", inv)},{sw.lon.ToString("F4", inv)},{ne.lat.ToString("F4", inv)},{ne.lon.ToString("F4", inv)}";
                string q = "[out:json][timeout:60][bbox:" + bbox + "];(way[\"natural\"=\"water\"];relation[\"natural\"=\"water\"][\"type\"=\"multipolygon\"];way[\"landuse\"=\"reservoir\"];);out geom;";
                string key = $"water-{Math.Round(eye.lat, 2)}-{Math.Round(eye.lon, 2)}.json";
                string cache = System.IO.Path.Combine(Application.temporaryCachePath, "fb-cache", key);
                string json = null;
                if (System.IO.File.Exists(cache) && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(30))
                    json = System.IO.File.ReadAllText(cache);
                foreach (var ep in Endpoints)
                {
                    if (json != null) break;
                    json = await Net.PostForm(ep, "data=" + Uri.EscapeDataString(q), 90);
                    if (json != null && !json.TrimStart().StartsWith("{")) json = null;
                }
                if (json == null || token != _token) return;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cache));
                System.IO.File.WriteAllText(cache, json);
                var rings = await Task.Run(() => Parse(json));
                if (token != _token) return;
                await Build(rings, token);
                _loadedAt = eye;
                _loaded = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[water] 水域載入失敗：" + e.Message);
                _loaded = true;
                _loadedAt = eye;
            }
            finally
            {
                if (token == _token) _loading = false;
            }
        }

        static List<(long id, List<LatLon> ring)> Parse(string json)
        {
            var r = JsonUtility.FromJson<OResponse>(json);
            var list = new List<(long, List<LatLon>)>();
            foreach (var el in r.elements ?? Array.Empty<OElement>())
            {
                if (el.type == "way" && el.geometry != null && el.geometry.Length >= 4)
                    list.Add((el.id, el.geometry.Select(g => new LatLon(g.lat, g.lon)).ToList()));
                else if (el.type == "relation" && el.members != null)
                {
                    var ways = el.members.Where(m => m.role != "inner" && m.geometry != null && m.geometry.Length >= 2)
                        .Select(m => m.geometry.Select(g => new LatLon(g.lat, g.lon)).ToList()).ToList();
                    foreach (var ring in Assemble(ways)) list.Add((el.id, ring));
                }
            }
            return list;
        }

        static bool Same(LatLon a, LatLon b) => Math.Abs(a.lat - b.lat) < 1e-9 && Math.Abs(a.lon - b.lon) < 1e-9;

        static List<List<LatLon>> Assemble(List<List<LatLon>> ways)
        {
            var pool = new List<List<LatLon>>(ways);
            var rings = new List<List<LatLon>>();
            while (pool.Count > 0)
            {
                var ring = new List<LatLon>(pool[0]);
                pool.RemoveAt(0);
                for (int guard = 0; guard < 5000 && !Same(ring[0], ring[^1]); guard++)
                {
                    var end = ring[^1];
                    int i = pool.FindIndex(w => Same(w[0], end) || Same(w[^1], end));
                    if (i < 0) break;
                    var w = pool[i];
                    pool.RemoveAt(i);
                    ring.AddRange(Same(w[0], end) ? w.Skip(1) : Enumerable.Reverse(w).Skip(1));
                }
                if (ring.Count >= 4 && Same(ring[0], ring[^1])) rings.Add(ring);
            }
            return rings;
        }

        // ---- 建模 ----

        async Task Build(List<(long id, List<LatLon> ring)> rings, int token)
        {
            foreach (var b in _bodies) if (b.go) UnityEngine.Object.Destroy(b.go);
            _bodies.Clear();
            foreach (var (id, ringRaw) in rings)
            {
                var ring = new List<LatLon>(ringRaw);
                if (Same(ring[0], ring[^1])) ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;
                // 太小的池塘略過（< 300 m²）
                var local = ring.Select(p => Geo.ToLocal(p)).Select(l => new Vector2((float)l.x, -(float)l.y)).ToList();
                float area = 0;
                for (int i = 0, j = local.Count - 1; i < local.Count; j = i++) area += local[j].x * local[i].y - local[i].x * local[j].y;
                area *= 0.5f;
                if (Mathf.Abs(area) < 300) continue;
                if (ring.Count > 200) Debug.Log($"[water] 大水域 {id}：{ring.Count} 點、{Mathf.Abs(area) / 1e6f:0.00} km²");
                if (area < 0)
                {
                    local.Reverse();
                    ring.Reverse();
                }
                // 大湖（上千個頂點）先簡化再於背景執行緒三角化；失敗時退回湖面高度的外框平面（湖岸地形較高，自然遮住）
                var simple = Simplify(local, 3f);
                var (idx, covered) = await Task.Run(() =>
                {
                    var t = Triangulate(simple);
                    float triArea = 0;
                    for (int i = 0; i + 2 < t.Count; i += 3)
                    {
                        Vector2 a = simple[t[i]], b = simple[t[i + 1]], c2 = simple[t[i + 2]];
                        triArea += Mathf.Abs((b.x - a.x) * (c2.y - a.y) - (b.y - a.y) * (c2.x - a.x)) * 0.5f;
                    }
                    return (t, triArea / Mathf.Max(1, Mathf.Abs(area)));
                });
                if (token != _token) return;
                // 水面高度：取湖內各點的 DEM；部分湖泊的 DEM 含湖底地形（中央較低），取高百分位數＝湖面，再略為抬高蓋過 DEM
                var samples = new List<LatLon>();
                if (covered > 0.9f)
                {
                    int triCount = idx.Count / 3, stride = Math.Max(1, triCount / 40);
                    for (int t = 0; t < triCount; t += stride)
                    {
                        var ctr = (simple[idx[t * 3]] + simple[idx[t * 3 + 1]] + simple[idx[t * 3 + 2]]) / 3f;
                        samples.Add(Geo.ToLatLon(ctr.x, -ctr.y));
                    }
                }
                else
                {
                    int step = Math.Max(1, ring.Count / 24);
                    for (int i = 0; i < ring.Count; i += step) samples.Add(ring[i]);
                }
                float[] h;
                try
                {
                    h = await Dem.Sample(samples, 13);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[water] {id} 高程取樣失敗：{e.Message}");
                    continue;
                }
                if (token != _token) return;
                Array.Sort(h);
                // DEM 湖面常有雜訊或湖底地形：取中位數，DEM 地形在水域內另外壓低（WorldController.ApplyWaterToTerrain）
                float ele = covered > 0.9f ? h[h.Length / 2] : h[h.Length / 3];
                var c = new LatLon(ring.Average(p => p.lat), ring.Average(p => p.lon));
                var cl = Geo.ToLocal(c);
                float y = _world.EleToY(ele, cl.x, cl.y) + 0.5f;
                var mesh = covered > 0.9f ? BuildMesh(simple, idx, y) : BuildQuad(simple, y);
                if (mesh == null)
                {
                    Debug.LogWarning($"[water] {id} 建模失敗（{simple.Count} 點，覆蓋 {covered:P0}）");
                    continue;
                }
                if (ring.Count > 200) Debug.Log($"[water] {id} 簡化為 {simple.Count} 點，三角化覆蓋 {covered:P0}，水面 y={y:0.0}");
                var go = new GameObject("Water " + id);
                go.transform.SetParent(_parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _world.assets.water;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                _bodies.Add(new Body { id = id, ring = ring, center = c, y = y, go = go, bounds = mesh.bounds });
            }
            _world.ApplyWaterToTerrain();
            _world.version++;
            Debug.Log($"[water] {rings.Count} 個水域環，建立 {_bodies.Count} 個水面：" + string.Join("、", _bodies.Take(5).Select(b => $"{b.id}@{b.y:0.0}m ({b.bounds.size.x:0}×{b.bounds.size.z:0})")));
        }

        /// <summary>Douglas–Peucker 簡化（公尺）</summary>
        static List<Vector2> Simplify(List<Vector2> pts, float tol)
        {
            if (pts.Count < 16) return pts;
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                float best = 0;
                int bi = -1;
                var d = pts[b] - pts[a];
                float len = Mathf.Max(1e-3f, d.magnitude);
                for (int i = a + 1; i < b; i++)
                {
                    var v = pts[i] - pts[a];
                    float dist = Mathf.Abs(d.x * v.y - d.y * v.x) / len;
                    if (dist > best)
                    {
                        best = dist;
                        bi = i;
                    }
                }
                if (bi >= 0 && best > tol)
                {
                    keep[bi] = true;
                    stack.Push((a, bi));
                    stack.Push((bi, b));
                }
            }
            var result = new List<Vector2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
            return result.Count >= 3 ? result : pts;
        }

        static Mesh BuildQuad(List<Vector2> ring, float y)
        {
            float x0 = ring.Min(p => p.x), x1 = ring.Max(p => p.x), z0 = ring.Min(p => p.y), z1 = ring.Max(p => p.y);
            var quad = new List<Vector2> { new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1) };
            return BuildMesh(quad, new List<int> { 0, 1, 2, 0, 2, 3 }, y);
        }

        static Mesh BuildMesh(List<Vector2> ring, List<int> idx, float y)
        {
            if (idx.Count < 3) return null;
            var v = ring.Select(p => new Vector3(p.x, y, p.y)).ToList();
            var uv = ring.Select(p => p / 24f).ToList();
            var n = Enumerable.Repeat(Vector3.up, v.Count).ToList();
            var tris = new List<int>(idx.Count);
            // 逆時針環：反轉成 Unity 從上方看的正面
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                tris.Add(idx[i]);
                tris.Add(idx[i + 2]);
                tris.Add(idx[i + 1]);
            }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static List<int> Triangulate(List<Vector2> p)
        {
            var result = new List<int>();
            var idx = Enumerable.Range(0, p.Count).ToList();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 20000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Vector2 a = p[ia], b = p[ib], c = p[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-6f) continue;
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        var q = p[j];
                        float d1 = (q.x - b.x) * (a.y - b.y) - (a.x - b.x) * (q.y - b.y);
                        float d2 = (q.x - c.x) * (b.y - c.y) - (b.x - c.x) * (q.y - c.y);
                        float d3 = (q.x - a.x) * (c.y - a.y) - (c.x - a.x) * (q.y - a.y);
                        if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)))
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
                if (!clipped) break;
            }
            if (idx.Count == 3) result.AddRange(idx);
            return result;
        }

        // ---- 倒影：平面反射探針 ----

        void UpdateProbe(Vector3 eye)
        {
            Body near = null;
            float best = 2500;
            foreach (var b in _bodies)
            {
                float d = Vector2.Distance(new Vector2(eye.x, eye.z), new Vector2(Mathf.Clamp(eye.x, b.bounds.min.x, b.bounds.max.x), Mathf.Clamp(eye.z, b.bounds.min.z, b.bounds.max.z)));
                if (d < best && eye.y > b.y)
                {
                    best = d;
                    near = b;
                }
            }
            bool want = near != null && _quality >= Quality.High && !NoProbe;
            if (!want)
            {
                if (_probe) _probe.gameObject.SetActive(false);
                return;
            }
            if (_probe == null)
            {
                var go = new GameObject("Water Reflection");
                go.transform.SetParent(_parent, false);
                _probe = go.AddComponent<PlanarReflectionProbe>();
                _probe.mode = ProbeSettings.Mode.Realtime;
                _probe.realtimeMode = ProbeSettings.RealtimeMode.EveryFrame;
                _probe.influenceVolume.shape = InfluenceShape.Box;
            }
            _probe.gameObject.SetActive(true);
            var bb = near.bounds;
            // 探針放在水面上、相機附近；影響範圍涵蓋整個水域（高度只取水面附近）
            _probe.transform.position = new Vector3(Mathf.Clamp(eye.x, bb.min.x, bb.max.x), near.y, Mathf.Clamp(eye.z, bb.min.z, bb.max.z));
            _probe.transform.rotation = Quaternion.identity;
            var size = new Vector3(bb.size.x * 2 + 50, 4, bb.size.z * 2 + 50);
            _probe.influenceVolume.boxSize = size;
        }
    }
}
