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
}
