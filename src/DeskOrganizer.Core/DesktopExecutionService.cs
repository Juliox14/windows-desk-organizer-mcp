namespace DeskOrganizer;

public sealed class DesktopExecutionService(DesktopPlanningService planning, IDesktopExecutionBackend backend,
    IOperationStore store, TimeProvider clock)
{
    public DesktopOperation GetOperation(string operationId)
    {
        var result = (store.Read(operationId) ?? throw new DesktopException("UNKNOWN_OPERATION", "Operation not found in this user's local history.")).Result;
        return result.Status == "running" && !store.IsWriterActive() ? Interrupted(result) : result;
    }

    public async Task<LayoutPlan> PreviewRestoreAsync(string backupId, string snapshotId, CancellationToken cancellationToken = default)
    {
        var original = store.Read(backupId) ?? throw new DesktopException("UNKNOWN_BACKUP", "Backup is unavailable.");
        if (original.Result.BackupId is null)
            throw new DesktopException("UNKNOWN_BACKUP", "This operation has no restorable backup.");
        var (state, observation) = planning.GetSnapshot(snapshotId);
        var current = await backend.CaptureAsync(cancellationToken);
        if (DesktopPlanningService.Fingerprint(current) != DesktopPlanningService.Fingerprint(observation))
            throw new DesktopException("STALE_SNAPSHOT", "Desktop changed; capture it again before planning a restoration.");
        if (state.ExpiresAt <= clock.GetUtcNow())
            throw new DesktopException("EXPIRED_SNAPSHOT", "The snapshot expired; capture the desktop again.");

        var comparison = original.ObservedAfter ?? original.ExpectedAfter;
        // Never silently replace user edits made after the recorded result.
        if (!SameLayout(current, comparison) && !SameLayout(current, original.Before))
            throw new DesktopException("RESTORE_CONFLICT", "The desktop differs from the recorded result. Review the current layout and create a custom plan instead.");
        var sourceIcons = original.Before.Icons.ToDictionary(icon => icon.IdentityKey ?? "", StringComparer.Ordinal);
        if (sourceIcons.Count != current.Icons.Count || current.Icons.Any(icon => icon.IdentityKey is null || !sourceIcons.ContainsKey(icon.IdentityKey)))
            throw new DesktopException("RESTORE_CONFLICT", "Desktop icon identities changed; automatic restoration is unavailable.");
        // Snapshot references follow the snapshot's order, independently of a fresh Shell enumeration order.
        var proposals = observation.Icons.Select((icon, index) =>
        {
            var target = sourceIcons[icon.IdentityKey!].Position;
            return new LayoutProposal(state.Icons[index].IconRef, icon.Name, null, icon.Position, target, icon.Position != target);
        }).ToArray();
        var plan = new LayoutPlan(Guid.NewGuid().ToString("N"), snapshotId, state.ExpiresAt, "restore",
            proposals.Length, proposals.Count(proposal => proposal.Changed), true, proposals,
            new[] { "Restoration is a new verified operation. It is not a transactional rollback." });
        return planning.RememberPlan(plan, observation, state, backupId);
    }

    public async Task<DesktopOperation> ApplyAsync(string planId, string operationId, bool restoration = false,
        CancellationToken cancellationToken = default)
    {
        FileOperationStore.ValidateId(operationId);
        if (!planning.WritesEnabled)
            throw new DesktopException("WRITES_DISABLED", "Desktop writing has not been enabled by the local operator.");
        string requestKey = $"{(restoration ? "restore" : "apply")}:{planId}";
        await using var lease = await store.AcquireAsync(cancellationToken);
        var previous = store.Read(operationId);
        if (previous is not null)
        {
            if (previous.RequestKey != requestKey)
                throw new DesktopException("OPERATION_CONFLICT", "This operation ID was already used for another request.");
            if (previous.Result.Status == "running")
            {
                previous = previous with { Result = Interrupted(previous.Result) };
                store.Save(previous);
            }
            // Consult the journal before resolving the session-local plan: retries survive server restart and plan eviction.
            return previous.Result;
        }
        var plan = planning.GetExecutionPlan(planId);
        if (restoration != (plan.RestoreOf is not null))
            throw new DesktopException("INVALID_PLAN", "Use restore_layout only with a restoration preview and apply_layout with an organization preview.");
        var mutation = new DesktopMutation(plan.Before, plan.Targets);
        var expected = MutationValidator.ExpectedAfter(mutation);
        var current = await backend.CaptureAsync(cancellationToken);
        if (DesktopPlanningService.Fingerprint(current) != DesktopPlanningService.Fingerprint(plan.Before))
            throw new DesktopException("STALE_PLAN", "Desktop identity, positions or geometry changed; preview a new plan.");
        if (plan.Plan.ExpiresAt <= clock.GetUtcNow())
            throw new DesktopException("EXPIRED_PLAN", "The plan expired during validation; preview it again.");
        CheckRecovery(current, plan.RestoreOf);
        cancellationToken.ThrowIfCancellationRequested();
        bool unchanged = DesktopPlanningService.Fingerprint(expected) == DesktopPlanningService.Fingerprint(current);
        var result = new DesktopOperation(operationId, unchanged ? "unchanged" : "running", unchanged ? null : operationId,
            plan.RestoreOf, plan.Plan.ChangedCount, clock.GetUtcNow(), unchanged ? clock.GetUtcNow() : null,
            !unchanged, null, unchanged ? "The requested positions are already in place." :
                "Operation started. Do not retry with a different ID; use get_operation to inspect its result.");
        var record = new OperationRecord(1, requestKey, result, current, expected, unchanged ? current : null);
        store.Save(record); // Must be durable before the first native mutation.
        if (!unchanged)
        {
            try
            {
                // After dispatch, cancellation/disconnection does not abandon verification and durable completion.
                var submission = await backend.ApplyAsync(mutation, CancellationToken.None);
                if (submission.OutcomeUncertain)
                    throw new DesktopException("RESULT_UNKNOWN", "The native call did not report a definite outcome.");
                if (!submission.Submitted)
                {
                    result = result with
                    {
                        Status = "rejected",
                        RecoveryRequired = false,
                        ErrorCode = submission.ErrorCode,
                        Message = "The worker rejected the operation before sending a movement to Explorer."
                    };
                }
                else
                {
                    DesktopObservation? observed = null;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        var next = await backend.CaptureAsync(CancellationToken.None);
                        if (observed is not null && DesktopPlanningService.Fingerprint(observed) == DesktopPlanningService.Fingerprint(next))
                        { observed = next; break; }
                        observed = next;
                        if (attempt == 2) throw new DesktopException("UNSTABLE_RESULT", "Desktop did not stabilize after movement.");
                        await Task.Delay(150);
                    }
                    record = record with { ObservedAfter = observed };
                    bool matched = DesktopPlanningService.Fingerprint(expected) == DesktopPlanningService.Fingerprint(observed!);
                    result = result with
                    {
                        Status = matched ? "applied" : "partial",
                        RecoveryRequired = !matched,
                        ErrorCode = matched ? null : submission.ErrorCode ?? "RESULT_MISMATCH",
                        Message = matched ? "All requested positions and untouched icons were verified." :
                            "Observed layout differs from the plan. Review it before restoring; no automatic retry was performed."
                    };
                }
            }
            catch (Exception)
            {
                result = result with
                {
                    Status = "unknown",
                    RecoveryRequired = true,
                    ErrorCode = "RESULT_UNKNOWN",
                    Message = "Movement may have occurred. Further writes are blocked for this Explorer process. Inspect the desktop and retain the operation ID; do not retry blindly."
                };
            }
        }
        result = result with { CompletedAt = clock.GetUtcNow() };
        record = record with { Result = result };
        store.Save(record);
        if ((result.Status is "applied" or "unchanged") && plan.RestoreOf is not null)
        {
            var original = store.Read(plan.RestoreOf);
            if (original is not null && original.Result.RecoveryRequired)
                store.Save(original with { Result = original.Result with { RecoveryRequired = false } });
        }
        return result;
    }

    private void CheckRecovery(DesktopObservation current, string? restoring)
    {
        var records = store.ReadAll();
        foreach (var record in records.Where(record => record.Result.RecoveryRequired))
        {
            bool restored = records.Any(other => other.Result.RestoreOf == record.Result.OperationId
                && other.Result.Status is "applied" or "unchanged");
            bool oldProcessEnded = record.Before.ExplorerInstance is not null && current.ExplorerInstance is not null
                && record.Before.ExplorerInstance != current.ExplorerInstance;
            if (restored || oldProcessEnded)
            {
                // Child workers are tied to their parent process by a Windows job. The lease excludes a live writer.
                store.Save(record with
                {
                    Result = record.Result with
                    {
                        Status = record.Result.Status == "running" ? "unknown" : record.Result.Status,
                        RecoveryRequired = false,
                        Message = restored ? "A restoration operation completed." :
                        "The previous Explorer process ended. Its final outcome remains recorded; fresh plans may now be evaluated."
                    }
                });
                continue;
            }
            if (record.Result.Status == "partial" && restoring == record.Result.OperationId)
                continue;
            throw new DesktopException("RECOVERY_REQUIRED", "A previous operation requires recovery. A known partial result can be restored from a fresh preview. An uncertain call blocks writes until the previous Explorer process has ended.");
        }
    }

    private static bool SameLayout(DesktopObservation first, DesktopObservation second)
    {
        // Older journals predate PlacementArea. Their geometry still includes the monitor work area.
        bool comparePlacement = first.PlacementArea is not null && second.PlacementArea is not null;
        return DesktopPlanningService.Fingerprint(first with
            { ViewToken = "", ExplorerInstance = null, PlacementArea = comparePlacement ? first.PlacementArea : null })
            == DesktopPlanningService.Fingerprint(second with
            { ViewToken = "", ExplorerInstance = null, PlacementArea = comparePlacement ? second.PlacementArea : null });
    }

    private static DesktopOperation Interrupted(DesktopOperation result) => result with
    {
        Status = "unknown",
        RecoveryRequired = true,
        ErrorCode = "INTERRUPTED_OPERATION",
        Message = "The writer ended without a durable final result. Movement may have occurred; do not retry with a different operation ID."
    };
}
