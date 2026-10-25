namespace DeskOrganizer;

public static class LayoutPlanner
{
    public const int MaxIcons = 2048;
    public const int MaxGroups = 64;

    public static LayoutPlan Create(DesktopState state, string strategy, LayoutGroup[]? groups = null)
    {
        if (state.Icons.Count > MaxIcons)
            throw new DesktopException("LIMIT_EXCEEDED", "The desktop contains too many icons for a preview.");
        if (state.Icons.Select(icon => icon.Position).Distinct().Count() != state.Icons.Count)
            throw new DesktopException("AMBIGUOUS_LAYOUT", "Some observed positions overlap; a layout cannot be planned reliably.");
        if (strategy == "spatial-groups") return SpatialLayoutPlanner.Create(state, groups);
        if (strategy == "category-rows") return CategoryRowsLayoutPlanner.Create(state, groups);
        if (strategy != "custom" && groups is not null)
            throw new DesktopException("INVALID_ARGUMENT", "Groups are accepted only with the custom strategy.");

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        DesktopStateIcon[] ordered;
        switch (strategy)
        {
            case "name-ascending":
                ordered = ByName(state.Icons).ToArray();
                break;
            case "usage-category":
                var categoryOrder = DesktopCategoryPreview.CategoryNames
                    .Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index);
                foreach (var icon in state.Icons)
                    labels.Add(icon.IconRef, DesktopCategoryPreview.CategoryFor(icon.Name));
                ordered = ByName(state.Icons).OrderBy(icon => categoryOrder[labels[icon.IconRef]]).ToArray();
                break;
            case "custom":
                ordered = ResolveGroups(state, groups, labels);
                break;
            default:
                throw new DesktopException("INVALID_ARGUMENT", "Strategy must be name-ascending, usage-category, custom, spatial-groups, or category-rows.");
        }

        // Only selected icons contribute slots; all omitted icons retain their exact positions.
        var slots = ordered.Select(icon => icon.Position).OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        var targets = ordered.Select((icon, index) => (icon.IconRef, Position: slots[index]))
            .ToDictionary(pair => pair.IconRef, pair => pair.Position, StringComparer.Ordinal);
        var proposals = state.Icons.Select(icon =>
        {
            DesktopPoint target = targets.GetValueOrDefault(icon.IconRef, icon.Position);
            return new LayoutProposal(icon.IconRef, icon.Name, labels.GetValueOrDefault(icon.IconRef),
                icon.Position, target, target != icon.Position);
        }).ToArray();
        var warnings = state.Warnings.Concat(new[]
        {
            "Preview only: this server cannot apply or restore layouts. A plan ID is not authorization.",
            "Targets reuse observed view coordinates; Windows acceptance and persistence have not been verified."
        }).ToList();
        if (strategy == "usage-category")
            warnings.Add("Categories are display-name heuristics, not verified application usage or file types.");

        return new LayoutPlan(Guid.NewGuid().ToString("N"), state.SnapshotId, state.ExpiresAt, strategy,
            proposals.Length, proposals.Count(proposal => proposal.Changed), true,
            Array.AsReadOnly(proposals), warnings.AsReadOnly());
    }

    private static IOrderedEnumerable<DesktopStateIcon> ByName(IEnumerable<DesktopStateIcon> icons) =>
        icons.OrderBy(icon => icon.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(icon => icon.Name, StringComparer.Ordinal)
            .ThenBy(icon => icon.Position.X).ThenBy(icon => icon.Position.Y);

    internal static DesktopStateIcon[] ResolveGroups(DesktopState state, LayoutGroup[]? groups,
        Dictionary<string, string> labels)
    {
        if (groups is null || groups.Length is 0 or > MaxGroups)
            throw new DesktopException("INVALID_ARGUMENT", $"Custom layouts require 1 to {MaxGroups} groups.");
        var icons = state.Icons.ToDictionary(icon => icon.IconRef, StringComparer.Ordinal);
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<DesktopStateIcon>();
        foreach (var group in groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 128
                || group.Name != group.Name.Trim() || !groupNames.Add(group.Name)
                || group.IconRefs is null || group.IconRefs.Length is 0 or > MaxIcons)
                throw new DesktopException("INVALID_ARGUMENT", "Groups need distinct labels of 1 to 128 characters and nonempty icon reference lists.");
            foreach (string? reference in group.IconRefs)
            {
                if (reference is null || reference.Length != 32 || !icons.TryGetValue(reference, out var icon))
                    throw new DesktopException("UNKNOWN_ICON", "An icon reference does not belong to this snapshot.");
                if (!labels.TryAdd(reference, group.Name))
                    throw new DesktopException("DUPLICATE_ICON", "An icon can appear in only one group and only once.");
                selected.Add(icon);
            }
        }
        return selected.ToArray();
    }
}
