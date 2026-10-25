using DeskOrganizer;
using static DeskOrganizer.LegacyDesktopReader;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;


// Router
if (args.Length == 0)
{
    Console.WriteLine("Starting desktop icon export...");
    ExportIconPositions(); // Preserve the existing no-argument export behavior.

    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
    return;
}

string endpoint = args[0];

switch (endpoint)
{
    case "disable-auto-arrange":
    case "desactivar-auto":
        DisableAutoArrange();
        break;

    case "get-desktop-handle":
    case "obtener-handles":
        FindDesktopView();
        break;

    case "count-icons":
        try
        {
            Console.WriteLine(CountIcons(false));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "contar-iconos":
        CountIcons();
        break;

    case "export-icon-positions":
        ExportIconPositions("desktop_icons.json", false);
        break;

    case "leer-posiciones":
        ExportIconPositions();
        break;

    case "help":
    case "ayuda":
    case "--help":
        Console.WriteLine("Commands: list-icons [--with-shell-ids] (JSON, no file), probe-shell-identities (read-only Shell identity experiment), query-desktop-geometry (read-only JSON), probe-desktop-coordinates (read-only DPI/coordinate observations), diagnose-desktop-layout (read-only JSON), preview-icon-layout (read-only alphabetical plan), preview-category-layout (read-only usage-category plan), move-shell-icon <snapshotId> <index> <X> <Y> (changes desktop; verifies result), capture-snapshot (JSON), verify-snapshot [snapshotId] (0=unchanged, 2=changed), export-icon-positions (writes desktop_icons.json to the desktop), count-icons, get-desktop-handle, move-icon [index] [X] [Y], disable-auto-arrange. Older command aliases remain available.");
        break;

    case "list-icons":
    case "listar-iconos":
        try
        {
            IntPtr desktopView = FindDesktopView(false);
            if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
            if (endpoint == "list-icons" && args.Length >= 2 && args[1] == "--with-shell-ids")
            {
                if (args.Length != 2) throw new ArgumentException("Usage: list-icons [--with-shell-ids]");
                var before = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
                var shellItems = ShellIdentityProbe.Capture();
                var after = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
                if (before.SnapshotId != after.SnapshotId)
                    throw new InvalidOperationException("Desktop changed during Shell identity lookup.");
                var matched = ShellIdentityMatch.Attach(before.Icons, shellItems);
                Console.WriteLine(JsonSerializer.Serialize(matched.Select(entry => new
                {
                    index = entry.Icon.Index, name = entry.Icon.DisplayName, x = entry.Icon.X, y = entry.Icon.Y,
                    shellId = entry.ShellId, hasFileSystemPath = entry.HasFileSystemPath
                })));
                break;
            }
            List<DesktopIcon> icons = ReadIcons(desktopView, false, false);
            Console.WriteLine(endpoint == "listar-iconos"
                ? JsonSerializer.Serialize(icons.Select(icon => new { indice = icon.Index, nombre = icon.DisplayName, x = icon.X, y = icon.Y }))
                : JsonSerializer.Serialize(icons.Select(icon => new { index = icon.Index, name = icon.DisplayName, x = icon.X, y = icon.Y })));
        }
        catch (Exception ex)
        {
            // Shell failures may mention private parsing paths; never expose them from the opt-in query.
            if (endpoint == "list-icons" && args.Length >= 2 && args[1] == "--with-shell-ids")
                Console.Error.WriteLine(args.Length != 2
                    ? "ERROR: Could not read desktop icons: Usage: list-icons [--with-shell-ids]"
                    : $"ERROR: Shell identity lookup failed (0x{ex.HResult:X8}).");
            else
                Console.Error.WriteLine($"ERROR: Could not read desktop icons: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "probe-shell-identities":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: probe-shell-identities");
            var shellItems = ShellIdentityProbe.Capture();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                count = shellItems.Count,
                items = shellItems.Select(item => new { viewIndex = item.ViewIndex, name = item.Name, x = item.X, y = item.Y, shellId = item.ShellId, hasFileSystemPath = item.HasFileSystemPath }),
                unresolved = shellItems.Count(item => item.ShellId is null)
            }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Shell identity probe failed (0x{ex.HResult:X8}).");
            Environment.ExitCode = 1;
        }
        break;

    case "move-shell-icon":
        try
        {
            if (args.Length != 5 || !DesktopSnapshot.IsValidId(args[1])
                || !int.TryParse(args[2], out int shellIndex)
                || !int.TryParse(args[3], out int targetX)
                || !int.TryParse(args[4], out int targetY))
                throw new ArgumentException("Usage: move-shell-icon <snapshotId> <index> <X> <Y>");
            IntPtr desktopView = FindDesktopView(false);
            if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
            if (DesktopGeometry.ReadAutoArrange(desktopView) != false)
                throw new InvalidOperationException("Auto-arrange must be disabled and observable.");
            var before = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
            if (before.SnapshotId != args[1])
                throw new InvalidOperationException("Desktop changed since the supplied snapshot.");
            DesktopIcon icon = before.Icons.SingleOrDefault(item => item.Index == shellIndex)
                ?? throw new InvalidOperationException("Icon index is unavailable.");
            if (before.Icons.Count(item => item.DisplayName == icon.DisplayName) != 1
                || before.Icons.GroupBy(item => (item.X, item.Y)).Any(group => group.Count() > 1))
                throw new InvalidOperationException("Desktop icon mapping is ambiguous.");
            if (before.Icons.Any(item => item.X == targetX && item.Y == targetY))
                throw new InvalidOperationException("Destination is occupied.");
            ShellDesktopMover.Move(icon.DisplayName, icon.X, icon.Y, targetX, targetY, before.Icons.Count);
            var after = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
            if (after.Icons.Count != before.Icons.Count
                || after.Icons.Count(item => item.DisplayName == icon.DisplayName
                    && item.X == targetX && item.Y == targetY) != 1
                || before.Icons.Where(item => item.DisplayName != icon.DisplayName)
                    .Any(item => !after.Icons.Any(observed => observed.DisplayName == item.DisplayName
                        && observed.X == item.X && observed.Y == item.Y)))
                throw new InvalidOperationException("Desktop changed unexpectedly after Shell move; stop without retrying.");
            Console.WriteLine(JsonSerializer.Serialize(new { moved = true, snapshotId = after.SnapshotId }));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("ERROR: Shell desktop move timed out; result unknown. Do not retry.");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Shell desktop move failed (0x{ex.HResult:X8}); result may be unknown. Do not retry without checking positions.");
            Environment.ExitCode = 1;
        }
        break;

    case "preview-category-layout":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: preview-category-layout");
            var snapshot = CaptureSnapshot();
            Console.WriteLine(JsonSerializer.Serialize(DesktopCategoryPreview.Create(snapshot.SnapshotId, snapshot.Icons)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "preview-icon-layout":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: preview-icon-layout");
            var snapshot = CaptureSnapshot();
            Console.WriteLine(JsonSerializer.Serialize(DesktopLayoutPreview.Create(snapshot.SnapshotId, snapshot.Icons)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "diagnose-desktop-layout":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: diagnose-desktop-layout");
            IntPtr desktopView = FindDesktopView(false);
            if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
            var bounds = DesktopGeometry.ReadClientBounds(desktopView);
            var before = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
            var after = DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
            if (before.SnapshotId != after.SnapshotId || bounds != DesktopGeometry.ReadClientBounds(desktopView))
                throw new InvalidOperationException("Desktop changed during layout diagnosis.");
            var outside = before.Icons.Where(icon => icon.X < bounds.Left || icon.X >= bounds.Right
                || icon.Y < bounds.Top || icon.Y >= bounds.Bottom)
                .Select(icon => new { x = icon.X, y = icon.Y }).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                iconCount = before.Icons.Count,
                outsideClientAreaCount = outside.Length,
                outsideClientAreaPositions = outside,
                note = "Outside the reported client area does not mean an icon is misplaced."
            }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "probe-desktop-coordinates":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: probe-desktop-coordinates");
            IntPtr desktopView = FindDesktopView(false);
            if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
            Console.WriteLine(JsonSerializer.Serialize(DesktopGeometry.ProbeCoordinates(desktopView)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "query-desktop-geometry":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: query-desktop-geometry");
            IntPtr desktopView = FindDesktopView(false);
            if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
            Console.WriteLine(JsonSerializer.Serialize(DesktopGeometry.Capture(desktopView)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "capture-snapshot":
    case "capturar-snapshot":
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: capture-snapshot");
            var snapshot = CaptureSnapshot();
            Console.WriteLine(endpoint == "capturar-snapshot"
                ? JsonSerializer.Serialize(new { snapshotId = snapshot.SnapshotId, icons = snapshot.Icons.Select(icon => new { indice = icon.Index, nombre = icon.DisplayName, x = icon.X, y = icon.Y }) })
                : JsonSerializer.Serialize(new { snapshotId = snapshot.SnapshotId, icons = snapshot.Icons.Select(icon => new { index = icon.Index, name = icon.DisplayName, x = icon.X, y = icon.Y }) }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "verify-snapshot":
    case "verificar-snapshot":
        try
        {
            if (args.Length != 2 || !DesktopSnapshot.IsValidId(args[1]))
                throw new ArgumentException("Usage: verify-snapshot [64-character hexadecimal snapshotId]");
            bool unchanged = string.Equals(args[1], CaptureSnapshot().SnapshotId, StringComparison.Ordinal);
            Console.WriteLine(JsonSerializer.Serialize(new { unchanged }));
            if (!unchanged) Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.ExitCode = 1;
        }
        break;

    case "move-icon":
    case "mover-icono":
        if (args.Length < 4)
        {
            Console.WriteLine("ERROR: Missing arguments. Usage: DeskOrganizer.exe move-icon [index] [X] [Y]");
            return;
        }

        if (int.TryParse(args[1], out int index) &&
            int.TryParse(args[2], out int x) &&
            int.TryParse(args[3], out int y))
        {
            MoveIcon(index, x, y);
        }
        else
        {
            Console.WriteLine("ERROR: Index, X, and Y must be integers.");
        }
        break;

    default:
        Console.WriteLine($"ERROR: Unknown command '{endpoint}'. Run --help for available commands.");
        break;
}


// Desktop controllers

static void RestartExplorer()
{
    Process[] explorerProcesses = Process.GetProcessesByName("explorer");

    foreach (Process explorerProcess in explorerProcesses)
    {
        explorerProcess.Kill();
        explorerProcess.WaitForExit();
    }

    Process.Start("explorer.exe");
    Console.WriteLine("Explorer restarted successfully.");
}

static void DisableAutoArrange()
{
    string registryPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    try
    {
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(registryPath, true))
        {
            if (key != null)
            {
                int fFlags = (int)key.GetValue("FFlags", 0);
                int autoArrangeBit = 0x40000000;

                if ((fFlags & autoArrangeBit) == autoArrangeBit)
                {
                    fFlags = fFlags & ~autoArrangeBit;
                    key.SetValue("FFlags", fFlags);
                    Console.WriteLine("SUCCESS: Auto-arrange disabled.");

                    RestartExplorer();
                }
                else
                {
                    Console.WriteLine("SUCCESS: Auto-arrange was already disabled.");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
}

static void ExportIconPositions(string outputFileName = "datos_entrenamiento.json", bool legacyFormat = true)
{
    IntPtr listViewHandle = FindDesktopView();
    if (listViewHandle == IntPtr.Zero) return;

    List<DesktopIcon> icons = ReadIcons(listViewHandle, true, true);
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    string jsonOutput = legacyFormat
        ? JsonSerializer.Serialize(icons.Select(icon => new IconoData
        {
            Indice = icon.Index,
            NombreVisual = icon.DisplayName,
            X = icon.X,
            Y = icon.Y,
            RutaFisica = icon.PhysicalPath,
            Destino = icon.Target,
            EsDirectorio = icon.IsDirectory
        }), jsonOptions)
        : JsonSerializer.Serialize(icons.Select(icon => new
        {
            index = icon.Index,
            name = icon.DisplayName,
            x = icon.X,
            y = icon.Y,
            physicalPath = icon.PhysicalPath,
            target = icon.Target,
            isDirectory = icon.IsDirectory
        }), jsonOptions);

    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    string outputPath = Path.Combine(desktopPath, outputFileName);

    File.WriteAllText(outputPath, jsonOutput);

    Console.WriteLine("--------------------------------------------------");
    Console.WriteLine("Desktop icon export completed.");
    Console.WriteLine("Exported data (which may contain personal information):");
    Console.WriteLine(outputPath);
    Console.WriteLine("Review the file before sharing it.");
    Console.WriteLine("--------------------------------------------------");
}
