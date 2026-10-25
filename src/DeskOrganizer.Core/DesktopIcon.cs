namespace DeskOrganizer;

/// <summary>A desktop ListView item; Index is not a persistent icon identity.</summary>
public sealed class DesktopIcon
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string PhysicalPath { get; set; } = "";
    public string Target { get; set; } = "";
    public bool IsDirectory { get; set; }
}
