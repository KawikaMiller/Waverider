using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Graphics;

namespace Waverider.Util;

public static class WindowExtensions
{
    public static IntPtr GetHandle(this Microsoft.UI.Xaml.Window window)
        => WindowNative.GetWindowHandle(window);

    public static AppWindow GetAppWindow(this Microsoft.UI.Xaml.Window window)
    {
        var hwnd = window.GetHandle();
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    public static void SetAlwaysOnTop(this Microsoft.UI.Xaml.Window window, bool onTop)
    {
        var aw = window.GetAppWindow();
        if (aw.Presenter is OverlappedPresenter op)
        {
            op.IsAlwaysOnTop = onTop;
        }
    }

    public static void Resize(this Microsoft.UI.Xaml.Window window, int width, int height)
    {
        var aw = window.GetAppWindow();
        aw.Resize(new SizeInt32(width, height));
    }

    public static void SetSizeAndPosition(this Microsoft.UI.Xaml.Window window, int x, int y, int width, int height)
    {
        var aw = window.GetAppWindow();
        aw.MoveAndResize(new RectInt32(x, y, width, height));
    }

    public static void SetTitleBarVisible(this Microsoft.UI.Xaml.Window window, bool visible)
    {
        var aw = window.GetAppWindow();
        if (aw.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(visible, visible);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }


    /// <summary>The window's DPI scale relative to 96 DPI (1.0 = 100%).</summary>
    public static double GetScale(this Microsoft.UI.Xaml.Window window)
    {
        var dpi = GetDpiForWindow(window.GetHandle());
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Starts the standard window-move loop, as if the user had grabbed a title bar. For dragging a
    /// borderless window by its content; call it from a pointer handler while the button is down.
    /// </summary>
    public static void BeginDragMove(this Microsoft.UI.Xaml.Window window)
    {
        const uint WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;
        ReleaseCapture();
        SendMessage(window.GetHandle(), WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>
    /// Clips the window to the disc drawn flush in its client area, with the spindle hole cut clear
    /// through. Everything outside the circle (frame, corners, hole) stops being part of the window,
    /// both visually and for mouse hits, so the window reads as just a disc. Call after the final resize.
    /// </summary>
    /// <param name="holeFraction">Spindle hole radius as a fraction of the disc radius; 0 for no hole.</param>
    public static void SetDiscRegion(this Microsoft.UI.Xaml.Window window, float holeFraction)
    {
        const int RGN_DIFF = 4;
        var hwnd = window.GetHandle();

        GetWindowRect(hwnd, out var outer);
        GetClientRect(hwnd, out var client);
        var origin = new NativePoint();
        ClientToScreen(hwnd, ref origin);

        // Region coordinates are relative to the window's top-left, but the disc is centered in the
        // client area, which a frame can inset from there.
        int clientW = client.Right - client.Left, clientH = client.Bottom - client.Top;
        float cx = origin.X - outer.Left + clientW / 2f, cy = origin.Y - outer.Top + clientH / 2f;
        float radius = Math.Min(clientW, clientH) / 2f;

        // One pixel inside the disc's own edge, so rounding can never leave a sliver of window around it.
        var region = Ellipse(cx, cy, radius - 1);
        if (holeFraction > 0)
        {
            var hole = Ellipse(cx, cy, radius * holeFraction);
            CombineRgn(region, region, hole, RGN_DIFF);
            DeleteObject(hole);
        }

        // The OS owns the region handle after a successful SetWindowRgn.
        _ = SetWindowRgn(hwnd, region, true);
    }

    /// <summary>
    /// Shows or hides the thin outline Windows 11 draws around a window's rectangle. DWM draws it
    /// outside any window region, so a window clipped to a disc has to turn it off.
    /// </summary>
    public static void SetWindowOutlineVisible(this Microsoft.UI.Xaml.Window window, bool visible)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1;
        const int COLOR_DEFAULT = unchecked((int)0xFFFFFFFF), COLOR_NONE = unchecked((int)0xFFFFFFFE);

        var hwnd = window.GetHandle();
        int corner = visible ? DWMWCP_DEFAULT : DWMWCP_DONOTROUND;
        int border = visible ? COLOR_DEFAULT : COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Gives the window its normal rectangular shape back.</summary>
    public static void ClearWindowRegion(this Microsoft.UI.Xaml.Window window)
        => _ = SetWindowRgn(window.GetHandle(), IntPtr.Zero, true);

    private static IntPtr Ellipse(float cx, float cy, float radius)
        => CreateEllipticRgn((int)Math.Round(cx - radius), (int)Math.Round(cy - radius),
                             (int)Math.Round(cx + radius) + 1, (int)Math.Round(cy + radius) + 1);

    public static (int Width, int Height) GetPrimaryScreenSize()
        => (GetSystemMetrics(0), GetSystemMetrics(1));
}
