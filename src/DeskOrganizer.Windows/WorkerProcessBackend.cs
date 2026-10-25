using System.Diagnostics;
using System.Text.Json;

namespace DeskOrganizer;

public sealed record WorkerLaunch(string Executable, IReadOnlyList<string> PrefixArguments);

public sealed class WorkerProcessBackend(WorkerLaunch launch, TimeSpan? timeout = null) : IDesktopExecutionBackend
{
    private readonly SemaphoreSlim workerGate = new(1, 1);
    public async Task<DesktopObservation> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var response = await RunAsync(new WorkerRequest("capture"), cancellationToken);
        return response.Observation ?? throw new DesktopException(response.ErrorCode ?? "WORKER_FAILED", "The worker could not capture the desktop.");
    }

    public async Task<DesktopMutationResult> ApplyAsync(DesktopMutation mutation, CancellationToken cancellationToken = default)
    {
        WorkerReply response;
        try { response = await RunAsync(new WorkerRequest("apply", mutation), cancellationToken); }
        catch (DesktopException error) when (error.Code == "WORKER_UNAVAILABLE")
        { return new DesktopMutationResult(false, "WORKER_NOT_STARTED"); }
        // A protocol-level failure has no proof of non-submission. The caller must record an unknown outcome.
        return response.Mutation ?? throw new DesktopException(response.ErrorCode ?? "WORKER_FAILED", "The mutation worker did not report a definite submission result.");
    }

    private async Task<WorkerReply> RunAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        await workerGate.WaitAsync(cancellationToken);
        try { return await RunIsolatedAsync(request, cancellationToken); }
        finally { workerGate.Release(); }
    }

    private async Task<WorkerReply> RunIsolatedAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(launch.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        foreach (string argument in launch.PrefixArguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add(request.Action == "apply" ? "--desktop-worker-write" : "--desktop-worker");
        using var job = WorkerJob.Create();
        bool requestReleased = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
            // The child does nothing before stdin reaches EOF. Attach it to the lifetime job before sending work.
            job.Attach(process);
            Task<string> output = WorkerProtocol.ReadBoundedAsync(process.StandardOutput, deadline.Token);
            Task<string> errors = WorkerProtocol.ReadBoundedAsync(process.StandardError, deadline.Token);
            string input = JsonSerializer.Serialize(request, WorkerProtocol.Json);
            if (input.Length > WorkerProtocol.MaximumCharacters)
                throw new DesktopException("WORKER_PROTOCOL_ERROR", "Worker request exceeds the protocol limit.");
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            requestReleased = true;
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            string json = await output;
            await errors; // Drain stderr without returning native exception details or private paths.
            if (process.ExitCode != 0) throw new InvalidOperationException();
            return JsonSerializer.Deserialize<WorkerReply>(json, WorkerProtocol.Json) ?? throw new JsonException();
        }
        catch (Exception) when (request.Action == "apply" && !requestReleased && !cancellationToken.IsCancellationRequested)
        { return new WorkerReply(Mutation: new DesktopMutationResult(false, "WORKER_NOT_STARTED")); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DesktopException("WORKER_TIMEOUT", "The desktop worker timed out. A submitted movement may have an uncertain result."); }
        catch (DesktopException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        { throw new DesktopException("WORKER_FAILED", "The desktop worker failed. Inspect the operation record before retrying a movement."); }
        finally
        {
            deadline.Cancel();
            // Disposing the job also kills children if this process crashes. It cannot undo a call already delivered to Explorer.
            job.Dispose();
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
