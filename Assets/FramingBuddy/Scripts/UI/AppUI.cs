using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 介面（UI Toolkit，全部以程式建立）：左側為地圖＋控制面板，右側為取景結果與資訊列。
    /// 與網頁版相同的區塊與操作；另加光圈／對焦、測光模式與畫質。
    /// </summary>
    public class AppUI
    {
        readonly Store _store;
        readonly IAppActions _app;
        readonly List<Action<ShotState>> _syncs = new();

        public VisualElement Root { get; }
        public MapPanel Map { get; }
        public ViewfinderOverlay Viewfinder { get; }

        VisualElement _vfHost;
        readonly VisualElement[] _bars = new VisualElement[4];
        Label _loading, _attrib, _hudTarget, _mapHint;
        readonly List<Label> _hud = new();
        ScrollView _scroll;
        VisualElement _activeSlider;
        string _frameKey = "";

        /// <summary>取景框（面板座標，單位為 UI 點）</summary>
        public Rect FrameRect { get; private set; }
        public event Action FrameChanged;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double v, int d = 1) => v.ToString("F" + d, Inv);

        public AppUI(VisualElement root, Store store, IAppActions app, MapPanel.IOrbitView orbit)
        {
            _store = store;
            _app = app;
            Root = root;
            root.AddToClassList("app");
            root.style.backgroundColor = Color.black;
            Fonts.Apply(root);

            // ---- 左側 ----
            var aside = new VisualElement();
            aside.AddToClassList("aside");
            root.Add(aside);

            var brand = new VisualElement();
            brand.AddToClassList("brand");
            var titles = new VisualElement();
            titles.Add(Cls(new Label("Framing Buddy"), "brand-title"));
            titles.Add(Cls(new Label("拍攝角度・光線・焦段規劃"), "brand-sub"));
            brand.Add(titles);
            aside.Add(brand);

            Map = new MapPanel(store, app, orbit);
            brand.Add(Map.ModeSelector);

            var search = new SearchBox(app, Map);
            aside.Add(search.Row);
            aside.Add(Map.Element);
            _mapHint = Cls(new Label(), "map-hint");
            aside.Add(_mapHint);
            Map.HintChanged += t => _mapHint.text = t;
            _mapHint.text = Map.Hint;

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("panel");
            aside.Add(_scroll);
            aside.Add(search.Results);

            BuildSections();

            // ---- 右側 ----
            var result = new VisualElement();
            result.AddToClassList("result");
            root.Add(result);
            _vfHost = new VisualElement();
            _vfHost.AddToClassList("viewfinder");
            result.Add(_vfHost);
            for (int i = 0; i < 4; i++)
            {
                _bars[i] = Cls(new VisualElement(), "bar");
                _bars[i].pickingMode = PickingMode.Ignore;
                _vfHost.Add(_bars[i]);
            }
            Viewfinder = new ViewfinderOverlay(store, app);
            _vfHost.Add(Viewfinder.Element);
            _loading = Cls(new Label("載入中…"), "loading");
            _loading.pickingMode = PickingMode.Ignore;
            _vfHost.Add(_loading);
            _attrib = new Label();
            _attrib.pickingMode = PickingMode.Ignore;
            _attrib.style.position = Position.Absolute;
            _attrib.style.right = 8;
            _attrib.style.bottom = 4;
            _attrib.style.fontSize = 10;
            _attrib.style.color = new Color(1, 1, 1, 0.75f);
            _attrib.style.maxWidth = 640;
            _attrib.style.whiteSpace = WhiteSpace.Normal;
            _attrib.style.unityTextAlign = TextAnchor.LowerRight;
            _vfHost.Add(_attrib);
            _vfHost.RegisterCallback<GeometryChangedEvent>(_ => LayoutFrame());

            var hud = Cls(new VisualElement(), "hud");
            var main = Cls(new VisualElement(), "hud-main");
            for (int i = 0; i < 10; i++)
            {
                var l = new Label();
                if (i == 0) l.AddToClassList("hud-big");
                _hud.Add(l);
                main.Add(l);
            }
            hud.Add(main);
            _hudTarget = Cls(new Label(), "hud-target");
            hud.Add(_hudTarget);
            result.Add(hud);

            // 拖曳滑桿時不要被狀態同步打斷
            root.RegisterCallback<PointerDownEvent>(e =>
            {
                var t = e.target as VisualElement;
                _activeSlider = t?.GetFirstOfType<Slider>();
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(_ => _activeSlider = null, TrickleDown.TrickleDown);

            store.Subscribe(OnState);
        }

        static T Cls<T>(T e, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        /// <summary>目前是否在文字框中輸入（鍵盤快捷鍵要略過）</summary>
        public bool TextFocused
        {
            get
            {
                var f = Root.focusController?.focusedElement as VisualElement;
                if (f == null) return false;
                return f is TextField || f.GetFirstAncestorOfType<TextField>() != null;
            }
        }

        void OnState(ShotState s, HashSet<string> changed)
        {
            foreach (var f in _syncs) f(s);
            if (changed.Contains("aspect") || changed.Contains("portrait")) LayoutFrame();
            UpdateHud();
            Map.Apply(s);
            Viewfinder.MarkDirty();
        }

        /// <summary>定期更新（目標清單、載入狀態、資訊列）</summary>
        public void Refresh()
        {
            foreach (var f in _syncs) f(_store.State);
            UpdateHud();
            bool busy = _app.Busy;
            _loading.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            if (busy) _loading.text = "載入中… " + _app.Status().Replace("\n", " · ");
            _attrib.text = _app.Attribution();
        }

        // ---- 取景框 ----

        void LayoutFrame()
        {
            var r = _vfHost.contentRect;
            if (r.width < 4 || r.height < 4) return;
            var s = _store.State;
            var fs = Lens.FrameSize(s.aspect, s.portrait);
            float aspect = fs.x / fs.y;
            const float pad = 16;
            float w = r.width - pad * 2, h = r.height - pad * 2;
            if (w / h > aspect) w = h * aspect;
            else h = w / aspect;
            w = Mathf.Max(1, Mathf.Floor(w));
            h = Mathf.Max(1, Mathf.Floor(h));
            float x = Mathf.Floor((r.width - w) / 2), y = Mathf.Floor((r.height - h) / 2);
            var el = Viewfinder.Element;
            el.style.left = x;
            el.style.top = y;
            el.style.width = w;
            el.style.height = h;
            void Bar(int i, float l, float t, float bw, float bh)
            {
                _bars[i].style.left = l;
                _bars[i].style.top = t;
                _bars[i].style.width = bw;
                _bars[i].style.height = bh;
            }
            Bar(0, 0, 0, r.width, y);
            Bar(1, 0, y + h, r.width, r.height - y - h);
            Bar(2, 0, y, x, h);
            Bar(3, x + w, y, r.width - x - w, h);
            var wb = _vfHost.worldBound;
            FrameRect = new Rect(wb.x + x, wb.y + y, w, h);
            string key = $"{FrameRect}";
            if (key == _frameKey) return;
            _frameKey = key;
            Viewfinder.Resize(w, h);
            FrameChanged?.Invoke();
        }

        // ---- 資訊列 ----

        public void UpdateHud()
        {
            var s = _store.State;
            var fov = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait));
            var info = _app.Celestial;
            var eye = _app.Eye;
            var utc = TimeUtil.ZonedToUtc(s.date, s.minutes, s.tz);
            string[] items =
            {
                $"{Mathf.Round(s.focal)}<size=12>mm</size> <size=13>f/{F(s.aperture, s.aperture < 10 ? 1 : 0)}</size>",
                $"{Lens.AspectText(s.aspect, s.portrait)} {(s.portrait ? "直幅" : "橫幅")}",
                $"視角 {F(fov.h)}° × {F(fov.v)}°",
                $"方位 {F(s.azimuth)}° {Geo.CompassName(s.azimuth)}",
                $"俯仰 {(s.pitch >= 0 ? "+" : "")}{F(s.pitch)}°",
                $"鏡頭 海拔 {F(eye.y + _app.OriginElevation)} m",
                $"{s.date} {TimeUtil.FormatMinutes(s.minutes)}（{TimeUtil.OffsetLabel(s.tz, utc)}）",
                info != null ? $"☀ {F(info.sunAz, 0)}° / {F(info.sunAlt)}°" : "",
                _app.MeterText,
                "",
            };
            for (int i = 0; i < _hud.Count; i++)
            {
                _hud[i].text = items[i];
                _hud[i].style.display = string.IsNullOrEmpty(items[i]) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            var rep = _app.Report;
            var t = rep?.target;
            string line = "<b>目標</b> 範圍內沒有已知地標（可到「場景載入」加大遠景範圍）";
            bool ok = false;
            if (t != null)
            {
                var l = Geo.ToLocal(t.at);
                double d = Math.Sqrt((l.x - s.x) * (l.x - s.x) + (l.y - s.z) * (l.y - s.z));
                double b = Geo.Bearing(s.x, s.z, l.x, l.y);
                int vis = Mathf.RoundToInt(rep.visible * 100);
                string status;
                if (t.kind == TargetKind.Peak)
                    status = !rep.inFrame ? "山頂不在畫面內" : vis >= 50 ? "山頂入鏡、無遮擋" : "山頂在畫面方向上，但被地形或建物擋住";
                else if (!rep.inFrame) status = "不在畫面內";
                else if (vis >= 98) status = "完整入鏡、無遮擋";
                else if (vis <= 2) status = "在畫面方向上，但被遮擋";
                else status = $"可見約 {vis}%（部分被遮擋）";
                int pct = rep.frameFraction.HasValue ? Mathf.RoundToInt(rep.frameFraction.Value * 100) : 0;
                string frac = rep.inFrame && rep.frameFraction.HasValue ? $" ・ 高度為畫面的 {pct}%{(pct > 100 ? "（超出畫面）" : "")}" : "";
                string dist = d >= 1000 ? $"{F(d / 1000, 2)} km" : $"{Math.Round(d)} m";
                ok = rep.inFrame && vis > 2;
                line = $"<b>{t.label}</b> 距離 {dist} ・ 方位 {F(b)}° ・ {status}{frac}";
            }
            _hudTarget.text = line;
            _hudTarget.EnableInClassList("ok", ok);
        }

        // ---- 面板元件 ----

        Foldout Section(string title, bool open = true)
        {
            var f = new Foldout { text = title, value = open };
            f.AddToClassList("sec");
            _scroll.Add(f);
            return f;
        }

        bool IsFocused(VisualElement e)
        {
            var f = e.focusController?.focusedElement as VisualElement;
            return f != null && (f == e || e.Contains(f));
        }

        class SliderOpts
        {
            public float min, max;
            public Func<ShotState, float> get;
            public Action<ShotState, float> set;
            public string unit = "";
            public Func<ShotState, string> suffix;
            public Func<float, float> toSlider, fromSlider;
            public int digits = 1;
            /// <summary>放開滑桿後才套用（重新載入代價高的設定）</summary>
            public bool commit;
            public float numMin = float.NegativeInfinity, numMax = float.PositiveInfinity;
        }

        void SliderRow(VisualElement parent, string label, SliderOpts o)
        {
            var ctl = Cls(new VisualElement(), "ctl");
            var row = Cls(new VisualElement(), "row");
            row.Add(Cls(new Label(label), "lab"));
            var slider = new Slider(o.min, o.max);
            row.Add(slider);
            var num = Cls(new TextField { isDelayed = true }, "num");
            row.Add(num);
            row.Add(Cls(new Label(o.unit), "unit"));
            ctl.Add(row);
            var suffix = Cls(new Label(), "suffix");
            ctl.Add(suffix);
            parent.Add(ctl);

            IVisualElementScheduledItem pending = null;
            slider.RegisterValueChangedCallback(e =>
            {
                float v = o.fromSlider != null ? o.fromSlider(e.newValue) : e.newValue;
                num.SetValueWithoutNotify(F(v, o.digits));
                if (o.commit)
                {
                    pending?.Pause();
                    pending = slider.schedule.Execute(() => _store.Set(s => o.set(s, v))).StartingIn(400);
                }
                else _store.Set(s => o.set(s, v));
            });
            num.RegisterValueChangedCallback(e =>
            {
                if (float.TryParse(e.newValue, NumberStyles.Float, Inv, out float v) && !float.IsNaN(v))
                {
                    v = Mathf.Clamp(v, o.numMin, o.numMax);
                    _store.Set(s => o.set(s, v));
                }
            });
            _syncs.Add(s =>
            {
                float v = o.get(s);
                if (_activeSlider != slider) slider.SetValueWithoutNotify(o.toSlider != null ? o.toSlider(v) : v);
                if (!IsFocused(num)) num.SetValueWithoutNotify(F(v, o.digits));
                string sx = o.suffix?.Invoke(s);
                suffix.text = sx ?? "";
                suffix.style.display = string.IsNullOrEmpty(sx) ? DisplayStyle.None : DisplayStyle.Flex;
            });
        }

        VisualElement Chips(VisualElement parent, IEnumerable<(string text, Action fn)> items, string extra = null)
        {
            var row = Cls(new VisualElement(), "chips");
            if (extra != null) row.AddToClassList(extra);
            foreach (var (text, fn) in items) row.Add(new Button(fn) { text = text });
            parent.Add(row);
            return row;
        }

        void Check(VisualElement parent, string label, Func<ShotState, bool> get, Action<ShotState, bool> set)
        {
            var t = Cls(new Toggle { text = label }, "check");
            t.RegisterValueChangedCallback(e => _store.Set(s => set(s, e.newValue)));
            parent.Add(t);
            _syncs.Add(s => t.SetValueWithoutNotify(get(s)));
        }

        VisualElement Row2(VisualElement parent, string label)
        {
            var row = Cls(new VisualElement(), "row2");
            if (label != null) row.Add(Cls(new Label(label), "lab"));
            parent.Add(row);
            return row;
        }

        static TextField Field(string placeholder)
        {
            var tf = new TextField();
            tf.textEdition.placeholder = placeholder;
            return tf;
        }

        // ---- 各區塊 ----

        void BuildSections()
        {
            BuildPosition();
            BuildHeight();
            BuildDirection();
            BuildLens();
            BuildLight();
            BuildScene();
            BuildShare();
        }

        void BuildPosition()
        {
            var sec = Section("位置");
            var presetBtn = new Button { text = "快速位置…" };
            presetBtn.style.marginTop = 4;
            presetBtn.clicked += () =>
            {
                var menu = new GenericDropdownMenu();
                string group = null;
                foreach (var p in _app.Presets)
                {
                    if (p.group != group)
                    {
                        if (group != null) menu.AddSeparator("");
                        group = p.group;
                        menu.AddDisabledItem($"── {group} ──", false);
                    }
                    var pp = p;
                    menu.AddItem(p.name, false, () => _app.ApplyPreset(pp));
                }
                menu.DropDown(presetBtn.worldBound, presetBtn, DropdownMenuSizeMode.Auto);
            };
            sec.Add(presetBtn);

            var row = Row2(sec, null);
            var coord = Field("緯度, 經度（例：25.0346, 121.5218）");
            var go = new Button { text = "前往" };
            row.Add(coord);
            row.Add(go);
            void GoCoord()
            {
                bool okp = Geo.TryParse(coord.value, out var p);
                coord.style.borderBottomColor = okp ? StyleKeyword.Null : new StyleColor(new Color(1, 0.4f, 0.3f));
                if (okp) _app.GoTo(p);
            }
            go.clicked += GoCoord;
            coord.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) GoCoord();
            }, TrickleDown.TrickleDown);

            SliderRow(sec, "東西", new SliderOpts { min = -1500, max = 1500, unit = "m", get = s => (float)s.x, set = (s, v) => s.x = v });
            SliderRow(sec, "南北", new SliderOpts
            {
                min = -1500, max = 1500, unit = "m", get = s => (float)-s.z, set = (s, v) => s.z = -v,
                suffix = s => $"目前 {s.CameraLatLon.Format(6)}（相對場景原點，正值＝以東／以北）",
            });
        }

        void BuildHeight()
        {
            var sec = Section("高度");
            SliderRow(sec, "離地", new SliderOpts
            {
                min = 0, max = 1000, unit = "m", digits = 2, numMin = 0, numMax = 2000,
                // 滑桿前段細、後段粗：0–1000 對應 0–500 m
                toSlider = v => Mathf.Sqrt(Mathf.Max(0, v) / 500f) * 1000f,
                fromSlider = v => Mathf.Round(Mathf.Pow(v / 1000f, 2) * 500f * 20f) / 20f,
                get = s => s.height, set = (s, v) => s.height = v,
                suffix = s =>
                {
                    float b = s.snap ? _app.Surface : 0;
                    return $"站立面 {F(b)} m ・ 鏡頭 {F(b + s.height)} m（相對原點地面）";
                },
            });
            Check(sec, "站在地面／屋頂／台階上（自動貼合高度）", s => s.snap, (s, v) => s.snap = v);
            Chips(sec, new (string, Action)[]
            {
                ("低角度 0.4m", () => _store.Set(s => s.height = 0.4f)),
                ("平視 1.6m", () => _store.Set(s => s.height = 1.6f)),
                ("舉高 2.4m", () => _store.Set(s => s.height = 2.4f)),
                ("空拍 60m", () => _store.Set(s => { s.height = 60; s.snap = false; })),
            });
        }

        void BuildDirection()
        {
            var sec = Section("方向");
            SliderRow(sec, "方位", new SliderOpts
            {
                min = 0, max = 360, unit = "°", get = s => s.azimuth, set = (s, v) => s.azimuth = v,
                suffix = s => $"朝{Geo.CompassName(s.azimuth)}（0°＝北，順時針）",
            });
            SliderRow(sec, "俯仰", new SliderOpts { min = -90, max = 90, unit = "°", get = s => s.pitch, set = (s, v) => s.pitch = v });
            SliderRow(sec, "水平", new SliderOpts { min = -45, max = 45, unit = "°", get = s => s.roll, set = (s, v) => s.roll = v });

            var row = Row2(sec, "目標");
            var tsel = new DropdownField();
            var aimBtn = new Button { text = "對準" };
            row.Add(tsel);
            row.Add(aimBtn);
            var ids = new List<string>();
            string lastKey = null;
            tsel.RegisterValueChangedCallback(_ =>
            {
                int i = tsel.index;
                if (i >= 0 && i < ids.Count && ids[i] != "") _store.Set(s => s.target = ids[i]);
            });
            aimBtn.clicked += () =>
            {
                int i = tsel.index;
                if (i >= 0 && i < ids.Count && ids[i] != "") _app.Aim(ids[i]);
            };
            _syncs.Add(s =>
            {
                var list = _app.Targets();
                string key = string.Join("|", list.Select(t => t.id)) + "#" + s.target;
                if (key == lastKey) return;
                lastKey = key;
                ids.Clear();
                var labels = new List<string>();
                foreach (var t in list)
                {
                    ids.Add(t.id);
                    labels.Add(t.label);
                }
                if (list.Count == 0)
                {
                    ids.Add("");
                    labels.Add("（範圍內沒有已知地標）");
                }
                tsel.choices = labels;
                int idx = ids.IndexOf(s.target);
                if (idx < 0) idx = 0;
                tsel.SetValueWithoutNotify(labels[idx]);
            });
            Chips(sec, new (string, Action)[]
            {
                ("對準太陽", () => _app.Aim("sun")),
                ("對準月亮", () => _app.Aim("moon")),
                ("水平歸零", () => _store.Set(s => { s.roll = 0; s.pitch = 0; })),
            });
        }

        void BuildLens()
        {
            var sec = Section("鏡頭");
            SliderRow(sec, "焦段", new SliderOpts
            {
                min = 0, max = 1000, unit = "mm", digits = 0,
                toSlider = Lens.FocalToSlider, fromSlider = Lens.SliderToFocal,
                get = s => s.focal, set = (s, v) => s.focal = v,
                suffix = s =>
                {
                    var f = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait));
                    return $"全片幅等效 ・ 視角 水平 {F(f.h)}° ／ 垂直 {F(f.v)}° ／ 對角 {F(f.d)}°";
                },
            });
            var chips = Chips(sec, Lens.FocalPresets.Select(f => (f.ToString(), (Action)(() => _store.Set(s => s.focal = f)))), "focal");
            _syncs.Add(s =>
            {
                foreach (var b in chips.Children().OfType<Button>())
                    b.EnableInClassList("on", b.text == Mathf.RoundToInt(s.focal).ToString());
            });

            var row = Row2(sec, "比例");
            var aspect = new DropdownField(Lens.Aspects.Select(a => a.label).ToList(), 0);
            aspect.RegisterValueChangedCallback(_ =>
            {
                int i = aspect.index;
                if (i >= 0) _store.Set(s => s.aspect = Lens.Aspects[i].id);
            });
            row.Add(aspect);
            var seg = Cls(new VisualElement(), "seg");
            var land = new Button(() => _store.Set(s => s.portrait = false)) { text = "橫幅" };
            var port = new Button(() => _store.Set(s => s.portrait = true)) { text = "直幅" };
            seg.Add(land);
            seg.Add(port);
            row.Add(seg);
            _syncs.Add(s =>
            {
                int i = Array.FindIndex(Lens.Aspects, a => a.id == s.aspect);
                if (i >= 0 && aspect.index != i) aspect.SetValueWithoutNotify(Lens.Aspects[i].label);
                land.EnableInClassList("on", !s.portrait);
                port.EnableInClassList("on", s.portrait);
            });

            // 光圈與對焦（景深）
            var ap = Chips(sec, Lens.Apertures.Select(a => ("f/" + a.ToString(Inv), (Action)(() => _store.Set(s => s.aperture = a)))));
            _syncs.Add(s =>
            {
                int k = 0;
                foreach (var b in ap.Children().OfType<Button>())
                    b.EnableInClassList("on", Mathf.Abs(Lens.Apertures[k++] - s.aperture) < 0.01f);
            });
            Check(sec, "自動對焦（畫面中央）", s => s.focus <= 0, (s, v) => s.focus = v ? 0 : Mathf.Max(1, _app.Report?.target != null ? 100 : 10));
            SliderRow(sec, "對焦", new SliderOpts
            {
                min = 0, max = 1000, unit = "m", digits = 1, numMin = 0.3f, numMax = 100000,
                // 對數刻度 0.3 m–10 km
                toSlider = v => v <= 0 ? 0 : Mathf.Log(v / 0.3f) / Mathf.Log(10000f / 0.3f) * 1000f,
                fromSlider = v => Mathf.Round(0.3f * Mathf.Pow(10000f / 0.3f, v / 1000f) * 10f) / 10f,
                get = s => s.focus, set = (s, v) => s.focus = Mathf.Max(0.3f, v),
                suffix = s => DofText(s),
            });
        }

        string DofText(ShotState s)
        {
            float f = s.focal, n = s.aperture;
            float H = Lens.Hyperfocal(f, n);
            string hyper = $"超焦距 {(H >= 1000 ? F(H / 1000, 2) + " km" : F(H, 1) + " m")}";
            if (s.focus <= 0) return $"自動對焦 ・ {hyper}";
            float d = s.focus, fm = f / 1000f;
            float near = d * (H - fm) / (H + d - 2 * fm);
            string far = d >= H ? "∞" : F(d * (H - fm) / (H - d), 1) + " m";
            return $"景深 {F(near, 1)} m – {far} ・ {hyper}";
        }

        void BuildLight()
        {
            var sec = Section("光線（日期與時間）");
            var row = Row2(sec, "日期");
            var date = Field("yyyy-MM-dd");
            date.isDelayed = true;
            date.RegisterValueChangedCallback(e =>
            {
                if (DateTime.TryParseExact(e.newValue.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out _))
                    _store.Set(s => s.date = e.newValue.Trim());
            });
            void Shift(int days)
            {
                var d = DateTime.ParseExact(_store.State.date, "yyyy-MM-dd", Inv).AddDays(days);
                _store.Set(s => s.date = d.ToString("yyyy-MM-dd", Inv));
            }
            row.Add(date);
            var prev = new Button(() => Shift(-1)) { text = "◀" };
            var next = new Button(() => Shift(1)) { text = "▶" };
            var now = new Button(() =>
            {
                var (d, m) = TimeUtil.Now(_store.State.tz);
                _store.Set(s => { s.date = d; s.minutes = m; });
            }) { text = "現在" };
            prev.style.marginRight = 4;
            next.style.marginRight = 4;
            row.Add(prev);
            row.Add(next);
            row.Add(now);
            _syncs.Add(s => { if (!IsFocused(date)) date.SetValueWithoutNotify(s.date); });

            {
                var ctl = Cls(new VisualElement(), "ctl");
                var r = Cls(new VisualElement(), "row");
                r.Add(Cls(new Label("時間"), "lab"));
                var slider = new Slider(0, 1439);
                var time = Cls(new TextField { isDelayed = true }, "num");
                r.Add(slider);
                r.Add(time);
                r.Add(Cls(new Label(""), "unit"));
                ctl.Add(r);
                var tzNote = Cls(new Label(), "suffix");
                ctl.Add(tzNote);
                sec.Add(ctl);
                slider.RegisterValueChangedCallback(e => _store.Set(s => s.minutes = Mathf.RoundToInt(e.newValue)));
                time.RegisterValueChangedCallback(e =>
                {
                    var p = e.newValue.Split(':');
                    if (p.Length == 2 && int.TryParse(p[0], out int hh) && int.TryParse(p[1], out int mm))
                        _store.Set(s => s.minutes = Mathf.Clamp(hh * 60 + mm, 0, 1439));
                });
                _syncs.Add(s =>
                {
                    if (_activeSlider != slider) slider.SetValueWithoutNotify(s.minutes);
                    if (!IsFocused(time)) time.SetValueWithoutNotify(TimeUtil.FormatMinutes(s.minutes));
                    tzNote.text = $"拍攝地點當地時間（{s.tz}，{TimeUtil.OffsetLabel(s.tz, TimeUtil.ZonedToUtc(s.date, s.minutes, s.tz))}）";
                });
            }

            Action Jump(string key, int offset = 0) => () =>
            {
                var c = _app.Celestial;
                if (c == null || !c.times.TryGetValue(key, out var t)) return;
                var st = _store.State;
                _store.Set(s => s.minutes = TimeUtil.MinutesOnDate(t, st.date, st.tz) + offset);
            };
            Chips(sec, new (string, Action)[]
            {
                ("日出", Jump("sunrise")),
                ("晨間金色時刻", Jump("goldenHourEnd", -20)),
                ("正午", Jump("solarNoon")),
                ("黃昏金色時刻", Jump("goldenHour", 20)),
                ("日落", Jump("sunset")),
                ("藍色時刻", Jump("dusk", -8)),
            });

            var info = Cls(new Label(), "info");
            sec.Add(info);
            _syncs.Add(s =>
            {
                var c = _app.Celestial;
                if (c == null) return;
                string T(string k) => c.times.TryGetValue(k, out var v) ? TimeUtil.FormatMinutes(TimeUtil.MinutesOnDate(v, s.date, s.tz)) : "—";
                info.text =
                    $"<b>太陽</b> 方位 {F(c.sunAz)}° ・ 仰角 {F(c.sunAlt)}°{(c.sunAlt < 0 ? "（地平線下）" : "")}\n" +
                    $"<b>日出</b> {T("sunrise")} ・ <b>日落</b> {T("sunset")} ・ <b>正午</b> {T("solarNoon")}\n" +
                    $"<b>金色時刻</b> 早 {T("sunrise")}–{T("goldenHourEnd")} ・ 晚 {T("goldenHour")}–{T("sunset")}\n" +
                    $"<b>藍色時刻</b> 早 {T("dawn")}–{T("sunrise")} ・ 晚 {T("sunset")}–{T("dusk")}\n" +
                    $"<b>月亮</b> {SunCalc.MoonPhaseName(c.moonPhase)} {Math.Round(c.moonFraction * 100)}% ・ 方位 {F(c.moonAz)}° ・ 仰角 {F(c.moonAlt)}°";
            });

            SliderRow(sec, "雲量", new SliderOpts { min = 0, max = 1, digits = 2, get = s => s.clouds, set = (s, v) => s.clouds = v });
            SliderRow(sec, "能見度", new SliderOpts
            {
                min = 2, max = 150, unit = "km", get = s => s.visibility, set = (s, v) => s.visibility = v,
                suffix = _ => "相機所在高度的水平能見度；霾集中在低空，高山山頂會比山腳清楚",
            });

            var mrow = Row2(sec, "測光");
            var modes = new List<string> { "多重測光（Sony 多重，偏重中央）", "中央重點測光", "點測光", "整體平均" };
            var meter = new DropdownField(modes, 0);
            meter.RegisterValueChangedCallback(_ => _store.Set(s => s.metering = (MeteringMode)Mathf.Max(0, meter.index)));
            mrow.Add(meter);
            _syncs.Add(s => { if (meter.index != (int)s.metering) meter.SetValueWithoutNotify(modes[(int)s.metering]); });
            SliderRow(sec, "曝光補償", new SliderOpts { min = -3, max = 3, unit = "EV", numMin = -5, numMax = 5, get = s => s.ev, set = (s, v) => s.ev = Mathf.Round(v * 10) / 10 });
        }

        void BuildScene()
        {
            var sec = Section("場景載入", false);
            SliderRow(sec, "遠景", new SliderOpts
            {
                min = 1, max = 20, unit = "km", commit = true, get = s => s.range, set = (s, v) => s.range = Mathf.Round(v * 2) / 2,
                suffix = _ => "此距離內有名稱的高樓、高塔（自建地標在沒有 Google 時也以此為載入範圍）。地形、遠山與 Google 模型一律載入到約 90 km 的地平線",
            });
            SliderRow(sec, "近景", new SliderOpts
            {
                min = 200, max = 3000, unit = "m", digits = 0, commit = true, get = s => s.near, set = (s, v) => s.near = Mathf.Round(v / 50) * 50,
                suffix = s => s.photoreal ? "此半徑內優先使用自建精細模型，其餘使用 Google 實景 3D 模型" : "此半徑內使用自建精細模型與 DEM 地形",
            });
            Check(sec, "使用 Google 實景 3D 圖磚", s => s.photoreal, (s, v) => s.photoreal = v);
            Check(sec, "實景模型套用模擬日照（取消則保留照片原始光影）", s => s.relight, (s, v) => s.relight = v);
            Check(sec, "顯示樹木（地標園區）", s => s.trees, (s, v) => s.trees = v);

            var qrow = Row2(sec, "畫質");
            var qs = new List<string> { "省電（低）", "平衡（中）", "精細（高）", "極致（Ultra）" };
            var q = new DropdownField(qs, 2);
            q.RegisterValueChangedCallback(_ => _store.Set(s => s.quality = (Quality)Mathf.Max(0, q.index)));
            qrow.Add(q);
            _syncs.Add(s => { if (q.index != (int)s.quality) q.SetValueWithoutNotify(qs[(int)s.quality]); });

            var status = Cls(new Label(), "info");
            sec.Add(status);
            status.schedule.Execute(() => status.text = _app.Status()).Every(800);

            var krow = Row2(sec, null);
            var key = Field(Config.HasGoogle ? "已設定（輸入新金鑰以更換）" : "貼上 Google Maps Platform 桌面用金鑰");
            key.isPasswordField = true;
            var save = new Button { text = "儲存並重新載入" };
            save.clicked += () =>
            {
                _app.SaveGoogleKey(key.value.Trim());
                key.SetValueWithoutNotify("");
                key.textEdition.placeholder = Config.HasGoogle ? "已設定（輸入新金鑰以更換）" : "貼上 Google Maps Platform 桌面用金鑰";
            };
            krow.Add(key);
            krow.Add(save);
            sec.Add(Cls(new Label(
                "金鑰只存在這台電腦的使用者設定中。需啟用 Map Tiles API（3D／2D 圖磚）、Places API (New) 與 Time Zone API，" +
                "且不可設定 HTTP referrer 限制（請改用 API 限制）。未設定時改用 OpenStreetMap 地圖與 DEM 地形。"), "help"));
        }

        void BuildShare()
        {
            var sec = Section("顯示與分享", false);
            Check(sec, "構圖輔助線（三分法、中心、水平線）", s => s.grid, (s, v) => s.grid = v);
            Button copy = null, paste = null;
            var chips = Chips(sec, new (string, Action)[]
            {
                ("複製分享連結", () =>
                {
                    GUIUtility.systemCopyBuffer = ShareLink.WebBase + "#" + ShareLink.Encode(_store.State);
                    Flash(copy, "已複製 ✓", "複製分享連結");
                }),
                ("貼上連結", () =>
                {
                    var probe = _store.State.Clone();
                    if (ShareLink.TryDecode(GUIUtility.systemCopyBuffer, probe))
                    {
                        _store.Set(s => ShareLink.TryDecode(GUIUtility.systemCopyBuffer, s));
                        Flash(paste, "已套用 ✓", "貼上連結");
                    }
                    else Flash(paste, "剪貼簿沒有分享連結", "貼上連結");
                }),
                ("儲存畫面 PNG", () => _app.Snapshot(false)),
                ("高解析輸出", () => _app.Snapshot(true)),
            });
            var buttons = chips.Children().OfType<Button>().ToList();
            copy = buttons[0];
            paste = buttons[1];
            copy.AddToClassList("primary");
            sec.Add(Cls(new Label(
                "分享連結與網頁版格式相同，可互相開啟。畫面存到「圖片／Framing Buddy」資料夾。\n" +
                "快捷鍵：W/S 前進後退、A/D 左右平移、R/F 升降、方向鍵轉向與俯仰、+/- 變焦、按住 Shift 加速。" +
                "在取景器上拖曳可轉動鏡頭、滾輪可變焦；Cmd+V 貼上分享連結。"), "help"));
        }

        void Flash(Button b, string text, string back)
        {
            if (b == null) return;
            b.text = text;
            b.schedule.Execute(() => b.text = back).StartingIn(1500);
        }
    }
}
