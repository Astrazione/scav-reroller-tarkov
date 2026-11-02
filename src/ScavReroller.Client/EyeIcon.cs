using UnityEngine;

namespace ScavReroller.Client;

internal static class EyeIcon
{
    // Built once for the menu button; no bundled assets or per-frame drawing work.
    internal static Texture2D Create()
    {
        const int size = 48;
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = (x + 0.5f - size * 0.5f) / (size * 0.42f);
            var dy = (y + 0.5f - size * 0.5f) / (size * 0.24f);
            var outline = Mathf.Abs(dx) <= 1f && Mathf.Abs(Mathf.Abs(dy) - (1f - dx * dx)) < 0.12f;
            var radius = Mathf.Sqrt(dx * dx + dy * dy * 0.3265f);
            var pupil = radius < 0.17f;
            var iris = Mathf.Abs(radius - 0.34f) < 0.055f;
            if (outline || pupil || iris) pixels[y * size + x] = new Color32(240, 240, 240, 255);
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ScavReroller.Eye",
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }
}
