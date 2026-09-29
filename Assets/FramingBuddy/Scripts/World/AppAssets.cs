using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 建置時需要一併打包的資產：地標預製物（GLB 匯入後換成 HDRP 材質）、材質範本、UI 樣式。
    /// 由 Editor/ProjectSetup 產生並掛在場景的 App 物件上（確保 shader 變體不被剔除）。
    /// </summary>
    [CreateAssetMenu(menuName = "Framing Buddy/App Assets")]
    public class AppAssets : ScriptableObject
    {
        [Serializable]
        public class LandmarkPrefab
        {
            public string id;
            public GameObject prefab;
        }

        public List<LandmarkPrefab> landmarks = new();

        [Header("材質範本（HDRP/Lit）")]
        public Material terrain;
        public Material water;
        /// <summary>地圖 3D 模式的視錐（HDRP/Unlit，透明）</summary>
        public Material gizmo;

        [Header("夜間發光材質（依名稱，執行時調整 emissive）")]
        public List<Material> nightGlow = new();

        [Header("UI")]
        public StyleSheet styleSheet;
        public ThemeStyleSheet theme;

        public GameObject Landmark(string id) => landmarks.Find(l => l.id == id)?.prefab;
    }
}
