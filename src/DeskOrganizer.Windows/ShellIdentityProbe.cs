using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskOrganizer;

/// <summary>
/// Read-only experiment: enumerate the actual desktop Shell view rather than inferring
/// an item's identity from its ListView index or visible text.
/// </summary>
public static class ShellIdentityProbe
{
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserId = typeof(IShellBrowser).GUID;

    private static readonly object QueryGate = new();
    private static Thread? pendingQuery;

    public sealed record Item(int ViewIndex, string Name, int X, int Y, string? ShellId, bool HasFileSystemPath);

    public static IReadOnlyList<Item> Capture()
    {
        // COM queries may outlive a timeout. Never accumulate abandoned STA threads.
        lock (QueryGate)
        {
            if (pendingQuery?.IsAlive == true)
                throw new DesktopException("SHELL_BUSY", "A previous Shell query is still running. Try again after it finishes.");
            return CaptureOnce();
        }
    }

    private static IReadOnlyList<Item> CaptureOnce()
    {
        IReadOnlyList<Item>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = ReadShellView(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        pendingQuery = thread;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Desktop Shell query timed out.");
        if (error is not null) throw error;
        return result ?? throw new InvalidOperationException("Desktop Shell query returned no result.");
    }

    private static IReadOnlyList<Item> ReadShellView()
    {
        using var dpi = DesktopDpiScope.ForWindow(WindowsDesktopBackend.FindView());
        object? windows = null;
        object? dispatch = null;
        IShellBrowser? browser = null;
        IFolderView? folderView = null;
        IntPtr browserPtr = IntPtr.Zero;
        IntPtr viewPtr = IntPtr.Zero;
        IntPtr desktopPidl = IntPtr.Zero;
        try
        {
            Type shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))
                ?? throw new InvalidOperationException("ShellWindows is unavailable.");
            windows = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("ShellWindows could not be created.");
            dynamic shellWindows = windows;
            object location = 0; // CSIDL_DESKTOP
            object empty = Type.Missing;
            int windowHandle;
            dispatch = shellWindows.FindWindowSW(ref location, ref empty, 8, out windowHandle, 1);
            if (dispatch is not IComServiceProvider services)
                throw new InvalidOperationException("Desktop Shell services are unavailable.");

            Guid serviceId = TopLevelBrowserService;
            Guid browserId = ShellBrowserId;
            Marshal.ThrowExceptionForHR(services.QueryService(ref serviceId, ref browserId, out browserPtr));
            if (browserPtr == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell browser is unavailable.");
            browser = (IShellBrowser)Marshal.GetTypedObjectForIUnknown(browserPtr, typeof(IShellBrowser));
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out viewPtr));
            if (viewPtr == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell view is unavailable.");
            folderView = (IFolderView)Marshal.GetTypedObjectForIUnknown(viewPtr, typeof(IFolderView));

            Marshal.ThrowExceptionForHR(SHGetSpecialFolderLocation(IntPtr.Zero, 0, out desktopPidl));
            if (desktopPidl == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell folder is unavailable.");
            Marshal.ThrowExceptionForHR(folderView.ItemCount(2, out int count)); // SVGIO_ALLVIEW
            if (count < 0 || count > LayoutPlanner.MaxIcons)
                throw new DesktopException("LIMIT_EXCEEDED", "Desktop icon count exceeds the supported query size.");

            var items = new List<Item>(count);
            for (int index = 0; index < count; index++)
            {
                IntPtr child = IntPtr.Zero;
                IntPtr absolute = IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(folderView.Item(index, out child));
                    if (child == IntPtr.Zero) throw new InvalidOperationException("Shell item has no PIDL.");
                    Marshal.ThrowExceptionForHR(folderView.GetItemPosition(child, out POINT position));
                    absolute = ILCombine(desktopPidl, child);
                    if (absolute == IntPtr.Zero) throw new InvalidOperationException("Could not resolve Shell item.");
                    string name = GetName(absolute, 0) ?? ""; // SIGDN_NORMALDISPLAY
                    if (name.Length > 1024)
                        throw new DesktopException("LIMIT_EXCEEDED", "Desktop icon label exceeds the supported query size.");
                    string? parsingName = GetName(absolute, 0x80028000); // SIGDN_DESKTOPABSOLUTEPARSING
                    string? shellId = string.IsNullOrWhiteSpace(parsingName)
                        ? null
                        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parsingName))).ToLowerInvariant();
                    // SIGDN_FILESYSPATH fails for items without a filesystem path; never return that path.
                    bool hasFileSystemPath = GetName(absolute, 0x80058000) is not null;
                    items.Add(new Item(index, name, position.X, position.Y, shellId, hasFileSystemPath));
                }
                finally
                {
                    if (absolute != IntPtr.Zero) Marshal.FreeCoTaskMem(absolute);
                    if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
                }
            }

            // A parsing name that resolves to multiple visible items is ambiguous, not an identity.
            var duplicates = items.Where(item => item.ShellId is not null)
                .GroupBy(item => item.ShellId).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet();
            return items.Select(item => duplicates.Contains(item.ShellId)
                ? item with { ShellId = null }
                : item).ToArray();
        }
        finally
        {
            if (desktopPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(desktopPidl);
            if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
            if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
            if (folderView is not null) Marshal.ReleaseComObject(folderView);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (dispatch is not null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
    }

    private static string? GetName(IntPtr absolutePidl, uint nameKind)
    {
        int hr = SHGetNameFromIDList(absolutePidl, nameKind, out IntPtr namePtr);
        try { return hr < 0 || namePtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(namePtr); }
        finally { if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // Twelve inherited IShellBrowser/IOleWindow vtable slots precede QueryActiveShellView.
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB();
        void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB();
        void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject();
        void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IntPtr view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr childPidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr childPidl, out POINT position);
    }
}
