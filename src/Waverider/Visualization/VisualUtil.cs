using SkiaSharp;

namespace Waverider.Visualization;

internal static class VisualUtil
{
    public static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    public static SKColor HsvToRgb(float h, float s, float v, float alpha = 1f)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = v * s;
        float x = c * (1 - MathF.Abs(((h / 60f) % 2) - 1));
        float m = v - c;
        float r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return new SKColor(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255),
            (byte)(Math.Clamp(alpha, 0f, 1f) * 255));
    }
}
