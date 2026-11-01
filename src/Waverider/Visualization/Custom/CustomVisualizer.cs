using System.ComponentModel;
using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization.Custom;

/// <summary>
/// A visualizer assembled from <see cref="CustomVisualizerSettings"/>: a stack of generator layers
/// drawn into a feedback buffer (trails / zoom / rotate), then presented through an optional
/// kaleidoscope. Settings are read every frame, so edits show up live.
/// </summary>
public sealed class CustomVisualizer : IVisualizer, INotifyPropertyChanged, IDisposable
{
    private readonly FeedbackBuffer _buffer = new();
    private double _lastTime;
    private float _hueShift;

    public CustomVisualizer(CustomVisualizerSettings settings)
    {
        Settings = settings;
    }

    public CustomVisualizerSettings Settings { get; }
    public bool IsBuiltIn => Settings.IsBuiltIn;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value) return;
            Settings.Name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        canvas.Clear(SKColors.Transparent);
        if (info.Width <= 0 || info.Height <= 0) return;

        float dt = (float)Math.Clamp(timeSeconds - _lastTime, 0, 0.05);
        _lastTime = timeSeconds;

        var fx = Settings.Fx;
        float retain = Math.Clamp(fx.Get("trails", frame), 0f, 0.99f);
        float zoom = fx.Get("zoom", frame);
        float rotate = fx.Get("rotate", frame);
        _hueShift = (_hueShift + fx.Get("hueCycle", frame) * dt) % 360f;

        // Trail decay is expressed per 1/60s so it looks the same at any frame rate.
        _buffer.Begin(info, MathF.Pow(retain, dt * 60f), zoom * dt, rotate * dt * MathF.PI / 180f);

        var ctx = new LayerContext(_buffer.Canvas, info, frame, theme, (float)timeSeconds, dt, _hueShift);
        foreach (var layer in Settings.Layers)
        {
            if (layer.Enabled) LayerRenderer.Draw(ctx, layer);
        }

        if (fx.Backdrop > 0.01)
        {
            using var backdrop = new SKPaint
            {
                Color = ThemeService.ParseSKColor(theme.Background).WithAlpha((byte)(Math.Clamp(fx.Backdrop, 0, 1) * 255))
            };
            canvas.DrawRect(0, 0, info.Width, info.Height, backdrop);
        }

        using var image = _buffer.Snapshot();
        DrawKaleidoscope(canvas, image, info, Math.Clamp(fx.Kaleidoscope, 1, 16));
    }

    // Mirrored wedges around the center: even wedges are rotated copies, odd wedges are flipped
    // so neighbouring wedges meet seamlessly.
    private static void DrawKaleidoscope(SKCanvas canvas, SKImage image, SKImageInfo info, int segments)
    {
        if (segments <= 1)
        {
            canvas.DrawImage(image, 0, 0);
            return;
        }

        float cx = info.Width * 0.5f;
        float cy = info.Height * 0.5f;
        float reach = MathF.Sqrt(cx * cx + cy * cy) + 4f;
        float step = 360f / segments;

        using var wedge = new SKPath();
        wedge.MoveTo(cx, cy);
        const int Arc = 12;
        for (int i = 0; i <= Arc; i++)
        {
            float a = step * i / Arc * MathF.PI / 180f;
            wedge.LineTo(cx + MathF.Cos(a) * reach, cy + MathF.Sin(a) * reach);
        }
        wedge.Close();

        for (int k = 0; k < segments; k++)
        {
            canvas.Save();
            if (k % 2 == 0)
            {
                canvas.RotateDegrees(k * step, cx, cy);
            }
            else
            {
                canvas.RotateDegrees((k + 1) * step, cx, cy);
                canvas.Translate(cx, cy);
                canvas.Scale(1, -1);
                canvas.Translate(-cx, -cy);
            }
            canvas.ClipPath(wedge, SKClipOperation.Intersect, true);
            canvas.DrawImage(image, 0, 0);
            canvas.Restore();
        }
    }

    public void Dispose() => _buffer.Dispose();
}
