namespace Waverider.Disc;

/// <summary>How the spinning disc that replaces the album art is drawn.</summary>
public enum DiscSkin
{
    Vinyl,
    Cd,
}

/// <summary>
/// What the disc shows besides its grooves. On a CD the visualizer replaces the printed album art; on
/// vinyl the art stays on the label and the visualizer plays across the grooves.
/// </summary>
public enum DiscFace
{
    Art,
    Visualizer,
}

/// <summary>Which mini player mini mode shows.</summary>
public enum MiniPlayerKind
{
    Visualizer,
    Disc,
}
