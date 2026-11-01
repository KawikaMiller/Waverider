using Waverider.Visualization.Custom;

namespace Waverider.Visualization;

public static class VisualizerClone
{
    /// <summary>
    /// A separate instance of the same visualizer, so something else can draw it at its own size without
    /// fighting over the original's per-instance state (feedback buffers, particles). Custom visualizers
    /// share their settings, so edits still show up live in the copy.
    /// </summary>
    public static IVisualizer Of(IVisualizer source)
    {
        try
        {
            return source switch
            {
                CustomVisualizer custom => new CustomVisualizer(custom.Settings),
                _ => (IVisualizer)Activator.CreateInstance(source.GetType())!,
            };
        }
        catch
        {
            return source; // better to share than to show nothing
        }
    }
}
