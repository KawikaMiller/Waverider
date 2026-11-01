using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Waverider.Audio;

/// <summary>An application that has an audio session on a playback device, like an entry in the Windows volume mixer.</summary>
/// <param name="ProcessName">Executable name without extension; what process-tree capture targets.</param>
/// <param name="DisplayName">Friendly name for the picker.</param>
/// <param name="IsPlaying">True while the app has an active audio stream.</param>
public sealed record AudioApp(string ProcessName, string DisplayName, bool IsPlaying);

public static class AudioAppEnumerator
{
    // Processes that are never a useful capture target: this app, and the WebView2 host that has its own
    // "This app (built-in player)" entry.
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Waverider", "msedgewebview2"
    };

    /// <summary>Apps with an audio session on any active playback device, playing ones first.</summary>
    public static IReadOnlyList<AudioApp> GetApps()
    {
        var byName = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.IsSystemSoundsSession) continue;
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                        var info = Describe(session.GetProcessID);
                        if (info is null || Excluded.Contains(info.Value.Name)) continue;

                        bool playing = session.State == AudioSessionState.AudioSessionStateActive;
                        if (byName.TryGetValue(info.Value.Name, out var existing) && (existing.IsPlaying || !playing)) continue;
                        byName[info.Value.Name] = new AudioApp(info.Value.Name, info.Value.Display, playing);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioAppEnumerator] {ex.Message}");
        }

        return byName.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Friendly name for a process name seen earlier, for entries whose app isn't running right now.</summary>
    public static string DisplayNameFor(string processName) => Capitalize(processName);

    private static (string Name, string Display)? Describe(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            string? description = null;
            try { description = proc.MainModule?.FileVersionInfo.FileDescription; }
            catch { /* protected or elevated process: fall back to the executable name */ }
            return (name, string.IsNullOrWhiteSpace(description) ? Capitalize(name) : description.Trim());
        }
        catch
        {
            return null; // the process exited between listing and lookup
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
