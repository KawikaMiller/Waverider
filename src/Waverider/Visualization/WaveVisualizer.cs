using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class WaveVisualizer : IVisualizer
{
    public string Name => "Waveform";

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        var samples = frame.Samples;
        if (samples.Length == 0) return;

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        var accent = ThemeService.ParseSKColor(theme.Accent);

        float w = info.Width;
        float h = info.Height;
        float midY = h * 0.5f;
        float amp = (1.4f + frame.Bass * 0.8f) * h * 0.4f;

        // Four phase-shifted layers, fading out. The deepest layer is widest +
        // most transparent; the top layer is the crisp current waveform.
        const int Layers = 4;
        for (int layer = Layers - 1; layer >= 0; layer--)
        {
            float t = layer / (float)Layers;
            float phaseShift = layer * 0.07f;
            float yOffset = (layer - (Layers - 1) * 0.5f) * 6f;
            float strokeWidth = 4.5f - layer * 0.85f;
            byte alpha = (byte)((1f - t * 0.7f) * 255);

            using var path = new SKPath();
            for (int x = 0; x < (int)w; x++)
            {
                int idx = (int)((x / w + phaseShift) * (samples.Length - 1)) % samples.Length;
                if (idx < 0) idx += samples.Length;
                float v = samples[idx];
                float y = midY + yOffset + v * amp * (1f - t * 0.25f);
                if (x == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }

            using var grad = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, 0),
                new[]
                {
                    primary.WithAlpha(alpha),
                    accent.WithAlpha(alpha),
                    secondary.WithAlpha(alpha)
                },
                SKShaderTileMode.Clamp);

            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeWidth = strokeWidth,
                Shader = grad
            };
            if (layer == Layers - 1)
            {
                stroke.ImageFilter = SKImageFilter.CreateBlur(8, 8);
                stroke.StrokeWidth = strokeWidth * 2;
            }
            canvas.DrawPath(path, stroke);
        }
    }
}
