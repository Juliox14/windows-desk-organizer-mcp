namespace DeskOrganizer;

/// <summary>Hypothetical reassignment of already occupied desktop icon positions; never applies changes.</summary>
internal static class DesktopLayoutPreview
{
    internal static object Create(string snapshotId, IReadOnlyList<DesktopIcon> icons)
    {
        // Reusing observed slots avoids inventing an origin, screen coordinate conversion, or capacity.
        var slots = icons.OrderBy(icon => icon.X).ThenBy(icon => icon.Y)
            .Select(icon => (icon.X, icon.Y)).ToArray();
        var ordered = icons.OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(icon => icon.DisplayName, StringComparer.Ordinal)
            .ThenBy(icon => icon.Index).ToArray();
        var proposals = ordered.Select((icon, slotIndex) => new
        {
            index = icon.Index,
            name = icon.DisplayName,
            fromX = icon.X, fromY = icon.Y,
            toX = slots[slotIndex].X, toY = slots[slotIndex].Y,
            changed = icon.X != slots[slotIndex].X || icon.Y != slots[slotIndex].Y
        }).ToArray();
        return new
        {
            snapshotId,
            strategy = "name-ascending",
            iconCount = proposals.Length,
            changedCount = proposals.Count(proposal => proposal.changed),
            proposals
        };
    }
}
