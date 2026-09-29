using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FramingBuddy
{
    public enum MeteringMode { Multi, CenterWeighted, Spot, Average }
    public enum Quality { Low, Medium, High, Ultra }

    /// <summary>
    /// 拍攝設定（與網頁版相同的欄位，另加光圈、對焦、測光模式、畫質）。
    /// 位置以「原點經緯度＋相對原點的 x 東／z 南」表示，分享連結因此能與網頁版互通。
    /// </summary>
    [Serializable]
    public class ShotState
    {
        public double lat0 = Geo.DefaultOrigin.lat;
        public double lon0 = Geo.DefaultOrigin.lon;
        public string tz = "Asia/Taipei";
        public double x = -312.6;
        public double z = -168.3;
        public float height = 1.6f;
        public bool snap = true;
        public float azimuth = 118.3f;
        public float pitch = 5f;
        public float roll = 0f;
        public float focal = 35f;
        public string aspect = "3:2";
        public bool portrait = false;
        public string date = "2026-09-28";
        public int minutes = 17 * 60 + 30;
        public float clouds = 0.25f;
        public float visibility = 25f;
        public float ev = 0f;
        public bool trees = true;
        public bool grid = true;
        public float range = 6f;
        public float near = 1000f;
        public bool photoreal = true;
        public bool relight = true;
        public string target = "taipei101";
        // Unity 版新增
        public float aperture = 8f;
        /// <summary>對焦距離（m）；0＝自動對焦（畫面中央）</summary>
        public float focus = 0f;
        public MeteringMode metering = MeteringMode.Multi;
        public Quality quality = Quality.High;

        public ShotState Clone() => (ShotState)MemberwiseClone();

        public void Sanitize()
        {
            lat0 = Math.Clamp(lat0, -85, 85);
            lon0 = ((lon0 + 180) % 360 + 360) % 360 - 180;
            azimuth = ((azimuth % 360) + 360) % 360;
            pitch = Mathf.Clamp(pitch, -90, 90);
            roll = Mathf.Clamp(roll, -90, 90);
            focal = Mathf.Clamp(focal, Lens.FocalMin, Lens.FocalMax);
            height = Mathf.Clamp(height, 0, 2000);
            clouds = Mathf.Clamp01(clouds);
            visibility = Mathf.Clamp(visibility, 1, 150);
            ev = Mathf.Clamp(ev, -5, 5);
            minutes = Mathf.Clamp(minutes, 0, 1439);
            range = Mathf.Clamp(range, 1, 20);
            near = Mathf.Clamp(near, 200, 3000);
            aperture = Mathf.Clamp(aperture, 1f, 32f);
            focus = Mathf.Clamp(focus, 0f, 100000f);
            if (Array.FindIndex(Lens.Aspects, a => a.id == aspect) < 0) aspect = "3:2";
            if (!TimeUtil.IsValidZone(tz)) tz = "Asia/Taipei";
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        public LatLon CameraLatLon => Geo.ToLatLon(x, z);
    }

    /// <summary>狀態容器：變更時通知訂閱者（附上改變的欄位名稱）。</summary>
    public class Store
    {
        public ShotState State { get; private set; }
        public event Action<ShotState, HashSet<string>> Changed;

        static readonly FieldInfo[] Fields = typeof(ShotState).GetFields(BindingFlags.Public | BindingFlags.Instance);

        public Store(ShotState initial)
        {
            State = initial;
            State.Sanitize();
        }

        public void Set(Action<ShotState> mutate)
        {
            var next = State.Clone();
            mutate(next);
            next.Sanitize();
            var changed = new HashSet<string>();
            foreach (var f in Fields)
                if (!Equals(f.GetValue(next), f.GetValue(State))) changed.Add(f.Name);
            if (changed.Count == 0) return;
            State = next;
            Changed?.Invoke(State, changed);
        }

        public void Subscribe(Action<ShotState, HashSet<string>> l)
        {
            Changed += l;
            var all = new HashSet<string>();
            foreach (var f in Fields) all.Add(f.Name);
            l(State, all);
        }
    }

    /// <summary>分享連結：與網頁版相同的 URL hash 格式（另加 Unity 版專屬欄位）。</summary>
    public static class ShareLink
    {
        public const string WebBase = "http://localhost:5173/";

        static readonly (string key, string field)[] Keys =
        {
            ("la", "lat0"), ("lo", "lon0"), ("tz", "tz"), ("x", "x"), ("z", "z"), ("h", "height"), ("s", "snap"),
            ("az", "azimuth"), ("p", "pitch"), ("r", "roll"), ("f", "focal"), ("ar", "aspect"), ("o", "portrait"),
            ("d", "date"), ("t", "minutes"), ("c", "clouds"), ("v", "visibility"), ("ev", "ev"), ("rg", "range"),
            ("nr", "near"), ("pr", "photoreal"), ("rl", "relight"), ("tg", "target"),
            ("ap", "aperture"), ("fd", "focus"), ("mm", "metering"),
        };

        public static string Encode(ShotState s)
        {
            var sb = new StringBuilder();
            foreach (var (key, field) in Keys)
            {
                var f = typeof(ShotState).GetField(field);
                object v = f.GetValue(s);
                string str = v switch
                {
                    double d when field is "lat0" or "lon0" => d.ToString("F6", CultureInfo.InvariantCulture),
                    double d => Math.Round(d, 2).ToString(CultureInfo.InvariantCulture),
                    float fl => Math.Round(fl, 2).ToString(CultureInfo.InvariantCulture),
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    bool b => b ? "1" : "0",
                    Enum e => e.ToString(),
                    _ => v?.ToString() ?? "",
                };
                if (sb.Length > 0) sb.Append('&');
                sb.Append(key).Append('=').Append(Uri.EscapeDataString(str));
            }
            return sb.ToString();
        }

        /// <summary>解析網頁版或本 App 的分享連結（整個 URL 或只有 hash 皆可）</summary>
        public static bool TryDecode(string text, ShotState into)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int hash = text.IndexOf('#');
            string q = hash >= 0 ? text[(hash + 1)..] : text.Trim();
            bool any = false;
            foreach (var part in q.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part[..eq];
                string raw = Uri.UnescapeDataString(part[(eq + 1)..]);
                var entry = Array.Find(Keys, k => k.key == key);
                if (entry.field == null) continue;
                var f = typeof(ShotState).GetField(entry.field);
                try
                {
                    object v = f.FieldType == typeof(double) ? double.Parse(raw, CultureInfo.InvariantCulture)
                        : f.FieldType == typeof(float) ? float.Parse(raw, CultureInfo.InvariantCulture)
                        : f.FieldType == typeof(int) ? (int)Math.Round(double.Parse(raw, CultureInfo.InvariantCulture))
                        : f.FieldType == typeof(bool) ? raw == "1"
                        : f.FieldType.IsEnum ? Enum.Parse(f.FieldType, raw)
                        : raw;
                    f.SetValue(into, v);
                    any = true;
                }
                catch (Exception)
                {
                    // 忽略無法解析的欄位
                }
            }
            return any;
        }
    }
}
