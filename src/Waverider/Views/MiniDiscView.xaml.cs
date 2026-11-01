using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Waverider.Audio;
using Waverider.Disc;
using Waverider.ViewModels;
using Windows.Foundation;

namespace Waverider.Views;

public sealed partial class MiniDiscView : UserControl
{
    /// <summary>Marquee scroll speed in device-independent pixels per second.</summary>
    private const double MarqueeSpeed = 55;
    /// <summary>Pointer travel (DIP) with the button down before a click turns into a window drag.</summary>
    private const double DragThreshold = 5;

    private PlayerViewModel? _viewModel;
    private readonly TranslateTransform _marqueeShift = new();
    private readonly DispatcherTimer _marqueeStopTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Storyboard? _marquee;
    private string _marqueeSource = "";
    private bool _hover;
    private bool _pressed;
    private Point _pressPoint;

    public MiniDiscView()
    {
        InitializeComponent();
        // The window is clipped to the disc, so the disc fills it edge to edge.
        Disc.Flush = true;
        MarqueeText.RenderTransform = _marqueeShift;
        _marqueeStopTimer.Tick += (_, _) =>
        {
            _marqueeStopTimer.Stop();
            if (!_hover) _marquee?.Stop();
        };

        var menu = new MenuFlyout();
        menu.Opening += OnMenuOpening;
        Root.ContextFlyout = menu;
    }

    public Action? ToggleMini { get; set; }

    /// <summary>Starts the OS window-move loop (set by the host window).</summary>
    public Action? BeginDrag { get; set; }

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
                Disc.Theme = _viewModel.Theme;
                Disc.Skin = _viewModel.DiscSkin;
                Disc.Face = _viewModel.DiscFace;
                Disc.VisualizerSpins = _viewModel.DiscVisualizerSpins;
                Disc.Visualizer = _viewModel.SelectedVisualizer;
                Refresh();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State): Refresh(); break;
            case nameof(PlayerViewModel.Theme): Disc.Theme = _viewModel.Theme; break;
            case nameof(PlayerViewModel.DiscSkin): Disc.Skin = _viewModel.DiscSkin; break;
            case nameof(PlayerViewModel.DiscFace): Disc.Face = _viewModel.DiscFace; break;
            case nameof(PlayerViewModel.DiscVisualizerSpins): Disc.VisualizerSpins = _viewModel.DiscVisualizerSpins; break;
            case nameof(PlayerViewModel.SelectedVisualizer): Disc.Visualizer = _viewModel.SelectedVisualizer; break;
        }
    }

    private void OnFrameReady(AudioFrame frame) => Disc.UpdateFrame(frame);

    private void Refresh()
    {
        if (_viewModel is null) return;
        var s = _viewModel.State;
        Disc.ArtUrl = s.AlbumArtUrl;
        Disc.IsPlaying = s.IsPlaying;

        var text = string.IsNullOrWhiteSpace(s.Artist) ? s.Title : $"{s.Title}  —  {s.Artist}";
        if (text != _marqueeSource)
        {
            _marqueeSource = text;
            RebuildMarquee();
        }
    }

    // ---- marquee ---------------------------------------------------------------------------

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double side = Math.Min(e.NewSize.Width, e.NewSize.Height);
        double height = Math.Clamp(side * 0.17, 24, 40);
        MarqueeBand.Height = height;
        MarqueeBand.CornerRadius = new CornerRadius(height / 2);
        // In the lower part of the disc, clear of the spindle hole (which is cut out of the window and
        // would otherwise interrupt the text as it scrolls past), and narrow enough to stay on the disc.
        MarqueeBand.Margin = new Thickness(side * 0.13, 0, side * 0.13, side * 0.15);
        MarqueeText.FontSize = Math.Clamp(side * 0.07, 12, 18);
        RebuildMarquee();
    }

    private void OnMarqueeClipSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MarqueeClip.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        RebuildMarquee();
    }

    private void RebuildMarquee()
    {
        _marquee?.Stop();
        _marquee = null;
        MarqueeText.Text = _marqueeSource;

        double bandWidth = MarqueeClip.ActualWidth;
        if (bandWidth <= 0 || _marqueeSource.Length == 0) return;

        MarqueeText.Width = double.NaN;
        MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = Math.Ceiling(MarqueeText.DesiredSize.Width) + 4;
        MarqueeText.Width = textWidth;

        // Enters at the right edge, leaves at the left, then starts over.
        var animation = new DoubleAnimation
        {
            From = bandWidth,
            To = -textWidth,
            Duration = TimeSpan.FromSeconds((bandWidth + textWidth) / MarqueeSpeed),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, _marqueeShift);
        Storyboard.SetTargetProperty(animation, "X");
        _marquee = new Storyboard();
        _marquee.Children.Add(animation);
        _marqueeShift.X = bandWidth;
        if (_hover) _marquee.Begin();
    }

    private void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        MarqueeBand.Opacity = on ? 1 : 0;
        if (on)
        {
            _marqueeStopTimer.Stop();
            _marquee?.Begin();
        }
        else
        {
            // Let the fade-out finish before freezing the text.
            _marqueeStopTimer.Stop();
            _marqueeStopTimer.Start();
        }
    }

    // ---- pointer ---------------------------------------------------------------------------

    /// <summary>True when the point is over the disc itself rather than the transparent corners.</summary>
    private bool OverDisc(Point p)
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        double dx = p.X - w / 2, dy = p.Y - h / 2;
        double radius = Math.Min(w, h) * 0.47;
        return dx * dx + dy * dy <= radius * radius;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        SetHover(OverDisc(point.Position));

        if (_pressed && point.Properties.IsLeftButtonPressed)
        {
            var dx = point.Position.X - _pressPoint.X;
            var dy = point.Position.Y - _pressPoint.Y;
            if (dx * dx + dy * dy >= DragThreshold * DragThreshold)
            {
                _pressed = false;
                Root.ReleasePointerCaptures();
                BeginDrag?.Invoke();
            }
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(false);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || !OverDisc(point.Position)) return;
        _pressed = true;
        _pressPoint = point.Position;
        Root.CapturePointer(e.Pointer);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool wasClick = _pressed;
        _pressed = false;
        Root.ReleasePointerCapture(e.Pointer);
        if (wasClick && OverDisc(e.GetCurrentPoint(Root).Position))
            _viewModel?.PlayPauseCommand.Execute(null);
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs e) => _pressed = false;

    // ---- context menu ----------------------------------------------------------------------

    private void OnMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || _viewModel is null) return;
        menu.Items.Clear();

        menu.Items.Add(Item(_viewModel.State.IsPlaying ? "Pause" : "Play", _viewModel.State.IsPlaying ? "" : "",
            () => _viewModel.PlayPauseCommand.Execute(null)));
        menu.Items.Add(Item("Previous", "", () => _viewModel.PreviousCommand.Execute(null)));
        menu.Items.Add(Item("Next", "", () => _viewModel.NextCommand.Execute(null)));
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Radio("Vinyl", DiscSkin.Vinyl));
        menu.Items.Add(Radio("CD", DiscSkin.Cd));
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(FaceRadio("Show album art", DiscFace.Art));
        menu.Items.Add(FaceRadio("Show visualizer", DiscFace.Visualizer));
        var spins = new ToggleMenuFlyoutItem { Text = "Visualizer spins with the disc", IsChecked = _viewModel.DiscVisualizerSpins };
        spins.Click += (_, _) => { if (_viewModel is not null) _viewModel.DiscVisualizerSpins = spins.IsChecked; };
        menu.Items.Add(spins);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("Switch to visualizer player", "", () => _viewModel.MiniPlayerKind = MiniPlayerKind.Visualizer));
        menu.Items.Add(Item("Restore full window", "", () => ToggleMini?.Invoke()));
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private RadioMenuFlyoutItem FaceRadio(string text, DiscFace face)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "DiscFace", IsChecked = _viewModel?.DiscFace == face };
        item.Click += (_, _) => { if (_viewModel is not null) _viewModel.DiscFace = face; };
        return item;
    }

    private RadioMenuFlyoutItem Radio(string text, DiscSkin skin)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "DiscSkin", IsChecked = _viewModel?.DiscSkin == skin };
        item.Click += (_, _) => { if (_viewModel is not null) _viewModel.DiscSkin = skin; };
        return item;
    }
}
