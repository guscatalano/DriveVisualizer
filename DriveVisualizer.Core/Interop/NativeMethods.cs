using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DriveVisualizer.Core.Interop;

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle() : base(ownsHandle: true) { }
    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_DATAW
{
    public uint dwFileAttributes;
    public uint ftCreationTimeLow;
    public uint ftCreationTimeHigh;
    public uint ftLastAccessTimeLow;
    public uint ftLastAccessTimeHigh;
    public uint ftLastWriteTimeLow;
    public uint ftLastWriteTimeHigh;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0; // reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
    public uint dwReserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;

    public readonly long FileSize => ((long)nFileSizeHigh << 32) | nFileSizeLow;
    public readonly long LastWriteTimeTicks
    {
        get
        {
            long ft = ((long)ftLastWriteTimeHigh << 32) | ftLastWriteTimeLow;
            // FILETIME epoch (1601) → DateTime ticks epoch (0001)
            return ft + 504911232000000000L;
        }
    }
}

internal static partial class NativeMethods
{
    public const int FindExInfoBasic = 1;          // skip 8.3 short names — measurably faster
    public const int FindExSearchNameMatch = 0;
    public const int FIND_FIRST_EX_LARGE_FETCH = 2;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;

    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const uint FILE_ATTRIBUTE_COMPRESSED = 0x800;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFindHandle FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
