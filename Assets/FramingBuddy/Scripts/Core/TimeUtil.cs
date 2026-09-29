using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 時區：日期與時間一律以「拍攝地點的當地時間」表示。
    /// IANA 時區 ID 由 Google Time Zone API 查詢（需在桌面用金鑰啟用），失敗時以經度估算；
    /// 夏令時間交給 .NET 的 TimeZoneInfo（macOS 讀取系統的 zoneinfo）。
    /// </summary>
    public static class TimeUtil
    {
        static readonly Dictionary<string, TimeZoneInfo> Cache = new();

        public static bool IsValidZone(string tz) => Find(tz) != null;

        public static TimeZoneInfo Find(string tz)
        {
            if (string.IsNullOrEmpty(tz)) return null;
            if (Cache.TryGetValue(tz, out var z)) return z;
            try
            {
                z = TimeZoneInfo.FindSystemTimeZoneById(tz);
            }
            catch (Exception)
            {
                z = null;
                if (tz.StartsWith("Etc/GMT"))
                {
                    // Etc/GMT-9 ＝ UTC+9
                    string n = tz.Substring(7);
                    int h = string.IsNullOrEmpty(n) ? 0 : -int.Parse(n, CultureInfo.InvariantCulture);
                    z = TimeZoneInfo.CreateCustomTimeZone(tz, TimeSpan.FromHours(h), tz, tz);
                }
            }
            Cache[tz] = z;
            return z;
        }

        public static string GuessZone(double lon)
        {
            int h = (int)Math.Round(lon / 15);
            if (h == 0) return "Etc/GMT";
            return "Etc/GMT" + (h > 0 ? "-" : "+") + Math.Abs(h);
        }

        public static async Task<string> LookupZone(double lat, double lon)
        {
            string key = Config.GoogleKey;
            if (!string.IsNullOrEmpty(key))
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string url = $"https://maps.googleapis.com/maps/api/timezone/json?location={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}&timestamp={ts}&key={key}";
                var json = await Net.GetText(url);
                if (json != null)
                {
                    var r = JsonUtility.FromJson<TzResponse>(json);
                    if (r != null && r.status == "OK" && IsValidZone(r.timeZoneId)) return r.timeZoneId;
                }
            }
            return GuessZone(lon);
        }

        [Serializable]
        class TzResponse
        {
            public string status;
            public string timeZoneId;
        }

        /// <summary>當地日期＋當日分鐘數 → UTC</summary>
        public static DateTime ZonedToUtc(string date, int minutes, string tz)
        {
            var d = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var local = DateTime.SpecifyKind(d.AddMinutes(minutes), DateTimeKind.Unspecified);
            var zone = Find(tz) ?? TimeZoneInfo.Utc;
            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            }
            catch (ArgumentException)
            {
                // 夏令時間切換的空白時段：往後推一小時
                return TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), zone);
            }
        }

        public static (string date, int minutes) UtcToZoned(DateTime utc, string tz)
        {
            var zone = Find(tz) ?? TimeZoneInfo.Utc;
            var l = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return (l.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.Hour * 60 + l.Minute);
        }

        public static (string date, int minutes) Now(string tz) => UtcToZoned(DateTime.UtcNow, tz);

        /// <summary>瞬間 → 相對於 date 當地午夜的分鐘數（可能 &lt;0 或 ≥1440）</summary>
        public static int MinutesOnDate(DateTime utc, string date, string tz) =>
            (int)Math.Round((utc - ZonedToUtc(date, 0, tz)).TotalMinutes);

        public static string OffsetLabel(string tz, DateTime utc)
        {
            var zone = Find(tz) ?? TimeZoneInfo.Utc;
            var off = zone.GetUtcOffset(utc);
            string sign = off >= TimeSpan.Zero ? "+" : "−";
            off = off.Duration();
            return "UTC" + sign + off.Hours + (off.Minutes != 0 ? ":" + off.Minutes.ToString("00") : "");
        }

        public static string FormatMinutes(int min)
        {
            int m = ((min % 1440) + 1440) % 1440;
            return (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
        }
    }
}
