using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Waverider.Audio;
using Waverider.Theming;
using Waverider.Visualization;

namespace Waverider.Disc;

/// <summary>
/// A spinning vinyl record or CD that shows the current track's album art and, optionally, a live
/// visualizer (see <see cref="Face"/>). Spins up when <see cref="IsPlaying"/> turns on and coasts to a
/// stop when it turns off.
/// </summary>
public sealed partial class DiscView : UserControl
{
    private static readonly HttpClient Http = new();

    /// <summary>Resting rotation speed in degrees per second (a real 33⅓ RPM LP is 200, which looks frantic on screen).</summary>
    private const double SpinDegreesPerSecond = 110;
    private const double SpinUpSeconds = 0.35;
    private const double CoastDownSeconds = 0.9;

    private readonly RotateTransform _rotation = new();
    private readonly RotateTransform _visualizerRotation = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _visualizerTimer;
    private IVisualizer? _visualizer;
    private AudioFrame _frame = new(Array.Empty<float>(), Array.Empty<float>(), new float[64], 0, 0, 0, 0, 0, 0, 48000, 0);
    private Theme _stageTheme = new();
    private DiscFace _face = DiscFace.Art;
    private bool _visualizerSpins;
    private bool _paused;
    private DiscSkin _skin = DiscSkin.Vinyl;
    private bool _flush;
    private SKImage? _art;
    private string? _artUrl;
    private SKColor _accent = new(0x1D, 0xB9, 0x54);
    private SKColor _accent2 = new(0x1E, 0xD7, 0x60);

    private bool _playing;
    private double _omega;       // current angular speed, degrees/second
    private double _angle;       // current rotation, degrees
    private long _lastTimestamp;
    private bool _loopRunning;

    public DiscView()
    {
        InitializeComponent();
        BodyCanvas.RenderTransform = _rotation;
        ApplyVisualizerRotation();
        Loaded += (_, _) =>
        {
            if (_playing || _omega > 0) StartLoop();
            UpdateVisualizerTimer();
        };
        Unloaded += (_, _) =>
        {
            StopLoop();
            UpdateVisualizerTimer();
        };
    }

    public DiscSkin Skin
    {
        get => _skin;
        set
        {
            if (_skin == value) return;
            _skin = value;
            InvalidateAll();
        }
    }

    /// <summary>
    /// Draws the disc edge to edge with no drop shadow, for when the window around it is clipped to
    /// the disc's outline.
    /// </summary>
    public bool Flush
    {
        get => _flush;
        set
        {
            if (_flush == value) return;
            _flush = value;
            ShadowCanvas.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            InvalidateAll();
        }
    }

    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            if (value) StartLoop();
        }
    }

    /// <summary>Colors used for the stand-in artwork when there's no album art.</summary>
    public Theme Theme
    {
        set
        {
            _stageTheme = ThemeService.ForStage(value);
            var a = ThemeService.ParseSKColor(value.Primary);
            var b = ThemeService.ParseSKColor(value.Secondary);
            if (a == _accent && b == _accent2) return;
            _accent = a;
            _accent2 = b;
            BodyCanvas.Invalidate();
        }
    }

    /// <summary>What the disc shows: the album art, or the visualizer (on a CD in place of the art, on vinyl over the grooves).</summary>
    public DiscFace Face
    {
        get => _face;
        set
        {
            if (_face == value) return;
            _face = value;
            BodyCanvas.Invalidate();
            UpdateVisualizerTimer();
        }
    }

    /// <summary>True when the visualizer turns with the disc; false holds it upright while the disc spins.</summary>
    public bool VisualizerSpins
    {
        get => _visualizerSpins;
        set
        {
            if (_visualizerSpins == value) return;
            _visualizerSpins = value;
            ApplyVisualizerRotation();
        }
    }

    /// <summary>The visualizer to draw. The disc keeps its own copy, so it cannot disturb the original's state.</summary>
    public IVisualizer? Visualizer
    {
        set
        {
            (_visualizer as IDisposable)?.Dispose();
            _visualizer = value is null ? null : VisualizerClone.Of(value);
            UpdateVisualizerTimer(); // the face may have been set before there was anything to draw
        }
    }

    /// <summary>Stops the visualizer's redraw loop without removing the view (see <see cref="VisualizerHost.Paused"/>).</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            UpdateVisualizerTimer();
        }
    }

    public void UpdateFrame(AudioFrame frame) => _frame = frame;

    public string? ArtUrl
    {
        get => _artUrl;
        set
        {
            if (value == _artUrl) return;
            _artUrl = value;
            _ = LoadArtAsync(value);
        }
    }

    private void InvalidateAll()
    {
        ShadowCanvas.Invalidate();
        BodyCanvas.Invalidate();
        VisualizerCanvas.Invalidate();
        SheenCanvas.Invalidate();
    }

    private bool VisualizerActive => _face == DiscFace.Visualizer && _visualizer is not null && !_paused && IsLoaded;

    /// <summary>Runs the redraw timer only while there is a visualizer to draw; the disc itself needs no timer.</summary>
    private void UpdateVisualizerTimer()
    {
        VisualizerCanvas.Visibility = _face == DiscFace.Visualizer ? Visibility.Visible : Visibility.Collapsed;
        if (VisualizerActive)
        {
            if (_visualizerTimer is not null) return;
            _visualizerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _visualizerTimer.Tick += (_, _) => VisualizerCanvas.Invalidate();
            _visualizerTimer.Start();
        }
        else
        {
            _visualizerTimer?.Stop();
            _visualizerTimer = null;
        }
    }

    private void ApplyVisualizerRotation()
        => VisualizerCanvas.RenderTransform = _visualizerSpins ? _visualizerRotation : null;

    private async Task LoadArtAsync(string? url)
    {
        SKImage? image = null;
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                image = SKImage.FromEncodedData(bytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Disc art: {ex.Message}");
            }
        }

        // A newer track may have been requested while this one downloaded.
        if (url != _artUrl)
        {
            image?.Dispose();
            return;
        }

        var old = _art;
        _art = image;
        BodyCanvas.Invalidate();
        old?.Dispose();
    }

    private void OnPaintShadow(object? sender, SKPaintSurfaceEventArgs e)
        => DiscRenderer.DrawShadow(e.Surface.Canvas, e.Info, _skin);

    private void OnPaintBody(object? sender, SKPaintSurfaceEventArgs e)
        => DiscRenderer.DrawBody(e.Surface.Canvas, e.Info, _skin, _face, _art, _accent, _accent2, _flush);

    private void OnPaintVisualizer(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_face != DiscFace.Visualizer || _visualizer is null) { e.Surface.Canvas.Clear(SKColors.Transparent); return; }
        try
        {
            DiscRenderer.DrawVisualizer(e.Surface.Canvas, e.Info, _skin, _flush, _visualizer, _frame, _stageTheme, _clock.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disc visualizer: {ex.Message}");
        }
    }

    private void OnPaintSheen(object? sender, SKPaintSurfaceEventArgs e)
        => DiscRenderer.DrawSheen(e.Surface.Canvas, e.Info, _skin, _flush);

    // ---- spin ------------------------------------------------------------------------------

    private void StartLoop()
    {
        if (_loopRunning || !IsLoaded) return;
        _loopRunning = true;
        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopLoop()
    {
        if (!_loopRunning) return;
        _loopRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _lastTimestamp) / (double)Stopwatch.Frequency);
        _lastTimestamp = now;

        double target = _playing ? SpinDegreesPerSecond : 0;
        double tau = target > _omega ? SpinUpSeconds : CoastDownSeconds;
        _omega += (target - _omega) * (1 - Math.Exp(-dt / tau));

        if (!_playing && _omega < 0.4)
        {
            _omega = 0;
            StopLoop();
            return;
        }

        _angle = (_angle + _omega * dt) % 360;
        _rotation.Angle = _angle;
        _visualizerRotation.Angle = _angle;
    }
}
