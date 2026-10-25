using System.Runtime.InteropServices;

namespace DeskOrganizer;

/// <summary>Compatibility adapter for the CLI. Not registered as an MCP tool.</summary>
public static class LegacyDesktopReader
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

    // List-view read messages pass pointers into Explorer; never truncate them to int on x64.
    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern IntPtr SendMessagePointer(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr processHandle, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr lpBaseAddress, out POINT lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    // Byte-array overload of the native ReadProcessMemory function.
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
    private static extern bool ReadProcessMemoryBytes(IntPtr processHandle, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr processHandle, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr lpBaseAddress, ref LVITEM lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    public static IntPtr FindDesktopView(bool showMessages = true)
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

    public static int CountIcons(bool showMessages = true)
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

    private static string[] GetFileMetadata(string displayName)
    {
        string[] desktopFolders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        string[] matchingEntries = desktopFolders.Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFileSystemEntries(path))
            .Where(path => string.Equals(Path.GetFileName(path), displayName, StringComparison.OrdinalIgnoreCase)
                || (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), displayName, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matchingEntries.Length == 1)
        {
            string physicalPath = matchingEntries[0];
            string target = physicalPath;

            if (physicalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve the shortcut target when possible.
                try
                {
                    Type wshShellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Shortcut metadata is unavailable.");
                    dynamic wshShell = Activator.CreateInstance(wshShellType)
                        ?? throw new InvalidOperationException("Shortcut metadata is unavailable.");
                    dynamic shortcut = wshShell.CreateShortcut(physicalPath);
                    target = shortcut.TargetPath;
                }
                catch { } // Keep the physical path when shortcut resolution fails.
            }

            return new string[] { physicalPath, target };
        }

        return new string[] { "", "" }; // No matching physical file was found.
    }


    private static string MaskUserProfilePath(string originalPath)
    {
        if (string.IsNullOrEmpty(originalPath)) return originalPath;

        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (originalPath.StartsWith(profilePath, StringComparison.OrdinalIgnoreCase))
        {
            return @"C:\Users\<USER>" + originalPath.Substring(profilePath.Length);
        }

        return originalPath;
    }

    // Queries Explorer through temporary remote memory. The caller decides whether to export or print.
    public static List<DesktopIcon> ReadIcons(IntPtr listViewHandle, bool includeMetadata, bool showMessages)
    {
        EnsureCompatibleArchitecture(listViewHandle);
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
        if (iconCount < 0 || iconCount > LayoutPlanner.MaxIcons) throw new InvalidOperationException("Could not count desktop icons.");

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
            textMemory = VirtualAllocEx(processHandle, IntPtr.Zero, 2048, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            itemMemory = VirtualAllocEx(processHandle, IntPtr.Zero, (nuint)Marshal.SizeOf(typeof(LVITEM)), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (positionMemory == IntPtr.Zero || textMemory == IntPtr.Zero || itemMemory == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate memory for the Explorer query.");

            List<DesktopIcon> icons = new List<DesktopIcon>();

            for (int i = 0; i < iconCount; i++)
            {
                if (SendMessagePointer(listViewHandle, LVM_GETITEMPOSITION, (IntPtr)i, positionMemory) == IntPtr.Zero)
                    throw new InvalidOperationException($"Could not read position for icon {i}.");
                if (!ReadProcessMemory(processHandle, positionMemory, out POINT position, 8, out nuint positionBytes) || positionBytes != 8)
                    throw new InvalidOperationException($"Could not copy position for icon {i}.");

                LVITEM item = new LVITEM();
                item.mask = LVIF_TEXT;
                item.iItem = i;
                item.cchTextMax = 1024;
                item.pszText = textMemory;

                nuint itemSize = (nuint)Marshal.SizeOf(typeof(LVITEM));
                if (!WriteProcessMemory(processHandle, itemMemory, ref item, itemSize, out nuint bytesWritten)
                    || bytesWritten != itemSize)
                    throw new InvalidOperationException($"Could not prepare text for icon {i}.");
                // Zero characters can mean an empty label; the memory read still checks for a native failure.
                SendMessagePointer(listViewHandle, LVM_GETITEMTEXTW, (IntPtr)i, itemMemory);

                byte[] textBuffer = new byte[2048];
                if (!ReadProcessMemoryBytes(processHandle, textMemory, textBuffer, 2048, out nuint textBytes) || textBytes != 2048)
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

    public static (string SnapshotId, IReadOnlyList<DesktopIcon> Icons) CaptureSnapshot()
    {
        IntPtr desktopView = FindDesktopView(false);
        if (desktopView == IntPtr.Zero) throw new InvalidOperationException("Desktop unavailable.");
        return DesktopSnapshot.Capture(() => ReadIcons(desktopView, false, false));
    }

    public static void MoveIcon(int index, int x, int y)
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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    private static void EnsureCompatibleArchitecture(IntPtr listView)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Legacy ListView reads support Windows x64 only.");
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not inspect Explorer architecture.");
        try
        {
            if (!IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine)
                || processMachine != 0 || nativeMachine != 0x8664)
                throw new InvalidOperationException("Explorer and this process must both use the native x64 architecture.");
        }
        finally { CloseHandle(handle); }
    }
}
