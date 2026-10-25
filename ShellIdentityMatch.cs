namespace DeskOrganizer;

/// <summary>
/// Matches independent Shell and ListView observations without assuming their indices
/// have the same order. Any duplicate or missing name+position pair stays unresolved.
/// </summary>
internal static class ShellIdentityMatch
{
    internal sealed record MatchedIcon(DesktopIcon Icon, string? ShellId, bool? HasFileSystemPath);

    internal static IReadOnlyList<MatchedIcon> Attach(
        IReadOnlyList<DesktopIcon> icons, IReadOnlyList<ShellIdentityProbe.Item> shellItems)
    {
        if (icons.Count != shellItems.Count)
            throw new InvalidOperationException("Desktop changed during Shell identity lookup.");

        var iconKeyCounts = icons.GroupBy(icon => (icon.DisplayName, icon.X, icon.Y))
            .ToDictionary(group => group.Key, group => group.Count());
        var shellGroups = shellItems.GroupBy(item => (item.Name, item.X, item.Y))
            .ToDictionary(group => group.Key, group => group.ToArray());

        return icons.Select(icon =>
        {
            var key = (icon.DisplayName, icon.X, icon.Y);
            ShellIdentityProbe.Item? match = iconKeyCounts[key] == 1
                && shellGroups.TryGetValue(key, out ShellIdentityProbe.Item[]? candidates)
                && candidates.Length == 1 ? candidates[0] : null;
            return match?.ShellId is null
                ? new MatchedIcon(icon, null, null)
                : new MatchedIcon(icon, match.ShellId, match.HasFileSystemPath);
        }).ToArray();
    }
}
