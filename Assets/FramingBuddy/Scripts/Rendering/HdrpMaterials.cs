using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace FramingBuddy
{
    /// <summary>HDRP/Lit 材質的常用設定（屬性名稱與關鍵字依 HDRP 17）。</summary>
    public static class HdrpMaterials
    {
        static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int NormalMap = Shader.PropertyToID("_NormalMap");
        static readonly int NormalScale = Shader.PropertyToID("_NormalScale");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int Metallic = Shader.PropertyToID("_Metallic");

        public static Shader Lit => Shader.Find("HDRP/Lit");

        /// <summary>設定顏色貼圖、法線圖與光滑度（地形、山體）</summary>
        public static void SetLit(Material mat, Texture2D albedo, Texture2D normal, float smoothness, float normalScale = 1f)
        {
            mat.SetTexture(BaseColorMap, albedo);
            mat.SetColor(BaseColor, Color.white);
            if (normal != null)
            {
                mat.SetTexture(NormalMap, normal);
                mat.SetFloat(NormalScale, normalScale);
                mat.EnableKeyword("_NORMALMAP");
                mat.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            mat.SetFloat(Smoothness, smoothness);
            mat.SetFloat(Metallic, 0);
            HDMaterial.ValidateMaterial(mat);
        }

        /// <summary>發光（夜間燈光）：color 為線性色、nits 為亮度</summary>
        public static void SetEmissive(Material mat, Color color, float nits)
        {
            HDMaterial.SetUseEmissiveIntensity(mat, true);
            HDMaterial.SetEmissiveColor(mat, color);
            HDMaterial.SetEmissiveIntensity(mat, nits, EmissiveIntensityUnit.Nits);
            HDMaterial.ValidateMaterial(mat);
        }
    }
}
