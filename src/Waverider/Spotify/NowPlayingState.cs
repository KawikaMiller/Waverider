namespace Waverider.Spotify;

public sealed record NowPlayingState(
    string? TrackId,
    string Title,
    string Artist,
    string Album,
    string? AlbumArtUrl,
    int DurationMs,
    int ProgressMs,
    bool IsPlaying,
    bool IsShuffle,
    string RepeatState)
{
    public static readonly NowPlayingState Empty = new(null, "Nothing playing", "", "", null, 0, 0, false, false, "off");
}
