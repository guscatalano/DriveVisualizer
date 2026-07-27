using System.Management;
using System.Runtime.InteropServices;

namespace DriveVisualizer_App.Services;

public sealed record DriveDetails(
    string Root,
    string? VolumeLabel,
    string? FileSystem,
    long TotalBytes,
    long FreeBytes,
    long ClusterSize,
    string? Model,
    string? MediaType,
    string? BusType,
    string? Health,
    uint? SpindleSpeedRpm);

/// <summary>
/// Volume + physical-disk facts for the drive hosting a path: capacity and
/// filesystem from DriveInfo, SSD/HDD + bus + model + health from the Windows
/// Storage WMI namespace. Everything is best-effort — nulls where unknown.
/// </summary>
public static class DriveStats
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    public static DriveDetails? Get(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // UNC paths have no local physical disk

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            return null;

        long cluster = 4096;
        if (GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _))
            cluster = (long)spc * bps;

        string? model = null, media = null, bus = null, health = null;
        uint? rpm = null;
        try
        {
            char letter = char.ToUpperInvariant(root[0]);
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter='{letter}'")))
            {
                foreach (ManagementObject p in partitions.Get())
                {
                    diskNumber = (uint)p["DiskNumber"];
                    break;
                }
            }

            if (diskNumber is { } n)
            {
                using var disks = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT FriendlyName, MediaType, BusType, HealthStatus, SpindleSpeed FROM MSFT_PhysicalDisk WHERE DeviceId='{n}'"));
                foreach (ManagementObject d in disks.Get())
                {
                    model = d["FriendlyName"] as string;
                    media = (ushort?)(d["MediaType"] as ushort?) switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "Unspecified",
                    };
                    bus = (ushort?)(d["BusType"] as ushort?) switch
                    {
                        1 => "SCSI",
                        2 => "ATAPI",
                        3 => "ATA",
                        5 => "1394",
                        6 => "SSA",
                        7 => "USB",
                        8 => "RAID",
                        9 => "iSCSI",
                        10 => "SAS",
                        11 => "SATA",
                        12 => "SD",
                        13 => "MMC",
                        15 => "File-backed virtual",
                        16 => "Storage Spaces",
                        17 => "NVMe",
                        _ => "Unknown",
                    };
                    health = (ushort?)(d["HealthStatus"] as ushort?) switch
                    {
                        0 => "Healthy",
                        1 => "Warning",
                        2 => "Unhealthy",
                        _ => null,
                    };
                    if (d["SpindleSpeed"] is uint s && s > 0 && s != uint.MaxValue)
                        rpm = s;
                    break;
                }
            }
        }
        catch
        {
            // WMI can fail on exotic volumes (VHD, dev drives) — volume facts still useful.
        }

        return new DriveDetails(
            root, drive.VolumeLabel, drive.DriveFormat,
            drive.TotalSize, drive.AvailableFreeSpace, cluster,
            model, media, bus, health, rpm);
    }
}
