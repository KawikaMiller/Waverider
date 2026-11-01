using System.Diagnostics;
using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

/// <summary>
/// The "Auto" entry in the visualizer list: it shows one visualizer for a few minutes, then crossfades to
/// another, like the old Windows Media Player did. It draws from whatever is in the list at that moment
/// (built-ins, presets and the user's own), working through a shuffled bag so every one is shown before
/// any repeats. Because it is just another <see cref="IVisualizer"/>, the main window, the mini player and
/// the disc all get it for free.
/// </summary>
public sealed class AutoVisualizer : IVisualizer, IDisposable
{
    /// <summary>How long Auto stays on one visualizer, in seconds, as offered in the picker.</summary>
    public static readonly IReadOnlyList<int> IntervalChoices = new[] { 120, 180, 300, 600 };
    public const int DefaultIntervalSeconds = 180;
    private const double FadeSeconds = 0.9;

    private readonly IReadOnlyList<IVisualizer> _all;
    private readonly Func<double> _seconds;
    private readonly List<IVisualizer> _bag = new();
    // Offscreen pairs for the crossfade, kept per size because the window, mini player and disc can all draw this
    // visualizer in the same frame at different sizes.
    private readonly Dictionary<(int W, int H), (SKSurface Outgoing, SKSurface Incoming)> _surfaces = new();

    private IVisualizer? _current;
    private IVisualizer? _previous;
    private double _fadeStart;
    private double _nextAt;
    private int _intervalSeconds;

    /// <param name="all">The live visualizer list. Auto itself is skipped if it appears in it.</param>
    /// <param name="seconds">Time source in seconds; only tests pass one, so they can move time without waiting.</param>
    public AutoVisualizer(IReadOnlyList<IVisualizer> all, int intervalSeconds, Func<double>? seconds = null)
    {
        _all = all;
        if (seconds is null)
        {
            var clock = Stopwatch.StartNew();
            seconds = () => clock.Elapsed.TotalSeconds;
        }
        _seconds = seconds;
        _intervalSeconds = IntervalChoices.Contains(intervalSeconds) ? intervalSeconds : DefaultIntervalSeconds;
    }

    public string Name => "Auto";

    /// <summary>The visualizer being shown right now (null before the first frame).</summary>
    public IVisualizer? Current => _current;

    /// <summary>Raised on the UI thread, from a paint, when Auto moves to another visualizer.</summary>
    public event Action? CurrentChanged;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            _intervalSeconds = value;
            _nextAt = Now + value;
        }
    }

    private double Now => _seconds();

    /// <summary>Moves to the next visualizer now and restarts the timer.</summary>
    public void Advance() => MoveNext(Now);

    public void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds)
    {
        double now = Now;
        Update(now);

        if (_current is null || info.Width <= 0 || info.Height <= 0)
        {
            canvas.Clear(SKColors.Transparent);
            return;
        }
        if (_previous is null)
        {
            _current.Render(canvas, info, frame, theme, timeSeconds);
            return;
        }

        float progress = (float)Math.Clamp((now - _fadeStart) / FadeSeconds, 0, 1);
        var (outgoing, incoming) = SurfacesFor(info);
        _previous.Render(outgoing.Canvas, info, frame, theme, timeSeconds);
        _current.Render(incoming.Canvas, info, frame, theme, timeSeconds);

        canvas.Clear(SKColors.Transparent);
        using var from = outgoing.Snapshot();
        using var to = incoming.Snapshot();
        // Weights that sum to 1, added together: two opaque pictures dissolve without the stage showing through
        // mid-fade (plain alpha blending would dip to 75% opacity there).
        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Color = SKColors.White.WithAlpha((byte)Math.Round(255 * (1 - progress))),
        };
        canvas.DrawImage(from, 0, 0, paint);
        paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * progress));
        canvas.DrawImage(to, 0, 0, paint);
    }

    private void Update(double now)
    {
        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            _current = _previous = null;
            return;
        }

        // Nothing chosen yet, or the current visualizer was deleted from the list.
        if (_current is null || !candidates.Contains(_current))
        {
            _previous = null;
            _current = candidates[Random.Shared.Next(candidates.Count)];
            _nextAt = now + _intervalSeconds;
            CurrentChanged?.Invoke();
            return;
        }

        if (now >= _nextAt) MoveNext(now);
        if (_previous is not null && (now - _fadeStart >= FadeSeconds || !candidates.Contains(_previous)))
            _previous = null;
    }

    private void MoveNext(double now)
    {
        var candidates = Candidates();
        _nextAt = now + _intervalSeconds;
        if (candidates.Count < 2) return;

        // Draw from the shuffled bag, skipping anything deleted since it was filled.
        IVisualizer? next = null;
        while (next is null)
        {
            if (_bag.Count == 0)
            {
                _bag.AddRange(candidates.Where(v => !ReferenceEquals(v, _current)).OrderBy(_ => Random.Shared.Next()));
            }
            var candidate = _bag[^1];
            _bag.RemoveAt(_bag.Count - 1);
            if (candidates.Contains(candidate) && !ReferenceEquals(candidate, _current)) next = candidate;
        }

        _previous = _current;
        _current = next;
        _fadeStart = now;
        CurrentChanged?.Invoke();
    }

    private List<IVisualizer> Candidates() => _all.Where(v => !ReferenceEquals(v, this)).ToList();

    private (SKSurface Outgoing, SKSurface Incoming) SurfacesFor(SKImageInfo info)
    {
        var key = (info.Width, info.Height);
        if (_surfaces.TryGetValue(key, out var pair)) return pair;

        if (_surfaces.Count >= 4) DisposeSurfaces(); // sizes churn while a window is being resized
        SKSurface Create() => SKSurface.Create(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        pair = (Create(), Create());
        _surfaces[key] = pair;
        return pair;
    }

    private void DisposeSurfaces()
    {
        foreach (var (outgoing, incoming) in _surfaces.Values)
        {
            outgoing.Dispose();
            incoming.Dispose();
        }
        _surfaces.Clear();
    }

    public void Dispose() => DisposeSurfaces();
}
