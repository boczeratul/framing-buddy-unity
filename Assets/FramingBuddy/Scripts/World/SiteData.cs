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

        // ---- 羅森堡宮：u 沿主樓長軸指向東南（129.6°），v 指向東北花園側 ----

        public static readonly LatLon RosenborgAnchor = new LatLon(55.685687, 12.577433);
        const double RbBearing = 129.6;

        public static LatLon RbSite(double u, double v)
        {
            double su = Math.Sin(RbBearing * Geo.Deg), cu = Math.Cos(RbBearing * Geo.Deg);
            double sv = Math.Sin((RbBearing - 90) * Geo.Deg), cv = Math.Cos((RbBearing - 90) * Geo.Deg);
            return Geo.Offset(RosenborgAnchor, u * su + v * sv, u * cu + v * cv);
        }

        static readonly double[,] RbMask = { { -28, 22 }, { -28, -26.5 }, { -23.5, -27.6 }, { -22.5, -56 }, { 62, -56 }, { 62, -6 }, { 65, -6 }, { 65, 9 }, { 61, 9 }, { 59, 22 } };

        // ---- 新港（哥本哈根）：a 沿運河指向港口（115°），c 指向北岸（25°） ----

        public static readonly LatLon NyhavnAnchor = new LatLon(55.6798, 12.5903);
        const double NyBearing = 115;

        public static LatLon NySite(double a, double c)
        {
            double sa = Math.Sin(NyBearing * Geo.Deg), ca = Math.Cos(NyBearing * Geo.Deg);
            double sc = Math.Sin((NyBearing - 90) * Geo.Deg), cc = Math.Cos((NyBearing - 90) * Geo.Deg);
            return Geo.Offset(NyhavnAnchor, a * sa + c * sc, a * ca + c * cc);
        }

        /// <summary>兩岸碼頭＋臨運河整排建物（OSM／Overture 輪廓），與網頁版 src/scene/nyhavn-data.ts 的 MASK 相同</summary>
        static readonly double[,] NyMask =
        {
            { -67.24, -43.96 }, { -81.84, -44.11 }, { -81.06, -20.43 }, { -79.9, -19.8 }, { -99.5, -19.8 }, { -99.5, -17.3 }, { -182, -17.3 },
            { -182, 15 }, { -192, 15 }, { -192, 38.5 }, { -190.66, 38.5 }, { -176.51, 54.7 }, { -166.9, 48.87 }, { -164.15, 51.9 },
            { -161.68, 50.46 }, { -164.26, 47.41 }, { -159.05, 47.23 }, { -158.22, 44.84 }, { -155.78, 49.11 }, { -149.41, 49.5 }, { -148, 51.46 },
            { -152.32, 54.26 }, { -147.08, 60.63 }, { -142.57, 57.56 }, { -140.35, 56.8 }, { -135.61, 53.97 }, { -136.23, 52.96 },
            { -135.38, 52.33 }, { -137.68, 49.49 }, { -137.14, 49.93 }, { -131.97, 49.54 }, { -132.19, 47.49 }, { -129.2, 47.51 },
            { -129.22, 45.72 }, { -129.02, 59.47 }, { -134.99, 64.49 }, { -132.9, 67.78 }, { -124.59, 61.73 }, { -125.16, 50.97 }, { -122, 50.75 },
            { -121.13, 49.13 }, { -117.54, 48.97 }, { -117.33, 54.15 }, { -113.64, 53.82 }, { -113.51, 52.05 }, { -110.9, 51.94 }, { -110.83, 47 },
            { -112.07, 47.01 }, { -105.48, 46.93 }, { -101.11, 47.1 }, { -100.16, 52.11 }, { -93.47, 51.61 }, { -96.51, 38.5 }, { -87.08, 38.5 },
            { -85.5, 46.98 }, { -80.75, 46.96 }, { -80.6, 49.64 }, { -77.3, 48.76 }, { -77.3, 47.49 }, { -76.16, 47.53 }, { -73.14, 46.23 },
            { -70.19, 53.28 }, { -62.18, 49.86 }, { -63.31, 47.11 }, { -59.5, 46.93 }, { -58.91, 48.88 }, { -56.05, 48.01 }, { -56.61, 45.62 },
            { -54.3, 45.36 }, { -52.45, 51.09 }, { -50.58, 51.01 }, { -53.33, 38.5 }, { -51.48, 46.91 }, { -44.43, 46.35 }, { -43.93, 49.27 },
            { -45.03, 49.53 }, { -44.85, 55.44 }, { -49.14, 56.41 }, { -47.9, 59.92 }, { -37.18, 57.46 }, { -37.48, 56.49 }, { -39.19, 55.96 },
            { -40.27, 38.5 }, { -39.59, 46.75 }, { -37.17, 46.57 }, { -36.65, 57.34 }, { -32.86, 56.48 }, { -29.29, 57.21 }, { -27.34, 57 },
            { -27.54, 52.76 }, { -24.85, 52.76 }, { -25.46, 38.5 }, { -24.77, 54.49 }, { -20.66, 54.27 }, { -20.27, 46.84 }, { -18.96, 46.92 },
            { -18.25, 53.38 }, { -11.6, 51.81 }, { -11.8, 46.53 }, { -7.33, 46.46 }, { -7.32, 47.03 }, { -1.56, 47.13 }, { -1.49, 50.97 },
            { -7.02, 50.97 }, { -6.91, 61.7 }, { 2.75, 60.65 }, { 2.04, 49.91 }, { 2.39, 54.15 }, { 10.76, 54.16 }, { 10.73, 50.5 },
            { 17.48, 50.33 }, { 17.57, 55.22 }, { 10.53, 55.44 }, { 10.77, 61.04 }, { 20.29, 59.67 }, { 20.42, 69.27 }, { 25.82, 68.98 },
            { 25.66, 49.76 }, { 30.94, 49.82 }, { 31.15, 38.5 }, { 30.97, 48.64 }, { 33.39, 48.36 }, { 33.84, 51.28 }, { 35.78, 51.27 },
            { 35.89, 59.75 }, { 35.35, 60.51 }, { 33, 60.65 }, { 33.23, 67.16 }, { 34.01, 71.96 }, { 37.88, 71.46 }, { 37.11, 66.72 },
            { 39.99, 65.93 }, { 39.73, 48.52 }, { 59.2, 46.44 }, { 59.31, 39.27 }, { 58.75, 38.5 }, { 71.02, 38.5 }, { 70.78, 66.1 },
            { 82.15, 65.25 }, { 82.39, 49.28 }, { 85.31, 49.24 }, { 85.24, 38.5 }, { 85.31, 50.18 }, { 87.74, 50.04 }, { 87.74, 61.16 },
            { 85.1, 61.23 }, { 85.05, 64.97 }, { 93.24, 64.51 }, { 92.54, 43.93 }, { 104.48, 43.42 }, { 104.8, 48.61 }, { 107.95, 48.48 },
            { 107.95, 46.15 }, { 108.98, 46.2 }, { 108.99, 53.7 }, { 111.25, 53.72 }, { 111.29, 56.16 }, { 114.98, 56.02 }, { 115.04, 46.88 },
            { 117.22, 46.66 }, { 117.22, 45.73 }, { 127.1, 45.61 }, { 127.2, 49.87 }, { 123.68, 50 }, { 123.9, 56.66 }, { 130.14, 55.66 },
            { 130.46, 44.05 }, { 142.9, 43.98 }, { 142.96, 45.94 }, { 155.01, 46.3 }, { 155.07, 49.73 }, { 150.07, 49.45 }, { 149.94, 48.4 },
            { 149.32, 47.92 }, { 143.92, 47.9 }, { 144.11, 54.66 }, { 182.84, 55.36 }, { 182.94, 38.5 }, { 194.82, 38.5 }, { 194.77, 50.82 },
            { 224.62, 51.01 }, { 224.67, 38.5 }, { 236, 38.5 }, { 236, -19.8 }, { 180.18, -19.8 }, { 182.14, -21.52 }, { 183.22, -24.44 },
            { 182.77, -66.38 }, { 164.24, -65.99 }, { 164.05, -60.78 }, { 169.86, -60.67 }, { 170.88, -59.66 }, { 171.42, -45.65 },
            { 162.3, -45.64 }, { 162.2, -36.85 }, { 170.88, -36.9 }, { 170.86, -31.06 }, { 151.04, -31.09 }, { 151.03, -19.8 }, { 151.04, -29.31 },
            { 124.04, -29.66 }, { 123.89, -19.8 }, { 124.01, -31.42 }, { 119.69, -31.32 }, { 119.67, -29.48 }, { 88.2, -29.47 }, { 88.23, -35.12 },
            { 89.31, -35.83 }, { 94.45, -35.74 }, { 94.55, -41.09 }, { 77.43, -41.09 }, { 77.21, -21.87 }, { 78.5, -19.8 }, { 59.19, -19.8 },
            { 59.26, -41.48 }, { 50.06, -41.5 }, { 49.86, -29.51 }, { 37.87, -29.54 }, { 37.79, -40.53 }, { 32.77, -40.55 }, { 32.37, -19.8 },
            { 32.59, -31.42 }, { 14.53, -31.97 }, { 14.32, -42.93 }, { 8.97, -42.82 }, { 8.87, -31.82 }, { -21.43, -31.77 }, { -21.39, -26.16 },
            { -33.06, -25.47 }, { -33.12, -41.44 }, { -38.15, -41.39 }, { -38.14, -31.63 }, { -51.33, -31.28 }, { -48.85, -34.4 },
            { -48.88, -43.52 }, { -53.76, -43.46 }, { -53.8, -37.16 }, { -54.44, -34.4 }, { -58.58, -34.3 }, { -58.49, -29.51 }, { -66.09, -29.31 },
            { -66.13, -30.47 }, { -69.46, -30.53 }, { -69.58, -40.06 }, { -69.11, -41.06 }, { -67.19, -41 }
        };

        public static readonly List<LandmarkDef> Landmarks = BuildLandmarks();

        static List<LandmarkDef> BuildLandmarks()
        {
            var cks = new List<LatLon>();
            for (int i = 0; i < CksPark.GetLength(0); i++) cks.Add(CksSite(CksPark[i, 0], CksPark[i, 1]));
            var t101 = new List<LatLon>();
            for (int i = 0; i < T101Footprint.GetLength(0); i++) t101.Add(Geo.Offset(T101Anchor, T101Footprint[i, 0], T101Footprint[i, 1]));
            var hg = new List<LatLon>();
            for (int i = 0; i < HgMask.GetLength(0); i++) hg.Add(HgSite(HgMask[i, 0], HgMask[i, 1]));
            var rb = new List<LatLon>();
            for (int i = 0; i < RbMask.GetLength(0); i++) rb.Add(RbSite(RbMask[i, 0], RbMask[i, 1]));
            var ny = new List<LatLon>();
            for (int i = 0; i < NyMask.GetLength(0); i++) ny.Add(NySite(NyMask[i, 0], NyMask[i, 1]));
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
                new LandmarkDef
                {
                    id = "rosenborg", name = "羅森堡宮", anchor = RosenborgAnchor, radius = 100, exclusion = rb,
                    targets = new List<Target> { new Target { id = "rosenborg", label = "羅森堡宮", at = RosenborgAnchor, topY = 50.6f, aimAt = 0.4f, radius = 26, kind = TargetKind.Landmark } },
                },
                new LandmarkDef
                {
                    id = "nyhavn", name = "新港", anchor = NyhavnAnchor, radius = 240, exclusion = ny,
                    targets = new List<Target> { new Target { id = "nyhavn", label = "新港北岸屋列", at = NySite(-40, 44), topY = 19, aimAt = 0.45f, radius = 12, kind = TargetKind.Landmark } },
                },
            };
        }

        // ---- 快速位置 ----

        public static readonly List<Preset> Presets = BuildPresets();

        static Preset Cks(string name, double u, double v, string aim = null, Action<ShotState> apply = null, float h = 1.6f, bool snap = true) =>
            new Preset { group = "中正紀念堂", name = name, at = CksSite(u, v), aim = aim, apply = apply, height = h, snap = snap };

        static Preset Hg(string name, double a, double c, string aim, Action<ShotState> apply, float h = 1.6f) =>
            new Preset { group = "冰島・哈爾格林姆教堂", name = name, at = HgSite(a, c), aim = aim, apply = apply, height = h };

        static Preset Rb(string name, double u, double v, Action<ShotState> apply, float h = 1.6f) =>
            new Preset { group = "丹麥・羅森堡宮", name = name, at = RbSite(u, v), aim = "rosenborg", apply = apply, height = h };

        static Preset Ny(string name, double a, double c, string aim, Action<ShotState> apply, float h = 1.6f) =>
            new Preset { group = "丹麥・新港（哥本哈根）", name = name, at = NySite(a, c), aim = aim, apply = apply, height = h };

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

                Rb("國王花園草坪（東南側，大塔＋東南山牆）", 95, -45, s => s.focal = 35),
                Rb("護城河對岸（西南，大塔倒影）", 2, -58, s => { s.focal = 16; s.portrait = true; }, 1.4f),
                Rb("格林橋上（臥獅與門柱入鏡）", 64, 1.6, s => s.focal = 24, 1.5f),
                Rb("前庭草坪（大塔正面）", 0, -24, s => { s.focal = 14; s.portrait = true; }),
                Rb("玫瑰園（東北立面三座塔）", 10, 72, s => s.focal = 35),

                Ny("新港橋上（沿運河望向北岸彩色屋）", 65.5, 13, null, s => { s.azimuth = 292; s.pitch = 2; s.focal = 24; }, 1.9f),
                Ny("南岸碼頭（隔運河拍北岸屋列與帆船）", -52, -8.5, "nyhavn", s => s.focal = 24),
                Ny("南岸 Nyhavn 18 前（望向新港橋）", 2, -9, null, s => { s.azimuth = 88; s.pitch = 3; s.focal = 35; }),
                Ny("北岸碼頭邊（沿運河往港口）", -112, 23.8, null, s => { s.azimuth = 115; s.pitch = 2; s.focal = 20; s.portrait = true; }),
                Ny("紀念錨旁（運河盡頭望向港口）", -178, 12, null, s => { s.azimuth = 115; s.pitch = 3; s.focal = 35; }),
                Ny("內港橋上（由港口望進新港，約略位置）", 262, 4, null, s => { s.azimuth = 295; s.pitch = 1; s.focal = 50; }),

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
