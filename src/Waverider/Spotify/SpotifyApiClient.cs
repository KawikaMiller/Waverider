using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Waverider.Spotify;

/// <summary>
/// Thin direct client for the Spotify Web API endpoints the library drawer needs. The
/// SpotifyAPI.Web package still targets the routes Spotify retired for dev-mode apps in
/// Feb 2026 (/me/tracks writes, /playlists/{id}/tracks), so these go through HTTP directly:
/// /me/library, /playlists/{id}/items, and 10-per-page search.
/// </summary>
public sealed class SpotifyApiClient
{
    private const int PageSize = 50;
    public const int SearchPageSize = 10;

    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.spotify.com/v1/") };

    private readonly SpotifyAuthService _auth;
    private string? _userId;

    /// <summary>Spotify Connect id of the built-in player; playback calls target it when set.</summary>
    public string? PreferredDeviceId { get; set; }

    public SpotifyApiClient(SpotifyAuthService auth)
    {
        _auth = auth;
    }

    // ---- Browsing --------------------------------------------------------------------

    public async Task<Page<PlaylistInfo>> GetMyPlaylistsAsync(int offset = 0)
    {
        var userId = await GetUserIdAsync();
        using var doc = await SendAsync(HttpMethod.Get, $"me/playlists?limit={PageSize}&offset={offset}");
        var root = doc!.RootElement;
        var items = new List<PlaylistInfo>();
        int raw = 0;
        foreach (var e in Items(root))
        {
            raw++;
            var p = ParsePlaylist(e, userId);
            if (p is not null) items.Add(p);
        }
        return new Page<PlaylistInfo>(items, Total(root), offset, raw);
    }

    public async Task<Page<TrackInfo>> GetSavedTracksAsync(int offset = 0)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"me/tracks?limit={PageSize}&offset={offset}");
        var root = doc!.RootElement;
        var items = new List<TrackInfo>();
        int raw = 0;
        foreach (var e in Items(root))
        {
            raw++;
            if (e.TryGetProperty("track", out var t) && ParseTrack(t) is { } track) items.Add(track);
        }
        return new Page<TrackInfo>(items, Total(root), offset, raw);
    }

    /// <summary>Only works for playlists the user owns or collaborates on (Spotify restriction).</summary>
    public async Task<Page<TrackInfo>> GetPlaylistTracksAsync(string playlistId, int offset = 0)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"playlists/{playlistId}/items?limit={PageSize}&offset={offset}&additional_types=track");
        var root = doc!.RootElement;
        var items = new List<TrackInfo>();
        int raw = 0;
        foreach (var e in Items(root))
        {
            raw++;
            // `item` replaced `track` in the Feb 2026 API; accept either.
            JsonElement t;
            if (!e.TryGetProperty("item", out t) || t.ValueKind != JsonValueKind.Object)
                if (!e.TryGetProperty("track", out t)) continue;
            if (ParseTrack(t) is { } track) items.Add(track);
        }
        return new Page<TrackInfo>(items, Total(root), offset, raw);
    }

    public async Task<Page<TrackInfo>> GetAlbumTracksAsync(AlbumInfo album, int offset = 0)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"albums/{album.Id}/tracks?limit={PageSize}&offset={offset}");
        var root = doc!.RootElement;
        var items = new List<TrackInfo>();
        int raw = 0;
        foreach (var e in Items(root))
        {
            raw++;
            if (ParseTrack(e, album) is { } track) items.Add(track);
        }
        return new Page<TrackInfo>(items, Total(root), offset, raw);
    }

    public async Task<Page<AlbumInfo>> GetArtistAlbumsAsync(string artistId, int offset = 0)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"artists/{artistId}/albums?include_groups=album,single&limit={PageSize}&offset={offset}");
        var root = doc!.RootElement;
        var items = new List<AlbumInfo>();
        int raw = 0;
        foreach (var e in Items(root))
        {
            raw++;
            if (ParseAlbum(e) is { } album) items.Add(album);
        }
        return new Page<AlbumInfo>(items, Total(root), offset, raw);
    }

    public async Task<AlbumInfo?> GetAlbumAsync(string albumId)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"albums/{albumId}");
        return ParseAlbum(doc!.RootElement);
    }

    public async Task<SearchResults> SearchAsync(string query, int offset = 0)
    {
        var userId = await GetUserIdAsync();
        var q = Uri.EscapeDataString(query);
        using var doc = await SendAsync(HttpMethod.Get,
            $"search?q={q}&type=track,artist,album,playlist&limit={SearchPageSize}&offset={offset}");
        var root = doc!.RootElement;

        return new SearchResults(
            Section(root, "tracks", offset, e => ParseTrack(e)),
            Section(root, "artists", offset, ParseArtist),
            Section(root, "albums", offset, ParseAlbum),
            Section(root, "playlists", offset, e => ParsePlaylist(e, userId)));
    }

    public async Task<QueueState> GetQueueAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "me/player/queue");
        var root = doc!.RootElement;
        TrackInfo? current = null;
        if (root.TryGetProperty("currently_playing", out var cp) && cp.ValueKind == JsonValueKind.Object)
            current = ParseTrack(cp);
        var upcoming = new List<TrackInfo>();
        if (root.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in q.EnumerateArray())
                if (ParseTrack(e) is { } t) upcoming.Add(t);
        }
        return new QueueState(current, upcoming);
    }

    // ---- Actions ---------------------------------------------------------------------

    public async Task PlayContextAsync(string contextUri, string? startTrackUri = null)
    {
        object body = startTrackUri is null
            ? new { context_uri = contextUri }
            : new { context_uri = contextUri, offset = new { uri = startTrackUri } };
        (await SendAsync(HttpMethod.Put, "me/player/play" + DeviceQuery('?'), body))?.Dispose();
    }

    public async Task PlayTracksAsync(IEnumerable<string> uris)
    {
        var list = uris.Take(100).ToArray();
        (await SendAsync(HttpMethod.Put, "me/player/play" + DeviceQuery('?'), new { uris = list }))?.Dispose();
    }

    public async Task AddToQueueAsync(string trackUri)
        => (await SendAsync(HttpMethod.Post, $"me/player/queue?uri={Uri.EscapeDataString(trackUri)}" + DeviceQuery('&')))?.Dispose();

    /// <summary>Makes the built-in player the active device if nothing else is currently active.</summary>
    public async Task ActivateIfIdleAsync(string deviceId)
    {
        using var state = await SendAsync(HttpMethod.Get, "me/player");
        if (state is not null &&
            state.RootElement.TryGetProperty("device", out var device) &&
            device.ValueKind == JsonValueKind.Object &&
            Str(device, "id") != deviceId &&
            state.RootElement.TryGetProperty("is_playing", out var playing) && playing.ValueKind == JsonValueKind.True)
        {
            return; // another device is playing right now; leave it alone
        }
        await TransferPlaybackAsync(deviceId, play: false);
    }

    public async Task TransferPlaybackAsync(string deviceId, bool play)
        => (await SendAsync(HttpMethod.Put, "me/player", new { device_ids = new[] { deviceId }, play }))?.Dispose();

    private string DeviceQuery(char separator)
        => PreferredDeviceId is null ? "" : $"{separator}device_id={Uri.EscapeDataString(PreferredDeviceId)}";

    public async Task SaveAsync(string uri)
        => (await SendAsync(HttpMethod.Put, $"me/library?uris={Uri.EscapeDataString(uri)}"))?.Dispose();

    public async Task RemoveAsync(string uri)
        => (await SendAsync(HttpMethod.Delete, $"me/library?uris={Uri.EscapeDataString(uri)}"))?.Dispose();

    public async Task<bool[]> AreSavedAsync(IReadOnlyList<string> uris)
    {
        var joined = string.Join(",", uris.Select(Uri.EscapeDataString));
        using var doc = await SendAsync(HttpMethod.Get, $"me/library/contains?uris={joined}");
        return doc!.RootElement.EnumerateArray().Select(e => e.GetBoolean()).ToArray();
    }

    public async Task AddToPlaylistAsync(string playlistId, string trackUri)
        => (await SendAsync(HttpMethod.Post, $"playlists/{playlistId}/items", new { uris = new[] { trackUri } }))?.Dispose();

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "me/player/devices");
        var list = new List<DeviceInfo>();
        foreach (var d in doc!.RootElement.GetProperty("devices").EnumerateArray())
        {
            var id = Str(d, "id");
            if (id is null) continue; // restricted devices have no id and can't be targeted
            list.Add(new DeviceInfo(
                id,
                Str(d, "name") ?? "Unknown",
                Str(d, "type") ?? "",
                d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
                d.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1));
        }
        return list;
    }

    // ---- Plumbing --------------------------------------------------------------------

    private async Task<string> GetUserIdAsync()
    {
        if (_userId is not null) return _userId;
        using var doc = await SendAsync(HttpMethod.Get, "me");
        _userId = doc!.RootElement.GetProperty("id").GetString() ?? "";
        return _userId;
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body = null, bool retry = true)
    {
        var token = await _auth.GetAccessTokenAsync()
            ?? throw new SpotifyApiException(401, "Sign in to Spotify first.");

        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);

        using var res = await Http.SendAsync(req);

        if (res.StatusCode == HttpStatusCode.TooManyRequests && retry)
        {
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait);
            return await SendAsync(method, path, body, retry: false);
        }

        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new SpotifyApiException((int)res.StatusCode, FriendlyError(res.StatusCode, text));

        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private static string FriendlyError(HttpStatusCode status, string body)
    {
        string? message = null, reason = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("message", out var m)) message = m.GetString();
                    if (err.TryGetProperty("reason", out var r)) reason = r.GetString();
                }
                else message = err.GetString();
            }
        }
        catch { /* non-JSON error body */ }

        if (reason == "NO_ACTIVE_DEVICE")
            return "No active Spotify device. Start playback in the Spotify app first.";
        if (reason == "PREMIUM_REQUIRED")
            return "Spotify Premium is required for playback control.";
        if (status == HttpStatusCode.Forbidden && string.IsNullOrEmpty(message))
            return "Spotify refused this request (403). Playlist contents are only available for playlists you own or collaborate on.";
        return message ?? $"Spotify returned {(int)status}.";
    }

    // ---- Parsing ---------------------------------------------------------------------

    private static IEnumerable<JsonElement> Items(JsonElement paging)
    {
        if (paging.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in items.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
    }

    private static int Total(JsonElement paging)
        => paging.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;

    private static Page<T> Section<T>(JsonElement root, string name, int offset, Func<JsonElement, T?> parse) where T : class
    {
        var items = new List<T>();
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object)
            return new Page<T>(items, 0, offset, 0);

        int raw = 0;
        if (section.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                raw++; // search results can contain null entries; they still count toward the offset
                if (e.ValueKind == JsonValueKind.Object && parse(e) is { } parsed) items.Add(parsed);
            }
        }
        return new Page<T>(items, Total(section), offset, raw);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? PickImage(JsonElement e)
    {
        if (!e.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        int bestWidth = int.MaxValue;
        foreach (var img in images.EnumerateArray())
        {
            var url = Str(img, "url");
            if (url is null) continue;
            int w = img.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : 0;
            // Smallest image that's still sharp at a 44px thumbnail (~2x).
            if (w >= 80 && w < bestWidth) { best = url; bestWidth = w; }
            else if (best is null) best = url;
        }
        return best;
    }

    private static TrackInfo? ParseTrack(JsonElement e, AlbumInfo? albumOverride = null)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Str(e, "type") is { } type && type != "track") return null;
        if (e.TryGetProperty("is_local", out var local) && local.ValueKind == JsonValueKind.True) return null;

        var id = Str(e, "id");
        var uri = Str(e, "uri");
        var name = Str(e, "name");
        if (id is null || uri is null || name is null) return null;

        string artists = "";
        string? artistId = null;
        if (e.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in arr.EnumerateArray())
            {
                if (Str(a, "name") is { } n) names.Add(n);
                artistId ??= Str(a, "id");
            }
            artists = string.Join(", ", names);
        }

        string album = albumOverride?.Name ?? "";
        string? albumId = albumOverride?.Id;
        string? image = albumOverride?.ImageUrl;
        if (albumOverride is null && e.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
        {
            album = Str(alb, "name") ?? "";
            albumId = Str(alb, "id");
            image = PickImage(alb);
        }

        int duration = e.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
        return new TrackInfo(uri, id, name, artists, artistId, album, albumId, image, duration);
    }

    private static AlbumInfo? ParseAlbum(JsonElement e)
    {
        var id = Str(e, "id");
        var uri = Str(e, "uri");
        var name = Str(e, "name");
        if (id is null || uri is null || name is null) return null;

        var names = new List<string>();
        string? artistId = null;
        if (e.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (Str(a, "name") is { } n) names.Add(n);
                artistId ??= Str(a, "id");
            }
        }

        var date = Str(e, "release_date") ?? "";
        var year = date.Length >= 4 ? date[..4] : date;
        int total = e.TryGetProperty("total_tracks", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        return new AlbumInfo(id, uri, name, string.Join(", ", names), artistId, PickImage(e), year, total);
    }

    private static ArtistInfo? ParseArtist(JsonElement e)
    {
        var id = Str(e, "id");
        var uri = Str(e, "uri");
        var name = Str(e, "name");
        return id is null || uri is null || name is null ? null : new ArtistInfo(id, uri, name, PickImage(e));
    }

    private static PlaylistInfo? ParsePlaylist(JsonElement e, string userId)
    {
        var id = Str(e, "id");
        var uri = Str(e, "uri");
        var name = Str(e, "name");
        if (id is null || uri is null || name is null) return null;

        string owner = "";
        string? ownerId = null;
        if (e.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object)
        {
            owner = Str(o, "display_name") ?? Str(o, "id") ?? "";
            ownerId = Str(o, "id");
        }

        // The track-count object was renamed tracks -> items in the Feb 2026 API.
        int count = 0;
        foreach (var key in new[] { "items", "tracks" })
        {
            if (e.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Object &&
                c.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number)
            {
                count = tot.GetInt32();
                break;
            }
        }

        bool collaborative = e.TryGetProperty("collaborative", out var col) && col.ValueKind == JsonValueKind.True;
        bool canEdit = collaborative || (ownerId is not null && ownerId == userId);
        return new PlaylistInfo(id, uri, name, owner, PickImage(e), count, canEdit);
    }
}
