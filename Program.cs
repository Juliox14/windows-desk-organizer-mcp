using DeskOrganizer;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Encodings.Web;


[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

// List-view read messages pass pointers into Explorer; never truncate them to int on x64.
[DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
static extern IntPtr SendMessagePointer(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

[DllImport("user32.dll", SetLastError = true)]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr VirtualAllocEx(IntPtr processHandle, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr lpBaseAddress, out POINT lpBuffer, int nSize, out int lpNumberOfBytesRead);

// Byte-array overload of the native ReadProcessMemory function.
[DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
static extern bool ReadProcessMemoryBytes(IntPtr processHandle, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool VirtualFreeEx(IntPtr processHandle, IntPtr lpAddress, uint dwSize, uint dwFreeType);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr hObject);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr lpBaseAddress, ref LVITEM lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

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
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath, true))
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

static IntPtr FindDesktopView(bool showMessages = true)
{
    IntPtr progman = FindWindow("Progman", null);
    IntPtr shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

    if (shellView == IntPtr.Zero)
    {
        IntPtr desktopWorkerW = IntPtr.Zero;

        do
        {
            desktopWorkerW = FindWindowEx(IntPtr.Zero, desktopWorkerW, "WorkerW", null);

            if (desktopWorkerW != IntPtr.Zero)
            {
                shellView = FindWindowEx(desktopWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    break;
                }
            }
        } while (desktopWorkerW != IntPtr.Zero);
    }

    if (shellView == IntPtr.Zero)
    {
        if (showMessages) Console.WriteLine("ERROR: Could not find SHELLDLL_DefView.");
        return IntPtr.Zero;
    }

    IntPtr listView = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);

    if (listView != IntPtr.Zero)
    {
        if (showMessages) Console.WriteLine($"SUCCESS: Found SysListView32 HWND -> {listView}");
        return listView;
    }
    else
    {
        if (showMessages) Console.WriteLine("ERROR: Could not find the desktop SysListView32.");
        return IntPtr.Zero;
    }
}

static int CountIcons(bool showMessages = true)
{
    IntPtr listViewHandle = FindDesktopView(showMessages);

    if (listViewHandle == IntPtr.Zero)
    {
        if (!showMessages) throw new InvalidOperationException("Desktop window handle is unavailable.");
        Console.WriteLine("ERROR: Desktop window handle is unavailable.");
        return 0;
    }

    uint LVM_GETITEMCOUNT = 0x1004;

    int iconCount = SendMessage(listViewHandle, LVM_GETITEMCOUNT, 0, 0);

    return iconCount;
}

static string[] GetFileMetadata(string displayName)
{
    string[] desktopFolders =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    };

    foreach (string desktopPath in desktopFolders)
    {
        string[] matchingEntries = Directory.GetFileSystemEntries(desktopPath, $"{displayName}*");

        if (matchingEntries.Length > 0)
        {
            string physicalPath = matchingEntries[0];
            string target = physicalPath;

            if (physicalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve the shortcut target when possible.
                try
                {
                    Type wshShellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic wshShell = Activator.CreateInstance(wshShellType);
                    dynamic shortcut = wshShell.CreateShortcut(physicalPath);
                    target = shortcut.TargetPath;
                }
                catch { } // Keep the physical path when shortcut resolution fails.
            }

            return new string[] { physicalPath, target };
        }
    }

    return new string[] { "", "" }; // No matching physical file was found.
}


static string MaskUserProfilePath(string originalPath)
{
    if (string.IsNullOrEmpty(originalPath)) return originalPath;

    string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    if (originalPath.StartsWith(profilePath, StringComparison.OrdinalIgnoreCase))
    {
        return @"C:\Users\<USER>" + originalPath.Substring(profilePath.Length);
    }

    return originalPath;
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

// Queries Explorer through temporary remote memory. The caller decides whether to export or print.
static List<DesktopIcon> ReadIcons(IntPtr listViewHandle, bool includeMetadata, bool showMessages)
{
    uint LVM_GETITEMPOSITION = 0x1010;
    uint LVM_GETITEMTEXTW = 0x1073;
    uint LVIF_TEXT = 0x0001;

    uint PROCESS_VM_OPERATION = 0x0008;
    uint PROCESS_VM_READ = 0x0010;
    uint PROCESS_VM_WRITE = 0x0020;
    uint MEM_COMMIT = 0x1000;
    uint MEM_RESERVE = 0x2000;
    uint MEM_RELEASE = 0x8000;
    uint PAGE_READWRITE = 0x04;

    int iconCount = showMessages
        ? CountIcons()
        : SendMessage(listViewHandle, 0x1004, 0, 0);
    if (iconCount < 0) throw new InvalidOperationException("Could not count desktop icons.");

    GetWindowThreadProcessId(listViewHandle, out uint processId);
    if (processId == 0) throw new InvalidOperationException("Could not identify Explorer.");
    IntPtr processHandle = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
    if (processHandle == IntPtr.Zero) throw new InvalidOperationException("Could not open Explorer.");

    IntPtr positionMemory = IntPtr.Zero;
    IntPtr textMemory = IntPtr.Zero;
    IntPtr itemMemory = IntPtr.Zero;
    try
    {
        positionMemory = VirtualAllocEx(processHandle, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        textMemory = VirtualAllocEx(processHandle, IntPtr.Zero, 512, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        itemMemory = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)Marshal.SizeOf(typeof(LVITEM)), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (positionMemory == IntPtr.Zero || textMemory == IntPtr.Zero || itemMemory == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate memory for the Explorer query.");

        List<DesktopIcon> icons = new List<DesktopIcon>();

        for (int i = 0; i < iconCount; i++)
        {
            if (SendMessagePointer(listViewHandle, LVM_GETITEMPOSITION, (IntPtr)i, positionMemory) == IntPtr.Zero)
                throw new InvalidOperationException($"Could not read position for icon {i}.");
            if (!ReadProcessMemory(processHandle, positionMemory, out POINT position, 8, out int positionBytes) || positionBytes != 8)
                throw new InvalidOperationException($"Could not copy position for icon {i}.");

            LVITEM item = new LVITEM();
            item.mask = LVIF_TEXT;
            item.iItem = i;
            item.cchTextMax = 255;
            item.pszText = textMemory;

            uint itemSize = (uint)Marshal.SizeOf(typeof(LVITEM));
            if (!WriteProcessMemory(processHandle, itemMemory, ref item, itemSize, out int bytesWritten)
                || bytesWritten != itemSize)
                throw new InvalidOperationException($"Could not prepare text for icon {i}.");
            // Zero characters can mean an empty label; the memory read still checks for a native failure.
            SendMessagePointer(listViewHandle, LVM_GETITEMTEXTW, (IntPtr)i, itemMemory);

            byte[] textBuffer = new byte[512];
            if (!ReadProcessMemoryBytes(processHandle, textMemory, textBuffer, 512, out int textBytes) || textBytes != 512)
                throw new InvalidOperationException($"Could not read text for icon {i}.");

            string decodedText = System.Text.Encoding.Unicode.GetString(textBuffer);
            string displayName = decodedText.Split('\0')[0];

            string[] metadata = includeMetadata ? GetFileMetadata(displayName) : new[] { "", "" };
            string physicalPath = metadata[0];
            string target = metadata[1];
            bool isDirectory = Directory.Exists(physicalPath);

            string cleanPhysicalPath = MaskUserProfilePath(physicalPath);
            string cleanTarget = MaskUserProfilePath(target);

            icons.Add(new DesktopIcon
            {
                Index = i,
                DisplayName = displayName,
                X = position.X,
                Y = position.Y,
                PhysicalPath = cleanPhysicalPath,
                Target = cleanTarget,
                IsDirectory = isDirectory
            });
        }

        return icons;
    }
    finally
    {
        if (positionMemory != IntPtr.Zero) VirtualFreeEx(processHandle, positionMemory, 0, MEM_RELEASE);
        if (textMemory != IntPtr.Zero) VirtualFreeEx(processHandle, textMemory, 0, MEM_RELEASE);
        if (itemMemory != IntPtr.Zero) VirtualFreeEx(processHandle, itemMemory, 0, MEM_RELEASE);
        CloseHandle(processHandle);
    }
}

static (string SnapshotId, IReadOnlyList<DesktopIcon> Icons) CaptureSnapshot()
{
    IntPtr desktopView = FindDesktopView(false);
    if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
    return DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
}

static void MoveIcon(int index, int x, int y)
{
    IntPtr listViewHandle = FindDesktopView();
    if (listViewHandle == IntPtr.Zero) return;

    // Set an item's position in the desktop ListView.
    uint LVM_SETITEMPOSITION = 0x100F;

    // Pack X into the low 16 bits and Y into the high 16 bits of lParam.
    int lParam = (y << 16) | (x & 0xFFFF);

    // Send the position change to Explorer.
    SendMessage(listViewHandle, LVM_SETITEMPOSITION, index, lParam);

    Console.WriteLine($"SUCCESS: Icon [{index}] moved to (X: {x}, Y: {y})");
}