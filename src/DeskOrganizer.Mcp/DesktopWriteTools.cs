using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DeskOrganizer.Mcp;

[McpServerToolType]
public sealed class DesktopWriteTools(DesktopExecutionService execution)
{
    [McpServerTool(Name = "apply_layout", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Apply an exact organization plan returned by preview_layout within the user's authorized request. Changes Windows desktop icon positions and writes a local backup/journal. Requires local --enable-writes. Reuse the SAME operationId when checking/retrying this request; never retry an unknown or partial result with another ID. This is a batch, not an atomic transaction. Inspect the returned status.")]
    public Task<DesktopOperation> ApplyLayout(
        [Description("Exact planId from preview_layout in this server session.")] string planId,
        [Description("New lowercase GUID in N format (32 hexadecimal characters), retained for all retries of this exact request.")] string operationId,
        CancellationToken cancellationToken = default) =>
        Invoke(() => execution.ApplyAsync(planId, operationId, cancellationToken: cancellationToken));

    [McpServerTool(Name = "get_operation", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Inspect a recorded operation without moving icons. Status is running, applied, unchanged, rejected, partial, or unknown. Running records can survive an interrupted server: do not infer failure or retry with a new ID. Records and backup references persist across server restarts in this user's current Windows session.")]
    public Task<DesktopOperation> GetOperation(string operationId) =>
        Invoke(() => Task.FromResult(execution.GetOperation(operationId)));

    [McpServerTool(Name = "preview_restore_layout", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Preview restoration from a prior operation's backupId using a fresh snapshot. Does not move icons. Rejects changes made since the recorded result, missing identities and different geometry. Review this new plan before calling restore_layout. A partial result can be recovered; an uncertain call requires the previous Explorer process to end first.")]
    public Task<LayoutPlan> PreviewRestoreLayout(string backupId, string snapshotId, CancellationToken cancellationToken = default) =>
        Invoke(() => execution.PreviewRestoreAsync(backupId, snapshotId, cancellationToken));

    [McpServerTool(Name = "restore_layout", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Execute an exact reviewed plan from preview_restore_layout. Changes icon positions and creates a new backup/journal entry. This is a verified new operation, not a transactional rollback. Use one lowercase GUID operationId for this request and all retries; inspect partial/unknown outcomes instead of retrying blindly.")]
    public Task<DesktopOperation> RestoreLayout(string planId, string operationId, CancellationToken cancellationToken = default) =>
        Invoke(() => execution.ApplyAsync(planId, operationId, restoration: true, cancellationToken));

    private static async Task<T> Invoke<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DesktopException error) { throw new McpException($"{error.Code}: {error.Message}"); }
    }
}
