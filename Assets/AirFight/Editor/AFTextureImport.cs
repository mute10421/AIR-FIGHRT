using UnityEditor;
using UnityEngine;

namespace AirFight.EditorTools
{
    /// <summary>Import settings for the generated terrain / cloud textures (normal map type, tiling, filtering).</summary>
    public class AFTextureImport : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains("AirFight/Textures/")) return;
            var ti = (TextureImporter)assetImporter;
            if (assetPath.EndsWith("_n.png") || assetPath.EndsWith("_n.jpg")) ti.textureType = TextureImporterType.NormalMap;
            ti.wrapMode = assetPath.Contains("cloudpuff") ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;
            ti.filterMode = FilterMode.Trilinear;
            ti.maxTextureSize = 1024;
            if (assetPath.Contains("/Photo/"))
            {
                // CC0 aerial photographs (Poly Haven): full 2K on PC, 1K on the headset
                ti.maxTextureSize = 2048;
                var android = ti.GetPlatformTextureSettings("Android");
                android.overridden = true;
                android.maxTextureSize = 1024;
                android.format = TextureImporterFormat.ASTC_6x6;
                ti.SetPlatformTextureSettings(android);
            }
            if (assetPath.Contains("cloudpuff")) ti.alphaIsTransparency = true;
            if (assetPath.EndsWith("/trees.png"))
            {
                // tree impostor atlas: keep the leaf coverage in the small mips (no thinning-out at a distance)
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.5f;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.maxTextureSize = 2048;
            }
        }
    }
}
