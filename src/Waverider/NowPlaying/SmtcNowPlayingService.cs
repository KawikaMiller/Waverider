using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Waverider.Audio;
using Waverider.Spotify;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Waverider.NowPlaying;

/// <summary>
/// Now-playing info and transport controls for whatever media app Windows currently treats as "the" player
/// (the same session the system media overlay shows): Spotify, a browser tab, a desktop player. It needs no
/// login. Apps that don't publish a media session simply show as "Nothing playing".
/// </summary>
/// <remarks>
/// SMTC objects are not safe to use across apartments, so every call, including the event handlers,
/// is queued onto one dedicated MTA thread.
/// </remarks>
public sealed class SmtcNowPlayingService : INowPlayingSource, IDisposable
{
    private static readonly string ArtDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waverider", "art-cache");
    private const int MaxCachedArt = 20;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly System.Threading.Timer _tick;
    private readonly AudioCaptureService _audio;

    // Touched only on the worker thread.
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _title = "", _artist = "", _album = "";
    private string? _artUrl;

    private NowPlayingState _state = NowPlayingState.Empty;
    private bool _started;

    public event Action<NowPlayingState>? StateChanged;

    public SmtcNowPlayingService(AudioCaptureService audio)
    {
        _audio = audio;
        // Controls and track info follow the app the visualizer is listening to.
        _audio.SourceChanged += () => Post(Rebind);

        _thread = new Thread(Run) { IsBackground = true, Name = "SMTC now playing" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _tick = new System.Threading.Timer(_ => Post(Publish), null, Timeout.Infinite, Timeout.Infinite);
    }

    public NowPlayingState State => _state;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Post(Initialize);
        // The playhead moves between events, so refresh it once a second.
        _tick.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _started = false;
        _tick.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public Task<bool> PlayPauseAsync() => Command(s => s.TryTogglePlayPauseAsync());
    public Task<bool> NextAsync() => Command(s => s.TrySkipNextAsync());
    public Task<bool> PreviousAsync() => Command(s => s.TrySkipPreviousAsync());
    public Task<bool> SeekAsync(int positionMs) => Command(s => s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(positionMs).Ticks));

    public Task<bool> ToggleShuffleAsync() => Command(s =>
        s.TryChangeShuffleActiveAsync(s.GetPlaybackInfo().IsShuffleActive != true));

    public Task<bool> CycleRepeatAsync() => Command(s =>
    {
        var next = s.GetPlaybackInfo().AutoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return s.TryChangeAutoRepeatModeAsync(next);
    });

    public void Dispose()
    {
        _tick.Dispose();
        _work.CompleteAdding();
    }

    // ---- worker thread ----------------------------------------------------------------------

    private void Run()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"[SMTC] {ex.Message}"); }
        }
    }

    private void Post(Action action)
    {
        try { _work.TryAdd(action); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private Task<bool> Command(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> send)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var session = _session;
                bool ok = session is not null && send(session).AsTask().GetAwaiter().GetResult();
                done.SetResult(ok);
                Publish();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SMTC] command failed: {ex.Message}");
                done.SetResult(false);
            }
        });
        return done.Task;
    }

    private void Initialize()
    {
        if (_manager is not null) return;
        _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        _manager.CurrentSessionChanged += (_, _) => Post(Rebind);
        _manager.SessionsChanged += (_, _) => Post(Rebind);
        PruneArtCache();
        Rebind();
    }

    /// <summary>Follows the system's current media session, which changes as different apps start playing.</summary>
    private void Rebind()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnPlaybackInfoChanged;
        }

        _session = PickSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnPlaybackInfoChanged;
        }
        RefreshMetadata();
    }

    /// <summary>
    /// The session of the app the visualizer follows, or Windows' current session when it follows a whole device.
    /// A followed app with no media session gives null (nothing playing) rather than someone else's controls.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        var followed = _audio.FollowedProcessName;
        if (followed is null) return _manager.GetCurrentSession();

        var ids = AppUserModelIds.ForProcess(followed);
        var matches = _manager.GetSessions().Where(s =>
            ids.Contains(s.SourceAppUserModelId, StringComparer.OrdinalIgnoreCase) ||
            s.SourceAppUserModelId.Contains(followed, StringComparison.OrdinalIgnoreCase)).ToList();

        return matches.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? matches.FirstOrDefault();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession _, MediaPropertiesChangedEventArgs __) => Post(RefreshMetadata);
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession _, object __) => Post(Publish);

    private void RefreshMetadata()
    {
        _title = _artist = _album = "";
        _artUrl = null;

        if (_session is not null)
        {
            try
            {
                var props = _session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                _title = props.Title ?? "";
                _artist = props.Artist ?? "";
                _album = props.AlbumTitle ?? "";
                _artUrl = props.Thumbnail is null ? null : SaveArt(props.Thumbnail);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SMTC] metadata: {ex.Message}");
            }
        }
        Publish();
    }

    private void Publish()
    {
        var next = Build();
        if (next == _state) return;
        _state = next;
        StateChanged?.Invoke(next);
    }

    private NowPlayingState Build()
    {
        var session = _session;
        if (session is null || (_title.Length == 0 && _artist.Length == 0))
            return NowPlayingState.Empty;

        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            int duration = (int)Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalMilliseconds);
            // The reported position is a snapshot taken at LastUpdatedTime; add the time since while playing.
            var position = timeline.Position - timeline.StartTime;
            if (playing) position += DateTimeOffset.Now - timeline.LastUpdatedTime;
            int progress = (int)Math.Clamp(position.TotalMilliseconds, 0, duration > 0 ? duration : int.MaxValue);

            string repeat = playback.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.List => "context",
                MediaPlaybackAutoRepeatMode.Track => "track",
                _ => "off",
            };

            // No TrackId: it identifies a Spotify track, which nothing here is guaranteed to be.
            return new NowPlayingState(null, _title, _artist, _album, _artUrl, duration, progress,
                playing, playback.IsShuffleActive == true, repeat);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] state: {ex.Message}"); // the session ended between events
            return NowPlayingState.Empty;
        }
    }

    // ---- album art ---------------------------------------------------------------------------

    /// <summary>Saves the session's thumbnail under a content-hash name and returns its file URI.</summary>
    private static string? SaveArt(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = thumbnail.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            using var memory = new MemoryStream();
            stream.AsStreamForRead().CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length == 0) return null;

            Directory.CreateDirectory(ArtDir);
            var path = Path.Combine(ArtDir, Convert.ToHexString(SHA1.HashData(bytes)) + ".img");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return new Uri(path).AbsoluteUri;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] art: {ex.Message}");
            return null;
        }
    }

    private static void PruneArtCache()
    {
        try
        {
            if (!Directory.Exists(ArtDir)) return;
            foreach (var file in new DirectoryInfo(ArtDir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCachedArt))
                file.Delete();
        }
        catch { /* the cache is best-effort */ }
    }
}
