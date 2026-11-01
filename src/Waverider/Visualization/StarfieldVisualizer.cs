using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class StarfieldVisualizer : IVisualizer
{
    public string Name => "Starfield";

    private struct Star { public float X, Y, Z, PrevX, PrevY; }
    private readonly Star[] _stars = new Star[450];
    private readonly Random _rng = new();
    private bool _initialized;
    private double _lastTime;

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        float w = info.Width;
        float h = info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float fovX = cx;
        float fovY = cy;

        if (!_initialized)
        {
            for (int i = 0; i < _stars.Length; i++)
            {
                _stars[i] = new Star
                {
                    X = (float)(_rng.NextDouble() * 2 - 1) * 2,
                    Y = (float)(_rng.NextDouble() * 2 - 1) * 2,
                    Z = (float)_rng.NextDouble() * 0.95f + 0.05f
                };
            }
            _initialized = true;
        }

        double dt = Math.Min(0.05, Math.Max(0.001, timeSeconds - _lastTime));
        _lastTime = timeSeconds;

        // Warp speed grows with overall energy + spikes on beats.
        float speed = 0.4f + frame.Rms * 9f + frame.Beat * 2.5f;

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        var accent = ThemeService.ParseSKColor(theme.Accent);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            BlendMode = SKBlendMode.Plus
        };

        for (int i = 0; i < _stars.Length; i++)
        {
            var s = _stars[i];
            s.PrevX = s.X / s.Z * fovX + cx;
            s.PrevY = s.Y / s.Z * fovY + cy;
            s.Z -= speed * (float)dt * 0.6f;

            if (s.Z <= 0.02f
                || s.X / s.Z * fovX + cx < -20 || s.X / s.Z * fovX + cx > w + 20
                || s.Y / s.Z * fovY + cy < -20 || s.Y / s.Z * fovY + cy > h + 20)
            {
                s.X = (float)(_rng.NextDouble() * 2 - 1) * 2;
                s.Y = (float)(_rng.NextDouble() * 2 - 1) * 2;
                s.Z = 1f;
                s.PrevX = s.X / s.Z * fovX + cx;
                s.PrevY = s.Y / s.Z * fovY + cy;
            }

            float px = s.X / s.Z * fovX + cx;
            float py = s.Y / s.Z * fovY + cy;

            float brightness = MathF.Min(1f, (1f - s.Z) * 1.2f);
            float thickness = brightness * 3f + 0.4f;

            // Color cycles through theme palette as star approaches camera.
            SKColor c = brightness < 0.5f
                ? VisualUtil.Lerp(secondary, primary, brightness * 2f)
                : VisualUtil.Lerp(primary, accent, (brightness - 0.5f) * 2f);
            paint.Color = c.WithAlpha((byte)(brightness * 255));
            paint.StrokeWidth = thickness;
            canvas.DrawLine(s.PrevX, s.PrevY, px, py, paint);

            _stars[i] = s;
        }

        // Center "warp" glow pulses with bass
        using var core = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                MathF.Min(w, h) * 0.18f * (1f + frame.Bass * 0.5f),
                new[] { accent.WithAlpha((byte)(80 + frame.Beat * 120)), accent.WithAlpha(0) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(cx, cy, MathF.Min(w, h) * 0.25f, core);
    }
}
