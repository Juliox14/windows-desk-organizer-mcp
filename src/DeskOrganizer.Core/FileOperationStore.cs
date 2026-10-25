using System.Text.Json;

namespace DeskOrganizer;

/// <summary>Atomic per-user journal and an OS-owned cross-process lease, released on process exit.</summary>
public sealed class FileOperationStore(string directory) : IOperationStore
{
    public const int MaxRecords = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        try { Directory.CreateDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new DesktopException("JOURNAL_UNAVAILABLE", "Could not open the local operation journal."); }
        long started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 10_000)
            { await Task.Delay(100, cancellationToken); }
            catch (IOException) { throw new DesktopException("DESKTOP_BUSY", "Another organizer process is writing or recovering an operation."); }
            catch (UnauthorizedAccessException) { throw new DesktopException("JOURNAL_UNAVAILABLE", "Could not acquire the local writer lease."); }
        }
    }

    public OperationRecord? Read(string operationId)
    {
        ValidateId(operationId);
        string path = Path.Combine(directory, operationId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 32 * 1024 * 1024) throw new JsonException();
            var record = JsonSerializer.Deserialize<OperationRecord>(stream, Json);
            if (record is null || record.Version != 1 || record.Result is null || record.Result.OperationId != operationId
                || !ValidObservation(record.Before) || !ValidObservation(record.ExpectedAfter)
                || record.ObservedAfter is not null && !ValidObservation(record.ObservedAfter)
                || record.Result.Status is not ("running" or "applied" or "unchanged" or "rejected" or "partial" or "unknown")
                || string.IsNullOrEmpty(record.RequestKey))
                throw new JsonException();
            return record;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new DesktopException("JOURNAL_CORRUPT", "An operation record cannot be read. Preserve the journal and resolve it before writing."); }
    }

    public IReadOnlyList<OperationRecord> ReadAll()
    {
        if (!Directory.Exists(directory)) return Array.Empty<OperationRecord>();
        try
        {
            var files = Directory.GetFiles(directory, "*.json");
            if (files.Length > MaxRecords) throw new DesktopException("HISTORY_LIMIT", "The operation history needs maintenance before further writes.");
            return files.Select(path => Read(Path.GetFileNameWithoutExtension(path))
                ?? throw new DesktopException("JOURNAL_CORRUPT", "An operation record disappeared during the read.")).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new DesktopException("JOURNAL_UNAVAILABLE", "Could not read the local operation history."); }
    }

    public void Save(OperationRecord record)
    {
        ValidateId(record.Result.OperationId);
        string destination = Path.Combine(directory, record.Result.OperationId + ".json");
        string temporary = Path.Combine(directory, record.Result.OperationId + ".pending");
        try
        {
            // Callers hold the lease. A record is durable before native dispatch; incomplete temp files never replace it.
            if (!File.Exists(destination) && Directory.GetFiles(directory, "*.json").Length >= MaxRecords)
                throw new DesktopException("HISTORY_LIMIT", "The operation history is full. Archive terminal records before further writes.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new DesktopException("JOURNAL_UNAVAILABLE", "Could not persist the operation. Do not repeat a movement without checking its operation ID."); }
    }

    public static void ValidateId(string? id)
    {
        if (id is null || !Guid.TryParseExact(id, "N", out Guid value) || id != value.ToString("N"))
            throw new DesktopException("INVALID_ARGUMENT", "Operation and backup IDs must be 32 lowercase hexadecimal characters without separators.");
    }

    private static bool ValidObservation(DesktopObservation? observation) =>
        observation is not null && !string.IsNullOrEmpty(observation.ViewToken)
        && observation.Icons is not null && observation.Icons.Count <= LayoutPlanner.MaxIcons
        && observation.Icons.All(icon => icon is not null && icon.Name is not null
            && icon.Name.Length <= 1024 && icon.Position is not null)
        && observation.Geometry is not null && observation.Geometry.UsableArea is not null
        && observation.Geometry.Monitors is not null && observation.Geometry.Monitors.Count <= 64
        && observation.Geometry.Monitors.All(monitor => monitor is not null && monitor.Bounds is not null && monitor.WorkArea is not null);

    public bool IsWriterActive()
    {
        string path = Path.Combine(directory, "writer.lock");
        if (!File.Exists(path)) return false;
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { throw new DesktopException("JOURNAL_UNAVAILABLE", "Could not inspect the writer lease."); }
    }
}
