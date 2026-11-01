namespace Waverider.Audio;

public enum AudioCaptureMode
{
    /// <summary>This app's own process tree: the built-in Web Playback SDK player.</summary>
    ThisApp,
    /// <summary>The Spotify desktop client's processes only.</summary>
    SpotifyOnly,
    /// <summary>One chosen application's processes, picked from the apps that currently have audio (see <see cref="AudioAppEnumerator"/>).</summary>
    Application,
    SystemLoopback
}
