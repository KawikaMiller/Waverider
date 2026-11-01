using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class ParticleVisualizer : IVisualizer
{
    public string Name => "Particles";

    private struct Particle
    {
        public float X, Y, VX, VY, Life, MaxLife, Size, Hue;
    }

    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private double _lastTime;

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        float w = info.Width;
        float h = info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;

        double dt = Math.Min(0.05, Math.Max(0, timeSeconds - _lastTime));
        _lastTime = timeSeconds;

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        primary.ToHsl(out float baseHue, out _, out _);

        // Spawn on beats; bigger beats spawn more, faster particles.
        if (frame.Beat > 0.5f)
        {
            int spawnCount = (int)(10 + frame.Beat * 30);
            for (int i = 0; i < spawnCount; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                float speed = 90f + (float)_rng.NextDouble() * 320f * frame.Beat;
                _particles.Add(new Particle
                {
                    X = cx + (float)(_rng.NextDouble() - 0.5) * 24f,
                    Y = cy + (float)(_rng.NextDouble() - 0.5) * 24f,
                    VX = MathF.Cos(angle) * speed,
                    VY = MathF.Sin(angle) * speed,
                    MaxLife = 1.4f + (float)_rng.NextDouble() * 0.6f,
                    Life = 1.4f + (float)_rng.NextDouble() * 0.6f,
                    Size = 2.5f + (float)_rng.NextDouble() * 4.5f,
                    Hue = baseHue + (float)((timeSeconds * 25 + _rng.NextDouble() * 80) % 360)
                });
            }
        }

        // Trickle of ambient particles driven by mid energy
        if (frame.Mid > 0.25f && _rng.NextDouble() < frame.Mid * 0.6)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float speed = 30f + (float)_rng.NextDouble() * 80f;
            _particles.Add(new Particle
            {
                X = cx,
                Y = cy,
                VX = MathF.Cos(angle) * speed,
                VY = MathF.Sin(angle) * speed,
                MaxLife = 2.0f,
                Life = 2.0f,
                Size = 1.5f + (float)_rng.NextDouble() * 2f,
                Hue = baseHue + (float)(timeSeconds * 40 % 360)
            });
        }

        // Update + cull
        float drag = 0.96f;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.VX * (float)dt;
            p.Y += p.VY * (float)dt;
            p.VX *= drag;
            p.VY *= drag;
            p.Life -= (float)dt;
            if (p.Life <= 0 || p.X < -50 || p.X > w + 50 || p.Y < -50 || p.Y > h + 50)
                _particles.RemoveAt(i);
            else
                _particles[i] = p;
        }
        while (_particles.Count > 900) _particles.RemoveAt(0);

        // Render with additive glow
        using var glow = new SKPaint
        {
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(6, 6),
            BlendMode = SKBlendMode.Plus
        };
        using var core = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus
        };

        // Faint center glow that pulses with bass
        using (var centerGlow = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                MathF.Min(w, h) * 0.25f * (1 + frame.Bass * 0.6f),
                new[] { secondary.WithAlpha((byte)(120 * frame.Bass)), secondary.WithAlpha(0) },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(cx, cy, MathF.Min(w, h) * 0.3f, centerGlow);
        }

        foreach (var p in _particles)
        {
            float alpha = p.Life / p.MaxLife;
            var c = VisualUtil.HsvToRgb(p.Hue, 0.85f, 1f, alpha);
            glow.Color = c.WithAlpha((byte)(alpha * 110));
            core.Color = c;
            canvas.DrawCircle(p.X, p.Y, p.Size + 2.5f, glow);
            canvas.DrawCircle(p.X, p.Y, p.Size, core);
        }
    }
}
