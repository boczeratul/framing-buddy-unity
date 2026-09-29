using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 全球數值地形（AWS Terrain Tiles，Mapzen Terrarium 編碼，免金鑰）。
    /// 海拔 = R×256 + G + B/256 − 32768（公尺，大地水準面高）。
    /// </summary>
    public static class Dem
    {
        const string Url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        static readonly Dictionary<string, Task<float[]>> Tiles = new();

        static Task<float[]> Tile(int z, int x, int y)
        {
            string key = $"{z}/{x}/{y}";
            if (!Tiles.TryGetValue(key, out var t))
            {
                t = Load(z, x, y);
                Tiles[key] = t;
            }
            return t;
        }

        static async Task<float[]> Load(int z, int x, int y)
        {
            var bytes = await Net.GetBytesCached(string.Format(Url, z, x, y), $"dem-{z}-{x}-{y}.png", TimeSpan.FromDays(60));
            if (bytes == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(bytes)) return null;
            var px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            var h = new float[256 * 256];
            // Texture2D 的第 0 列是影像底部：翻轉成「第 0 列＝北」
            for (int j = 0; j < 256; j++)
                for (int i = 0; i < 256; i++)
                {
                    var c = px[(255 - j) * 256 + i];
                    h[j * 256 + i] = c.r * 256f + c.g + c.b / 256f - 32768f;
                }
            return h;
        }

        public static Vector2d Pixel(double lat, double lon, int z)
        {
            double n = 256.0 * Math.Pow(2, z);
            double r = lat * Geo.Deg;
            return new Vector2d((lon + 180) / 360 * n, (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
        }

        /// <summary>批次取樣：先下載涵蓋範圍內的圖磚，再逐點雙線性內插</summary>
        public static async Task<float[]> Sample(IList<LatLon> points, int z)
        {
            var px = new Vector2d[points.Count];
            var need = new Dictionary<string, (int x, int y)>();
            for (int i = 0; i < points.Count; i++)
            {
                px[i] = Pixel(points[i].lat, points[i].lon, z);
                int tx = (int)Math.Floor(px[i].x / 256), ty = (int)Math.Floor(px[i].y / 256);
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                        need[$"{tx + dx}/{ty + dy}"] = (tx + dx, ty + dy);
            }
            var loaded = new Dictionary<string, float[]>();
            var tasks = new List<Task>();
            foreach (var kv in need)
            {
                var k = kv.Key;
                var (x, y) = kv.Value;
                tasks.Add(Tile(z, x, y).ContinueWith(t => { lock (loaded) loaded[k] = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext()));
            }
            await Task.WhenAll(tasks);
            bool any = false;
            foreach (var v in loaded.Values) any |= v != null;
            if (!any) throw new Exception("DEM 圖磚無法取得");

            float At(long gx, long gy)
            {
                loaded.TryGetValue($"{Math.Floor(gx / 256.0)}/{Math.Floor(gy / 256.0)}", out var t);
                if (t == null) return 0;
                return t[(int)(((gy % 256) + 256) % 256) * 256 + (int)(((gx % 256) + 256) % 256)];
            }

            var out_ = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                double x = px[i].x - 0.5, y = px[i].y - 0.5;
                long x0 = (long)Math.Floor(x), y0 = (long)Math.Floor(y);
                float fx = (float)(x - x0), fy = (float)(y - y0);
                float v = Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
                out_[i] = Mathf.Max(v, -50);
            }
            return out_;
        }
    }

    public struct Vector2d
    {
        public double x, y;
        public Vector2d(double x, double y) { this.x = x; this.y = y; }
    }
}
