using System.Collections.ObjectModel;
using System.Diagnostics;
using Waverider.Util;
using Waverider.Visualization.Custom;

namespace Waverider.Visualization;

/// <summary>
/// The list of visualizers shown in the picker: the hand-coded ones, the WMP-style presets,
/// and user-made custom visualizers persisted as JSON under %LocalAppData%.
/// </summary>
public sealed class VisualizerCatalog
{
    private readonly string _dir;

    public ObservableCollection<IVisualizer> Items { get; } = new();

    /// <summary>The first entry in the list; shuffles through all the others.</summary>
    public AutoVisualizer Auto { get; }

    public VisualizerCatalog()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Waverider", "Visualizers");

        Auto = new AutoVisualizer(Items, AppSettings.AutoVisualizerSeconds);
        Items.Add(Auto);

        Items.Add(new BarsVisualizer());
        Items.Add(new WaveVisualizer());
        Items.Add(new RadialVisualizer());
        Items.Add(new SpectrogramVisualizer());
        Items.Add(new ParticleVisualizer());
        Items.Add(new TunnelVisualizer());
        Items.Add(new PlasmaVisualizer());
        Items.Add(new StarfieldVisualizer());

        foreach (var preset in VisualizerPresets.All()) Items.Add(preset);
        LoadCustom();
    }

    private void LoadCustom()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var path in Directory.GetFiles(_dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var settings = CustomVisualizerSettings.FromJson(File.ReadAllText(path));
                if (settings is null) continue;
                settings.SourcePath = path;
                Items.Add(new CustomVisualizer(settings));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Skipping visualizer '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>Adds a new user visualizer (optionally cloned from a template) and saves it.</summary>
    public CustomVisualizer CreateNew(CustomVisualizerSettings? template = null)
    {
        var settings = template?.Clone() ?? CustomVisualizerSettings.CreateDefault();
        if (template is not null) settings.Name = UniqueName(template.Name.Replace("WMP · ", "") + " (copy)");
        else settings.Name = UniqueName(settings.Name);

        var visualizer = new CustomVisualizer(settings);
        Items.Add(visualizer);
        Save(visualizer);
        return visualizer;
    }

    public void Save(CustomVisualizer visualizer)
    {
        if (visualizer.IsBuiltIn) return;
        try
        {
            Directory.CreateDirectory(_dir);
            var s = visualizer.Settings;
            if (string.IsNullOrEmpty(s.SourcePath))
                s.SourcePath = Path.Combine(_dir, $"{Sanitize(s.Name)}-{Guid.NewGuid().ToString("N")[..8]}.json");
            File.WriteAllText(s.SourcePath, s.ToJson());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving visualizer failed: {ex.Message}");
        }
    }

    public void Delete(CustomVisualizer visualizer)
    {
        if (visualizer.IsBuiltIn) return;
        try
        {
            if (File.Exists(visualizer.Settings.SourcePath)) File.Delete(visualizer.Settings.SourcePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deleting visualizer failed: {ex.Message}");
        }
        Items.Remove(visualizer);
        visualizer.Dispose();
    }

    private string UniqueName(string baseName)
    {
        var existing = new HashSet<string>(Items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
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
        var chars = name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "visualizer" : s;
    }
}
