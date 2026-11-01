using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;

namespace Waverider.Visualization;

public interface IVisualizer
{
    string Name { get; }
    void Render(SKCanvas canvas, SKImageInfo info, AudioFrame frame, Theme theme, double timeSeconds);
}
