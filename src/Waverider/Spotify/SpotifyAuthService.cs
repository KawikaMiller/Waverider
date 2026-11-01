using System.Diagnostics;
using System.Text.Json;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace Waverider.Spotify;

public sealed class SpotifyAuthService
{
    private const int CallbackPort = 5544;
    private static readonly Uri Callback = new($"http://127.0.0.1:{CallbackPort}/callback");
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Waverider", "token.json");

    // Everything the library drawer needs. A saved token missing any of these is discarded so the
    // user is prompted to sign in again with the wider grant.
    private static readonly string[] RequiredScopes =
    {
        Scopes.UserReadCurrentlyPlaying,
        Scopes.UserReadPlaybackState,
        Scopes.UserModifyPlaybackState,
        Scopes.UserReadPrivate,
        Scopes.UserLibraryRead,
        Scopes.UserLibraryModify,
        Scopes.UserFollowRead,
        Scopes.UserFollowModify,
        Scopes.PlaylistReadPrivate,
        Scopes.PlaylistReadCollaborative,
        Scopes.PlaylistModifyPublic,
        Scopes.PlaylistModifyPrivate,
        Scopes.Streaming,
        Scopes.UserReadEmail
    };

    private readonly string _clientId;
    private PKCETokenResponse? _token;

    // Set once the saved token turns out to be unusable (expired and not refreshable). Without it the
    // next call would re-read the same dead file, announce "signed in", fail to refresh, announce
    // "signed out", and so on forever, which pins the UI thread.
    private bool _savedTokenRejected;

    public event Action<bool>? AuthStateChanged;

    public SpotifyAuthService(string clientId)
    {
        _clientId = clientId;
    }

    public bool IsAuthenticated => _token is not null && !string.IsNullOrEmpty(_token.AccessToken);

    public async Task<SpotifyClient?> GetClientAsync()
    {
        if (_token is null && !_savedTokenRejected && File.Exists(TokenPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(TokenPath);
                _token = JsonSerializer.Deserialize<PKCETokenResponse>(json);
                if (_token is not null) _token.CreatedAt = File.GetLastWriteTimeUtc(TokenPath);
                if (_token is not null && !HasRequiredScopes(_token))
                {
                    _token = null;
                    try { File.Delete(TokenPath); } catch { }
                    AuthStateChanged?.Invoke(false);
                }
                else if (_token is not null)
                {
                    // The token is loaded lazily, so tell the UI we're signed in.
                    AuthStateChanged?.Invoke(true);
                }
            }
            catch { _token = null; }
        }

        if (_token is null) return null;

        if (_token.IsExpired)
        {
            try
            {
                if (string.IsNullOrEmpty(_token.RefreshToken)) throw new InvalidOperationException("No refresh token.");
                var refreshed = await new OAuthClient().RequestToken(
                    new PKCETokenRefreshRequest(_clientId, _token.RefreshToken));
                _token = refreshed;
                await PersistAsync();
            }
            catch
            {
                _token = null;
                _savedTokenRejected = true;
                AuthStateChanged?.Invoke(false);
                return null;
            }
        }

        var config = SpotifyClientConfig.CreateDefault().WithToken(_token.AccessToken);
        return new SpotifyClient(config);
    }

    /// <summary>A valid bearer token (refreshing if needed), or null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        await GetClientAsync();
        return _token?.AccessToken;
    }

    private static bool HasRequiredScopes(PKCETokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.Scope)) return true;
        var granted = token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return RequiredScopes.All(granted.Contains);
    }

    public async Task<bool> SignInAsync()
    {
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var loginRequest = new LoginRequest(Callback, _clientId, LoginRequest.ResponseType.Code)
        {
            CodeChallengeMethod = "S256",
            CodeChallenge = challenge,
            Scope = RequiredScopes
        };

        var server = new EmbedIOAuthServer(Callback, CallbackPort);
        var tcs = new TaskCompletionSource<bool>();
        await server.Start();

        server.AuthorizationCodeReceived += async (_, response) =>
        {
            try
            {
                _token = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_clientId, response.Code, Callback, verifier));
                _savedTokenRejected = false;
                await PersistAsync();
                AuthStateChanged?.Invoke(true);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Spotify token error: {ex}");
                tcs.TrySetResult(false);
            }
            finally
            {
                await server.Stop();
            }
        };

        server.ErrorReceived += async (_, error, _) =>
        {
            Debug.WriteLine($"Spotify auth error: {error}");
            await server.Stop();
            tcs.TrySetResult(false);
        };

        try { Process.Start(new ProcessStartInfo(loginRequest.ToUri().ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); tcs.TrySetResult(false); }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(5)));
        if (completed != tcs.Task)
        {
            await server.Stop();
            return false;
        }
        return await tcs.Task;
    }

    public async Task SignOutAsync()
    {
        _token = null;
        if (File.Exists(TokenPath))
        {
            try { File.Delete(TokenPath); } catch { }
        }
        await Task.CompletedTask;
        AuthStateChanged?.Invoke(false);
    }

    private async Task PersistAsync()
    {
        if (_token is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        await File.WriteAllTextAsync(TokenPath, JsonSerializer.Serialize(_token));
    }
}
