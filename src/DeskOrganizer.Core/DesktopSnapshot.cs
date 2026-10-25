using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskOrganizer;

/// <summary>
/// A fingerprint of one observed desktop layout, not a persistent identity for its icons.
/// An item replaced by an indistinguishable one cannot be detected by this fingerprint.
/// </summary>
public static class DesktopSnapshot
{
    public static (string SnapshotId, IReadOnlyList<DesktopIcon> Icons) Capture(Func<List<DesktopIcon>> readIcons)
    {
        List<DesktopIcon> first = readIcons();
        string fingerprint = Fingerprint(first);
        if (!string.Equals(fingerprint, Fingerprint(readIcons()), StringComparison.Ordinal))
            throw new InvalidOperationException("The desktop changed during the read. Try again.");
        return (fingerprint, first);
    }

    public static bool IsValidId(string? snapshotId) =>
        snapshotId is { Length: 64 } && snapshotId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Fingerprint(IReadOnlyList<DesktopIcon> icons)
    {
        // Preserve the legacy JSON field names here so previously captured IDs still verify.
        // JSON encoding also avoids ambiguous concatenations of names and coordinates.
        string json = JsonSerializer.Serialize(icons.Select(icon => new
        {
            indice = icon.Index,
            nombre = icon.DisplayName,
            x = icon.X,
            y = icon.Y
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
