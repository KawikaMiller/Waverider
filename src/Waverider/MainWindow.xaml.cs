using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waverider.Audio;
using Waverider.Disc;
using Waverider.Spotify;
using Waverider.Theming;
using Waverider.Util;
using Waverider.ViewModels;
using Waverider.Views;
using Waverider.Visualization;
using Waverider.WebPlayback;
using Microsoft.Extensions.DependencyInjection;

namespace Waverider;

public sealed partial class MainWindow : Window
{
    public PlayerViewModel ViewModel { get; }
    private readonly PlayerView _full;
    private readonly MiniPlayerView _mini;
    private readonly MiniDiscView _miniDisc;
    private bool _inMiniChrome;
    private bool _discMiniActive;
    private Windows.Graphics.PointInt32? _fullPosition;

    private WebPlayerService? _webPlayer;

    public MainWindow()
    {
        WebPlayerService.ConfigureEnvironment();
        InitializeComponent();

        var services = AppHost.Services;
        ViewModel = new PlayerViewModel(
            DispatcherQueue.GetForCurrentThread(),
            services.GetRequiredService<AudioCaptureService>(),
            services.GetRequiredService<NowPlayingService>(),
            services.GetRequiredService<SpotifyAuthService>(),
            services.GetRequiredService<ThemeService>(),
            services.GetRequiredService<IReadOnlyList<IVisualizer>>());

        _full = new PlayerView { ViewModel = ViewModel, ToggleMini = ToggleMini };
        _mini = new MiniPlayerView { ViewModel = ViewModel, ToggleMini = ToggleMini };
        _miniDisc = new MiniDiscView { ViewModel = ViewModel, ToggleMini = ToggleMini, BeginDrag = () => this.BeginDragMove() };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyTheme(ViewModel.Theme);
        ViewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.Theme): ApplyTheme(ViewModel.Theme); break;
                case nameof(ViewModel.MiniPlayerKind): if (ViewModel.IsMiniMode) EnterMini(); break;
                // The spindle hole differs between the vinyl and the CD, and it is cut out of the window.
                case nameof(ViewModel.DiscSkin): if (_discMiniActive) ApplyDiscRegion(); break;
                case nameof(ViewModel.IsAuthenticated): { RefreshAuthUi(); if (ViewModel.IsAuthenticated) _webPlayer?.ReloadIfWaitingForToken(); break; }
            }
        };

        ShellHost.Content = _full;
        this.Resize(1100, 720);
        ViewModel.StartServices();
        RefreshAuthUi();

        _webPlayer = new WebPlayerService(PlayerWebView, services.GetRequiredService<SpotifyAuthService>());
        AppHost.WebPlayer = _webPlayer;
        services.GetRequiredService<AudioCaptureService>().ThisAppPidProvider = () => _webPlayer?.BrowserProcessId;
        var api = services.GetRequiredService<SpotifyApiClient>();
        string? lastBuiltInId = null;
        _webPlayer.DeviceChanged += async id =>
        {
            // Only claim playback for the built-in player when the user hasn't picked another device.
            if (api.PreferredDeviceId is null || api.PreferredDeviceId == lastBuiltInId) api.PreferredDeviceId = id;
            lastBuiltInId = id ?? lastBuiltInId;
            if (id is null) return;
            _webPlayer.SendCommand("volume", AppSettings.Volume);
            try { await api.ActivateIfIdleAsync(id); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Activate player: {ex.Message}"); }
        };
        _ = _webPlayer.InitializeAsync();
    }

    private void ApplyTheme(Theme theme)
    {
        var requested = theme.ElementTheme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        // This also runs on every color-picker tick while editing a theme, so only redo the
        // expensive parts (theme flip, backdrop swap) when they actually changed.
        ThemeService.ApplyPanelBrushes(theme);
        RootGrid.Background = ThemeService.Brush(theme.Background);

        if (theme.Accent != _appliedAccent)
        {
            // Recolors the live accent brushes in place; no theme flip needed.
            ThemeService.ApplyAccentResources(theme.Accent);
            _appliedAccent = theme.Accent;
        }

        if (requested != _appliedElementTheme)
        {
            RootGrid.RequestedTheme = requested;
            _appliedElementTheme = requested;
        }

        if (theme.Backdrop != _appliedBackdrop)
        {
            SystemBackdrop = theme.Backdrop switch
            {
                "Mica" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                "MicaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                "Acrylic" => new DesktopAcrylicBackdrop(),
                _ => null
            };
            _appliedBackdrop = theme.Backdrop;
        }
    }

    private string? _appliedAccent;
    private ElementTheme? _appliedElementTheme;
    private string? _appliedBackdrop;

    private void RefreshAuthUi()
    {
        SignInLabel.Text = ViewModel.IsAuthenticated ? "Signed in" : "Sign in";
        SignInButton.IsEnabled = !ViewModel.IsAuthenticated;
    }

    private void ToggleMini()
    {
        ViewModel.IsMiniMode = !ViewModel.IsMiniMode;
        if (ViewModel.IsMiniMode) EnterMini();
        else ExitMini();
    }

    /// <summary>Shows whichever mini player is picked in settings (also used to swap between them).</summary>
    private void EnterMini()
    {
        bool disc = ViewModel.MiniPlayerKind == MiniPlayerKind.Disc;
        var aw = this.GetAppWindow();
        var previousCenter = (X: aw.Position.X + aw.Size.Width / 2, Y: aw.Position.Y + aw.Size.Height / 2);
        bool swapping = _inMiniChrome;
        _inMiniChrome = true;
        // Remember where the full window was so leaving mini mode puts it back there.
        if (!swapping) _fullPosition = aw.Position;

        // The full view's visualizer repaints on the UI thread and can saturate it, which starves the layout
        // pass that swaps in the mini UI (it then renders stale). Stop it before swapping.
        _full.SetVisualizerPaused(true);
        ShellHost.Content = disc ? _miniDisc : _mini;
        AppTitleBar.Visibility = Visibility.Collapsed;
        this.SetTitleBarVisible(false);
        this.SetAlwaysOnTop(true);
        // The disc is shaped by a window region, but DWM still draws the sizing frame outside any region,
        // so the disc window can't have one.
        if (aw.Presenter is OverlappedPresenter op)
        {
            op.IsResizable = !disc;
            op.IsMaximizable = !disc;
        }

        var (sw, sh) = WindowExtensions.GetPrimaryScreenSize();
        int w, h;
        if (disc) w = h = (int)Math.Round(220 * this.GetScale());
        else { w = 360; h = 130; }

        int x, y;
        if (swapping)
        {
            // Keep the window where the user left it, centered on the same spot.
            x = Math.Clamp(previousCenter.X - w / 2, 0, Math.Max(0, sw - w));
            y = Math.Clamp(previousCenter.Y - h / 2, 0, Math.Max(0, sh - h));
        }
        else
        {
            x = sw - w - 24;
            y = sh - h - 80;
        }
        this.SetSizeAndPosition(x, y, w, h);

        _discMiniActive = disc;
        if (disc) ApplyDiscRegion();
        else this.ClearWindowRegion();
        this.SetWindowOutlineVisible(!disc);
    }

    private void ApplyDiscRegion() => this.SetDiscRegion(DiscRenderer.HoleFraction(ViewModel.DiscSkin));

    private void ExitMini()
    {
        _discMiniActive = false;
        _inMiniChrome = false;
        this.ClearWindowRegion();
        this.SetWindowOutlineVisible(true);
        ShellHost.Content = _full;
        _full.SetVisualizerPaused(false);
        AppTitleBar.Visibility = Visibility.Visible;
        this.SetTitleBarVisible(true);
        this.SetAlwaysOnTop(false);
        if (this.GetAppWindow().Presenter is OverlappedPresenter op)
        {
            op.IsResizable = true;
            op.IsMaximizable = true;
        }
        var (sw, sh) = WindowExtensions.GetPrimaryScreenSize();
        var at = _fullPosition ?? new Windows.Graphics.PointInt32(Math.Max(0, (sw - 1100) / 2), Math.Max(0, (sh - 720) / 2));
        this.SetSizeAndPosition(at.X, at.Y, 1100, 720);
        // Re-anchor the custom title bar drag region after toggling presenter chrome.
        SetTitleBar(AppTitleBar);
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) => ViewModel.SignInCommand.Execute(null);
    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (ShellHost.Content is PlayerView pv) pv.ToggleLayout();
    }

    private void OnLibraryClick(object sender, RoutedEventArgs e)
    {
        if (ShellHost.Content is PlayerView pv) pv.ToggleLibrary();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ShellHost.Content is PlayerView pv) pv.OpenSettings();
    }
    private void OnMiniClick(object sender, RoutedEventArgs e) => ToggleMini();
}
