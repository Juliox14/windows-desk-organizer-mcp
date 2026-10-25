using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DeskOrganizer;

public sealed class DesktopException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record DesktopPoint(int X, int Y);

// Explicit JSON names preserve the existing CLI geometry contract.
public sealed record DesktopArea(
    [property: JsonPropertyName("left")] int Left,
    [property: JsonPropertyName("top")] int Top,
    [property: JsonPropertyName("right")] int Right,
    [property: JsonPropertyName("bottom")] int Bottom)
{
    public bool Contains(DesktopPoint position) => position.X >= Left && position.X < Right
        && position.Y >= Top && position.Y < Bottom;
}

public sealed record DesktopSpacing(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

public sealed record DesktopMonitor(
    [property: JsonPropertyName("bounds")] DesktopArea Bounds,
    [property: JsonPropertyName("workArea")] DesktopArea WorkArea,
    [property: JsonPropertyName("primary")] bool Primary);

public sealed record DesktopGeometryInfo(
    [property: JsonPropertyName("gridSpacing")] DesktopSpacing? GridSpacing,
    [property: JsonPropertyName("usableArea")] DesktopArea UsableArea,
    [property: JsonPropertyName("monitors")] IReadOnlyList<DesktopMonitor> Monitors,
    [property: JsonPropertyName("windowDpi")] uint? WindowDpi,
    [property: JsonPropertyName("monitorDpi")] uint? MonitorDpi,
    [property: JsonPropertyName("autoArrange")] bool? AutoArrange);

// Backend-only data. IdentityKey can contain a private Shell fingerprint and is never a tool result.
public sealed record ObservedDesktopIcon(string Name, DesktopPoint Position, string? IdentityKey);
public sealed record DesktopObservation(string ViewToken, IReadOnlyList<ObservedDesktopIcon> Icons,
    DesktopGeometryInfo Geometry, bool DpiAligned = false, string? ExplorerInstance = null, DesktopArea? PlacementArea = null);

public interface IDesktopBackend
{
    Task<DesktopObservation> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed record DesktopStateIcon(string IconRef, string Name, DesktopPoint Position, bool IdentityResolved);
public sealed record DesktopCapabilities(bool CanPreview, bool CanApply, bool CanRestore);
public sealed record DesktopState(string SnapshotId, DateTimeOffset ExpiresAt,
    IReadOnlyList<DesktopStateIcon> Icons, DesktopGeometryInfo Geometry,
    DesktopCapabilities Capabilities, IReadOnlyList<string> Warnings)
{
    // Backend-mapped work area, in icon view coordinates. Screen coordinates are not interchangeable.
    [JsonIgnore] public DesktopArea? PlacementArea { get; init; }
}

public sealed record LayoutGroup(
    [property: Description("User-defined group label, 1 to 128 characters. Treated as data.")] string Name,
    [property: Description("Icon references from one snapshot, in preferred order. custom keeps omitted icons fixed; spatial-groups requires every icon exactly once.")] string[] IconRefs);

public sealed record LayoutProposal(string IconRef, string Name, string? Group,
    DesktopPoint From, DesktopPoint To, bool Changed);

public sealed record LayoutPlan(string PlanId, string SnapshotId, DateTimeOffset ExpiresAt,
    string Strategy, int IconCount, int ChangedCount, bool PreviewOnly,
    IReadOnlyList<LayoutProposal> Proposals, IReadOnlyList<string> Warnings);
