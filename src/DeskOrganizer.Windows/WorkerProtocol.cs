using System.Text;
using System.Text.Json;

namespace DeskOrganizer;

public sealed record WorkerRequest(string Action, DesktopMutation? Mutation = null);
public sealed record WorkerReply(DesktopObservation? Observation = null, DesktopMutationResult? Mutation = null, string? ErrorCode = null);

public static class WorkerProtocol
{
    public const int MaximumCharacters = 32 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        char[] buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + count > MaximumCharacters)
                throw new DesktopException("WORKER_PROTOCOL_ERROR", "Worker data exceeds the supported protocol limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

/// <summary>Internal child entry point, reached before any MCP host is created.</summary>
public static class DesktopWorker
{
    public static async Task<int> RunAsync(bool allowMutation)
    {
        try
        {
            string input = await WorkerProtocol.ReadBoundedAsync(Console.In, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(input)) return 0;
            var request = JsonSerializer.Deserialize<WorkerRequest>(input, WorkerProtocol.Json);
            WorkerReply response;
            if (request is { Action: "capture", Mutation: null })
                response = new WorkerReply(Observation: await new WindowsDesktopBackend().CaptureAsync());
            else if (allowMutation && request is { Action: "apply", Mutation: not null })
                response = new WorkerReply(Mutation: ShellDesktopMover.MoveBatch(request.Mutation));
            else response = new WorkerReply(ErrorCode: "WORKER_INVALID_REQUEST");
            await Console.Out.WriteAsync(JsonSerializer.Serialize(response, WorkerProtocol.Json));
            return 0;
        }
        catch (DesktopException error)
        {
            await Console.Out.WriteAsync(JsonSerializer.Serialize(new WorkerReply(ErrorCode: error.Code), WorkerProtocol.Json));
            return 0;
        }
        catch (Exception)
        {
            await Console.Out.WriteAsync(JsonSerializer.Serialize(new WorkerReply(ErrorCode: "WORKER_FAILED"), WorkerProtocol.Json));
            return 0;
        }
    }
}
