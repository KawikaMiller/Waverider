using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Waverider.Audio;
using Waverider.Disc;
using Waverider.Spotify;
using Waverider.Theming;
using Waverider.Util;
using Waverider.Visualization;

namespace Waverider.ViewModels;

public sealed class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly AudioCaptureService _audio;
    private readonly NowPlayingService _nowPlaying;
    private readonly SpotifyAuthService _auth;
    private readonly ThemeService _themeService;
    private readonly IReadOnlyList<IVisualizer> _visualizers;
    private AudioFrame _frame = new(Array.Empty<float>(), Array.Empty<float>(), new float[64], 0, 0, 0, 0, 0, 0, 48000, 0);

    public PlayerViewModel(
        DispatcherQueue dispatcher,
        AudioCaptureService audio,
        NowPlayingService nowPlaying,
        SpotifyAuthService auth,
        ThemeService themeService,
        IReadOnlyList<IVisualizer> visualizers)
    {
        _dispatcher = dispatcher;
        _audio = audio;
        _nowPlaying = nowPlaying;
        _auth = auth;
        _themeService = themeService;
        _visualizers = visualizers;
        var savedVisualizer = AppSettings.VisualizerName;
        _selectedVisualizer = _visualizers.FirstOrDefault(v => v.Name == savedVisualizer) ?? _visualizers[0];

        _isAuthenticated = _auth.IsAuthenticated;
        _state = _nowPlaying.State;
        _theme = _themeService.Current;

        if (Enum.TryParse<AudioCaptureMode>(AppSettings.CaptureMode, out var savedMode))
            _audio.Restore(savedMode, AppSettings.CaptureApp);
        _captureMode = _audio.Mode;
        _captureStatus = _audio.StatusText;

        _audio.FrameReady += OnAudioFrame;
        _audio.RunningChanged += running => _dispatcher.TryEnqueue(() => IsCapturing = running);
        _audio.StatusChanged += s => _dispatcher.TryEnqueue(() => CaptureStatus = s);
        _nowPlaying.StateChanged += OnNowPlayingChanged;
        _auth.AuthStateChanged += b => _dispatcher.TryEnqueue(() => IsAuthenticated = b);
        _themeService.ThemeChanged += t => _dispatcher.TryEnqueue(() =>
        {
            // An in-place edit re-announces the same instance; make that count as a change.
            if (ReferenceEquals(_theme, t)) Raise(nameof(Theme));
            else Theme = t;
        });

        SignInCommand = new AsyncRelayCommand(SignInAsync);
        SignOutCommand = new AsyncRelayCommand(_auth.SignOutAsync);
        StartCaptureCommand = new RelayCommand(_audio.Start);
        StopCaptureCommand = new RelayCommand(_audio.Stop);
        PlayPauseCommand = new AsyncRelayCommand(async () => { await _nowPlaying.PlayPauseAsync(); });
        NextCommand = new AsyncRelayCommand(async () => { await _nowPlaying.NextAsync(); });
        PreviousCommand = new AsyncRelayCommand(async () => { await _nowPlaying.PreviousAsync(); });
        ShuffleCommand = new AsyncRelayCommand(async () => { await _nowPlaying.ToggleShuffleAsync(); });
        RepeatCommand = new AsyncRelayCommand(async () => { await _nowPlaying.CycleRepeatAsync(); });
    }

    public IReadOnlyList<IVisualizer> Visualizers => _visualizers;
    public IReadOnlyList<Theme> Themes => _themeService.Available;

    private NowPlayingState _state;
    public NowPlayingState State { get => _state; private set { if (Set(ref _state, value)) UpdateProgress(); } }

    private Theme _theme;
    public Theme Theme { get => _theme; private set => Set(ref _theme, value); }

    private IVisualizer _selectedVisualizer;
    public IVisualizer SelectedVisualizer
    {
        get => _selectedVisualizer;
        set
        {
            if (Set(ref _selectedVisualizer, value ?? _visualizers[0])) AppSettings.VisualizerName = _selectedVisualizer.Name;
        }
    }

    private bool _isAuthenticated;
    public bool IsAuthenticated { get => _isAuthenticated; set => Set(ref _isAuthenticated, value); }

    private bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; set => Set(ref _isCapturing, value); }

    private DiscSkin _discSkin = AppSettings.DiscSkin;
    public DiscSkin DiscSkin
    {
        get => _discSkin;
        set { if (Set(ref _discSkin, value)) AppSettings.DiscSkin = value; }
    }

    private DiscFace _discFace = AppSettings.DiscFace;
    public DiscFace DiscFace
    {
        get => _discFace;
        set { if (Set(ref _discFace, value)) AppSettings.DiscFace = value; }
    }

    private bool _discVisualizerSpins = AppSettings.DiscVisualizerSpins;
    public bool DiscVisualizerSpins
    {
        get => _discVisualizerSpins;
        set { if (Set(ref _discVisualizerSpins, value)) AppSettings.DiscVisualizerSpins = value; }
    }

    private MiniPlayerKind _miniPlayerKind = AppSettings.MiniPlayerKind;
    public MiniPlayerKind MiniPlayerKind
    {
        get => _miniPlayerKind;
        set { if (Set(ref _miniPlayerKind, value)) AppSettings.MiniPlayerKind = value; }
    }

    private bool _isMiniMode;
    public bool IsMiniMode { get => _isMiniMode; set => Set(ref _isMiniMode, value); }

    private AudioCaptureMode _captureMode;
    public AudioCaptureMode CaptureMode
    {
        get => _captureMode;
        set
        {
            if (!Set(ref _captureMode, value)) return;
            _audio.SetMode(value);
            AppSettings.CaptureMode = value.ToString();
            Raise(nameof(CaptureApp));
        }
    }

    /// <summary>Executable name of the app being captured in Application mode, otherwise null.</summary>
    public string? CaptureApp => _captureMode == AudioCaptureMode.Application ? _audio.AppProcessName : null;

    /// <summary>Captures one application's audio instead of a whole device.</summary>
    public void SetCaptureApp(AudioApp app)
    {
        _audio.SetApplication(app.ProcessName, app.DisplayName);
        AppSettings.CaptureApp = app.ProcessName;
        AppSettings.CaptureMode = nameof(AudioCaptureMode.Application);
        _ = Set(ref _captureMode, AudioCaptureMode.Application, nameof(CaptureMode));
        Raise(nameof(CaptureApp));
    }

    private string _captureStatus = "";
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }

    public AudioFrame LatestFrame => _frame;
    public event Action<AudioFrame>? FrameReady;

    public Task SeekAsync(int positionMs) => _nowPlaying.SeekAsync(positionMs);

    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public RelayCommand StartCaptureCommand { get; }
    public RelayCommand StopCaptureCommand { get; }
    public AsyncRelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand ShuffleCommand { get; }
    public AsyncRelayCommand RepeatCommand { get; }

    public string ProgressText => Format(_state.ProgressMs);
    public string DurationText => Format(_state.DurationMs);
    public double ProgressFraction => _state.DurationMs <= 0 ? 0 : (double)_state.ProgressMs / _state.DurationMs;

    public void StartServices()
    {
        try { _audio.Start(); } catch (Exception ex) { Debug.WriteLine($"Audio start: {ex}"); }
        _nowPlaying.Start();
    }

    public void ApplyTheme(Theme theme) => _themeService.Apply(theme);

    private async Task SignInAsync()
    {
        var ok = await _auth.SignInAsync();
        if (ok) _nowPlaying.Start();
    }

    private void OnAudioFrame(AudioFrame frame)
    {
        _frame = frame;
        FrameReady?.Invoke(frame);
    }

    private void OnNowPlayingChanged(NowPlayingState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            State = state;
        });
    }

    private void UpdateProgress()
    {
        Raise(nameof(ProgressText));
        Raise(nameof(DurationText));
        Raise(nameof(ProgressFraction));
    }

    private static string Format(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _audio.FrameReady -= OnAudioFrame;
        _nowPlaying.StateChanged -= OnNowPlayingChanged;
    }
}
