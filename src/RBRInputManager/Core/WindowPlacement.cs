using System.Runtime.InteropServices;

namespace RBRInputManager.Core;

/// <summary>Saved window position, in screen pixels, of the window when not maximized.</summary>
public sealed record WindowBounds(int Left, int Top, int Right, int Bottom, bool Maximized);

/// <summary>
/// Reads and restores a window's normal (restored) position through Win32 placement, which works in
/// physical pixels and so survives DPI differences between monitors.
/// </summary>
public static class WindowPlacement
{
    // Allows for the invisible resize borders and windows snapped against a screen edge.
    private const int Tolerance = 16;

    public static WindowBounds? Read(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return null;
        var (dx, dy) = WorkspaceOffset();
        var r = wp.rcNormalPosition;
        return new WindowBounds(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy, wp.showCmd == SW_SHOWMAXIMIZED);
    }

    /// <summary>
    /// Moves a not-yet-shown window to <paramref name="b"/> (it stays hidden). Returns false, leaving the window
    /// alone, when the bounds no longer fit on any monitor (monitor unplugged, resolution lowered...).
    /// </summary>
    public static bool Restore(IntPtr hwnd, WindowBounds b)
    {
        var rect = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;

        IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return false;
        var work = mi.rcWork;
        if (rect.Left < work.Left - Tolerance || rect.Top < work.Top - Tolerance ||
            rect.Right > work.Right + Tolerance || rect.Bottom > work.Bottom + Tolerance)
            return false;

        var (dx, dy) = WorkspaceOffset();
        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = SW_HIDE,
            rcNormalPosition = new RECT { Left = b.Left - dx, Top = b.Top - dy, Right = b.Right - dx, Bottom = b.Bottom - dy },
        };
        return SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>
    /// rcNormalPosition is in workspace coordinates, which are offset from screen coordinates by the
    /// primary monitor's taskbar when it sits on the left or top edge.
    /// </summary>
    private static (int dx, int dy) WorkspaceOffset()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr primary = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        if (!GetMonitorInfo(primary, ref mi)) return (0, 0);
        return (mi.rcWork.Left - mi.rcMonitor.Left, mi.rcWork.Top - mi.rcMonitor.Top);
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOWMAXIMIZED = 3;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
