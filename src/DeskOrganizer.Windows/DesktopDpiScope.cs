using System.Runtime.InteropServices;

namespace DeskOrganizer;

/// <summary>Query view geometry in Explorer's DPI context, restoring the caller's context on the same thread.</summary>
internal sealed class DesktopDpiScope : IDisposable
{
    private readonly IntPtr previous;
    private DesktopDpiScope(IntPtr previous) => this.previous = previous;

    internal static DesktopDpiScope ForWindow(IntPtr window)
    {
        IntPtr context = GetWindowDpiAwarenessContext(window);
        IntPtr previous = context == IntPtr.Zero ? IntPtr.Zero : SetThreadDpiAwarenessContext(context);
        if (previous == IntPtr.Zero)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Could not align desktop queries with Explorer's DPI awareness context.");
        return new DesktopDpiScope(previous);
    }

    public void Dispose() => SetThreadDpiAwarenessContext(previous);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
