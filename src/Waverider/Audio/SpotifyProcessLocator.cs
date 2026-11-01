using System.Diagnostics;

namespace Waverider.Audio;

internal static class SpotifyProcessLocator
{
    // Spotify spawns several Spotify.exe instances (renderer, GPU, audio, ...).
    // For process-tree loopback we want the root parent — the oldest one.
    public static uint? FindMainSpotifyPid()
    {
        var procs = Process.GetProcessesByName("Spotify");
        try
        {
            if (procs.Length == 0) return null;

            Process? main = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var p in procs)
            {
                try
                {
                    if (p.StartTime < oldest)
                    {
                        oldest = p.StartTime;
                        main = p;
                    }
                }
                catch
                {
                    // Some processes refuse StartTime queries — skip them.
                }
            }
            main ??= procs[0];
            return (uint)main.Id;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
