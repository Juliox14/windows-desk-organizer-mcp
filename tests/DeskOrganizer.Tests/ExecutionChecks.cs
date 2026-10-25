using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DeskOrganizer;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

static class ExecutionChecks
{
    internal static string DataDirectory() => Path.Combine(AppContext.BaseDirectory, "journal-tests", Guid.NewGuid().ToString("N"));
    private static void Check(bool value, string message = "Assertion failed")
    { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string code)
    {
        try { await action(); }
        catch (DesktopException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return; }
        throw new Exception($"Expected {code}");
    }
    private static (ExecutionFixture Backend, ManualClock Clock, DesktopPlanningService Planning, DesktopExecutionService Execution,
        FileOperationStore Store) Create(bool enabled = true)
    {
        var backend = new ExecutionFixture();
        var clock = new ManualClock();
        var store = new FileOperationStore(DataDirectory());
        var planning = new DesktopPlanningService(backend, clock, new DesktopAccessPolicy(enabled));
        return (backend, clock, planning, new DesktopExecutionService(planning, backend, store, clock), store);
    }
    private static async Task<LayoutPlan> Plan(DesktopPlanningService planning)
    {
        var state = await planning.GetStateAsync();
        return await planning.PreviewAsync(state.SnapshotId, "name-ascending");
    }
    private static JsonElement Data(CallToolResult result)
    {
        Check(result.IsError != true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("batch execution handles swaps and verifies the full desktop", async () =>
        {
            var context = Create();
            var plan = await Plan(context.Planning);
            Check(!plan.PreviewOnly);
            var result = await context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"));
            Check(result.Status == "applied" && result.BackupId == result.OperationId && !result.RecoveryRequired);
            Check(context.Backend.ApplyCalls == 1);
            Check(context.Store.Read(result.OperationId)!.Before.Icons[0].Name == "Zeta");
        });
        test("custom three-icon cycles use a single backend batch", async () =>
        {
            var context = Create();
            var state = await context.Planning.GetStateAsync();
            var plan = await context.Planning.PreviewAsync(state.SnapshotId, "custom", new[]
            {
                new LayoutGroup("Cycle", new[] { state.Icons[1].IconRef, state.Icons[2].IconRef, state.Icons[0].IconRef })
            });
            var result = await context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"));
            Check(result.Status == "applied" && context.Backend.ApplyCalls == 1 && plan.ChangedCount == 3);
            Check(context.Backend.Observation.Icons[3].Position == state.Icons[3].Position);
        });
        test("operation replay survives plan eviction and server restart", async () =>
        {
            var context = Create();
            var plan = await Plan(context.Planning);
            string id = Guid.NewGuid().ToString("N");
            var first = await context.Execution.ApplyAsync(plan.PlanId, id);
            var freshPlanning = new DesktopPlanningService(context.Backend, context.Clock, new DesktopAccessPolicy(true));
            var restarted = new DesktopExecutionService(freshPlanning, context.Backend, context.Store, context.Clock);
            Check(await restarted.ApplyAsync(plan.PlanId, id) == first && context.Backend.ApplyCalls == 1);
            await Reject(() => restarted.ApplyAsync("other-plan", id), "OPERATION_CONFLICT");
        });
        test("read-only policy, stale plans and expired plans never dispatch", async () =>
        {
            var readOnly = Create(enabled: false);
            var plan = await Plan(readOnly.Planning);
            await Reject(() => readOnly.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N")), "WRITES_DISABLED");
            Check(readOnly.Backend.ApplyCalls == 0);
            var context = Create();
            plan = await Plan(context.Planning);
            context.Backend.Observation = context.Backend.Observation with { ViewToken = "replacement-view" };
            await Reject(() => context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N")), "STALE_PLAN");
            context.Clock.Now += DesktopPlanningService.SnapshotLifetime;
            await Reject(() => context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N")), "EXPIRED_PLAN");
            Check(context.Backend.ApplyCalls == 0);
        });
        test("auto-arrange, ambiguous identity, topology and out-of-bounds targets are rejected", async () =>
        {
            foreach (string kind in new[] { "auto", "identity", "dpi", "destination" })
            {
                var context = Create();
                context.Backend.Observation = kind switch
                {
                    "auto" => context.Backend.Observation with { Geometry = context.Backend.Observation.Geometry with { AutoArrange = true } },
                    "identity" => context.Backend.Observation with { Icons = context.Backend.Observation.Icons.Select((icon, index) => index == 0 ? icon with { IdentityKey = null } : icon).ToArray() },
                    "dpi" => context.Backend.Observation with { DpiAligned = false },
                    _ => context.Backend.Observation with { Geometry = context.Backend.Observation.Geometry with { UsableArea = new DesktopArea(0, 0, 50, 50) } }
                };
                var plan = await Plan(context.Planning);
                Check(plan.PreviewOnly);
                await Reject(() => context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N")), kind switch
                { "auto" => "AUTO_ARRANGE_ENABLED", "identity" => "UNRESOLVED_IDENTITY", "dpi" => "UNSUPPORTED_GEOMETRY", _ => "INVALID_DESTINATION" });
                Check(context.Backend.ApplyCalls == 0);
            }
        });
        test("scaled single-monitor geometry requires Explorer DPI alignment", async () =>
        {
            foreach (uint dpi in new uint[] { 96, 120, 144 })
            {
                var context = Create();
                context.Backend.Observation = context.Backend.Observation with
                { Geometry = context.Backend.Observation.Geometry with { WindowDpi = dpi } };
                var plan = await Plan(context.Planning);
                Check(!plan.PreviewOnly);
                Check((await context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"))).Status == "applied");
            }
        });
        test("backup restoration is previewed, verified and separately deduplicated", async () =>
        {
            var context = Create();
            string before = DesktopPlanningService.Fingerprint(context.Backend.Observation);
            var plan = await Plan(context.Planning);
            var applied = await context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"));
            var state = await context.Planning.GetStateAsync();
            // Shell order changes do not change the mapping between snapshot references and backup identities.
            context.Backend.Observation = context.Backend.Observation with { Icons = context.Backend.Observation.Icons.Reverse().ToArray() };
            var restore = await context.Execution.PreviewRestoreAsync(applied.BackupId!, state.SnapshotId);
            await Reject(() => context.Execution.ApplyAsync(restore.PlanId, Guid.NewGuid().ToString("N")), "INVALID_PLAN");
            string id = Guid.NewGuid().ToString("N");
            var result = await context.Execution.ApplyAsync(restore.PlanId, id, restoration: true);
            Check(result.Status == "applied" && result.RestoreOf == applied.OperationId);
            Check(DesktopPlanningService.Fingerprint(context.Backend.Observation) == before);
            await context.Execution.ApplyAsync(restore.PlanId, id, restoration: true);
            Check(context.Backend.ApplyCalls == 2);
        });
        test("restoration rejects user edits and missing icons", async () =>
        {
            foreach (string change in new[] { "position", "removed", "geometry" })
            {
                var context = Create();
                var applied = await context.Execution.ApplyAsync((await Plan(context.Planning)).PlanId, Guid.NewGuid().ToString("N"));
                context.Backend.Observation = change switch
                {
                    "position" => context.Backend.Observation with
                    {
                        Icons = context.Backend.Observation.Icons.Select((icon, index) =>
                        index == 0 ? icon with { Position = new DesktopPoint(500, 500) } : icon).ToArray()
                    },
                    "removed" => context.Backend.Observation with { Icons = context.Backend.Observation.Icons.Skip(1).ToArray() },
                    _ => context.Backend.Observation with { Geometry = context.Backend.Observation.Geometry with { WindowDpi = 120 } }
                };
                var state = await context.Planning.GetStateAsync();
                await Reject(() => context.Execution.PreviewRestoreAsync(applied.BackupId!, state.SnapshotId), "RESTORE_CONFLICT");
            }
        });
        test("partial results block new organization and allow a validated restoration", async () =>
        {
            var context = Create();
            var before = context.Backend.Observation;
            context.Backend.Mode = "partial";
            var result = await context.Execution.ApplyAsync((await Plan(context.Planning)).PlanId, Guid.NewGuid().ToString("N"));
            Check(result.Status == "partial" && result.RecoveryRequired);
            var state = await context.Planning.GetStateAsync();
            var restore = await context.Execution.PreviewRestoreAsync(result.BackupId!, state.SnapshotId);
            context.Backend.Mode = "normal";
            var restored = await context.Execution.ApplyAsync(restore.PlanId, Guid.NewGuid().ToString("N"), restoration: true);
            Check(restored.Status == "applied" && !context.Store.Read(result.OperationId)!.Result.RecoveryRequired);
            Check(DesktopPlanningService.Fingerprint(before) == DesktopPlanningService.Fingerprint(context.Backend.Observation));
        });
        test("unknown outcomes quarantine the Explorer view and do not retry", async () =>
        {
            var context = Create();
            context.Backend.Mode = "unknown";
            string id = Guid.NewGuid().ToString("N");
            var plan = await Plan(context.Planning);
            var result = await context.Execution.ApplyAsync(plan.PlanId, id);
            Check(result.Status == "unknown" && result.RecoveryRequired);
            await context.Execution.ApplyAsync(plan.PlanId, id);
            var next = await Plan(context.Planning);
            await Reject(() => context.Execution.ApplyAsync(next.PlanId, Guid.NewGuid().ToString("N")), "RECOVERY_REQUIRED");
            Check(context.Backend.ApplyCalls == 1);
            context.Backend.Observation = context.Backend.Observation with { ViewToken = "new-explorer-view" };
            next = await Plan(context.Planning);
            await Reject(() => context.Execution.ApplyAsync(next.PlanId, Guid.NewGuid().ToString("N")), "RECOVERY_REQUIRED");
            context.Backend.Observation = context.Backend.Observation with { ExplorerInstance = "replacement-explorer-process" };
            next = await Plan(context.Planning);
            var unchanged = await context.Execution.ApplyAsync(next.PlanId, Guid.NewGuid().ToString("N"));
            Check(unchanged.Status == "unchanged" && context.Backend.ApplyCalls == 1);
            Check(!context.Store.Read(id)!.Result.RecoveryRequired);
        });
        test("pre-dispatch rejection and no-op plans are definite outcomes", async () =>
        {
            var context = Create();
            context.Backend.Mode = "reject";
            var result = await context.Execution.ApplyAsync((await Plan(context.Planning)).PlanId, Guid.NewGuid().ToString("N"));
            Check(result.Status == "rejected" && !result.RecoveryRequired);
            var state = await context.Planning.GetStateAsync();
            var plan = await context.Planning.PreviewAsync(state.SnapshotId, "custom", new[] { new LayoutGroup("Fixed", new[] { state.Icons[0].IconRef }) });
            var unchanged = await context.Execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"));
            Check(unchanged.Status == "unchanged" && unchanged.BackupId is null && context.Backend.ApplyCalls == 1);
        });
        test("cancellation after dispatch still records the final result", async () =>
        {
            var context = Create();
            using var cancellation = new CancellationTokenSource();
            context.Backend.OnApply = cancellation.Cancel;
            var result = await context.Execution.ApplyAsync((await Plan(context.Planning)).PlanId,
                Guid.NewGuid().ToString("N"), cancellationToken: cancellation.Token);
            Check(result.Status == "applied" && context.Store.Read(result.OperationId)!.Result.Status == "applied");
        });
        test("journal failure before dispatch prevents movement", async () =>
        {
            var context = Create();
            var fault = new FaultStore(context.Store) { FailSaveNumber = 1 };
            var engine = new DesktopExecutionService(context.Planning, context.Backend, fault, context.Clock);
            var plan = await Plan(context.Planning);
            await Reject(() => engine.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N")), "JOURNAL_UNAVAILABLE");
            Check(context.Backend.ApplyCalls == 0);
        });
        test("missing durable completion becomes unknown and never repeats native dispatch", async () =>
        {
            var context = Create();
            var fault = new FaultStore(context.Store) { FailSaveNumber = 2 };
            var engine = new DesktopExecutionService(context.Planning, context.Backend, fault, context.Clock);
            var plan = await Plan(context.Planning);
            string id = Guid.NewGuid().ToString("N");
            await Reject(() => engine.ApplyAsync(plan.PlanId, id), "JOURNAL_UNAVAILABLE");
            Check(engine.GetOperation(id).Status == "unknown");
            Check((await context.Execution.ApplyAsync(plan.PlanId, id)).Status == "unknown");
            Check(context.Backend.ApplyCalls == 1);
        });
        test("exclusive leases coordinate concurrent operation requests", async () =>
        {
            var context = Create();
            var plan = await Plan(context.Planning);
            string id = Guid.NewGuid().ToString("N");
            var results = await Task.WhenAll(context.Execution.ApplyAsync(plan.PlanId, id), context.Execution.ApplyAsync(plan.PlanId, id));
            Check(results[0] == results[1] && context.Backend.ApplyCalls == 1);
        });
        test("journal corruption and path traversal fail closed", async () =>
        {
            string directory = DataDirectory();
            Directory.CreateDirectory(directory);
            string id = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, id + ".json"), "{invalid}");
            var store = new FileOperationStore(directory);
            await Reject(() => Task.FromResult(store.Read(id)), "JOURNAL_CORRUPT");
            await Reject(() => Task.FromResult(store.Read("../escape")), "INVALID_ARGUMENT");
        });
        test("process backend exchanges bounded private protocol without native calls", async () =>
        {
            var worker = new WorkerProcessBackend(new WorkerLaunch("dotnet",
                new[] { Assembly.GetExecutingAssembly().Location, "--fixture-worker", "normal" }));
            var observation = await worker.CaptureAsync();
            Check(observation.Icons.Count == 4);
            var targets = observation.Icons.Select(icon => new IconTarget(icon.IdentityKey!, icon.Position)).ToArray();
            Check((await worker.ApplyAsync(new DesktopMutation(observation, targets))).Submitted);
        });
        test("worker timeout kills the disposable process and returns an uncertain failure", async () =>
        {
            var worker = new WorkerProcessBackend(new WorkerLaunch("dotnet",
                new[] { Assembly.GetExecutingAssembly().Location, "--fixture-worker", "hang" }), TimeSpan.FromSeconds(2));
            await Reject(() => worker.CaptureAsync(), "WORKER_TIMEOUT");
        });
        test("worker startup failure is a definite pre-dispatch rejection", async () =>
        {
            var fixture = new ExecutionFixture();
            var worker = new WorkerProcessBackend(new WorkerLaunch("missing-DeskOrganizer-test-executable", Array.Empty<string>()));
            var result = await worker.ApplyAsync(new DesktopMutation(fixture.Observation,
                fixture.Observation.Icons.Select(icon => new IconTarget(icon.IdentityKey!, icon.Position)).ToArray()));
            Check(!result.Submitted && !result.OutcomeUncertain && result.ErrorCode == "WORKER_NOT_STARTED");
        });
        test("worker crash after dispatch hides private stderr and reports uncertainty", async () =>
        {
            var fixture = new ExecutionFixture();
            var worker = new WorkerProcessBackend(new WorkerLaunch("dotnet",
                new[] { Assembly.GetExecutingAssembly().Location, "--fixture-worker", "crash" }));
            try
            {
                await worker.ApplyAsync(new DesktopMutation(fixture.Observation,
                    fixture.Observation.Icons.Select(icon => new IconTarget(icon.IdentityKey!, icon.Position)).ToArray()));
            }
            catch (DesktopException error)
            {
                Check(error.Code == "WORKER_FAILED" && !error.Message.Contains("private-path"));
                return;
            }
            throw new Exception("Expected worker failure after dispatch");
        });
        test("Windows job closes orphan child workers when their parent exits", async () =>
        {
            using var parent = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { Assembly.GetExecutingAssembly().Location, "--job-parent-fixture" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string output = await parent.StandardOutput.ReadToEndAsync(timeout.Token);
            await parent.WaitForExitAsync(timeout.Token);
            Check(parent.ExitCode == 0 && int.TryParse(output.Trim(), out _));
            int childId = int.Parse(output.Trim());
            await Task.Delay(150);
            try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "Orphan worker survived parent exit"); }
            catch (ArgumentException) { }
        });
        test("MCP write workflow applies and restores only synthetic icons", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = new[] { Assembly.GetExecutingAssembly().Location, "--serve-write-fixture" },
                ShutdownTimeout = TimeSpan.FromSeconds(3)
            }), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Check(tools.Count == 6 && tools.Single(tool => tool.Name == "apply_layout").ProtocolTool.Annotations?.ReadOnlyHint == false);
            var state = Data(await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token));
            var icons = state.GetProperty("icons").EnumerateArray().ToArray();
            var plan = Data(await client.CallToolAsync("preview_layout", new Dictionary<string, object?>
            {
                ["snapshotId"] = state.GetProperty("snapshotId").GetString(), ["strategy"] = "spatial-groups",
                ["groups"] = new[]
                {
                    new { name = "A", iconRefs = icons.Take(2).Select(icon => icon.GetProperty("iconRef").GetString()).ToArray() },
                    new { name = "B", iconRefs = icons.Skip(2).Select(icon => icon.GetProperty("iconRef").GetString()).ToArray() }
                }
            }, cancellationToken: timeout.Token));
            string operationId = Guid.NewGuid().ToString("N");
            var arguments = new Dictionary<string, object?> { ["planId"] = plan.GetProperty("planId").GetString(), ["operationId"] = operationId };
            var applied = Data(await client.CallToolAsync("apply_layout", arguments, cancellationToken: timeout.Token));
            Check(applied.GetProperty("status").GetString() == "applied");
            Data(await client.CallToolAsync("apply_layout", arguments, cancellationToken: timeout.Token));
            Data(await client.CallToolAsync("get_operation", new Dictionary<string, object?> { ["operationId"] = operationId }, cancellationToken: timeout.Token));
            state = Data(await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token));
            var restore = Data(await client.CallToolAsync("preview_restore_layout", new Dictionary<string, object?>
            {
                ["backupId"] = applied.GetProperty("backupId").GetString(),
                ["snapshotId"] = state.GetProperty("snapshotId").GetString()
            }, cancellationToken: timeout.Token));
            var result = Data(await client.CallToolAsync("restore_layout", new Dictionary<string, object?>
            {
                ["planId"] = restore.GetProperty("planId").GetString(),
                ["operationId"] = Guid.NewGuid().ToString("N")
            }, cancellationToken: timeout.Token));
            Check(result.GetProperty("status").GetString() == "applied");
        });
    }
}

sealed class FaultStore(IOperationStore inner) : IOperationStore
{
    public int FailSaveNumber { get; set; }
    private int saves;
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) => inner.AcquireAsync(cancellationToken);
    public OperationRecord? Read(string id) => inner.Read(id);
    public IReadOnlyList<OperationRecord> ReadAll() => inner.ReadAll();
    public bool IsWriterActive() => inner.IsWriterActive();
    public void Save(OperationRecord record)
    {
        if (++saves == FailSaveNumber) throw new DesktopException("JOURNAL_UNAVAILABLE", "Simulated journal fault.");
        inner.Save(record);
    }
}

sealed class ExecutionFixture : IDesktopExecutionBackend
{
    public DesktopObservation Observation { get; set; } = new FixtureBackend().Observation with
    { DpiAligned = true, ExplorerInstance = "fixture-explorer-process" };
    public string Mode { get; set; } = "normal";
    public int ApplyCalls { get; private set; }
    public Action? OnApply { get; set; }
    public Task<DesktopObservation> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Observation);
    }
    public Task<DesktopMutationResult> ApplyAsync(DesktopMutation mutation, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        OnApply?.Invoke();
        if (Mode == "reject") return Task.FromResult(new DesktopMutationResult(false, "STALE_PLAN"));
        var expected = MutationValidator.ExpectedAfter(mutation);
        Observation = Mode == "partial" ? expected with { Icons = expected.Icons.Select((icon, index) => index == 0 ? icon : Observation.Icons[index]).ToArray() } : expected;
        if (Mode == "unknown") throw new TimeoutException();
        return Task.FromResult(new DesktopMutationResult(true));
    }
}
