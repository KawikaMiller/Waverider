using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waverider.Visualization;
using Waverider.Visualization.Custom;

namespace Waverider.Views;

/// <summary>
/// Live editor for a <see cref="CustomVisualizer"/>. Built in code because the layer list is
/// dynamic. Every control writes straight into the visualizer's settings, which the renderer
/// reads each frame — so the preview behind this panel updates as you drag.
/// </summary>
public sealed class VisualizerEditorPanel : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 10 };
    private readonly ScrollViewer _scroll;
    private readonly HashSet<object> _expanded = new();
    private readonly Random _rng = new();
    private CustomVisualizer? _target;
    private bool _dirty;

    public VisualizerEditorPanel()
    {
        _scroll = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0)
        };
        Content = _scroll;
        Rebuild();
    }

    public VisualizerCatalog? Catalog { get; set; }

    public event Action? CloseRequested;
    /// <summary>Raised when the panel has nothing to edit and the user asks for a new visualizer.</summary>
    public event Action? NewRequested;
    public event Action<CustomVisualizer>? VisualizerCreated;
    public event Action<CustomVisualizer>? VisualizerDeleted;

    public CustomVisualizer? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            Flush();
            _target = value;
            _expanded.Clear();
            Rebuild();
        }
    }

    /// <summary>Writes pending edits to disk.</summary>
    public void Flush()
    {
        if (_dirty && _target is not null) Catalog?.Save(_target);
        _dirty = false;
    }

    private void Touch() => _dirty = true;

    private void Rebuild()
    {
        double offset = _scroll.VerticalOffset;
        _root.Children.Clear();

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "Visualizer editor",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var close = new Button { Content = "✕", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { Flush(); CloseRequested?.Invoke(); };
        header.Children.Add(close);
        _root.Children.Add(header);

        if (_target is null)
        {
            _root.Children.Add(Caption("Pick one of your own visualizers, or start a new one. Built-in visualizers and presets can be copied into an editable version."));
            _root.Children.Add(MakeButton("New visualizer", () => NewRequested?.Invoke()));
            return;
        }

        var settings = _target.Settings;

        var nameBox = new TextBox { Text = settings.Name, Header = "Name", MaxLength = 40 };
        nameBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text)) { _target.Name = nameBox.Text; Touch(); }
        };
        _root.Children.Add(nameBox);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(MakeButton("Randomize", () =>
        {
            settings.Randomize(_rng);
            Touch();
            _expanded.Clear();
            Rebuild();
        }));
        actions.Children.Add(MakeButton("Duplicate", () =>
        {
            if (Catalog is null) return;
            Flush();
            VisualizerCreated?.Invoke(Catalog.CreateNew(settings));
        }));
        actions.Children.Add(MakeButton("New", () =>
        {
            if (Catalog is null) return;
            Flush();
            VisualizerCreated?.Invoke(Catalog.CreateNew());
        }));
        _root.Children.Add(actions);

        var actions2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions2.Children.Add(MakeButton("Save", () => { Touch(); Flush(); }));
        actions2.Children.Add(MakeButton("Delete", () =>
        {
            if (Catalog is null || _target is null) return;
            var doomed = _target;
            _target = null;
            Catalog.Delete(doomed);
            _dirty = false;
            VisualizerDeleted?.Invoke(doomed);
            Rebuild();
        }));
        _root.Children.Add(actions2);

        // --- Effects -------------------------------------------------------------------
        var fxContent = new StackPanel { Spacing = 10 };
        foreach (var def in ParamDefs.Fx)
            fxContent.Children.Add(ParamRow(def, settings.Fx.Ensure(def)));

        var kaleido = new ComboBox
        {
            Header = "Mirror segments",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "1 (off)", "2", "4", "6", "8", "12" }
        };
        kaleido.SelectedItem = settings.Fx.Kaleidoscope <= 1 ? "1 (off)" : settings.Fx.Kaleidoscope.ToString();
        kaleido.SelectionChanged += (_, _) =>
        {
            if (kaleido.SelectedItem is string s)
            {
                settings.Fx.Kaleidoscope = int.Parse(s.Split(' ')[0]);
                Touch();
            }
        };
        fxContent.Children.Add(kaleido);

        var backdrop = new Slider
        {
            Header = "Background strength",
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            Value = Math.Clamp(settings.Fx.Backdrop, 0, 1)
        };
        backdrop.ValueChanged += (_, e) => { settings.Fx.Backdrop = e.NewValue; Touch(); };
        fxContent.Children.Add(backdrop);

        _root.Children.Add(MakeExpander("Effects", settings.Fx, fxContent, true));

        // --- Layers --------------------------------------------------------------------
        _root.Children.Add(new TextBlock
        {
            Text = "Layers",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        });

        for (int i = 0; i < settings.Layers.Count; i++)
            _root.Children.Add(LayerExpander(settings, i));

        if (settings.Layers.Count < CustomVisualizerSettings.MaxLayers)
        {
            var add = new Grid { ColumnSpacing = 8 };
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var kindCombo = new ComboBox
            {
                ItemsSource = Enum.GetNames<GeneratorKind>(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var addButton = MakeButton("Add layer", () =>
            {
                var kind = Enum.Parse<GeneratorKind>((string)kindCombo.SelectedItem);
                var layer = LayerSettings.Create(kind);
                settings.Layers.Add(layer);
                _expanded.Add(layer);
                Touch();
                Rebuild();
            });
            Grid.SetColumn(addButton, 1);
            add.Children.Add(kindCombo);
            add.Children.Add(addButton);
            _root.Children.Add(add);
        }

        _root.UpdateLayout();
        _scroll.ChangeView(null, offset, null, true);
    }

    private UIElement LayerExpander(CustomVisualizerSettings settings, int index)
    {
        var layer = settings.Layers[index];

        var enabled = new CheckBox { IsChecked = layer.Enabled, MinWidth = 0, Content = $"{index + 1}. {layer.Kind}" };
        enabled.Click += (_, _) => { layer.Enabled = enabled.IsChecked == true; Touch(); };

        var content = new StackPanel { Spacing = 10 };

        var kind = EnumCombo("Generator", layer.Kind, v =>
        {
            layer.Kind = v;
            foreach (var def in ParamDefs.For(v)) layer.Ensure(def);
            Touch();
            Rebuild();
        });
        content.Children.Add(kind);
        content.Children.Add(EnumCombo("Color", layer.ColorMode, v => layer.ColorMode = v));

        var additive = new CheckBox { Content = "Additive blend (bright overlap)", IsChecked = layer.Additive };
        additive.Click += (_, _) => { layer.Additive = additive.IsChecked == true; Touch(); };
        content.Children.Add(additive);

        foreach (var def in ParamDefs.For(layer.Kind))
            content.Children.Add(ParamRow(def, layer.Ensure(def)));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = MakeButton("▲", () => MoveLayer(settings, index, -1));
        var down = MakeButton("▼", () => MoveLayer(settings, index, 1));
        up.IsEnabled = index > 0;
        down.IsEnabled = index < settings.Layers.Count - 1;
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(MakeButton("Remove layer", () =>
        {
            settings.Layers.RemoveAt(index);
            Touch();
            Rebuild();
        }));
        content.Children.Add(row);

        return MakeExpander(enabled, layer, content, _expanded.Contains(layer));
    }

    private void MoveLayer(CustomVisualizerSettings settings, int index, int delta)
    {
        int to = index + delta;
        if (to < 0 || to >= settings.Layers.Count) return;
        (settings.Layers[index], settings.Layers[to]) = (settings.Layers[to], settings.Layers[index]);
        Touch();
        Rebuild();
    }

    private Expander MakeExpander(object header, object key, UIElement content, bool expanded)
    {
        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanding += (_, _) => _expanded.Add(key);
        expander.Collapsed += (_, _) => _expanded.Remove(key);
        return expander;
    }

    private UIElement ParamRow(ParamDef def, ParamValue pv)
    {
        var valueText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        void Show() => valueText.Text = def.Step >= 1 ? $"{pv.Value:0}" : $"{pv.Value:0.00}";
        Show();

        var header = new Grid();
        header.Children.Add(new TextBlock { Text = def.Label, FontSize = 12 });
        header.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = def.Min,
            Maximum = def.Max,
            StepFrequency = def.Step,
            Value = Math.Clamp(pv.Value, def.Min, def.Max),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        slider.ValueChanged += (_, e) =>
        {
            pv.Value = e.NewValue;
            Show();
            Touch();
        };

        var amount = new Slider
        {
            Minimum = -1,
            Maximum = 1,
            StepFrequency = 0.05,
            Value = Math.Clamp(pv.Amount, -1, 1),
            IsEnabled = pv.Source != AudioSource.None,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(amount, "How strongly the audio signal pushes this value (negative inverts it)");
        amount.ValueChanged += (_, e) => { pv.Amount = e.NewValue; Touch(); };

        var source = new ComboBox
        {
            ItemsSource = Enum.GetNames<AudioSource>(),
            SelectedItem = pv.Source.ToString(),
            MinWidth = 110,
            FontSize = 12
        };
        ToolTipService.SetToolTip(source, "Audio signal that modulates this value");
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is not string name) return;
            pv.Source = Enum.Parse<AudioSource>(name);
            amount.IsEnabled = pv.Source != AudioSource.None;
            Touch();
        };

        var bind = new Grid { ColumnSpacing = 10 };
        bind.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bind.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(amount, 1);
        bind.Children.Add(source);
        bind.Children.Add(amount);

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(slider);
        stack.Children.Add(bind);
        return stack;
    }

    private ComboBox EnumCombo<T>(string header, T current, Action<T> onChanged) where T : struct, Enum
    {
        var combo = new ComboBox
        {
            Header = header,
            ItemsSource = Enum.GetNames<T>(),
            SelectedItem = current.ToString(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var last = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string name) return;
            var v = Enum.Parse<T>(name);
            if (v.Equals(last)) return;
            last = v;
            onChanged(v);
            Touch();
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
