namespace DeskOrganizer;

/// <summary>Allocates disjoint grid rectangles in two bands, with a blank row and columns between groups.</summary>
internal static class SpatialLayoutPlanner
{
    private readonly record struct Allocation(int Split, int TopRows, int TopWidth, int BottomWidth);

    public static LayoutPlan Create(DesktopState state, LayoutGroup[]? groups)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = LayoutPlanner.ResolveGroups(state, groups, labels);
        if (groups!.Length < 2 || selected.Length != state.Icons.Count)
            throw new DesktopException("INCOMPLETE_GROUPS", "Spatial layouts require 2 to 64 nonempty groups covering every icon exactly once. Use custom to keep omitted icons fixed.");
        if (!MutationValidator.SupportedGeometry(state.Geometry) || state.PlacementArea is not { } area)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Spatial groups require one primary monitor and a work area mapped into Explorer's icon coordinates.");
        var spacing = state.Geometry.GridSpacing;
        if (spacing is null || spacing.X is < 16 or > 1024 || spacing.Y is < 16 or > 1024)
            throw new DesktopException("UNSUPPORTED_GRID", "Desktop icon grid spacing is unavailable or unsupported.");
        if (area.Left < state.Geometry.UsableArea.Left || area.Top < state.Geometry.UsableArea.Top
            || area.Right > state.Geometry.UsableArea.Right || area.Bottom > state.Geometry.UsableArea.Bottom)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Mapped work area lies outside the desktop view.");

        long originX = (long)area.Left + Phase(state.Icons.Select(icon => icon.Position.X), area.Left, spacing.X);
        long originY = (long)area.Top + Phase(state.Icons.Select(icon => icon.Position.Y), area.Top, spacing.Y);
        // Reserve a complete grid cell for each icon, including the final row/column near the taskbar.
        long columns = ((long)area.Right - originX) / spacing.X;
        long rows = ((long)area.Bottom - originY) / spacing.Y;
        if (columns is < 1 or > 2048 || rows is < 3 or > 2048)
            throw new DesktopException("INSUFFICIENT_SPACE", "The work area cannot fit two separated icon bands at the current grid spacing.");

        Allocation? best = null;
        (int, int, int, int, int) Rank(Allocation item) => (Math.Max(item.TopWidth, item.BottomWidth),
            Math.Abs(2 * item.TopRows - ((int)rows - 1)), Math.Abs(2 * item.Split - groups.Length), item.Split, item.TopRows);
        for (int split = 1; split < groups.Length; split++)
        {
            for (int topRows = 1; topRows < rows - 1; topRows++)
            {
                int topWidth = Width(groups, 0, split, topRows);
                int bottomWidth = Width(groups, split, groups.Length, (int)rows - topRows - 1);
                if (topWidth <= columns && bottomWidth <= columns)
                {
                    var candidate = new Allocation(split, topRows, topWidth, bottomWidth);
                    if (best is null || Rank(candidate).CompareTo(Rank(best.Value)) < 0) best = candidate;
                }
            }
        }
        // Balance occupied band widths, then band heights, then the number of groups. Ties are stable.
        var allocation = best
            ?? throw new DesktopException("INSUFFICIENT_SPACE", "Groups do not fit with blank grid separators. Merge categories or reduce icon size, then capture a new snapshot.");

        var destinations = new Dictionary<string, DesktopPoint>(StringComparer.Ordinal);
        for (int band = 0; band < 2; band++)
        {
            var bandGroups = band == 0 ? groups[..allocation.Split] : groups[allocation.Split..];
            int height = band == 0 ? allocation.TopRows : (int)rows - allocation.TopRows - 1;
            int startRow = band == 0 ? 0 : allocation.TopRows + 1;
            int column = 0;
            foreach (var group in bandGroups)
            {
                for (int index = 0; index < group.IconRefs.Length; index++)
                    destinations.Add(group.IconRefs[index], new DesktopPoint(
                        checked((int)(originX + (column + index / height) * spacing.X)),
                        checked((int)(originY + (startRow + index % height) * spacing.Y))));
                column += (group.IconRefs.Length + height - 1) / height + 1;
            }
        }
        var proposals = state.Icons.Select(icon => new LayoutProposal(icon.IconRef, icon.Name, labels[icon.IconRef],
            icon.Position, destinations[icon.IconRef], icon.Position != destinations[icon.IconRef])).ToArray();
        return new LayoutPlan(Guid.NewGuid().ToString("N"), state.SnapshotId, state.ExpiresAt, "spatial-groups",
            proposals.Length, proposals.Count(item => item.Changed), true, Array.AsReadOnly(proposals),
            state.Warnings.Concat(new[]
            {
                "Group order is left to right in the top band, then left to right in the bottom band. The split adapts to capacity; inspect the exact proposals.",
                "A complete blank grid row separates bands, and a blank grid column separates groups within each band. No visible frames are drawn."
            }).ToArray());
    }

    private static int Width(LayoutGroup[] groups, int start, int end, int rows)
    {
        int width = end - start - 1;
        for (int index = start; index < end; index++) width += (groups[index].IconRefs.Length + rows - 1) / rows;
        return width;
    }

    private static int Phase(IEnumerable<int> coordinates, int origin, int spacing) => coordinates
        .Select(coordinate => (int)((((long)coordinate - origin) % spacing + spacing) % spacing))
        .GroupBy(value => value).OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
        .Select(group => group.Key).FirstOrDefault();
}
