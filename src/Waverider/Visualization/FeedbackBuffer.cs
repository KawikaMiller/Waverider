using SkiaSharp;

namespace Waverider.Visualization;

/// <summary>
/// Offscreen "previous frame" buffer used for trails / feedback effects (the Milkdrop / WMP
/// Alchemy look). Each <see cref="Begin"/> redraws the last frame faded, zoomed and rotated
/// about the center; the caller then draws new shapes on <see cref="Canvas"/>.
/// </summary>
public sealed class FeedbackBuffer : IDisposable
{
    private SKSurface? _surface;
    private int _width;
    private int _height;

    public SKCanvas Canvas => _surface!.Canvas;

    /// <param name="retain">Fraction of the previous frame's alpha that survives (0 = no trail).</param>
    /// <param name="zoom">Scale delta applied to the previous frame (0.02 = grows 2%).</param>
    /// <param name="rotateRadians">Rotation applied to the previous frame.</param>
    public void Begin(SKImageInfo info, float retain, float zoom, float rotateRadians)
    {
        if (_surface is null || _width != info.Width || _height != info.Height)
        {
            _surface?.Dispose();
            _width = info.Width;
            _height = info.Height;
            _surface = SKSurface.Create(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _surface.Canvas.Clear(SKColors.Transparent);
            return;
        }

        if (retain <= 0.01f)
        {
            _surface.Canvas.Clear(SKColors.Transparent);
            return;
        }

        using var previous = _surface.Snapshot();
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        canvas.Save();
        canvas.Translate(_width * 0.5f, _height * 0.5f);
        canvas.Scale(1f + zoom);
        canvas.RotateRadians(rotateRadians);
        canvas.Translate(-_width * 0.5f, -_height * 0.5f);

        using var fade = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                1, 0, 0, 0, 0,
                0, 1, 0, 0, 0,
                0, 0, 1, 0, 0,
                0, 0, 0, retain, 0
            })
        };
        canvas.DrawImage(previous, 0, 0, fade);
        canvas.Restore();
    }

    public SKImage Snapshot() => _surface!.Snapshot();

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}
