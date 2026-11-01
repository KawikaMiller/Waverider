using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Waverider.Spotify;

namespace Waverider.Views;

/// <summary>
/// Right-hand drawer for browsing Spotify: search, liked songs, playlists and the queue, with
/// drill-down into playlists, albums and artists. Playback is remote-controlled through the
/// Spotify client; this panel only issues Web API calls.
/// </summary>
public sealed partial class LibraryPanel : UserControl
{
    private sealed record PageData(List<BrowseRow> Rows, int NextOffset, bool HasMore);

    private sealed class BrowsePage
    {
        public required string Title { get; init; }
        public required Func<int, Task<PageData>> Load { get; init; }
        /// <summary>Playlist/album/artist URI: clicking a track plays within this context.</summary>
        public string? ContextUri { get; init; }
        /// <summary>Search root: nothing loads until the user submits a query.</summary>
        public bool ManualLoad { get; init; }
        public ObservableCollection<BrowseRow> Rows { get; } = new();
        public int NextOffset { get; set; }
        public bool Loaded { get; set; }
    }

    private readonly Stack<BrowsePage> _stack = new();
    private BrowsePage? _root;
    private string _query = "";
    private string _tabTag = "";
    private List<PlaylistInfo>? _editablePlaylists;
    private int _statusVersion;

    public LibraryPanel()
    {
        InitializeComponent();
        Tabs.SelectedItem = Tabs.Items[2]; // Playlists
    }

    public SpotifyApiClient? Api { get; set; }

    /// <summary>The drawer shows a close button; the inline Browse layout does not.</summary>
    public bool ShowClose
    {
        set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action? CloseRequested;
    public event Action? SignInRequested;

    private BrowsePage? Current => _stack.Count > 0 ? _stack.Peek() : _root;

    /// <summary>Called by the host when sign-in state changes (and when the drawer is first shown).</summary>
    public void SetAuthenticated(bool signedIn)
    {
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn)
        {
            SearchBox.Visibility = Visibility.Collapsed;
            _root = null;
            _stack.Clear();
            _editablePlaylists = null;
            return;
        }

        if (_root is null) ShowTab(SelectedTag());
    }

    private string SelectedTag() => (Tabs.SelectedItem as SelectorBarItem)?.Tag as string ?? "playlists";

    // ---- Navigation ------------------------------------------------------------------

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Api is null || SignedOutPanel.Visibility == Visibility.Visible) return;
        ShowTab(SelectedTag());
    }

    private void ShowTab(string tag)
    {
        if (Api is null) return;
        var api = Api;
        _tabTag = tag;
        _stack.Clear();
        SetStatus("");

        _root = tag switch
        {
            "search" => new BrowsePage
            {
                Title = "Search",
                ManualLoad = true,
                Load = off => SearchAsync(api, off)
            },
            "liked" => new BrowsePage
            {
                Title = "Liked Songs",
                Load = async off =>
                {
                    var page = await api.GetSavedTracksAsync(off);
                    return new PageData(page.Items.Select(BrowseRow.From).ToList(), page.NextOffset, page.HasMore);
                }
            },
            "queue" => new BrowsePage { Title = "Queue", Load = _ => QueueAsync(api) },
            _ => new BrowsePage
            {
                Title = "Playlists",
                Load = async off =>
                {
                    var page = await api.GetMyPlaylistsAsync(off);
                    return new PageData(page.Items.Select(BrowseRow.From).ToList(), page.NextOffset, page.HasMore);
                }
            }
        };
        ShowPage(_root);
    }

    private void ShowPage(BrowsePage? page)
    {
        if (page is null) return;
        TitleText.Text = page.Title;
        BackButton.Visibility = _stack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = _stack.Count == 0 && _tabTag == "search" ? Visibility.Visible : Visibility.Collapsed;
        List.ItemsSource = page.Rows;
        if (!page.Loaded && !page.ManualLoad) _ = LoadPageAsync(page, append: false);
    }

    private void Push(BrowsePage page)
    {
        _stack.Push(page);
        SetStatus("");
        ShowPage(page);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_stack.Count == 0) return;
        _stack.Pop();
        SetStatus("");
        ShowPage(Current);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnSignInClick(object sender, RoutedEventArgs e) => SignInRequested?.Invoke();

    // ---- Loading ---------------------------------------------------------------------

    private async Task LoadPageAsync(BrowsePage page, bool append)
    {
        bool showSpinner = page.Rows.Count == 0 || !append;
        if (showSpinner) SetBusy(true);
        try
        {
            var data = await page.Load(append ? page.NextOffset : 0);
            if (!ReferenceEquals(page, Current)) return;

            if (!append) page.Rows.Clear();
            if (page.Rows.Count > 0 && page.Rows[^1].Kind == RowKind.More) page.Rows.RemoveAt(page.Rows.Count - 1);

            if (!append && page.ContextUri is not null &&
                (data.Rows.Any(r => r.Kind == RowKind.Track) || page.ContextUri.Contains(":artist:")))
                page.Rows.Add(BrowseRow.PlayAll("▶  Play"));
            foreach (var row in data.Rows) page.Rows.Add(row);

            page.NextOffset = data.NextOffset;
            if (data.HasMore) page.Rows.Add(BrowseRow.LoadMore());
            page.Loaded = true;

            if (page.Rows.Count == 0) SetStatus("Nothing here yet.");
        }
        catch (SpotifyApiException ex)
        {
            if (ReferenceEquals(page, Current)) SetStatus(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine(ex);
            if (ReferenceEquals(page, Current)) SetStatus("Couldn't reach Spotify. Check your connection.");
        }
        finally
        {
            if (ReferenceEquals(page, Current)) SetBusy(false);
        }
    }

    private async Task<PageData> SearchAsync(SpotifyApiClient api, int offset)
    {
        var results = await api.SearchAsync(_query, offset);
        var rows = new List<BrowseRow>();
        bool headers = offset == 0;

        void Section<T>(string title, Page<T> page, Func<T, BrowseRow> map)
        {
            if (page.Items.Count == 0) return;
            if (headers) rows.Add(BrowseRow.Header(title));
            rows.AddRange(page.Items.Select(map));
        }

        Section("Songs", results.Tracks, BrowseRow.From);
        Section("Artists", results.Artists, BrowseRow.From);
        Section("Albums", results.Albums, BrowseRow.From);
        Section("Playlists", results.Playlists, BrowseRow.From);

        int raw = new[] { results.Tracks.RawCount, results.Artists.RawCount, results.Albums.RawCount, results.Playlists.RawCount }.Max();
        bool more = results.Tracks.HasMore || results.Artists.HasMore || results.Albums.HasMore || results.Playlists.HasMore;
        return new PageData(rows, offset + raw, more);
    }

    private static async Task<PageData> QueueAsync(SpotifyApiClient api)
    {
        var queue = await api.GetQueueAsync();
        var rows = new List<BrowseRow>();
        if (queue.Current is not null)
        {
            rows.Add(BrowseRow.Header("Now playing"));
            rows.Add(BrowseRow.From(queue.Current));
        }
        if (queue.Upcoming.Count > 0)
        {
            rows.Add(BrowseRow.Header("Next up"));
            rows.AddRange(queue.Upcoming.Select(BrowseRow.From));
        }
        return new PageData(rows, 0, false);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _root is null || _tabTag != "search") return;
        var text = SearchBox.Text.Trim();
        if (text.Length == 0) return;

        _query = text;
        var page = _root;
        page.Rows.Clear();
        page.Loaded = false;
        page.NextOffset = 0;
        SetStatus("");
        _ = LoadPageAsync(page, append: false);
        e.Handled = true;
    }

    // ---- Row interaction -------------------------------------------------------------

    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BrowseRow row || Api is null || Current is not { } page) return;

        switch (row.Kind)
        {
            case RowKind.More:
                _ = LoadPageAsync(page, append: true);
                break;
            case RowKind.PlayAll:
                if (page.ContextUri is not null)
                    _ = RunAsync(() => Api.PlayContextAsync(page.ContextUri), "Playing.");
                break;
            case RowKind.Track:
                PlayTrack(row, page);
                break;
            case RowKind.Playlist when row.Data is PlaylistInfo p:
                OpenPlaylist(p);
                break;
            case RowKind.Album when row.Data is AlbumInfo a:
                OpenAlbum(a);
                break;
            case RowKind.Artist when row.Data is ArtistInfo ar:
                OpenArtist(ar);
                break;
        }
    }

    private void OpenPlaylist(PlaylistInfo p)
    {
        var api = Api!;
        Push(new BrowsePage
        {
            Title = p.Name,
            ContextUri = p.Uri,
            Load = async off =>
            {
                var page = await api.GetPlaylistTracksAsync(p.Id, off);
                return new PageData(page.Items.Select(BrowseRow.From).ToList(), page.NextOffset, page.HasMore);
            }
        });
    }

    private void OpenAlbum(AlbumInfo a)
    {
        var api = Api!;
        Push(new BrowsePage
        {
            Title = a.Name,
            ContextUri = a.Uri,
            Load = async off =>
            {
                var page = await api.GetAlbumTracksAsync(a, off);
                return new PageData(page.Items.Select(BrowseRow.From).ToList(), page.NextOffset, page.HasMore);
            }
        });
    }

    private void OpenArtist(ArtistInfo artist)
    {
        var api = Api!;
        Push(new BrowsePage
        {
            Title = artist.Name,
            ContextUri = artist.Uri,
            Load = async off =>
            {
                var page = await api.GetArtistAlbumsAsync(artist.Id, off);
                return new PageData(page.Items.Select(BrowseRow.From).ToList(), page.NextOffset, page.HasMore);
            }
        });
    }

    private void PlayTrack(BrowseRow row, BrowsePage page)
    {
        var api = Api;
        if (api is null || row.Track is not { } track) return;

        // Inside a playlist/album, play within that context so next/previous behave naturally.
        var context = page.ContextUri;
        if (context is not null && (context.Contains(":album:") || context.Contains(":playlist:")))
        {
            _ = RunAsync(() => api.PlayContextAsync(context, track.Uri), $"Playing {track.Name}.");
            return;
        }

        // Liked songs / search / queue have no context: play this track and the ones after it.
        var uris = page.Rows.Where(r => r.Kind == RowKind.Track && r.Track is not null)
            .Select(r => r.Track!.Uri)
            .SkipWhile(u => u != track.Uri)
            .ToList();
        if (uris.Count == 0) uris.Add(track.Uri);
        _ = RunAsync(() => api.PlayTracksAsync(uris), $"Playing {track.Name}.");
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowseRow { Kind: RowKind.Track } row } fe)
        {
            _ = ShowTrackMenuAsync(fe, row);
            e.Handled = true;
        }
    }

    private void OnRowMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowseRow { Kind: RowKind.Track } row } fe)
            _ = ShowTrackMenuAsync(fe, row);
    }

    private async Task ShowTrackMenuAsync(FrameworkElement anchor, BrowseRow row)
    {
        var api = Api;
        if (api is null || row.Track is not { } track) return;

        bool saved = false;
        try { saved = (await api.AreSavedAsync(new[] { track.Uri }))[0]; }
        catch { /* menu still works without the liked state */ }

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", () => RunAsync(() => api.PlayTracksAsync(new[] { track.Uri }), $"Playing {track.Name}.")));
        menu.Items.Add(Item("Add to queue", () => RunAsync(() => api.AddToQueueAsync(track.Uri), "Added to queue.")));
        menu.Items.Add(saved
            ? Item("Remove from Liked Songs", () => RunAsync(() => api.RemoveAsync(track.Uri), "Removed from Liked Songs."))
            : Item("Save to Liked Songs", () => RunAsync(() => api.SaveAsync(track.Uri), "Saved to Liked Songs.")));

        var sub = new MenuFlyoutSubItem { Text = "Add to playlist" };
        var playlists = await GetEditablePlaylistsAsync();
        if (playlists.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "No editable playlists", IsEnabled = false });
        }
        else
        {
            foreach (var p in playlists)
            {
                var target = p;
                sub.Items.Add(Item(target.Name, () => RunAsync(
                    () => api.AddToPlaylistAsync(target.Id, track.Uri), $"Added to {target.Name}.")));
            }
        }
        menu.Items.Add(sub);
        menu.Items.Add(new MenuFlyoutSeparator());

        if (track.AlbumId is not null)
        {
            menu.Items.Add(Item("Go to album", async () =>
            {
                try
                {
                    var album = await api.GetAlbumAsync(track.AlbumId);
                    if (album is not null) OpenAlbum(album);
                }
                catch (SpotifyApiException ex) { SetStatus(ex.Message); }
            }));
        }
        if (track.ArtistId is not null)
        {
            menu.Items.Add(Item("Go to artist", () =>
            {
                var name = track.Artists.Split(',')[0].Trim();
                OpenArtist(new ArtistInfo(track.ArtistId, $"spotify:artist:{track.ArtistId}", name, null));
                return Task.CompletedTask;
            }));
        }

        menu.ShowAt(anchor);
    }

    private static MenuFlyoutItem Item(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) => await action();
        return item;
    }

    private async Task<List<PlaylistInfo>> GetEditablePlaylistsAsync()
    {
        if (_editablePlaylists is not null || Api is null) return _editablePlaylists ?? new();
        var all = new List<PlaylistInfo>();
        try
        {
            int offset = 0;
            for (int i = 0; i < 6; i++) // up to 300 playlists
            {
                var page = await Api.GetMyPlaylistsAsync(offset);
                all.AddRange(page.Items.Where(p => p.CanEdit));
                if (!page.HasMore) break;
                offset = page.NextOffset;
            }
            _editablePlaylists = all;
        }
        catch (SpotifyApiException ex)
        {
            SetStatus(ex.Message);
        }
        return all;
    }

    // ---- Helpers ---------------------------------------------------------------------

    private async Task RunAsync(Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            SetStatus(successMessage);
        }
        catch (SpotifyApiException ex)
        {
            SetStatus(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine(ex);
            SetStatus("Couldn't reach Spotify. Check your connection.");
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetStatus(string text)
    {
        int version = ++_statusVersion;
        StatusText.Text = text;
        if (text.Length == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (version == _statusVersion) StatusText.Text = "";
    }
}
