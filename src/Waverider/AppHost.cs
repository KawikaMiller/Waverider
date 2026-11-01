using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waverider.Audio;
using Waverider.NowPlaying;
using Waverider.Spotify;
using Waverider.Theming;
using Waverider.Visualization;

namespace Waverider;

public static class AppHost
{
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>The built-in Web Playback SDK player, owned by the main window.</summary>
    public static Waverider.WebPlayback.WebPlayerService? WebPlayer { get; set; }

    // For dev: paste your Spotify "Client ID" from https://developer.spotify.com/dashboard.
    // The redirect URI you must register on the dashboard is exactly: http://127.0.0.1:5544/callback
    public const string SpotifyClientId = "cd34d6efe7ab48dbb966c5a6cc3a482e";

    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton<ThemeService>();
        services.AddSingleton(_ => new FftAnalyzer(2048, 64, 0.55f));
        services.AddSingleton<AudioCaptureService>();

        services.AddSingleton(_ => new SpotifyAuthService(SpotifyClientId));
        services.AddSingleton<NowPlayingService>();
        services.AddSingleton<SmtcNowPlayingService>();
        services.AddSingleton<INowPlayingSource, NowPlayingRouter>();
        services.AddSingleton<SpotifyApiClient>();

        services.AddSingleton<VisualizerCatalog>();
        services.AddSingleton<IReadOnlyList<IVisualizer>>(sp => sp.GetRequiredService<VisualizerCatalog>().Items);

        Services = services.BuildServiceProvider();
        return Services;
    }
}
