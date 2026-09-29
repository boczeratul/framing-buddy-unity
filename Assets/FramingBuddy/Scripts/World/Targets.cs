using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace FramingBuddy
{
    public enum TargetKind { Landmark, Building, Peak }

    /// <summary>可對準、可在資訊列追蹤遮擋情況的目標（位置以經緯度＋海拔／橢球高表示）。</summary>
    public class Target
    {
        public string id;
        public string label;
        public LatLon at;
        /// <summary>底部與頂部高度（相對場景原點高度，公尺；由 WorldController 換算）</summary>
        public float baseY, topY;
        public float aimAt = 0.5f;
        /// <summary>水平半徑：遮擋射線停在此距離之前，避免打到目標自己</summary>
        public float radius;
        public TargetKind kind;
    }

    /// <summary>
    /// 山峰：OpenFreeMap 向量圖磚（z8）的 mountain_peak 圖層，取地平線內最醒目的 12 座。
    /// </summary>
    public class PeakService
    {
        public class Peak
        {
            public string name;
            public LatLon at;
            public double ele;
            public bool volcano;
            public int rank;
        }

        public List<Peak> list = new();
        public int version;
        int _token;

        static string _template;

        public static async Task<string> TileTemplate()
        {
            if (_template != null) return _template;
            var json = await Net.GetText("https://tiles.openfreemap.org/planet");
            if (json == null) return null;
            var m = Regex.Match(json, "\"tiles\"\\s*:\\s*\\[\\s*\"([^\"]+)\"");
            _template = m.Success ? m.Groups[1].Value : null;
            return _template;
        }

        public async Task Load(LatLon center, double radius, double originEle)
        {
            int token = ++_token;
            list = new List<Peak>();
            version++;
            try
            {
                string tpl = await TileTemplate();
                if (tpl == null) return;
                const int z = 8;
                var found = new List<Peak>();
                var tiles = TilesAround(center, radius, z);
                var tasks = tiles.Select(async t =>
                {
                    string url = tpl.Replace("{z}", z.ToString()).Replace("{x}", t.x.ToString()).Replace("{y}", t.y.ToString());
                    var bytes = await Net.GetBytesCached(url, $"mvt-{z}-{t.x}-{t.y}.pbf", TimeSpan.FromDays(14));
                    if (bytes == null) return;
                    var pts = Mvt.DecodePoints(bytes, "mountain_peak", out int extent);
                    foreach (var f in pts)
                    {
                        string name = Str(f.props, "name:zh-Hant") ?? Str(f.props, "name") ?? Str(f.props, "name:zh");
                        if (string.IsNullOrEmpty(name) || !f.props.TryGetValue("ele", out var e) || !(e is double ele)) continue;
                        double n = Math.Pow(2, z);
                        double lon = (t.x + f.x / (double)extent) / n * 360 - 180;
                        double yy = (t.y + f.y / (double)extent) / n;
                        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * yy))) / Geo.Deg;
                        var at = new LatLon(lat, lon);
                        if (Geo.Distance(center, at) > radius) continue;
                        lock (found)
                            found.Add(new Peak
                            {
                                name = name, at = at, ele = ele, volcano = Str(f.props, "class") == "volcano",
                                rank = f.props.TryGetValue("rank", out var r) && r is double rd ? (int)rd : 9,
                            });
                    }
                });
                await Task.WhenAll(tasks);
                if (token != _token) return;
                double Score(Peak p) => (p.ele - originEle) / Math.Max(2000, Geo.Distance(center, p.at)) * (p.volcano ? 1.5 : 1) / Math.Sqrt(p.rank);
                var kept = new List<Peak>();
                foreach (var p in found.OrderByDescending(Score))
                {
                    if (p.ele - originEle < 150) continue;
                    if (kept.Any(k => k.name == p.name || Geo.Distance(k.at, p.at) < 1500)) continue;
                    kept.Add(p);
                    if (kept.Count >= 12) break;
                }
                list = kept;
                version++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[peaks] 山峰資料取得失敗: " + e.Message);
            }
        }

        static string Str(Dictionary<string, object> p, string k) => p.TryGetValue(k, out var v) ? v as string : null;

        public static List<(int x, int y)> TilesAround(LatLon p, double radius, int z)
        {
            double dLat = radius / 111000.0;
            double dLon = radius / (111000.0 * Math.Max(0.05, Math.Cos(p.lat * Geo.Deg)));
            var a = Dem.Pixel(p.lat + dLat, p.lon - dLon, z);
            var b = Dem.Pixel(p.lat - dLat, p.lon + dLon, z);
            var list = new List<(int, int)>();
            for (int x = (int)(a.x / 256); x <= (int)(b.x / 256); x++)
                for (int y = (int)(a.y / 256); y <= (int)(b.y / 256); y++)
                    list.Add((x, y));
            return list;
        }
    }

    /// <summary>範圍內有名稱的高樓（≥150 m）與高塔（≥100 m），來自 OSM（Overpass）。</summary>
    public class NamedTargetService
    {
        public List<Target> list = new();
        public int version;
        int _token;

        static readonly string[] Endpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        };

        public async Task Load(LatLon center, double radius, Func<LatLon, bool> excluded)
        {
            int token = ++_token;
            list = new List<Target>();
            version++;
            var sw = Geo.Offset(center, -radius, -radius);
            var ne = Geo.Offset(center, radius, radius);
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3:F4}", sw.lat, sw.lon, ne.lat, ne.lon);
            string q = "[out:json][timeout:60][bbox:" + bbox + "];(nwr[\"name\"][\"building\"](if: number(t[\"height\"]) >= 150);" +
                       "nwr[\"name\"][\"man_made\"~\"^(tower|mast)$\"](if: number(t[\"height\"]) >= 100););out tags bb;";
            string json = null;
            foreach (var ep in Endpoints)
            {
                json = await Net.PostForm(ep, "data=" + Uri.EscapeDataString(q));
                if (json != null) break;
            }
            if (json == null || token != _token) return;
            // JsonUtility 不支援含冒號的鍵：先把中文名稱欄位改名
            json = json.Replace("\"name:zh-Hant\"", "\"nameZhHant\"").Replace("\"name:zh\"", "\"nameZh\"");
            var resp = JsonUtility.FromJson<OverpassResponse>(json);
            var seen = new HashSet<string>();
            foreach (var el in resp.elements ?? Array.Empty<OverpassElement>())
            {
                var tags = el.tags;
                if (tags == null) continue;
                string label = !string.IsNullOrEmpty(tags.nameZhHant) ? tags.nameZhHant : !string.IsNullOrEmpty(tags.nameZh) ? tags.nameZh : tags.name;
                if (string.IsNullOrEmpty(label) || seen.Contains(label)) continue;
                if (!float.TryParse(Regex.Match(tags.height ?? "", @"[\d.]+").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float h)) continue;
                var b = el.bounds;
                var at = b != null && b.maxlat != 0 ? new LatLon((b.minlat + b.maxlat) / 2, (b.minlon + b.maxlon) / 2) : new LatLon(el.lat, el.lon);
                if (Geo.Distance(center, at) > radius || excluded(at)) continue;
                seen.Add(label);
                float r = b != null && b.maxlat != 0 ? (float)Geo.Distance(new LatLon(b.minlat, b.minlon), new LatLon(b.maxlat, b.maxlon)) / 2 : 20;
                list.Add(new Target { id = $"osm-{el.type}/{el.id}", label = label, at = at, topY = h, radius = r, kind = TargetKind.Building });
            }
            version++;
        }

        [Serializable] class OverpassResponse { public OverpassElement[] elements; }
        [Serializable] class OverpassElement { public string type; public long id; public double lat, lon; public Bounds bounds; public Tags tags; }
        [Serializable] class Bounds { public double minlat, minlon, maxlat, maxlon; }

        [Serializable]
        class Tags
        {
            public string name;
            public string height;
            // JsonUtility 不支援含冒號的鍵；中文名稱在前處理時改名
            public string nameZhHant;
            public string nameZh;
        }
    }
}
