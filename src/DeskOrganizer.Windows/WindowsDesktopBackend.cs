using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskOrganizer;

/// <summary>Read-only Shell backend. Does not open Explorer's memory or invoke legacy CLI commands.</summary>
public sealed class WindowsDesktopBackend : IDesktopBackend
{
    private static readonly SemaphoreSlim QueryGate = new(1, 1);

    public async Task<DesktopObservation> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new DesktopException("UNSUPPORTED_PLATFORM", "Desktop queries require an interactive Windows session.");
        await QueryGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => CaptureConsistent(cancellationToken), cancellationToken);
        }
        catch (DesktopException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            throw new DesktopException("DESKTOP_TIMEOUT", "Desktop query timed out. A pending Shell query may need to finish before another query can run.");
        }
        catch (Exception)
        {
            // COM errors can include filesystem parsing names. Never expose native exception text.
            throw new DesktopException("DESKTOP_UNAVAILABLE", "Could not read the desktop. Explorer must be running in the same interactive Windows session.");
        }
        finally { QueryGate.Release(); }
    }

    private static DesktopObservation CaptureConsistent(CancellationToken cancellationToken)
    {
        IntPtr view = FindView();
        using var dpi = DesktopDpiScope.ForWindow(view);
        string token = ViewToken(view);
        var firstGeometry = DesktopGeometry.Capture(view);
        var firstPlacement = DesktopGeometry.ReadPlacementArea(view, firstGeometry);
        var first = ShellIdentityProbe.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        var second = ShellIdentityProbe.Capture();
        var secondGeometry = DesktopGeometry.Capture(view);
        var secondPlacement = DesktopGeometry.ReadPlacementArea(view, secondGeometry);
        if (FindView() != view || ViewToken(view) != token)
            throw new DesktopException("DESKTOP_CHANGED", "Desktop view changed during the query; capture it again.");

        static DesktopObservation Observe(string token, IReadOnlyList<ShellIdentityProbe.Item> items, DesktopGeometryInfo geometry, DesktopArea? placement) =>
            new(token, items.Select(item => new ObservedDesktopIcon(item.Name,
                new DesktopPoint(item.X, item.Y), item.ShellId)).ToArray(), geometry, DpiAligned: true,
                ExplorerInstance: token[..token.LastIndexOf(':')], PlacementArea: placement);

        var before = Observe(token, first, firstGeometry, firstPlacement);
        var after = Observe(token, second, secondGeometry, secondPlacement);
        if (DesktopPlanningService.Fingerprint(before) != DesktopPlanningService.Fingerprint(after))
            throw new DesktopException("DESKTOP_CHANGED", "Desktop identity, positions or geometry changed during the query; capture it again.");
        return after;
    }

    internal static IntPtr FindView()
    {
        IntPtr view = LegacyDesktopReader.FindDesktopView(false);
        return view != IntPtr.Zero ? view : throw new DesktopException("DESKTOP_UNAVAILABLE", "Desktop icon view is unavailable.");
    }

    internal static string ViewToken(IntPtr view)
    {
        GetWindowThreadProcessId(view, out uint pid);
        using var explorer = Process.GetProcessById(checked((int)pid));
        return $"{pid}:{explorer.StartTime.ToUniversalTime().Ticks}:{view.ToInt64()}";
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
