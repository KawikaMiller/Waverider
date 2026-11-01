using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waverider.Theming;

namespace Waverider.Views;

/// <summary>
/// Live editor for a user <see cref="Theme"/>. Every color picker tick writes into the theme and
/// re-applies it (throttled), so the whole app updates while you drag.
/// </summary>
public sealed class ThemeEditorPanel : UserControl
{
    private sealed record ColorSlot(string Label, string Hint, Func<Theme, string> Get, Action<Theme, string> Set);

    private static readonly ColorSlot[] Slots =
    {
        new("Accent", "Buttons, sliders, seek bar", t => t.Accent, (t, v) => t.Accent = v),
        new("Visualizer primary", "Main visualizer color", t => t.Primary, (t, v) => t.Primary = v),
        new("Visualizer secondary", "Second visualizer color", t => t.Secondary, (t, v) => t.Secondary = v),
        new("Window", "App background", t => t.Background, (t, v) => t.Background = v),
        new("Panels", "Cards, transport bar, drawers", t => ThemeService.PanelOf(t), (t, v) => { t.Panel = v; t.Surface = v; }),
        new("Stage", "Behind the visualizer", t => ThemeService.StageOf(t), (t, v) => t.Stage = v),
        new("Outlines", "Panel borders", t => ThemeService.StrokeOf(t), (t, v) => t.Stroke = v),
    };

    private readonly StackPanel _root = new() { Spacing = 10 };
    private readonly ScrollViewer _scroll;
    private readonly Random _rng = new();
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private Theme? _target;
    private bool _dirty;
    private bool _applyPending;

    public ThemeEditorPanel()
    {
        _scroll = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0)
        };
        Content = _scroll;

        _applyTimer.Tick += (_, _) =>
        {
            if (!_applyPending || _target is null) { _applyTimer.Stop(); return; }
            _applyPending = false;
            Themes?.NotifyEdited(_target);
        };
        Rebuild();
    }

    public ThemeService? Themes { get; set; }

    public event Action? CloseRequested;
    /// <summary>The panel has nothing to edit and the user asked for a new theme.</summary>
    public event Action? NewRequested;
    public event Action<Theme>? ThemeCreated;
    public event Action<Theme>? ThemeDeleted;

    public Theme? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            Flush();
            _target = value;
            Rebuild();
        }
    }

    public void Flush()
    {
        if (_dirty && _target is not null) Themes?.Save(_target);
        _dirty = false;
    }

    private void Touch() => _dirty = true;

    private void RequestApply()
    {
        Touch();
        _applyPending = true;
        if (!_applyTimer.IsEnabled) _applyTimer.Start();
    }

    private void ApplyNow()
    {
        Touch();
        _applyPending = false;
        if (_target is not null) Themes?.NotifyEdited(_target);
    }

    private void Rebuild()
    {
        double offset = _scroll.VerticalOffset;
        _root.Children.Clear();

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "Theme editor",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var close = new Button { Content = "✕", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { Flush(); CloseRequested?.Invoke(); };
        header.Children.Add(close);
        _root.Children.Add(header);

        if (_target is null)
        {
            _root.Children.Add(Caption("Built-in themes are read-only. Create your own, or copy the current theme to start from it."));
            _root.Children.Add(MakeButton("New theme", () => NewRequested?.Invoke()));
            return;
        }

        var theme = _target;

        var nameBox = new TextBox { Text = theme.Name, Header = "Name", MaxLength = 40 };
        nameBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text)) { theme.Name = nameBox.Text; Touch(); }
        };
        _root.Children.Add(nameBox);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(MakeButton("Randomize", () =>
        {
            ThemeRandomizer.Randomize(theme, _rng);
            ApplyNow();
            Rebuild();
        }));
        actions.Children.Add(MakeButton("Duplicate", () =>
        {
            if (Themes is null) return;
            Flush();
            ThemeCreated?.Invoke(Themes.CreateNew(theme));
        }));
        actions.Children.Add(MakeButton("New", () =>
        {
            if (Themes is null) return;
            Flush();
            ThemeCreated?.Invoke(Themes.CreateNew());
        }));
        _root.Children.Add(actions);

        var actions2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions2.Children.Add(MakeButton("Save", () => { Touch(); Flush(); }));
        actions2.Children.Add(MakeButton("Delete", () =>
        {
            if (Themes is null || _target is null) return;
            var doomed = _target;
            _target = null;
            _dirty = false;
            Themes.Delete(doomed);
            ThemeDeleted?.Invoke(doomed);
            Rebuild();
        }));
        _root.Children.Add(actions2);

        // Mode + material
        _root.Children.Add(StringCombo("Mode", new[] { "Light", "Dark", "Default" }, theme.ElementTheme, v =>
        {
            theme.ElementTheme = v;
            ApplyNow();
        }));
        _root.Children.Add(StringCombo("Window material", new[] { "None", "Mica", "MicaAlt", "Acrylic" }, theme.Backdrop, v =>
        {
            theme.Backdrop = v;
            ApplyNow();
        }));

        _root.Children.Add(new TextBlock
        {
            Text = "Colors",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        });
        foreach (var slot in Slots) _root.Children.Add(ColorRow(theme, slot));

        _root.UpdateLayout();
        _scroll.ChangeView(null, offset, null, true);
    }

    private UIElement ColorRow(Theme theme, ColorSlot slot)
    {
        var current = ThemeService.ParseColor(slot.Get(theme));

        var swatch = new Border
        {
            Width = 34,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 128, 128, 128)),
            Background = new SolidColorBrush(current)
        };
        var hexText = new TextBlock
        {
            Text = ThemeService.ToHex(current),
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = slot.Label });
        labels.Children.Add(new TextBlock { Text = slot.Hint, FontSize = 11, Opacity = 0.6 });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labels, 1);
        Grid.SetColumn(hexText, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(labels);
        grid.Children.Add(hexText);

        var picker = new ColorPicker
        {
            Color = current,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true
        };
        picker.ColorChanged += (_, e) =>
        {
            var hex = ThemeService.ToHex(e.NewColor);
            slot.Set(theme, hex);
            swatch.Background = new SolidColorBrush(e.NewColor);
            hexText.Text = hex;
            RequestApply();
        };

        return new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
            Flyout = new Flyout { Content = picker }
        };
    }

    private ComboBox StringCombo(string header, string[] options, string current, Action<string> onChanged)
    {
        var combo = new ComboBox
        {
            Header = header,
            ItemsSource = options,
            SelectedItem = options.Contains(current) ? current : options[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        string last = (string)combo.SelectedItem;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string value || value == last) return;
            last = value;
            onChanged(value);
        };
        return combo;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap
    };

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }
}
