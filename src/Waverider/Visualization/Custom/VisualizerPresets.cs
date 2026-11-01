namespace Waverider.Visualization.Custom;

/// <summary>
/// Built-in "Windows Media Player" style looks, expressed as ordinary custom-visualizer settings.
/// Selecting one and pressing Customize in the editor clones it into an editable copy.
/// </summary>
public static class VisualizerPresets
{
    public static IEnumerable<CustomVisualizer> All()
    {
        yield return Build(BarsAndWaves());
        yield return Build(Ambience());
        yield return Build(Battery());
        yield return Build(Alchemy());
    }

    private static CustomVisualizer Build(CustomVisualizerSettings s)
    {
        s.IsBuiltIn = true;
        foreach (var layer in s.Layers)
            foreach (var def in ParamDefs.For(layer.Kind)) layer.Ensure(def);
        foreach (var def in ParamDefs.Fx) s.Fx.Ensure(def);
        return new CustomVisualizer(s);
    }

    // Glowing spectrum columns with a ribbon of sine-ish waveform over them.
    private static CustomVisualizerSettings BarsAndWaves()
    {
        var s = new CustomVisualizerSettings { Name = "WMP · Bars and Waves" };
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Bars, ColorMode = ColorMode.Theme, Additive = false }
            .Set(ParamDefs.Count, 40).Set(ParamDefs.Size, 0.5).Set(ParamDefs.Thickness, 10)
            .Set(ParamDefs.Amplitude, 1.2).Set(ParamDefs.Glow, 0.5).Set(ParamDefs.Opacity, 0.85));
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Wave, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Size, 0.3).Set(ParamDefs.Thickness, 3)
            .Set(ParamDefs.Amplitude, 1.1, AudioSource.Bass, 0.2).Set(ParamDefs.Glow, 0.7));
        s.Fx.Set(ParamDefs.Trails, 0.55).Set(ParamDefs.HueCycle, 20);
        s.Fx.Backdrop = 0.4;
        return s;
    }

    // Slow, soft clouds of color that breathe with the bass.
    private static CustomVisualizerSettings Ambience()
    {
        var s = new CustomVisualizerSettings { Name = "WMP · Ambience" };
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Blobs, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Count, 7).Set(ParamDefs.Size, 0.32, AudioSource.Bass, 0.15)
            .Set(ParamDefs.Amplitude, 0.8).Set(ParamDefs.Spin, 6).Set(ParamDefs.Opacity, 0.55).Set(ParamDefs.Glow, 0.3));
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Blobs, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Count, 4).Set(ParamDefs.Size, 0.18).Set(ParamDefs.Hue, 160)
            .Set(ParamDefs.Amplitude, 1.2).Set(ParamDefs.Spin, -9).Set(ParamDefs.Opacity, 0.5).Set(ParamDefs.Glow, 0));
        s.Fx.Set(ParamDefs.Trails, 0.9).Set(ParamDefs.Zoom, 0.02, AudioSource.Bass, 0.08).Set(ParamDefs.HueCycle, 14);
        s.Fx.Backdrop = 0.55;
        return s;
    }

    // Spokes that flare out on every hit, throwing sparks.
    private static CustomVisualizerSettings Battery()
    {
        var s = new CustomVisualizerSettings { Name = "WMP · Battery" };
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Radial, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Count, 72).Set(ParamDefs.Size, 0.14, AudioSource.Beat, 0.1)
            .Set(ParamDefs.Thickness, 3).Set(ParamDefs.Amplitude, 1.1).Set(ParamDefs.Glow, 0.8).Set(ParamDefs.Spin, 14));
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Particles, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Count, 48).Set(ParamDefs.Size, 0.08).Set(ParamDefs.Thickness, 3)
            .Set(ParamDefs.Amplitude, 0.9).Set(ParamDefs.Glow, 0.6));
        s.Fx.Set(ParamDefs.Trails, 0.84).Set(ParamDefs.Zoom, 0.04, AudioSource.Beat, 0.25)
            .Set(ParamDefs.Rotate, 8).Set(ParamDefs.HueCycle, 30);
        s.Fx.Backdrop = 0.6;
        return s;
    }

    // Feedback swirl in the spirit of Geiss / Milkdrop: a ringed scope fed back through a zoom,
    // a twist and a six-way mirror.
    private static CustomVisualizerSettings Alchemy()
    {
        var s = new CustomVisualizerSettings { Name = "WMP · Alchemy" };
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Scope, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Size, 0.2, AudioSource.Bass, 0.12).Set(ParamDefs.Thickness, 3)
            .Set(ParamDefs.Amplitude, 1.3).Set(ParamDefs.Glow, 0.7).Set(ParamDefs.Spin, 25));
        s.Layers.Add(new LayerSettings { Kind = GeneratorKind.Rings, ColorMode = ColorMode.Rainbow }
            .Set(ParamDefs.Count, 5).Set(ParamDefs.Size, 0.35).Set(ParamDefs.Thickness, 2)
            .Set(ParamDefs.Amplitude, 1.0).Set(ParamDefs.Glow, 0.4).Set(ParamDefs.Opacity, 0.7));
        s.Fx.Set(ParamDefs.Trails, 0.93).Set(ParamDefs.Zoom, 0.12, AudioSource.Beat, 0.2)
            .Set(ParamDefs.Rotate, 22).Set(ParamDefs.HueCycle, 40);
        s.Fx.Kaleidoscope = 6;
        s.Fx.Backdrop = 0.7;
        return s;
    }
}
