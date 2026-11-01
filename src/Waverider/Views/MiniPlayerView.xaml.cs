using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waverider.Audio;
using Waverider.Disc;
using Waverider.ViewModels;

namespace Waverider.Views;

public sealed partial class MiniPlayerView : UserControl
{
    private PlayerViewModel? _viewModel;

    public MiniPlayerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        Visualizer.Theme = _viewModel.Theme;
        Visualizer.Visualizer = _viewModel.SelectedVisualizer;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // See PlayerView: keep VM subscriptions across reattachment.
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State):
                Refresh(); break;
            case nameof(PlayerViewModel.Theme):
                Visualizer.Theme = _viewModel.Theme; break;
            case nameof(PlayerViewModel.SelectedVisualizer):
                Visualizer.Visualizer = _viewModel.SelectedVisualizer; break;
        }
    }

    private void OnFrameReady(AudioFrame frame) => Visualizer.UpdateFrame(frame);

    private void Refresh()
    {
        if (_viewModel is null) return;
        var s = _viewModel.State;
        TrackTitle.Text = s.Title;
        TrackArtist.Text = s.Artist;
        PlayPauseIcon.Glyph = s.IsPlaying ? "" : "";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _viewModel?.PlayPauseCommand.Execute(null);
    private void OnNextClick(object sender, RoutedEventArgs e) => _viewModel?.NextCommand.Execute(null);
    private void OnPreviousClick(object sender, RoutedEventArgs e) => _viewModel?.PreviousCommand.Execute(null);
    private void OnExpandClick(object sender, RoutedEventArgs e) => ToggleMini?.Invoke();
    private void OnSwitchToDiscClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.MiniPlayerKind = MiniPlayerKind.Disc;
    }

    // Track info and the restore button only show while the pointer is over the mini player.
    // PointerMoved is handled too so the state recovers if an Entered/Exited pair is missed
    // (e.g. when the window is activated with the cursor already over it).
    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoverInfo.Opacity = 1;
    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoverInfo.Opacity = 1;
    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoverInfo.Opacity = 0;
}