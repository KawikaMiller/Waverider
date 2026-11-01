using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

// Classic demoscene plasma: multiple sine waves combined per pixel, palette-mapped
// against the theme. Rendered to a small backing buffer (192×108) and bilinearly
// scaled up to the canvas — keeps the per-frame cost tractable.
public sealed class PlasmaVisualizer : IVisualizer
{
    public string Name => "Plasma";

    private const int BufW = 192;
    private const int BufH = 108;
    private SKBitmap? _buffer;

    public unsafe void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        if (_buffer is null)
            _buffer = new SKBitmap(BufW, BufH, SKColorType.Bgra8888, SKAlphaType.Premul);

        var primary = ThemeService.ParseSKColor(theme.Primary);
        var secondary = ThemeService.ParseSKColor(theme.Secondary);
        var accent = ThemeService.ParseSKColor(theme.Accent);
        var background = ThemeService.ParseSKColor(theme.Background);

        float t = (float)timeSeconds;
        float bass = frame.Bass;
        float treble = frame.Treble;

        // Bass adds spatial frequency; treble adds time speed.
        float spatial = 6f + bass * 6f;
        float timeFactor = 1f + treble * 1.3f;

        var pixels = (uint*)_buffer.GetPixels().ToPointer();
        for (int y = 0; y < BufH; y++)
        {
            float fy = y / (float)BufH - 0.5f;
            for (int x = 0; x < BufW; x++)
            {
                float fx = x / (float)BufW - 0.5f;

                float v = MathF.Sin(fx * spatial + t * 0.7f * timeFactor)
                        + MathF.Sin(fy * spatial + t * 1.1f * timeFactor)
                        + MathF.Sin((fx + fy) * spatial * 0.7f + t * 0.5f * timeFactor)
                        + MathF.Sin(MathF.Sqrt(fx * fx + fy * fy) * spatial * 1.6f + t * 0.9f);

                float n = (v + 4f) / 8f;
                n = MathF.Pow(n, 1.2f);

                SKColor c = n < 0.5f
                    ? VisualUtil.Lerp(background, primary, n * 2f)
                    : n < 0.8f
                        ? VisualUtil.Lerp(primary, accent, (n - 0.5f) * 3.33f)
                        : VisualUtil.Lerp(accent, secondary, (n - 0.8f) * 5f);

                byte r = c.Red, gr = c.Green, b = c.Blue;
                pixels[y * BufW + x] = 0xFF000000u | ((uint)r << 16) | ((uint)gr << 8) | b;
            }
        }

        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(_buffer);
        canvas.DrawImage(image,
            new SKRect(0, 0, info.Width, info.Height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }
}
