using System.Text.Json;
using System.Text.Json.Serialization;
using Waverider.Audio;

namespace Waverider.Visualization.Custom;

public enum AudioSource { None, Bass, Mid, Treble, Beat, Loudness }

public enum GeneratorKind { Bars, Wave, Radial, Rings, Scope, Particles, Blobs }

public enum ColorMode { Theme, Rainbow, Solid }

/// <summary>Describes one tweakable parameter: its range and the slider step in the editor.</summary>
public sealed record ParamDef(string Key, string Label, double Min, double Max, double Default, double Step);

public static class ParamDefs
{
    public static readonly ParamDef Count = new("count", "Count", 3, 96, 32, 1);
    public static readonly ParamDef Size = new("size", "Size", 0.05, 1, 0.5, 0.01);
    public static readonly ParamDef Thickness = new("thickness", "Thickness", 1, 24, 4, 0.5);
    public static readonly ParamDef Amplitude = new("amplitude", "Amplitude", 0, 3, 1, 0.05);
    public static readonly ParamDef Spin = new("spin", "Spin (°/s)", -180, 180, 0, 1);
    public static readonly ParamDef Glow = new("glow", "Glow", 0, 1, 0.4, 0.01);
    public static readonly ParamDef Opacity = new("opacity", "Opacity", 0.05, 1, 0.9, 0.01);
    public static readonly ParamDef Hue = new("hue", "Hue", 0, 360, 0, 1);

    public static readonly ParamDef Trails = new("trails", "Trails", 0, 0.98, 0.8, 0.01);
    public static readonly ParamDef Zoom = new("zoom", "Zoom (per sec)", -0.6, 0.6, 0, 0.01);
    public static readonly ParamDef Rotate = new("rotate", "Rotate (°/s)", -90, 90, 0, 1);
    public static readonly ParamDef HueCycle = new("hueCycle", "Hue cycle (°/s)", 0, 180, 15, 1);

    public static readonly IReadOnlyList<ParamDef> Fx = new[] { Trails, Zoom, Rotate, HueCycle };

    private static readonly Dictionary<string, ParamDef> ByKey =
        new[] { Count, Size, Thickness, Amplitude, Spin, Glow, Opacity, Hue, Trails, Zoom, Rotate, HueCycle }
            .ToDictionary(d => d.Key);

    public static ParamDef Get(string key) => ByKey[key];

    /// <summary>The parameters that a generator actually reads, in editor order.</summary>
    public static IReadOnlyList<ParamDef> For(GeneratorKind kind) => kind switch
    {
        GeneratorKind.Bars => new[] { Count, Size, Thickness, Amplitude, Glow, Opacity, Hue, Spin },
        GeneratorKind.Wave => new[] { Size, Thickness, Amplitude, Glow, Opacity, Hue, Spin },
        GeneratorKind.Radial => new[] { Count, Size, Thickness, Amplitude, Glow, Opacity, Hue, Spin },
        GeneratorKind.Rings => new[] { Count, Size, Thickness, Amplitude, Glow, Opacity, Hue, Spin },
        GeneratorKind.Scope => new[] { Size, Thickness, Amplitude, Glow, Opacity, Hue, Spin },
        GeneratorKind.Particles => new[] { Count, Size, Thickness, Amplitude, Glow, Opacity, Hue },
        GeneratorKind.Blobs => new[] { Count, Size, Amplitude, Glow, Opacity, Hue, Spin },
        _ => Array.Empty<ParamDef>()
    };
}

/// <summary>A parameter's base value, plus an optional audio signal that modulates it.</summary>
public sealed class ParamValue
{
    public double Value { get; set; }
    public AudioSource Source { get; set; } = AudioSource.None;
    /// <summary>-1..1, as a fraction of the parameter's full range.</summary>
    public double Amount { get; set; } = 0.5;

    public ParamValue() { }

    public ParamValue(double value, AudioSource source = AudioSource.None, double amount = 0.5)
    {
        Value = value;
        Source = source;
        Amount = amount;
    }

    public float Eval(ParamDef def, AudioFrame frame)
    {
        double v = Value;
        if (Source != AudioSource.None)
            v += Level(frame, Source) * Amount * (def.Max - def.Min);
        return (float)Math.Clamp(v, def.Min, def.Max);
    }

    public static float Level(AudioFrame f, AudioSource source) => source switch
    {
        AudioSource.Bass => f.Bass,
        AudioSource.Mid => f.Mid,
        AudioSource.Treble => f.Treble,
        AudioSource.Beat => f.Beat,
        AudioSource.Loudness => Math.Clamp(f.Rms * 4f, 0f, 1f),
        _ => 0f
    };
}

/// <summary>Per-layer mutable render state (not persisted).</summary>
public sealed class LayerRuntime
{
    public float Angle;
    public double SpawnAccumulator;
    public readonly List<Particle> Particles = new();
}

public struct Particle
{
    public float X, Y, VX, VY, Life, MaxLife, T;
}

public sealed class LayerSettings
{
    public GeneratorKind Kind { get; set; } = GeneratorKind.Bars;
    public bool Enabled { get; set; } = true;
    public ColorMode ColorMode { get; set; } = ColorMode.Theme;
    public bool Additive { get; set; } = true;
    public Dictionary<string, ParamValue> Params { get; set; } = new();

    [JsonIgnore]
    public LayerRuntime Runtime { get; } = new();

    public float Get(string key, AudioFrame frame)
    {
        var def = ParamDefs.Get(key);
        return Params.TryGetValue(key, out var pv) ? pv.Eval(def, frame) : (float)def.Default;
    }

    public ParamValue Ensure(ParamDef def)
    {
        if (!Params.TryGetValue(def.Key, out var pv))
            Params[def.Key] = pv = new ParamValue(def.Default);
        return pv;
    }

    /// <summary>Fluent setter used by presets: base value plus an optional audio binding.</summary>
    public LayerSettings Set(ParamDef def, double value, AudioSource source = AudioSource.None, double amount = 0.5)
    {
        Params[def.Key] = new ParamValue(value, source, amount);
        return this;
    }

    public static LayerSettings Create(GeneratorKind kind)
    {
        var layer = new LayerSettings { Kind = kind };
        foreach (var def in ParamDefs.For(kind)) layer.Ensure(def);

        switch (kind)
        {
            case GeneratorKind.Bars:
                layer.Set(ParamDefs.Count, 48).Set(ParamDefs.Size, 0.45).Set(ParamDefs.Thickness, 6);
                break;
            case GeneratorKind.Wave:
                layer.Set(ParamDefs.Size, 0.3).Set(ParamDefs.Thickness, 3);
                break;
            case GeneratorKind.Radial:
                layer.Set(ParamDefs.Count, 64).Set(ParamDefs.Size, 0.25).Set(ParamDefs.Thickness, 3);
                break;
            case GeneratorKind.Rings:
                layer.Set(ParamDefs.Count, 8).Set(ParamDefs.Size, 0.6).Set(ParamDefs.Thickness, 3);
                break;
            case GeneratorKind.Scope:
                layer.Set(ParamDefs.Size, 0.25).Set(ParamDefs.Thickness, 3);
                break;
            case GeneratorKind.Particles:
                layer.Set(ParamDefs.Count, 32).Set(ParamDefs.Size, 0.1).Set(ParamDefs.Thickness, 3).Set(ParamDefs.Amplitude, 0.8);
                break;
            case GeneratorKind.Blobs:
                layer.Set(ParamDefs.Count, 6).Set(ParamDefs.Size, 0.3).Set(ParamDefs.Spin, 8).Set(ParamDefs.Opacity, 0.55);
                break;
        }
        return layer;
    }
}

public sealed class FxSettings
{
    public Dictionary<string, ParamValue> Params { get; set; } = new();
    /// <summary>Mirror segments: 1 = off, otherwise 2, 4, 6, 8 ...</summary>
    public int Kaleidoscope { get; set; } = 1;
    /// <summary>0..1 — how strongly the theme background is painted behind the effect.</summary>
    public double Backdrop { get; set; } = 0.35;

    public float Get(string key, AudioFrame frame)
    {
        var def = ParamDefs.Get(key);
        return Params.TryGetValue(key, out var pv) ? pv.Eval(def, frame) : (float)def.Default;
    }

    public ParamValue Ensure(ParamDef def)
    {
        if (!Params.TryGetValue(def.Key, out var pv))
            Params[def.Key] = pv = new ParamValue(def.Default);
        return pv;
    }

    public FxSettings Set(ParamDef def, double value, AudioSource source = AudioSource.None, double amount = 0.5)
    {
        Params[def.Key] = new ParamValue(value, source, amount);
        return this;
    }
}

public sealed class CustomVisualizerSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Name { get; set; } = "My Visualizer";
    public List<LayerSettings> Layers { get; set; } = new();
    public FxSettings Fx { get; set; } = new();

    [JsonIgnore] public string SourcePath { get; set; } = string.Empty;
    [JsonIgnore] public bool IsBuiltIn { get; set; }

    public const int MaxLayers = 8;

    public static CustomVisualizerSettings CreateDefault()
    {
        var s = new CustomVisualizerSettings { Name = "My Visualizer" };
        s.Layers.Add(LayerSettings.Create(GeneratorKind.Wave)
            .Set(ParamDefs.Amplitude, 1.2, AudioSource.Bass, 0.2));
        s.Layers.Add(LayerSettings.Create(GeneratorKind.Rings)
            .Set(ParamDefs.Size, 0.55, AudioSource.Beat, 0.12));
        foreach (var layer in s.Layers) layer.ColorMode = ColorMode.Rainbow;
        foreach (var def in ParamDefs.Fx) s.Fx.Ensure(def);
        s.Fx.Set(ParamDefs.Trails, 0.82).Set(ParamDefs.HueCycle, 20);
        return s;
    }

    public CustomVisualizerSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var copy = JsonSerializer.Deserialize<CustomVisualizerSettings>(json, JsonOpts)!;
        copy.IsBuiltIn = false;
        copy.SourcePath = string.Empty;
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static CustomVisualizerSettings? FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<CustomVisualizerSettings>(json, JsonOpts);
        if (s is null) return null;
        foreach (var layer in s.Layers)
            foreach (var def in ParamDefs.For(layer.Kind)) layer.Ensure(def);
        foreach (var def in ParamDefs.Fx) s.Fx.Ensure(def);
        return s;
    }

    /// <summary>Replaces the layers and effects with a random (but sensible) combination.</summary>
    public void Randomize(Random rng)
    {
        var kinds = Enum.GetValues<GeneratorKind>();
        Layers.Clear();
        int count = rng.Next(2, 5);
        for (int i = 0; i < count; i++)
        {
            var layer = LayerSettings.Create(kinds[rng.Next(kinds.Length)]);
            layer.ColorMode = (ColorMode)rng.Next(0, 3);
            layer.Additive = rng.NextDouble() < 0.7;
            foreach (var def in ParamDefs.For(layer.Kind))
            {
                if (def.Key == ParamDefs.Opacity.Key)
                {
                    layer.Set(def, 0.6 + rng.NextDouble() * 0.4);
                    continue;
                }
                double range = def.Max - def.Min;
                double value = def.Min + range * (0.15 + rng.NextDouble() * 0.6);
                if (def.Key == ParamDefs.Size.Key) value = 0.15 + rng.NextDouble() * 0.55;
                if (def.Key == ParamDefs.Spin.Key) value = rng.NextDouble() < 0.5 ? 0 : (rng.NextDouble() - 0.5) * 120;
                if (def.Key == ParamDefs.Hue.Key) value = rng.NextDouble() * 360;
                var source = AudioSource.None;
                double amount = 0.5;
                bool bindable = def.Key is "size" or "amplitude" or "thickness" or "glow" or "count";
                if (bindable && rng.NextDouble() < 0.45)
                {
                    source = (AudioSource)rng.Next(1, 6);
                    amount = 0.15 + rng.NextDouble() * 0.35;
                }
                layer.Set(def, value, source, amount);
            }
            Layers.Add(layer);
        }

        Fx.Params.Clear();
        Fx.Set(ParamDefs.Trails, 0.5 + rng.NextDouble() * 0.42);
        Fx.Set(ParamDefs.Zoom, rng.NextDouble() < 0.5 ? 0 : (rng.NextDouble() - 0.5) * 0.3,
            rng.NextDouble() < 0.4 ? AudioSource.Beat : AudioSource.None, 0.1);
        Fx.Set(ParamDefs.Rotate, rng.NextDouble() < 0.5 ? 0 : (rng.NextDouble() - 0.5) * 60);
        Fx.Set(ParamDefs.HueCycle, rng.NextDouble() * 60);
        int[] segments = { 1, 1, 2, 4, 6, 8 };
        Fx.Kaleidoscope = segments[rng.Next(segments.Length)];
        Fx.Backdrop = 0.3 + rng.NextDouble() * 0.4;
    }
}
