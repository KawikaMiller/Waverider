using System.Diagnostics;

namespace Waverider.Audio;

internal static class AppProcessLocator
{
    // Apps like Spotify, Discord and browsers spawn many processes of the same name (renderer, GPU,
    // audio service, ...). For process-tree loopback we want the root parent: the oldest one.
    public static uint? FindRootPid(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            if (procs.Length == 0) return null;

            Process? root = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var p in procs)
            {
                try
                {
                    if (p.StartTime < oldest)
                    {
                        oldest = p.StartTime;
                        root = p;
                    }
                }
                catch
                {
                    // Some processes refuse StartTime queries — skip them.
                }
            }
            root ??= procs[0];
            return (uint)root.Id;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
