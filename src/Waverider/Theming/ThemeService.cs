using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.UI;

namespace Waverider.Theming;

public sealed class ThemeService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _themesDir;
    private readonly string _userDir;
    private Theme _current = new();

    public ObservableCollection<Theme> Available { get; } = new();
    public Theme Current => _current;

    public event Action<Theme>? ThemeChanged;

    public ThemeService()
    {
        _themesDir = Path.Combine(AppContext.BaseDirectory, "Themes");
        Directory.CreateDirectory(_themesDir);

        // User-made themes live outside the install folder so they survive updates.
        _userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Waverider", "Themes");
    }

    public async Task LoadAsync()
    {
        Available.Clear();
        await LoadFromAsync(_themesDir, builtIn: true);
        await LoadFromAsync(_userDir, builtIn: false);

        if (Available.Count == 0) Available.Add(new Theme { IsBuiltIn = true });
        var saved = Waverider.Util.AppSettings.ThemeName;
        Apply(Available.FirstOrDefault(t => t.Name == saved) ?? Available[0]);
    }

    private async Task LoadFromAsync(string dir, bool builtIn)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var theme = await JsonSerializer.DeserializeAsync<Theme>(fs, JsonOpts);
                if (theme is null) continue;
                theme.SourcePath = path;
                theme.IsBuiltIn = builtIn;
                Available.Add(theme);
            }
            catch { /* skip malformed */ }
        }
    }

    public void Apply(Theme theme)
    {
        _current = theme;
        Waverider.Util.AppSettings.ThemeName = theme.Name;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>Re-applies the active theme after an in-place edit (e.g. a color picker drag).</summary>
    public void NotifyEdited(Theme theme)
    {
        if (ReferenceEquals(theme, _current)) ThemeChanged?.Invoke(theme);
    }

    /// <summary>Adds an editable theme (cloned from <paramref name="template"/> when given) and saves it.</summary>
    public Theme CreateNew(Theme? template = null)
    {
        var theme = template is null ? ThemeRandomizer.Starter() : Clone(template);
        theme.Name = UniqueName(template is null ? "My Theme" : $"{template.Name} (copy)");
        theme.IsBuiltIn = false;
        theme.SourcePath = string.Empty;
        Available.Add(theme);
        Save(theme);
        return theme;
    }

    public void Save(Theme theme)
    {
        if (theme.IsBuiltIn) return;
        try
        {
            Directory.CreateDirectory(_userDir);
            if (string.IsNullOrEmpty(theme.SourcePath))
                theme.SourcePath = Path.Combine(_userDir, $"{Sanitize(theme.Name)}-{Guid.NewGuid().ToString("N")[..8]}.json");
            File.WriteAllText(theme.SourcePath, JsonSerializer.Serialize(theme, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Saving theme failed: {ex.Message}");
        }
    }

    public void Delete(Theme theme)
    {
        if (theme.IsBuiltIn) return;
        try
        {
            if (File.Exists(theme.SourcePath)) File.Delete(theme.SourcePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Deleting theme failed: {ex.Message}");
        }
        Available.Remove(theme);
    }

    public static Theme Clone(Theme theme)
        => JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme, JsonOpts), JsonOpts)!;

    private string UniqueName(string baseName)
    {
        var existing = new HashSet<string>(Available.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_');
        return s.Length == 0 ? "theme" : s;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ParseColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Colors.White;
        var s = hex.TrimStart('#');
        byte a = 0xFF, r, g, b;
        if (s.Length == 8)
        {
            a = Convert.ToByte(s[..2], 16);
            r = Convert.ToByte(s.Substring(2, 2), 16);
            g = Convert.ToByte(s.Substring(4, 2), 16);
            b = Convert.ToByte(s.Substring(6, 2), 16);
        }
        else if (s.Length == 6)
        {
            r = Convert.ToByte(s[..2], 16);
            g = Convert.ToByte(s.Substring(2, 2), 16);
            b = Convert.ToByte(s.Substring(4, 2), 16);
        }
        else return Colors.White;
        return Color.FromArgb(a, r, g, b);
    }

    public static SKColor ParseSKColor(string hex)
    {
        var c = ParseColor(hex);
        return new SKColor(c.R, c.G, c.B, c.A);
    }

    public static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    // ---- Panel / stage palette ---------------------------------------------------------

    public static string PanelOf(Theme t) => t.Panel ?? t.Surface;
    public static string StageOf(Theme t) => t.Stage ?? Mix(t.Background, "#000000", 0.45f);
    public static string StrokeOf(Theme t) => t.Stroke ?? Mix(PanelOf(t), t.Foreground, 0.18f);

    /// <summary>The theme as the visualizers should see it: drawn on the stage, with readable foreground.</summary>
    public static Theme ForStage(Theme t)
    {
        var stage = StageOf(t);
        var c = ParseColor(stage);
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return new Theme
        {
            Name = t.Name,
            Backdrop = t.Backdrop,
            ElementTheme = t.ElementTheme,
            Accent = t.Accent,
            Primary = t.Primary,
            Secondary = t.Secondary,
            Background = stage,
            Surface = t.Surface,
            Foreground = luminance < 0.5 ? "#FFFFFF" : t.Foreground,
            Muted = t.Muted,
            Panel = t.Panel,
            Stage = stage,
            Stroke = t.Stroke
        };
    }

    /// <summary>Pushes the theme's panel colors into the shared brushes declared in App.xaml.</summary>
    public static void ApplyPanelBrushes(Theme t)
    {
        var res = Application.Current.Resources;
        void Set(string key, string hex)
        {
            if (res.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
                brush.Color = ParseColor(hex);
        }
        Set("AppPanelBrush", PanelOf(t));
        Set("AppStageBrush", StageOf(t));
        Set("AppStrokeBrush", StrokeOf(t));
    }

    private static string Mix(string fromHex, string toHex, float amount)
    {
        var a = ParseColor(fromHex);
        var b = ParseColor(toHex);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return $"#{L(a.R, b.R):X2}{L(a.G, b.G):X2}{L(a.B, b.B):X2}";
    }

    /// <summary>
    /// Re-points the system accent palette at the theme's accent so stock controls (accent
    /// buttons, sliders, progress bars) pick it up instead of the Windows accent color.
    /// </summary>
    public static void ApplyAccentResources(string accentHex)
    {
        var baseColor = ParseSKColor(accentHex);
        baseColor.ToHsv(out float h, out float s, out float v);

        Color Shade(float valueScale, float satScale)
        {
            var c = SKColor.FromHsv(h, Math.Clamp(s * satScale, 0, 100), Math.Clamp(v * valueScale, 0, 100));
            return Color.FromArgb(255, c.Red, c.Green, c.Blue);
        }

        var main = Color.FromArgb(255, baseColor.Red, baseColor.Green, baseColor.Blue);
        var light1 = Shade(1.10f, 0.90f);
        var light2 = Shade(1.20f, 0.80f);
        var light3 = Shade(1.30f, 0.70f);
        var dark1 = Shade(0.85f, 1.00f);
        var dark2 = Shade(0.72f, 1.00f);
        var dark3 = Shade(0.60f, 1.00f);

        // For dictionaries WinUI has not realized yet, they will pick these up when they load.
        var res = Application.Current.Resources;
        res["SystemAccentColor"] = main;
        res["SystemAccentColorLight1"] = light1;
        res["SystemAccentColorLight2"] = light2;
        res["SystemAccentColorLight3"] = light3;
        res["SystemAccentColorDark1"] = dark1;
        res["SystemAccentColorDark2"] = dark2;
        res["SystemAccentColorDark3"] = dark3;

        // Already-realized control brushes captured the old colors, so recolor those instances in
        // place. This is what makes accent edits show up immediately.
        var light = new Dictionary<string, Color>
        {
            ["AccentFillColorDefaultBrush"] = dark1,
            ["AccentFillColorSecondaryBrush"] = dark1,
            ["AccentFillColorTertiaryBrush"] = dark1,
            ["AccentFillColorSelectedTextBackgroundBrush"] = dark1,
            ["AccentTextFillColorPrimaryBrush"] = dark2,
            ["AccentTextFillColorSecondaryBrush"] = dark3,
            ["AccentTextFillColorTertiaryBrush"] = dark1,
        };
        var dark = new Dictionary<string, Color>
        {
            ["AccentFillColorDefaultBrush"] = light2,
            ["AccentFillColorSecondaryBrush"] = light2,
            ["AccentFillColorTertiaryBrush"] = light2,
            ["AccentFillColorSelectedTextBackgroundBrush"] = dark1,
            ["AccentTextFillColorPrimaryBrush"] = light3,
            ["AccentTextFillColorSecondaryBrush"] = light3,
            ["AccentTextFillColorTertiaryBrush"] = light2,
        };
        RecolorBrushes(res, light, dark, darkMode: null);
    }

    // Walks the resource tree; under a "Dark" theme dictionary the dark palette applies, under
    // "Light"/"Default" the light one. Dictionaries outside any theme dictionary are left alone.
    private static void RecolorBrushes(
        ResourceDictionary dict, Dictionary<string, Color> light, Dictionary<string, Color> dark, bool? darkMode)
    {
        if (darkMode is bool isDark)
        {
            var palette = isDark ? dark : light;
            foreach (var (key, color) in palette)
            {
                if (dict.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                    brush.Color = color;
            }
        }

        foreach (var merged in dict.MergedDictionaries)
            RecolorBrushes(merged, light, dark, darkMode);

        foreach (var (key, value) in dict.ThemeDictionaries)
        {
            if (value is not ResourceDictionary themed) continue;
            string name = key?.ToString() ?? "";
            if (name == "HighContrast") continue;
            RecolorBrushes(themed, light, dark, darkMode: name == "Dark");
        }
    }
}
