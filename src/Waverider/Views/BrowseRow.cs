using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Waverider.Spotify;

namespace Waverider.Views;

public enum RowKind { Track, Playlist, Album, Artist, Header, PlayAll, More }

/// <summary>One line in the library drawer's list: a track, a collection, a section header, or "load more".</summary>
public sealed class BrowseRow
{
    private BrowseRow(RowKind kind, string title, string subtitle, string? imageUrl, object? data, string glyph)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Data = data;
        Glyph = glyph;
        if (!string.IsNullOrEmpty(imageUrl) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            Thumb = new BitmapImage(uri) { DecodePixelWidth = 88 };
    }

    public RowKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public object? Data { get; }
    public string Glyph { get; }
    public ImageSource? Thumb { get; }

    public TrackInfo? Track => Data as TrackInfo;

    public Visibility ThumbVisibility => Kind is RowKind.Header or RowKind.More or RowKind.PlayAll ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MoreVisibility => Kind == RowKind.Track ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    public Windows.UI.Text.FontWeight TitleWeight =>
        Kind is RowKind.Header or RowKind.PlayAll ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    public static BrowseRow From(TrackInfo t)
        => new(RowKind.Track, t.Name, string.IsNullOrEmpty(t.Album) ? t.Artists : $"{t.Artists} · {t.Album}",
            t.ImageUrl, t, "");

    public static BrowseRow From(PlaylistInfo p)
        => new(RowKind.Playlist, p.Name, $"{p.TrackCount} tracks · {p.Owner}", p.ImageUrl, p, "");

    public static BrowseRow From(AlbumInfo a)
        => new(RowKind.Album, a.Name, string.IsNullOrEmpty(a.Year) ? a.Artists : $"{a.Artists} · {a.Year}",
            a.ImageUrl, a, "");

    public static BrowseRow From(ArtistInfo a)
        => new(RowKind.Artist, a.Name, "Artist", a.ImageUrl, a, "");

    public static BrowseRow Header(string title) => new(RowKind.Header, title, "", null, null, "");
    public static BrowseRow PlayAll(string title) => new(RowKind.PlayAll, title, "", null, null, "");
    public static BrowseRow LoadMore() => new(RowKind.More, "Load more…", "", null, null, "");
}
