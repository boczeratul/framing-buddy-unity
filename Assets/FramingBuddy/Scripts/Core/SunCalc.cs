using System;
using System.Collections.Generic;

namespace FramingBuddy
{
    /// <summary>
    /// 太陽與月亮位置、日出日落等時刻、月相（移植自 suncalc，Vladimir Agafonkin，BSD-2）。
    /// 角度一律為度；方位角以北為 0°、順時針。
    /// </summary>
    public static class SunCalc
    {
        const double Rad = Math.PI / 180.0;
        const double DayMs = 86400000.0;
        const double J1970 = 2440588.0;
        const double J2000 = 2451545.0;
        const double E = Rad * 23.4397;
        const double J0 = 0.0009;

        static double ToJulian(DateTime utc) => (utc - DateTime.UnixEpoch).TotalMilliseconds / DayMs - 0.5 + J1970;
        static DateTime FromJulian(double j) => DateTime.UnixEpoch.AddMilliseconds((j + 0.5 - J1970) * DayMs);
        static double ToDays(DateTime utc) => ToJulian(utc) - J2000;

        static double RightAscension(double l, double b) => Math.Atan2(Math.Sin(l) * Math.Cos(E) - Math.Tan(b) * Math.Sin(E), Math.Cos(l));
        static double Declination(double l, double b) => Math.Asin(Math.Sin(b) * Math.Cos(E) + Math.Cos(b) * Math.Sin(E) * Math.Sin(l));
        static double AzimuthS(double h, double phi, double dec) => Math.Atan2(Math.Sin(h), Math.Cos(h) * Math.Sin(phi) - Math.Tan(dec) * Math.Cos(phi));
        static double Altitude(double h, double phi, double dec) => Math.Asin(Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Cos(h));
        static double SiderealTime(double d, double lw) => Rad * (280.16 + 360.9856235 * d) - lw;

        /// <summary>大氣折射修正（弧度）</summary>
        static double AstroRefraction(double h)
        {
            if (h < 0) h = 0;
            return 0.0002967 / Math.Tan(h + 0.00312536 / (h + 0.08901179));
        }

        static double SolarMeanAnomaly(double d) => Rad * (357.5291 + 0.98560028 * d);

        static double EclipticLongitude(double m)
        {
            double c = Rad * (1.9148 * Math.Sin(m) + 0.02 * Math.Sin(2 * m) + 0.0003 * Math.Sin(3 * m));
            double p = Rad * 102.9372;
            return m + c + p + Math.PI;
        }

        static void SunCoords(double d, out double dec, out double ra)
        {
            double m = SolarMeanAnomaly(d);
            double l = EclipticLongitude(m);
            dec = Declination(l, 0);
            ra = RightAscension(l, 0);
        }

        public struct Position
        {
            public double azimuth;
            public double altitude;
        }

        static double NorthAz(double southAz) => ((southAz / Rad + 180.0) % 360.0 + 360.0) % 360.0;

        public static Position SunPosition(DateTime utc, double lat, double lon)
        {
            double lw = Rad * -lon, phi = Rad * lat, d = ToDays(utc);
            SunCoords(d, out double dec, out double ra);
            double h = SiderealTime(d, lw) - ra;
            double alt = Altitude(h, phi, dec);
            return new Position { azimuth = NorthAz(AzimuthS(h, phi, dec)), altitude = (alt + AstroRefraction(alt)) / Rad };
        }

        /// <summary>地方恆星時（度），用來旋轉星空</summary>
        public static double LocalSiderealDeg(DateTime utc, double lon) =>
            ((SiderealTime(ToDays(utc), Rad * -lon) / Rad) % 360 + 360) % 360;

        // ---- 日出日落 ----

        static readonly (double angle, string rise, string set)[] Times =
        {
            (-0.833, "sunrise", "sunset"),
            (-0.3, "sunriseEnd", "sunsetStart"),
            (-6, "dawn", "dusk"),
            (-12, "nauticalDawn", "nauticalDusk"),
            (-18, "nightEnd", "night"),
            (6, "goldenHourEnd", "goldenHour"),
        };

        static double JulianCycle(double d, double lw) => Math.Round(d - J0 - lw / (2 * Math.PI));
        static double ApproxTransit(double ht, double lw, double n) => J0 + (ht + lw) / (2 * Math.PI) + n;
        static double SolarTransitJ(double ds, double m, double l) => J2000 + ds + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);
        static double HourAngle(double h, double phi, double d) => Math.Acos((Math.Sin(h) - Math.Sin(phi) * Math.Sin(d)) / (Math.Cos(phi) * Math.Cos(d)));
        static double ObserverAngle(double height) => -2.076 * Math.Sqrt(height) / 60;

        static double GetSetJ(double h, double lw, double phi, double dec, double n, double m, double l)
        {
            double w = HourAngle(h, phi, dec);
            double a = ApproxTransit(w, lw, n);
            return SolarTransitJ(a, m, l);
        }

        /// <summary>傳入日期（UTC 瞬間）所在太陽日的各時刻；極區不發生的事件不會出現在結果中</summary>
        public static Dictionary<string, DateTime> GetTimes(DateTime utc, double lat, double lon, double height = 0)
        {
            double lw = Rad * -lon, phi = Rad * lat, dh = ObserverAngle(height);
            double d = ToDays(utc);
            double n = JulianCycle(d, lw);
            double ds = ApproxTransit(0, lw, n);
            double m = SolarMeanAnomaly(ds);
            double l = EclipticLongitude(m);
            double dec = Declination(l, 0);
            double jnoon = SolarTransitJ(ds, m, l);
            var result = new Dictionary<string, DateTime>
            {
                ["solarNoon"] = FromJulian(jnoon),
                ["nadir"] = FromJulian(jnoon - 0.5),
            };
            foreach (var t in Times)
            {
                double h0 = (t.angle + dh) * Rad;
                double jset = GetSetJ(h0, lw, phi, dec, n, m, l);
                if (double.IsNaN(jset)) continue;
                double jrise = jnoon - (jset - jnoon);
                result[t.rise] = FromJulian(jrise);
                result[t.set] = FromJulian(jset);
            }
            return result;
        }

        // ---- 月亮 ----

        static void MoonCoords(double d, out double ra, out double dec, out double dist)
        {
            double l = Rad * (218.316 + 13.176396 * d);
            double m = Rad * (134.963 + 13.064993 * d);
            double f = Rad * (93.272 + 13.229350 * d);
            double lng = l + Rad * 6.289 * Math.Sin(m);
            double b = Rad * 5.128 * Math.Sin(f);
            dist = 385001 - 20905 * Math.Cos(m);
            ra = RightAscension(lng, b);
            dec = Declination(lng, b);
        }

        public struct MoonPos
        {
            public double azimuth;
            public double altitude;
            public double distanceKm;
            public double parallacticAngle;
        }

        public static MoonPos MoonPosition(DateTime utc, double lat, double lon)
        {
            double lw = Rad * -lon, phi = Rad * lat, d = ToDays(utc);
            MoonCoords(d, out double ra, out double dec, out double dist);
            double h = SiderealTime(d, lw) - ra;
            double alt = Altitude(h, phi, dec);
            double pa = Math.Atan2(Math.Sin(h), Math.Tan(phi) * Math.Cos(dec) - Math.Sin(dec) * Math.Cos(h));
            alt += AstroRefraction(alt);
            return new MoonPos { azimuth = NorthAz(AzimuthS(h, phi, dec)), altitude = alt / Rad, distanceKm = dist, parallacticAngle = pa / Rad };
        }

        public struct Illumination
        {
            public double fraction;
            public double phase;
            public double angle;
        }

        public static Illumination MoonIllumination(DateTime utc)
        {
            double d = ToDays(utc);
            SunCoords(d, out double sdec, out double sra);
            MoonCoords(d, out double mra, out double mdec, out double mdist);
            const double sdist = 149598000;
            double phi = Math.Acos(Math.Sin(sdec) * Math.Sin(mdec) + Math.Cos(sdec) * Math.Cos(mdec) * Math.Cos(sra - mra));
            double inc = Math.Atan2(sdist * Math.Sin(phi), mdist - sdist * Math.Cos(phi));
            double angle = Math.Atan2(Math.Cos(sdec) * Math.Sin(sra - mra),
                Math.Sin(sdec) * Math.Cos(mdec) - Math.Cos(sdec) * Math.Sin(mdec) * Math.Cos(sra - mra));
            return new Illumination
            {
                fraction = (1 + Math.Cos(inc)) / 2,
                phase = 0.5 + 0.5 * inc * (angle < 0 ? -1 : 1) / Math.PI,
                angle = angle / Rad,
            };
        }

        public static string MoonPhaseName(double p)
        {
            string[] names = { "新月", "眉月", "上弦月", "盈凸月", "滿月", "虧凸月", "下弦月", "殘月" };
            return names[(int)Math.Round(p * 8) % 8];
        }
    }
}
