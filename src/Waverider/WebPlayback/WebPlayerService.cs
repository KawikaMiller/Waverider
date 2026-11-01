using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Waverider.Spotify;

namespace Waverider.WebPlayback;

/// <summary>
/// Hosts Spotify's Web Playback SDK in a hidden WebView2 so this app registers as its own
/// Spotify Connect device ("Waverider") and no longer depends on the desktop client.
/// </summary>
public sealed class WebPlayerService : IDisposable
{
    private const string VirtualHost = "spotifyplayer.local";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Waverider", "webplayer.log");

    private readonly WebView2 _view;
    private readonly SpotifyAuthService _auth;
    private bool _initialized;

    public WebPlayerService(WebView2 view, SpotifyAuthService auth)
    {
        _view = view;
        _auth = auth;
    }

    /// <summary>Spotify Connect device id once the SDK reports ready, otherwise null.</summary>
    public string? DeviceId { get; private set; }
    /// <summary>null = not probed yet.</summary>
    public bool? WidevineAvailable { get; private set; }
    public string Status { get; private set; } = "Not started";

    /// <summary>PID of WebView2's browser process (the root of the player's audio), once started.</summary>
    /// <remarks>Cached on the UI thread: the WebView2 object can't be touched from the capture watchdog.</remarks>
    public uint? BrowserProcessId { get; private set; }

    public event Action<string?>? DeviceChanged;
    public event Action<string>? StatusChanged;

    /// <summary>Must run before the first WebView2 is created: the env vars are read at creation.</summary>
    public static void ConfigureEnvironment()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Waverider", "WebView2");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dataDir);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            // Playback is started from API calls, not clicks in the page, so autoplay must be allowed.
            // (The WinUI control ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, hence explicit options.)
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
            };
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Waverider", "WebView2");
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;
            BrowserProcessId = core.BrowserProcessId;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                Path.Combine(AppContext.BaseDirectory, "WebPlayer"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.Settings.AreDevToolsEnabled = false;
            core.ProcessFailed += (_, e) =>
            {
                SetStatus($"Player process failed ({e.ProcessFailedKind}); restarting.");
                DeviceId = null;
                DeviceChanged?.Invoke(null);
                core.Reload();
            };
            _watchdog = new System.Threading.Timer(_ => _view.DispatcherQueue.TryEnqueue(CheckConnection), null, 10000, 10000);
            core.Navigate($"https://{VirtualHost}/player.html");
            SetStatus("Loading web player…");
        }
        catch (Exception ex)
        {
            SetStatus($"WebView2 failed to start: {ex.Message}");
        }
    }

    /// <summary>Reload only if the SDK asked for a token while signed out (so it never got one).</summary>
    public void ReloadIfWaitingForToken()
    {
        if (_waitingForToken) { _waitingForToken = false; _view.CoreWebView2?.Reload(); }
    }

    private bool _waitingForToken;
    private System.Threading.Timer? _watchdog;
    private DateTime _offlineSince = DateTime.MaxValue;
    private int _reconnectAttempts;

    // If the device has been missing for a while (SDK dropped, laptop slept, ...), ask the SDK to
    // reconnect first and fall back to reloading the page.
    private async void CheckConnection()
    {
        // The browser process can be replaced after a crash; keep the cached PID current.
        try { BrowserProcessId = _view.CoreWebView2?.BrowserProcessId; } catch { }

        if (DeviceId is not null)
        {
            _offlineSince = DateTime.MaxValue;
            _reconnectAttempts = 0;
            return;
        }
        if (_waitingForToken || _view.CoreWebView2 is null) return;
        if (!_auth.IsAuthenticated) return;

        if (_offlineSince == DateTime.MaxValue) _offlineSince = DateTime.UtcNow;
        if (DateTime.UtcNow - _offlineSince < TimeSpan.FromSeconds(15)) return;

        _reconnectAttempts++;
        _offlineSince = DateTime.UtcNow;
        if (_reconnectAttempts == 1)
        {
            SetStatus("Built-in player offline; reconnecting…");
            SendCommand("connect");
        }
        else
        {
            SetStatus("Built-in player still offline; reloading it…");
            _view.CoreWebView2.Reload();
        }
        await Task.CompletedTask;
    }

    public void SendCommand(string name, double? value = null)
    {
        if (_view.CoreWebView2 is null) return;
        _view.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new { type = "command", name, value }));
    }

    private async void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var data = root.TryGetProperty("data", out var d) ? d : default;
            // Per-message traffic goes to the debugger only (state events fire several times a second).
            if (type != "state")
                Debug.WriteLine($"[webplayer] <- {type}");

            switch (type)
            {
                case "drm":
                    WidevineAvailable = data.TryGetProperty("widevine", out var w) && w.GetBoolean();
                    SetStatus(WidevineAvailable == true
                        ? "Widevine DRM available."
                        : "Widevine DRM NOT available in this WebView2.");
                    break;

                case "needToken":
                    var token = await _auth.GetAccessTokenAsync();
                    if (token is null)
                    {
                        _waitingForToken = true;
                        SetStatus("Sign in to Spotify to start the built-in player.");
                        break;
                    }
                    sender.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "token", token }));
                    break;

                case "ready":
                    DeviceId = data.GetProperty("device_id").GetString();
                    SetStatus("Built-in player ready.");
                    DeviceChanged?.Invoke(DeviceId);
                    break;

                case "not_ready":
                    DeviceId = null;
                    SetStatus("Built-in player went offline.");
                    DeviceChanged?.Invoke(null);
                    break;

                case "error":
                    SetStatus($"Player error ({data.GetProperty("kind").GetString()}): {data.GetProperty("message").GetString()}");
                    // An expired or rejected token needs a fresh page load to re-authenticate.
                    if (data.GetProperty("kind").GetString() == "authentication_error")
                        _view.DispatcherQueue.TryEnqueue(() => _view.CoreWebView2?.Reload());
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"message error: {ex.Message}", toFile: true);
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Log($"status: {status}", toFile: true);
        StatusChanged?.Invoke(status);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void Log(string line, bool toFile = false)
    {
        Debug.WriteLine($"[webplayer] {line}");
        if (!toFile) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* logging must never break the app */ }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        if (_view.CoreWebView2 is { } core) core.WebMessageReceived -= OnWebMessage;
    }
}
