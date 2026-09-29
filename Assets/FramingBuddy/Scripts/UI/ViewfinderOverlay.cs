using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 取景框：顯示相機的渲染結果（RenderTexture），疊上構圖輔助線（三分法、中心、水平線）、
    /// 目標標記與畫面外的方向箭頭；拖曳轉動鏡頭、滾輪變焦。
    /// </summary>
    public class ViewfinderOverlay
    {
        readonly Store _store;
        readonly IAppActions _app;
        public VisualElement Element { get; }
        float _w = 1, _h = 1;
        readonly List<Label> _tags = new();
        readonly List<Mark> _marks = new();
        (Vector2 a, Vector2 b)? _horizon;
        bool _level;

        struct Mark
        {
            public Vector2 pos;
            public Color color;
            public bool arrow;
            public float angle;
        }

        public ViewfinderOverlay(Store store, IAppActions app)
        {
            _store = store;
            _app = app;
            Element = new VisualElement();
            Element.AddToClassList("overlay");
            Element.style.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
            Element.style.overflow = Overflow.Hidden;
            Element.generateVisualContent += Draw;
            BindPointer();
        }

        public void SetTexture(RenderTexture rt)
        {
            Element.style.backgroundImage = rt ? Background.FromRenderTexture(rt) : new StyleBackground(StyleKeyword.None);
        }

        public void Resize(float w, float h)
        {
            _w = w;
            _h = h;
            MarkDirty();
        }

        public void MarkDirty() => _dirty = true;
        bool _dirty = true;

        /// <summary>每幀呼叫：相機移動後重新投影標記</summary>
        public void Update()
        {
            if (!_dirty) return;
            _dirty = false;
            Layout();
            Element.MarkDirtyRepaint();
        }

        Label Tag(int i)
        {
            while (_tags.Count <= i)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("tag");
                l.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                Element.Add(l);
                _tags.Add(l);
            }
            return _tags[i];
        }

        void Layout()
        {
            var cam = _app.PhotoCamera;
            _marks.Clear();
            _horizon = null;
            int used = 0;
            if (cam == null)
            {
                foreach (var t in _tags) t.style.display = DisplayStyle.None;
                return;
            }
            var s = _store.State;
            var eye = cam.transform.position;

            if (s.grid)
            {
                // 水平線：方位 ±30° 的遠方水平點
                var pts = new List<Vector2>();
                foreach (float off in new[] { -30f, 30f })
                {
                    float az = (s.azimuth + off) * Mathf.Deg2Rad;
                    var w = eye + new Vector3(Mathf.Sin(az), 0, Mathf.Cos(az)) * 10000;
                    var v = cam.WorldToViewportPoint(w);
                    if (v.z <= 0) break;
                    pts.Add(new Vector2(v.x * _w, (1 - v.y) * _h));
                }
                if (pts.Count == 2) _horizon = (pts[0], pts[1]);
                _level = Mathf.Abs(s.roll) < 0.25f;
            }

            var info = _app.Celestial;
            var sel = _app.Report?.target;
            var inv = CultureInfo.InvariantCulture;
            void Add(string label, Vector3 world, Color color, bool edge)
            {
                var local = cam.transform.InverseTransformPoint(world);
                var v = cam.WorldToViewportPoint(world);
                bool front = local.z > 0;
                bool inFrame = front && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1;
                if (inFrame)
                {
                    var p = new Vector2(v.x * _w, (1 - v.y) * _h);
                    _marks.Add(new Mark { pos = p, color = color });
                    var l = Tag(used++);
                    l.text = label;
                    l.style.color = color;
                    l.style.left = p.x;
                    l.style.top = p.y - 18;
                    l.style.display = DisplayStyle.Flex;
                    return;
                }
                if (!edge) return;
                // 畫面外：在邊緣畫箭頭指向目標
                float dx = local.x, dy = local.y;
                if (!front && Mathf.Abs(dx) + Mathf.Abs(dy) < 1e-6f) dx = 1;
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                dx /= len;
                dy /= len;
                const float margin = 22;
                float hw = _w / 2 - margin, hh = _h / 2 - margin;
                float t = Mathf.Min(Mathf.Abs(hw / (Mathf.Abs(dx) < 1e-9f ? 1e-9f : dx)), Mathf.Abs(hh / (Mathf.Abs(dy) < 1e-9f ? 1e-9f : dy)));
                var ap = new Vector2(_w / 2 + dx * t, _h / 2 - dy * t);
                _marks.Add(new Mark { pos = ap, color = color, arrow = true, angle = Mathf.Atan2(-dy, dx) });
                var tl = Tag(used++);
                tl.text = label;
                tl.style.color = color;
                tl.style.left = Mathf.Clamp(ap.x - dx * 26, 60, _w - 60);
                tl.style.top = Mathf.Clamp(ap.y + dy * 22, 14, _h - 14);
                tl.style.display = DisplayStyle.Flex;
            }

            foreach (var t in _app.Targets())
            {
                bool selected = sel != null && t.id == sel.id;
                var l = Geo.ToLocal(t.at);
                float d = Mathf.Sqrt((float)((l.x - s.x) * (l.x - s.x) + (l.y - s.z) * (l.y - s.z)));
                string km = d >= 1000 ? (d / 1000).ToString("F1", inv) + " km" : Mathf.Round(d) + " m";
                var world = new Vector3((float)l.x, Mathf.Lerp(t.baseY, t.topY, t.aimAt), -(float)l.y);
                Add(selected ? $"{t.label} · {km}" : t.label, world,
                    selected ? new Color(0.5f, 0.89f, 0.78f) : new Color(0.61f, 0.76f, 1f), selected);
            }
            if (info != null)
            {
                if (info.sunAlt > -8) Add($"太陽 {info.sunAlt.ToString("F1", inv)}°", eye + info.sunDir * 20000, new Color(1f, 0.78f, 0.38f), true);
                if (info.moonAlt > -8) Add($"月亮 {Math.Round(info.moonFraction * 100)}%", eye + info.moonDir * 20000, new Color(0.85f, 0.89f, 1f), true);
            }
            for (int i = used; i < _tags.Count; i++) _tags[i].style.display = DisplayStyle.None;
        }

        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var s = _store.State;
            float w = _w, h = _h;
            if (s.grid)
            {
                p.strokeColor = new Color(1, 1, 1, 0.35f);
                p.lineWidth = 1;
                p.BeginPath();
                foreach (float f in new[] { 1f / 3, 2f / 3 })
                {
                    p.MoveTo(new Vector2(w * f, 0));
                    p.LineTo(new Vector2(w * f, h));
                    p.MoveTo(new Vector2(0, h * f));
                    p.LineTo(new Vector2(w, h * f));
                }
                p.Stroke();
                p.strokeColor = new Color(1, 1, 1, 0.6f);
                p.BeginPath();
                p.MoveTo(new Vector2(w / 2 - 8, h / 2));
                p.LineTo(new Vector2(w / 2 + 8, h / 2));
                p.MoveTo(new Vector2(w / 2, h / 2 - 8));
                p.LineTo(new Vector2(w / 2, h / 2 + 8));
                p.Stroke();
                if (_horizon.HasValue)
                {
                    var (a, b) = _horizon.Value;
                    p.strokeColor = _level ? new Color(0.47f, 1f, 0.67f, 0.7f) : new Color(1f, 0.82f, 0.35f, 0.7f);
                    p.lineWidth = 1;
                    var dir = b - a;
                    float len = dir.magnitude;
                    if (len > 1)
                    {
                        dir /= len;
                        // 延長到畫面外再畫虛線
                        var start = a - dir * 4000;
                        p.BeginPath();
                        for (float t = 0; t < len + 8000; t += 12)
                        {
                            p.MoveTo(start + dir * t);
                            p.LineTo(start + dir * (t + 6));
                        }
                        p.Stroke();
                    }
                }
            }
            foreach (var m in _marks)
            {
                if (!m.arrow)
                {
                    p.strokeColor = m.color;
                    p.lineWidth = 1.5f;
                    p.BeginPath();
                    p.Arc(m.pos, 6, Angle.Degrees(0), Angle.Degrees(360));
                    p.Stroke();
                }
                else
                {
                    float c = Mathf.Cos(m.angle), sn = Mathf.Sin(m.angle);
                    Vector2 R(float x, float y) => m.pos + new Vector2(x * c - y * sn, x * sn + y * c);
                    p.fillColor = m.color;
                    p.BeginPath();
                    p.MoveTo(R(10, 0));
                    p.LineTo(R(-6, -7));
                    p.LineTo(R(-6, 7));
                    p.ClosePath();
                    p.Fill();
                }
            }
        }

        // ---- 拖曳轉向、滾輪變焦 ----

        Vector2? _last;

        void BindPointer()
        {
            Element.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                _last = e.position;
                Element.CapturePointer(e.pointerId);
            });
            Element.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (_last == null) return;
                var s = _store.State;
                var fov = Lens.FieldOfView(s.focal, Lens.FrameSize(s.aspect, s.portrait));
                float degPerPt = fov.v / Mathf.Max(1, _h);
                Vector2 pos = e.position;
                var d = pos - _last.Value;
                _last = pos;
                // 「抓住畫面」的拖曳：往右拖＝鏡頭往左轉
                _store.Set(st =>
                {
                    st.azimuth = s.azimuth - d.x * degPerPt;
                    st.pitch = s.pitch + d.y * degPerPt;
                });
            });
            Element.RegisterCallback<PointerUpEvent>(e =>
            {
                _last = null;
                Element.ReleasePointer(e.pointerId);
            });
            Element.RegisterCallback<PointerCaptureOutEvent>(_ => _last = null);
            Element.RegisterCallback<WheelEvent>(e =>
            {
                float k = Mathf.Pow(1.06f, -e.delta.y);
                _store.Set(st => st.focal = st.focal * k);
                e.StopPropagation();
            });
        }
    }
}
