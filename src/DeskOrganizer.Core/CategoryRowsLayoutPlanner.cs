namespace DeskOrganizer;

/// <summary>Places each complete category on one horizontal grid row.</summary>
internal static class CategoryRowsLayoutPlanner
{
    public static LayoutPlan Create(DesktopState state, LayoutGroup[]? groups)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = LayoutPlanner.ResolveGroups(state, groups, labels);
        if (selected.Length != state.Icons.Count)
            throw new DesktopException("INCOMPLETE_GROUPS", "Category rows require groups covering every icon exactly once.");
        if (!MutationValidator.SupportedGeometry(state.Geometry) || state.PlacementArea is not { } area)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Category rows require one primary monitor and a work area mapped into Explorer's icon coordinates.");

        var spacing = state.Geometry.GridSpacing;
        if (spacing is null || spacing.X is < 16 or > 1024 || spacing.Y is < 16 or > 1024)
            throw new DesktopException("UNSUPPORTED_GRID", "Desktop icon grid spacing is unavailable or unsupported.");
        if (area.Left < state.Geometry.UsableArea.Left || area.Top < state.Geometry.UsableArea.Top
            || area.Right > state.Geometry.UsableArea.Right || area.Bottom > state.Geometry.UsableArea.Bottom)
            throw new DesktopException("UNSUPPORTED_GEOMETRY", "Mapped work area lies outside the desktop view.");

        long originX = (long)area.Left + Phase(state.Icons.Select(icon => icon.Position.X), area.Left, spacing.X);
        long originY = (long)area.Top + Phase(state.Icons.Select(icon => icon.Position.Y), area.Top, spacing.Y);
        long columns = ((long)area.Right - originX) / spacing.X;
        long rows = ((long)area.Bottom - originY) / spacing.Y;
        if (groups!.Length > rows || groups.Max(group => group.IconRefs.Length) > columns)
            throw new DesktopException("INSUFFICIENT_SPACE", "There are not enough grid rows or columns for one horizontal row per category. Merge categories or reduce the number of icons, then capture a new snapshot.");

        var destinations = new Dictionary<string, DesktopPoint>(StringComparer.Ordinal);
        for (int row = 0; row < groups.Length; row++)
        {
            for (int column = 0; column < groups[row].IconRefs.Length; column++)
            {
                destinations.Add(groups[row].IconRefs[column], new DesktopPoint(
                    checked((int)(originX + column * spacing.X)),
                    checked((int)(originY + row * spacing.Y))));
            }
        }

        var proposals = state.Icons.Select(icon => new LayoutProposal(icon.IconRef, icon.Name, labels[icon.IconRef],
            icon.Position, destinations[icon.IconRef], icon.Position != destinations[icon.IconRef])).ToArray();
        return new LayoutPlan(Guid.NewGuid().ToString("N"), state.SnapshotId, state.ExpiresAt, "category-rows",
            proposals.Length, proposals.Count(item => item.Changed), true, Array.AsReadOnly(proposals),
            state.Warnings.Concat(new[]
            {
                "Each group occupies its own horizontal grid row. Groups follow the supplied order; icon references follow the supplied order within each row. No visual frames are drawn."
            }).ToArray());
    }

    private static int Phase(IEnumerable<int> coordinates, int origin, int spacing) => coordinates
        .Select(coordinate => (int)((((long)coordinate - origin) % spacing + spacing) % spacing))
        .GroupBy(value => value).OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
        .Select(group => group.Key).FirstOrDefault();
}
