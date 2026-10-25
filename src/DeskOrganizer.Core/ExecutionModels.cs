namespace DeskOrganizer;

public sealed record DesktopAccessPolicy(bool WritesEnabled = false);
public sealed record IconTarget(string IdentityKey, DesktopPoint Position);
public sealed record DesktopMutation(DesktopObservation Before, IReadOnlyList<IconTarget> Targets);
public sealed record DesktopMutationResult(bool Submitted, string? ErrorCode = null, bool OutcomeUncertain = false);

public interface IDesktopExecutionBackend : IDesktopBackend
{
    // A thrown exception may mean the native call is still outstanding. Never retry automatically.
    Task<DesktopMutationResult> ApplyAsync(DesktopMutation mutation, CancellationToken cancellationToken = default);
}

public sealed record ExecutionPlan(LayoutPlan Plan, DesktopObservation Before,
    IReadOnlyList<IconTarget> Targets, string? RestoreOf = null);

public sealed record DesktopOperation(string OperationId, string Status, string? BackupId,
    string? RestoreOf, int ChangedCount, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    bool RecoveryRequired, string? ErrorCode, string Message);

// Private journal data: never serialize this object as an MCP result.
public sealed record OperationRecord(int Version, string RequestKey, DesktopOperation Result,
    DesktopObservation Before, DesktopObservation ExpectedAfter, DesktopObservation? ObservedAfter);

public interface IOperationStore
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    OperationRecord? Read(string operationId);
    IReadOnlyList<OperationRecord> ReadAll();
    void Save(OperationRecord record);
    bool IsWriterActive();
}

public static class MutationValidator
{
    public static DesktopObservation ExpectedAfter(DesktopMutation mutation)
    {
        Validate(mutation);
        var targets = mutation.Targets.ToDictionary(target => target.IdentityKey, target => target.Position, StringComparer.Ordinal);
        return mutation.Before with
        {
            Icons = mutation.Before.Icons.Select(icon => icon with { Position = targets[icon.IdentityKey!] }).ToArray()
        };
    }

    public static bool SupportedGeometry(DesktopGeometryInfo geometry) =>
        geometry.Monitors.Count == 1 && geometry.Monitors[0].Primary
        && geometry.WindowDpi > 0 && geometry.UsableArea.Right > geometry.UsableArea.Left
        && geometry.UsableArea.Bottom > geometry.UsableArea.Top;

    public static void Validate(DesktopMutation mutation)
    {
        var before = mutation.Before;
        if (!SupportedGeometry(before.Geometry) || !before.DpiAligned)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Writing requires one primary monitor and geometry aligned with Explorer's DPI awareness.");
        if (before.Geometry.AutoArrange != false)
            throw new DesktopException("AUTO_ARRANGE_ENABLED", "Disable desktop auto-arrange manually before applying a layout.");
        if (before.Icons.Count > LayoutPlanner.MaxIcons || mutation.Targets.Count != before.Icons.Count)
            throw new DesktopException("INVALID_PLAN", "The plan must account for every desktop icon.");
        if (before.Icons.Any(icon => string.IsNullOrEmpty(icon.IdentityKey))
            || before.Icons.Select(icon => icon.IdentityKey).Distinct(StringComparer.Ordinal).Count() != before.Icons.Count)
            throw new DesktopException("UNRESOLVED_IDENTITY", "Every icon must have an unambiguous Shell identity before writing.");
        var identities = before.Icons.Select(icon => icon.IdentityKey!).ToHashSet(StringComparer.Ordinal);
        if (mutation.Targets.Any(target => !identities.Contains(target.IdentityKey))
            || mutation.Targets.Select(target => target.IdentityKey).Distinct(StringComparer.Ordinal).Count() != before.Icons.Count
            || mutation.Targets.Select(target => target.Position).Distinct().Count() != before.Icons.Count)
            throw new DesktopException("INVALID_PLAN", "Plan identities and destinations must be unique and complete.");
        var sources = before.Icons.ToDictionary(icon => icon.IdentityKey!, StringComparer.Ordinal);
        if (mutation.Targets.Any(target => target.Position != sources[target.IdentityKey].Position
            && !before.Geometry.UsableArea.Contains(target.Position)))
            throw new DesktopException("INVALID_DESTINATION", "A changed target lies outside the reported desktop view rectangle.");
    }
}
