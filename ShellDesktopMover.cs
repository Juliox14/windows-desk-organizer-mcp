using System.Runtime.InteropServices;

namespace DeskOrganizer;

/// <summary>Positions one desktop Shell item by its child PIDL, never by a ListView index.</summary>
internal static class ShellDesktopMover
{
    private static readonly Guid BrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid BrowserId = typeof(IShellBrowser).GUID;
    private const uint PositionItem = 0x80; // SVSI_POSITIONITEM, points in view coordinates.

    internal static void Move(string name, int fromX, int fromY, int toX, int toY, int expectedCount)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { MoveOnSta(name, fromX, fromY, toX, toY, expectedCount); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A timed-out COM call may still complete later: never retry or treat it as a definite failure.
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Desktop Shell move timed out; result is unknown. Do not retry.");
        if (error is not null) throw error;
    }

    private static void MoveOnSta(string name, int fromX, int fromY, int toX, int toY, int expectedCount)
    {
        object? windows = null;
        object? dispatch = null;
        IShellBrowser? browser = null;
        IFolderView? view = null;
        IntPtr browserPtr = IntPtr.Zero;
        IntPtr viewPtr = IntPtr.Zero;
        IntPtr desktopPidl = IntPtr.Zero;
        IntPtr selected = IntPtr.Zero;
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
            Guid service = BrowserService;
            Guid iid = BrowserId;
            Marshal.ThrowExceptionForHR(services.QueryService(ref service, ref iid, out browserPtr));
            if (browserPtr == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell browser is unavailable.");
            browser = (IShellBrowser)Marshal.GetTypedObjectForIUnknown(browserPtr, typeof(IShellBrowser));
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out viewPtr));
            if (viewPtr == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell view is unavailable.");
            view = (IFolderView)Marshal.GetTypedObjectForIUnknown(viewPtr, typeof(IFolderView));
            Marshal.ThrowExceptionForHR(SHGetSpecialFolderLocation(IntPtr.Zero, 0, out desktopPidl));
            if (desktopPidl == IntPtr.Zero) throw new InvalidOperationException("Desktop Shell folder is unavailable.");
            Marshal.ThrowExceptionForHR(view.ItemCount(2, out int count)); // SVGIO_ALLVIEW
            if (count != expectedCount) throw new InvalidOperationException("Desktop icon count changed before Shell move.");

            int matches = 0;
            bool occupied = false;
            for (int i = 0; i < count; i++)
            {
                IntPtr child = IntPtr.Zero;
                IntPtr absolute = IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(view.Item(i, out child));
                    if (child == IntPtr.Zero) throw new InvalidOperationException("Shell item has no PIDL.");
                    Marshal.ThrowExceptionForHR(view.GetItemPosition(child, out POINT position));
                    if (position.X == toX && position.Y == toY) occupied = true;
                    absolute = ILCombine(desktopPidl, child);
                    if (absolute == IntPtr.Zero) throw new InvalidOperationException("Could not resolve Shell item.");
                    int hr = SHGetNameFromIDList(absolute, 0, out IntPtr namePtr); // SIGDN_NORMALDISPLAY
                    string? displayName;
                    try { Marshal.ThrowExceptionForHR(hr); displayName = Marshal.PtrToStringUni(namePtr); }
                    finally { if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr); }
                    if (displayName == name && position.X == fromX && position.Y == fromY)
                    {
                        matches++;
                        if (selected == IntPtr.Zero)
                        {
                            selected = child;
                            child = IntPtr.Zero;
                        }
                    }
                }
                finally
                {
                    if (absolute != IntPtr.Zero) Marshal.FreeCoTaskMem(absolute);
                    if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
                }
            }
            if (matches != 1) throw new InvalidOperationException("Shell icon identity or position changed before move.");
            if (occupied) throw new InvalidOperationException("Shell move destination is occupied.");
            var target = new[] { new POINT { X = toX, Y = toY } };
            Marshal.ThrowExceptionForHR(view.SelectAndPositionItems(1, new[] { selected }, target, PositionItem));
            Marshal.ThrowExceptionForHR(view.GetItemPosition(selected, out POINT observed));
            if (observed.X != toX || observed.Y != toY)
                throw new InvalidOperationException("Shell move position did not match the request; result may change later.");
        }
        finally
        {
            if (selected != IntPtr.Zero) Marshal.FreeCoTaskMem(selected);
            if (desktopPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(desktopPidl);
            if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
            if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
            if (view is not null) Marshal.ReleaseComObject(view);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (dispatch is not null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
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
        void GetSpacing(); void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem();
        [PreserveSig] int SelectAndPositionItems(uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] childPidls,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] POINT[] positions, uint flags);
    }
}
