using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Unity.Mathematics;

namespace FramingBuddy
{
    /// <summary>經緯度（度）。</summary>
    [Serializable]
    public struct LatLon
    {
        public double lat;
        public double lon;

        public LatLon(double lat, double lon)
        {
            this.lat = lat;
            this.lon = lon;
        }

        public override string ToString() => Format(6);

        public string Format(int digits) =>
            lat.ToString("F" + digits, CultureInfo.InvariantCulture) + ", " + lon.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 地理座標工具。場景座標與網頁版相同：原點＝選定地點，x 朝東、z 朝南（公尺）。
    /// Unity 端換成 Cesium 的「東－上－北」座標時，Unity Z ＝ −z。
    /// 20 km 內用局部切平面（依 WGS84 子午圈／卯酉圈曲率半徑），誤差遠小於模型精度。
    /// </summary>
    public static class Geo
    {
        public const double A = 6378137.0;
        public const double E2 = 0.00669437999014;
        public const double Deg = Math.PI / 180.0;

        public static readonly LatLon DefaultOrigin = new LatLon(25.034638, 121.521821);

        static LatLon _origin = DefaultOrigin;
        static double _mPerDegLat, _mPerDegLon;

        static Geo() => SetOrigin(DefaultOrigin);

        public static LatLon Origin => _origin;

        static void Scales(double lat, out double mLat, out double mLon)
        {
            double s = Math.Sin(lat * Deg);
            double w = 1 - E2 * s * s;
            double m = A * (1 - E2) / Math.Pow(w, 1.5);
            double n = A / Math.Sqrt(w);
            mLat = m * Deg;
            mLon = n * Math.Cos(lat * Deg) * Deg;
        }

        public static void SetOrigin(LatLon p)
        {
            _origin = p;
            Scales(p.lat, out _mPerDegLat, out _mPerDegLon);
        }

        /// <summary>經緯度 → 相對原點（x 東、z 南）</summary>
        public static double2 ToLocal(LatLon p) =>
            new double2((p.lon - _origin.lon) * _mPerDegLon, -(p.lat - _origin.lat) * _mPerDegLat);

        public static LatLon ToLatLon(double x, double z) =>
            new LatLon(_origin.lat - z / _mPerDegLat, _origin.lon + x / _mPerDegLon);

        public static LatLon Offset(LatLon p, double east, double north)
        {
            Scales(p.lat, out double mLat, out double mLon);
            return new LatLon(p.lat + north / mLat, p.lon + east / mLon);
        }

        public static double Distance(LatLon a, LatLon b)
        {
            const double R = 6371008.8;
            double dLat = (b.lat - a.lat) * Deg;
            double dLon = (b.lon - a.lon) * Deg;
            double h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(a.lat * Deg) * Math.Cos(b.lat * Deg) * Math.Pow(Math.Sin(dLon / 2), 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /// <summary>地球曲率造成的視線下沉（含大氣折射 k≈0.13）</summary>
        public static double CurvatureDrop(double d) => d * d / (2 * A) * (1 - 0.13);

        /// <summary>(x,z) 指向 (tx,tz) 的方位角，0°＝北、順時針（x 東、z 南）</summary>
        public static double Bearing(double x, double z, double tx, double tz)
        {
            double deg = Math.Atan2(tx - x, -(tz - z)) / Deg;
            return (deg + 360) % 360;
        }

        public static double BearingLL(LatLon a, LatLon b)
        {
            double e = (b.lon - a.lon) * Math.Cos(a.lat * Deg);
            double n = b.lat - a.lat;
            return (Math.Atan2(e, n) / Deg + 360) % 360;
        }

        static readonly string[] Compass =
        {
            "北", "北北東", "東北", "東北東", "東", "東南東", "東南", "南南東",
            "南", "南南西", "西南", "西南西", "西", "西北西", "西北", "北北西",
        };

        public static string CompassName(double az) =>
            Compass[(int)Math.Round((((az % 360) + 360) % 360) / 22.5) % 16];

        static readonly Regex LatLonRe = new Regex(@"^\s*(-?\d+(?:\.\d+)?)\s*[,，\s]\s*(-?\d+(?:\.\d+)?)\s*$");

        public static bool TryParse(string text, out LatLon p)
        {
            p = default;
            var m = LatLonRe.Match(text ?? "");
            if (!m.Success) return false;
            double lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(lat) > 85 || Math.Abs(lon) > 180) return false;
            p = new LatLon(lat, lon);
            return true;
        }

        /// <summary>WGS84 經緯高 → 地心地固（ECEF）</summary>
        public static double3 ToEcef(double lat, double lon, double h)
        {
            double sl = Math.Sin(lat * Deg), cl = Math.Cos(lat * Deg);
            double n = A / Math.Sqrt(1 - E2 * sl * sl);
            return new double3((n + h) * cl * Math.Cos(lon * Deg), (n + h) * cl * Math.Sin(lon * Deg), (n * (1 - E2) + h) * sl);
        }
    }
}
