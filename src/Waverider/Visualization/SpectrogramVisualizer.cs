using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed class SpectrogramVisualizer : IVisualizer
{
    public string Name => "Spectrogram";

    private SKBitmap? _scrollBuffer;
    private int _writeColumn;

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        if (info.Width <= 0 || info.Height <= 0) return;
        var bands = frame.Bands;
        if (bands.Length == 0) return;

        if (_scrollBuffer is null || _scrollBuffer.Width != info.Width || _scrollBuffer.Height != bands.Length)
        {
            _scrollBuffer?.Dispose();
            _scrollBuffer = new SKBitmap(info.Width, bands.Length, SKColorType.Bgra8888, SKAlphaType.Premul);
            _writeColumn = 0;
        }

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);

        // Write a single new column
        for (int i = 0; i < bands.Length; i++)
        {
            float v = bands[bands.Length - 1 - i]; // top = high freq
            var c = Lerp(SKColors.Transparent, Lerp(primary, secondary, v), MathF.Pow(v, 0.6f));
            _scrollBuffer.SetPixel(_writeColumn, i, c);
        }
        _writeColumn = (_writeColumn + 1) % info.Width;

        canvas.Clear(SKColors.Transparent);
        // Draw the buffer in two parts so newest column is at the right edge
        var src1 = new SKRect(_writeColumn, 0, info.Width, bands.Length);
        var dst1 = new SKRect(0, 0, info.Width - _writeColumn, info.Height);
        canvas.DrawBitmap(_scrollBuffer, src1, dst1);
        if (_writeColumn > 0)
        {
            var src2 = new SKRect(0, 0, _writeColumn, bands.Length);
            var dst2 = new SKRect(info.Width - _writeColumn, 0, info.Width, info.Height);
            canvas.DrawBitmap(_scrollBuffer, src2, dst2);
        }
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }
}
