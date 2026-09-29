using System;
using System.Collections.Generic;
using UnityEngine;

namespace FramingBuddy
{
    /// <summary>目前時刻的天體資訊（由 Celestial 計算，面板、HUD、地圖讀取）。</summary>
    public class CelestialInfo
    {
        public double sunAz, sunAlt;
        public double moonAz, moonAlt;
        public double moonFraction, moonPhase;
        /// <summary>0＝白天、1＝全黑夜（依太陽仰角平滑過渡）</summary>
        public float night;
        public Dictionary<string, DateTime> times = new();
        /// <summary>太陽、月亮方向（Unity 世界座標，單位向量）</summary>
        public Vector3 sunDir = Vector3.up, moonDir = Vector3.up;
    }

    /// <summary>資訊列用的目標可見度。</summary>
    public class VisibilityReport
    {
        public Target target;
        /// <summary>未被遮擋的比例（0–1；不在畫面內也會計算）</summary>
        public float visible;
        /// <summary>目標在畫面中的垂直佔比（在相機後方時為 null）</summary>
        public float? frameFraction;
        public bool inFrame;
    }

    /// <summary>UI 需要的應用程式操作（由 App 實作）。</summary>
    public interface IAppActions
    {
        IReadOnlyList<SiteData.Preset> Presets { get; }
        void ApplyPreset(SiteData.Preset p);
        void GoTo(LatLon p, Action<ShotState> patch = null);
        /// <summary>目標 id，或 "sun"、"moon"</summary>
        void Aim(string id);
        void AimAt(LatLon p);
        List<Target> Targets();
        /// <summary>相機實際位置（Unity 世界座標）</summary>
        Vector3 Eye { get; }
        /// <summary>站立面高度（場景 y，相對原點地面）</summary>
        float Surface { get; }
        float OriginElevation { get; }
        CelestialInfo Celestial { get; }
        VisibilityReport Report { get; }
        /// <summary>測光結果說明（HUD 用）</summary>
        string MeterText { get; }
        void Snapshot(bool highRes);
        string Status();
        bool Busy { get; }
        string Attribution();
        void SaveGoogleKey(string key);
        Camera PhotoCamera { get; }
    }
}
