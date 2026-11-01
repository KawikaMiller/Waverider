using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class TunnelVisualizer : IVisualizer
{
    public string Name => "Tunnel";

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        float w = info.Width;
        float h = info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float maxR = MathF.Max(w, h) * 0.7f;

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        var accent = ThemeService.ParseSKColor(theme.Accent);
        var fg = ThemeService.ParseSKColor(theme.Foreground);

        // Bass widens the tunnel, beats kick the rings outward.
        float pulse = frame.Bass * 0.35f + frame.Beat * 0.4f;

        const int Rings = 28;
        const int Sides = 7;
        double speed = 0.18 + frame.Rms * 0.6;

        using var ring = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeJoin = SKStrokeJoin.Round
        };

        // Centre core glow
        using (var core = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                MathF.Min(w, h) * 0.18f,
                new[] { accent.WithAlpha(180), accent.WithAlpha(0) },
                SKShaderTileMode.Clamp),
            BlendMode = SKBlendMode.Plus
        })
        {
            canvas.DrawCircle(cx, cy, MathF.Min(w, h) * 0.22f, core);
        }

        for (int i = 0; i < Rings; i++)
        {
            // t goes from 0 (center) to 1 (edge), animated outward.
            float t = (float)((i / (float)Rings + timeSeconds * speed) % 1f);
            float r = maxR * (t * t) * (1f + pulse);
            float rotation = (float)(timeSeconds * (0.18 + i * 0.04) + i);
            float fade = MathF.Min(1f, t * 4f) * (1f - t);
            float widthAtT = (1f - t) * 6f + 1f;

            var col = i % 3 == 0
                ? VisualUtil.Lerp(primary, accent, t)
                : VisualUtil.Lerp(primary, secondary, t);
            ring.Color = col.WithAlpha((byte)(fade * 230));
            ring.StrokeWidth = widthAtT;

            using var path = new SKPath();
            for (int j = 0; j <= Sides; j++)
            {
                float ang = j * MathF.PI * 2 / Sides + rotation;
                float radial = r * (1f + 0.06f * MathF.Sin(ang * 3 + (float)timeSeconds * 2));
                float x = cx + MathF.Cos(ang) * radial;
                float y = cy + MathF.Sin(ang) * radial;
                if (j == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            canvas.DrawPath(path, ring);
        }

        // Subtle radial scan line driven by treble
        if (frame.Treble > 0.2f)
        {
            using var scan = new SKPaint
            {
                IsAntialias = true,
                Color = fg.WithAlpha((byte)(frame.Treble * 80)),
                StrokeWidth = 1f,
                Style = SKPaintStyle.Stroke,
                BlendMode = SKBlendMode.Plus
            };
            float ang = (float)(timeSeconds * 1.6);
            canvas.DrawLine(cx, cy, cx + MathF.Cos(ang) * maxR, cy + MathF.Sin(ang) * maxR, scan);
        }
    }
}
