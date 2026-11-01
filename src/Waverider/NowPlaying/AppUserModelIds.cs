using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Waverider.NowPlaying;

/// <summary>
/// Finds the Windows app identities (AppUserModelIDs) that a running program's windows use, so a media session
/// can be tied back to the program that owns it. Some apps use an ID unrelated to their name: Firefox's, for
/// example, is a hash.
/// </summary>
internal static class AppUserModelIds
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static IReadOnlyList<string> ForProcess(string processName)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p) pids.Add((uint)p.Id);
        }
        if (pids.Count == 0) return Array.Empty<string>();

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pids.Contains(pid) && ReadId(hwnd) is { Length: > 0 } id) ids.Add(id);
            return true;
        }, IntPtr.Zero);
        return ids.ToList();
    }

    private static string? ReadId(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var obj) != 0 || obj is not IPropertyStore store) return null;
            try
            {
                var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 }; // System.AppUserModel.ID
                if (store.GetValue(ref key, out var value) != 0) return null;
                try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch
        {
            return null; // a window that disappeared or refuses the query
        }
    }

    private const ushort VtLpwstr = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
