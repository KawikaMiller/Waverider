using Waverider.Spotify;

namespace Waverider.NowPlaying;

/// <summary>
/// Chooses between the Spotify account (when signed in) and the Windows media session. Spotify wins while it is
/// playing, since it knows more (shuffle, repeat, likes); otherwise whichever source is playing wins, and when
/// neither is, the last active one stays. Transport commands go to the active source.
/// </summary>
public sealed class NowPlayingRouter : INowPlayingSource
{
    private readonly NowPlayingService _spotify;
    private readonly SmtcNowPlayingService _smtc;
    private readonly SpotifyAuthService _auth;
    private readonly object _lock = new();
    private INowPlayingSource _active;
    private NowPlayingState _state;

    public event Action<NowPlayingState>? StateChanged;

    public NowPlayingRouter(NowPlayingService spotify, SmtcNowPlayingService smtc, SpotifyAuthService auth)
    {
        _spotify = spotify;
        _smtc = smtc;
        _auth = auth;
        _active = auth.IsAuthenticated ? spotify : smtc;
        _state = _active.State;

        spotify.StateChanged += _ => Reevaluate();
        smtc.StateChanged += _ => Reevaluate();
        auth.AuthStateChanged += _ => Reevaluate();
    }

    public NowPlayingState State => _state;

    public void Start()
    {
        _spotify.Start();
        _smtc.Start();
    }

    public void Stop()
    {
        _spotify.Stop();
        _smtc.Stop();
    }

    public Task<bool> PlayPauseAsync() => _active.PlayPauseAsync();
    public Task<bool> NextAsync() => _active.NextAsync();
    public Task<bool> PreviousAsync() => _active.PreviousAsync();
    public Task<bool> SeekAsync(int positionMs) => _active.SeekAsync(positionMs);
    public Task<bool> ToggleShuffleAsync() => _active.ToggleShuffleAsync();
    public Task<bool> CycleRepeatAsync() => _active.CycleRepeatAsync();

    private void Reevaluate()
    {
        NowPlayingState state;
        lock (_lock)
        {
            if (!_auth.IsAuthenticated) _active = _smtc;
            else if (_spotify.State.IsPlaying) _active = _spotify;
            else if (_smtc.State.IsPlaying) _active = _smtc;

            state = _active.State;
            if (state == _state) return;
            _state = state;
        }
        StateChanged?.Invoke(state);
    }
}
