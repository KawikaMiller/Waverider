using System.Diagnostics;
using SpotifyAPI.Web;
using Waverider.NowPlaying;

namespace Waverider.Spotify;

public sealed class NowPlayingService : INowPlayingSource, IDisposable
{
    private readonly SpotifyAuthService _auth;
    private readonly System.Threading.Timer _timer;
    private NowPlayingState _state = NowPlayingState.Empty;
    private DateTime _lastPollUtc;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    public event Action<NowPlayingState>? StateChanged;

    public NowPlayingService(SpotifyAuthService auth)
    {
        _auth = auth;
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public NowPlayingState State => _state;

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public async Task<bool> PlayPauseAsync()
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        try
        {
            if (_state.IsPlaying) await client.Player.PausePlayback();
            else await client.Player.ResumePlayback();
            await PollAsync();
            return true;
        }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    public async Task<bool> NextAsync()
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        try { await client.Player.SkipNext(); await PollAsync(); return true; }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    public async Task<bool> PreviousAsync()
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        try { await client.Player.SkipPrevious(); await PollAsync(); return true; }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    public async Task<bool> SeekAsync(int positionMs)
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        try { await client.Player.SeekTo(new PlayerSeekToRequest(positionMs)); return true; }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    public async Task<bool> ToggleShuffleAsync()
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        try { await client.Player.SetShuffle(new PlayerShuffleRequest(!_state.IsShuffle)); await PollAsync(); return true; }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    public async Task<bool> CycleRepeatAsync()
    {
        var client = await _auth.GetClientAsync();
        if (client is null) return false;
        var next = _state.RepeatState switch
        {
            "off" => PlayerSetRepeatRequest.State.Context,
            "context" => PlayerSetRepeatRequest.State.Track,
            _ => PlayerSetRepeatRequest.State.Off
        };
        try { await client.Player.SetRepeat(new PlayerSetRepeatRequest(next)); await PollAsync(); return true; }
        catch (APIException ex) { Debug.WriteLine(ex); return false; }
    }

    private async Task PollAsync()
    {
        if (DateTime.UtcNow - _lastPollUtc < TimeSpan.FromMilliseconds(500)) return;
        _lastPollUtc = DateTime.UtcNow;

        var client = await _auth.GetClientAsync();
        if (client is null)
        {
            UpdateState(NowPlayingState.Empty);
            return;
        }

        try
        {
            var current = await client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
            if (current is null || current.Item is null)
            {
                UpdateState(_state with { IsPlaying = false });
                return;
            }

            if (current.Item is FullTrack track)
            {
                var artwork = track.Album.Images.OrderByDescending(i => i.Width).FirstOrDefault()?.Url;
                var artists = string.Join(", ", track.Artists.Select(a => a.Name));
                var ns = new NowPlayingState(
                    track.Id,
                    track.Name,
                    artists,
                    track.Album.Name,
                    artwork,
                    track.DurationMs,
                    current.ProgressMs ?? 0,
                    current.IsPlaying,
                    false,
                    "off");

                // shuffle/repeat live on /me/player but we get them cheaper via context lookup later
                try
                {
                    var playback = await client.Player.GetCurrentPlayback();
                    if (playback is not null)
                        ns = ns with { IsShuffle = playback.ShuffleState, RepeatState = playback.RepeatState ?? "off" };
                }
                catch { }

                UpdateState(ns);
            }
        }
        catch (APIException ex)
        {
            Debug.WriteLine($"Spotify poll: {ex.Message}");
        }
    }

    private void UpdateState(NowPlayingState next)
    {
        if (next == _state) return;
        _state = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose() => _timer.Dispose();
}
