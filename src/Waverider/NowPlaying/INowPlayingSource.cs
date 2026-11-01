using Waverider.Spotify;

namespace Waverider.NowPlaying;

/// <summary>Something that reports the current track and accepts transport commands.</summary>
public interface INowPlayingSource
{
    NowPlayingState State { get; }
    event Action<NowPlayingState>? StateChanged;

    void Start();
    void Stop();

    Task<bool> PlayPauseAsync();
    Task<bool> NextAsync();
    Task<bool> PreviousAsync();
    Task<bool> SeekAsync(int positionMs);
    Task<bool> ToggleShuffleAsync();
    Task<bool> CycleRepeatAsync();
}
