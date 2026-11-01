using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Waverider.Audio;
using Waverider.Disc;
using Waverider.Spotify;
using Waverider.Theming;
using Waverider.Util;
using Waverider.ViewModels;
using Waverider.Visualization;
using Waverider.Visualization.Custom;
using Microsoft.Extensions.DependencyInjection;

namespace Waverider.Views;

public sealed partial class PlayerView : UserControl
{
    private PlayerViewModel? _viewModel;
    private VisualizerCatalog? _catalog;
    private SpotifyApiClient? _api;
    private int _baseMs;
    private long _baseTick;
    private int _durationMs;
    private bool _playing;
    private bool _updatingSeek;
    private long _seekHoldUntil;
    private CancellationTokenSource? _seekDebounce;
    private DispatcherTimer? _progressTimer;
    private string? _likeTrackId;
    private bool _liked;

    public PlayerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Action? ToggleMini { get; set; }

    public PlayerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnVmPropertyChanged;
                _viewModel.FrameReady -= OnFrameReady;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnVmPropertyChanged;
                _viewModel.FrameReady += OnFrameReady;
            }
        }
    }

    public void OpenSettings() => SettingsOverlay.Visibility = Visibility.Visible;

    /// <summary>Pauses or resumes the visualizer's repaint loop (see <see cref="VisualizerHost.Paused"/>).</summary>
    public void SetVisualizerPaused(bool paused)
    {
        Visualizer.Paused = paused;
        Disc.Paused = paused;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        VisualizerCombo.ItemsSource = _viewModel.Visualizers;
        VisualizerCombo.DisplayMemberPath = nameof(IVisualizer.Name);
        VisualizerCombo.SelectedItem = _viewModel.SelectedVisualizer;

        ThemeCombo.ItemsSource = _viewModel.Themes;
        ThemeCombo.DisplayMemberPath = nameof(Theme.Name);
        ThemeCombo.SelectedItem = _viewModel.Theme;

        SyncCaptureModeCombo();

        InitEditor();
        InitLibrary();
        InitThemeEditor();
        _browseLayout = AppSettings.BrowseLayout;
        ApplyLayout();
        VolumeSlider.Value = AppSettings.Volume * 100;
        StartProgressTimer();
        UpdateVolumeIcon(VolumeSlider.Value);

        Visualizer.Theme = _viewModel.Theme;
        Visualizer.Visualizer = _viewModel.SelectedVisualizer;
        Disc.Theme = _viewModel.Theme;
        Disc.Skin = _viewModel.DiscSkin;
        Disc.Face = _viewModel.DiscFace;
        Disc.VisualizerSpins = _viewModel.DiscVisualizerSpins;
        Disc.Visualizer = _viewModel.SelectedVisualizer;
        SyncDiscCombos();

        RefreshAuthUi();
        RefreshNowPlaying();
        RefreshCaptureStatus();
    }

    private const string AppTagPrefix = "App:";

    // Apps currently offered in the capture picker, by executable name.
    private readonly Dictionary<string, AudioApp> _captureApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingCaptureCombo;

    private void SyncCaptureModeCombo()
    {
        if (_viewModel is null) return;
        RefreshCaptureApps();
        string tag = _viewModel.CaptureMode == AudioCaptureMode.Application
            ? AppTagPrefix + _viewModel.CaptureApp
            : _viewModel.CaptureMode.ToString();
        foreach (ComboBoxItem item in CaptureModeCombo.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tag)
            {
                _syncingCaptureCombo = true;
                try { CaptureModeCombo.SelectedItem = item; }
                finally { _syncingCaptureCombo = false; }
                return;
            }
        }
    }

    /// <summary>
    /// Rebuilds the per-app entries below the fixed ones, like the app list in the Windows volume mixer.
    /// The currently selected app stays listed even when it has no audio session right now.
    /// </summary>
    private void RefreshCaptureApps()
    {
        if (_viewModel is null) return;
        var apps = AudioAppEnumerator.GetApps()
            // The Spotify desktop app already has its own fixed entry.
            .Where(a => !a.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? selected = _viewModel.CaptureApp;
        if (selected is not null && !apps.Any(a => a.ProcessName.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            apps.Add(new AudioApp(selected, AudioAppEnumerator.DisplayNameFor(selected), false));

        _syncingCaptureCombo = true;
        try
        {
            var items = CaptureModeCombo.Items;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is ComboBoxItem { Tag: string tag } && tag.StartsWith(AppTagPrefix, StringComparison.Ordinal))
                    items.RemoveAt(i);
            }

            _captureApps.Clear();
            foreach (var app in apps)
            {
                _captureApps[app.ProcessName] = app;
                items.Add(new ComboBoxItem
                {
                    Content = app.IsPlaying ? $"{app.DisplayName} (playing)" : app.DisplayName,
                    Tag = AppTagPrefix + app.ProcessName,
                });
            }
        }
        finally { _syncingCaptureCombo = false; }
    }

    private void OnCaptureDropDownOpened(object? sender, object e)
    {
        // Pick up apps that started playing since the list was last built, without losing the selection.
        SyncCaptureModeCombo();
    }

    private void SyncDiscCombos()
    {
        if (_viewModel is null) return;
        SelectByTag(DiscSkinCombo, _viewModel.DiscSkin.ToString());
        SelectByTag(MiniKindCombo, _viewModel.MiniPlayerKind.ToString());
        SelectByTag(DiscFaceCombo, _viewModel.DiscFace.ToString());
        DiscSpinCheck.IsChecked = _viewModel.DiscVisualizerSpins;
        DiscSpinCheck.IsEnabled = _viewModel.DiscFace == DiscFace.Visualizer;
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tag)
            {
                if (!ReferenceEquals(combo.SelectedItem, item)) combo.SelectedItem = item;
                return;
            }
        }
    }

    private void OnDiscSkinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (DiscSkinCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<DiscSkin>((string?)item.Tag, out var skin))
            _viewModel.DiscSkin = skin;
    }

    private void OnDiscFaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (DiscFaceCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<DiscFace>((string?)item.Tag, out var face))
            _viewModel.DiscFace = face;
    }

    private void OnDiscSpinChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.DiscVisualizerSpins = DiscSpinCheck.IsChecked == true;
    }

    private void OnMiniKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (MiniKindCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<MiniPlayerKind>((string?)item.Tag, out var kind))
            _viewModel.MiniPlayerKind = kind;
    }

    private void OnCaptureModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _syncingCaptureCombo) return;
        if (CaptureModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        if (tag.StartsWith(AppTagPrefix, StringComparison.Ordinal))
        {
            if (_captureApps.TryGetValue(tag[AppTagPrefix.Length..], out var app))
                _viewModel.SetCaptureApp(app);
        }
        else if (Enum.TryParse<AudioCaptureMode>(tag, out var mode))
        {
            _viewModel.CaptureMode = mode;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State):
            case nameof(PlayerViewModel.ProgressFraction):
            case nameof(PlayerViewModel.ProgressText):
            case nameof(PlayerViewModel.DurationText):
                RefreshNowPlaying(); break;
            case nameof(PlayerViewModel.IsAuthenticated):
                RefreshAuthUi();
                Library.SetAuthenticated(_viewModel.IsAuthenticated);
                _likeTrackId = null;
                _ = RefreshLikeAsync(_viewModel.State.TrackId);
                break;
            case nameof(PlayerViewModel.IsCapturing):
            case nameof(PlayerViewModel.CaptureStatus):
                RefreshCaptureStatus(); break;
            case nameof(PlayerViewModel.CaptureMode):
            case nameof(PlayerViewModel.CaptureApp):
                SyncCaptureModeCombo(); break;
            case nameof(PlayerViewModel.DiscSkin):
                Disc.Skin = _viewModel.DiscSkin;
                SyncDiscCombos(); break;
            case nameof(PlayerViewModel.DiscFace):
                Disc.Face = _viewModel.DiscFace;
                SyncDiscCombos(); break;
            case nameof(PlayerViewModel.DiscVisualizerSpins):
                Disc.VisualizerSpins = _viewModel.DiscVisualizerSpins;
                SyncDiscCombos(); break;
            case nameof(PlayerViewModel.MiniPlayerKind):
                SyncDiscCombos(); break;
            case nameof(PlayerViewModel.Theme):
                Visualizer.Theme = _viewModel.Theme;
                Disc.Theme = _viewModel.Theme;
                if (!ReferenceEquals(ThemeCombo.SelectedItem, _viewModel.Theme))
                    ThemeCombo.SelectedItem = _viewModel.Theme;
                // The editor only ever targets the user's own themes; built-ins are copied first.
                ThemeEditor.Target = _viewModel.Theme.IsBuiltIn ? null : _viewModel.Theme;
                break;
            case nameof(PlayerViewModel.SelectedVisualizer):
                Disc.Visualizer = _viewModel.SelectedVisualizer;
                OnSelectedVisualizerChanged(); break;
        }
    }

    private void OnFrameReady(AudioFrame frame)
    {
        Visualizer.UpdateFrame(frame);
        Disc.UpdateFrame(frame);
    }

    private void RefreshNowPlaying()
    {
        if (_viewModel is null) return;
        var s = _viewModel.State;
        TrackTitle.Text = s.Title;
        TrackArtist.Text = s.Artist;
        TrackAlbum.Text = s.Album;
        // The position is interpolated between polls by _progressTimer for a smooth bar.
        _baseMs = s.ProgressMs;
        _baseTick = Environment.TickCount64;
        _durationMs = s.DurationMs;
        _playing = s.IsPlaying;
        UpdateProgress();
        PlayPauseIcon.Glyph = s.IsPlaying ? "" : "";
        _ = RefreshLikeAsync(s.TrackId);
        // Both setters ignore unchanged values, so the per-poll refresh doesn't reload the art.
        Disc.ArtUrl = s.AlbumArtUrl;
        Disc.IsPlaying = s.IsPlaying;
    }

    private void RefreshAuthUi()
    {
        if (_viewModel is null) return;
        SignOutButton.IsEnabled = _viewModel.IsAuthenticated;
    }

    private void RefreshCaptureStatus()
    {
        if (_viewModel is null) return;
        var status = _viewModel.CaptureStatus;
        if (string.IsNullOrEmpty(status))
            status = _viewModel.IsCapturing ? "Capturing." : "Capture stopped.";
        CaptureStatus.Text = status;
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e) => _viewModel?.SignOutCommand.Execute(null);
    private void OnStartCaptureClick(object sender, RoutedEventArgs e) => _viewModel?.StartCaptureCommand.Execute(null);
    private void OnStopCaptureClick(object sender, RoutedEventArgs e) => _viewModel?.StopCaptureCommand.Execute(null);
    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _viewModel?.PlayPauseCommand.Execute(null);
    private void OnNextClick(object sender, RoutedEventArgs e) => _viewModel?.NextCommand.Execute(null);
    private void OnPreviousClick(object sender, RoutedEventArgs e) => _viewModel?.PreviousCommand.Execute(null);
    private void OnShuffleClick(object sender, RoutedEventArgs e) => _viewModel?.ShuffleCommand.Execute(null);
    private void OnRepeatClick(object sender, RoutedEventArgs e) => _viewModel?.RepeatCommand.Execute(null);
    private void OnCloseSettingsClick(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (ThemeCombo.SelectedItem is Theme t) _viewModel.ApplyTheme(t);
    }

    private void OnVisualizerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (VisualizerCombo.SelectedItem is IVisualizer v) _viewModel.SelectedVisualizer = v;
    }

    private void InitEditor()
    {
        if (_viewModel is null || _catalog is not null) return;
        _catalog = AppHost.Services.GetRequiredService<VisualizerCatalog>();
        Editor.Catalog = _catalog;
        Editor.CloseRequested += () => EditorHost.Visibility = Visibility.Collapsed;
        Editor.NewRequested += OnCustomizeClickCore;
        Editor.VisualizerCreated += v => _viewModel.SelectedVisualizer = v;
        Editor.VisualizerDeleted += _ => _viewModel.SelectedVisualizer = _catalog.Items[0];
        Unloaded += (_, _) => Editor.Flush();

        foreach (int seconds in AutoVisualizer.IntervalChoices)
            AutoIntervalCombo.Items.Add(new ComboBoxItem { Content = $"{seconds / 60} min", Tag = seconds });
        _catalog.Auto.CurrentChanged += UpdateAutoOptions;
        UpdateAutoOptions();
    }

    /// <summary>Shows the Auto controls (what it is showing, how long per visualizer, skip) only while Auto is selected.</summary>
    private void UpdateAutoOptions()
    {
        if (_catalog is null || _viewModel is null) return;
        bool auto = ReferenceEquals(_viewModel.SelectedVisualizer, _catalog.Auto);
        AutoOptions.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        if (!auto) return;

        AutoNowText.Text = _catalog.Auto.Current is { } now ? $"Now: {now.Name}" : "";
        foreach (ComboBoxItem item in AutoIntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if ((int)item.Tag == _catalog.Auto.IntervalSeconds && !ReferenceEquals(AutoIntervalCombo.SelectedItem, item))
                AutoIntervalCombo.SelectedItem = item;
        }
    }

    private void OnAutoIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || AutoIntervalCombo.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
        if (_catalog.Auto.IntervalSeconds == seconds) return;
        _catalog.Auto.IntervalSeconds = seconds;
        AppSettings.AutoVisualizerSeconds = seconds;
    }

    private void OnAutoNextClick(object sender, RoutedEventArgs e) => _catalog?.Auto.Advance();

    private void OnSelectedVisualizerChanged()
    {
        if (_viewModel is null) return;
        var selected = _viewModel.SelectedVisualizer;
        Visualizer.Visualizer = selected;
        if (!ReferenceEquals(VisualizerCombo.SelectedItem, selected))
            VisualizerCombo.SelectedItem = selected;
        // The editor only ever targets the user's own visualizers; presets are copied first.
        Editor.Target = selected is CustomVisualizer { IsBuiltIn: false } custom ? custom : null;
        UpdateAutoOptions();
    }

    private void OnCustomizeClick(object sender, RoutedEventArgs e) => OnCustomizeClickCore();

    private void OnCustomizeClickCore()
    {
        // The visualizer is what's being edited, so make sure it's the big one.
        if (_browseLayout) SetLayout(false);
        ThemeEditorHost.Visibility = Visibility.Collapsed;
        if (_viewModel is null || _catalog is null) return;
        if (_viewModel.SelectedVisualizer is CustomVisualizer { IsBuiltIn: false })
        {
            EditorHost.Visibility = EditorHost.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        // Built-in or preset selected: start a new editable visualizer (cloned from presets).
        // With Auto selected, copy whichever visualizer it is showing right now.
        var shown = _viewModel.SelectedVisualizer is AutoVisualizer auto ? auto.Current : _viewModel.SelectedVisualizer;
        var template = (shown as CustomVisualizer)?.Settings;
        _viewModel.SelectedVisualizer = _catalog.CreateNew(template);
        EditorHost.Visibility = Visibility.Visible;
    }

    private bool _browseLayout;

    /// <summary>Switches between the Now Playing and Browse layouts.</summary>
    public void ToggleLayout() => SetLayout(!_browseLayout);

    private void SetLayout(bool browse)
    {
        _browseLayout = browse;
        AppSettings.BrowseLayout = browse;
        ApplyLayout();
    }

    // Moves the existing elements between grid cells rather than duplicating them, so the
    // visualizer, now-playing state and library keep running across a switch.
    private void ApplyLayout()
    {
        if (_browseLayout)
        {
            Grid.SetColumn(VisualizerCard, 1);
            Grid.SetRow(VisualizerCard, 0);
            Grid.SetRowSpan(VisualizerCard, 1);
            Grid.SetRow(ArtCard, 1);
            Grid.SetRowSpan(ArtCard, 1);

            // The library moves out of the slide-over drawer into the big left pane.
            LibraryHost.Visibility = Visibility.Collapsed;
            LibraryHost.Child = null;
            BrowseHost.Child = Library;
            BrowseHost.Visibility = Visibility.Visible;
            Library.ShowClose = false;
            if (_viewModel is not null) Library.SetAuthenticated(_viewModel.IsAuthenticated);
        }
        else
        {
            Grid.SetColumn(VisualizerCard, 0);
            Grid.SetRow(VisualizerCard, 0);
            Grid.SetRowSpan(VisualizerCard, 3);
            Grid.SetRow(ArtCard, 0);
            Grid.SetRowSpan(ArtCard, 2);

            BrowseHost.Visibility = Visibility.Collapsed;
            BrowseHost.Child = null;
            LibraryHost.Child = Library;
            Library.ShowClose = true;
        }
    }

    public void ToggleLibrary()
    {
        // In the Browse layout the library is always showing.
        if (_browseLayout) return;
        bool show = LibraryHost.Visibility != Visibility.Visible;
        LibraryHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _viewModel is not null) Library.SetAuthenticated(_viewModel.IsAuthenticated);
    }

    private void InitLibrary()
    {
        if (_api is not null) return;
        _api = AppHost.Services.GetRequiredService<SpotifyApiClient>();
        Library.Api = _api;
        Library.CloseRequested += () => LibraryHost.Visibility = Visibility.Collapsed;
        Library.SignInRequested += () => _viewModel?.SignInCommand.Execute(null);
    }

    private async Task RefreshLikeAsync(string? trackId)
    {
        if (_api is null || trackId == _likeTrackId) return;
        _likeTrackId = trackId;
        _liked = false;
        SetLikeIcon();
        LikeButton.IsEnabled = trackId is not null && (_viewModel?.IsAuthenticated ?? false);
        if (!LikeButton.IsEnabled) return;

        try
        {
            var result = await _api.AreSavedAsync(new[] { $"spotify:track:{trackId}" });
            if (_likeTrackId == trackId)
            {
                _liked = result.Length > 0 && result[0];
                SetLikeIcon();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Like check: {ex.Message}"); }
    }

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || _likeTrackId is null) return;
        var uri = $"spotify:track:{_likeTrackId}";
        bool target = !_liked;
        try
        {
            if (target) await _api.SaveAsync(uri); else await _api.RemoveAsync(uri);
            _liked = target;
            SetLikeIcon();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Like toggle: {ex.Message}"); }
    }

    private void SetLikeIcon()
    {
        LikeIcon.Glyph = _liked ? "" : "";
        ToolTipService.SetToolTip(LikeButton, _liked ? "Remove from Liked Songs" : "Save to Liked Songs");
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        double volume = e.NewValue / 100.0;
        AppSettings.Volume = volume;
        AppHost.WebPlayer?.SendCommand("volume", volume);
        UpdateVolumeIcon(e.NewValue);
    }

    private void UpdateVolumeIcon(double percent)
    {
        VolumeIcon.Glyph = percent <= 0 ? "" : percent < 34 ? "" : percent < 67 ? "" : "";
    }

    // ---- Seek bar -----------------------------------------------------------------------

    private void StartProgressTimer()
    {
        if (_progressTimer is not null) return;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();
        Unloaded += (_, _) => _progressTimer?.Stop();
        Loaded += (_, _) => _progressTimer?.Start();
    }

    private int CurrentPositionMs()
    {
        if (!_playing) return _baseMs;
        long elapsed = Environment.TickCount64 - _baseTick;
        return (int)Math.Min(_durationMs > 0 ? _durationMs : int.MaxValue, _baseMs + elapsed);
    }

    private void UpdateProgress()
    {
        int position = CurrentPositionMs();
        ProgressLabel.Text = FormatTime(position);
        DurationLabel.Text = FormatTime(_durationMs);

        // Don't fight the user while they drag or while a seek is still landing.
        if (Environment.TickCount64 < _seekHoldUntil) return;
        _updatingSeek = true;
        try
        {
            SeekSlider.Maximum = Math.Max(1, _durationMs);
            SeekSlider.Value = Math.Min(position, SeekSlider.Maximum);
        }
        finally { _updatingSeek = false; }
    }

    private void OnSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _viewModel is null) return;

        // A user-driven change: show it immediately, then seek once they settle.
        int target = (int)e.NewValue;
        _seekHoldUntil = Environment.TickCount64 + 1500;
        ProgressLabel.Text = FormatTime(target);

        _seekDebounce?.Cancel();
        var cts = _seekDebounce = new CancellationTokenSource();
        _ = SeekAfterDelayAsync(target, cts.Token);
    }

    private async Task SeekAfterDelayAsync(int target, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            if (_viewModel is null) return;
            _baseMs = target;
            _baseTick = Environment.TickCount64;
            _seekHoldUntil = Environment.TickCount64 + 1200;
            await _viewModel.SeekAsync(target);
        }
        catch (TaskCanceledException) { }
    }

    private static string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    // ---- Device picker ------------------------------------------------------------------

    private async void OnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || _viewModel is null) return;
        try
        {
            var devices = await _api.GetDevicesAsync();
            var builtInId = AppHost.WebPlayer?.DeviceId;
            var menu = new MenuFlyout();

            if (devices.Count == 0)
                menu.Items.Add(new MenuFlyoutItem { Text = "No devices available", IsEnabled = false });

            foreach (var device in devices)
            {
                var d = device;
                bool builtIn = d.Id == builtInId;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = builtIn ? $"This app ({d.Name})" : $"{d.Name} ({d.Type})",
                    IsChecked = d.IsActive
                };
                item.Click += async (_, _) => await SelectDeviceAsync(d, builtIn);
                menu.Items.Add(item);
            }
            menu.ShowAt(DeviceButton);
        }
        catch (SpotifyApiException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Devices: {ex.Message}");
        }
    }

    private async Task SelectDeviceAsync(DeviceInfo device, bool builtIn)
    {
        if (_api is null || _viewModel is null) return;
        try
        {
            await _api.TransferPlaybackAsync(device.Id, play: _viewModel.State.IsPlaying);
            _api.PreferredDeviceId = device.Id;
            // The visualizer can only hear what it captures: follow the chosen device.
            if (builtIn) _viewModel.CaptureMode = AudioCaptureMode.ThisApp;
            else if (device.Type == "Computer") _viewModel.CaptureMode = AudioCaptureMode.SpotifyOnly;
        }
        catch (SpotifyApiException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Transfer: {ex.Message}");
        }
    }

    // ---- Theme editor ---------------------------------------------------------------------

    private ThemeService? _themes;

    private void InitThemeEditor()
    {
        if (_themes is not null) return;
        _themes = AppHost.Services.GetRequiredService<ThemeService>();
        ThemeEditor.Themes = _themes;
        ThemeEditor.CloseRequested += () => ThemeEditorHost.Visibility = Visibility.Collapsed;
        ThemeEditor.NewRequested += () => OpenThemeEditor(createFromCurrent: true);
        ThemeEditor.ThemeCreated += t => _viewModel?.ApplyTheme(t);
        ThemeEditor.ThemeDeleted += _ =>
        {
            if (_themes.Available.Count > 0) _viewModel?.ApplyTheme(_themes.Available[0]);
        };
        Unloaded += (_, _) => ThemeEditor.Flush();
    }

    private void OnCustomizeThemeClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        OpenThemeEditor(createFromCurrent: _viewModel?.Theme.IsBuiltIn ?? false);
    }

    private void OpenThemeEditor(bool createFromCurrent)
    {
        if (_viewModel is null || _themes is null) return;
        var theme = _viewModel.Theme;
        if (createFromCurrent || theme.IsBuiltIn)
        {
            // Built-in themes are read-only: start from an editable copy and switch to it.
            theme = _themes.CreateNew(theme);
            _viewModel.ApplyTheme(theme);
        }
        EditorHost.Visibility = Visibility.Collapsed;
        ThemeEditorHost.Visibility = Visibility.Visible;
        ThemeEditor.Target = theme;
    }
}