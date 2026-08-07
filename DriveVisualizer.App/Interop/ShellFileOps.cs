using System.Runtime.InteropServices;

namespace DriveVisualizer_App.Interop;

/// <summary>
/// Shell delete via SHFileOperation so Recycle Bin, undo, and the shell's own
/// progress dialog all behave the way Explorer users expect.
/// </summary>
public static class ShellFileOps
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>Deletes a file or directory tree. Returns true if it is gone afterwards.</summary>
    public static bool Delete(string path, bool permanent, IntPtr ownerHwnd)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = ownerHwnd,
            wFunc = FO_DELETE,
            pFrom = path + "\0", // marshaling adds the second terminator
            fFlags = (ushort)(FOF_NOCONFIRMATION | (permanent ? 0 : FOF_ALLOWUNDO)),
        };
        _ = SHFileOperationW(ref op);
        return !File.Exists(path) && !Directory.Exists(path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint flags);

    /// <summary>
    /// Opens Explorer with the item selected, via the shell COM API rather than
    /// spawning explorer.exe — that spawn is unreliable from elevated processes
    /// and opens a fresh window every time.
    /// </summary>
    public static bool RevealInExplorer(string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0)
            return false;
        try
        {
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 2;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

    /// <summary>
    /// Puts text on the clipboard. Tries the WinRT clipboard first (with Flush so
    /// the text survives app exit and reaches RDP sessions); falls back to the
    /// Win32 clipboard, which also works from elevated processes.
    /// </summary>
    public static bool SetClipboardText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch
        {
        }

        // Win32 fallback.
        if (!OpenClipboard(IntPtr.Zero))
            return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (hGlobal == IntPtr.Zero)
                return false;
            IntPtr target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(hGlobal);
            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal); // still ours on failure
                return false;
            }
            return true; // clipboard owns hGlobal now
        }
        finally
        {
            CloseClipboard();
        }
    }
}
