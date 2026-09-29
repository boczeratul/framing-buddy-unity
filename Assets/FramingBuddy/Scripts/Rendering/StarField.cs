using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 夜空：以赤道座標烘焙成立方體貼圖（+Y＝天球北極，赤經 0h 在 +Z），交給 Physically Based Sky 的 Space Emission，
    /// 再依當地恆星時與緯度旋轉。
    /// - 恆星：d3-celestial 的星表（亮於 6 等，約 5000 顆，含 B−V 色指數）；下載失敗時用隨機星點。
    /// - 銀河：以銀河座標建立的亮度模型（核心在人馬座、盤面塵埃帶、雜訊團塊）——對銀河拱橋、銀心位置的規劃足夠準確。
    /// 亮度為物理單位（nits），貼圖內存 ×1000 的值，由 spaceEmissionMultiplier 0.001 還原。
    /// </summary>
    public static class StarField
    {
        public const float StoredScale = 1000f;
        const string CatalogUrl = "https://cdn.jsdelivr.net/gh/ofrohn/d3-celestial@master/data/stars.6.json";

        [Serializable] class Props { public float mag; public string bv; }
        [Serializable] class Geom { public float[] coordinates; }
        [Serializable] class Feature { public Props properties; public Geom geometry; }
        [Serializable] class Collection { public List<Feature> features; }

        struct Star
        {
            public double ra, dec;
            public float mag, bv;
        }

        public static async Task<Cubemap> Build(int size)
        {
            var stars = new List<Star>();
            try
            {
                var bytes = await Net.GetBytesCached(CatalogUrl, "stars.6.json", TimeSpan.FromDays(365));
                if (bytes != null)
                {
                    var c = JsonUtility.FromJson<Collection>(System.Text.Encoding.UTF8.GetString(bytes));
                    foreach (var f in c.features)
                    {
                        if (f.geometry?.coordinates == null || f.geometry.coordinates.Length < 2) continue;
                        float.TryParse(f.properties.bv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bv);
                        stars.Add(new Star { ra = (f.geometry.coordinates[0] + 360) % 360, dec = f.geometry.coordinates[1], mag = f.properties.mag, bv = bv });
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[stars] 星表載入失敗：" + e.Message);
            }
            if (stars.Count < 100)
            {
                var rng = new System.Random(7);
                for (int i = 0; i < 5000; i++)
                {
                    double z = rng.NextDouble() * 2 - 1;
                    stars.Add(new Star
                    {
                        ra = rng.NextDouble() * 360, dec = Math.Asin(z) * 180 / Math.PI,
                        mag = (float)(6 - Math.Pow(rng.NextDouble(), 0.35) * 7.5), bv = (float)(rng.NextDouble() * 1.6 - 0.1),
                    });
                }
            }

            var faces = new Color[6][];
            await Task.Run(() =>
            {
                for (int f = 0; f < 6; f++) faces[f] = new Color[size * size];
                BakeMilkyWay(faces, size);
                foreach (var s in stars) Splat(faces, size, s);
            });

            var cube = new Cubemap(size, TextureFormat.RGBAHalf, true) { name = "Stars", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            for (int f = 0; f < 6; f++) cube.SetPixels(faces[f], (CubemapFace)f);
            cube.Apply(true, true);
            Debug.Log($"[stars] 星空貼圖：{stars.Count} 顆星，{size}²×6");
            return cube;
        }

        /// <summary>赤經赤緯 → 立方體貼圖空間方向（與 Celestial 的旋轉約定一致）</summary>
        static Vector3 Dir(double raDeg, double decDeg)
        {
            double a = raDeg * Math.PI / 180, d = decDeg * Math.PI / 180;
            return new Vector3((float)(-Math.Cos(d) * Math.Sin(a)), (float)Math.Sin(d), (float)(Math.Cos(d) * Math.Cos(a)));
        }

        /// <summary>立方體貼圖面 f 的像素 (x,y) → 方向（OpenGL 約定，y＝0 為上緣）</summary>
        static Vector3 FaceDir(int f, float s, float t)
        {
            float sc = s * 2 - 1, tc = t * 2 - 1;
            return f switch
            {
                0 => new Vector3(1, -tc, -sc),
                1 => new Vector3(-1, -tc, sc),
                2 => new Vector3(sc, 1, tc),
                3 => new Vector3(sc, -1, -tc),
                4 => new Vector3(sc, -tc, 1),
                _ => new Vector3(-sc, -tc, -1),
            };
        }

        static void DirToFace(Vector3 d, out int f, out float s, out float t)
        {
            float ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            float sc, tc, ma;
            if (ax >= ay && ax >= az)
            {
                ma = ax;
                if (d.x > 0) { f = 0; sc = -d.z; tc = -d.y; }
                else { f = 1; sc = d.z; tc = -d.y; }
            }
            else if (ay >= az)
            {
                ma = ay;
                if (d.y > 0) { f = 2; sc = d.x; tc = d.z; }
                else { f = 3; sc = d.x; tc = -d.z; }
            }
            else
            {
                ma = az;
                if (d.z > 0) { f = 4; sc = d.x; tc = -d.y; }
                else { f = 5; sc = -d.x; tc = -d.y; }
            }
            s = (sc / ma + 1) * 0.5f;
            t = (tc / ma + 1) * 0.5f;
        }

        static Color StarColor(float bv)
        {
            bv = Mathf.Clamp(bv, -0.4f, 2f);
            float temp = Mathf.Clamp(4600f * (1f / (0.92f * bv + 1.7f) + 1f / (0.92f * bv + 0.62f)), 1500, 20000) / 100f;
            // 黑體色近似（Tanner Helland），在背景執行緒可用；結果為 sRGB，轉成線性
            float r = temp <= 66 ? 255 : 329.7f * Mathf.Pow(temp - 60, -0.1332f);
            float g = temp <= 66 ? 99.47f * Mathf.Log(temp) - 161.12f : 288.12f * Mathf.Pow(temp - 60, -0.0755f);
            float b = temp >= 66 ? 255 : temp <= 19 ? 0 : 138.52f * Mathf.Log(temp - 10) - 305.04f;
            float Lin(float v)
            {
                v = Mathf.Clamp01(v / 255f);
                return v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            }
            var c = new Color(Lin(r), Lin(g), Lin(b));
            float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return lum > 0 ? c / lum : Color.white;
        }

        static void Splat(Color[][] faces, int n, Star s)
        {
            // 星等 → 照度（lux）：0 等星約 2.54e-6 lux
            double lux = 2.54e-6 * Math.Pow(10, -0.4 * s.mag);
            DirToFace(Dir(s.ra, s.dec), out int f, out float u, out float v);
            float px = u * n - 0.5f, py = v * n - 0.5f;
            int x0 = Mathf.FloorToInt(px), y0 = Mathf.FloorToInt(py);
            float uc = u * 2 - 1, vc = v * 2 - 1;
            // 像素立體角
            double omega = (4.0 / (n * n)) / Math.Pow(1 + uc * uc + vc * vc, 1.5);
            var col = StarColor(s.bv);
            const float sigma = 0.6f;
            float wsum = 0;
            Span<float> w = stackalloc float[16];
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    float dx = x0 - 1 + i - px, dy = y0 - 1 + j - py;
                    w[j * 4 + i] = Mathf.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                    wsum += w[j * 4 + i];
                }
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    int x = x0 - 1 + i, y = y0 - 1 + j;
                    if (x < 0 || y < 0 || x >= n || y >= n) continue;
                    float L = (float)(lux * (w[j * 4 + i] / wsum) / omega) * StoredScale;
                    faces[f][y * n + x] += col * L;
                }
        }

        // J2000 赤道 → 銀河座標
        static readonly double[,] ToGal =
        {
            { -0.0548755604, -0.8734370902, -0.4838350155 },
            { 0.4941094279, -0.4448296300, 0.7469822445 },
            { -0.8676661490, -0.1980763734, 0.4559837762 },
        };

        static float Hash(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177;
                return (h ^ (h >> 16)) / 4294967295f;
            }
        }

        static float Noise3(Vector3 p)
        {
            int ix = Mathf.FloorToInt(p.x), iy = Mathf.FloorToInt(p.y), iz = Mathf.FloorToInt(p.z);
            float fx = p.x - ix, fy = p.y - iy, fz = p.z - iz;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            fz = fz * fz * (3 - 2 * fz);
            float L(float a, float b, float t) => a + (b - a) * t;
            return L(L(L(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), fx), L(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), fx), fy),
                L(L(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), fx), L(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), fx), fy), fz);
        }

        static float Fbm(Vector3 p)
        {
            float s = 0, a = 0.5f;
            for (int i = 0; i < 4; i++)
            {
                s += a * Noise3(p);
                p = p * 2.07f + new Vector3(3.1f, 1.7f, 5.3f);
                a *= 0.5f;
            }
            return s;
        }

        static void BakeMilkyWay(Color[][] faces, int n)
        {
            // 夜天光（大氣輝光＋黃道光的平均）約 21.8 mag/arcsec² ≈ 2e-4 nits
            var airglow = new Color(0.85f, 1f, 0.9f) * (1.6e-4f * StoredScale);
            var warm = new Color(1f, 0.93f, 0.82f);
            var cool = new Color(0.86f, 0.9f, 1f);
            System.Threading.Tasks.Parallel.For(0, 6 * n, row =>
            {
                int f = row / n, y = row % n;
                for (int x = 0; x < n; x++)
                {
                    var d = FaceDir(f, (x + 0.5f) / n, (y + 0.5f) / n).normalized;
                    // 立方體空間 → 赤道直角座標 (cosδcosα, cosδsinα, sinδ)
                    double ex = d.z, ey = -d.x, ez = d.y;
                    double gx = ToGal[0, 0] * ex + ToGal[0, 1] * ey + ToGal[0, 2] * ez;
                    double gy = ToGal[1, 0] * ex + ToGal[1, 1] * ey + ToGal[1, 2] * ez;
                    double gz = ToGal[2, 0] * ex + ToGal[2, 1] * ey + ToGal[2, 2] * ez;
                    float b = (float)(Math.Asin(Math.Clamp(gz, -1, 1)) * 180 / Math.PI);
                    float l = (float)(Math.Atan2(gy, gx) * 180 / Math.PI); // −180..180，0＝銀心
                    float al = Mathf.Abs(l);
                    // 盤面厚度往外變薄；銀心附近隆起
                    float width = Mathf.Lerp(7.5f, 4.5f, al / 180f);
                    float disk = Mathf.Exp(-(b * b) / (2 * width * width)) * Mathf.Lerp(1f, 0.28f, Mathf.Pow(al / 180f, 0.7f));
                    float bulge = Mathf.Exp(-(l * l + b * b * 1.8f) / (2 * 11f * 11f)) * 1.6f;
                    var gp = new Vector3((float)gx, (float)gy, (float)gz);
                    float clump = Fbm(gp * 9f);
                    float fine = Fbm(gp * 38f + new Vector3(9, 4, 2));
                    float glow = (disk + bulge) * (0.45f + 0.9f * clump) * (0.8f + 0.4f * fine);
                    // 塵埃帶（大裂縫）：天鵝座到人馬座盤面中央偏暗
                    float lane = Mathf.Exp(-(b + 0.6f) * (b + 0.6f) / (2 * 1.6f * 1.6f)) * Mathf.Clamp01(1 - Mathf.Abs(l > 0 ? l - 40 : l + 10) / 90f);
                    glow *= 1 - 0.65f * lane * (0.5f + fine);
                    // 銀河最亮處（人馬座星雲）約 20 mag/arcsec² ≈ 1e-3 nits
                    float L = glow * 9e-4f * StoredScale;
                    var col = Color.Lerp(cool, warm, Mathf.Clamp01(bulge * 0.8f + 0.2f));
                    faces[f][y * n + x] = airglow + col * L;
                }
            });
        }
    }
}
