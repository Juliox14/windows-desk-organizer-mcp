using System.Runtime.InteropServices;

namespace DeskOrganizer;

/// <summary>Observed geometry of the desktop ListView and the current display topology.</summary>
internal static class DesktopGeometry
{
    private const uint LvmGetItemSpacing = 0x1033;
    private const int GwlStyle = -16;
    private const int LvsAutoArrange = 0x0100;
    private const uint MonitorInfoPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowStyle(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    internal static object Capture(IntPtr listView)
    {
        if (!GetClientRect(listView, out Rect client))
            throw new InvalidOperationException("Could not read desktop ListView client area.");

        long packedSpacing = SendMessage(listView, LvmGetItemSpacing, IntPtr.Zero, IntPtr.Zero).ToInt64();
        int spacingX = (int)(packedSpacing & 0xffff);
        int spacingY = (int)((packedSpacing >> 16) & 0xffff);
        var gridSpacing = spacingX > 0 && spacingY > 0 ? new { x = spacingX, y = spacingY } : null;

        // ListView style reports its current auto-arrange mode, not a possibly stale registry value.
        Marshal.SetLastPInvokeError(0);
        int style = GetWindowStyle(listView, GwlStyle);
        bool? autoArrange = style == 0 && Marshal.GetLastWin32Error() != 0
            ? null : (style & LvsAutoArrange) != 0;

        var monitors = new List<object>();
        bool infoFailed = false;
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                infoFailed = true;
                return false;
            }
            monitors.Add(new
            {
                bounds = Area(info.Monitor), workArea = Area(info.Work),
                primary = (info.Flags & MonitorInfoPrimary) != 0
            });
            return true;
        };
        bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (!enumerated || infoFailed || monitors.Count == 0)
            throw new InvalidOperationException("Could not enumerate desktop monitors.");

        uint windowDpi = GetDpiForWindow(listView);
        return new
        {
            gridSpacing,
            usableArea = Area(client),
            monitors,
            windowDpi = windowDpi == 0 ? (uint?)null : windowDpi,
            // A window DPI is not evidence of each monitor's physical DPI or scale.
            monitorDpi = (uint?)null,
            autoArrange
        };
    }

    internal static bool? ReadAutoArrange(IntPtr listView)
    {
        Marshal.SetLastPInvokeError(0);
        int style = GetWindowStyle(listView, GwlStyle);
        return style == 0 && Marshal.GetLastWin32Error() != 0
            ? null : (style & LvsAutoArrange) != 0;
    }

    internal static (int Left, int Top, int Right, int Bottom) ReadClientBounds(IntPtr listView)
    {
        if (!GetClientRect(listView, out Rect client))
            throw new InvalidOperationException("Could not read desktop ListView client area.");
        return (client.Left, client.Top, client.Right, client.Bottom);
    }

    /// <summary>Read the caller's DPI context and map the icon view into its reported screen coordinates.</summary>
    internal static object ProbeCoordinates(IntPtr listView)
    {
        Rect client = default;
        bool hasClient = GetClientRect(listView, out client);
        Point origin = new() { X = client.Left, Y = client.Top };
        Point end = new() { X = client.Right, Y = client.Bottom };
        bool mapped = hasClient && ClientToScreen(listView, ref origin) && ClientToScreen(listView, ref end);
        Rect? screen = mapped ? new Rect { Left = origin.X, Top = origin.Y, Right = end.X, Bottom = end.Y } : null;

        var monitors = new List<(Rect Bounds, object Data)>();
        bool infoFailed = false;
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                infoFailed = true;
                return false;
            }
            monitors.Add((info.Monitor, new
            {
                bounds = Area(info.Monitor), workArea = Area(info.Work),
                primary = (info.Flags & MonitorInfoPrimary) != 0
            }));
            return true;
        };
        bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (!enumerated || infoFailed || monitors.Count == 0)
            throw new InvalidOperationException("Could not enumerate desktop monitors.");

        // These API observations do not establish physical pixels or per-monitor DPI.
        return new
        {
            threadDpiAwareness = Awareness(() => GetThreadDpiAwarenessContext()),
            windowDpiAwareness = Awareness(() => GetWindowDpiAwarenessContext(listView)),
            clientScreenRect = screen.HasValue ? Area(screen.Value) : null,
            monitors = monitors.Select(monitor => monitor.Data),
            overlappingMonitorIndices = screen.HasValue
                ? monitors.Select((monitor, index) => (monitor, index))
                    .Where(pair => Overlaps(screen.Value, pair.monitor.Bounds))
                    .Select(pair => pair.index).ToArray()
                : null
        };
    }

    private static bool Overlaps(Rect first, Rect second) => first.Left < second.Right
        && second.Left < first.Right && first.Top < second.Bottom && second.Top < first.Bottom;

    private static string? Awareness(Func<IntPtr> getContext)
    {
        try
        {
            IntPtr context = getContext();
            if (context == IntPtr.Zero) return null;
            return GetAwarenessFromDpiAwarenessContext(context) switch
            {
                0 => "unaware", 1 => "system", 2 => "perMonitor", _ => null
            };
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static object Area(Rect rect) => new
    {
        left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom
    };
}
