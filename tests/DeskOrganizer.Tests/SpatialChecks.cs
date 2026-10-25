using System.Text.Json;
using DeskOrganizer;

static class SpatialChecks
{
    private static void Check(bool value, string message = "Assertion failed")
    { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string code)
    {
        try { await action(); }
        catch (DesktopException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return; }
        throw new Exception($"Expected {code}");
    }
    private static LayoutGroup[] Groups(DesktopState state, params int[] counts)
    {
        int offset = 0;
        return counts.Select((count, index) =>
        {
            var refs = state.Icons.Skip(offset).Take(count).Select(icon => icon.IconRef).ToArray();
            offset += count;
            return new LayoutGroup($"Group {index}", refs);
        }).ToArray();
    }
    private static ExecutionFixture Fixture(int count = 55, uint dpi = 120, int left = 0, int top = 0)
    {
        var backend = new ExecutionFixture();
        var area = new DesktopArea(left, top, left + 1920, top + 1040);
        backend.Observation = backend.Observation with
        {
            Icons = Enumerable.Range(0, count).Select(index => new ObservedDesktopIcon(
                index < 2 ? "Same display name" : $"Unknown {index}",
                new DesktopPoint(left + 17 + index / 8 * 95, top + 2 + index % 8 * 130), $"private-{index}")).ToArray(),
            Geometry = backend.Observation.Geometry with
            {
                GridSpacing = new DesktopSpacing(95, 130), WindowDpi = dpi,
                UsableArea = new DesktopArea(left, top, left + 1920, top + 1080)
            },
            PlacementArea = area
        };
        return backend;
    }
    private static void Separators(LayoutPlan plan, DesktopSpacing grid)
    {
        // Every pair of groups must have at least one whole empty cell between their bounding rectangles.
        var bounds = plan.Proposals.GroupBy(item => item.Group).Select(group => new
        {
            Left = group.Min(item => item.To.X), Right = group.Max(item => item.To.X),
            Top = group.Min(item => item.To.Y), Bottom = group.Max(item => item.To.Y)
        }).ToArray();
        for (int i = 0; i < bounds.Length; i++)
            for (int j = i + 1; j < bounds.Length; j++)
                Check(bounds[i].Right + 2 * grid.X <= bounds[j].Left || bounds[j].Right + 2 * grid.X <= bounds[i].Left
                    || bounds[i].Bottom + 2 * grid.Y <= bounds[j].Top || bounds[j].Bottom + 2 * grid.Y <= bounds[i].Top,
                    "Groups lack an empty grid separator");
    }

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("spatial groups separate 55 icons, preserve explicit order and stay in the work area at varied DPI", async () =>
        {
            foreach (uint dpi in new uint[] { 96, 120, 144, 192 })
            {
                var backend = Fixture(dpi: dpi, left: 30, top: 40);
                var planning = new DesktopPlanningService(backend, new ManualClock());
                var state = await planning.GetStateAsync();
                var groups = Groups(state, 2, 3, 4, 17, 3, 3, 12, 11);
                var plan = await planning.PreviewAsync(state.SnapshotId, "spatial-groups", groups);
                Check(plan.Proposals.Count == 55 && plan.Proposals.Select(item => item.To).Distinct().Count() == 55);
                var area = backend.Observation.PlacementArea!;
                Check(plan.Proposals.All(item => area.Contains(item.To)
                    && item.To.X + 95 <= area.Right && item.To.Y + 130 <= area.Bottom));
                foreach (var group in groups)
                    Check(plan.Proposals.Where(item => item.Group == group.Name).OrderBy(item => item.To.X).ThenBy(item => item.To.Y)
                        .Select(item => item.IconRef).SequenceEqual(group.IconRefs));
                Separators(plan, state.Geometry.GridSpacing!);
                Check(plan.PreviewOnly && backend.ApplyCalls == 0);
                Check(!JsonSerializer.Serialize(state).Contains("PlacementArea"));
            }
        });
        test("spatial allocation adapts band heights and handles two groups", async () =>
        {
            var backend = Fixture(30);
            var planning = new DesktopPlanningService(backend, new ManualClock());
            var state = await planning.GetStateAsync();
            var plan = await planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 29, 1));
            Separators(plan, state.Geometry.GridSpacing!);
            Check(plan.Proposals.Count == 30 && backend.ApplyCalls == 0);
        });
        test("spatial input rejects omitted, repeated, unknown and malformed groups", async () =>
        {
            var backend = Fixture(4);
            var planning = new DesktopPlanningService(backend, new ManualClock());
            var state = await planning.GetStateAsync();
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 1, 2)), "INCOMPLETE_GROUPS");
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 4)), "INCOMPLETE_GROUPS");
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", new[]
                { new LayoutGroup("A", new[] { state.Icons[0].IconRef }), new LayoutGroup("B", new[] { state.Icons[0].IconRef }) }), "DUPLICATE_ICON");
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", new[]
                { new LayoutGroup("A", new[] { new string('a', 32) }), new LayoutGroup("B", new[] { state.Icons[1].IconRef }) }), "UNKNOWN_ICON");
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", new[]
                { new LayoutGroup(" A", new[] { state.Icons[0].IconRef }) }), "INVALID_ARGUMENT");
            Check(backend.ApplyCalls == 0);
        });
        test("spatial capacity and unsupported geometry fail without movement", async () =>
        {
            foreach (string failure in new[] { "area", "grid", "monitor", "rows", "capacity" })
            {
                var backend = Fixture(55);
                backend.Observation = failure switch
                {
                    "area" => backend.Observation with { PlacementArea = null },
                    "grid" => backend.Observation with { Geometry = backend.Observation.Geometry with { GridSpacing = null } },
                    "monitor" => backend.Observation with { Geometry = backend.Observation.Geometry with
                        { Monitors = new[] { backend.Observation.Geometry.Monitors[0], backend.Observation.Geometry.Monitors[0] } } },
                    "rows" => backend.Observation with { PlacementArea = new DesktopArea(0, 0, 1920, 260) },
                    _ => backend.Observation with { PlacementArea = new DesktopArea(0, 0, 400, 600) }
                };
                var planning = new DesktopPlanningService(backend, new ManualClock());
                var state = await planning.GetStateAsync();
                await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 3, 4, 17, 3, 3, 12, 11)),
                    failure == "grid" ? "UNSUPPORTED_GRID" : failure is "area" or "monitor" ? "UNSUPPORTED_GEOMETRY" : "INSUFFICIENT_SPACE");
                Check(backend.ApplyCalls == 0);
            }
        });
        test("spatial plans detect work-area changes and expiry", async () =>
        {
            var backend = Fixture(4);
            var clock = new ManualClock();
            var planning = new DesktopPlanningService(backend, clock);
            var state = await planning.GetStateAsync();
            backend.Observation = backend.Observation with { PlacementArea = new DesktopArea(0, 0, 1900, 1040) };
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 2)), "STALE_SNAPSHOT");
            clock.Now += DesktopPlanningService.SnapshotLifetime;
            await Reject(() => planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 2)), "EXPIRED_SNAPSHOT");
            Check(backend.ApplyCalls == 0);
        });
        test("spatial batch is opt-in, deduplicated and restorable using synthetic icons", async () =>
        {
            var backend = Fixture();
            var before = DesktopPlanningService.Fingerprint(backend.Observation);
            var clock = new ManualClock();
            var store = new FileOperationStore(ExecutionChecks.DataDirectory());
            var readOnly = new DesktopPlanningService(backend, clock);
            var state = await readOnly.GetStateAsync();
            var deniedPlan = await readOnly.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 3, 4, 17, 3, 3, 12, 11));
            await Reject(() => new DesktopExecutionService(readOnly, backend, store, clock)
                .ApplyAsync(deniedPlan.PlanId, Guid.NewGuid().ToString("N")), "WRITES_DISABLED");
            var planning = new DesktopPlanningService(backend, clock, new DesktopAccessPolicy(true));
            var execution = new DesktopExecutionService(planning, backend, store, clock);
            state = await planning.GetStateAsync();
            var plan = await planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 3, 4, 17, 3, 3, 12, 11));
            Check(!plan.PreviewOnly);
            string id = Guid.NewGuid().ToString("N");
            var result = await execution.ApplyAsync(plan.PlanId, id);
            Check(result.Status == "applied" && backend.ApplyCalls == 1);
            Check(await execution.ApplyAsync(plan.PlanId, id) == result && backend.ApplyCalls == 1);
            state = await planning.GetStateAsync();
            var restore = await execution.PreviewRestoreAsync(result.BackupId!, state.SnapshotId);
            Check((await execution.ApplyAsync(restore.PlanId, Guid.NewGuid().ToString("N"), restoration: true)).Status == "applied");
            Check(before == DesktopPlanningService.Fingerprint(backend.Observation));
        });
        test("restoration supports older journals and rejects changes to a recorded mapped area", async () =>
        {
            var backend = Fixture(4);
            var clock = new ManualClock();
            var store = new FileOperationStore(ExecutionChecks.DataDirectory());
            var planning = new DesktopPlanningService(backend, clock, new DesktopAccessPolicy(true));
            var execution = new DesktopExecutionService(planning, backend, store, clock);
            var state = await planning.GetStateAsync();
            var plan = await planning.PreviewAsync(state.SnapshotId, "spatial-groups", Groups(state, 2, 2));
            var applied = await execution.ApplyAsync(plan.PlanId, Guid.NewGuid().ToString("N"));
            var original = backend.Observation;
            backend.Observation = original with { PlacementArea = new DesktopArea(0, 0, 1900, 1040) };
            state = await planning.GetStateAsync();
            await Reject(() => execution.PreviewRestoreAsync(applied.BackupId!, state.SnapshotId), "RESTORE_CONFLICT");
            backend.Observation = original;
            await using (await store.AcquireAsync(CancellationToken.None))
            {
                var record = store.Read(applied.OperationId)!;
                store.Save(record with { Before = record.Before with { PlacementArea = null },
                    ExpectedAfter = record.ExpectedAfter with { PlacementArea = null },
                    ObservedAfter = record.ObservedAfter! with { PlacementArea = null } });
            }
            state = await planning.GetStateAsync();
            var restore = await execution.PreviewRestoreAsync(applied.BackupId!, state.SnapshotId);
            Check((await execution.ApplyAsync(restore.PlanId, Guid.NewGuid().ToString("N"), restoration: true)).Status == "applied");
        });
    }
}
