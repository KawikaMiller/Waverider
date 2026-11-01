using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization.Custom;

public readonly record struct LayerContext(
    SKCanvas Canvas, SKImageInfo Info, AudioFrame Frame, Theme Theme, float Time, float Dt, float HueShift);

/// <summary>Resolves a 0..1 position along a layer to a color for the layer's <see cref="ColorMode"/>.</summary>
internal readonly struct Palette
{
    private readonly SKColor _a;
    private readonly SKColor _b;
    private readonly ColorMode _mode;
    private readonly float _hue;
    private readonly float _opacity;

    public Palette(Theme theme, ColorMode mode, float layerHue, float cycle, float opacity)
    {
        _a = ThemeService.ParseSKColor(theme.Primary);
        _b = ThemeService.ParseSKColor(theme.Secondary);
        _mode = mode;
        _hue = layerHue + cycle;
        _opacity = opacity;
    }

    public SKColor At(float t, float alpha = 1f)
    {
        SKColor c;
        switch (_mode)
        {
            case ColorMode.Rainbow:
                c = VisualUtil.HsvToRgb(_hue + t * 300f, 0.85f, 1f);
                break;
            case ColorMode.Solid:
                c = VisualUtil.HsvToRgb(_hue, 0.85f, 1f);
                break;
            default:
                c = VisualUtil.Lerp(_a, _b, t);
                if (MathF.Abs(_hue % 360f) > 0.5f)
                {
                    c.ToHsv(out float h, out float s, out float v);
                    c = VisualUtil.HsvToRgb(h + _hue, s / 100f, v / 100f);
                }
                break;
        }
        return c.WithAlpha((byte)Math.Clamp(alpha * _opacity * 255f, 0f, 255f));
    }
}

internal static class LayerRenderer
{
    public static void Draw(LayerContext c, LayerSettings layer)
    {
        var f = c.Frame;
        var rt = layer.Runtime;
        float w = c.Info.Width;
        float h = c.Info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float m = MathF.Min(w, h);

        float glow = layer.Get("glow", f);
        float spin = layer.Get("spin", f);
        rt.Angle = (rt.Angle + spin * c.Dt) % 360f;
        var pal = new Palette(c.Theme, layer.ColorMode, layer.Get("hue", f), c.HueShift, layer.Get("opacity", f));

        // All generators build their per-frame state first and return a pure draw closure, so the
        // glow pass (blurred layer composited additively) can run it twice without side effects.
        Action<SKCanvas, SKPaint>? draw = layer.Kind switch
        {
            GeneratorKind.Bars => Bars(layer, f, pal, w, h),
            GeneratorKind.Wave => Wave(layer, f, pal, w, h),
            GeneratorKind.Radial => Radial(layer, f, pal, cx, cy, m),
            GeneratorKind.Rings => Rings(layer, f, pal, cx, cy, m),
            GeneratorKind.Scope => Scope(layer, f, pal, cx, cy, m),
            GeneratorKind.Particles => Particles(layer, f, pal, c.Dt, cx, cy, m, w, h),
            GeneratorKind.Blobs => Blobs(layer, f, pal, c.Time, cx, cy, w, h, m),
            _ => null
        };
        if (draw is null) return;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            BlendMode = layer.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver
        };

        var canvas = c.Canvas;
        canvas.Save();
        canvas.RotateDegrees(rt.Angle, cx, cy);

        if (glow > 0.02f)
        {
            float sigma = 2f + glow * 12f;
            using var glowPaint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
                BlendMode = SKBlendMode.Plus,
                Color = SKColors.White.WithAlpha((byte)(Math.Min(1f, glow * 1.2f) * 255))
            };
            canvas.SaveLayer(glowPaint);
            draw(canvas, paint);
            canvas.Restore();
        }
        draw(canvas, paint);
        canvas.Restore();
    }

    // Resample the 64 spectrum bands to n values, taking the peak of each group.
    private static float[] Resample(float[] src, int n)
    {
        var dst = new float[n];
        if (src.Length == 0) return dst;
        for (int i = 0; i < n; i++)
        {
            int lo = i * src.Length / n;
            int hi = Math.Max(lo + 1, (i + 1) * src.Length / n);
            float peak = 0;
            for (int j = lo; j < hi && j < src.Length; j++) peak = MathF.Max(peak, src[j]);
            dst[i] = peak;
        }
        return dst;
    }

    private static Action<SKCanvas, SKPaint> Bars(LayerSettings l, AudioFrame f, Palette pal, float w, float h)
    {
        int n = Math.Clamp((int)l.Get("count", f), 3, 128);
        float size = l.Get("size", f);
        float thick = l.Get("thickness", f);
        float amp = l.Get("amplitude", f);
        var vals = Resample(f.Bands, n);
        return (cv, paint) =>
        {
            float slot = w / n;
            float bw = MathF.Min(thick, slot * 0.9f);
            float maxHalf = size * h * 0.5f;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = bw;
            for (int i = 0; i < n; i++)
            {
                float v = vals[i];
                float len = MathF.Max(1f, v * amp * maxHalf);
                float x = slot * (i + 0.5f);
                paint.Color = pal.At(i / (float)(n - 1), 0.4f + 0.6f * v);
                cv.DrawLine(x, h * 0.5f - len, x, h * 0.5f + len, paint);
            }
        };
    }

    private static Action<SKCanvas, SKPaint>? Wave(LayerSettings l, AudioFrame f, Palette pal, float w, float h)
    {
        var samples = f.Samples;
        if (samples.Length == 0) return null;
        float size = l.Get("size", f);
        float amp = l.Get("amplitude", f);
        float thick = l.Get("thickness", f);
        const int Points = 240;
        return (cv, paint) =>
        {
            using var path = new SKPath();
            for (int j = 0; j < Points; j++)
            {
                float u = j / (float)(Points - 1);
                float s = samples[(int)(u * (samples.Length - 1))];
                float x = u * w;
                float y = h * 0.5f + s * amp * size * h * 1.5f;
                if (j == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[] { pal.At(0f), pal.At(0.5f), pal.At(1f) }, SKShaderTileMode.Clamp);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thick;
            paint.Shader = shader;
            cv.DrawPath(path, paint);
            paint.Shader = null;
        };
    }

    private static Action<SKCanvas, SKPaint> Radial(LayerSettings l, AudioFrame f, Palette pal, float cx, float cy, float m)
    {
        int n = Math.Clamp((int)l.Get("count", f), 3, 128);
        float r0 = l.Get("size", f) * m * 0.5f;
        float amp = l.Get("amplitude", f);
        float thick = l.Get("thickness", f);
        // Mirror the spectrum around the circle so the seam at angle 0 has no jump.
        var half = Resample(f.Bands, n / 2 + 1);
        return (cv, paint) =>
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thick;
            for (int i = 0; i < n; i++)
            {
                float v = half[i <= n / 2 ? i : n - i];
                float len = MathF.Max(1f, v * amp * m * 0.35f);
                float a = i / (float)n * MathF.Tau;
                float cos = MathF.Cos(a), sin = MathF.Sin(a);
                paint.Color = pal.At(i / (float)n, 0.4f + 0.6f * v);
                cv.DrawLine(cx + cos * r0, cy + sin * r0, cx + cos * (r0 + len), cy + sin * (r0 + len), paint);
            }
        };
    }

    private static Action<SKCanvas, SKPaint> Rings(LayerSettings l, AudioFrame f, Palette pal, float cx, float cy, float m)
    {
        int n = Math.Clamp((int)l.Get("count", f), 1, 40);
        float rMax = l.Get("size", f) * m * 0.5f;
        float amp = l.Get("amplitude", f);
        float thick = l.Get("thickness", f);
        var vals = Resample(f.Bands, n);
        return (cv, paint) =>
        {
            paint.Style = SKPaintStyle.Stroke;
            for (int i = 0; i < n; i++)
            {
                float v = vals[i];
                float r = rMax * (i + 1) / n + v * amp * m * 0.06f;
                paint.StrokeWidth = thick * (0.5f + v);
                paint.Color = pal.At(n == 1 ? 0f : i / (float)(n - 1), 0.3f + 0.7f * v);
                cv.DrawCircle(cx, cy, r, paint);
            }
        };
    }

    private static Action<SKCanvas, SKPaint>? Scope(LayerSettings l, AudioFrame f, Palette pal, float cx, float cy, float m)
    {
        var samples = f.Samples;
        if (samples.Length == 0) return null;
        float r0 = l.Get("size", f) * m * 0.5f;
        float amp = l.Get("amplitude", f);
        float thick = l.Get("thickness", f);
        const int Points = 180;
        return (cv, paint) =>
        {
            using var path = new SKPath();
            for (int j = 0; j < Points; j++)
            {
                float u = j / (float)Points;
                float s = samples[(int)(u * (samples.Length - 1))];
                float r = r0 + s * amp * m * 0.5f;
                float a = u * MathF.Tau;
                float x = cx + MathF.Cos(a) * r, y = cy + MathF.Sin(a) * r;
                if (j == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
            using var shader = SKShader.CreateSweepGradient(
                new SKPoint(cx, cy),
                new[] { pal.At(0f), pal.At(0.33f), pal.At(0.66f), pal.At(1f), pal.At(0f) },
                null);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thick;
            paint.Shader = shader;
            cv.DrawPath(path, paint);
            paint.Shader = null;
        };
    }

    private static Action<SKCanvas, SKPaint> Particles(
        LayerSettings l, AudioFrame f, Palette pal, float dt, float cx, float cy, float m, float w, float h)
    {
        var rt = l.Runtime;
        var rng = Random.Shared;
        float n = l.Get("count", f);
        float spawnRadius = l.Get("size", f) * m * 0.5f;
        float amp = l.Get("amplitude", f);
        float radius = l.Get("thickness", f);

        // Bursts on beats, plus a trickle driven by mid energy.
        rt.SpawnAccumulator += n * (f.Beat * dt * 60f * 0.15f + f.Mid * dt * 0.6f);
        int spawn = (int)rt.SpawnAccumulator;
        rt.SpawnAccumulator -= spawn;
        for (int i = 0; i < spawn && rt.Particles.Count < 800; i++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float r = (float)rng.NextDouble() * spawnRadius;
            float speed = m * amp * (0.15f + (float)rng.NextDouble() * 0.45f) * (0.5f + f.Beat);
            float life = 1.2f + (float)rng.NextDouble() * 0.8f;
            rt.Particles.Add(new Particle
            {
                X = cx + MathF.Cos(a) * r,
                Y = cy + MathF.Sin(a) * r,
                VX = MathF.Cos(a) * speed,
                VY = MathF.Sin(a) * speed,
                Life = life,
                MaxLife = life,
                T = (float)rng.NextDouble()
            });
        }

        float drag = MathF.Pow(0.97f, dt * 60f);
        var list = rt.Particles;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var p = list[i];
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.VX *= drag;
            p.VY *= drag;
            p.Life -= dt;
            if (p.Life <= 0 || p.X < -50 || p.X > w + 50 || p.Y < -50 || p.Y > h + 50)
                list.RemoveAt(i);
            else
                list[i] = p;
        }

        return (cv, paint) =>
        {
            paint.Style = SKPaintStyle.Fill;
            foreach (var p in list)
            {
                float life = p.Life / p.MaxLife;
                paint.Color = pal.At(p.T, life);
                cv.DrawCircle(p.X, p.Y, MathF.Max(0.5f, radius * (0.4f + life * 0.6f)), paint);
            }
        };
    }

    private static Action<SKCanvas, SKPaint> Blobs(
        LayerSettings l, AudioFrame f, Palette pal, float time, float cx, float cy, float w, float h, float m)
    {
        int n = Math.Clamp((int)l.Get("count", f), 1, 12);
        float size = l.Get("size", f);
        float amp = l.Get("amplitude", f);
        var vals = Resample(f.Bands, n);
        return (cv, paint) =>
        {
            paint.Style = SKPaintStyle.Fill;
            for (int i = 0; i < n; i++)
            {
                float phase = i * 2.399f; // golden angle keeps orbs spread out
                float x = cx + MathF.Sin(time * (0.21f + 0.03f * i) + phase) * w * 0.32f;
                float y = cy + MathF.Cos(time * (0.27f + 0.02f * i) + phase * 1.3f) * h * 0.32f;
                float r = m * size * (0.35f + vals[i] * amp * 0.9f);
                var color = pal.At(n == 1 ? 0f : i / (float)(n - 1));
                using var shader = SKShader.CreateRadialGradient(
                    new SKPoint(x, y), MathF.Max(1f, r),
                    new[] { color, color.WithAlpha(0) }, SKShaderTileMode.Clamp);
                paint.Shader = shader;
                cv.DrawCircle(x, y, r, paint);
            }
            paint.Shader = null;
        };
    }
}
