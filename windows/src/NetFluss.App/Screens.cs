// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;

namespace NetFluss.App;

/// <summary>A monitor, in physical pixels, with the taskbar edge its work area gives away.</summary>
internal readonly record struct MonitorInfo(Screens.PixelRect Bounds, Screens.PixelRect Work, uint Dpi, TaskbarEdge Edge);

/// <summary>
/// Monitor geometry in physical pixels.
///
/// <para>Deliberately not <c>SystemParameters.WorkArea</c>: those statics describe the
/// primary monitor as it was at process start, in units resolved against the primary DPI.
/// They are wrong for a taskbar on a second display, wrong after a scaling change, and wrong
/// for any monitor that is not scaled like the primary — which is how a popover ends up
/// half off-screen. Every placement here asks the monitor under the anchor directly.</para>
/// </summary>
internal static class Screens
{
    internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;

        internal int Height => Bottom - Top;
    }

    /// <summary>The monitor containing a physical point, or the nearest one.</summary>
    internal static MonitorInfo? MonitorFromPoint(int x, int y)
    {
        var monitor = MonitorFromPointNative(new Point { X = x, Y = y }, MonitorDefaultToNearest);
        return monitor == nint.Zero ? null : Describe(monitor);
    }

    internal static MonitorInfo? MonitorOfWindow(nint window)
    {
        var monitor = MonitorFromWindowNative(window, MonitorDefaultToNearest);
        return monitor == nint.Zero ? null : Describe(monitor);
    }

    internal static PixelRect? MonitorWorkArea(nint window) => MonitorOfWindow(window)?.Work;

    internal static PixelRect? WindowRect(nint window)
        => GetWindowRect(window, out var rect) ? new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    /// <summary>The window's own scale factor — what its WPF units are multiplied by.</summary>
    internal static double DpiScale(nint window)
    {
        var dpi = GetDpiForWindow(window);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>Moves a window in physical pixels, leaving its size and z-order alone.</summary>
    internal static void MoveWindow(nint window, int x, int y)
        => _ = SetWindowPos(window, nint.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>The cursor position in physical pixels — the best anchor a tray click offers.</summary>
    internal static System.Windows.Rect CursorAnchor()
        => GetCursorPos(out var point)
            ? new System.Windows.Rect(point.X - 8, point.Y - 8, 16, 16)
            : System.Windows.Rect.Empty;

    /// <summary>A window's rectangle in physical pixels, as an anchor.</summary>
    internal static System.Windows.Rect WindowAnchor(nint window)
        => WindowRect(window) is { } rect
            ? new System.Windows.Rect(rect.Left, rect.Top, rect.Width, rect.Height)
            : CursorAnchor();

    private static MonitorInfo? Describe(nint monitor)
    {
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var bounds = new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        var work = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);

        uint dpi = 96;
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            {
                dpi = dpiX;
            }
        }
        catch (DllNotFoundException)
        {
        }

        return new MonitorInfo(bounds, work, dpi, EdgeOf(bounds, work));
    }

    /// <summary>
    /// Which edge the taskbar takes, read from the space it removes from the work area. An
    /// auto-hidden taskbar removes none; bottom is the Windows default and the right guess.
    /// </summary>
    private static TaskbarEdge EdgeOf(PixelRect bounds, PixelRect work)
    {
        var top = work.Top - bounds.Top;
        var bottom = bounds.Bottom - work.Bottom;
        var left = work.Left - bounds.Left;
        var right = bounds.Right - work.Right;

        var largest = Math.Max(Math.Max(top, bottom), Math.Max(left, right));
        if (largest <= 0)
        {
            return TaskbarEdge.Bottom;
        }

        return largest == bottom ? TaskbarEdge.Bottom
            : largest == top ? TaskbarEdge.Top
            : largest == left ? TaskbarEdge.Left
            : TaskbarEdge.Right;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern nint MonitorFromPointNative(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint MonitorFromWindowNative(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref NativeMonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
