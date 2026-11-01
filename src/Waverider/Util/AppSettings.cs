using System.Text.Json;
using Waverider.Disc;

namespace Waverider.Util;

/// <summary>Tiny persisted user settings (volume, ...), stored as JSON under %LocalAppData%.</summary>
public static class AppSettings
{
    private sealed class Data
    {
        public double Volume { get; set; } = 0.8;
        public string? ThemeName { get; set; }
        public bool BrowseLayout { get; set; }
        public string? VisualizerName { get; set; }
        public string? DiscSkin { get; set; }
        public string? MiniPlayerKind { get; set; }
        public string? DiscFace { get; set; }
        public bool DiscVisualizerSpins { get; set; }
        public string? CaptureMode { get; set; }
        public string? CaptureApp { get; set; }
    }

    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Waverider", "settings.json");

    private static readonly Data Current = Load();

    /// <summary>Player volume, 0..1.</summary>
    public static double Volume
    {
        get => Current.Volume;
        set
        {
            Current.Volume = Math.Clamp(value, 0, 1);
            Save();
        }
    }

    /// <summary>True when the full-size window uses the Browse layout instead of Now Playing.</summary>
    public static bool BrowseLayout
    {
        get => Current.BrowseLayout;
        set { Current.BrowseLayout = value; Save(); }
    }

    /// <summary>Name of the last selected theme.</summary>
    public static string? ThemeName
    {
        get => Current.ThemeName;
        set { Current.ThemeName = value; Save(); }
    }

    /// <summary>Name of the last selected visualizer.</summary>
    public static string? VisualizerName
    {
        get => Current.VisualizerName;
        set { Current.VisualizerName = value; Save(); }
    }

    /// <summary>Which disc (vinyl or CD) replaces the album art.</summary>
    public static DiscSkin DiscSkin
    {
        get => Enum.TryParse<DiscSkin>(Current.DiscSkin, out var v) ? v : Disc.DiscSkin.Vinyl;
        set { Current.DiscSkin = value.ToString(); Save(); }
    }

    /// <summary>Which mini player mini mode shows (visualizer or spinning disc).</summary>
    public static MiniPlayerKind MiniPlayerKind
    {
        get => Enum.TryParse<MiniPlayerKind>(Current.MiniPlayerKind, out var v) ? v : Disc.MiniPlayerKind.Visualizer;
        set { Current.MiniPlayerKind = value.ToString(); Save(); }
    }

    /// <summary>Whether the disc shows the album art or the visualizer.</summary>
    public static DiscFace DiscFace
    {
        get => Enum.TryParse<DiscFace>(Current.DiscFace, out var v) ? v : Disc.DiscFace.Art;
        set { Current.DiscFace = value.ToString(); Save(); }
    }

    /// <summary>True when the visualizer on the disc turns with it; false keeps it upright while the disc spins.</summary>
    public static bool DiscVisualizerSpins
    {
        get => Current.DiscVisualizerSpins;
        set { Current.DiscVisualizerSpins = value; Save(); }
    }

    /// <summary>Last chosen audio capture mode (an <c>AudioCaptureMode</c> name).</summary>
    public static string? CaptureMode
    {
        get => Current.CaptureMode;
        set { Current.CaptureMode = value; Save(); }
    }

    /// <summary>Executable name of the application chosen in Application capture mode.</summary>
    public static string? CaptureApp
    {
        get => Current.CaptureApp;
        set { Current.CaptureApp = value; Save(); }
    }

    private static Data Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(Path_)) ?? new Data();
        }
        catch { /* fall back to defaults */ }
        return new Data();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(Current));
        }
        catch { /* settings are best-effort */ }
    }
}
