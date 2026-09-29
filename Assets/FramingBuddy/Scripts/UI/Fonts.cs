using System.IO;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FramingBuddy
{
    /// <summary>
    /// 介面字型：Unity 內建字型沒有中文，執行時從 macOS 系統字型建立動態 SDF 字型
    /// （不把系統字型打包進 App，字型授權留在系統上）。
    /// </summary>
    public static class Fonts
    {
        static FontAsset _ui;

        static readonly (string path, int face)[] Candidates =
        {
            ("/System/Library/Fonts/STHeiti Medium.ttc", 0),      // 黑體-繁 中
            ("/System/Library/Fonts/STHeiti Light.ttc", 0),
            ("/System/Library/Fonts/Hiragino Sans GB.ttc", 0),
            ("/System/Library/Fonts/Supplemental/Arial Unicode.ttf", 0),
            ("C:/Windows/Fonts/msjh.ttc", 0),                     // Windows：微軟正黑體
        };

        public static FontAsset Ui
        {
            get
            {
                if (_ui != null) return _ui;
                foreach (var (path, face) in Candidates)
                {
                    if (!File.Exists(path)) continue;
                    _ui = Create(path, face);
                    if (_ui != null)
                    {
                        Debug.Log($"[ui] 字型：{Path.GetFileName(path)}#{face}");
                        break;
                    }
                }
                if (_ui == null) _ui = FontAsset.CreateFontAsset("PingFang TC", "Regular");
                return _ui;
            }
        }

        static FontAsset Create(string path, int face)
        {
            var fa = FontAsset.CreateFontAsset(path, face, 64, 6, GlyphRenderMode.SDFAA, 2048, 2048);
            if (fa == null) return null;
            fa.isMultiAtlasTexturesEnabled = true;
            fa.name = Path.GetFileNameWithoutExtension(path);
            return fa;
        }

        public static void Apply(VisualElement root)
        {
            var fa = Ui;
            if (fa != null) root.style.unityFontDefinition = FontDefinition.FromSDFFont(fa);
        }
    }
}
