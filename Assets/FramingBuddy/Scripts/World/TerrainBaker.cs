using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace FramingBuddy
{
    /// <summary>規則格網高程（列優先：第 0 列在北／−z 側）。座標為相對某錨點的（東、北）公尺。</summary>
    public class HeightGrid
    {
        public int n;
        public double step;
        /// <summary>西北角（東、北）</summary>
        public double west, north;
        public float[] h;

        public double East(int i) => west + i * step;
        public double North(int j) => north - j * step;
        public double Size => (n - 1) * step;

        public float Sample(double east, double northC)
        {
            double fx = (east - west) / step;
            double fy = (north - northC) / step;
            fx = Math.Clamp(fx, 0, n - 1.001);
            fy = Math.Clamp(fy, 0, n - 1.001);
            int i = (int)fx, j = (int)fy;
            float tx = (float)(fx - i), ty = (float)(fy - j);
            float a = h[j * n + i], b = h[j * n + i + 1], c = h[(j + 1) * n + i], d = h[(j + 1) * n + i + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        /// <summary>以錨點為中心、半寬 half 的正方形格網，從 DEM 取樣</summary>
        public static async Task<HeightGrid> Fetch(LatLon anchor, double half, double step, int zoom)
        {
            int n = (int)Math.Round(2 * half / step) + 1;
            var pts = new List<LatLon>(n * n);
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    pts.Add(Geo.Offset(anchor, -half + i * step, half - j * step));
            var h = await Dem.Sample(pts, zoom);
            return new HeightGrid { n = n, step = step, west = -half, north = half, h = h };
        }
    }

    /// <summary>
    /// 由高程格網產生網格與烘焙貼圖（顏色＋法線），供遠山精細山體與無 Google 時的地形使用。
    /// 顏色依海拔、坡度、季節雪線與林線決定，並加上多層雜訊；法線圖取自較細的格網，保留沖蝕溝等細節。
    /// </summary>
    public static class TerrainBaker
    {
        public struct Palette
        {
            public float snowLine;
            public float treeLine;
        }

        // 執行緒安全的值雜訊（Parallel.For 內不呼叫 Unity API）
        static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                return (h ^ (h >> 16)) / 4294967295f;
            }
        }

        static float Noise(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return (a + (b - a) * ux) + ((c + (d - c) * ux) - (a + (b - a) * ux)) * uy;
        }

        /// <summary>GLSL 的 smoothstep(e0, e1, x)（注意 Mathf.SmoothStep 語意不同）</summary>
        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        static float Fbm(float x, float y, int oct = 5)
        {
            float s = 0, a = 0.5f;
            for (int i = 0; i < oct; i++)
            {
                s += a * Noise(x, y);
                x = x * 2.03f + 17.1f;
                y = y * 2.03f + 9.3f;
                a *= 0.5f;
            }
            return s;
        }

        /// <summary>
        /// 產生網格：每 stride 個格點取一個頂點；yOf(east, north, ele) 回傳頂點高度（例如扣掉曲率、錨點高度）。
        /// keep(east, north) 為 false 的三角形略過；若 skirt &gt; 0 會沿外緣補一圈往下的裙邊。
        /// </summary>
        public static Mesh BuildMesh(HeightGrid g, int stride, Func<double, double, float, float> yOf, Func<double, double, bool> keep = null, float skirt = 0)
        {
            int m = (g.n - 1) / stride + 1;
            var verts = new List<Vector3>(m * m);
            var uvs = new List<Vector2>(m * m);
            for (int j = 0; j < m; j++)
                for (int i = 0; i < m; i++)
                {
                    int gi = Math.Min(g.n - 1, i * stride), gj = Math.Min(g.n - 1, j * stride);
                    double e = g.East(gi), no = g.North(gj);
                    float y = yOf(e, no, g.h[gj * g.n + gi]);
                    verts.Add(new Vector3((float)e, y, (float)no));
                    uvs.Add(new Vector2(gi / (float)(g.n - 1), 1f - gj / (float)(g.n - 1)));
                }
            var tris = new List<int>();
            for (int j = 0; j < m - 1; j++)
                for (int i = 0; i < m - 1; i++)
                {
                    int a = j * m + i, b = a + 1, c = a + m, d = c + 1;
                    if (keep != null)
                    {
                        var p = (verts[a] + verts[d]) * 0.5f;
                        if (!keep(p.x, p.z)) continue;
                    }
                    // Unity 為左手座標、從上方看順時針為正面：(a, b, c) 中 b 在東、c 在南
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            if (skirt > 0)
            {
                // 找出只被一個三角形使用的邊
                var count = new Dictionary<long, (int, int)>();
                for (int t = 0; t < tris.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int u = tris[t + k], v = tris[t + (k + 1) % 3];
                        long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                        if (count.ContainsKey(key)) count.Remove(key);
                        else count[key] = (u, v);
                    }
                var lowered = new Dictionary<int, int>();
                int Low(int v)
                {
                    if (lowered.TryGetValue(v, out int k)) return k;
                    k = verts.Count;
                    verts.Add(verts[v] - new Vector3(0, skirt, 0));
                    uvs.Add(uvs[v]);
                    lowered[v] = k;
                    return k;
                }
                foreach (var (u, v) in count.Values)
                {
                    int lu = Low(u), lv = Low(v);
                    tris.Add(u); tris.Add(v); tris.Add(lv);
                    tris.Add(u); tris.Add(lv); tris.Add(lu);
                }
            }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>烘焙顏色貼圖（sRGB）</summary>
        public static Texture2D BakeAlbedo(HeightGrid g, int size, Palette pal, Texture2D reuse = null)
        {
            var tex = reuse ?? new Texture2D(size, size, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, anisoLevel = 8 };
            var px = new Color32[size * size];
            double span = g.Size;
            Parallel.For(0, size, row =>
            {
                for (int col = 0; col < size; col++)
                {
                    double e = g.west + (col + 0.5) / size * span;
                    double no = g.north - (size - 1 - row + 0.5) / size * span;
                    float ele = g.Sample(e, no);
                    float dx = (g.Sample(e + g.step, no) - g.Sample(e - g.step, no)) / (float)(2 * g.step);
                    float dz = (g.Sample(e, no + g.step) - g.Sample(e, no - g.step)) / (float)(2 * g.step);
                    float slope = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz));
                    px[row * size + col] = Shade(ele, slope, (float)e, (float)no, pal);
                }
            });
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        static Color32 Shade(float ele, float slope, float e, float no, Palette pal)
        {
            if (ele <= 0.5f) return new Color32(38, 58, 70, 255); // 海面
            float n = Fbm(e / 180f, no / 180f);
            float n2 = Fbm(e / 35f + 50f, no / 35f + 50f, 3);
            var forest = Color.Lerp(new Color(0.12f, 0.19f, 0.1f), new Color(0.19f, 0.25f, 0.13f), n);
            var scrub = Color.Lerp(new Color(0.31f, 0.29f, 0.19f), new Color(0.39f, 0.35f, 0.23f), n2);
            var rock = Color.Lerp(new Color(0.33f, 0.22f, 0.17f), new Color(0.45f, 0.33f, 0.25f), n2);
            rock = Color.Lerp(rock, new Color(0.3f, 0.28f, 0.27f), Smooth(0.45f, 0.8f, n) * 0.6f);
            var field = Color.Lerp(new Color(0.3f, 0.34f, 0.22f), new Color(0.42f, 0.4f, 0.3f), n2);
            float treeEdge = pal.treeLine + (n - 0.5f) * 260f;
            // 低地（< 400 m）偏農田色調、陡坡露岩
            var col = Color.Lerp(field, forest, Smooth(0, 1, (ele - 150f) / 400f));
            col = Color.Lerp(col, scrub, Smooth(treeEdge - 180f, treeEdge, ele) * Mathf.Clamp01((ele - treeEdge + 180f) / 180f));
            col = Color.Lerp(col, rock, Mathf.Clamp01((ele - treeEdge) / 250f));
            col = Color.Lerp(col, rock, Smooth(0.55f, 0.95f, slope) * 0.7f);
            float snowEdge = pal.snowLine + (n - 0.5f) * 320f + (n2 - 0.5f) * 120f + slope * 260f;
            float snow = Mathf.Clamp01((ele - snowEdge + 40f) / 80f);
            col = Color.Lerp(col, new Color(0.93f, 0.94f, 0.97f), snow);
            return col;
        }

        /// <summary>烘焙切線空間法線圖（UV 的 u＝東、v＝北，與網格一致）</summary>
        public static Texture2D BakeNormal(HeightGrid g, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp, anisoLevel = 8 };
            var px = new Color32[size * size];
            double span = g.Size;
            double d = Math.Max(g.step, span / size);
            Parallel.For(0, size, row =>
            {
                for (int col = 0; col < size; col++)
                {
                    double e = g.west + (col + 0.5) / size * span;
                    double no = g.north - (size - 1 - row + 0.5) / size * span;
                    float dx = (g.Sample(e + d, no) - g.Sample(e - d, no)) / (float)(2 * d);
                    float dz = (g.Sample(e, no + d) - g.Sample(e, no - d)) / (float)(2 * d);
                    // 細部起伏：以雜訊擾動
                    float nx = (Fbm((float)e / 70f, (float)no / 70f, 3) - 0.5f) * 0.25f;
                    float nz = (Fbm((float)e / 70f + 31f, (float)no / 70f + 7f, 3) - 0.5f) * 0.25f;
                    var nrm = new Vector3(-dx + nx, -dz + nz, 1f).normalized;
                    px[row * size + col] = new Color32((byte)(nrm.x * 127.5f + 127.5f), (byte)(nrm.y * 127.5f + 127.5f), (byte)(nrm.z * 127.5f + 127.5f), 255);
                }
            });
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }
    }
}
