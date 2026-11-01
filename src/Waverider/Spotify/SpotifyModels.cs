namespace Waverider.Spotify;

public sealed record TrackInfo(
    string Uri,
    string Id,
    string Name,
    string Artists,
    string? ArtistId,
    string Album,
    string? AlbumId,
    string? ImageUrl,
    int DurationMs);

public sealed record PlaylistInfo(
    string Id,
    string Uri,
    string Name,
    string Owner,
    string? ImageUrl,
    int TrackCount,
    bool CanEdit);

public sealed record AlbumInfo(
    string Id,
    string Uri,
    string Name,
    string Artists,
    string? ArtistId,
    string? ImageUrl,
    string Year,
    int TotalTracks);

public sealed record ArtistInfo(string Id, string Uri, string Name, string? ImageUrl);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset, int RawCount)
{
    public int NextOffset => Offset + RawCount;
    public bool HasMore => RawCount > 0 && NextOffset < Total;
}

public sealed record SearchResults(
    Page<TrackInfo> Tracks,
    Page<ArtistInfo> Artists,
    Page<AlbumInfo> Albums,
    Page<PlaylistInfo> Playlists)
{
    public int Total => Math.Max(Math.Max(Tracks.Total, Artists.Total), Math.Max(Albums.Total, Playlists.Total));
}

public sealed record DeviceInfo(string Id, string Name, string Type, bool IsActive, int VolumePercent);

public sealed record QueueState(TrackInfo? Current, IReadOnlyList<TrackInfo> Upcoming);

public sealed class SpotifyApiException : Exception
{
    public int Status { get; }

    public SpotifyApiException(int status, string message) : base(message)
    {
        Status = status;
    }
}
