using System;
using System.Collections.Generic;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 自建精細模型（由網頁版匯出的 GLB）的地理資料：錨點、Google 模型挖空範圍、目標、快速位置。
    /// 數值與網頁版 src/scene/*.ts 相同。
    /// </summary>
    public static class SiteData
    {
        public class LandmarkDef
        {
            public string id;
            public string name;
            public LatLon anchor;
            public float radius;
            public List<LatLon> exclusion;
            public List<Target> targets;
        }

        public class Preset
        {
            public string group;
            public string name;
            public LatLon at;
            public float height = 1.6f;
            public bool snap = true;
            public string aim;
            public Action<ShotState> apply;
        }

        // ---- 中正紀念堂：園區座標 (u 沿主軸指向紀念堂後方 118.3°，v 指向右側 208.3°) ----

        public static readonly LatLon CksAnchor = new LatLon(25.034638, 121.521821);
        const double CksAxis = 118.3;
        static readonly double CksCos = Math.Cos((CksAxis - 90) * Geo.Deg), CksSin = Math.Sin((CksAxis - 90) * Geo.Deg);

        public static LatLon CksSite(double u, double v)
        {
            double x = u * CksCos - v * CksSin; // 東
            double z = u * CksSin + v * CksCos; // 南
            return Geo.Offset(CksAnchor, x, -z);
        }

        static readonly double[,] CksPark =
        {
            { -466, 179.7 }, { -72.8, 173.4 }, { 153.2, 175.4 }, { 155.5, -175.6 }, { -537.9, -165.7 }, { -550.6, -157.1 },
            { -553.8, -145.8 }, { -537.7, -78.2 }, { -525.2, -64.9 }, { -492, -50.3 }, { -491.8, 49.8 }, { -500.5, 56.6 },
        };

        // ---- 台北 101 ----

        public static readonly LatLon T101Anchor = new LatLon(25.033668, 121.56481);
        static readonly double[,] T101Footprint = { { -106, -40 }, { 40, -40 }, { 40, 106 }, { -106, 106 } };

        // ---- 哈爾格林姆教堂：a 沿主軸指向正面（328.5°），c 指向東北側 ----

        public static readonly LatLon HallgrimsAnchor = new LatLon(64.141941, -21.926912);
        const double HgBearing = 328.5;

        public static LatLon HgSite(double a, double c)
        {
            double sa = Math.Sin(HgBearing * Geo.Deg), ca = Math.Cos(HgBearing * Geo.Deg);
            double sc = Math.Sin((HgBearing + 90) * Geo.Deg), cc = Math.Cos((HgBearing + 90) * Geo.Deg);
            return Geo.Offset(HallgrimsAnchor, a * sa + c * sc, a * ca + c * cc);
        }

        static readonly double[,] HgMask = { { -64, -44 }, { -64, 44 }, { 40, 46 }, { 88, 46 }, { 92, 0 }, { 88, -46 }, { 40, -46 } };

        public static readonly List<LandmarkDef> Landmarks = BuildLandmarks();

        static List<LandmarkDef> BuildLandmarks()
        {
            var cks = new List<LatLon>();
            for (int i = 0; i < CksPark.GetLength(0); i++) cks.Add(CksSite(CksPark[i, 0], CksPark[i, 1]));
            var t101 = new List<LatLon>();
            for (int i = 0; i < T101Footprint.GetLength(0); i++) t101.Add(Geo.Offset(T101Anchor, T101Footprint[i, 0], T101Footprint[i, 1]));
            var hg = new List<LatLon>();
            for (int i = 0; i < HgMask.GetLength(0); i++) hg.Add(HgSite(HgMask[i, 0], HgMask[i, 1]));
            return new List<LandmarkDef>
            {
                new LandmarkDef
                {
                    id = "cks", name = "中正紀念堂", anchor = CksAnchor, radius = 600, exclusion = cks,
                    targets = new List<Target> { new Target { id = "cks-hall", label = "中正紀念堂", at = CksAnchor, topY = 70, aimAt = 0.55f, radius = 90, kind = TargetKind.Landmark } },
                },
                new LandmarkDef
                {
                    id = "taipei101", name = "台北 101", anchor = T101Anchor, radius = 160, exclusion = t101,
                    targets = new List<Target> { new Target { id = "taipei101", label = "台北 101", at = T101Anchor, topY = 508, aimAt = 0.5f, radius = 34, kind = TargetKind.Landmark } },
                },
                new LandmarkDef
                {
                    id = "hallgrimskirkja", name = "哈爾格林姆教堂", anchor = HallgrimsAnchor, radius = 120, exclusion = hg,
                    targets = new List<Target> { new Target { id = "hallgrimskirkja", label = "哈爾格林姆教堂", at = HallgrimsAnchor, topY = 74.5f, aimAt = 0.55f, radius = 30, kind = TargetKind.Landmark } },
                },
            };
        }

        // ---- 快速位置 ----

        public static readonly List<Preset> Presets = BuildPresets();

        static Preset Cks(string name, double u, double v, string aim = null, Action<ShotState> apply = null, float h = 1.6f, bool snap = true) =>
            new Preset { group = "中正紀念堂", name = name, at = CksSite(u, v), aim = aim, apply = apply, height = h, snap = snap };

        static Preset Hg(string name, double a, double c, string aim, Action<ShotState> apply, float h = 1.6f) =>
            new Preset { group = "冰島・哈爾格林姆教堂", name = name, at = HgSite(a, c), aim = aim, apply = apply, height = h };

        static Preset Fuji(string name, double lat, double lon, Action<ShotState> apply, float h = 1.6f) =>
            new Preset { group = "日本・富士山周邊", name = name, at = new LatLon(lat, lon), aim = "peak:富士山", apply = apply, height = h };

        static Preset World(string name, double lat, double lon, string aim, Action<ShotState> apply) =>
            new Preset { group = "世界拍攝點", name = name, at = new LatLon(lat, lon), aim = aim, apply = apply };

        static List<Preset> BuildPresets()
        {
            const float ax = (float)CksAxis;
            return new List<Preset>
            {
                Cks("自由廣場牌樓下（望向紀念堂）", -462, 0, "cks-hall", s => s.focal = 50),
                Cks("自由廣場中央", -355, 0, "cks-hall", s => s.focal = 35),
                Cks("民主大道（紀念堂正面）", -180, 0, "cks-hall", s => s.focal = 24),
                Cks("紀念堂台階下", -96, 13, "cks-hall", s => s.focal = 16),
                Cks("紀念堂正門平台（望向牌樓）", -33, 0, null, s => { s.azimuth = ax + 180; s.pitch = -2; s.focal = 35; }),
                Cks("紀念堂後方高台（望向台北 101）", 33, 0, "taipei101", s => s.focal = 200),
                Cks("紀念堂東北角高台（101 × 屋簷）", 30, -30, "taipei101", s => s.focal = 70),
                Cks("國家戲劇院前（南側）", -355, 50, "cks-hall", s => s.focal = 35),
                Cks("國家音樂廳前（北側）", -355, -50, "cks-hall", s => s.focal = 35),
                Cks("光華池畔", -230, -96, "cks-hall", s => s.focal = 50),
                Cks("空拍：牌樓外 120 m 高", -600, 0, null, s => { s.azimuth = ax; s.pitch = -13; s.focal = 24; }, 120, false),

                Hg("教堂前鞦韆（網美角度：低角度直幅超廣角）", 71, 36.5, "hallgrimskirkja", s => { s.focal = 16; s.portrait = true; }, 0.9f),
                Hg("鞦韆後方（鞦韆＋教堂同框）", 74, 39, "hallgrimskirkja", s => { s.focal = 20; s.portrait = true; }, 1.5f),
                Hg("廣場中軸（萊夫像後方）", 72, 0, "hallgrimskirkja", s => s.focal = 24),
                Hg("教堂正門前", 16, 0, "hallgrimskirkja", s => { s.focal = 14; s.portrait = true; }, 1.4f),
                Hg("Skólavörðustígur 街上（望向教堂，約略位置）", 300, 0, "hallgrimskirkja", s => { s.focal = 85; s.portrait = true; }),

                Fuji("河口湖・大石公園（湖對岸的富士山）", 35.52329, 138.74654, s => s.focal = 35),
                Fuji("河口湖・產屋崎（湖面倒影）", 35.51312, 138.76598, s => s.focal = 24, 1.2f),
                Fuji("新倉山淺間公園・忠靈塔觀景台（五重塔＋富士山）", 35.50145, 138.80143, s => { s.focal = 50; s.portrait = true; }),
                Fuji("忍野八海（湧泉池與富士山）", 35.46024, 138.8327, s => s.focal = 35),
                Fuji("山中湖・長池親水公園", 35.42702, 138.87136, s => s.focal = 35),
                Fuji("山中湖・Panorama 台（俯瞰湖與富士山）", 35.41261, 138.90946, s => s.focal = 24),
                Fuji("本栖湖・中之倉峠（千圓鈔票構圖，約略位置）", 35.47568, 138.57299, s => s.focal = 35),

                World("台北・象山六巨石（望向台北 101）", 25.02745, 121.57635, "taipei101", s => s.focal = 35),
                World("巴黎・夏樂宮人權廣場（艾菲爾鐵塔）", 48.86185, 2.28875, null, s => { s.azimuth = 128; s.pitch = 8; s.focal = 35; }),
                World("東京・增上寺前廣場（東京鐵塔）", 35.65718, 139.74925, null, s => { s.azimuth = 292; s.pitch = 16; s.focal = 24; }),
                World("紐約・DUMBO 華盛頓街（曼哈頓橋）", 40.70323, -73.98968, null, s => { s.azimuth = 340; s.pitch = 3; s.focal = 50; }),
                World("香港・尖沙咀星光大道（維港天際線）", 22.2931, 114.174, null, s => { s.azimuth = 215; s.pitch = 3; s.focal = 24; }),
            };
        }

        public static bool PointInPolygon(LatLon p, List<LatLon> ring)
        {
            bool hit = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.lat > p.lat) != (b.lat > p.lat) && p.lon < (b.lon - a.lon) * (p.lat - a.lat) / (b.lat - a.lat) + a.lon) hit = !hit;
            }
            return hit;
        }

        // ---- 季節與緯度的地表估算（與網頁版 season.ts 相同） ----

        public static float SnowLine(int month, double lat)
        {
            double summer = Math.Max(600, 4800 - 55 * Math.Max(0, Math.Abs(lat) - 20));
            double winter = summer - 2500;
            double phase = lat >= 0 ? month - 1.5 : month - 7.5;
            double w = 0.5 + 0.5 * Math.Cos(phase * Math.PI / 6);
            return (float)(summer + (winter - summer) * w);
        }

        public static float TreeLine(double lat) => (float)Math.Max(300, 3700 - 70 * Math.Max(0, Math.Abs(lat) - 20));
    }
}
