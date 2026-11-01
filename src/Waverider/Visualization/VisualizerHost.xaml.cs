using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp.Views.Windows;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public sealed partial class VisualizerHost : UserControl
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private AudioFrame _latestFrame = new(Array.Empty<float>(), Array.Empty<float>(), new float[64], 0, 0, 0, 0, 0, 0, 48000, 0);
    private IVisualizer _visualizer = new BarsVisualizer();
    private Theme _theme = new();
    private DispatcherTimer? _redrawTimer;

    private bool _paused;

    public VisualizerHost()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_paused) StartRedrawTimer(); };
        Unloaded += (_, _) => StopRedrawTimer();
    }

    /// <summary>
    /// Stops the redraw loop without removing the view. The visualizer repaints on the UI thread and can
    /// saturate it; a view that is about to be swapped out should pause first so the layout pass that
    /// swaps it (and lays out whatever replaces it) isn't starved of CPU.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value) StopRedrawTimer();
            else if (IsLoaded) StartRedrawTimer();
        }
    }

    private void StartRedrawTimer()
    {
        if (_redrawTimer is not null) return;
        _redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _redrawTimer.Tick += (_, _) => SkiaCanvas.Invalidate();
        _redrawTimer.Start();
    }

    private void StopRedrawTimer()
    {
        _redrawTimer?.Stop();
        _redrawTimer = null;
    }

    public IVisualizer Visualizer
    {
        get => _visualizer;
        set => _visualizer = value ?? new BarsVisualizer();
    }

    public Theme Theme
    {
        get => _theme;
        set => _theme = ThemeService.ForStage(value ?? new Theme());
    }

    public void UpdateFrame(AudioFrame frame) => _latestFrame = frame;

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        try
        {
            _visualizer.Render(e.Surface.Canvas, e.Info, _latestFrame, _theme, _clock.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Visualizer error: {ex}");
        }
    }
}
