using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeskOrganizer;
using DeskOrganizer.Mcp;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;

if (args is ["--fixture-worker", var workerMode, var workerFlag])
{
    string input = await WorkerProtocol.ReadBoundedAsync(Console.In, CancellationToken.None);
    if (workerMode == "hang") { await Task.Delay(Timeout.Infinite); return 0; }
    if (workerMode == "crash") { Console.Error.Write("private-path-must-not-leak"); Environment.Exit(17); }
    var request = JsonSerializer.Deserialize<WorkerRequest>(input, WorkerProtocol.Json)!;
    var reply = request.Action == "capture" ? new WorkerReply(Observation: new ExecutionFixture().Observation)
        : new WorkerReply(Mutation: new DesktopMutationResult(true));
    Console.Write(JsonSerializer.Serialize(reply, WorkerProtocol.Json));
    return 0;
}
if (args is ["--job-parent-fixture"])
{
    var job = WorkerJob.Create();
    using var child = Process.Start(new ProcessStartInfo("dotnet")
    {
        ArgumentList = { Assembly.GetExecutingAssembly().Location, "--fixture-worker", "hang", "--desktop-worker" },
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
    })!;
    job.Attach(child);
    Console.WriteLine(child.Id);
    Console.Out.Flush();
    GC.KeepAlive(job);
    Environment.Exit(0); // Exercise OS cleanup, deliberately bypassing Dispose.
    return 0;
}
if (args is ["--serve-write-fixture"])
{
    using var host = McpHost.Build(new ExecutionFixture(), enableWrites: true, new FileOperationStore(ExecutionChecks.DataDirectory()));
    await host.RunAsync();
    return 0;
}

if (args is ["--serve-fixture"] or ["--serve-error"])
{
    using var host = McpHost.Build(new FixtureBackend { ThrowPrivateError = args[0] == "--serve-error" });
    await host.RunAsync();
    return 0;
}

if (args is ["--live-readonly"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet", Arguments = new[] { typeof(DesktopTools).Assembly.Location },
        WorkingDirectory = Path.GetTempPath(), ShutdownTimeout = TimeSpan.FromSeconds(3)
    }), cancellationToken: timeout.Token);
    var before = Data(await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token));
    var preview = Data(await client.CallToolAsync("preview_layout", new Dictionary<string, object?>
    {
        ["snapshotId"] = before.GetProperty("snapshotId").GetString(), ["strategy"] = "usage-category"
    }, cancellationToken: timeout.Token));
    Check(preview.GetProperty("previewOnly").GetBoolean());
    var after = Data(await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token));
    static string[] Positions(JsonElement state) => state.GetProperty("icons").EnumerateArray()
        .Select(icon => icon.GetProperty("name").GetString() + ":" + icon.GetProperty("position").GetRawText())
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
    Check(Positions(before).SequenceEqual(Positions(after)), "Desktop changed during read-only MCP check.");
    var worker = new WorkerProcessBackend(new WorkerLaunch("dotnet", new[] { typeof(DesktopTools).Assembly.Location }));
    var observed = await worker.CaptureAsync(timeout.Token);
    var preflight = await worker.ApplyAsync(new DesktopMutation(observed,
        observed.Icons.Select(icon => new IconTarget(icon.IdentityKey ?? "", icon.Position)).ToArray()), timeout.Token);
    Check(!preflight.Submitted, "No-op native preflight must never submit a positioning call.");
    Check(DesktopPlanningService.Fingerprint(observed) == DesktopPlanningService.Fingerprint(await worker.CaptureAsync(timeout.Token)),
        "Desktop changed during native no-op preflight.");
    Console.WriteLine($"Live MCP read-only check passed: {before.GetProperty("icons").GetArrayLength()} icons; positions unchanged.");
    Console.WriteLine($"Native worker no-op preflight submitted no positioning call; rejection code: {preflight.ErrorCode ?? "none"}.");
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
static void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
static async Task Reject(Func<Task> action, string code)
{
    try { await action(); }
    catch (DesktopException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return; }
    throw new Exception($"Expected {code}");
}
static (FixtureBackend Backend, ManualClock Clock, DesktopPlanningService Service) Create()
{
    var backend = new FixtureBackend();
    var clock = new ManualClock();
    return (backend, clock, new DesktopPlanningService(backend, clock));
}
static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
static JsonElement Data(CallToolResult result)
{
    Check(result.IsError != true, Text(result));
    return JsonSerializer.SerializeToElement(result.StructuredContent);
}

Test("alphabetical plan preserves every observed slot", async () =>
{
    var (_, _, service) = Create();
    var state = await service.GetStateAsync();
    var plan = await service.PreviewAsync(state.SnapshotId, "name-ascending");
    Check(plan.Proposals.OrderBy(item => item.To.X).ThenBy(item => item.To.Y).Select(item => item.Name)
        .SequenceEqual(new[] { "Alpha", "Visual Studio Code", "Zeta", "Árbol" }));
    Check(plan.Proposals.Select(item => item.To).ToHashSet().SetEquals(state.Icons.Select(icon => icon.Position)));
    Check(plan.PreviewOnly && !state.Capabilities.CanApply && !state.Capabilities.CanRestore);
});
Test("custom groups swap selected icons and keep omitted icons fixed", async () =>
{
    var (_, _, service) = Create();
    var state = await service.GetStateAsync();
    var plan = await service.PreviewAsync(state.SnapshotId, "custom",
        new[] { new LayoutGroup("Trabajo", new[] { state.Icons[1].IconRef, state.Icons[0].IconRef }) });
    Check(plan.Proposals[0].To == state.Icons[1].Position && plan.Proposals[1].To == state.Icons[0].Position);
    Check(plan.Proposals.Skip(2).All(proposal => !proposal.Changed && proposal.Group is null));
    Check(plan.ChangedCount == 2);
});
Test("usage categories reuse the existing classifier", async () =>
{
    var (_, _, service) = Create();
    var state = await service.GetStateAsync();
    var plan = await service.PreviewAsync(state.SnapshotId, "usage-category");
    Check(plan.Proposals.Single(item => item.Name == "Visual Studio Code").Group == "development");
    Check(plan.Proposals.Where(item => item.Name != "Visual Studio Code").All(item => item.Group == "other"));
});
Test("unknown, duplicate and cross-snapshot references are rejected before querying", async () =>
{
    var (backend, _, service) = Create();
    var first = await service.GetStateAsync();
    var second = await service.GetStateAsync();
    int calls = backend.Calls;
    await Reject(() => service.PreviewAsync(first.SnapshotId, "custom",
        new[] { new LayoutGroup("Work", new[] { second.Icons[0].IconRef }) }), "UNKNOWN_ICON");
    await Reject(() => service.PreviewAsync(first.SnapshotId, "custom",
        new[] { new LayoutGroup("Work", new[] { first.Icons[0].IconRef, first.Icons[0].IconRef }) }), "DUPLICATE_ICON");
    Check(backend.Calls == calls);
});
Test("invalid groups and strategies are rejected", async () =>
{
    var (_, _, service) = Create();
    var state = await service.GetStateAsync();
    await Reject(() => service.PreviewAsync(state.SnapshotId, "other"), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(state.SnapshotId, "custom"), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(state.SnapshotId, "custom", new LayoutGroup[] { null! }), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(state.SnapshotId, "custom", new[] { new LayoutGroup("Empty", Array.Empty<string>()) }), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending", Array.Empty<LayoutGroup>()), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(state.SnapshotId, "custom", new[]
    {
        new LayoutGroup("Work", new[] { state.Icons[0].IconRef }),
        new LayoutGroup("work", new[] { state.Icons[1].IconRef })
    }), "INVALID_ARGUMENT");
});
Test("same-count icon replacement invalidates a snapshot", async () =>
{
    var (backend, _, service) = Create();
    var state = await service.GetStateAsync();
    var icons = backend.Observation.Icons.ToArray();
    icons[0] = icons[0] with { IdentityKey = "replacement" };
    backend.Observation = backend.Observation with { Icons = icons };
    await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending"), "STALE_SNAPSHOT");
});
Test("geometry, positions and Explorer generation invalidate a snapshot", async () =>
{
    foreach (string change in new[] { "geometry", "position", "view" })
    {
        var (backend, _, service) = Create();
        var state = await service.GetStateAsync();
        backend.Observation = change switch
        {
            "geometry" => backend.Observation with { Geometry = backend.Observation.Geometry with { WindowDpi = 144 } },
            "position" => backend.Observation with { Icons = backend.Observation.Icons.Select((icon, index) =>
                index == 0 ? icon with { Position = new DesktopPoint(300, 300) } : icon).ToArray() },
            _ => backend.Observation with { ViewToken = "new-explorer" }
        };
        await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending"), "STALE_SNAPSHOT");
    }
});
Test("Shell enumeration order is not icon identity", async () =>
{
    var (backend, _, service) = Create();
    var state = await service.GetStateAsync();
    backend.Observation = backend.Observation with { Icons = backend.Observation.Icons.Reverse().ToArray() };
    await service.PreviewAsync(state.SnapshotId, "name-ascending");
});
Test("expiration, eviction and invalid snapshot IDs are explicit", async () =>
{
    var (_, clock, service) = Create();
    var state = await service.GetStateAsync();
    clock.Now += DesktopPlanningService.SnapshotLifetime;
    await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending"), "EXPIRED_SNAPSHOT");
    await Reject(() => service.PreviewAsync("invalid", "name-ascending"), "INVALID_ARGUMENT");
    await Reject(() => service.PreviewAsync(Guid.NewGuid().ToString("N"), "name-ascending"), "UNKNOWN_SNAPSHOT");
    var oldest = await service.GetStateAsync();
    for (int index = 0; index < DesktopPlanningService.MaxSnapshots; index++)
    {
        clock.Now += TimeSpan.FromSeconds(1);
        await service.GetStateAsync();
    }
    await Reject(() => service.PreviewAsync(oldest.SnapshotId, "name-ascending"), "UNKNOWN_SNAPSHOT");
});
Test("snapshots that expire while reading are rejected", async () =>
{
    var (backend, clock, service) = Create();
    var state = await service.GetStateAsync();
    backend.OnCapture = () => clock.Now += DesktopPlanningService.SnapshotLifetime;
    await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending"), "EXPIRED_SNAPSHOT");
});
Test("cancellation does not turn a query into a valid preview", async () =>
{
    var (backend, _, service) = Create();
    var state = await service.GetStateAsync();
    using var cancellation = new CancellationTokenSource();
    backend.OnCapture = cancellation.Cancel;
    try { await service.PreviewAsync(state.SnapshotId, "name-ascending", cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Expected cancellation");
});
Test("overlapping slots are rejected and unresolved identities are disclosed", async () =>
{
    var (backend, _, service) = Create();
    backend.Observation = backend.Observation with { Icons = new[]
    {
        new ObservedDesktopIcon("Same", new DesktopPoint(10, 10), null),
        new ObservedDesktopIcon("Same", new DesktopPoint(10, 10), null)
    } };
    var state = await service.GetStateAsync();
    Check(state.Icons.All(icon => !icon.IdentityResolved));
    await Reject(() => service.PreviewAsync(state.SnapshotId, "name-ascending"), "AMBIGUOUS_LAYOUT");
});
Test("empty desktop and equal display names are supported", async () =>
{
    var (backend, _, service) = Create();
    backend.Observation = backend.Observation with { Icons = Array.Empty<ObservedDesktopIcon>() };
    var empty = await service.GetStateAsync();
    Check((await service.PreviewAsync(empty.SnapshotId, "name-ascending")).IconCount == 0);
    backend.Observation = backend.Observation with { Icons = new[]
    {
        new ObservedDesktopIcon("Same", new DesktopPoint(10, 10), "first"),
        new ObservedDesktopIcon("Same", new DesktopPoint(10, 100), "second")
    } };
    var duplicateNames = await service.GetStateAsync();
    Check(duplicateNames.Icons.Select(icon => icon.IconRef).Distinct().Count() == 2);
    Check((await service.PreviewAsync(duplicateNames.SnapshotId, "name-ascending")).ChangedCount == 0);
});
Test("oversized observations and groups are rejected", async () =>
{
    var (backend, _, service) = Create();
    var state = await service.GetStateAsync();
    await Reject(() => service.PreviewAsync(state.SnapshotId, "custom",
        Enumerable.Repeat(new LayoutGroup("Group", new[] { state.Icons[0].IconRef }), LayoutPlanner.MaxGroups + 1).ToArray()), "INVALID_ARGUMENT");
    backend.Observation = backend.Observation with { Icons = Enumerable.Repeat(backend.Observation.Icons[0], LayoutPlanner.MaxIcons + 1).ToArray() };
    await Reject(() => service.GetStateAsync(), "LIMIT_EXCEEDED");
});
Test("state JSON does not expose backend identity or paths", async () =>
{
    var (_, _, service) = Create();
    var state = await service.GetStateAsync();
    string json = JsonSerializer.Serialize(state);
    Check(!json.Contains("private-key") && !json.Contains("ViewToken") && !json.Contains("IdentityKey"));
    Check(state.Icons.All(icon => Guid.TryParseExact(icon.IconRef, "N", out _)));
});
Test("native memory sizes match SIZE_T", () =>
{
    foreach (string name in new[] { "ReadProcessMemory", "ReadProcessMemoryBytes", "WriteProcessMemory" })
    {
        var method = typeof(LegacyDesktopReader).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(method.GetParameters()[3].ParameterType == typeof(nuint));
        Check(method.GetParameters()[4].ParameterType == typeof(nuint).MakeByRefType());
    }
    if (IntPtr.Size == 8) Check(Marshal.SizeOf<LVITEM>() == 88);
    return Task.CompletedTask;
});
Test("geometry keeps the legacy CLI field names", () =>
{
    var observation = new FixtureBackend().Observation;
    var element = JsonSerializer.SerializeToElement(observation.Geometry);
    Check(element.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(
        new[] { "gridSpacing", "usableArea", "monitors", "windowDpi", "monitorDpi", "autoArrange" }));
    Check(element.GetProperty("usableArea").GetProperty("left").GetInt32() == 0);
    return Task.CompletedTask;
});
Test("MCP stdio discovery, structured results, custom groups and safe errors", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet", Arguments = new[] { Assembly.GetExecutingAssembly().Location, "--serve-fixture" },
        WorkingDirectory = Path.GetTempPath(), ShutdownTimeout = TimeSpan.FromSeconds(3)
    }), cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    Check(tools.Select(tool => tool.Name).ToHashSet().SetEquals(new[] { "get_desktop_state", "preview_layout" }));
    Check(tools.All(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true && tool.ProtocolTool.OutputSchema is not null));
    var state = Data(await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token));
    Check(state.GetProperty("capabilities").GetProperty("canApply").GetBoolean() == false);
    string snapshot = state.GetProperty("snapshotId").GetString()!;
    var icons = state.GetProperty("icons").EnumerateArray().ToArray();
    var result = await client.CallToolAsync("preview_layout", new Dictionary<string, object?>
    {
        ["snapshotId"] = snapshot, ["strategy"] = "custom",
        ["groups"] = new[] { new { name = "Trabajo y comunicación", iconRefs = new[]
            { icons[1].GetProperty("iconRef").GetString(), icons[0].GetProperty("iconRef").GetString() } } }
    }, cancellationToken: timeout.Token);
    Check(Data(result).GetProperty("changedCount").GetInt32() == 2);
    var spatial = Data(await client.CallToolAsync("preview_layout", new Dictionary<string, object?>
    {
        ["snapshotId"] = snapshot, ["strategy"] = "spatial-groups",
        ["groups"] = new[]
        {
            new { name = "Development", iconRefs = icons.Take(2).Select(icon => icon.GetProperty("iconRef").GetString()).ToArray() },
            new { name = "Other", iconRefs = icons.Skip(2).Select(icon => icon.GetProperty("iconRef").GetString()).ToArray() }
        }
    }, cancellationToken: timeout.Token));
    Check(spatial.GetProperty("previewOnly").GetBoolean() && spatial.GetProperty("proposals").GetArrayLength() == 4);
    var invalid = await client.CallToolAsync("preview_layout", new Dictionary<string, object?> { ["snapshotId"] = "invalid" }, cancellationToken: timeout.Token);
    Check(invalid.IsError == true && Text(invalid).Contains("INVALID_ARGUMENT"));
    var invalidGroup = await client.CallToolAsync("preview_layout", new Dictionary<string, object?>
    {
        ["snapshotId"] = snapshot, ["strategy"] = "custom", ["groups"] = new[] { new { name = "Work", iconRefs = new[] { "unknown" } } }
    }, cancellationToken: timeout.Token);
    Check(invalidGroup.IsError == true && Text(invalidGroup).Contains("UNKNOWN_ICON"));
});
Test("MCP hides unexpected backend exception text", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet", Arguments = new[] { Assembly.GetExecutingAssembly().Location, "--serve-error" },
        ShutdownTimeout = TimeSpan.FromSeconds(3)
    }), cancellationToken: timeout.Token);
    var result = await client.CallToolAsync("get_desktop_state", cancellationToken: timeout.Token);
    Check(result.IsError == true && !Text(result).Contains("private-key"));
});

ExecutionChecks.Register(Test);
SpatialChecks.Register(Test);

int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class FixtureBackend : IDesktopBackend
{
    public bool ThrowPrivateError { get; set; }
    public int Calls { get; private set; }
    public Action? OnCapture { get; set; }
    public DesktopObservation Observation { get; set; } = new("fixture-view", new[]
    {
        new ObservedDesktopIcon("Zeta", new DesktopPoint(10, 10), "private-key-1"),
        new ObservedDesktopIcon("Alpha", new DesktopPoint(10, 110), "private-key-2"),
        new ObservedDesktopIcon("Árbol", new DesktopPoint(110, 10), "private-key-3"),
        new ObservedDesktopIcon("Visual Studio Code", new DesktopPoint(110, 110), "private-key-4")
    }, new DesktopGeometryInfo(new DesktopSpacing(100, 100), new DesktopArea(0, 0, 1920, 1080),
        new[] { new DesktopMonitor(new DesktopArea(0, 0, 1920, 1080), new DesktopArea(0, 0, 1920, 1040), true) }, 96, null, false),
        PlacementArea: new DesktopArea(0, 0, 1920, 1040));

    public Task<DesktopObservation> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OnCapture?.Invoke();
        if (ThrowPrivateError) throw new InvalidOperationException("private-key-path-must-not-leak");
        return Task.FromResult(Observation);
    }
}
