using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 地點搜尋：有 Google 金鑰時用 Places API (New) Text Search，否則（或查無結果時）用 OSM Nominatim。
    /// 也可直接輸入「緯度, 經度」。
    /// </summary>
    public class SearchBox
    {
        public VisualElement Row { get; }
        public VisualElement Results { get; }

        readonly IAppActions _app;
        readonly MapPanel _map;
        readonly TextField _field;
        int _token;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public SearchBox(IAppActions app, MapPanel map)
        {
            _app = app;
            _map = map;
            Row = new VisualElement();
            Row.AddToClassList("search-row");
            _field = new TextField();
            _field.textEdition.placeholder = "搜尋地點或地址…（或輸入緯度, 經度）";
            var go = new Button(Submit) { text = "搜尋" };
            Row.Add(_field);
            Row.Add(go);
            _field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Submit();
                if (e.keyCode == KeyCode.Escape) Hide();
            }, TrickleDown.TrickleDown);

            Results = new VisualElement();
            Results.AddToClassList("search-results");
            Row.RegisterCallback<GeometryChangedEvent>(_ => Results.style.top = Row.layout.yMax);
        }

        void Hide() => Results.style.display = DisplayStyle.None;

        void Show(List<(string title, string sub, LatLon at, double? zoom)> items, string empty = null)
        {
            Results.Clear();
            if (items.Count == 0)
            {
                var l = new Label(empty ?? "查無結果");
                l.AddToClassList("search-empty");
                Results.Add(l);
            }
            foreach (var it in items)
            {
                var item = it;
                var b = new Button(() =>
                {
                    Hide();
                    _app.GoTo(item.at);
                    _map.PanTo(item.at, item.zoom);
                })
                {
                    text = string.IsNullOrEmpty(item.sub) ? item.title : $"<b>{item.title}</b>\n<size=11>{item.sub}</size>",
                };
                Results.Add(b);
            }
            var close = new Button(Hide) { text = "關閉" };
            close.AddToClassList("search-close");
            Results.Add(close);
            Results.style.display = DisplayStyle.Flex;
            Results.BringToFront();
        }

        async void Submit()
        {
            string q = _field.value?.Trim();
            if (string.IsNullOrEmpty(q)) return;
            if (Geo.TryParse(q, out var p))
            {
                Hide();
                _app.GoTo(p);
                _map.PanTo(p);
                return;
            }
            int token = ++_token;
            Show(new List<(string, string, LatLon, double?)>(), "搜尋中…");
            var list = new List<(string, string, LatLon, double?)>();
            if (Config.HasGoogle)
            {
                try
                {
                    list = await Google(q);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[search] Google Places：" + e.Message);
                }
            }
            if (list.Count == 0) list = await Nominatim(q);
            if (token != _token) return;
            Show(list);
        }

        // ---- Google Places API (New) ----

        [Serializable] class GText { public string text; }
        [Serializable] class GLatLng { public double latitude, longitude; }
        [Serializable] class GViewport { public GLatLng low, high; }
        [Serializable] class GPlace { public GText displayName; public string formattedAddress; public GLatLng location; public GViewport viewport; }
        [Serializable] class GResponse { public List<GPlace> places; }

        async Task<List<(string, string, LatLon, double?)>> Google(string q)
        {
            var o = Geo.Origin;
            string body = "{\"textQuery\":" + Json(q) + ",\"languageCode\":\"zh-TW\",\"pageSize\":6," +
                          "\"locationBias\":{\"circle\":{\"center\":{\"latitude\":" + o.lat.ToString(Inv) + ",\"longitude\":" + o.lon.ToString(Inv) + "},\"radius\":50000.0}}}";
            var json = await Net.PostJson("https://places.googleapis.com/v1/places:searchText", body, new Dictionary<string, string>
            {
                ["X-Goog-Api-Key"] = Config.GoogleKey,
                ["X-Goog-FieldMask"] = "places.displayName,places.formattedAddress,places.location,places.viewport",
            });
            var list = new List<(string, string, LatLon, double?)>();
            if (json == null) return list;
            var r = JsonUtility.FromJson<GResponse>(json);
            if (r?.places == null) return list;
            foreach (var pl in r.places)
            {
                if (pl.location == null) continue;
                double? zoom = null;
                if (pl.viewport?.low != null && pl.viewport.high != null)
                    zoom = ZoomFor(pl.viewport.high.latitude - pl.viewport.low.latitude, pl.location.latitude);
                list.Add((pl.displayName?.text ?? pl.formattedAddress, pl.formattedAddress, new LatLon(pl.location.latitude, pl.location.longitude), zoom));
            }
            return list;
        }

        // ---- Nominatim ----

        [Serializable] class NItem { public string lat, lon, display_name, name; public string[] boundingbox; }
        [Serializable] class NWrap { public List<NItem> items; }

        static async Task<List<(string, string, LatLon, double?)>> Nominatim(string q)
        {
            var list = new List<(string, string, LatLon, double?)>();
            string url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=6&accept-language=zh-TW,zh,en&q=" + Uri.EscapeDataString(q);
            var json = await Net.GetText(url);
            if (json == null) return list;
            var r = JsonUtility.FromJson<NWrap>("{\"items\":" + json + "}");
            if (r?.items == null) return list;
            foreach (var it in r.items)
            {
                if (!double.TryParse(it.lat, NumberStyles.Float, Inv, out double lat) || !double.TryParse(it.lon, NumberStyles.Float, Inv, out double lon)) continue;
                double? zoom = null;
                if (it.boundingbox is { Length: 4 } &&
                    double.TryParse(it.boundingbox[0], NumberStyles.Float, Inv, out double s) &&
                    double.TryParse(it.boundingbox[1], NumberStyles.Float, Inv, out double n))
                    zoom = ZoomFor(n - s, lat);
                string title = string.IsNullOrEmpty(it.name) ? it.display_name.Split(',')[0] : it.name;
                list.Add((title, it.display_name, new LatLon(lat, lon), zoom));
            }
            return list;
        }

        /// <summary>讓緯度範圍 spanDeg 大約填滿 300 點高的地圖</summary>
        static double ZoomFor(double spanDeg, double lat)
        {
            double meters = Math.Max(150, spanDeg * 110574);
            double mpp = meters / 300;
            return Math.Clamp(Math.Log(156543.03392 * Math.Cos(lat * Math.PI / 180) / mpp, 2), 4, 18);
        }

        static string Json(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
