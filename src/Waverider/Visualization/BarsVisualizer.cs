using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class BarsVisualizer : IVisualizer
{
    public string Name => "Bars";

    private float[]? _peakHeight;
    private float[]? _peakVelocity;
    private double _lastTime;

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        var bands = frame.Bands;
        if (bands.Length == 0) return;

        if (_peakHeight is null || _peakHeight.Length != bands.Length)
        {
            _peakHeight = new float[bands.Length];
            _peakVelocity = new float[bands.Length];
        }

        double dt = Math.Min(0.05, Math.Max(0, timeSeconds - _lastTime));
        _lastTime = timeSeconds;

        float w = info.Width;
        float h = info.Height;
        float midY = h * 0.62f;
        float maxBarH = midY * 0.92f;
        float bandW = w / bands.Length;
        float pad = MathF.Max(1f, bandW * 0.18f);

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        var accent = ThemeService.ParseSKColor(theme.Accent);
        var foreground = ThemeService.ParseSKColor(theme.Foreground);

        using var paint = new SKPaint { IsAntialias = true };
        using var peakPaint = new SKPaint
        {
            IsAntialias = true,
            Color = foreground.WithAlpha(220)
        };

        for (int i = 0; i < bands.Length; i++)
        {
            float v = bands[i];
            float bh = MathF.Max(2f, v * maxBarH);
            float x = i * bandW + pad * 0.5f;
            float bw = bandW - pad;

            // Peak marker physics: instantly snap up, fall under gravity.
            if (bh > _peakHeight![i])
            {
                _peakHeight[i] = bh;
                _peakVelocity![i] = 0;
            }
            else
            {
                _peakVelocity![i] += (float)(dt * 280);
                _peakHeight[i] = MathF.Max(0, _peakHeight[i] - _peakVelocity[i] * (float)dt);
            }

            float t = i / (float)bands.Length;
            var bandColor = VisualUtil.Lerp(primary, secondary, t);

            // Main bar (top half)
            var barRect = new SKRect(x, midY - bh, x + bw, midY);
            using (var grad = SKShader.CreateLinearGradient(
                new SKPoint(x, midY - bh), new SKPoint(x, midY),
                new[] { accent.WithAlpha(230), bandColor },
                SKShaderTileMode.Clamp))
            {
                paint.Shader = grad;
                canvas.DrawRoundRect(barRect, 3, 3, paint);
            }

            // Mirrored reflection (faded)
            float mh = bh * 0.55f;
            var mirrorRect = new SKRect(x, midY, x + bw, midY + mh);
            using (var mGrad = SKShader.CreateLinearGradient(
                new SKPoint(x, midY), new SKPoint(x, midY + mh),
                new[] { bandColor.WithAlpha(130), bandColor.WithAlpha(0) },
                SKShaderTileMode.Clamp))
            {
                paint.Shader = mGrad;
                canvas.DrawRoundRect(mirrorRect, 3, 3, paint);
            }

            // Peak marker
            float peakY = midY - _peakHeight[i];
            canvas.DrawRect(x, peakY - 2, bw, 2, peakPaint);
        }

        paint.Shader = null;
        paint.Color = foreground.WithAlpha(40);
        canvas.DrawLine(0, midY, w, midY, paint);
    }
}
