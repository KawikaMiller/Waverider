using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class RadialVisualizer : IVisualizer
{
    public string Name => "Radial";

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        var bands = frame.Bands;
        if (bands.Length == 0) return;

        float w = info.Width;
        float h = info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float radius = MathF.Min(w, h) * 0.22f;
        float maxLen = MathF.Min(w, h) * 0.28f;

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);

        using var corePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), radius * 1.1f,
                new[] { secondary.WithAlpha(180), primary.WithAlpha(0) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(cx, cy, radius * 1.1f, corePaint);

        using var spokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = MathF.Max(2f, MathF.PI * radius / bands.Length * 0.8f)
        };

        float rotate = (float)(timeSeconds * 6.0); // degrees per second
        for (int i = 0; i < bands.Length; i++)
        {
            float v = bands[i];
            float len = v * maxLen;
            float angle = (i / (float)bands.Length) * MathF.PI * 2 + rotate * MathF.PI / 180f;
            float x1 = cx + MathF.Cos(angle) * radius;
            float y1 = cy + MathF.Sin(angle) * radius;
            float x2 = cx + MathF.Cos(angle) * (radius + len);
            float y2 = cy + MathF.Sin(angle) * (radius + len);

            float t = i / (float)bands.Length;
            spokePaint.Color = Lerp(primary, secondary, t).WithAlpha((byte)(120 + 135 * v));
            canvas.DrawLine(x1, y1, x2, y2, spokePaint);
        }
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) =>
        new(
            (byte)(a.Red + (b.Red - a.Red) * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
}
