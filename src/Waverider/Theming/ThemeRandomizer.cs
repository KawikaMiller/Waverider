using SkiaSharp;

namespace Waverider.Theming;

/// <summary>Builds coherent random palettes (hue-based, with panel/stage contrast baked in).</summary>
public static class ThemeRandomizer
{
    public static Theme Starter() => new()
    {
        Backdrop = "None",
        ElementTheme = "Dark",
        Accent = "#1DB954",
        Primary = "#1DB954",
        Secondary = "#7ED9A0",
        Background = "#121614",
        Surface = "#1C211E",
        Foreground = "#F2F5F3",
        Muted = "#9AA39D",
        Panel = "#1C211E",
        Stage = "#070A08",
        Stroke = "#313833"
    };

    /// <summary>Rewrites every color of <paramref name="theme"/> in place (the name is kept).</summary>
    public static void Randomize(Theme theme, Random rng)
    {
        bool dark = rng.NextDouble() < 0.7;
        float hue = (float)(rng.NextDouble() * 360);
        // Second hue: analogous or roughly complementary.
        float offset = rng.NextDouble() < 0.5 ? 30 + (float)rng.NextDouble() * 40 : 150 + (float)rng.NextDouble() * 60;
        float hue2 = (hue + offset) % 360;
        float tint = 6 + (float)rng.NextDouble() * 10; // saturation of the neutral surfaces
        float R(float min, float span) => min + (float)rng.NextDouble() * span;

        theme.ElementTheme = dark ? "Dark" : "Light";
        theme.Backdrop = "None";

        if (dark)
        {
            theme.Background = Hsv(hue, tint, R(11, 6));
            theme.Surface = Hsv(hue, tint, R(17, 6));
            theme.Panel = theme.Surface;
            theme.Stage = Hsv(hue, tint, R(4, 4));
            theme.Stroke = Hsv(hue, tint * 0.8f, R(28, 8));
            theme.Foreground = Hsv(hue, 4, 96);
            theme.Muted = Hsv(hue, 8, 65);
            theme.Accent = Hsv(hue, R(70, 25), R(85, 15));
            theme.Primary = theme.Accent;
            theme.Secondary = Hsv(hue2, R(55, 30), R(90, 10));
        }
        else
        {
            theme.Background = Hsv(hue, tint, R(78, 6));
            theme.Surface = Hsv(hue, tint * 0.6f, R(90, 5));
            theme.Panel = theme.Surface;
            theme.Stage = Hsv(hue, tint * 1.5f, R(6, 6));
            theme.Stroke = Hsv(hue, tint, R(66, 6));
            theme.Foreground = Hsv(hue, 20, 12);
            theme.Muted = Hsv(hue, 15, 38);
            theme.Accent = Hsv(hue, R(80, 15), R(52, 12));
            theme.Primary = Hsv(hue, R(75, 20), R(80, 15));
            theme.Secondary = Hsv(hue2, R(50, 30), R(85, 12));
        }
    }

    // h: 0..360, s/v: 0..100
    private static string Hsv(float h, float s, float v)
    {
        var c = SKColor.FromHsv(h, Math.Clamp(s, 0, 100), Math.Clamp(v, 0, 100));
        return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
    }
}
