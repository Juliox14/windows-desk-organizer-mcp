namespace DeskOrganizer;

/// <summary>Conservative name-based usage groups for a read-only layout preview.</summary>
public static class DesktopCategoryPreview
{
    private static readonly string[] Groups =
    {
        "development", "communication", "media", "games", "utilities", "documents", "other"
    };

    private static readonly Dictionary<string, string> KnownApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GitHub Desktop"] = "development",
        ["MongoDBCompass"] = "development",
        ["Docker Desktop"] = "development",
        ["WinSCP"] = "development",
        ["Wireshark"] = "development",
        ["Postman"] = "development",
        ["Visual Studio Code"] = "development",
        ["XAMPP Control Panel"] = "development",
        ["SQL Server Management Studio 20"] = "development",
        ["Python"] = "development",
        ["Roblox Studio"] = "development",
        ["Bitvise SSH Client"] = "development",
        ["Zoom Workplace"] = "communication",
        ["Webex"] = "communication",
        ["Discord"] = "communication",
        ["VLC media player"] = "media",
        ["OBS Studio"] = "media",
        ["Spotify"] = "media",
        ["Wallpaper Engine"] = "media",
        ["Steam"] = "games",
        ["Epic Games Launcher"] = "games",
        ["Logitech G HUB"] = "utilities",
        ["MiniTool Partition Wizard"] = "utilities",
        ["Google Chrome"] = "utilities",
        ["Microsoft Edge"] = "utilities",
        ["Tor Browser"] = "utilities",
        ["NVIDIA App"] = "utilities",
        ["DDU v18.1.5.6"] = "utilities",
        ["Hillstone Secure Connect"] = "utilities",
        ["Iriun Webcam"] = "utilities",
        ["Grammarly"] = "utilities",
        ["Papelera de reciclaje"] = "utilities",
        ["Este equipo"] = "utilities",
        ["Red"] = "utilities"
    };

    public static IReadOnlyList<string> CategoryNames { get; } = Array.AsReadOnly(Groups);

    public static object Create(string snapshotId, IReadOnlyList<DesktopIcon> icons)
    {
        var slots = icons.OrderBy(icon => icon.X).ThenBy(icon => icon.Y)
            .Select(icon => (icon.X, icon.Y)).ToArray();
        var ordered = icons.Select(icon => (Icon: icon, Category: CategoryFor(icon.DisplayName)))
            .OrderBy(entry => Array.IndexOf(Groups, entry.Category))
            .ThenBy(entry => entry.Icon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Icon.DisplayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Icon.Index).ToArray();
        var proposals = ordered.Select((entry, slotIndex) => new
        {
            index = entry.Icon.Index,
            name = entry.Icon.DisplayName,
            category = entry.Category,
            fromX = entry.Icon.X, fromY = entry.Icon.Y,
            toX = slots[slotIndex].X, toY = slots[slotIndex].Y,
            changed = entry.Icon.X != slots[slotIndex].X || entry.Icon.Y != slots[slotIndex].Y
        }).ToArray();
        return new
        {
            snapshotId,
            strategy = "usage-category",
            iconCount = proposals.Length,
            changedCount = proposals.Count(proposal => proposal.changed),
            categories = Groups.Select(group => new
            {
                name = group, count = proposals.Count(proposal => proposal.category == group)
            }),
            proposals
        };
    }

    public static string CategoryFor(string name)
    {
        if (KnownApplications.TryGetValue(name, out string? category)) return category;
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return "documents";
        return "other";
    }
}
