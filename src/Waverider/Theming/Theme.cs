using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Waverider.Theming;

public sealed class Theme : INotifyPropertyChanged
{
    private string _name = "Untitled";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            // Lets pickers that display the name update while it is being edited.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public string Backdrop { get; set; } = "Mica"; // Mica | MicaAlt | Acrylic | None
    public string ElementTheme { get; set; } = "Default"; // Default | Light | Dark
    public string Accent { get; set; } = "#1DB954";
    public string Primary { get; set; } = "#1DB954";
    public string Secondary { get; set; } = "#1ED760";
    public string Background { get; set; } = "#101010";
    public string Surface { get; set; } = "#181818";
    public string Foreground { get; set; } = "#FFFFFF";
    public string Muted { get; set; } = "#B3B3B3";

    // Optional extras. When omitted they are derived from the colors above (see ThemeService).
    /// <summary>Cards and panels (album art, library drawer, transport bar).</summary>
    public string? Panel { get; set; }
    /// <summary>Background behind the visualizer; dark by default so glow effects read well.</summary>
    public string? Stage { get; set; }
    /// <summary>Panel outlines.</summary>
    public string? Stroke { get; set; }

    [JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Ships with the app (read-only); customizing one makes an editable copy.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}
