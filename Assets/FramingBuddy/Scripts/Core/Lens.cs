using System;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>
    /// 全片幅等效焦段 → 視角。畫面比例以「從 36×24 mm 感光元件裁切」計算，
    /// 與在全片幅機身上切換長寬比的實際結果一致。
    /// </summary>
    public static class Lens
    {
        public struct Aspect
        {
            public string id;
            public string label;
            public float w;
            public float h;
        }

        public static readonly Aspect[] Aspects =
        {
            new Aspect { id = "3:2", label = "3:2（全片幅原生）", w = 36f, h = 24f },
            new Aspect { id = "4:3", label = "4:3", w = 32f, h = 24f },
            new Aspect { id = "5:4", label = "5:4（直幅即 IG 4:5）", w = 30f, h = 24f },
            new Aspect { id = "1:1", label = "1:1", w = 24f, h = 24f },
            new Aspect { id = "16:9", label = "16:9", w = 36f, h = 20.25f },
            new Aspect { id = "65:24", label = "65:24（XPan 寬景）", w = 36f, h = 13.29f },
        };

        public const float FocalMin = 8f;
        public const float FocalMax = 1200f;
        public static readonly int[] FocalPresets = { 14, 16, 20, 24, 28, 35, 50, 70, 85, 105, 135, 200, 300, 400, 600, 800 };
        public static readonly float[] Apertures = { 1.4f, 1.8f, 2f, 2.8f, 4f, 5.6f, 8f, 11f, 16f, 22f };

        public static Aspect Find(string id)
        {
            foreach (var a in Aspects) if (a.id == id) return a;
            return Aspects[0];
        }

        public static string AspectText(string id, bool portrait)
        {
            var p = id.Split(':');
            return portrait && p.Length == 2 && p[0] != p[1] ? p[1] + ":" + p[0] : id;
        }

        /// <summary>感光範圍（mm）</summary>
        public static Vector2 FrameSize(string aspect, bool portrait)
        {
            var a = Find(aspect);
            return portrait ? new Vector2(a.h, a.w) : new Vector2(a.w, a.h);
        }

        public struct Fov
        {
            public float h, v, d;
        }

        public static Fov FieldOfView(float focal, Vector2 frame)
        {
            float diag = Mathf.Sqrt(frame.x * frame.x + frame.y * frame.y);
            return new Fov
            {
                h = 2f * Mathf.Atan(frame.x / (2f * focal)) * Mathf.Rad2Deg,
                v = 2f * Mathf.Atan(frame.y / (2f * focal)) * Mathf.Rad2Deg,
                d = 2f * Mathf.Atan(diag / (2f * focal)) * Mathf.Rad2Deg,
            };
        }

        // 焦段滑桿採對數刻度
        public static float FocalToSlider(float f) => Mathf.Log(f / FocalMin) / Mathf.Log(FocalMax / FocalMin) * 1000f;

        public static float SliderToFocal(float v)
        {
            float f = FocalMin * Mathf.Pow(FocalMax / FocalMin, v / 1000f);
            return f < 100 ? Mathf.Round(f) : Mathf.Round(f / 5f) * 5f;
        }

        /// <summary>超焦距（m），容許模糊圈 0.03 mm</summary>
        public static float Hyperfocal(float focal, float fNumber) => focal * focal / (fNumber * 0.03f) / 1000f + focal / 1000f;

        public static string ShutterText(double seconds)
        {
            if (seconds >= 1) return Math.Round(seconds, 1) + "s";
            return "1/" + Math.Round(1 / seconds);
        }
    }
}
